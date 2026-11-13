using AutoFixScout.Areas;
using AutoFixScout.Stress;
using AutoFixScout.Updates;
using Xunit;

namespace AutoFixScout.Tests;

/// <summary>
/// Release checks against the real world. Run only with AFS_LIVE set (path of the output file).
/// The update check talks to the real GitHub release and downloads its setup into a temp folder (never started).
/// The load test runs at full length on this machine (it must be plugged in).
/// </summary>
[Collection("Live machine")]
public class LiveReleaseChecks
{
    private static string? Output => Environment.GetEnvironmentVariable("AFS_LIVE");

    [Fact]
    public async Task UpdateCheck_FindsTheRealLatestRelease_AndVerifiesTheDownload()
    {
        if (string.IsNullOrEmpty(Output)) return;
        var dir = Path.Combine(Path.GetTempPath(), "afs-update-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            var updates = new UpdateService(dataDir: dir, currentVersion: () => AppVersion.Parse("0.1.0")!.Value);
            var info = await updates.CheckAsync();
            Assert.NotNull(info);
            var path = await updates.DownloadAsync(info!);
            Assert.NotNull(path);
            Assert.Equal(info!.SizeBytes, new FileInfo(path!).Length);
            File.AppendAllLines(Output!, new[]
            {
                $"update: offered {info.Version}, size {info.SizeBytes}, sha256 asset {(info.Sha256Url.Length > 0 ? "yes" : "no")}, downloaded and verified",
                $"update: notes start \"{new string(info.Notes.Take(60).ToArray()).ReplaceLineEndings(" ")}\"",
            });
            // A build of the same version is offered nothing.
            var same = new UpdateService(dataDir: dir + "-2", currentVersion: () => AppVersion.Parse(info.Version)!.Value);
            Assert.Null(await same.CheckAsync());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
            try { Directory.Delete(dir + "-2", true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ChangeHistory_ReadsTheRealSessionLog()
    {
        if (string.IsNullOrEmpty(Output)) return;
        var list = new ChangeHistory().List();
        File.AppendAllLines(Output!, new[] { $"history: {list.Count} changes, {list.Count(e => e.CanUndo)} can be undone" });
    }

    [Fact]
    public async Task LoadTest_FullLength()
    {
        if (string.IsNullOrEmpty(Output)) return;
        var samples = new List<StressSample>();
        var started = DateTime.UtcNow;
        var d = await StressFlow.DiagnoseAsync(new Progress<StressSample>(s => { lock (samples) samples.Add(s); }));
        var r = (StressResult)d.State;
        lock (samples)
            File.AppendAllLines(Output!, new[]
            {
                $"load test: {(DateTime.UtcNow - started).TotalSeconds:0} s, outcome {r.Outcome}, {samples.Count} progress updates",
                $"load test: max GPU {samples.Max(s => s.GpuTemperatureC ?? 0)} C, min CPU limit {samples.Where(s => s.CpuLimitPercent != null).Select(s => s.CpuLimitPercent!.Value).DefaultIfEmpty(-1).Min():0} %",
            }.Concat(d.Findings.Select(f => $"load test finding {f.Code}: {f.PlainMessage}")).Concat(d.Ladder.Select(x => $"load test rung {x.Id}")));
        Assert.True((DateTime.UtcNow - started).TotalSeconds < 150);
    }
}
