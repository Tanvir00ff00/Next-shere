using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using NextShare.Core;

namespace NextShare.App.Services;

internal static class QuickShareSenderService
{
    public static async Task<string?> SendAsync(SendTarget target,string sourceName,IReadOnlyList<OutgoingFile> files,IProgress<SendProgress> progress,Func<bool> canSwitchWifi,CancellationToken token)
    {
        foreach(var file in files)file.ValidateUnchanged();
        QuickShareBleClient? bridge=null;var wifi=new TemporaryWifiJoin();var host=new QuickShareHotspot();
        string request=Path.Combine(App.DataRoot,"SendRequests",Guid.NewGuid()+".json");
        try
        {
            if(target.Ble)bridge=await QuickShareBleClient.OpenAsync(target.Address,token);
            Directory.CreateDirectory(Path.GetDirectoryName(request)!);
            await File.WriteAllTextAsync(request,JsonSerializer.Serialize(new{address=bridge?.Address??target.Address,targetName=target.Name,sourceName,files=files.Select(f=>f.Path),ble=target.Ble}),new UTF8Encoding(false),token);
            var info=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"nextshare-quickshare.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardInput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,StandardInputEncoding=new UTF8Encoding(false)};
            info.ArgumentList.Add("--send-request");info.ArgumentList.Add(request);
            using var child=Process.Start(info)??throw new IOException("Quick Share sender could not start.");
            using var cancel=token.Register(()=>{try{if(!child.HasExited)child.Kill(true);}catch{}});
            var errorTask=ReadErrorsAsync(child.StandardError,token);bool done=false;
            try
            {
                while(await child.StandardOutput.ReadLineAsync(token) is {} line)
                {
                    if(line.Length>16384)throw new InvalidDataException("Quick Share event too large.");
                    using var doc=JsonDocument.Parse(line);var message=doc.RootElement;
                    switch(message.GetProperty("type").GetString())
                    {
                        case "wifi-host":
                            try
                            {
                                if(!canSwitchWifi())throw new IOException("Another transfer is receiving. Retry after it finishes.");
                                progress.Report(new(0,files.Sum(f=>f.Length),"Preparing a local Wi-Fi connection for the phone…"));
                                using var wait=CancellationTokenSource.CreateLinkedTokenSource(token);wait.CancelAfter(TimeSpan.FromSeconds(20));
                                var credentials=await host.EnsureStartedAsync(wait.Token);
                                await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{type="wifi-host-ready",success=true,ssid=credentials.Ssid,password=credentials.Password,gateway=credentials.Gateway,frequency=credentials.Frequency}));
                            }
                            catch(Exception e)when(!token.IsCancellationRequested){await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{type="wifi-host-ready",success=false,message=e.Message}));}
                            break;
                        case "send-done": done=message.GetProperty("state").GetString()=="Finished";break;
                        case "send-progress":
                            var state=message.GetProperty("state").GetString()??"Connecting";
                            string? pin=message.TryGetProperty("pin",out var code)&&code.ValueKind==JsonValueKind.String?code.GetString():null;
                            string status=state switch {"SendingFiles"=>"Sending via Quick Share…","SentIntroduction" or "WaitingForUserConsent"=>"Accept this transfer on the phone…","Finished"=>"Phone confirmed completion.","Rejected"=>"The phone declined this transfer.","Disconnected"=>"Phone disconnected.",_=>"Connecting via Quick Share…"};
                            progress.Report(new(Number("sent"),Number("total"),status,pin));
                            break;
                        case "wifi-join":
                            try
                            {
                                if(!canSwitchWifi())throw new IOException("Another transfer is receiving. Retry sending after it finishes.");
                                progress.Report(new(0,files.Sum(f=>f.Length),"Connecting to the phone's local Wi-Fi…"));
                                using var wait=CancellationTokenSource.CreateLinkedTokenSource(token);wait.CancelAfter(TimeSpan.FromSeconds(28));
                                await wifi.ConnectAsync(message.GetProperty("ssid").GetString()!,message.GetProperty("password").GetString()!,wait.Token);
                                await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{type="wifi-ready",success=true}));
                            }
                            catch(Exception e)when(!token.IsCancellationRequested){await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{type="wifi-ready",success=false,message=e.Message}));}
                            break;
                    }
                    long Number(string key)=>message.TryGetProperty(key,out var n)&&n.ValueKind==JsonValueKind.Number&&n.TryGetInt64(out var value)?value:0;
                }
                await child.WaitForExitAsync(token);string error=await errorTask;
                if(child.ExitCode!=0 || !done)throw new IOException(string.IsNullOrWhiteSpace(error)?"Quick Share did not confirm completion.":error);
                foreach(var file in files)file.ValidateUnchanged();
            }
            finally {try{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}catch{}try{await errorTask;}catch{}}
        }
        finally
        {
            await host.StopAsync();await wifi.DisposeAsync();if(bridge is not null)await bridge.DisposeAsync();
            try{File.Delete(request);}catch{}
        }
        return wifi.RestoreWarning;
    }
    private static async Task<string> ReadErrorsAsync(StreamReader reader,CancellationToken token)
    {
        string result="";
        while(await reader.ReadLineAsync(token) is {} line){result+=line+"\n";if(result.Length>4000)result=result[^4000..];}
        return result.Trim();
    }
}
