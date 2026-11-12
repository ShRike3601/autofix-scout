namespace AutoFixScout.Repair;

public enum CrashCause
{
    /// <summary>Visual C++ runtime files (vcruntime, msvcp, ucrtbase): reinstalling the Microsoft runtime fixes most.</summary>
    Runtime,
    /// <summary>A required file is missing (0xc0000135) or failed to load (0xc0000142).</summary>
    MissingFile,
    /// <summary>.NET (clr, coreclr, or the .NET exception code e0434352).</summary>
    DotNet,
    /// <summary>Graphics driver (NVIDIA, AMD, Intel user-mode drivers, Direct3D).</summary>
    Graphics,
    /// <summary>Core Windows files (ntdll, kernelbase, combase...): system file repair first.</summary>
    WindowsFiles,
    /// <summary>The program's own files: repair or reinstall the program.</summary>
    Program,
    Unknown,
}

/// <summary>
/// Turns the faulting module and exception code from a Windows "Application Error" (event 1000) into a likely cause.
/// Pure logic, so it is unit tested. Order matters: the exception code is more specific than the module name.
/// </summary>
public static class CrashCauses
{
    private static readonly string[] RuntimeModules = { "vcruntime", "msvcp", "msvcr", "ucrtbase", "api-ms-win-crt", "concrt", "vcomp" };
    private static readonly string[] DotNetModules = { "clr.dll", "coreclr.dll", "clrjit.dll", "mscorwks.dll", "mscorlib", "system.private.corelib" };
    private static readonly string[] GraphicsModules =
    {
        "nvwgf2um", "nvoglv", "nvd3dum", "nvldumd", "nvcuda", "atiumd", "atidxx", "atio6axx", "amdxc", "amdxx", "amfrt",
        "igd10", "igdumd", "igd12", "igxelpicd", "ig9icd", "ig7icd", "igc64", "d3d11.dll", "d3d12.dll", "d3d9.dll", "dxgi.dll", "opengl32.dll",
    };
    private static readonly string[] WindowsModules =
    {
        "ntdll.dll", "kernelbase.dll", "kernel32.dll", "combase.dll", "user32.dll", "win32u.dll", "gdi32full.dll", "ole32.dll",
        "shell32.dll", "windows.storage.dll", "twinapi.appcore.dll", "msvcrt.dll", "rpcrt4.dll", "sechost.dll", "comctl32.dll",
    };

    public static CrashCause Classify(string? module, string? exceptionCode, string? appName = null)
    {
        var m = (module ?? "").Trim().ToLowerInvariant();
        var code = (exceptionCode ?? "").Trim().ToLowerInvariant().Replace("0x", "");
        if (code is "c0000135" or "c0000142" or "c06d007e") return CrashCause.MissingFile; // dll not found / init failed / delay-load
        if (code == "e0434352" || code == "e0434f4d") return CrashCause.DotNet;               // .NET managed exception
        if (code == "c0000409" && RuntimeModules.Any(m.StartsWith)) return CrashCause.Runtime;
        if (RuntimeModules.Any(m.StartsWith)) return CrashCause.Runtime;
        if (DotNetModules.Any(m.StartsWith)) return CrashCause.DotNet;
        if (GraphicsModules.Any(m.StartsWith)) return CrashCause.Graphics;
        if (m.Length > 0 && appName is { Length: > 0 } && m.Equals(appName.Trim().ToLowerInvariant())) return CrashCause.Program;
        if (WindowsModules.Contains(m)) return CrashCause.WindowsFiles;
        if (m.Length == 0 || m == "unknown") return CrashCause.Unknown;
        return CrashCause.Program; // a module that is neither Windows, runtime nor graphics belongs to the program (or a plug-in)
    }

    /// <summary>Windows' own shell programs; their crashes mean the Start menu, taskbar or search are affected.</summary>
    public static readonly string[] ShellApps =
    {
        "explorer.exe", "startmenuexperiencehost.exe", "shellexperiencehost.exe", "searchhost.exe", "searchapp.exe",
        "searchui.exe", "textinputhost.exe", "sihost.exe", "ctfmon.exe", "runtimebroker.exe",
    };

    public static bool IsShell(string app) => ShellApps.Contains(app.Trim().ToLowerInvariant());
}
