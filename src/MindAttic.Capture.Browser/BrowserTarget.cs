using System.IO;
using System.Text;

namespace MindAttic.Capture.Browser;

/// <summary>
/// What the browser opens: a URL, or a local site folder/file served from a virtual https host
/// (WebView2 folder mapping), so static sites behave as if deployed.
/// </summary>
internal sealed record BrowserTarget(string Url, string? SiteFolder, string? VirtualHost, string AppName)
{
    public const string Home = "https://mindattic.com/";

    /// <summary>
    /// Parses the command line: the first plain argument is a URL, folder or .htm/.html file; --host overrides
    /// the virtual host name. Words consumed by -capture / -capture-config are skipped.
    /// </summary>
    public static BrowserTarget Parse(IReadOnlyList<string> args, Func<string, bool>? directoryExists = null, Func<string, bool>? fileExists = null)
    {
        directoryExists ??= Directory.Exists;
        fileExists ??= File.Exists;
        string? target = null, host = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            var name = a.TrimStart('-', '/').ToLowerInvariant();
            if (a.StartsWith('-') && name.StartsWith("capture-config", StringComparison.Ordinal))
            {
                if (!a.Contains('=')) i++;
                continue;
            }
            if (a.StartsWith('-') && name.StartsWith("capture", StringComparison.Ordinal))
            {
                while (i + 1 < args.Count && CaptureArgs.IsModeWord(args[i + 1])) i++;
                continue;
            }
            if (a.StartsWith("--host", StringComparison.OrdinalIgnoreCase))
            {
                host = a.Contains('=') ? a[(a.IndexOf('=') + 1)..] : i + 1 < args.Count ? args[++i] : null;
                continue;
            }
            if (a.StartsWith('-')) continue;
            target ??= a;
        }

        if (target is null) return new BrowserTarget(Home, null, null, "MindAttic.Browser");

        if (directoryExists(target))
        {
            var folder = Path.GetFullPath(target);
            var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            var vhost = host ?? HostName(name);
            var index = new[] { "index.htm", "index.html" }.FirstOrDefault(f => fileExists(Path.Combine(folder, f))) ?? "";
            return new BrowserTarget($"https://{vhost}/{index}", folder, vhost, SubjectName(folder, name));
        }
        if (fileExists(target) && Path.GetExtension(target).ToLowerInvariant() is ".htm" or ".html")
        {
            var full = Path.GetFullPath(target);
            var folder = Path.GetDirectoryName(full)!;
            var name = Path.GetFileName(folder);
            var vhost = host ?? HostName(name);
            return new BrowserTarget($"https://{vhost}/{Uri.EscapeDataString(Path.GetFileName(full))}", folder, vhost, SubjectName(folder, name));
        }
        var url = NormalizeUrl(target);
        return new BrowserTarget(url, null, null, Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host : "MindAttic.Browser");
    }

    private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "dist", "build", "out", "public", "wwwroot", "www", "site", "html", "docs", "src", "static",
    };

    /// <summary>
    /// What the captures are named after: the site folder ("mindattic.com", so every file says which site it shows),
    /// or the repository when the folder name says nothing (dist, wwwroot, ...).
    /// </summary>
    internal static string SubjectName(string folder, string name) =>
        GenericFolders.Contains(name) ? CaptureNaming.RepoName(folder) ?? name : name;

    /// <summary>Address-bar text to a URL: keeps schemes, adds https:// to host-like text, searches otherwise.</summary>
    public static string NormalizeUrl(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return Home;
        if (Uri.TryCreate(t, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https" or "file" or "about" or "edge") return abs.ToString();
        if (!t.Contains(' ') && (t.Contains('.') || t.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)))
        {
            var scheme = t.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || t.StartsWith("127.0.0.1", StringComparison.Ordinal) ? "http://" : "https://";
            return scheme + t;
        }
        return "https://www.bing.com/search?q=" + Uri.EscapeDataString(t);
    }

    /// <summary>A valid virtual host name from a folder name: "mindattic.com" → "mindattic.com.local".</summary>
    internal static string HostName(string folder)
    {
        var sb = new StringBuilder();
        foreach (var c in folder.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) && c < 128 || c is '.' or '-' ? c : '-');
        var s = sb.ToString().Trim('-', '.');
        return (s.Length == 0 ? "site" : s) + ".local";
    }
}
