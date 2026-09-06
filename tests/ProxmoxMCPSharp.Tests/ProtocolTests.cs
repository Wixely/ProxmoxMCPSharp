using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ProxmoxMCPSharp.Tools;
using Xunit;

namespace ProxmoxMCPSharp.Tests;

public sealed class ProtocolTests
{
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
