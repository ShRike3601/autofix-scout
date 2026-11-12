namespace AutoFixScout.Triage;

/// <summary>Plain meanings of the common Windows Installer (MSI) result codes.</summary>
public static class InstallerCodes
{
    public static string Meaning(int code) => code switch
    {
        0 => "the installation finished without a problem",
        112 => "there is not enough free space on the drive",
        1601 => "the Windows Installer service could not be reached",
        1602 => "the installation was cancelled",
        1603 => "a fatal error stopped the installation",
        1618 => "another installation is already running",
        1619 => "the installation file could not be opened",
        1620 => "the installation file is damaged or not a valid installation package",
        1625 => "the installation is blocked by a policy on this computer",
        1638 => "another version of the program is already installed",
        1641 or 3010 => "the installation finished but Windows must restart to complete it",
        2502 or 2503 => "the installer could not use the temporary folder",
        _ => $"the installer reported the code {code}",
    };

    /// <summary>Codes that mean the installation worked (a restart may still be needed).</summary>
    public static bool IsSuccess(int code) => code is 0 or 1641 or 3010;
}
