using System.IO;
using System.Text.Json;

namespace NextShare.App.Services;

internal sealed class AppSettings
{
    public string ServiceName { get; set; } = "Next Share";
    public string? DeviceNameOverride { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string DeviceName => string.IsNullOrWhiteSpace(DeviceNameOverride) ? Environment.MachineName : DeviceNameOverride;
    public string? QuickShareFolder { get; set; }
    public bool StartBluetooth { get; set; } = true;
    public string? SaveFolder { get; set; }
    public List<string> PreviousSaveFolders { get; set; } = [];
    public string Theme { get; set; } = "System";
    public string Accent { get; set; } = "Teal";
    public bool CloseToTray { get; set; } = true;
    public bool DesktopNotifications { get; set; } = true;
    public bool ReceiveSound { get; set; } = true;
    public string? SoundOutputDeviceId {get;set;}
    public double WindowWidth { get; set; } = 830;
    public double WindowHeight { get; set; } = 560;
    public List<string> HiddenHistoryIds { get; set; } = [];
}

internal static class SettingsStore
{
    public static AppSettings Load()
    {
        var path = Path.Combine(App.DataRoot, "settings.json");
        if (!File.Exists(path)) return new AppSettings();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
    }
    public static void Save(AppSettings settings)
    {
        var path = Path.Combine(App.DataRoot, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
