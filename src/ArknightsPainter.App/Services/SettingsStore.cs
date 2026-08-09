using System.Text.Json;
using ArknightsPainter.Core.Models;

namespace ArknightsPainter.App.Services;

public sealed class AppSettings
{
    public string ConnectionMode { get; set; } = "adb";

    public string? AdbPath { get; set; }

    public string Endpoint { get; set; } = "127.0.0.1:16384";

    public string DesktopPid { get; set; } = string.Empty;

    public bool IgnoreVisualValidation { get; set; }

    public List<CalibrationProfile> Calibrations { get; set; } = [];
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArknightsPainter",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, _path, true);
    }
}
