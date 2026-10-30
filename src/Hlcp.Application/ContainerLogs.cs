using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Hlcp.Application;

public enum LogFailure { Unavailable, Timeout, Forbidden, NotFound, Unsupported, Capacity, CursorExpired, InvalidQuery, InvalidResponse, InvalidSequence, Interrupted, ByteLimit, CredentialUnavailable }
public sealed class LogReadException(LogFailure failure) : Exception($"Log read failed: {failure}.") { public LogFailure Failure { get; } = failure; }

public sealed record LogQuery(int Tail = 100, string Stream = "both", string Contains = "", string Since = "", string Until = "")
{
    public void Validate()
    {
        if (Tail is < 0 or > 1000 || Stream is not ("both" or "stdout" or "stderr") || Contains is null ||
            Encoding.UTF8.GetByteCount(Contains) > 256 || Contains.Contains('\0') || !LogContract.ValidTimestamp(Since) || !LogContract.ValidTimestamp(Until) ||
            Since.Length > 0 && Until.Length > 0 && DateTimeOffset.Parse(Since, CultureInfo.InvariantCulture) >= DateTimeOffset.Parse(Until, CultureInfo.InvariantCulture))
            throw new LogReadException(LogFailure.InvalidQuery);
    }
}

public sealed record LogRecord
{
    public string Timestamp { get; init; } = "";
    public required string Stream { get; init; }
    public required string Text { get; init; }
    public required bool Truncated { get; init; }
}
public sealed record LogPage
{
    public required int SchemaVersion { get; init; }
    public required string StreamSemantics { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required string SourceSession { get; init; }
    public required LogRecord[] Records { get; init; }
    public string? NextCursor { get; init; }
    public required string Code { get; init; }
    public required bool Gap { get; init; }
    public required bool Truncated { get; init; }
}
public sealed record LogEvent
{
    public required int SchemaVersion { get; init; }
    public required string StreamSemantics { get; init; }
    public required string RequestId { get; init; }
    public required ulong Sequence { get; init; }
    public required string Kind { get; init; }
    public LogRecord? Record { get; init; }
    public string? Code { get; init; }
    public required bool Gap { get; init; }
}

internal static partial class LogContract
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();
    public static bool ValidTimestamp(string? value) => value is not null && (value.Length == 0 || value.Length <= 40 && TimestampPattern().IsMatch(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    public static bool Identifier(string? value, int length = 128) => value is { Length: > 0 } && value.Length <= length && value is not "." and not ".." && value.All(c => c > 0x20 && c < 0x7f);
    public static bool Code(string? value) => value is "OK" or "NOT_ALLOWED" or "UNSUPPORTED" or "CAPACITY" or "RUNTIME_FAILED" or "TIME_LIMIT" or "BYTE_LIMIT" or "CANCELED" or "BACKPRESSURE" or "SESSION_LOST" or "FORBIDDEN" or "INVALID_SEQUENCE";
    public static bool Record(LogRecord? record) => record is not null && record.Stream is "stdout" or "stderr" && record.Text is not null && !record.Text.Contains('\0') && Encoding.UTF8.GetByteCount(record.Text) <= 4096 && ValidTimestamp(record.Timestamp);
}

public sealed class LogBuffer
{
    public const int MaximumRecords = 500;
    public const int MaximumBytes = 512 * 1024;
    private readonly Queue<(LogRecord Record, int Bytes)> rows = new();
    public int Bytes { get; private set; }
    public long Removed { get; private set; }
    public bool HasTruncatedRecords { get; private set; }
    public IReadOnlyList<LogRecord> Records => rows.Select(row => row.Record).ToArray();
    public void Add(LogRecord record)
    {
        if (!LogContract.Record(record)) throw new LogReadException(LogFailure.InvalidResponse);
        var bytes = Math.Max(Encoding.UTF8.GetByteCount(record.Text), record.Text.Length * 2) + record.Timestamp.Length * 2 + 128;
        rows.Enqueue((record, bytes)); Bytes += bytes;
        HasTruncatedRecords |= record.Truncated;
        while (rows.Count > MaximumRecords || Bytes > MaximumBytes)
        { var removed = rows.Dequeue(); Bytes -= removed.Bytes; Removed++; }
    }
    public void Clear() { rows.Clear(); Bytes = 0; Removed = 0; HasTruncatedRecords = false; }
    public static string Display(string text) => string.Concat(text.Select(c => char.IsControl(c) && c != '\t' ? '�' : c));
}
