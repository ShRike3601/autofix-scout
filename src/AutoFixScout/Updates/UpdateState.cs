using System.Text.Json;

namespace AutoFixScout.Updates;

/// <summary>What the last update check found, kept in %LOCALAPPDATA%\AutoFixScout\update.json.</summary>
public sealed class UpdateState
{
    public DateTime? LastCheckUtc { get; set; }
    /// <summary>The version whose "What's new" was last shown (see <see cref="WhatsNew"/>).</summary>
    public string? LastSeenVersion { get; set; }
    /// <summary>The newest release found by the last successful check, even if it is not newer than this build.</summary>
    public UpdateInfo? Latest { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FileIn(string dir) => Path.Combine(dir, "update.json");

    public static UpdateState Load(string dir)
    {
        try
        {
            var file = FileIn(dir);
            return File.Exists(file) ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(file)) ?? new() : new();
        }
        catch (Exception) { return new(); }
    }

    public void Save(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(FileIn(dir), JsonSerializer.Serialize(this, Json));
        }
        catch (Exception) { /* a file that cannot be written only means the check runs again next time */ }
    }
}
