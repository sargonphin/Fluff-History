using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using FluffHistory.Application;
using FluffHistory.Tests.Infrastructure;
using FluffHistory.Windows;
using FluffHistory.Core;
using FluffHistory.Storage;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

// Real resource scheduling diagnostics must not compete with the suite's bulk filesystem workloads.
// Timing/policy correctness is tested separately with fake time/providers, without elapsed-time assertions.
[Collection("NativeBackupPlatform")]
public sealed class WindowsBackupPlatformTests(ITestOutputHelper output)
{
    [WindowsFact]
    public async Task NativeThreadBackgroundScopeRestoresThreadAndLeavesCallerAndProcessAlone()
    {
        using var callerThread = OpenThread(0x0800, false, GetCurrentThreadId()); // QUERY_LIMITED_INFORMATION
        Assert.False(callerThread.IsInvalid);
        var caller = GetThreadPriority(callerThread);
        var process = Process.GetCurrentProcess().PriorityClass;
        var diagnostic = await Task.Factory.StartNew(() =>
        {
            var before = GetThreadPriority(GetCurrentThread());
            var scope = new WindowsWorkerImpact().Enter();
            var during = GetThreadPriority(GetCurrentThread());
            try
            {
                Assert.Equal(ImpactState.Applied, scope.Diagnostic.State);
                Assert.Equal(process, Process.GetCurrentProcess().PriorityClass);
                Assert.Equal(caller, GetThreadPriority(callerThread));
            }
            finally { scope.Dispose(); }
            var after = GetThreadPriority(GetCurrentThread());
            Assert.Equal(ImpactState.Applied, scope.Diagnostic.State);
            Assert.Equal(before, after);
            return $"workerPriorityBefore={before}; during={during}; after={after}; begin/end={scope.Diagnostic.State}; processPriority={process}; otherThreadPriority={GetThreadPriority(callerThread)}";
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.Equal(caller, GetThreadPriority(callerThread));
        Assert.Equal(process, Process.GetCurrentProcess().PriorityClass);
        output.WriteLine(diagnostic);
    }

    [WindowsFact]
    public void NativePowerAndNetworkWiringReportsHintsWithoutAssumingHostState()
    {
        var power = new WindowsPowerStateProvider().Read();
        var network = new WindowsNetworkStateProvider().Read();
        Assert.True(Enum.IsDefined(power.Supply)); Assert.True(Enum.IsDefined(network.Availability)); Assert.True(Enum.IsDefined(network.Cost));
        output.WriteLine($"power={power.Supply}; networkAvailability={network.Availability}; defaultInternetRouteCost={network.Cost}");
    }

    [WindowsFact]
    public async Task ComposedScheduledEncryptedScanUsesNativeWorkerImpactAndPublishesOnlyFileHistory()
    {
        using var temp = new TemporaryDirectory();
        var source = Directory.CreateDirectory(temp.Child("source")).FullName;
        File.WriteAllText(Path.Combine(source, "a.txt"), "synthetic scheduled data");
        using (Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, true)) { }
        var time = new ManualSchedulerTime(BackupCoordinatorTests.Anchor.AddMinutes(-1));
        var rootId = Guid.NewGuid();
        // This deliberate disposable source is inside Windows Temp. Production source safety policy
        // requires explicit per-source consent for that location, just like the product warning.
        var configuration = new BackupConfiguration(temp.Child(), [new(new SourceRootConfiguration(rootId, source), source)],
            safeguards: new(AllowOnBattery: true)) { ForcedSourceIds = [rootId] };
        await using var coordinator = WindowsBackupRuntime.CreateCoordinator(configuration,
            new(TimeSpan.FromMinutes(10), BackupCoordinatorTests.Anchor), time);
        coordinator.Start(); time.Advance(TimeSpan.FromMinutes(1));
        try { await BackupCoordinatorTests.Until(coordinator, s => s.LastAttempt is not null && !s.IsAttemptActive, TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { output.WriteLine($"Watchdog status: {coordinator.Status}"); throw; }
        var result = coordinator.Status.LastAttempt!.Result;
        Assert.Equal(BackupOutcome.Completed, result.Outcome); Assert.Equal(ImpactState.Applied, result.Impact!.State);
        Assert.Equal(1, result.Scan!.VersionsCreated);
        using var repository = Repository.OpenWithWindowsUser(temp.Child()); using var reader = repository.OpenReader();
        Assert.Single(reader.EnumerateCurrent());
        output.WriteLine($"scheduled encrypted scan={result.Outcome}; versions={result.Scan.VersionsCreated}; impact={result.Impact.State}");
    }

    [Fact]
    public void WindowsAdaptersReportUnsupportedOrUnknownOffWindows()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(PowerSupply.Unknown, new WindowsPowerStateProvider().Read().Supply);
        Assert.Equal(NetworkAvailability.Unknown, new WindowsNetworkStateProvider().Read().Availability);
        using var scope = new WindowsWorkerImpact().Enter(); Assert.Equal(ImpactState.Unsupported, scope.Diagnostic.State);
    }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int GetThreadPriority(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int GetThreadPriority(SafeWaitHandle thread);
}

[CollectionDefinition("NativeBackupPlatform", DisableParallelization = true)]
public sealed class NativeBackupPlatformCollection;
