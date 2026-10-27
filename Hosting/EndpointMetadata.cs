using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ProxmoxMCPSharp.Services;

namespace ProxmoxMCPSharp.Hosting;

public static class EndpointMetadata
{
    public const string EndpointsKey = "proxmox/endpoints";
    public const string FailoverKey = "proxmox/failover";

    /// <summary>Reports in each tool result's _meta which Proxmox endpoints served it, and any endpoints skipped on the way.</summary>
    public static IMcpServerBuilder WithProxmoxEndpointMetadata(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
        {
            using var scope = ProxmoxCallScope.Begin();
            var result = await next(context, ct);
            if (scope.Endpoints.Count > 0)
            {
                result.Meta ??= new JsonObject();
                result.Meta[EndpointsKey] = new JsonArray(scope.Endpoints.Select(e => (JsonNode)JsonValue.Create(e)).ToArray());
                if (scope.Failovers.Count > 0)
                    result.Meta[FailoverKey] = new JsonArray(scope.Failovers
                        .Select(f => (JsonNode)new JsonObject { ["endpoint"] = f.Endpoint, ["failure"] = f.Failure }).ToArray());
            }
            return result;
        }));
}
