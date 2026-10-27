using System.Reflection;
using System.Runtime.Loader;
using System.IO.Hashing;
using FluffHistory.Core;
using Xunit;

namespace FluffHistory.Tests.Infrastructure;

public sealed class LegacyStorageTheoryAttribute : TheoryAttribute
{
    public LegacyStorageTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLUFF_HISTORY_LEGACY_STORAGE_ROOT")))
            Skip = "Opt-in released-client compatibility: set FLUFF_HISTORY_LEGACY_STORAGE_ROOT to the untouched legacy payload.";
    }
}

/// <summary>Runs the actual previously delivered assemblies in an isolated loader, without shipping
/// or rebuilding old code. Never mixes legacy Core objects with the current API graph.</summary>
internal sealed class LegacyStorage : IDisposable
{
    private readonly AssemblyLoadContext _context = new("FluffHistory.Legacy." + Guid.NewGuid().ToString("N"), isCollectible: true);
    private readonly Assembly _storage, _core;
    private delegate object PasswordOpen(string path, ReadOnlySpan<char> password);
    private delegate void WriteBytes(ReadOnlySpan<byte> bytes);
    internal LegacyStorage()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("FLUFF_HISTORY_LEGACY_STORAGE_ROOT")!);
        _context.Resolving += (_, name) => name.Name?.StartsWith("FluffHistory.", StringComparison.Ordinal) == true
            ? _context.LoadFromAssemblyPath(Path.Combine(root, name.Name + ".dll")) : null;
        _storage = _context.LoadFromAssemblyPath(Path.Combine(root, "FluffHistory.Storage.dll"));
        _core = _context.LoadFromAssemblyPath(Path.Combine(root, "FluffHistory.Core.dll"));
        Assert.Null(_storage.GetType("FluffHistory.Storage.EncryptionMigrationControl"));
    }
    internal IDisposable Open(string path, bool encrypted)
    {
        var type = _storage.GetType("FluffHistory.Storage.Repository", true)!;
        return encrypted ? (IDisposable)type.GetMethod("OpenWithPassword")!.CreateDelegate<PasswordOpen>()(path, EncryptionTests.Password)
            : (IDisposable)type.GetMethod("Open")!.Invoke(null, [path])!;
    }
    internal IDisposable Writer(object repository) => (IDisposable)repository.GetType().GetMethod("OpenWriter", Type.EmptyTypes)!.Invoke(repository, null)!;
    internal void CompactAndClean(object repository)
    {
        using var writer = Writer(repository);
        writer.GetType().GetMethod("CompactMetadata")!.Invoke(writer, [null, CancellationToken.None]);
        writer.GetType().GetMethod("CleanupRetiredMetadata")!.Invoke(writer, [CancellationToken.None]);
    }
    internal void Add(object repository, string name)
    {
        using var writer = Writer(repository);
        using var reader = (IDisposable)repository.GetType().GetMethod("OpenReader")!.Invoke(repository, null)!;
        using var draft = (IDisposable)writer.GetType().GetMethod("CreatePayloadDraft")!.Invoke(writer, null)!;
        var bytes = "legacy writer after conversion"u8.ToArray();
        draft.GetType().GetMethod("Write")!.CreateDelegate<WriteBytes>(draft)(bytes);
        draft.GetType().GetMethod("CompleteCopy")!.Invoke(draft, [(long)bytes.Length]);
        var metadata = Activator.CreateInstance(_core.GetType("FluffHistory.Core.LogicalFileMetadata", true)!,
            [FoundationTests.Root, name, (long)bytes.Length, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive]);
        var checksum = Activator.CreateInstance(_core.GetType("FluffHistory.Core.XxHash64Checksum", true)!, [Convert.ToHexString(XxHash64.Hash(bytes))]);
        var prepared = draft.GetType().GetMethod("PrepareVersion")!.Invoke(draft, [metadata, FoundationTests.Time, checksum])!;
        var array = Array.CreateInstance(prepared.GetType(), 1); array.SetValue(prepared, 0);
        var batch = Activator.CreateInstance(_core.GetType("FluffHistory.Core.MutationBatch", true)!, [array, null, null, null]);
        var authority = reader.GetType().GetProperty("Authority")!.GetValue(reader);
        using var transaction = (IDisposable)writer.GetType().GetMethod("PrepareMutation")!.Invoke(writer, [batch, authority, CancellationToken.None])!;
        draft.GetType().GetMethod("Install")!.Invoke(draft, null);
        transaction.GetType().GetMethod("Commit")!.Invoke(transaction, [CancellationToken.None]);
    }
    public void Dispose() => _context.Unload();
}
