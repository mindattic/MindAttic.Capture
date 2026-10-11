using System.Globalization;

namespace MindAttic.Capture;

/// <summary>Output naming: R:\MindAttic\Capture\{RepoName}\{timestamp}.{ext}.</summary>
public static class CaptureNaming
{
    /// <summary>
    /// ISO 8601 local time with ':' replaced by '.' (Windows file names cannot contain ':'),
    /// to the millisecond: 2026-10-11T14.29.20.123.
    /// </summary>
    public static string Timestamp(DateTimeOffset time) =>
        time.ToString("yyyy-MM-dd'T'HH.mm.ss.fff", CultureInfo.InvariantCulture);

    /// <summary>
    /// The name of the git repository containing <paramref name="path"/> (the nearest folder holding a .git
    /// directory or file), or null when it is not inside one. Apps run from bin\Debug, Unity projects and
    /// local site folders all resolve to their repo, so every repo gets its own capture folder.
    /// </summary>
    public static string? RepoName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(path!));
            if (!dir.Exists && dir.Parent is not null) dir = dir.Parent;
            for (; dir is not null; dir = dir.Parent)
            {
                var git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return dir.Name;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return null;
    }
}
