namespace FluffHistory.Application;

public sealed class UpdateAdmissionResult(IDisposable? lease, UpdateFailure failure) : IDisposable
{
    public UpdateFailure Failure { get; } = failure;
    private IDisposable? _lease = lease;
    public UpdateAdmissionResult TransferOwnership()
    {
        var owned = Interlocked.Exchange(ref _lease, null) ?? throw new InvalidOperationException("Admission is no longer owned.");
        return new(owned, Failure);
    }
    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}
/// <summary>The existing mutation gate is the only admission authority. Cancellation is manual/confirmed only.</summary>
public sealed class UpdateInstallAdmission(BackupOperationGate gate, Func<bool> backupActive, Func<Task> cancelBackup,
    TimeProvider? clock = null, TimeSpan? cancellationTimeout = null)
{
    public async Task<UpdateAdmissionResult> AcquireAsync(bool manual, bool cancelConfirmed, CancellationToken token)
    {
        if (backupActive())
        {
            if (!manual || !cancelConfirmed) return new(null, UpdateFailure.BackupStillRunning);
            var time = clock ?? TimeProvider.System;
            using var deadline = new CancellationTokenSource(cancellationTimeout ?? TimeSpan.FromSeconds(20), time);
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            try
            {
                await cancelBackup().WaitAsync(waiting.Token).ConfigureAwait(false);
                // CancelActiveAsync acknowledges the request, not completion of blocked worker I/O.
                while (backupActive()) await Task.Delay(TimeSpan.FromMilliseconds(100), time, waiting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(null, UpdateFailure.BackupStillRunning); }
        }
        var lease = gate.TryAcquire(); return new(lease, lease is null ? UpdateFailure.Busy : UpdateFailure.None);
    }
}
