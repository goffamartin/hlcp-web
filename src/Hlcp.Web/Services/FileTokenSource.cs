using Hlcp.Application;

namespace Hlcp.Web.Services;

public sealed class FileTokenSource(string? path) : IHlcpTokenSource
{
    public async ValueTask<string> ReadAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new HlcpReadException(HlcpFailure.CredentialUnavailable);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var bytes = new byte[4098];
        var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, cancellationToken);
        if (count > 4097) throw new HlcpReadException(HlcpFailure.CredentialUnavailable);
        return System.Text.Encoding.UTF8.GetString(bytes, 0, count).TrimEnd('\r', '\n');
    }
}
