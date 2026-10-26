using FluffHistory.Application;
using FluffHistory.Desktop.Presentation.Models;
using Xunit;

namespace FluffHistory.Tests;

public sealed partial class PresentationTests
{
    [Fact]
    public async Task UpdateSettingsDefaultsAndNeverKeepExplicitCheckAvailable()
    {
        var owner = new Context(); var model = new SettingsViewModel(owner);
        Assert.Equal(UpdateChannel.Stable, model.SelectedUpdateChannel.Value);
        Assert.Equal(UpdateFrequency.Weekly, model.SelectedUpdateFrequency.Value);
        model.SelectedUpdateFrequency = model.UpdateFrequencies.Single(c => c.Value == UpdateFrequency.Never);
        Assert.True(model.AutomaticChecksDisabled); Assert.True(model.CanCheckUpdates);
        await model.CheckUpdatesAsync(); Assert.Equal(1, owner.UpdateChecks);
        Assert.Equal(UpdateFrequency.Never, owner.Updates!.Preferences.Frequency); Assert.Equal(0, owner.Saves);
    }
    [Fact]
    public void UpdateLabelsRefreshWithLiveLanguageAndBetaCurrentIsExplicit()
    {
        var owner = new Context { Updates = new("1.11.0-beta.2", UpdatePreferences.Default with { Channel = UpdateChannel.Beta }, null, UpdatePhase.Idle, null) };
        var model = new SettingsViewModel(owner); var old = model.UpdateChannels;
        owner.Strings = new TestAppStrings("ar-SA"); owner.Notify();
        Assert.NotSame(old, model.UpdateChannels);
        Assert.Equal(owner.Strings[TextId.Update_Beta], model.SelectedUpdateChannel.Label);
        Assert.Contains("1.11.0-beta.2", model.CurrentUpdateVersion); Assert.Contains(owner.Strings[TextId.Update_Beta], model.CurrentUpdateVersion);
        Assert.Equal(owner.Strings[TextId.Update_Title], model.UpdateTitle);
    }
    [Fact]
    public async Task ManualInstallDeclineDoesNothingAndAcceptanceCarriesExplicitCancellation()
    {
        var owner = new Context { Status = new(0, SchedulerLifecycle.Idle, false, new(TimeSpan.FromHours(1), DateTimeOffset.UtcNow),
            null, true, false, null, null, new(BackupPhase.Scanning), null, null, 0, SafeguardDecision.Allow, null) };
        var dialogs = new Dialogs { Confirm = false }; var model = new SettingsViewModel(owner, dialogs);
        await model.InstallUpdateAsync(); Assert.Empty(owner.UpdateInstalls);
        dialogs.Confirm = true; await model.InstallUpdateAsync(); Assert.True(Assert.Single(owner.UpdateInstalls));
    }
    [Fact]
    public void UpdateFailureCopyIsTypedLocalizedAndSeparateFromRepositoryReadiness()
    {
        var owner = new Context { Updates = new("1.0.0", UpdatePreferences.Default, null, UpdatePhase.Failed, null, UpdateFailure.Signature) };
        var model = new SettingsViewModel(owner);
        Assert.Equal(owner.Strings[TextId.Update_VerificationFailed], model.UpdateStatus);
        Assert.Equal(RepositoryReadiness.Ready, owner.Inspection.State);
        owner.Updates = owner.Updates with { Failure = UpdateFailure.SignerNotApproved }; owner.Notify();
        Assert.Equal(owner.Strings[TextId.Update_SignerGate], model.UpdateStatus);
    }
}
