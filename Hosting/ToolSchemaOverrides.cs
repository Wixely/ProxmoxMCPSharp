using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using ProxmoxMCPSharp.Services;

namespace ProxmoxMCPSharp.Hosting;

public static class ToolSchemaOverrides
{
    // JsonElement parameters export as the boolean schema `true`, which hides the real contract and is rejected by some clients (e.g. LM Studio).
    public static IMcpServerBuilder WithStructuredConfigurationSchemas(this IMcpServerBuilder builder)
    {
        builder.Services.PostConfigure<McpServerOptions>(options =>
        {
            if (options.ToolCollection is not { } tools) return;
            foreach (var tool in tools)
            {
                var kind = tool.ProtocolTool.Name switch { "update_qemu_config" => "qemu", "update_lxc_config" => "lxc", _ => null };
                if (kind is null) continue;
                var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
                schema["properties"]!["fields"] = StructuredConfigurationService.FieldsSchema(kind);
                tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
            }
        });
        return builder;
    }
}
