using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace NextShare.App;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    public static string DataRoot { get; private set; } = "";
    public static string[] Arguments { get; private set; } = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Arguments = e.Args;
        var rootIndex = Array.IndexOf(e.Args, "--data-root");
        DataRoot = rootIndex >= 0 && rootIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[rootIndex + 1]) :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NextShare");
        Directory.CreateDirectory(DataRoot);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataRoot.ToUpperInvariant())))[..16];
        instance = new Mutex(true, "Local\\NextShare-" + key, out bool created);
        if (!created)
        {
            if (!Arguments.Contains("--startup")) System.Windows.MessageBox.Show("Next Share is already running. Open it from the system tray.", "Next Share");
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.AppendAllText(Path.Combine(DataRoot, "errors.log"), DateTimeOffset.UtcNow + " " + args.Exception + Environment.NewLine); } catch { }
            if (Arguments.Contains("--smoke-test") || Arguments.Contains("--smoke-ui-only")) { args.Handled = true; Shutdown(1); return; }
            System.Windows.MessageBox.Show(args.Exception.Message, "Next Share — কাজটি শেষ হয়নি");
            args.Handled = true;
        };
        var window = new MainWindow();
        MainWindow = window;
        if(Arguments.Contains("--startup")){window.Opacity=0;window.ShowActivated=false;window.ShowInTaskbar=false;}
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
