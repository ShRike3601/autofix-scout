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
    public void CutOffFlow_IsClosedAsAbandoned_OnNextStart_FinishedOnesUntouched()
    {
        // Test step 5c: the app was closed mid driver change and session.db had no trace of the flow.
        string cutOff, finished;
        using (var db = new SessionLog(_path))
        {
            cutOff = db.Start("audio");
            finished = db.Start("network");
            db.End(finished, Outcome.Fixed);
        }
        using var reopened = new SessionLog(_path);
        Assert.Equal(1, reopened.CloseUnfinished());
        Assert.Equal("abandoned", reopened.Scalar($"SELECT outcome FROM session WHERE session_id = '{cutOff}'"));
        Assert.NotNull(reopened.Scalar($"SELECT ended_at FROM session WHERE session_id = '{cutOff}'"));
        Assert.Equal("fixed", reopened.Scalar($"SELECT outcome FROM session WHERE session_id = '{finished}'"));
        Assert.Equal(0, reopened.CloseUnfinished());
    }

    [Fact]
    public void StartedAt_IsTheFlowStart_NotWhenTheRowWasWritten()
    {
        using var db = new SessionLog(_path);
        var id = db.Start("camera", new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc));
        Assert.Equal("2026-11-11T09:00:00.000Z", db.Scalar($"SELECT started_at FROM session WHERE session_id = '{id}'"));
    }

    [Fact]
    public void SchemaRejectsUnknownSymptom()
    {
        using var db = new SessionLog(_path);
        Assert.Throws<SqliteException>(() => db.Start("fax"));
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

    [Fact]
    public void NoFaultFound_IsStored()
    {
        using var db = new SessionLog(_path);
        var id = db.Start("network");
        db.End(id, Outcome.NoFaultFound);
        Assert.Equal("no_fault_found", db.Scalar($"SELECT outcome FROM session WHERE session_id = '{id}'"));
    }

    [Fact]
    public void OldDatabase_IsMigrated_KeepingRows()
    {
        // A session.db created by the earlier app: its CHECK doesn't know 'no_fault_found'.
        using (var old = new SqliteConnection($"Data Source={_path}"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE session (session_id TEXT PRIMARY KEY,
                  started_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), ended_at TEXT,
                  symptom TEXT NOT NULL CHECK (symptom IN ('audio','camera','network','other')),
                  outcome TEXT CHECK (outcome IN ('fixed','not_fixed','probable_hardware','abandoned')),
                  app_version TEXT NOT NULL, playbook_version TEXT NOT NULL);
                CREATE TABLE step_log (step_id INTEGER PRIMARY KEY AUTOINCREMENT,
                  session_id TEXT NOT NULL REFERENCES session(session_id), playbook_step TEXT NOT NULL, action_id TEXT NOT NULL,
                  tier INTEGER NOT NULL, proposed_by TEXT NOT NULL, approved INTEGER NOT NULL DEFAULT 0, approved_at TEXT,
                  started_at TEXT, finished_at TEXT, result TEXT, user_confirmed_fixed INTEGER, error TEXT);
                INSERT INTO session (session_id, symptom, outcome, app_version, playbook_version) VALUES ('old1', 'audio', 'fixed', '1', '0.1.0');
                INSERT INTO step_log (session_id, playbook_step, action_id, tier, proposed_by) VALUES ('old1', 'unmute', 'audio.unmute_and_set_volume', 1, 'playbook');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var db = new SessionLog(_path);
        db.End(db.Start("camera"), Outcome.NoFaultFound);
        Assert.Equal("fixed", db.Scalar("SELECT outcome FROM session WHERE session_id = 'old1'"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM session WHERE outcome = 'no_fault_found'"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM step_log WHERE session_id = 'old1'"));
        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check"));
        db.End(db.Start("printer"), Outcome.Fixed); // new problem areas are accepted after the migration
        Assert.Throws<SqliteException>(() => db.Start("fax")); // other CHECKs still enforced
    }

    [Fact]
    public void V010Database_GainsNewAreas_KeepingRows()
    {
        // session.db from v0.1.0: knows no_fault_found but not the new problem areas.
        using (var db = new SessionLog(_path)) db.End(db.Start("audio"), Outcome.Fixed);
        using (var old = new SqliteConnection($"Data Source={_path}"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = OFF; BEGIN; CREATE TABLE s2 AS SELECT * FROM session; DROP TABLE session; " +
                "CREATE TABLE session (session_id TEXT PRIMARY KEY, started_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), " +
                "ended_at TEXT, symptom TEXT NOT NULL CHECK (symptom IN ('audio','camera','network','other')), " +
                "outcome TEXT CHECK (outcome IN ('fixed','not_fixed','probable_hardware','abandoned','no_fault_found')), " +
                "app_version TEXT NOT NULL, playbook_version TEXT NOT NULL); INSERT INTO session SELECT * FROM s2; DROP TABLE s2; COMMIT;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var reopened = new SessionLog(_path);
        foreach (var area in new[] { "microphone", "bluetooth", "printer", "display", "storage", "update" })
            reopened.End(reopened.Start(area), Outcome.NoFaultFound);
        Assert.Equal(7L, reopened.Scalar("SELECT COUNT(*) FROM session"));
    }

    private sealed class Host : IFixHost
    {
        private int _tests;
        public Task<bool> ConfirmAsync(Rung rung) => Task.FromResult(true);
        public Task<(bool Ok, string Detail)> RunAsync(Rung rung) => Task.FromResult((true, ""));
        public Task<bool> TestAsync() => Task.FromResult(_tests++ == 1);
        public Task<bool> SettleAsync(Rung rung, bool keep) => Task.FromResult(true);
    }
}
