using AutoFixScout.Probes;

namespace AutoFixScout.Watching;

/// <summary>One finished outage, as kept in wifi-watch.json. Counts only: never a network or profile name.</summary>
public sealed record WifiWatchRecord(DateTime TimeUtc, string Reason, bool ReconnectedByWatcher, int SecondsOffline);

/// <summary>What the watcher should do right now.</summary>
public abstract record WatchDecision
{
    public sealed record Nothing : WatchDecision;
    /// <summary>Run "netsh wlan connect" for this saved profile. The name lives in memory only.</summary>
    public sealed record Reconnect(string Profile) : WatchDecision;
}

/// <summary>
/// The decision logic of the Wi-Fi watcher, with no Windows calls so it can be tested with plain timestamps.
/// A disconnect that a person asked for is ignored. Any other disconnect starts a clock; if Windows has not reconnected
/// within <see cref="Grace"/>, the watcher reconnects the profile that was connected, but only if that profile is set to
/// connect automatically. At most one attempt per <see cref="MinGap"/>, and at most <see cref="MaxAttempts"/> attempts
/// until the next successful connection. All times are UTC.
/// </summary>
public sealed class WifiWatchLogic
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(30);
    /// <summary>A pause between two ticks this long means the computer slept; the outage is then not counted.</summary>
    public static readonly TimeSpan SleepGap = TimeSpan.FromMinutes(2);
    public const int MaxAttempts = 5;
    public static readonly TimeSpan MaxOutage = TimeSpan.FromMinutes(30);

    private string? _lastProfile;        // the profile that was connected (memory only)
    private bool _pending;               // a disconnect that is not by the person is waiting for a reconnect
    private DateTime _dropAt;
    private WifiDrops.Category _category;
    private string? _dropProfile;
    private int _attempts;               // attempts since the last successful connection
    private DateTime? _lastAttempt;
    private bool _watcherTried;
    private DateTime? _lastTick;

    public int Attempts => _attempts;
    public bool Pending => _pending;

    /// <summary>Raised when an outage is over (or the watcher gave up). Counts only.</summary>
    public event Action<WifiWatchRecord>? Completed;

    public void OnConnected(DateTime now, string? profile)
    {
        if (profile != null) _lastProfile = profile;
        if (_pending) Finish(now, reconnected: true);
        _attempts = 0;
        _lastAttempt = null;
    }

    public void OnDisconnected(DateTime now, int reasonCode, string? reasonText, string? profile)
    {
        var category = WifiDrops.Categorize(reasonCode, reasonText);
        if (category == WifiDrops.Category.Person) { _pending = false; return; }
        if (profile != null) _lastProfile = profile;
        if (_pending) return; // already counting this outage
        _pending = true;
        _dropAt = now;
        _category = category;
        _dropProfile = profile ?? _lastProfile;
        _watcherTried = false;
    }

    /// <summary>Call every few seconds. connectedNow = what Windows reports right now; isAutoConnect is asked only when a reconnect is due.</summary>
    public WatchDecision Tick(DateTime now, bool connectedNow, Func<string, bool> isAutoConnect)
    {
        var slept = _lastTick is { } t && now - t > SleepGap;
        _lastTick = now;
        if (!_pending) return new WatchDecision.Nothing();
        if (slept) { _pending = false; return new WatchDecision.Nothing(); }
        if (connectedNow) { OnConnected(now, null); return new WatchDecision.Nothing(); }
        if (now - _dropAt < Grace) return new WatchDecision.Nothing();
        if (_attempts >= MaxAttempts)
        {
            // Gave up until the next successful connection; the outage is recorded once.
            Finish(now, reconnected: false);
            return new WatchDecision.Nothing();
        }
        if (_lastAttempt is { } last && now - last < MinGap) return new WatchDecision.Nothing();
        var profile = _dropProfile ?? _lastProfile;
        if (string.IsNullOrWhiteSpace(profile) || !isAutoConnect(profile)) return new WatchDecision.Nothing();
        _attempts++;
        _lastAttempt = now;
        _watcherTried = true;
        return new WatchDecision.Reconnect(profile);
    }

    private void Finish(DateTime now, bool reconnected)
    {
        _pending = false;
        // A very long gap is the computer asleep or off, not a Wi-Fi drop.
        if (now - _dropAt > MaxOutage) return;
        var seconds = (int)Math.Max(0, Math.Round((now - _dropAt).TotalSeconds));
        Completed?.Invoke(new WifiWatchRecord(now, _category.ToString(), reconnected && _watcherTried, seconds));
    }
}
