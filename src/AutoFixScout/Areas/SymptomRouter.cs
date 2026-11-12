namespace AutoFixScout.Areas;

/// <summary>
/// "Describe the problem in your own words": maps free text to the check that fits best. Pure keyword scoring, no
/// network and no AI, so it works offline and the same way every time. Text that matches nothing goes to the full
/// check-up, which looks at the whole computer.
/// </summary>
public static class SymptomRouter
{
    public sealed record Route(string Area, string Title, string Explanation);

    public static readonly IReadOnlyDictionary<string, Route> Routes = new Dictionary<string, Route>
    {
        ["sound"] = new("sound", "Sound is not working", "This sounds like a problem with sound."),
        ["bluetooth"] = new("bluetooth", "Bluetooth is not working", "This sounds like a problem with Bluetooth."),
        ["microphone"] = new("microphone", "Microphone not working", "This sounds like a problem with the microphone."),
        ["camera"] = new("camera", "Camera is not working", "This sounds like a problem with the camera."),
        ["network"] = new("network", "Cannot connect to the internet", "This sounds like a problem with the internet connection."),
        ["program"] = new("program", "A program keeps closing", "This sounds like a program that closes or stops responding."),
        ["storage"] = new("storage", "Slow or full computer", "This sounds like a computer that is slow or running out of space."),
        ["update"] = new("update", "Windows Update problems", "This sounds like a problem with Windows Update."),
        ["printer"] = new("printer", "Printer not working", "This sounds like a problem with printing."),
        ["display"] = new("display", "Screen or display problem", "This sounds like a problem with the screen or display."),
        ["health"] = new("health", "Full computer check-up", "AutoFix Scout will check the whole computer for common software problems."),
    };

    // Phrases score 3, single words 1. Lower-case; matched as substrings of the lower-cased text.
    // On a tie the earlier entry wins, so the specific Windows problems (health) come before "a program".
    private static readonly (string Area, string[] Phrases, string[] Words)[] Keywords =
    {
        ("bluetooth", new[] { "bluetooth", "airpods", "earbuds not connecting", "won't pair", "wont pair", "will not pair", "cannot pair", "can't pair", "not pairing" },
                      new[] { "bluetooth", "airpods", "pair", "pairing", "wireless mouse", "wireless keyboard", "earbuds" }),
        ("microphone", new[] { "cannot hear me", "can't hear me", "cant hear me", "nobody can hear", "no one can hear", "they can't hear", "mic not", "microphone not" },
                       new[] { "microphone", "mic ", "mic.", "recording", "voice", "mute myself" }),
        ("sound", new[] { "no sound", "no audio", "can't hear", "cant hear", "cannot hear", "sound not", "audio not", "speakers not", "headphones not" },
                  new[] { "sound", "audio", "speaker", "speakers", "volume", "headphone", "headphones", "earphone", "earphones", "music", "silent" }),
        ("camera", new[] { "camera not", "webcam not", "black screen in zoom", "camera is black", "no video" },
                   new[] { "camera", "webcam", "video call", "cam " }),
        ("network", new[] { "no internet", "not connecting", "can't connect", "cannot connect", "cant connect", "no wifi", "wi-fi not", "wifi not", "pages not loading", "websites not", "dns", "limited connectivity" },
                    new[] { "internet", "wifi", "wi-fi", "network", "online", "connection", "router", "ethernet", "website", "websites", "browser", "offline" }),
        ("health", new[] { "blue screen", "bsod", "start menu", "taskbar", "search not", "clock is wrong", "wrong time", "certificate", "virus", "pop-ups", "popups", "redirect" },
                   new[] { "restart", "restarts", "reboot", "error", "errors", "broken", "weird", "strange", "malware", "hijack" }),
        ("program", new[] { "keeps closing", "keeps crashing", "stopped working", "not responding", "won't open", "wont open", "will not open", "doesn't open", "does not open", "closes by itself", "app crash", "freezes" },
                    new[] { "crash", "crashes", "crashing", "closes", "freeze", "freezing", "hangs", "error message", "missing dll", ".dll", "vcruntime", "msvcp" }),
        ("storage", new[] { "running slow", "very slow", "is slow", "disk full", "out of space", "low disk", "takes long to start", "slow startup" },
                    new[] { "slow", "sluggish", "lag", "lagging", "space", "storage", "full", "disk", "memory", "startup" }),
        ("update", new[] { "windows update", "update failed", "updates fail", "update error", "update stuck", "cannot update", "can't update" },
                   new[] { "update", "updates", "updating", "0x8024", "0x8007" }),
        ("printer", new[] { "won't print", "wont print", "will not print", "cannot print", "can't print", "printer offline", "print queue" },
                    new[] { "printer", "print", "printing", "scanner" }),
        ("display", new[] { "second monitor", "external monitor", "screen is dark", "screen too dark", "screen flickers", "no display", "projector not" },
                    new[] { "monitor", "display", "screen", "brightness", "hdmi", "projector", "resolution", "flicker", "flickering" }),
    };

    public static IReadOnlyList<(string Area, int Score)> Scores(string text)
    {
        var t = " " + (text ?? "").ToLowerInvariant().Replace('’', '\'') + " ";
        return Keywords
            .Select(k => (k.Area, Score: k.Phrases.Count(p => t.Contains(p)) * 3 + k.Words.Count(w => t.Contains(w))))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ToList();
    }

    /// <summary>The best check for the text; the full check-up when nothing matches.</summary>
    public static Route Best(string text)
    {
        var s = Scores(text);
        return Routes[s.Count > 0 ? s[0].Area : "health"];
    }
}
