namespace AutoFixScout;

public sealed class Orchestrator : IDisposable
{
    private readonly SessionManager _manager;
    private readonly ActionCatalog _catalog;
    private int? _nextId;
    
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public bool IsConnected => true;

    public Orchestrator(IProbes probes = null)
    {
        _manager = new SessionManager();
        ActionCatalog.Initialize();
        _catalog = ActionCatalog.Instance;
    }

    public StepResult ExecuteAction(StepAction action, int sessionId)
    {
        var context = Simulator.FakeSession(sessionId);
        context.Log(action, null);
        return Simulator.FakeExecute(action, sessionId);
    }

    public Task<TriagedResult> TriageAudioAsync() => Task.FromResult(new TriagedResult { Symptom = "Sound", Findings = new List<Finding>() });
    public Task<TriagedResult> TriageCameraAsync() => Task.FromResult(new TriagedResult { Symptom = "Camera", Findings = new List<Finding>() });
    public Task<TriagedResult> TriageNetworkAsync() => Task.FromResult(new TriagedResult { Symptom = "WiFi", Findings = new List<Finding>() });

    public void Dispose() => _manager.Dispose();
}

public sealed class TriagedResult
{
    public string Symptom { get; set; } = "";
    public List<Finding> Findings { get; set; } = new();
}

public sealed class Finding
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
}
