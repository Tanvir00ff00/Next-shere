using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using NextShare.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace NextShare.App.Services;

internal sealed class QuickShareGattService
{
    private GattServiceProvider? provider;
    private GattLocalCharacteristic? notify;
    private GattLocalCharacteristic? readCharacteristic, writeCharacteristic;
    private Windows.Foundation.TypedEventHandler<GattServiceProvider,GattServiceProviderAdvertisementStatusChangedEventArgs>? advertisementHandler;
    private CancellationTokenSource? stop;
    private byte[] advertisement = [];
    private int port;
    private BleNotificationPump<GattSubscribedClient>? notificationPump;
    private GattSubscribedClient[] subscriptions = [];
    private readonly ConcurrentDictionary<string, Peer> peers = new();
    private readonly object peerGate = new();
    private int metadataWarning;
    public string State { get; private set; } = "Stopped";
    public string Detail { get; private set; } = "Offline discovery stopped";
    public event Action? Changed;
    public event Action<string>? Activity;

    private void SetState(string state, string detail) { State = state; Detail = detail; Changed?.Invoke(); }
    public async Task StartAsync(int nativePort, byte[] slot, byte[] header)
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (adapter is null || !adapter.IsPeripheralRoleSupported)
            { SetState("Unsupported", "Bluetooth adapter does not support BLE peripheral mode"); return; }
            port = nativePort; advertisement = slot; stop = new(); metadataWarning=0;
            var created = await GattServiceProvider.CreateAsync(Guid.Parse("0000fef3-0000-1000-8000-00805f9b34fb")).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (created.Error != BluetoothError.Success) throw new IOException("GATT service: " + created.Error);
            provider = created.ServiceProvider;
            var read = await CreateCharacteristic(Guid.Parse("00000000-0000-3000-8000-000000000000"), GattCharacteristicProperties.Read);
            // Request/response serializes the handshake, avoiding reordered Windows write-command callbacks.
            var write = await CreateCharacteristic(Guid.Parse("00000100-0004-1000-8000-001a11000101"), GattCharacteristicProperties.Write);
            readCharacteristic = read; writeCharacteristic = write;
            notify = await CreateCharacteristic(Guid.Parse("00000100-0004-1000-8000-001a11000102"), GattCharacteristicProperties.Notify);
            var outgoing = notify;
            notificationPump = new BleNotificationPump<GattSubscribedClient>(
                // Retain subscription-event clients as in the working 0.3.1 receiver;
                // refresh them on subscription changes, and check capacity/state live.
                () => Volatile.Read(ref subscriptions).Select(c => new BleSubscription<GattSubscribedClient>(c.Session.DeviceId.Id, c, c.MaxNotificationSize, c.Session.SessionStatus == GattSessionStatus.Active)).ToArray(),
                (client, packet, token) => BleNotificationDelivery.SendAsync(
                    () => outgoing.NotifyValueAsync(packet.ToArray().AsBuffer(), client),
                    WaitForNotificationAsync,
                    result => result.Status switch
                    {
                        GattCommunicationStatus.Success => BleNotificationOutcome.Success,
                        GattCommunicationStatus.Unreachable => BleNotificationOutcome.Unreachable,
                        GattCommunicationStatus.ProtocolError => BleNotificationOutcome.ProtocolError,
                        GattCommunicationStatus.AccessDenied => BleNotificationOutcome.AccessDenied,
                        _ => BleNotificationOutcome.Unknown
                    },
                    result => result.BytesSent, result => result.ProtocolError,
                    packet.Length, token,
                    message => {if(Interlocked.Exchange(ref metadataWarning,1)==0)Activity?.Invoke("Quick Share BLE notify: "+message);}),
                message => Activity?.Invoke("Quick Share BLE notify: " + message));
            read.ReadRequested += ReadRequested;
            write.WriteRequested += WriteRequested;
            notify.SubscribedClientsChanged += SubscribedClientsChanged;
            advertisementHandler = (sender, e) =>
            {
                if (provider != sender) return;
                bool active = e.Status == GattServiceProviderAdvertisementStatus.Started;
                Activity?.Invoke("Quick Share BLE advertisement: " + e.Status + " / " + e.Error);
                SetState(active ? "Advertising" : "Faulted", active ? "BLE Quick Share discovery is running" : "BLE advertisement: " + e.Status + " / " + e.Error + " • Close Google Quick Share completely");
            };
            provider.AdvertisementStatusChanged += advertisementHandler;
            SetState("Starting", "Starting BLE discovery…");
            provider.StartAdvertising(new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true, ServiceData = header.AsBuffer() });
        }
        catch (Exception e) { await StopAsync(); SetState("Faulted", e.Message); Activity?.Invoke("Quick Share GATT: " + e.Message); }
    }

    private async Task<GattClientNotificationResult> WaitForNotificationAsync(
        Windows.Foundation.IAsyncOperation<GattClientNotificationResult> operation, CancellationToken token)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            return await operation.AsTask(deadline.Token);
        }
        catch (Exception error)
        {
            // Native ErrorCode exceptions have no managed call-site stack. Record the
            // actual operation state/HRESULT before the bridge wraps the exception.
            string state, nativeError;
            try { state = operation.Status.ToString(); }
            catch (Exception ex) { state = "unavailable (0x" + ex.HResult.ToString("X8") + ")"; }
            try { nativeError = operation.ErrorCode is { } ex ? "0x" + ex.HResult.ToString("X8") : "none"; }
            catch (Exception ex) { nativeError = "unavailable (0x" + ex.HResult.ToString("X8") + ")"; }
            Activity?.Invoke($"Quick Share BLE native notification: state={state} nativeError={nativeError} waitError=0x{error.HResult:X8}; no packet replay");
            throw;
        }
    }

    private async Task<GattLocalCharacteristic> CreateCharacteristic(Guid id, GattCharacteristicProperties properties)
    {
        var result = await provider!.Service.CreateCharacteristicAsync(id, new GattLocalCharacteristicParameters
        { CharacteristicProperties = properties, ReadProtectionLevel = GattProtectionLevel.Plain, WriteProtectionLevel = GattProtectionLevel.Plain });
        if (result.Error != BluetoothError.Success) throw new IOException("GATT characteristic: " + result.Error);
        return result.Characteristic;
    }

    private async void ReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request is null) return;
            if (request.Offset > advertisement.Length) { request.RespondWithProtocolError(0x07); return; }
            // Windows splits long values to fit the negotiated ATT MTU.
            request.RespondWithValue(advertisement[(int)request.Offset..].AsBuffer());
            Activity?.Invoke("Quick Share advertisement read by a phone.");
        }
        catch (Exception e) { Activity?.Invoke("Quick Share read: " + e.Message); }
        finally { deferral.Complete(); }
    }

    private void SubscribedClientsChanged(GattLocalCharacteristic sender, object args)
    {
        if (stop is null || stop.IsCancellationRequested) return;
        lock (peerGate)
        {
            var subscribed = sender.SubscribedClients.ToDictionary(c => c.Session.DeviceId.Id);
            Volatile.Write(ref subscriptions, subscribed.Values.ToArray());
            Activity?.Invoke($"Quick Share BLE subscriptions: {subscribed.Count}; " + string.Join(", ", subscribed.Values.Select(c => $"state={c.Session.SessionStatus} capacity={c.MaxNotificationSize} ATT={c.Session.MaxPduSize}")));
            foreach (var existing in peers.ToArray())
                if (!subscribed.ContainsKey(existing.Key)) { if (peers.TryRemove(existing.Key, out var old)) old.Stop(); }
            foreach (var client in subscribed)
            {
                if (peers.TryGetValue(client.Key, out var existing)) { existing.UpdateClient(client.Value); continue; }
                if (peers.Count >= 4) continue;
                var peer = new Peer(client.Key, client.Value, notificationPump!, port, stop.Token, Activity);
                if (peers.TryAdd(client.Key, peer))
                    _ = RunPeerAsync(client.Key, peer);
            }
        }
    }

    private async Task RunPeerAsync(string key, Peer peer)
    {
        await peer.RunAsync();
        lock (peerGate) { if (peers.TryGetValue(key, out var current) && current == peer) peers.TryRemove(key, out _); }
        peer.Stop();
    }

    private void WriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        Task<GattWriteRequest>? request = null;
        try
        {
            // Acquire the WinRT request while handling its event; queue the single operation, not expired event args.
            request = args.GetRequestAsync().AsTask();
            var key = args.Session.DeviceId.Id;
            if (!peers.ContainsKey(key) && notify is not null) SubscribedClientsChanged(notify, EventArgs.Empty);
            if (peers.TryGetValue(key, out var peer) && peer.Queue.Writer.TryWrite((request, deferral))) return;
            if (peer is not null) peer.Stop();
        }
        catch (Exception e) { Activity?.Invoke("Quick Share GATT write acquisition: " + e); }
        if (request is not null) _ = RejectAsync(request, deferral); else deferral.Complete();
    }
    private static async Task RejectAsync(Task<GattWriteRequest> operation, Windows.Foundation.Deferral deferral)
    {
        try
        {
            var request = await operation;
            if (request?.Option == GattWriteOption.WriteWithResponse) request.RespondWithProtocolError(0x11);
        }
        catch { }
        finally { deferral.Complete(); }
    }

    public Task StopAsync()
    {
        var oldProvider = provider; provider = null;
        if (oldProvider is not null && advertisementHandler is not null) oldProvider.AdvertisementStatusChanged -= advertisementHandler;
        advertisementHandler = null;
        if (readCharacteristic is not null) readCharacteristic.ReadRequested -= ReadRequested;
        if (writeCharacteristic is not null) writeCharacteristic.WriteRequested -= WriteRequested;
        stop?.Cancel();
        lock (peerGate) { Volatile.Write(ref subscriptions, []); foreach (var peer in peers.Values) peer.Stop(); peers.Clear(); }
        try { oldProvider?.StopAdvertising(); } catch { }
        if (notify is not null) notify.SubscribedClientsChanged -= SubscribedClientsChanged;
        provider = null; notify = null; notificationPump = null; readCharacteristic = null; writeCharacteristic = null; stop?.Dispose(); stop = null;
        SetState("Stopped", "Offline discovery stopped");
        return Task.CompletedTask;
    }

    private sealed class Peer
    {
        public Channel<(Task<GattWriteRequest> Request, Windows.Foundation.Deferral Deferral)> Queue { get; } = Channel.CreateBounded<(Task<GattWriteRequest>, Windows.Foundation.Deferral)>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private GattSubscribedClient client;
        private readonly BleNotificationPump<GattSubscribedClient> notificationPump;
        private readonly string clientKey;
        private readonly int port;
        private readonly CancellationTokenSource cancellation;
        private readonly Action<string>? activity;
        private readonly TcpClient socket = new();
        private readonly WeaveSession weave = new();
        private readonly SemaphoreSlim sends = new(1);
        private readonly string diagnosticId = Guid.NewGuid().ToString("N")[..8];
        private string stage = "waiting for connection request";
        private int packetsRead;
        private int stopped;
        public Peer(string key, GattSubscribedClient client, BleNotificationPump<GattSubscribedClient> pump, int port, CancellationToken token, Action<string>? activity)
        { clientKey = key; this.client = client; notificationPump = pump; this.port = port; this.activity = activity; cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); }
        public void UpdateClient(GattSubscribedClient current) => Volatile.Write(ref client, current);
        public void Stop()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            cancellation.Cancel(); socket.Dispose(); Queue.Writer.TryComplete();
            while (Queue.Reader.TryRead(out var pending)) _ = RejectAsync(pending.Request, pending.Deferral);
        }
        public async Task RunAsync()
        {
            try
            {
                var token = cancellation.Token;
                var handshake = await ReadPacketAsync(token).WaitAsync(TimeSpan.FromSeconds(15), token);
                if (WeaveSession.IsPeerError(handshake)) { activity?.Invoke($"Quick Share BLE {diagnosticId}: phone closed the previous handshake."); return; }
                activity?.Invoke($"Quick Share BLE {diagnosticId}: request={Convert.ToHexString(handshake.AsSpan(0,Math.Min(7,handshake.Length)))} bytes={handshake.Length}");
                // Respond/complete the ATT write before initiating a server notification.
                await Task.Delay(100, token);
                stage = "reading negotiated ATT size";
                int maximumPacket = await notificationPump.WaitForCapacityAsync(clientKey, token);
                var current = Volatile.Read(ref client);
                activity?.Invoke($"Quick Share BLE {diagnosticId}: session={current.Session.SessionStatus} notifyCapacity={current.MaxNotificationSize} ATT={current.Session.MaxPduSize} negotiatedLimit={maximumPacket}");
                stage = "confirming weave connection";
                foreach (var packet in weave.Connect(handshake, maximumPacket)) await NotifyAsync(packet, token);
                stage = "connecting to local protocol bridge";
                await socket.ConnectAsync("127.0.0.1", port, token);
                activity?.Invoke("Quick Share BLE connection opened.");
                Task incoming = ReceiveAsync(socket.GetStream(), token), outgoing = SendAsync(socket.GetStream(), token);
                await Task.WhenAny(incoming, outgoing);
                cancellation.Cancel(); socket.Dispose();
                await Task.WhenAll(incoming, outgoing);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { activity?.Invoke($"Quick Share BLE {diagnosticId} at {stage} (0x{e.HResult:X8}): {e}"); }
            finally { Stop(); cancellation.Dispose(); }
        }
        private async Task<byte[]> ReadPacketAsync(CancellationToken token)
        {
            var pending = await Queue.Reader.ReadAsync(token);
            try
            {
                var request = await pending.Request.WaitAsync(token);
                if (request is null) throw new IOException("GATT write expired");
                if (request.Offset != 0 || request.Value.Length is < 1 or > 509)
                { if (request.Option == GattWriteOption.WriteWithResponse) request.RespondWithProtocolError(0x0D); throw new InvalidDataException("Invalid GATT packet"); }
                byte[] packet = request.Value.ToArray();
                if (packetsRead++ < 4) activity?.Invoke($"Quick Share BLE {diagnosticId}: received header=0x{packet[0]:X2} bytes={packet.Length} write={request.Option}");
                if (request.Option == GattWriteOption.WriteWithResponse) request.Respond();
                return packet;
            }
            finally { pending.Deferral.Complete(); }
        }
        private async Task ReceiveAsync(NetworkStream stream, CancellationToken token)
        {
            while (true)
            {
                var packet = await ReadPacketAsync(token).WaitAsync(TimeSpan.FromSeconds(120), token);
                stage = "reading weave data/control";
                if (WeaveSession.IsConnectionRequest(packet))
                {
                    await sends.WaitAsync(token);
                    try { foreach (var confirmation in weave.RetryConnection(packet)) await NotifyAsync(confirmation,token); }
                    finally { sends.Release(); }
                    continue;
                }
                var frame = weave.Receive(packet);
                if (frame is not null)
                {
                    await sends.WaitAsync(token);
                    try { foreach (var ack in weave.Acknowledge(frame.Length)) await NotifyAsync(ack, token); }
                    finally { sends.Release(); }
                    await stream.WriteAsync(frame, token);
                }
            }
        }
        private async Task SendAsync(NetworkStream stream, CancellationToken token)
        {
            byte[] header = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(header, token);
                int size = BinaryPrimitives.ReadInt32BigEndian(header);
                if (size < 1 || size > WeaveSession.MaximumFrame) throw new InvalidDataException("Invalid Nearby frame.");
                byte[] frame = new byte[size + 4]; header.CopyTo(frame, 0);
                await stream.ReadExactlyAsync(frame.AsMemory(4), token);
                await sends.WaitAsync(token);
                try { foreach (var packet in weave.Send(frame)) await NotifyAsync(packet, token); }
                finally { sends.Release(); }
            }
        }
        private async Task NotifyAsync(byte[] packet, CancellationToken token)
        {
            await notificationPump.SendAsync(clientKey, packet, token);
            if ((packet[0] & 0x80) != 0) activity?.Invoke($"Quick Share BLE {diagnosticId}: confirm notification=Success bytes={packet.Length}");
        }
    }
}
