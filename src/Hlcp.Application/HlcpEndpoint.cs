namespace Hlcp.Application;

internal static class HlcpEndpoint
{
    public static void Validate(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "http" || !endpoint.IsLoopback ||
            endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("The controller endpoint must be a plain loopback HTTP origin.", nameof(endpoint));
    }
}
