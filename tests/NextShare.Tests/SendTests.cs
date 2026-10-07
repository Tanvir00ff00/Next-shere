using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NextShare.Core;

internal static class SendTests
{
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    public static async Task RunAsync(Func<string,Func<string,Task>,Task> test)
    {
        await test("OBEX sender streams Bengali, large and empty files with receiver acknowledgements",async root=>
        {
            var source=Path.Combine(root,"Source");Directory.CreateDirectory(source);var files=new List<OutgoingFile>();
            foreach(var pair in new[]{("ছবি.bin",RandomNumberGenerator.GetBytes(230123)),("empty.txt",Array.Empty<byte>()),("document.txt",Encoding.UTF8.GetBytes("বাংলা original quality"))})
            {string path=Path.Combine(source,pair.Item1);await File.WriteAllBytesAsync(path,pair.Item2);files.Add(OutgoingFile.Inspect(path));}
            var store=new InboxStore(Path.Combine(root,"Inbox"),Path.Combine(root,"Metadata"));long progress=0;
            await Pair(async stream=>await new ObexReceiver(store,new ReceiverOptions()).RunAsync(new ShortReads(stream),stream,"sender",(_,_)=>Task.FromResult(true),CancellationToken.None),
                async stream=>await new ObexSender().SendAsync(new ShortReads(stream),stream,files,n=>{Check(n>=progress,"Progress went backwards");progress=n;},CancellationToken.None));
            var records=store.Load();Check(records.Count==3&&progress==files.Sum(f=>f.Length),"Batch/progress mismatch");
            foreach(var file in files)
            {var record=records.Single(r=>r.Name==file.Name);Check(SHA256.HashData(await File.ReadAllBytesAsync(file.Path)).SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(record.FilePath))),"Original bytes changed");}
        });
        await test("OBEX sender respects a 255-byte packet and repeats connection ID",async root=>
        {
            var path=Path.Combine(root,"a.bin");await File.WriteAllBytesAsync(path,new byte[1047]);var file=OutgoingFile.Inspect(path);int packets=0,body=0;
            await Pair(async stream=>
            {
                Check((await Packet(stream))[0]==0x80,"Missing CONNECT");await stream.WriteAsync(new byte[]{0xA0,0,12,0x10,0,0,255,0xCB,1,2,3,4});
                while(true)
                {
                    var packet=await Packet(stream);Check(packet.Length<=255,"Exceeded negotiated packet size");Check(packet.AsSpan(3,5).SequenceEqual(new byte[]{0xCB,1,2,3,4}),"Connection ID missing");
                    if(packet[0]==0x81){await stream.WriteAsync(new byte[]{0xA0,0,3});break;}
                    packets++;if(packet[8] is 0x48 or 0x49)body+=packet.Length-11;
                    await stream.WriteAsync(new byte[]{packet[0]==0x82?(byte)0xA0:(byte)0x90,0,3});
                }
            },stream=>new ObexSender().SendAsync(stream,stream,[file],null,CancellationToken.None));
            Check(packets>4&&body==1047,"File chunking incorrect");
        });
        await test("OBEX sender surfaces refusal and leaves no committed file",async root=>
        {
            var path=Path.Combine(root,"a.txt");await File.WriteAllTextAsync(path,"hi");bool refused=false;
            var store=new InboxStore(Path.Combine(root,"Inbox"),Path.Combine(root,"Meta"));
            await Pair(async stream=>await new ObexReceiver(store,new ReceiverOptions()).RunAsync(stream,stream,"sender",(_,_)=>Task.FromResult(false),CancellationToken.None),
                async stream=>{try{await new ObexSender().SendAsync(stream,stream,[OutgoingFile.Inspect(path)],null,CancellationToken.None);}catch(IOException e){refused=e.Message.Contains("declined");}});
            Check(refused&&store.Load().Count==0,"Refused file reported as success");
        });
        await test("Outgoing source mutation is rejected before any transport bytes",async root=>
        {
            string path=Path.Combine(root,"a.txt");await File.WriteAllTextAsync(path,"before");var file=OutgoingFile.Inspect(path);await File.WriteAllTextAsync(path,"after mutation");
            using var input=new MemoryStream();using var output=new MemoryStream();bool rejected=false;
            try{await new ObexSender().SendAsync(input,output,[file],null,CancellationToken.None);}catch(IOException){rejected=true;}
            Check(rejected&&output.Length==0,"Changed source was sent");
        });
        await test("OBEX waiting for an acknowledgement honours cancellation",async root=>
        {
            string path=Path.Combine(root,"a.txt");await File.WriteAllTextAsync(path,"a");bool cancelled=false;
            await Pair(async stream=>{await Packet(stream);await Task.Delay(180);},async stream=>
            {
                using var stop=new CancellationTokenSource(70);
                try{await new ObexSender().SendAsync(stream,stream,[OutgoingFile.Inspect(path)],null,stop.Token);}catch(OperationCanceledException){cancelled=true;}
            });Check(cancelled,"Sender ignored cancellation");
        });
        await test("OBEX malformed variable-length connection header is rejected",async root=>
        {
            string path=Path.Combine(root,"a");await File.WriteAllTextAsync(path,"a");using var input=new MemoryStream(new byte[]{0xA0,0,10,0x10,0,0,255,0x42,0,2});using var output=new MemoryStream();bool rejected=false;
            try{await new ObexSender().SendAsync(input,output,[OutgoingFile.Inspect(path)],null,CancellationToken.None);}catch(InvalidDataException){rejected=true;}Check(rejected,"Malformed header accepted");
        });
        await test("Weave client and server exchange fragmented bidirectional frames and ACK counters",root=>
        {
            var client=new WeaveSession();var server=new WeaveSession();var responses=server.Connect(WeaveSession.ClientRequest(97),97);
            foreach(var intro in client.AcceptConfirmation(responses[0],97))Check(server.Receive(intro) is null,"Client introduction exposed as data");
            Check(client.Receive(responses[1]) is null,"Server introduction exposed as data");
            for(int n=0;n<12;n++)
            {
                byte[] frame=new byte[2003+n*17];BinaryPrimitives.WriteInt32BigEndian(frame,frame.Length-4);RandomNumberGenerator.Fill(frame.AsSpan(4));
                byte[]? received=null;foreach(var packet in client.Send(frame))received=server.Receive(packet)??received;Check(frame.SequenceEqual(received!),"Client frame changed");
                foreach(var packet in server.Acknowledge(frame.Length))Check(client.Receive(packet) is null,"ACK exposed as data");
                received=null;foreach(var packet in server.Send(frame))received=client.Receive(packet)??received;Check(frame.SequenceEqual(received!),"Server frame changed");
                foreach(var packet in client.Acknowledge(frame.Length))Check(server.Receive(packet) is null,"Client ACK exposed as data");
            }
            return Task.CompletedTask;
        });
        await test("Weave client refuses invalid version and oversize confirmation",root=>
        {
            foreach(var packet in new byte[][]{[0x81,0,2,0,20],[0x81,0,1,2,0],[0x81,0,1,0,19],[0x80,0,1,0,20]})
            {bool rejected=false;try{new WeaveSession().AcceptConfirmation(packet,97).ToArray();}catch(InvalidDataException){rejected=true;}Check(rejected,"Invalid confirmation accepted");}
            return Task.CompletedTask;
        });
        await test("Quick Share advertising validates name boundaries, UTF-8 and visibility",root=>
        {
            byte[] name=Encoding.UTF8.GetBytes("Tanvir বাংলা");byte[] info=new byte[18+name.Length];info[0]=2;info[17]=(byte)name.Length;name.CopyTo(info,18);
            byte[] body=[0x23,0xFC,0x9F,0x5E,1,2,3,4,(byte)info.Length,..info,0,0,0,0,0,0,0,0,0,0,1];
            byte[] advert=new byte[body.Length+8];advert[0]=0x48;advert[1]=0xFC;advert[2]=0x9F;advert[3]=0x5E;BinaryPrimitives.WriteUInt32BigEndian(advert.AsSpan(4),(uint)body.Length);body.CopyTo(advert,8);
            var parsed=QuickShareAdvertisement.Parse(advert);Check(parsed?.Name=="Tanvir বাংলা"&&parsed.Endpoint=="01020304","Public advertisement not parsed");
            for(int size=0;size<advert.Length-11;size++)Check(QuickShareAdvertisement.Parse(advert.AsSpan(0,size)) is null,"Truncated advertisement accepted");
            var malformed=advert.ToArray();malformed[17]|=0x10;Check(QuickShareAdvertisement.Parse(malformed) is null,"Contacts-only advertisement exposed");
            malformed=advert.ToArray();malformed[34]=255;Check(QuickShareAdvertisement.Parse(malformed) is null,"Oversize name accepted");
            malformed=advert.ToArray();malformed[35]=0xFF;Check(QuickShareAdvertisement.Parse(malformed) is null,"Invalid UTF-8 accepted");return Task.CompletedTask;
        });
    }
    private static async Task Pair(Func<NetworkStream,Task> server,Func<NetworkStream,Task> client)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        using var sender=new TcpClient();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var accept=listener.AcceptTcpClientAsync(stop.Token);await sender.ConnectAsync((IPEndPoint)listener.LocalEndpoint,stop.Token);using var receiver=await accept;
            await Task.WhenAll(server(receiver.GetStream()),client(sender.GetStream())).WaitAsync(stop.Token);
        }
        finally{listener.Stop();}
    }
    private static async Task<byte[]> Packet(Stream stream)
    {
        byte[] prefix=new byte[3];await stream.ReadExactlyAsync(prefix);int length=BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(1));
        if(length<3)throw new InvalidDataException();byte[] packet=new byte[length];prefix.CopyTo(packet,0);await stream.ReadExactlyAsync(packet.AsMemory(3));return packet;
    }
    private sealed class ShortReads(Stream stream):Stream
    {
        public override bool CanRead=>true;public override bool CanWrite=>false;public override bool CanSeek=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)=>stream.ReadAsync(buffer[..Math.Min(buffer.Length,7)],token);
        public override int Read(byte[] b,int o,int n)=>stream.Read(b,o,Math.Min(n,7));public override void Flush()=>throw new NotSupportedException();
        public override long Seek(long n,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();
    }
}
