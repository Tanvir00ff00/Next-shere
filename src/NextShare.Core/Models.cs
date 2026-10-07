namespace NextShare.Core;

public sealed record TransferRecord(string Id, string SessionId, string Name, string Route,
    string Sender, long Size, string Sha256, string FilePath, DateTimeOffset ReceivedAt,
    string State = "Ready", string? SourceKey = null);

public sealed record TransferOffer(string SessionId, string Name, string Route, string Sender, long? Size);
public sealed record TransferProgress(string SessionId, string Name, long Received, long? Expected);

public sealed class ReceiverOptions
{
    public long MaximumFileBytes { get; init; } = 16L * 1024 * 1024 * 1024;
    public TimeSpan PacketTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ConsentTimeout { get; init; } = TimeSpan.FromSeconds(20);
}
