using System.Text.RegularExpressions;
using AutoFixScout.Triage;
using AutoFixScout.Weekly;
using Xunit;

namespace AutoFixScout.Tests;

public class WeeklyCheckupTests
{
    [Fact]
    public void Create_arguments_use_the_task_name_the_weekday_and_the_rounded_time()
    {
        var a = WeeklyCheckupSetup.CreateArguments(@"C:\Program Files\AutoFix Scout\AutoFixScout.UI.exe", new DateTime(2026, 11, 14, 10, 31, 20)); // Saturday
        Assert.Equal("AutoFix Scout weekly check-up", a[a.ToList().IndexOf("/tn") + 1]);
        Assert.Equal("weekly", a[a.ToList().IndexOf("/sc") + 1]);
        Assert.Equal("SAT", a[a.ToList().IndexOf("/d") + 1]);
        Assert.Equal("10:40", a[a.ToList().IndexOf("/st") + 1]); // 10:36 rounded up to 10:40
        Assert.Equal("/f", a[^1]);
        Assert.DoesNotContain("/ru", a);
        Assert.DoesNotContain("/rl", a);
    }

    [Fact]
    public void First_run_rolls_over_midnight_and_the_weekday_follows()
    {
        var first = WeeklyCheckupSetup.FirstRun(new DateTime(2026, 11, 14, 23, 58, 0));
        Assert.Equal(new DateTime(2026, 11, 15, 0, 5, 0), first);
        var a = WeeklyCheckupSetup.CreateArguments(@"C:\a\AutoFixScout.UI.exe", new DateTime(2026, 11, 14, 23, 58, 0));
        Assert.Equal("SUN", a[a.ToList().IndexOf("/d") + 1]);
        Assert.Equal("00:05", a[a.ToList().IndexOf("/st") + 1]);
        Assert.Equal(new DateTime(2026, 11, 14, 10, 5, 0), WeeklyCheckupSetup.FirstRun(new DateTime(2026, 11, 14, 10, 0, 0)));
    }

    [Fact]
    public void Task_run_quotes_a_path_with_spaces_and_adds_the_argument()
    {
        Assert.Equal("\"C:\\Program Files\\AutoFix Scout\\AutoFixScout.UI.exe\" --weekly-checkup",
            WeeklyCheckupSetup.TaskRun(@"C:\Program Files\AutoFix Scout\AutoFixScout.UI.exe"));
        Assert.Equal("\"C:\\x\\a.exe\" --weekly-checkup", WeeklyCheckupSetup.TaskRun("\"C:\\x\\a.exe\""));
    }

    [Fact]
    public void Delete_and_query_arguments_name_the_task()
    {
        Assert.Equal(new[] { "/delete", "/tn", "AutoFix Scout weekly check-up", "/f" }, WeeklyCheckupSetup.DeleteArguments());
        Assert.Equal(new[] { "/query", "/tn", "AutoFix Scout weekly check-up" }, WeeklyCheckupSetup.QueryArguments());
    }

    [Fact]
    public void Catch_up_settings_start_when_available_and_allow_battery()
    {
        var script = WeeklyCheckupSetup.CatchUpArguments()[^1];
        Assert.Contains("-StartWhenAvailable", script);
        Assert.Contains("-AllowStartIfOnBatteries", script);
        Assert.Contains("'AutoFix Scout weekly check-up'", script);
    }

    [Fact]
    public void Only_real_problems_cause_a_notification()
    {
        Assert.False(WeeklyCheckup.ShouldNotify(Array.Empty<FindingCode>()));
        Assert.False(WeeklyCheckup.ShouldNotify(new[] { FindingCode.LooksNormal }));
        Assert.False(WeeklyCheckup.ShouldNotify(new[] { FindingCode.LooksNormal, FindingCode.ProbeIncomplete, FindingCode.PowerBatteryAgeing }));
        Assert.True(WeeklyCheckup.ShouldNotify(new[] { FindingCode.LooksNormal, FindingCode.AudioServiceStopped }));
        Assert.Equal(2, WeeklyCheckup.CountProblems(new[] { FindingCode.AudioServiceStopped, FindingCode.EndpointBuilderStopped, FindingCode.ProbeIncomplete }));
    }

    [Fact]
    public void Result_round_trips_and_holds_only_date_count_and_codes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "afs-weekly-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = WeeklyCheckup.Summarize(new[] { FindingCode.AudioServiceStopped, FindingCode.LooksNormal }, new DateTime(2026, 11, 10));
            WeeklyCheckup.Save(r, dir);
            var back = WeeklyCheckup.Load(dir)!;
            Assert.Equal("2026-11-10", back.Date);
            Assert.Equal(1, back.Problems);
            Assert.Equal(r.Codes, back.Codes);
            var text = File.ReadAllText(WeeklyCheckup.FileIn(dir));
            Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\", text);
            Assert.Matches(@"^\s*\{\s*""Date"":\s*""2026-11-10"",\s*""Problems"":\s*1,\s*""Codes"":\s*\[[^\]]*\]\s*\}\s*$", text);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void A_damaged_or_missing_file_reads_as_no_result()
    {
        var dir = Path.Combine(Path.GetTempPath(), "afs-weekly-" + Guid.NewGuid().ToString("N"));
        Assert.Null(WeeklyCheckup.Load(dir));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(WeeklyCheckup.FileIn(dir), "{ not json");
            Assert.Null(WeeklyCheckup.Load(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Run_notifies_only_when_something_needs_attention_and_never_throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "afs-weekly-" + Guid.NewGuid().ToString("N"));
        try
        {
            var shown = new List<(string, string)>();
            Task<IReadOnlyList<Finding>> Fine() =>
                Task.FromResult<IReadOnlyList<Finding>>(new[] { new Finding(FindingCode.LooksNormal, "", "") });
            Assert.Equal(0, await WeeklyCheckup.RunAsync((t, b) => shown.Add((t, b)), Fine, dir, new DateTime(2026, 11, 10)));
            Assert.Empty(shown);
            Assert.Equal("Last check-up: 10 Nov, no problems found", WeeklyCheckup.Describe(WeeklyCheckup.Load(dir)));

            Task<IReadOnlyList<Finding>> Bad() => Task.FromResult<IReadOnlyList<Finding>>(new[]
            {
                new Finding(FindingCode.AudioServiceStopped, "", ""), new Finding(FindingCode.EndpointBuilderStopped, "", ""),
            });
            Assert.Equal(2, await WeeklyCheckup.RunAsync((t, b) => shown.Add((t, b)), Bad, dir, new DateTime(2026, 11, 17)));
            var (title, body) = Assert.Single(shown);
            Assert.Equal("AutoFix Scout found something to check", title);
            Assert.Equal("The weekly check-up found 2 things that need attention. Open AutoFix Scout to see them.", body);

            Assert.Equal(-1, await WeeklyCheckup.RunAsync((_, _) => throw new InvalidOperationException(), () => throw new InvalidOperationException(), dir));
            Assert.Equal(-1, await WeeklyCheckup.RunAsync((_, _) => { }, () => new TaskCompletionSource<IReadOnlyList<Finding>>().Task, dir, null, TimeSpan.FromMilliseconds(50)));
            Assert.Equal(2, await WeeklyCheckup.RunAsync((_, _) => throw new InvalidOperationException(), Bad, dir)); // a failing toast changes nothing
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Wording_is_formal_and_has_no_contractions_or_exclamations()
    {
        var texts = new[]
        {
            WeeklyCheckup.ToastTitle, WeeklyCheckup.ToastBody(1), WeeklyCheckup.ToastBody(2), WeeklyCheckup.Describe(null),
            WeeklyCheckup.Describe(new WeeklyResult("2026-11-10", 0, Array.Empty<string>())),
            WeeklyCheckup.Describe(new WeeklyResult("2026-11-10", 1, Array.Empty<string>())),
            "Once a week AutoFix Scout checks this computer in the background and shows a notification only when something needs attention. It changes nothing.",
        };
        foreach (var t in texts)
        {
            Assert.DoesNotMatch(@"n't|'d |'ll |'re |'ve |'s |!", t);
            Assert.DoesNotMatch(@"\b(I|we|We|my|our)\b", t);
        }
        Assert.Equal("Last check-up: 10 Nov, no problems found", texts[4]);
    }
}
