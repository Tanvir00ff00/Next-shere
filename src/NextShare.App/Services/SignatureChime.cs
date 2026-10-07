using System.IO;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace NextShare.App.Services;

public sealed record SoundOutput(string? Id,string Name);
internal sealed record SoundPlaybackReport(string Backend,string Output,string OutputId,bool MediaOpened,bool MediaEnded,double DurationMilliseconds,long ElapsedMilliseconds,string? MixerWarning);

/// <summary>Own PCM chime through Windows Media Engine, with an explicit render device and full lifetime.</summary>
internal sealed class SignatureChime : IDisposable
{
    private readonly byte[] wave;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<string?> selectedOutput;
    private int playing;
    public bool IsPlaying => Volatile.Read(ref playing) != 0;
    public SoundPlaybackReport? LastPlayback {get;private set;}
    public SignatureChime(Func<string?> selectedOutput)
    {
        this.selectedOutput=selectedOutput;
        using var resource = typeof(SignatureChime).Assembly.GetManifestResourceStream("NextShare.ReceiveChime")
            ?? throw new IOException("Receive chime missing");
        using var buffer = new MemoryStream(); resource.CopyTo(buffer); wave = buffer.ToArray();
        using var reader = new WaveFileReader(new MemoryStream(wave, false));
        if (reader.Length == 0) throw new IOException("Receive chime is empty");
        if (reader.WaveFormat.Channels != 2) throw new IOException("Receive chime must use front-left/right stereo channels.");
    }

    public void Play(Action<string> status)
    {
        if (lifetime.IsCancellationRequested) return;
        if (Interlocked.CompareExchange(ref playing, 1, 0) != 0) { status("Signature sound is already playing."); return; }
        LastPlayback=null;
        string? outputId=selectedOutput();
        _ = Task.Run(async () =>
        {
            try
            {
                LastPlayback=await PlayMediaAsync(outputId,lifetime.Token);
                status(LastPlayback.MixerWarning ?? "Signature playback completed · "+LastPlayback.Output+" · Windows Media Engine");
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex) { status("Sound unavailable: " + ex.Message); }
            finally { Interlocked.Exchange(ref playing, 0); }
        });
    }

    public static async Task<SoundOutput[]> OutputsAsync()
    {
        var devices=await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
        return devices.Where(d=>d.IsEnabled).Select(d=>new SoundOutput(d.Id,d.Name)).ToArray();
    }
    private async Task<SoundPlaybackReport> PlayMediaAsync(string? selectedId,CancellationToken token)
    {
        var watch=Stopwatch.StartNew();
        string id=selectedId??MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
        if(string.IsNullOrEmpty(id))throw new IOException("No Windows audio output is available.");
        var info=await DeviceInformation.CreateFromIdAsync(id);
        if(info is null||!info.IsEnabled)throw new IOException("Selected sound output is unavailable. Choose another output in Settings.");
        using var devices=new MMDeviceEnumerator();
        using var endpoint=FindEndpoint(devices,info.Id);
        if(endpoint is not null&&(endpoint.AudioEndpointVolume.Mute||endpoint.AudioEndpointVolume.MasterVolumeLevelScalar==0))
            throw new IOException("Output is muted — check Windows volume for "+info.Name+".");
        using var stream=new InMemoryRandomAccessStream();
        using(var writer=new DataWriter(stream))
        {
            writer.WriteBytes(WithLeadIn(wave));await writer.StoreAsync();await writer.FlushAsync();writer.DetachStream();
        }
        stream.Seek(0);
        using var source=MediaSource.CreateFromStream(stream,"audio/wav");
        using var player=new Windows.Media.Playback.MediaPlayer
        {
            AudioDevice=info,AudioCategory=MediaPlayerAudioCategory.SoundEffects,
            AutoPlay=false,IsLoopingEnabled=false,IsMuted=false,Volume=1
        };
        var opened=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened+=(_,_)=>opened.TrySetResult();
        player.MediaEnded+=(_,_)=>ended.TrySetResult();
        player.MediaFailed+=(_,e)=>
        {
            var error=new IOException($"Windows Media Engine: {e.Error} · {e.ErrorMessage} · 0x{e.ExtendedErrorCode.HResult:X8}");
            opened.TrySetException(error);ended.TrySetException(error);
        };
        try
        {
            token.ThrowIfCancellationRequested();player.Source=source;player.Play();
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(5),token);
            double duration=player.PlaybackSession.NaturalDuration.TotalMilliseconds;
            if(duration<500)throw new IOException("Signature sound duration is invalid.");
            await Task.Delay(180,token);
            string? warning=null;
            if(endpoint is not null)
            {
                var sessions=endpoint.AudioSessionManager.Sessions;
                for(int i=0;i<sessions.Count;i++)
                {
                    using var session=sessions[i];
                    if(session.GetProcessID==Environment.ProcessId&&(session.SimpleAudioVolume.Mute||session.SimpleAudioVolume.Volume==0))
                        warning="Next Share is muted — check Windows Volume mixer.";
                }
            }
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(5),token);
            return new("Windows Media Engine",info.Name,info.Id,true,true,duration,watch.ElapsedMilliseconds,warning);
        }
        finally {player.Pause();player.Source=null;}
    }

    private static MMDevice? FindEndpoint(MMDeviceEnumerator devices,string id)
    {
        MMDevice? found=null;
        foreach(var device in devices.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active))
        {
            if(found is null&&id.Contains(device.ID,StringComparison.OrdinalIgnoreCase))found=device;
            else device.Dispose();
        }
        return found;
    }

    private static byte[] WithLeadIn(byte[] wav)
    {
        using var reader=new WaveFileReader(new MemoryStream(wav,false));
        using var buffer=new MemoryStream();
        using(var writer=new WaveFileWriter(buffer,reader.WaveFormat))
        {
            // Brief silence gives sleeping output devices time to wake before the signature notes.
            var silence=new byte[reader.WaveFormat.AverageBytesPerSecond/4];
            writer.Write(silence,0,silence.Length);reader.CopyTo(writer);writer.Write(silence,0,silence.Length);
            writer.Flush();
        }
        return buffer.ToArray();
    }
    public void Dispose() => lifetime.Cancel();
}
