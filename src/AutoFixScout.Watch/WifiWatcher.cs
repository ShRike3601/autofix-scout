using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;

namespace AutoFixScout.Watching;

/// <summary>
/// Glue between Windows and <see cref="WifiWatchLogic"/>: listens to the WLAN-AutoConfig log (8003 disconnect, 8001 connect),
/// checks the connection every few seconds while an outage is open, and runs "netsh wlan connect" when the logic says so.
/// Only the saved profile that was connected before is ever reconnected. Nothing is sent anywhere.
/// </summary>
internal sealed class WifiWatcher : IDisposable
{
    private const string Log = "Microsoft-Windows-WLAN-AutoConfig/Operational";
    private readonly WifiWatchLogic _logic = new();
    private readonly WifiWatchStore _store;
    private readonly object _gate = new();
    private EventLogWatcher? _events;
    private Timer? _timer;

    public WifiWatcher(WifiWatchStore store)
    {
        _store = store;
        _logic.Completed += r => { try { _store.Add(r, DateTime.UtcNow); } catch (Exception) { /* a full disk must not stop the watcher */ } };
    }

    public void Start()
    {
        // Learn the current connection first, so a drop right after start-up knows what to reconnect.
        var (connected, profile) = ReadInterfaces();
        if (connected) lock (_gate) _logic.OnConnected(DateTime.UtcNow, profile);

        _events = new EventLogWatcher(new EventLogQuery(Log, PathType.LogName, "*[System[(EventID=8003 or EventID=8001)]]"));
        _events.EventRecordWritten += OnEvent;
        _events.Enabled = true;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private void OnEvent(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventRecord is null) return;
        try
        {
            using var rec = e.EventRecord;
            var xml = rec.ToXml();
            var (id, code, reason) = AutoFixScout.Probes.WifiDrops.ParseEvent(xml);
            var profile = WifiNetsh.ParseProfileName(xml);
            lock (_gate)
            {
                if (id == 8001) _logic.OnConnected(DateTime.UtcNow, profile);
                else if (id == 8003) _logic.OnDisconnected(DateTime.UtcNow, code, reason, profile);
            }
        }
        catch (Exception) { /* a malformed event is skipped */ }
    }

    private void Tick()
    {
        try
        {
            WatchDecision decision;
            lock (_gate)
            {
                if (!_logic.Pending) { _logic.Tick(DateTime.UtcNow, true, _ => false); return; }
            }
            var connected = ReadInterfaces().Connected;
            lock (_gate) decision = _logic.Tick(DateTime.UtcNow, connected, IsAutoConnect);
            if (decision is WatchDecision.Reconnect r && WifiNetsh.ConnectArguments(r.Profile) is { } args)
                RunNetsh(args, out _);
        }
        catch (Exception) { /* try again at the next tick */ }
    }

    private static bool IsAutoConnect(string profile) =>
        WifiNetsh.ShowProfileArguments(profile) is { } args && RunNetsh(args, out var text) && WifiNetsh.IsAutoConnect(text);

    private static (bool Connected, string? Profile) ReadInterfaces() =>
        RunNetsh("wlan show interfaces", out var text) ? WifiNetsh.ParseInterfaces(text) : (false, null);

    private static bool RunNetsh(string arguments, out string output)
    {
        output = "";
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi);
        if (p is null) return false;
        output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(15_000)) { try { p.Kill(); } catch (Exception) { } return false; }
        return p.ExitCode == 0;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        if (_events != null) { _events.Enabled = false; _events.Dispose(); }
    }
}
