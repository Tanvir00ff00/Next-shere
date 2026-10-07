namespace NextShare.Core;

public enum BleNotificationOutcome { Success, Unreachable, ProtocolError, AccessDenied, Unknown }

// Only a rejection before obtaining a WinRT operation is safe to retry.
public sealed class BleNotificationRejectedException(Exception inner)
    : IOException("Windows rejected the BLE notification before dispatch.", inner);

public static class BleNotificationDelivery
{
    public static async Task SendAsync<TOperation,TResult>(
        Func<TOperation> begin,
        Func<TOperation,CancellationToken,Task<TResult>> wait,
        Func<TResult,BleNotificationOutcome> status,
        Func<TResult,int> bytesSent,
        Func<TResult,byte?> protocolError,
        int expectedBytes, CancellationToken token, Action<string>? diagnostic = null)
    {
        token.ThrowIfCancellationRequested();
        TOperation operation;
        try { operation=begin(); }
        catch(Exception ex) when(ex.HResult==BleNotificationPump<object>.IllegalMethodCall)
        { throw new BleNotificationRejectedException(ex); }
        TResult result;
        try { result=await wait(operation,token); }
        catch(OperationCanceledException) { throw; }
        catch(Exception ex) { throw new IOException("Awaiting BLE notification delivery failed; delivery may already have occurred.",ex); }
        BleNotificationOutcome outcome;
        try { outcome=status(result); }
        catch(Exception ex) { throw new IOException("Reading BLE notification status failed; delivery may already have occurred.",ex); }
        if(outcome!=BleNotificationOutcome.Success)
        {
            // BytesSent is not a success indicator, and failed results need not expose it.
            string detail="";
            if(outcome==BleNotificationOutcome.ProtocolError)
            {
                try { detail="; ATT error "+protocolError(result); }
                catch(Exception ex) { detail="; ATT error metadata unavailable (0x"+ex.HResult.ToString("X8")+")"; }
            }
            throw new IOException("BLE notification result: "+outcome+detail);
        }
        int delivered;
        try { delivered=bytesSent(result); }
        catch(Exception ex) when(ex.HResult==BleNotificationPump<object>.IllegalMethodCall)
        {
            // Successful delivery must never be repeated because an optional metadata getter fails.
            // Capacity is checked before dispatch; Nearby framing/commit validates the payload later.
            diagnostic?.Invoke("Notification status=Success; BytesSent metadata unavailable (0x8000000E). Packet was not resent.");
            return;
        }
        catch(Exception ex) { throw new IOException("Reading BLE notification byte count failed; packet was not resent.",ex); }
        if(delivered!=expectedBytes)throw new IOException($"BLE notification truncated: {delivered}/{expectedBytes} bytes.");
    }
}
