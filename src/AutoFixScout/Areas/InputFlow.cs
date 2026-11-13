using AutoFixScout.Probes;
using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>"Keyboard, mouse or touchpad is not working". Real test: the person's answer.</summary>
public static class InputFlow
{
    public const string Topic = "input";
    public const string What = "Keyboard, mouse and touchpad";
    public const string TestQuestion = "Do your keyboard, mouse and touchpad work now?";

    private static readonly IInputProbe Probe = new RealInputProbe();

    public static Task<Diagnosis> DiagnoseAsync() => DiagnoseAsync(false);

    /// <summary>With typingFocus the layout findings come first (the person reported wrong characters).</summary>
    public static Task<Diagnosis> DiagnoseAsync(bool typingFocus) => Task.Run(() =>
    {
        var s = Read();
        s.TypingFocus = typingFocus;
        var f = InputTriage.Evaluate(s);
        return new Diagnosis(s, f, InputTriage.Ladder(s, f), InputTriage.ProbablyHardware(s), InputTriage.NotFixedHint(f));
    });

    /// <summary>Read-only, no admin; failures are in <see cref="InputState.ProbeErrors"/>.</summary>
    public static InputState Read() => Probe.Read();
}
