using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NextShare.App.Services;

internal sealed class BluetoothVisibility : IDisposable
{
    private SafeFileHandle? radio;
    private bool wasDiscoverable, wasConnectable;
    public string Name { get; private set; } = Environment.MachineName;
    public bool Discoverable { get; private set; }

    public void Enable()
    {
        var parameters = new FindParameters { Size = (uint)Marshal.SizeOf<FindParameters>() };
        var enumeration = BluetoothFindFirstRadio(ref parameters, out var handle);
        if (enumeration == IntPtr.Zero) return;
        BluetoothFindRadioClose(enumeration);
        radio = handle;
        var info = new RadioInfo { Size = (uint)Marshal.SizeOf<RadioInfo>(), Name = "" };
        if (BluetoothGetRadioInfo(radio, ref info) == 0 && !string.IsNullOrWhiteSpace(info.Name)) Name = info.Name;
        wasDiscoverable = BluetoothIsDiscoverable(radio);
        wasConnectable = BluetoothIsConnectable(radio);
        if (!wasConnectable) BluetoothEnableIncomingConnections(radio, true);
        if (!wasDiscoverable) BluetoothEnableDiscovery(radio, true);
        Discoverable = BluetoothIsDiscoverable(radio);
    }

    public void Dispose()
    {
        if (radio is null || radio.IsInvalid) return;
        if (!wasDiscoverable) BluetoothEnableDiscovery(radio, false);
        if (!wasConnectable) BluetoothEnableIncomingConnections(radio, false);
        radio.Dispose(); radio = null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FindParameters { public uint Size; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct RadioInfo
    {
        public uint Size; public ulong Address;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
        public uint Class; public ushort Subversion; public ushort Manufacturer;
    }
    [DllImport("bthprops.cpl", SetLastError = true)] private static extern IntPtr BluetoothFindFirstRadio(ref FindParameters parameters, out SafeFileHandle radio);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindRadioClose(IntPtr handle);
    [DllImport("bthprops.cpl")] private static extern uint BluetoothGetRadioInfo(SafeFileHandle radio, ref RadioInfo info);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothIsDiscoverable(SafeFileHandle radio);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothIsConnectable(SafeFileHandle radio);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothEnableDiscovery(SafeFileHandle radio, [MarshalAs(UnmanagedType.Bool)] bool enabled);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothEnableIncomingConnections(SafeFileHandle radio, [MarshalAs(UnmanagedType.Bool)] bool enabled);
}
