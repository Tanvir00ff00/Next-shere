using System.Windows;
using System.Windows.Threading;
using NextShare.Core;
namespace NextShare.App.Services;

internal sealed class ReceiveFeedback : IDisposable
{
    private sealed class Arrival(string name, string sender)
    {
        public string Name { get; } = name;
        public string Sender { get; } = sender;
        public Dictionary<string, long> Saved { get; } = [];
    }
    private readonly Window host;
    private readonly AppSettings settings;
    private readonly Action<string> log;
    private readonly bool muted;
    private readonly Dictionary<string, Arrival> active = [];
    private readonly HashSet<string> cardSessions = [];
    private readonly DispatcherTimer soundTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private SignatureChime? chime;
    private ReceiveNotification? card, completedCard;
    private long lastSoundTick = -10000;
    private bool disposed;
    public long LastDismissMilliseconds { get; private set; }
    public int PresentedFiles { get; private set; }
    public int CompletedFiles { get; private set; }
    public int SoundRequests { get; private set; }
    public ReceiveNotification? VisibleCard => card ?? completedCard;
    public ReceiveNotification? CompletionCard => completedCard;
    public bool SoundLoaded => chime is not null;
    public SoundPlaybackReport? LastPlayback=>chime?.LastPlayback;
    public event Action<string>? SoundStatusChanged;
    public ReceiveFeedback(Window host, AppSettings settings, Action<string> log, bool muted)
    {
        this.host=host; this.settings=settings; this.log=log; this.muted=muted;
        try { chime=new SignatureChime(()=>settings.SoundOutputDeviceId); }
        catch(Exception ex) { log("Receive sound: "+ex.Message); }
        soundTimer.Tick += (_,_) =>
        {
            if (!settings.ReceiveSound || disposed) { soundTimer.Stop(); return; }
            if (chime?.IsPlaying == true || Environment.TickCount64-lastSoundTick < 1400) return;
            soundTimer.Stop(); PlaySound();
        };
    }
    // Card expiry never forgets a transfer: arrival and durable completion are independent.
    public void Started(string key, string name, string sender, long? size, int count=1)
    {
        if (!active.TryAdd(key,new Arrival(name,sender))) return;
        Present(key,name,sender,size,count,false,false);
    }
    public void Saved(string key, TransferRecord record)
    {
        if (active.TryGetValue(key,out var arrival)) arrival.Saved.TryAdd(record.Id,record.Size);
    }
    public void Finished(string key, bool saved)
    {
        if (!active.Remove(key,out var arrival)) return;
        if (cardSessions.Remove(key) && !saved) card?.Interrupted();
        if (!saved || arrival.Saved.Count==0) return;
        Present(key,arrival.Name,arrival.Sender,arrival.Saved.Values.Sum(),arrival.Saved.Count,true,false);
    }
    private void Present(string key,string name,string sender,long? size,int count,bool completed,bool preview)
    {
        if (disposed) return;
        if(settings.DesktopNotifications)
        {
            var target = completed ? completedCard : card;
            if(target is null)
            {
                target=new ReceiveNotification(host,completed); var shown=target;
                if(completed) completedCard=shown; else { card=shown;cardSessions.Clear(); }
                shown.Closed+=(_,_)=>
                {
                    LastDismissMilliseconds=shown.MillisecondsSinceUpdate;
                    if(card==shown){card=null;cardSessions.Clear();completedCard?.SetSlot(0);}
                    if(completedCard==shown)completedCard=null;
                };
                target.Add(name,sender,size,count,preview);
                if(completed)target.SetSlot(card is null?0:1);
                else completedCard?.SetSlot(1);
                target.Show();
            }
            else target.Add(name,sender,size,count,preview);
            if(completed)CompletedFiles+=count;else {PresentedFiles+=count;cardSessions.Add(key);}
        }
        if (!settings.ReceiveSound || chime is null) return;
        if (completed)
        {
            // Coalesce a completion burst, and wait for the arrival sound to finish.
            if(chime.IsPlaying || Environment.TickCount64-lastSoundTick<1400)soundTimer.Start();
            else {soundTimer.Stop();PlaySound();}
        }
        else if (preview || (!soundTimer.IsEnabled && !chime.IsPlaying && Environment.TickCount64-lastSoundTick>=800))PlaySound();
    }
    private void PlaySound()
    {
        lastSoundTick=Environment.TickCount64;SoundRequests++;
        if(!muted)chime?.Play(status=>host.Dispatcher.InvokeAsync(()=>{log("Receive sound: "+status);SoundStatusChanged?.Invoke(status);}));
    }
    public void Preview(){Dismiss();Present("preview","Notification preview","Next Share signature sound",null,1,false,true);}
    public void Dismiss(){card?.Close();completedCard?.Close();soundTimer.Stop();}
    public void Dispose(){disposed=true;Dismiss();active.Clear();chime?.Dispose();}
}
