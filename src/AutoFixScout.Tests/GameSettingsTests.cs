using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class GameSettingsParserTests
{
    [Fact]
    public void Number_AcceptsRegistryTypes_AndRejectsOddValues()
    {
        Assert.Equal(1, GameSettingsParsers.Number(1));
        Assert.Equal(2, GameSettingsParsers.Number(2u));
        Assert.Equal(1, GameSettingsParsers.Number(new byte[] { 1, 0, 0, 0 }));
        Assert.Equal(1, GameSettingsParsers.Number(" 1 "));
        Assert.Null(GameSettingsParsers.Number(null));
        Assert.Null(GameSettingsParsers.Number("on"));
        Assert.Null(GameSettingsParsers.Number(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void GameMode_MissingMeansOnOnlyOnWindows11()
    {
        Assert.Equal(Tri.Yes, GameSettingsParsers.GameMode(null, true));
        Assert.Equal(Tri.Unknown, GameSettingsParsers.GameMode(null, false));
        Assert.Equal(Tri.No, GameSettingsParsers.GameMode(0, true));
        Assert.Equal(Tri.Yes, GameSettingsParsers.GameMode(1, false));
        Assert.Equal(Tri.Unknown, GameSettingsParsers.GameMode("banana", true));
        Assert.Equal(Tri.Unknown, GameSettingsParsers.GameMode(7, true));
    }

    [Fact]
    public void HardwareScheduling_TwoIsOn_OneIsOff_RestUnknown()
    {
        Assert.Equal(Tri.Yes, GameSettingsParsers.HardwareScheduling(2));
        Assert.Equal(Tri.No, GameSettingsParsers.HardwareScheduling(1));
        Assert.Equal(Tri.Unknown, GameSettingsParsers.HardwareScheduling(null));
        Assert.Equal(Tri.Unknown, GameSettingsParsers.HardwareScheduling(0));
        Assert.Equal(Tri.Unknown, GameSettingsParsers.HardwareScheduling(3));
        Assert.Equal(Tri.Unknown, GameSettingsParsers.HardwareScheduling("x"));
    }

    [Theory]
    [InlineData("SwapEffectUpgradeEnable=1;VRROptimizeEnable=0;", Tri.Yes, Tri.No)]
    [InlineData("VRROptimizeEnable=1;SwapEffectUpgradeEnable=0;", Tri.No, Tri.Yes)]
    [InlineData("swapeffectupgradeenable=1;", Tri.Yes, Tri.Unknown)]
    [InlineData("SwapEffectUpgradeEnable=1", Tri.Yes, Tri.Unknown)]
    [InlineData("AutoHDREnable=1;", Tri.Unknown, Tri.Unknown)]
    [InlineData("SwapEffectUpgradeEnable=;VRROptimizeEnable=maybe;", Tri.Unknown, Tri.Unknown)]
    [InlineData(";;=;==;garbage", Tri.Unknown, Tri.Unknown)]
    [InlineData("", Tri.Unknown, Tri.Unknown)]
    public void GlobalSettings_ParsesDefensively(string text, Tri windowed, Tri vrr)
    {
        Assert.Equal((windowed, vrr), GameSettingsParsers.GlobalSettings(text));
    }

    [Fact]
    public void GlobalSettings_MissingOrNotText_IsUnknown()
    {
        Assert.Equal((Tri.Unknown, Tri.Unknown), GameSettingsParsers.GlobalSettings(null));
        Assert.Equal((Tri.Unknown, Tri.Unknown), GameSettingsParsers.GlobalSettings(5));
        Assert.Equal((Tri.Unknown, Tri.Unknown), GameSettingsParsers.GlobalSettings(new byte[] { 1 }));
    }

    [Theory]
    [InlineData("GpuPreference=1;", 1)]
    [InlineData("GpuPreference=2;", 2)]
    [InlineData("GpuPreference=0;", 0)]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=1;", 1)]
    [InlineData("GpuPreference=5;", null)]
    [InlineData("GpuPreference=;", null)]
    [InlineData("Other=1;", null)]
    [InlineData("", null)]
    public void GpuPreference_Parses(string text, int? expected) => Assert.Equal(expected, GameSettingsParsers.GpuPreference(text));

    [Fact]
    public void GpuPreference_NotText_IsNull()
    {
        Assert.Null(GameSettingsParsers.GpuPreference(null));
        Assert.Null(GameSettingsParsers.GpuPreference(1));
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\ELDEN RING\Game\eldenring.exe", "eldenring.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Hades\x64\Hades.exe", "Hades.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", "FortniteClient-Win64-Shipping.exe")]
    [InlineData(@"C:\XboxGames\Halo\Content\halo.exe", "halo.exe")]
    [InlineData(@"C:\Riot Games\VALORANT\live\VALORANT.exe", "VALORANT.exe")]
    [InlineData(@"C:\GOG Galaxy\Games\Witcher 3\witcher3.exe", "witcher3.exe")]
    [InlineData(@"C:\Program Files\EA Games\Apex\r5apex.exe", "r5apex.exe")]
    [InlineData(@"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Far Cry 6\FarCry6.exe", "FarCry6.exe")]
    [InlineData("C:/Program Files/Epic Games/Game/game.exe", "game.exe")]
    public void GameFileName_ReturnsOnlyTheFileName_ForGameFolders(string path, string expected)
    {
        var name = GameSettingsParsers.GameFileName(path);
        Assert.Equal(expected, name);
        Assert.DoesNotContain('\\', name!);
        Assert.DoesNotContain('/', name!);
    }

    [Theory]
    [InlineData(@"C:\Users\Alex\AppData\Local\Programs\Notepad++\notepad++.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe")]
    [InlineData(@"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\UbisoftConnect.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Game\readme.txt")]
    [InlineData("Microsoft.MinecraftUWP_8wekyb3d8bbwe!App")]
    [InlineData("eldenring.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void GameFileName_IgnoresEverythingElse(string? path) => Assert.Null(GameSettingsParsers.GameFileName(path));

    [Fact]
    public void PowerSavingGames_ListsOnlyGamesOnPowerSaving_AsFileNames()
    {
        var user = "Alexandra" + "Smith";
        var entries = new List<KeyValuePair<string, object?>>
        {
            new(@"C:\Program Files (x86)\Steam\steamapps\common\ELDEN RING\Game\eldenring.exe", "GpuPreference=1;"),
            new(@"C:\Program Files (x86)\Steam\steamapps\common\Hades\Hades.exe", "GpuPreference=2;"),
            new(@"D:\SteamLibrary\steamapps\common\Celeste\Celeste.exe", "GpuPreference=1;"),
            new(@"D:\SteamLibrary\steamapps\common\Celeste\Celeste.exe", "GpuPreference=1;"),
            new($@"C:\Users\{user}\AppData\Local\Programs\Notepad\notepad.exe", "GpuPreference=1;"),
            new(@"C:\Riot Games\VALORANT\live\VALORANT.exe", 7),
            new(@"C:\Riot Games\League\League.exe", null),
        };
        var names = GameSettingsParsers.PowerSavingGames(entries);
        Assert.Equal(new[] { "Celeste.exe", "eldenring.exe" }, names);
        Assert.DoesNotContain(names, n => n.Contains(user) || n.Contains('\\'));
    }

    [Fact]
    public void PowerSavingGames_IsCappedAtTen()
    {
        var entries = Enumerable.Range(0, 30)
            .Select(i => new KeyValuePair<string, object?>($@"C:\Epic Games\G{i:00}\g{i:00}.exe", "GpuPreference=1;"));
        Assert.Equal(10, GameSettingsParsers.PowerSavingGames(entries).Count);
    }

    [Fact]
    public void BackgroundRecording_NeedsAllThree()
    {
        Assert.Equal(Tri.Yes, GameSettingsParsers.BackgroundRecording(Tri.Yes, Tri.Yes, Tri.Yes));
        Assert.Equal(Tri.No, GameSettingsParsers.BackgroundRecording(Tri.Yes, Tri.Yes, Tri.No));
        Assert.Equal(Tri.No, GameSettingsParsers.BackgroundRecording(Tri.No, Tri.Unknown, Tri.Yes));
        Assert.Equal(Tri.Unknown, GameSettingsParsers.BackgroundRecording(Tri.Yes, Tri.Unknown, Tri.Yes));
    }
}

public class GameSettingsTriageTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
    private const long GiB = 1024L * 1024 * 1024;

    private static GpuInfo Dgpu(string name = "NVIDIA GeForce RTX 3070") => new(name, "31.0", Now.AddDays(-30), 8 * GiB, GpuKind.Dedicated, true);
    private static GpuInfo Igpu() => new("Intel(R) UHD Graphics", "31.0", Now.AddDays(-30), null, GpuKind.Integrated, false);

    private static GraphicsState State(Tri desktop = Tri.No, GpuInfo? d = null) =>
        new() { Gpus = new[] { d ?? Dgpu(), Igpu() }, IsDesktop = desktop };

    private static HashSet<FindingCode> Codes(GraphicsState s) => GraphicsTriage.Evaluate(s, Now).Select(f => f.Code).ToHashSet();
    private static bool Has(GraphicsState s, FindingCode c) => Codes(s).Contains(c);

    [Fact]
    public void NothingUnknownIsAFinding()
    {
        var s = State();
        var f = GraphicsTriage.Evaluate(s, Now);
        Assert.Equal(FindingCode.LooksNormal, Assert.Single(f).Code);
        Assert.DoesNotContain(Codes(s), c => c.ToString().StartsWith("GameSet"));
    }

    [Fact]
    public void GameMode_OnlyWhenReadAsOff()
    {
        var s = State();
        s.Games.GameMode = Tri.No;
        Assert.True(Has(s, FindingCode.GameSetGameModeOff));
        s.Games.GameMode = Tri.Yes;
        Assert.False(Has(s, FindingCode.GameSetGameModeOff));
        s.Games.GameMode = Tri.Unknown;
        Assert.False(Has(s, FindingCode.GameSetGameModeOff));
    }

    [Fact]
    public void HardwareScheduling_OnlyOffWithSupportedDedicatedCard()
    {
        var s = State();
        s.Games.HardwareScheduling = Tri.No;
        Assert.True(Has(s, FindingCode.GameSetHardwareSchedulingOff));
        var amd = State(d: Dgpu("AMD Radeon RX 6600"));
        amd.Games.HardwareScheduling = Tri.No;
        Assert.True(Has(amd, FindingCode.GameSetHardwareSchedulingOff));
        s.Games.HardwareScheduling = Tri.Yes;
        Assert.False(Has(s, FindingCode.GameSetHardwareSchedulingOff));
        s.Games.HardwareScheduling = Tri.Unknown;
        Assert.False(Has(s, FindingCode.GameSetHardwareSchedulingOff));
        var noCard = new GraphicsState { Gpus = new[] { Igpu() }, IsDesktop = Tri.No };
        noCard.Games.HardwareScheduling = Tri.No;
        Assert.False(Has(noCard, FindingCode.GameSetHardwareSchedulingOff));
    }

    [Fact]
    public void GameOnPowerSaving_LaptopWithDedicatedCardOnly()
    {
        var laptop = State(Tri.No);
        laptop.Games.PowerSavingGames = new[] { "eldenring.exe" };
        var f = GraphicsTriage.Evaluate(laptop, Now).Single(x => x.Code == FindingCode.GameSetGameOnPowerSaving);
        Assert.Equal("The game \"eldenring.exe\" is set to use the power-saving graphics chip instead of the graphics card.", f.PlainMessage);

        var desktop = State(Tri.Yes);
        desktop.Games.PowerSavingGames = new[] { "eldenring.exe" };
        Assert.False(Has(desktop, FindingCode.GameSetGameOnPowerSaving));

        var unknown = State(Tri.Unknown);
        unknown.Games.PowerSavingGames = new[] { "eldenring.exe" };
        Assert.False(Has(unknown, FindingCode.GameSetGameOnPowerSaving));

        var integratedOnly = new GraphicsState { Gpus = new[] { Igpu() }, IsDesktop = Tri.No };
        integratedOnly.Games.PowerSavingGames = new[] { "eldenring.exe" };
        Assert.False(Has(integratedOnly, FindingCode.GameSetGameOnPowerSaving));

        Assert.False(Has(State(Tri.No), FindingCode.GameSetGameOnPowerSaving));
    }

    [Fact]
    public void GameOnPowerSaving_SeveralGames_ListsFileNamesOnly()
    {
        var s = State(Tri.No);
        s.Games.PowerSavingGames = new[] { "a.exe", "b.exe" };
        var f = GraphicsTriage.Evaluate(s, Now).Single(x => x.Code == FindingCode.GameSetGameOnPowerSaving);
        Assert.Contains("\"a.exe\", \"b.exe\"", f.PlainMessage);
        Assert.DoesNotContain("\\", f.PlainMessage + f.TechnicalDetail);
    }

    [Fact]
    public void PowerEfficiency_OnlyWhenPluggedIn()
    {
        var s = State();
        s.Games.PowerOverlayGuid = PerfTriage.OverlayBestEfficiencyGuid.ToUpperInvariant();
        s.Games.OnBattery = false;
        Assert.True(Has(s, FindingCode.GameSetPowerEfficiencyPluggedIn));
        s.Games.OnBattery = true;
        Assert.False(Has(s, FindingCode.GameSetPowerEfficiencyPluggedIn));
        s.Games.OnBattery = null;
        Assert.False(Has(s, FindingCode.GameSetPowerEfficiencyPluggedIn));
        s.Games.OnBattery = false;
        s.Games.PowerOverlayGuid = "ded574b5-45a0-4f42-8737-46345c09c238";
        Assert.False(Has(s, FindingCode.GameSetPowerEfficiencyPluggedIn));
        s.Games.PowerOverlayGuid = null;
        Assert.False(Has(s, FindingCode.GameSetPowerEfficiencyPluggedIn));
    }

    [Fact]
    public void WindowedOptimisationsAndVrr_OnlyWhenExplicitlyOff()
    {
        var s = State();
        s.Games.WindowedOptimisations = Tri.No;
        var one = GraphicsTriage.Evaluate(s, Now).Single(x => x.Code == FindingCode.GameSetWindowedOptimisationsOff);
        Assert.Contains("\"Optimizations for windowed games\" is turned off", one.PlainMessage);
        s.Games.VariableRefresh = Tri.No;
        var both = GraphicsTriage.Evaluate(s, Now).Single(x => x.Code == FindingCode.GameSetWindowedOptimisationsOff);
        Assert.Contains("\"Variable refresh rate\"", both.PlainMessage);
        s.Games.WindowedOptimisations = Tri.Yes;
        s.Games.VariableRefresh = Tri.Unknown;
        Assert.False(Has(s, FindingCode.GameSetWindowedOptimisationsOff));
        s.Games.WindowedOptimisations = Tri.Unknown;
        Assert.False(Has(s, FindingCode.GameSetWindowedOptimisationsOff));
    }

    [Fact]
    public void BackgroundRecording_OnlyOnWeakerComputers()
    {
        var s = State();
        s.Games.BackgroundRecording = Tri.Yes;
        s.Games.TotalRamBytes = 8 * GiB;
        Assert.True(Has(s, FindingCode.GameSetBackgroundRecording));
        s.Games.TotalRamBytes = 16 * GiB;
        Assert.False(Has(s, FindingCode.GameSetBackgroundRecording));
        s.Games.TotalRamBytes = null;
        Assert.False(Has(s, FindingCode.GameSetBackgroundRecording));
        s.Games.TotalRamBytes = 8 * GiB;
        s.Games.BackgroundRecording = Tri.No;
        Assert.False(Has(s, FindingCode.GameSetBackgroundRecording));
        s.Games.BackgroundRecording = Tri.Unknown;
        Assert.False(Has(s, FindingCode.GameSetBackgroundRecording));
    }

    private static GraphicsState Everything()
    {
        var s = State(Tri.No);
        s.Games = new GameSettingsState
        {
            GameMode = Tri.No, HardwareScheduling = Tri.No, PowerSavingGames = new[] { "eldenring.exe", "hades.exe" },
            PowerOverlayGuid = PerfTriage.OverlayBestEfficiencyGuid, OnBattery = false, WindowedOptimisations = Tri.No, VariableRefresh = Tri.No,
            BackgroundRecording = Tri.Yes, TotalRamBytes = 8 * GiB,
        };
        return s;
    }

    [Fact]
    public void Ladder_HasRungPerFinding_UniqueIds_AndSettingsPages()
    {
        var s = Everything();
        var f = GraphicsTriage.Evaluate(s, Now);
        Assert.Equal(6, f.Count(x => x.Code.ToString().StartsWith("GameSet")));
        var l = GraphicsTriage.Ladder(s, f);
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
        Assert.All(l, r => Assert.True(r.Tier == 0 && r.ActionId is "ui.human_step" or "ui.open_settings_page"));
        Assert.Contains(l, r => r.Arg == "gaming");
        Assert.Contains(l, r => r.Arg == "capture");
        Assert.Contains(l, r => r.Arg == "powersleep");
        Assert.Contains(l, r => r.Id == "gpu_game_preference");
        Assert.Contains(l, r => r.Id == "gpu_default_graphics" && r.Say.Contains("Hardware-accelerated GPU scheduling"));
        Assert.Equal("gpu_settings", l[^1].Id);
    }

    [Fact]
    public void Text_FollowsWritingGuide()
    {
        var s = Everything();
        var f = GraphicsTriage.Evaluate(s, Now);
        var texts = f.Select(x => x.PlainMessage).Concat(GraphicsTriage.Ladder(s, f).Select(r => r.Say)).ToList();
        var bad = new Regex(@"n't|'ll|'re|'ve|'s |'d |(^|[\s""(])I\s|\bwe\b|!", RegexOptions.IgnoreCase);
        foreach (var t in texts)
        {
            Assert.DoesNotMatch(bad, t);
            Assert.EndsWith(".", t);
        }
    }

    [Fact]
    public void RealProbe_ReportsActivityWithoutPathsOrUserName()
    {
        using var scope = Activity.Scope("gamesettest");
        var g = RealGraphicsProbe.ReadGameSettings();
        var lines = Activity.Recent().Where(x => x.Area == "gamesettest").Select(x => x.Text).ToList();
        Assert.True(lines.Count >= 1, string.Join(" | ", lines)); // the shared readers may add lines; only privacy matters here
        foreach (var l in lines)
        {
            Assert.DoesNotContain(Environment.UserName, l, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@":\", l);
        }
        Assert.All(g.PowerSavingGames, n => Assert.DoesNotContain('\\', n));
    }
}

[Collection("Live machine")]
public class LiveGameSettingsTests
{
    [Fact]
    public void GameSettings_AreReadOnThisMachine()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var s = new RealGraphicsProbe().Read();
        var g = s.Games;
        var f = GraphicsTriage.Evaluate(s).Where(x => x.Code.ToString().StartsWith("GameSet")).ToList();
        var lines = new List<string>
        {
            "errors: " + string.Join(" | ", s.ProbeErrors),
            $"desktop {s.IsDesktop}; dedicated GPU: {s.Dedicated != null}",
            $"Game Mode {g.GameMode}; hardware scheduling {g.HardwareScheduling}; windowed optimisations {g.WindowedOptimisations}; variable refresh {g.VariableRefresh}",
            $"power mode overlay {g.PowerOverlayGuid}; on battery {g.OnBattery}; background recording {g.BackgroundRecording}; RAM {g.TotalRamBytes / 1048576} MB",
            $"games on power saving (file names only): {string.Join(", ", g.PowerSavingGames)}",
        };
        lines.AddRange(f.Select(x => $"FINDING {x.Code}: {x.PlainMessage}"));
        File.WriteAllLines(output, lines);
        Assert.Empty(s.ProbeErrors);
        Assert.All(g.PowerSavingGames, n => Assert.DoesNotContain('\\', n));
    }
}
