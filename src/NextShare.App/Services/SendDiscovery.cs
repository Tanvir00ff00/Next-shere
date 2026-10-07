using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using NextShare.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace NextShare.App.Services;

internal sealed record SendTarget(string Id, string Name, string Route, string Address, bool Ble = false, bool Paired = true)
{
    public string Detail => Route=="Bluetooth" ? Paired ? "Bluetooth · Paired device" : "Bluetooth · Pair to send" : Ble ? "Quick Share · Bluetooth discovery" : "Quick Share · Local Wi-Fi";
}
internal sealed class SendDiscovery
{
    public event Action<SendTarget>? Found;
    public event Action<string>? Removed;
    public event Action<string>? Notice;
    private static readonly Guid NearbyService=Guid.Parse("0000fef3-0000-1000-8000-00805f9b34fb");
    public async Task ScanAsync(bool radios, CancellationToken token)
    {
        var tasks=new List<Task>{LanAsync(token)};
        if(radios) { tasks.Add(ClassicAsync(token)); tasks.Add(BleAsync(token)); }
        await Task.WhenAll(tasks);
    }
    private async Task LanAsync(CancellationToken token)
    {
        var info=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"nextshare-quickshare.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add("--discover");info.ArgumentList.Add("--seconds");info.ArgumentList.Add("25");
        try
        {
            using var child=Process.Start(info)??throw new IOException("Discovery backend unavailable");
            using var cancel=token.Register(()=>{try{if(!child.HasExited)child.Kill(true);}catch{}});
            var errors=child.StandardError.ReadToEndAsync(token);
            try
            {
            while(await child.StandardOutput.ReadLineAsync(token) is {} line)
            {
                if(line.Length>8192)throw new InvalidDataException("Discovery event too large");
                using var doc=JsonDocument.Parse(line);var message=doc.RootElement;
                string id="lan:"+message.GetProperty("id").GetString();
                if(message.GetProperty("type").GetString()=="device-removed") Removed?.Invoke(id);
                else Found?.Invoke(new(id,message.GetProperty("name").GetString()!,"Quick Share",message.GetProperty("address").GetString()!));
            }
            await child.WaitForExitAsync(token);await errors;
            if(child.ExitCode!=0)Notice?.Invoke("Quick Share Wi-Fi discovery could not start.");
            }
            finally
            {
                try{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}catch{}
                try{await errors;}catch{}
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception e){Notice?.Invoke("Quick Share discovery: "+e.Message);}
    }
    private async Task ClassicAsync(CancellationToken token)
    {
        DeviceWatcher? watcher=null;
        try
        {
            watcher=DeviceInformation.CreateWatcher(BluetoothDevice.GetDeviceSelector(),["System.Devices.Aep.IsPaired"],DeviceInformationKind.AssociationEndpoint);
            watcher.Added+=(_,device)=>Found?.Invoke(new("bt:"+device.Id,string.IsNullOrWhiteSpace(device.Name)?"Bluetooth device":device.Name,"Bluetooth",device.Id,false,device.Pairing.IsPaired));
            watcher.Removed+=(_,device)=>Removed?.Invoke("bt:"+device.Id);
            watcher.Start();await Task.Delay(TimeSpan.FromSeconds(25),token);
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception e){Notice?.Invoke("Bluetooth discovery: "+e.Message);}
        finally{if(watcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)watcher.Stop();}
    }
    private async Task BleAsync(CancellationToken token)
    {
        var watcher=new BluetoothLEAdvertisementWatcher{ScanningMode=BluetoothLEScanningMode.Active};
        watcher.Stopped+=(_,args)=>{if(args.Error!=BluetoothError.Success&&!token.IsCancellationRequested)Notice?.Invoke("Quick Share Bluetooth discovery: "+args.Error);};
        var seen=new ConcurrentDictionary<ulong,long>();var jobs=new ConcurrentBag<Task>();using var slots=new SemaphoreSlim(3);
        watcher.Received+=(_,args)=>
        {
            if(token.IsCancellationRequested)return;
            var section=args.Advertisement.DataSections.FirstOrDefault(s=>s.DataType==0x16 && s.Data.Length>=3 && s.Data.ToArray()[0]==0xF3 && s.Data.ToArray()[1]==0xFE);
            if(section is null && !args.Advertisement.ServiceUuids.Contains(NearbyService))return;
            if(seen.Count>=64 || !seen.TryAdd(args.BluetoothAddress,Environment.TickCount64))return;
            jobs.Add(Task.Run(async()=>
            {
                await slots.WaitAsync(token);
                try
                {
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(14));var probe=deadline.Token;
                    var advert=section is null?null:QuickShareAdvertisement.Parse(section.Data.ToArray().AsSpan(2));
                    if(advert is null)
                    {
                        using var device=await BluetoothLEDevice.FromBluetoothAddressAsync(args.BluetoothAddress,args.BluetoothAddressType).AsTask(probe);
                        if(device is null)return;
                        var services=await device.GetGattServicesForUuidAsync(NearbyService,BluetoothCacheMode.Uncached).AsTask(probe);
                        try
                        {
                            if(services.Status!=GattCommunicationStatus.Success)return;
                            foreach(var service in services.Services)
                            {
                                var chars=await service.GetCharacteristicsForUuidAsync(Guid.Parse("00000000-0000-3000-8000-000000000000"),BluetoothCacheMode.Uncached).AsTask(probe);
                                if(chars.Status!=GattCommunicationStatus.Success)continue;
                                foreach(var characteristic in chars.Characteristics)
                                { var value=await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(probe);if(value.Status==GattCommunicationStatus.Success)advert=QuickShareAdvertisement.Parse(value.Value.ToArray());if(advert is not null)break; }
                                if(advert is not null)break;
                            }
                        }
                        finally{foreach(var service in services.Services)service.Dispose();}
                    }
                    if(advert is not null && !token.IsCancellationRequested)
                        Found?.Invoke(new("ble:"+advert.Endpoint,advert.Name,"Quick Share",args.BluetoothAddress+":"+(args.BluetoothAddressType==BluetoothAddressType.Random?"random":"public"),true));
                }
                catch(OperationCanceledException)when(token.IsCancellationRequested){}
                catch(Exception){/* Non-Nearby advertisements and unavailable GATT slots are skipped. */}
                finally{slots.Release();}
            },token));
        };
        try{watcher.Start();await Task.Delay(TimeSpan.FromSeconds(25),token);}
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception e){Notice?.Invoke("Quick Share Bluetooth discovery: "+e.Message);}
        finally{watcher.Stop();try{await Task.WhenAll(jobs);}catch(OperationCanceledException)when(token.IsCancellationRequested){}}
    }
}
