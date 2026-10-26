using Xunit;

namespace FluffHistory.Tests.Infrastructure;

public sealed class LargeDiagnosticRootTests
{
    [Theory]
    [InlineData(@"P:\")]
    [InlineData(@"p:\diagnostics\baseline")]
    [InlineData(@"P:\Projects\Fluff-History\fixture")]
    [InlineData(@"P:\Release\fixture")]
    public void WindowsPersistentRootsAreRejectedBeforeFilesystemObservation(string path)
    {
        var storage = new FakeStorage();
        var error = Assert.Throws<InvalidOperationException>(() => Admit(path, storage));
        Assert.Contains("disposable/high-capacity", error.Message);
        Assert.Contains("persistent storage", error.Message);
        Assert.Empty(storage.Resolved);
        Assert.Null(storage.CapacityDirectory);
    }

    [Theory]
    [InlineData(@"D:\source\Fluff-History")]
    [InlineData(@"d:\SOURCE\fluff-history\fixtures")]
    [InlineData(@"D:\source")]
    public void WorktreeAndItsDescendantsAndAncestorsAreRejected(string path)
    {
        var storage = new FakeStorage();
        Assert.Throws<InvalidOperationException>(() => Admit(path, storage));
        Assert.Empty(storage.Resolved);
    }

    [Theory]
    [InlineData(@"D:\Projects\Other\fixture")]
    [InlineData(@"D:\elsewhere\bin\fixture")]
    [InlineData(@"D:\elsewhere\obj\fixture")]
    [InlineData(@"D:\elsewhere\artifacts\fixture")]
    [InlineData(@"D:\Release\fixture")]
    [InlineData(@"D:\Build\fixture")]
    public void OtherClearlyUnsafeProjectAndOutputLocationsAreRejected(string path)
    {
        var storage = new FakeStorage();
        Assert.Throws<InvalidOperationException>(() => Admit(path, storage));
        Assert.Null(storage.CapacityDirectory);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(LargeDiagnosticRoot.MinimumAvailableBytes - 1)]
    public void MissingOrInsufficientHeadroomIsRejected(long available)
    {
        var storage = new FakeStorage { Free = available };
        var error = Assert.Throws<InvalidOperationException>(() => Admit(FakeStorage.SafeRoot, storage));
        Assert.Contains("16 GiB", error.Message);
        Assert.Equal(@"D:\diagnostics", storage.CapacityDirectory);
    }

    [Theory]
    [InlineData(LargeDiagnosticRoot.MinimumAvailableBytes)]
    [InlineData(64L * 1024 * 1024 * 1024)]
    public void ExplicitDisposableRootWithHeadroomIsAccepted(long available)
    {
        var storage = new FakeStorage { Free = available };
        Assert.Equal(FakeStorage.SafeRoot, Admit(FakeStorage.SafeRoot, storage));
        Assert.Equal(@"D:\diagnostics", storage.CapacityDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    public void ExactDiagnosticsOptInIsStillRequired(string? optIn)
    {
        var storage = new FakeStorage();
        Assert.Throws<InvalidOperationException>(() => LargeDiagnosticRoot.Admit(optIn, FakeStorage.SafeRoot, FakeStorage.Worktree, storage));
        Assert.Equal(0, storage.FullPathCalls);
        Assert.Empty(storage.Resolved);
    }

    [Fact]
    public void DiscoveryAttributesKeepTheirExistingOptInBehavior()
    {
        var enabled = Environment.GetEnvironmentVariable("FLUFF_HISTORY_DIAGNOSTICS") == "1";
        Assert.Equal(enabled, new MetadataDiagnosticFactAttribute().Skip is null);
        Assert.Equal(enabled, new MetadataDiagnosticTheoryAttribute().Skip is null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingExplicitRootIsRejected(string? root)
    {
        var storage = new FakeStorage();
        Assert.Throws<InvalidOperationException>(() => LargeDiagnosticRoot.Admit("1", root, FakeStorage.Worktree, storage));
        Assert.Equal(0, storage.FullPathCalls);
    }

    [Theory]
    [InlineData(@"P:\hidden\fixture", "other-volume")]
    [InlineData(@"D:\source\Fluff-History\fixture", "other-volume")]
    [InlineData(@"D:\elsewhere\obj\fixture", "other-volume")]
    [InlineData(FakeStorage.SafeRoot, "persistent-volume")]
    [InlineData(FakeStorage.SafeRoot, null)]
    public void ResolvedAliasesCannotBypassPathOrPersistentVolumeChecks(string resolved, string? volume)
    {
        var storage = new FakeStorage { RootResolution = new(resolved, @"D:\diagnostics", volume) };
        Assert.Throws<InvalidOperationException>(() => Admit(FakeStorage.SafeRoot, storage));
        Assert.Null(storage.CapacityDirectory);
    }

    [Fact]
    public void ResolvedWorktreeAliasIsAlsoProtected()
    {
        var storage = new FakeStorage { SourceResolution = new(@"D:\diagnostics", @"D:\diagnostics", "disposable-volume") };
        Assert.Throws<InvalidOperationException>(() => Admit(FakeStorage.SafeRoot, storage));
        Assert.Null(storage.CapacityDirectory);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolutionAndCapacityErrorsFailClosed(bool resolutionFailure)
    {
        var storage = new FakeStorage { ResolutionFailure = resolutionFailure, CapacityFailure = !resolutionFailure };
        var error = Assert.Throws<InvalidOperationException>(() => Admit(FakeStorage.SafeRoot, storage));
        Assert.Contains("Cannot safely resolve", error.Message);
        Assert.Contains("disposable/high-capacity", error.Message);
    }

    [Fact]
    public void WorktreePrefixSiblingIsNotMistakenForDescendant()
    {
        const string sibling = @"D:\source\Fluff-History-diagnostics\fixture";
        var storage = new FakeStorage { RootResolution = new(sibling, @"D:\source", "disposable-volume") };
        Assert.Equal(sibling, Admit(sibling, storage));
    }

    [Fact]
    public void PortablePolicyAcceptsSafeRootAndRejectsWorktreeWithoutWindowsDriveRules()
    {
        var storage = new FakeStorage { Windows = false, RootResolution = new("/scratch/diagnostics/case", "/scratch", null),
            SourceResolution = new("/checkout/fluff", "/checkout/fluff", null) };
        Assert.Equal("/scratch/diagnostics/case", LargeDiagnosticRoot.Admit("1", "/scratch/diagnostics/case", "/checkout/fluff", storage));
        Assert.Throws<InvalidOperationException>(() => LargeDiagnosticRoot.Admit("1", "/checkout/fluff/fixture", "/checkout/fluff", storage));
    }

    [Fact]
    public void RealResolverNormalizesDotsAndFindsAncestorWithoutCreatingDestination()
    {
        using var temporary = new TemporaryDirectory();
        var storage = new LargeDiagnosticRoot.LocalStorage();
        var requested = Path.Combine(temporary.Root, "absent", "..", "future", "case");
        var resolution = storage.Resolve(storage.FullPath(requested));
        var existing = storage.Resolve(temporary.Root);
        Assert.Equal(Path.Combine(existing.Path, "future", "case"), resolution.Path);
        Assert.Equal(existing.Path, resolution.ExistingDirectory);
        Assert.False(Directory.Exists(Path.Combine(temporary.Root, "future")));
        Assert.True(storage.AvailableBytes(resolution.ExistingDirectory) >= 0);
    }

    [Fact]
    public void RealResolverRejectsExistingFileAncestorAndRelativeRoot()
    {
        using var temporary = new TemporaryDirectory();
        var file = temporary.Child("file"); File.WriteAllText(file, "small");
        var storage = new LargeDiagnosticRoot.LocalStorage();
        Assert.Throws<IOException>(() => storage.Resolve(file));
        Assert.Throws<ArgumentException>(() => storage.FullPath("relative/fixture"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshAndExistingPopulationsBothRequireCapacityBeforeAnyNewWrites(bool reuse)
    {
        using var temporary = new TemporaryDirectory();
        var source = Directory.CreateDirectory(temporary.Child("source")).FullName;
        var root = temporary.Child("diagnostics");
        if (reuse) Directory.CreateDirectory(Path.Combine(root, "baseline"));
        var before = TemporaryDirectory.Snapshot(temporary.Root);
        var storage = new CapacityStub { Free = LargeDiagnosticRoot.MinimumAvailableBytes - 1 };
        Assert.Throws<InvalidOperationException>(() => LargeDiagnosticRoot.Admit("1", root, source, storage, ["baseline", "work"]));
        Assert.Equal(before.OrderBy(pair => pair.Key), TemporaryDirectory.Snapshot(temporary.Root).OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData(".git")]
    [InlineData("Directory.Build.props")]
    [InlineData("Other.csproj")]
    [InlineData("Other.sln")]
    public void OtherWorktreesAndProjectsAreRejectedByExistingMarkers(string marker)
    {
        using var temporary = new TemporaryDirectory();
        var source = Directory.CreateDirectory(temporary.Child("source")).FullName;
        var other = Directory.CreateDirectory(temporary.Child("other-project")).FullName;
        File.WriteAllText(Path.Combine(other, marker), "marker");
        Assert.Throws<InvalidOperationException>(() => LargeDiagnosticRoot.Admit("1", Path.Combine(other, "fixture"), source, new CapacityStub()));
        Assert.False(Directory.Exists(Path.Combine(other, "fixture")));
    }

    [Fact]
    public void AdmittedRootAndChildrenAreCanonicalAndAdmissionCreatesNothing()
    {
        using var temporary = new TemporaryDirectory();
        var source = Directory.CreateDirectory(temporary.Child("source")).FullName;
        var root = temporary.Child("diagnostics");
        var storage = new CapacityStub();
        Assert.Equal(storage.Resolve(root).Path, LargeDiagnosticRoot.Admit("1", root, source, storage, ["baseline", "work"]));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void ExistingFixtureChildCannotRedirectOutsideSafeRoot()
    {
        using var temporary = new TemporaryDirectory();
        var source = Directory.CreateDirectory(temporary.Child("source")).FullName;
        var outside = Directory.CreateDirectory(temporary.Child("outside")).FullName;
        var root = Directory.CreateDirectory(temporary.Child("diagnostics")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(root, "baseline"), outside);
        var failure = Assert.Throws<InvalidOperationException>(() => LargeDiagnosticRoot.Admit("1", root, source, new CapacityStub(), ["baseline", "work"]));
        Assert.Contains("redirects outside", failure.Message);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public void RealResolverFollowsDirectoryLinksBeforeCheckingWorktree()
    {
        using var temporary = new TemporaryDirectory();
        var source = Directory.CreateDirectory(temporary.Child("source")).FullName;
        var alias = temporary.Child("alias"); Directory.CreateSymbolicLink(alias, source);
        var storage = new LargeDiagnosticRoot.LocalStorage();
        var requested = Path.Combine(alias, "future", "case");
        Assert.Equal(Path.Combine(source, "future", "case"), storage.Resolve(requested).Path);
        Assert.Throws<InvalidOperationException>(() => LargeDiagnosticRoot.Admit("1", requested, source, storage));
        Assert.False(Directory.Exists(Path.Combine(source, "future")));
    }

    private static string Admit(string path, FakeStorage storage) => LargeDiagnosticRoot.Admit("1", path, FakeStorage.Worktree, storage);

    private sealed class CapacityStub : LargeDiagnosticRoot.Storage
    {
        private readonly LargeDiagnosticRoot.LocalStorage _local = new();
        internal long Free = LargeDiagnosticRoot.MinimumAvailableBytes;
        internal override bool IsWindows => _local.IsWindows;
        internal override string FullPath(string path) => _local.FullPath(path);
        internal override LargeDiagnosticRoot.Resolution Resolve(string fullPath) => _local.Resolve(fullPath);
        internal override long AvailableBytes(string existingDirectory) => Free;
        internal override string? PersistentVolume => _local.PersistentVolume;
        internal override bool HasProjectAncestor(string directory) => _local.HasProjectAncestor(directory);
    }

    private sealed class FakeStorage : LargeDiagnosticRoot.Storage
    {
        internal const string Worktree = @"D:\source\Fluff-History";
        internal const string SafeRoot = @"D:\diagnostics\future\case";
        internal bool Windows = true;
        internal long Free = LargeDiagnosticRoot.MinimumAvailableBytes;
        internal int FullPathCalls;
        internal readonly List<string> Resolved = [];
        internal string? CapacityDirectory;
        internal bool ResolutionFailure, CapacityFailure;
        internal LargeDiagnosticRoot.Resolution RootResolution = new(SafeRoot, @"D:\diagnostics", "disposable-volume");
        internal LargeDiagnosticRoot.Resolution SourceResolution = new(Worktree, Worktree, "disposable-volume");
        internal override bool IsWindows => Windows;
        internal override string FullPath(string path) { FullPathCalls++; return path; }
        internal override string? PersistentVolume => "persistent-volume";
        internal override LargeDiagnosticRoot.Resolution Resolve(string fullPath)
        {
            Resolved.Add(fullPath);
            if (ResolutionFailure) throw new IOException("Cannot resolve fixture root.");
            return Resolved.Count == 1 ? RootResolution : SourceResolution;
        }
        internal override long AvailableBytes(string existingDirectory)
        {
            CapacityDirectory = existingDirectory;
            if (CapacityFailure) throw new IOException("No capacity observation.");
            return Free;
        }
    }
}
