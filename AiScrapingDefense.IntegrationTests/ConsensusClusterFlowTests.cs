using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;
using StackExchange.Redis;

namespace AiScrapingDefense.IntegrationTests;

[Collection(EndToEndCollection.Name)]
public sealed class ConsensusClusterFlowTests
{
    private readonly DefenseStackFixture _fixture;

    public ConsensusClusterFlowTests(DefenseStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ThreeNodeCluster_ElectsLeader_ReplicatesLog_AndFailsOver()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await ExerciseThreeNodeClusterAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (
                attempt < 3 && IsAddressAlreadyInUse(exception))
            {
                // Ephemeral-port discovery and Raft binding cannot be atomic.
                // Retry the isolated cluster if another process wins that race.
            }
        }
    }

    private async Task ExerciseThreeNodeClusterAsync(CancellationToken cancellationToken)
    {
        var ports = ReserveTcpPorts(3);
        var members = ports.Select(port => new ConsensusMemberOptions
        {
            RaftEndpoint = $"127.0.0.1:{port}",
            ApiBaseUrl = $"http://127.0.0.1:{port}"
        }).ToArray();
        var root = Path.Combine(
            Path.GetTempPath(),
            "ai-scraping-defense-tests",
            "raft",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        await using var redis = await ConnectionMultiplexer.ConnectAsync(_fixture.RedisConnectionString);
        for (var database = 8; database <= 10; database++)
        {
            await redis.GetDatabase(database).ExecuteAsync("FLUSHDB");
        }

        using var httpServices = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var httpClientFactory = httpServices.GetRequiredService<IHttpClientFactory>();
        var nodes = ports.Select((port, index) => ConsensusTestNode.Create(
            port,
            database: 8 + index,
            storagePath: Path.Combine(root, $"node-{index}"),
            members,
            redis,
            httpClientFactory)).ToArray();

        try
        {
            await Task.WhenAll(nodes.Select(node => node.Coordinator.StartAsync(cancellationToken)));
            var initialLeader = await WaitForSingleLeaderAsync(nodes, cancellationToken);
            var initialTerm = initialLeader.Coordinator.GetStatus().Term;

            const string replicatedIp = "198.51.100.81";
            var committed = await initialLeader.Coordinator.ReplicateAsync(
                ConsensusCommand.Block(
                    replicatedIp,
                    "integration_replication",
                    ["raft_test"],
                    DateTimeOffset.UtcNow.AddMinutes(5)),
                cancellationToken);

            Assert.True(committed);
            await WaitForAsync(
                async () => (await Task.WhenAll(nodes.Select(node =>
                    node.Blocklist.IsBlockedAsync(replicatedIp, cancellationToken)))).All(blocked => blocked),
                "the committed block command to materialize on all three state machines",
                cancellationToken);

            await initialLeader.Coordinator.StopAsync(cancellationToken);
            var remainingNodes = nodes.Where(node => !ReferenceEquals(node, initialLeader)).ToArray();
            var failoverLeader = await WaitForSingleLeaderAsync(remainingNodes, cancellationToken);
            Assert.True(failoverLeader.Coordinator.GetStatus().Term > initialTerm);

            const string failoverIp = "198.51.100.82";
            committed = await failoverLeader.Coordinator.ReplicateAsync(
                ConsensusCommand.Block(
                    failoverIp,
                    "integration_failover",
                    ["raft_failover_test"],
                    DateTimeOffset.UtcNow.AddMinutes(5)),
                cancellationToken);

            Assert.True(committed);
            await WaitForAsync(
                async () => (await Task.WhenAll(remainingNodes.Select(node =>
                    node.Blocklist.IsBlockedAsync(failoverIp, cancellationToken)))).All(blocked => blocked),
                "the post-failover command to replicate to the surviving quorum",
                cancellationToken);
        }
        finally
        {
            foreach (var node in nodes.Reverse())
            {
                await node.DisposeAsync();
            }

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Windows can briefly retain DotNext WAL handles after cluster disposal.
            }
        }
    }

    private static bool IsAddressAlreadyInUse(Exception exception)
    {
        if (exception is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
        {
            return true;
        }

        if (exception is AggregateException aggregateException)
        {
            return aggregateException.InnerExceptions.Any(IsAddressAlreadyInUse);
        }

        return exception.InnerException is { } inner && IsAddressAlreadyInUse(inner);
    }

    private static async Task<ConsensusTestNode> WaitForSingleLeaderAsync(
        IReadOnlyCollection<ConsensusTestNode> nodes,
        CancellationToken cancellationToken)
    {
        ConsensusTestNode? elected = null;
        await WaitForAsync(() =>
        {
            var leaders = nodes.Where(node => node.Coordinator.GetStatus().IsLeader).ToArray();
            elected = leaders.Length == 1 ? leaders[0] : null;
            return Task.FromResult(elected is not null);
        }, "a single Raft leader to be elected", cancellationToken);
        return elected!;
    }

    private static async Task WaitForAsync(
        Func<Task<bool>> condition,
        string description,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        Assert.Fail($"Timed out waiting for {description}.");
    }

    private static int[] ReserveTcpPorts(int count)
    {
        var listeners = Enumerable.Range(0, count)
            .Select(_ => new TcpListener(IPAddress.Loopback, 0))
            .ToArray();

        try
        {
            foreach (var listener in listeners)
            {
                listener.Start();
            }

            return listeners
                .Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port)
                .ToArray();
        }
        finally
        {
            foreach (var listener in listeners)
            {
                listener.Stop();
            }
        }
    }

    private sealed class ConsensusTestNode : IAsyncDisposable
    {
        private ConsensusTestNode(
            BlocklistConsensusCoordinator coordinator,
            RedisBlocklistService blocklist)
        {
            Coordinator = coordinator;
            Blocklist = blocklist;
        }

        public BlocklistConsensusCoordinator Coordinator { get; }

        public RedisBlocklistService Blocklist { get; }

        public static ConsensusTestNode Create(
            int port,
            int database,
            string storagePath,
            ConsensusMemberOptions[] members,
            IConnectionMultiplexer redis,
            IHttpClientFactory httpClientFactory)
        {
            var options = Options.Create(new DefenseEngineOptions
            {
                Redis = new RedisOptions
                {
                    BlocklistDatabase = database,
                    BlocklistKeyPrefix = "raft-test:"
                },
                Consensus = new ConsensusOptions
                {
                    Enabled = true,
                    ListenAddress = "127.0.0.1",
                    AdvertisedHost = "127.0.0.1",
                    Port = port,
                    StoragePath = storagePath,
                    SharedSecret = "integration-consensus-secret-value",
                    RequestTimeoutSeconds = 2,
                    LowerElectionTimeoutMilliseconds = 250,
                    UpperElectionTimeoutMilliseconds = 500,
                    Members = members
                }
            });
            var connectionProvider = new FixedRedisConnectionProvider(redis);
            var blocklist = new RedisBlocklistService(
                connectionProvider,
                options,
                NullLogger<RedisBlocklistService>.Instance);
            var coordinator = new BlocklistConsensusCoordinator(
                options,
                blocklist,
                httpClientFactory,
                NullLoggerFactory.Instance,
                NullLogger<BlocklistConsensusCoordinator>.Instance);
            return new ConsensusTestNode(coordinator, blocklist);
        }

        public ValueTask DisposeAsync() => Coordinator.DisposeAsync();
    }

    private sealed class FixedRedisConnectionProvider : IRedisConnectionProvider
    {
        private readonly IConnectionMultiplexer _redis;

        public FixedRedisConnectionProvider(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        public Task<IConnectionMultiplexer> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_redis);
    }
}
