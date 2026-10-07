using System.IO;

namespace NextShare.App.Services;

internal static class AppBrand
{
    public static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = typeof(AppBrand).Assembly.GetManifestResourceStream("NextShare.AppIcon")
            ?? throw new IOException("Next Share icon missing");
        using var icon = new System.Drawing.Icon(stream, new System.Drawing.Size(32, 32));
        return (System.Drawing.Icon)icon.Clone();
    }
}
