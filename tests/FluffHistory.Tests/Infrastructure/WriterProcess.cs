using System.Diagnostics;
using System.Runtime.InteropServices;
using FluffHistory.Storage;
using FluffHistory.Core;

namespace FluffHistory.Tests.Infrastructure;

/// <summary>Runs this test assembly as a child so OS-handle ownership and process death are exercised.</summary>
internal sealed class WriterProcess(Process process, string directory) : IAsyncDisposable
{
    private Process? _owner;
    private string? _ownerPath;

    private string ProcessDescription =>
        $"Launcher PID {process.Id} ({process.StartInfo.FileName}); lock owner PID {_owner?.Id} ({_ownerPath}).";

    internal bool OwnerHasExited => _owner?.HasExited ?? throw new InvalidOperationException("The owner has not reported readiness.");

    private static void ReportReady()
    {
        Console.WriteLine($"ready:{Environment.ProcessId}:{Environment.ProcessPath}");
        Console.Out.Flush();
    }

    private static void ProbeStreams(FileStream handle)
    {
        var buffer = new byte[16 * 1024];
        if (!GetFileInformation(handle.SafeFileHandle, 7, buffer, (uint)buffer.Length))
            throw new IOException("Retained handle stream query failed.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
    }
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformation(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int informationClass, [Out] byte[] buffer, uint size);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2)
        {
            return 2;
        }

        try
        {
            if (args[0].StartsWith("convert-", StringComparison.Ordinal))
            {
                var parts = args[0].Split('-');
                var encrypted = parts[1] == "encrypt";
                var point = Enum.Parse<EncryptionConversionStage>(parts[2]);
                var options = EncryptionConversionTests.Options(Path.GetDirectoryName(args[1])!) with
                {
                    Observed = stage => { if (stage == point) { ReportReady(); Thread.Sleep(Timeout.Infinite); } }
                };
                Repository.ConvertEncryption(args[1], encrypted, EncryptionTests.Password, options);
                return 3;
            }
            if (args[0] == "admission")
            {
                if (!OperatingSystem.IsWindows()) return 2;
                using var lease = new FluffHistory.Windows.WindowsOperationAdmission(Path.Combine(args[1], "writer.lock")).TryAcquire();
                if (lease is null) return 3;
                ReportReady(); Thread.Sleep(Timeout.Infinite); return 0;
            }
            if (args[0] == "silent-open")
            {
                using var repository = Repository.OpenWithWindowsUser(args[1]);
                using var reader = repository.OpenReader();
                var version = reader.EnumerateCurrent().Single();
                using var payload = reader.OpenVersionPayload(version.Identity)!;
                using var bytes = new MemoryStream(); payload.Content.CopyTo(bytes);
                if (!bytes.ToArray().AsSpan().SequenceEqual("canary-private-file-content"u8)) return 3;
                ReportReady(); return 0;
            }
            if (args[0].StartsWith("held-prepared", StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows()) return 2;
                var repository = Repository.Open(args[1]); using var writer = repository.OpenWriter(Path.GetTempPath());
                using var publication = writer.PrepareMutation(new(sourceRoots: [new(Guid.NewGuid(), "Resumed writer")]), repository.ReadAuthority().Identity);
                var handle = (FileStream)typeof(RepositoryWriter).GetField("_handle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(writer)!;
                if (args[0].EndsWith("-streams-prewarm", StringComparison.Ordinal)) ProbeStreams(handle);
                ReportReady(); if (Console.ReadLine() != "continue") return 2;
                try
                {
                    // Diagnostic probe on the actual retained stream, without changing the production checker.
                    if (args[0].EndsWith("-flush", StringComparison.Ordinal)) handle.Flush(true);
                    if (args[0].EndsWith("-lock", StringComparison.Ordinal)) { handle.Lock(0, 1); handle.Unlock(0, 1); }
                    if (args[0].EndsWith("-read", StringComparison.Ordinal)) _ = RandomAccess.Read(handle.SafeFileHandle, new byte[1], 0);
                    if (args[0].Contains("-streams", StringComparison.Ordinal)) ProbeStreams(handle);
                    publication.Commit(); Console.WriteLine("published"); return 0;
                }
                catch (IOException error) { Console.WriteLine("error:" + error.GetType().Name + ":" + error.Message); return 10; }
            }
            if (args[0].StartsWith("fault-", StringComparison.Ordinal))
                return SmbFaultChild.Run(args[0][10..], args[1], ReportReady);
            if (args[0] is "before-candidate" or "confirmation")
            {
                var repository = Repository.Open(args[1]);
                using var writer = repository.OpenWriter(Path.GetTempPath());
                writer.Observe = stage =>
                {
                    if (stage != (args[0] == "before-candidate" ? PublicationStage.LocalPrepared : PublicationStage.AfterReplace)) return;
                    if (args[0] == "confirmation") { writer.LoseHandleForTest(); return; }
                    ReportReady(); using var wait = new ManualResetEventSlim(); wait.Wait();
                };
                try { writer.Apply(new(sourceRoots: [new(Guid.NewGuid(), "Source")]), repository.ReadAuthority().Identity); }
                catch (PublicationUncertainException) when (args[0] == "confirmation")
                { ReportReady(); using var wait = new ManualResetEventSlim(); wait.Wait(); }
                return 2;
            }
            if (args[0].StartsWith("reusable-b-", StringComparison.Ordinal))
            {
                var repository = Repository.Open(args[1]); var source = Path.Combine(Path.GetDirectoryName(args[1])!, "source");
                var engine = new FluffHistory.Engine.FileHistoryEngine(repository);
                void Wait() { ReportReady(); using var wait = new ManualResetEventSlim(); wait.Wait(); }
                engine.ConfigureWriter = w =>
                {
                    w.PayloadObserve = (stage, _) =>
                    { if (args[0] == "reusable-b-partial" && stage == PayloadStage.BeforeWrite || args[0] == "reusable-b-installed" && stage == PayloadStage.Installed) Wait(); };
                    w.ReusableObserve = (stage, _) =>
                    { if (args[0] == "reusable-b-ready" && stage == ReusablePayloadStage.Ready || args[0] == "reusable-b-revoked" && stage == ReusablePayloadStage.Revoked) Wait(); };
                    w.Observe = stage =>
                    { if (args[0] == "reusable-b-prepared" && stage == PublicationStage.CandidateFlushed || args[0] == "reusable-b-published" && stage == PublicationStage.AfterReplace) Wait(); };
                };
                engine.Scan([new(new(FoundationTests.Root, source), source)]); return 2;
            }
            if (args[0].StartsWith("reusable-a-", StringComparison.Ordinal))
            {
                var repository = Repository.Open(args[1]); using var writer = repository.OpenWriter();
                void Wait() { ReportReady(); using var wait = new ManualResetEventSlim(); wait.Wait(); }
                writer.ReusableObserve = (stage, _) =>
                {
                    if (args[0] == "reusable-a-ready" && stage == ReusablePayloadStage.Ready ||
                        args[0] == "reusable-a-revoked" && stage == ReusablePayloadStage.Revoked) Wait();
                };
                writer.Observe = stage => { if (args[0] == "reusable-a-published" && stage == PublicationStage.AfterReplace) Wait(); };
                using var draft = writer.CreatePayloadDraft(); draft.Write("he"u8);
                if (args[0] == "reusable-a-partial") Wait();
                draft.Write("llo"u8); draft.CompleteCopy(5);
                writer.MarkPayloadReusable(draft, ReuseFixture.PathKey, FoundationTests.Time, ReuseFixture.Hash("hello"u8.ToArray()));
                using var publication = ReuseFixture.Prepare(repository, writer, draft);
                if (args[0] == "reusable-a-prepared") Wait();
                draft.Install(); if (args[0] == "reusable-a-installed") Wait();
                publication.Commit(); return 2;
            }
            if (args[0].StartsWith("live-capacity-", StringComparison.Ordinal))
            {
                var repository = Repository.Open(args[1]); var source = Path.Combine(Path.GetDirectoryName(args[1])!, "source");
                var probe = new LiveProbe();
                var engine = new FluffHistory.Engine.FileHistoryEngine(repository,
                    new(Capacity: new(RetentionPolicy.UntilSaturation)), probe, new AllocationProbe());
                void Wait() { ReportReady(); using var wait = new ManualResetEventSlim(); wait.Wait(); }
                if (args[0] == "live-capacity-retained")
                {
                    probe.Free = 0;
                    probe.Before = (repo, _) => { using var reader = repo.OpenReader(); if (!reader.EnumerateRetentionVersions().Any()) Wait(); };
                }
                if (args[0] == "live-capacity-staged") engine.ConfigureWriter = w => w.PayloadObserve = (stage, _) => { if (stage == PayloadStage.BeforeFlush) Wait(); };
                if (args[0] == "live-capacity-published") engine.BatchCommitted = _ => Wait();
                engine.Scan([new(new(FoundationTests.Root, source), source)]); return 2;
            }
            if (args[0] is "capacity-derived" or "capacity-cleaned")
            {
                var repository = Repository.Open(args[1]);
                var engine = new FluffHistory.Engine.CapacityRetentionEngine(repository,
                    new(BatchSize: 2, WorkspaceRoot: Path.Combine(Path.GetDirectoryName(args[1])!, "capacity-work")), new ScriptedCapacityProbe(new(1000, 0)));
                engine.Observe = (stage, _) =>
                {
                    var target = args[0] == "capacity-derived" ? FluffHistory.Engine.CapacityExecutionStage.OrderDerived : FluffHistory.Engine.CapacityExecutionStage.BatchCommitted;
                    if (stage != target) return; ReportReady(); using var wait = new ManualResetEventSlim(); wait.Wait();
                };
                engine.EnsureCapacity(0); return 2;
            }
            if (args[0] == "manual-cleanup-between-batches")
            {
                var repository = Repository.Open(args[1]);
                var engine = new FluffHistory.Engine.HistoryCleanupEngine(repository, new(BatchSize: 2));
                var preview = engine.Preview(new HistoryCleanupEvaluation(HistoryCleanupMode.KeepOnlyMostRecent));
                engine.BatchCommitted = _ => { ReportReady(); using var wait = new ManualResetEventSlim(); wait.Wait(); };
                engine.Execute(preview); return 2;
            }
            if (args[0] == "retention-between-batches")
            {
                var repository = Repository.Open(args[1]);
                var engine = new FluffHistory.Engine.RetentionEngine(repository, new(BatchSize: 2));
                var preview = engine.Preview(RetentionPolicy.OneMonth, FoundationTests.Time.AddDays(1000));
                engine.BatchCommitted = _ => { ReportReady(); using var wait = new ManualResetEventSlim(); wait.Wait(); };
                engine.Execute(preview); return 2;
            }
            if (args[0].StartsWith("remove-", StringComparison.Ordinal) || args[0].StartsWith("compact-removal-", StringComparison.Ordinal) || args[0] == "hold-payload")
            {
                var repository = Repository.Open(args[1]);
                using var reader = repository.OpenReader(); var path = new HistoryPath(FoundationTests.Root, "foo.txt");
                if (args[0] == "hold-payload")
                {
                    using var payload = reader.OpenVersionPayload(reader.EnumerateVersions(path).First().Identity)!;
                    ReportReady(); await Task.Delay(Timeout.Infinite); return 0;
                }
                var removals = reader.EnumerateVersions(path).Where(v => v.Identity != reader.GetCurrent(path)?.Identity).Select(v => v.Identity).ToArray();
                using var writer = repository.OpenWriter();
                writer.Observe = stage =>
                {
                    var target = args[0].EndsWith("prepared", StringComparison.Ordinal) ? PublicationStage.CandidateFlushed :
                        args[0].EndsWith("before", StringComparison.Ordinal) ? PublicationStage.BeforeReplace : PublicationStage.AfterReplace;
                    if (args[0] == "remove-cleanup" || stage != target) return;
                    ReportReady(); using var wait = new ManualResetEventSlim(); wait.Wait();
                };
                writer.PayloadCleanupObserve = (stage, _) =>
                {
                    if (args[0] != "remove-cleanup" || stage != PayloadCleanupStage.Deleted) return;
                    ReportReady(); using var wait = new ManualResetEventSlim(); wait.Wait();
                };
                if (args[0].StartsWith("compact-removal-", StringComparison.Ordinal)) writer.CompactMetadata();
                else writer.RemoveVersions(removals, reader.Authority);
                return 2;
            }
            if (args[0] == "reader")
            {
                var repository = FluffHistory.Storage.Repository.Open(args[1]);
                using var reader = repository.OpenReader();
                _ = reader.EnumerateCurrent().ToArray();
                ReportReady();
                await Task.Delay(Timeout.Infinite);
                return 0;
            }

            if (args[0] is "before-replace" or "after-replace" or "prepared")
            {
                var repository = FluffHistory.Storage.Repository.Open(args[1]);
                using var writer = repository.OpenWriter(Path.Combine(Path.GetDirectoryName(args[1])!, "process-workspace"));
                using var reader = repository.OpenReader();
                writer.Observe = step =>
                {
                    var target = args[0] == "prepared" ? PublicationStage.CandidateFlushed : args[0] == "before-replace" ? PublicationStage.BeforeReplace : PublicationStage.AfterReplace;
                    if (step != target) return;
                    ReportReady();
                    using var wait = new ManualResetEventSlim();
                    wait.Wait();
                };
                writer.Apply(new FluffHistory.Core.MutationBatch(sourceRoots: [new(Guid.NewGuid(), "Source")]), reader.Authority);
                return 2;
            }

            if (args[0] is "hold-writer" or "try-writer")
            {
                var repository = Repository.Open(args[1]);
                using var writer = repository.OpenWriter();
                if (args[0] == "hold-writer")
                {
                    ReportReady();
                    await Task.Delay(Timeout.Infinite);
                }
                return 0;
            }
            return 2;
        }
        catch (IOException)
        {
            return 10;
        }
    }

    internal static WriterProcess Start(string mode, string directory)
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(directory)!,
        };
        start.ArgumentList.Add(typeof(WriterProcess).Assembly.Location);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(directory);
        return new WriterProcess(Process.Start(start) ?? throw new InvalidOperationException("Could not launch lock probe."), directory);
    }

    internal async Task WaitUntilReadyAsync()
    {
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var fields = line?.Split(':', 3);
        if (fields is not ["ready", var ownerId, var ownerPath] || !int.TryParse(ownerId, out var pid))
        {
            throw new InvalidOperationException($"Lock probe failed to start: {await process.StandardError.ReadToEndAsync()}");
        }

        _ownerPath = ownerPath;
        _owner = pid == process.Id ? process : Process.GetProcessById(pid);
        // Retain a handle to the actual owner before termination, rather than looking up a PID
        // after it could have exited or been reused.
        _ = _owner.SafeHandle;
    }

    internal async Task<int> ExitCodeAsync()
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        return process.ExitCode;
    }

    internal void Continue() { process.StandardInput.WriteLine("continue"); process.StandardInput.Flush(); }
    internal string ReadOutput() => process.StandardOutput.ReadToEnd();

    internal async Task KillAsync()
    {
        KillTreeIfRunning(process);
        if (_owner is not null && !ReferenceEquals(_owner, process))
        {
            KillTreeIfRunning(_owner);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            if (_owner is not null)
            {
                if (!ReferenceEquals(_owner, process))
                {
                    // A tree-kill root exiting does not prove that descendants have exited.
                    await _owner.WaitForExitAsync(timeout.Token);
                }

                if (process.StartInfo.ArgumentList[1] == "reader")
                {
                    // The parent may own writer.lock. Observe release of metadata reader handles instead.
                    await WaitForFileAccessAsync(() =>
                    {
                        foreach (var path in Directory.GetFiles(Path.Combine(directory, "metadata"), "*.seg"))
                        {
                            using var released = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                        }
                    }, OperatingSystem.IsWindows(), timeout.Token);
                }
                else
                {
                    await WaitForLockReleaseAsync(timeout.Token);
                }
            }
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Probe termination or observable file-access release exceeded 20 seconds for '{directory}'. {ProcessDescription}", exception);
        }
    }

    private Task WaitForLockReleaseAsync(CancellationToken cancellationToken)
    {
        // Process exit and Windows file-sharing teardown are distinct observable conditions.
        // Only an acknowledged owner is checked: a try-writer probe may have correctly failed
        // because the parent still owns the lock. Never create/delete the persistent lock.
        return WaitForFileAccessAsync(() =>
        {
            using var released = new FileStream(
                Path.Combine(directory, "writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }, OperatingSystem.IsWindows(), cancellationToken);
    }

    // Test infrastructure only. The caller supplies a bounded cancellation deadline;
    // this narrow seam also lets tests deterministically exercise delayed Windows teardown.
    internal static async Task WaitForFileAccessAsync(Action tryOpen, bool isWindows, CancellationToken cancellationToken)
    {
        using var poll = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                tryOpen();
                return;
            }
            catch (IOException exception) when (isWindows &&
                exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
            {
                // Wait only while the required access is observably denied by sharing/locking.
                await poll.WaitForNextTickAsync(cancellationToken);
            }
        }
    }

    private static void KillTreeIfRunning(Process target)
    {
        try
        {
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (target.HasExited)
        {
            // A short-lived probe can exit between HasExited and Kill.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await KillAsync();
        }
        finally
        {
            if (_owner is not null && !ReferenceEquals(_owner, process))
            {
                _owner.Dispose();
            }

            process.Dispose();
        }
    }
}
