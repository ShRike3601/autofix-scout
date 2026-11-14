using System.Text.RegularExpressions;

namespace AutoFixScout.Probes;

public enum GpuKind { Integrated, Dedicated, Virtual }

/// <summary>A graphics adapter as Win32_VideoController reports it. DrivesDisplay: Windows lists a current resolution for it (it owns an active screen).</summary>
public sealed record GpuInfo(string Name, string? DriverVersion, DateTime? DriverDate, long? AdapterRamBytes, GpuKind Kind, bool DrivesDisplay, string? PnpId = null)
{
    public bool IsNvidia => Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase);

    /// <summary>Integrated, dedicated or virtual, from the adapter name. Unknown names count as integrated, so nothing is claimed about them.</summary>
    public static GpuKind Classify(string? name)
    {
        var n = name ?? "";
        bool Has(string s) => n.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("Microsoft Basic") || Has("Remote Display") || Has("Hyper-V") || Has("VMware") || Has("VirtualBox") || Has("Virtual") || Has("Parsec") || Has("Citrix") || Has("Mirage"))
            return GpuKind.Virtual;
        if (Has("NVIDIA") || Has("GeForce") || Has("Quadro")) return GpuKind.Dedicated;
        if (Has("Intel"))
            return Has("Arc") && !Has("Graphics Family") ? GpuKind.Dedicated : GpuKind.Integrated;
        if (Has("Radeon") || Has("AMD"))
        {
            // "AMD Radeon(TM) Graphics", "Radeon Vega 8", "Radeon 780M" are integrated; "Radeon RX 6600", "Radeon Pro" are dedicated.
            if (Regex.IsMatch(n, @"\b\d{3,4}M\b") || Has("Vega 3") || Has("Vega 6") || Has("Vega 7") || Has("Vega 8") || Has("Vega 10") || Has("Vega 11")
                || Has("Radeon(TM) Graphics") || Has("Radeon Graphics"))
                return GpuKind.Integrated;
            return Regex.IsMatch(n, @"\b(RX|R[579]|HD|Pro|Vega|VII|Fury)\b", RegexOptions.IgnoreCase) ? GpuKind.Dedicated : GpuKind.Integrated;
        }
        return GpuKind.Integrated;
    }
}

/// <summary>One nvidia-smi row. Null = the card reported [N/A] or the field does not exist on this driver.</summary>
public sealed record NvidiaReading(
    string Name, int? TemperatureC, int? ClockGraphicsMhz, int? ClockGraphicsMaxMhz, int? ClockMemoryMhz, int? ClockMemoryMaxMhz,
    double? PowerDrawW, double? PowerLimitW, int? PcieGenCurrent, int? PcieGenMax, int? PcieWidthCurrent, int? PcieWidthMax,
    ulong? ThrottleReasons, bool? HwThermalSlowdown, bool? SwThermalSlowdown, bool? HwPowerBrake)
{
    /// <summary>Thermal slowdown reported by the card (explicit flags or the thermal bits 0x20 and 0x40 of the reasons mask).</summary>
    public bool ThermalSlowdown => HwThermalSlowdown == true || SwThermalSlowdown == true || ((ThrottleReasons ?? 0) & 0x60) != 0;
    public bool PowerBrake => HwPowerBrake == true || ((ThrottleReasons ?? 0) & 0x80) != 0;
    /// <summary>True when the link is narrower than the card supports at a width that is not an idle power saving step (x1 and x2 are).</summary>
    public bool PcieWidthReduced => PcieWidthCurrent is { } c && PcieWidthMax is { } m && c < m && c >= 4;
}

/// <summary>One saved Afterburner file: clock offsets in kHz (as Afterburner stores them) and voltage offset in mV. InStartupSection = the "[Startup]" values Afterburner applies when it starts.</summary>
public sealed record AfterburnerProfile(string File, int CoreClockKhz, int MemoryClockKhz, int VoltageMv, bool CustomCurve, bool InStartupSection)
{
    public bool HasOffsets => CoreClockKhz != 0 || MemoryClockKhz != 0 || VoltageMv != 0 || CustomCurve;
}

public sealed class OverclockToolState
{
    public Tri AfterburnerInstalled { get; set; } = Tri.Unknown;
    public Tri AfterburnerRunning { get; set; } = Tri.Unknown;
    public Tri AfterburnerStartsWithWindows { get; set; } = Tri.Unknown;
    public IReadOnlyList<AfterburnerProfile> Profiles { get; set; } = new List<AfterburnerProfile>();
    /// <summary>Other tuning tools found installed (RivaTuner Statistics Server, EVGA Precision).</summary>
    public IReadOnlyList<string> OtherTools { get; set; } = new List<string>();

    /// <summary>An offset is saved for startup and Afterburner is running or starts with Windows, so the overclock is in effect.</summary>
    public bool OverclockActive => AfterburnerInstalled == Tri.Yes
        && Profiles.Any(p => p.InStartupSection && p.HasOffsets)
        && (AfterburnerRunning == Tri.Yes || AfterburnerStartsWithWindows == Tri.Yes);

    public bool ToolInstalled => AfterburnerInstalled == Tri.Yes || OtherTools.Count > 0;
}

/// <summary>Windows game settings that commonly cause stutter. Unknown (or null) means "could not be read" and never raises a finding.</summary>
public sealed class GameSettingsState
{
    public Tri GameMode { get; set; } = Tri.Unknown;
    /// <summary>Hardware-accelerated GPU scheduling: Yes = on (HwSchMode 2), No = off (1).</summary>
    public Tri HardwareScheduling { get; set; } = Tri.Unknown;
    /// <summary>File names only (never folders) of games set to the power saving graphics chip.</summary>
    public IReadOnlyList<string> PowerSavingGames { get; set; } = new List<string>();
    /// <summary>Windows 11 power mode (overlay) GUID from the performance check's reader; null when not available.</summary>
    public string? PowerOverlayGuid { get; set; }
    /// <summary>Running on battery now; null = unknown.</summary>
    public bool? OnBattery { get; set; }
    /// <summary>"Optimizations for windowed games": Yes = on, No = turned off, Unknown = no saved value.</summary>
    public Tri WindowedOptimisations { get; set; } = Tri.Unknown;
    public Tri VariableRefresh { get; set; } = Tri.Unknown;
    /// <summary>Xbox Game Bar background recording is switched on.</summary>
    public Tri BackgroundRecording { get; set; } = Tri.Unknown;
    public long? TotalRamBytes { get; set; }
}

/// <summary>probe.graphics. Null or Unknown means "could not be read", never "fine".</summary>
public sealed class GraphicsState
{
    public IReadOnlyList<GpuInfo> Gpus { get; set; } = new List<GpuInfo>();
    public IReadOnlyList<NvidiaReading> Nvidia { get; set; } = new List<NvidiaReading>();
    /// <summary>nvidia-smi.exe exists on this computer.</summary>
    public bool NvidiaToolPresent { get; set; }
    /// <summary>BAR1 total in MiB from "nvidia-smi -q -d MEMORY"; null = not read. Above 256 MiB means Resizable BAR is on.</summary>
    public long? Bar1TotalMiB { get; set; }
    public Tri ResizableBar { get; set; } = Tri.Unknown;
    public OverclockToolState Tools { get; set; } = new();
    /// <summary>Desktop computer (not a laptop or tablet) according to the chassis type and battery; Unknown when not readable.</summary>
    public Tri IsDesktop { get; set; } = Tri.Unknown;
    /// <summary>Graphics driver resets in the last 14 days (shared reader with the health check).</summary>
    public int GraphicsResets { get; set; }
    public GameSettingsState Games { get; set; } = new();
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();

    public GpuInfo? Dedicated => Gpus.FirstOrDefault(g => g.Kind == GpuKind.Dedicated);
}

public interface IGraphicsProbe { GraphicsState Read(); }
