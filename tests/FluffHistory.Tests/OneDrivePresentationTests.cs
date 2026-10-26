using FluffHistory.Application;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed partial class PresentationTests
{
    [Theory]
    [InlineData(ExperimentalRecoveryState.Legacy, TextId.OneDrive_Legacy)]
    [InlineData(ExperimentalRecoveryState.ConfigurationRequired, TextId.OneDrive_NotConfigured)]
    public async Task OneDriveLegacyAndUnconfiguredStatusNeverOfferLegacyRecovery(ExperimentalRecoveryState state, TextId expected)
    {
        using var temp = new TemporaryDirectory();
        var owner = new Context
        {
            Settings = AppSettings.Empty with { RepositoryPath = temp.Child() },
            Inspection = new(RepositoryReadiness.RecoveryRequired, true, Guid.NewGuid()),
            SecurityInspector = _ => Task.FromResult(state)
        };
        var model = new ConfigurationViewModel(owner, new Dialogs()); await model.RefreshSecurityAsync();
        Assert.Equal(owner.Strings[expected], model.ExperimentalStatus);
        Assert.False(model.CanOfferExperimentalRecovery); Assert.False(model.RecoverExperimental.CanExecute(null));
        Assert.True(model.Recover.CanExecute(null)); // Independent password recovery remains visible.
    }

    [Fact]
    public async Task OneDriveRepairAndLocalDisableRequireSeparateExplicitConfirmations()
    {
        using var temp = new TemporaryDirectory(); var operations = new List<RepositorySecurityOperation>();
        var state = ExperimentalRecoveryState.Available;
        var owner = new Context
        {
            Settings = AppSettings.Empty with { RepositoryPath = temp.Child() },
            Inspection = new(RepositoryReadiness.Ready, true, Guid.NewGuid()),
            SecurityInspector = _ => Task.FromResult(state),
            SecurityExecutor = operation =>
            {
                operations.Add(operation);
                if (operation == RepositorySecurityOperation.DisableRoamingLocally)
                {
                    state = ExperimentalRecoveryState.Off;
                    return new(SecurityOperationOutcome.Success, Cleanup: OneDriveRecoveryCleanup.Remote, CleanupFileName: "synthetic-recovery.json");
                }
                return new(SecurityOperationOutcome.Success);
            }
        };
        var dialogs = new Dialogs { Confirm = false }; var model = new ConfigurationViewModel(owner, dialogs);
        await model.RefreshSecurityAsync();
        await model.RepairExperimental.ExecuteAsync(); await model.DisableExperimentalLocally.ExecuteAsync(); Assert.Empty(operations);
        dialogs.Confirm = true; await model.RepairExperimental.ExecuteAsync();
        Assert.Equal(RepositorySecurityOperation.RepairRoaming, Assert.Single(operations));
        await model.DisableExperimentalLocally.ExecuteAsync(); Assert.False(model.ExperimentalEnabled);
        Assert.Contains(owner.Strings[TextId.OneDrive_DisabledLocally], dialogs.Notices.Last());
        Assert.Contains(owner.Strings.Format(TextId.OneDrive_RemoteCleanupFile, "synthetic-recovery.json"), dialogs.Notices.Last());
    }

    [Theory]
    [InlineData(CloudRecoveryFailure.NetworkUnavailable, TextId.OneDrive_Offline)]
    [InlineData(CloudRecoveryFailure.ObjectNotFound, TextId.Security_CredentialNotAvailable)]
    [InlineData(CloudRecoveryFailure.PermissionUnavailable, TextId.OneDrive_PermissionUnavailable)]
    [InlineData(CloudRecoveryFailure.ConfigurationRequired, TextId.OneDrive_NotConfigured)]
    public async Task OneDriveFailuresUseResourceCopyWithoutRepositoryCorruptionClaims(CloudRecoveryFailure failure, TextId expected)
    {
        using var temp = new TemporaryDirectory();
        var owner = new Context
        {
            Settings = AppSettings.Empty with { RepositoryPath = temp.Child() },
            Inspection = new(RepositoryReadiness.Ready, true, Guid.NewGuid()),
            SecurityInspector = _ => Task.FromResult(ExperimentalRecoveryState.Available),
            SecurityExecutor = _ => new(SecurityOperationOutcome.Unavailable, failure)
        };
        var dialogs = new Dialogs(); var model = new ConfigurationViewModel(owner, dialogs); await model.RefreshSecurityAsync();
        await model.VerifyExperimental.ExecuteAsync();
        Assert.Equal(owner.Strings[expected], Assert.Single(dialogs.Notices)); Assert.Equal(RepositoryReadiness.Ready, owner.Inspection.State);
    }
}
