using System.Text.Json;

namespace AutoFixScout.Updates;

/// <summary>
/// Remembers where the main window was (%LOCALAPPDATA%\AutoFixScout\window.json), so AutoFix Scout opens where it was
/// closed. A position that is no longer on any screen (monitor unplugged) is not used.
/// </summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized)
{
    public static string FileIn(string dir) => Path.Combine(dir, "window.json");

    public static WindowPlacement? Load(string? dir = null)
    {
        try
        {
            var file = FileIn(dir ?? AppPaths.DataDir);
            return File.Exists(file) ? JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(file)) : null;
        }
        catch (Exception) { return null; }
    }

    public void Save(string? dir = null)
    {
        try
        {
            dir ??= AppPaths.DataDir;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FileIn(dir), JsonSerializer.Serialize(this));
        }
        catch (Exception) { /* the next start simply uses the default position */ }
    }

    /// <summary>
    /// The placement to use inside the visible desktop (virtual screen, in device-independent pixels), or null when it
    /// is unusable: too small, or less than 120 x 80 of its title area visible.
    /// </summary>
    public WindowPlacement? FitTo(double screenLeft, double screenTop, double screenWidth, double screenHeight, double minWidth, double minHeight)
    {
        if (double.IsNaN(Left) || double.IsNaN(Top) || Width < minWidth || Height < minHeight) return null;
        var visibleX = Math.Min(Left + Width, screenLeft + screenWidth) - Math.Max(Left, screenLeft);
        var visibleY = Math.Min(Top + 80, screenTop + screenHeight) - Math.Max(Top, screenTop);
        if (visibleX < 120 || visibleY < 40) return null;
        return this with { Width = Math.Min(Width, screenWidth), Height = Math.Min(Height, screenHeight) };
    }
}
