using System.Net;
using DotNext.Buffers;
using DotNext.IO;
using DotNext.Net;
using DotNext.Net.Cluster.Consensus.Raft.Membership;

namespace RedisBlocklistMiddlewareApp.Services;

internal sealed class PersistentEndPointConfigurationStorage
    : PersistentClusterConfigurationStorage<EndPoint>
{
    public PersistentEndPointConfigurationStorage(string path)
        : base(
            path,
            4096,
            EndPointFormatter.UriEndPointComparer,
            allocator: null)
    {
    }

    protected override void Encode(EndPoint address, ref BufferWriterSlim<byte> output) =>
        EndPointFormatter.WriteEndPoint(ref output, address);

    protected override EndPoint Decode(ref SequenceReader reader) =>
        EndPointFormatter.ReadEndPoint(ref reader);
}
