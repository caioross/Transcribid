using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioRecorder;

public enum WhisperModelSize
{
    Tiny,
    Base,
    Small,
    Medium,
    LargeV3,
}

public sealed class AppSettings
{
    /// <summary>Language hint passed to Whisper. "auto" = detect.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Whisper model size used for transcription.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WhisperModelSize ModelSize { get; set; } = WhisperModelSize.Small;

    /// <summary>Start transcription automatically when a recording stops.</summary>
    public bool AutoTranscribe { get; set; } = true;

    /// <summary>Write an .srt with per-segment timestamps next to the .txt.</summary>
    public bool WriteSrt { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string SettingsFolder
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "Transcribid");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsPath => Path.Combine(SettingsFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch { /* fall through to default */ }

        var fresh = new AppSettings();
        try { fresh.Save(); } catch { }
        return fresh;
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(SettingsPath, json);
        }
        catch { /* ignore — settings persistence is best-effort */ }
    }
}
