using System.Buffers.Binary;
using System.Text;

namespace NextShare.Core;

/// <summary>Object Push client over an authenticated RFCOMM connection.</summary>
public sealed class ObexSender
{
    public async Task SendAsync(Stream input, Stream output, IReadOnlyList<OutgoingFile> files,
        Action<long>? progress, CancellationToken token)
    {
        if (files.Count is < 1 or > 1000) throw new InvalidDataException("Select 1–1000 files.");
        foreach (var file in files) { file.ValidateUnchanged(); if (file.Length > uint.MaxValue) throw new IOException("Bluetooth files must be smaller than 4 GB."); }
        var reply = await Exchange([0x80,0,7,0x10,0,0xFF,0xFF], input, output, token);
        if (reply[0] != 0xA0 || reply.Length < 7) throw new IOException("Bluetooth connection refused.");
        int maximum = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(5));
        if (maximum < 255) throw new InvalidDataException("Invalid Bluetooth packet size.");
        byte[] connection = [];
        for (int p = 7; p < reply.Length;)
        {
            byte id = reply[p]; int length = (id >> 6) switch { 0 or 1 => p + 3 <= reply.Length ? BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(p+1)) : 0, 2 => 2, _ => 5 };
            if (length < ((id >> 6) <= 1 ? 3 : 2) || p + length > reply.Length) throw new InvalidDataException("Invalid OBEX response headers.");
            if (id == 0xCB) connection = reply[p..(p+5)];
            p += length;
        }
        long acknowledged = 0;
        foreach (var file in files)
        {
            file.ValidateUnchanged();
            await using var source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            byte[] name = Encoding.BigEndianUnicode.GetBytes(file.Name + "\0");
            byte[] headers = new byte[connection.Length + 3 + name.Length + 5]; connection.CopyTo(headers,0);
            int n = connection.Length; headers[n] = 1; BinaryPrimitives.WriteUInt16BigEndian(headers.AsSpan(n+1), checked((ushort)(name.Length+3))); name.CopyTo(headers,n+3);
            n += 3 + name.Length; headers[n]=0xC3; BinaryPrimitives.WriteUInt32BigEndian(headers.AsSpan(n+1), (uint)file.Length);
            if (headers.Length + 3 > maximum) throw new IOException("Filename too long for this Bluetooth device.");
            await Put(0x02, headers, 0x90);
            int capacity = maximum - 6 - connection.Length;
            byte[] buffer = new byte[capacity]; long remaining = file.Length;
            do
            {
                int count = (int)Math.Min(remaining, capacity);
                if (count > 0) await source.ReadExactlyAsync(buffer.AsMemory(0,count),token);
                remaining -= count;
                byte[] body = new byte[connection.Length+3+count]; connection.CopyTo(body,0);
                body[connection.Length]=(byte)(remaining==0?0x49:0x48);
                BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(connection.Length+1),checked((ushort)(count+3)));
                buffer.AsSpan(0,count).CopyTo(body.AsSpan(connection.Length+3));
                await Put((byte)(remaining==0?0x82:0x02),body,(byte)(remaining==0?0xA0:0x90));
                acknowledged += count; progress?.Invoke(acknowledged);
            } while (remaining > 0);
            file.ValidateUnchanged();
        }
        // File success is based on final PUT responses; disconnect failure is not a file failure.
        try { await Exchange(Packet(0x81,connection),input,output,token); } catch (Exception ex) when (ex is IOException && !token.IsCancellationRequested) { }

        async Task Put(byte opcode, byte[] body, byte expected)
        {
            var response = await Exchange(Packet(opcode,body),input,output,token);
            if (response[0] != expected) throw new IOException(response[0] == 0xC3 ? "The device declined this file." : $"Bluetooth device response: 0x{response[0]:X2}");
        }
    }
    private static byte[] Packet(byte opcode, byte[] body)
    {
        byte[] packet = new byte[body.Length+3]; packet[0]=opcode;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1),checked((ushort)packet.Length)); body.CopyTo(packet,3); return packet;
    }
    private static async Task<byte[]> Exchange(byte[] packet, Stream input, Stream output, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(60));
        await output.WriteAsync(packet,wait.Token); await output.FlushAsync(wait.Token);
        byte[] prefix = new byte[3]; await input.ReadExactlyAsync(prefix,wait.Token);
        int length = BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(1));
        if (length < 3) throw new InvalidDataException("Invalid Bluetooth response length.");
        byte[] response = new byte[length]; prefix.CopyTo(response,0); await input.ReadExactlyAsync(response.AsMemory(3),wait.Token); return response;
    }
}
