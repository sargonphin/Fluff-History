using System.IO.Hashing;
using System.Text;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;

namespace FluffHistory.Tests;

internal sealed class AllocationProbe(long unit = 4096) : IRepositoryAllocationProbe
{ public AllocationGeometry Observe(Repository repository, string? workspaceRoot) => new(unit); }

internal sealed class LiveProbe : IRepositoryCapacityProbe
{
    internal long Capacity = 1_000_000_000, Free = 900_000_000, Calls;
    internal Action<Repository, long>? Before;
    public CapacityObservation Observe(Repository repository)
    { Calls++; Before?.Invoke(repository, Calls); return new(Capacity, Free); }
}

internal sealed class LiveSource : SourceAccess
{
    internal Action<string>? BeforeOpen, BeforeObserve;
    internal Func<string, SourceObservation, SourceObservation>? Metadata;
    internal long Streams;
    internal override IEnumerable<string> EnumerateNames(string directory) => base.EnumerateNames(directory).Order(StringComparer.Ordinal);
    internal override SourceObservation Observe(string path)
    { BeforeObserve?.Invoke(path); var value = base.Observe(path); return Metadata?.Invoke(path, value) ?? value; }
    internal override Stream OpenRead(string path) { BeforeOpen?.Invoke(path); Streams++; return base.OpenRead(path); }
}

internal sealed class LiveCapacityFixture : IDisposable
{
    internal readonly TemporaryDirectory Temp = new();
    internal Repository Repository { get; }
    internal string Source { get; }
    internal SourceSelection Selection { get; }
    internal readonly LiveProbe Probe = new();
    internal readonly LiveSource Access = new();
    internal LiveCapacityFixture(bool encrypted = false)
    {
        Repository = encrypted ? Repository.CreateEncrypted(Temp.Child(), EncryptionTests.Password) : Repository.Create(Temp.Child()); Source = Temp.Child("source"); Directory.CreateDirectory(Source);
        Selection = new(new(FoundationTests.Root, Source), Source);
        using var writer = Repository.OpenWriter(); using var reader = Repository.OpenReader(); writer.Apply(new(sourceRoots: [Selection.Configuration]), reader.Authority);
    }
    internal string File(string name = "a", string content = "hello", int day = 0)
    {
        var path = Path.Combine(Source, name.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content, new UTF8Encoding(false)); System.IO.File.SetLastWriteTimeUtc(path, FoundationTests.Time.AddDays(day).UtcDateTime); return path;
    }
    internal StoredVersion Seed(string name = "a", string content = "hello", int day = 0, bool current = true)
    {
        var physical = File(name, content, day); var file = new FileInfo(physical);
        var hash = new XxHash64(); hash.Append(System.IO.File.ReadAllBytes(physical));
        using var writer = Repository.OpenWriter(); using var reader = Repository.OpenReader();
        using var draft = writer.CreatePayloadDraft(); draft.Write(System.IO.File.ReadAllBytes(physical)); draft.CompleteCopy(file.Length);
        var version = draft.PrepareVersion(new(FoundationTests.Root, name, file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc, FileAttributes.Archive),
            FoundationTests.Time.AddDays(day), new(Convert.ToHexString(hash.GetCurrentHash())));
        using (var prepared = writer.PrepareMutation(new([version]), reader.Authority)) { draft.Install(); prepared.Commit(); }
        using var fresh = Repository.OpenReader(); var stored = fresh.GetCurrent(version.Path)!;
        if (!current) writer.Apply(new(currentRemovals: [stored.Identity.Path]), fresh.Authority);
        return stored;
    }
    internal FileHistoryEngine Engine(RetentionPolicy policy = RetentionPolicy.UntilSaturation, int batch = 128,
        long byteTarget = 64 * 1024 * 1024, CapacityRetentionOptions? retention = null) => new(Repository,
            new(BatchSize: batch, MaximumPendingPayloadBytes: byteTarget, Capacity: new(policy, retention)), Access, Probe, new AllocationProbe());
    internal ScanResult Scan(FileHistoryEngine engine, CancellationToken token = default) => engine.Scan([Selection], token);
    public void Dispose() => Temp.Dispose();
}
