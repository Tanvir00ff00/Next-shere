using System.Text.Json;
using Windows.Devices.Bluetooth;

// Read-only capability probe. Does not advertise, pair, receive, rename, or change radios.
if (args.Length != 0 && (args.Length != 2 || args[0] != "--output"))
{
    Console.Error.WriteLine("Usage: NextShare.Diagnostics [--output report.json]");
    return 2;
}

var report = new Dictionary<string, object?>
{
    ["capturedAtUtc"] = DateTimeOffset.UtcNow,
    ["osVersion"] = Environment.OSVersion.VersionString,
    ["probeType"] = "read-only Bluetooth capabilities",
    ["transferTested"] = false
};

try
{
    var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask()
        .WaitAsync(TimeSpan.FromSeconds(10));
    if (adapter is null)
    {
        report["status"] = "NoDefaultBluetoothAdapter";
    }
    else
    {
        report["status"] = "CapabilitiesRead";
        report["classicSupported"] = adapter.IsClassicSupported;
        report["lowEnergySupported"] = adapter.IsLowEnergySupported;
        report["centralRoleSupported"] = adapter.IsCentralRoleSupported;
        report["peripheralRoleSupported"] = adapter.IsPeripheralRoleSupported;
        report["extendedAdvertisingSupported"] = adapter.IsExtendedAdvertisingSupported;
        report["advertisementOffloadSupported"] = adapter.IsAdvertisementOffloadSupported;
        report["maxAdvertisementDataLength"] = adapter.MaxAdvertisementDataLength;
        var radio = await adapter.GetRadioAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        report["radioState"] = radio.State.ToString();
    }
}
catch (Exception ex)
{
    report["status"] = "ProbeFailed";
    report["errorType"] = ex.GetType().Name;
    report["error"] = ex.Message;
}

var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
if (args.Length == 2)
{
    var path = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, json + Environment.NewLine);
}
return report["status"] as string == "ProbeFailed" ? 1 : 0;
