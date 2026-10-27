using FluffHistory.Application;
using FluffHistory.Desktop.Presentation.Models;
using Xunit;

namespace FluffHistory.Tests;

public sealed partial class PresentationTests
{
    [Fact]
    public async Task LateAgentIdentityRefreshesSecurityWithoutAcceptingTheOldPendingRead()
    {
        var pending = new TaskCompletionSource<ExperimentalRecoveryState>(); var calls = 0;
        var owner = new Context { Settings = AppSettings.Empty with { RepositoryPath = Path.GetFullPath("security-backup") },
            Inspection = new(RepositoryReadiness.Missing),
            SecurityInspector = _ => ++calls == 1 ? pending.Task : Task.FromResult(ExperimentalRecoveryState.Available) };
        var model = new ConfigurationViewModel(owner, new Dialogs());
        var first = model.RefreshSecurityAsync();
        owner.Inspection = new(RepositoryReadiness.Ready, RepositoryId: Guid.NewGuid(), Encrypted: true); owner.Notify();
        Assert.Equal(2, calls); Assert.True(model.ExperimentalEnabled); Assert.True(model.VerifyExperimental.CanExecute(null));
        pending.SetResult(ExperimentalRecoveryState.Off); await first;
        Assert.True(model.ExperimentalEnabled); Assert.True(model.VerifyExperimental.CanExecute(null));
        for (var i = 0; i < 10; i++) owner.Notify(); Assert.Equal(2, calls); // No status-poll security I/O.
    }

    [Fact]
    public async Task SameBindingRefreshCannotBeOverwrittenByAnOlderRead()
    {
        var pending = new TaskCompletionSource<ExperimentalRecoveryState>(); var calls = 0;
        var owner = new Context { Settings = AppSettings.Empty with { RepositoryPath = Path.GetFullPath("security-backup") },
            Inspection = new(RepositoryReadiness.Ready, RepositoryId: Guid.NewGuid(), Encrypted: true),
            SecurityInspector = _ => ++calls == 1 ? pending.Task : Task.FromResult(ExperimentalRecoveryState.Available) };
        var model = new ConfigurationViewModel(owner, new Dialogs()); var first = model.RefreshSecurityAsync();
        await model.RefreshSecurityAsync(); Assert.True(model.ExperimentalEnabled);
        pending.SetResult(ExperimentalRecoveryState.Off); await first;
        Assert.True(model.ExperimentalEnabled);
    }
}
