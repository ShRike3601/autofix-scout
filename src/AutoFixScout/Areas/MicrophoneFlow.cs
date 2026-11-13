using AutoFixScout.Probes;
using AutoFixScout.Triage;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AutoFixScout.Areas;

/// <summary>"My microphone is not working": check, ladder and an automatic test that listens for the person's voice.</summary>
public static class MicrophoneFlow
{
    public const string Topic = "microphone";
    public const string What = "microphone";

    /// <summary>Shown before AutoTestAsync starts listening, so the person knows to speak.</summary>
    public const string TestPrompt = "AutoFix Scout will now listen to your microphone for a few seconds. " +
        "Please say a few words after selecting \"Continue\".";

    /// <summary>Peak level (0..1) that counts as a voice. Room noise on a laptop microphone stays well below it.</summary>
    public const float VoicePeak = 0.03f;

    public static Task<Diagnosis> DiagnoseAsync() => Task.Run(() =>
    {
        var s = new RealMicrophoneProbe().Read();
        var f = MicrophoneTriage.Evaluate(s);
        return new Diagnosis(s, f, MicrophoneTriage.Ladder(s, f), MicrophoneTriage.ProbablyHardware(s), MicrophoneTriage.NotFixedHint(f));
    });

    /// <summary>
    /// Records about 2.5 seconds from the selected microphone and passes if the loudest moment is above VoicePeak.
    /// Nothing is saved: only the peak level is kept. Never throws; any failure counts as "not working".
    /// </summary>
    public static Task<bool> AutoTestAsync() => Task.Run(() =>
    {
        try { return PeakLevel(TimeSpan.FromSeconds(2.5)) > VoicePeak; }
        catch (Exception) { return false; }
    });

    /// <summary>Loudest sample (0..1) heard on the default input during the given time.</summary>
    internal static float PeakLevel(TimeSpan duration)
    {
        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        using var capture = new WasapiCapture(dev, false, 100);
        var format = capture.WaveFormat;
        float peak = 0;
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        capture.DataAvailable += (_, e) => peak = Math.Max(peak, Peak(e.Buffer, e.BytesRecorded, format));
        capture.RecordingStopped += (_, e) => { error = e.Exception; done.Set(); };
        capture.StartRecording();
        Thread.Sleep(duration);
        capture.StopRecording();
        if (!done.Wait(TimeSpan.FromSeconds(3)) || error != null) throw error ?? new TimeoutException("capture did not stop");
        return peak;
    }

    internal static float Peak(byte[] buffer, int bytes, WaveFormat format)
    {
        float peak = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32))
            for (var i = 0; i + 4 <= bytes; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i)));
        else if (format.BitsPerSample == 16)
            for (var i = 0; i + 2 <= bytes; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, i) / 32768f));
        else if (format.BitsPerSample == 24)
            for (var i = 0; i + 3 <= bytes; i += 3) peak = Math.Max(peak, Math.Abs(((buffer[i] << 8) | (buffer[i + 1] << 16) | (buffer[i + 2] << 24)) / 2147483648f));
        return float.IsFinite(peak) ? Math.Min(peak, 1f) : 0f;
    }
}
