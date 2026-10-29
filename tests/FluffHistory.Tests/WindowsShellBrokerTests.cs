using System.IO.Pipes;
using FluffHistory.Application;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Windows;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class WindowsShellBrokerTests
{
    [WindowsTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealUserPipeNegotiatesBoundedTableOrLegacyReplyAndRejectsUnownedPreview(bool table)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new HistoryFixture();
        for (var i = 0; i < 21; i++) fixture.Add("report.txt", "saved version " + i, i);
        var mapping = SourceMapping.New(fixture.Temp.Child("live")) with { SourceRootId = HistoryFixture.Root };
        var settings = AppSettings.Empty with { RepositoryPath = fixture.Temp.Child(), Sources = [mapping], SchedulingEnabled = false };
        var store = new AppSettingsStore(fixture.Temp.Child("settings.json")); await store.SaveAsync(settings);
        var before = File.ReadAllBytes(Path.Combine(settings.RepositoryPath, "state.json"));
        var token = Guid.NewGuid().ToString("N");
        var server = WindowsShellBroker.RunAsync(token, store.FilePath, _ => new ShellHistoryText(TestAppStrings.English));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new NamedPipeClientStream(".", WindowsShellBroker.PipePrefix + token, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        await client.WriteAsync(ShellWire.Encode(new ShellOpenRequest(Path.Combine(mapping.RuntimePath!, "report.txt"), ShellItemKind.File, "en-US", table)), timeout.Token);
        var message = await ShellWire.ReadAsync(client, timeout.Token);
        var summary = table ? Assert.IsType<ShellTableReply>(message).Value : Assert.IsType<ShellOpenReply>(message).Value;
        Assert.Equal(table ? 20 : 5, summary.Rows.Length);
        if (table)
        {
            Assert.DoesNotContain("Latest saved", summary.Summary); Assert.Empty(summary.BackupLocation);
            Assert.Contains("21", summary.VersionCount); Assert.DoesNotContain(summary.Rows, row => row.Oldest);
            // A forged owner must fail before materialization or launching any preview application.
            await client.WriteAsync(ShellWire.Encode(new ShellPreviewRequest(summary.SessionId, summary.Rows[0].Token, 0)), timeout.Token);
        }
        else await client.WriteAsync(ShellWire.Encode(new ShellCloseRequest()), timeout.Token);
        await server.WaitAsync(timeout.Token);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(settings.RepositoryPath, "state.json")));
    }
}
