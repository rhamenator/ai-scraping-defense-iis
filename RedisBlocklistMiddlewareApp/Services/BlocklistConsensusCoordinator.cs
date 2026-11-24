using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DotNext.Net;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Membership;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class BlocklistConsensusCoordinator : IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ConsensusOptions _options;
    private readonly RedisBlocklistService _blocklist;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<BlocklistConsensusCoordinator> _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private RaftCluster? _cluster;
    private EndPoint? _localEndPoint;
    private bool _disposed;

    public BlocklistConsensusCoordinator(
        IOptions<DefenseEngineOptions> options,
        RedisBlocklistService blocklist,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ILogger<BlocklistConsensusCoordinator> logger)
    {
        _options = options.Value.Consensus;
        _blocklist = blocklist;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    public bool IsLeader => _cluster is { } cluster && IsLocalLeader(cluster);

    public ConsensusRuntimeStatus GetStatus()
    {
        var cluster = _cluster;
        return new ConsensusRuntimeStatus(
            _options.Enabled,
            cluster is not null,
            IsLeader,
            cluster?.Leader?.EndPoint.ToString(),
            cluster?.Term ?? 0,
            cluster?.Members.Count ?? 0,
            _localEndPoint?.ToString());
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cluster is not null)
            {
                return;
            }

            Directory.CreateDirectory(_options.StoragePath);
            var membershipPath = Path.Join(_options.StoragePath, "membership");
            Directory.CreateDirectory(membershipPath);
            var (logPath, snapshotPath) = await RaftLogLayout.PrepareAsync(_options.StoragePath, _logger, cancellationToken);

            var configuredMembers = _options.Members
                .Select(member => ParseEndPoint(member.RaftEndpoint))
                .ToArray();
            _localEndPoint = CreateEndPoint(_options.AdvertisedHost, _options.Port);

            await ConsensusMembership.EnsureMatchesAsync(membershipPath, configuredMembers, cancellationToken);
            var storage = ConsensusMembership.CreateStorage(configuredMembers);

            var listenAddress = IPAddress.Parse(_options.ListenAddress);
            var configuration = new RaftCluster.TcpConfiguration(
                new IPEndPoint(listenAddress, _options.Port))
            {
                PublicEndPoint = _localEndPoint,
                ConfigurationStorage = storage,
                ColdStart = false,
                LowerElectionTimeout = _options.LowerElectionTimeoutMilliseconds,
                UpperElectionTimeout = _options.UpperElectionTimeoutMilliseconds,
                RequestTimeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds),
                // DotNext requires the TCP connect timeout not to exceed the
                // lower election timeout; otherwise failed peer dials can
                // stall voting long enough to prevent a stable election.
                ConnectTimeout = TimeSpan.FromMilliseconds(Math.Min(
                    _options.RequestTimeoutSeconds * 1000L,
                    _options.LowerElectionTimeoutMilliseconds)),
                LoggerFactory = _loggerFactory
            };

            var stateMachine = new RaftBlocklistStateMachine(
                snapshotPath,
                _blocklist,
                _loggerFactory.CreateLogger<RaftBlocklistStateMachine>(),
                _options.SnapshotEveryCommands);
            // The most recent snapshot must be restored before the log is opened, so replay starts after it.
            await stateMachine.RestoreAsync(cancellationToken);
            var writeAheadLog = new WriteAheadLog(new WriteAheadLog.Options { Location = logPath }, stateMachine);
            await writeAheadLog.InitializeAsync(cancellationToken);
            var cluster = new RaftCluster(configuration)
            {
                AuditTrail = writeAheadLog
            };

            try
            {
                await cluster.StartAsync(cancellationToken);
                _cluster = cluster;
            }
            catch
            {
                await cluster.DisposeAsync();
                throw;
            }

            _logger.LogInformation(
                "Started durable Raft consensus node {LocalEndPoint} with {MemberCount} configured members and WAL path {LogPath}.",
                _localEndPoint,
                configuredMembers.Length,
                logPath);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await using var cluster = Interlocked.Exchange(ref _cluster, null);
            if (cluster is null)
            {
                return;
            }

            await cluster.StopAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<bool> ReplicateAsync(
        ConsensusCommand command,
        CancellationToken cancellationToken)
    {
        EnsureValidCommand(command);
        if (string.Equals(command.Operation, ConsensusCommand.BlockOperation, StringComparison.Ordinal) &&
            !_blocklist.CanBlock(command.IpAddress))
        {
            return false;
        }
        var cluster = _cluster ?? throw new InvalidOperationException("The Raft consensus node is not running.");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var leader = await cluster.WaitForLeaderAsync(
                TimeSpan.FromSeconds(_options.RequestTimeoutSeconds),
                cancellationToken);
            if (leader is null)
            {
                if (attempt == 3)
                {
                    return false;
                }
            }
            else if (IsLocalLeader(cluster))
            {
                if (await ReplicateOnLeaderAsync(command, cancellationToken))
                {
                    return true;
                }
            }
            else
            {
                var forwardingResult = await ForwardToLeaderAsync(
                    leader.EndPoint,
                    command,
                    cancellationToken);
                if (forwardingResult.Committed)
                {
                    return true;
                }
            }

            if (attempt < 3)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Max(
                        200,
                        _options.UpperElectionTimeoutMilliseconds * attempt)),
                    cancellationToken);
            }
        }

        return false;
    }

    public async Task<bool> ReplicateOnLeaderAsync(
        ConsensusCommand command,
        CancellationToken cancellationToken)
    {
        EnsureValidCommand(command);
        if (string.Equals(command.Operation, ConsensusCommand.BlockOperation, StringComparison.Ordinal) &&
            !_blocklist.CanBlock(command.IpAddress))
        {
            return false;
        }
        var cluster = _cluster ?? throw new InvalidOperationException("The Raft consensus node is not running.");
        if (!IsLocalLeader(cluster))
        {
            return false;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(command, SerializerOptions);
        try
        {
            // In DotNext 6 this completes when the entry is committed and throws when it cannot be.
            await cluster.ReplicateAsync(payload, command.CommandId, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Raft replication of command {CommandId} did not commit.", command.CommandId);
            return false;
        }
    }

    private async Task<ConsensusMutationResponse> ForwardToLeaderAsync(
        EndPoint leaderEndPoint,
        ConsensusCommand command,
        CancellationToken cancellationToken)
    {
        var leaderMember = _options.Members.FirstOrDefault(member =>
            ConsensusMembership.Comparer.Equals(
                ParseEndPoint(member.RaftEndpoint),
                leaderEndPoint));
        if (leaderMember is null)
        {
            throw new InvalidOperationException(
                $"The elected Raft leader {leaderEndPoint} has no configured internal API URL.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{leaderMember.ApiBaseUrl}/internal/consensus/commands")
        {
            Content = JsonContent.Create(command, options: SerializerOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SharedSecret);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return new ConsensusMutationResponse(false, "leader_changed");
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ConsensusMutationResponse>(
            SerializerOptions,
            cancellationToken) ?? new ConsensusMutationResponse(false, "empty_leader_response");
    }

    private static bool IsLocalLeader(RaftCluster cluster) =>
        !cluster.LeadershipToken.IsCancellationRequested;

    internal static EndPoint ParseEndPoint(string value)
    {
        if (!Uri.TryCreate($"tcp://{value}", UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Port is < 1 or > 65535)
        {
            throw new FormatException($"Invalid Raft endpoint '{value}'.");
        }

        return CreateEndPoint(uri.Host, uri.Port);
    }

    private static EndPoint CreateEndPoint(string host, int port) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var address)
            ? new IPEndPoint(address, port)
            : new DnsEndPoint(host, port);

    private static void EnsureValidCommand(ConsensusCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CommandId == Guid.Empty ||
            !IPAddress.TryParse(command.IpAddress, out _) ||
            (!string.Equals(command.Operation, ConsensusCommand.BlockOperation, StringComparison.Ordinal) &&
             !string.Equals(command.Operation, ConsensusCommand.UnblockOperation, StringComparison.Ordinal)) ||
            string.IsNullOrWhiteSpace(command.Reason) ||
            command.Signals is null ||
            command.Reason.Length > 1024 ||
            command.Signals.Length > 128 ||
            command.Signals.Any(signal => signal is null || signal.Length > 512))
        {
            throw new ArgumentException("The consensus command is invalid.", nameof(command));
        }
        if (string.Equals(command.Operation, ConsensusCommand.BlockOperation, StringComparison.Ordinal) &&
            command.ExpiresAtUtc is null)
        {
            throw new ArgumentException("A consensus block command must contain an expiration.", nameof(command));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync(CancellationToken.None);
        _lifecycleLock.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed record ConsensusRuntimeStatus(
    bool Enabled,
    bool Started,
    bool IsLeader,
    string? LeaderEndPoint,
    long Term,
    int MemberCount,
    string? LocalEndPoint);
