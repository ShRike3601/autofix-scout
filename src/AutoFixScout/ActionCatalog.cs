namespace AutoFixScout;

public sealed class StepAction
{
    public string Id { get; set; } = "";
    public RiskTier Tier { get; set; }
    public bool AdminRequired { get; set; }
    public string? UndoInfo { get; set; }
}

public static class ActionCatalog
{
    public Dictionary<string, StepAction> ActionsByStepId => new()
    {
        ["Audio.Service.Restart"] = ActionContext.AudioServiceRestart,
        ["Camera.Driver.Update"] = ActionContext.CameraDriverUpdate,
        ["WiFi.Reconnect"] = ActionContext.WiFiReconnect
    };

    public StepAction? Get(string stepId) => ActionsByStepId.GetValueOrDefault(stepId);
}

public static class RiskTier
{
    public static readonly int T0 = 0; // Safe
    public static readonly int T1 = 1; // Moderate
    public static readonly int T2 = 2; // Requires approval
    public static readonly int T3 = 3; // Admin/Reboot

    public static bool IsTier(Tier tier, int maxTier) => tier <= maxTier;
}

public enum Tier { T0, T1, T2, T3 }
