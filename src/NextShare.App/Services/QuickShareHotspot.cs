using System.Collections.Concurrent;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Windows.Devices.WiFiDirect;
using NextShare.Platform;
using Windows.Security.Credentials;

namespace NextShare.App.Services;

internal sealed record QuickShareHotspotCredentials(string Ssid, string Password, string Gateway, int Frequency);

internal sealed class QuickShareHotspot
{
    private readonly SemaphoreSlim gate = new(1);
    private WiFiDirectAdvertisementPublisher? publisher;
    private WiFiDirectConnectionListener? listener;
    private QuickShareHotspotCredentials? credentials;
    private readonly ConcurrentDictionary<string, WiFiDirectDevice> devices = new();
    public event Action<string>? Activity;
    public async Task<QuickShareHotspotCredentials> EnsureStartedAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (publisher?.Status == WiFiDirectAdvertisementPublisherStatus.Started && credentials is not null) return credentials;
            // Wi-Fi Direct GO is local transport. Never enable Windows Mobile Hotspot / Internet Sharing.
            await OfflineGuardClient.VerifyAsync(token);
            string ssid = "DIRECT-NS-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
            publisher = new WiFiDirectAdvertisementPublisher();
            listener = new WiFiDirectConnectionListener(); listener.ConnectionRequested += ConnectionRequested;
            publisher.Advertisement.IsAutonomousGroupOwnerEnabled = true;
            publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;
            publisher.Advertisement.LegacySettings.IsEnabled = true;
            publisher.Advertisement.LegacySettings.Ssid = ssid;
            publisher.Advertisement.LegacySettings.Passphrase = new PasswordCredential { Password = password };
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            publisher.StatusChanged += (_, args) =>
            {
                if (args.Status == WiFiDirectAdvertisementPublisherStatus.Started) started.TrySetResult();
                if (args.Status == WiFiDirectAdvertisementPublisherStatus.Aborted) started.TrySetException(new IOException("Wi-Fi Direct: " + args.Error));
            };
            publisher.Start();
            if (publisher.Status == WiFiDirectAdvertisementPublisherStatus.Started) started.TrySetResult();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(12), token);
            for (int attempt = 0; attempt < 30; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var address = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."));
                if (address is not null)
                {
                    await OfflineGuardClient.VerifyAsync(token);
                    credentials = new(ssid, password, address.ToString(), 0);
                    Activity?.Invoke("Quick Share local Wi-Fi Direct hotspot started; Internet Sharing remains disabled.");
                    return credentials;
                }
                await Task.Delay(200, token);
            }
            throw new IOException("Wi-Fi Direct gateway not found");
        }
        catch { StopCore(); throw; }
        finally { gate.Release(); }
    }

    private async void ConnectionRequested(WiFiDirectConnectionListener sender, WiFiDirectConnectionRequestedEventArgs args)
    {
        using var request = args.GetConnectionRequest();
        try
        {
            if (publisher?.Status != WiFiDirectAdvertisementPublisherStatus.Started || devices.Count >= 4) return;
            var device = await WiFiDirectDevice.FromIdAsync(request.DeviceInformation.Id);
            if (device is null) return;
            if (publisher?.Status != WiFiDirectAdvertisementPublisherStatus.Started || !devices.TryAdd(device.DeviceId, device)) device.Dispose();
            else device.ConnectionStatusChanged += (_, _) =>
            {
                if (device.ConnectionStatus == WiFiDirectConnectionStatus.Disconnected && devices.TryRemove(device.DeviceId, out var disconnected)) disconnected.Dispose();
            };
        }
        catch (Exception e) { Activity?.Invoke("Quick Share hotspot connection: " + e.Message); }
    }
    public async Task StopAsync() { await gate.WaitAsync(); try { StopCore(); } finally { gate.Release(); } }
    private void StopCore()
    {
        if (listener is not null) listener.ConnectionRequested -= ConnectionRequested;
        try { publisher?.Stop(); } catch { }
        foreach (var device in devices.Values) device.Dispose(); devices.Clear();
        publisher = null; listener = null; credentials = null;
    }
}
