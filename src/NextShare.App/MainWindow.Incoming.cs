using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using NextShare.Core;
using NextShare.App.Services;

namespace NextShare.App;
public partial class MainWindow
{
    public ObservableCollection<IncomingView> Incoming { get; }=[];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,long> receiveTicks=new();
    private int quickStarts,quickProgressEvents,quickProcessingEvents,quickSavedSessions;
    private void QueueBluetoothProgress(TransferProgress p)
    {
        string key="Bluetooth/"+p.SessionId+"/"+p.Name;long now=Environment.TickCount64;
        if(receiveTicks.TryGetValue(key,out long tick)&&now-tick<100&&p.Received!=p.Expected)return;
        receiveTicks[key]=now;Dispatcher.InvokeAsync(()=>UpdateIncoming("Bluetooth",p,false));
    }
    public Visibility IncomingVisibility=>Incoming.Count>0?Visibility.Visible:Visibility.Collapsed;
    public bool CanChangeReceiver=>!busy&&!sending&&!preparing&&Incoming.Count==0&&!bluetooth.HasActiveTransfers&&!quickShare.HasActiveTransfers&&!exiting;
    private string deviceNameDraft="";
    public string DeviceNameDraft { get=>deviceNameDraft; set { deviceNameDraft=value;Changed(); } }
    private static string IncomingKey(TransferOffer offer)=>offer.Route+"/"+offer.SessionId+(offer.Route=="Bluetooth"?"/"+offer.Name:"");
    private void BeginIncoming(TransferOffer offer,int count=1)
    {
        string key=IncomingKey(offer);
        if(Incoming.Any(x=>x.Key==key)||exiting)return;
        if(offer.Route=="Quick Share")quickStarts++;
        Incoming.Add(new IncomingView(key,offer,count));
        feedback?.Started(key,offer.Name,offer.Sender,offer.Size,count);
        RefreshIncoming();
    }
    private void UpdateIncoming(string route,TransferProgress p,bool processing)
    {
        var row=Incoming.FirstOrDefault(x=>x.Offer.Route==route&&x.Offer.SessionId==p.SessionId&&(route!="Bluetooth"||x.Offer.Name==p.Name));
        if(row is not null&&route=="Quick Share")quickProgressEvents++;
        row?.Update(p.Received,p.Expected,processing);Refresh();
    }
    private void ProcessIncoming(string route,string session)
    {if(route=="Quick Share")quickProcessingEvents++;foreach(var row in Incoming.Where(x=>x.Offer.Route==route&&x.Offer.SessionId==session))row.Update(row.Received,row.Expected,true);Refresh();}
    private void EndIncomingFile(TransferRecord record)
    {
        var row=Incoming.FirstOrDefault(x=>x.Offer.Route==record.Route&&x.Offer.SessionId==record.SessionId&&x.Offer.Name==record.Name);
        if(row is null)return;
        feedback.Finished(row.Key,true);Incoming.Remove(row);receiveTicks.TryRemove(row.Key,out _);RefreshIncoming();
    }
    private void EndIncoming(string route,string session,bool saved)
    {
        if(route=="Quick Share"&&saved&&Incoming.Any(x=>x.Offer.Route==route&&x.Offer.SessionId==session))quickSavedSessions++;
        foreach(var row in Incoming.Where(x=>x.Offer.Route==route&&x.Offer.SessionId==session).ToArray())
        {feedback.Finished(row.Key,saved);Incoming.Remove(row);receiveTicks.TryRemove(row.Key,out _);}
        RefreshIncoming();
    }
    private void RefreshIncoming(){Changed(nameof(IncomingVisibility));Refresh();Animate();}
    private async void SaveDeviceName_Click(object sender,RoutedEventArgs e)=>await ChangeDeviceNameAsync(DeviceNameDraft);
    private async void ResetDeviceName_Click(object sender,RoutedEventArgs e)=>await ChangeDeviceNameAsync(Environment.MachineName);
    private async Task ChangeDeviceNameAsync(string draft)
    {
        if(!CanChangeReceiver){notifications.Enqueue("Wait for active transfers to finish before changing the device name.");return;}
        string name=draft.Trim();
        if(string.IsNullOrEmpty(name)||name.Any(char.IsControl)||name.Length>32||Encoding.UTF8.GetByteCount(name)>80)
        {notifications.Enqueue("Use 1–32 characters, without control characters (up to 80 UTF-8 bytes).");return;}
        if(name==settings.DeviceName){DeviceNameDraft=name;return;}
        bool wasEnabled=enabled,wasQuickRunning=quickShare.Running;string? previous=settings.DeviceNameOverride;
        busy=true;Refresh();scanCancellation?.Cancel();
        try
        {
            if(scanTask is not null)try{await scanTask;}catch(OperationCanceledException){}
            await quickShare.StopAsync();await bluetooth.StopAsync();
            settings.DeviceNameOverride=name==Environment.MachineName?null:name;
            SettingsStore.Save(settings);
            if(wasEnabled)await StartAsync();
            if(wasQuickRunning&&!quickShare.Running)throw new InvalidOperationException(quickShare.Detail);
            DeviceNameDraft=settings.DeviceName;
            notifications.Enqueue("Device name updated.");
        }
        catch(Exception ex)
        {
            settings.DeviceNameOverride=previous;SavePreferences();DeviceNameDraft=settings.DeviceName;
            if(wasEnabled)await StartAsync();
            ShowError("Device name update failed: "+ex.Message);
        }
        finally{busy=false;Refresh();if(page=="Send")StartScan();}
    }
}
public sealed class IncomingView : INotifyPropertyChanged
{
    public string Key {get;}
    public TransferOffer Offer {get;}
    public string Name {get;}
    public string Device=>Offer.Sender+" · "+Offer.Route;
    public long Received {get;private set;}
    public long? Expected {get;private set;}
    public bool Processing {get;private set;}
    public double Percent=>Expected>0?Math.Clamp(Received*100.0/Expected.Value,0,100):0;
    public bool Indeterminate=>Processing||Expected is null||Expected==0;
    public string Detail=>Processing?"Saving to your folder…":MainWindow.FormatSize(Received)+(Expected.HasValue?" / "+MainWindow.FormatSize(Expected.Value)+$" · {Percent:0}%":" received");
    public event PropertyChangedEventHandler? PropertyChanged;
    public IncomingView(string key,TransferOffer offer,int count)
    {Key=key;Offer=offer;Name=count>1?$"{offer.Name} + {count-1} more":offer.Name;Expected=offer.Size;}
    public void Update(long bytes,long? expected,bool processing)
    {
        Received=Math.Max(Received,Math.Max(0,bytes));Expected=expected??Expected;Processing=processing;
        foreach(var p in new[]{nameof(Received),nameof(Expected),nameof(Processing),nameof(Percent),nameof(Indeterminate),nameof(Detail)})PropertyChanged?.Invoke(this,new(p));
    }
}
