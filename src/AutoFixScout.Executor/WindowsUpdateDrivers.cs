namespace AutoFixScout.Executor;

/// <summary>
/// driver.install_from_windows_update (spec 7.1, 10): Windows Update Agent COM API, Type='Driver', matched by hardware ID.
/// Packages from Windows Update are signed and vetted by Microsoft; nothing is downloaded from anywhere else.
/// </summary>
internal static class WindowsUpdateDrivers
{
    private const int OrcSucceeded = 2; // OperationResultCode.orcSucceeded

    private static dynamic NewCom(string progId) => Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!;

    /// <summary>The driver update Windows Update offers for one of these hardware IDs, or null (none, or WU unreachable).</summary>
    public static dynamic? Find(IReadOnlyList<string> hardwareIds)
    {
        try
        {
            dynamic session = NewCom("Microsoft.Update.Session");
            session.ClientApplicationID = "AutoFix Scout";
            dynamic result = session.CreateUpdateSearcher().Search("IsInstalled=0 and Type='Driver'");
            // Most specific hardware ID first, so a generic match never wins over an exact one.
            foreach (var hwid in hardwareIds)
                foreach (dynamic update in result.Updates)
                    if (string.Equals((string)update.DriverHardwareID, hwid, StringComparison.OrdinalIgnoreCase))
                        // Never accept a licence on the person's behalf (spec 15): skip drivers that need one.
                        return (bool)update.EulaAccepted ? update : null;
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Windows Update search failed: {ex.Message}");
            return null;
        }
    }

    public static void Install(dynamic update)
    {
        dynamic session = NewCom("Microsoft.Update.Session");
        session.ClientApplicationID = "AutoFix Scout";
        dynamic updates = NewCom("Microsoft.Update.UpdateColl");
        updates.Add(update);

        dynamic downloader = session.CreateUpdateDownloader();
        downloader.Updates = updates;
        if ((int)downloader.Download().ResultCode != OrcSucceeded) throw new InvalidOperationException("Windows Update download failed");

        dynamic installer = session.CreateUpdateInstaller();
        installer.Updates = updates;
        // A result that needs a reboot still goes through the post-check; if the device isn't right yet, it's undone.
        if ((int)installer.Install().ResultCode != OrcSucceeded) throw new InvalidOperationException("Windows Update install failed");
    }
}
