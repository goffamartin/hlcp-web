using Hlcp.Application;

namespace Hlcp.Tests;

public sealed class DashboardStateTests
{
    [Fact]
    public void FailedRefreshRetainsDataButInvalidatesHealthyCountsAndBacksOff()
    {
        var state = new DashboardState();
        var now = DateTimeOffset.UtcNow;
        Assert.Null(state.Nodes);
        state.Accept([Node(now)], now);
        Assert.Equal(1, state.Project(now).Counts.RunningContainers);
        state.Refuse(HlcpFailure.Unavailable);
        Assert.Equal(now, state.CapturedAt);
        Assert.True(state.IsStale(now));
        Assert.Equal(ResourceDisplayState.Stale, Assert.Single(state.Project(now).Resources).State);
        Assert.Equal(0, state.Project(now).Counts.RunningContainers);
        Assert.Equal(TimeSpan.FromSeconds(10), state.RefreshDelay);
        for (var index = 0; index < 100; index++) state.Refuse(HlcpFailure.RateLimited);
        Assert.Equal(TimeSpan.FromSeconds(60), state.RefreshDelay);
        state.Accept([], now.AddSeconds(1));
        Assert.Null(state.Failure);
        Assert.False(state.IsStale(now.AddSeconds(1)));
        Assert.Empty(state.Nodes!);
        Assert.Equal(TimeSpan.FromSeconds(5), state.RefreshDelay);
    }

    [Fact]
    public void SnapshotAgesOutEvenWithoutAnotherResponse()
    {
        var state = new DashboardState();
        var now = DateTimeOffset.UtcNow;
        state.Accept([Node(now)], now);
        Assert.False(state.IsStale(now.AddSeconds(20)));
        Assert.True(state.IsStale(now.AddSeconds(21)));
        Assert.Equal(ResourceDisplayState.Stale, Assert.Single(state.Project(now.AddSeconds(21)).Resources).State);
    }

    private static NodeSnapshot Node(DateTimeOffset now) => new() { NodeId = "n", State = "online", Inventory = new() { Revision = 1, Resources = [new() { Id = "r", Type = "container", Actions = ["status"] }] }, Resources = new() { InventoryRevision = 1, Resources = [new() { ResourceId = "r", State = "running", ObservedAt = now }] } };
}
