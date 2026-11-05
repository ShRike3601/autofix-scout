namespace AutoFixScout.UI;

public class StepAction
{
    public string Id { get; set; } = "";
    public int Tier { get; set; }
    public bool AdminRequired { get; set; }
    public string? UndoInfo { get; set; }
}

public static class ActionContext
{
    public static readonly StepAction AudioServiceRestart = new()
    {
        Id = "Audio.Service.Restart",
        Tier = 0,
        AdminRequired = false,
        UndoInfo = "Restarted audio service"
    };

    public static readonly StepAction CameraDriverUpdate = new()
    {
        Id = "Camera.Driver.Update",
        Tier = 2,
        AdminRequired = true,
        UndoInfo = null
    };

    public static readonly StepAction WiFiReconnect = new()
    {
        Id = "WiFi.Reconnect",
        Tier = 0,
        AdminRequired = false,
        UndoInfo = "Reconnected to Wi-Fi"
    };
}
