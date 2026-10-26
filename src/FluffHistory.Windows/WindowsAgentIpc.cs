using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using FluffHistory.Application;
using Microsoft.Win32.SafeHandles;

namespace FluffHistory.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsAgentClient
{
    internal string Endpoint { get; init; } = PipeName;
    public static string PipeName => "FluffHistory.Agent.v2." + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value)))[..32];
    public async Task<AgentReply> SendAsync(AgentRequest request, CancellationToken token = default)
    {
        AgentProtocol.Validate(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(35));
        using var pipe = new NamedPipeClientStream(".", Endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(1500, deadline.Token).ConfigureAwait(false);
        await AgentProtocol.WriteAsync(pipe, request, false, deadline.Token).ConfigureAwait(false);
        return await AgentProtocol.ReadAsync<AgentReply>(pipe, true, deadline.Token).ConfigureAwait(false);
    }
    public async Task<AgentReply> ConnectOrStartAsync(CancellationToken token = default)
    {
        try { return await SendAsync(new(AgentOperation.GetState), token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException && !token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
        var executable = Path.Combine(AppContext.BaseDirectory, "FluffHistory.Agent.exe");
        WindowsOperationAdmission.CheckParents(AppContext.BaseDirectory);
        if ((File.GetAttributes(executable) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new IOException("Agent executable unavailable.");
        using var started = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            try { return await SendAsync(new(AgentOperation.GetState), timeout.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or TimeoutException && !timeout.IsCancellationRequested)
            { await Task.Delay(250, timeout.Token).ConfigureAwait(false); }
        }
    }
    public async Task<bool> ShutdownForUpdateAsync(CancellationToken token = default)
    {
        AgentReply reply;
        try { reply = await SendAsync(new(AgentOperation.ShutdownForUpdate), token).ConfigureAwait(false); }
        catch (TimeoutException) { return true; } // No pipe owner; no process was started.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var process = Process.GetProcessById(reply.State.ProcessId);
            // PID is same-user IPC input, not authority to terminate anything. Only wait; never kill.
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); return true;
        }
        catch (ArgumentException) { return true; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }
}

/// <summary>Four bounded current-user clients. Native PIPE_REJECT_REMOTE_CLIENTS is explicit;
/// each connection carries exactly one typed request and one detached reply.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsAgentServer(AgentController controller)
{
    internal string Endpoint { get; init; } = WindowsAgentClient.PipeName;
    private readonly TaskCompletionSource _shutdownReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task ShutdownRequested => _shutdownReply.Task;
    public async Task RunAsync(CancellationToken token)
    {
        var clients = new List<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var pipe = CreatePipe();
                try { await pipe.WaitForConnectionAsync(token).ConfigureAwait(false); }
                catch { pipe.Dispose(); throw; }
                clients.RemoveAll(task => task.IsCompleted);
                if (clients.Count >= 4) { pipe.Dispose(); continue; }
                clients.Add(ServeAsync(pipe, token));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await Task.WhenAll(clients).ConfigureAwait(false); }
    }
    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stopping)
    {
        using (pipe)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var shutdownRequest = false;
            try
            {
                var request = await AgentProtocol.ReadAsync<AgentRequest>(pipe, false, deadline.Token).ConfigureAwait(false);
                AgentReply reply;
                if (controller.State.ShuttingDown && request.Operation is not (AgentOperation.GetState or AgentOperation.WaitForState or AgentOperation.ShutdownForUpdate))
                    reply = new(AgentError.ShuttingDown, controller.State);
                else switch (request.Operation)
                {
                    case AgentOperation.GetState: reply = new(AgentError.None, controller.State); break;
                    case AgentOperation.WaitForState:
                        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                        {
                            wait.CancelAfter(TimeSpan.FromSeconds(20));
                            try { await controller.WaitForStateAsync(request.SinceRevision, wait.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                        }
                        reply = new(AgentError.None, controller.State); break;
                    case AgentOperation.RunNow:
                        var admission = controller.RunNow(); reply = new(admission == BackupAdmission.Busy ? AgentError.Busy : AgentError.None, controller.State, admission); break;
                    case AgentOperation.CancelActive:
                        await controller.CancelActiveAsync().ConfigureAwait(false); reply = new(AgentError.None, controller.State); break;
                    case AgentOperation.ApplySettings:
                        reply = await controller.ApplySettingsAsync(request, deadline.Token).ConfigureAwait(false); break;
                    case AgentOperation.Reinspect:
                        await controller.ReinspectAsync(deadline.Token).ConfigureAwait(false); reply = new(AgentError.None, controller.State); break;
                    case AgentOperation.ReloadUpdatePreferences:
                        await controller.ReloadUpdatePreferencesAsync(deadline.Token).ConfigureAwait(false); reply = new(AgentError.None, controller.State); break;
                    case AgentOperation.CheckUpdates:
                        reply = new(controller.RequestUpdateCheck() ? AgentError.None : AgentError.Busy, controller.State); break;
                    case AgentOperation.InstallUpdate:
                        reply = new(controller.RequestUpdateInstall(request.ConfirmUpdateCancellation) ? AgentError.None : AgentError.Busy, controller.State); break;
                    case AgentOperation.ShutdownForUpdate:
                        shutdownRequest = true;
                        _ = controller.ShutdownAsync(); reply = new(AgentError.None, controller.State); break;
                    default: throw new InvalidDataException("Unknown agent operation.");
                }
                await AgentProtocol.WriteAsync(pipe, reply, true, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or
                ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
            { /* Disconnected/malformed clients own no agent lifetime; canonical transitions finish independently. */ }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Agent request failed ({0}).", error.GetType().Name); }
            // A status long-poll may finish as shutdown begins. Only the shutdown
            // request's own reply/closure may release the host to cancel pipe work.
            finally { if (shutdownRequest) _shutdownReply.TrySetResult(); }
        }
    }
    private NamedPipeServerStream CreatePipe()
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;GA;;;" + sid + ")", 1, out var descriptor, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipeW(@"\\.\pipe\" + Endpoint, 0x40000003, 0x8, 8, 4096, 4096, 0, ref security);
            if (handle.IsInvalid) { handle.Dispose(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public nint Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipeW(
        string name, uint access, uint mode, uint instances, uint output, uint input, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
