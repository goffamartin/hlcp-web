namespace Hlcp.Application;

// HLCP 1.1 inventory is the source of truth; these DTOs contain no desired state.
public sealed record NodeSnapshot
{
    public required string NodeId { get; init; }
    public required string State { get; init; }
    public string? AgentVersion { get; init; }
    public DateTimeOffset LastActivity { get; init; }
    public required ResourceInventory Inventory { get; init; }
    public required ObservationSnapshot Resources { get; init; }
}

public sealed record ResourceInventory
{
    public ulong Revision { get; init; }
    public ResourceDescriptor[]? Resources { get; init; }
}

public sealed record ResourceDescriptor
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string[] Actions { get; init; }
    public string[]? Tags { get; init; }
}

public sealed record ObservationSnapshot
{
    public ulong InventoryRevision { get; init; }
    public ResourceObservation[]? Resources { get; init; }
}

public sealed record ResourceObservation
{
    public required string ResourceId { get; init; }
    public required string State { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public bool Stale { get; init; }
}
