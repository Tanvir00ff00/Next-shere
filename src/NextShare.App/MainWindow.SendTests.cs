using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NextShare.Core;
using NextShare.App.Services;

namespace NextShare.App;

public partial class MainWindow
{
    private async Task<object> ExerciseSendUiAsync()
    {
        if(!smoke)throw new InvalidOperationException("Isolated smoke only");
        var theme=settings.Theme;var root=Path.Combine(App.DataRoot,"SendFixtures");Directory.CreateDirectory(root);
        string path=Path.Combine(root,"ছবি.bin"),folder=Path.Combine(root,"Documents");Directory.CreateDirectory(Path.Combine(folder,"nested"));
        var bytes=RandomNumberGenerator.GetBytes(183777);await File.WriteAllBytesAsync(path,bytes);await File.WriteAllTextAsync(Path.Combine(folder,"nested","বাংলা.txt"),"Original folder content");
        var drop=new DataObject();drop.SetData(DataFormats.FileDrop,new[]{path,folder});await AddDropAsync(drop);
        await AddDropAsync(drop); // Existing plain-file selection is deduplicated; each folder archive is a fresh snapshot.
        var text=new DataObject();text.SetData(DataFormats.UnicodeText,"বাংলা text to send\noriginal Unicode");await AddDropAsync(text);
        if(Selection.Count!=4||!CanSend||page!="Send")throw new IOException("Selection/drop/text integration failed");
        AddImage(BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,new byte[]{0,0,255,255,0,255,0,255,255,0,0,255,0,0,0,255},8));
        var pasted=new BitmapImage();pasted.BeginInit();pasted.CacheOption=BitmapCacheOption.OnLoad;pasted.UriSource=new Uri(Selection.Last().Path);pasted.EndInit();
        if(pasted.PixelWidth!=2||pasted.PixelHeight!=2)throw new IOException("Pasted image was resized");
        var zips=Selection.Where(f=>f.Name.EndsWith(".zip")).ToArray();
        foreach(var zip in zips)
        {using var archive=ZipFile.OpenRead(zip.Path);using var reader=new StreamReader(archive.GetEntry("nested/বাংলা.txt")!.Open());if(await reader.ReadToEndAsync()!="Original folder content")throw new IOException("Folder snapshot lost nested bytes");}
        var generated=Selection.Where(f=>f.Path!=path).Select(f=>f.Path).ToArray();
        NearbyDevices.Add(new(new("fixture-a","Software receiver","Quick Share","127.0.0.1:"+quickShare.Port)));
        NearbyDevices.Add(new(new("fixture-b","Bluetooth test fixture","Bluetooth","fixture",false,false)));
        discoveryStatus="Smoke fixtures — no nearby phone was used.";RefreshSend();
        var sizes=new List<object>();
        foreach(var size in new[]{(830,560),(640,440),(1100,740)})
        {
            Width=size.Item1;Height=size.Item2;ThemeMode="Light";await Task.Delay(100);UpdateLayout();
            if(SendPage.ActualWidth<400||SendPage.ActualHeight<280||SendPage.HorizontalOffset!=0||DropZone.ActualWidth>SendPage.ActualWidth)throw new IOException("Send viewport does not fit");
            Capture(Path.Combine(App.DataRoot,$"send-{size.Item1}x{size.Item2}.png"));sizes.Add(new{Width,Height,ViewportWidth=SendPage.ActualWidth,ViewportHeight=SendPage.ActualHeight,ScrollHeight=SendPage.ScrollableHeight});
        }
        Width=830;Height=560;ThemeMode="Dark";await Task.Delay(80);Capture(Path.Combine(App.DataRoot,"send-dark.png"));
        bool sent=false,cancelled=false;
        if(quickShare.Running)
        {
            int initial=store.Load().Count;var originals=Selection.Select(f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.Path)))).Order().ToArray();
            await SendSelectedAsync(NearbyDevices[0].Target);
            var until=DateTime.UtcNow.AddSeconds(15);while(store.Load().Count<initial+5&&DateTime.UtcNow<until)await Task.Delay(100);
            var received=store.Load().OrderBy(r=>r.ReceivedAt).Skip(initial).ToArray();
            if(!sendStatus.StartsWith("Sent ")||received.Length!=5||!originals.SequenceEqual(received.Select(r=>r.Sha256).Order()))throw new IOException("Production Send flow did not deliver original files: "+sendStatus);
            sent=true;
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            using var stop=new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                var accept=listener.AcceptTcpClientAsync();
                var transfer=QuickShareSenderService.SendAsync(new("silent","Silent software peer","Quick Share",listener.LocalEndpoint.ToString()!),"Test sender",[OutgoingFile.Inspect(path)],new Progress<SendProgress>(_=>{}),()=>false,stop.Token);
                using var peer=await accept.WaitAsync(TimeSpan.FromSeconds(3));
                try{await transfer.WaitAsync(TimeSpan.FromSeconds(8));}catch(OperationCanceledException){cancelled=true;}
                if(!cancelled)throw new IOException("Quick Share sender did not honour cancellation");
            }
            finally{listener.Stop();}
        }
        ClearSelection_Click(this,new RoutedEventArgs());
        if(generated.Any(File.Exists)||!File.Exists(path)||!File.Exists(Path.Combine(folder,"nested","বাংলা.txt")))throw new IOException("Selection cleanup touched original files or leaked temporary files");
        NearbyDevices.Clear();ThemeMode=theme;ReceiveNav.IsChecked=true;RefreshSend();
        return new{Passed=true,FileFolderTextDropVerified=true,PasteImageEncodingKeepsDimensions=true,NestedZipVerified=true,ClearPreservesOriginals=true,ProductionQuickShareSenderVerified=sent,QuickShareCancelVerified=cancelled,RadioUsed=false,PhoneUsed=false,ResizeCases=sizes,ClipboardModified=false};
    }
}
