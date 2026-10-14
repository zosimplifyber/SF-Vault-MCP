using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class SearchRunnerTests
{
    private record Item(long Id, string Name);

    private static MappedCondition Mc(long def, string text, bool starts) => new(def, "p" + def, 1, text, starts);

    private static (Func<IReadOnlyList<MappedCondition>, bool, int, RawFetch<Item>> Fetch, List<(string Conds, bool Any, int Cap)> Calls) FakeFetch(
        Func<IReadOnlyList<MappedCondition>, IReadOnlyList<Item>> data, bool more = false)
    {
        var calls = new List<(string, bool, int)>();
        return ((c, any, cap) => { calls.Add((string.Join(",", c.Select(x => x.PropDefId)), any, cap)); return new RawFetch<Item>(data(c), more); }, calls);
    }

    // value table: item id -> propdef -> text
    private static Func<IReadOnlyList<long>, IReadOnlyDictionary<long, IReadOnlyDictionary<long, string>>?> Props(Dictionary<long, Dictionary<long, string>> t) =>
        ids => ids.Where(t.ContainsKey).ToDictionary(i => i, i => (IReadOnlyDictionary<long, string>)t[i]);

    [Fact]
    public void And_semantics_keep_only_files_whose_value_starts_with_the_text()
    {
        var items = new[] { new Item(1, "a"), new Item(2, "b"), new Item(3, "c") };
        var (fetch, calls) = FakeFetch(_ => items);
        var vals = new Dictionary<long, Dictionary<long, string>> { [1] = new() { [3] = "CD-1" }, [2] = new() { [3] = "XCD-2" }, [3] = new() { [3] = "cd-3" } };
        var res = SearchRunner.Run(new MappedSearch(new[] { Mc(3, "CD", true), Mc(2, "x", false) }, false, null, true, true, 50), fetch, i => i.Id, Props(vals));
        Assert.Equal(new long[] { 1, 3 }, res.Items.Select(i => i.Id).ToArray());
        Assert.Single(calls);
        Assert.False(calls[0].Any);
        Assert.False(res.Truncated);
        Assert.False(res.Partial);
    }

    [Fact]
    public void Or_with_mixed_conditions_enforces_starts_with_and_unions_both_searches()
    {
        var vals = new Dictionary<long, Dictionary<long, string>>
        {
            [2] = new() { [3] = "CD-2" }, [3] = new() { [3] = "CD-3" }, [4] = new() { [3] = "XCD-4" },
        };
        var (fetch, calls) = FakeFetch(c => c.Any(x => x.StartsWith)
            ? new[] { new Item(2, "b"), new Item(3, "c"), new Item(4, "d") }
            : new[] { new Item(1, "a"), new Item(2, "b") });
        var res = SearchRunner.Run(new MappedSearch(new[] { Mc(2, "Released", false), Mc(3, "CD", true) }, true, null, true, true, 50), fetch, i => i.Id, Props(vals));
        Assert.Equal(new long[] { 1, 2, 3 }, res.Items.Select(i => i.Id).OrderBy(i => i).ToArray());
        Assert.DoesNotContain(res.Items, i => i.Id == 4);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.True(c.Any));
    }

    [Fact]
    public void Or_with_only_starts_with_keeps_files_matching_any_of_them()
    {
        var items = new[] { new Item(1, "a"), new Item(2, "b"), new Item(3, "c") };
        var (fetch, _) = FakeFetch(_ => items);
        var vals = new Dictionary<long, Dictionary<long, string>> { [1] = new() { [3] = "AA", [4] = "zz" }, [2] = new() { [3] = "zz", [4] = "BB" }, [3] = new() { [3] = "zz", [4] = "zz" } };
        var res = SearchRunner.Run(new MappedSearch(new[] { Mc(3, "AA", true), Mc(4, "BB", true) }, true, null, true, true, 50), fetch, i => i.Id, Props(vals));
        Assert.Equal(new long[] { 1, 2 }, res.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void Without_starts_with_it_is_a_plain_single_search_with_truncation()
    {
        var (fetch, calls) = FakeFetch(_ => Enumerable.Range(1, 6).Select(i => new Item(i, "x")).ToList());
        var res = SearchRunner.Run(new MappedSearch(new[] { Mc(2, "x", false) }, false, null, true, true, 5), fetch, i => i.Id, _ => throw new InvalidOperationException("no property read expected"));
        Assert.Equal(5, res.Items.Count);
        Assert.True(res.Truncated);
        Assert.Equal(6, calls[0].Cap);
    }

    [Fact]
    public void Raw_cap_is_reported_with_count_and_truncated()
    {
        var items = Enumerable.Range(1, SearchRunner.RawCap).Select(i => new Item(i, "x")).ToList();
        var (fetch, _) = FakeFetch(_ => items, more: true);
        var vals = items.ToDictionary(i => i.Id, i => new Dictionary<long, string> { [3] = "CD" });
        var res = SearchRunner.Run(new MappedSearch(new[] { Mc(3, "CD", true) }, false, null, true, true, 5000), fetch, i => i.Id, Props(vals));
        Assert.True(res.RawCapHit);
        Assert.True(res.Truncated);
        Assert.Equal(SearchRunner.RawCap, res.RawCount);
        Assert.Contains("1000", res.Warning);
        Assert.Equal(SearchRunner.RawCap, res.Items.Count);
    }

    [Fact]
    public void Under_the_cap_with_nothing_more_is_not_truncated()
    {
        var items = Enumerable.Range(1, 10).Select(i => new Item(i, "x")).ToList();
        var (fetch, _) = FakeFetch(_ => items);
        var vals = items.ToDictionary(i => i.Id, i => new Dictionary<long, string> { [3] = "CD" });
        var res = SearchRunner.Run(new MappedSearch(new[] { Mc(3, "CD", true) }, false, null, true, true, 50), fetch, i => i.Id, Props(vals));
        Assert.False(res.RawCapHit);
        Assert.False(res.Truncated);
        Assert.Null(res.Warning);
    }

    [Fact]
    public void Failed_property_batch_is_partial_with_a_warning_not_silent()
    {
        var items = Enumerable.Range(1, 150).Select(i => new Item(i, "x")).ToList();
        var (fetch, _) = FakeFetch(_ => items);
        var vals = items.ToDictionary(i => i.Id, i => new Dictionary<long, string> { [3] = "CD" });
        var batches = 0;
        IReadOnlyDictionary<long, IReadOnlyDictionary<long, string>>? Flaky(IReadOnlyList<long> ids)
        {
            batches++;
            return batches == 2 ? null : Props(vals)(ids);
        }
        var res = SearchRunner.Run(new MappedSearch(new[] { Mc(3, "CD", true) }, false, null, true, true, 500), fetch, i => i.Id, Flaky);
        Assert.True(res.Partial);
        Assert.Equal(100, res.Items.Count);
        Assert.Contains("50 candidate", res.Warning);
    }

    [Fact]
    public void Matches_helper_handles_missing_values()
    {
        var starts = new[] { Mc(3, "A", true), Mc(4, "B", true) };
        Assert.False(SearchRunner.Matches(null, starts, true));
        Assert.True(SearchRunner.Matches(new Dictionary<long, string> { [4] = "bee" }, starts, true));
        Assert.False(SearchRunner.Matches(new Dictionary<long, string> { [4] = "bee" }, starts, false));
    }
}
