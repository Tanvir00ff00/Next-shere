namespace NextShare.Core;

public sealed record OutgoingFile(string Path, string Name, long Length, DateTime LastWriteUtc)
{
    public static OutgoingFile Inspect(string path)
    {
        var file = new FileInfo(System.IO.Path.GetFullPath(path));
        if (!file.Exists) throw new FileNotFoundException("File is no longer available", path);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Select the original file instead of a shortcut or link.");
        InboxStore.ValidateName(file.Name);
        return new(file.FullName, file.Name, file.Length, file.LastWriteTimeUtc);
    }
    public void ValidateUnchanged()
    {
        var current = Inspect(Path);
        if (current.Length != Length || current.LastWriteUtc != LastWriteUtc) throw new IOException("File changed after selection: " + Name);
    }
}
