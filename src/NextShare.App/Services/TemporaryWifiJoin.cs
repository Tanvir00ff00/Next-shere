using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.WiFi;
using Windows.Security.Credentials;

namespace NextShare.App.Services;

internal sealed class TemporaryWifiJoin : IAsyncDisposable
{
    private WiFiAdapter? adapter;
    private string? previous;
    private string? temporary;
    public string? RestoreWarning {get;private set;}
    public async Task ConnectAsync(string ssid,string password,CancellationToken token)
    {
        if(ssid.Length is <1 or >32 || password.Length is <8 or >63)throw new IOException("Invalid phone Wi-Fi credentials.");
        if(adapter is not null)throw new IOException("A Wi-Fi upgrade is already running.");
        if(await WiFiAdapter.RequestAccessAsync().AsTask(token)!=WiFiAccessStatus.Allowed)
            throw new IOException("Windows denied Wi-Fi access. Check Wi-Fi and location permissions, or use both devices on the same local Wi-Fi.");
        var adapters=await WiFiAdapter.FindAllAdaptersAsync().AsTask(token);
        foreach(var candidate in adapters)
        {
            await candidate.ScanAsync().AsTask(token);
            var network=candidate.NetworkReport.AvailableNetworks.FirstOrDefault(n=>n.Ssid==ssid);
            if(network is null)continue;
            adapter=candidate;temporary=ssid;
            var profile=await adapter.NetworkAdapter.GetConnectedProfileAsync().AsTask(token);
            previous=profile?.IsWlanConnectionProfile==true?profile.WlanConnectionProfileDetails.GetConnectedSsid():null;
            // Manual connection only. No Internet Sharing, NAT or saved hotspot profile.
            var result=await adapter.ConnectAsync(network,WiFiReconnectionKind.Manual,new PasswordCredential{Password=password}).AsTask(token);
            if(result.ConnectionStatus!=WiFiConnectionStatus.Success)throw new IOException("Phone Wi-Fi connection: "+result.ConnectionStatus);
            return;
        }
        throw new IOException("Phone's local Wi-Fi network was not found.");
    }
    public async ValueTask DisposeAsync()
    {
        if(adapter is null)return;
        try
        {
            using var wait=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var current=await adapter.NetworkAdapter.GetConnectedProfileAsync().AsTask(wait.Token);
            // Do not undo a user's manual network change during a transfer.
            if(current?.IsWlanConnectionProfile==true && current.WlanConnectionProfileDetails.GetConnectedSsid()!=temporary)return;
            if(previous is null){adapter.Disconnect();return;}
            await adapter.ScanAsync().AsTask(wait.Token);
            var network=adapter.NetworkReport.AvailableNetworks.FirstOrDefault(n=>n.Ssid==previous);
            if(network is null)throw new IOException("Previous Wi-Fi network is out of range.");
            var result=await adapter.ConnectAsync(network,WiFiReconnectionKind.Manual).AsTask(wait.Token);
            if(result.ConnectionStatus!=WiFiConnectionStatus.Success)throw new IOException("Reconnect to your previous Wi-Fi in Windows.");
        }
        catch(Exception e){RestoreWarning=e.Message;}
    }
}
