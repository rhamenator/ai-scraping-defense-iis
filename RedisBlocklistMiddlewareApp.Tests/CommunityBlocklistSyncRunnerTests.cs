using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class CommunityBlocklistSyncRunnerTests
{
    [Fact]
    public async Task RunOnceAsync_ImportsValidIpsAndTracksRejectedEntries()
    {
        var options = Options.Create(new DefenseEngineOptions
        {
            CommunityBlocklist = new CommunityBlocklistOptions
            {
                Enabled = true,
                MaximumEntriesPerSource = 10,
                Sources =
                [
                    new CommunityBlocklistSourceOptions
                    {
                        Name = "community-a",
                        Url = "https://community.example.test/list"
                    }
                ]
            }
        });
        var blocklist = new TestBlocklistService();
        var statusStore = new CommunityBlocklistSyncStatusStore(options);
        var runner = new CommunityBlocklistSyncRunner(
            options,
            new TestFeedClient(["198.51.100.10", "127.0.0.1", "not-an-ip"]),
            blocklist,
            statusStore,
            NullLogger<CommunityBlocklistSyncRunner>.Instance);

        var status = await runner.RunOnceAsync(CancellationToken.None);

        Assert.True(status.Enabled);
        Assert.Equal(1, status.ImportedCount);
        Assert.Equal(2, status.RejectedCount);
        Assert.Single(blocklist.BlockCalls);
        Assert.Equal("198.51.100.10", blocklist.BlockCalls[0].IpAddress);
        Assert.Equal("community_blocklist:community-a", blocklist.BlockCalls[0].Reason);
        Assert.Single(status.Sources);
        Assert.Equal(1, status.Sources[0].ImportedCount);
        Assert.Equal(2, status.Sources[0].RejectedCount);
        Assert.NotNull(status.LastSuccessAtUtc);
    }

    [Fact]
    public async Task RunOnceAsync_StoresDisabledStatus_WhenSyncIsDisabled()
    {
        var options = Options.Create(new DefenseEngineOptions
        {
            CommunityBlocklist = new CommunityBlocklistOptions
            {
                Enabled = false
            }
        });
        var runner = new CommunityBlocklistSyncRunner(
            options,
            new TestFeedClient([]),
            new TestBlocklistService(),
            new CommunityBlocklistSyncStatusStore(options),
            NullLogger<CommunityBlocklistSyncRunner>.Instance);

        var status = await runner.RunOnceAsync(CancellationToken.None);

        Assert.False(status.Enabled);
        Assert.Equal(0, status.ImportedCount);
    }

    [Fact]
    public async Task RunOnceAsync_TruncatesAndCountsExcessEntriesAsRejected()
    {
        var options = Options.Create(new DefenseEngineOptions
        {
            CommunityBlocklist = new CommunityBlocklistOptions
            {
                Enabled = true,
                MaximumEntriesPerSource = 2,
                Sources =
                [
                    new CommunityBlocklistSourceOptions
                    {
                        Name = "community-b",
                        Url = "https://community.example.test/list"
                    }
                ]
            }
        });
        var blocklist = new TestBlocklistService();
        var runner = new CommunityBlocklistSyncRunner(
            options,
            new TestFeedClient(["198.51.100.1", "198.51.100.2", "198.51.100.3", "198.51.100.4"]),
            blocklist,
            new CommunityBlocklistSyncStatusStore(options),
            NullLogger<CommunityBlocklistSyncRunner>.Instance);

        var status = await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, status.ImportedCount);
        Assert.Equal(2, status.RejectedCount);
        Assert.Equal(2, blocklist.BlockCalls.Count);
        Assert.Equal(2, status.Sources[0].ImportedCount);
        Assert.Equal(2, status.Sources[0].RejectedCount);
    }

    private sealed class TestFeedClient : ICommunityBlocklistFeedClient
    {
        private readonly IReadOnlyList<string> _ips;

        public TestFeedClient(IReadOnlyList<string> ips)
        {
            _ips = ips;
        }

        public Task<IReadOnlyList<string>> FetchAsync(CommunityBlocklistSourceOptions source, CancellationToken cancellationToken)
        {
            return Task.FromResult(_ips);
        }
    }

    private sealed class TestBlocklistService : IBlocklistService
    {
        public List<(string IpAddress, string Reason, IReadOnlyCollection<string> Signals)> BlockCalls { get; } = [];

        public Task<bool> IsBlockedAsync(string ipAddress, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public Task BlockAsync(string ipAddress, string reason, IReadOnlyCollection<string> signals, CancellationToken cancellationToken)
        {
            BlockCalls.Add((ipAddress, reason, signals));
            return Task.CompletedTask;
        }

        public Task UnblockAsync(string ipAddress, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
