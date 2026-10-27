using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using ProxmoxMCPSharp.Services;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class EndpointTools
{
    [McpServerTool(Name = "get_api_endpoints", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("Show the configured Proxmox API endpoints in preference order, which one answered most recently, and each one's last failure (unreachable, connect-timeout, tls-failure, authentication-rejected, transport-failure) and cooldown. probe=true checks every endpoint now with an authenticated GET /version, ignoring cooldowns.")]
    public static async Task<string> GetEndpoints(ProxmoxClient pve,
        [Description("Check every endpoint now instead of only reporting recorded state.")] bool probe = false,
        CancellationToken ct = default)
    {
        var probes = probe ? await pve.ProbeEndpointsAsync(ct) : null;
        return JsonSerializer.Serialize(new
        {
            activeEndpoint = pve.ActiveEndpoint,
            selection = "Reads use the first available endpoint in order and move on after transport, TLS, connect-timeout or authentication failures. Mutations are sent once to one endpoint and never resent.",
            endpoints = pve.EndpointStatuses(),
            probe = probes?.Select(p => new { endpoint = p.Endpoint, result = p.Result }),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
