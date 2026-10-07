using System.Buffers.Binary;
using System.Text;

namespace NextShare.Core;

// OBEX Object Push over an already-established RFCOMM stream. No LAN listener.
public sealed class ObexReceiver(InboxStore store, ReceiverOptions options)
{
    public event Action<TransferProgress>? Progress;
    public event Action<TransferOffer>? Started;
    public event Action<TransferProgress>? Processing;
    public event Action<string>? SessionEnded;
    public async Task RunAsync(Stream input, Stream output, string sender,
        Func<TransferOffer, CancellationToken, Task<bool>> consent, CancellationToken token)
    {
        var session = Guid.NewGuid().ToString("N");
        IncomingFile? file = null;
        string? name = null;
        long? length = null;
        bool connected = false, approved = false, ended = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(options.PacketTimeout);
                var packet = await ReadPacketAsync(input, timeout.Token);
                if (packet is null) break;
                byte opcode = packet[0];
                try
                {
                    if (opcode == 0x80) // CONNECT
                    {
                        if (connected || packet.Length < 7 || packet[3] != 0x10 || packet[4] != 0)
                            throw new InvalidDataException("Invalid OBEX CONNECT.");
                        var peerMax = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(5, 2));
                        if (peerMax < 255) throw new InvalidDataException("Invalid maximum packet size.");
                        ParseHeaders(packet, 7); // validate optional headers as well
                        connected = true;
                        await ReplyAsync(output, [0xA0, 0, 7, 0x10, 0, 0xFF, 0xFF], token);
                        continue;
                    }
                    if (!connected) throw new InvalidDataException("OBEX CONNECT required.");
                    if (opcode is 0x81 or 0xFF) // DISCONNECT / ABORT
                    {
                        if (file is not null) await file.DisposeAsync();
                        file = null; name = null; length = null; ended = false;
                        try { SessionEnded?.Invoke(session); } catch { }
                        await ReplyAsync(output, [0xA0, 0, 3], token);
                        if (opcode == 0x81) break;
                        continue;
                    }
                    if (opcode is not (0x02 or 0x82))
                    {
                        await ReplyAsync(output, [0xD1, 0, 3], token);
                        continue;
                    }
                    var headers = ParseHeaders(packet, 3);
                    foreach (var h in headers)
                    {
                        if (h.Id == 0x01)
                        {
                            var nextName = DecodeName(h.Data);
                            InboxStore.ValidateName(nextName);
                            if (name is not null && name != nextName)
                                throw new InvalidDataException("File name changed during transfer.");
                            name = nextName;
                        }
                        if (h.Id == 0xC3)
                        {
                            var nextLength = (long)BinaryPrimitives.ReadUInt32BigEndian(h.Data);
                            if (file is not null && length != nextLength)
                                throw new InvalidDataException("File length changed during transfer.");
                            if (length.HasValue && length != nextLength)
                                throw new InvalidDataException("Conflicting length headers.");
                            length = nextLength;
                        }
                    }
                    if (length > options.MaximumFileBytes) throw new InvalidDataException("File is too large.");
                    bool hasBody = headers.Any(h => h.Id is 0x48 or 0x49);
                    if (hasBody || opcode == 0x82)
                    {
                        if (name is null) throw new InvalidDataException("File name is missing.");
                        if (file is null)
                        {
                            var offer = new TransferOffer(session, name, "Bluetooth", sender, length);
                            if (!approved)
                            {
                                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                                wait.CancelAfter(options.ConsentTimeout);
                                try { approved = await consent(offer, wait.Token).WaitAsync(wait.Token); }
                                catch (OperationCanceledException) when (!token.IsCancellationRequested) { approved = false; }
                                if (!approved)
                                {
                                    await ReplyAsync(output, [0xC3, 0, 3], token);
                                    break;
                                }
                            }
                            file = store.Begin(offer, options.MaximumFileBytes);
                            try { Started?.Invoke(offer); } catch { }
                        }
                        foreach (var h in headers.Where(h => h.Id is 0x48 or 0x49))
                        {
                            if (ended) throw new InvalidDataException("Body after End-of-Body.");
                            await file.WriteAsync(h.Data, token);
                            if (h.Id == 0x49) ended = true;
                        }
                        try { Progress?.Invoke(new TransferProgress(session, name, file.Received, length)); } catch { }
                    }
                    if (opcode == 0x82)
                    {
                        if (file is null || (!ended && length != 0))
                            throw new InvalidDataException("Final PUT lacks End-of-Body.");
                        try { Processing?.Invoke(new TransferProgress(session, name!, file.Received, length)); } catch { }
                        await file.CommitAsync(token);
                        await file.DisposeAsync();
                        file = null; name = null; length = null; ended = false;
                    }
                    await ReplyAsync(output, opcode == 0x82 ? [0xA0, 0, 3] : [0x90, 0, 3], token);
                }
                catch (InvalidDataException)
                {
                    await ReplyAsync(output, [0xC0, 0, 3], token);
                    throw;
                }
                catch (IOException)
                {
                    await ReplyAsync(output, [0xD3, 0, 3], token);
                    throw;
                }
            }
        }
        finally
        {
            if (file is not null) await file.DisposeAsync();
            try { SessionEnded?.Invoke(session); } catch { }
        }
    }

    private static async Task<byte[]?> ReadPacketAsync(Stream input, CancellationToken token)
    {
        var header = new byte[3];
        var first = await input.ReadAsync(header.AsMemory(0, 1), token);
        if (first == 0) return null;
        await input.ReadExactlyAsync(header.AsMemory(1, 2), token);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1, 2));
        if (length < 3) throw new InvalidDataException("Invalid OBEX packet length.");
        var packet = new byte[length];
        header.CopyTo(packet, 0);
        await input.ReadExactlyAsync(packet.AsMemory(3), token);
        return packet;
    }

    private static async Task ReplyAsync(Stream output, byte[] packet, CancellationToken token)
    {
        await output.WriteAsync(packet, token);
        await output.FlushAsync(token);
    }

    private sealed record Header(byte Id, byte[] Data);
    private static List<Header> ParseHeaders(byte[] packet, int start)
    {
        var result = new List<Header>();
        for (int i = start; i < packet.Length;)
        {
            byte id = packet[i];
            int kind = id >> 6;
            int total;
            int prefix;
            if (kind < 2)
            {
                if (packet.Length - i < 3) throw new InvalidDataException("Truncated OBEX header.");
                total = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(i + 1, 2));
                prefix = 3;
                if (total < 3) throw new InvalidDataException("Invalid OBEX header size.");
            }
            else { prefix = 1; total = kind == 2 ? 2 : 5; }
            if (total > packet.Length - i) throw new InvalidDataException("OBEX header outside packet.");
            result.Add(new Header(id, packet.AsSpan(i + prefix, total - prefix).ToArray()));
            i += total;
        }
        return result;
    }

    private static string DecodeName(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes.Length % 2 != 0 || bytes[^1] != 0 || bytes[^2] != 0)
            throw new InvalidDataException("Invalid Unicode OBEX name.");
        try { return new UnicodeEncoding(true, false, true).GetString(bytes, 0, bytes.Length - 2); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Invalid Unicode OBEX name."); }
    }
}
