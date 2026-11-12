namespace AutoFixScout.Probes;

/// <summary>Status: Running, Stopped, NotFound, Unknown. StartType: Automatic, Manual, Disabled, NotFound, Unknown.</summary>
public sealed record UpdateServiceInfo(string Name, string Status, string StartType)
{
    public bool Disabled => StartType == "Disabled";
}

/// <summary>probe.update: the services Windows Update needs, a waiting restart, the last successful install and disk space.</summary>
public sealed class UpdateState
{
    /// <summary>wuauserv, BITS and CryptSvc, in that order.</summary>
    public IReadOnlyList<UpdateServiceInfo> Services { get; set; } = new List<UpdateServiceInfo>();
    public Tri RebootPending { get; set; } = Tri.Unknown;
    /// <summary>Last successful update installation (UTC); null = unknown (not readable without admin, or never).</summary>
    public DateTime? LastInstallUtc { get; set; }
    /// <summary>Free space on the system drive, from the same reading the storage check uses; null = unknown.</summary>
    public long? FreeBytes { get; set; }
    public long? TotalBytes { get; set; }
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}

public interface IUpdateProbe { UpdateState Read(); }
