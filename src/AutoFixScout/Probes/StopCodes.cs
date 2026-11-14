using System.Text.RegularExpressions;

namespace AutoFixScout.Probes;

public enum StopCategory { Memory, Driver, Storage, Graphics, Other }

/// <summary>A stop code seen in the System log and how often (last 14 days).</summary>
public sealed record StopCodeCount(uint Code, int Count);

/// <summary>What a stop code usually means. Hardware = the usual cause is a failing part, not software.</summary>
public sealed record StopMeaning(string Name, StopCategory Category, string Explanation, bool Hardware = false);

/// <summary>Blue-screen stop codes: reading them from the event text and explaining the common ones. Pure logic.</summary>
public static class StopCodes
{
    private static readonly Regex AfterLabel = new(@"bugcheck was:\s*0x([0-9a-fA-F]{1,8})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnyHex = new(@"\b0x([0-9a-fA-F]{1,8})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The stop code from the event text ("The bugcheck was: 0x0000001a (0x41790, ...)") or from its first parameter
    /// ("0x0000001a (...)" or just "0x1a"). Null when no hexadecimal code is present.
    /// </summary>
    public static uint? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = AfterLabel.Match(text);
        if (!m.Success) m = AnyHex.Match(text);
        return m.Success && uint.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : null;
    }

    public static IReadOnlyList<StopCodeCount> Count(IEnumerable<uint> codes) =>
        codes.GroupBy(c => c).Select(g => new StopCodeCount(g.Key, g.Count())).OrderByDescending(c => c.Count).ThenBy(c => c.Code).ToList();

    public static string Hex(uint code) => $"0x{code:X}";

    private static readonly Dictionary<uint, StopMeaning> Known = new()
    {
        [0x1A] = new("MEMORY_MANAGEMENT", StopCategory.Memory, "Windows found a serious problem in the way memory is used. Faulty memory (RAM) is a common cause, and so is a faulty driver."),
        [0x50] = new("PAGE_FAULT_IN_NONPAGED_AREA", StopCategory.Memory, "Windows tried to use a part of memory that does not exist or is damaged. Faulty memory (RAM) is a common cause, and so is a faulty driver or antivirus program."),
        [0x19] = new("BAD_POOL_HEADER", StopCategory.Memory, "Windows found that its own memory records were damaged. Faulty memory (RAM) or a faulty driver is the usual cause."),
        [0x3B] = new("SYSTEM_SERVICE_EXCEPTION", StopCategory.Driver, "A driver or a system service caused an error while Windows was running it. An out-of-date or faulty driver is the usual cause."),
        [0x7E] = new("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", StopCategory.Driver, "A part of Windows or a driver caused an error that it could not recover from. An out-of-date or faulty driver is the usual cause."),
        [0x0A] = new("IRQL_NOT_LESS_OR_EQUAL", StopCategory.Driver, "A driver used memory in a way that is not allowed. A faulty driver is the usual cause, and faulty memory (RAM) is possible."),
        [0xD1] = new("DRIVER_IRQL_NOT_LESS_OR_EQUAL", StopCategory.Driver, "A driver used memory in a way that is not allowed. A faulty or out-of-date driver is the usual cause, often for a network adapter."),
        [0xC2] = new("BAD_POOL_CALLER", StopCategory.Driver, "A driver or program asked Windows for memory in a way that is not allowed. A faulty driver is the usual cause."),
        [0x133] = new("DPC_WATCHDOG_VIOLATION", StopCategory.Driver, "A driver took too long to answer, so Windows stopped. An out-of-date driver or firmware, often for a storage device, is the usual cause."),
        [0x139] = new("KERNEL_SECURITY_CHECK_FAILURE", StopCategory.Driver, "Windows found damaged data in one of its own structures. A faulty driver is the usual cause, and faulty memory (RAM) is possible."),
        [0x9F] = new("DRIVER_POWER_STATE_FAILURE", StopCategory.Driver, "A driver did not respond correctly when the computer went to sleep or woke up. An out-of-date driver or a power setting is the usual cause."),
        [0x116] = new("VIDEO_TDR_FAILURE", StopCategory.Graphics, "The graphics driver stopped responding and Windows could not recover it. An out-of-date graphics driver is the usual cause, and overheating is possible."),
        [0x117] = new("VIDEO_TDR_TIMEOUT_DETECTED", StopCategory.Graphics, "The graphics driver did not respond in time. An out-of-date graphics driver is the usual cause, and overheating is possible."),
        [0x7A] = new("KERNEL_DATA_INPAGE_ERROR", StopCategory.Storage, "Windows could not read needed data from the drive. A failing drive, a loose cable or a faulty drive controller is the usual cause."),
        [0x154] = new("UNEXPECTED_STORE_EXCEPTION", StopCategory.Storage, "A problem occurred in the part of Windows that handles compressed memory, which depends on the drive. A failing drive or a faulty storage driver is the usual cause."),
        [0xF4] = new("CRITICAL_OBJECT_TERMINATION", StopCategory.Storage, "A process that Windows cannot run without was ended. A failing drive or damaged system files is the usual cause."),
        [0x124] = new("WHEA_UNCORRECTABLE_ERROR", StopCategory.Other, "The computer's hardware reported an error that could not be corrected. The processor, memory, drive or power supply is the usual cause, so software repairs rarely help.", Hardware: true),
        [0x77] = new("KERNEL_STACK_INPAGE_ERROR", StopCategory.Storage, "Windows could not read needed data from the drive. A failing drive, a loose cable or a faulty drive controller is the usual cause."),
        [0x8E] = new("KERNEL_MODE_EXCEPTION_NOT_HANDLED", StopCategory.Driver, "A part of Windows or a driver caused an error that it could not recover from. An out-of-date or faulty driver is the usual cause, and faulty memory (RAM) is possible."),
        [0x1E] = new("KMODE_EXCEPTION_NOT_HANDLED", StopCategory.Driver, "A part of Windows or a driver caused an error that it could not recover from. An out-of-date or faulty driver is the usual cause, and faulty memory (RAM) is possible."),
        [0x3D] = new("INTERRUPT_EXCEPTION_NOT_HANDLED", StopCategory.Driver, "A driver did not handle a request from the hardware correctly. An out-of-date or faulty driver is the usual cause."),
        [0xEF] = new("CRITICAL_PROCESS_DIED", StopCategory.Other, "A process that Windows cannot run without was ended. Damaged system files, a faulty driver or a failing drive is the usual cause."),
    };

    /// <summary>Null for a code that is not in the list of common ones.</summary>
    public static StopMeaning? Meaning(uint code) => Known.TryGetValue(code, out var m) ? m : null;

    public static bool IsHardware(uint code) => Meaning(code)?.Hardware == true;
}
