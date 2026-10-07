using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace NextShare.Installer;
public partial class SetupWindow : Window
{
 private readonly bool preview;
 private readonly string? smokeOutput;
 private bool busy,finished;
 private readonly List<int> shownStages=[];
 public SetupWindow(bool preview,string? smokeOutput)
 {
  this.preview=preview;this.smokeOutput=smokeOutput;
  InitializeComponent();
  InstallPath.Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"NextShare");
  VersionText.Text="WINDOWS · VERSION "+typeof(SetupWindow).Assembly.GetName().Version!.ToString(3);
  if(preview){Badge.Text="DESIGN PREVIEW";Footer.Text="Preview only. No Windows changes.";}
  Loaded+=async(_,_)=>
  {
   Animate();
   if(smokeOutput is not null)await SmokeAsync();
  };
  Closing+=OnClosing;
 }
 private void Animate()
 {
  if(!SystemParameters.ClientAreaAnimation)return;
  Rotation.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromSeconds(8)){RepeatBehavior=RepeatBehavior.Forever});
  foreach(var property in new[]{ScaleTransform.ScaleXProperty,ScaleTransform.ScaleYProperty})
   PulseScale.BeginAnimation(property,new DoubleAnimation(0.9,1.12,TimeSpan.FromSeconds(2.2)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever});
  Pulse.BeginAnimation(OpacityProperty,new DoubleAnimation(0.4,0.9,TimeSpan.FromSeconds(2.2)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever});
 }
 private void Swap(FrameworkElement panel)
 {
  WelcomePanel.Visibility=ProgressPanel.Visibility=ResultPanel.Visibility=Visibility.Collapsed;
  panel.Visibility=Visibility.Visible;
  if(SystemParameters.ClientAreaAnimation)
   panel.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(220)));
 }
 private void BeginInstallation()
 {
  busy=true;CloseButton.IsEnabled=false;ActionButton.IsEnabled=false;ActionButton.Content="Installing…";
  Badge.Text=preview?"PREVIEW · INSTALLING":"INSTALLING";
  Footer.Text=preview?"Preview only. No Windows changes.":"Preparing local sharing on this PC.";
  Swap(ProgressPanel);Stage(0);
  UpdateLayout();
  if(SystemParameters.ClientAreaAnimation)
   ProgressSweep.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(-104,InstallTrack.ActualWidth+104,TimeSpan.FromSeconds(1.8)){RepeatBehavior=RepeatBehavior.Forever});
 }
 private void Stage(int number)
 {
  shownStages.Add(number);
  StageText.Text=number switch {10=>"Checking your installation…",30=>"Copying Next Share…",60=>"Configuring local receiving…",85=>"Adding startup & shortcuts…",100=>"Installation complete",_=>"Verifying installation files…"};
  string[] steps=["Prepare installation","Copy application files","Configure local receiving","Add startup and shortcuts"];
  int[] stages=[10,30,60,85];
  StepsText.Text=string.Join("\n",steps.Select((step,i)=>(number>stages[i]?"✓   ":number==stages[i]?"●   ":"○   ")+step));
 }
 private void Result(bool success,string? error=null)
 {
  busy=false;finished=true;CloseButton.IsEnabled=true;ActionButton.IsEnabled=true;ActionButton.Content="Close";
  Badge.Text=preview?"PREVIEW · "+(success?"READY":"ERROR"):success?"READY TO SHARE":"NEEDS ATTENTION";
  ResultIcon.Text=success?"✓":"!";
  ResultIconBox.Background=new SolidColorBrush(success?Color.FromRgb(221,241,232):Color.FromRgb(255,229,218));
  ResultTitle.Text=success?"All set. Share away.":"Let's finish this setup.";
  ResultDetail.Text=success?"Next Share is installed. Your PC is ready for nearby files.":error;
  ResultNote.Text=success?"Receiving starts automatically when you sign in. Open Next Share from the desktop or Start menu.":"Exit Next Share from its tray menu, then run this installer again. Your received files and preferences are kept.";
  Footer.Text=preview?"Preview only. No Windows changes.":success?"Ready for the next file.":"Setup did not report success.";
  Swap(ResultPanel);
 }
 private async void Action_Click(object sender,RoutedEventArgs e)
 {
  if(finished){Close();return;}
  if(busy)return;
  BeginInstallation();
  try
  {
   if(preview)
    foreach(int number in new[]{10,30,60,85,100}){await Task.Delay(650);Stage(number);}
   else await Task.Run(()=>InstallationEngine.Install(number=>Dispatcher.Invoke(()=>Stage(number))));
   Result(true);
  }
  catch(Exception ex){Result(false,ex.Message);}
 }
 private void Close_Click(object sender,RoutedEventArgs e)=>Close();
 private void CopyDetails_Click(object sender,RoutedEventArgs e){try{Clipboard.SetText(ResultDetail.Text);}catch(System.Runtime.InteropServices.COMException){}}
 private void OnClosing(object? sender,CancelEventArgs e)
 {
  if(!busy)return;
  e.Cancel=true;ProgressHint.Text="Finishing installation safely. Please keep this window open.";
 }
 private void DragWindow(object sender,MouseButtonEventArgs e){if(e.ClickCount==1)DragMove();}
 private void Capture(string name)
 {
  UpdateLayout();var bitmap=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(Shell);
  var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
  using var output=File.Create(Path.Combine(smokeOutput!,name+".png"));encoder.Save(output);
 }
 private async Task SmokeAsync()
 {
  Directory.CreateDirectory(smokeOutput!);
  try
  {
   await Task.Delay(300);Capture("installer-welcome");double angle=Rotation.Angle;
   await Task.Delay(350);bool animated=!SystemParameters.ClientAreaAnimation||Math.Abs(Rotation.Angle-angle)>1;
   BeginInstallation();bool locked=!ActionButton.IsEnabled&&!CloseButton.IsEnabled;
   var close=new CancelEventArgs();OnClosing(this,close);locked&=close.Cancel;
   foreach(int stage in new[]{10,30,60,85,100}){Stage(stage);await Task.Delay(350);Capture("installer-stage-"+stage);}
   Result(true);await Task.Delay(300);Capture("installer-ready");
   bool ready=finished&&!busy&&ActionButton.IsEnabled&&ResultPanel.IsVisible;
   string diagnostic="A running Next Share instance must be closed before upgrading.\n"+string.Join("\n",Enumerable.Repeat("Installer diagnostic: the application is still active. Exit it from the system tray before trying again.",8));
   Result(false,diagnostic);await Task.Delay(300);Capture("installer-error");
   bool failed=ResultTitle.Text.Contains("finish")&&ResultDetail.Text.Contains("closed")&&ActionButton.IsEnabled;
   UpdateLayout();var bounds=ResultPanel.TransformToAncestor(Body).TransformBounds(new Rect(ResultPanel.RenderSize));
   failed&=ResultDetail.Text==diagnostic&&bounds.Bottom<=Body.ActualHeight+1;
   var state=new EngineProgress();
   bool protocol=state.Read("10")&&!state.Read("bad")&&!state.Read("11")&&state.Read("60")&&!state.Read("30")&&!state.Succeeded(0)&&state.Read("100")&&state.Succeeded(0)&&!state.Succeeded(1)&&state.Read("ERROR|test failure")&&!state.Succeeded(0);
   state.Read("Exit the running application.");protocol&=state.Error!.Contains("Exit the running application.");
   bool payload=true;var assembly=typeof(SetupWindow).Assembly;
   using(var engine=assembly.GetManifestResourceStream("NextShare.Setup.Engine"))
   {if(engine is not null){using var hash=assembly.GetManifestResourceStream("NextShare.Setup.EngineHash")!;payload=InstallationEngine.VerifyPayload(engine,hash);}}
   using(var engine=new MemoryStream([1,2,3]))using(var hash=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(new string('0',64))))payload&=!InstallationEngine.VerifyPayload(engine,hash);
   bool passed=animated&&locked&&ready&&failed&&protocol&&payload;
   File.WriteAllText(Path.Combine(smokeOutput!,"installer-smoke.json"),JsonSerializer.Serialize(new{Passed=passed,AnimationVerified=animated,CloseBlockedDuringInstall=locked,SuccessView=ready,ErrorView=failed,EngineStageProtocol=protocol,PayloadHashVerified=payload,PreviewOnly=true,SystemChanges=false,Stages=shownStages},new JsonSerializerOptions{WriteIndented=true}));
   Application.Current.Shutdown(passed?0:1);
  }
  catch(Exception ex){File.WriteAllText(Path.Combine(smokeOutput!,"installer-error.txt"),ex.ToString());busy=false;Application.Current.Shutdown(1);}
 }
}
