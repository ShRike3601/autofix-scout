namespace AutoFixScout;

/// <summary>
/// Symptom type for session tracking (moved from DatabaseModels).
/// </summary>
public enum Symptom { Audio, Camera, Network, Other }

/// <summary>
/// Outcome type for session tracking (moved from DatabaseModels).
/// NoFaultFound: the check found nothing wrong and the real test worked, so nothing was offered or changed.
/// </summary>
public enum Outcome { Fixed, NotFixed, ProbableHardware, Abandoned, NoFaultFound }
