using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace NextShare.Installer;

internal sealed class EngineProgress
{
 public int Stage {get;private set;}
 public string? Error {get;private set;}
 public bool Read(string line)
 {
  if(line.StartsWith("ERROR|",StringComparison.Ordinal)) {Error=line[6..];return true;}
  if(Error is not null){if(!string.IsNullOrWhiteSpace(line))Error+="\n"+line;return false;}
  if(Error is not null || !int.TryParse(line,out int stage) || stage is not (10 or 30 or 60 or 85 or 100) || stage<=Stage)return false;
  Stage=stage;return true;
 }
 public bool Succeeded(int exitCode)=>exitCode==0&&Stage==100&&Error is null;
}

internal static class InstallationEngine
{
 internal static bool VerifyPayload(Stream engine,Stream hash)
 {
  using var reader=new StreamReader(hash);
  string expected=reader.ReadToEnd().Trim();
  string actual=Convert.ToHexString(SHA256.HashData(engine));
  return string.Equals(expected,actual,StringComparison.OrdinalIgnoreCase);
 }
 internal static void Install(Action<int> progress)
 {
  if(!Environment.Is64BitOperatingSystem||!OperatingSystem.IsWindowsVersionAtLeast(10,0,19041))
   throw new InvalidOperationException("Next Share requires 64-bit Windows 10 version 2004 or Windows 11.");
  using var identity=WindowsIdentity.GetCurrent();
  if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
   throw new InvalidOperationException("Run this installer normally and accept Windows Administrator permission.");
  using var gate=new Mutex(false,@"Global\NextShare.Setup.Installation");
  bool owned;
  try {owned=gate.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
  if(!owned)throw new InvalidOperationException("Another Next Share installation is running. Finish it before trying again.");
  string? folder=null;
  try
  {
   var assembly=typeof(InstallationEngine).Assembly;
   using var payload=assembly.GetManifestResourceStream("NextShare.Setup.Engine")??throw new IOException("Installation payload is missing.");
   using var expected=assembly.GetManifestResourceStream("NextShare.Setup.EngineHash")??throw new IOException("Installation verification data is missing.");
   if(!VerifyPayload(payload,expected))throw new IOException("Installation payload verification failed. Build or download a fresh installer.");
   payload.Position=0;
   // Create the private elevated working directory before writing executable bytes.
   // Administrators own it; a standard-user token cannot replace the engine/status log.
   string systemTemp=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Temp");
   if((File.GetAttributes(systemTemp)&FileAttributes.ReparsePoint)!=0)throw new IOException("Unexpected Windows temporary directory.");
   folder=Path.Combine(systemTemp,"NextShare-Setup-"+Guid.NewGuid().ToString("N"));
   var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
   var administrators=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
   acl.SetOwner(administrators);
   foreach(var sid in new[]{administrators,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
    acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
   acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
   new DirectoryInfo(folder).Create(acl);
   string executable=Path.Combine(folder,"engine.exe"),statusFile=Path.Combine(folder,"setup-status.log");
   using(var output=new FileStream(executable,FileMode.CreateNew,FileAccess.Write,FileShare.None))payload.CopyTo(output);
   using(var engine=File.OpenRead(executable))
   using(var hash=assembly.GetManifestResourceStream("NextShare.Setup.EngineHash")!)
    if(!VerifyPayload(engine,hash))throw new IOException("Extracted installation payload verification failed.");
   var state=new EngineProgress();
   using var process=Process.Start(new ProcessStartInfo(executable){Arguments="/S",UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=folder})??throw new IOException("Could not start installation.");
   void Poll()
   {
    if(!File.Exists(statusFile))return;
    try
    {
     using var stream=new FileStream(statusFile,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
     using var reader=new StreamReader(stream);
     while(reader.ReadLine() is {} line)if(state.Read(line)&&state.Error is null)progress(state.Stage);
    }
    catch(IOException){} // A stage write can briefly overlap the read; read it on the next poll.
   }
   while(!process.WaitForExit(150))Poll();
   Poll();
   if(!state.Succeeded(process.ExitCode))
    throw new IOException((state.Error??$"Installation did not finish (code {process.ExitCode}).")+"\nInstaller log: "+statusFile);
   // Only known files inside this uniquely created directory are removed.
   try {File.Delete(executable);File.Delete(statusFile);Directory.Delete(folder);}
   catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){} // Cache cleanup cannot turn a completed install into failure.
  }
  finally {gate.ReleaseMutex();}
 }
}
