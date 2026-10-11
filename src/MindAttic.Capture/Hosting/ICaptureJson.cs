#if NET
using System.Text.Json;
using System.Text.Json.Serialization;
#endif

namespace MindAttic.Capture.Hosting;

/// <summary>
/// JSON used for config files, sidecars, manifests and finalize state. On .NET it defaults to
/// System.Text.Json; netstandard2.1 hosts (Unity) must supply one (the Unity package uses Newtonsoft).
/// Serialize camelCase, enums as strings; deserialize case-insensitively.
/// </summary>
public interface ICaptureJson
{
    /// <summary>Serializes <paramref name="value"/> as indented JSON.</summary>
    string Serialize<T>(T value);
    /// <summary>Parses <paramref name="json"/>.</summary>
    T? Deserialize<T>(string json);
}

#if NET
/// <summary>System.Text.Json implementation.</summary>
public sealed class SystemTextCaptureJson : ICaptureJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc/>
    public string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <inheritdoc/>
    public T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
#endif

internal static class CaptureJsonDefaults
{
    public static ICaptureJson? Create() =>
#if NET
        new SystemTextCaptureJson();
#else
        null;
#endif
}
