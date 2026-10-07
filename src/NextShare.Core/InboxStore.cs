using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NextShare.Core;

public sealed class InboxStore
{
    internal object MetadataGate { get; } = new();
    public string Root { get; }
    public string MetadataRoot { get; }
    public event Action<TransferRecord>? Committed;

    public InboxStore(string root, string? metadataRoot = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())));
        MetadataRoot = Path.GetFullPath(metadataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NextShare", "Storage", key));
        if (IsInside(MetadataRoot, Root) || IsInside(Root, MetadataRoot) || PathsEqual(MetadataRoot, Root))
            throw new ArgumentException("Transfer metadata must be separate from the save folder.");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MetadataRoot);
    }

    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name is "." or ".." ||
            name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) || name.EndsWith('.') || name.EndsWith(' '))
            throw new InvalidDataException("Invalid file name.");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            Enumerable.Range(1, 9).Any(n => stem == $"COM{n}" || stem == $"LPT{n}"))
            throw new InvalidDataException("Reserved file name.");
        return name;
    }

    public IncomingFile Begin(TransferOffer offer, long limit, string? sourceKey = null)
    {
        ValidateName(offer.Name);
        if (offer.Size is < 0 || offer.Size > limit)
            throw new InvalidDataException("File exceeds the configured size limit.");
        var disk = new DriveInfo(Path.GetPathRoot(Root)!);
        if (offer.Size.HasValue && disk.IsReady && disk.AvailableFreeSpace < offer.Size.Value + 16 * 1024 * 1024)
            throw new IOException("Not enough free disk space.");
        return new IncomingFile(this, offer, limit, sourceKey);
    }

    internal string CreateDirectoryFor(string id)
    {
        var folder = Path.Combine(MetadataRoot, id);
        Directory.CreateDirectory(folder);
        return folder;
    }

    internal static void WriteManifest(string folder, TransferRecord record)
    {
        var temp = Path.Combine(folder, "manifest.tmp");
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, record);
            stream.Flush(true);
        }
        File.Move(temp, Path.Combine(folder, "manifest.json"), true);
    }

    internal void Notify(TransferRecord record)
    {
        foreach (var handler in Committed?.GetInvocationList() ?? [])
            try { ((Action<TransferRecord>)handler)(record); } catch { }
    }

    internal TransferRecord Commit(string partialPath, string folder, TransferRecord record, Action moved)
    {
        lock (MetadataGate)
        {
            for (int suffix = 0; suffix < 100000; suffix++)
            {
                string name = DestinationName(record.Name, suffix);
                string path = Path.Combine(Root, name);
                if (File.Exists(path) || Directory.Exists(path)) continue;
                var candidate = record with { Name = name, FilePath = path, State = "Committing" };
                WriteManifest(folder, candidate);
                try { File.Move(partialPath, path, false); }
                catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { continue; }
                // The rename stays on the save volume, even when metadata is on another drive.
                moved();
                File.SetAttributes(path, FileAttributes.Normal);
                candidate = candidate with { State = "Ready" };
                WriteManifest(folder, candidate);
                return candidate;
            }
            throw new IOException("Could not allocate an unused file name.");
        }
    }

    private static string DestinationName(string name, int suffix)
    {
        if (suffix == 0) return name;
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] + $" ({suffix})" + name[dot..] : name + $" ({suffix})";
    }

    public IReadOnlyList<TransferRecord> Load()
    {
        lock (MetadataGate)
        {
            var records = new List<TransferRecord>();
            ReadRecords(MetadataRoot, false, records);
            // Read older per-transfer folders without moving or deleting the user's files.
            ReadRecords(Root, true, records);
            return records.DistinctBy(x => x.Id).OrderByDescending(x => x.ReceivedAt).ToArray();
        }
    }

    private void ReadRecords(string parent, bool legacy, List<TransferRecord> records)
    {
        foreach (var folder in Directory.EnumerateDirectories(parent))
        {
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _)) continue;
            try
            {
                var manifest = Path.Combine(folder, "manifest.json");
                if (!File.Exists(manifest)) continue;
                var record = JsonSerializer.Deserialize<TransferRecord>(File.ReadAllText(manifest));
                if (record is null || record.Id != Path.GetFileName(folder) || record.Size < 0 ||
                    string.IsNullOrEmpty(record.Name) || string.IsNullOrEmpty(record.SessionId) ||
                    record.Sha256 is null || record.Sha256.Length != 64 || !record.Sha256.All(Uri.IsHexDigit) ||
                    !File.Exists(record.FilePath)) continue;
                var full = Path.GetFullPath(record.FilePath);
                if (legacy ? !IsInside(full, folder) : !PathsEqual(Path.GetDirectoryName(full)!, Root) || Path.GetFileName(full) != record.Name)
                    continue;
                if (new FileInfo(full).Length != record.Size) continue;
                if (record.State == "Committing")
                {
                    using var stream = File.OpenRead(full);
                    if (Convert.ToHexString(SHA256.HashData(stream)) != record.Sha256) continue;
                    if (!legacy) File.SetAttributes(full, FileAttributes.Normal);
                    record = record with { State = "Ready" };
                    WriteManifest(folder, record);
                }
                if (record.State == "Ready") records.Add(record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
    }

    private static bool PathsEqual(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
    private static bool IsInside(string path, string parent)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if (!Path.EndsInDirectorySeparator(prefix)) prefix += Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class IncomingFile : IAsyncDisposable
{
    private readonly InboxStore store;
    private readonly TransferOffer offer;
    private readonly long limit;
    private readonly string? sourceKey;
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly string folder;
    private readonly string partialPath;
    private readonly FileStream stream;
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool complete;
    public long Received { get; private set; }

    internal IncomingFile(InboxStore store, TransferOffer offer, long limit, string? sourceKey)
    {
        this.store = store; this.offer = offer; this.limit = limit; this.sourceKey = sourceKey;
        folder = store.CreateDirectoryFor(id);
        partialPath = Path.Combine(store.Root, ".nextshare-" + id + ".partial");
        stream = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        try { File.SetAttributes(partialPath, FileAttributes.Hidden); }
        catch { stream.Dispose(); File.Delete(partialPath); throw; }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        if (complete) throw new InvalidOperationException("Transfer already committed.");
        if (bytes.Length > limit - Received || (offer.Size.HasValue && bytes.Length > offer.Size.Value - Received))
            throw new InvalidDataException("Received data exceeds declared file size.");
        await stream.WriteAsync(bytes, token);
        hash.AppendData(bytes.Span);
        Received += bytes.Length;
    }

    public async Task<TransferRecord> CommitAsync(CancellationToken token)
    {
        if (complete) throw new InvalidOperationException("Transfer already committed.");
        if (offer.Size.HasValue && Received != offer.Size.Value)
            throw new InvalidDataException("Incomplete file: byte count does not match the offer.");
        await stream.FlushAsync(token);
        stream.Flush(true);
        await stream.DisposeAsync();
        var record = new TransferRecord(id, offer.SessionId, offer.Name, offer.Route, offer.Sender,
            Received, Convert.ToHexString(hash.GetHashAndReset()), "", DateTimeOffset.UtcNow, "Committing", sourceKey);
        record = store.Commit(partialPath, folder, record, () => complete = true);
        store.Notify(record);
        return record;
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync();
        hash.Dispose();
        if (!complete)
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch (IOException) { }
    }
}
