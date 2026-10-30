namespace Hlcp.Application;

public enum ResourceDisplayState { Running, Stopped, Error, Unknown, Stale }

public sealed record ResourceView(string NodeId, ResourceDescriptor Resource, ResourceDisplayState State);

public sealed record DashboardCounts(int OnlineNodes, int DegradedNodes, int OfflineNodes, int UnknownNodes,
    int RunningContainers, int StoppedContainers, int ErrorContainers, int UnknownContainers, int StaleContainers);

public sealed record DashboardSnapshot(DashboardCounts Counts, IReadOnlyList<ResourceView> Resources);

public static class DashboardProjection
{
    // Input must already be scoped by the controller. Presentation filtering grants no access.
    public static DashboardSnapshot Build(IEnumerable<NodeSnapshot> nodes, string? nodeId = null, IEnumerable<string>? tags = null)
    {
        var selectedTags = ResourceTags.Validate(tags);
        var selected = nodes.Where(node => nodeId is null || node.NodeId == nodeId)
            .Where(node => selectedTags.Length == 0 || (node.Inventory.Resources ?? []).Any(resource => ResourceTags.Matches(resource.Tags, selectedTags)))
            .ToArray();
        var rows = new List<ResourceView>();
        foreach (var node in selected)
        {
            var observations = (node.Resources.Resources ?? []).GroupBy(item => item.ResourceId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            foreach (var resource in node.Inventory.Resources ?? [])
            {
                if (!ResourceTags.Matches(resource.Tags, selectedTags)) continue;
                observations.TryGetValue(resource.Id, out var observation);
                var state = node.State != "online" || observation?.Stale == true ||
                    observation is not null && node.Resources.InventoryRevision != node.Inventory.Revision
                    ? ResourceDisplayState.Stale
                    : observation is null || observation.ObservedAt == default
                        ? ResourceDisplayState.Unknown
                        : observation.State switch
                        {
                            "running" => ResourceDisplayState.Running,
                            "stopped" => ResourceDisplayState.Stopped,
                            "error" => ResourceDisplayState.Error,
                            _ => ResourceDisplayState.Unknown
                        };
                rows.Add(new(node.NodeId, resource, state));
            }
        }
        var unique = rows.DistinctBy(row => (row.NodeId, row.Resource.Id)).ToArray();
        int Count(ResourceDisplayState state) => unique.Count(row => row.Resource.Type == "container" && row.State == state);
        return new(new(selected.Count(node => node.State == "online"), selected.Count(node => node.State == "degraded"),
            selected.Count(node => node.State == "offline"), selected.Count(node => node.State is not ("online" or "degraded" or "offline")),
            Count(ResourceDisplayState.Running), Count(ResourceDisplayState.Stopped), Count(ResourceDisplayState.Error),
            Count(ResourceDisplayState.Unknown), Count(ResourceDisplayState.Stale)), unique);
    }
}
