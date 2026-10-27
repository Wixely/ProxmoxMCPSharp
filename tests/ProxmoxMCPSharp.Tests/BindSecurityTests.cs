using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ProxmoxMCPSharp.Configuration;
using ProxmoxMCPSharp.Hosting;
using Xunit;

namespace ProxmoxMCPSharp.Tests;

public sealed class BindSecurityTests
{
    public static readonly TheoryData<string> LoopbackHosts = new() { "localhost", "LocalHost", "127.0.0.1", "127.0.0.2", "::1" };
    public static readonly TheoryData<string> RemoteHosts = new() { "0.0.0.0", "::", "*", "+", "192.168.1.10", "10.0.0.5", "fe80::1", "pve-mcp.lan", "" };

    [Theory]
    [MemberData(nameof(LoopbackHosts))]
    public void LoopbackBindWithBlankPasswordStarts(string host)
    {
        foreach (var password in new[] { "", " ", "\t" })
            Assert.Equal(BindSecurity.LoopbackOnly, BindSecurity.Validate(new ServerOptions { Host = host, Password = password }));
    }

    [Theory]
    [MemberData(nameof(RemoteHosts))]
    public void RemoteBindWithBlankPasswordFailsClosed(string host)
    {
        foreach (var password in new[] { "", " ", "\t" })
        {
            var error = Assert.Throws<InvalidOperationException>(() => BindSecurity.Validate(new ServerOptions { Host = host, Password = password }));
            Assert.Contains("Server:Password", error.Message);
            Assert.Equal(BindSecurity.Blocked, BindSecurity.Describe(new ServerOptions { Host = host, Password = password }));
        }
    }

    [Theory]
    [MemberData(nameof(RemoteHosts))]
    public void RemoteBindIsAllowedWithPasswordOrExplicitOverride(string host)
    {
        Assert.Equal(BindSecurity.Password, BindSecurity.Validate(new ServerOptions { Host = host, Password = "secret" }));
        Assert.Equal(BindSecurity.RemoteOverride, BindSecurity.Validate(new ServerOptions { Host = host, AllowUnauthenticatedRemote = true }));
    }

    [Fact]
    public void OverrideDefaultsToFalse() => Assert.False(new ServerOptions().AllowUnauthenticatedRemote);

    [Fact]
    public void DescribeNeverRevealsThePassword() =>
        Assert.DoesNotContain("hunter2", BindSecurity.Describe(new ServerOptions { Host = "0.0.0.0", Password = "hunter2" }));

    [Theory]
    [InlineData("secret", null, "203.0.113.5", false, 401)]
    [InlineData("secret", "Bearer wrong", "127.0.0.1", false, 401)]
    [InlineData("secret", "Bearer secret", "203.0.113.5", false, 200)]
    [InlineData("secret", "Basic", "203.0.113.5", false, 200)]
    [InlineData("", null, "127.0.0.1", false, 200)]
    [InlineData("", null, "::1", false, 200)]
    [InlineData("", null, "203.0.113.5", false, 403)]
    [InlineData(" ", null, "192.168.1.20", false, 403)]
    [InlineData("", null, null, false, 403)]
    [InlineData("", null, "203.0.113.5", true, 200)]
    public async Task MiddlewareRequiresPasswordOrLoopbackClient(string password, string? authorization, string? remote, bool allowRemote, int expected)
    {
        var options = new ServerOptions { Password = password, AllowUnauthenticatedRemote = allowRemote };
        var reached = false;
        var middleware = new McpPasswordMiddleware(_ => { reached = true; return Task.CompletedTask; }, new Monitor(options));
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        if (authorization == "Basic") authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("any:" + password));
        if (authorization is not null) context.Request.Headers.Authorization = authorization;

        await middleware.InvokeAsync(context);

        Assert.Equal(expected == 200, reached);
        Assert.Equal(expected, context.Response.StatusCode);
    }

    [Fact]
    public async Task MiddlewareLeavesNonMcpPathsAlone()
    {
        var reached = false;
        var middleware = new McpPasswordMiddleware(_ => { reached = true; return Task.CompletedTask; }, new Monitor(new ServerOptions()));
        var context = new DefaultHttpContext();
        context.Request.Path = "/healthz";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");
        await middleware.InvokeAsync(context);
        Assert.True(reached);
    }

    private sealed class Monitor(ServerOptions value) : IOptionsMonitor<ServerOptions>
    {
        public ServerOptions CurrentValue => value;
        public ServerOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ServerOptions, string?> listener) => null;
    }
}
