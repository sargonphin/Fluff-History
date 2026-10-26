using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluffHistory.Core;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed partial class PresentationTests
{
    [Fact]
    public async Task DrivePollRetainsLastValueAndTextAndUsesOnlyTargetedNotifications()
    {
        var time = new ManualSchedulerTime(DateTimeOffset.UtcNow);
        var calls = 0; var slow = new TaskCompletionSource<CapacityObservation?>();
        var owner = new Context { Status = StatusForUi(true), DriveObserver = (_, _) =>
            ++calls == 1 ? Task.FromResult<CapacityObservation?>(new(1000, 300)) : slow.Task };
        var model = new StorageUsageViewModel(owner, false, time);
        await model.ActivateAsync("first"); var text = model.DriveSpace;
        var notifications = new List<string?>(); model.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        time.Advance(TimeSpan.FromSeconds(2)); await Eventually(() => calls == 2);
        Assert.True(model.HasDriveSpace); Assert.Equal(70, model.UsedPercent); Assert.Equal(text, model.DriveSpace);
        Assert.Empty(notifications);
        slow.SetResult(new(1000, 100)); await Eventually(() => model.UsedPercent == 90);
        Assert.Contains(nameof(model.UsedPercent), notifications); Assert.Contains(nameof(model.DriveSpace), notifications);
        Assert.DoesNotContain("", notifications); Assert.DoesNotContain(nameof(model.HasDriveSpace), notifications);
        model.Deactivate();
    }

    [Fact]
    public async Task LocationChangeDropsOldGaugeAndUnavailableReplyCannotKeepOldPercentage()
    {
        var pending = new TaskCompletionSource<CapacityObservation?>();
        var owner = new Context { DriveObserver = (path, _) => path == "first" ?
            Task.FromResult<CapacityObservation?>(new(1000, 300)) : pending.Task };
        var model = new StorageUsageViewModel(owner, false);
        await model.ActivateAsync("first"); var change = model.SetPathAsync("second");
        Assert.False(model.HasDriveSpace); Assert.Equal(0, model.UsedPercent);
        pending.SetResult(null); await change; Assert.False(model.HasDriveSpace); Assert.Contains("unavailable", model.DriveSpace);
        model.Deactivate();
    }

    [Fact]
    public void SharedGaugeHasStableGeometryAndNetworkCopyNamesTheOperationDependency()
    {
        var root = M251SourceRoot();
        var styles = XDocument.Load(Path.Combine(root, "src/FluffHistory.Desktop/Presentation/Styles.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var width = styles.Root!.Elements().Single(e => (string?)e.Attribute(x + "Key") == "DriveUsageWidth");
        Assert.Equal(300, double.Parse(width.Value, System.Globalization.CultureInfo.InvariantCulture));
        var container = styles.Root.Elements().Single(e => (string?)e.Attribute(x + "Key") == "DriveUsageContainer");
        Assert.Contains(container.Elements(), e => (string?)e.Attribute("Property") == "Width" && (string?)e.Attribute("Value") == "{StaticResource DriveUsageWidth}");
        foreach (var page in new[] { "OverviewPage.xaml", "ConfigurationPage.xaml" })
            Assert.Contains("{StaticResource DriveUsageContainer}", File.ReadAllText(Path.Combine(root, "src/FluffHistory.Desktop/Pages", page)));
        var configuration = XDocument.Load(Path.Combine(root, "src/FluffHistory.Desktop/Pages/ConfigurationPage.xaml"));
        var metered = Assert.Single(configuration.Descendants(), e => (string?)e.Attribute(XName.Get("Localize.Uid", "using:FluffHistory.Desktop.Presentation")) == "Configuration_CheckBox_AllowMeteredNetwork");
        Assert.Equal("{x:Bind Model.AllowMetered, Mode=TwoWay}", (string?)metered.Attribute("IsChecked"));
        Assert.Equal("Allow backups that use the network on metered connections", TestAppStrings.Read("en-US")["Configuration_CheckBox_AllowMeteredNetwork.Content"]);
    }

    private static string M251SourceRoot([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../.."));
}
