using System.Text.Json;

namespace ProxmoxMCPSharp.Services;

public sealed class ResponseBudgetException : Exception
{
    public ResponseBudgetException() : base("Upstream response exceeded the byte budget; evidence is incomplete.") { }
}

internal static class DiagnosticReads
{
    public static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Any(char.IsControl) || value.Contains('/') || value.Contains('\\'))
            throw new ArgumentException("Expected a nonempty path segment without slashes or control characters.");
        return Uri.EscapeDataString(value);
    }

    public static int Bound(int? value, int fallback, int maximum, string name, int minimum = 1)
    {
        var result = value ?? fallback;
        if (result < minimum || result > maximum) throw new ArgumentOutOfRangeException(name, $"Must be between {minimum} and {maximum}.");
        return result;
    }

    public static void Window(long? since, long? until)
    {
        if (since < 0 || until < 0 || since > until) throw new ArgumentException("Epochs must be nonnegative and since must not exceed until.");
    }

    public static JsonElement Data(JsonElement envelope) => envelope.GetProperty("data");
    public static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind != JsonValueKind.Null ? item.ToString() : null;
    public static long? Number(JsonElement value, string key) => long.TryParse(Text(value, key), out var number) ? number : null;
    public static bool? Flag(JsonElement value, string key) => Text(value, key) switch { "1" or "True" or "true" => true, "0" or "False" or "false" => false, _ => null };
    public static Dictionary<string, JsonElement> Project(JsonElement value, params string[] keys) =>
        value.ValueKind != JsonValueKind.Object ? new() : value.EnumerateObject().Where(p => keys.Contains(p.Name, StringComparer.Ordinal)).ToDictionary(p => p.Name, p => p.Value.Clone());

    public static string Error(Exception ex) => ex switch
    {
        ProxmoxApiException api => api.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized ? "permission-denied" : $"http-{(int)api.StatusCode}",
        ResponseBudgetException => "response-byte-budget-exceeded",
        OperationCanceledException => "read-timeout",
        JsonException or InvalidOperationException or KeyNotFoundException => "invalid-or-unavailable-evidence",
        _ => "read-failed"
    };
}
