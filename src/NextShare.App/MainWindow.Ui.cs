using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using NextShare.Core;
using NextShare.App.Services;

namespace NextShare.App;

public partial class MainWindow
{
    private string page = "Receive", query = "", route = "All transfers";
    private HashSet<string> hiddenHistory = new(StringComparer.Ordinal);
    private bool darkTheme;
    private readonly SnackbarMessageQueue notifications = new(TimeSpan.FromSeconds(5));
    public ICollectionView HistoryView { get; private set; } = null!;
    public string[] Themes { get; } = ["System", "Light", "Dark"];
    public string[] Accents { get; } = ["Teal", "Blue", "Violet"];
    public string[] HistoryRoutes { get; } = ["All transfers", "Bluetooth", "Quick Share"];
    public string PageTitle => page;
    public string AppVersion => "Version " + typeof(App).Assembly.GetName().Version!.ToString(3);
    public Visibility ReceiveVisibility => page == "Receive" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HistoryVisibility => page == "History" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SettingsVisibility => page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
    public string SidebarState => busy ? "Starting…" : enabled ? "Receiving is on" : "Receiving is off";
    public string SaveFolder => store.Root;
    private TransferView? Latest => Files.FirstOrDefault(f => !hiddenHistory.Contains(f.Record.Id));
    public Visibility LatestVisibility => Latest is null || Incoming.Count>0 ? Visibility.Collapsed : Visibility.Visible;
    public string LatestName => Latest?.Name ?? "";
    public string LatestDetail => Latest?.Detail ?? "";
    public string HistoryQuery { get => query; set { if (query == value) return; query = value; Changed(); RefreshHistory(); } }
    public string HistoryRoute { get => route; set { if (route == value || value is null) return; route = value; Changed(); RefreshHistory(); } }
    private int HistoryVisibleCount => HistoryView is CollectionView view ? view.Count : 0;
    public string HistoryCount => HistoryVisibleCount + (HistoryVisibleCount == 1 ? " received file" : " received files");
    public bool HasHistory => Files.Any(f => !hiddenHistory.Contains(f.Record.Id));
    public Visibility HistoryEmptyVisibility => HistoryVisibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string HistoryEmptyText => HasHistory ? "No matching files" : "No transfers yet";
    public string ThemeMode
    {
        get => settings.Theme;
        set { if (!Themes.Contains(value) || settings.Theme == value) return; settings.Theme = value; SavePreferences(); ApplyTheme(); Changed(); }
    }
    public string Accent
    {
        get => settings.Accent;
        set { if (!Accents.Contains(value) || settings.Accent == value) return; settings.Accent = value; SavePreferences(); ApplyTheme(); Changed(); }
    }
    public bool CloseToTray { get => settings.CloseToTray; set { if (settings.CloseToTray == value) return; settings.CloseToTray = value; SavePreferences(); Changed(); } }
    public bool DesktopNotifications { get => settings.DesktopNotifications; set { if (settings.DesktopNotifications == value) return; settings.DesktopNotifications = value; if (!value) feedback?.Dismiss(); SavePreferences(); Changed(); } }
    public bool ReceiveSound { get => settings.ReceiveSound; set { if (settings.ReceiveSound == value) return; settings.ReceiveSound = value; SavePreferences(); Changed(); } }
    private string soundStatus = "";
    public System.Collections.ObjectModel.ObservableCollection<SoundOutput> SoundOutputs {get;}=[new(null,"Windows default output")];
    private SoundOutput? soundOutput;
    public SoundOutput? SoundOutput
    {
        get=>soundOutput;
        set
        {
            if(value is null||soundOutput==value)return;
            soundOutput=value;settings.SoundOutputDeviceId=value.Id;SavePreferences();Changed();
        }
    }
    private async Task RefreshSoundOutputsAsync()
    {
        try
        {
            var outputs=await SignatureChime.OutputsAsync();
            SoundOutputs.Clear();SoundOutputs.Add(new(null,"Windows default output"));
            foreach(var output in outputs)SoundOutputs.Add(output);
            soundOutput=SoundOutputs.FirstOrDefault(x=>x.Id==settings.SoundOutputDeviceId);
            if(soundOutput is null)
            {soundOutput=new(settings.SoundOutputDeviceId,"Selected output is unavailable");SoundOutputs.Add(soundOutput);}
            Changed(nameof(SoundOutput));
        }
        catch(Exception ex){soundStatus="Audio devices unavailable: "+ex.Message;Changed(nameof(SoundStatus));}
    }
    private async void RefreshSoundOutputs_Click(object sender,RoutedEventArgs e)=>await RefreshSoundOutputsAsync();
    public string SoundStatus => soundStatus;
    private void PreviewFeedback_Click(object sender, RoutedEventArgs e)
    {
        soundStatus = ReceiveSound ? "Playing signature sound…" : "Signature sound is off.";
        Changed(nameof(SoundStatus));
        feedback.Preview();
    }
    private void SaveWindowSize()
    {
        if (smoke) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.Width >= MinWidth && bounds.Height >= MinHeight)
        { settings.WindowWidth = bounds.Width; settings.WindowHeight = bounds.Height; SavePreferences(); }
    }
    private void UpdateResponsiveLayout()
    {
        bool compact = Width < 740;
        SidebarColumn.Width = new GridLength(compact ? 76 : 200);
        Sidebar.Padding = compact ? new Thickness(6, 26, 6, 18) : new Thickness(12, 26, 12, 18);
        Brand.Margin = compact ? new Thickness(19, 0, 0, 28) : new Thickness(12, 0, 0, 28);
        BrandName.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var nav in new[] { ReceiveNav, SendNav, HistoryNav, SettingsNav })
        {
            if (nav.Content is StackPanel panel && panel.Children[1] is TextBlock label)
                label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            nav.ToolTip = nav.Tag;
        }
        SidebarStatus.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PageContent.Margin = compact ? new Thickness(20, 14, 20, 16) : new Thickness(28, 18, 28, 20);
    }
    private void InitializeUi()
    {
        DeviceNameDraft=settings.DeviceName;
        hiddenHistory = new(settings.HiddenHistoryIds ?? [], StringComparer.Ordinal);
        if (!Themes.Contains(settings.Theme)) settings.Theme = "System";
        if (!Accents.Contains(settings.Accent)) settings.Accent = "Teal";
        HistoryView = CollectionViewSource.GetDefaultView(Files);
        HistoryView.Filter = item => item is TransferView file && !hiddenHistory.Contains(file.Record.Id) &&
            (route == "All transfers" || file.Record.Route.Contains(route, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query) || file.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || file.Record.Sender.Contains(query, StringComparison.OrdinalIgnoreCase));
        Toast.MessageQueue = notifications;
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += SystemThemeChanged;
    }
    private void SystemThemeChanged(object sender, UserPreferenceChangedEventArgs args)
    { if (settings.Theme == "System" || SystemParameters.HighContrast) Dispatcher.InvokeAsync(ApplyTheme); }
    private void ApplyTheme() { darkTheme = AppTheme.Apply(settings.Theme, settings.Accent); trayMenu?.ApplyTheme(); Refresh(); }
    private void SavePreferences() { try { SettingsStore.Save(settings); } catch (Exception e) { ShowError("Settings save failed: " + e.Message); } }
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton nav && nav.Tag is string target)
        {
            page = target;
            foreach (string property in new[] { nameof(PageTitle), nameof(ReceiveVisibility), nameof(SendVisibility), nameof(HistoryVisibility), nameof(SettingsVisibility) }) Changed(property);
            if (target == "Send") StartScan(); else scanCancellation?.Cancel();
        }
    }
    private void History_Click(object sender, RoutedEventArgs e) => HistoryNav.IsChecked = true;
    private void RefreshHistory()
    {
        HistoryView?.Refresh();
        foreach (string property in new[] { nameof(HistoryCount), nameof(HasHistory), nameof(HistoryEmptyVisibility), nameof(HistoryEmptyText), nameof(LatestVisibility), nameof(LatestName), nameof(LatestDetail) }) Changed(property);
    }
    private void HideHistory(IEnumerable<string> ids)
    {
        var before = hiddenHistory.ToHashSet(StringComparer.Ordinal);
        hiddenHistory.UnionWith(ids); settings.HiddenHistoryIds = hiddenHistory.ToList(); SavePreferences(); RefreshHistory();
        notifications.Enqueue("History cleared. Saved files are kept.", "UNDO", () =>
        {
            Dispatcher.Invoke(() => { hiddenHistory = before; settings.HiddenHistoryIds = before.ToList(); SavePreferences(); RefreshHistory(); });
        });
    }
    private void ClearHistory_Click(object sender, RoutedEventArgs e) => HideHistory(Files.Select(f => f.Record.Id));
    private void LatestFile_Click(object sender, RoutedEventArgs e) { if (Latest is { } file) ShowFile(file); }
    private void ShowFile(TransferView file)
    {
        if (!File.Exists(file.Record.FilePath)) { notifications.Enqueue("This file was moved or deleted."); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file.Record.FilePath + "\"") { UseShellExecute = true });
    }
    private void FileMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TransferView file } button) return;
        var menu = new ContextMenu();
        var show = new MenuItem { Header = "Show in folder", Icon = new PackIcon { Kind = PackIconKind.FolderOpenOutline } }; show.Click += (_, _) => ShowFile(file);
        var copy = new MenuItem { Header = "Copy file path", Icon = new PackIcon { Kind = PackIconKind.ContentCopy } }; copy.Click += (_, _) => { Clipboard.SetText(file.Record.FilePath); notifications.Enqueue("File path copied."); };
        var remove = new MenuItem { Header = "Remove from history", Icon = new PackIcon { Kind = PackIconKind.DeleteOutline } }; remove.Click += (_, _) => HideHistory([file.Record.Id]);
        menu.Items.Add(show); menu.Items.Add(copy); menu.Items.Add(new Separator()); menu.Items.Add(remove);
        menu.PlacementTarget = button; menu.IsOpen = true;
    }
    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Save received files to", InitialDirectory = store.Root };
        if (picker.ShowDialog(this) == true) await ChangeFolderAsync(picker.FolderName);
    }
    private async void ReceiverInfo_Click(object sender, RoutedEventArgs e)
    {
        if (MainDialog.IsOpen) return;
        var panel = new StackPanel { Margin = new Thickness(28), Width = 430 };
        panel.Children.Add(new TextBlock { Text = "Receiver information", FontSize = 22, Margin = new Thickness(0,0,0,18) });
        panel.Children.Add(new TextBlock { Text = CapabilityDetail, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = (Brush)FindResource("Muted"), LineHeight = 22 });
        var close = new Button { Content = "Done", Command = DialogHost.CloseDialogCommand, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,24,0,0), Style = (Style)FindResource("MaterialDesignFlatButton") };
        panel.Children.Add(close);
        await MainDialog.ShowDialog(panel);
    }
    private async Task ExitWhenReadyAsync()
    { while (busy) await Task.Delay(100); await ExitAsync(); }

    private async Task<object> ExerciseMaterialUiAsync()
    {
        bool brandingVerified = Icon is not null && BrandLogo.Source is System.Windows.Media.Imaging.BitmapSource { PixelWidth: 512, PixelHeight: 512 }
            && tray.Icon is { Width: 32, Height: 32 };
        if (!brandingVerified) throw new InvalidOperationException("Packaged window/sidebar/tray icon could not be decoded");
        var originalTheme = settings.Theme; var originalAccent = settings.Accent;
        var originalHidden = hiddenHistory.ToHashSet();
        ThemeMode = "Light"; ReceiveNav.IsChecked = true; await Task.Delay(80);
        Capture(Path.Combine(App.DataRoot,"receive-light.png"));
        HistoryNav.IsChecked = true; await Task.Delay(80); Capture(Path.Combine(App.DataRoot,"history-light.png"));
        bool searchVerified = true, clearVerified = true;
        if (Files.Any())
        {
            HistoryQuery = "ছবি";
            searchVerified = HistoryVisibleCount > 0 && HistoryView.Cast<TransferView>().All(f => f.Name.Contains("ছবি"));
            HistoryQuery = "";
            var paths = Files.Select(f=>f.Record.FilePath).ToArray(); HideHistory(Files.Select(f=>f.Record.Id));
            clearVerified = HistoryVisibleCount == 0 && paths.All(File.Exists);
            Capture(Path.Combine(App.DataRoot,"history-empty.png"));
            hiddenHistory = originalHidden; settings.HiddenHistoryIds = originalHidden.ToList(); SavePreferences(); RefreshHistory();
        }
        notifications.Clear(); Toast.IsActive = false;
        SettingsNav.IsChecked = true; await Task.Delay(80); Capture(Path.Combine(App.DataRoot,"settings-light.png"));
        ThemeMode = "Dark"; await Task.Delay(80); Capture(Path.Combine(App.DataRoot,"settings-dark.png"));
        bool persisted = SettingsStore.Load().Theme == "Dark";
        ReceiveNav.IsChecked = true; Capture(Path.Combine(App.DataRoot,"receive-dark.png"));
        Accent = "Blue"; Capture(Path.Combine(App.DataRoot,"receive-blue-dark.png"));
        bool themeVerified = darkTheme && ((SolidColorBrush)FindResource("Primary")).Color == (Color)ColorConverter.ConvertFromString("#A4C7FF");
        ThemeMode="Light"; Accent="Teal"; ReceiveNav.IsChecked=true;
        await ExerciseTrayMenuAsync();
        var resizeResults = new List<object>();
        bool resizeVerified = true;
        foreach (var size in new[] { (830,560), (820,552), (760,480), (640,440), (1100,740) })
        {
            Width=size.Item1; Height=size.Item2; await Task.Delay(150); UpdateLayout();
            var viewport = HeroViewport.TransformToAncestor(ReceivePage).TransformBounds(new Rect(HeroViewport.RenderSize));
            var hero = ReceiveHero.TransformToAncestor(ReceivePage).TransformBounds(new Rect(ReceiveHero.RenderSize));
            var dial = ReceiverDial.TransformToAncestor(ReceivePage).TransformBounds(new Rect(ReceiverDial.RenderSize));
            bool inside = hero.Left >= viewport.Left - 1 && hero.Top >= viewport.Top - 1 && hero.Right <= viewport.Right + 1 && hero.Bottom <= viewport.Bottom + 1 && hero.Bottom <= ReceivePage.ActualHeight && dial.Width > 95;
            resizeVerified &= inside;
            resizeResults.Add(new { Width, Height, HeroBounds=hero.ToString(), DialWidth=dial.Width, FitsViewport=inside });
            Capture(Path.Combine(App.DataRoot,$"receive-{size.Item1}x{size.Item2}.png"));
        }
        double angle = OrbitRotation.Angle; await Task.Delay(350);
        bool animationVerified = !SystemParameters.ClientAreaAnimation || Math.Abs(OrbitRotation.Angle - angle) > 1;
        Width=640; Height=440; SettingsNav.IsChecked=true; await Task.Delay(80); Capture(Path.Combine(App.DataRoot,"settings-small.png"));
        Width=830; Height=560; ThemeMode=originalTheme; Accent=originalAccent; ReceiveNav.IsChecked=true;
        string originalName=settings.DeviceName;
        await ChangeDeviceNameAsync("Next Share test desk");
        using var named=System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(App.DataRoot,"quickshare-native-status.json")));
        bool nameVerified=ReceiverName=="Next Share test desk"&&SettingsStore.Load().DeviceName==ReceiverName&&named.RootElement.GetProperty("name").GetString()==ReceiverName;
        await ChangeDeviceNameAsync(Environment.MachineName);
        nameVerified&=ReceiverName==Environment.MachineName&&settings.DeviceNameOverride is null;
        await ToggleAsync();await ChangeDeviceNameAsync("Offline desk");nameVerified&=!enabled&&!quickShare.Running;
        await ChangeDeviceNameAsync(originalName);await ToggleAsync();
        if(!nameVerified||CapabilityDetail.Any(c=>c>='\u0980'&&c<='\u09ff'))throw new InvalidOperationException("Device name propagation or English receiver information failed");
        if (!searchVerified || !clearVerified || !themeVerified || !persisted) throw new InvalidOperationException("Material UI checks failed");
        if (!resizeVerified || !animationVerified) throw new InvalidOperationException("Receive layout clips or animation stopped: " + System.Text.Json.JsonSerializer.Serialize(resizeResults));
        return new { Passed = true, BrandingVerified=brandingVerified,DeviceNamePersistedAndAdvertised=nameVerified,EnglishReceiverInfo=true, SearchVerified=searchVerified, ClearKeepsFiles=clearVerified, ThemePersisted=persisted, DarkAndAccentVerified=themeVerified, ResponsiveReceiveVerified=resizeVerified, ContinuousAnimationVerified=animationVerified, ResizeCases=resizeResults, MaterialToolkit="5.3.2", Screens=new[] {"Receive","History","Settings"} };
    }

    private async Task ExerciseTrayMenuAsync()
    {
        var workArea = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var location = new System.Drawing.Point(workArea.Right - 300, workArea.Bottom - 230);
        void CaptureMenu(string name)
        {
            using var bitmap = new System.Drawing.Bitmap(trayMenu.Width, trayMenu.Height);
            trayMenu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(Path.Combine(App.DataRoot, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        try
        {
            trayMenu.Show(location); await Task.Delay(100); CaptureMenu("tray-menu-light");
            trayMenu.OpenItem.Select(); await Task.Delay(50); CaptureMenu("tray-menu-open-hover");
            trayMenu.ExitItem.Select(); await Task.Delay(50); CaptureMenu("tray-menu-exit-hover");
            if (trayMenu.OpenItem.Bounds.Bottom > trayMenu.Height || trayMenu.ExitItem.Bounds.Bottom > trayMenu.Height)
                throw new InvalidOperationException("Tray actions clip outside their menu");
            trayMenu.Close();
            ThemeMode = "Dark";
            trayMenu.Show(location); await Task.Delay(100); CaptureMenu("tray-menu-dark");
            trayMenu.Close();
            ThemeMode = "Light";
            Hide(); trayMenu.OpenItem.PerformClick(); await Task.Delay(80);
            if (!IsVisible || WindowState != WindowState.Normal) throw new InvalidOperationException("Tray Open did not restore the app");
            // Verify the styled Exit action invokes its callback without ending the smoke host early.
            int exits = 0;
            using var menu = new StyledTrayMenu(trayIcon, () => { }, () => exits++);
            menu.ExitItem.PerformClick();
            if (exits != 1) throw new InvalidOperationException("Tray Exit callback failed");
        }
        finally { trayMenu.Close(); ThemeMode = "Light"; }
    }

    private async Task<object> ExerciseReceiveFeedbackAsync()
    {
        feedback.Dismiss();await Task.Delay(850);
        bool oldDesktop=DesktopNotifications,oldSound=ReceiveSound;
        DesktopNotifications=true;ReceiveSound=true;ReceiveNav.IsChecked=true;
        int before=feedback.PresentedFiles,sounds=feedback.SoundRequests,doneBefore=feedback.CompletedFiles;
        var offers=Enumerable.Range(0,3).Select(i=>new TransferOffer(Guid.NewGuid().ToString("N"),$"Incoming document {i}.txt","Bluetooth",$"Test device {i}",1024)).ToArray();
        var files=new List<IncomingFile>();int recordsBefore=Files.Count;
        Hide();
        foreach(var offer in offers)
        {
            var file=store.Begin(offer,2048);files.Add(file);BeginIncoming(offer);
            await file.WriteAsync(new byte[512],CancellationToken.None);
            UpdateIncoming("Bluetooth",new(offer.SessionId,offer.Name,512,1024),false);
        }
        await Task.Delay(150);
        var card=feedback.VisibleCard;
        bool early=card is {IsVisible:true,IsActive:false,FileCount:3}&&Files.Count==recordsBefore&&Incoming.Count==3&&Incoming.All(x=>x.Percent==50)&&feedback.PresentedFiles==before+3;
        bool sound=feedback.SoundLoaded&&feedback.SoundRequests==sounds+1;
        if(card is not null)
        {
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)card.ActualWidth,(int)card.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render((FrameworkElement)card.Content);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output=File.Create(Path.Combine(App.DataRoot,"receive-notification.png"));encoder.Save(output);
        }
        Show();double angle=OrbitRotation.Angle;await Task.Delay(180);
        bool animates=!SystemParameters.ClientAreaAnimation||Math.Abs(OrbitRotation.Angle-angle)>1;
        bool fits=true;
        foreach(var size in new[]{(779,560),(640,440)})
        {
            Width=size.Item1;Height=size.Item2;await Task.Delay(80);UpdateLayout();
            var hero=ReceiveHero.TransformToAncestor(ReceivePage).TransformBounds(new Rect(ReceiveHero.RenderSize));
            var scroll=IncomingScroll.TransformToAncestor(ReceivePage).TransformBounds(new Rect(IncomingScroll.RenderSize));
            fits&=hero.Top>=-1&&hero.Bottom<=scroll.Top+1&&scroll.Bottom<=ReceivePage.ActualHeight&&ReceiverDial.ActualWidth>0;
            Capture(Path.Combine(App.DataRoot,$"receiving-{size.Item1}x{size.Item2}.png"));
        }
        UpdateIncoming("Bluetooth",new(offers[0].SessionId,offers[0].Name,1024,1024),true);
        bool processing=Incoming.First().Processing&&Incoming.First().Indeterminate;
        for(int i=0;i<3;i++){await files[i].WriteAsync(new byte[512],CancellationToken.None);await files[i].CommitAsync(CancellationToken.None);await files[i].DisposeAsync();}
        await Task.Delay(180);
        bool completed=Incoming.Count==0&&Files.Count==recordsBefore+3&&feedback.CompletedFiles==doneBefore+3&&feedback.CompletionCard is {IsVisible:true,IsActive:false,IsCompletion:true,FileCount:3,TotalBytes:3072};
        if(feedback.CompletionCard is {} completedCard)
        {
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)completedCard.ActualWidth,(int)completedCard.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render((FrameworkElement)completedCard.Content);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output=File.Create(Path.Combine(App.DataRoot,"receive-completed-notification.png"));encoder.Save(output);
        }
        foreach(var offer in offers)EndIncoming("Bluetooth",offer.SessionId,false);
        bool deduped=feedback.CompletedFiles==doneBefore+3;
        await Task.Delay(2100);
        bool secondSound=feedback.SoundRequests==sounds+2;
        long notificationLifetime=feedback.LastDismissMilliseconds;
        bool dismissed=feedback.VisibleCard is null&&notificationLifetime is >=1900 and <2800;
        var slow=new TransferOffer(Guid.NewGuid().ToString("N"),"slow.bin","Bluetooth","Slow device",null);
        await using(var slowFile=store.Begin(slow,2048))
        {
            BeginIncoming(slow);await slowFile.WriteAsync(new byte[777],CancellationToken.None);
            await Task.Delay(2200);
            bool expired=feedback.VisibleCard is null;
            await slowFile.CommitAsync(CancellationToken.None);await Task.Delay(120);
            completed &= expired&&feedback.CompletionCard is {FileCount:1,TotalBytes:777,IsVisible:true}&&feedback.CompletedFiles==doneBefore+4;
        }
        feedback.Dismiss();
        var batch=new TransferOffer(Guid.NewGuid().ToString("N"),"batch-a.bin","Quick Share","Batch device",15);
        BeginIncoming(batch,2);
        foreach(var (name,size) in new[]{("batch-a.bin",5),("batch-b.bin",10)})
        {
            await using var file=store.Begin(batch with{Name=name,Size=size},2048);
            await file.WriteAsync(new byte[size],CancellationToken.None);await file.CommitAsync(CancellationToken.None);
        }
        await Task.Delay(100);int preBatch=feedback.CompletedFiles;
        bool batchWait=feedback.CompletionCard is null;
        EndIncoming("Quick Share",batch.SessionId,true);EndIncoming("Quick Share",batch.SessionId,true);
        bool batchComplete=batchWait&&feedback.CompletedFiles==preBatch+2&&feedback.CompletionCard is {FileCount:2,TotalBytes:15};
        feedback.Dismiss();int preFailure=feedback.CompletedFiles;
        var interrupted=new TransferOffer(Guid.NewGuid().ToString("N"),"cancelled.txt","Bluetooth","Disconnected device",null);
        BeginIncoming(interrupted);UpdateIncoming("Bluetooth",new(interrupted.SessionId,interrupted.Name,10,null),false);
        bool unknown=Incoming.Single().Indeterminate;
        EndIncoming("Bluetooth",interrupted.SessionId,false);bool cleanup=Incoming.Count==0&&feedback.CompletedFiles==preFailure&&feedback.CompletionCard is null;
        var partial=batch with{SessionId=Guid.NewGuid().ToString("N")};BeginIncoming(partial,2);
        await using(var file=store.Begin(partial with{Size=5},2048))
        {await file.WriteAsync(new byte[5],CancellationToken.None);await file.CommitAsync(CancellationToken.None);}
        await Task.Delay(100);EndIncoming("Quick Share",partial.SessionId,false);
        cleanup &= feedback.CompletedFiles==preFailure&&feedback.CompletionCard is null;
        feedback.Dismiss();DesktopNotifications=false;ReceiveSound=false;
        int disabledBefore=feedback.PresentedFiles,disabledSound=feedback.SoundRequests;feedback.Preview();
        bool toggles=feedback.VisibleCard is null&&disabledBefore==feedback.PresentedFiles&&disabledSound==feedback.SoundRequests;
        DesktopNotifications=oldDesktop;ReceiveSound=oldSound;Width=830;Height=560;
        if(!early||!sound||!secondSound||!deduped||!batchComplete||!animates||!fits||!processing||!completed||!dismissed||!unknown||!cleanup||!toggles)
            throw new InvalidOperationException($"Receive feedback: early={early},sound={sound},secondSound={secondSound},deduped={deduped},batch={batchComplete},animates={animates},fits={fits},processing={processing},completed={completed},dismissed={dismissed},unknown={unknown},cleanup={cleanup},toggles={toggles}");
        return new{Passed=true,NotificationBeforeCommit=early,ConcurrentProgressVerified=true,AnimationVerified=animates,SmallWindowFits=fits,ProcessingState=processing,UnknownSizeSupported=unknown,FailureCleanup=cleanup,CompletionNotificationAfterDurableSave=completed,QuickShareBatchCompletion=batchComplete,DuplicateCompletionSuppressed=deduped,CompletionChimeQueued=secondSound,ClosedAfterTwoSeconds=dismissed,NotificationLifetimeMilliseconds=notificationLifetime,SoundLoaded=sound,SoundPlaybackSkipped=smoke,SettingsDisableVerified=toggles};
    }
}
