using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>
/// "My computer is slow or full". Two real tests: when the check found the system drive low or critical, use
/// <see cref="AutoTestAsync"/> (free space back above the warning level); otherwise ask <see cref="TestQuestion"/>.
/// <see cref="UsesAutoTest"/> picks between them.
/// </summary>
public static class StorageFlow
{
    public const string Topic = "storage";
    public const string What = "computer";
    public const string TestQuestion = "Is your computer running better now?";

    private static readonly IStorageProbe Probe = new RealStorageProbe();

    public static async Task<Diagnosis> DiagnoseAsync()
    {
        // Read-only (T0); off the UI thread because the temp folder and WMI can take seconds.
        var s = await Task.Run(Probe.Read);
        var f = StorageTriage.Evaluate(s);
        return new Diagnosis(s, f, StorageTriage.Ladder(s, f), StorageTriage.ProbablyHardware(s), StorageTriage.NotFixedHint(f));
    }

    /// <summary>True when the system drive has more free space than the warning level (10% and 10 GB).</summary>
    public static Task<bool> AutoTestAsync() => Task.Run(() =>
    {
        try
        {
            var (_, free, total) = RealStorageProbe.ReadSystemDrive();
            return StorageTriage.Space(free, total) == SpaceLevel.Ok;
        }
        catch (Exception) { return false; } // fail closed
    });

    /// <summary>The automatic test fits only the "drive is full" path; slowness needs the person's answer.</summary>
    public static bool UsesAutoTest(IReadOnlyList<Finding> findings) =>
        findings.Any(f => f.Code is FindingCode.SystemDriveCritical or FindingCode.SystemDriveLow);
}
