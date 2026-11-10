namespace AutoFixScout.Probes;

/// <summary>
/// Test double for <see cref="IAudioProbe"/> that returns a pre-computed state.
/// Use during development to simulate probe results without actual system access.
/// </summary>
public sealed class FakeAudioProbe(AudioState? state = default) : IAudioProbe
{
    public AudioState Read()
    {
        return state ?? new AudioState();
    }
}
