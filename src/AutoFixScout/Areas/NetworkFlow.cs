using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>"Cannot get online": the internet probe, then the proxy, hosts file, clock and network add-ons as software causes. Read-only.</summary>
public static class NetworkFlow
{
    public const string Topic = "network";
    public const string What = "internet connection";

    public static async Task<Diagnosis> DiagnoseAsync()
    {
        using var _ = Activity.Scope(Topic);
        var s = await Task.Run(new RealNetworkProbe().ReadAsync);
        var f = Analysis.Judge(() => NetworkTriage.Evaluate(s));
        var network = new Diagnosis(s, f, Analysis.Plan(NetworkTriage.Ladder(s, f)), NetworkTriage.ProbablyHardware(s), NetworkTriage.NotFixedHint(f));
        // Proxy, hosts file, clock and network add-ons: the software causes when the connection itself looks fine.
        try { return HealthTriage.CombineNetwork(network, await Task.Run(new RealHealthProbe().Read)); }
        catch (Exception) { return network; }
    }
}
