using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace NextShare.Platform;
public static class OfflineGuardClient
{
    public const string ServiceName="NextShareOfflineGuard";
    public const string PipeName="NextShare.OfflineGuard.v1";
    public static Task VerifyAsync(CancellationToken token)=>VerifyCoreAsync(PipeName,TrustedService,token);
    internal static async Task VerifyCoreAsync(string name,Func<uint,bool> trusted,CancellationToken token)
    {
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(token);limit.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var pipe=await ConnectAsync(name,limit.Token);
            if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out uint id)||!trusted(id))
                throw new IOException("Offline guard identity could not be verified. Repair Next Share installation.");
            await pipe.WriteAsync(new byte[]{1},limit.Token);
            byte[] response=new byte[1];await pipe.ReadExactlyAsync(response,limit.Token);
            await pipe.WriteAsync(new byte[]{6},limit.Token);
            switch(response[0])
            {
                case 0x10:return;
                case 0x11:throw new IOException("Windows Internet Sharing is enabled; offline hotspot was not started.");
                default:throw new IOException("Offline guard cannot verify Internet Sharing. Check or repair Next Share installation.");
            }
        }
        catch(OperationCanceledException) when(!token.IsCancellationRequested)
        {throw new IOException("Offline guard unavailable. Run the Next Share installer once as administrator; daily use requires no elevation.");}
    }
    internal static async Task<NamedPipeClientStream> ConnectAsync(string name,CancellationToken token)
    {
        while(true)
        {
            token.ThrowIfCancellationRequested();
            // WriteData rather than GENERIC_WRITE: users cannot create an instance of the service pipe.
            var handle=CreateFile(@"\\.\pipe\"+name,0x80000002,0,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
            if(!handle.IsInvalid)return new NamedPipeClientStream(PipeDirection.InOut,true,true,handle);
            int error=Marshal.GetLastWin32Error();handle.Dispose();
            if(error is not (2 or 231))throw new IOException("Cannot access offline guard: "+new Win32Exception(error).Message);
            await Task.Delay(50,token);
        }
    }
    private static bool TrustedService(uint pipePid)
    {
        using var manager=OpenSCManager(null,null,1);if(manager.IsInvalid)return false;
        using var service=OpenService(manager,ServiceName,4);if(service.IsInvalid)return false;
        return QueryServiceStatusEx(service,0,out var status,Marshal.SizeOf<ServiceStatus>(),out _)&&status.State==4&&status.ProcessId==pipePid&&pipePid!=0;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus{public uint Type,State,Accepted,Win32Exit,ServiceExit,Checkpoint,WaitHint,ProcessId,Flags;}
    private sealed class ServiceHandle:SafeHandleZeroOrMinusOneIsInvalid
    {public ServiceHandle():base(true){}protected override bool ReleaseHandle()=>CloseServiceHandle(handle);}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern ServiceHandle OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern ServiceHandle OpenService(ServiceHandle manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(ServiceHandle service,int level,out ServiceStatus status,int size,out int required);
    [DllImport("advapi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint id);
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafePipeHandle CreateFile(string name,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
}
