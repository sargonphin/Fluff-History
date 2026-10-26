using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluffHistory.Windows.Interop;

namespace FluffHistory.Windows;

[SupportedOSPlatform("windows")]
internal sealed class NativeShellCopyOperation : IShellCopyOperation
{
    private readonly IFileOperation _operation;
    private NativeProgressSink? _sink;
    private uint _cookie;
    private bool _advised, _disposed;
    internal NativeShellCopyOperation()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Shell operation requires STA.");
        _operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
    }
    public void Configure(nint owner, uint flags, IShellCopyCallbacks callbacks)
    {
        Marshal.ThrowExceptionForHR(_operation.SetOperationFlags(flags));
        if (owner != 0) Marshal.ThrowExceptionForHR(_operation.SetOwnerWindow(owner));
        _sink = new(callbacks); Marshal.ThrowExceptionForHR(_operation.Advise(_sink, out _cookie)); _advised = true;
    }
    public void Queue(string source, string destinationFolder, string historicalName)
    {
        IShellItem? from = null, to = null;
        try
        {
            from = Item(source); to = Item(destinationFolder);
            Marshal.ThrowExceptionForHR(_operation.CopyItem(from, to, historicalName, null)); // Advise owns the single sink.
        }
        finally { Release(to); Release(from); }
    }
    public ShellExecution Perform()
    {
        var result = _operation.PerformOperations();
        var query = _operation.GetAnyOperationsAborted(out var aborted); // Also required after failed PerformOperations.
        return new(result, aborted, query);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { if (_advised) _ = _operation.Unadvise(_cookie); }
        finally { Release(_operation); GC.KeepAlive(_sink); _sink = null; }
    }
    private static IShellItem Item(string path)
    {
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, in iid, out var item)); return item;
    }
    internal static string ItemPath(IShellItem item)
    {
        Marshal.ThrowExceptionForHR(item.GetDisplayName(0x80058000, out var text)); // SIGDN_FILESYSPATH
        try { return Marshal.PtrToStringUni(text) ?? throw new IOException("Shell item has no filesystem path."); }
        finally { Marshal.FreeCoTaskMem(text); }
    }
    internal static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None), SupportedOSPlatform("windows")]
internal sealed class NativeProgressSink(IShellCopyCallbacks callbacks) : IFileOperationProgressSink
{
    private int Safe(Func<int> action, params object?[] borrowed)
    {
        try { return action(); }
        catch (Exception e) { try { callbacks.Fault(e); } catch { } return unchecked((int)0x80004005); }
        finally
        {
            foreach (var value in borrowed)
                try { NativeShellCopyOperation.Release(value); } catch { } // Callback-owned RCWs never escape.
        }
    }
    public int StartOperations() => Safe(callbacks.CheckCancellation);
    public int FinishOperations(int result) => Safe(() => callbacks.Finish(result));
    public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? name) => Safe(() =>
        callbacks.PreCopy(NativeShellCopyOperation.ItemPath(item), NativeShellCopyOperation.ItemPath(destination)), item, destination);
    public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? created) => Safe(() =>
        callbacks.PostCopy(NativeShellCopyOperation.ItemPath(item), NativeShellCopyOperation.ItemPath(destination), result,
            created is null ? null : NativeShellCopyOperation.ItemPath(created)), item, destination, created);
    public int UpdateProgress(uint total, uint completed) => Safe(callbacks.CheckCancellation);
    public int ResetTimer() => 0;
    public int PauseTimer() => 0;
    public int ResumeTimer() => 0;
    private static int Unexpected() => throw new InvalidOperationException("Non-copy Shell operation refused by restore sink.");
    public int PreRenameItem(uint flags, IShellItem item, string? name) => Safe(Unexpected, item);
    public int PostRenameItem(uint flags, IShellItem item, string? name, int result, IShellItem? created) => Safe(() => 0, item, created);
    public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? name) => Safe(Unexpected, item, destination);
    public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? created) => Safe(() => 0, item, destination, created);
    public int PreDeleteItem(uint flags, IShellItem item) => Safe(Unexpected, item);
    public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created) => Safe(() => 0, item, created);
    public int PreNewItem(uint flags, IShellItem destination, string? name) => Safe(Unexpected, destination);
    public int PostNewItem(uint flags, IShellItem destination, string? name, string? template, uint attributes, int result, IShellItem? created) => Safe(() => 0, destination, created);
}
