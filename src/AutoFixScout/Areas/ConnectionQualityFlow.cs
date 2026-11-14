using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>
/// "The internet is slow" / "games lag online": measures delay, jitter and packet loss to the router and to the internet
/// for about 15 seconds, and says whether the problem is the Wi-Fi or home network, or the internet connection. Read-only.
/// Real test: the person's answer.
/// </summary>
public static class ConnectionQualityFlow
{
    public const string Topic = "netquality";
    public const string What = "connection quality";
    public const string TestQuestion = "Is the internet connection now fast and steady enough for what you were doing?";

    public static async Task<Diagnosis> DiagnoseAsync(CancellationToken ct = default)
    {
        using var _ = Activity.Scope(Topic);
        var s = await new RealConnectionQualityProbe().ReadAsync(ct: ct);
        var f = Analysis.Judge(() => ConnectionQualityTriage.Evaluate(s));
        return new Diagnosis(s, f, Analysis.Plan(ConnectionQualityTriage.Ladder(s, f)), false, ConnectionQualityTriage.NotFixedHint(f));
    }
}
