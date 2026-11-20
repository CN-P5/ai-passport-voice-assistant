using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiPassportVoice.Core.Config;

/// <summary>持久化配置。字段名沿用 Python 版 voice-config.json，便于迁移用户旧配置。</summary>
public sealed class AppConfig
{
    [JsonPropertyName("hotkey_key")]
    public string HotkeyKey { get; set; } = "right-alt";

    [JsonPropertyName("hotkey_mode")]
    public string HotkeyMode { get; set; } = "hold";

    [JsonPropertyName("output_substr")]
    public string OutputSubstr { get; set; } = "CABLE Input";

    [JsonPropertyName("udp_port")]
    public int UdpPort { get; set; } = 33333;

    [JsonPropertyName("device_ip")]
    public string DeviceIp { get; set; } = "";

    [JsonPropertyName("start_minimized")]
    public bool StartMinimized { get; set; }

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    /// <summary>注入 API：sendinput（默认）或 keybd_event（对照排查用）。</summary>
    [JsonPropertyName("inject_api")]
    public string InjectApi { get; set; } = "sendinput";

    /// <summary>是否把运行日志写入 exe 同级 logs 目录，便于事后排查。</summary>
    [JsonPropertyName("log_to_file")]
    public bool LogToFile { get; set; } = true;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new AppConfig();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    [JsonIgnore]
    public bool IsValidHotkeyMode => HotkeyMode is "hold" or "tap";
}
