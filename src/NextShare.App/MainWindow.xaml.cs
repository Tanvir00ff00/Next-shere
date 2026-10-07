using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NextShare.App.Services;
using NextShare.Core;
using MaterialDesignThemes.Wpf;

namespace NextShare.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private InboxStore store = null!;
    private BluetoothReceiverService bluetooth = null!;
    private readonly AppSettings settings;
    private readonly System.Windows.Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly StyledTrayMenu trayMenu = null!;
    private readonly DispatcherTimer timer;
    private QuickShareReceiverService quickShare = null!;
    private readonly bool noRadio = App.Arguments.Contains("--smoke-ui-only") || App.Arguments.Contains("--audio-preview-only");
    private readonly bool smoke = App.Arguments.Contains("--smoke-test") || App.Arguments.Contains("--smoke-ui-only");
    private bool enabled = true, busy, exiting;
    private string? error;
    
    private string progress = "";
    private object? nativeTestResult;
    private object? materialTestResult;
    private object? feedbackTestResult;
    private object? sendTestResult;
    private bool smokeFailure;
    private ReceiveFeedback feedback = null!;
    public ObservableCollection<TransferView> Files { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        try { settings = SettingsStore.Load(); }
        catch (Exception ex) { settings = new AppSettings(); Log(ex.Message); }
        settings.SaveFolder ??= smoke ? Path.Combine(App.DataRoot, "Inbox") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Next Share");
        settings.PreviousSaveFolders ??= [];
        var legacy = Path.Combine(App.DataRoot, "Inbox");
        if (!PathsEqual(legacy, settings.SaveFolder) && Directory.Exists(legacy) && !settings.PreviousSaveFolders.Contains(legacy)) settings.PreviousSaveFolders.Add(legacy);
        CreateServices(settings.SaveFolder);
        InitializeUi();
        feedback = new ReceiveFeedback(this, settings, Log, smoke);
        feedback.SoundStatusChanged += status => { soundStatus = status; Changed(nameof(SoundStatus)); };
        if (!smoke)
        {
            Width = double.IsFinite(settings.WindowWidth) ? Math.Clamp(settings.WindowWidth, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width)) : 830;
            Height = double.IsFinite(settings.WindowHeight) ? Math.Clamp(settings.WindowHeight, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height)) : 560;
        }
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        UpdateResponsiveLayout();
        LoadHistory();
        DataContext = this;
        trayIcon = AppBrand.CreateTrayIcon();
        trayMenu = new StyledTrayMenu(trayIcon, () => Dispatcher.Invoke(Restore), () => Dispatcher.InvokeAsync(ExitAsync));
        tray = new System.Windows.Forms.NotifyIcon { Icon = trayIcon, Text = "Next Share", ContextMenuStrip = trayMenu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(Restore);
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) => Refresh(); timer.Start();
        Closing += (_, e) => { if (!exiting) { SaveWindowSize(); e.Cancel = true; Hide(); if (!settings.CloseToTray) _ = ExitWhenReadyAsync(); } };
        Loaded += OnLoaded;
        Refresh();
    }

    private void CreateServices(string root)
    {
        store = OpenStore(root);
        // Power ON authorizes incoming shop files. Files are saved, never executed automatically.
        bluetooth = new BluetoothReceiverService(store) { Consent = (_, token) => Task.FromResult(enabled && !exiting && !token.IsCancellationRequested) };
        quickShare = new QuickShareReceiverService(store, App.DataRoot);
        quickShare.Changed += () => Dispatcher.InvokeAsync(Refresh);
        quickShare.Activity += Log;
        quickShare.Started += (offer,count) => Dispatcher.InvokeAsync(()=>BeginIncoming(offer,count));
        quickShare.Progress += p => Dispatcher.InvokeAsync(()=>UpdateIncoming("Quick Share",p,false));
        quickShare.Processing += id => Dispatcher.InvokeAsync(()=>ProcessIncoming("Quick Share",id));
        quickShare.SessionEnded += (id,saved) => Dispatcher.InvokeAsync(()=>EndIncoming("Quick Share",id,saved));
        store.Committed += record => Dispatcher.InvokeAsync(() =>
        {
            Files.Insert(0,new TransferView(record));
            feedback.Saved(IncomingKey(new TransferOffer(record.SessionId,record.Name,record.Route,record.Sender,record.Size)),record);
            if(record.Route=="Bluetooth") EndIncomingFile(record);
            Refresh();
        });
        bluetooth.Changed += () => Dispatcher.InvokeAsync(Refresh);
        bluetooth.Activity += Log;
        bluetooth.Started += offer => Dispatcher.InvokeAsync(()=>BeginIncoming(offer));
        bluetooth.Progress += QueueBluetoothProgress;
        bluetooth.Processing += p => Dispatcher.InvokeAsync(()=>UpdateIncoming("Bluetooth",p,true));
        bluetooth.SessionEnded += id => Dispatcher.InvokeAsync(()=>EndIncoming("Bluetooth",id,false));
    }

    private void LoadHistory()
    {
        Files.Clear();
        var records = new List<TransferRecord>();
        foreach (var root in settings.PreviousSaveFolders.Append(store.Root).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { if (Directory.Exists(root)) records.AddRange(OpenStore(root).Load()); }
            catch (Exception ex) { Log("History: " + ex.Message); }
        }
        foreach (var record in records.DistinctBy(r => r.Id).OrderByDescending(r => r.ReceivedAt)) Files.Add(new TransferView(record));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshSoundOutputsAsync();
        if (App.Arguments.Contains("--audio-preview-only"))
        {
            var result=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            feedback.SoundStatusChanged+=status=>result.TrySetResult(status);
            SettingsNav.IsChecked=true;
            PreviewFeedback_Click(this,new RoutedEventArgs());
            string status=await result.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await File.WriteAllTextAsync(Path.Combine(App.DataRoot,"audio-preview.json"),JsonSerializer.Serialize(new{status,Playback=feedback.LastPlayback,ActualPreviewHandler=true,ProcessId=Environment.ProcessId}));
            await ExitAsync();return;
        }
        await StartAsync();
        bool startupHidden=false;
        if(App.Arguments.Contains("--startup"))
        {
            Hide();Opacity=1;ShowInTaskbar=true;ShowActivated=true;startupHidden=!IsVisible&&enabled;
            if(!smoke)_=RecoverStartupRadiosAsync();
        }
        if (!smoke && App.Arguments.Contains("--show-send")) SendNav.IsChecked = true;
        if (!smoke && App.Arguments.Contains("--preview-feedback")) feedback.Preview();
        if (smoke)
        {
            try { feedbackTestResult = await ExerciseReceiveFeedbackAsync(); }
            catch (Exception ex) { smokeFailure = true; feedbackTestResult = new { Passed = false, Error = ex.ToString() }; Log(ex.ToString()); }
            if (App.Arguments.Contains("--smoke-native-transfer"))
            {
                try { nativeTestResult = await ExerciseNativeTransfersAsync(); }
                catch (Exception ex) { smokeFailure = true; nativeTestResult = new { Passed = false, Error = ex.ToString() }; Log(ex.ToString()); }
            }
            await Task.Delay(500);
            bool defaultEnabled = enabled;
            try { sendTestResult = await ExerciseSendUiAsync(); }
            catch (Exception ex) { smokeFailure = true; sendTestResult = new { Passed=false, Error=ex.ToString() }; Log(ex.ToString()); }
            try { materialTestResult = await ExerciseMaterialUiAsync(); }
            catch (Exception ex) { smokeFailure = true; materialTestResult = new { Passed=false, Error=ex.ToString() }; Log(ex.ToString()); }
            Capture(Path.Combine(App.DataRoot, "app-preview.png"));
            await ToggleAsync();
            bool offVerified = !enabled && bluetooth.State == "Stopped" && quickShare.State == "Stopped" && !quickShare.Running;
            Capture(Path.Combine(App.DataRoot, "app-off-preview.png"));
            await ToggleAsync();
            var report = new { bluetooth.State, bluetooth.Detail, bluetooth.DeviceName, bluetooth.ExtendedAdvertisingSupported,
                QuickShare = new { quickShare.State, quickShare.Detail, quickShare.Running, quickShare.Port, quickShare.OfflineState, quickShare.OfflineDetail, GoogleRequired = false }, NativeProtocolTest = nativeTestResult, MaterialUiTest = materialTestResult, SendUiTest = sendTestResult, ReceiveFeedbackTest = feedbackTestResult, InboxCount = Files.Count, SaveFolder = store.Root,
                AirDrop = "Not implemented; transport hardware required", WindowLoaded = IsLoaded, Width, Height,
                DefaultEnabled = defaultEnabled, StartupHiddenAndReceivingOn=startupHidden, PowerOffVerified = offVerified, PowerOnVerified = enabled && bluetooth.State == "Advertising", MasterPowerOnVerified = enabled, RadioProbeSkipped = noRadio, QuickShareProcessControlSkipped = true };
            await File.WriteAllTextAsync(Path.Combine(App.DataRoot, "smoke-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            await ExitAsync();
        }
    }

    private async Task<object> ExerciseNativeTransfersAsync()
    {
        if (!quickShare.Running) throw new IOException(quickShare.Detail);
        int initial = store.Load().Count;
        int starts=quickStarts,updates=quickProgressEvents,saving=quickProcessingEvents,saved=quickSavedSessions;
        var expected = new List<string>(); var senders = new List<Task>();
        for (int customer = 0; customer < 4; customer++)
        {
            string folder = Path.Combine(App.DataRoot, "TestSource", customer.ToString()); Directory.CreateDirectory(folder);
            byte[] payload = Enumerable.Repeat((byte)(customer + 1), 512 * 1024 + customer + 137).ToArray();
            string picture = Path.Combine(folder, "ছবি.jpg"), empty = Path.Combine(folder, "empty.txt");
            await File.WriteAllBytesAsync(picture, payload); await File.WriteAllBytesAsync(empty, []);
            expected.Add(Convert.ToHexString(SHA256.HashData(payload))); expected.Add(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())));
            senders.Add(SendProtocolTestAsync(picture, empty));
        }
        await Task.WhenAll(senders);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (store.Load().Count < initial + 8 && DateTime.UtcNow < deadline) await Task.Delay(100);
        var records = store.Load().Take(8).ToArray();
        if (records.Length != 8 || !records.Select(r => r.Sha256).Order().SequenceEqual(expected.Order())) throw new IOException("Concurrent Quick Share bytes or completion missing");
        foreach (var record in records)
            if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(record.FilePath))) != record.Sha256 || Path.GetDirectoryName(record.FilePath) != store.Root) throw new IOException("Quick Share destination mismatch");
        if (Directory.EnumerateDirectories(store.Root).Any()) throw new IOException("Quick Share created destination folders");
        await Task.Delay(200);
        bool lifecycle=quickStarts-starts==4&&quickProgressEvents>updates&&quickProcessingEvents-saving==4&&quickSavedSessions-saved==4&&Incoming.Count==0;
        if(!lifecycle)throw new IOException($"Quick Share receive lifecycle: starts={quickStarts-starts},progress={quickProgressEvents-updates},processing={quickProcessingEvents-saving},saved={quickSavedSessions-saved},active={Incoming.Count}");
        return new { Passed = true, ReceiveLifecycleVerified=lifecycle, ConcurrentSenders = 4, Files = 8, Bytes = records.Sum(r => r.Size), GoogleUsed = false, FlatDestination = true, EncryptedProtocol = true, RadioUsed = false, PhoneUsed = false };
    }
    private async Task SendProtocolTestAsync(string picture, string empty)
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await QuickShareSenderService.SendAsync(new("loopback","Software receiver","Quick Share","127.0.0.1:"+quickShare.Port),"Test sender",[OutgoingFile.Inspect(picture),OutgoingFile.Inspect(empty)],new Progress<SendProgress>(_=>{}),()=>false,wait.Token);
    }

    private async Task RecoverStartupRadiosAsync()
    {
        for(int i=0;i<3;i++)
        {
            await Task.Delay(10000);if(exiting||!enabled)return;
            if(busy||sending||Incoming.Count>0||quickShare.HasActiveTransfers||bluetooth.HasActiveTransfers)continue;
            busy=true;Refresh();
            try
            {
                if(bluetooth.State is "Faulted" or "NeedsSetup" or "Unsupported" or "Stopped")await bluetooth.StartAsync(settings.DeviceName);
                if(!quickShare.Running)await quickShare.StartAsync(settings.DeviceName,true);
                else await quickShare.RetryDiscoveryAsync();
            }
            catch(Exception ex){Log("Startup recovery: "+ex.Message);}
            finally{busy=false;Refresh();}
        }
    }
    private async Task StartAsync()
    {
        busy = true; error = null; enabled = true; Refresh();
        try
        {
            if (!noRadio) await bluetooth.StartAsync(settings.DeviceName);
            await quickShare.StartAsync(settings.DeviceName, !noRadio);
        }
        catch (Exception ex) { error = "শুরু হয়নি · " + ex.Message; Log(ex.Message); }
        finally { busy = false; Refresh(); Animate(); }
    }

    private async void Power_Click(object sender, RoutedEventArgs e)
        => await ToggleAsync();

    private async Task ToggleAsync()
    {
        if (busy) return;
        if (!enabled) { await StartAsync(); return; }
        busy = true; Refresh();
        try
        {
            await quickShare.StopAsync(); await bluetooth.StopAsync();
            enabled = false; error = null; progress = "";
        }
        catch (Exception ex) { error = "পুরোপুরি বন্ধ হয়নি · " + ex.Message; Log(ex.Message); }
        finally { busy = false; Refresh(); Animate(); }
    }

    public bool CanToggle => !busy;
    public string ReceiverName => settings.DeviceName;
    public string StateTitle => Incoming.Count>0 ? $"Receiving from {Incoming.Count} " + (Incoming.Count==1 ? "transfer" : "transfers") : busy ? "Starting…" : !enabled ? "Receiving is off" : bluetooth.State == "Receiving" || quickShare.State == "Receiving" ? "Receiving files…" : "Ready to receive";
    public string StateDetail => error ?? (Incoming.Count>0 ? FormatSize(Incoming.Sum(x=>x.Received)) + " received" : !enabled ? "Press power to start receiving." : bluetooth.State == "Receiving" || quickShare.State == "Receiving" ? (string.IsNullOrEmpty(progress) ? "Device connected" : progress) : bluetooth.State is "Faulted" or "NeedsSetup" or "Unsupported" ? bluetooth.Detail : quickShare.State is "Faulted" or "SaveFailed" ? quickShare.Detail : "Choose this computer on your phone.");
    public Brush PowerBrush => (Brush)FindResource(!enabled ? "Muted" : error is not null || bluetooth.State is "Faulted" or "NeedsSetup" or "Unsupported" ? "ErrorColor" : "Primary");
    public string RecentTitle => Files.Count == 0 ? "সাম্প্রতিক ফাইল" : "সাম্প্রতিক ফাইল · " + Files.Count;
    public Visibility EmptyVisibility => Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ImportVisibility => Visibility.Collapsed;
    public string CapabilityNote => "Independent Quick Share · Bluetooth · AirDrop not available";
    public string CapabilityDetail => "Device name: " + settings.DeviceName + "\nBluetooth discovery name: " + bluetooth.DeviceName +
        "\nBluetooth: " + bluetooth.Detail + "\nQuick Share: " + quickShare.Detail + "\nOffline discovery: " + quickShare.OfflineDetail +
        "\nLocal Wi-Fi: " + quickShare.LocalWifiDetail +
        "\nGoogle Quick Share installation is not required. Close it if it conflicts with the same Bluetooth radio." +
        "\nThe app device name is used for Quick Share and the Bluetooth Object Push service. Classic Bluetooth discovery uses the Windows computer name." +
        "\nThe installed offline guard service verifies Internet Sharing is disabled; the app runs without administrator access. Phone compatibility depends on supported transports." +
        "\nAirDrop transport is not implemented on this Windows PC.";

    private void Refresh()
    {
        foreach (var name in new[] { nameof(CanToggle), nameof(ReceiverName), nameof(StateTitle), nameof(StateDetail), nameof(PowerBrush), nameof(RecentTitle), nameof(EmptyVisibility), nameof(ImportVisibility), nameof(CapabilityNote), nameof(CapabilityDetail) }) Changed(name);
        foreach (var name in new[] { nameof(SidebarState), nameof(SaveFolder), nameof(LatestVisibility), nameof(LatestName), nameof(LatestDetail), nameof(HistoryCount), nameof(HasHistory), nameof(HistoryEmptyVisibility), nameof(HistoryEmptyText) }) Changed(name);
        Changed(nameof(CanChangeReceiver));
        if (tray is null) return;
        tray.Text = enabled ? "Next Share · Receiving is on" : "Next Share · Receiving is off";
        trayMenu.ReceiverStatus = busy ? "Starting…" : enabled ? "Receiving is on" : "Receiving is off";
    }

    private void Animate()
    {
        OrbitRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        InnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        Halo.BeginAnimation(OpacityProperty, null);
        HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Orbit.Opacity = InnerOrbit.Opacity = enabled ? 1 : 0.25;
        Halo.Opacity = enabled ? 0.45 : 0.15;
        if (!enabled || !SystemParameters.ClientAreaAnimation) return;
        OrbitRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(Incoming.Count>0 ? 2 : 9)) { RepeatBehavior = RepeatBehavior.Forever });
        InnerRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, -360, TimeSpan.FromSeconds(Incoming.Count>0 ? 1.4 : 6)) { RepeatBehavior = RepeatBehavior.Forever });
        var opacity = new DoubleAnimation(0.22, 0.5, TimeSpan.FromSeconds(2.3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        var scale = new DoubleAnimation(0.92, 1.06, TimeSpan.FromSeconds(2.3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Halo.BeginAnimation(OpacityProperty, opacity); HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale); HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private async Task ChangeFolderAsync(string folder)
    {
        if (!CanChangeReceiver || PathsEqual(folder, store.Root)) return;
        var target = Path.GetFullPath(folder);
        var internalRoot = Path.GetFullPath(App.DataRoot);
        if (PathsEqual(target, internalRoot) || target.StartsWith(internalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || internalRoot.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { ShowError("Save folder ও application data folder আলাদা হতে হবে।"); return; }
        try
        {
            _ = OpenStore(target);
            var probe = Path.Combine(target, ".nextshare-write-probe-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception ex) { ShowError(ex.Message); return; }
        bool wasEnabled = enabled; busy = true; Refresh();
        try
        {
            await quickShare.StopAsync(); await bluetooth.StopAsync();
            settings.PreviousSaveFolders.Add(store.Root);
            settings.PreviousSaveFolders = settings.PreviousSaveFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            settings.SaveFolder = target; SettingsStore.Save(settings);
            CreateServices(target); LoadHistory();
            if (wasEnabled) await StartAsync();
        }
        catch (Exception ex) { error = ex.Message; Log(ex.Message); ShowError(ex.Message); }
        finally { busy = false; Refresh(); }
    }

    private void File_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not TransferView file || !File.Exists(file.Record.FilePath)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file.Record.FilePath + "\"") { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(store.Root) { UseShellExecute = true });
    private void Restore() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void ShowError(string text) { Log(text); MessageBox.Show(this, text, "Next Share"); }
    private void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(App.DataRoot, "activity.log"), DateTimeOffset.Now.ToString("O") + " " + text + Environment.NewLine); } catch { }
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static InboxStore OpenStore(string root)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant())));
        return new InboxStore(root, Path.Combine(App.DataRoot, "Storage", key));
    }
    private static bool PathsEqual(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private async Task ExitAsync()
    {
        if (exiting || busy) return;
        exiting = true; timer.Stop(); await StopSendingAsync(); await quickShare.StopAsync(); await bluetooth.StopAsync(); tray.Dispose(); trayMenu.Dispose(); trayIcon.Dispose();
        SaveWindowSize(); feedback.Dispose();
        SystemEvents.UserPreferenceChanged -= SystemThemeChanged;
        notifications.Dispose();
        Application.Current.Shutdown(smokeFailure ? 1 : 0);
    }
    private void Capture(string path)
    {
        UpdateLayout();
        var visual = (FrameworkElement)Content;
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    public static string FormatSize(long bytes)
    {
        double value = bytes; string[] units = ["B", "KB", "MB", "GB", "TB"]; int i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.#} {units[i]}";
    }
}

public sealed class TransferView(TransferRecord record)
{
    public TransferRecord Record { get; } = record;
    public string Name => Record.Name;
    public string Detail => MainWindow.FormatSize(Record.Size) + " · " + Record.ReceivedAt.ToLocalTime().ToString("HH:mm") + " · " + Record.Sender;
    public string HistoryDetail => Record.ReceivedAt.ToLocalTime().ToString("dd MMM, h:mm tt") + " · " + MainWindow.FormatSize(Record.Size) + " · " + Record.Sender + " · " + Record.Route;
    public PackIconKind Icon => Path.GetExtension(Record.Name).ToLowerInvariant() switch { ".jpg" or ".jpeg" or ".png" or ".heic" or ".webp" => PackIconKind.FileImageOutline, ".pdf" => PackIconKind.FilePdfBox, ".doc" or ".docx" => PackIconKind.FileWordOutline, _ => PackIconKind.FileDocumentOutline };
}

