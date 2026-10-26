using System.Runtime.InteropServices;
using FluffHistory.Application;

namespace FluffHistory.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class WindowsUpdateNetwork
{
    public static UpdateNetworkPolicy Read()
    {
        object? manager = null;
        try
        {
            manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), true)!);
            var internet = (bool)((dynamic)manager!).IsConnectedToInternet;
            var unrestricted = new WindowsNetworkStateProvider().Read().Cost == NetworkCost.Unrestricted;
            return new(internet && unrestricted, internet && unrestricted);
        }
        catch (Exception error) when (error is COMException or InvalidCastException or TypeLoadException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { return new(false, false); }
        finally { if (manager is not null && Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager); }
    }
}
