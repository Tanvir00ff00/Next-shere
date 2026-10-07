using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using NextShare.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;

namespace NextShare.App.Services;

internal static class BluetoothSenderService
{
    public static async Task SendAsync(SendTarget target, IReadOnlyList<OutgoingFile> files, IProgress<SendProgress> progress, CancellationToken token)
    {
        var information = await DeviceInformation.CreateFromIdAsync(target.Address,null,DeviceInformationKind.AssociationEndpoint).AsTask(token);
        if (!information.Pairing.IsPaired)
        {
            progress.Report(new(0, files.Sum(f=>f.Length), "Complete Bluetooth pairing in Windows…"));
            var pairing = await information.Pairing.PairAsync().AsTask(token);
            if (pairing.Status is not (DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired))
                throw new IOException("Bluetooth pairing was not completed: " + pairing.Status);
        }
        using var device = await BluetoothDevice.FromIdAsync(target.Address).AsTask(token).WaitAsync(TimeSpan.FromSeconds(15),token)
            ?? throw new IOException("Bluetooth device is unavailable. Turn on Bluetooth on the phone.");
        var services = await device.GetRfcommServicesForIdAsync(RfcommServiceId.ObexObjectPush, BluetoothCacheMode.Uncached).AsTask(token).WaitAsync(TimeSpan.FromSeconds(20),token);
        try
        {
            if (services.Error != BluetoothError.Success || services.Services.Count == 0)
                throw new IOException("This device is not offering Bluetooth file receiving (OBEX Object Push). Enable receiving on the phone and retry.");
            var service = services.Services[0];
            using var socket = new StreamSocket();
            using var cancellation = token.Register(socket.Dispose);
            await socket.ConnectAsync(service.ConnectionHostName,service.ConnectionServiceName,SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask(token).WaitAsync(TimeSpan.FromSeconds(20),token);
            using var input = socket.InputStream.AsStreamForRead();
            using var output = socket.OutputStream.AsStreamForWrite();
            await new ObexSender().SendAsync(input,output,files, sent=>progress.Report(new(sent,files.Sum(f=>f.Length),"Sending via Bluetooth…")),token);
        }
        finally { foreach (var service in services.Services) service.Dispose(); }
    }
}

internal sealed record SendProgress(long Sent, long Total, string Status, string? Pin = null);
