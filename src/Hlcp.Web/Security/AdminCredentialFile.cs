using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace Hlcp.Web.Security;

// PasswordHasher is a framework crypto helper only: no Identity accounts, stores or database.
public sealed class AdminCredentialFile(string path)
{
    private static readonly object Subject = new();
    private static readonly PasswordHasher<object> Hasher = new();

    private async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            var bytes = new byte[4097];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, cancellationToken);
            return count is > 0 and <= 4096 ? Encoding.UTF8.GetString(bytes, 0, count).Trim() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public async Task<string?> VerifyAsync(string? password, CancellationToken cancellationToken)
    {
        if (password is not { Length: >= 16 and <= 1024 }) return null;
        var hash = await ReadAsync(cancellationToken);
        if (hash is null) return null;
        try
        {
            return Hasher.VerifyHashedPassword(Subject, hash, password) != PasswordVerificationResult.Failed ? Fingerprint(hash) : null;
        }
        catch (FormatException) { return null; }
    }

    public async Task<string?> VersionAsync(CancellationToken cancellationToken) =>
        await ReadAsync(cancellationToken) is { } hash ? Fingerprint(hash) : null;

    public async Task WriteAsync(string password, CancellationToken cancellationToken = default)
    {
        if (password.Length is < 16 or > 1024) throw new ArgumentException("Password must have 16–1024 characters.");
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
                await stream.WriteAsync(Encoding.UTF8.GetBytes(Hasher.HashPassword(Subject, password)), cancellationToken);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
