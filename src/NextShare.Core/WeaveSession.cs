using System.Buffers.Binary;

namespace NextShare.Core;

// Nearby Connections BLE weave framing, separate from encrypted payload processing.
public sealed class WeaveSession
{
    public const int MaximumFrame = 5 * 1024 * 1024;
    private readonly MemoryStream incoming = new();
    private bool assembling;
    private bool receivedData;
    private int? expectedCounter;
    private int sendCounter = 2;
    public bool Connected { get; private set; }
    public int PacketSize { get; private set; } = 20;
    public static byte[] ClientRequest(int maximumPacket) => maximumPacket is >= 20 and <= 509
        ? [0x80,0,1,0,1,(byte)(maximumPacket>>8),(byte)maximumPacket] : throw new InvalidDataException("Invalid client packet size");
    public IEnumerable<byte[]> AcceptConfirmation(byte[] packet, int maximumPacket)
    {
        if (Connected || packet.Length != 5 || (packet[0]&0x8F)!=0x81 || BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(1))!=1)
            throw new InvalidDataException("Invalid weave confirmation");
        int selected=BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(3));
        if (selected<20 || selected>maximumPacket || selected>509) throw new InvalidDataException("Invalid negotiated packet size");
        Connected=true; PacketSize=selected; expectedCounter=(((packet[0]>>4)&7)+1)&7; sendCounter=1;
        return Fragment([0,0,0,8,1,0x12,7,0x0A,3,0xFC,0x9F,0x5E,0x10,2]);
    }

    public IReadOnlyList<byte[]> Connect(byte[] packet, int maximumPacket)
    {
        if (Connected || packet.Length is < 7 or > 20 || !IsConnectionRequest(packet)) throw new InvalidDataException("Invalid weave connection request.");
        int minVersion = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(1));
        int maxVersion = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(3));
        if (minVersion > 1 || maxVersion < 1) throw new InvalidDataException("Unsupported weave version.");
        int proposed = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(5));
        if (proposed == 0) proposed = maximumPacket;
        if (proposed < 20 || maximumPacket < 20) throw new InvalidDataException("Invalid weave packet size.");
        PacketSize = Math.Min(509, Math.Min(proposed, maximumPacket));
        Connected = true;
        expectedCounter = (((packet[0] >> 4) & 7) + 1) & 7;
        return [new byte[] { 0x81, 0, 1, (byte)(PacketSize >> 8), (byte)PacketSize },
            new byte[] { 0x1C, 0, 0, 0, 8, 1, 0x12, 7, 0x0A, 3, 0xFC, 0x9F, 0x5E, 0x10, 2 }];
    }

    public static bool IsConnectionRequest(byte[] packet) => packet.Length > 0 && (packet[0] & 0x8F) == 0x80;
    public static bool IsPeerError(byte[] packet) => packet.Length == 1 && (packet[0] & 0x8F) == 0x82;

    public IReadOnlyList<byte[]> RetryConnection(byte[] packet)
    {
        if (!Connected || receivedData) throw new InvalidDataException("Unexpected weave connection restart.");
        var proposal = new WeaveSession();
        var responses = proposal.Connect(packet, PacketSize);
        if (proposal.PacketSize != PacketSize) throw new InvalidDataException("Weave retry changed packet size.");
        expectedCounter = (((packet[0] >> 4) & 7) + 1) & 7;
        return responses;
    }

    public IEnumerable<byte[]> Acknowledge(int receivedSize)
    {
        if (!Connected || receivedSize < 1 || receivedSize > MaximumFrame + 4) throw new InvalidDataException("Invalid BLE acknowledgement size.");
        var size = new List<byte>(); uint value = (uint)receivedSize;
        do { size.Add((byte)((value & 0x7Fu) | (value > 0x7F ? 0x80u : 0u))); value >>= 7; } while (value != 0);
        // SocketControlFrame: type=PACKET_ACKNOWLEDGEMENT, service_id_hash, received_size.
        byte[] message = [0,0,0,8,3,0x22,(byte)(6+size.Count),0x0A,3,0xFC,0x9F,0x5E,0x10,..size];
        return Fragment(message);
    }

    // Returns a complete Nearby length-prefixed frame, or null for a partial/control packet.
    public byte[]? Receive(byte[] packet)
    {
        if (!Connected || packet.Length < 1 || packet.Length > PacketSize) throw new InvalidDataException("Invalid weave packet.");
        int header = packet[0];
        if ((header & 0x80) != 0) throw new IOException("Weave peer closed or restarted.");
        int counter = (header >> 4) & 7;
        if (expectedCounter.HasValue && counter != expectedCounter) throw new InvalidDataException("Weave packets out of order.");
        expectedCounter = (counter + 1) & 7;
        receivedData = true;
        if ((header & 3) != 0) throw new InvalidDataException("Invalid weave reserved bits.");
        bool first = (header & 8) != 0, last = (header & 4) != 0;
        if (first == assembling) throw new InvalidDataException("Invalid weave fragment boundary.");
        if (first) { incoming.SetLength(0); assembling = true; }
        if (incoming.Length + packet.Length - 1 > MaximumFrame + 7) throw new InvalidDataException("Weave frame too large.");
        incoming.Write(packet, 1, packet.Length - 1);
        if (!last) return null;
        assembling = false;
        byte[] message = incoming.ToArray(); incoming.SetLength(0);
        if (message.Length < 5) throw new InvalidDataException("Short weave message.");
        if (message[0] == 0 && message[1] == 0 && message[2] == 0)
        {
            if (message.Length > 1024) throw new InvalidDataException("BLE socket control too large.");
            var fields = ReadFields(message.AsSpan(3));
            int type = fields.Numbers.GetValueOrDefault(1);
            if (type is < 1 or > 3 || !fields.Bytes.TryGetValue(type + 1, out var nested)) throw new InvalidDataException("Unknown BLE socket control.");
            var body = ReadFields(nested);
            if (!body.Bytes.TryGetValue(1, out var hash) || !hash.AsSpan().SequenceEqual(new byte[] {0xFC,0x9F,0x5E})) throw new InvalidDataException("Unknown BLE control service.");
            if (type == 2) throw new IOException("BLE socket disconnected by peer.");
            if (type == 1 && body.Numbers.GetValueOrDefault(2) != 2) throw new InvalidDataException("Unsupported BLE socket version.");
            if (type == 3 && (!body.Numbers.TryGetValue(2, out int acknowledged) || acknowledged < 0 || acknowledged > MaximumFrame + 4)) throw new InvalidDataException("Invalid BLE acknowledgement.");
            return null;
        }
        if (message[0] != 0xFC || message[1] != 0x9F || message[2] != 0x5E || message.Length < 8)
            throw new InvalidDataException("Unknown BLE service.");
        int size = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(3));
        if (size < 1 || size > MaximumFrame || size != message.Length - 7) throw new InvalidDataException("Invalid Nearby frame size.");
        return message[3..];
    }

    public IEnumerable<byte[]> Send(byte[] frame)
    {
        if (!Connected || frame.Length < 5 || frame.Length > MaximumFrame + 4 ||
            BinaryPrimitives.ReadInt32BigEndian(frame) != frame.Length - 4) throw new InvalidDataException("Invalid outbound Nearby frame.");
        byte[] message = new byte[frame.Length + 3];
        message[0] = 0xFC; message[1] = 0x9F; message[2] = 0x5E;
        frame.CopyTo(message, 3);
        return Fragment(message);
    }

    private IEnumerable<byte[]> Fragment(byte[] message)
    {
        for (int offset = 0; offset < message.Length; offset += PacketSize - 1)
        {
            int size = Math.Min(PacketSize - 1, message.Length - offset);
            byte[] packet = new byte[size + 1];
            packet[0] = (byte)((sendCounter++ & 7) << 4 | (offset == 0 ? 8 : 0) | (offset + size == message.Length ? 4 : 0));
            Array.Copy(message, offset, packet, 1, size);
            yield return packet;
        }
    }

    private static (Dictionary<int,int> Numbers, Dictionary<int,byte[]> Bytes) ReadFields(ReadOnlySpan<byte> data)
    {
        var numbers = new Dictionary<int,int>(); var bytes = new Dictionary<int,byte[]>(); int offset = 0;
        uint Varint(ReadOnlySpan<byte> input, ref int position)
        {
            uint value = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if (position >= input.Length) throw new InvalidDataException("Truncated BLE control.");
                byte part = input[position++];
                if (shift == 28 && (part & 0xF0) != 0) throw new InvalidDataException("Oversized BLE control integer.");
                value |= (uint)(part & 0x7F) << shift;
                if ((part & 0x80) == 0) return value;
            }
            throw new InvalidDataException("Invalid BLE control integer.");
        }
        while (offset < data.Length)
        {
            uint tag = Varint(data, ref offset); int field = (int)(tag >> 3);
            if (field == 0) throw new InvalidDataException("Invalid BLE control tag.");
            switch (tag & 7)
            {
                case 0: numbers[field] = unchecked((int)Varint(data, ref offset)); break;
                case 2:
                    uint length = Varint(data, ref offset);
                    if (length > data.Length - offset) throw new InvalidDataException("Truncated BLE control field.");
                    bytes[field] = data.Slice(offset,(int)length).ToArray(); offset += (int)length; break;
                case 1: if (data.Length-offset < 8) throw new InvalidDataException("Truncated BLE field."); offset += 8; break;
                case 5: if (data.Length-offset < 4) throw new InvalidDataException("Truncated BLE field."); offset += 4; break;
                default: throw new InvalidDataException("Unsupported BLE control wire type.");
            }
        }
        return (numbers,bytes);
    }
}
