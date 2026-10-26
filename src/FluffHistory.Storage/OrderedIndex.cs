namespace FluffHistory.Storage;

/// <summary>Format-specific immutable B+tree. Internal entries bind each child's inclusive maximum.</summary>
internal sealed class OrderedIndex(IndexKind kind, Func<FrameAddress, byte[]> read, SegmentAppender? writer = null,
    Func<FrameKind, byte[], FrameAddress>? emit = null)
{
    private readonly Dictionary<FrameAddress, IndexPage> _cache = [];
    private readonly Queue<FrameAddress> _order = [];
    internal IndexPage Page(FrameAddress address, PageEntry? parent = null, int? expectedLevel = null, byte[]? lower = null)
    {
        MetadataCodec.RequireKind(address, FrameKind.Page);
        if (!_cache.TryGetValue(address, out var page))
        {
            page = MetadataCodec.DecodePage(read(address), address.Serial);
            if (_cache.Count == 64) _cache.Remove(_order.Dequeue());
            _cache.Add(address, page); _order.Enqueue(address);
        }
        Format.Require(page.Kind == kind && (expectedLevel is null || page.Level == expectedLevel), "index kind/level");
        Format.Require(parent is null || parent.Key.AsSpan().SequenceEqual(page.Entries[^1].Key), "child maximum binding");
        Format.Require(lower is null || PathKeys.Comparer.Compare(lower, page.Entries[0].Key) < 0, "overlapping child ranges");
        return page;
    }
    internal FrameAddress? Find(FrameAddress? root, byte[] key)
    {
        if (root is null) return null;
        var page = Page(root);
        while (true)
        {
            var i = FirstAtLeast(page.Entries, key);
            if (i == page.Entries.Count) return null;
            var entry = page.Entries[i];
            if (page.Level == 0) return entry.Key.AsSpan().SequenceEqual(key) ? entry.Value : null;
            page = Page(entry.Value, entry, page.Level - 1, i == 0 ? null : page.Entries[i - 1].Key);
        }
    }
    internal IEnumerable<PageEntry> Range(FrameAddress? root, byte[]? lower = null, byte[]? upper = null, bool reverse = false)
    {
        if (root is null) yield break;
        foreach (var entry in Walk(Page(root), lower, upper, reverse)) yield return entry;
    }
    private IEnumerable<PageEntry> Walk(IndexPage page, byte[]? lower, byte[]? upper, bool reverse)
    {
        var start = lower is null ? 0 : FirstAtLeast(page.Entries, lower);
        for (var offset = 0; offset < page.Entries.Count - start; offset++)
        {
            var i = reverse ? page.Entries.Count - 1 - offset : start + offset;
            var e = page.Entries[i]; var previous = i == 0 ? null : page.Entries[i - 1].Key;
            if (upper is not null && previous is not null && PathKeys.Comparer.Compare(previous, upper) >= 0) continue;
            if (page.Level == 0)
            {
                if (upper is null || PathKeys.Comparer.Compare(e.Key, upper) < 0) yield return e;
            }
            else foreach (var item in Walk(Page(e.Value, e, page.Level - 1, previous), lower, upper, reverse)) yield return item;
        }
    }
    private static int FirstAtLeast(IReadOnlyList<PageEntry> entries, byte[] key)
    {
        int low = 0, high = entries.Count;
        while (low < high) { var mid = low + (high - low) / 2; if (PathKeys.Comparer.Compare(entries[mid].Key, key) < 0) low = mid + 1; else high = mid; }
        return low;
    }
    internal FrameAddress? Update(FrameAddress? root, IReadOnlyList<(byte[] Key, FrameAddress? Value)> changes)
    {
        if (changes.Count == 0) return root;
        var sorted = changes.OrderBy(c => c.Key, PathKeys.Comparer).ToArray();
        for (var i = 0; i < sorted.Length; i++) { PathKeys.Validate(sorted[i].Key, kind); Format.Require(i == 0 || PathKeys.Comparer.Compare(sorted[i - 1].Key, sorted[i].Key) < 0, "duplicate index update"); }
        var level = root is null ? 0 : Page(root).Level;
        var nodes = Rewrite(root, sorted);
        while (nodes.Count > 1) { level++; Format.Require(level < Format.MaximumDepth, "tree depth exhausted"); nodes = Pack((byte)level, nodes); }
        if (nodes.Count == 0) return null;
        var result = nodes[0].Value;
        while (Page(result).Level > 0 && Page(result).Entries.Count == 1) result = Page(result).Entries[0].Value;
        return result;
    }
    private List<PageEntry> Rewrite(FrameAddress? address, IReadOnlyList<(byte[] Key, FrameAddress? Value)> changes)
    {
        var page = address is null ? new IndexPage(kind, 0, []) : Page(address);
        if (page.Level == 0)
        {
            var merged = new SortedDictionary<byte[], FrameAddress>(PathKeys.Comparer);
            foreach (var e in page.Entries) merged.Add(e.Key, e.Value);
            foreach (var c in changes) { if (c.Value is null) merged.Remove(c.Key); else merged[c.Key] = c.Value; }
            return Pack(0, merged.Select(p => new PageEntry(p.Key, p.Value)).ToList());
        }
        var output = new List<PageEntry>(); var cursor = 0;
        for (var i = 0; i < page.Entries.Count; i++)
        {
            var e = page.Entries[i]; var begin = cursor;
            while (cursor < changes.Count && (i == page.Entries.Count - 1 || PathKeys.Comparer.Compare(changes[cursor].Key, e.Key) <= 0)) cursor++;
            if (cursor == begin) output.Add(e);
            else
            {
                _ = Page(e.Value, e, page.Level - 1, i == 0 ? null : page.Entries[i - 1].Key);
                output.AddRange(Rewrite(e.Value, changes.Skip(begin).Take(cursor - begin).ToArray()));
            }
        }
        return Pack(page.Level, output);
    }
    private List<PageEntry> Pack(byte level, IReadOnlyList<PageEntry> entries)
    {
        // Greedy boundaries give the minimum number of contiguous capacity-valid pages.
        // Redistribute each adjacent pair from right to left before emitting immutable
        // frames, so an overflow does not freeze a full-page + tiny-tail boundary.
        var groups = new List<List<PageEntry>>(); var group = new List<PageEntry>(); var bytes = 4;
        foreach (var entry in entries)
        {
            var size = EntryBytes(entry);
            if (group.Count == Format.MaximumPageEntries || bytes + size > Format.PageBytes)
            { groups.Add(group); group = []; bytes = 4; }
            group.Add(entry); bytes += size;
        }
        if (group.Count > 0) groups.Add(group);
        for (var i = groups.Count - 2; i >= 0; i--)
        {
            var pair = groups[i].Concat(groups[i + 1]).ToArray();
            var total = pair.Sum(EntryBytes); var leftBytes = 0;
            var best = groups[i].Count; long bestDifference = long.MaxValue;
            for (var cut = 1; cut < pair.Length; cut++)
            {
                leftBytes += EntryBytes(pair[cut - 1]); var rightBytes = total - leftBytes;
                if (cut > Format.MaximumPageEntries || pair.Length - cut > Format.MaximumPageEntries ||
                    4 + leftBytes > Format.PageBytes || 4 + rightBytes > Format.PageBytes) continue;
                // Balance the limiting resource (bytes or entries), with no global fill factor.
                var leftLoad = Math.Max((long)(4 + leftBytes) * Format.MaximumPageEntries, (long)cut * Format.PageBytes);
                var rightLoad = Math.Max((long)(4 + rightBytes) * Format.MaximumPageEntries, (long)(pair.Length - cut) * Format.PageBytes);
                var difference = Math.Abs(leftLoad - rightLoad);
                if (difference < bestDifference) { best = cut; bestDifference = difference; }
            }
            groups[i] = pair.Take(best).ToList(); groups[i + 1] = pair.Skip(best).ToList();
        }
        return groups.Select(g => Emit(level, g)).ToList();
    }
    private static int EntryBytes(PageEntry entry) => 2 + entry.Key.Length + Format.AddressBytes;
    private PageEntry Emit(byte level, IReadOnlyList<PageEntry> entries)
    {
        var body = MetadataCodec.EncodePage(new(kind, level, entries));
        var pointer = emit is null ? writer!.Append(FrameKind.Page, body) : emit(FrameKind.Page, body);
        return new(entries[^1].Key, pointer);
    }

    /// <summary>One-pass sorted construction; each final page is emitted once. No historical-entry list.</summary>
    internal FrameAddress? BuildSorted(IEnumerable<PageEntry> entries) => Root(entries, 0);
    private FrameAddress? Root(IEnumerable<PageEntry> entries, byte level)
    {
        Format.Require(level < Format.MaximumDepth, "tree depth exhausted");
        using var pages = Level(entries, level).GetEnumerator();
        if (!pages.MoveNext()) return null;
        var first = pages.Current;
        if (!pages.MoveNext()) return first.Value;
        var second = pages.Current;
        return Root(Children(), checked((byte)(level + 1)));
        IEnumerable<PageEntry> Children()
        {
            yield return first; yield return second;
            while (pages.MoveNext()) yield return pages.Current;
        }
    }
    private IEnumerable<PageEntry> Level(IEnumerable<PageEntry> entries, byte level)
    {
        var group = new List<PageEntry>(); var bytes = 4; byte[]? previous = null;
        foreach (var entry in entries)
        {
            PathKeys.Validate(entry.Key, kind);
            Format.Require(previous is null || PathKeys.Comparer.Compare(previous, entry.Key) < 0, "unsorted/duplicate bulk key");
            MetadataCodec.RequireKind(entry.Value, level == 0 ? FrameKind.Semantic : FrameKind.Page);
            var size = EntryBytes(entry);
            if (group.Count == Format.MaximumPageEntries || bytes + size > Format.PageBytes)
            {
                yield return Emit(level, group.ToArray()); group.Clear(); bytes = 4;
            }
            group.Add(entry); bytes += size; previous = entry.Key;
        }
        if (group.Count > 0) yield return Emit(level, group);
    }
}
