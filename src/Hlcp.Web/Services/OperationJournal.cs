using System.Text.Json;
using Hlcp.Application;

namespace Hlcp.Web.Services;

public sealed record WebOperation(string Id, string Node, string Resource, string Action, DateTimeOffset CreatedAt)
{
    public ResourceCommand Command => new(Id, Resource, Action);
}
public sealed record OperationStatus(WebOperation Operation, bool Submitted, CommandResult? Result);

// One native host process. Immutable files and a create-once admission marker avoid a DB
// while ensuring a request is never automatically resubmitted after an ambiguous failure.
public sealed class OperationJournal(string directory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 8 };

    public async Task<WebOperation> PrepareAsync(string node, string resource, string action, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            // Never evict unresolved IDs to make room for new mutations.
            if (Directory.EnumerateFiles(directory, "*.operation.json").Take(10000).Count() >= 10000)
                throw new IOException("Operation journal is full.");
            var operation = new WebOperation(Guid.NewGuid().ToString("N"), node, resource, action, DateTimeOffset.UtcNow);
            await WriteNewAsync(PathFor(operation.Id, ".operation.json"), operation, cancellationToken);
            return operation;
        }
        finally { gate.Release(); }
    }

    public async Task<WebOperation> ReadAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(PathFor(id, ".operation.json"));
        if (stream.Length > 4096) throw new IOException("Invalid operation metadata.");
        var result = await JsonSerializer.DeserializeAsync<WebOperation>(stream, Json, cancellationToken) ?? throw new IOException("Invalid operation metadata.");
        if (result.Id != id) throw new IOException("Invalid operation metadata.");
        return result;
    }

    public async Task<bool> MarkSubmittedAsync(string id)
    {
        var path = PathFor(id, ".submitted");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        FileStream stream;
        // Claim the final marker with exclusive creation. File.Move(overwrite: false)
        // may race on Unix between its existence check and rename. Marker contents
        // are never read: even a partial marker must prevent a second submission.
        try { stream = new FileStream(path, options); }
        catch (IOException) when (File.Exists(path)) { return false; }
        await using (stream)
        {
            await JsonSerializer.SerializeAsync(stream, new { SubmittedAt = DateTimeOffset.UtcNow }, Json);
            await stream.FlushAsync();
            stream.Flush(flushToDisk: true);
        }
        return true;
    }

    public async Task SaveResultAsync(string id, CommandResult result)
    {
        // Adapter output is returned to the current UI only, never persisted in the journal.
        try { await WriteNewAsync(PathFor(id, ".result.json"), result with { Message = null }, CancellationToken.None); }
        catch (IOException) when (File.Exists(PathFor(id, ".result.json"))) { }
    }

    public async Task<OperationStatus[]> ListAsync(CancellationToken cancellationToken, string? node = null, string? resource = null)
    {
        if (!Directory.Exists(directory)) return [];
        var operations = new List<WebOperation>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.operation.json").Take(10000))
        {
            var id = Path.GetFileName(file).Split('.')[0];
            operations.Add(await ReadAsync(id, cancellationToken));
        }
        var result = new List<OperationStatus>();
        foreach (var item in operations.Where(item => node is null || item.Node == node).Where(item => resource is null || item.Resource == resource).OrderByDescending(item => item.CreatedAt).Take(50))
        {
            CommandResult? terminal = null;
            var file = PathFor(item.Id, ".result.json");
            if (File.Exists(file))
            {
                await using var stream = File.OpenRead(file);
                if (stream.Length > 4096) throw new IOException("Invalid operation result.");
                terminal = await JsonSerializer.DeserializeAsync<CommandResult>(stream, Json, cancellationToken);
            }
            result.Add(new(item, File.Exists(PathFor(item.Id, ".submitted")), terminal));
        }
        return result.ToArray();
    }

    private string PathFor(string id, string suffix)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid web operation ID.");
        return Path.Combine(directory, id + suffix);
    }

    private static async Task WriteNewAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
