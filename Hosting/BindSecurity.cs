using System.Net;
using ProxmoxMCPSharp.Configuration;

namespace ProxmoxMCPSharp.Hosting;

public static class BindSecurity
{
    public const string Password = "password";
    public const string LoopbackOnly = "none-loopback";
    public const string RemoteOverride = "none-remote-override";
    public const string Blocked = "blocked-missing-password";

    // Must agree with Program's Kestrel binding: anything other than "localhost" or a parseable loopback IP listens on all interfaces.
    public static bool IsLoopbackHost(string? host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);

    public static string Describe(ServerOptions options) =>
        !string.IsNullOrWhiteSpace(options.Password) ? Password
        : IsLoopbackHost(options.Host) ? LoopbackOnly
        : options.AllowUnauthenticatedRemote ? RemoteOverride
        : Blocked;

    public static string Validate(ServerOptions options)
    {
        var mode = Describe(options);
        if (mode == Blocked)
            throw new InvalidOperationException(
                $"Server:Host '{options.Host}' accepts non-loopback connections but Server:Password is blank. " +
                "Set Server:Password, bind Server:Host to localhost, or set Server:AllowUnauthenticatedRemote=true to accept unauthenticated remote access.");
        return mode;
    }
}
