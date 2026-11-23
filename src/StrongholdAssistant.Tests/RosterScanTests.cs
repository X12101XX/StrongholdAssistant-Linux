using System.Reflection;
using ASPAssistant.Core.Services;
using Moq;
using StrongholdAssistant.Host;

namespace StrongholdAssistant.Tests;

public sealed class RosterScanTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SortRecapturesUntilRecognizedWithoutRepeatingInput(bool requireTier)
    {
        var results = new Queue<RosterSort?>([null, new("covenant", 1700, 60), new("tier", 1620, 60)]);
        int captures = 0;
        var sort = await HostApi.RecheckRosterSortAsync(null, () =>
        {
            captures++;
            return Task.FromResult(results.Dequeue());
        }, requireTier, CancellationToken.None);
        Assert.Equal(requireTier ? "tier" : "covenant", sort?.Mode);
        Assert.Equal(requireTier ? 3 : 2, captures);
    }

    [Fact]
    public async Task UnrecognizedSortStopsAfterThreeRecaptures()
    {
        int captures = 0;
        var sort = await HostApi.RecheckRosterSortAsync(null, () =>
        {
            captures++;
            return Task.FromResult<RosterSort?>(null);
        }, false, CancellationToken.None);
        Assert.Null(sort);
        Assert.Equal(3, captures);
    }

    [Theory]
    [InlineData(false, "user")]
    [InlineData(true, "input")]
    public async Task StartupCancellationReportsStatusWithoutThrowing(bool input, string reason)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var data = Path.Combine(AppContext.BaseDirectory, "data");
            var api = await HostApi.CreateAsync(data, root);
            using var connection = new GameConnectionService();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var statuses = new List<RosterScanStatus>();
            api.Changed += (name, value) => { if (name == "rosterScan") statuses.Add((RosterScanStatus)value); };
            api.InitializeRoster(data, Mock.Of<IOcrEngine>(), connection, async paused =>
            {
                if (paused) { entered.SetResult(); await release.Task; }
            });
            // 在创建游戏控制器前暂停，测试不连接游戏、不发任何操作。
            var start = api.StartRosterScanAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task stopped = Task.CompletedTask;
            if (input) api.NotifyRosterInput(); else stopped = api.StopRosterScanAsync();
            release.SetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(5));
            await stopped.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(api.IsRosterScanning);
            var status = Assert.Single(statuses);
            Assert.False(status.Running);
            Assert.Equal(reason, status.Reason);
            Assert.False(string.IsNullOrWhiteSpace(status.Message));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("mumu", false)]
    [InlineData("ldplayer", false)]
    [InlineData("pc", true)]
    public async Task InputCancellationUsesSourceAndSlidingGrace(string source, bool cancels)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var api = await HostApi.CreateAsync(Path.Combine(AppContext.BaseDirectory, "data"), root);
            api.UpdateConnection(new("connected", Source: source));
            using var stop = new CancellationTokenSource();
            Set("_rosterStop", stop);
            Set("_rosterStarted", DateTimeOffset.UtcNow);
            api.NotifyRosterInput();
            Assert.False(stop.IsCancellationRequested);
            Set("_rosterStarted", DateTimeOffset.UtcNow.AddSeconds(-2));
            api.NotifyRosterInput();
            Assert.Equal(cancels, stop.IsCancellationRequested);

            void Set(string name, object value) => typeof(HostApi).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api, value);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
