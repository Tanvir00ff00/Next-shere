using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Bluetooth.Advertisement;

var adapter = await BluetoothAdapter.GetDefaultAsync();
var results = new List<object>();
Console.WriteLine(JsonSerializer.Serialize(new { adapter.IsPeripheralRoleSupported, adapter.IsAdvertisementOffloadSupported, adapter.IsExtendedAdvertisingSupported, adapter.MaxAdvertisementDataLength }));
foreach (bool withData in new[] { false, true })
{
    var created = await GattServiceProvider.CreateAsync(Guid.Parse("0000fef3-0000-1000-8000-00805f9b34fb"));
    if (created.Error != BluetoothError.Success) { results.Add(new { withData, createError = created.Error.ToString() }); continue; }
    var provider = created.ServiceProvider;
    var characteristic = await provider.Service.CreateCharacteristicAsync(Guid.Parse("00000000-0000-3000-8000-000000000000"), new GattLocalCharacteristicParameters { CharacteristicProperties = GattCharacteristicProperties.Read, StaticValue = new byte[] { 1 }.AsBuffer(), ReadProtectionLevel = GattProtectionLevel.Plain });
    var statuses = new List<object>();
    provider.AdvertisementStatusChanged += (_, e) => { lock(statuses) statuses.Add(new { status = e.Status.ToString(), error = e.Error.ToString() }); Console.WriteLine($"GATT data={withData}: {e.Status} / {e.Error}"); };
    var parameters = new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true };
    if (withData) parameters.ServiceData = new byte[] { 0x41,1,2,3,4,5,6,7,8,9,10,11,12,13,14 }.AsBuffer();
    provider.StartAdvertising(parameters);
    await Task.Delay(4000);
    results.Add(new { withData, characteristic = characteristic.Error.ToString(), final = provider.AdvertisementStatus.ToString(), statuses = statuses.ToArray() });
    provider.StopAdvertising(); await Task.Delay(500);
}
var advertisement = new BluetoothLEAdvertisement();
advertisement.DataSections.Add(new BluetoothLEAdvertisementDataSection(0x16, new byte[] { 0xF3,0xFE,0x41,1,2,3,4,5,6,7,8,9,10,11,12,13,14 }.AsBuffer()));
var publisher = new BluetoothLEAdvertisementPublisher(advertisement);
publisher.StatusChanged += (_, e) => Console.WriteLine($"Raw: {e.Status} / {e.Error}");
publisher.Start(); await Task.Delay(4000);
results.Add(new { rawPublisher = publisher.Status.ToString() }); publisher.Stop();
Directory.CreateDirectory("artifacts");
await File.WriteAllTextAsync("artifacts/gatt-probe.json",JsonSerializer.Serialize(new { adapter.IsPeripheralRoleSupported, adapter.IsAdvertisementOffloadSupported, adapter.IsExtendedAdvertisingSupported, adapter.MaxAdvertisementDataLength, results }, new JsonSerializerOptions { WriteIndented=true }));
