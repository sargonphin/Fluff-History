namespace FluffHistory.Windows;

internal readonly record struct ShellExecution(int HResult, bool Aborted, int AbortedQueryHResult = 0);
internal interface IShellCopyCallbacks
{
    int CheckCancellation();
    int PreCopy(string source, string destinationFolder);
    int PostCopy(string source, string destinationFolder, int hResult, string? actualPath);
    int Finish(int hResult);
    void Fault(Exception exception);
}
// Copy-specific seam: production always uses IFileOperation, never another copy engine.
internal interface IShellCopyOperation : IDisposable
{
    void Configure(nint owner, uint flags, IShellCopyCallbacks callbacks);
    void Queue(string source, string destinationFolder, string historicalName);
    ShellExecution Perform();
}
