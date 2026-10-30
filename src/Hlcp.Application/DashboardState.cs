namespace Hlcp.Application;

// Per-view state, never a cross-user cache or a source of synthetic production data.
public sealed class DashboardState
{
    public NodeSnapshot[]? Nodes { get; private set; }
    public DateTimeOffset? CapturedAt { get; private set; }
    public HlcpFailure? Failure { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public TimeSpan RefreshDelay => TimeSpan.FromSeconds(ConsecutiveFailures == 0 ? 5 : Math.Min(60, 5 * Math.Pow(2, Math.Min(ConsecutiveFailures, 4))));

    public void Accept(IEnumerable<NodeSnapshot> nodes, DateTimeOffset capturedAt)
    {
        Nodes = nodes.ToArray();
        CapturedAt = capturedAt;
        Failure = null;
        ConsecutiveFailures = 0;
    }

    public void Refuse(HlcpFailure failure)
    {
        Failure = failure;
        ConsecutiveFailures = Math.Min(ConsecutiveFailures + 1, 4);
    }

    public bool IsStale(DateTimeOffset now) => Nodes is not null &&
        (Failure is not null || CapturedAt is null || now - CapturedAt > TimeSpan.FromSeconds(20));

    public DashboardSnapshot Project(DateTimeOffset now, string? node = null, IEnumerable<string>? tags = null)
    {
        var nodes = Nodes ?? [];
        // A failed refresh invalidates the freshness of every row, even if the old node was online.
        return DashboardProjection.Build(IsStale(now) ? nodes.Select(item => item with { State = "unknown" }) : nodes, node, tags);
    }
}
