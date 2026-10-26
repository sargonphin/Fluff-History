using System.Runtime.ExceptionServices;
using FluffHistory.Core;

namespace FluffHistory.Storage;

public sealed class RecoveryPasswordChangeUncertainException(Exception inner)
    : IOException("The recovery password change could not be confirmed. Reopen this backup before retrying.", inner);

internal enum PasswordChangeStage { CandidateCreated, CandidateFlushed, BeforeReplace, AfterReplace, BeforeConfirmation }

public sealed partial class Repository
{
    internal Action<PasswordChangeStage>? PasswordChangeObserved { get; set; }

    /// <summary>Rewrap the existing master under exclusive writer ownership. No history or data
    /// rewrite. An authenticated intended installation is success even if replacement reported an
    /// error; an exact predecessor is failure; any other outcome is explicitly uncertain.</summary>
    public void ChangeRecoveryPassword(ReadOnlySpan<char> newPassword, CancellationToken token = default)
    {
        CheckUnlocked();
        if (!IsEncrypted) throw new InvalidOperationException("Unencrypted backups do not have recovery passwords.");
        if (newPassword.Length is < 1 or > 1024) throw new ArgumentException("Recovery password must contain 1..1024 characters.", nameof(newPassword));
        token.ThrowIfCancellationRequested();
        using var writer = OpenWriter();
        writer.ChangeRecoveryPassword(newPassword, token);
    }
}

public sealed partial class RepositoryWriter
{
    internal void ChangeRecoveryPassword(ReadOnlySpan<char> password, CancellationToken token)
    {
        string? candidate = null;
        try
        {
            Check(); RequireNoCandidates(); RequireIdle(); token.ThrowIfCancellationRequested();
            var authority = _repository.ReadAuthority();
            var prior = RepositoryBootstrap.Read(_repository.DirectoryPath);
            var priorBytes = AuthorityFiles.ReadSmall(Path.Combine(_repository.DirectoryPath, "repository.json"));
            var intended = prior.WithRecoveryPassword(_repository.Crypto!, password);
            var bytes = intended.Bytes();
            Format.Require(bytes.Length <= AuthorityFiles.MaximumControlBytes, "password bootstrap bound");
            // Confirm the candidate's new wrapper authenticates the existing state/root before publication.
            ConfirmPassword(intended, password, authority.Identity);
            token.ThrowIfCancellationRequested();
            var name = $"bootstrap-{Guid.NewGuid():N}.tmp";
            using (var stream = new FileStream(Path.Combine(_repository.DirectoryPath, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                candidate = name;
                _repository.PasswordChangeObserved?.Invoke(PasswordChangeStage.CandidateCreated);
                stream.Write(bytes); stream.Flush(true);
            }
            Format.Require(AuthorityFiles.ReadSmall(Path.Combine(_repository.DirectoryPath, candidate)).AsSpan().SequenceEqual(bytes), "password candidate readback");
            _repository.PasswordChangeObserved?.Invoke(PasswordChangeStage.CandidateFlushed);
            token.ThrowIfCancellationRequested(); Check();
            Format.Require(AuthorityFiles.ReadSmall(Path.Combine(_repository.DirectoryPath, "repository.json")).AsSpan().SequenceEqual(priorBytes), "password bootstrap predecessor changed");
            Format.Require(_repository.ReadAuthority().Identity == authority.Identity, "password authority changed");
            _repository.PasswordChangeObserved?.Invoke(PasswordChangeStage.BeforeReplace);
            token.ThrowIfCancellationRequested(); Check();
            Exception? replaceError = null;
            try
            {
                AuthorityFiles.Replace(_repository.DirectoryPath, candidate, "repository.json");
                _repository.PasswordChangeObserved?.Invoke(PasswordChangeStage.AfterReplace);
            }
            catch (Exception error) { replaceError = error; }
            try
            {
                Check();
                _repository.PasswordChangeObserved?.Invoke(PasswordChangeStage.BeforeConfirmation);
                var actual = AuthorityFiles.ReadSmall(Path.Combine(_repository.DirectoryPath, "repository.json"));
                if (actual.AsSpan().SequenceEqual(bytes)) { ConfirmPassword(RepositoryBootstrap.Read(_repository.DirectoryPath), password, authority.Identity); return; }
                if (!actual.AsSpan().SequenceEqual(priorBytes)) throw Format.Invalid("unrecognized password replacement outcome");
                // Exact predecessor is distinguishable from an unknown/failed confirmation.
            }
            catch (Exception error) { throw new RecoveryPasswordChangeUncertainException(error); }
            ExceptionDispatchInfo.Capture(replaceError ?? new IOException("Recovery password replacement retained the previous bootstrap.")).Throw();

        }
        catch { _aborted = true; throw; }
        finally
        {
            if (candidate is not null)
                try { File.Delete(Path.Combine(_repository.DirectoryPath, candidate)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }
    private void ConfirmPassword(RepositoryBootstrap bootstrap, ReadOnlySpan<char> password, AuthorityIdentity authority)
    {
        using var crypto = bootstrap.Unlock(password);
        Format.Require(AuthorityFiles.Read(_repository.DirectoryPath, _repository.Identity, crypto: crypto).Identity == authority,
            "password authenticated authority readback");
    }
}
