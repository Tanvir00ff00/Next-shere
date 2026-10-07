using System.IO.Pipes;
using System.Text.Json;
using NextShare.Platform;
var results=new List<object>();int failed=0;
async Task Test(string name,Func<Task> test){try{await test();results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;results.Add(new{name,passed=false,error=ex.ToString()});Console.WriteLine("FAIL "+name+": "+ex.Message);}}
void Check(bool value,string why){if(!value)throw new Exception(why);}
async Task WithServer(Func<bool> state,Func<string,Task> action)
{
 string name="NextShare.Guard.Test."+Guid.NewGuid().ToString("N");using var server=new OfflineGuardServer(name,state);using var stop=new CancellationTokenSource();var run=server.RunAsync(stop.Token);
 try{await action(name);}finally{stop.Cancel();await run;}
}
await Test("Live Sharing state is queried for every request; enabled sharing fails closed",async()=>
{
 bool disabled=true;int calls=0;
 await WithServer(()=>{calls++;return disabled;},async name=>
 {
  await OfflineGuardClient.VerifyCoreAsync(name,pid=>pid==Environment.ProcessId,CancellationToken.None);disabled=false;
  bool blocked=false;try{await OfflineGuardClient.VerifyCoreAsync(name,pid=>pid==Environment.ProcessId,CancellationToken.None);}catch(IOException e){blocked=e.Message.Contains("is enabled");}
  Check(blocked&&calls==2,"Sharing was cached or allowed");
 });
});
await Test("Identity check rejects an untrusted local pipe before privileged check",async()=>
{
 int calls=0;await WithServer(()=>{calls++;return true;},async name=>
 {
  bool blocked=false;try{await OfflineGuardClient.VerifyCoreAsync(name,_=>false,CancellationToken.None);}catch(IOException e){blocked=e.Message.Contains("identity");}
  await Task.Delay(80);Check(blocked&&calls==0,"Spoofed server accepted");
 });
});
await Test("Unsupported commands and failed COM checks never report disabled sharing",async()=>
{
 int calls=0;await WithServer(()=>{calls++;throw new UnauthorizedAccessException();},async name=>
 {
  using(var pipe=await OfflineGuardClient.ConnectAsync(name,CancellationToken.None)){await pipe.WriteAsync(new byte[]{255});byte[] answer=new byte[1];await pipe.ReadExactlyAsync(answer);Check(answer[0]==0x13&&calls==0,"Unsupported operation executed");}
  bool blocked=false;try{await OfflineGuardClient.VerifyCoreAsync(name,_=>true,CancellationToken.None);}catch(IOException e){blocked=e.Message.Contains("cannot verify");}
  Check(blocked&&calls==1,"Permission error allowed offline mode");
 });
});
await Test("Four simultaneous local callers get independent guard replies",async()=>
{
 int calls=0;await WithServer(()=>{Interlocked.Increment(ref calls);return true;},async name=>
 {await Task.WhenAll(Enumerable.Range(0,4).Select(_=>OfflineGuardClient.VerifyCoreAsync(name,pid=>pid==Environment.ProcessId,CancellationToken.None)));Check(calls==4,"Caller lost");});
});
await Test("Missing guard honours caller cancellation",async()=>
{
 using var stop=new CancellationTokenSource(150);bool cancelled=false;
 try{await OfflineGuardClient.VerifyCoreAsync("NextShare.Missing."+Guid.NewGuid(),_=>true,stop.Token);}catch(OperationCanceledException){cancelled=true;}
 Check(cancelled,"Caller cancellation ignored");
});
await Test("Pipe cannot be replaced by a second server instance",async()=>
{
 await WithServer(()=>true,name=>{bool blocked=false;try{using var other=new OfflineGuardServer(name,()=>true);}catch(System.ComponentModel.Win32Exception){blocked=true;}Check(blocked,"Second server bound same name");return Task.CompletedTask;});
});
await Test("Idle clients time out without blocking subsequent requests",async()=>
{
 await WithServer(()=>true,async name=>
 {
  using var idle=await OfflineGuardClient.ConnectAsync(name,CancellationToken.None);
  var watch=System.Diagnostics.Stopwatch.StartNew();
  await OfflineGuardClient.VerifyCoreAsync(name,pid=>pid==Environment.ProcessId,CancellationToken.None);
  Check(watch.ElapsedMilliseconds<4500,"Idle client monopolized service");
 });
});
var report=JsonSerializer.Serialize(new{passed=results.Count-failed,failed,ElevatedServiceTested=false,results},new JsonSerializerOptions{WriteIndented=true});
Directory.CreateDirectory("artifacts");await File.WriteAllTextAsync("artifacts/windows-guard-tests-042.json",report);Console.WriteLine($"{results.Count-failed}/{results.Count} passed");return failed==0?0:1;
