namespace AutoFixScout.UI;

public interface IActionContext
{
    StepAction AudioServiceRestart { get; }
    StepAction CameraDriverUpdate { get; }
    StepAction WiFiReconnect { get; }
}
