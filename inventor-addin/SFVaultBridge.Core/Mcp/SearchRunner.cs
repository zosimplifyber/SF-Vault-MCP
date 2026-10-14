namespace SFVaultBridge.Core;

/// <summary>One fetched page set: the items, and whether Vault reported more beyond them.</summary>
public sealed record RawFetch<T>(IReadOnlyList<T> Items, bool More);

public sealed record SearchRunResult<T>(IReadOnlyList<T> Items, bool Truncated, bool Partial, bool RawCapHit, int RawCount, string? Warning);

/// <summary>Runs a mapped advanced search against a fetch function. Vault has no starts_with operator, so those conditions are sent as contains and checked
/// client-side per file. OR (any) is honoured exactly: a file matches if ANY condition matches, so the server conditions and the starts_with conditions are searched
/// separately and the results united. Pure: no Vault types, so it is tested with fakes.</summary>
public static class SearchRunner
{
    public const int RawCap = 1000;
    public const int PropertyBatch = 100;

    /// <param name="fetch">Fetches up to cap items for the conditions (any = OR on the server) and says whether more remain.</param>
    /// <param name="props">Property values per item id (propDefId to text) for a batch of ids; null when the batch could not be read.</param>
    public static SearchRunResult<T> Run<T>(MappedSearch s, Func<IReadOnlyList<MappedCondition>, bool, int, RawFetch<T>> fetch, Func<T, long> idOf,
                                            Func<IReadOnlyList<long>, IReadOnlyDictionary<long, IReadOnlyDictionary<long, string>>?> props)
    {
        var starts = s.Conditions.Where(c => c.StartsWith).ToList();
        var others = s.Conditions.Where(c => !c.StartsWith).ToList();
        if (starts.Count == 0)
        {
            var f = fetch(s.Conditions, s.Any, s.Limit + 1);
            return new SearchRunResult<T>(f.Items.Take(s.Limit).ToList(), f.Items.Count > s.Limit || f.More, false, false, 0, null);
        }

        var matched = new List<T>();
        var seen = new HashSet<long>();
        bool capHit = false, partial = false;
        var raw = 0;
        var unchecked_ = 0;

        void Pass(IReadOnlyList<MappedCondition> conds, bool any, IReadOnlyList<MappedCondition> check, bool checkAny)
        {
            var f = fetch(conds, any, RawCap);
            raw += f.Items.Count;
            if (f.Items.Count >= RawCap && f.More) capHit = true;
            if (check.Count == 0)
            {
                foreach (var i in f.Items) if (seen.Add(idOf(i))) matched.Add(i);
                return;
            }
            for (var i = 0; i < f.Items.Count; i += PropertyBatch)
            {
                var batch = f.Items.Skip(i).Take(PropertyBatch).ToList();
                var values = props(batch.Select(idOf).ToList());
                if (values is null) { partial = true; unchecked_ += batch.Count; continue; }
                foreach (var item in batch)
                {
                    values.TryGetValue(idOf(item), out var m);
                    if (Matches(m, check, checkAny) && seen.Add(idOf(item))) matched.Add(item);
                }
            }
        }

        if (!s.Any) Pass(s.Conditions, false, starts, false);
        else if (others.Count == 0) Pass(s.Conditions, true, starts, true);
        else
        {
            Pass(others, true, Array.Empty<MappedCondition>(), true);
            Pass(starts, true, starts, true);
        }

        var warnings = new List<string>();
        if (partial) warnings.Add($"{unchecked_} candidate file(s) could not be checked for starts_with (property read failed) and are left out; the result may be incomplete. Try again.");
        if (capHit) warnings.Add($"The search stopped after {RawCap} raw candidates before the starts_with check; more files may match. Add a more specific condition or a folder.");
        return new SearchRunResult<T>(matched.Take(s.Limit).ToList(), matched.Count > s.Limit || capHit, partial, capHit, raw, warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    /// <summary>starts_with test of one file's property values: all conditions (AND) or at least one (OR). A property with no value never matches.</summary>
    public static bool Matches(IReadOnlyDictionary<long, string>? values, IReadOnlyList<MappedCondition> starts, bool any)
    {
        var ok = starts.Select(c => values is not null && values.TryGetValue(c.PropDefId, out var v) && v.StartsWith(c.Text, StringComparison.OrdinalIgnoreCase));
        return any ? ok.Any(x => x) : ok.All(x => x);
    }
}
