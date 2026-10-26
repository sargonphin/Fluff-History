using Xunit;

namespace FluffHistory.Tests.Infrastructure;

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux native FIFO integration.";
    }
}
