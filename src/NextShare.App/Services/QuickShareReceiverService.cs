using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text;
using System.Threading.Channels;
using NextShare.Core;

namespace NextShare.App.Services;

internal sealed class QuickShareReceiverService(InboxStore store, string dataRoot)
{
    private Process? process;
    private CancellationTokenSource? stop;
    private Task? reader, imports, errors;
    private TaskCompletionSource<JsonElement>? ready;
    private readonly QuickShareGattService gatt = new();
    private readonly QuickShareHotspot hotspot = new();
    private readonly SemaphoreSlim commands = new(1);
    private Task? hotspotRequest;
    private readonly ConcurrentDictionary<string, byte> receiving = new();
    private readonly ConcurrentDictionary<string, long> progressTicks = new();
    private readonly string staging = Path.Combine(dataRoot, "QuickShareNative");
    public string State { get; private set; } = "Stopped";
    public string Detail { get; private set; } = "Quick Share stopped";
    public string OfflineState => gatt.State;
    public string OfflineDetail => gatt.Detail;
    public bool LocalWifiNeedsSetup { get; private set; }
    public string LocalWifiDetail { get; private set; } = "Local Wi-Fi starts when a transfer requests it";
    public int Port { get; private set; }
    public int BridgePort { get; private set; }
    public bool Running => process is { HasExited: false };
    public bool HasActiveTransfers => !receiving.IsEmpty;
    public event Action? Changed;
    public event Action<string>? Activity;
    public event Action<TransferProgress>? Progress;
    public event Action<TransferOffer, int>? Started;
    public event Action<string>? Processing;
    public event Action<string, bool>? SessionEnded;
    private void SetState(string state, string detail) { if (State == state && Detail == detail) return; State = state; Detail = detail; Changed?.Invoke(); }

    public async Task StartAsync(string name, bool useRadio)
    {
        if (Running) return;
        try
        {
            Directory.CreateDirectory(staging);
            LocalWifiNeedsSetup = false;
            LocalWifiDetail = "Local Wi-Fi starts when a transfer requests it";
            hotspot.Activity -= GattActivity; hotspot.Activity += GattActivity;
            string executable = Path.Combine(AppContext.BaseDirectory, "nextshare-quickshare.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Independent Quick Share backend not found", executable);
            stop = new(); ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var completions = Channel.CreateBounded<string>(new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = true });
            var importer = new QuickShareCompletionImporter(store, staging);
            imports = Task.Run(async () =>
            {
                foreach (var marker in Directory.EnumerateFiles(staging, "*.complete.json"))
                    try { await ImportCompletionAsync(importer, await File.ReadAllTextAsync(marker, stop.Token), stop.Token); }
                    catch (Exception e) when (e is not OperationCanceledException) { Activity?.Invoke("Quick Share recovery: " + e.Message); }
                await foreach (var json in completions.Reader.ReadAllAsync(stop.Token))
                    try { await ImportCompletionAsync(importer, json, stop.Token); }
                    catch (Exception e) when (e is not OperationCanceledException) { Activity?.Invoke("Quick Share save: " + e.Message); SetState("SaveFailed", "File could not be saved: " + e.Message); }
            });
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false) };
            info.ArgumentList.Add("--staging"); info.ArgumentList.Add(staging);
            info.ArgumentList.Add("--name"); info.ArgumentList.Add(name);
            if (!useRadio) info.ArgumentList.Add("--loopback-only");
            process = Process.Start(info) ?? throw new IOException("Quick Share backend start failed");
            var child = process; var token = stop.Token;
            reader = Task.Run(async () =>
            {
                try
                {
                    while (await child.StandardOutput.ReadLineAsync(token) is { } line)
                    {
                        if (line.Length > 1024 * 1024) throw new InvalidDataException("Quick Share event too large");
                        using var doc = JsonDocument.Parse(line);
                        var message = doc.RootElement;
                        switch (message.GetProperty("type").GetString())
                        {
                            case "listening": ready.TrySetResult(message.Clone()); break;
                            case "need-hotspot": hotspotRequest ??= ProvideHotspotAsync(child, token); break;
                            case "complete":
                                Processing?.Invoke(message.GetProperty("id").GetString()!);
                                await completions.Writer.WriteAsync(line, token);
                                break;
                            case "transfer":
                                string state = message.GetProperty("state").GetString()!;
                                if (state == "ReceivingFiles")
                                {
                                    string id = message.GetProperty("id").GetString()!;
                                    long? offeredSize = message.TryGetProperty("total", out var offeredTotal) && offeredTotal.ValueKind == JsonValueKind.Number ? offeredTotal.GetInt64() : null;
                                    if (receiving.TryAdd(id, 0)) Started?.Invoke(new TransferOffer(id,
                                        message.TryGetProperty("name", out var name) ? name.GetString() ?? "Files" : "Files", "Quick Share",
                                        message.TryGetProperty("sender", out var sender) ? sender.GetString() ?? "Quick Share device" : "Quick Share device", offeredSize),
                                        message.TryGetProperty("fileCount", out var count) ? Math.Max(1, count.GetInt32()) : 1);
                                    if (State != "SaveFailed") SetState("Receiving", "Receiving Quick Share files…");
                                    long received = message.GetProperty("received").ValueKind == JsonValueKind.Number ? message.GetProperty("received").GetInt64() : 0;
                                    long? total = message.GetProperty("total").ValueKind == JsonValueKind.Number ? message.GetProperty("total").GetInt64() : null;
                                    long now = Environment.TickCount64;
                                    if (!progressTicks.TryGetValue(id, out long tick) || now - tick >= 100 || received == total)
                                    { progressTicks[id] = now; Progress?.Invoke(new TransferProgress(id, "Quick Share", received, total)); }
                                }
                                else if (state is "Rejected" or "Cancelled" or "Disconnected" or "Failed")
                                    EndSession(message.GetProperty("id").GetString()!, false);
                                break;
                            case "error":
                                Activity?.Invoke("Quick Share: " + message.GetProperty("message").GetString());
                                if (message.TryGetProperty("id", out var failedId)) EndSession(failedId.GetString()!, false);
                                if (State == "Receiving" && receiving.IsEmpty) SetState("Listening", "Transfer did not finish; try sending again");
                                break;
                        }
                    }
                    ready.TrySetException(new IOException("Quick Share backend closed before readiness"));
                    if (!token.IsCancellationRequested) SetState("Faulted", "Quick Share backend closed; turn receiving off and on");
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { ready.TrySetException(e); if (!token.IsCancellationRequested) SetState("Faulted", e.Message); }
                finally { completions.Writer.TryComplete(); foreach (var id in receiving.Keys) EndSession(id, false); }
            });
            errors = Task.Run(async () => { try { while (await child.StandardError.ReadLineAsync(token) is { } line) Activity?.Invoke("Quick Share backend: " + line); } catch (OperationCanceledException) { } });
            SetState("Starting", "Starting independent Quick Share…");
            var status = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Port = status.GetProperty("port").GetInt32();
            BridgePort = status.GetProperty("bridgePort").GetInt32();
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "quickshare-native-status.json"), status.GetRawText());
            gatt.Changed += GattChanged; gatt.Activity += GattActivity;
            if (useRadio) await gatt.StartAsync(BridgePort, Convert.FromBase64String(status.GetProperty("advertisement").GetString()!), Convert.FromBase64String(status.GetProperty("advertisementHeader").GetString()!));
            SetState("Listening", "Independent Quick Share receiver is running; Google app is not required");
        }
        catch (Exception e) { await StopAsync(); SetState("Faulted", "Quick Share failed to start: " + e.Message); Activity?.Invoke(Detail); }
    }
    public async Task RetryDiscoveryAsync()
    {
        if(!Running||HasActiveTransfers||gatt.State is "Advertising" or "Starting")return;
        using var status=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dataRoot,"quickshare-native-status.json")));
        var root=status.RootElement;
        await gatt.StopAsync();
        await gatt.StartAsync(BridgePort,Convert.FromBase64String(root.GetProperty("advertisement").GetString()!),Convert.FromBase64String(root.GetProperty("advertisementHeader").GetString()!));
    }
    private void EndSession(string id, bool saved)
    {
        progressTicks.TryRemove(id, out _);
        if (receiving.TryRemove(id, out _)) SessionEnded?.Invoke(id, saved);
        if (receiving.IsEmpty && State == "Receiving") SetState("Listening", "Ready for Quick Share");
    }
    private void GattChanged() => Changed?.Invoke();
    private void GattActivity(string text) => Activity?.Invoke(text);
    private async Task ImportCompletionAsync(QuickShareCompletionImporter importer, string json, CancellationToken token)
    {
        using var document = JsonDocument.Parse(json);
        string id = document.RootElement.GetProperty("id").GetString()!;
        try { await importer.ImportAsync(json, token); }
        catch { EndSession(id, false); throw; }
        EndSession(id, true);
        string receipt = Path.Combine(staging, id + ".complete.json");
        // Move the receipt only after durable commits. A restart/change of save folder will not re-import old shares.
        File.Move(receipt, Path.Combine(staging, id + ".imported.json"), true);
        string session = Path.GetFullPath(Path.Combine(staging, id));
        if (!Guid.TryParseExact(id, "N", out _) || Path.GetDirectoryName(session) != Path.GetFullPath(staging)) return;
        foreach (var item in document.RootElement.GetProperty("files").EnumerateArray())
        {
            string path = Path.GetFullPath(item.GetString()!);
            if (Path.GetDirectoryName(path) != session) continue;
            try { File.Delete(path); } catch (IOException) { }
        }
        try { Directory.Delete(session, false); } catch (IOException) { }
    }
    private async Task ProvideHotspotAsync(Process child, CancellationToken token)
    {
        string command;
        try
        {
            var configuration = await hotspot.EnsureStartedAsync(token);
            LocalWifiNeedsSetup = false;
            LocalWifiDetail = "Local Wi-Fi Direct is running; Internet Sharing is disabled";
            Changed?.Invoke();
            command = JsonSerializer.Serialize(new { type = "hotspot", ssid = configuration.Ssid, password = configuration.Password, gateway = configuration.Gateway, frequency = configuration.Frequency });
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) { LocalWifiNeedsSetup = true; LocalWifiDetail = e.Message; Changed?.Invoke(); Activity?.Invoke("Quick Share local Wi-Fi upgrade: " + e.Message); command = "{\"type\":\"hotspot-unavailable\"}"; }
        try
        {
            await commands.WaitAsync(token);
            try { await child.StandardInput.WriteLineAsync(command.AsMemory(), token); await child.StandardInput.FlushAsync(token); }
            finally { commands.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Activity?.Invoke("Quick Share command: " + e.Message); }
    }

    public async Task StopAsync()
    {
        stop?.Cancel();
        if (hotspotRequest is not null) try { await hotspotRequest.WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
        hotspotRequest = null;
        await hotspot.StopAsync();
        await gatt.StopAsync(); gatt.Changed -= GattChanged; gatt.Activity -= GattActivity;
        if (process is { } child)
        {
            try
            {
                if (!child.HasExited) { await commands.WaitAsync(); try { await child.StandardInput.WriteLineAsync("{\"type\":\"stop\"}"); await child.StandardInput.FlushAsync(); } finally { commands.Release(); } await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
            }
            catch { try { if (!child.HasExited) child.Kill(true); } catch { } }
        }
        stop?.Cancel();
        foreach (var task in new[] { reader, imports, errors })
            if (task is not null) try { await task.WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
        process?.Dispose(); process = null; stop?.Dispose(); stop = null; Port = 0;
        foreach (var id in receiving.Keys) EndSession(id, false);
        progressTicks.Clear(); BridgePort = 0;
        SetState("Stopped", "Quick Share stopped");
    }
}
