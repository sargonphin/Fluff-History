using System.Collections.Concurrent;
using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class BackupAttemptRunnerTests
{
    [Theory]
    [InlineData("success")] [InlineData("cancel")] [InlineData("scan-error")] [InlineData("open-error")]
    public async Task DedicatedWorkerRestoresImpactForAllExitPaths(string scenario)
    {
        using var temp = new TemporaryDirectory(); var source = Directory.CreateDirectory(temp.Child("source")).FullName;
        File.WriteAllText(Path.Combine(source, "a.txt"), "synthetic data");
        using (var repository = Repository.Create(temp.Child())) { }
        var options = scenario == "scan-error" ? new ScanOptions(BatchSize: 0) : new ScanOptions();
        var configuration = new BackupConfiguration(scenario == "open-error" ? temp.Child("absent") : temp.Child(),
            [new(new SourceRootConfiguration(Guid.NewGuid(), source), source)], options);
        using var cancel = new CancellationTokenSource(); var impact = new ImpactProbe();
        var phases = new ConcurrentQueue<BackupPhase>();
        var result = await new EngineBackupAttemptRunner(impact).RunAsync(configuration, AttemptTrigger.Scheduled, p =>
        {
            phases.Enqueue(p.Phase);
            if (p.Phase == BackupPhase.Scanning && scenario == "cancel") cancel.Cancel();
        }, cancel.Token);
        Assert.Equal(1, impact.Entries); Assert.Equal(1, impact.Exits);
        Assert.Equal(impact.EntryThread, impact.ExitThread); Assert.False(impact.ThreadPool);
        Assert.Equal(ImpactState.Applied, result.Impact!.State);
        Assert.Equal(scenario switch { "cancel" => BackupOutcome.Cancelled, "scan-error" => BackupOutcome.ScanControlFailure,
            "open-error" => BackupOutcome.RepositoryUnavailable, _ => BackupOutcome.Completed }, result.Outcome);
        Assert.Contains(BackupPhase.OpeningRepository, phases); Assert.Equal(BackupPhase.Finished, phases.Last());
        if (scenario == "success")
        {
            Assert.NotNull(result.Scan); Assert.Equal(1, result.Scan.VersionsCreated);
            Assert.Equal(14, result.Scan.SourceBytesRead); Assert.Equal(14, result.Scan.PayloadBytesWritten);
        }
        using var reopened = Repository.Open(temp.Child()); using var writer = reopened.OpenWriter(); // Attempt writer is gone.
    }

    [Theory]
    [InlineData("throw-enter", ImpactState.SetupFailed)] [InlineData("native-failure", ImpactState.SetupFailed)]
    [InlineData("throw-exit", ImpactState.RestoreFailed)]
    public async Task QosFailureIsDiagnosticAndDoesNotAbandonBackup(string fault, ImpactState expected)
    {
        using var temp = new TemporaryDirectory(); var source = Directory.CreateDirectory(temp.Child("source")).FullName;
        File.WriteAllText(Path.Combine(source, "a"), "test"); using (Repository.Create(temp.Child())) { }
        var impact = new ImpactProbe { Fault = fault };
        var result = await new EngineBackupAttemptRunner(impact).RunAsync(Config(temp, source), AttemptTrigger.Scheduled, null, default);
        Assert.Equal(BackupOutcome.Completed, result.Outcome); Assert.Equal(expected, result.Impact!.State);
        Assert.Equal(1, result.Scan!.VersionsCreated);
    }

    [Fact]
    public async Task ManualRunnerDoesNotRequestScheduledImpactAndObserverFailureCannotAbort()
    {
        using var temp = new TemporaryDirectory(); var source = Directory.CreateDirectory(temp.Child("source")).FullName;
        using (Repository.Create(temp.Child())) { }
        var impact = new ImpactProbe();
        var result = await new EngineBackupAttemptRunner(impact).RunAsync(Config(temp, source), AttemptTrigger.Manual,
            _ => throw new InvalidOperationException("observer"), default);
        Assert.Equal(0, impact.Entries); Assert.Equal(ImpactState.NotRequested, result.Impact!.State);
        Assert.Equal(BackupOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task SourceSkipsArePreservedAndAreNotRepositoryFailures()
    {
        using var temp = new TemporaryDirectory(); var source = Directory.CreateDirectory(temp.Child("source")).FullName;
        File.WriteAllText(Path.Combine(source, "a"), "too big"); using (Repository.Create(temp.Child())) { }
        var config = new BackupConfiguration(temp.Child(), [new(new SourceRootConfiguration(Guid.NewGuid(), source, maximumFileSize: 1), source)]);
        var result = await new EngineBackupAttemptRunner().RunAsync(config, AttemptTrigger.Manual, null, default);
        Assert.Equal(BackupOutcome.CompletedWithSourceProblems, result.Outcome); Assert.Equal(1, result.Scan!.SourceSkips);
        Assert.Single(result.Scan.Problems); Assert.Equal(0, result.Scan.VersionsCreated);
    }

    [Fact]
    public void FinalProjectionDetachesProblemsAndPreservesCapacityCancellationReconciliation()
    {
        var scan = new ScanResult { Outcome = ScanOutcome.CapacityTargetImpossible, VersionsCreated = 3, BatchesCommitted = 2,
            Cancelled = true, ReconciliationMayBeRequired = true, Reactivations = 4, Removals = 5, Unchanged = 6,
            FilesEnumerated = 100, DirectoriesEnumerated = 10, SourceBytesRead = 20, PayloadBytesWritten = 30 };
        scan.Capacity.RemovalBatches = 7; scan.Capacity.VersionsRemoved = 8; scan.Capacity.LatestRetentionOutcome = CapacityRetentionOutcome.ImpossibleTarget;
        for (int i = 0; i < 300; i++) scan.Skip(Guid.NewGuid(), "a", new string('x', 2000));
        var projected = BackupAttemptResult.FromScan(scan);
        scan.VersionsCreated = 0; scan.Skip(Guid.NewGuid(), "b", "later");
        Assert.Equal(BackupOutcome.CapacityBlocked, projected.Outcome);
        var snapshot = projected.Scan!;
        Assert.True(snapshot.Cancelled); Assert.True(snapshot.Partial); Assert.True(snapshot.ReconciliationMayBeRequired);
        Assert.Equal(3, snapshot.VersionsCreated); Assert.Equal(300, snapshot.SourceSkips); Assert.Equal(256, snapshot.Problems.Length);
        Assert.Equal(1024, snapshot.Problems[0].Reason.Length); Assert.Equal(7, snapshot.CapacityRemovalBatches); Assert.Equal(8, snapshot.CapacityVersionsRemoved);
        Assert.Equal(CapacityRetentionOutcome.ImpossibleTarget, snapshot.CapacityOutcome);
    }

    [WindowsFact]
    public async Task EncryptedAttemptUsesLocalDpapiEveryTimeAndKeepsNoUnlockedOwner()
    {
        using var temp = new TemporaryDirectory(); var source = Directory.CreateDirectory(temp.Child("source")).FullName;
        File.WriteAllText(Path.Combine(source, "a"), "encrypted synthetic data");
        using (Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, true)) { }
        var runner = new EngineBackupAttemptRunner(); var config = Config(temp, source);
        var first = await runner.RunAsync(config, AttemptTrigger.Manual, null, default);
        Assert.Equal(BackupOutcome.Completed, first.Outcome); Assert.Equal(1, first.Scan!.VersionsCreated);
        var before = TemporaryDirectory.Snapshot(temp.Child());
        var second = await runner.RunAsync(config, AttemptTrigger.Manual, null, default);
        Assert.Equal(BackupOutcome.Completed, second.Outcome); Assert.Equal(1, second.Scan!.Unchanged);
        Assert.Equal(before, TemporaryDirectory.Snapshot(temp.Child())); // No durable scheduler/no-change record.
        // The same runner must reopen after ownership changes. It cannot reuse a live repository/master.
        var header = Path.Combine(temp.Child(), "repository.json"); var bootstrap = RepositoryBootstrap.Read(temp.Child());
        File.WriteAllBytes(header, (bootstrap with { WindowsUserKey = null }).Bytes());
        var unavailable = await runner.RunAsync(config, AttemptTrigger.Manual, null, default);
        Assert.Equal(BackupOutcome.RecoveryRequired, unavailable.Outcome); Assert.Null(unavailable.Scan);
        using var recovered = Repository.OpenWithPassword(temp.Child(), EncryptionTests.Password);
        recovered.EnrollCurrentWindowsUser();
        var enrolled = await runner.RunAsync(config, AttemptTrigger.Manual, null, default);
        Assert.Equal(BackupOutcome.Completed, enrolled.Outcome);
    }

    [Theory]
    [InlineData(null)] [InlineData("invalid-base64")]
    public async Task MissingOrDamagedConvenienceSlotIsRecoveryRequiredWithoutRepositoryMutation(string? slot)
    {
        using var temp = new TemporaryDirectory(); var source = Directory.CreateDirectory(temp.Child("source")).FullName;
        using (Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password)) { }
        var bootstrap = RepositoryBootstrap.Read(temp.Child());
        File.WriteAllBytes(Path.Combine(temp.Child(), "repository.json"), (bootstrap with { WindowsUserKey = slot }).Bytes());
        var before = TemporaryDirectory.Snapshot(temp.Child());
        var result = await new EngineBackupAttemptRunner().RunAsync(Config(temp, source), AttemptTrigger.Scheduled, null, default);
        Assert.Equal(BackupOutcome.RecoveryRequired, result.Outcome); Assert.Null(result.Scan);
        Assert.Equal(before, TemporaryDirectory.Snapshot(temp.Child()));
    }

    [WindowsFact]
    public async Task FailedDpapiUnwrapIsRecoveryButAuthenticatedStateDamageIsRepositoryFailure()
    {
        using var temp = new TemporaryDirectory(); var source = Directory.CreateDirectory(temp.Child("source")).FullName;
        using (Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, true)) { }
        var config = Config(temp, source); var runner = new EngineBackupAttemptRunner();
        var header = Path.Combine(temp.Child(), "repository.json"); var original = File.ReadAllBytes(header);
        var bootstrap = RepositoryBootstrap.Read(temp.Child());
        File.WriteAllBytes(header, (bootstrap with { WindowsUserKey = Convert.ToBase64String(new byte[32]) }).Bytes());
        Assert.Equal(BackupOutcome.RecoveryRequired, (await runner.RunAsync(config, AttemptTrigger.Manual, null, default)).Outcome);
        File.WriteAllBytes(header, original);
        var state = Path.Combine(temp.Child(), "state.json"); var damaged = File.ReadAllBytes(state); damaged[^1] ^= 1; File.WriteAllBytes(state, damaged);
        Assert.Equal(BackupOutcome.RepositoryUnavailable, (await runner.RunAsync(config, AttemptTrigger.Manual, null, default)).Outcome);
    }

    [Fact]
    public void ConfigurationSnapshotsCallerSelections()
    {
        var inputs = BackupCoordinatorTests.Configuration().Sources.ToList();
        var config = new BackupConfiguration(Path.GetFullPath("repository"), inputs); inputs.Clear();
        Assert.Single(config.Sources);
    }

    private static BackupConfiguration Config(TemporaryDirectory temp, string source) =>
        new(temp.Child(), [new(new SourceRootConfiguration(Guid.NewGuid(), source), source)]);
    private sealed class ImpactProbe : IWorkerImpact
    {
        internal string? Fault;
        internal int Entries, Exits, EntryThread, ExitThread;
        internal bool ThreadPool;
        public IWorkerImpactScope Enter()
        {
            Entries++; EntryThread = Environment.CurrentManagedThreadId; ThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            if (Fault == "throw-enter") throw new InvalidOperationException("synthetic-secret");
            return new Scope(this);
        }
        private sealed class Scope(ImpactProbe owner) : IWorkerImpactScope
        {
            public ImpactDiagnostic Diagnostic => new(owner.Fault == "native-failure" ? ImpactState.SetupFailed : ImpactState.Applied);
            public void Dispose()
            { owner.Exits++; owner.ExitThread = Environment.CurrentManagedThreadId; if (owner.Fault == "throw-exit") throw new InvalidOperationException("synthetic-secret"); }
        }
    }
}
