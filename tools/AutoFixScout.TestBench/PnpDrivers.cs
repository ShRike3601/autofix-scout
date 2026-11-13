namespace AutoFixScout.TestBench;

/// <summary>One entry of `pnputil /enum-drivers`.</summary>
internal sealed record PnpDriver(string PublishedName, string OriginalName, string Version);

/// <summary>
/// Driver store bookkeeping for the bench. Compiled into the tests too (no admin, no pnputil needed).
/// Learned the hard way (test step 5c): a regex over the whole pnputil output ran from one entry into the next and
/// "unstage" deleted the laptop's original driver package. Parse entry by entry and only ever delete what stage added.
/// </summary>
internal static class PnpDrivers
{
    /// <summary>Splits `pnputil /enum-drivers` output into entries (blank-line separated) and reads each one's own fields.</summary>
    public static List<PnpDriver> Parse(string output)
    {
        var list = new List<PnpDriver>();
        foreach (var block in output.Replace("\r", "").Split("\n\n"))
        {
            string? Field(string key) => block.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                .Select(l => l[(key.Length + 1)..].Trim())
                .FirstOrDefault();
            var published = Field("Published Name");
            var original = Field("Original Name");
            var version = Field("Driver Version")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (published != null && original != null && version != null) list.Add(new PnpDriver(published, original, version));
        }
        return list;
    }

    /// <summary>What "stage" wrote down: the package the device had before, and the one stage added.</summary>
    public sealed record StageRecord(string OriginalOem, string StagedOem)
    {
        public string Serialize() => $"original={OriginalOem}\nstaged={StagedOem}\n";

        public static StageRecord? Parse(string? text)
        {
            if (text is null) return null;
            var map = text.Replace("\r", "").Split('\n').Where(l => l.Contains('='))
                .ToDictionary(l => l[..l.IndexOf('=')].Trim(), l => l[(l.IndexOf('=') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
            return map.TryGetValue("original", out var o) && map.TryGetValue("staged", out var s) && o.Length > 0 && s.Length > 0
                ? new StageRecord(o, s) : null;
        }
    }

    /// <summary>
    /// The one package unstage may delete: the staged one, only if it's still in the store, isn't the original,
    /// and the original is still there to fall back to. Null = delete nothing.
    /// </summary>
    public static string? ToDelete(IReadOnlyList<PnpDriver> store, StageRecord? record)
    {
        if (record is null || record.StagedOem.Equals(record.OriginalOem, StringComparison.OrdinalIgnoreCase)) return null;
        bool InStore(string oem) => store.Any(d => d.PublishedName.Equals(oem, StringComparison.OrdinalIgnoreCase));
        return InStore(record.StagedOem) && InStore(record.OriginalOem) ? record.StagedOem : null;
    }
}
