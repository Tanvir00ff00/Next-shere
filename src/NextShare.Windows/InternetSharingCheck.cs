using System.Runtime.InteropServices;
namespace NextShare.Platform;
public static class InternetSharingCheck
{
    // Read-only live check. Never enables/disables Sharing, NAT, adapters or firewall.
    public static bool IsDisabled()
    {
        var type=Type.GetTypeFromProgID("HNetCfg.HNetShare")??throw new IOException("Internet Sharing API unavailable.");
        dynamic manager=Activator.CreateInstance(type)!;
        object? connections=null;
        try
        {
            connections=manager.EnumEveryConnection;
            foreach(var connection in (System.Collections.IEnumerable)connections)
            {
                object? configuration=null;
                try { configuration=manager.INetSharingConfigurationForINetConnection(connection); if((bool)((dynamic)configuration).SharingEnabled)return false; }
                finally {Release(configuration);Release(connection);}
            }
            return true;
        }
        finally{Release(connections);Release(manager);}
    }
    private static void Release(object? value){if(value is not null&&Marshal.IsComObject(value))Marshal.ReleaseComObject(value);}
}
