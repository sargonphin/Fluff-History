using System.Security.Cryptography;

namespace FluffHistory.Storage;

public sealed partial class Repository
{
    // Ready is an authenticated physical commit preparation, not another history authority. Once
    // its verified target header is exposed, older clients may legitimately advance target history.
    private static EncryptionConversionResult CompleteEncryptionConversion(string directory, EncryptionMigrationControl control,
        ReadOnlySpan<char> password, EncryptionConversionOptions options, IProgress<EncryptionConversionProgress>? progress, CancellationToken token)
    {
        var bootstrap = RepositoryBootstrap.Parse(Convert.FromBase64String(control.TargetBootstrap));
        using var target = new Repository(directory, bootstrap, UnlockMaintenance(bootstrap, password), Convert.FromBase64String(control.TargetState!));
        using var sourceProof = control.SourceEncrypted ? UnlockMaintenance(RepositoryBootstrap.Parse(Convert.FromBase64String(control.SourceBootstrap)), password) : null;
        using var writer = target.OpenWriter(options.WorkspaceRoot, streamMetadataReads: true);
        var result = CompleteEncryptionConversionOwned(target, writer, control, sourceProof ?? target.Crypto!, password, options, token);
        progress?.Report(new("Completed", control.TargetEncrypted, control.LogicalBytes, control.LogicalBytes, control.VersionCount, control.VersionCount));
        return result;
    }

    private static EncryptionConversionResult CompleteEncryptionConversionOwned(Repository target, RepositoryWriter writer,
        EncryptionMigrationControl control, RepositoryCryptography proofKey, ReadOnlySpan<char> password,
        EncryptionConversionOptions options, CancellationToken token)
    {
        var directory = target.DirectoryPath;
        VerifyControl();
        var proof = proofKey.Unprotect(Convert.FromBase64String(control.CompletionProof!), "state", control.CompletionContext());
        try { Format.Require(CryptographicOperations.FixedTimeEquals(proof, control.CompletionBinding()), "conversion authenticated completion binding"); }
        finally { CryptographicOperations.ZeroMemory(proof); }
        var intendedBootstrap = RepositoryBootstrap.Parse(Convert.FromBase64String(control.TargetBootstrap));
        var installedBytes = AuthorityFiles.ReadSmall(Path.Combine(directory, "repository.json"));
        var fenced = installedBytes.AsSpan().SequenceEqual(control.FenceBytes());
        if (!fenced) RequireTargetHeader(RepositoryBootstrap.Parse(installedBytes));
        var expectedState = Convert.FromBase64String(control.TargetState!);
        var currentState = AuthorityFiles.ReadSmall(Path.Combine(directory, "state.json"));
        var expected = AuthorityFiles.RetainedIdentity(expectedState, target.Identity, target.Crypto);
        target._maintenanceState = currentState;
        using (var reader = target.OpenReader())
        {
            var current = reader.Authority;
            Format.Require(fenced ? currentState.AsSpan().SequenceEqual(expectedState) :
                current.Generation > expected.Generation && current.MutationSequence >= expected.MutationSequence ||
                current == expected && currentState.AsSpan().SequenceEqual(expectedState), "completed conversion target authority");
            if (current == expected) Format.Require(SemanticDigest(reader, token) == control.SemanticDigest, "completed conversion semantic equality");
            foreach (var version in Surviving(reader)) VerifyPayload(target, version, target.GetPayloadPath(version.Identity.VersionId), token);
        }
        var advanced = !currentState.AsSpan().SequenceEqual(expectedState);
        if (fenced)
        {
            token.ThrowIfCancellationRequested();
            if (target.IsEncrypted && OperatingSystem.IsWindows())
            {
                try
                {
                    using var local = intendedBootstrap.UnlockWindows();
                    var challenge = target.Crypto!.Protect("conversion-key-continuity"u8, "conversion-key-test");
                    Format.Require(local.Unprotect(challenge, "conversion-key-test").AsSpan().SequenceEqual("conversion-key-continuity"u8), "completed target DPAPI master");
                }
                catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
                {
                    intendedBootstrap = intendedBootstrap with { WindowsUserKey = target.Crypto!.ProtectCurrentWindowsUser() };
                    using var confirmed = intendedBootstrap.UnlockWindows();
                    control = control with { TargetBootstrap = Convert.ToBase64String(intendedBootstrap.Bytes()), CompletionProof = null };
                    control = control with { CompletionProof = Convert.ToBase64String(proofKey.Protect(control.CompletionBinding(), "state", control.CompletionContext())) };
                    control.Install(directory, false);
                }
            }
            var name = "encryption-pivot-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                AuthorityFiles.WriteNew(Path.Combine(directory, name), intendedBootstrap.Bytes());
                VerifyControl();
                Format.Require(AuthorityFiles.ReadSmall(Path.Combine(directory, "repository.json")).AsSpan().SequenceEqual(control.FenceBytes()), "conversion final header fence");
                options.Observed?.Invoke(EncryptionConversionStage.BeforeBootstrapPivot);
                try
                {
                    AuthorityFiles.Replace(directory, name, "repository.json");
                    options.Observed?.Invoke(EncryptionConversionStage.AfterBootstrapPivot);
                }
                catch
                {
                    if (!AuthorityFiles.ReadSmall(Path.Combine(directory, "repository.json")).AsSpan().SequenceEqual(intendedBootstrap.Bytes())) throw;
                }
                Format.Require(AuthorityFiles.ReadSmall(Path.Combine(directory, "repository.json")).AsSpan().SequenceEqual(intendedBootstrap.Bytes()), "conversion final header readback");
            }
            finally
            {
                try { File.Delete(Path.Combine(directory, name)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        // The header commit has occurred. Pausing cleanup is still safe; it never rolls data back.
        // A new-profile resume enrolls the actual target wrapper.
        if (target.IsEncrypted && OperatingSystem.IsWindows())
        {
            bool enroll;
            try
            {
                using var local = RepositoryBootstrap.Read(directory).UnlockWindows();
                _ = AuthorityFiles.Read(directory, target.Identity, crypto: local); enroll = false;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException) { enroll = true; }
            if (enroll) writer.EnrollCompletedTarget(target);
        }
        VerifyControl(); RequireTargetHeader(RepositoryBootstrap.Read(directory));
        Format.Require(AuthorityFiles.Read(directory, target.Identity, crypto: target.Crypto).Identity == target.ReadAuthority().Identity, "completed target readback");
        token.ThrowIfCancellationRequested();
        options.Observed?.Invoke(EncryptionConversionStage.BeforeMarkerRemoval);
        try { File.Delete(Path.Combine(directory, EncryptionMigrationControl.FileName)); }
        catch { if (EncryptionMigrationControl.Exists(directory)) throw; }
        options.Observed?.Invoke(EncryptionConversionStage.AfterMarkerRemoval);
        Format.Require(!EncryptionMigrationControl.Exists(directory), "completed conversion marker retirement");
        using (var ordinary = target.IsEncrypted ? OperatingSystem.IsWindows() ? OpenWithWindowsUser(directory) : OpenWithPassword(directory, password) : Open(directory))
        using (var reader = ordinary.OpenReader()) Format.Require(reader.Authority == target.ReadAuthority().Identity, "converted ordinary reopen");
        return new(control.TargetEncrypted, control.SemanticDigest, control.VersionCount, control.LogicalBytes, control.SourcePhysicalBytes, control.TargetPhysicalBytes)
            { PostCommitAuthorityAdvanced = advanced };

        void VerifyControl()
        {
            writer.CheckConversionOwnership();
            Format.Require(EncryptionMigrationControl.Read(directory).Bytes().AsSpan().SequenceEqual(control.Bytes()), "completed conversion control changed");
        }
        void RequireTargetHeader(RepositoryBootstrap actual) => Format.Require(
            (actual with { WindowsUserKey = null }) == (intendedBootstrap with { WindowsUserKey = null }), "completed conversion target header");
    }
}

public sealed partial class RepositoryWriter
{
    internal void CheckConversionOwnership() => Check();
    internal void EnrollCompletedTarget(Repository target)
    {
        Format.Require(ReferenceEquals(_repository, target), "completed enrollment owner");
        EnrollCurrentWindowsUser(CancellationToken.None);
    }
}
