using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HighlightCut.App.Services;

/// <summary>Keeps <see cref="AppSettings"/> in a small JSON file in the user's app data.</summary>
public sealed class AppSettingsStore(string file)
{
    public static string DefaultFile { get; } = AppDataFolder.PathFor("settings.json");

    /// <summary>Where transcription models go unless the user picks another folder.</summary>
    public static string DefaultModelsFolder { get; } = AppDataFolder.PathFor("models");

    public string File { get; } = file;

    /// <summary>The saved settings; a missing or broken file reads as the defaults.</summary>
    public AppSettings Load()
    {
        try
        {
            if (!System.IO.File.Exists(File))
                return AppSettings.Default;
            return JsonSerializer.Deserialize(System.IO.File.ReadAllBytes(File), AppSettingsJson.Default.AppSettings) is { Transcription: not null } s
                ? s
                : AppSettings.Default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return AppSettings.Default;
        }
    }

    /// <summary>Saves atomically. Failing to save settings is not worth an error message.</summary>
    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            string temp = File + ".tmp";
            System.IO.File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(settings, AppSettingsJson.Default.AppSettings));
            System.IO.File.Move(temp, File, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

// Enums are saved by name; a name this version does not know reads as that setting's default, not as a broken file.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, Converters =
[
    typeof(LenientEnumConverter<StartupAction>), typeof(LenientEnumConverter<HardwareDecodingMode>),
    typeof(LenientEnumConverter<VideoRendererMode>), typeof(LenientEnumConverter<ExportDefaultMode>),
    typeof(LenientEnumConverter<ExportContainerDefault>), typeof(LenientEnumConverter<ExportFolderMode>),
    typeof(LenientEnumConverter<ExportAudioTracksMode>), typeof(LenientEnumConverter<ReencodeVideoPreset>),
    typeof(LenientEnumConverter<ReencodeAudioChoice>), typeof(LenientEnumConverter<FileExistsAction>),
    typeof(LenientEnumConverter<AfterExportAction>),
])]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;

/// <summary>An enum by name (or number); anything else reads as the enum's first value, which every setting uses as its default.</summary>
internal sealed class LenientEnumConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt32(out int n) ? n.ToString(CultureInfo.InvariantCulture) : null,
            _ => null,
        };
        reader.Skip();
        return Enum.TryParse(text, ignoreCase: true, out T value) && Enum.IsDefined(value) ? value : default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
