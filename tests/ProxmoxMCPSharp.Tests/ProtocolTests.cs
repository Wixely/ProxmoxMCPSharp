using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ProxmoxMCPSharp.Hosting;
using ProxmoxMCPSharp.Tools;
using Xunit;

namespace ProxmoxMCPSharp.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task StructuredConfigurationObjectAndLegacyPreviewWorkOverMcp()
    {
        await using var fixture = new ManagementFixture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Client);
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly(typeof(TaskTools).Assembly);
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        using var http = app.GetTestClient();
        await using var transport = new HttpClientTransport(new() { Endpoint = new Uri("http://localhost/mcp") }, http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        var qemu = tools.Single(t => t.Name == "update_qemu_config").ProtocolTool;
        foreach (var name in new[] { "clone_lxc", "migrate_lxc", "clone_qemu", "migrate_qemu" })
        {
            var transfer = tools.Single(t => t.Name == name).ProtocolTool;
            Assert.False(transfer.Annotations!.ReadOnlyHint);
            Assert.False(transfer.Annotations.IdempotentHint);
            Assert.DoesNotContain("dryRun", transfer.InputSchema.GetProperty("required").EnumerateArray().Select(v => v.GetString()));
            Assert.DoesNotContain("waitSeconds", transfer.InputSchema.GetProperty("required").EnumerateArray().Select(v => v.GetString()));
        }
        Assert.Equal(new[] { "vmid" }, qemu.InputSchema.GetProperty("required").EnumerateArray().Select(v => v.GetString()));
        foreach (var kind in new[] { "qemu", "lxc" })
        {
            var result = await client.CallToolAsync("update_" + kind + "_config", new Dictionary<string, object?>
            {
                ["vmid"] = 100,
                ["expectedDigest"] = ManagementFixture.Digest,
                ["fields"] = new Dictionary<string, object?> { [kind == "qemu" ? "name" : "hostname"] = "guest-a", ["onboot"] = true }
            }, cancellationToken: timeout.Token);
            Assert.NotEqual(true, result.IsError);
        }
        var legacy = new Dictionary<string, object?> { ["vmid"] = 100, ["keyValuePairs"] = "name=guest-a", ["node"] = "node-a", ["dryRun"] = true };
        Assert.NotEqual(true, (await client.CallToolAsync("update_qemu_config", legacy, cancellationToken: timeout.Token)).IsError);
        Assert.All(fixture.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        var count = fixture.Requests.Count;
        legacy.Remove("dryRun");
        Assert.True((await client.CallToolAsync("update_qemu_config", legacy, cancellationToken: timeout.Token)).IsError);
        Assert.Equal(count, fixture.Requests.Count);
        await app.StopAsync();
    }

    [Fact]
    public async Task StructuredConfigurationFieldsAdvertiseConstrainedObjectSchema()
    {
        await using var fixture = new ManagementFixture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Client);
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly(typeof(TaskTools).Assembly).WithStructuredConfigurationSchemas();
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        using var http = app.GetTestClient();
        await using var transport = new HttpClientTransport(new() { Endpoint = new Uri("http://localhost/mcp") }, http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);

        foreach (var tool in tools) AssertNoBooleanSchemas(tool.ProtocolTool.InputSchema, tool.Name);
        foreach (var (kind, accepted, rejected) in new[]
        {
            ("lxc", new[] { "hostname", "onboot", "swap", "net0", "net31", "mp0", "rootfs", "delete", "digest" }, new[] { "name", "sockets", "net32", "scsi0", "unprivileged", "features", "lxc" }),
            ("qemu", new[] { "name", "onboot", "sockets", "balloon", "net0", "scsi30", "virtio15", "ide3", "delete" }, new[] { "hostname", "swap", "scsi31", "mp0", "efidisk0", "args", "hookscript" }),
        })
        {
            var schema = tools.Single(t => t.Name == "update_" + kind + "_config").ProtocolTool.InputSchema;
            var fields = schema.GetProperty("properties").GetProperty("fields");
            Assert.Equal("object", fields.GetProperty("type").GetString());
            Assert.Equal("string", fields.GetProperty("additionalProperties").GetProperty("type").GetString());
            var properties = fields.GetProperty("properties");
            Assert.Equal("boolean", properties.GetProperty("onboot").GetProperty("type").GetString());
            Assert.Equal("integer", properties.GetProperty("cores").GetProperty("type").GetString());
            Assert.Equal("number", properties.GetProperty("cpulimit").GetProperty("type").GetString());
            var names = new Regex(fields.GetProperty("propertyNames").GetProperty("pattern").GetString()!);
            Assert.All(accepted, key => Assert.Matches(names, key));
            Assert.All(rejected, key => Assert.DoesNotMatch(names, key));
        }
        var lxc = tools.Single(t => t.Name == "update_lxc_config").ProtocolTool.InputSchema;
        Assert.Contains("fields", lxc.GetProperty("required").EnumerateArray().Select(v => v.GetString()));
        Assert.Contains("expectedDigest", lxc.GetProperty("required").EnumerateArray().Select(v => v.GetString()));
        Assert.True(lxc.GetProperty("properties").GetProperty("dryRun").GetProperty("default").GetBoolean());

        var valid = await client.CallToolAsync("update_lxc_config", new Dictionary<string, object?>
        {
            ["vmid"] = 100,
            ["expectedDigest"] = ManagementFixture.Digest,
            ["fields"] = new Dictionary<string, object?> { ["hostname"] = "guest-a", ["onboot"] = true, ["description"] = "notes" }
        }, cancellationToken: timeout.Token);
        Assert.NotEqual(true, valid.IsError);
        foreach (var fields in new object?[]
        {
            new[] { "hostname" },
            "hostname=guest-a",
            new Dictionary<string, object?> { ["hostname"] = null },
            new Dictionary<string, object?> { ["tags"] = new[] { "a" } },
            new Dictionary<string, object?> { ["onboot"] = new Dictionary<string, object?> { ["value"] = true } },
            new Dictionary<string, object?> { ["unprivileged"] = false },
        })
        {
            var result = await client.CallToolAsync("update_lxc_config", new Dictionary<string, object?>
            {
                ["vmid"] = 100,
                ["expectedDigest"] = ManagementFixture.Digest,
                ["fields"] = fields
            }, cancellationToken: timeout.Token);
            Assert.True(result.IsError);
        }
        Assert.All(fixture.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        await app.StopAsync();
    }

    private static void AssertNoBooleanSchemas(JsonElement schema, string tool)
    {
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        foreach (var keyword in new[] { "additionalProperties", "items", "propertyNames" })
            if (schema.TryGetProperty(keyword, out var child)) AssertNoBooleanSchemas(child, tool + "." + keyword);
        if (schema.TryGetProperty("properties", out var properties))
            foreach (var property in properties.EnumerateObject()) AssertNoBooleanSchemas(property.Value, tool + "." + property.Name);
    }

    [Fact]
    public async Task ResizeProtocolDefaultsToPreviewAndDeniesClosedWriteGates()
    {
        await using var fixture = new ManagementFixture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Client);
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly(typeof(TaskTools).Assembly);
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        using var http = app.GetTestClient();
        await using var transport = new HttpClientTransport(new() { Endpoint = new Uri("http://localhost/mcp") }, http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tool = (await client.ListToolsAsync(cancellationToken: timeout.Token)).Single(t => t.Name == "resize_qemu_disk").ProtocolTool;
        Assert.False(tool.Annotations!.ReadOnlyHint);
        Assert.False(tool.Annotations.IdempotentHint);
        Assert.Equal(new[] { "vmid", "disk", "size", "expectedDigest" }, tool.InputSchema.GetProperty("required").EnumerateArray().Select(v => v.GetString()));
        var args = new Dictionary<string, object?> { ["vmid"] = 100, ["disk"] = "scsi0", ["size"] = "+10G", ["expectedDigest"] = ManagementFixture.Digest };
        var result = await client.CallToolAsync("resize_qemu_disk", args, cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        Assert.All(fixture.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        var count = fixture.Requests.Count;
        args["dryRun"] = false;
        result = await client.CallToolAsync("resize_qemu_disk", args, cancellationToken: timeout.Token);
        Assert.True(result.IsError);
        Assert.Equal(count, fixture.Requests.Count);
        fixture.Config["boot"] = "order=scsi1";
        var diskTools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        foreach (var name in new[] { "add_qemu_disk", "attach_qemu_disk", "detach_qemu_disk", "move_qemu_disk", "move_lxc_volume", "add_lxc_mount_point", "detach_lxc_mount_point" })
        {
            var diskTool = diskTools.Single(t => t.Name == name).ProtocolTool;
            Assert.False(diskTool.Annotations!.ReadOnlyHint);
            Assert.False(diskTool.Annotations.IdempotentHint);
            Assert.DoesNotContain("dryRun", diskTool.InputSchema.GetProperty("required").EnumerateArray().Select(v => v.GetString()));
        }
        var detach = new Dictionary<string, object?> { ["vmid"] = 100, ["disk"] = "scsi0", ["expectedDigest"] = ManagementFixture.Digest };
        result = await client.CallToolAsync("detach_qemu_disk", detach, cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        Assert.All(fixture.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        count = fixture.Requests.Count;
        detach["dryRun"] = false;
        result = await client.CallToolAsync("detach_qemu_disk", detach, cancellationToken: timeout.Token);
        Assert.True(result.IsError);
        Assert.Equal(count, fixture.Requests.Count);
        await app.StopAsync();
    }

    [Fact]
    public async Task AccessToolsAdvertiseExplicitInputsAndPreviewByDefault()
    {
        await using var fixture = new AccessFixture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Client);
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly(typeof(TaskTools).Assembly);
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        using var http = app.GetTestClient();
        await using var transport = new HttpClientTransport(new() { Endpoint = new Uri("http://localhost/mcp") }, http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        string[] Required(string name) => tools.Single(t => t.Name == name).ProtocolTool.InputSchema.GetProperty("required").EnumerateArray().Select(v => v.GetString()!).ToArray();

        foreach (var name in new[] { "list_access_users", "list_access_roles", "list_access_acl", "list_user_tokens", "get_access_permissions" })
            Assert.True(tools.Single(t => t.Name == name).ProtocolTool.Annotations!.ReadOnlyHint);
        foreach (var name in new[] { "create_access_user", "create_user_token", "set_access_acl", "remove_access_acl", "delete_user_token", "delete_access_user", "provision_service_identity" })
        {
            var tool = tools.Single(t => t.Name == name).ProtocolTool;
            Assert.False(tool.Annotations!.ReadOnlyHint);
            Assert.False(tool.Annotations.IdempotentHint);
            Assert.DoesNotContain("dryRun", Required(name));
            Assert.True(tool.InputSchema.GetProperty("properties").GetProperty("dryRun").GetProperty("default").GetBoolean());
        }
        Assert.Equal(new[] { "path", "roleId", "authId" }, Required("set_access_acl"));
        Assert.Equal(new[] { "userId", "tokenId", "roleId", "path" }, Required("provision_service_identity"));

        var args = new Dictionary<string, object?> { ["userId"] = "monitor@pve", ["tokenId"] = "monitor", ["roleId"] = "PVEAuditor", ["path"] = "/" };
        var preview = await client.CallToolAsync("provision_service_identity", args, cancellationToken: timeout.Token);
        Assert.NotEqual(true, preview.IsError);
        args["dryRun"] = false;
        Assert.True((await client.CallToolAsync("provision_service_identity", args, cancellationToken: timeout.Token)).IsError);
        Assert.Empty(fixture.Mutations);

        fixture.EnableWrites();
        var created = await client.CallToolAsync("provision_service_identity", args, cancellationToken: timeout.Token);
        Assert.NotEqual(true, created.IsError);
        Assert.Contains(AccessFixture.Secret, JsonSerializer.Serialize(created));
        var listing = await client.CallToolAsync("list_user_tokens", new Dictionary<string, object?> { ["userId"] = "monitor@pve" }, cancellationToken: timeout.Token);
        Assert.DoesNotContain(AccessFixture.Secret, JsonSerializer.Serialize(listing));
        Assert.True((await client.CallToolAsync("provision_service_identity", args, cancellationToken: timeout.Token)).IsError);
        Assert.Equal(3, fixture.Mutations.Count());
        await app.StopAsync();
    }

    [Fact]
    public async Task DiscoveryAndOldCallsWorkInReadOnlyMode()
    {
        await using var fixture = new Fixture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Client);
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly(typeof(TaskTools).Assembly);
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        using var http = app.GetTestClient();
        await using var transport = new HttpClientTransport(new() { Endpoint = new Uri("http://localhost/mcp") }, http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        var tasks = tools.Single(t => t.Name == "list_node_tasks");
        var schema = tasks.ProtocolTool.InputSchema;
        if (schema.TryGetProperty("required", out var required)) Assert.Empty(required.EnumerateArray());
        Assert.True(tasks.ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.True(tasks.ProtocolTool.Annotations.OpenWorldHint);
        foreach (var name in new[] { "get_node_disk_smart", "list_node_zfs_pools", "get_node_zfs_pool_status", "list_node_directory_mounts", "get_storage_diagnostics", "get_backup_coverage" })
            Assert.Contains(tools, t => t.Name == name);
        var log = tools.Single(t => t.Name == "get_task_log").ProtocolTool.InputSchema;
        Assert.Equal(new[] { "upid" }, log.GetProperty("required").EnumerateArray().Select(v => v.GetString()).ToArray());
        var result = await client.CallToolAsync("list_node_tasks", new Dictionary<string, object?> { ["node"] = "node-a", ["running"] = false, ["limit"] = 50, ["source"] = "all" }, cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        result = await client.CallToolAsync("get_task_log", new Dictionary<string, object?> { ["upid"] = "UPID:node-a:123:", ["node"] = "node-a", ["limit"] = 50 }, cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        result = await client.CallToolAsync("list_node_disks", new Dictionary<string, object?> { ["node"] = "node-a" }, cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(3, fixture.Requests.Count);
        foreach (var call in new (string Name, Dictionary<string, object?> Args)[] {
            ("get_node_disk_smart", new() { ["disk"] = "/dev/sda" }),
            ("list_node_zfs_pools", new()),
            ("get_node_zfs_pool_status", new() { ["pool"] = "pool-a" }),
            ("list_node_directory_mounts", new()),
            ("get_storage_diagnostics", new() { ["storage"] = "backup-a" }),
            ("get_backup_coverage", new() { ["node"] = "node-a", ["vmids"] = new[] { 100 }, ["storages"] = new[] { "backup-a" } }) })
        {
            result = await client.CallToolAsync(call.Name, call.Args, cancellationToken: timeout.Token);
            Assert.NotEqual(true, result.IsError);
        }
        await app.StopAsync();
    }
}
