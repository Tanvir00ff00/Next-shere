using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using NextShare.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace NextShare.App.Services;

// Owns one GATT client connection and a private loopback bridge to the encrypted sender.
internal sealed class QuickShareBleClient : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime;
    private readonly TcpListener listener = new(IPAddress.Loopback,0);
    private BluetoothLEDevice? device;
    private GattDeviceService? service;
    private GattCharacteristic? read, write;
    private readonly Channel<byte[]> packets=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly SemaphoreSlim writes=new(1);
    private readonly WeaveSession weave=new();
    private Task? pump;
    public string Address => "127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port;
    private QuickShareBleClient(CancellationToken token) { lifetime=CancellationTokenSource.CreateLinkedTokenSource(token); }
    public static async Task<QuickShareBleClient> OpenAsync(string address, CancellationToken token)
    {
        var bridge=new QuickShareBleClient(token);
        try { await bridge.ConnectAsync(address,token); return bridge; }
        catch { await bridge.DisposeAsync(); throw; }
    }
    private async Task ConnectAsync(string address,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var parts=address.Split(':');
        device=await BluetoothLEDevice.FromBluetoothAddressAsync(ulong.Parse(parts[0]),parts.ElementAtOrDefault(1)=="random"?BluetoothAddressType.Random:BluetoothAddressType.Public).AsTask(deadline.Token)
            ?? throw new IOException("Quick Share device is unavailable.");
        var result=await device.GetGattServicesForUuidAsync(Guid.Parse("0000fef3-0000-1000-8000-00805f9b34fb"),BluetoothCacheMode.Uncached).AsTask(deadline.Token);
        if(result.Status!=GattCommunicationStatus.Success || result.Services.Count==0)throw new IOException("Quick Share connection failed. Keep the phone's Receive screen open.");
        service=result.Services[0];foreach(var extra in result.Services.Skip(1))extra.Dispose();
        write=await Find("00000100-0004-1000-8000-001a11000101");
        read=await Find("00000100-0004-1000-8000-001a11000102");
        read.ValueChanged+=OnValue;
        var subscribed=await read.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(deadline.Token);
        if(subscribed!=GattCommunicationStatus.Success)throw new IOException("Phone did not enable Quick Share notifications.");
        int maximum=Math.Clamp(service.Session.MaxPduSize-3,20,509);
        await WriteAsync(WeaveSession.ClientRequest(maximum),deadline.Token);
        var confirmation=await packets.Reader.ReadAsync(deadline.Token);
        await WritePacketsAsync(weave.AcceptConfirmation(confirmation,maximum),deadline.Token);
        listener.Start(1);pump=PumpAsync(lifetime.Token);

        async Task<GattCharacteristic> Find(string uuid)
        {
            var chars=await service.GetCharacteristicsForUuidAsync(Guid.Parse(uuid),BluetoothCacheMode.Uncached).AsTask(deadline.Token);
            if(chars.Status!=GattCommunicationStatus.Success || chars.Characteristics.Count==0)
                throw new IOException("This phone offers a different Quick Share Bluetooth transport. Put both devices on the same local Wi-Fi and refresh to use Quick Share over Wi-Fi.");
            return chars.Characteristics[0];
        }
    }
    private void OnValue(GattCharacteristic sender,GattValueChangedEventArgs args)
    {
        if(!packets.Writer.TryWrite(args.CharacteristicValue.ToArray()))
            packets.Writer.TryComplete(new IOException("Quick Share Bluetooth receive queue overflowed."));
    }
    private async Task WriteAsync(byte[] packet,CancellationToken token)
    {
        var option=write!.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)?GattWriteOption.WriteWithResponse:GattWriteOption.WriteWithoutResponse;
        var status=await write.WriteValueAsync(packet.AsBuffer(),option).AsTask(token).WaitAsync(TimeSpan.FromSeconds(15),token);
        if(status!=GattCommunicationStatus.Success)throw new IOException("Phone stopped accepting Quick Share packets: "+status);
    }
    private async Task WritePacketsAsync(IEnumerable<byte[]> fragments,CancellationToken token)
    {
        await writes.WaitAsync(token);
        try{foreach(var packet in fragments)await WriteAsync(packet,token);}finally{writes.Release();}
    }
    private async Task PumpAsync(CancellationToken token)
    {
        try
        {
            using var peer=await listener.AcceptTcpClientAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(15),token);
            using var stream=peer.GetStream();
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token);
            var incoming=ReceiveAsync(stream,linked.Token);var outgoing=TransmitAsync(stream,linked.Token);
            await Task.WhenAny(incoming,outgoing);linked.Cancel();peer.Close();
            // EOF on the loopback stream also occurs during a successful Wi-Fi upgrade.
            try{await Task.WhenAll(incoming,outgoing);}catch(OperationCanceledException)when(linked.IsCancellationRequested){}
        }
        catch(Exception)when(!token.IsCancellationRequested){listener.Stop();}
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
    }
    private async Task ReceiveAsync(NetworkStream stream,CancellationToken token)
    {
        await foreach(var packet in packets.Reader.ReadAllAsync(token))
        {
            var frame=weave.Receive(packet);
            if(frame is null)continue;
            await stream.WriteAsync(frame,token);
            await WritePacketsAsync(weave.Acknowledge(frame.Length),token);
        }
    }
    private async Task TransmitAsync(NetworkStream stream,CancellationToken token)
    {
        while(true)
        {
            byte[] prefix=new byte[4];int count=await stream.ReadAsync(prefix.AsMemory(0,1),token);if(count==0)return;
            await stream.ReadExactlyAsync(prefix.AsMemory(1),token);int size=BinaryPrimitives.ReadInt32BigEndian(prefix);
            if(size is <1 or >WeaveSession.MaximumFrame)throw new InvalidDataException("Invalid Quick Share frame.");
            byte[] frame=new byte[size+4];prefix.CopyTo(frame,0);await stream.ReadExactlyAsync(frame.AsMemory(4),token);
            await WritePacketsAsync(weave.Send(frame),token);
        }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();listener.Stop();if(read is not null)read.ValueChanged-=OnValue;
        service?.Dispose();device?.Dispose();packets.Writer.TryComplete();
        if(pump is not null){try{await pump;}catch{}}
        lifetime.Dispose();writes.Dispose();
    }
}
