using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProxmoxMCPSharp.Configuration;

namespace ProxmoxMCPSharp.Services;

/// <summary>
/// Thin Proxmox VE API client. Uses the official PVE2 HTTPS API (api2/json) without scraping or hacks.
/// Supports both API token auth (preferred) and ticket-based auth (username/password + optional TOTP).
/// Requests go to an ordered pool of cluster endpoints: reads fail over between them, mutations are sent exactly once.
/// </summary>
public sealed class ProxmoxClient : IAsyncDisposable
{
    private readonly ProxmoxOptions _options;
    private readonly ProxmoxEndpoint[] _endpoints;
    private readonly TimeProvider _time;
    private readonly object _health = new();
    private readonly SemaphoreSlim _ticketLock = new(1, 1);
    private string? _ticket;
    private string? _csrfToken;
    private DateTimeOffset _ticketExpiresAt;

    public ProxmoxClient(IOptions<ProxmoxOptions> options)
        : this(options, _ => BuildHandler(options.Value))
    {
    }

    // The supplied client is owned by this instance. Enables credential-free HTTP contract tests against one endpoint.
    public ProxmoxClient(IOptions<ProxmoxOptions> options, HttpClient http)
    {
        _options = options.Value;
        _time = TimeProvider.System;
        _endpoints = [new ProxmoxEndpoint(http.BaseAddress ?? throw new ArgumentException("The HttpClient needs a BaseAddress."), http, 0)];
    }

    // One handler per configured endpoint, in preference order. Enables failover contract tests.
    public ProxmoxClient(IOptions<ProxmoxOptions> options, Func<Uri, HttpMessageHandler> handlerFactory, TimeProvider? time = null)
    {
        _options = options.Value;
        _time = time ?? TimeProvider.System;
        _endpoints = EndpointConfiguration.Parse(_options)
            .Select((uri, index) => new ProxmoxEndpoint(uri, CreateClient(handlerFactory(uri), uri, _options), index))
            .ToArray();
    }

    public ProxmoxOptions Options => _options;
    public bool IsReadOnly => _options.ReadOnly;

    /// <summary>The endpoint that most recently answered, or null before the first request.</summary>
    public string? ActiveEndpoint
    {
        get { lock (_health) return _endpoints.Where(e => e.LastSuccessAt is not null).MaxBy(e => e.LastSuccessAt)?.Url; }
    }

    public IReadOnlyList<EndpointStatus> EndpointStatuses()
    {
        var now = _time.GetUtcNow();
        lock (_health)
        {
            return _endpoints.Select(e => new EndpointStatus(e.Url, e.Priority,
                e.RetryAfter > now ? "cooling-down" : e.LastSuccessAt is null && e.LastFailure is null ? "unused" :
                    e.LastFailureAt is null || e.LastSuccessAt > e.LastFailureAt ? "available" : "eligible-for-retry",
                e.LastFailure, e.LastFailureAt, e.LastSuccessAt, e.RetryAfter > now ? e.RetryAfter : null)).ToArray();
        }
    }

    /// <summary>Checks every endpoint independently with an authenticated GET /version, ignoring cooldowns.</summary>
    public async Task<IReadOnlyList<(string Endpoint, string Result)>> ProbeEndpointsAsync(CancellationToken ct = default)
    {
        using var budget = Budget(ct);
        var results = new List<(string, string)>();
        try { await EnsureAuthenticatedAsync(budget.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is ProxmoxEndpointException or ProxmoxApiException or HttpRequestException)
        {
            return _endpoints.Select(e => (e.Url, "login-failed: " + DiagnosticReads.Error(ex))).ToArray();
        }
        foreach (var endpoint in _endpoints)
        {
            try
            {
                using var response = await AttemptAsync(endpoint, () => new HttpRequestMessage(HttpMethod.Get, "api2/json/version"), authenticate: true,
                    HttpCompletionOption.ResponseContentRead, budget.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized) { MarkFailed(endpoint, EndpointFailure.Authentication); results.Add((endpoint.Url, EndpointFailure.Authentication)); }
                else if (!response.IsSuccessStatusCode) results.Add((endpoint.Url, $"http-{(int)response.StatusCode}"));
                else { MarkHealthy(endpoint); results.Add((endpoint.Url, "ok")); }
            }
            catch (Exception ex) when (EndpointFailure.Classify(ex, budget.Token) is { } failure)
            {
                MarkFailed(endpoint, failure);
                results.Add((endpoint.Url, failure));
            }
        }
        return results;
    }

    public async Task<JsonElement> ReadAsync(string path, IDictionary<string, object?>? query = null,
        CancellationToken ct = default, int maxBytes = 4 * 1024 * 1024)
        => await SendBoundedAsync(HttpMethod.Get, path, query, ct, maxBytes).ConfigureAwait(false);

    // Never retries: losing a response does not prove that a mutation failed.
    public Task<JsonElement> MutateAsync(HttpMethod method, string path, IDictionary<string, object?> parameters, CancellationToken ct = default)
    {
        EnsureWriteAllowed("management");
        if (method != HttpMethod.Put && method != HttpMethod.Post && method != HttpMethod.Delete) throw new ArgumentException("Expected PUT, POST or DELETE.");
        return SendBoundedAsync(method, path, parameters, ct, 1024 * 1024);
    }

    private async Task<JsonElement> SendBoundedAsync(HttpMethod method, string path, IDictionary<string, object?>? parameters,
        CancellationToken ct, int maxBytes)
    {
        if (maxBytes is < 1 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        using var timeout = Budget(ct);
        var token = timeout.Token;
        var inQuery = method == HttpMethod.Get || method == HttpMethod.Delete;
        var suffix = inQuery && parameters is not null ? BuildQueryString(parameters) : "";
        var relative = "api2/json/" + path + (suffix.Length == 0 ? "" : "?" + suffix);
        using var response = await SendAsync(method, () =>
        {
            var request = new HttpRequestMessage(method, relative);
            if (!inQuery && parameters is not null) request.Content = new FormUrlEncodedContent(FlattenForForm(parameters));
            return request;
        }, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ProxmoxApiException($"Proxmox request failed (HTTP {(int)response.StatusCode}).", response.StatusCode);
        if (response.Content.Headers.ContentLength > maxBytes) throw new ResponseBudgetException();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > maxBytes) throw new ResponseBudgetException();
            buffer.Write(chunk, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

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
        => SendLegacyAsync(HttpMethod.Get, path, parameters: null, ct);

    public Task<JsonElement> PostAsync(string path, IDictionary<string, object?>? parameters, CancellationToken ct = default)
        => SendLegacyAsync(HttpMethod.Post, path, parameters, ct);

    public Task<JsonElement> PutAsync(string path, IDictionary<string, object?>? parameters, CancellationToken ct = default)
        => SendLegacyAsync(HttpMethod.Put, path, parameters, ct);

    public Task<JsonElement> DeleteAsync(string path, IDictionary<string, object?>? parameters = null, CancellationToken ct = default)
        => SendLegacyAsync(HttpMethod.Delete, path, parameters, ct);

    private async Task<JsonElement> SendLegacyAsync(HttpMethod method, string path, IDictionary<string, object?>? parameters, CancellationToken ct)
    {
        path = path.TrimStart('/');
        if (!path.StartsWith("api2/json/", StringComparison.OrdinalIgnoreCase))
            path = "api2/json/" + path;

        var relative = path;
        var inQuery = method == HttpMethod.Get || method == HttpMethod.Delete;
        if (parameters is { Count: > 0 } && inQuery)
        {
            var qs = BuildQueryString(parameters);
            relative = path + (qs.Length > 0 ? "?" + qs : string.Empty);
        }

        using var timeout = Budget(ct);
        using var response = await SendAsync(method, () =>
        {
            var request = new HttpRequestMessage(method, relative);
            if (parameters is { Count: > 0 } && !inQuery) request.Content = new FormUrlEncodedContent(FlattenForForm(parameters));
            return request;
        }, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

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

    private CancellationTokenSource Budget(CancellationToken ct)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        return budget;
    }

    // Reads may move to another endpoint within one request budget; mutations go to exactly one selected endpoint.
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Func<HttpRequestMessage> build, HttpCompletionOption completion, CancellationToken budget)
    {
        await EnsureAuthenticatedAsync(budget).ConfigureAwait(false);
        if (method == HttpMethod.Get)
            return (await FailoverAsync(build, authenticate: true, completion, budget).ConfigureAwait(false)).Response;

        var endpoint = await MutationEndpointAsync(budget).ConfigureAwait(false);
        try
        {
            var response = await AttemptAsync(endpoint, build, authenticate: true, completion, budget).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) MarkFailed(endpoint, EndpointFailure.Authentication);
            else MarkHealthy(endpoint);
            ProxmoxCallScope.Current?.Served(endpoint.Url, []);
            return response;
        }
        catch (Exception ex) when (EndpointFailure.Classify(ex, budget) is { } failure)
        {
            MarkFailed(endpoint, failure);
            throw new ProxmoxEndpointException(EndpointFailure.NotDelivered(failure) ? ProxmoxEndpointException.MutationNotDelivered : ProxmoxEndpointException.MutationIndeterminate,
                [(endpoint.Url, failure)], ex);
        }
    }

    private async Task<ProxmoxEndpoint> MutationEndpointAsync(CancellationToken budget)
    {
        if (_endpoints.Length == 1) return _endpoints[0];
        var now = _time.GetUtcNow();
        var first = Candidates()[0];
        lock (_health)
        {
            if (first.RetryAfter is null || first.RetryAfter <= now)
                if (first.LastSuccessAt >= now - TimeSpan.FromSeconds(_options.EndpointRetryAfterSeconds) && !(first.LastFailureAt > first.LastSuccessAt))
                    return first;
        }
        // Select and authenticate a healthy endpoint before submitting anything that changes state.
        var (response, endpoint) = await FailoverAsync(() => new HttpRequestMessage(HttpMethod.Get, "api2/json/version"), authenticate: true,
            HttpCompletionOption.ResponseContentRead, budget).ConfigureAwait(false);
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new ProxmoxApiException($"Endpoint selection failed (HTTP {(int)response.StatusCode}).", response.StatusCode);
        }
        return endpoint;
    }

    private async Task<(HttpResponseMessage Response, ProxmoxEndpoint Endpoint)> FailoverAsync(Func<HttpRequestMessage> build, bool authenticate,
        HttpCompletionOption completion, CancellationToken budget)
    {
        var failures = new List<(string Endpoint, string Failure)>();
        Exception? last = null;
        foreach (var endpoint in Candidates())
        {
            try
            {
                var response = await AttemptAsync(endpoint, build, authenticate, completion, budget).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && _endpoints.Length > 1)
                {
                    // With a shared cluster ticket or token, a 401 from one node is an authentication disagreement.
                    response.Dispose();
                    MarkFailed(endpoint, EndpointFailure.Authentication);
                    failures.Add((endpoint.Url, EndpointFailure.Authentication));
                    continue;
                }
                MarkHealthy(endpoint);
                ProxmoxCallScope.Current?.Served(endpoint.Url, failures);
                return (response, endpoint);
            }
            catch (Exception ex) when (EndpointFailure.Classify(ex, budget) is { } failure)
            {
                last = ex;
                MarkFailed(endpoint, failure);
                failures.Add((endpoint.Url, failure));
            }
        }
        if (!HasApiToken && failures.Any(f => f.Failure == EndpointFailure.Authentication)) _ticket = null;
        throw new ProxmoxEndpointException(ProxmoxEndpointException.AllUnavailable, failures, last);
    }

    private async Task<HttpResponseMessage> AttemptAsync(ProxmoxEndpoint endpoint, Func<HttpRequestMessage> build, bool authenticate,
        HttpCompletionOption completion, CancellationToken budget)
    {
        using var request = build();
        if (authenticate) ApplyAuth(request);
        return await endpoint.Http.SendAsync(request, completion, budget).ConfigureAwait(false);
    }

    // Healthy endpoints in preference order, then cooling-down endpoints as a last resort so a fully failed pool still recovers.
    private ProxmoxEndpoint[] Candidates()
    {
        var now = _time.GetUtcNow();
        lock (_health)
        {
            return _endpoints.Where(e => e.RetryAfter is null || e.RetryAfter <= now).OrderBy(e => e.Priority)
                .Concat(_endpoints.Where(e => e.RetryAfter > now).OrderBy(e => e.Priority)).ToArray();
        }
    }

    private void MarkHealthy(ProxmoxEndpoint endpoint)
    {
        bool recovered;
        lock (_health)
        {
            recovered = endpoint.ConsecutiveFailures > 0;
            endpoint.LastSuccessAt = _time.GetUtcNow();
            endpoint.RetryAfter = null;
            endpoint.ConsecutiveFailures = 0;
        }
        if (recovered) Serilog.Log.Information("Proxmox endpoint {Endpoint} is available again", endpoint.Url);
    }

    // Bounded exponential backoff: RetryAfter, 2x, 4x, then 8x the configured base.
    private void MarkFailed(ProxmoxEndpoint endpoint, string failure)
    {
        TimeSpan cooldown;
        lock (_health)
        {
            var now = _time.GetUtcNow();
            endpoint.ConsecutiveFailures++;
            cooldown = TimeSpan.FromSeconds(_options.EndpointRetryAfterSeconds * (1 << Math.Min(endpoint.ConsecutiveFailures - 1, 3)));
            endpoint.LastFailure = failure;
            endpoint.LastFailureAt = now;
            endpoint.RetryAfter = now + cooldown;
        }
        Serilog.Log.Warning("Proxmox endpoint {Endpoint} failed ({Failure}); preferring other endpoints for {Seconds}s",
            endpoint.Url, failure, (int)cooldown.TotalSeconds);
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

    // PVE tickets are signed with the cluster key, so one ticket is valid on every node of the same cluster.
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

            // Login creates no cluster state, so it may move to another endpoint like a read.
            var (response, _) = await FailoverAsync(() => new HttpRequestMessage(HttpMethod.Post, "api2/json/access/ticket")
            {
                Content = new FormUrlEncodedContent(form),
            }, authenticate: false, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new ProxmoxApiException(
                        $"Ticket login failed (HTTP {(int)response.StatusCode}).",
                        response.StatusCode);

                using var doc = JsonDocument.Parse(body);
                var data = doc.RootElement.GetProperty("data");
                _ticket = data.GetProperty("ticket").GetString();
                _csrfToken = data.GetProperty("CSRFPreventionToken").GetString();
                // PVE tickets are valid for 2 hours; refresh just before that.
                _ticketExpiresAt = DateTimeOffset.UtcNow.AddMinutes(110);
            }
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

    // Every endpoint gets the same certificate policy; failover never relaxes validation.
    public static SocketsHttpHandler BuildHandler(ProxmoxOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(options.EndpointConnectTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        if (options.IgnoreCertificateErrors)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        else if (!string.IsNullOrWhiteSpace(options.CaCertificatePath) && File.Exists(options.CaCertificatePath))
        {
            var trusted = new X509Certificate2(options.CaCertificatePath);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                if (certificate is not X509Certificate2 cert || chain is null) return false;
                chain.ChainPolicy.ExtraStore.Add(trusted);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                if (!chain.Build(cert)) return false;
                return chain.ChainElements
                    .Cast<X509ChainElement>()
                    .Any(e => e.Certificate.Thumbprint == trusted.Thumbprint);
            };
        }
        return handler;
    }

    private static HttpClient CreateClient(HttpMessageHandler handler, Uri baseUri, ProxmoxOptions options)
    {
        // The per-request budget is applied by the caller so it can span failover attempts.
        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseUri,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(options.UserAgent, "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var endpoint in _endpoints) endpoint.Http.Dispose();
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
