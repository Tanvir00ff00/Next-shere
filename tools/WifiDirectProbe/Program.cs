using System.Net.NetworkInformation;
using System.Text.Json;
using Windows.Devices.WiFiDirect;
using Windows.Security.Credentials;

var publisher = new WiFiDirectAdvertisementPublisher();
var listener = new WiFiDirectConnectionListener();
var devices = new List<WiFiDirectDevice>();
var events = new List<object>();
listener.ConnectionRequested += async (_, args) =>
{
    using var request = args.GetConnectionRequest();
    try { var device = await WiFiDirectDevice.FromIdAsync(request.DeviceInformation.Id); if(device != null) devices.Add(device); }
    catch(Exception e) { Console.WriteLine(e.Message); }
};
publisher.Advertisement.IsAutonomousGroupOwnerEnabled = true;
publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;
publisher.Advertisement.LegacySettings.IsEnabled = true;
publisher.Advertisement.LegacySettings.Ssid = "DIRECT-NS-PROBE";
publisher.Advertisement.LegacySettings.Passphrase = new PasswordCredential { Password = "NextShareProbe1234" };
publisher.StatusChanged += (_, e) => { events.Add(new { status=e.Status.ToString(), error=e.Error.ToString() }); Console.WriteLine($"{e.Status} / {e.Error}"); };
try
{
    publisher.Start(); await Task.Delay(6000);
    var network = NetworkInterface.GetAllNetworkInterfaces().Select(n => new { n.Name, n.Description, status=n.OperationalStatus.ToString(), addresses=n.GetIPProperties().UnicastAddresses.Select(a=>a.Address.ToString()).ToArray() }).ToArray();
    Directory.CreateDirectory("artifacts");
    await File.WriteAllTextAsync("artifacts/wifi-direct-probe.json",JsonSerializer.Serialize(new { status=publisher.Status.ToString(), events, network, InternetSharingConfigured=false },new JsonSerializerOptions { WriteIndented=true }));
    Console.WriteLine(publisher.Status);
}
finally { publisher.Stop(); foreach(var device in devices) device.Dispose(); }
