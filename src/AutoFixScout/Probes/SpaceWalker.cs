using System.Runtime.InteropServices;

namespace AutoFixScout.Probes;

/// <summary>A size that may be a lower bound ("at least") because parts could not be read or the walk was cut short.</summary>
public sealed record SpaceItem(long Bytes, bool AtLeast);

/// <summary>A folder directly in the person's profile with its size. Only sizes are kept, never file names.</summary>
public sealed record ProfileFolder(string Name, long Bytes, bool AtLeast);

/// <summary>Bounded, read-only, best-effort folder measuring. Never follows links, skips what is denied, never throws.</summary>
public static class SpaceWalker
{
    public const int MaxEntries = 400_000;
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(6);

    public sealed record Result(long Bytes, int Files, int OldFiles, bool Complete, bool TopReadable);

    /// <summary>Size of a tree. Complete is false when a folder was denied or the entry or time bound was reached.</summary>
    public static Result Measure(string root, TimeSpan budget, DateTime? oldCutoffUtc = null)
    {
        var stop = System.Diagnostics.Stopwatch.StartNew();
        long bytes = 0;
        int files = 0, old = 0, entries = 0;
        var complete = true;
        var topReadable = false;
        var pending = new Stack<string>();
        pending.Push(root);
        var first = true;
        while (pending.Count > 0)
        {
            if (stop.Elapsed > budget || entries > MaxEntries) { complete = false; break; }
            var dir = pending.Pop();
            try
            {
                foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    entries++;
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (e is DirectoryInfo d) { pending.Push(d.FullName); continue; }
                    try
                    {
                        var f = (FileInfo)e;
                        bytes += f.Length;
                        files++;
                        if (oldCutoffUtc is { } c && f.LastWriteTimeUtc < c) old++;
                    }
                    catch (Exception) { complete = false; }
                }
                if (first) topReadable = true;
            }
            catch (Exception) { complete = false; }
            first = false;
        }
        return new Result(bytes, files, old, complete, topReadable);
    }

    /// <summary>Null when the folder does not exist or its top level cannot be listed (then the size is Unknown).</summary>
    public static SpaceItem? Folder(string path, TimeSpan budget)
    {
        try
        {
            if (!Directory.Exists(path)) return null;
            var r = Measure(path, budget);
            return r.TopReadable ? new SpaceItem(r.Bytes, !r.Complete) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Size of a file by metadata only; falls back to a directory listing (FindFirstFile) when the direct call is denied. Zero = absent.</summary>
    public static SpaceItem? FileSize(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists) return new SpaceItem(fi.Length, false);
        }
        catch (Exception) { /* try the listing */ }
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            var hit = new DirectoryInfo(dir).EnumerateFiles(Path.GetFileName(path)).FirstOrDefault();
            return new SpaceItem(hit?.Length ?? 0, false);
        }
        catch (Exception) { return null; }
    }

    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-CA7E01DA61C0");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    public static string? DownloadsPath()
    {
        try
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var p) == 0)
            {
                var s = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch (Exception) { /* fall back */ }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>The three largest visible folders directly in the profile (hidden and system folders such as AppData are skipped).</summary>
    public static IReadOnlyList<ProfileFolder>? BigProfileFolders(string profile, TimeSpan perFolder, TimeSpan total)
    {
        try
        {
            var stop = System.Diagnostics.Stopwatch.StartNew();
            var list = new List<ProfileFolder>();
            foreach (var d in new DirectoryInfo(profile).EnumerateDirectories())
            {
                if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                var left = total - stop.Elapsed;
                if (left <= TimeSpan.Zero) break;
                var r = Measure(d.FullName, left < perFolder ? left : perFolder);
                if (r.TopReadable) list.Add(new ProfileFolder(d.Name, r.Bytes, !r.Complete));
            }
            return list.OrderByDescending(x => x.Bytes).Take(3).ToList();
        }
        catch (Exception) { return null; }
    }
}
