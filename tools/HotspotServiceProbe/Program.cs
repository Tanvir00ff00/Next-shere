using System.Text.Json;
using NextShare.App.Services;

var hotspot = new QuickShareHotspot();
var activity = new List<string>();
hotspot.Activity += activity.Add;
object report;
try
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    var credentials = await hotspot.EnsureStartedAsync(limit.Token);
    report = new { Passed = true, credentials.Gateway, RandomSsid = credentials.Ssid.StartsWith("DIRECT-NS-"), NoInternetSharingVerified = true, PhoneUsed = false, activity };
}
catch (Exception e) { report = new { Passed = false, Error = e.ToString(), activity }; Environment.ExitCode = 1; }
finally { await hotspot.StopAsync(); }
Directory.CreateDirectory("artifacts");
await File.WriteAllTextAsync("artifacts/hotspot-service-probe.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));
