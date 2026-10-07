using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

if (args.Length > 0 && args[0]=="--inventory")
{
    using var inventory=new MMDeviceEnumerator();
    var outputs=inventory.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.All).Select(d=>
    {
        using(d)
        {
            var sessions=new List<object>();
            if(d.State==DeviceState.Active)
            {var list=d.AudioSessionManager.Sessions;for(int i=0;i<list.Count;i++){using var s=list[i];sessions.Add(new{s.GetProcessID,s.DisplayName,s.State,Muted=s.SimpleAudioVolume.Mute,Volume=s.SimpleAudioVolume.Volume});}}
            return new{d.ID,d.FriendlyName,d.State,Muted=d.State==DeviceState.Active?d.AudioEndpointVolume.Mute:(bool?)null,Volume=d.State==DeviceState.Active?d.AudioEndpointVolume.MasterVolumeLevelScalar:(float?)null,
                ChannelVolumes=d.State==DeviceState.Active?Enumerable.Range(0,d.AudioEndpointVolume.Channels.Count).Select(i=>d.AudioEndpointVolume.Channels[i].VolumeLevelScalar).ToArray():null,
                MixFormat=d.State==DeviceState.Active?d.AudioClient.MixFormat.ToString():null,Sessions=sessions};
        }
    }).ToArray();
    var defaults=Enum.GetValues<Role>().Select(role=>{try{using var d=inventory.GetDefaultAudioEndpoint(DataFlow.Render,role);return new{Role=role.ToString(),d.ID,d.FriendlyName};}catch{return new{Role=role.ToString(),ID="",FriendlyName="Unavailable"};}}).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new{outputs,defaults},new JsonSerializerOptions{WriteIndented=true}));return;
}
if (args.Length < 2) throw new ArgumentException("AudioProbe <wav> <report.json>");
using var enumerator = new MMDeviceEnumerator();
using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
var device = new { endpoint.FriendlyName, endpoint.State, Muted = endpoint.AudioEndpointVolume.Mute, Volume = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar };
byte[] wave = await File.ReadAllBytesAsync(args[0]);
var sessions = Enumerable.Range(0, endpoint.AudioSessionManager.Sessions.Count).Select(i =>
{
    var session = endpoint.AudioSessionManager.Sessions[i];
    return new { session.GetProcessID, session.IsSystemSoundsSession, session.DisplayName,
        Muted = session.SimpleAudioVolume.Mute, Volume = session.SimpleAudioVolume.Volume, session.State };
}).ToArray();
async Task<object> Measure(string mode, Func<Task> play)
{
    using var capture = new WasapiLoopbackCapture(endpoint);
    double peak = 0, squares = 0; long samples = 0;
    int channelCount=capture.WaveFormat.Channels;
    var channelPeaks=new double[channelCount];var channelSquares=new double[channelCount];var channelSamples=new long[channelCount];
    var measurementGate = new object();
    capture.DataAvailable += (_, e) =>
    {
        if (capture.WaveFormat.BitsPerSample != 32) return;
        lock (measurementGate)
            for (int i = 0; i + 4 <= e.BytesRecorded; i += 4)
            { float value = BitConverter.ToSingle(e.Buffer, i); peak = Math.Max(peak, Math.Abs(value)); squares += value * value; samples++;
                int channel=(i/4)%channelCount;channelPeaks[channel]=Math.Max(channelPeaks[channel],Math.Abs(value));channelSquares[channel]+=value*value;channelSamples[channel]++; }
    };
    capture.StartRecording(); await Task.Delay(200);
    await play(); await Task.Delay(250); capture.StopRecording();
    await Task.Delay(150);
    lock (measurementGate) return new { mode, MixFormat=capture.WaveFormat.ToString(),Samples=samples, Peak=peak, Rms=samples>0?Math.Sqrt(squares/samples):0,
        Channels=Enumerable.Range(0,channelCount).Select(i=>new{Channel=i,Peak=channelPeaks[i],Rms=channelSamples[i]>0?Math.Sqrt(channelSquares[i]/channelSamples[i]):0}).ToArray() };
}
bool accepted=false;
if (args.Length > 2)
{
    var appPreview = await Measure("Packaged app Preview notification", async () =>
    {
        string root=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!,"preview-"+Guid.NewGuid().ToString("N"));
        var start=new ProcessStartInfo(args[2]) {UseShellExecute=false,CreateNoWindow=true};
        start.ArgumentList.Add("--audio-preview-only");start.ArgumentList.Add("--data-root");start.ArgumentList.Add(root);
        using var child=Process.Start(start)!;await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(18));
        Console.WriteLine(await File.ReadAllTextAsync(Path.Combine(root,"audio-preview.json")));
    });
    var previewReport = JsonSerializer.Serialize(new { device, sessions, appPreview }, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(args[1], previewReport); Console.WriteLine(previewReport);
    if(args.Contains("--verify-stereo"))
    {
        var channels=JsonSerializer.SerializeToElement(appPreview).GetProperty("Channels");
        if(channels.GetArrayLength()<2||channels[0].GetProperty("Rms").GetDouble()<0.02||channels[1].GetProperty("Rms").GetDouble()<0.02)
            throw new InvalidOperationException("Actual app Preview did not produce sufficient front-left/right output. Inspect channel metrics and output volume.");
        Console.WriteLine("PASS: actual app Preview reaches both front-left/right channels.");
    }
    return;
}
var soundPlayer = await Measure("Exact SoundPlayer async stream path", async () =>
{
    using var stream = new MemoryStream(wave, false);
    using var player = new SoundPlayer(stream);
    player.Load(); player.Play();
    await Task.Delay(1100);
});
var winmm = await Measure("Original PlaySound memory API", () =>
{
    accepted = PlaySound(wave, IntPtr.Zero, 0x0004 | 0x0002); // MEMORY | NODEFAULT | synchronous
    return Task.CompletedTask;
});
string? wasapiError=null;
var wasapi = await Measure("WASAPI shared", async () =>
{
    using var stream = new MemoryStream(wave, false); using var reader = new WaveFileReader(stream);
    using var output = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 80);
    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    output.PlaybackStopped += (_, e) => { wasapiError=e.Exception?.ToString(); stopped.TrySetResult(); };
    output.Init(reader); output.Play();
    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
});
var report = new { device, sessions, SoundPlayer=soundPlayer, WinmmAccepted=accepted, Winmm=winmm, Wasapi=wasapi, WasapiError=wasapiError, Capture="Output loopback amplitude metrics only; no audio or microphone recording stored" };
var json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);await File.WriteAllTextAsync(args[1],json);Console.WriteLine(json);

[DllImport("winmm.dll", EntryPoint="PlaySoundW", SetLastError=true)]
[return:MarshalAs(UnmanagedType.Bool)]
static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);
