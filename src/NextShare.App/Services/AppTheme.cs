using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace NextShare.App.Services;

internal static class AppTheme
{
    public static bool Apply(string mode, string accent)
    {
        bool dark = mode == "Dark" || mode == "System" && SystemDark();
        string primary = accent switch { "Blue" => dark ? "#A4C7FF" : "#285DA8", "Violet" => dark ? "#D2BBFF" : "#70509C", _ => dark ? "#83D5C2" : "#006B60" };
        string container = accent switch { "Blue" => dark ? "#203D63" : "#D6E5FF", "Violet" => dark ? "#48345F" : "#EBDDFF", _ => dark ? "#204D43" : "#C2EAE0" };
        var palette = new PaletteHelper(); var theme = palette.GetTheme();
        theme.SetBaseTheme(dark ? BaseTheme.Dark : BaseTheme.Light);
        theme.SetPrimaryColor(Color(primary)); theme.SetSecondaryColor(Color(primary)); palette.SetTheme(theme);
        var colors = new Dictionary<string, string>
        {
            ["Surface"] = dark ? "#101815" : "#F4FAF7", ["SurfaceContainer"] = dark ? "#19241F" : "#E8F3EE",
            ["SurfaceHigh"] = dark ? "#25322B" : "#E1EDE7", ["Primary"] = primary, ["PrimaryContainer"] = container,
            ["OnPrimary"] = dark ? "#12221B" : "#FFFFFF", ["Ink"] = dark ? "#E1ECE5" : "#17201C",
            ["Muted"] = dark ? "#AFBFB5" : "#54645D", ["Outline"] = dark ? "#465B4E" : "#B7C9C0",
            ["ErrorColor"] = dark ? "#FFB49F" : "#A43E2B"
        };
        foreach (var token in colors) Application.Current.Resources[token.Key] = new SolidColorBrush(Color(token.Value));
        if (SystemParameters.HighContrast)
        {
            foreach (var token in new[] { "Surface", "SurfaceContainer", "SurfaceHigh" }) Application.Current.Resources[token] = SystemColors.WindowBrush;
            foreach (var token in new[] { "Ink", "Muted", "Outline" }) Application.Current.Resources[token] = SystemColors.WindowTextBrush;
            Application.Current.Resources["Primary"] = SystemColors.HighlightBrush; Application.Current.Resources["PrimaryContainer"] = SystemColors.HighlightBrush;
            Application.Current.Resources["OnPrimary"] = SystemColors.HighlightTextBrush;
        }
        return dark;
    }
    private static System.Windows.Media.Color Color(string value) => (System.Windows.Media.Color)ColorConverter.ConvertFromString(value);
    private static bool SystemDark()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return key?.GetValue("AppsUseLightTheme") is int value && value == 0; }
        catch { return false; }
    }
}
