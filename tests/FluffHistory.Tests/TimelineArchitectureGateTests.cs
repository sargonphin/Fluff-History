using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class TimelineArchitectureGateTests
{
    [Theory]
    [InlineData(HistoryDirection.OldestFirst)] [InlineData(HistoryDirection.NewestFirst)]
    public void ExactFirstVisiblePageNeedsTheFinalPathEvenUnderPublicationRollback(HistoryDirection direction)
    {
        using var f = new HistoryFixture();
        f.Add("folder/a", day: 5); f.Add("folder/b", day: 4);
        f.Add("folder/zz-final", day: direction == HistoryDirection.OldestFirst ? -5 : 20);
        f.Add("folder/a", day: 2); // Last publication is neither oldest nor newest StoredUtc.
        using var r = f.Repository.OpenReader(); var scope = new HistoryScope(FoundationTests.Root, "folder");
        var page = r.ReadTimelinePage(new(1), scope, 1, direction);
        var reference = TimelineProjectionTests.Reference(r, scope);
        Assert.Equal(direction == HistoryDirection.OldestFirst ? reference[0] : reference[^1], Assert.Single(page.Items));
        Assert.Equal(4, r.LastTimelineStatistics!.ValidatedVersions);
        Assert.Equal(4, r.LastTimelineStatistics.Reads.Windows);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TransientPrototypeMatchesIndependentFullReaderAcrossDirectionsAndPages(bool unique)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, 83, storedTime: (p, i) => FoundationTests.Time.AddTicks(unique ? p * 83 + i + 1 : (p + i) % 7 + 1),
            name: TimelineResponsivenessMeasurementTests.Name);
        using var r = repo.OpenReader();
        foreach (var folder in new[] { "", "Documents", "Pictures/area00", "empty", "" })
        {
            var scope = new HistoryScope(FoundationTests.Root, folder);
            var expected = TimelineProjectionTests.Reference(r, scope);
            var prototype = new TimelineResponsivenessMeasurementTests.Prototype(); prototype.Build(r, scope);
            Assert.Equal(expected.Select(t => t.UtcTicks), prototype.Ticks);
            foreach (var direction in Enum.GetValues<HistoryDirection>())
            foreach (var size in new[] { 1, 3, 128 })
            {
                var actual = new List<DateTimeOffset>(); DateTimeOffset? after = null;
                do
                {
                    var page = prototype.Page(size, direction, after); actual.AddRange(page.Items); after = page.Continuation?.Time;
                } while (after is not null);
                Assert.Equal(direction == HistoryDirection.OldestFirst ? expected : expected.Reverse(), actual);
            }
        }
    }
}
