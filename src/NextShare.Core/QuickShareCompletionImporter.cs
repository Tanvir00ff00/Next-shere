using System.Text.Json;

namespace NextShare.Core;

// Imports only an explicit protocol-completion receipt emitted after all authenticated payloads flush.
public sealed class QuickShareCompletionImporter(InboxStore store, string stagingRoot)
{
    private readonly string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
    private readonly SemaphoreSlim gate = new(1);
    public async Task ImportAsync(string json, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            using var document = JsonDocument.Parse(json);
            var receipt = document.RootElement;
            if (receipt.GetProperty("type").GetString() != "complete") throw new InvalidDataException("Missing protocol completion.");
            var id = receipt.GetProperty("id").GetString()!;
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid transfer ID.");
            var sender = receipt.GetProperty("sender").GetString() ?? "Quick Share device";
            var files = receipt.GetProperty("files").EnumerateArray().Select(p => Path.GetFullPath(p.GetString()!)).ToArray();
            if (files.Length is < 1 or > 512 || files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
                throw new InvalidDataException("Invalid attachment list.");
            var sessionRoot = Path.Combine(root, id);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(sessionRoot) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked staging folder.");
            long total = 0;
            foreach (var path in files)
            {
                if (!string.Equals(Path.GetDirectoryName(path), sessionRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Attachment outside transfer folder.");
                InboxStore.ValidateName(Path.GetFileName(path));
                var info = new FileInfo(path);
                if (!info.Exists) throw new FileNotFoundException("Completed attachment missing", path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked attachment.");
                total = checked(total + info.Length);
            }
            if (total > 16L * 1024 * 1024 * 1024 || total != receipt.GetProperty("total").GetInt64()) throw new InvalidDataException("Incomplete Quick Share batch.");
            var previous = store.Load().Where(r => r.SourceKey is not null).Select(r => r.SourceKey).ToHashSet();
            foreach (var path in files)
            {
                string key = "quickshare:" + id + ":" + Path.GetFileName(path);
                if (previous.Contains(key)) continue;
                await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 65536, true);
                await using var incoming = store.Begin(new TransferOffer(id, Path.GetFileName(path), "Quick Share", sender, source.Length), 16L * 1024 * 1024 * 1024, key);
                var buffer = new byte[65536]; int count;
                while ((count = await source.ReadAsync(buffer, token)) != 0) await incoming.WriteAsync(buffer.AsMemory(0, count), token);
                await incoming.CommitAsync(token);
            }
        }
        finally { gate.Release(); }
    }
}
