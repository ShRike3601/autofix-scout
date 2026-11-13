namespace AutoFixScout.Drivers;

/// <summary>What the ladders need to decide on a rollback (spec 7: changed within 30 days and a previous package available).</summary>
public sealed record DriverStatus(string? Version, DateTime? StagedUtc, string? PreviousVersion)
{
    /// <summary>
    /// shortcut: "changed" = when the current package entered the driver store, not Windows Update history;
    /// read WUA history (spec 7.1 probe.update_history) if pilots show this misses or over-reports.
    /// </summary>
    public bool CanRollBack(TimeSpan window) =>
        PreviousVersion != null && StagedUtc is { } staged && DateTime.UtcNow - staged <= window;

    /// <summary>Never throws: a driver we can't read simply isn't a rollback candidate (fail closed).</summary>
    public static DriverStatus? For(string instanceId)
    {
        try
        {
            var d = DriverStore.Read(instanceId);
            if (d is null) return null;
            return new DriverStatus(d.Version, DriverStore.StagedUtc(d.InfName), DriverStore.FindPrevious(d)?.Version.ToString());
        }
        catch (Exception) { return null; }
    }
}
