using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using Xunit;

namespace AutoFixScout.Tests;

/// <summary>Scratch registry root: HKCU\Software\AutoFixScoutTests\guid, deleted afterwards. The real keys are never touched.</summary>
public sealed class ScratchRoot : IDisposable
{
    private readonly string _path = @"Software\AutoFixScoutTests\" + Guid.NewGuid().ToString("N");
    public RegistryKey Key { get; }
    public ScratchRoot() => Key = Registry.CurrentUser.CreateSubKey(_path, writable: true);
    public void Dispose()
    {
        Key.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
    }

    public object? Get(string path, string name) { using var k = Key.OpenSubKey(path); return k?.GetValue(name); }
    public void Set(string path, string name, object value, RegistryValueKind kind = RegistryValueKind.DWord) { using var k = Key.CreateSubKey(path, true); k.SetValue(name, value, kind); }
}

public class GameFixTests : IDisposable
{
    private readonly ScratchRoot _s = new();
    public void Dispose() => _s.Dispose();

    private const string Hades = @"C:\Program Files (x86)\Steam\steamapps\common\Hades\Hades.exe";
    private const string Other = @"D:\Epic Games\Hades\Hades.exe";
    private const string Odd = @"C:\Users\Someone\Downloads\tool.exe";

    // ---- Game Mode ----

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void GameMode_RoundTrip_RestoresThePreviousValue(object? previous)
    {
        if (previous != null) _s.Set(GameFixes.GameBarKey, GameFixes.GameModeValue, previous);
        var (ok, detail) = GameFixes.GameModeOn(_s.Key);
        Assert.True(ok);
        Assert.Equal(1, _s.Get(GameFixes.GameBarKey, GameFixes.GameModeValue));
        var payload = GameFixes.PayloadFromDetail(detail);
        Assert.NotNull(payload);

        Assert.True(GameFixes.Undo(_s.Key, payload!).Ok);
        Assert.Equal(previous, _s.Get(GameFixes.GameBarKey, GameFixes.GameModeValue));
    }

    [Fact]
    public void GameMode_Idempotent_AndUnexpectedTypeRefused()
    {
        _s.Set(GameFixes.GameBarKey, GameFixes.GameModeValue, 1);
        var (ok, detail) = GameFixes.GameModeOn(_s.Key);
        Assert.True(ok);
        Assert.Contains("nothing to do", detail);
        Assert.Null(GameFixes.PayloadFromDetail(detail));

        _s.Set(GameFixes.GameBarKey, GameFixes.GameModeValue, new byte[] { 0 }, RegistryValueKind.Binary);
        Assert.False(GameFixes.GameModeOn(_s.Key).Ok);
    }

    // ---- background recording ----

    [Fact]
    public void Recording_RoundTrip_MixedPreviousValues()
    {
        _s.Set(GameFixes.StoreKey, GameFixes.StoreValue, 1);
        _s.Set(GameFixes.DvrKey, GameFixes.CaptureValue, 0);          // already off
        // HistoricalCaptureEnabled is missing
        var (ok, detail) = GameFixes.BackgroundRecordingOff(_s.Key);
        Assert.True(ok);
        Assert.Equal(0, _s.Get(GameFixes.StoreKey, GameFixes.StoreValue));
        Assert.Equal(0, _s.Get(GameFixes.DvrKey, GameFixes.CaptureValue));
        Assert.Equal(0, _s.Get(GameFixes.DvrKey, GameFixes.HistoricalValue));

        Assert.True(GameFixes.Undo(_s.Key, GameFixes.PayloadFromDetail(detail)!).Ok);
        Assert.Equal(1, _s.Get(GameFixes.StoreKey, GameFixes.StoreValue));
        Assert.Equal(0, _s.Get(GameFixes.DvrKey, GameFixes.CaptureValue));
        Assert.Null(_s.Get(GameFixes.DvrKey, GameFixes.HistoricalValue));
    }

    [Fact]
    public void Recording_AllMissing_RoundTripDeletesAgain_AndIdempotent()
    {
        var (ok, detail) = GameFixes.BackgroundRecordingOff(_s.Key);
        Assert.True(ok);
        Assert.True(GameFixes.Undo(_s.Key, GameFixes.PayloadFromDetail(detail)!).Ok);
        Assert.Null(_s.Get(GameFixes.StoreKey, GameFixes.StoreValue));
        Assert.Null(_s.Get(GameFixes.DvrKey, GameFixes.CaptureValue));
        Assert.Null(_s.Get(GameFixes.DvrKey, GameFixes.HistoricalValue));

        GameFixes.BackgroundRecordingOff(_s.Key);
        var again = GameFixes.BackgroundRecordingOff(_s.Key);
        Assert.True(again.Ok);
        Assert.Contains("nothing to do", again.Detail);
        Assert.Null(GameFixes.PayloadFromDetail(again.Detail));
    }

    // ---- graphics card of one game ----

    private void Pref(string path, string value) => _s.Set(GameFixes.GpuKey, path, value, RegistryValueKind.String);
    private object? PrefOf(string path) => _s.Get(GameFixes.GpuKey, path);

    [Fact]
    public void GraphicsCard_RoundTrip_KeepsTheOtherFields()
    {
        Pref(Hades, "GpuPreference=1;SwapEffectUpgradeEnable=1;");
        Pref(Odd, "GpuPreference=1;");
        var (ok, detail) = GameFixes.UseGraphicsCard(_s.Key, "hades.exe");
        Assert.True(ok);
        Assert.Equal("GpuPreference=2;SwapEffectUpgradeEnable=1;", PrefOf(Hades));
        Assert.Equal("GpuPreference=1;", PrefOf(Odd));

        Assert.True(GameFixes.Undo(_s.Key, GameFixes.PayloadFromDetail(detail)!).Ok);
        Assert.Equal("GpuPreference=1;SwapEffectUpgradeEnable=1;", PrefOf(Hades));
    }

    [Theory]
    [InlineData("GpuPreference=1;", 2, "GpuPreference=2;")]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=1;AutoHDREnable=0;", 2, "SwapEffectUpgradeEnable=1;GpuPreference=2;AutoHDREnable=0;")]
    [InlineData("gpupreference=1", 2, "gpupreference=2")]
    [InlineData("VRROptimizeEnable=1;", 2, null)]
    public void WithGpuPreference_ChangesOnlyThatPart(string value, int pref, string? expected) =>
        Assert.Equal(expected, GameFixes.WithGpuPreference(value, pref));

    [Fact]
    public void GraphicsCard_Idempotent_AndOtherPreferencesRefused()
    {
        Pref(Hades, "GpuPreference=2;");
        var r = GameFixes.UseGraphicsCard(_s.Key, "Hades.exe");
        Assert.True(r.Ok);
        Assert.Contains("nothing to do", r.Detail);
        Assert.Null(GameFixes.PayloadFromDetail(r.Detail));
        Assert.Equal("GpuPreference=2;", PrefOf(Hades));

        Pref(Hades, "GpuPreference=0;");
        Assert.False(GameFixes.UseGraphicsCard(_s.Key, "Hades.exe").Ok);
        Assert.Equal("GpuPreference=0;", PrefOf(Hades));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Hades")]
    [InlineData(@"C:\x\Hades.exe")]
    [InlineData("..\\Hades.exe")]
    [InlineData("../Hades.exe")]
    [InlineData("*.exe")]
    [InlineData("Had?s.exe")]
    [InlineData("Hades.exe ")]
    [InlineData("Hades.exe\n")]
    [InlineData("Hades.dll")]
    [InlineData("a;b.exe")]
    public void GraphicsCard_InvalidNamesRefused_NothingChanged(string? name)
    {
        Pref(Hades, "GpuPreference=1;");
        var r = GameFixes.UseGraphicsCard(_s.Key, name);
        Assert.False(r.Ok);
        Assert.Equal("GpuPreference=1;", PrefOf(Hades));
    }

    [Fact]
    public void GraphicsCard_NoMatch_NotInAGameFolder_AndTwoMatchesAreRefused()
    {
        Pref(Odd, "GpuPreference=1;");
        Assert.False(GameFixes.UseGraphicsCard(_s.Key, "tool.exe").Ok);          // not in a game folder
        Assert.False(GameFixes.UseGraphicsCard(_s.Key, "Missing.exe").Ok);       // no such entry
        Assert.Equal("GpuPreference=1;", PrefOf(Odd));

        Pref(Hades, "GpuPreference=1;");
        Pref(Other, "GpuPreference=1;");
        var r = GameFixes.UseGraphicsCard(_s.Key, "Hades.exe");
        Assert.False(r.Ok);
        Assert.Equal("GpuPreference=1;", PrefOf(Hades));
        Assert.Equal("GpuPreference=1;", PrefOf(Other));
    }

    [Fact]
    public void GraphicsCard_NameMustBeTheWholeFileName()
    {
        Pref(@"C:\Program Files (x86)\Steam\steamapps\common\X\SuperHades.exe", "GpuPreference=1;");
        Assert.False(GameFixes.UseGraphicsCard(_s.Key, "Hades.exe").Ok);
    }

    [Fact]
    public void Undo_RefusesBadPayloads_AndWritesNothing()
    {
        foreach (var bad in new[] { "", "gamemode", "gamemode|x", "capture|missing", "gpu|a", "other|d%3A1", "gamemode|d%3Aabc", "gpu|" + Uri.EscapeDataString(Odd) + "|x" })
        {
            var r = GameFixes.Undo(_s.Key, bad);
            Assert.False(r.Ok);
        }
        Assert.Null(_s.Get(GameFixes.GameBarKey, GameFixes.GameModeValue));
        Assert.Null(_s.Get(GameFixes.GpuKey, Odd));
    }
}

public class GameFixHistoryTests : IDisposable
{
    private readonly ScratchRoot _s = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "afs-gamefix-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "session.db");

    public GameFixHistoryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { _s.Dispose(); SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string Hades = @"C:\Program Files (x86)\Steam\steamapps\common\Hades\Hades.exe";

    private static StepLog Step(string action, string detail) => new()
    {
        PlaybookStep = action, ActionId = action, Tier = 1, ProposedBy = "playbook", Approved = true,
        ApprovedAt = DateTime.UtcNow, StartedAt = DateTime.UtcNow, FinishedAt = DateTime.UtcNow, Result = StepResult.Success, Detail = detail,
    };

    private ChangeHistory History() =>
        new(Db, _dir, (kind, payload) => Task.FromResult(kind == ChangeHistory.GameFixKind ? GameFixes.Undo(_s.Key, payload) : (false, "unexpected")));

    [Fact]
    public async Task EachFix_IsListedWithUndo_AndUndoRestoresTheValues()
    {
        _s.Set(GameFixes.GameBarKey, GameFixes.GameModeValue, 0);
        _s.Set(GameFixes.StoreKey, GameFixes.StoreValue, 1);
        _s.Set(GameFixes.GpuKey, Hades, "GpuPreference=1;Other=5;", RegistryValueKind.String);

        var steps = new[]
        {
            Step(GameFixes.GameModeAction, GameFixes.GameModeOn(_s.Key).Detail),
            Step(GameFixes.RecordingAction, GameFixes.BackgroundRecordingOff(_s.Key).Detail),
            Step(GameFixes.GraphicsCardAction, GameFixes.UseGraphicsCard(_s.Key, "Hades.exe").Detail),
        };
        using (var log = new SessionLog(Db)) log.AddSteps(log.Start("graphics"), steps);

        var h = History();
        var list = h.List();
        Assert.Equal(3, list.Count);
        Assert.All(list, e => { Assert.True(e.CanUndo); Assert.Null(e.Hint); Assert.False(e.Undone); });

        foreach (var e in list) Assert.True((await h.UndoAsync(e.StepId)).Ok);
        Assert.Equal(0, _s.Get(GameFixes.GameBarKey, GameFixes.GameModeValue));
        Assert.Equal(1, _s.Get(GameFixes.StoreKey, GameFixes.StoreValue));
        Assert.Null(_s.Get(GameFixes.DvrKey, GameFixes.CaptureValue));
        Assert.Equal("GpuPreference=1;Other=5;", _s.Get(GameFixes.GpuKey, Hades));
        Assert.All(h.List(), e => { Assert.True(e.Undone); Assert.False(e.CanUndo); });
        Assert.False((await h.UndoAsync(list[0].StepId)).Ok);   // not twice
    }

    [Fact]
    public void NothingToDo_IsNotListedAsAChange_AndGetsNoUndoRecord()
    {
        _s.Set(GameFixes.GameBarKey, GameFixes.GameModeValue, 1);
        var detail = GameFixes.GameModeOn(_s.Key).Detail;
        Assert.Null(ChangeHistory.UndoFor(GameFixes.GameModeAction, detail));
        using (var log = new SessionLog(Db)) log.AddSteps(log.Start("graphics"), new[] { Step(GameFixes.GameModeAction, detail) });
        Assert.Empty(History().List());
    }

    [Fact]
    public void UndoFor_RecognisesGameFixes_AndDescribeAndHintAreFormal()
    {
        var (_, detail) = GameFixes.GameModeOn(_s.Key);
        var u = ChangeHistory.UndoFor(GameFixes.GameModeAction, detail);
        Assert.Equal(ChangeHistory.GameFixKind, u!.Value.Kind);
        Assert.Null(ChangeHistory.UndoFor("service.restart", detail));
        Assert.Null(ChangeHistory.UndoFor(GameFixes.GameModeAction, "undo=gamemode|bogus"));
        var bad = new Regex(@"n't\b|'ll\b|'re\b|'ve\b|'m\b|'d\b|\b(it|that|there|here|what)'s\b|\bI\b|\bwe\b|!", RegexOptions.IgnoreCase);
        foreach (var id in new[] { GameFixes.GameModeAction, GameFixes.GraphicsCardAction, GameFixes.RecordingAction })
            foreach (var s in new[] { ChangeHistory.Describe(id, null), ChangeHistory.HintFor(id) })
            {
                Assert.DoesNotMatch(bad, s);
                Assert.EndsWith(".", s);
                Assert.DoesNotContain("made a change to Windows", s);
            }
    }
}

public class GameFixLadderTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);

    private static GraphicsState State(params string[] games) => new()
    {
        Gpus = new[]
        {
            new GpuInfo("NVIDIA GeForce RTX 3070", "31.0", Now.AddDays(-30), 8L << 30, GpuKind.Dedicated, true),
            new GpuInfo("Intel(R) UHD Graphics", "31.0", Now.AddDays(-30), null, GpuKind.Integrated, false),
        },
        IsDesktop = Tri.No,
        Games = new GameSettingsState { GameMode = Tri.No, PowerSavingGames = games, BackgroundRecording = Tri.Yes, TotalRamBytes = 8L << 30 },
    };

    private static IReadOnlyList<Rung> Ladder(GraphicsState s) => GraphicsTriage.Ladder(s, GraphicsTriage.Evaluate(s, Now));

    [Fact]
    public void FixRungsComeBeforeTheAdviceFallback_WithUniqueIds()
    {
        var l = Ladder(State("hades.exe", "eldenring.exe"));
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
        foreach (var (fix, advice) in new[] { ("gpu_game_mode_fix", "gpu_game_mode"), ("gpu_game_preference_fix_2", "gpu_game_preference"), ("gpu_game_capture_fix", "gpu_game_capture") })
        {
            var f = l.ToList().FindIndex(r => r.Id == fix);
            var a = l.ToList().FindIndex(r => r.Id == advice);
            Assert.True(f >= 0 && a >= 0 && f < a, $"{fix} must come before {advice}");
        }
        var pref = l.Where(r => r.ActionId == GameFixes.GraphicsCardAction).ToList();
        Assert.Equal(new[] { "hades.exe", "eldenring.exe" }, pref.Select(r => r.Arg));
        Assert.All(l.Where(r => GameFixes.IsGameFix(r.ActionId)), r => Assert.Equal(1, r.Tier));
        Assert.Equal("gpu_settings", l[^1].Id);
    }

    [Fact]
    public void GameFixRungs_AreCapped_AndSkipNamesTheFixWouldRefuse()
    {
        var l = Ladder(State("a.exe", "b.exe", "c.exe", "d.exe", "it's.exe"));
        Assert.Equal(GraphicsTriage.MaxGameFixes, l.Count(r => r.ActionId == GameFixes.GraphicsCardAction));
        var l2 = Ladder(State("it's.exe"));
        Assert.DoesNotContain(l2, r => r.ActionId == GameFixes.GraphicsCardAction);
        Assert.Contains(l2, r => r.Id == "gpu_game_preference");
    }

    [Fact]
    public void FixRungWording_FollowsTheWritingGuide()
    {
        var bad = new Regex(@"n't|'ll|'re|'ve|'s |'d |(^|[\s""(])I\s|\bwe\b|!", RegexOptions.IgnoreCase);
        var fixes = Ladder(State("hades.exe")).Where(r => GameFixes.IsGameFix(r.ActionId)).ToList();
        Assert.Equal(3, fixes.Count);
        foreach (var r in fixes)
        {
            Assert.DoesNotMatch(bad, r.Say);
            Assert.EndsWith(".", r.Say);
            Assert.StartsWith("AutoFix Scout will ", r.Say);
            Assert.Contains("This can be undone from History.", r.Say);
        }
        Assert.DoesNotContain(GameFixes.GameModeAction, FixActions.ElevatedActions);
        Assert.DoesNotContain(GameFixes.GraphicsCardAction, FixActions.ElevatedActions);
        Assert.DoesNotContain(GameFixes.RecordingAction, FixActions.ElevatedActions);
    }
}
