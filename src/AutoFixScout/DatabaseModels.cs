namespace AutoFixScout;

/// <summary>
/// Database models (section 12).
/// </summary>
public enum StepResult { Success, Failed, Undone, Skipped }

public sealed class Session
{
    public string SessionId { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public Symptom SymptomType { get; set; }  // renamed to avoid confusion with property
    public Outcome? Outcome { get; set; }
    public string AppVersion { get; set; } = "";
    public string PlaybookVersion { get; set; } = "";
}

public sealed class StepLog
{
    public int StepId { get; set; }
    public string SessionId { get; set; } = "";
    public string PlaybookStep { get; set; } = "";
    public string ActionId { get; set; } = "";
    public int Tier { get; set; }
    public string ProposedBy { get; set; } = "";  // playbook, llm, helper
    public bool Approved { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public StepResult? Result { get; set; }
    public bool? UserConfirmedFixed { get; set; }
    public string? Error { get; set; }
    /// <summary>Not a column: what the action reported (e.g. prior volume), shown in the helper report.</summary>
    public string? Detail { get; set; }

    public override string ToString() =>
        $"{PlaybookStep} ({ActionId}, T{Tier}): {(Approved ? Result?.ToString() ?? "unknown" : "declined")}" +
        (UserConfirmedFixed is { } f ? $", fixed it: {(f ? "yes" : "no")}" : "") +
        (string.IsNullOrEmpty(Detail) ? "" : $" -- {Detail}");
}

public sealed class UndoRecord
{
    public int UndoId { get; set; }
    public int StepId { get; set; }
    public string Kind { get; set; } = "";  // "driver", "service", "device", etc.
    public string Payload { get; set; } = "";  // serialized undo payload
    public bool Applied { get; set; }
    public DateTime? AppliedAt { get; set; }
}

public sealed class DeviceSnapshot
{
    public int SnapshotId { get; set; }
    public string SessionId { get; set; } = "";
    public DateTime TakenAt { get; set; }
    public string DeviceClass { get; set; } = "";  // "Audio", "Camera", "Network"
    public string HardwareIds { get; set; } = "";
    public string? DriverInf { get; set; }
    public string? DriverVersion { get; set; }
    public string? DriverProvider { get; set; }
    public int? ProblemCode { get; set; }
}

public sealed class Setting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
