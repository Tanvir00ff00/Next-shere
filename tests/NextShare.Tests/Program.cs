using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NextShare.Core;

var suiteRoot = Path.Combine(Path.GetTempPath(), "NextShareTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(suiteRoot);
var results = new List<object>();
int failed = 0;
async Task Test(string name, Func<string, Task> test)
{
    var root = Path.Combine(suiteRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
    try { await test(root); Console.WriteLine("PASS " + name); results.Add(new { name, passed = true }); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); results.Add(new { name, passed = false, error = ex.ToString() }); }
}
void Check(bool value, string message) { if (!value) throw new Exception(message); }
InboxStore OpenStore(string root) => new(root, Path.Combine(suiteRoot, "Metadata", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))));
byte[] Packet(byte opcode, params byte[][] data)
{
    var body = data.SelectMany(x => x).ToArray();
    var packet = new byte[body.Length + 3]; packet[0] = opcode;
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), checked((ushort)packet.Length)); body.CopyTo(packet, 3); return packet;
}
byte[] Header(byte id, byte[] payload)
{
    var h = new byte[payload.Length + 3]; h[0] = id;
    BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(1), checked((ushort)h.Length)); payload.CopyTo(h, 3); return h;
}
byte[] Name(string name) => Header(0x01, Encoding.BigEndianUnicode.GetBytes(name + "\0"));
byte[] Size(uint size) { var h = new byte[5]; h[0] = 0xC3; BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(1), size); return h; }
byte[] Connect() => Packet(0x80, [0x10, 0, 0xFF, 0xFF]);
async Task<(InboxStore Store, byte[] Replies, Exception? Error)> Run(string root, byte[][] packets, bool allow = true, long limit = 1024 * 1024, int fragment = 2)
{
    var store = OpenStore(Path.Combine(root, "Inbox"));
    using var input = new FragmentedStream(packets.SelectMany(x => x).ToArray(), fragment);
    using var output = new MemoryStream();
    Exception? error = null;
    try { await new ObexReceiver(store, new ReceiverOptions { MaximumFileBytes = limit }).RunAsync(input, output, "Test sender", (_, _) => Task.FromResult(allow), CancellationToken.None); }
    catch (Exception ex) { error = ex; }
    return (store, output.ToArray(), error);
}
bool HasReply(byte[] replies, byte code)
{
    for (int i = 0; i + 3 <= replies.Length;)
    {
        if (replies[i] == code) return true;
        int length = BinaryPrimitives.ReadUInt16BigEndian(replies.AsSpan(i + 1)); if (length < 3) break; i += length;
    }
    return false;
}

await Test("Fragmented Bengali file arrives byte-for-byte", async root =>
{
    var data = Encoding.UTF8.GetBytes("বাংলা ডকুমেন্ট — original bytes\n");
    var r = await Run(root, [Connect(), Packet(0x02, Name("ছবি-পরীক্ষা.txt"), Size((uint)data.Length)), Packet(0x82, Header(0x49, data)), Packet(0x81)]);
    Check(r.Error is null, "Transfer failed"); var record = r.Store.Load().Single();
    Check((await File.ReadAllBytesAsync(record.FilePath)).SequenceEqual(data), "Bytes changed");
    Check(record.Sha256 == Convert.ToHexString(SHA256.HashData(data)), "Hash mismatch");
    Check(HasReply(r.Replies, 0x90) && HasReply(r.Replies, 0xA0), "Missing responses");
});
await Test("Multi-file batch stays in one session and uses one approval", async root =>
{
    var store = OpenStore(Path.Combine(root, "Inbox")); int approvals = 0;
    using var input = new FragmentedStream(new[] { Connect(), Packet(0x82, Name("a.txt"), Size(1), Header(0x49, [1])), Packet(0x82, Name("b.txt"), Size(1), Header(0x49, [2])), Packet(0x81) }.SelectMany(x => x).ToArray(), 3);
    using var output = new MemoryStream();
    await new ObexReceiver(store, new ReceiverOptions()).RunAsync(input, output, "Sender", (_, _) => { approvals++; return Task.FromResult(true); }, CancellationToken.None);
    var records = store.Load(); Check(records.Count == 2 && records.Select(x => x.SessionId).Distinct().Count() == 1, "Batch split"); Check(approvals == 1, "Repeated approval");
});
await Test("Declined offer writes no printable file", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, Name("a.txt"), Header(0x49, [1]))], false);
    Check(r.Store.Load().Count == 0 && HasReply(r.Replies, 0xC3), "Decline was ignored");
});
await Test("Truncated upload stays out of inbox", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x02, Name("a.txt"), Size(5), Header(0x48, [1, 2]))]);
    Check(r.Store.Load().Count == 0, "Partial file became ready"); Check(!Directory.EnumerateFiles(r.Store.Root, "*.partial", SearchOption.AllDirectories).Any(), "Partial leaked after disconnect");
});
await Test("Declared size mismatch fails", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, Name("a.txt"), Size(5), Header(0x49, [1, 2]))]);
    Check(r.Error is InvalidDataException && r.Store.Load().Count == 0, "Size mismatch accepted");
});
await Test("Body larger than declared size fails", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, Name("a.txt"), Size(1), Header(0x49, [1, 2]))]);
    Check(r.Error is InvalidDataException && r.Store.Load().Count == 0, "Oversize accepted");
});
await Test("Path traversal never escapes inbox", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, Name("../escape.txt"), Header(0x49, [1]))]);
    Check(r.Error is InvalidDataException && r.Store.Load().Count == 0 && !File.Exists(Path.Combine(root, "escape.txt")), "Unsafe path accepted");
});
await Test("Reserved Windows name rejected", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, Name("CON.txt"), Header(0x49, [1]))]);
    Check(r.Error is InvalidDataException, "Reserved name accepted");
});
await Test("Sender manifest.json name does not overwrite metadata", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, Name("manifest.json"), Header(0x49, Encoding.UTF8.GetBytes("customer file")))]);
    Check(r.Error is null && r.Store.Load().Count == 1, "Metadata collision");
});
await Test("Zero-byte file preserved", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, Name("empty.txt"), Size(0), Header(0x49, []))]);
    Check(r.Error is null && r.Store.Load().Single().Size == 0, "Zero byte file failed");
});
await Test("Unknown length streams within limit", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x02, Name("a.txt"), Header(0x48, [1])), Packet(0x82, Header(0x49, [2, 3]))]);
    Check(r.Error is null && r.Store.Load().Single().Size == 3, "Unknown length rejected");
});
await Test("Configured limit enforced without length header", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, Name("a.txt"), Header(0x49, [1, 2, 3]))], limit: 2);
    Check(r.Error is InvalidDataException && r.Store.Load().Count == 0, "Limit bypassed");
});
await Test("Malformed header length rejected", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, [0x01, 0, 40, 0])]);
    Check(r.Error is InvalidDataException && r.Store.Load().Count == 0, "Malformed header accepted");
});
await Test("Invalid UTF16 rejected", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, Header(0x01, [0xD8, 0, 0, 0]), Header(0x49, [1]))]);
    Check(r.Error is InvalidDataException, "Malformed Unicode accepted");
});
await Test("PUT before CONNECT rejected", async root =>
{
    var r = await Run(root, [Packet(0x82, Name("a.txt"), Header(0x49, [1]))]);
    Check(r.Error is InvalidDataException && r.Store.Load().Count == 0, "Missing CONNECT accepted");
});
await Test("Name cannot change mid-transfer", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x02, Name("a.txt"), Header(0x48, [1])), Packet(0x82, Name("b.txt"), Header(0x49, [2]))]);
    Check(r.Error is InvalidDataException && r.Store.Load().Count == 0, "Name changed");
});
await Test("End-of-body forbids following body", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, Name("a.txt"), Header(0x49, [1]), Header(0x48, [2]))]);
    Check(r.Error is InvalidDataException && r.Store.Load().Count == 0, "Body after end accepted");
});
await Test("ABORT removes partial and permits next file", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x02, Name("a.txt"), Header(0x48, [1])), Packet(0xFF), Packet(0x82, Name("b.txt"), Header(0x49, [2]))]);
    Check(r.Error is null && r.Store.Load().Single().Name == "b.txt", "Abort failed");
});
await Test("Repeated filenames never overwrite previous customer file", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, Name("a.txt"), Header(0x49, [1])), Packet(0x82, Name("a.txt"), Header(0x49, [2]))]);
    Check(r.Error is null && r.Store.Load().Count == 2 && r.Store.Load().Select(x => x.FilePath).Distinct().Count() == 2, "Overwrite occurred");
});
await Test("Commit is recovered after metadata finalization interruption", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, Name("a.txt"), Header(0x49, [1, 2]))]);
    var record = r.Store.Load().Single();
    var manifest = Directory.EnumerateFiles(r.Store.MetadataRoot, "manifest.json", SearchOption.AllDirectories).Single();
    await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(record with { State = "Committing" }));
    Check(OpenStore(r.Store.Root).Load().Single().State == "Ready", "Recovery failed");
    var recovered = JsonSerializer.Deserialize<TransferRecord>(await File.ReadAllTextAsync(manifest))!;
    Check(recovered.State == "Ready", "Recovery not persisted");
});
await Test("Corrupt committed payload does not recover as ready", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, Name("a.txt"), Header(0x49, [1, 2]))]);
    var record = r.Store.Load().Single(); var manifest = Directory.EnumerateFiles(r.Store.MetadataRoot, "manifest.json", SearchOption.AllDirectories).Single();
    await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(record with { State = "Committing" }));
    await File.WriteAllBytesAsync(record.FilePath, [3, 4]); Check(r.Store.Load().Count == 0, "Corruption recovered as ready");
});
await Test("Companion import preserves source and persists duplicate detection", async root =>
{
    var store = OpenStore(Path.Combine(root, "Inbox")); var drop = Path.Combine(root, "Drop"); Directory.CreateDirectory(drop);
    var path = Path.Combine(drop, "বাংলা.pdf"); await File.WriteAllBytesAsync(path, [1, 2, 3]);
    var importer = new FolderImporter(store); importer.Watch(drop);
    var record = await importer.ImportCompletedAsync(path, CancellationToken.None); importer.Stop();
    var second = new FolderImporter(OpenStore(store.Root)); second.Watch(drop);
    Check(await second.ImportCompletedAsync(path, CancellationToken.None) is null, "Duplicate imported after restart"); second.Stop();
    Check(record is not null && File.Exists(path) && store.Load().Count == 1, "Source changed or lost");
});
await Test("Locked native receive file is not imported", async root =>
{
    var store = OpenStore(Path.Combine(root, "Inbox")); var drop = Path.Combine(root, "Drop"); Directory.CreateDirectory(drop);
    var path = Path.Combine(drop, "a.txt"); await File.WriteAllBytesAsync(path, [1]);
    var importer = new FolderImporter(store); importer.Watch(drop);
    using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
    bool rejected = false; try { await importer.ImportCompletedAsync(path, CancellationToken.None); } catch (IOException) { rejected = true; }
    importer.Stop(); Check(rejected && store.Load().Count == 0, "Locked file imported");
});
await Test("Import folder cannot be inbox or parent", root =>
{
    var store = OpenStore(Path.Combine(root, "Inbox")); var importer = new FolderImporter(store);
    bool rejected = false; try { importer.Watch(root); } catch (InvalidOperationException) { rejected = true; }
    importer.Stop(); Check(rejected, "Recursive import permitted"); return Task.CompletedTask;
});
await Test("Committed event observer cannot break successful receive", async root =>
{
    var store = OpenStore(Path.Combine(root, "Inbox")); store.Committed += _ => throw new Exception("Observer fault");
    await using var incoming = store.Begin(new TransferOffer("s", "a.txt", "Test", "Sender", 1), 100);
    await incoming.WriteAsync(new byte[] { 1 }, CancellationToken.None); await incoming.CommitAsync(CancellationToken.None);
    Check(store.Load().Count == 1, "Observer broke commit");
});
await Test("Large multi-packet binary stream preserves data", async root =>
{
    var data = new byte[2 * 1024 * 1024]; new Random(42).NextBytes(data);
    var packets = new List<byte[]> { Connect(), Packet(0x02, Name("binary.bin"), Size((uint)data.Length)) };
    for (int i = 0; i < data.Length; i += 32000)
    {
        var count = Math.Min(32000, data.Length - i); bool last = i + count == data.Length;
        packets.Add(Packet(last ? (byte)0x82 : (byte)0x02, Header(last ? (byte)0x49 : (byte)0x48, data.AsSpan(i, count).ToArray())));
    }
    var r = await Run(root, packets.ToArray(), limit: 3 * 1024 * 1024, fragment: 173);
    Check(r.Error is null && r.Store.Load().Single().Sha256 == Convert.ToHexString(SHA256.HashData(data)), "Large stream changed");
});

await Test("Four simultaneous customers with identical filenames stay isolated", async root =>
{
    var store = OpenStore(Path.Combine(root, "Inbox"));
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int waiting = 0;
    var payloads = Enumerable.Range(0, 4).Select(i => Enumerable.Repeat((byte)(i + 1), 45000).ToArray()).ToArray();
    var tasks = payloads.Select(async (data, i) =>
    {
        using var input = new FragmentedStream(new[] { Connect(), Packet(0x82, Name("ছবি.jpg"), Size((uint)data.Length), Header(0x49, data)), Packet(0x81) }.SelectMany(x => x).ToArray(), 317);
        using var output = new MemoryStream();
        await new ObexReceiver(store, new ReceiverOptions()).RunAsync(input, output, "Customer " + i, async (_, token) =>
        {
            if (Interlocked.Increment(ref waiting) == 4) gate.TrySetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5), token); return true;
        }, CancellationToken.None);
        Check(HasReply(output.ToArray(), 0xA0), "Customer was not acknowledged");
    }).ToArray();
    await Task.WhenAll(tasks);
    var records = store.Load();
    Check(records.Count == 4 && records.Select(r => r.SessionId).Distinct().Count() == 4 && records.Select(r => r.FilePath).Distinct().Count() == 4, "Customers collided");
    for (int i = 0; i < 4; i++)
    {
        var record = records.Single(r => r.Sender == "Customer " + i);
        Check((await File.ReadAllBytesAsync(record.FilePath)).SequenceEqual(payloads[i]), "Wrong customer payload");
        Check(record.Sha256 == Convert.ToHexString(SHA256.HashData(payloads[i])), "Wrong customer hash");
    }
});

await Test("Interrupted customer cannot damage another customer's completed file", async root =>
{
    var store = OpenStore(Path.Combine(root, "Inbox"));
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int waiting = 0;
    async Task Receive(bool complete)
    {
        var packets = complete ? new[] { Connect(), Packet(0x82, Name("same.pdf"), Size(2), Header(0x49, [4, 5])) } :
            new[] { Connect(), Packet(0x02, Name("same.pdf"), Size(2), Header(0x48, [9])) };
        using var input = new FragmentedStream(packets.SelectMany(x => x).ToArray(), 1);
        using var output = new MemoryStream();
        await new ObexReceiver(store, new ReceiverOptions()).RunAsync(input, output, complete ? "Complete" : "Interrupted", async (_, token) =>
        {
            if (Interlocked.Increment(ref waiting) == 2) gate.TrySetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5), token); return true;
        }, CancellationToken.None);
    }
    await Task.WhenAll(Receive(false), Receive(true));
    var record = store.Load().Single();
    Check(record.Sender == "Complete" && (await File.ReadAllBytesAsync(record.FilePath)).SequenceEqual(new byte[] { 4, 5 }), "Interrupted customer changed completed payload");
    Check(!Directory.EnumerateFiles(store.Root, "*.partial", SearchOption.AllDirectories).Any(), "Interrupted partial leaked");
});

await Test("History reads cannot race recovery against active commits", async root =>
{
    var store = OpenStore(Path.Combine(root, "Inbox"));
    using var stop = new CancellationTokenSource();
    var reader = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            foreach (var record in store.Load()) Check(File.Exists(record.FilePath) && record.State == "Ready", "Uncommitted record exposed");
            await Task.Delay(1);
        }
    });
    try
    {
        await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            await using var incoming = store.Begin(new TransferOffer("s" + i, "same.txt", "Test", "Customer " + i, 1024), 4096);
            await incoming.WriteAsync(Enumerable.Repeat((byte)i, 1024).ToArray(), CancellationToken.None);
            await incoming.CommitAsync(CancellationToken.None);
        }));
    }
    finally { stop.Cancel(); await reader; }
    Check(store.Load().Count == 40, "Active commit was lost");
});

await Test("Completed files save directly with no receive folders or metadata in destination", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, Name("photo.jpg"), Header(0x49, [1, 2, 3]))]);
    Check(r.Error is null, "Transfer failed");
    var record = r.Store.Load().Single();
    Check(record.FilePath == Path.Combine(r.Store.Root, "photo.jpg"), "File was nested");
    Check(!Directory.EnumerateDirectories(r.Store.Root).Any(), "Output contains a generated folder");
    Check(Directory.EnumerateFiles(r.Store.Root).Single() == record.FilePath, "Output contains extra files");
    Check((File.GetAttributes(record.FilePath) & FileAttributes.Hidden) == 0, "Final file remains hidden");
    Check(Directory.EnumerateFiles(r.Store.MetadataRoot, "manifest.json", SearchOption.AllDirectories).Count() == 1, "Metadata missing");
});

await Test("Existing files and folders get numbered names without overwriting", async root =>
{
    var store = OpenStore(Path.Combine(root, "Inbox"));
    Directory.CreateDirectory(Path.Combine(store.Root, "same.pdf"));
    var existing = Path.Combine(store.Root, "same (1).pdf"); await File.WriteAllBytesAsync(existing, [9]);
    await using var incoming = store.Begin(new TransferOffer("s", "same.pdf", "Test", "Sender", 2), 100);
    await incoming.WriteAsync(new byte[] { 1, 2 }, CancellationToken.None);
    var record = await incoming.CommitAsync(CancellationToken.None);
    Check(record.Name == "same (2).pdf" && record.FilePath == Path.Combine(store.Root, "same (2).pdf"), "Suffix collision handling failed");
    Check((await File.ReadAllBytesAsync(existing)).SequenceEqual(new byte[] { 9 }), "Existing file changed");
    Check(Directory.Exists(Path.Combine(store.Root, "same.pdf")), "Existing folder changed");
    Check(OpenStore(store.Root).Load().Single().Id == record.Id, "Flat history lost after restart");
});

await Test("Legacy customer history is still read without moving original files", async root =>
{
    var store = OpenStore(Path.Combine(root, "Inbox")); var id = Guid.NewGuid().ToString("N");
    var folder = Path.Combine(store.Root, id); var files = Path.Combine(folder, "files"); Directory.CreateDirectory(files);
    var path = Path.Combine(files, "old.jpg"); await File.WriteAllBytesAsync(path, [2, 3]);
    var record = new TransferRecord(id, "old-session", "old.jpg", "Bluetooth", "Phone", 2, Convert.ToHexString(SHA256.HashData(new byte[] { 2, 3 })), path, DateTimeOffset.UtcNow);
    await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(record));
    Check(store.Load().Single().FilePath == path && File.Exists(path), "Legacy history not preserved");
});

await Test("Flat manifest cannot point outside the selected save folder", async root =>
{
    var r = await Run(root, [Connect(), Packet(0x82, Name("a.txt"), Header(0x49, [1]))]);
    var record = r.Store.Load().Single(); var foreign = Path.Combine(root, "foreign.txt"); await File.WriteAllBytesAsync(foreign, [1]);
    var manifest = Directory.EnumerateFiles(r.Store.MetadataRoot, "manifest.json", SearchOption.AllDirectories).Single();
    await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(record with { Name = "foreign.txt", FilePath = foreign }));
    Check(r.Store.Load().Count == 0, "Foreign file accepted into history");
});

await Test("BLE weave fragmented encrypted frames survive counter wrap and four independent peers", root =>
{
    foreach (int packetSize in new[] { 20, 100, 509 })
    {
        var sessions = Enumerable.Range(0, 4).Select(_ => new WeaveSession()).ToArray();
        foreach (var session in sessions) { var hello = session.Connect([0x80, 0, 1, 0, 1, 1, 0xFD], packetSize); session.Receive(hello[1]); }
        byte[] frame = new byte[8197]; BinaryPrimitives.WriteInt32BigEndian(frame, frame.Length - 4); RandomNumberGenerator.Fill(frame.AsSpan(4));
        var packets = sessions.Select(s => s.Send(frame).ToArray()).ToArray();
        byte[]?[] received = new byte[4][];
        for (int i = 0; i < packets[0].Length; i++)
            for (int peer = 0; peer < 4; peer++) received[peer] = sessions[peer].Receive(packets[peer][i]);
        Check(received.All(r => r is not null && r.SequenceEqual(frame)), "BLE peers mixed or lost bytes");
    }
    return Task.CompletedTask;
});
await Test("BLE weave rejects missing first fragment, reordered packets and malformed length", root =>
{
    WeaveSession Session() { var s = new WeaveSession(); var hello = s.Connect([0x80,0,1,0,1,0,20],20); s.Receive(hello[1]); return s; }
    void Reject(Action action) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Check(rejected, "Malformed BLE accepted"); }
    Reject(() => Session().Receive([4,1,2,3,4]));
    byte[] frame = new byte[200]; BinaryPrimitives.WriteInt32BigEndian(frame,196);
    var s = Session(); var packets = s.Send(frame).ToArray(); s.Receive(packets[0]); Reject(() => s.Receive(packets[2]));
    Reject(() => Session().Receive([0x1C,0xFC,0x9F,0x5E,0,0,1,0,1]));
    Reject(() => Session().Connect([0x80,0,1,0,1,0,20],20));
    return Task.CompletedTask;
});
await Test("BLE accepts counter-bearing retry handshakes and confirms the negotiated MTU", root =>
{
    foreach (byte header in new byte[] {0x80,0x90,0xF0})
    {
        var session = new WeaveSession(); var hello = session.Connect([header,0,1,0,1,0,100],100);
        Check(hello[0].SequenceEqual(new byte[] {0x81,0,1,0,100}),"Weave confirmation bytes differ from protocol");
        var intro = hello[1].ToArray(); intro[0] = (byte)(((((header>>4)+1)&7)<<4)|0x0C);
        Check(session.Receive(intro) is null,"Client introduction not accepted");
    }
    var retried = new WeaveSession(); retried.Connect([0x80,0,1,0,1,0,20],20);
    var retriedHello = retried.RetryConnection([0x90,0,1,0,1,0,20]);
    var nextIntro = retriedHello[1].ToArray(); nextIntro[0] = 0x2C; retried.Receive(nextIntro);
    bool rejected=false; try { retried.RetryConnection([0xA0,0,1,0,1,0,20]); } catch(InvalidDataException) { rejected=true; }
    Check(rejected,"An established stream was reset by a later connection request");
    var unspecified = new WeaveSession(); unspecified.Connect([0x80,0,1,0,1,0,0],20);
    Check(unspecified.PacketSize==20,"Unspecified requested MTU ignored transport limit");
    return Task.CompletedTask;
});
await Test("BLE acknowledgement matches the Google SocketControlFrame wire format", root =>
{
    var session = new WeaveSession(); var hello = session.Connect([0x80,0,1,0,1,0,20],20); session.Receive(hello[1]);
    byte[] ack = session.Acknowledge(128).Single();
    Check(ack.SequenceEqual(new byte[] {0x2C,0,0,0,8,3,0x22,8,0x0A,3,0xFC,0x9F,0x5E,0x10,0x80,1}),"Acknowledgement protobuf or counter is incorrect");
    Check(session.Receive(ack) is null,"Valid peer acknowledgement rejected as an introduction");
    byte[] frame = [0,0,0,1,0x08];
    Check(session.Send(frame).Single()[0]==0x3C,"Acknowledgement and data notification counters diverged");
    bool rejected=false;
    try { session.Receive([0x3C,0,0,0,8,3,0x22,1,0x0A]); } catch(InvalidDataException) { rejected=true; }
    Check(rejected,"Malformed acknowledgement accepted");
    return Task.CompletedTask;
});
await Test("BLE notification retries refresh the rejected Windows subscription without broadcasting", async root =>
{
    var stale = new object(); var fresh = new object(); var other = new object();
    int reads=0, sends=0;
    var pump = new BleNotificationPump<object>(
        () => new[] { new BleSubscription<object>("a", ++reads==1 ? stale : fresh,20,true), new BleSubscription<object>("b",other,20,true) },
        (target,bytes,token) =>
        {
            Check(!ReferenceEquals(target,other),"A customer's notification was sent to another subscriber");
            sends++;
            if (ReferenceEquals(target,stale)) throw new BleNotificationRejectedException(new System.Runtime.InteropServices.COMException("CCCD not ready",BleNotificationPump<object>.IllegalMethodCall));
            Check(ReferenceEquals(target,fresh) && bytes.Span.SequenceEqual(new byte[]{0x81,0,1,0,20}),"Notification target or bytes changed");
            return Task.CompletedTask;
        });
    await pump.SendAsync("a",new byte[]{0x81,0,1,0,20},CancellationToken.None);
    Check(sends==2 && reads==2,"Notification did not refresh/retry after the real Windows HRESULT");
});
await Test("BLE notification waits for active CCCD and negotiates the notification payload limit", async root =>
{
    var client=new object(); int reads=0,sends=0;
    var pump=new BleNotificationPump<object>(() => ++reads<3 ? Array.Empty<BleSubscription<object>>() : new[]{new BleSubscription<object>("a",client,20,true)},
        (_,_,_) => { sends++; return Task.CompletedTask; });
    Check(await pump.WaitForCapacityAsync("a",CancellationToken.None)==20,"Negotiation used ATT MTU rather than notification capacity");
    bool rejected=false;try { await pump.SendAsync("a",new byte[21],CancellationToken.None); } catch(InvalidDataException){rejected=true;}
    Check(rejected && sends==0,"Oversized notification was emitted");
    await pump.SendAsync("a",new byte[20],CancellationToken.None); Check(sends==1,"Valid ATT payload not sent");
});
await Test("BLE characteristic serializes four customers while preserving each packet's recipient", async root =>
{
    var clients=Enumerable.Range(0,4).Select(_=>new object()).ToArray(); int active=0,peak=0; var delivered=new List<string>();
    var pump=new BleNotificationPump<object>(()=>clients.Select((c,i)=>new BleSubscription<object>(i.ToString(),c,20,true)).ToArray(),
        async (client,packet,token)=>
        {
            int concurrent=Interlocked.Increment(ref active); peak=Math.Max(peak,concurrent);
            int id=Array.IndexOf(clients,client);Check(id==packet.Span[0],"Notification crossed customer sessions");
            await Task.Delay(5,token);delivered.Add($"{id}:{packet.Span[1]}");Interlocked.Decrement(ref active);
        });
    await Task.WhenAll(Enumerable.Range(0,4).Select(async id=> {for(byte n=0;n<10;n++)await pump.SendAsync(id.ToString(),new byte[]{(byte)id,n},CancellationToken.None);}));
    Check(peak==1 && delivered.Count==40 && delivered.Distinct().Count()==40,"BLE sends overlapped, duplicated or lost packets");
});
await Test("BLE notification never retries uncertain delivery failures and cancellation stops queued sends", async root =>
{
    var client=new object();int sends=0;
    var pump=new BleNotificationPump<object>(()=>new[]{new BleSubscription<object>("a",client,20,true)},
        (_,_,_)=>{sends++;throw new IOException("Unreachable result after attempted delivery");});
    bool failedOnce=false;try{await pump.SendAsync("a",new byte[]{1},CancellationToken.None);}catch(IOException){failedOnce=true;}
    Check(failedOnce && sends==1,"Potentially delivered packet was retried");
    using var stop=new CancellationTokenSource();stop.Cancel();bool cancelled=false;
    try{await pump.SendAsync("a",new byte[]{1},stop.Token);}catch(OperationCanceledException){cancelled=true;}
    Check(cancelled && sends==1,"Cancelled peer emitted a notification");
});
await Test("BLE notification bounds persistent Windows-state retries and releases its shared gate", async root =>
{
    var client=new object();int sends=0;bool unavailable=true;
    var pump=new BleNotificationPump<object>(()=>new[]{new BleSubscription<object>("a",client,20,true)},
        (_,_,_)=>{sends++;if(unavailable)throw new BleNotificationRejectedException(new System.Runtime.InteropServices.COMException("Not ready",BleNotificationPump<object>.IllegalMethodCall));return Task.CompletedTask;});
    bool rejected=false;try{await pump.SendAsync("a",new byte[]{1},CancellationToken.None);}catch(BleNotificationRejectedException){rejected=true;}
    Check(rejected && sends==8,"State retry is unbounded or fails immediately");
    unavailable=false;await pump.SendAsync("a",new byte[]{2},CancellationToken.None);Check(sends==9,"Failed peer retained the characteristic gate");
});
await Test("Successful BLE delivery with an unavailable BytesSent getter is never resent", async root =>
{
    var client=new object();int dispatched=0,warnings=0,byteReads=0;
    var pump=new BleNotificationPump<object>(()=>new[]{new BleSubscription<object>("a",client,20,true)},
        (_,packet,token)=>BleNotificationDelivery.SendAsync(
            ()=>{dispatched++;return Task.FromResult(1);},(operation,_)=>operation,
            _=>BleNotificationOutcome.Success,
            _=>{byteReads++;throw new System.Runtime.InteropServices.COMException("Metadata unavailable",BleNotificationPump<object>.IllegalMethodCall);},
            _=>throw new Exception("ProtocolError must not be read on success"),packet.Length,token,_=>warnings++));
    await pump.SendAsync("a",new byte[]{0x81,0,1,0,20},CancellationToken.None);
    Check(dispatched==1&&byteReads==1&&warnings==1,"Delivered confirmation was duplicated or rejected because of metadata");
});
await Test("BLE retries only a synchronous native rejection before operation creation", async root =>
{
    var client=new object();int dispatched=0,waited=0;
    var pump=new BleNotificationPump<object>(()=>new[]{new BleSubscription<object>("a",client,20,true)},
        (_,packet,token)=>BleNotificationDelivery.SendAsync(
            ()=>{if(++dispatched==1)throw new System.Runtime.InteropServices.COMException("Not dispatched",BleNotificationPump<object>.IllegalMethodCall);return Task.FromResult(1);},
            (operation,_)=>{waited++;return operation;},_=>BleNotificationOutcome.Success,_=>packet.Length,_=>null,packet.Length,token));
    await pump.SendAsync("a",new byte[]{1},CancellationToken.None);
    Check(dispatched==2&&waited==1,"Safe native rejection was not distinguished from accepted operation");
});
await Test("BLE result HRESULT and status getter failures never trigger a second dispatch", async root =>
{
    foreach(bool failWhileAwaiting in new[]{true,false})
    {
        var client=new object();int dispatched=0;
        var pump=new BleNotificationPump<object>(()=>new[]{new BleSubscription<object>("a",client,20,true)},
            (_,packet,token)=>BleNotificationDelivery.SendAsync(
                ()=>{dispatched++;return Task.FromResult(1);},
                (operation,_)=>failWhileAwaiting?Task.FromException<int>(new System.Runtime.InteropServices.COMException("Result unavailable",BleNotificationPump<object>.IllegalMethodCall)):operation,
                _=>throw new System.Runtime.InteropServices.COMException("Status unavailable",BleNotificationPump<object>.IllegalMethodCall),
                _=>packet.Length,_=>null,packet.Length,token));
        bool failed=false;try{await pump.SendAsync("a",new byte[]{1},CancellationToken.None);}catch(IOException ex){failed=ex.Message.Contains("delivery may already have occurred");}
        Check(failed&&dispatched==1,"Unknown delivery was repeated or misreported as a pre-dispatch rejection");
    }
});
await Test("BLE failed results avoid invalid metadata access and short successes are rejected", async root =>
{
    foreach(var outcome in new[]{BleNotificationOutcome.Unreachable,BleNotificationOutcome.AccessDenied,BleNotificationOutcome.ProtocolError,BleNotificationOutcome.Success})
    {
        bool failed=false;int byteReads=0,protocolReads=0;
        try{await BleNotificationDelivery.SendAsync(()=>Task.FromResult(1),(operation,_)=>operation,
            _=>outcome,_=>{byteReads++;return 0;},_=>{protocolReads++;return (byte)0x11;},5,CancellationToken.None);}
        catch(IOException ex){failed=outcome==BleNotificationOutcome.Success?ex.Message.Contains("truncated"):ex.Message.Contains(outcome.ToString());}
        Check(failed&&byteReads==(outcome==BleNotificationOutcome.Success?1:0)&&protocolReads==(outcome==BleNotificationOutcome.ProtocolError?1:0),"Failed status was masked by an invalid property or truncated packet accepted");
    }
});
await Test("Samsung's counter-bearing weave error closes the old handshake instead of starting a new one", root =>
{
    foreach(byte header in new byte[]{0x82,0x92,0xF2})Check(WeaveSession.IsPeerError(new[]{header}),"Peer error treated as a fresh connection request");
    Check(!WeaveSession.IsPeerError(new byte[]{0x80}) && !WeaveSession.IsPeerError(new byte[]{0x1C}) && !WeaveSession.IsPeerError(new byte[]{0x92,1}),"Malformed packet treated as a valid disconnect");
    return Task.CompletedTask;
});
await Test("Native completion saves directly and remains idempotent after restart", async root =>
{
    string staging = Path.Combine(root,"Staging"), id = Guid.NewGuid().ToString("N"), session = Path.Combine(staging,id);
    Directory.CreateDirectory(session); string file = Path.Combine(session,"ছবি.jpg"); await File.WriteAllBytesAsync(file,[1,2,3]);
    string receipt = JsonSerializer.Serialize(new { type="complete", id, sender="Android", files=new[] {file}, total=3 });
    var store = OpenStore(Path.Combine(root,"Inbox"));
    await new QuickShareCompletionImporter(store,staging).ImportAsync(receipt,CancellationToken.None);
    await new QuickShareCompletionImporter(OpenStore(store.Root),staging).ImportAsync(receipt,CancellationToken.None);
    var record = store.Load().Single();
    Check(record.Route=="Quick Share" && record.Name=="ছবি.jpg" && record.FilePath==Path.Combine(store.Root,"ছবি.jpg"),"Native output incorrect");
    Check(!Directory.EnumerateDirectories(store.Root).Any(),"Native output nested");
    Check(File.Exists(file),"Recovery source destroyed");
});
await Test("Native completion rejects outside files and mismatched batch sizes before committing", async root =>
{
    string staging=Path.Combine(root,"Staging"),id=Guid.NewGuid().ToString("N"),session=Path.Combine(staging,id);
    Directory.CreateDirectory(session);string file=Path.Combine(session,"a.txt"),outside=Path.Combine(root,"foreign.txt");
    await File.WriteAllBytesAsync(file,[1]); await File.WriteAllBytesAsync(outside,[2]);
    var store=OpenStore(Path.Combine(root,"Inbox"));var importer=new QuickShareCompletionImporter(store,staging);
    foreach(var receipt in new[] { new {type="complete",id,sender="Android",files=new[] {file,outside},total=2}, new {type="complete",id,sender="Android",files=new[] {file},total=2} })
    {
        bool rejected=false;try { await importer.ImportAsync(JsonSerializer.Serialize(receipt),CancellationToken.None); } catch(InvalidDataException) {rejected=true;}
        Check(rejected&&store.Load().Count==0,"Invalid completion created a file");
    }
});

await Test("Receive lifecycle notifies before bytes and durable commit",async root=>
{
    var store=OpenStore(Path.Combine(root,"Inbox"));var events=new List<string>();
    var receiver=new ObexReceiver(store,new ReceiverOptions());
    receiver.Started+=offer=>{Check(store.Load().Count==0,"Already saved at start");events.Add("started");};
    receiver.Progress+=p=>{Check(store.Load().Count==0,"Already saved at progress");Check(p.Received==2,"Progress bytes wrong");events.Add("progress");};
    receiver.Processing+=p=>{Check(store.Load().Count==0,"Already saved at processing");events.Add("processing");};
    store.Committed+=_=>events.Add("committed");receiver.SessionEnded+=_=>events.Add("ended");
    using var input=new FragmentedStream(new[]{Connect(),Packet(0x82,Name("a.txt"),Size(2),Header(0x49,[1,2])),Packet(0x81)}.SelectMany(x=>x).ToArray(),2);
    using var output=new MemoryStream();await receiver.RunAsync(input,output,"Sender",(_,_)=>Task.FromResult(true),CancellationToken.None);
    Check(events.Take(4).SequenceEqual(new[]{"started","progress","processing","committed"})&&events.Last()=="ended","Lifecycle order incorrect");
});
await Test("Disconnect ends incomplete receive without committing",async root=>
{
    var store=OpenStore(Path.Combine(root,"Inbox"));var receiver=new ObexReceiver(store,new ReceiverOptions());string? start=null,end=null;
    receiver.Started+=p=>start=p.SessionId;receiver.SessionEnded+=id=>end=id;
    using var input=new FragmentedStream(new[]{Connect(),Packet(0x02,Name("a.txt"),Size(5),Header(0x48,[1]))}.SelectMany(x=>x).ToArray(),2);
    using var output=new MemoryStream();await receiver.RunAsync(input,output,"Sender",(_,_)=>Task.FromResult(true),CancellationToken.None);
    Check(start is not null&&start==end&&store.Load().Count==0&&!Directory.EnumerateFiles(store.Root,"*.partial").Any(),"Failed receive was not cleaned up");
});
await SendTests.RunAsync(Test);
Console.WriteLine($"{results.Count - failed}/{results.Count} passed. Phone/radio interoperability is not covered by these tests.");
var outputIndex = Array.IndexOf(args, "--report");
if (outputIndex >= 0 && outputIndex + 1 < args.Length)
{
    var output = Path.GetFullPath(args[outputIndex + 1]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { capturedAtUtc = DateTimeOffset.UtcNow, passed = results.Count - failed, failed, radioTested = false, results }, new JsonSerializerOptions { WriteIndented = true }));
}
// Retain isolated temp results for diagnosing failures; no user data is modified or deleted.
return failed == 0 ? 0 : 1;

sealed class FragmentedStream(byte[] bytes, int fragment) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, fragment)], token);
}

