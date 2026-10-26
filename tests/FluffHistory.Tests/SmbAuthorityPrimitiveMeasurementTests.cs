using System.ComponentModel;
using System.Runtime.InteropServices;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

[Collection("SMB diagnostics"), Trait("Category", "MetadataDiagnostic")]
public sealed class SmbAuthorityPrimitiveMeasurementTests(ITestOutputHelper output)
{
    [SmbFact]
    public async Task CompareWindowsReplacementPrimitivesUnderConcurrentValidStateObservers()
    {
        using var f = new SmbTestEnvironment(); var repo = Repository.Create(Path.Combine(f.Remote, "repository"));
        var old = repo.ReadAuthority();
        using (var writer = repo.OpenWriter(f.Local))
            writer.Apply(new(sourceRoots: [new(FoundationTests.Root, "Source")]), old.Identity);
        var next = repo.ReadAuthority(); var states = new[] { AuthorityFiles.StateBytes(old), AuthorityFiles.StateBytes(next) };
        var results = new List<object>();
        foreach (var mode in new[] { "ReplaceFileW", "MoveFileExW" })
        {
            var name = "visibility-" + mode + ".json"; var target = Path.Combine(repo.DirectoryPath, name);
            AuthorityFiles.WriteNew(target, states[0]);
            var errors = new Dictionary<string, int>(); var mutations = new Dictionary<int, int>(); var valid = 0; var replacements = 0;
            using var stop = new CancellationTokenSource();
            var observer = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        var observed = AuthorityFiles.Read(repo.DirectoryPath, repo.Identity, name);
                        Assert.True(observed == old || observed == next); Interlocked.Increment(ref valid);
                    }
                    catch (IOException error) { lock (errors) errors[error.GetType().Name + ":" + (error.HResult & 0xffff)] = errors.GetValueOrDefault(error.GetType().Name + ":" + (error.HResult & 0xffff)) + 1; }
                }
            });
            try
            {
                for (var i = 0; i < 100; i++)
                {
                    var candidate = Path.Combine(repo.DirectoryPath, "visibility-candidate.tmp");
                    AuthorityFiles.WriteNew(candidate, states[i % 2]);
                    var ok = mode == "ReplaceFileW" ? ReplaceFile(target, candidate, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero) :
                        MoveFileEx(candidate, target, 1);
                    if (!ok)
                    {
                        var error = Marshal.GetLastPInvokeError(); mutations[error] = mutations.GetValueOrDefault(error) + 1;
                        // The next independent diagnostic replacement is not a retry of repository publication.
                        if (File.Exists(candidate)) File.Delete(candidate);
                    }
                    else replacements++;
                }
            }
            finally { stop.Cancel(); await observer.WaitAsync(TimeSpan.FromSeconds(30)); }
            var result = new { Primitive = mode, Replacements = replacements, Valid = valid, Errors = errors, ReplacementErrors = mutations };
            results.Add(result); new SmbNetworkMeasurementTests(output).Save("authority-" + mode, result);
            if (mode == "MoveFileExW") { Assert.True(valid > 0); Assert.Empty(errors); }
        }
        new SmbNetworkMeasurementTests(output).Save("authority-primitives", results);
    }
    [DllImport("kernel32.dll", EntryPoint = "ReplaceFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReplaceFile(string target, string replacement, IntPtr backup, uint flags, IntPtr exclude, IntPtr reserved);
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveFileEx(string source, string target, uint flags);
}
