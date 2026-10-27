using FluffHistory.Application;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed partial class PresentationTests
{
    [Theory]
    [InlineData(false, "en-US")][InlineData(true, "en-US")][InlineData(true, "de-DE")]
    public async Task CompletedDecryptionOffersExplicitCleanupWithoutChangingActiveRecovery(bool encrypted, string language)
    {
        using var temp = new TemporaryDirectory(); var operations = new List<RepositorySecurityOperation>();
        var state = encrypted ? ExperimentalRecoveryState.AvailableWithCleanup : ExperimentalRecoveryState.CleanupRequired;
        var owner = new Context
        {
            Strings = new TestAppStrings(language),
            Settings = AppSettings.Empty with { RepositoryPath = temp.Child() },
            Inspection = new(RepositoryReadiness.Ready, encrypted, Guid.NewGuid()),
            SecurityInspector = _ => Task.FromResult(state),
            SecurityExecutor = operation =>
            { operations.Add(operation); state = encrypted ? ExperimentalRecoveryState.Available : ExperimentalRecoveryState.Off; return new(SecurityOperationOutcome.Success); }
        };
        var dialogs = new Dialogs(); var model = new ConfigurationViewModel(owner, dialogs); await model.RefreshSecurityAsync();
        Assert.True(model.HasPendingCloudCleanup); Assert.True(model.RetryCloudCleanup.CanExecute(null));
        Assert.Equal(encrypted, model.ToggleExperimental.CanExecute(null)); Assert.Empty(operations);
        await model.RetryCloudCleanup.ExecuteAsync();
        Assert.Equal(RepositorySecurityOperation.RetryCloudCleanup, Assert.Single(operations));
        Assert.False(model.HasPendingCloudCleanup);
        Assert.Equal(encrypted, model.ExperimentalEnabled);
        Assert.Equal(owner.Strings[TextId.OneDrive_CleanupCompleted], Assert.Single(dialogs.Notices));
        Assert.NotEqual(owner.Strings[TextId.Security_ExperimentalDisabled], dialogs.Notices[0]);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CloudCleanupSharesMutualExclusionAndSiblingRefreshWithSecurityCommands(bool cleanupFirst)
    {
        using var temp = new TemporaryDirectory();
        var pending = new TaskCompletionSource<SecurityOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new List<RepositorySecurityOperation>();
        var owner = new Context
        {
            Settings = AppSettings.Empty with { RepositoryPath = temp.Child() },
            Inspection = new(RepositoryReadiness.Ready, true, Guid.NewGuid()),
            SecurityInspector = _ => Task.FromResult(ExperimentalRecoveryState.AvailableWithCleanup),
            AsyncSecurityExecutor = operation => { operations.Add(operation); return pending.Task; }
        };
        var model = new ConfigurationViewModel(owner, new Dialogs()); await model.RefreshSecurityAsync();
        var active = cleanupFirst ? model.RetryCloudCleanup : model.VerifyExperimental;
        var sibling = cleanupFirst ? model.VerifyExperimental : model.RetryCloudCleanup;
        Assert.True(active.CanExecute(null)); Assert.True(sibling.CanExecute(null));
        var refreshes = 0; sibling.CanExecuteChanged += (_, _) => refreshes++;
        var work = active.ExecuteAsync();
        Assert.True(active.IsBusy); Assert.False(sibling.CanExecute(null)); Assert.True(refreshes > 0);
        await sibling.ExecuteAsync(); Assert.Single(operations);
        pending.SetResult(new(SecurityOperationOutcome.Success)); await work;
        Assert.False(active.IsBusy); Assert.True(sibling.CanExecute(null));
    }

    [Fact]
    public async Task CloudCleanupUsesCommonCommandFailureHandlingAndRestoresEnablement()
    {
        using var temp = new TemporaryDirectory();
        var owner = new Context
        {
            Settings = AppSettings.Empty with { RepositoryPath = temp.Child() },
            Inspection = new(RepositoryReadiness.Ready, true, Guid.NewGuid()),
            SecurityInspector = _ => Task.FromResult(ExperimentalRecoveryState.AvailableWithCleanup),
            SecurityExecutor = _ => throw new IOException("Synthetic command failure.")
        };
        var model = new ConfigurationViewModel(owner, new Dialogs()); await model.RefreshSecurityAsync();
        await model.RetryCloudCleanup.ExecuteAsync();
        Assert.Equal(owner.Strings[TextId.Status_CommandFailure], owner.LastOutcome);
        Assert.False(model.RetryCloudCleanup.IsBusy);
        Assert.True(model.RetryCloudCleanup.CanExecute(null)); Assert.True(model.VerifyExperimental.CanExecute(null));
    }
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
