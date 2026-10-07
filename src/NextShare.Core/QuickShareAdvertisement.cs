using System.Buffers.Binary;
using System.Text;

namespace NextShare.Core;

public sealed record QuickShareAdvertisement(string Endpoint, string Name, int DeviceType)
{
    public static QuickShareAdvertisement? Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (bytes.Length < 8 || bytes[0] != 0x48 || !bytes.Slice(1,3).SequenceEqual(new byte[] {0xFC,0x9F,0x5E})) return null;
            uint length=BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(4,4));
            if (length<27 || length>4096 || length>bytes.Length-8) return null;
            var data=bytes.Slice(8,(int)length);
            if (data[0]!=0x23 || !data.Slice(1,3).SequenceEqual(new byte[]{0xFC,0x9F,0x5E})) return null;
            int size=data[8]; if (size<18 || size>data.Length-9) return null;
            var info=data.Slice(9,size); int nameSize=info[17];
            if ((info[0]&0x10)!=0 || nameSize==0 || nameSize>info.Length-18) return null;
            string name=new UTF8Encoding(false,true).GetString(info.Slice(18,nameSize));
            if (name.Any(char.IsControl)) return null;
            return new(Convert.ToHexString(data.Slice(4,4)),name,(info[0]>>1)&7);
        }
        catch (DecoderFallbackException) { return null; }
    }
}
