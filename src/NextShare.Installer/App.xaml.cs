using System.Windows;
namespace NextShare.Installer;
public partial class App : Application
{
 protected override void OnStartup(StartupEventArgs e)
 {
  base.OnStartup(e);
  bool preview=e.Args.Contains("--preview");
  string? output=null;int at=Array.IndexOf(e.Args,"--smoke-output");
  if(at>=0&&at+1<e.Args.Length)output=e.Args[at+1];
  if(output is not null&&!preview){Shutdown(2);return;}
  new SetupWindow(preview,output).Show();
 }
}
