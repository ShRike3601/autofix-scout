namespace AutoFixScout.Triage;

/// <summary>
/// "I can't get online" (spec 8.3). Pure local logic: works with no LLM and no internet.
/// shortcut: ladder is hardcoded like AudioLadder; move both to YAML playbooks together.
/// </summary>
public static class NetworkTriage
{
    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    /// <summary>Findings in severity order; always at least one.</summary>
    public static IReadOnlyList<Finding> Evaluate(NetworkState s)
    {
        if (s.ProbeErrors.Count > 0)
            return new[] { F(FindingCode.ProbeIncomplete, "We couldn't complete the network check.", string.Join("; ", s.ProbeErrors)) };
        if (s.Adapters.Count == 0)
            return new[] { F(FindingCode.NoNetworkAdapter, "I can't find a Wi-Fi or network adapter in this computer.", "No physical adapters") };

        if (!s.Adapters.Any(a => a.Connected))
        {
            if (s.AirplaneMode == Tri.Yes)
                return new[] { F(FindingCode.AirplaneModeOn, "Airplane mode is turned on, so Wi-Fi is off.", "SystemRadioState=1") };
            var disabled = s.Adapters.OrderByDescending(a => a.IsWireless).FirstOrDefault(a => a.Disabled);
            if (disabled != null)
                return new[] { F(FindingCode.NetAdapterDisabled, "Your Wi-Fi or network adapter is switched off.", $"'{disabled.Name}' disabled (code 22)") };
            var broken = s.Adapters.FirstOrDefault(a => a.ProblemCode is > 0);
            if (broken != null)
                return new[] { F(FindingCode.NetAdapterError, "Your Wi-Fi or network adapter reports a problem.", $"'{broken.Name}' problem code {broken.ProblemCode}") };
            return new[] { F(FindingCode.NotConnected, "Your computer isn't connected to Wi-Fi or a network cable.", "No adapter connected") };
        }

        // Ping alone isn't trusted (some routers ignore it), so a silent router only counts when DNS fails too.
        return (s.GatewayReachable, s.DnsResolving, s.InternetReachable) switch
        {
            (_, _, Tri.Yes) => new[] { F(FindingCode.LooksNormal, "Your internet connection is working.", "NCSI test passed") },
            (Tri.No, Tri.No, _) => new[] { F(FindingCode.GatewayUnreachable, "Your computer is connected, but it can't reach your router.", "Gateway ping failed and DNS failed") },
            (Tri.Yes, Tri.No, _) => new[] { F(FindingCode.DnsFailing, "Your computer reaches your router, but it can't look up websites.", "Gateway ok, DNS failed") },
            (_, Tri.Yes, Tri.No) => new[] { F(FindingCode.NoInternet, "Your computer reaches your router, but the internet isn't answering.", "DNS ok, NCSI test failed") },
            _ => new[] { F(FindingCode.ProbeIncomplete, "We couldn't complete the network check.", $"gateway={s.GatewayReachable} dns={s.DnsResolving} internet={s.InternetReachable}") },
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
                    "Airplane mode is on. I'll open the Airplane mode settings. Please switch \"Airplane mode\" to Off.", "airplane"));
                break;
            case FindingCode.NetAdapterDisabled:
                var off = s.Adapters.OrderByDescending(a => a.IsWireless).First(a => a.Disabled);
                rungs.Add(new("enable_adapter", "device.enable", 1,
                    $"Your network adapter \"{off.Name}\" is switched off. I'd like to switch it on. Windows will ask for permission.", off.InstanceId));
                break;
            case FindingCode.NetAdapterError:
                var broken = s.Adapters.First(a => a.ProblemCode is > 0);
                rungs.Add(new("toggle_adapter", "device.disable_enable", 1,
                    $"Your network adapter \"{broken.Name}\" reports a problem. I'd like to switch it off and on again. Windows will ask for permission.", broken.InstanceId));
                AddIfNotNull(rungs, DriverRules.Rollback("network", broken.Name, broken.InstanceId, broken.Driver));
                break;
            case FindingCode.NotConnected:
                rungs.Add(s.Adapters.Any(a => a.IsWireless)
                    ? new("pick_wifi", "ui.open_settings_page", 0,
                        "You're not connected to Wi-Fi. I'll open the Wi-Fi settings. Please choose your network and connect.", "wifi")
                    : new("check_cable", "ui.human_step", 0,
                        "Please check that the network cable is firmly plugged in at both ends: the computer and the router."));
                break;
            case FindingCode.GatewayUnreachable or FindingCode.DnsFailing or FindingCode.NoInternet:
                if (code == FindingCode.DnsFailing)
                    rungs.Add(new("flush_dns", "network.flush_dns", 1,
                        "I'd like to clear your computer's list of website addresses. This is safe."));
                // A hand-typed DNS server survives a flush and an adapter restart (found in test step 3d). Loopback ones
                // belong to a VPN such as Cloudflare WARP, which manages them itself, so those are left alone.
                if (code == FindingCode.DnsFailing && s.StaticDnsAdapterId != null
                    && s.StaticDnsServers.Count > 0 && !s.StaticDnsServers.All(d => d.StartsWith("127.") || d == "::1"))
                    rungs.Add(new("reset_dns", "network.reset_dns", 1,
                        $"Your computer is set to use a fixed website-lookup (DNS) server ({string.Join(", ", s.StaticDnsServers)}) " +
                        "that isn't answering. I'd like to switch it back to automatic. Windows will ask for permission.", s.StaticDnsAdapterId));
                if (connected != null)
                    rungs.Add(new("toggle_adapter", "device.disable_enable", 1,
                        $"I'd like to switch your network adapter \"{connected.Name}\" off and on again. Windows will ask for permission.", connected.InstanceId));
                if (connected != null)
                    AddIfNotNull(rungs, DriverRules.Rollback("network", connected.Name, connected.InstanceId, connected.Driver));
                rungs.Add(new("restart_router", "ui.human_step", 0,
                    "Please unplug your router for 30 seconds, then plug it back in. Wait about two minutes until its lights settle."));
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
        FindingCode.LooksNormal => "Your internet connection is working now. If one website or app still won't load, the problem is probably on their side.",
        FindingCode.GatewayUnreachable => "Your computer can't reach your router. The problem is probably the router itself. Your internet provider can help.",
        FindingCode.NoInternet => "Your router is working, but the internet isn't reaching it. Your internet provider can check the line.",
        _ => null,
    };
}
