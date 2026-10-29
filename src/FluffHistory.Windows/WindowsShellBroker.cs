using System.IO.Pipes;
using System.Runtime.InteropServices;
using FluffHistory.Application;
using Microsoft.Win32.SafeHandles;

namespace FluffHistory.Windows;

/// <summary>One on-demand page/client. No XAML, coordinator, backup, retention or recovery prompt.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class WindowsShellBroker
{
    public const string PipePrefix = "FluffHistory.Shell.v1.";
    public static async Task RunAsync(string token, string settingsPath, Func<string?, IShellHistoryText> presentation)
    {
        if (!ShellWire.IsToken(token)) return;
        using var pipe = new NamedPipeServerStream(PipePrefix + token, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        ShellPathSession? session = null;
        PreviewLease? previewLease = null;
        try
        {
            using (var connect = new CancellationTokenSource(TimeSpan.FromSeconds(15))) await pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcess)) return;
            var store = new AppSettingsStore(settingsPath);
            using var first = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var firstToken = first.Token;
            if (await ShellWire.ReadAsync(pipe, firstToken).ConfigureAwait(false) is not ShellOpenRequest open) return;
            var text = presentation(open.Language);
            // A blocked native UNC read cannot be cancelled promptly. This read-only child process exits
            // after the bounded reply rather than joining such a worker on Explorer's UI thread.
            var loading = OpenAsync();
            if (await Task.WhenAny(loading, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false) != loading)
            {
                _ = loading.ContinueWith(async completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion && completed.Result is { } late) await late.DisposeAsync().ConfigureAwait(false);
                    else _ = completed.Exception;
                }, TaskScheduler.Default).Unwrap();
                await OpenReplyAsync(ShellPathSession.Empty(ShellHistoryStatus.Unavailable, text)).ConfigureAwait(false); return;
            }
            session = await loading.ConfigureAwait(false);
            await OpenReplyAsync(session?.Summary ?? ShellPathSession.Empty(ShellHistoryStatus.Unavailable, text)).ConfigureAwait(false);
            if (session is null) return;
            if (open.VersionTable) await session.ReleaseReadCaptureAsync().ConfigureAwait(false);
            while (true)
            {
                // New pages retain only twenty detached selections, not a repository/key capture.
                // The owned pipe ends with the page. Old loaded DLLs retain their two-minute contract.
                using var idle = open.VersionTable ? new CancellationTokenSource() : new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var message = await ShellWire.ReadAsync(pipe, idle.Token).ConfigureAwait(false);
                if (message is ShellCloseRequest) return;
                if (message is ShellPreviewRequest preview && open.VersionTable)
                {
                    if (!OwnedWindow(preview.OwnerWindow)) return;
                    var opened = false;
                    try
                    {
                        var settings = await store.LoadAsync().ConfigureAwait(false);
                        if (settings.State != SettingsLoadState.Loaded || settings.Settings is null) throw new ApplicationOperationException(ApplicationFailure.RefreshRequired);
                        var exact = await session.RevalidateActionAsync(preview.SessionId, preview.VersionToken, settings.Settings).ConfigureAwait(false);
                        using var cancellation = new CancellationTokenSource();
                        var opening = new WindowsHistoryActions().PreviewAsync(settings.Settings.RepositoryPath, exact.Version, (nint)preview.OwnerWindow, cancellation.Token);
                        while (await Task.WhenAny(opening, Task.Delay(250)).ConfigureAwait(false) != opening)
                            if (!PeekNamedPipe(pipe.SafePipeHandle, 0, 0, 0, 0, 0) || !IsWindow((nint)preview.OwnerWindow)) { await cancellation.CancelAsync().ConfigureAwait(false); break; }
                        var lease = await opening.ConfigureAwait(false);
                        previewLease?.Dispose(); previewLease = lease; opened = true;
                    }
                    catch (Exception error) when (error is ApplicationOperationException or OperationCanceledException) { }
                    await ReplyAsync(new ShellResultReply(text.Preview(opened))).ConfigureAwait(false);
                    continue;
                }
                if (message is not ShellRestoreRequest restore) return;
                if (!OwnedWindow(restore.OwnerWindow)) return;
                ShellRestoreStatus result;
                try
                {
                    var settings = await store.LoadAsync().ConfigureAwait(false);
                    if (settings.State != SettingsLoadState.Loaded || settings.Settings is null) throw new ApplicationOperationException(ApplicationFailure.RefreshRequired);
                    var exact = open.VersionTable
                        ? await session.RevalidateActionAsync(restore.SessionId, restore.VersionToken, settings.Settings).ConfigureAwait(false)
                        : await session.GetRestoreAsync(restore.SessionId, restore.VersionToken, settings.Settings).ConfigureAwait(false);
                    using var cancellation = new CancellationTokenSource();
                    var restoring = new WindowsHistoryActions().RestoreExactAsync(settings.Settings.RepositoryPath, exact.Version,
                        null, (nint)restore.OwnerWindow, exact.Mapping.ToSelection(), cancellation.Token);
                    while (await Task.WhenAny(restoring, Task.Delay(250)).ConfigureAwait(false) != restoring)
                        if (!PeekNamedPipe(pipe.SafePipeHandle, 0, 0, 0, 0, 0) || !IsWindow((nint)restore.OwnerWindow)) { await cancellation.CancelAsync().ConfigureAwait(false); break; }
                    var outcome = await restoring.ConfigureAwait(false);
                    result = outcome.Unconfirmed > 0 || outcome.Failed > 0 || outcome.MetadataWarnings > 0 ? ShellRestoreStatus.NeedsAttention :
                        outcome.Cancelled ? ShellRestoreStatus.Cancelled : outcome.Copied == 1 ? ShellRestoreStatus.Restored : ShellRestoreStatus.NothingRestored;
                }
                catch (ApplicationOperationException error) { result = error.Failure == ApplicationFailure.RefreshRequired ? ShellRestoreStatus.RefreshRequired : ShellRestoreStatus.Unavailable; }
                catch (OperationCanceledException) { result = ShellRestoreStatus.CancelledInspect; }
                await ReplyAsync(new ShellResultReply(open.VersionTable && result == ShellRestoreStatus.RefreshRequired ? text.Preview(false) : text.Restore(result))).ConfigureAwait(false);
            }
            async Task<ShellPathSession?> OpenAsync()
            {
                var loaded = await store.LoadAsync(firstToken).ConfigureAwait(false);
                return loaded.State == SettingsLoadState.Loaded && loaded.Settings is { } settings
                    ? await ShellPathSession.OpenAsync(settings, open.Path, open.Kind, text, firstToken).ConfigureAwait(false) : null;
            }
            async Task ReplyAsync(ShellMessage reply)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await pipe.WriteAsync(ShellWire.Encode(reply), timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            Task OpenReplyAsync(ShellHistorySummary summary) => ReplyAsync(open.VersionTable
                ? new ShellTableReply(summary with { Summary = open.Kind == ShellItemKind.File ? text.Status(summary.Status) : summary.Summary })
                : new ShellOpenReply(summary));
            bool OwnedWindow(ulong window) => window <= long.MaxValue && IsWindow((nint)window) &&
                GetWindowThreadProcessId((nint)window, out var process) != 0 && process == clientProcess;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or ApplicationOperationException or ArgumentException) { }
        finally { previewLease?.Dispose(); if (session is not null) await session.DisposeAsync().ConfigureAwait(false); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafePipeHandle pipe, nint buffer, uint size, nint read, nint available, nint remaining);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
}
