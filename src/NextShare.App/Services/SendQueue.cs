using System.IO;
using System.IO.Compression;
using System.Text;
using NextShare.Core;

namespace NextShare.App.Services;

internal sealed class SendQueue(string root)
{
    private readonly HashSet<string> owned=new(StringComparer.OrdinalIgnoreCase);
    public async Task<OutgoingFile> TextAsync(string text,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(text))throw new IOException("Enter text to send.");
        if(Encoding.UTF8.GetByteCount(text)>8*1024*1024)throw new IOException("Text is too large. Select a file instead.");
        var path=CreatePath("Text-"+DateTime.Now.ToString("HHmmss")+".txt");
        try{await File.WriteAllTextAsync(path,text,new UTF8Encoding(false),token);return OutgoingFile.Inspect(path);}catch{Release(path);throw;}
    }
    public string CreatePath(string name)
    {
        InboxStore.ValidateName(name);var folder=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,name);owned.Add(path);return path;
    }
    public async Task<OutgoingFile> FolderAsync(string source,CancellationToken token)
    {
        var directory=new DirectoryInfo(source);
        if(!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("Choose a regular folder.");
        var path=CreatePath((string.IsNullOrEmpty(directory.Name)?"Folder":directory.Name)+".zip");
        try
        {
            await Task.Run(async()=>
            {
                using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true);
                using var zip=new ZipArchive(stream,ZipArchiveMode.Create);
                var pending=new Stack<DirectoryInfo>();pending.Push(directory);int entries=0;
                while(pending.TryPop(out var folder))
                {
                    token.ThrowIfCancellationRequested();
                    foreach(var item in folder.EnumerateFileSystemInfos())
                    {
                        token.ThrowIfCancellationRequested();if(++entries>10000)throw new IOException("Folder has too many entries. Select smaller folders.");
                        if(item.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("Folder contains a link or junction: "+item.Name);
                        var name=Path.GetRelativePath(directory.FullName,item.FullName).Replace('\\','/');
                        if(item is DirectoryInfo child){zip.CreateEntry(name+"/");pending.Push(child);continue;}
                        var file=OutgoingFile.Inspect(item.FullName);
                        var entry=zip.CreateEntry(name,CompressionLevel.Fastest);
                        await using var input=new FileStream(file.Path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true);
                        await using(var output=entry.Open())await input.CopyToAsync(output,token);
                        file.ValidateUnchanged();
                    }
                }
            },token);
            return OutgoingFile.Inspect(path);
        }
        catch{Release(path);throw;}
    }
    public void Release(string path)
    {
        if(!owned.Remove(path))return;
        try{File.Delete(path);Directory.Delete(Path.GetDirectoryName(path)!);}catch{}
    }
    public void Clear(){foreach(var path in owned.ToArray())Release(path);}
}
