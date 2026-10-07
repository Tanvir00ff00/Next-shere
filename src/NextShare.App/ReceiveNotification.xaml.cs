using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NextShare.App;

// A short, non-activating desktop card. OS toast timeout settings cannot guarantee two seconds.
public partial class ReceiveNotification : Window
{
    private readonly DispatcherTimer lifetime = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Window host;
    private readonly bool completed;
    private int slot;
    private int count;
    private long bytes;
    private bool allKnown=true;
    private readonly HashSet<string> senders=[];
    private readonly Stopwatch sinceUpdate = new();
    public int FileCount => count;
    public long TotalBytes => bytes;
    public bool IsCompletion => completed;
    public long MillisecondsSinceUpdate => sinceUpdate.ElapsedMilliseconds;

    public ReceiveNotification(Window host, bool completed = false)
    {
        this.host = host;
        this.completed = completed;
        InitializeComponent();
        Phase.Text = completed ? "NEXT SHARE · SAVED" : "NEXT SHARE · INCOMING";
        Left = host.Left; Top = host.Top;
        lifetime.Tick += (_, _) => { lifetime.Stop(); Close(); };
        Closed += (_, _) => lifetime.Stop();
        SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(this);
            source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                // Prevent a mouse click from activating the receiver or interrupting typing.
                if (message == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
                if (message == 0x2E0) Dispatcher.BeginInvoke(Place, DispatcherPriority.Loaded); // DPI changed
                return IntPtr.Zero;
            });
        };
        Loaded += (_, _) =>
        {
            Place();
            if (SystemParameters.ClientAreaAnimation)
            {
                Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        };
    }

    public void Add(string name, string sender, long? size, int number = 1, bool preview = false)
    {
        senders.Add(sender);allKnown&=size.HasValue;
        count += number; bytes += size ?? 0;
        FileTitle.Text = preview ? "Notification preview" : completed
            ? count==1 ? "Received · "+name : $"{count} files received"
            : count == 1 ? "Receiving · " + name : $"Receiving {count} files";
        FileDetail.Text = (allKnown ? MainWindow.FormatSize(bytes) + " · " : "") + (senders.Count>1?$"{senders.Count} devices":sender) + (completed ? " · Saved" : "");
        lifetime.Stop(); sinceUpdate.Restart(); lifetime.Start();
    }

    public void SetSlot(int value) { slot=value; if(IsLoaded)Place(); }
    public void Interrupted() { FileTitle.Text="Transfer interrupted"; FileDetail.Text="Check the sender and try again."; }
    private void Place()
    {
        var target = host.IsVisible && host.WindowState != WindowState.Minimized
            ? System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(host).Handle)
            : System.Windows.Forms.Screen.PrimaryScreen!;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
        int gap = (int)Math.Round(16 * GetDpiForWindow(hwnd) / 96.0);
        SetWindowPos(hwnd, new IntPtr(-1), target.WorkingArea.Right - (rect.Right - rect.Left) - gap,
            Math.Max(target.WorkingArea.Top, target.WorkingArea.Bottom - (rect.Bottom - rect.Top) - gap - slot*((rect.Bottom-rect.Top)+gap/2)), 0, 0, 0x0011); // NOSIZE | NOACTIVATE
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
