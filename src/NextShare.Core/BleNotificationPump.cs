namespace NextShare.Core;

public sealed record BleSubscription<T>(string Key, T Client, int MaximumSize, bool Active) where T : class;

// The characteristic owns one pump for all peers. Never broadcast a customer's packet.
public sealed class BleNotificationPump<T>(
    Func<IReadOnlyList<BleSubscription<T>>> subscriptions,
    Func<T, ReadOnlyMemory<byte>, CancellationToken, Task> send,
    Action<string>? diagnostic = null) where T : class
{
    public const int IllegalMethodCall = unchecked((int)0x8000000E);
    private readonly SemaphoreSlim gate = new(1);
    private const int Attempts = 8;

    public async Task<int> WaitForCapacityAsync(string key, CancellationToken token)
    {
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var current = subscriptions().SingleOrDefault(c => c.Key == key);
            if (current is { Active: true, MaximumSize: >= 20 }) return Math.Min(509, current.MaximumSize);
            if (attempt != Attempts - 1) await Task.Delay(100, token);
        }
        throw new IOException("BLE notification capacity is not ready.");
    }

    public async Task SendAsync(string key, ReadOnlyMemory<byte> packet, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                // A CCCD/MTU transition can invalidate a previously captured WinRT client.
                var current = subscriptions().SingleOrDefault(c => c.Key == key);
                if (current is null || !current.Active || current.MaximumSize == 0)
                {
                    if (attempt == Attempts - 1) throw new IOException("BLE notification subscription is not active.");
                    diagnostic?.Invoke($"waiting for active notification subscription (attempt {attempt + 1})");
                }
                else
                {
                    if (packet.Length > current.MaximumSize) throw new InvalidDataException("BLE packet exceeds the current notification capacity.");
                    try
                    {
                        await send(current.Client, packet, token);
                        return;
                    }
                    catch (BleNotificationRejectedException) when (attempt < Attempts - 1)
                    {
                        // The adapter proved no operation was created. Results/getters must not be retried.
                        diagnostic?.Invoke($"Windows rejected notification state (attempt {attempt + 1}, capacity {current.MaximumSize}); refreshing subscription");
                    }
                }
                await Task.Delay(100, token);
            }
        }
        finally { gate.Release(); }
    }
}
