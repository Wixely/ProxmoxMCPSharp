using System.Security.Authentication;
using ProxmoxMCPSharp.Configuration;

namespace ProxmoxMCPSharp.Services;

/// <summary>Observed state of one configured Proxmox API endpoint. Contains no credentials.</summary>
public sealed record EndpointStatus(string Url, int Priority, string State, string? LastFailure,
    DateTimeOffset? LastFailureAtUtc, DateTimeOffset? LastSuccessAtUtc, DateTimeOffset? RetryAfterUtc);

/// <summary>
/// Raised when no endpoint could serve a request, or when a mutation failed in transport.
/// Mutations are never resent; <see cref="Kind"/> says whether delivery is known not to have happened.
/// </summary>
public sealed class ProxmoxEndpointException : HttpRequestException
{
    public const string AllUnavailable = "all-endpoints-unavailable";
    public const string MutationNotDelivered = "mutation-not-delivered";
    public const string MutationIndeterminate = "mutation-indeterminate";

    public string Kind { get; }
    public IReadOnlyList<(string Endpoint, string Failure)> Failures { get; }

    public ProxmoxEndpointException(string kind, IReadOnlyList<(string Endpoint, string Failure)> failures, Exception? inner = null)
        : base(Describe(kind, failures), inner)
    {
        Kind = kind;
        Failures = failures;
    }

    private static string Describe(string kind, IReadOnlyList<(string Endpoint, string Failure)> failures)
    {
        var detail = string.Join("; ", failures.Select(f => $"{f.Endpoint} ({f.Failure})"));
        return kind switch
        {
            MutationNotDelivered => $"The Proxmox mutation was not delivered: {detail}. It was not sent to another endpoint.",
            MutationIndeterminate => $"The Proxmox mutation outcome is unknown: {detail}. It was not resent; reconcile current state before any retry.",
            _ => $"No Proxmox API endpoint was available: {detail}.",
        };
    }
}

internal sealed class ProxmoxEndpoint(Uri baseUri, HttpClient http, int priority)
{
    public Uri BaseUri { get; } = baseUri;
    public HttpClient Http { get; } = http;
    public int Priority { get; } = priority;
    public string? LastFailure { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? RetryAfter { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string Url => BaseUri.AbsoluteUri;
}

internal static class EndpointFailure
{
    public const string Unreachable = "unreachable";
    public const string ConnectTimeout = "connect-timeout";
    public const string Tls = "tls-failure";
    public const string Authentication = "authentication-rejected";
    public const string Transport = "transport-failure";

    /// <summary>Classifies a failed attempt, or returns null when the failure must propagate unchanged (caller cancellation, request budget).</summary>
    public static string? Classify(Exception ex, CancellationToken budget)
    {
        if (budget.IsCancellationRequested) return null;
        return ex switch
        {
            // Not caused by the caller or the overall budget, so it is the handler's connect timeout.
            OperationCanceledException => ConnectTimeout,
            HttpRequestException h when h.HttpRequestError == HttpRequestError.SecureConnectionError || h.InnerException is AuthenticationException => Tls,
            HttpRequestException h when h.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError => Unreachable,
            HttpRequestException => Transport,
            _ => null,
        };
    }

    /// <summary>True when the request cannot have reached the Proxmox API handler.</summary>
    public static bool NotDelivered(string failure) => failure is Unreachable or ConnectTimeout or Tls;
}

internal static class EndpointConfiguration
{
    public static Uri[] Parse(ProxmoxOptions options)
    {
        var raw = options.BaseUrls is { Count: > 0 } ? options.BaseUrls : [options.BaseUrl];
        var result = new List<Uri>();
        foreach (var value in raw)
        {
            if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new InvalidOperationException("Each Proxmox:BaseUrls entry must be an absolute https:// URL without credentials, query or fragment, e.g. https://pve1:8006/.");
            var normalized = new Uri(uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/");
            if (!result.Any(u => string.Equals(u.AbsoluteUri, normalized.AbsoluteUri, StringComparison.OrdinalIgnoreCase))) result.Add(normalized);
        }
        if (options.EndpointRetryAfterSeconds is < 1 or > 3600) throw new InvalidOperationException("Proxmox:EndpointRetryAfterSeconds must be between 1 and 3600.");
        if (options.EndpointConnectTimeoutSeconds is < 1 or > 300) throw new InvalidOperationException("Proxmox:EndpointConnectTimeoutSeconds must be between 1 and 300.");
        return result.ToArray();
    }
}

/// <summary>Collects the endpoints that served Proxmox requests during one MCP tool call.</summary>
public sealed class ProxmoxCallScope : IDisposable
{
    private static readonly AsyncLocal<ProxmoxCallScope?> Ambient = new();
    private readonly ProxmoxCallScope? _previous;
    private readonly object _gate = new();
    private readonly List<string> _endpoints = [];
    private readonly List<(string Endpoint, string Failure)> _failovers = [];

    private ProxmoxCallScope()
    {
        _previous = Ambient.Value;
        Ambient.Value = this;
    }

    public static ProxmoxCallScope Begin() => new();
    internal static ProxmoxCallScope? Current => Ambient.Value;

    public IReadOnlyList<string> Endpoints { get { lock (_gate) return _endpoints.ToArray(); } }
    public IReadOnlyList<(string Endpoint, string Failure)> Failovers { get { lock (_gate) return _failovers.ToArray(); } }

    internal void Served(string endpoint, IEnumerable<(string Endpoint, string Failure)> skipped)
    {
        lock (_gate)
        {
            if (!_endpoints.Contains(endpoint)) _endpoints.Add(endpoint);
            foreach (var failure in skipped) if (!_failovers.Contains(failure)) _failovers.Add(failure);
        }
    }

    public void Dispose() => Ambient.Value = _previous;
}
