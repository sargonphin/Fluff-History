using System.Text.Json;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class SmbQuotaFactAttribute : FactAttribute
{
    public SmbQuotaFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_QUOTA_ROOT")))
            Skip = "Requires a genuinely configured, isolated server-side hard quota and FLUFF_HISTORY_SMB_QUOTA_ROOT.";
    }
}
[Collection("SMB diagnostics"), Trait("Category", "MetadataDiagnostic"), Trait("Category", "SmbIntegration")]
public sealed class SmbQuotaTests(ITestOutputHelper output)
{
    [SmbQuotaFact]
    public void ObserveWhetherWindowsReportsActualServerQuotaAndVerifyEnforcement()
    {
        var root = Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_QUOTA_ROOT")!;
        var quota = long.Parse(Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_QUOTA_BYTES")!);
        using var f = new SmbTestEnvironment(root); var repo = Repository.Create(Path.Combine(f.Remote, "repository"));
        var before = repo.ObserveCapacity(); long acknowledged = 0; int? error = null;
        var target = Path.Combine(f.Remote, "quota-fill.bin");
        using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1))
        {
            var buffer = new byte[1024 * 1024];
            try
            {
                while (acknowledged <= quota + buffer.Length)
                { file.Write(buffer); file.Flush(true); acknowledged += buffer.Length; }
            }
            catch (IOException failure) { error = failure.HResult & 0xffff; }
        }
        Assert.Contains(error, new int?[] { 112, 39 }); Assert.True(acknowledged <= quota);
        var full = repo.ObserveCapacity(); File.Delete(target); var released = repo.ObserveCapacity();
        var result = new { ServerHardQuotaBytes = quota, Before = before, Full = full, Released = released,
            WindowsReportsQuota = before.Capacity <= quota, AcknowledgedBytes = acknowledged, Error = error };
        output.WriteLine("SMB_QUOTA " + JsonSerializer.Serialize(result));
        new SmbNetworkMeasurementTests(output).Save("quota", result);
    }
}
