namespace AutoFixScout.Triage;

/// <summary>
/// "I can't get online" (spec 8.3). Pure local logic: works with no LLM and no internet.
/// shortcut: ladder is hardcoded like AudioLadder; move both to YAML playbooks together.
/// </summary>
public static class NetworkTriage
{
    /// <summary>A Wi-Fi driver older than two years (from the adapter's class key).</summary>
    public static bool OldWifiDriver(NetworkState s, DateTime? now = null) =>
        s.WifiDriverDate is { } d && (now ?? DateTime.Now) - d > Repair.WifiTuning.OldDriver;

    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    private const string Incomplete = "AutoFix Scout could not complete the network check.";

    /// <summary>Findings in severity order; always at least one.</summary>
    public static IReadOnlyList<Finding> Evaluate(NetworkState s)
    {
        if (s.ProbeErrors.Count > 0)
            return new[] { F(FindingCode.ProbeIncomplete, Incomplete, string.Join("; ", s.ProbeErrors)) };
        if (s.Adapters.Count == 0)
            return new[] { F(FindingCode.NoNetworkAdapter, "Windows cannot find a Wi-Fi or network adapter in this computer.", "No physical adapters") };

        if (!s.Adapters.Any(a => a.Connected))
        {
            if (s.AirplaneMode == Tri.Yes)
                return new[] { F(FindingCode.AirplaneModeOn, "\"Airplane mode\" is turned on, so Wi-Fi is turned off.", "SystemRadioState=1") };
            var disabled = s.Adapters.OrderByDescending(a => a.IsWireless).FirstOrDefault(a => a.Disabled);
            if (disabled != null)
                return new[] { F(FindingCode.NetAdapterDisabled, "Your Wi-Fi or network adapter is turned off.", $"'{disabled.Name}' disabled (code 22)") };
            var broken = s.Adapters.FirstOrDefault(a => a.ProblemCode is > 0);
            if (broken != null)
                return new[] { F(FindingCode.NetAdapterError, "Your Wi-Fi or network adapter reports a problem.", $"'{broken.Name}' problem code {broken.ProblemCode}") };
            return new[] { F(FindingCode.NotConnected, "Your computer is not connected to Wi-Fi or to a network cable.", "No adapter connected") };
        }

        // Ping alone isn't trusted (some routers ignore it), so a silent router only counts when DNS fails too.
        return WithExtras(s, Main(s));
    }

    /// <summary>The existing verdict for a connected computer. Unchanged by the extra checks.</summary>
    private static Finding[] Main(NetworkState s)
    {
        return (s.GatewayReachable, s.DnsResolving, s.InternetReachable) switch
        {
            (_, _, Tri.Yes) => new[] { F(FindingCode.LooksNormal, "Your internet connection is working.", "NCSI test passed") },
            (Tri.No, Tri.No, _) => new[] { F(FindingCode.GatewayUnreachable, "Your computer is connected, but it cannot reach your router.", "Gateway ping failed and DNS failed") },
            (Tri.Yes, Tri.No, _) => new[] { F(FindingCode.DnsFailing, "Your computer can reach your router, but it cannot look up website addresses.", "Gateway ok, DNS failed") },
            (_, Tri.Yes, Tri.No) => new[] { F(FindingCode.NoInternet, "Your computer can reach your router, but the internet is not responding.", "DNS ok, NCSI test failed") },
            _ => new[] { F(FindingCode.ProbeIncomplete, Incomplete, $"gateway={s.GatewayReachable} dns={s.DnsResolving} internet={s.InternetReachable}") },
        };
    }

    /// <summary>
    /// Extra findings after the main one. The main finding stays first, except that "no address from the router" replaces
    /// "cannot reach your router" (it is the reason for it). Order: no address, IP conflict, weak signal, metered.
    /// </summary>
    private static IReadOnlyList<Finding> WithExtras(NetworkState s, Finding[] main)
    {
        if (main[0].Code == FindingCode.ProbeIncomplete) return main; // fail closed
        var list = new List<Finding>();
        var noAddress = s.AddressObtained == Tri.No;
        if (noAddress && main[0].Code == FindingCode.GatewayUnreachable) list.Add(NoAddress()); // the reason for it
        else
        {
            list.AddRange(main);
            if (noAddress) list.Add(NoAddress());
        }
        if (s.IpConflictRecent)
            list.Add(F(FindingCode.NetIpConflict, "Another device on your network recently used the same address as your computer.", "Tcpip event 4199 in the last 24 hours"));
        if (s.WifiSignalPercent is < NetworkChecks.WeakSignalPercent and var pct)
            list.Add(F(FindingCode.NetWeakWifi, $"Your Wi-Fi signal is weak ({pct} percent).", $"signal quality {pct}%"));
        if (s.WifiDrops is { Unintentional: >= WifiDrops.Threshold } d)
            list.Add(F(FindingCode.NetWifiDrops,
                $"Your Wi-Fi has disconnected {d.Unintentional} times in the last {WifiDrops.Days} days without being switched off by a person." +
                (d.SignInFailures > 0 ? " Windows could not always sign in to the network again by itself." : "") +
                (OldWifiDriver(s) ? $" The Wi-Fi driver is from {s.WifiDriverDate:MMMM yyyy}, which is old." : ""),
                $"{d.Describe()}; sign-in failures {d.SignInFailures}; power saving may turn the adapter off: {s.WifiPowerSavingMayTurnOff}; " +
                $"driver {s.WifiDriverVersion} ({s.WifiDriverDate:yyyy-MM-dd}); enterprise {s.WifiEnterprise}; tuning pending: {string.Join(", ", s.WifiTuningPending)}; Wi-Fi watcher {s.WifiWatcher}" +
                (s.WifiWatch is { } w ? $"; the watcher reconnected {w.ReconnectedByWatcher} times ({w.Drops} drops recorded in {w.Days} days)" : "")));
        if (s.Metered == Tri.Yes)
            list.Add(F(FindingCode.NetMetered, "Your connection is set as a \"Metered connection\", so Windows Update and OneDrive may pause.", "connection cost is fixed or variable"));
        return list;
    }

    private static Finding NoAddress() =>
        F(FindingCode.NetNoAddress, "Your computer is connected, but your router has not given it an address.", "only a 169.254.x.x address or no default gateway");

    public static IReadOnlyList<Rung> Ladder(NetworkState s, IReadOnlyList<Finding> findings)
    {
        var code = findings[0].Code;
        var connected = s.Adapters.OrderByDescending(a => a.IsWireless).FirstOrDefault(a => a.Connected);
        var rungs = new List<Rung>();
        switch (code)
        {
            case FindingCode.NoNetworkAdapter:
                rungs.Add(Rescan.Rung("network", "Wi-Fi or network adapter"));
                break;
            case FindingCode.AirplaneModeOn:
                rungs.Add(new("airplane", "ui.open_settings_page", 0,
                    "\"Airplane mode\" is turned on. AutoFix Scout will open the \"Airplane mode\" settings. " +
                    "Please set \"Airplane mode\" to \"Off\".", "airplane"));
                break;
            case FindingCode.NetAdapterDisabled:
                var off = s.Adapters.OrderByDescending(a => a.IsWireless).First(a => a.Disabled);
                rungs.Add(new("enable_adapter", "device.enable", 1,
                    $"Your network adapter \"{off.Name}\" is turned off. AutoFix Scout can turn it on. Windows will ask for your permission.", off.InstanceId));
                break;
            case FindingCode.NetAdapterError:
                var broken = s.Adapters.First(a => a.ProblemCode is > 0);
                rungs.Add(new("toggle_adapter", "device.disable_enable", 1,
                    $"Your network adapter \"{broken.Name}\" reports a problem. AutoFix Scout can turn it off and on again. " +
                    "Windows will ask for your permission.", broken.InstanceId));
                AddIfNotNull(rungs, DriverRules.Rollback("network", broken.Name, broken.InstanceId, broken.Driver));
                break;
            case FindingCode.NotConnected:
                rungs.Add(s.Adapters.Any(a => a.IsWireless)
                    ? new("pick_wifi", "ui.open_settings_page", 0,
                        "Your computer is not connected to Wi-Fi. AutoFix Scout will open the \"Wi-Fi\" settings. " +
                        "Please select your network and connect to it.", "wifi")
                    : new("check_cable", "ui.human_step", 0,
                        "Please check that the network cable is firmly plugged in at both ends: at the computer and at the router."));
                break;
            case FindingCode.GatewayUnreachable or FindingCode.DnsFailing or FindingCode.NoInternet:
                if (code == FindingCode.DnsFailing)
                    rungs.Add(new("flush_dns", "network.flush_dns", 1,
                        "AutoFix Scout can clear the list of website addresses that your computer has stored. This is safe."));
                // A hand-typed DNS server survives a flush and an adapter restart (found in test step 3d). Loopback ones
                // belong to a VPN such as Cloudflare WARP, which manages them itself, so those are left alone.
                if (code == FindingCode.DnsFailing && s.StaticDnsAdapterId != null
                    && s.StaticDnsServers.Count > 0 && !s.StaticDnsServers.All(d => d.StartsWith("127.") || d == "::1"))
                    rungs.Add(new("reset_dns", "network.reset_dns", 1,
                        $"Your computer is set to use a fixed website lookup (DNS) server ({string.Join(", ", s.StaticDnsServers)}) " +
                        "that is not responding. AutoFix Scout can switch it back to automatic. Windows will ask for your permission.", s.StaticDnsAdapterId));
                if (connected != null)
                    rungs.Add(new("toggle_adapter", "device.disable_enable", 1,
                        $"AutoFix Scout can turn your network adapter \"{connected.Name}\" off and on again. Windows will ask for your permission.", connected.InstanceId));
                if (connected != null)
                    AddIfNotNull(rungs, DriverRules.Rollback("network", connected.Name, connected.InstanceId, connected.Driver));
                rungs.Add(new("restart_router", "ui.human_step", 0,
                    "Please unplug your router for 30 seconds, then plug it back in. Then wait about two minutes until its lights are steady."));
                break;
        }
        // Rungs of the extra findings follow those of the main one (a working connection has none of its own).
        foreach (var f in findings)
            switch (f.Code)
            {
                case FindingCode.NetNoAddress or FindingCode.NetIpConflict:
                    if (connected == null) break;
                    rungs.Add(new("renew_address", "network.renew_address", 1,
                        $"AutoFix Scout can ask your router for a new address for \"{connected.Name}\". " +
                        "Your connection may pause for a few seconds. Windows will ask for your permission.", connected.InstanceId));
                    if (f.Code == FindingCode.NetNoAddress)
                        rungs.Add(new("toggle_adapter", "device.disable_enable", 1,
                            $"AutoFix Scout can turn your network adapter \"{connected.Name}\" off and on again. Windows will ask for your permission.", connected.InstanceId));
                    rungs.Add(RestartRouter);
                    break;
                case FindingCode.NetWeakWifi:
                    rungs.Add(new("move_closer", "ui.human_step", 0,
                        "Please move closer to your router and away from thick walls or large metal objects. Then run the check again."));
                    if (s.Supports5GHz == Tri.Yes)
                        rungs.Add(new("use_5ghz", "ui.open_settings_page", 0,
                            "Your Wi-Fi adapter supports the 5 GHz band, which is often faster at short range. " +
                            "AutoFix Scout will open the \"Wi-Fi\" settings. If your router offers a 5 GHz network, please select it and connect.", "wifi"));
                    rungs.Add(RestartRouter);
                    break;
                case FindingCode.NetWifiDrops:
                    // Found on the bench (university WPA2-Enterprise): without a saved sign-in, every short drop became a
                    // long outage because Windows could not sign in again by itself (event 12013). Saving it fixed that.
                    if (s.WifiDrops is { SignInFailures: > 0 })
                        rungs.Add(new("wifi_save_signin", "ui.human_step", 0,
                            "Windows could not sign in to this Wi-Fi network again by itself after it disconnected, so each short drop lasts much longer. " +
                            "Please save your network sign-in: press Windows+R, type ncpa.cpl and press Enter. Right-click \"Wi-Fi\", select \"Status\", then \"Wireless Properties\". " +
                            "On the \"Security\" tab, select \"Advanced settings\", tick \"Specify authentication mode\", choose \"User authentication\", select \"Save credentials\" " +
                            "and enter the user name and password for this network."));
                    // Opt-in helper from the bench case: Windows did not always reconnect by itself and the owner had to do it by hand.
                    if (s.WifiWatcher == Tri.No)
                        rungs.Add(new("wifi_watch", "network.wifi_watch_on", 1,
                            "AutoFix Scout can switch on the Wi-Fi watcher, a small helper without a window. It starts with Windows and reconnects your Wi-Fi automatically " +
                            "when Windows does not do so within about 20 seconds. It reconnects only to saved networks that you have used before, and it sends nothing anywhere. " +
                            "It keeps only a count of the disconnects, never the name of a network. You can switch it off at any time in AutoFix Scout " +
                            "or in the Windows \"Settings\" under \"Apps\" and \"Startup\". Windows will not ask for permission."));
                    // Same bench case: an old driver on a busy enterprise network also needed these adapter settings.
                    if (s.WifiTuningPending.Count > 0 && s.WifiInstanceId != null && (s.WifiEnterprise == Tri.Yes || OldWifiDriver(s)))
                        rungs.Add(new("wifi_stability", "network.wifi_stability", 1,
                            "AutoFix Scout can change Wi-Fi adapter settings that often cause drops on busy networks with older drivers: " +
                            string.Join(", ", s.WifiTuningPending) + ". The current settings are saved first, the Wi-Fi reconnects within a few seconds, " +
                            "and if it does not reconnect within four minutes the previous settings are restored automatically. Windows will ask for your permission.",
                            s.WifiInstanceId));
                    if (OldWifiDriver(s))
                        rungs.Add(new("wifi_old_driver", "ui.open_settings_page", 0,
                            $"The Wi-Fi driver is from {s.WifiDriverDate:MMMM yyyy}. A newer driver fixes many disconnects. AutoFix Scout will open \"Windows Update\". " +
                            "Please choose \"Advanced options\", then \"Optional updates\", and install any driver listed for the Wi-Fi adapter. If none is listed, " +
                            "the newest Wi-Fi driver is available from the computer maker's support page or the Wi-Fi chip maker (for example Intel).", "windowsupdate"));
                    if (s.WifiPowerSavingMayTurnOff != Tri.No)
                        rungs.Add(new("wifi_power_saving", "ui.human_step", 0,
                            "Windows may be turning your Wi-Fi adapter off to save power. Please open \"Device Manager\", open \"Network adapters\", " +
                            "double-click your Wi-Fi adapter, choose the \"Power Management\" tab and clear \"Allow the computer to turn off this device to save power\". " +
                            "Windows may ask for permission, because this change needs an administrator."));
                    if (s.WifiEnterprise == Tri.Yes)
                    {
                        // A campus or office network has no router the person can restart; its support team can check the access points.
                        rungs.Add(new("wifi_enterprise_support", "ui.human_step", 0,
                            "If the drops continue in one particular building or room, please tell the network support team of your school or workplace " +
                            "where and when they happen; they can check the access points there."));
                        break;
                    }
                    rungs.Add(new("wifi_distance_band", "ui.human_step", 0,
                        "Please move closer to your router, or move the router to a more open place. If your router offers both a 2.4 GHz and a 5 GHz network, " +
                        "try the other one: 5 GHz is faster at short range, 2.4 GHz reaches further through walls."));
                    if (!OldWifiDriver(s))
                        rungs.Add(new("wifi_driver_update", "ui.open_settings_page", 0,
                            "A newer Wi-Fi driver may fix the disconnects. AutoFix Scout will open \"Windows Update\". " +
                            "Please choose \"Advanced options\", then \"Optional updates\", and install any driver update listed for your Wi-Fi adapter.", "windowsupdate"));
                    rungs.Add(RestartRouter);
                    break;
                case FindingCode.NetMetered:
                    rungs.Add(new("metered_off", "ui.open_settings_page", 0,
                        "Windows Update and OneDrive may pause on a \"Metered connection\". AutoFix Scout will open the \"Wi-Fi\" settings. " +
                        "If you want them to continue, please select your network and set \"Metered connection\" to \"Off\".", "wifi"));
                    break;
            }
        return rungs.DistinctBy(r => r.Id).ToList();
    }

    private static readonly Rung RestartRouter = new("restart_router", "ui.human_step", 0,
        "Please unplug your router for 30 seconds, then plug it back in. Then wait about two minutes until its lights are steady.");

    private static void AddIfNotNull(List<Rung> rungs, Rung? rung) { if (rung != null) rungs.Add(rung); }

    public static bool ProbablyHardware(NetworkState s) =>
        s.ProbeErrors.Count == 0 && (s.Adapters.Count == 0 || s.Adapters.All(a => a.ProblemCode is > 0 and not 22));

    /// <summary>The honest "not fixed" sentence (spec 1: say when it's the router or provider, not this computer).</summary>
    public static string? NotFixedHint(IReadOnlyList<Finding> after) => after[0].Code switch
    {
        FindingCode.LooksNormal => "Your internet connection is now working. " +
            "If a particular website or app still does not load, the problem most likely lies with that website or app.",
        FindingCode.GatewayUnreachable => "Your computer cannot reach your router. The problem is most likely the router itself. " +
            "Your internet provider can help you with this.",
        FindingCode.NoInternet => "Your router is working, but the internet connection is not reaching it. Your internet provider can check the line.",
        _ => null,
    };
}
