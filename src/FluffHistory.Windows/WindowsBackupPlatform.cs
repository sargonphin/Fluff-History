using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluffHistory.Application;

namespace FluffHistory.Windows;

public sealed class WindowsPowerStateProvider : IPowerStateProvider
{
    public PowerState Read()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemPowerStatus(out var status)) return new(PowerSupply.Unknown);
        return new(status.ACLineStatus switch { 0 => PowerSupply.Battery, 1 => PowerSupply.AC, _ => PowerSupply.Unknown });
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        internal byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        internal uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}

/// <summary>Non-loopback interface availability plus NLM's machine-wide preferred Internet-route cost.
/// This cost is an advisory policy hint, not the route/cost/reachability of a particular SMB server.
/// No Internet requirement, active network probing, filesystem access or credentials.</summary>
public sealed class WindowsNetworkStateProvider : INetworkStateProvider
{
    public NetworkState Read()
    {
        if (!OperatingSystem.IsWindows()) return new(NetworkAvailability.Unknown, NetworkCost.Unknown);
        NetworkAvailability availability;
        try { availability = NetworkInterface.GetIsNetworkAvailable() ? NetworkAvailability.Available : NetworkAvailability.Unavailable; }
        catch (NetworkInformationException) { availability = NetworkAvailability.Unknown; }
        return new(availability, ReadCost());
    }

    [SupportedOSPlatform("windows")]
    private static NetworkCost ReadCost()
    {
        object? manager = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), true)!;
            manager = Activator.CreateInstance(type);
            ((INetworkCostManager)manager!).GetCost(out var cost, 0);
            return ClassifyCost(cost);
        }
        catch (Exception error) when (error is COMException or InvalidCastException or TypeLoadException)
        { return NetworkCost.Unknown; }
        finally { if (manager is not null && Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager); }
    }

    internal static NetworkCost ClassifyCost(uint cost) =>
        (cost & 0xF0000) != 0 ? NetworkCost.Costed : (cost & 6) != 0 ? NetworkCost.Metered :
        (cost & 1) != 0 ? NetworkCost.Unrestricted : NetworkCost.Unknown;

    // Only the first IUnknown method is used; later GetDataPlanStatus/SetDestinationAddresses slots are not called.
    [ComImport, Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager { void GetCost(out uint cost, nint destinationAddress); }
}

/// <summary>Scopes Windows resource background mode to the current worker thread, never the process.
/// Use on a dedicated synchronous worker; enter and dispose on that same thread.</summary>
public sealed class WindowsWorkerImpact : IWorkerImpact
{
    public IWorkerImpactScope Enter() => new Scope();

    private sealed class Scope : IWorkerImpactScope
    {
        private readonly uint _owner;
        private readonly bool _entered;
        private bool _disposed;
        public ImpactDiagnostic Diagnostic { get; private set; }
        internal Scope()
        {
            if (!OperatingSystem.IsWindows()) { Diagnostic = new(ImpactState.Unsupported); return; }
            _owner = GetCurrentThreadId();
            _entered = SetThreadPriority(GetCurrentThread(), 0x10000); // THREAD_MODE_BACKGROUND_BEGIN
            Diagnostic = _entered ? new(ImpactState.Applied) : new(ImpactState.SetupFailed, Marshal.GetLastPInvokeError());
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_entered) return;
            if (GetCurrentThreadId() != _owner) { Diagnostic = new(ImpactState.RestoreFailed, 87); return; }
            if (!SetThreadPriority(GetCurrentThread(), 0x20000)) // THREAD_MODE_BACKGROUND_END restores prior resource priorities.
                Diagnostic = new(ImpactState.RestoreFailed, Marshal.GetLastPInvokeError());
        }
    }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetThreadPriority(nint thread, int priority);
}
