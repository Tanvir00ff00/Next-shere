using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using NextShare.App.Services;
using NextShare.Core;

namespace NextShare.App;

public partial class MainWindow
{
    private SendQueue? sendQueue;
    private SendQueue Queue=>sendQueue??=new(Path.Combine(App.DataRoot,"SendQueue"));
    private CancellationTokenSource? scanCancellation,sendCancellation;
    private Task? scanTask,sendTask;
    private bool scanning,sending,preparing;
    private string sendStatus="Choose files, then select a nearby device.",discoveryStatus="Turn on Bluetooth or open Quick Share → Receive on the phone.";
    private double sendPercent;
    public ObservableCollection<SendFileView> Selection {get;}=[];
    public ObservableCollection<SendDeviceView> NearbyDevices {get;}=[];
    public Visibility SendVisibility=>page=="Send"?Visibility.Visible:Visibility.Collapsed;
    public string SelectionSummary=>Selection.Count==0?"Selection":$"{Selection.Count} selected · {FormatSize(Selection.Sum(f=>f.File.Length))}";
    public string SendStatus=>sendStatus;
    public string DiscoveryStatus=>discoveryStatus;
    public double SendPercent=>sendPercent;
    public bool CanEditSelection=>!sending&&!preparing&&!exiting;
    public bool CanSend=>CanEditSelection&&Selection.Count>0;
    public bool CanRefreshDevices=>!scanning&&!sending&&!exiting;
    public Visibility SendingVisibility=>sending||preparing?Visibility.Visible:Visibility.Collapsed;
    public Visibility SelectionVisibility=>Selection.Count>0?Visibility.Visible:Visibility.Collapsed;
    public Visibility DropHintVisibility=>Selection.Count==0?Visibility.Visible:Visibility.Collapsed;
    public Visibility DeviceEmptyVisibility=>NearbyDevices.Count==0?Visibility.Visible:Visibility.Collapsed;
    public string DeviceEmptyText=>scanning?"Looking for nearby devices…":"No devices found yet";
    public Visibility ScanningVisibility=>scanning?Visibility.Visible:Visibility.Collapsed;
    private void RefreshSend()
    {
        Changed(nameof(CanChangeReceiver));
        foreach(var name in new[]{nameof(SelectionSummary),nameof(SendStatus),nameof(DiscoveryStatus),nameof(SendPercent),nameof(CanEditSelection),nameof(CanSend),nameof(CanRefreshDevices),nameof(SendingVisibility),nameof(SelectionVisibility),nameof(DropHintVisibility),nameof(DeviceEmptyVisibility),nameof(DeviceEmptyText),nameof(ScanningVisibility)})Changed(name);
    }
    private void StartScan()
    {
        if(exiting||sending||(scanning&&scanCancellation?.IsCancellationRequested!=true)||smoke)return;
        scanCancellation?.Dispose();scanCancellation=new();var token=scanCancellation.Token;
        NearbyDevices.Clear();scanning=true;discoveryStatus="Keep Quick Share's Receive screen open; Bluetooth devices may need pairing.";RefreshSend();
        var discovery=new SendDiscovery();
        discovery.Found+=target=>Dispatcher.InvokeAsync(()=>
        {
            if(token.IsCancellationRequested||exiting)return;
            var old=NearbyDevices.FirstOrDefault(d=>d.Target.Id==target.Id);if(old is not null)NearbyDevices.Remove(old);
            var entry=new SendDeviceView(target);int index=0;
            while(index<NearbyDevices.Count&&string.Compare(NearbyDevices[index].Name,entry.Name,StringComparison.CurrentCultureIgnoreCase)<=0)index++;
            NearbyDevices.Insert(index,entry);RefreshSend();
        });
        discovery.Removed+=id=>Dispatcher.InvokeAsync(()=>{if(token.IsCancellationRequested)return;var item=NearbyDevices.FirstOrDefault(d=>d.Target.Id==id);if(item is not null)NearbyDevices.Remove(item);RefreshSend();});
        discovery.Notice+=message=>Dispatcher.InvokeAsync(()=>{if(token.IsCancellationRequested)return;discoveryStatus=message;Log("Send discovery: "+message);RefreshSend();});
        scanTask=Run();
        async Task Run()
        {
            try{await discovery.ScanAsync(!noRadio,token);}
            catch(OperationCanceledException)when(token.IsCancellationRequested){}
            catch(Exception e){if(!token.IsCancellationRequested)discoveryStatus=e.Message;}
            finally{if(scanCancellation?.Token==token){scanning=false;if(!token.IsCancellationRequested)Log($"Send discovery completed: {NearbyDevices.Count} device routes found.");RefreshSend();}}
        }
    }
    private void RefreshDevices_Click(object sender,RoutedEventArgs e)=>StartScan();
    private async void SelectFiles_Click(object sender,RoutedEventArgs e)
    {
        if(!CanEditSelection)return;
        var picker=new OpenFileDialog{Title="Select files to send",Multiselect=true};
        if(picker.ShowDialog(this)==true)await AddPathsAsync(picker.FileNames);
    }
    private async void SelectFolder_Click(object sender,RoutedEventArgs e)
    {
        if(!CanEditSelection)return;var picker=new OpenFolderDialog{Title="Choose a folder to send as a ZIP file"};
        if(picker.ShowDialog(this)==true)await AddPathsAsync([picker.FolderName]);
    }
    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if(!CanEditSelection)return;
        preparing=true;sendCancellation=new();var token=sendCancellation.Token;sendStatus="Preparing selection…";RefreshSend();
        var additions=new List<OutgoingFile>();
        try
        {
            foreach(var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if(Selection.Count+additions.Count>=1000)throw new IOException("Select at most 1000 files per transfer.");
                token.ThrowIfCancellationRequested();
                if(Selection.Any(f=>string.Equals(f.File.Path,Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase)))continue;
                additions.Add(Directory.Exists(path)?await Queue.FolderAsync(path,token):OutgoingFile.Inspect(path));
            }
            foreach(var file in additions)Selection.Add(new(file));
            sendStatus="Ready. Choose a device below to send. Folders are sent as ZIP files.";
        }
        catch(OperationCanceledException){sendStatus="Preparation cancelled.";foreach(var file in additions)Queue.Release(file.Path);}
        catch(Exception e){sendStatus=e.Message;foreach(var file in additions)Queue.Release(file.Path);}
        finally{preparing=false;sendCancellation.Dispose();sendCancellation=null;RefreshSend();}
    }
    private async void SelectText_Click(object sender,RoutedEventArgs e)
    {
        if(!CanEditSelection||MainDialog.IsOpen)return;
        var panel=new StackPanel{Margin=new Thickness(24),Width=Math.Min(380,ActualWidth-130)};
        panel.Children.Add(new TextBlock{Text="Text to send",FontSize=21,Margin=new Thickness(0,0,0,16)});
        var text=new TextBox{AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=160,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxLength=2*1024*1024,Style=(Style)FindResource("MaterialDesignOutlinedTextBox")};panel.Children.Add(text);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,20,0,0)};
        buttons.Children.Add(new Button{Content="Cancel",Command=DialogHost.CloseDialogCommand,CommandParameter=false,Style=(Style)FindResource("MaterialDesignFlatButton")});
        buttons.Children.Add(new Button{Content="Add text",Command=DialogHost.CloseDialogCommand,CommandParameter=true,Margin=new Thickness(10,0,0,0),Style=(Style)FindResource("MaterialDesignRaisedButton")});panel.Children.Add(buttons);
        if(await MainDialog.ShowDialog(panel) is true)await AddTextAsync(text.Text);
    }
    private async Task AddTextAsync(string text)
    {
        if(!CanEditSelection)return;
        preparing=true;sendCancellation=new();RefreshSend();
        try{if(Selection.Count>=1000)throw new IOException("Selection is full.");Selection.Add(new(await Queue.TextAsync(text,sendCancellation.Token)));sendStatus="Text added. Choose a device to send.";}
        catch(OperationCanceledException){sendStatus="Preparation cancelled.";}
        catch(Exception e){sendStatus=e.Message;}
        finally{preparing=false;sendCancellation.Dispose();sendCancellation=null;RefreshSend();}
    }
    private async void PasteSelection_Click(object sender,RoutedEventArgs e)
    {
        if(!CanEditSelection)return;
        try
        {
            if(Clipboard.ContainsFileDropList()){await AddPathsAsync(Clipboard.GetFileDropList().Cast<string>());return;}
            if(Clipboard.ContainsImage())
            {
                AddImage(Clipboard.GetImage());return;
            }
            if(Clipboard.ContainsText()){await AddTextAsync(Clipboard.GetText());return;}
            sendStatus="Copy files, an image or text, then paste here.";
        }
        catch(Exception ex){sendStatus=ex.Message;}RefreshSend();
    }
    private void AddImage(BitmapSource image)
    {
        if(!CanEditSelection)return;
        if(Selection.Count>=1000)throw new IOException("Selection is full.");
        string path=Queue.CreatePath("Clipboard-"+DateTime.Now.ToString("HHmmss")+".png");
        try{var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var stream=File.Create(path))encoder.Save(stream);Selection.Add(new(OutgoingFile.Inspect(path)));}
        catch{Queue.Release(path);throw;}
        sendStatus="Clipboard image added at its original size.";RefreshSend();
    }
    private void RemoveSelection_Click(object sender,RoutedEventArgs e)
    {
        if(!CanEditSelection||sender is not Button{DataContext:SendFileView file})return;
        Selection.Remove(file);Queue.Release(file.File.Path);RefreshSend();
    }
    private void ClearSelection_Click(object sender,RoutedEventArgs e)
    {if(!CanEditSelection)return;Selection.Clear();Queue.Clear();sendStatus="Choose files, then select a nearby device.";RefreshSend();}
    private void Window_DragOver(object sender,DragEventArgs e)
    {e.Effects=CanEditSelection&&(e.Data.GetDataPresent(DataFormats.FileDrop)||e.Data.GetDataPresent(DataFormats.UnicodeText))?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;}
    private async void Window_Drop(object sender,DragEventArgs e)
    {
        e.Handled=true;await AddDropAsync(e.Data);
    }
    private async Task AddDropAsync(IDataObject data)
    {
        if(!CanEditSelection)return;SendNav.IsChecked=true;
        if(data.GetData(DataFormats.FileDrop) is string[] paths)await AddPathsAsync(paths);
        else if(data.GetData(DataFormats.UnicodeText) is string text)await AddTextAsync(text);
    }
    private async void SendDevice_Click(object sender,RoutedEventArgs e)
    {
        if(!CanSend||sender is not Button{DataContext:SendDeviceView device})return;
        await SendSelectedAsync(device.Target);
    }
    private async Task SendSelectedAsync(SendTarget target)
    {
        var files=Selection.Select(f=>f.File).ToArray();if(files.Length==0)return;
        sending=true;sendPercent=0;sendStatus="Connecting to "+target.Name+"…";
        scanCancellation?.Cancel();sendCancellation=new();var token=sendCancellation.Token;RefreshSend();
        var progress=new Progress<SendProgress>(p=>
        {
            if(!sending||token.IsCancellationRequested||sendCancellation?.Token!=token)return;
            sendPercent=p.Total>0?Math.Clamp(p.Sent*100d/p.Total,0,100):sendPercent;
            sendStatus=p.Status+(p.Pin is null?"":" · Code "+p.Pin)+(p.Total>0?$" · {FormatSize(p.Sent)} / {FormatSize(p.Total)}":"");RefreshSend();
        });
        sendTask=Run();await sendTask;
        async Task Run()
        {
            try
            {
                if(scanTask is not null)await scanTask;
                token.ThrowIfCancellationRequested();
                foreach(var file in files)file.ValidateUnchanged();
                string? wifiWarning=null;
                if(target.Route=="Bluetooth")await BluetoothSenderService.SendAsync(target,files,progress,token);
                else wifiWarning=await QuickShareSenderService.SendAsync(target,settings.DeviceName,files,progress,()=>!busy&&!quickShare.HasActiveTransfers&&!bluetooth.HasActiveTransfers,token);
                sendPercent=100;sendStatus=$"Sent {files.Length} file{(files.Length==1?"":"s")} to {target.Name}."+(wifiWarning is null?"":" Wi-Fi: "+wifiWarning);notifications.Enqueue(sendStatus);
            }
            catch(Exception)when(token.IsCancellationRequested){sendStatus="Sending cancelled. Files already accepted by the phone may remain there.";}
            catch(Exception ex){sendStatus="Could not send: "+ex.Message;Log("Send: "+ex.Message);}
            finally{sending=false;sendCancellation.Dispose();sendCancellation=null;RefreshSend();}
        }
    }
    private void CancelSend_Click(object sender,RoutedEventArgs e)=>sendCancellation?.Cancel();
    private async Task StopSendingAsync()
    {
        scanCancellation?.Cancel();sendCancellation?.Cancel();
        if(scanTask is not null){try{await scanTask;}catch{}}
        if(sendTask is not null){try{await sendTask;}catch{}}
        // Folder preparation owns its temporary archive until its cancellation unwinds.
        while(preparing)await Task.Delay(50);
        Queue.Clear();scanCancellation?.Dispose();
    }
    public sealed record SendFileView(OutgoingFile File)
    {public string Name=>File.Name;public string Detail=>FormatSize(File.Length);public string Path=>File.Path;}
    public sealed class SendDeviceView
    {
        internal SendTarget Target{get;}
        internal SendDeviceView(SendTarget target){Target=target;}
        public string Name=>Target.Name;
        public string Detail=>Target.Detail;
        public string Route=>Target.Route;
        public PackIconKind Icon=>Target.Route=="Bluetooth"?PackIconKind.Bluetooth:PackIconKind.CellphoneWireless;
    }
}
