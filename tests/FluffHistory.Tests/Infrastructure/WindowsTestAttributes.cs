using Xunit;

namespace FluffHistory.Tests.Infrastructure;

// These bodies always run on Windows. Ubuntu reports them as skipped rather than claiming Win32 metadata coverage.
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requires real Windows filesystem/metadata APIs."; }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requires real Windows filesystem/metadata APIs."; }
}
