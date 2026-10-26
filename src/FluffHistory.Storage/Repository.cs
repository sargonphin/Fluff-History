using FluffHistory.Core;

namespace FluffHistory.Storage;

/// <summary>Explicit format-v4 entry point. Never migrates, initializes on open, or repairs.</summary>
public sealed partial class Repository : IDisposable
{
    /// <summary>Normalized destination location, for validating source/destination separation.</summary>
    public string DirectoryPath { get; }
    public RepositoryIdentity Identity { get; }
    internal RepositoryCryptography? Crypto { get; }
    private readonly RepositoryBootstrap _bootstrap;
    private byte[]? _maintenanceState;
    private bool _disposed;
    public bool IsEncrypted => _bootstrap.Encrypted;
    public bool IsLocked => IsEncrypted && Crypto is null;
    private Repository(string directory, RepositoryBootstrap bootstrap, RepositoryCryptography? crypto = null, byte[]? maintenanceState = null)
    { DirectoryPath = directory; Identity = bootstrap.Identity; _bootstrap = bootstrap; Crypto = crypto; _maintenanceState = maintenanceState; }
    public static Repository Create(string directory, CancellationToken cancellationToken = default)
        => CreateCore(directory, null, null, cancellationToken);
    public static Repository CreateEncrypted(string directory, ReadOnlySpan<char> recoveryPassword,
        bool useWindowsUserProtector = false, CancellationToken cancellationToken = default)
    {
        var identity = new RepositoryIdentity(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var (bootstrap, crypto) = RepositoryBootstrap.Create(identity, recoveryPassword, useWindowsUserProtector);
        try { return CreateCore(directory, bootstrap, crypto, cancellationToken); }
        catch { crypto.Dispose(); throw; }
    }
    private static Repository CreateCore(string directory, RepositoryBootstrap? bootstrap, RepositoryCryptography? crypto, CancellationToken cancellationToken)
    {
        directory = RepositoryLayout.NormalizeDirectory(directory); cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(directory) && !RepositoryDestinationPolicy.IsEffectivelyEmpty(directory))
            throw new IOException("V4 initialization requires an absent or empty destination. No existing data was changed.");
        Directory.CreateDirectory(directory);
        using var handle = new FileStream(Path.Combine(directory, "writer.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        if (!RepositoryDestinationPolicy.IsEffectivelyEmpty(directory, ignoreOwnedWriterLock: true))
            throw new IOException("Destination changed during initialization.");
        Directory.CreateDirectory(Path.Combine(directory, "metadata")); Directory.CreateDirectory(Path.Combine(directory, "versions"));
        bootstrap ??= RepositoryBootstrap.Plain(new(Guid.NewGuid(), DateTimeOffset.UtcNow));
        var identity = bootstrap.Identity; var epoch = Guid.NewGuid();
        using (var append = new SegmentAppender(directory, identity, epoch, null, crypto: crypto))
        {
            var root = new MetadataRoot(0, 0, Guid.NewGuid(), epoch, -1, Guid.Empty, new string('0', 64), null, null, null, null, null);
            var pointer = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root)); append.FlushAndValidate();
            AuthorityFiles.WriteNew(Path.Combine(directory, "state.json"), AuthorityFiles.StateBytes(new(identity, root, pointer), crypto));
        }
        AuthorityFiles.WriteNew(Path.Combine(directory, "repository.json.tmp"), bootstrap.Bytes());
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(Path.Combine(directory, "repository.json.tmp"), Path.Combine(directory, "repository.json"));
        return OpenCore(directory, bootstrap, crypto); // Header installation is initialization's publication boundary.
    }
    public static Repository? Discover(string directory)
    {
        directory = RepositoryLayout.NormalizeDirectory(directory);
        EncryptionMigrationControl.RefuseOrdinaryOpen(directory);
        if (!Directory.Exists(directory) || !Directory.EnumerateFileSystemEntries(directory).Any(p => Path.GetFileName(p) == "repository.json")) return null;
        var bootstrap = RepositoryBootstrap.Read(directory);
        return bootstrap.Encrypted ? new(directory, bootstrap) : OpenCore(directory, bootstrap, null);
    }
    public static Repository Open(string directory)
    {
        directory = RepositoryLayout.NormalizeDirectory(directory);
        EncryptionMigrationControl.RefuseOrdinaryOpen(directory);
        var bootstrap = RepositoryBootstrap.Read(directory);
        if (bootstrap.Encrypted) throw new RepositoryLockedException();
        return OpenCore(directory, bootstrap, null);
    }
    public static Repository OpenWithPassword(string directory, ReadOnlySpan<char> recoveryPassword)
    {
        directory = RepositoryLayout.NormalizeDirectory(directory); EncryptionMigrationControl.RefuseOrdinaryOpen(directory); var b = RepositoryBootstrap.Read(directory);
        RepositoryCryptography crypto;
        try { crypto = b.Unlock(recoveryPassword); }
        catch (InvalidDataException error) when (error.InnerException is System.Security.Cryptography.CryptographicException)
        { throw new RecoveryPasswordAuthenticationException(error); }
        try { return OpenCore(directory, b, crypto); } catch { crypto.Dispose(); throw; }
    }
    public static Repository OpenWithWindowsUser(string directory)
    {
        directory = RepositoryLayout.NormalizeDirectory(directory); EncryptionMigrationControl.RefuseOrdinaryOpen(directory); var b = RepositoryBootstrap.Read(directory);
        RepositoryCryptography crypto;
        try { crypto = b.UnlockWindows(); }
        catch (Exception error) when (b.Encrypted && (error is IOException or InvalidDataException or RepositoryLockedException or PlatformNotSupportedException))
        { throw new WindowsUserProtectorUnavailableException(error); }
        try { return OpenCore(directory, b, crypto); } catch { crypto.Dispose(); throw; }
    }
    private static Repository OpenCore(string directory, RepositoryBootstrap bootstrap, RepositoryCryptography? crypto)
    {
        EncryptionMigrationControl.RefuseOrdinaryOpen(directory);
        var identity = bootstrap.Identity;
        RepositoryLayout.RequireFile(Path.Combine(directory, "writer.lock"));
        foreach (var name in new[] { "metadata", "versions" }) Format.Require(RepositoryLayout.IsExistingDirectory(Path.Combine(directory, name)), "missing v4 directory");
        _ = AuthorityFiles.Read(directory, identity, crypto: crypto); return new(directory, bootstrap, crypto);
    }
    internal void CheckUnlocked()
    { ObjectDisposedException.ThrowIf(_disposed, this); if (IsLocked) throw new RepositoryLockedException(); }
    internal void CheckBootstrap()
    {
        if (_maintenanceState is not null) { CheckUnlocked(); return; }
        CheckUnlocked(); RepositoryBootstrap actual;
        try { actual = RepositoryBootstrap.Read(DirectoryPath); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        { EncryptionMigrationControl.RefuseOrdinaryOpen(DirectoryPath); throw; }
        if (actual.Identity == Identity && actual.Encrypted != IsEncrypted) throw new ReaderGenerationExpiredException();
        // Recovery and Windows slots wrap the same master and are excluded from object binding.
        Format.Require((actual with { Recovery = null, WindowsUserKey = null }) ==
            (_bootstrap with { Recovery = null, WindowsUserKey = null }), "repository bootstrap changed");
    }
    internal Authority ReadAuthority()
    {
        _ = RepositoryLayout.NormalizeDirectory(DirectoryPath);
        if (_maintenanceState is null) EncryptionMigrationControl.RefuseOrdinaryOpen(DirectoryPath);
        CheckBootstrap();
        if (_maintenanceState is not null) return AuthorityFiles.ReadBytes(DirectoryPath, Identity, _maintenanceState, Crypto);
        return AuthorityFiles.Read(DirectoryPath, Identity, crypto: Crypto);
    }
    public RepositoryReader OpenReader() => new(this, ReadAuthority());
    /// <summary>Native calling-identity capacity/free bytes on this repository's filesystem. No alternate probe path.</summary>
    public CapacityObservation ObserveCapacity() => RepositoryCapacity.Observe(this);
    /// <summary>Captures comparison authority. EnumerateCurrentComparison uses bounded range reads;
    /// full metadata queries may cache physical ranges on disk and decoded blocks in bounded RAM. Recovery needs neither cache.</summary>
    public RepositoryReader OpenComparisonReader(string? workspaceRoot = null, CancellationToken cancellationToken = default)
    {
        var workspace = new MetadataWorkspace(this, workspaceRoot, cancellationToken);
        try { return new(this, ReadAuthority(), workspace, true); }
        catch { workspace.Dispose(); throw; }
    }
    internal RepositoryReader OpenWorkingReader(MetadataWorkspace workspace) => new(this, ReadAuthority(), workspace);
    public RepositoryWriter OpenWriter() { CheckUnlocked(); return new(this); }
    public RepositoryWriter OpenWriter(string? workspaceRoot, bool streamMetadataReads = false) { CheckUnlocked(); return new(this, workspaceRoot, streamMetadataReads); }
    public long EstimatePayloadPhysicalBytes(long logicalLength) => Crypto is null ? logicalLength : EncryptedPayload.PhysicalLength(logicalLength);
    public void Dispose() { if (_disposed) return; _disposed = true; Crypto?.Dispose(); }
    public string GetPayloadPath(Guid versionId)
    {
        if (versionId == Guid.Empty) throw new ArgumentException("A generated version ID is required.", nameof(versionId));
        var id = versionId.ToString("N");
        Format.Require(RepositoryLayout.IsExistingDirectory(Path.Combine(DirectoryPath, "versions")), "missing payload directory");
        _ = RepositoryLayout.IsExistingDirectory(Path.Combine(DirectoryPath, "versions", id[..2]));
        return Path.Combine(DirectoryPath, "versions", id[..2], id + ".bin");
    }
}

/// <summary>A single captured authority generation; dispose after enumeration. Not thread safe.</summary>
public sealed partial class RepositoryReader : IDisposable
{
    private readonly Repository _repository;
    internal Authority Captured { get; }
    internal SegmentReader Segments { get; }
    internal OrderedIndex CurrentIndex { get; }
    internal OrderedIndex VersionIndex { get; }
    internal OrderedIndex SourceIndex { get; }
    private bool _disposed;
    private readonly bool _ownsWorkspace;
    internal MetadataWorkspace? Workspace { get; }
    internal RepositoryReader(Repository repository, Authority authority, MetadataWorkspace? workspace = null, bool ownsWorkspace = false)
    {
        _repository = repository; Captured = authority; Workspace = workspace; _ownsWorkspace = ownsWorkspace;
        Segments = new(repository.DirectoryPath, repository.Identity, authority.Root.Epoch, authority.Address, workspace, crypto: repository.Crypto);
        CurrentIndex = new(IndexKind.Current, Read); VersionIndex = new(IndexKind.Versions, Read); SourceIndex = new(IndexKind.Sources, Read);
    }
    public AuthorityIdentity Authority => Captured.Identity;
    internal byte[] Read(FrameAddress address) { ObjectDisposedException.ThrowIf(_disposed, this); return Segments.Read(address); }
    internal SemanticRecord Semantic(FrameAddress address)
    {
        MetadataCodec.RequireKind(address, FrameKind.Semantic); var record = MetadataCodec.DecodeSemantic(Read(address));
        foreach (var pointer in new[] { record.Previous, record.VersionAddress })
            if (pointer is not null) { Segments.CheckAddress(pointer); Format.Require(pointer.Serial < address.Serial, "semantic forward/cycle"); }
        if (record.Version is not null) Format.Require(record.Version.Identity.Sequence <= Captured.Root.Sequence, "future version sequence");
        if (record.RemovedVersion is not null) Format.Require(record.RemovedVersion.Sequence <= Captured.Root.Sequence, "future removed version sequence");
        return record;
    }
    internal StoredVersion Version(FrameAddress address, HistoryPath? expected = null, byte[]? versionKey = null)
    {
        var record = Semantic(address); Format.Require(record.Kind == SemanticKind.VersionAdded, "version reference type"); var v = record.Version!;
        Format.Require(expected is null || v.Identity.Path == expected, "version path binding");
        Format.Require(versionKey is null || PathKeys.Version(v.Identity).AsSpan().SequenceEqual(versionKey), "version ordering binding"); return v;
    }
    public StoredVersion? GetCurrent(HistoryPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var address = CurrentIndex.Find(Captured.Root.Current, PathKeys.Current(path));
        return address is null ? null : Version(address, path);
    }
    public IEnumerable<StoredVersion> EnumerateCurrent(Guid? sourceRootId = null, string folder = "")
    {
        if (sourceRootId is null && folder.Length != 0) throw new ArgumentException("A folder requires a source root.", nameof(folder));
        var lower = sourceRootId is null ? null : PathKeys.Scope(sourceRootId.Value, folder);
        foreach (var e in CurrentIndex.Range(Captured.Root.Current, lower, lower is null ? null : PathKeys.ScopeUpperBound(lower)))
            yield return Version(e.Value, PathKeys.DecodePath(e.Key));
    }
    public IEnumerable<StoredVersion> EnumerateVersions(HistoryPath path, bool newestFirst = false)
    {
        var prefix = PathKeys.Current(path);
        foreach (var e in VersionIndex.Range(Captured.Root.Versions, prefix, PathKeys.UpperBound(prefix), newestFirst)) yield return Version(e.Value, path, e.Key);
    }
    public StoredVersion? GetNewestVersion(HistoryPath path) => EnumerateVersions(path, true).FirstOrDefault();
    public SourceRootConfiguration? GetSourceRoot(Guid id)
    {
        var address = SourceIndex.Find(Captured.Root.Sources, PathKeys.Source(id));
        if (address is null) return null;
        var record = Semantic(address); Format.Require(record.Kind == SemanticKind.SourceRoot && record.Source!.SourceRootId == id, "source index binding"); return record.Source;
    }
    public IEnumerable<SourceRootConfiguration> EnumerateSourceRoots()
    {
        foreach (var e in SourceIndex.Range(Captured.Root.Sources))
        {
            var record = Semantic(e.Value); Format.Require(record.Kind == SemanticKind.SourceRoot && PathKeys.Source(record.Source!.SourceRootId).AsSpan().SequenceEqual(e.Key), "source index binding");
            yield return record.Source!;
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; DropTimelineSnapshot(); _timelineScope = null; Segments.Dispose();
        foreach (var session in _comparisonSessions) session.Dispose();
        _comparisonSessions.Clear();
        if (_ownsWorkspace) Workspace?.Dispose();
    }
}
