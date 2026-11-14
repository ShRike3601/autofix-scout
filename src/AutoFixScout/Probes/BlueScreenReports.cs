using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoFixScout.Probes;

/// <summary>The kind of cause a stop code usually points at. Drives the advice shown after a blue screen.</summary>
public enum CauseGroup { Driver, Memory, Graphics, Disk, Files, Hardware, Unknown }

/// <summary>The groups for one stop code: the usual cause first, then a second possible one (or null).</summary>
public sealed record StopCauses(CauseGroup Primary, CauseGroup? Secondary, string Kind);

/// <summary>A faulting module as the person should read it. IsWindowsItself: the core of Windows, so no specific driver is to blame.</summary>
public sealed record DriverName(string Module, string Display, bool IsWindowsItself, CauseGroup Group);

/// <summary>A blue screen module name and how often Windows named it.</summary>
public sealed record ModuleCount(string Module, int Count);

/// <summary>The fields of one Windows Error Reporting report (Report.wer) that are needed. Nothing else is kept.</summary>
public sealed record WerBlueScreen(string EventType, uint? Code, string? Module, DateTime? WhenUtc);

/// <summary>Mappers and parsers for blue screens. Pure logic: no file or event log access.</summary>
public static class BlueScreenReports
{
    // ---- stop code -> cause group ----

    private static readonly Dictionary<uint, StopCauses> Causes = new()
    {
        [0x0A] = new(CauseGroup.Driver, CauseGroup.Memory, "a driver used memory in a way that is not allowed"),
        [0xD1] = new(CauseGroup.Driver, null, "a driver used memory in a way that is not allowed"),
        [0x50] = new(CauseGroup.Driver, CauseGroup.Memory, "Windows used memory that does not exist or is damaged"),
        [0x1A] = new(CauseGroup.Memory, CauseGroup.Driver, "Windows found a serious problem in the way memory is used"),
        [0x19] = new(CauseGroup.Memory, CauseGroup.Driver, "Windows found its own memory records damaged"),
        [0x3B] = new(CauseGroup.Driver, null, "a driver or system service caused an error"),
        [0x7E] = new(CauseGroup.Driver, null, "a driver or part of Windows caused an error it could not recover from"),
        [0x8E] = new(CauseGroup.Driver, CauseGroup.Memory, "a driver or part of Windows caused an error it could not recover from"),
        [0x1E] = new(CauseGroup.Driver, CauseGroup.Memory, "a driver or part of Windows caused an error it could not recover from"),
        [0x3D] = new(CauseGroup.Driver, null, "a driver mishandled a request from the hardware"),
        [0xC2] = new(CauseGroup.Driver, null, "a driver or program asked for memory in a way that is not allowed"),
        [0x9F] = new(CauseGroup.Driver, null, "a driver did not respond correctly when the computer went to sleep or woke up"),
        [0x116] = new(CauseGroup.Graphics, CauseGroup.Hardware, "the graphics driver stopped responding"),
        [0x117] = new(CauseGroup.Graphics, CauseGroup.Hardware, "the graphics driver did not respond in time"),
        [0x124] = new(CauseGroup.Hardware, null, "the hardware reported an error that could not be corrected"),
        [0x133] = new(CauseGroup.Driver, CauseGroup.Disk, "a driver took too long to answer"),
        [0x139] = new(CauseGroup.Driver, CauseGroup.Files, "Windows found damaged data in one of its own structures"),
        [0xEF] = new(CauseGroup.Files, CauseGroup.Disk, "a process that Windows needs was ended"),
        [0x7A] = new(CauseGroup.Disk, null, "Windows could not read needed data from the drive"),
        [0x77] = new(CauseGroup.Disk, null, "Windows could not read needed data from the drive"),
    };

    /// <summary>Null for a code that is not in the list of common ones.</summary>
    public static StopCauses? CausesOf(uint code) => Causes.TryGetValue(code, out var c) ? c : null;

    public static CauseGroup GroupOf(uint code) => CausesOf(code)?.Primary ?? CauseGroup.Unknown;

    // ---- module -> friendly name ----

    private static readonly (string Pattern, string Display, bool Windows, CauseGroup Group)[] Modules =
    {
        (@"^nvlddmkm$", "NVIDIA graphics driver", false, CauseGroup.Graphics),
        (@"^(atikmdag|atikmpag|amdkmdag|amdkmdap|atidxx64|amdwddmg)$", "AMD graphics driver", false, CauseGroup.Graphics),
        (@"^(igdkmd64|igdkmd32|igdkmdn64|igdkmdn32|iigd_dch)$", "Intel graphics driver", false, CauseGroup.Graphics),
        (@"^netwtw\w*$", "Intel Wi-Fi driver", false, CauseGroup.Driver),
        (@"^(e1d\w*|e1r\w*|e2f\w*|e1i\w*)$", "Intel network (Ethernet) driver", false, CauseGroup.Driver),
        (@"^(rt640x64|rt64win7|rt68cx21x64|rtwlane\w*|rtwlanu\w*|rtl8\w*)$", "Realtek network driver", false, CauseGroup.Driver),
        (@"^(rtkvhd64|rtkhdaud|rtkaud\w*)$", "Realtek audio driver", false, CauseGroup.Driver),
        (@"^(athw8x|athwbx|qcamain\w*|qcwlan\w*)$", "Qualcomm or Atheros Wi-Fi driver", false, CauseGroup.Driver),
        (@"^(iastora|iastorac|iastorav|iastorvd)$", "Intel storage (RST) driver", false, CauseGroup.Disk),
        (@"^(stornvme|storahci|storport|disk|partmgr|classpnp)$", "Windows storage driver", false, CauseGroup.Disk),
        (@"^(ntfs|fltmgr|refs)$", "Windows file system driver", false, CauseGroup.Files),
        (@"^(tcpip|ndis|netio|wdiwifi|wfplwfs)$", "Windows network driver", false, CauseGroup.Driver),
        (@"^(dxgkrnl|dxgmms2|dxgmms1)$", "Windows graphics system", false, CauseGroup.Graphics),
        (@"^(usbxhci|usbhub3|usbport|ucx01000)$", "Windows USB driver", false, CauseGroup.Driver),
        (@"^(bthport|bthusb|bthmini|btha2dp)$", "Bluetooth driver", false, CauseGroup.Driver),
        (@"^(klif|aswsp|aswvmm|mbam|mbamchameleon|epfwwfp|avgsp|ekrn|wdfilter|wdboot)$", "security (antivirus) driver", false, CauseGroup.Driver),
        (@"^(ntoskrnl|ntkrnlmp|ntkrnlpa|hal|win32k|win32kbase|win32kfull|ci|ksecdd|wdf01000)$", "Windows itself (not a specific driver)", true, CauseGroup.Unknown),
    };

    private static readonly Regex[] ModuleRegexes = Modules.Select(m => new Regex(m.Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray();

    /// <summary>
    /// A friendly name for a module such as "nvlddmkm.sys". Accepts a path (only the file name is used). An unknown
    /// module is shown as its file name. Null for empty text.
    /// </summary>
    public static DriverName? NameModule(string? module)
    {
        if (string.IsNullOrWhiteSpace(module)) return null;
        var file = module.Trim().Trim('"').Replace('/', '\\');
        file = file[(file.LastIndexOf('\\') + 1)..];
        if (file.Length == 0) return null;
        var stem = Regex.Replace(file, @"\.(sys|dll|exe)$", "", RegexOptions.IgnoreCase);
        for (var i = 0; i < ModuleRegexes.Length; i++)
            if (ModuleRegexes[i].IsMatch(stem))
                return new DriverName(file, Modules[i].Display, Modules[i].Windows, Modules[i].Group);
        return new DriverName(file, file, false, CauseGroup.Driver);
    }

    // ---- sources ----

    private static readonly Regex Pair = new(@"^\s*(?<k>[A-Za-z0-9_.\[\]]+)\s*=\s*(?<v>.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex SigName = new(@"^(Dynamic)?Sig\[(?<n>\d+)\]\.Name$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SigValue = new(@"^(Dynamic)?Sig\[(?<n>\d+)\]\.Value$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ModuleKey = new(@"^(fault(ing)?\s*)?(module|driver|image)(\s*name)?$|^bugcheckmodule$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Reads only the needed fields from the text of a Report.wer file: EventType, EventTime, the stop code (BCCode,
    /// BugcheckCode or Code) and a module name when the report carries one. Null when it is not a BlueScreen or
    /// LiveKernelEvent report.
    /// </summary>
    public static WerBlueScreen? ParseReport(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string? type = null;
        DateTime? when = null;
        var names = new Dictionary<string, string>();
        var values = new Dictionary<string, string>();
        foreach (var line in text.Split('\n'))
        {
            var m = Pair.Match(line.TrimEnd('\r'));
            if (!m.Success) continue;
            var k = m.Groups["k"].Value; var v = m.Groups["v"].Value;
            if (k.Equals("EventType", StringComparison.OrdinalIgnoreCase)) type = v;
            else if (k.Equals("EventTime", StringComparison.OrdinalIgnoreCase) && long.TryParse(v, out var ft) && ft > 0)
            { try { when = DateTime.FromFileTimeUtc(ft); } catch (ArgumentOutOfRangeException) { } }
            else if (SigName.Match(k) is { Success: true } sn) names[sn.Groups["n"].Value] = v;
            else if (SigValue.Match(k) is { Success: true } sv) values[sv.Groups["n"].Value] = v;
        }
        if (type is null || !(type.Equals("BlueScreen", StringComparison.OrdinalIgnoreCase) || type.Equals("LiveKernelEvent", StringComparison.OrdinalIgnoreCase)))
            return null;
        uint? code = null; string? module = null;
        foreach (var (n, name) in names)
        {
            if (!values.TryGetValue(n, out var val) || val.Length == 0) continue;
            var key = name.Trim();
            if (code is null && (key.Equals("BCCode", StringComparison.OrdinalIgnoreCase) || key.Equals("BugcheckCode", StringComparison.OrdinalIgnoreCase)
                                 || key.Equals("Code", StringComparison.OrdinalIgnoreCase)))
                code = ParseCode(val);
            else if (module is null && ModuleKey.IsMatch(key) && !val.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                module = val;
        }
        return new WerBlueScreen(type, code, module, when);
    }

    /// <summary>A code from a report ("0x133", "133", "1000007e"). WER adds the flag 0x10000000 to a stop code; it is removed.</summary>
    public static uint? ParseCode(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = v[2..];
        if (!uint.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)) return null;
        return n & 0x0FFFFFFF;
    }

    // ---- grouping and recurrence ----

    /// <summary>Modules named by Windows, most frequent first. Empty names are ignored; the folder is dropped.</summary>
    public static IReadOnlyList<ModuleCount> CountModules(IEnumerable<string?> modules) =>
        modules.Select(m => NameModule(m)?.Module).Where(m => m is not null)
            .GroupBy(m => m!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ModuleCount(g.Key, g.Count())).OrderByDescending(c => c.Count).ThenBy(c => c.Module, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// How often the blue screens recur. Days = different calendar days; KeepsHappening = three or more in all, or two
    /// on two different days; SameCodeEachTime = one stop code, seen at least twice.
    /// </summary>
    public sealed record Recurrence(int Total, int Days, bool SameCodeEachTime, bool KeepsHappening);

    public static Recurrence Recur(int total, IEnumerable<DateTime> times, IReadOnlyList<StopCodeCount> codes)
    {
        var days = times.Select(t => t.Date).Distinct().Count();
        var same = codes.Count == 1 && codes[0].Count >= 2;
        var keeps = total >= 3 || (total >= 2 && days >= 2);
        return new Recurrence(total, days, same, keeps);
    }
}
