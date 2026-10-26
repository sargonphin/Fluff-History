using FluffHistory.Application;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class BackupLocationUxTests
{
    [Fact]
    public async Task UnrelatedFolderHasOkOnlyNoticeAndPreservesDraftAndFolder()
    {
        using var temp = new TemporaryDirectory(); var path = temp.Child("unrelated"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "user.txt"), "keep");
        var owner = new ProductContext(); var dialogs = new ProductDialogs { SelectedFolder = path };
        var model = new ConfigurationViewModel(owner, dialogs); var before = model.RepositoryPath;
        await model.ChangeLocation.ExecuteAsync();
        Assert.Equal(1, dialogs.Notices); Assert.Equal(0, dialogs.Confirmations); Assert.Null(dialogs.NewLocation);
        Assert.Equal(owner.Strings[TextId.Configuration_UnrelatedFolder], dialogs.LastWarning); Assert.Equal(before, model.RepositoryPath); Assert.False(model.IsDirty);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "user.txt"))); Assert.Single(Directory.GetFiles(path));
    }

    [Fact]
    public async Task EmptyFolderProminentlyIdentifiesPathAndCanBeCancelledWithoutCreation()
    {
        using var temp = new TemporaryDirectory(); var path = temp.Child("empty"); Directory.CreateDirectory(path);
        var dialogs = new ProductDialogs { SelectedFolder = path, ProtectionChoice = null };
        var model = new ConfigurationViewModel(new ProductContext(), dialogs); await model.ChangeLocation.ExecuteAsync();
        Assert.Equal(path, dialogs.NewLocation); Assert.Equal(0, dialogs.Notices); Assert.Empty(Directory.GetFileSystemEntries(path));
        Assert.Empty(model.RepositoryPath);
    }

    [Theory]
    [InlineData(false, "en-US")] [InlineData(true, "en-US")]
    [InlineData(false, "de-DE")] [InlineData(true, "de-DE")]
    public async Task ExistingLocationDialogSeparatesProminentPathAndEncryptionStatus(bool encrypted, string language)
    {
        using var temp = new TemporaryDirectory();
        using var repo = encrypted ? Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password) : Repository.Create(temp.Child());
        var strings = new TestAppStrings(language);
        var owner = new ProductContext { Strings = strings }; var dialogs = new ProductDialogs { Strings = strings, SelectedFolder = repo.DirectoryPath, Confirm = false };
        var model = new ConfigurationViewModel(owner, dialogs); await model.ChangeLocation.ExecuteAsync();
        var shown = Assert.IsType<BackupLocationConfirmation>(dialogs.LocationConfirmation);
        Assert.Equal(repo.DirectoryPath, shown.Path); Assert.Contains(strings[encrypted ? TextId.Encryption_Encrypted : TextId.Encryption_Unencrypted], shown.Status);
        Assert.Equal(strings[TextId.Configuration_AdoptExplanation], shown.Explanation);
        Assert.Null(shown.Primary); // The presentation boundary resolves its localized default action.
        Assert.Equal(strings[TextId.Action_UseBackupLocation], dialogs.LastPrimary);
        Assert.Empty(model.RepositoryPath); Assert.Equal(0, owner.Saves);
    }

    [Theory]
    [InlineData("en-US")] [InlineData("de-DE")]
    public void ForeverWarningUsesThePresentationCatalog(string language)
    {
        var strings = new TestAppStrings(language);
        var model = new ConfigurationViewModel(new ProductContext { Strings = strings }, new ProductDialogs());
        model.SelectedRetention = model.RetentionPolicies.Single(p => p.Name == "forever");
        Assert.Equal(strings[TextId.Retention_ForeverExplanation], model.RetentionNotice);
    }
}
