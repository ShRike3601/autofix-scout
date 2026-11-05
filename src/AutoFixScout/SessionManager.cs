namespace AutoFixScout;

public sealed class SessionManager : IDisposable
{
    private readonly Database _db = new();
    
    public bool HasDatabase => true;
    public string? LastError => null;
    
    public StepResult CreateSession(string symptomType, DateTime startedAt)
    {
        return new StepResult
        {
            ActionId = "Session.Create",
            Tier = RiskTier.T0,
            SessionId = Guid.NewGuid().ToString("N"),
            ProposedBy = "Orchestrator",
            Approved = true,
            ResultType = "Created"
        };
    }

    public StepResult SaveSession(string sessionId, DateTime startedAt) => CreateSession(sessionId, startedAt);

    public void Dispose() => _db.Dispose();
}

public sealed class Database : IDisposable
{
    private SqliteConnection? _connection;
    
    public bool HasDatabase => false;
    public string? LastError => null;

    private SqliteConnection Connection
    {
        get
        {
            if (_connection == null)
            {
                var connString = DataSource.GetFilename();
                _connection = new SqliteConnection(connString);
                _connection.Open();
            }
            return _connection;
        }
    }

    public int Insert(string tableName, object values)
    {
        if (!HasDatabase) throw new InvalidOperationException("Database not initialized");
        return 0; // MVP stub
    }

    public void Dispose() => _connection?.Dispose();
}
