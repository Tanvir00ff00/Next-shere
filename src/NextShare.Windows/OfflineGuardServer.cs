using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace NextShare.Platform;
public sealed class OfflineGuardServer : IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly Func<bool> check;
    public OfflineGuardServer():this(OfflineGuardClient.PipeName,InternetSharingCheck.IsDisabled){}
    internal OfflineGuardServer(string name,Func<bool> check)
    {
        this.check=check;
        // Local authenticated users may read/write messages, but may not create pipe instances.
        // Administrators/System control it; remote connections are rejected by the kernel.
        if(!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x12019b;;;AU)",1,out var descriptor,out _))throw new Win32Exception();
        try
        {
            var attrs=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Descriptor=descriptor};
            var handle=CreateNamedPipe(@"\\.\pipe\"+name,0x40080003,8,1,512,512,5000,ref attrs);
            if(handle.IsInvalid){int error=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error);}
            pipe=new NamedPipeServerStream(PipeDirection.InOut,true,false,handle);
        }
        finally{LocalFree(descriptor);}
    }
    public async Task RunAsync(CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(token);
                using var limit=CancellationTokenSource.CreateLinkedTokenSource(token);limit.CancelAfter(TimeSpan.FromSeconds(2));
                byte[] request=new byte[1];await pipe.ReadExactlyAsync(request,limit.Token);
                byte result=0x13;
                if(request[0]==1)
                {try{result=check()?(byte)0x10:(byte)0x11;}catch{result=0x12;}}
                await pipe.WriteAsync(new[]{result},limit.Token);
                await pipe.FlushAsync(limit.Token);
                // Keep the reply buffered until it is consumed; timeout bounds clients that never read.
                await pipe.ReadExactlyAsync(request,limit.Token);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            catch(Exception e) when(e is IOException or OperationCanceledException){ }
            finally{if(pipe.IsConnected)pipe.Disconnect();}
        }
    }
    public void Dispose()=>pipe.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes{public int Length;public IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor,uint revision,out IntPtr security,out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll",EntryPoint="CreateNamedPipeW",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafePipeHandle CreateNamedPipe(string name,uint mode,uint pipeMode,uint instances,uint output,uint input,uint timeout,ref SecurityAttributes attrs);
}
