using System.ServiceProcess;
using NextShare.Platform;
if(args.Contains("--probe"))
{
    try{await OfflineGuardClient.VerifyAsync(CancellationToken.None);Console.WriteLine("Internet Sharing disabled; trusted installed offline guard responded.");return 0;}
    catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
}
ServiceBase.Run(new GuardService());return 0;
sealed class GuardService:ServiceBase
{
    private readonly CancellationTokenSource stop=new();
    private OfflineGuardServer? server;
    private Task? worker;
    public GuardService(){ServiceName=OfflineGuardClient.ServiceName;CanStop=true;CanShutdown=true;AutoLog=true;}
    protected override void OnStart(string[] args)
    {
        server=new OfflineGuardServer();worker=Task.Run(()=>server.RunAsync(stop.Token));
        _=worker.ContinueWith(t=>{if(!stop.IsCancellationRequested)Environment.Exit(1);},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted,TaskScheduler.Default);
    }
    protected override void OnStop(){stop.Cancel();try{worker?.Wait(TimeSpan.FromSeconds(3));}catch{}server?.Dispose();}
    protected override void OnShutdown()=>OnStop();
}
