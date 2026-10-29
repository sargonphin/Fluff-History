using System.Diagnostics;
using System.Text.Json;
using FluffHistory.Application;
using FluffHistory.Desktop.Presentation.Models;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class ShellPropertiesPerformanceTests(ITestOutputHelper output)
{
    // Run separately from other classes when collecting timings. These observations are not
    // a machine-independent latency threshold or a claim about cold SMB/NAS performance.
    [Fact]
    public async Task CompareFiveAndTwentyDetachedRowsOnTheSameHistory()
    {
        using var fixture = new HistoryFixture();
        for (var i = 0; i < 256; i++) fixture.Add("report.txt", "content" + i, i);
        var mapping = SourceMapping.New(fixture.Temp.Child("live")) with { SourceRootId = HistoryFixture.Root };
        var settings = AppSettings.Empty with { RepositoryPath = fixture.Temp.Child(), Sources = [mapping] };
        var path = Path.Combine(mapping.RuntimePath!, "report.txt");
        var text = new ShellHistoryText(TestAppStrings.English);
        var before = File.ReadAllBytes(Path.Combine(settings.RepositoryPath, "state.json"));
        var samples = new List<(int Rows, double Ms, long Allocations, int WireBytes)>();
        for (var round = -1; round < 12; round++)
        foreach (var limit in round % 2 == 0 ? new[] { 5, 20 } : new[] { 20, 5 })
        {
            var allocations = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
            await using var session = await ShellPathSession.OpenBoundedAsync(settings, path, ShellItemKind.File,
                ShellPathSession.MaximumCount, text, recentLimit: limit);
            var wire = ShellWire.Encode(new ShellTableReply(session.Summary));
            await session.ReleaseReadCaptureAsync(); watch.Stop(); allocations = GC.GetTotalAllocatedBytes(true) - allocations;
            Assert.Equal(limit, session.Summary.Rows.Length); Assert.DoesNotContain(session.Summary.Rows, row => row.Oldest);
            Assert.True(wire.Length < ShellWire.MaximumBodyBytes);
            if (round >= 0) samples.Add((limit, watch.Elapsed.TotalMilliseconds, allocations, wire.Length));
        }
        foreach (var group in samples.GroupBy(sample => sample.Rows))
        {
            var sorted = group.OrderBy(sample => sample.Ms).ToArray();
            output.WriteLine("PROPERTIES_ROWS_JSON " + JsonSerializer.Serialize(new
            {
                retainedHistory = 256, displayedRows = group.Key, samples = sorted.Length,
                medianMs = sorted[sorted.Length / 2].Ms, maximumMs = sorted[^1].Ms,
                medianAllocatedBytes = group.Select(sample => sample.Allocations).Order().ElementAt(sorted.Length / 2),
                replyBytes = sorted[0].WireBytes, authorityUnchanged = true
            }));
        }
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(settings.RepositoryPath, "state.json")));
    }
}
