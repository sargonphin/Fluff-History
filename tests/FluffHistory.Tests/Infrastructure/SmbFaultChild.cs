using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;

namespace FluffHistory.Tests.Infrastructure;

internal static class SmbFaultChild
{
    internal sealed record Configuration(string Source, string Workspace, string Destination);
    internal static int Run(string mode, string path, Action ready)
    {
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "fault.json")))!;
        var repo = Repository.Open(path); var signaled = false;
        void Pause()
        {
            if (signaled) return; signaled = true; ready();
            if (Console.ReadLine() != "continue") throw new IOException("No watchdog continuation.");
        }
        try
        {
            if (mode is "restore" or "restore-destination")
            {
                using var reader = repo.OpenReader(); var identity = reader.EnumerateCurrent().First().Identity;
                var restore = new RestoreEngine(repo) { Observe = (stage, _) => { if (stage == (mode == "restore" ? RestoreStage.BeforeRead : RestoreStage.BeforeInstall)) Pause(); } };
                var result = restore.RestoreFile(identity, config.Destination);
                Console.WriteLine("result:" + JsonSerializer.Serialize(result)); return 0;
            }
            if (mode == "metadata-transfer")
            {
                MetadataWorkspace.CopyObserved = (source, destination, _) =>
                {
                    if (source is FileStream input && destination is FileStream output &&
                        input.Name.StartsWith(path, StringComparison.OrdinalIgnoreCase) && !output.Name.StartsWith(path, StringComparison.OrdinalIgnoreCase)) Pause();
                };
                using var writer = repo.OpenWriter(config.Workspace);
                writer.Apply(new(sourceRoots: [new(FoundationTests.Root, "Changed")]), repo.ReadAuthority().Identity);
                Console.WriteLine("result:committed"); return 0;
            }
            var engine = new FileHistoryEngine(repo, new(WorkspaceRoot: config.Workspace), new FaultSource(mode, Pause));
            engine.ConfigureWriter = writer =>
            {
                writer.PayloadObserve = (stage, _) => { if (mode == "payload-write" && stage == PayloadStage.BeforeWrite) Pause(); };
                writer.Observe = stage =>
                {
                    if (mode == "before-replace" && stage == PublicationStage.BeforeReplace ||
                        mode == "after-replace" && stage == PublicationStage.AfterReplace) Pause();
                };
            };
            var scan = engine.Scan([new(new(FoundationTests.Root, config.Source), config.Source)]);
            Console.WriteLine("result:" + JsonSerializer.Serialize(scan)); return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.WriteLine("error:" + error.GetType().Name + ":" + error.Message); return 10; }
        finally { MetadataWorkspace.CopyObserved = null; }
    }
    private sealed class FaultSource(string mode, Action pause) : SourceAccess
    {
        internal override IEnumerable<string> EnumerateNames(string path)
        {
            if (mode == "root-enumeration" && !path.EndsWith("child", StringComparison.Ordinal) || mode == "child-enumeration" && path.EndsWith("child", StringComparison.Ordinal)) pause();
            return base.EnumerateNames(path);
        }
        internal override SourceObservation Observe(string path)
        { if (mode == "observation" && path.EndsWith("foo.txt", StringComparison.Ordinal)) pause(); return base.Observe(path); }
        internal override int Read(Stream stream, Span<byte> buffer)
        { if (mode == "source-read") pause(); return base.Read(stream, buffer); }
    }
}
