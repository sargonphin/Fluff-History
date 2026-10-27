using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

[Collection("SMB diagnostics"), Trait("Category", "MetadataDiagnostic"), Trait("Category", "SmbIntegration")]
public sealed class SmbCapacityTests(ITestOutputHelper output)
{
    private static string Root => Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_CAPACITY_ROOT")
        ?? throw new InvalidOperationException("A genuinely constrained dedicated SMB share is required.");
    [SmbFact("FLUFF_HISTORY_SMB_CAPACITY_ROOT")]
    public void ConstrainedBackingFilesystemReportsActualFreeAndGenuineEnospc()
    {
        using var f = new SmbTestEnvironment(Root); var repo = Repository.Create(Path.Combine(f.Remote, "repository"));
        var before = repo.ObserveCapacity(); Assert.InRange(before.Capacity, 32 * 1024 * 1024, 512 * 1024 * 1024);
        var target = Path.Combine(f.Remote, "fill.bin"); int? code = null; long acknowledged = 0;
        using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1))
        {
            var bytes = new byte[1024 * 1024];
            try { while (acknowledged < before.Capacity + bytes.Length) { file.Write(bytes); file.Flush(true); acknowledged += bytes.Length; } }
            catch (IOException error) { code = error.HResult & 0xffff; output.WriteLine(error.ToString()); }
        }
        Assert.Contains(code, new int?[] { 112, 39 });
        var full = repo.ObserveCapacity(); Assert.True(full.Free < 2 * 1024 * 1024);
        File.Delete(target); var deleted = repo.ObserveCapacity(); Assert.True(deleted.Free > full.Free);
        var geometry = Geometry(Root);
        output.WriteLine("SMB_CAPACITY_FULL " + JsonSerializer.Serialize(new { Before = before, Full = full, Deleted = deleted, Error = code, AcknowledgedBytes = acknowledged, Geometry = geometry }));
    }

    [SmbTheory("FLUFF_HISTORY_SMB_CAPACITY_ROOT")]
    [InlineData(RetentionPolicy.UntilSaturation)] [InlineData(RetentionPolicy.Forever)] [InlineData(RetentionPolicy.OneMonth)]
    public void ActualPressureEvictsReadyBeforeHistoryAndPreservingPoliciesNeverPressureDelete(RetentionPolicy policy)
    {
        using var f = new SmbTestEnvironment(Root); var repo = Repository.Create(Path.Combine(f.Remote, "repository"));
        Assert.InRange(repo.ObserveCapacity().Capacity, 32 * 1024 * 1024, 512 * 1024 * 1024);
        var source = Path.Combine(f.Local, "source"); Directory.CreateDirectory(source); var file = Path.Combine(source, "foo.txt");
        SourceSelection[] selection = [new(new(FoundationTests.Root, source), source)];
        File.WriteAllBytes(file, new byte[32 * 1024 * 1024]); new FileHistoryEngine(repo, new(WorkspaceRoot: f.Local)) { Clock = () => FoundationTests.Time }.Scan(selection);
        File.WriteAllBytes(file, new byte[8 * 1024 * 1024]); new FileHistoryEngine(repo, new(WorkspaceRoot: f.Local)).Scan(selection);
        using (var writer = repo.OpenWriter(f.Local))
        {
            using var draft = writer.CreatePayloadDraft(); var buffer = new byte[1024 * 1024];
            var hash = new System.IO.Hashing.XxHash64();
            for (var i = 0; i < 16; i++) { draft.Write(buffer); hash.Append(buffer); }
            draft.CompleteCopy(16 * 1024 * 1024);
            writer.MarkPayloadReusable(draft, new(FoundationTests.Root, "unused-cache"), FoundationTests.Time, new(Convert.ToHexString(hash.GetCurrentHash())));
        }
        var before = repo.ObserveCapacity();
        FillTo(repo, Path.Combine(f.Remote, "filler.bin"), 8 * 1024 * 1024);
        var pressure = repo.ObserveCapacity();
        File.WriteAllBytes(file, new byte[32 * 1024 * 1024]);
        // Phase A explicitly supplies the server-observed allocation unit. Native UNC geometry
        // failure is separately recorded; Phase B must validate the ordinary production probe.
        IRepositoryAllocationProbe? allocation = Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_NATIVE_GEOMETRY") == "1" ? null : new ServerGeometryProbe(Geometry(Root));
        var result = new FileHistoryEngine(repo, new(WorkspaceRoot: f.Local, Capacity: new(policy)), allocationProbe: allocation).Scan(selection);
        Assert.True(result.Reuse.CapacityEvictions > 0);
        if (policy == RetentionPolicy.UntilSaturation)
        { Assert.Equal(ScanOutcome.Completed, result.Outcome); Assert.True(result.Capacity.VersionsRemoved > 0); Assert.Equal(1, result.VersionsCreated); }
        else
        { Assert.True(result.CapacityBlocked); Assert.Equal(0, result.Capacity.VersionsRemoved); Assert.Equal(0, result.VersionsCreated); }
        var afterScan = repo.ObserveCapacity(); long? explicitlyExpired = null;
        if (policy == RetentionPolicy.OneMonth)
        {
            var age = new RetentionEngine(repo, new(WorkspaceRoot: f.Local));
            var preview = age.Preview(RetentionPolicy.OneMonth, DateTimeOffset.UtcNow);
            Assert.True(preview.Complete); Assert.Equal(1, preview.Eligible);
            var expired = age.Execute(preview); Assert.Equal(1, expired.Removed); Assert.Equal(1, expired.Cleanup.Deleted);
            explicitlyExpired = expired.Removed;
        }
        using var reader = repo.OpenReader(); Assert.NotNull(reader.GetCurrent(ReuseFixture.PathKey));
        output.WriteLine("SMB_CAPACITY_POLICY " + JsonSerializer.Serialize(new { Policy = policy, Before = before, Pressure = pressure,
            AfterScan = afterScan, ExplicitlyExpired = explicitlyExpired, After = repo.ObserveCapacity(), Result = result }));
    }
    private sealed class ServerGeometryProbe(long unit) : IRepositoryAllocationProbe
    { public AllocationGeometry Observe(Repository repository, string? workspaceRoot) => new(unit); }

    [SmbFact("FLUFF_HISTORY_SMB_CAPACITY_ROOT")]
    public void ExternalConsumptionAfterAdmissionProducesRealPayloadDiskFullWithoutDeletingCurrent()
    {
        using var f = new SmbTestEnvironment(Root); var repo = Repository.Create(Path.Combine(f.Remote, "repository"));
        Assert.InRange(repo.ObserveCapacity().Capacity, 32 * 1024 * 1024, 512 * 1024 * 1024);
        var source = Path.Combine(f.Local, "source"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "foo.txt"), "hello");
        SourceSelection[] selections = [new(new(FoundationTests.Root, source), source)];
        new FileHistoryEngine(repo, new(WorkspaceRoot: f.Local)).Scan(selections);
        using var capture = repo.OpenReader(); var expected = capture.GetCurrent(ReuseFixture.PathKey)!.Identity;
        File.WriteAllBytes(Path.Combine(source, "foo.txt"), new byte[16 * 1024 * 1024]);
        var engine = new FileHistoryEngine(repo, new(WorkspaceRoot: f.Local, Capacity: new(RetentionPolicy.Forever)),
            allocationProbe: Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_NATIVE_GEOMETRY") == "1" ? null : new ServerGeometryProbe(Geometry(Root)));
        var filled = false;
        engine.ConfigureWriter = writer => writer.PayloadObserve = (stage, _) =>
        { if (!filled && stage == PayloadStage.BeforeWrite) { filled = true; FillTo(repo, Path.Combine(f.Remote, "external.bin"), 1024 * 1024); } };
        var result = engine.Scan(selections); Assert.True(filled); Assert.True(result.CapacityBlocked); Assert.Equal(0, result.VersionsCreated);
        using var fresh = repo.OpenReader(); Assert.Equal(expected, fresh.GetCurrent(ReuseFixture.PathKey)!.Identity);
        output.WriteLine("SMB_CAPACITY_RACE " + JsonSerializer.Serialize(result));
    }

    private static void FillTo(Repository repository, string target, long remaining)
    {
        var bytes = new byte[1024 * 1024];
        using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        for (var i = 0; i < 1024; i++)
        {
            var free = repository.ObserveCapacity().Free;
            if (free <= remaining) return;
            var count = (int)Math.Min(bytes.Length, free - remaining); file.Write(bytes, 0, count); file.Flush(true);
        }
        throw new IOException("Constrained fixture did not reach target within its explicit bound.");
    }
    private static long Geometry(string path)
    {
        var root = Path.GetPathRoot(path)!;
        if (!GetDiskFreeSpace(root, out var sectors, out var bytes, out _, out _)) throw new IOException("Server geometry unavailable.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return checked((long)sectors * bytes);
    }
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDiskFreeSpace(string path, out uint sectors, out uint bytes, out uint free, out uint total);
}
