namespace AutoFixScout.Areas;

/// <summary>One suggested check for a description: the route, its keyword score and a 0-1 confidence.</summary>
/// <param name="Covers">One formal line saying what the check looks at, for a "closest checks" screen.</param>
public sealed record SymptomCandidate(SymptomRouter.Route Route, int Score, double Confidence, string Covers);

/// <summary>The closest checks, best first (at most three, one per check), and whether none of them fits well.</summary>
/// <param name="NoGoodMatch">True when nothing matched, or only a single general word did; the full check-up is then the safe choice.</param>
public sealed record SymptomRanking(IReadOnlyList<SymptomCandidate> Candidates, bool NoGoodMatch);

public static partial class SymptomRouter
{
    /// <summary>A phrase is worth 3 and a word 1 (see <see cref="Scores"/>); below this the best match is only one general word.</summary>
    public const int GoodMatchScore = 2;

    /// <summary>What each check looks at, in one formal line (shown under the check's title).</summary>
    public static readonly IReadOnlyDictionary<string, string> Covers = new Dictionary<string, string>
    {
        ["sound"] = "Speakers and headphones, volume, the selected output, sound services and drivers.",
        ["bluetooth"] = "The Bluetooth adapter and switch, paired devices and the Bluetooth service.",
        ["microphone"] = "The microphone, its privacy settings, the selected input and its level.",
        ["camera"] = "The camera, its privacy settings, apps using it and its driver.",
        ["network"] = "Wi-Fi and network cable, the connection to the router, DNS, proxy and network settings.",
        ["netquality"] = "Delay, jitter and lost packets to the router and to the internet, to tell whether a slow or laggy connection is caused by the Wi-Fi or by the internet provider.",
        ["program"] = "Programs that closed unexpectedly and the likely cause recorded by Windows.",
        ["storage"] = "Free space, temporary files, startup apps and programs that slow the computer down.",
        ["update"] = "Windows Update services, failed updates, a waiting restart and free space.",
        ["printer"] = "Printers, the print queue and the print service.",
        ["scanner"] = "Scanners, the scanning service and the printer settings they share.",
        ["display"] = "Screens and projectors, brightness and the display adapter.",
        ["graphics"] = "The graphics card, its driver, temperature, link to the motherboard and overclocking tools such as MSI Afterburner.",
        ["shell"] = "The Start menu, the taskbar, search and the parts of Windows that draw them.",
        ["store"] = "The Microsoft Store, its services and app downloads.",
        ["office"] = "Word, Excel, Outlook and the Office repair tool.",
        ["onedrive"] = "OneDrive and file syncing.",
        ["security"] = "Antivirus, firewall, browser settings taken over by programs, and found threats.",
        ["power"] = "The battery, sleep, power settings and why the computer wakes up.",
        ["input"] = "The keyboard, mouse and touchpad, and keyboard accessibility settings.",
        ["install"] = "Programs that fail to install, the Windows Installer service and a waiting restart.",
        ["notifications"] = "Notifications, Do not disturb and the apps allowed to show notifications.",
        ["performance"] = "Processor speed limits from heat or power, the power plan, memory speed, the BIOS and signs of unstable hardware.",
        ["health"] = "The whole computer: crashes, drives, services, updates, settings and recent changes.",
    };

    /// <summary>
    /// Up to <paramref name="max"/> closest checks for a description, best first, one per check. Confidence is each
    /// check's share of the matching score among the candidates shown. When nothing fits well, the full check-up is
    /// included as the last candidate (and as the only one when nothing matched) and <see cref="SymptomRanking.NoGoodMatch"/> is true.
    /// <see cref="Best"/> is unchanged and still returns the single top route.
    /// </summary>
    public static SymptomRanking Rank(string text, int max = 3)
    {
        max = Math.Max(1, max);
        var scores = Scores(text).Where(s => Routes.ContainsKey(s.Area)).DistinctBy(s => s.Area).Take(max).ToList();
        var noGoodMatch = scores.Count == 0 || scores[0].Score < GoodMatchScore;
        var total = (double)scores.Sum(s => s.Score);
        var list = scores.Select(s => Candidate(s.Area, s.Score, total == 0 ? 0 : s.Score / total)).ToList();
        if (noGoodMatch && list.All(c => c.Route.Area != "health"))
        {
            if (list.Count >= max) list.RemoveAt(list.Count - 1);
            list.Add(Candidate("health", 0, list.Count == 0 ? 1 : 0));
        }
        return new SymptomRanking(list, noGoodMatch);
    }

    private static SymptomCandidate Candidate(string area, int score, double confidence) =>
        new(Routes[area], score, Math.Round(confidence, 2), Covers.TryGetValue(area, out var c) ? c : Routes[area].Explanation);
}
