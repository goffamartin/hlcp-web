using Hlcp.Application;

namespace Hlcp.Tests;

public sealed class DashboardProjectionTests
{
    [Theory]
    [InlineData("online", false, "running", ResourceDisplayState.Running)]
    [InlineData("offline", false, "running", ResourceDisplayState.Stale)]
    [InlineData("degraded", false, "running", ResourceDisplayState.Stale)]
    [InlineData("online", true, "running", ResourceDisplayState.Stale)]
    [InlineData("online", false, "stopped", ResourceDisplayState.Stopped)]
    [InlineData("online", false, "error", ResourceDisplayState.Error)]
    [InlineData("online", false, "new-future-state", ResourceDisplayState.Unknown)]
    public void EffectiveStateDoesNotPretendOldRunningMeansHealthy(string nodeState, bool stale, string state, ResourceDisplayState expected)
    {
        var node = Node(nodeState, stale, state);
        var result = DashboardProjection.Build([node]);
        Assert.Equal(expected, Assert.Single(result.Resources).State);
        Assert.Equal(expected == ResourceDisplayState.Running ? 1 : 0, result.Counts.RunningContainers);
    }

    [Fact]
    public void TagFilteringUsesANDAndCountsUniqueResources()
    {
        var node = Node();
        var resource = node.Inventory.Resources![0];
        node = node with { Inventory = node.Inventory with { Resources = [resource, resource] } };
        var result = DashboardProjection.Build([node], tags: ["kostka", "production"]);
        Assert.Single(result.Resources);
        Assert.Equal(1, result.Counts.RunningContainers);
        Assert.Empty(DashboardProjection.Build([node], tags: ["kostka", "frontend"]).Resources);
        Assert.Equal(0, DashboardProjection.Build([node], tags: ["medaile"]).Counts.OnlineNodes);
    }

    [Fact]
    public void MissingObservationIsUnknownAndWrongRevisionIsNotFresh()
    {
        var node = Node();
        var missing = node with { Resources = node.Resources with { Resources = [] } };
        Assert.Equal(ResourceDisplayState.Unknown, Assert.Single(DashboardProjection.Build([missing]).Resources).State);
        var wrong = node with { Resources = node.Resources with { InventoryRevision = 2 } };
        Assert.Equal(ResourceDisplayState.Stale, Assert.Single(DashboardProjection.Build([wrong]).Resources).State);
        Assert.Empty(DashboardProjection.Build([]).Resources);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("bad tag")]
    [InlineData("-bad")]
    [InlineData("český")]
    [InlineData("")]
    public void InvalidTagsAreRejected(string tag) => Assert.Throws<ArgumentException>(() => ResourceTags.Validate([tag]));

    private static NodeSnapshot Node(string state = "online", bool stale = false, string resourceState = "running") => new()
    {
        NodeId = "n", State = state,
        Inventory = new() { Revision = 1, Resources = [new() { Id = "app", Type = "container", Actions = ["status"], Tags = ["kostka", "production"] }] },
        Resources = new() { InventoryRevision = 1, Resources = [new() { ResourceId = "app", State = resourceState, Stale = stale, ObservedAt = DateTimeOffset.UtcNow }] }
    };
}
