using Microsoft.Data.Sqlite;

namespace AutoFixScout;

public sealed class Simulator
{
    public static StepResult FakeExecute(StepAction action, int sessionId)
    {
        return new StepResult
        {
            ActionId = action.Id,
            Tier = action.Tier,
            SessionId = sessionId,
            ProposedBy = "Test",
            Approved = true,
            ResultType = "Fake"
        };
    }

    public static SessionContext FakeSession(int id) => new(id);
}

public sealed class SessionContext
{
    public int Id { get; }
    public DateTime StartedAt { get; set; }
    public List<StepLog> Steps { get; } = new();
    
    public SessionContext(int id)
    {
        Id = id;
        StartedAt = DateTime.UtcNow;
    }

    public StepLog Log(StepAction action, string? error = null)
    {
        var step = new StepLog
        {
            ActionId = action.Id,
            Tier = (int)action.Tier,
            ProposedBy = "Orchestrator",
            Approved = true,
            Error = error
        };
        Steps.Add(step);
        return step;
    }
}

public sealed class SymptomType
{
    public int Id { get; set; }
    public string Description { get; set; } = "";
}
