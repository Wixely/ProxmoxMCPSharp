using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProxmoxMCPSharp.Configuration;

namespace ProxmoxMCPSharp.Services;

/// <summary>
/// Thin Proxmox VE API client. Uses the official PVE2 HTTPS API (api2/json) without scraping or hacks.
/// Supports both API token auth (preferred) and ticket-based auth (username/password + optional TOTP).
/// </summary>
public sealed class ProxmoxClient : IAsyncDisposable
{
    private readonly ProxmoxOptions _options;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _ticketLock = new(1, 1);
    private string? _ticket;
    private string? _csrfToken;
    private DateTimeOffset _ticketExpiresAt;

    public ProxmoxClient(IOptions<ProxmoxOptions> options)
    {
        _options = options.Value;
        _http = BuildHttpClient(_options);
    }

    public ProxmoxOptions Options => _options;
    public bool IsReadOnly => _options.ReadOnly;

    public string ResolveNode(string? node)
    {
        var n = string.IsNullOrWhiteSpace(node) ? _options.DefaultNode : node;
        if (string.IsNullOrWhiteSpace(n))
            throw new InvalidOperationException("No node specified and Proxmox:DefaultNode is not configured.");
        if (_options.AllowedNodes.Count > 0 &&
            !_options.AllowedNodes.Contains(n, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Node '{n}' is not in the AllowedNodes list.");
        }
        if (_options.BlockedNodes.Contains(n, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Node '{n}' is in the BlockedNodes list.");
        }
        return n!;
    }

    public void EnsureVmAllowed(int vmid)
    {
        if (_options.AllowedVmIds.Count > 0 && !_options.AllowedVmIds.Contains(vmid))
            throw new InvalidOperationException($"VMID {vmid} is not in the AllowedVmIds list.");
        if (_options.BlockedVmIds.Contains(vmid))
            throw new InvalidOperationException($"VMID {vmid} is in the BlockedVmIds list.");
    }

    public void EnsureWriteAllowed(string operation)
    {
        if (_options.ReadOnly)
            throw new InvalidOperationException(
                $"Operation '{operation}' is blocked: server is running in read-only mode. " +
                "Set Proxmox:ReadOnly=false to allow writes.");
    }

    public void EnsureDestroyAllowed(string operation)
    {
        EnsureWriteAllowed(operation);
        if (!_options.AllowDestroy)
            throw new InvalidOperationException(
                $"Operation '{operation}' is blocked: destructive operations are disabled. " +
                "Set Proxmox:AllowDestroy=true to allow.");
    }

    public Task<JsonElement> GetAsync(string path, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, path, parameters: null, ct);

    public Task<JsonElement> PostAsync(string path, IDictionary<string, object?>? parameters, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, path, parameters, ct);

    public Task<JsonElement> PutAsync(string path, IDictionary<string, object?>? parameters, CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, path, parameters, ct);

    public Task<JsonElement> DeleteAsync(string path, IDictionary<string, object?>? parameters = null, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, path, parameters, ct);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, IDictionary<string, object?>? parameters, CancellationToken ct)
    {
        path = path.TrimStart('/');
        if (!path.StartsWith("api2/json/", StringComparison.OrdinalIgnoreCase))
            path = "api2/json/" + path;

        await EnsureAuthenticatedAsync(ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(method, path);
        ApplyAuth(request);

        if (parameters is { Count: > 0 })
        {
            if (method == HttpMethod.Get || method == HttpMethod.Delete)
            {
                var qs = BuildQueryString(parameters);
                request.RequestUri = new Uri(path + (qs.Length > 0 ? "?" + qs : string.Empty), UriKind.Relative);
            }
            else
            {
                request.Content = new FormUrlEncodedContent(FlattenForForm(parameters));
            }
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new ProxmoxApiException(
                $"Proxmox API {method} {path} failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {Truncate(body, 1024)}",
                response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(body))
            return JsonDocument.Parse("{}").RootElement.Clone();

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (HasApiToken)
        {
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                $"PVEAPIToken={_options.ApiTokenId}={_options.ApiTokenSecret}");
            return;
        }

        if (!string.IsNullOrEmpty(_ticket))
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"PVEAuthCookie={_ticket}");
            if (!string.IsNullOrEmpty(_csrfToken) &&
                request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            {
                request.Headers.TryAddWithoutValidation("CSRFPreventionToken", _csrfToken);
            }
        }
    }

    private bool HasApiToken =>
        !string.IsNullOrWhiteSpace(_options.ApiTokenId) && !string.IsNullOrWhiteSpace(_options.ApiTokenSecret);

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (HasApiToken) return;

        if (!string.IsNullOrEmpty(_ticket) && DateTimeOffset.UtcNow < _ticketExpiresAt) return;

        if (string.IsNullOrWhiteSpace(_options.Username) || string.IsNullOrWhiteSpace(_options.Password))
            throw new InvalidOperationException(
                "Proxmox auth is not configured. Provide ApiTokenId+ApiTokenSecret, or Username+Password.");

        await _ticketLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_ticket) && DateTimeOffset.UtcNow < _ticketExpiresAt) return;

            var form = new Dictionary<string, string>
            {
                ["username"] = _options.Username!,
                ["password"] = _options.Password!,
            };
            if (!string.IsNullOrWhiteSpace(_options.TotpCode))
                form["otp"] = _options.TotpCode!;

            using var request = new HttpRequestMessage(HttpMethod.Post, "api2/json/access/ticket")
            {
                Content = new FormUrlEncodedContent(form),
            };
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ProxmoxApiException(
                    $"Ticket login failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {Truncate(body, 512)}",
                    response.StatusCode);

            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            _ticket = data.GetProperty("ticket").GetString();
            _csrfToken = data.GetProperty("CSRFPreventionToken").GetString();
            // PVE tickets are valid for 2 hours; refresh just before that.
            _ticketExpiresAt = DateTimeOffset.UtcNow.AddMinutes(110);
        }
        finally
        {
            _ticketLock.Release();
        }
    }

    private static string BuildQueryString(IDictionary<string, object?> parameters)
    {
        var sb = new StringBuilder();
        foreach (var kv in parameters)
        {
            if (kv.Value is null) continue;
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture) ?? ""));
        }
        return sb.ToString();
    }

    private static IEnumerable<KeyValuePair<string, string>> FlattenForForm(IDictionary<string, object?> parameters)
    {
        foreach (var kv in parameters)
        {
            if (kv.Value is null) continue;
            var value = kv.Value switch
            {
                bool b => b ? "1" : "0",
                _ => Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            };
            yield return new KeyValuePair<string, string>(kv.Key, value);
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private static HttpClient BuildHttpClient(ProxmoxOptions options)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
        };

        if (options.IgnoreCertificateErrors)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }
        else if (!string.IsNullOrWhiteSpace(options.CaCertificatePath) && File.Exists(options.CaCertificatePath))
        {
            var trusted = new X509Certificate2(options.CaCertificatePath);
            handler.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                if (cert is null || chain is null) return false;
                chain.ChainPolicy.ExtraStore.Add(trusted);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                if (!chain.Build(cert)) return false;
                return chain.ChainElements
                    .Cast<X509ChainElement>()
                    .Any(e => e.Certificate.Thumbprint == trusted.Thumbprint);
            };
        }

        var baseUri = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";
        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(baseUri),
            Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
        };
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(options.UserAgent, "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _ticketLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ProxmoxApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public ProxmoxApiException(string message, HttpStatusCode statusCode) : base(message)
        => StatusCode = statusCode;
}
