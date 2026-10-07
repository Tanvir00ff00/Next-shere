using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using NextShare.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;

namespace NextShare.App.Services;

internal sealed class BluetoothReceiverService(InboxStore store)
{
    private RfcommServiceProvider? provider;
    private StreamSocketListener? listener;
    private CancellationTokenSource? cancellation;
    private BluetoothVisibility? visibility;
    private readonly ConcurrentDictionary<long, StreamSocket> sockets = new();
    private readonly ConcurrentDictionary<long, Task> connections = new();
    private readonly SemaphoreSlim slots = new(4);
    private long connectionId;
    private int active;
    public string State { get; private set; } = "Stopped";
    public string Detail { get; private set; } = "Receiver stopped";
    public string DeviceName { get; private set; } = Environment.MachineName;
    public bool? ExtendedAdvertisingSupported { get; private set; }
    public bool HasActiveTransfers => Volatile.Read(ref active) > 0;
    public event Action? Changed;
    public event Action<string>? Activity;
    public event Action<TransferProgress>? Progress;
    public event Action<TransferOffer>? Started;
    public event Action<TransferProgress>? Processing;
    public event Action<string>? SessionEnded;
    public Func<TransferOffer, CancellationToken, Task<bool>> Consent { get; set; } = (_, _) => Task.FromResult(false);

    private void SetState(string state, string detail)
    {
        State = state; Detail = detail;
        Changed?.Invoke();
    }

    public async Task StartAsync(string serviceName)
    {
        if (provider is not null) return;
        SetState("Starting", "Starting Bluetooth service…");
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (adapter is null || !adapter.IsClassicSupported)
            { SetState("Unsupported", "No Bluetooth Classic adapter found"); return; }
            var radio = await adapter.GetRadioAsync();
            if (radio.State != Windows.Devices.Radios.RadioState.On)
            { SetState("NeedsSetup", "Turn on Bluetooth in Windows settings"); return; }
            ExtendedAdvertisingSupported = adapter.IsExtendedAdvertisingSupported;
            cancellation = new CancellationTokenSource();
            provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.ObexObjectPush).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10));
            if (provider is null) throw new InvalidOperationException("Windows did not grant an Object Push provider.");
            listener = new StreamSocketListener();
            listener.ConnectionReceived += ConnectionReceived;
            await listener.BindServiceNameAsync(provider.ServiceId.AsString(),
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            var name = Encoding.UTF8.GetBytes(serviceName);
            if (name.Length > 240) throw new InvalidOperationException("Receiver name is too long.");
            provider.SdpRawAttributes[0x0100] = new byte[] { 0x25, (byte)name.Length }.Concat(name).ToArray().AsBuffer();
            provider.SdpRawAttributes[0x0303] = new byte[] { 0x35, 0x02, 0x08, 0xFF }.AsBuffer();
            provider.SdpRawAttributes[0x0009] = new byte[] { 0x35, 0x08, 0x35, 0x06, 0x19, 0x11, 0x05, 0x09, 0x01, 0x00 }.AsBuffer();
            provider.StartAdvertising(listener, true);
            visibility = new BluetoothVisibility();
            visibility.Enable();
            DeviceName = visibility.Name;
            SetState("Advertising", visibility.Discoverable ? "Object Push service is running" :
                "Service is running; open Windows Bluetooth settings for discovery");
            Activity?.Invoke("Bluetooth OBEX receiver started.");
        }
        catch (Exception ex)
        {
            await StopAsync();
            SetState("Faulted", "Receiver failed to start: " + ex.Message);
            Activity?.Invoke("Bluetooth start failed: " + ex.Message);
        }
    }

    private void ConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        var token = cancellation?.Token ?? new CancellationToken(true);
        var id = Interlocked.Increment(ref connectionId);
        sockets[id] = args.Socket;
        var task = HandleAsync(id, args.Socket, token);
        connections[id] = task;
        _ = task.ContinueWith(_ => connections.TryRemove(id, out var removed), TaskScheduler.Default);
    }

    private async Task HandleAsync(long id, StreamSocket socket, CancellationToken token)
    {
        bool acquired = false;
        try
        {
            acquired = await slots.WaitAsync(0, token);
            if (!acquired) { Activity?.Invoke("Bluetooth receiver busy; connection closed."); return; }
            Interlocked.Increment(ref active);
            SetState("Receiving", "Device connected; waiting for a file offer");
            var receiver = new ObexReceiver(store, new ReceiverOptions());
            receiver.Progress += p => Progress?.Invoke(p);
            receiver.Started += p => Started?.Invoke(p);
            receiver.Processing += p => Processing?.Invoke(p);
            receiver.SessionEnded += id => SessionEnded?.Invoke(id);
            using var input = socket.InputStream.AsStreamForRead();
            using var output = socket.OutputStream.AsStreamForWrite();
            string senderName = "Bluetooth device";
            try
            {
                using var device = await BluetoothDevice.FromHostNameAsync(socket.Information.RemoteHostName).AsTask().WaitAsync(TimeSpan.FromSeconds(2), token);
                if (device is not null && !string.IsNullOrWhiteSpace(device.Name)) senderName = device.Name;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested) { }
            await receiver.RunAsync(input, output, senderName, Consent, token);
        }
        catch (OperationCanceledException) { Activity?.Invoke("Bluetooth connection closed or timed out."); }
        catch (Exception ex) { Activity?.Invoke("Bluetooth transfer incomplete: " + ex.Message); }
        finally
        {
            socket.Dispose(); sockets.TryRemove(id, out _);
            if (acquired) { Interlocked.Decrement(ref active); slots.Release(); }
            if (!token.IsCancellationRequested && Volatile.Read(ref active) == 0)
                SetState("Advertising", "Waiting for the next Bluetooth transfer");
        }
    }

    public async Task StopAsync()
    {
        cancellation?.Cancel();
        try { provider?.StopAdvertising(); } catch { }
        provider = null;
        listener?.Dispose(); listener = null;
        foreach (var socket in sockets.Values) socket.Dispose();
        try { await Task.WhenAll(connections.Values).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        visibility?.Dispose(); visibility = null;
        cancellation?.Dispose(); cancellation = null;
        SetState("Stopped", "Receiver stopped");
    }
}
