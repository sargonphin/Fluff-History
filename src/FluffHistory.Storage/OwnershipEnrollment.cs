namespace FluffHistory.Storage;

/// <summary>The replacement was attempted but not confirmed. Reopen using Windows or password recovery;
/// an error does not prove that the old Windows slot is still installed. Never retry blindly.</summary>
public sealed class OwnershipEnrollmentUncertainException(Exception inner)
    : IOException("Windows ownership enrollment was not confirmed. Reopen with Windows or password recovery before explicitly retrying.", inner);

internal enum EnrollmentStage { CandidateFlushed, BeforeReplace, AfterReplace }

public sealed partial class Repository
{
    internal Action<EnrollmentStage>? EnrollmentObserved { get; set; }

    /// <summary>Explicitly replace the local convenience wrapper with user-scoped DPAPI for the calling
    /// Windows identity. Requires authenticated state/root and exclusive writer ownership. Password
    /// recovery and all history bytes stay unchanged. Password open itself never enrolls. A successful
    /// call confirms readback; replacement/confirmation errors have an explicitly uncertain outcome.</summary>
    public void EnrollCurrentWindowsUser(CancellationToken cancellationToken = default)
    {
        CheckUnlocked();
        if (!IsEncrypted) throw new InvalidOperationException("Unencrypted repositories do not have ownership key slots.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows ownership enrollment requires user-scoped DPAPI.");
        cancellationToken.ThrowIfCancellationRequested();
        using var writer = OpenWriter();
        writer.EnrollCurrentWindowsUser(cancellationToken);
    }
}

public sealed partial class RepositoryWriter
{
    internal void EnrollCurrentWindowsUser(CancellationToken token)
    {
        string? candidate = null; var replacementAttempted = false;
        try
        {
            Check(); RequireNoCandidates(); RequireIdle(); token.ThrowIfCancellationRequested();
            // Password unwrap alone is insufficient: re-authenticate current state and root under ownership.
            var authority = _repository.ReadAuthority();
            var prior = RepositoryBootstrap.Read(_repository.DirectoryPath);
            var intended = prior with { WindowsUserKey = _repository.Crypto!.ProtectCurrentWindowsUser() };
            var bytes = intended.Bytes();
            Format.Require(bytes.Length <= AuthorityFiles.MaximumControlBytes, "bootstrap enrollment bound");
            var name = $"bootstrap-{Guid.NewGuid():N}.tmp";
            var path = Path.Combine(_repository.DirectoryPath, name);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                candidate = name; // Only a successfully created candidate is ours to clean.
                stream.Write(bytes); stream.Flush(true);
            }
            Format.Require(AuthorityFiles.ReadSmall(path).AsSpan().SequenceEqual(bytes), "ownership candidate readback");
            _repository.EnrollmentObserved?.Invoke(EnrollmentStage.CandidateFlushed);
            token.ThrowIfCancellationRequested(); Check();
            Format.Require(RepositoryBootstrap.Read(_repository.DirectoryPath) == prior, "bootstrap changed during enrollment");
            Format.Require(_repository.ReadAuthority().Identity == authority.Identity, "authority changed during enrollment");
            _repository.EnrollmentObserved?.Invoke(EnrollmentStage.BeforeReplace);
            token.ThrowIfCancellationRequested(); Check();
            replacementAttempted = true;
            AuthorityFiles.Replace(_repository.DirectoryPath, candidate, "repository.json");
            _repository.EnrollmentObserved?.Invoke(EnrollmentStage.AfterReplace);
            Check();
            Format.Require(AuthorityFiles.ReadSmall(Path.Combine(_repository.DirectoryPath, "repository.json")).AsSpan().SequenceEqual(bytes), "ownership bootstrap readback");
            var actual = RepositoryBootstrap.Read(_repository.DirectoryPath);
            Format.Require(actual == intended, "ownership bootstrap identity");
            using var confirmed = actual.UnlockWindows();
            Format.Require(AuthorityFiles.Read(_repository.DirectoryPath, _repository.Identity, crypto: confirmed).Identity == authority.Identity,
                "ownership authenticated authority readback");
        }
        catch (Exception error)
        {
            _aborted = true;
            if (replacementAttempted) throw new OwnershipEnrollmentUncertainException(error);
            throw;
        }
        finally
        {
            if (candidate is not null)
                try { File.Delete(Path.Combine(_repository.DirectoryPath, candidate)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }
}
