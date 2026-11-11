using System.Reflection;
using Microsoft.Data.Sqlite;

namespace AutoFixScout;

/// <summary>Session log (spec 12). Local only; one row per session and per offered step.</summary>
public sealed class SessionLog : IDisposable
{
    // Spec 12 schema, verbatim apart from IF NOT EXISTS so it can run on every start,
    // and the added 'no_fault_found' outcome (owner decision 2026-11-11, found in real-hardware test step 1).
    private const string SessionTable = """
        CREATE TABLE IF NOT EXISTS session (
          session_id TEXT PRIMARY KEY,
          started_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
          ended_at TEXT,
          symptom TEXT NOT NULL CHECK (symptom IN ('audio','camera','network','other')),
          outcome TEXT CHECK (outcome IN ('fixed','not_fixed','probable_hardware','abandoned','no_fault_found')),
          app_version TEXT NOT NULL,
          playbook_version TEXT NOT NULL);

        """;

    private const string Schema = SessionTable + """
        CREATE TABLE IF NOT EXISTS step_log (
          step_id INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL REFERENCES session(session_id),
          playbook_step TEXT NOT NULL,
          action_id TEXT NOT NULL,
          tier INTEGER NOT NULL CHECK (tier BETWEEN 0 AND 3),
          proposed_by TEXT NOT NULL CHECK (proposed_by IN ('playbook','llm','helper')),
          approved INTEGER NOT NULL DEFAULT 0,
          approved_at TEXT,
          started_at TEXT,
          finished_at TEXT,
          result TEXT CHECK (result IN ('success','failed','undone','skipped')),
          user_confirmed_fixed INTEGER,
          error TEXT);
        CREATE TABLE IF NOT EXISTS undo_record (
          undo_id INTEGER PRIMARY KEY AUTOINCREMENT,
          step_id INTEGER NOT NULL REFERENCES step_log(step_id),
          kind TEXT NOT NULL,
          payload TEXT NOT NULL,
          applied INTEGER NOT NULL DEFAULT 0,
          applied_at TEXT);
        CREATE TABLE IF NOT EXISTS device_snapshot (
          snapshot_id INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL REFERENCES session(session_id),
          taken_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
          device_class TEXT NOT NULL,
          hardware_ids TEXT,
          driver_inf TEXT,
          driver_version TEXT,
          driver_provider TEXT,
          problem_code INTEGER);
        CREATE TABLE IF NOT EXISTS setting (
          key TEXT PRIMARY KEY,
          value TEXT NOT NULL);
        INSERT OR IGNORE INTO setting (key, value) VALUES
          ('telemetry_opt_in', '0'),
          ('llm_enabled', '0'),
          ('helper_approval_required_for_t2', '1');
        """;

    /// <summary>Bump when a ladder changes, so outcomes can be compared per version (spec 13).</summary>
    public const string PlaybookVersion = "0.1.0";

    private readonly SqliteConnection _db;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoFixScout", "session.db");

    public SessionLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        MigrateSessionOutcomes();
        Exec("PRAGMA foreign_keys = ON;");
        Exec(Schema);
    }

    /// <summary>
    /// A session table made before 'no_fault_found' existed has a CHECK that rejects it. SQLite can't change a CHECK,
    /// so the table is rebuilt with its rows kept. Runs with foreign keys off (step_log points at session).
    /// </summary>
    private void MigrateSessionOutcomes()
    {
        var sql = Scalar("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'session'") as string;
        if (sql is null || sql.Contains("no_fault_found")) return;
        Exec("PRAGMA foreign_keys = OFF;");
        Exec("BEGIN;" +
             SessionTable.Replace("EXISTS session (", "EXISTS session_new (") +
             "INSERT INTO session_new SELECT * FROM session; DROP TABLE session; ALTER TABLE session_new RENAME TO session; COMMIT;");
    }

    public string Start(string symptom)
    {
        var id = Guid.NewGuid().ToString("N");
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        Exec("INSERT INTO session (session_id, symptom, app_version, playbook_version) VALUES ($id, $symptom, $app, $pb)",
            ("$id", id), ("$symptom", symptom), ("$app", version), ("$pb", PlaybookVersion));
        return id;
    }

    public void AddSteps(string sessionId, IEnumerable<StepLog> steps)
    {
        foreach (var s in steps)
            Exec("""
                INSERT INTO step_log (session_id, playbook_step, action_id, tier, proposed_by, approved, approved_at,
                                      started_at, finished_at, result, user_confirmed_fixed, error)
                VALUES ($sid, $step, $action, $tier, $by, $approved, $approvedAt, $started, $finished, $result, $fixed, $error)
                """,
                ("$sid", sessionId), ("$step", s.PlaybookStep), ("$action", s.ActionId), ("$tier", s.Tier), ("$by", s.ProposedBy),
                ("$approved", s.Approved ? 1 : 0), ("$approvedAt", Iso(s.ApprovedAt)), ("$started", Iso(s.StartedAt)),
                ("$finished", Iso(s.FinishedAt)), ("$result", s.Result?.ToString().ToLowerInvariant()),
                ("$fixed", s.UserConfirmedFixed is { } f ? (f ? 1 : 0) : null), ("$error", s.Error));
    }

    public void End(string sessionId, Outcome outcome) =>
        Exec("UPDATE session SET ended_at = strftime('%Y-%m-%dT%H:%M:%fZ','now'), outcome = $o WHERE session_id = $id",
            ("$id", sessionId), ("$o", outcome switch
            {
                Outcome.Fixed => "fixed",
                Outcome.NotFixed => "not_fixed",
                Outcome.ProbableHardware => "probable_hardware",
                Outcome.NoFaultFound => "no_fault_found",
                _ => "abandoned",
            }));

    /// <summary>Spec 15: "delete my data".</summary>
    public void DeleteAll() => Exec("DELETE FROM undo_record; DELETE FROM step_log; DELETE FROM device_snapshot; DELETE FROM session;");

    internal object? Scalar(string sql)
    {
        using var cmd = new SqliteCommand(sql, _db);
        return cmd.ExecuteScalar();
    }

    private static string? Iso(DateTime? t) => t?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private void Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = new SqliteCommand(sql, _db);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
