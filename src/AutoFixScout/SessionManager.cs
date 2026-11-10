using Microsoft.Data.Sqlite;
using System.Diagnostics.CodeAnalysis;

namespace AutoFixScout;

public sealed class SessionManager : IDisposable
{
    private readonly Database _db = new();
    private SqliteConnection? _connection;
    
    public bool HasDatabase => true;
    public string? LastError => null;

    public ActionStepResult CreateSession(string symptomType, DateTime startedAt)
    {
        return new ActionStepResult
        {
            ActionId = "Session.Create",
            Tier = RiskTier.T0,
            SessionId = Guid.NewGuid().ToString("N"),
            ProposedBy = "Orchestrator",
            Approved = true,
            ResultType = "Created"
        };
    }

    public ActionStepResult SaveSession(string sessionId, DateTime startedAt) => CreateSession(sessionId, startedAt);

    public void Dispose() => _connection?.Dispose();
}

public sealed class Database : IDisposable
{
    private SqliteConnection? _connection;
    
    public bool HasDatabase => false;
    public string? LastError => null;

    public int Insert(string tableName, object values)
    {
        return 0; // MVP stub
    }

    public void Dispose() => _connection?.Dispose();
}
