using FluffHistory.Core;

namespace FluffHistory.Storage;

public sealed partial class RepositoryWriter
{
    /// <summary>Conservative optional-maintenance admission, never a reservation or authority to
    /// delete history. Charge complete output/staging/scratch budgets and retain 5% free on BOTH
    /// caller-visible volumes, pessimistically charging the same amount if they are distinct.</summary>
    public bool HasMaintenanceHeadroom(MaintenanceLimits limits)
    {
        SemanticHistoryFold.ValidateLimits(limits);
        using var reader = OpenCapacityReader(_repository);
        var workspace = RepositoryLayout.NormalizeDirectory(_workspaceRoot ?? Path.GetTempPath());
        var unit = _repository.ObserveAllocationGeometry(workspace).AllocationUnit;
        while (!Directory.Exists(workspace)) workspace = Path.GetDirectoryName(workspace) ?? throw new IOException("No workspace ancestor.");
        var controls = checked(3 * AllocationGeometry.RoundUp(AuthorityFiles.MaximumControlBytes + 64, unit) + 512 * unit);
        var allocation = AllocationGeometry.RoundUp(checked(3 * limits.MaximumOutputBytes + limits.MaximumScratchBytes +
            (_streamMetadataReads ? 0 : limits.MaximumMaterializedBytes) + controls), unit);
        var target = new CapacityTarget(_repository.ObserveCapacity(), allocation);
        var temporary = new CapacityTarget(RepositoryCapacity.ObserveDirectory(workspace), allocation);
        Check(); Format.Require(_repository.ReadAuthority().Identity == reader.Authority, "maintenance admission authority changed");
        return target.Satisfied && temporary.Satisfied;
    }
}
