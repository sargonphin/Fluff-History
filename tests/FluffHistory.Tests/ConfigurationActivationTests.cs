using FluffHistory.Application;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Engine;
using Xunit;

namespace FluffHistory.Tests;

public sealed class ConfigurationActivationTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void OnlyFirstReadySavedConfigurationRequestsImmediateBackupEvenWithAutomaticOff(bool automatic)
    {
        var empty = AppSettings.Empty; Assert.True(empty.SchedulingEnabled); Assert.Equal(60, empty.IntervalMinutes);
        var activation = new ConfigurationActivation(empty); var saved = empty with { RepositoryPath = Path.GetFullPath("repo"), Sources = [SourceMapping.New(Path.GetFullPath("source"))], SchedulingEnabled = automatic };
        var calls = 0; BackupAdmission Request() { calls++; return BackupAdmission.Started; }
        Assert.Null(activation.AfterSaved(saved with { Sources = [] }, true, Request));
        Assert.Null(activation.AfterSaved(saved, false, Request)); Assert.Equal(0, calls);
        Assert.Equal(BackupAdmission.Started, activation.AfterSaved(saved, true, Request)); Assert.Equal(1, calls);
        Assert.Null(activation.AfterSaved(saved with { IntervalMinutes = 180 }, true, Request));
        Assert.Null(activation.AfterSaved(saved with { Sources = [.. saved.Sources, SourceMapping.New(Path.GetFullPath("another"))] }, true, Request));
        Assert.Equal(1, calls); Assert.True(saved.ToSchedule().NextAfter(saved.AnchorUtc) > saved.AnchorUtc);
        Assert.Null(new ConfigurationActivation(saved).AfterSaved(saved, true, Request)); Assert.Equal(1, calls);
        activation.Reset(); Assert.Equal(BackupAdmission.Started, activation.AfterSaved(saved, true, Request)); Assert.Equal(2, calls);
    }

    [Fact]
    public void SourceChangeNoticeDistinguishesAutomaticOnAndOff()
    {
        var saved = AppSettings.Empty with { RepositoryPath = Path.GetFullPath("repo"), Sources = [SourceMapping.New(Path.GetFullPath("source"))] };
        var changed = saved with { Sources = [.. saved.Sources, SourceMapping.New(Path.GetFullPath("another"))] };
        Assert.Contains("next scheduled backup", ConfigurationActivation.SavedMessage(saved, changed));
        Assert.Contains("next backup", ConfigurationActivation.SavedMessage(saved, changed with { SchedulingEnabled = false }));
        Assert.DoesNotContain("scheduled", ConfigurationActivation.SavedMessage(saved, changed with { SchedulingEnabled = false }));
        Assert.Equal("Configuration saved", ConfigurationActivation.SavedMessage(saved, saved with { IntervalMinutes = 180 }));
        Assert.Contains("already running or finishing", ConfigurationActivation.AdmissionMessage(BackupAdmission.Busy));
    }

    [Fact]
    public async Task ImmediateUnchangedEngineAttemptIsSuccessAndDoesNotInvalidateTimeline()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("file", "contents");
        var configuration = new BackupConfiguration(f.RepositoryPath, [new(new(f.Root, f.Source), f.Source)]);
        var runner = new EngineBackupAttemptRunner(); var first = await runner.RunAsync(configuration, AttemptTrigger.Manual, null, default);
        Assert.Equal(BackupOutcome.Completed, first.Outcome); Assert.True(first.TimelineChanged);
        var second = await runner.RunAsync(configuration, AttemptTrigger.Manual, null, default);
        Assert.Equal(BackupOutcome.Completed, second.Outcome); Assert.False(second.TimelineChanged);
        Assert.True(PresentationText.IsNoChange(second)); Assert.Equal("No changes to back up.", PresentationText.Outcome(TestAppStrings.English, second));
        Assert.Equal(0, second.Scan!.VersionsCreated); Assert.Equal(0, second.Scan.BatchesCommitted);
        Assert.True(new BackupAttemptResult(BackupOutcome.ScanControlFailure, CommittedProgressMayExist: true).TimelineChanged);
    }
}
