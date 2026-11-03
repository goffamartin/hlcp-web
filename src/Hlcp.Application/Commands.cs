namespace Hlcp.Application;

public sealed record ResourceCommand(string CommandId, string ResourceId, string Action);
public sealed record CommandPreview
{
    public required string Node { get; init; }
    public required string Resource { get; init; }
    public required string Action { get; init; }
    public required ResourceCommand Command { get; init; }
    public bool ReadOnly { get; init; }
    public bool KnownOperation { get; init; }
}
public sealed record CommandResult
{
    public required string CommandId { get; init; }
    public required bool Success { get; init; }
    public required string Code { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}
public sealed record CommandProgress
{
    public required ResourceCommand Command { get; init; }
    public required string State { get; init; }
    public CommandResult? Result { get; init; }
}
