using FluffHistory.Application;
using FluffHistory.Windows;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.Activation;

namespace FluffHistory.Desktop;

internal static class Program
{
    private static readonly object Gate = new();
    private static Action<long, string?>? _receiver;
    private static long _revision;
    private static string? _pending;
    private static IDisposable? _startup;
    [STAThread]
    private static void Main(string[] args)
    {
        _startup = WindowsStartupAdmission.TryAcquire();
        if (_startup is null) return;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (args is ["--shell-broker", var brokerToken] && ShellWire.IsToken(brokerToken))
        {
            // At most eight demand-driven page processes. No UI lifetime/coordinator is constructed.
            AppInstance? slot = null;
            for (var i = 0; i < 8; i++)
            { var candidate = AppInstance.FindOrRegisterForKey("FluffHistory.ShellBroker." + i); if (candidate.IsCurrent) { slot = candidate; break; } }
            if (slot is null) return;
            ReleaseStartup();
            try { WindowsShellBroker.RunAsync(brokerToken, WindowsProductPaths.Settings,
                language => new Presentation.Models.ShellHistoryText(new Presentation.MrtAppStrings(languageOverride: language))).GetAwaiter().GetResult(); }
            finally { slot.UnregisterKey(); }
            return;
        }
        if (args.Length > 0 && args[0] == "--shell-broker") return;
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var instance = AppInstance.FindOrRegisterForKey("FluffHistory.UI");
        if (!instance.IsCurrent)
        {
            AllowSetForegroundWindow(instance.ProcessId);
            Redirect(instance, activation);
            return;
        }
        instance.Activated += (_, incoming) => Queue(Parse(incoming));
        Queue(args is ["--shell-request", var token] && ShellWire.IsToken(token) ? token : null);
        Presentation.UiLanguageStartup.Apply(new ApplicationPreferencesStore(WindowsProductPaths.Preferences)
            .LoadAsync().GetAwaiter().GetResult());
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
    internal static void ReleaseStartup() { Interlocked.Exchange(ref _startup, null)?.Dispose(); }
    private static void Redirect(AppInstance instance, AppActivationArguments arguments)
    {
        // Keep the actual entry thread STA. An async Main's generated runtime entry point does not
        // preserve STAThread on .NET 10. Redirect off-thread and dispatch COM/window calls while waiting.
        using var completed = new ManualResetEvent(false);
        var task = Task.Run(async () =>
        { try { await instance.RedirectActivationToAsync(arguments); } finally { completed.Set(); } });
        var result = CoWaitForMultipleHandles(0x18, uint.MaxValue, 1, [completed.SafeWaitHandle.DangerousGetHandle()], out _);
        Marshal.ThrowExceptionForHR(result); task.GetAwaiter().GetResult();
    }
    private static string? Parse(AppActivationArguments args)
    {
        if (args.Data is not ILaunchActivatedEventArgs launch) return null;
        // Win32 launch activation can include the executable as the first argument. The only accepted
        // intent is a fixed switch plus one exact token; paths never cross the command line.
        var parts = launch.Arguments.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[^2] == "--shell-request" && ShellWire.IsToken(parts[^1]) ? parts[^1] : null;
    }
    private static void Queue(string? token)
    {
        lock (Gate) { _pending = token; _revision++; _receiver?.Invoke(_revision, token); }
    }
    internal static void Attach(Action<long, string?> receiver)
    { lock (Gate) { _receiver = receiver; receiver(_revision, _pending); } }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("ole32.dll")] private static extern int CoWaitForMultipleHandles(uint flags, uint timeout, uint count, nint[] handles, out uint index);
}
