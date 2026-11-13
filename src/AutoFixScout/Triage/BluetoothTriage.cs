using AutoFixScout.Probes;

namespace AutoFixScout.Triage;

/// <summary>
/// "Bluetooth is not working" (headphones will not connect, mouse or keyboard not found). Pure local logic.
/// The radio switch itself is never changed by the app: the person turns it on in Settings (spec 8.1).
/// </summary>
public static class BluetoothTriage
{
    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    private const string Incomplete = "AutoFix Scout could not complete the Bluetooth check.";

    public static BtAdapterInfo? Adapter(BluetoothState s) =>
        s.Adapters.OrderBy(a => a.ProblemCode is null or 0 ? 0 : 1).FirstOrDefault();

    /// <summary>Findings, most important first; always at least one.</summary>
    public static IReadOnlyList<Finding> Evaluate(BluetoothState s)
    {
        if (s.ProbeErrors.Count > 0)
            return new[] { F(FindingCode.ProbeIncomplete, Incomplete, string.Join("; ", s.ProbeErrors)) };
        var f = new List<Finding>();
        var a = Adapter(s);

        if (a is null)
            f.Add(F(FindingCode.NoBluetoothAdapter, "Windows cannot find a Bluetooth adapter in this computer at the moment.", "no Bluetooth-class adapter"));
        else if (a.Disabled)
            f.Add(F(FindingCode.BluetoothAdapterDisabled, "The Bluetooth adapter is switched off in Windows.", $"'{a.Name}' problem code 22"));
        else if (a.ProblemCode is { } code and not 0)
            f.Add(F(FindingCode.BluetoothAdapterError, "The Bluetooth adapter reports a problem.", $"'{a.Name}' problem code {code}"));

        if (s.ServiceStatus is "Stopped" or "Disabled")
            f.Add(F(FindingCode.BluetoothServiceStopped, "The part of Windows that manages Bluetooth connections is not running.", $"bthserv {s.ServiceStatus}"));

        if (a is { ProblemCode: null or 0 } && s.RadioOn == Tri.No)
            f.Add(F(FindingCode.BluetoothOff, "Bluetooth is switched off.", "radio state off"));

        if (a is { ProblemCode: null or 0 } && s.RadioOn != Tri.No && s.Paired.Count == 0)
            f.Add(F(FindingCode.BluetoothNothingPaired, "No Bluetooth devices are paired with this computer yet.", "0 paired devices"));
        else if (s.Paired.Any(p => p.IsAudio) && s.Paired.Where(p => p.IsAudio).All(p => p.Connected == Tri.No))
            f.Add(F(FindingCode.BluetoothAudioNotConnected, "Your Bluetooth headphones or speaker are paired but not connected.",
                string.Join(", ", s.Paired.Where(p => p.IsAudio).Select(p => p.Name))));

        if (f.Count > 0) return f;
        if (s.RadioOn == Tri.Unknown || s.ServiceStatus == "Unknown")
            return new[] { F(FindingCode.ProbeIncomplete, Incomplete, $"radio={s.RadioOn}; service={s.ServiceStatus}") };
        return new[] { F(FindingCode.LooksNormal, "Bluetooth is switched on and Windows reports no problem with it.",
            $"adapter '{a!.Name}'; paired: {string.Join(", ", s.Paired.Select(p => $"{p.Name} ({p.Connected})"))}") };
    }

    /// <summary>Least risky first.</summary>
    public static IReadOnlyList<Rung> Ladder(BluetoothState s, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(x => x.Code).ToHashSet();
        var r = new List<Rung>();
        if (codes.Contains(FindingCode.ProbeIncomplete)) return r;
        var a = Adapter(s);

        if (codes.Contains(FindingCode.BluetoothServiceStopped))
            r.Add(s.ServiceStatus == "Disabled"
                ? new("restore_bthserv", "service.restore_default", 1,
                    "The \"Bluetooth Support Service\" is switched off. AutoFix Scout can set it back to the way Windows normally runs it. Windows will ask for your permission.", "bthserv")
                : new("start_bthserv", "service.restart", 1,
                    "AutoFix Scout can start the \"Bluetooth Support Service\". Windows will ask for your permission.", "bthserv"));
        if (codes.Contains(FindingCode.BluetoothOff) || codes.Contains(FindingCode.LooksNormal))
            r.Add(new("bluetooth_settings", "ui.open_settings_page", 0,
                "AutoFix Scout will open the \"Bluetooth and devices\" settings. Please make sure the \"Bluetooth\" switch is on, " +
                "and that \"Airplane mode\" is off.", "bluetooth"));
        if (codes.Contains(FindingCode.BluetoothAdapterDisabled) && a is not null)
            r.Add(new("enable_adapter", "device.enable", 1,
                "AutoFix Scout can switch the Bluetooth adapter back on. Windows will ask for your permission.", a.InstanceId));
        if (codes.Contains(FindingCode.BluetoothAdapterError) && a is not null)
            r.Add(new("restart_adapter", "device.disable_enable", 1,
                "AutoFix Scout can restart the Bluetooth adapter. Connected Bluetooth devices disconnect for a moment. Windows will ask for your permission.", a.InstanceId));
        if (codes.Contains(FindingCode.NoBluetoothAdapter) || codes.Contains(FindingCode.BluetoothAdapterError))
            r.Add(Rescan.Rung("bluetooth", "Bluetooth adapter"));
        if (codes.Contains(FindingCode.BluetoothNothingPaired))
            r.Add(new("pair_device", "ui.open_settings_page", 0,
                "AutoFix Scout will open the \"Bluetooth and devices\" settings. Please put your device into pairing mode (see its manual; " +
                "often a button held for a few seconds), then select \"Add device\", then \"Bluetooth\", and choose your device.", "bluetooth"));
        if (codes.Contains(FindingCode.BluetoothAudioNotConnected))
        {
            r.Add(new("connect_audio", "ui.open_settings_page", 0,
                "Please switch your headphones or speaker on and keep them close to the computer. AutoFix Scout will open the " +
                "\"Bluetooth and devices\" settings; please select your device, then \"Connect\".", "bluetooth"));
            r.Add(new("repair_pairing", "ui.open_settings_page", 0,
                "If the device still does not connect, pairing it again usually helps. In \"Bluetooth and devices\", select the three dots " +
                "next to the device, then \"Remove device\", and then add it again with \"Add device\".", "bluetooth"));
        }
        if (a is { ProblemCode: null or 0 } && !r.Any(x => x.ActionId == "device.disable_enable") && !codes.Contains(FindingCode.BluetoothOff))
            r.Add(new("restart_adapter", "device.disable_enable", 1,
                "AutoFix Scout can restart the Bluetooth adapter. Connected Bluetooth devices disconnect for a moment. Windows will ask for your permission.", a.InstanceId));
        return r;
    }

    /// <summary>No adapter even after a rescan, or an adapter that keeps reporting an error, points at hardware.</summary>
    public static bool ProbablyHardware(BluetoothState s) =>
        s.ProbeErrors.Count == 0 && (Adapter(s) is null || Adapter(s)!.ProblemCode is { } c && c != 0 && c != 22);

    public static string? NotFixedHint(IReadOnlyList<Finding> after) =>
        after.Any(x => x.Code == FindingCode.NoBluetoothAdapter)
            ? "Windows still cannot find a Bluetooth adapter. Some laptops have a key or switch that turns wireless off; please check it. " +
              "If the computer never had Bluetooth, a small USB Bluetooth adapter can add it."
            : null;

    /// <summary>Automatic part of the real test: adapter working, service running, radio on.</summary>
    public static bool Ready(BluetoothState s) =>
        s.ProbeErrors.Count == 0 && Adapter(s) is { ProblemCode: null or 0 } && s.ServiceStatus == "Running" && s.RadioOn == Tri.Yes;
}
