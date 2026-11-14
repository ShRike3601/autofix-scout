using System.Runtime.InteropServices;

namespace AutoFixScout.Areas;

/// <summary>
/// Non-admin clean-up actions for the storage ladder. Only the person's own %TEMP% folder and Recycle Bin; nothing
/// that needs admin, nothing outside those places.
/// </summary>
public static class StorageCleanup
{
    /// <summary>
    /// Deletes files older than the cutoff inside <paramref name="tempRoot"/> only. Junctions and links are never
    /// followed or deleted, files in use are skipped, then empty old folders are removed. Returns bytes freed.
    /// </summary>
    public static (long Freed, int Deleted, int Skipped) ClearTemp(string tempRoot, DateTime cutoffUtc)
    {
        Activity.Report(ActivityKind.Change, "Deleting temporary files older than 24 hours from the temporary folder (links are never followed, files in use are skipped)");
        var root = Path.GetFullPath(tempRoot);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        long freed = 0;
        int deleted = 0, skipped = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts).ToList())
        {
            try
            {
                if (!Path.GetFullPath(f.FullName).StartsWith(root, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                if (f.LastWriteTimeUtc >= cutoffUtc) continue;
                var len = f.Length;
                if (f.IsReadOnly) { skipped++; continue; }
                f.Delete();
                freed += len;
                deleted++;
            }
            catch (Exception) { skipped++; } // in use or no access: leave it
        }
        // Deepest folders first, so a folder emptied above can go too.
        foreach (var d in new DirectoryInfo(root).EnumerateDirectories("*", opts).OrderByDescending(d => d.FullName.Length).ToList())
        {
            try
            {
                if (d.LastWriteTimeUtc < cutoffUtc && !d.EnumerateFileSystemInfos().Any()) d.Delete(false);
            }
            catch (Exception) { /* in use or not empty */ }
        }
        return (freed, deleted, skipped);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    private const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;

    /// <summary>Empties the Recycle Bin on all drives. The person has already confirmed that this cannot be undone.</summary>
    public static (bool Ok, string Detail) EmptyRecycleBin()
    {
        Activity.Report(ActivityKind.Change, "Emptying the Recycle Bin on all drives (SHEmptyRecycleBin)");
        var before = Probes.RealStorageProbe.RecycleBinSize();

        var hr = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
        var after = Probes.RealStorageProbe.RecycleBinSize();
        var freed = before is { } b && after is { } a ? Math.Max(0, b - a) : (long?)null;
        // An already empty bin returns an error code on some builds; what counts is that it is empty now.
        var ok = hr == 0 || after == 0;
        return (ok, $"SHEmptyRecycleBin hr=0x{hr:X8}; freed {(freed is { } x ? x + " bytes" : "unknown")}");
    }
}
