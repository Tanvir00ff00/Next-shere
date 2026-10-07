using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace NextShare.App.Services;

internal sealed record QuickShareInfo(bool Installed, bool Running, string Name, string? SaveFolder, string? Executable);

internal static class QuickShareCompanion
{
    public static QuickShareInfo Inspect()
    {
        var paths = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        string? exe = paths.Select(p => Path.Combine(p, "Google", "NearbyShare", "nearby_share.exe")).FirstOrDefault(File.Exists);
        string name = "Unknown", folder = "";
        try
        {
            using var prefs = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Nearby", "Sharing", "preferences.json")));
            if (prefs.RootElement.TryGetProperty("nearby_sharing.device_name", out var n)) name = n.GetString() ?? name;
            if (prefs.RootElement.TryGetProperty("nearby_sharing.custom_save_path", out var f)) folder = f.GetString() ?? "";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        var processes = Process.GetProcessesByName("nearby_share");
        bool running = processes.Length > 0;
        foreach (var process in processes) process.Dispose();
        return new QuickShareInfo(exe is not null, running, name, string.IsNullOrWhiteSpace(folder) ? null : folder, exe);
    }

    public static void Open()
    {
        var info = Inspect();
        if (info.Executable is null) throw new FileNotFoundException("Google Quick Share installed নেই।");
        Process.Start(new ProcessStartInfo(info.Executable) { UseShellExecute = true });
    }

    public static void StartBackground()
    {
        var info = Inspect();
        if (info.Running || info.Executable is null) return;
        Process.Start(new ProcessStartInfo(info.Executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
    }

    // Only an explicit user power-OFF stops Google's receiver; probes and app exit leave it alone.
    public static async Task StopReceivingAsync()
    {
        foreach (var process in Process.GetProcessesByName("nearby_share"))
        {
            using (process)
            {
                if (process.HasExited) continue;
                process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
