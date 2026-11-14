namespace AutoFixScout.Triage;

/// <summary>
/// The optional rungs that read admin-only Windows reports through the elevated helper. They change nothing and are
/// never started automatically: the person is asked first, and Windows asks for permission.
/// </summary>
public static class ReadRungs
{
    public const string BootId = "read_startup_times", CrashId = "read_crash_reports";

    public const string DriveId = "read_drive_health";

    public static Rung DriveHealth() => new(DriveId, Probes.DiagResults.DriveAction, 1,
        "AutoFix Scout can read the health counters of your drives, to show how worn an SSD is, how warm a drive gets, and whether a drive has reported errors it could not correct. " +
        "Only the drive model name and these numbers are read, never serial numbers or files. Only an administrator can read these counters. " +
        "Windows will ask for your permission. AutoFix Scout only reads the drive health and changes nothing.");

    /// <summary>The drive health read is offered when drives exist and have not been read yet (the same rule for every ladder).</summary>
    public static bool OfferDriveRead(int driveCount, bool alreadyRead) => driveCount > 0 && !alreadyRead;

    public static Rung StartupTimes() => new(BootId, Probes.DiagResults.BootAction, 1,
        "AutoFix Scout can read the start-up times that Windows records, to show how long the computer takes to start and which items slow it down. " +
        "Only an administrator can read this report. " +
        "Windows will ask for your permission. AutoFix Scout only reads the start-up report and changes nothing.");

    public static Rung CrashReports() => new(CrashId, Probes.DiagResults.CrashAction, 1,
        "AutoFix Scout can read the blue screen reports that Windows keeps, to find out which driver Windows named. " +
        "Only the stop code, the driver file name and the time are read, and the crash dump files are not opened. Only an administrator can read these reports. " +
        "Windows will ask for your permission. AutoFix Scout only reads the crash reports and changes nothing.");
}
