using System.Globalization;

namespace MindAttic.Capture;

/// <summary>Output naming: R:\MindAttic\Capture\{RepoName}\{timestamp}_{RepoName}\{timestamp}_{RepoName}_{Kind}.{ext}.</summary>
public static class CaptureNaming
{
    /// <summary>
    /// UTC, ISO 8601-flavored with ':' replaced by '.' (Windows file names cannot contain ':') and a
    /// trailing 'Z', to the second: 2026.10.13T04.49.12Z. One session's files all share this exact
    /// string, so folder and file names agree without needing a separate per-file timestamp.
    /// </summary>
    public static string Timestamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy.MM.dd'T'HH.mm.ss'Z'", CultureInfo.InvariantCulture);

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
