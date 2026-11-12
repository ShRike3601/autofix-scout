using AutoFixScout.Areas;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class ShellTests
{
    private static ShellState Healthy() => new()
    {
        Search = new UpdateServiceInfo("WSearch", "Running", "Automatic"),
        Packages = new[]
        {
            new ShellPackage("Microsoft.Windows.StartMenuExperienceHost", "Microsoft.Windows.StartMenuExperienceHost_10.0_neutral_neutral_cw5n1h2txyewy", "Ok"),
            new ShellPackage("Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.ShellExperienceHost_10.0_neutral_neutral_cw5n1h2txyewy", "Ok"),
        },
    };

    [Fact]
    public void NoTrace_StillOffersStepsMildestFirst()
    {
        var s = Healthy();
        var f = ShellTriage.Evaluate(s);
        Assert.Equal(FindingCode.LooksNormal, f[0].Code);
        var l = ShellTriage.Ladder(s, f).Select(r => r.ActionId).ToList();
        Assert.Equal("shell.restart_start_menu", l[0]);
        Assert.True(l.IndexOf("apps.reregister") < l.IndexOf("repair.dism"));
        Assert.Equal("repair.sfc", l[^1]);
    }

    [Fact]
    public void DamagedPackage_IsFoundAndReregisteredFirstAmongPackages()
    {
        var s = Healthy();
        s.Packages = new[] { s.Packages[0], s.Packages[1] with { Status = "Modified" } };
        var f = ShellTriage.Evaluate(s);
        Assert.Equal(FindingCode.ShellPackageDamaged, f[0].Code);
        Assert.Equal(s.Packages[1].FullName, ShellTriage.Ladder(s, f).First(r => r.ActionId == "apps.reregister").Arg);
    }

    [Fact]
    public void SearchServiceOff_IsRestored()
    {
        var s = Healthy(); s.Search = new UpdateServiceInfo("WSearch", "Stopped", "Disabled");
        var f = ShellTriage.Evaluate(s);
        Assert.Contains(f, x => x.Code == FindingCode.HealthServiceOff);
        Assert.Contains(ShellTriage.Ladder(s, f), r => r.ActionId == "service.restore_default" && r.Arg == "WSearch");
    }

    [Fact]
    public void Packages_Parse()
    {
        Assert.Single(ShellFlow.ParsePackages("{\"Name\":\"A\",\"PackageFullName\":\"A_1\",\"Status\":\"Ok\"}"));
        Assert.Empty(ShellFlow.ParsePackages(" "));
    }

    [Fact]
    public void Sentences_FollowTheWritingGuide()
    {
        var s = Healthy(); s.Search = new UpdateServiceInfo("WSearch", "Stopped", "Disabled");
        s.ShellCrashes = new[] { new CrashInfo("explorer.exe", "ntdll.dll", "c0000374", 2, DateTime.UtcNow) };
        var f = ShellTriage.Evaluate(s);
        var l = ShellTriage.Ladder(s, f);
        foreach (var t in f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)).Append(ShellTriage.NotFixedHint(f) ?? ""))
            Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!|\bit's\b|\bthat's\b", t);
        foreach (var r in l.Where(r => FixActions.ElevatedActions.Contains(r.ActionId)))
            Assert.Contains("Windows will ask for your permission", r.Say);
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
    }
}
