using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtDlp;

/// <summary>One folder in a user's folder tree.</summary>
public class FolderEntry
{
    /// <summary>Gets or sets the full path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the path below the user's folder ("" for the user's folder itself).</summary>
    public string Relative { get; set; } = string.Empty;

    /// <summary>Gets or sets the user's folder it is in.</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Gets or sets the depth below the user's folder (0 for the folder itself).</summary>
    public int Depth { get; set; }

    /// <summary>Gets or sets the number of files in it and its sub folders.</summary>
    public int Files { get; set; }

    /// <summary>Gets or sets the size of its files and sub folders.</summary>
    public long Size { get; set; }
}

/// <summary>The result of a folder or move operation.</summary>
public class FolderResult
{
    /// <summary>Gets or sets the path the operation was for.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether it worked.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets the new path, when there is one.</summary>
    public string NewPath { get; set; } = string.Empty;

    /// <summary>Gets or sets why it did not work.</summary>
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Creates, renames, moves and deletes folders, and moves files, inside a user's own folders.
/// Callers check that every path is inside the user's folders; this class checks names, clashes and running downloads.
/// </summary>
public class FolderService
{
    private const int MaxDepth = 8;
    private const int MaxFolders = 3000;
    private readonly ILibraryManager _libraryManager;
    private readonly DownloadQueue _queue;
    private readonly ILogger<FolderService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FolderService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="queue">The download queue.</param>
    /// <param name="logger">Logger.</param>
    public FolderService(ILibraryManager libraryManager, DownloadQueue queue, ILogger<FolderService> logger)
    {
        _libraryManager = libraryManager;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>Lists a user's folder and every folder below it.</summary>
    /// <param name="root">The user's folder.</param>
    /// <returns>The folders, parents before their children, alphabetical.</returns>
    public List<FolderEntry> List(string root)
    {
        var list = new List<FolderEntry>();
        var top = new FolderEntry { Path = root, Relative = string.Empty, Root = root, Depth = 0 };
        list.Add(top);
        if (!Directory.Exists(root))
        {
            return list;
        }

        Walk(root, root, 1, list);
        Count(list);
        return list;
    }

    /// <summary>Creates a folder.</summary>
    /// <param name="parent">The folder to create it in (checked by the caller).</param>
    /// <param name="name">The new folder's name.</param>
    /// <returns>The result.</returns>
    public FolderResult Create(string parent, string? name)
    {
        var result = new FolderResult { Path = parent };
        if (CheckName(name) is { } bad)
        {
            result.Error = bad;
            return result;
        }

        var target = System.IO.Path.Combine(parent, name!.Trim());
        if (Directory.Exists(target) || File.Exists(target))
        {
            result.Error = "There is already a folder or file with that name here.";
            return result;
        }

        return Run(result, () =>
        {
            Directory.CreateDirectory(target);
            result.NewPath = target;
            _logger.LogInformation("Media Downloader: created folder {Path}", target);
        }, false);
    }

    /// <summary>Renames a folder.</summary>
    /// <param name="path">The folder (inside a user's folder, not the user's folder itself; checked by the caller).</param>
    /// <param name="name">The new name.</param>
    /// <returns>The result.</returns>
    public FolderResult Rename(string path, string? name)
    {
        var result = new FolderResult { Path = path };
        if (CheckName(name) is { } bad)
        {
            result.Error = bad;
            return result;
        }

        if (!Directory.Exists(path))
        {
            result.Error = "The folder is not there any more.";
            return result;
        }

        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name!.Trim());
        return MoveDirectory(result, path, target);
    }

    /// <summary>Moves a folder into another folder.</summary>
    /// <param name="path">The folder (checked by the caller).</param>
    /// <param name="into">The folder to move it into (checked by the caller).</param>
    /// <returns>The result.</returns>
    public FolderResult MoveFolder(string path, string into)
    {
        var result = new FolderResult { Path = path };
        if (!Directory.Exists(path))
        {
            result.Error = "The folder is not there any more.";
            return result;
        }

        if (!Directory.Exists(into))
        {
            result.Error = "The folder to move it into is not there any more.";
            return result;
        }

        if (IsSameOrInside(into, path))
        {
            result.Error = "A folder cannot be moved into itself or into one of its own sub folders.";
            return result;
        }

        var target = System.IO.Path.Combine(into, System.IO.Path.GetFileName(path));
        return MoveDirectory(result, path, target);
    }

    /// <summary>Deletes a folder with everything in it.</summary>
    /// <param name="path">The folder (inside a user's folder, not the user's folder itself; checked by the caller).</param>
    /// <returns>The result.</returns>
    public FolderResult Delete(string path)
    {
        var result = new FolderResult { Path = path };
        if (!Directory.Exists(path))
        {
            result.Error = "The folder is not there any more.";
            return result;
        }

        if (Busy(path))
        {
            result.Error = "A download is still saving into this folder. Wait until it finishes or cancel it.";
            return result;
        }

        return Run(result, () =>
        {
            Directory.Delete(path, true);
            _logger.LogInformation("Media Downloader: deleted folder {Path}", path);
        }, true);
    }

    /// <summary>Moves files into a folder. A media file moves with its poster, description and subtitles; one alone in its own folder moves with the whole folder.</summary>
    /// <param name="paths">The files (checked by the caller).</param>
    /// <param name="into">The folder to move them into (checked by the caller).</param>
    /// <param name="keepRoots">The user's own folders, which are never moved.</param>
    /// <returns>One result per file.</returns>
    public List<FolderResult> MoveFiles(IEnumerable<string> paths, string into, IReadOnlyList<string> keepRoots)
    {
        var results = new List<FolderResult>();
        var changed = false;
        foreach (var full in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var result = new FolderResult { Path = full };
            results.Add(result);
            if (!File.Exists(full))
            {
                result.Error = "The file is not there any more.";
                continue;
            }

            if (!Directory.Exists(into))
            {
                result.Error = "The folder to move it into is not there any more.";
                continue;
            }

            var dir = System.IO.Path.GetDirectoryName(full)!;
            if (string.Equals(dir, into.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                result.Error = "It is already in that folder.";
                continue;
            }

            if (Busy(dir))
            {
                result.Error = "A download is still saving into its folder. Wait until it finishes or cancel it.";
                continue;
            }

            var isMedia = StorageService.MediaExtensions.Contains(System.IO.Path.GetExtension(full));
            try
            {
                if (isMedia && OwnsFolder(full, dir, keepRoots) && !IsSameOrInside(into, dir))
                {
                    // A film saved in its own "Title (Year)" folder: move the whole folder, with its poster.
                    var target = System.IO.Path.Combine(into, System.IO.Path.GetFileName(dir));
                    if (Directory.Exists(target) || File.Exists(target))
                    {
                        result.Error = "The folder to move it into already has a folder called " + System.IO.Path.GetFileName(dir) + ".";
                        continue;
                    }

                    Directory.Move(dir, target);
                    result.NewPath = System.IO.Path.Combine(target, System.IO.Path.GetFileName(full));
                }
                else
                {
                    var baseName = System.IO.Path.GetFileNameWithoutExtension(full);
                    var files = new List<string> { full };
                    if (isMedia)
                    {
                        files.AddRange(Directory.GetFiles(dir).Where(f => !string.Equals(f, full, StringComparison.OrdinalIgnoreCase)
                            && StorageService.SidecarExtensions.Contains(System.IO.Path.GetExtension(f))
                            && System.IO.Path.GetFileName(f).StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)));
                    }

                    var clash = files.FirstOrDefault(f => File.Exists(System.IO.Path.Combine(into, System.IO.Path.GetFileName(f))) || Directory.Exists(System.IO.Path.Combine(into, System.IO.Path.GetFileName(f))));
                    if (clash is not null)
                    {
                        result.Error = "The folder to move it into already has " + System.IO.Path.GetFileName(clash) + ".";
                        continue;
                    }

                    foreach (var f in files)
                    {
                        File.Move(f, System.IO.Path.Combine(into, System.IO.Path.GetFileName(f)));
                    }

                    result.NewPath = System.IO.Path.Combine(into, System.IO.Path.GetFileName(full));
                }

                result.Ok = true;
                changed = true;
                _logger.LogInformation("Media Downloader: moved {Path} to {Target}", full, result.NewPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                result.Error = Explain(ex);
                _logger.LogWarning(ex, "Could not move {Path}", full);
            }
        }

        if (changed)
        {
            _libraryManager.QueueLibraryScan();
        }

        return results;
    }

    /// <summary>Checks whether a path is a folder or inside it.</summary>
    /// <param name="path">The path.</param>
    /// <param name="folder">The folder.</param>
    /// <returns>True when the same or inside.</returns>
    public static bool IsSameOrInside(string path, string folder)
    {
        var p = path.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var f = folder.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        return string.Equals(p, f, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(f + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks a new file or folder name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>Why it cannot be used, or null when it is fine.</returns>
    public static string? CheckName(string? name)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length == 0)
        {
            return "Type a name.";
        }

        if (n.Length > 120)
        {
            return "The name is too long (120 characters at most).";
        }

        if (n == "." || n == ".." || n.EndsWith('.'))
        {
            return "A name cannot be . or .. or end with a dot.";
        }

        var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        if (n.Any(c => invalid.Contains(c) || char.IsControl(c)))
        {
            return "A name cannot contain any of these: < > : \" / \\ | ? *";
        }

        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        if (reserved.Contains(n.Split('.')[0].ToUpperInvariant()))
        {
            return "Windows does not allow that name.";
        }

        return null;
    }

    private static bool OwnsFolder(string file, string dir, IReadOnlyList<string> keepRoots)
    {
        if (keepRoots.Any(r => string.Equals(r.TrimEnd(System.IO.Path.DirectorySeparatorChar), dir, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (Directory.GetDirectories(dir).Length > 0)
        {
            return false;
        }

        var others = Directory.GetFiles(dir).Where(f => !string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
        return others.All(f => StorageService.SidecarExtensions.Contains(System.IO.Path.GetExtension(f)));
    }

    private static string Explain(Exception ex) =>
        $"Jellyfin's service account ({Environment.UserName}) could not do it: {ex.Message} Close any program that has the files open, and give that account Modify permission on the library folder.";

    private static void Walk(string root, string dir, int depth, List<FolderEntry> list)
    {
        if (depth > MaxDepth || list.Count >= MaxFolders)
        {
            return;
        }

        string[] subs;
        try
        {
            subs = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var sub in subs.OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
        {
            if (list.Count >= MaxFolders)
            {
                return;
            }

            var attr = File.GetAttributes(sub);
            if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0 || (attr & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            list.Add(new FolderEntry
            {
                Path = sub,
                Relative = System.IO.Path.GetRelativePath(root, sub).Replace(System.IO.Path.DirectorySeparatorChar, '/'),
                Root = root,
                Depth = depth
            });
            Walk(root, sub, depth + 1, list);
        }
    }

    private static void Count(List<FolderEntry> list)
    {
        foreach (var entry in list)
        {
            try
            {
                var files = new DirectoryInfo(entry.Path).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                entry.Files = files.Count;
                entry.Size = files.Sum(f => f.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // left at 0
            }
        }
    }

    private bool Busy(string folder) =>
        _queue.GetJobs().Any(j => (j.State == JobState.Queued || j.State == JobState.Running)
            && JobFolder(j) is { } target
            && (IsSameOrInside(target, folder) || IsSameOrInside(folder, target)));

    private static string? JobFolder(DownloadJob job)
    {
        // Once queued, the request's TargetFolder is the full folder the download saves into.
        try
        {
            var target = job.Request.TargetFolder ?? string.Empty;
            return target.Length == 0 ? null : System.IO.Path.GetFullPath(target);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private FolderResult MoveDirectory(FolderResult result, string path, string target)
    {
        if (string.Equals(path.TrimEnd(System.IO.Path.DirectorySeparatorChar), target, StringComparison.Ordinal))
        {
            result.Ok = true;
            result.NewPath = target;
            return result;
        }

        // A change of case only (Babui -> babui) is allowed; anything else must not clash.
        var caseOnly = string.Equals(path.TrimEnd(System.IO.Path.DirectorySeparatorChar), target, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (Directory.Exists(target) || File.Exists(target)))
        {
            result.Error = "There is already a folder or file with that name there.";
            return result;
        }

        if (Busy(path))
        {
            result.Error = "A download is still saving into this folder. Wait until it finishes or cancel it.";
            return result;
        }

        return Run(result, () =>
        {
            if (caseOnly)
            {
                var temp = target + ".md-rename-" + Guid.NewGuid().ToString("N")[..8];
                Directory.Move(path, temp);
                Directory.Move(temp, target);
            }
            else
            {
                Directory.Move(path, target);
            }

            result.NewPath = target;
            _logger.LogInformation("Media Downloader: moved folder {Path} to {Target}", path, target);
        }, true);
    }

    private FolderResult Run(FolderResult result, Action action, bool scan)
    {
        try
        {
            action();
            result.Ok = true;
            if (scan)
            {
                _libraryManager.QueueLibraryScan();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            result.Error = Explain(ex);
            _logger.LogWarning(ex, "Media Downloader folder change failed for {Path}", result.Path);
        }

        return result;
    }
}
