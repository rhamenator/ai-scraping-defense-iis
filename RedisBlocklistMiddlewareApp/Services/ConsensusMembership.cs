using System.Net;
using System.Text.Json;
using DotNext.Buffers;
using DotNext.IO;
using DotNext.Net;
using DotNext.Net.Cluster.Consensus.Raft.Membership;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// The Raft membership of this deployment. It is static: it comes from <c>DefenseEngine:Consensus:Members</c> on every start,
/// and a small file records what it was so that a node refuses to start with a different member list than the one it ran with
/// (changing membership needs every node changed together; silently adopting a different list could split the cluster).
/// </summary>
internal static class ConsensusMembership
{
    public const string FileName = "members.json";

    /// <summary>Compares end points by host and port, ignoring case and how they were created.</summary>
    public static readonly IEqualityComparer<EndPoint> Comparer = new EndPointByHostAndPort();

    /// <summary>Records the member list on first use and refuses a different one afterwards.</summary>
    public static async Task EnsureMatchesAsync(string directory, IReadOnlyCollection<EndPoint> configured, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, FileName);
        var expected = configured.Select(Describe).Order(StringComparer.Ordinal).ToArray();

        if (!File.Exists(path))
        {
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(expected), cancellationToken);
            File.Move(temporary, path, overwrite: true);
            return;
        }

        var recorded = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(path, cancellationToken)) ?? [];
        if (!recorded.Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The Raft membership recorded for this node differs from DefenseEngine:Consensus:Members " +
                $"(recorded: {string.Join(", ", recorded)}; configured: {string.Join(", ", expected)}). " +
                "Restore the configured membership on every node, or change it on every node together and remove " +
                $"{path} on each; do not replace the WAL directory.");
        }
    }

    /// <summary>An in-memory configuration holding exactly the configured members.</summary>
    public static IClusterConfigurationStorage<EndPoint> CreateStorage(IEnumerable<EndPoint> members)
    {
        var storage = new EndPointConfigurationStorage();
        var builder = storage.CreateInitialConfigurationBuilder();
        foreach (var member in members)
        {
            builder.Add(member);
        }

        builder.Build();
        return storage;
    }

    internal static string Describe(EndPoint endPoint) => endPoint switch
    {
        IPEndPoint ip => $"{ip.Address}:{ip.Port}",
        DnsEndPoint dns => $"{dns.Host.ToLowerInvariant()}:{dns.Port}",
        _ => endPoint.ToString()?.ToLowerInvariant() ?? string.Empty
    };

    private sealed class EndPointByHostAndPort : IEqualityComparer<EndPoint>
    {
        public bool Equals(EndPoint? x, EndPoint? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && Describe(x) == Describe(y));

        public int GetHashCode(EndPoint obj) => Describe(obj).GetHashCode(StringComparison.Ordinal);
    }

    private sealed class EndPointConfigurationStorage : InMemoryClusterConfigurationStorage<EndPoint>
    {
        protected override void Encode(EndPoint address, ref BufferWriterSlim<byte> output) =>
            EndPointFormatter.WriteEndPoint(ref output, address);

        protected override EndPoint Decode(ref SequenceReader reader) =>
            EndPointFormatter.ReadEndPoint(ref reader);
    }
}
