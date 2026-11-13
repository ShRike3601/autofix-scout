using AutoFixScout.Probes;
using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>
/// "Bluetooth is not working". Real test: Bluetooth ready (<see cref="BluetoothTriage.Ready"/>), then the person
/// confirms their device works, because only they can tell whether the headphones play or the mouse moves.
/// </summary>
public static class BluetoothFlow
{
    public const string Topic = "bluetooth";
    public const string What = "Bluetooth";
    public const string TestQuestion = "Does your Bluetooth device work now?";

    private static readonly IBluetoothProbe Probe = new RealBluetoothProbe();

    public static Task<Diagnosis> DiagnoseAsync() => Task.Run(() =>
    {
        using var _ = Activity.Scope(Topic);
        var s = Probe.Read();
        var f = Analysis.Judge(() => BluetoothTriage.Evaluate(s));
        return new Diagnosis(s, f, Analysis.Plan(BluetoothTriage.Ladder(s, f)), BluetoothTriage.ProbablyHardware(s), BluetoothTriage.NotFixedHint(f));
    });

    public static Task<bool> AutoTestAsync() => Task.Run(() =>
    {
        try { return BluetoothTriage.Ready(Probe.Read()); }
        catch (Exception) { return false; } // fail closed
    });
}
