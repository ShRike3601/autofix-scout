namespace AutoFixScout.Triage;

/// <summary>
/// "I can't get online" (spec 8.3). Pure local logic: works with no LLM and no internet.
/// shortcut: ladder is hardcoded like AudioLadder; move both to YAML playbooks together.
/// </summary>
public static class NetworkTriage
{
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
        return (s.GatewayReachable, s.DnsResolving, s.InternetReachable) switch
        {
            (_, _, Tri.Yes) => new[] { F(FindingCode.LooksNormal, "Your internet connection is working.", "NCSI test passed") },
            (Tri.No, Tri.No, _) => new[] { F(FindingCode.GatewayUnreachable, "Your computer is connected, but it cannot reach your router.", "Gateway ping failed and DNS failed") },
            (Tri.Yes, Tri.No, _) => new[] { F(FindingCode.DnsFailing, "Your computer can reach your router, but it cannot look up website addresses.", "Gateway ok, DNS failed") },
            (_, Tri.Yes, Tri.No) => new[] { F(FindingCode.NoInternet, "Your computer can reach your router, but the internet is not responding.", "DNS ok, NCSI test failed") },
            _ => new[] { F(FindingCode.ProbeIncomplete, Incomplete, $"gateway={s.GatewayReachable} dns={s.DnsResolving} internet={s.InternetReachable}") },
        };
    }

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
        return rungs;
    }

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
