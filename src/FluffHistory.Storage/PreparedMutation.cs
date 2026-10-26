using FluffHistory.Core;

namespace FluffHistory.Storage;

/// <summary>Session-owned, non-authoritative publication. Required payloads may be installed before Commit.</summary>
public sealed class PreparedMutation : IDisposable
{
    private readonly RepositoryWriter _writer;
    internal Authority Baseline { get; }
    internal Authority Intended { get; }
    internal MetadataWorkspace Workspace { get; }
    internal bool OwnsWorkspace { get; }
    internal IReadOnlyList<PreparedVersion> Versions { get; }
    internal Action<RepositoryReader>? Confirm { get; }
    internal List<MetadataTransfer> Transfers { get; } = [];
    internal bool Completed { get; set; }
    internal string CandidateName => $"state.{Intended.Root.TransactionId:N}.tmp";
    internal string ControlName => $"prepare.{Intended.Root.TransactionId:N}.root";
    public AuthorityIdentity PredecessorAuthority => Baseline.Identity;
    public AuthorityIdentity IntendedAuthority => Intended.Identity;
    public bool Changed => Intended != Baseline;
    internal PreparedMutation(RepositoryWriter writer, Authority baseline, Authority intended, MetadataWorkspace workspace,
        bool ownsWorkspace, IReadOnlyList<PreparedVersion> versions, Action<RepositoryReader>? confirm)
    {
        _writer = writer; Baseline = baseline; Intended = intended; Workspace = workspace;
        OwnsWorkspace = ownsWorkspace; Versions = versions; Confirm = confirm;
    }
    public MutationResult Commit(CancellationToken cancellationToken = default) => _writer.Commit(this, cancellationToken);
    public void Dispose() => _writer.Discard(this);
    internal void CleanupLocal()
    {
        if (OwnsWorkspace) Workspace.Dispose();
    }
}

internal sealed record MetadataTransfer(SegmentHeader Header, long OriginalLength, long FinalLength, string TemporaryPath, string Sha256);
