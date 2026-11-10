namespace AutoFixScout;

public sealed class ActionStepResult
{
    public string ActionId { get; set; } = "";
    public int Tier { get; set; }
    public string SessionId { get; set; } = "";
    public string ProposedBy { get; set; } = "";
    public bool Approved { get; set; }
    public string ResultType { get; set; } = "";
}
