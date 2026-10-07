using System.Collections.Concurrent;

namespace NextShare.Core;

// A staging-folder companion import, not a Quick Share protocol implementation.
// Automatic import is deliberately disabled: an unlocked/unchanged file does not prove native completion.
public sealed class FolderImporter(InboxStore store)
{
    private readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> importedKeys = new(StringComparer.Ordinal);
    private FileSystemWatcher? watcher;
    private string? folder;
    public event Action? CandidatesChanged;
    public string? Folder => folder;
    public IReadOnlyList<string> Candidates => pending.Keys.Where(File.Exists).OrderBy(x => x).ToArray();

    public void Watch(string path)
    {
        var nextFolder = Path.GetFullPath(path);
        if (nextFolder.StartsWith(store.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            store.Root.StartsWith(nextFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nextFolder, store.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The receive folder must be separate from the inbox.");
        Directory.CreateDirectory(nextFolder);
        Stop(); folder = nextFolder;
        importedKeys.Clear();
        foreach (var record in store.Load()) if (record.SourceKey is not null) importedKeys[record.SourceKey] = 0;
        watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Created += (_, e) => Add(e.FullPath);
        watcher.Changed += (_, e) => Add(e.FullPath);
        watcher.Renamed += (_, e) => { pending.TryRemove(e.OldFullPath, out var removed); Add(e.FullPath); };
        watcher.Deleted += (_, e) => { pending.TryRemove(e.FullPath, out var removed); CandidatesChanged?.Invoke(); };
        watcher.Error += (_, _) => Scan();
        watcher.EnableRaisingEvents = true;
        Scan();
    }

    private void Add(string path)
    {
        if (Path.GetFileName(path).StartsWith('.') || Path.GetExtension(path).ToLowerInvariant() is ".tmp" or ".partial" or ".part")
            return;
        if (!File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            if (importedKeys.ContainsKey(SourceKey(info, info.Length))) return;
            if (pending.TryAdd(path, 0)) CandidatesChanged?.Invoke();
        }
        catch (IOException) { }
    }
    public void Scan()
    {
        if (folder is null) return;
        foreach (var path in Directory.EnumerateFiles(folder)) Add(path);
    }
    public void Stop()
    {
        watcher?.Dispose(); watcher = null;
        pending.Clear(); folder = null;
    }

    // Operator invokes this after the native app says receiving is complete.
    public async Task<TransferRecord?> ImportCompletedAsync(string path, CancellationToken token)
    {
        var full = Path.GetFullPath(path);
        if (folder is null || !string.Equals(Path.GetDirectoryName(full), folder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("File is outside the selected receive folder.");
        var info = new FileInfo(full);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked files cannot be imported.");
        await using var source = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.None, 65536, true);
        var key = SourceKey(info, source.Length);
        if (store.Load().Any(r => r.SourceKey == key))
        {
            pending.TryRemove(full, out _); CandidatesChanged?.Invoke(); return null;
        }
        await using var incoming = store.Begin(new TransferOffer(Guid.NewGuid().ToString("N"),
            InboxStore.ValidateName(info.Name), "Quick Share import", "Native receive folder", source.Length),
            16L * 1024 * 1024 * 1024, key);
        var buffer = new byte[65536];
        int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
            await incoming.WriteAsync(buffer.AsMemory(0, read), token);
        var record = await incoming.CommitAsync(token);
        importedKeys[key] = 0;
        pending.TryRemove(full, out _);
        CandidatesChanged?.Invoke();
        return record;
    }
    private static string SourceKey(FileInfo info, long size) => info.FullName + "|" + info.CreationTimeUtc.Ticks + "|" + info.LastWriteTimeUtc.Ticks + "|" + size;
}
