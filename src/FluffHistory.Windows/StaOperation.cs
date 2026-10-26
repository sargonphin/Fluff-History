using System.Runtime.InteropServices;

namespace FluffHistory.Windows;

internal static class StaOperation
{
    internal static Task<T> Run<T>(Func<T> action)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Shell requires Windows.");
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = false;
            try
            {
                Marshal.ThrowExceptionForHR(OleInitialize(0)); initialized = true;
                completion.SetResult(action());
            }
            catch (OperationCanceledException e) when (e.CancellationToken.IsCancellationRequested) { completion.SetCanceled(e.CancellationToken); }
            catch (Exception e) { completion.SetException(e); }
            finally { if (initialized) OleUninitialize(); }
        }) { IsBackground = true, Name = "Fluff-History Shell operation" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
}
