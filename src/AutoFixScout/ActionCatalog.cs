namespace AutoFixScout;

public sealed class StepAction
{
    public string Id { get; set; } = "";
    public int Tier { get; set; }
    public bool AdminRequired { get; set; }
    public string? UndoInfo { get; set; }
}

public static class ActionCatalog
{
    private static readonly Dictionary<string, StepAction> _actionsByStepId = new()
    {
        ["Audio.Service.Restart"] = ActionContext.AudioServiceRestart,
        ["Camera.Driver.Update"] = ActionContext.CameraDriverUpdate,
        ["WiFi.Reconnect"] = ActionContext.WiFiReconnect
    };

    public static StepAction? Get(string stepId) => _actionsByStepId.GetValueOrDefault(stepId);
}

public static class RiskTier
{
    public const int T0 = 0; // Safe
    public const int T1 = 1; // Moderate
    public const int T2 = 2; // Requires approval
    public const int T3 = 3; // Admin/Reboot

    public static bool IsTier(int tier, int maxTier) => tier <= maxTier;
}
