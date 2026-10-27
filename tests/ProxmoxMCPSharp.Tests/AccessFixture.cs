using System.Net;
using System.Text;
using System.Text.Json;
using ProxmoxMCPSharp.Configuration;
using ProxmoxMCPSharp.Services;

namespace ProxmoxMCPSharp.Tests;

/// <summary>In-memory model of the PVE /access API: realms, users, privilege-separated tokens, roles and ACLs.</summary>
internal sealed class AccessFixture : HttpMessageHandler, IAsyncDisposable
{
    public const string Secret = "5ec2e7a1-0000-4000-8000-c0ffee000001";
    public const string Auditor = "Datastore.Audit,Mapping.Audit,Pool.Audit,SDN.Audit,Sys.Audit,VM.Audit";
    public ProxmoxOptions Options { get; } = new() { DefaultNode = "node-a", ApiTokenId = "fixture@pve!reader", ApiTokenSecret = "synthetic-token" };
    public ProxmoxClient Client { get; }
    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];
    public IEnumerable<(HttpMethod Method, string Path, string? Body)> Mutations => Requests.Where(r => r.Method != HttpMethod.Get);
    public Dictionary<string, string> Realms { get; } = new() { ["pve"] = "pve", ["pam"] = "pam" };
    public Dictionary<string, string?> Users { get; } = new(StringComparer.OrdinalIgnoreCase) { ["root@pam"] = null, ["fixture@pve"] = null };
    public Dictionary<string, Dictionary<string, bool>> Tokens { get; } = new(StringComparer.OrdinalIgnoreCase) { ["fixture@pve"] = new() { ["reader"] = true } };
    public Dictionary<string, string> Roles { get; } = new()
    {
        ["PVEAuditor"] = Auditor,
        ["PVEVMUser"] = "VM.Audit,VM.Console,VM.PowerMgmt",
        ["Administrator"] = "Permissions.Modify,Sys.Console,Sys.Modify,User.Modify,VM.Allocate",
    };
    public List<(string Path, string Type, string Ugid, string Role, bool Propagate)> Acl { get; } = [];
    /// <summary>Return a replacement response for a request, or null for default handling.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }
    /// <summary>Apply the matching mutation, then lose its response.</summary>
    public Func<HttpRequestMessage, bool>? LoseResponse { get; set; }

    public AccessFixture() => Client = new(Microsoft.Extensions.Options.Options.Create(Options), new HttpClient(this) { BaseAddress = new Uri("https://pve.example.invalid/") });

    public void EnableWrites(bool destroy = false)
    {
        Options.ReadOnly = false;
        Options.AllowAccessManagement = true;
        Options.AllowDestroy = destroy;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath)["/api2/json/".Length..];
        Requests.Add((request.Method, path + request.RequestUri.Query, body));
        if (Override?.Invoke(request) is { } replacement) return replacement;
        var form = Form(request.Method == HttpMethod.Get || request.Method == HttpMethod.Delete ? request.RequestUri.Query.TrimStart('?') : body);
        var response = Handle(request.Method, path.Split('/'), form);
        if (request.Method != HttpMethod.Get && LoseResponse?.Invoke(request) == true)
            throw new HttpRequestException("synthetic transport failure carrying " + Secret);
        return response;
    }

    private HttpResponseMessage Handle(HttpMethod method, string[] p, Dictionary<string, string> form)
    {
        if (method == HttpMethod.Get)
        {
            return p switch
            {
                ["access", "domains"] => Ok(Realms.Select(r => new { realm = r.Key, type = r.Value })),
                ["access", "users"] => Ok(Users.Select(u => new { userid = u.Key, enable = 1, expire = 0, comment = u.Value, email = "private@example.invalid" })),
                ["access", "roles"] => Ok(Roles.Select(r => new { roleid = r.Key, privs = r.Value, special = 0 })),
                ["access", "acl"] => Ok(Acl.Select(a => new { path = a.Path, type = a.Type, ugid = a.Ugid, roleid = a.Role, propagate = a.Propagate ? 1 : 0 })),
                ["access", "users", var u, "token"] => Users.ContainsKey(u)
                    ? Ok(Tokens.GetValueOrDefault(u, []).Select(t => new { tokenid = t.Key, privsep = t.Value ? 1 : 0, expire = 0, value = "never-listed" }))
                    : Error(HttpStatusCode.InternalServerError),
                ["access", "permissions"] => Ok(new Dictionary<string, Dictionary<string, int>> { [form["path"]] = Effective(form["userid"], form["path"]).ToDictionary(v => v, _ => 1) }),
                _ => throw new InvalidOperationException("Unexpected test endpoint " + string.Join('/', p)),
            };
        }
        switch (method.Method, p)
        {
            case ("POST", ["access", "users"]):
                if (Users.ContainsKey(form["userid"])) return Error(HttpStatusCode.InternalServerError);
                Users[form["userid"]] = form.GetValueOrDefault("comment");
                return Ok<object?>(null);
            case ("POST", ["access", "users", var u, "token", var t]):
                if (!Users.ContainsKey(u) || Tokens.GetValueOrDefault(u)?.ContainsKey(t) == true) return Error(HttpStatusCode.InternalServerError);
                var privsep = form.GetValueOrDefault("privsep", "1") == "1";
                (Tokens.TryGetValue(u, out var set) ? set : Tokens[u] = [])[t] = privsep;
                return Ok(new Dictionary<string, object> { ["full-tokenid"] = u + "!" + t, ["value"] = Secret, ["info"] = new { privsep = privsep ? 1 : 0 } });
            case ("PUT", ["access", "acl"]):
                var subjects = Split(form, "users").Select(s => ("user", s)).Concat(Split(form, "tokens").Select(s => ("token", s)));
                foreach (var (type, ugid) in subjects)
                {
                    Acl.RemoveAll(a => a.Path == form["path"] && a.Type == type && a.Ugid == ugid && a.Role == form["roles"]);
                    if (form.GetValueOrDefault("delete") != "1") Acl.Add((form["path"], type, ugid, form["roles"], form.GetValueOrDefault("propagate", "1") == "1"));
                }
                return Ok<object?>(null);
            case ("DELETE", ["access", "users", var u, "token", var t]):
                if (Tokens.GetValueOrDefault(u)?.Remove(t) != true) return Error(HttpStatusCode.InternalServerError);
                Acl.RemoveAll(a => a.Type == "token" && a.Ugid == u + "!" + t);
                return Ok<object?>(null);
            case ("DELETE", ["access", "users", var u]):
                if (!Users.Remove(u)) return Error(HttpStatusCode.InternalServerError);
                Tokens.Remove(u);
                return Ok<object?>(null);
        }
        throw new InvalidOperationException("Unexpected test mutation " + method + " " + string.Join('/', p));
    }

    // Privilege-separated tokens receive the intersection of their own and their user's privileges.
    public HashSet<string> Effective(string authId, string path)
    {
        HashSet<string> Grants(string type, string ugid) => Acl
            .Where(a => a.Type == type && a.Ugid == ugid && (a.Path == path || (a.Propagate && (a.Path == "/" || path.StartsWith(a.Path + "/")))))
            .SelectMany(a => Roles[a.Role].Split(',')).ToHashSet();
        var bang = authId.IndexOf('!');
        if (bang < 0) return Grants("user", authId);
        var user = Grants("user", authId[..bang]);
        user.IntersectWith(Grants("token", authId));
        return user;
    }

    private static IEnumerable<string> Split(Dictionary<string, string> form, string key) =>
        form.TryGetValue(key, out var value) ? value.Split(',') : [];

    private static Dictionary<string, string> Form(string? encoded) => string.IsNullOrEmpty(encoded) ? [] : encoded.Split('&')
        .Select(v => v.Split('=', 2)).ToDictionary(v => Uri.UnescapeDataString(v[0]), v => Uri.UnescapeDataString(v[1].Replace('+', ' ')));

    private static HttpResponseMessage Ok<T>(T data) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"data\":" + JsonSerializer.Serialize(data) + "}", Encoding.UTF8, "application/json")
    };

    public static HttpResponseMessage Error(HttpStatusCode status) => new(status) { Content = new StringContent("{\"data\":null,\"message\":\"synthetic upstream detail " + Secret + "\"}") };

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
