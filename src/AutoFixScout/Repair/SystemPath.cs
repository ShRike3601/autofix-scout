namespace AutoFixScout.Repair;

/// <summary>
/// The system PATH tells Windows where its own tools are. When an installer damages it, many unrelated things fail
/// ("is not recognized as an internal or external command", tools and installers that cannot start). Shared with the
/// elevated executor, which only ever adds these four Windows folders back in front; nothing is removed.
/// </summary>
internal static class SystemPath
{
    public static readonly string[] Required =
    {
        @"%SystemRoot%\system32", @"%SystemRoot%", @"%SystemRoot%\System32\Wbem", @"%SystemRoot%\System32\WindowsPowerShell\v1.0\",
    };

    private static string Norm(string entry, string systemRoot) =>
        entry.Trim().Trim('"')
            .Replace("%SystemRoot%", systemRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("%windir%", systemRoot, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('\\').ToLowerInvariant();

    /// <summary>Required entries missing from the raw (unexpanded) machine PATH.</summary>
    public static IReadOnlyList<string> Missing(string? rawPath, string systemRoot)
    {
        var have = (rawPath ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(e => Norm(e, systemRoot)).ToHashSet();
        return Required.Where(r => !have.Contains(Norm(r, systemRoot))).ToList();
    }

    /// <summary>Missing Windows folders first (in the usual order), then everything that was there, unchanged.</summary>
    public static string Repair(string? rawPath, string systemRoot)
    {
        var missing = Missing(rawPath, systemRoot);
        var kept = (rawPath ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(";", missing.Concat(kept));
    }
}
