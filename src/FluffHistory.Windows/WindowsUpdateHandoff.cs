using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json.Serialization;
using FluffHistory.Application;
using Microsoft.Win32.SafeHandles;

namespace FluffHistory.Windows;

public sealed record UpdateOwnedProcess(int Id, DateTimeOffset StartedUtc, string Path);
public sealed record UpdateHandoffRequest([property: JsonRequired] int Schema, [property: JsonRequired] string Nonce,
    [property: JsonRequired] UpdateRelease Release, [property: JsonRequired] string CurrentReleaseVersion,
    [property: JsonRequired] string CurrentBuildVersion, [property: JsonRequired] string HelperSha256,
    [property: JsonRequired] UpdateOwnedProcess Agent, [property: JsonRequired] UpdateOwnedProcess[] Desktop,
    [property: JsonRequired] bool Relaunch)
{
    public void Validate()
    {
        (Release ?? throw new InvalidDataException("Missing release.")).Validate();
        if (Release is null || Desktop is null || Agent is null || Schema != 1 || !Guid.TryParseExact(Nonce, "N", out _) || !ReleaseVersion.TryParse(CurrentReleaseVersion, out var current) ||
            current.ToString() != CurrentReleaseVersion || Release.Version <= current || !ProductBuildVersion.TryParse(CurrentBuildVersion, out _) ||
            !UpdateRelease.IsDigest(HelperSha256) || Desktop.Length > 16 || Desktop.Append(Agent).Any(p => p is null || p.Id <= 0 || p.Path is null || p.Path.Length > 1024 || p.StartedUtc.Offset != TimeSpan.Zero) ||
            Desktop.Select(p => p.Id).Distinct().Count() != Desktop.Length || Desktop.Any(p => p.Id == Agent.Id))
            throw new InvalidDataException("Invalid update handoff.");
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WindowsUpdateProcesses
{
    internal static UpdateOwnedProcess Capture(Process process, string build, string executable)
    {
        var expected = Path.Combine(WindowsProductPaths.InstallRoot, "Versions", build, executable);
        if (!ProductBuildVersion.TryParse(build, out _) || process.SessionId != Process.GetCurrentProcess().SessionId ||
            !string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) throw new UpdateException(UpdateFailure.Busy);
        using var handle = OpenProcess(0x1000, false, process.Id);
        if (handle.IsInvalid || !OpenProcessToken(handle, 8, out var token)) throw new UpdateException(UpdateFailure.Busy);
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
            if (identity.User != current.User) throw new UpdateException(UpdateFailure.Busy);
        return new(process.Id, process.StartTime.ToUniversalTime(), expected);
    }
    internal static Process? Open(UpdateOwnedProcess expected, string build, string executable)
    {
        try
        {
            var process = Process.GetProcessById(expected.Id);
            try
            {
                var actual = Capture(process, build, executable);
                if (actual != expected) { process.Dispose(); return null; } // PID was reused: never act on it.
                return process;
            }
            catch { process.Dispose(); throw; }
        }
        catch (ArgumentException) { return null; }
    }
    internal static UpdateOwnedProcess[] Desktop(string build)
    {
        var result = new List<UpdateOwnedProcess>();
        foreach (var process in Process.GetProcessesByName("FluffHistory.Desktop"))
        {
            using (process)
            {
                if (process.SessionId != Process.GetCurrentProcess().SessionId) continue;
                var path = process.MainModule?.FileName;
                if (path is null || !path.StartsWith(WindowsProductPaths.InstallRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(Capture(process, build, "FluffHistory.Desktop.exe"));
                if (result.Count > 16) throw new UpdateException(UpdateFailure.Busy);
            }
        }
        return result.ToArray();
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int process);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}

/// <summary>Portable backup coordination is injected; Windows owns exact installed processes and detached helper.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsUpdateInstaller(WindowsUpdateStaging staging, BackupOperationGate gate,
    Func<bool> backupActive, Func<Task> cancelBackup, Assembly identity, IUpdateSignerPolicy signer) : IUpdateInstaller, IDisposable
{
    private UpdateAdmissionResult? _handoffAdmission;
    public void Dispose() => Interlocked.Exchange(ref _handoffAdmission, null)?.Dispose();
    public Task<StagedUpdate> StageAsync(UpdateRelease release, IProgress<long> progress, CancellationToken token)
    {
        if (signer is ApprovedUpdateSignerPolicy { IsConfigured: false }) throw new UpdateException(UpdateFailure.SignerNotApproved);
        return staging.StageAsync(release, progress, token);
    }
    public async Task<UpdateFailure> HandoffAsync(StagedUpdate update, bool manual, bool cancel, CancellationToken token)
    {
        if (InstalledReleaseIdentity.FromAssembly(identity) is not { } current) return UpdateFailure.DevelopmentBuild;
        if (update.Release.Version <= current) return UpdateFailure.InvalidResponse;
        using var admission = await new UpdateInstallAdmission(gate, backupActive, cancelBackup).AcquireAsync(manual, cancel, token).ConfigureAwait(false);
        if (admission.Failure != UpdateFailure.None) return admission.Failure;
        var build = identity.GetName().Version!.ToString();
        var desktops = WindowsUpdateProcesses.Desktop(build);
        if (!manual && desktops.Length != 0) return UpdateFailure.Busy;
        using var verified = staging.OpenVerified(update);
        var nonce = Guid.NewGuid().ToString("N"); var directory = staging.GetDirectory(update.Release);
        var helper = Path.Combine(directory, "FluffHistory.InstallSupport.exe");
        using var parents = new WindowsUpdatePathLease(directory);
        var source = Path.Combine(AppContext.BaseDirectory, "FluffHistory.InstallSupport.exe");
        using var original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        WindowsUpdateFileVerifier.VerifySignature(source, original.SafeFileHandle, signer);
        var helperHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        if (!File.Exists(helper))
        {
            original.Position = 0;
            using var output = new FileStream(helper, FileMode.CreateNew, FileAccess.Write, FileShare.None); original.CopyTo(output); output.Flush(true);
        }
        using var heldHelper = new FileStream(helper, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(heldHelper)).ToLowerInvariant() != helperHash) return UpdateFailure.Integrity;
        using var self = Process.GetCurrentProcess();
        var request = new UpdateHandoffRequest(1, nonce, update.Release, current.ToString(), build, helperHash,
            WindowsUpdateProcesses.Capture(self, build, "FluffHistory.Agent.exe"), desktops, manual);
        request.Validate();
        var store = new UpdateJsonStore<UpdateHandoffRequest?>(Path.Combine(directory, "handoff-v1.json"), null,
            r => (r ?? throw new InvalidDataException()).Validate(), 16384);
        await store.SaveAsync(request, token).ConfigureAwait(false);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\FluffHistory.UpdateReady." + nonce);
        using var commit = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\FluffHistory.UpdateCommit." + nonce);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(update.Release.Version.ToString()); start.ArgumentList.Add(nonce);
        using var process = Process.Start(start) ?? throw new UpdateException(UpdateFailure.Installation);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
        while (!ready.WaitOne(0))
        {
            if (process.HasExited || DateTimeOffset.UtcNow >= deadline) return UpdateFailure.Installation;
            await Task.Delay(50, token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested(); commit.Set();
        // Keep fencing existing Agent timers/RunNow until controller shutdown has drained.
        // Process startup is separately fenced by the helper's update.lock barrier.
        _handoffAdmission = admission.TransferOwnership();
        // Helper now owns the startup barrier. Agent retains mutation admission until this returns;
        // host waits for complete Agent/UI exit, then reacquires mutation before launching Setup.
        return UpdateFailure.None;
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class WindowsUpdateHost
{
    internal static string[] SetupArguments(bool manual, string nonce)
    {
        if (!Guid.TryParseExact(nonce, "N", out _)) throw new ArgumentException("Invalid setup handoff nonce.", nameof(nonce));
        // Manual installation may need a new agreement; automatic updates never invent consent.
        return manual ? ["/NORESTART", "/SP-", "/UPDATEHANDOFF=" + nonce]
            : ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/UPDATEHANDOFF=" + nonce];
    }

    public static async Task<int> ApplyAsync(string version, string nonce, Assembly identity)
    {
        if (!ReleaseVersion.TryParse(version, out var target) || target.ToString() != version || !Guid.TryParseExact(nonce, "N", out _)) return 2;
        var root = Path.Combine(WindowsProductPaths.Root, "Updates", version);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        UpdateHandoffRequest? request = null; int? exit = null; bool installed = false, relaunchFailed = false;
        var failure = UpdateFailure.Installation;
        try
        {
            using var parents = new WindowsUpdatePathLease(root);
            var store = new UpdateJsonStore<UpdateHandoffRequest?>(Path.Combine(root, "handoff-v1.json"), null,
                r => (r ?? throw new InvalidDataException()).Validate(), 16384);
            request = (await store.LoadAsync(timeout.Token).ConfigureAwait(false)).Value ?? throw new InvalidDataException();
            var expectedHelper = Path.Combine(root, "FluffHistory.InstallSupport.exe");
            if (request.Nonce != nonce || request.Release.Version != target ||
                InstalledReleaseIdentity.FromAssembly(identity)?.ToString() != request.CurrentReleaseVersion ||
                identity.GetName().Version!.ToString() != request.CurrentBuildVersion ||
                !string.Equals(Environment.ProcessPath, expectedHelper, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            var signer = ApprovedUpdateSignerPolicy.FromAssembly(identity);
            using var helper = new FileStream(expectedHelper, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(SHA256.HashData(helper)).ToLowerInvariant() != request.HelperSha256) throw new UpdateException(UpdateFailure.Integrity);
            WindowsUpdateFileVerifier.VerifySignature(expectedHelper, helper.SafeFileHandle, signer);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            var staging = new WindowsUpdateStaging(http, new WindowsUpdateFileVerifier(signer));
            using var verified = staging.OpenVerified(new(request.Release, staging.GetPath(request.Release)));
            IDisposable? barrier = new WindowsOperationAdmission(WindowsProductPaths.UpdateLock).TryAcquire();
            if (barrier is null) throw new UpdateException(UpdateFailure.Busy);
            using (barrier)
            {
                var actual = WindowsUpdateProcesses.Desktop(request.CurrentBuildVersion);
                if (actual.Any(p => !request.Desktop.Contains(p)) || !request.Relaunch && actual.Length != 0) throw new UpdateException(UpdateFailure.Busy);
                using var agent = WindowsUpdateProcesses.Open(request.Agent, request.CurrentBuildVersion, "FluffHistory.Agent.exe");
                if (agent is null) throw new UpdateException(UpdateFailure.Busy);
                using var ready = EventWaitHandle.OpenExisting("Local\\FluffHistory.UpdateReady." + nonce);
                using var commit = EventWaitHandle.OpenExisting("Local\\FluffHistory.UpdateCommit." + nonce);
                ready.Set();
                using var closeDeadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); closeDeadline.CancelAfter(TimeSpan.FromSeconds(60));
                while (!commit.WaitOne(0)) await Task.Delay(50, closeDeadline.Token).ConfigureAwait(false);
                foreach (var desktop in request.Desktop)
                {
                    using var process = WindowsUpdateProcesses.Open(desktop, request.CurrentBuildVersion, "FluffHistory.Desktop.exe");
                    if (process is null) continue;
                    // Normal WM_CLOSE invokes DesktopLifetime disposal. Broker/blocked windows are waited out.
                    if (request.Relaunch && process.MainWindowHandle != 0) process.CloseMainWindow();
                    await process.WaitForExitAsync(closeDeadline.Token).ConfigureAwait(false);
                }
                await agent.WaitForExitAsync(closeDeadline.Token).ConfigureAwait(false);
                using var mutation = new WindowsOperationAdmission(WindowsProductPaths.MutationLock).TryAcquire();
                if (mutation is null) throw new UpdateException(UpdateFailure.Busy);
                using var setupReady = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\FluffHistory.UpdateSetup." + nonce);
                var start = new ProcessStartInfo(staging.GetPath(request.Release)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
                foreach (var arg in SetupArguments(request.Relaunch, nonce)) start.ArgumentList.Add(arg);
#if FH_UPDATE_TESTING
                var faultFile = Path.Combine(WindowsProductPaths.Root, "UpdateTest", "fault.txt");
                WindowsOperationAdmission.CheckParents(faultFile);
                if (File.Exists(faultFile))
                {
                    if (new FileInfo(faultFile).Length > 32) throw new InvalidDataException();
                    var fault = File.ReadAllText(faultFile).Trim();
                    if (fault is not ("Properties" or "Agent")) throw new InvalidDataException();
                    start.ArgumentList.Add("/FAULT=" + fault);
                }
#endif
                using var setup = Process.Start(start) ?? throw new UpdateException(UpdateFailure.Installation);
                // Startup barrier closes the gap until Setup owns its existing installation lock.
                // Once Setup starts, cancellation/timeout cannot prove that installation stopped.
                // Retain admission and wait for its actual exit; never abandon/terminate it.
                while (!setupReady.WaitOne(0) && !setup.HasExited) await Task.Delay(50).ConfigureAwait(false);
                if (setupReady.WaitOne(0)) barrier.Dispose();
                await setup.WaitForExitAsync().ConfigureAwait(false); exit = setup.ExitCode;
                if (exit != 0) throw new UpdateException(UpdateFailure.Installation);
                installed = ConfirmInstalled(target);
                if (!installed) throw new UpdateException(UpdateFailure.Installation);
                failure = UpdateFailure.None;
            }
            if (request.Relaunch)
            {
                try { using var launched = Process.Start(new ProcessStartInfo(CurrentDesktop(target)) { UseShellExecute = true }); }
                catch (Exception) { relaunchFailed = true; }
            }
        }
        catch (UpdateException error) { failure = error.Failure; }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Update handoff failed ({0}).", error.GetType().Name); }
        finally
        {
            if (request is not null)
            {
                if (!installed)
                {
                    // Setup retains rollback authority. Restart only a validated current-version Agent,
                    // never the staging helper's directory and never guess that the target committed.
                    try
                    {
                        var restored = CurrentDesktop(ReleaseVersion.Parse(request.CurrentReleaseVersion));
                        var agentPath = Path.Combine(Path.GetDirectoryName(restored)!, "FluffHistory.Agent.exe");
                        WindowsOperationAdmission.CheckParents(agentPath);
                        using var restarted = Process.Start(new ProcessStartInfo(agentPath) { UseShellExecute = false, CreateNoWindow = true });
                    }
                    catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Update rollback Agent restart unavailable ({0}).", error.GetType().Name); }
                }
                var result = new UpdateInstallResult(1, version, DateTimeOffset.UtcNow, exit, installed, failure, relaunchFailed);
                try { await new UpdateJsonStore<UpdateInstallResult?>(WindowsProductPaths.UpdateResult, null, r => (r ?? throw new InvalidDataException()).Validate(), 4096).SaveAsync(result).ConfigureAwait(false); }
                catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Update result unavailable ({0}).", error.GetType().Name); }
            }
        }
        return installed ? 0 : 2;
    }
    private static bool ConfirmInstalled(ReleaseVersion target)
    { try { return File.Exists(CurrentDesktop(target)); } catch (Exception) { return false; } }
    private static string CurrentDesktop(ReleaseVersion target)
    {
        var marker = Path.Combine(WindowsProductPaths.InstallRoot, "current-version.txt");
        WindowsOperationAdmission.CheckParents(marker);
        using var input = File.OpenRead(marker); if (input.Length > 32) throw new InvalidDataException();
        using var reader = new StreamReader(input); var text = reader.ReadToEnd();
        if (!ProductBuildVersion.TryParse(text, out var build) || !ProductBuildVersion.MatchesRelease(build, target)) throw new InvalidDataException();
        var desktop = Path.Combine(WindowsProductPaths.InstallRoot, "Versions", text, "FluffHistory.Desktop.exe");
        var info = Path.Combine(Path.GetDirectoryName(desktop)!, "build-info.json");
        WindowsOperationAdmission.CheckParents(info);
        using var metadata = File.OpenRead(info); if (metadata.Length > 65536) throw new InvalidDataException();
        using var json = System.Text.Json.JsonDocument.Parse(metadata, new() { MaxDepth = 4 });
        if (json.RootElement.GetProperty("version").GetString() != text || json.RootElement.GetProperty("releaseVersion").GetString() != target.ToString())
            throw new InvalidDataException();
        WindowsOperationAdmission.CheckParents(desktop); return desktop;
    }
}
