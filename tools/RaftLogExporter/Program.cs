using RaftLogExporter;

// Usage: RaftLogExporter <consensus-storage-path>
// Run it with the node STOPPED, against the directory configured as Consensus:StoragePath. It reads <path>/log (DotNext 5.x
// format) and writes <path>/export/raft-log-v1/, which the DotNext 6 release imports. It never modifies the log.
if (args.Length != 1 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("Usage: RaftLogExporter <consensus-storage-path>   (stop the node first)");
    return args.Length == 1 ? 0 : 2;
}

try
{
    var meta = await RaftLogExport.ExportAsync(args[0], Console.WriteLine, CancellationToken.None);
    if (meta is null)
    {
        Console.WriteLine($"No Raft log found in {Path.Join(args[0], "log")}; nothing to export.");
    }

    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Export failed; the log was not modified: {exception.Message}");
    return 1;
}
