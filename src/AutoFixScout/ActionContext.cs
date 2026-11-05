namespace AutoFixScout;

public static class ActionContext
{
    public static readonly StepAction AudioServiceRestart = new()
    {
        Id = "Audio.Service.Restart",
        Tier = RiskTier.T0,
        AdminRequired = false,
        UndoInfo = "Restarted audio service"
    };

    public static readonly StepAction CameraDriverUpdate = new()
    {
        Id = "Camera.Driver.Update",
        Tier = RiskTier.T2,
        AdminRequired = true,
        UndoInfo = null  // No undo for driver updates in MVP
    };

    public static readonly StepAction WiFiReconnect = new()
    {
        Id = "WiFi.Reconnect",
        Tier = RiskTier.T0,
        AdminRequired = false,
        UndoInfo = "Reconnected to Wi-Fi"
    };
}
