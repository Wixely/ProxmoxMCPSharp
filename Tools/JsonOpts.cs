using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxmoxMCPSharp.Tools;

internal static class JsonOpts
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Default);

    public static string Pass(JsonElement element) => element.GetRawText();
}
