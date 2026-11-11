using AutoFixScout.Triage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AutoFixScout.Tests;

public class SessionLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"afs-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public async Task RunnerSteps_RoundTripIntoSpecSchema()
    {
        var steps = new List<StepLog>();
        var ladder = new[] { new Rung("unmute", "audio.unmute_and_set_volume", 1, "x"), new Rung("restart", "service.restart", 1, "y") };
        await LadderRunner.RunAsync(ladder, new Host(), steps);

        using var db = new SessionLog(_path);
        var id = db.Start("audio");
        db.AddSteps(id, steps);
        db.End(id, Outcome.Fixed);

        Assert.Equal("fixed", db.Scalar($"SELECT outcome FROM session WHERE session_id = '{id}'"));
        Assert.Equal(2L, db.Scalar("SELECT COUNT(*) FROM step_log"));
        // First rung ran but the person said no; second fixed it.
        Assert.Equal(0L, db.Scalar("SELECT user_confirmed_fixed FROM step_log WHERE playbook_step = 'unmute'"));
        Assert.Equal("success", db.Scalar("SELECT result FROM step_log WHERE playbook_step = 'restart'"));
        Assert.Equal("1", db.Scalar("SELECT value FROM setting WHERE key = 'helper_approval_required_for_t2'"));
    }

    [Fact]
    public void SchemaRejectsUnknownSymptom()
    {
        using var db = new SessionLog(_path);
        Assert.Throws<SqliteException>(() => db.Start("printer"));
    }

    [Fact]
    public void ReopenIsIdempotent_AndDeleteAllClears()
    {
        using (var db = new SessionLog(_path)) db.End(db.Start("network"), Outcome.NotFixed);
        using var again = new SessionLog(_path);
        Assert.Equal(1L, again.Scalar("SELECT COUNT(*) FROM session"));
        again.DeleteAll();
        Assert.Equal(0L, again.Scalar("SELECT COUNT(*) FROM session"));
    }

    private sealed class Host : IFixHost
    {
        private int _tests;
        public Task<bool> ConfirmAsync(Rung rung) => Task.FromResult(true);
        public Task<(bool Ok, string Detail)> RunAsync(Rung rung) => Task.FromResult((true, ""));
        public Task<bool> TestAsync() => Task.FromResult(_tests++ == 1);
    }
}
