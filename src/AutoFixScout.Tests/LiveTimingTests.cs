using AutoFixScout.Areas;
using Xunit;

namespace AutoFixScout.Tests;

/// <summary>Times every area's read-only DiagnoseAsync on the real machine. Runs only when AFS_LIVE is set (a file path the timings are written to).</summary>
[Collection("Live machine")]
public class LiveTimingTests
{
    [Fact]
    public async Task EveryAreaDiagnose_Timed()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var areas = new (string Name, Func<Task> Run)[]
        {
            ("health", () => HealthFlow.DiagnoseAsync()), ("bluetooth", () => BluetoothFlow.DiagnoseAsync()),
            ("display", () => DisplayFlow.DiagnoseAsync()), ("input", () => InputFlow.DiagnoseAsync()),
            ("microphone", () => MicrophoneFlow.DiagnoseAsync()), ("power", () => PowerFlow.DiagnoseAsync()),
            ("printer", () => PrinterFlow.DiagnoseAsync()), ("security", () => SecurityFlow.DiagnoseAsync()),
            ("shell", () => ShellFlow.DiagnoseAsync()), ("storage", () => StorageFlow.DiagnoseAsync()),
            ("update", () => UpdateFlow.DiagnoseAsync()), ("apps", () => AppsFlow.DiagnoseAsync()),
        };
        var lines = new List<string>();
        foreach (var (name, run) in areas)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string result;
            try { await run(); result = "ok"; } catch (Exception ex) { result = "failed: " + ex.Message; }
            lines.Add($"{name,-10} {sw.ElapsedMilliseconds,7} ms  {result}");
        }
        File.WriteAllLines(output, lines);
    }
}
