using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtDlp;

/// <summary>A drive that holds a library folder.</summary>
public class DriveSpace
{
    /// <summary>Gets or sets the drive (for example "C:\" or "/").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the size in bytes.</summary>
    public long Total { get; set; }

    /// <summary>Gets or sets the free space in bytes.</summary>
    public long Free { get; set; }
}

/// <summary>A library folder and how much it holds.</summary>
public class FolderSpace
{
    /// <summary>Gets or sets the library name.</summary>
    public string Library { get; set; } = string.Empty;

    /// <summary>Gets or sets the folder.</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>Gets or sets the total size of all files in it.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets the number of media files in it.</summary>
    public int MediaFiles { get; set; }

    /// <summary>Gets or sets the drive it is on.</summary>
    public string Drive { get; set; } = string.Empty;
}

/// <summary>Who watched a file.</summary>
public class WatchInfo
{
    /// <summary>Gets or sets the user name.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether it was watched to the end.</summary>
    public bool Played { get; set; }

    /// <summary>Gets or sets how far it was watched, 0-100 (for unfinished).</summary>
    public int Percent { get; set; }

    /// <summary>Gets or sets when it was last played.</summary>
    public DateTime? LastPlayed { get; set; }
}

/// <summary>A file in a library folder.</summary>
public class StoredFile
{
    /// <summary>Gets or sets the full path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the path inside its library folder.</summary>
    public string Relative { get; set; } = string.Empty;

    /// <summary>Gets or sets the library name.</summary>
    public string Library { get; set; } = string.Empty;

    /// <summary>Gets or sets the size of the file plus its posters, .nfo and subtitles.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets when it was added (file time).</summary>
    public DateTime Added { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a media file (video or audio).</summary>
    public bool Media { get; set; }

    /// <summary>Gets or sets the Jellyfin item id (empty when Jellyfin does not know the file).</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets the users who played it (watched or started).</summary>
    public List<WatchInfo> Watched { get; } = new();
}

/// <summary>Everything the Storage tab shows.</summary>
public class StorageReport
{
    /// <summary>Gets the drives.</summary>
    public List<DriveSpace> Drives { get; } = new();

    /// <summary>Gets the library folders.</summary>
    public List<FolderSpace> Folders { get; } = new();

    /// <summary>Gets the files, biggest first.</summary>
    public List<StoredFile> Files { get; } = new();
}

/// <summary>The outcome of deleting one file.</summary>
public class DeleteResult
{
    /// <summary>Gets or sets the path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether it is gone.</summary>
    public bool Deleted { get; set; }

    /// <summary>Gets or sets the reason it was not deleted.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>Gets or sets the bytes freed.</summary>
    public long Freed { get; set; }
}

/// <summary>
/// Shows what the library folders hold and deletes files (through Jellyfin, so its library stays right).
/// </summary>
public class StorageService
{
    /// <summary>Media file extensions.</summary>
    public static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv", ".3gp", ".iso", ".vob",
        ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wav", ".wma"
    };

    /// <summary>Files that belong to a media file or folder (posters, .nfo, subtitles) and are shown only as part of its size.</summary>
    public static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo", ".jpg", ".jpeg", ".png", ".webp", ".tbn", ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".json", ".txt", ".url", ".db", ".ini"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<StorageService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="logger">Logger.</param>
    public StorageService(ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager, ILogger<StorageService> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    private List<(string Library, string Folder)> LibraryFolders()
    {
        var list = new List<(string, string)>();
        foreach (var vf in _libraryManager.GetVirtualFolders())
        {
            foreach (var loc in vf.Locations ?? Array.Empty<string>())
            {
                list.Add((vf.Name, Path.GetFullPath(loc).TrimEnd(Path.DirectorySeparatorChar)));
            }
        }

        return list;
    }

    /// <summary>Builds the report.</summary>
    /// <returns>Drives, folders and files.</returns>
    public StorageReport Build()
    {
        var report = new StorageReport();
        var users = _userManager.GetUsers().ToList();
        var drives = new Dictionary<string, DriveSpace>(StringComparer.OrdinalIgnoreCase);

        foreach (var (library, folder) in LibraryFolders())
        {
            var space = new FolderSpace { Library = library, Folder = folder };
            report.Folders.Add(space);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                var root = Path.GetPathRoot(folder) ?? folder;
                var drive = new DriveInfo(root);
                space.Drive = drive.Name;
                if (!drives.ContainsKey(drive.Name) && drive.IsReady)
                {
                    drives[drive.Name] = new DriveSpace { Name = drive.Name, Total = drive.TotalSize, Free = drive.AvailableFreeSpace };
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "No drive information for {Folder}", folder);
            }

            List<FileInfo> files;
            try
            {
                files = new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System })
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Cannot list {Folder}", folder);
                continue;
            }

            space.Size = files.Sum(f => f.Length);
            var mainFiles = files.Where(f => !SidecarExtensions.Contains(f.Extension)).ToList();
            space.MediaFiles = mainFiles.Count(f => MediaExtensions.Contains(f.Extension));

            foreach (var file in mainFiles)
            {
                var entry = new StoredFile
                {
                    Path = file.FullName,
                    Relative = Path.GetRelativePath(folder, file.FullName),
                    Library = library,
                    Size = file.Length + SidecarSize(file, files, folder),
                    Added = file.CreationTimeUtc > file.LastWriteTimeUtc ? file.CreationTimeUtc : file.LastWriteTimeUtc,
                    Media = MediaExtensions.Contains(file.Extension)
                };

                var item = entry.Media ? _libraryManager.FindByPath(file.FullName, false) : null;
                if (item is not null)
                {
                    entry.ItemId = item.Id.ToString("N");
                    entry.Title = item.Name ?? string.Empty;
                    foreach (var user in users)
                    {
                        var data = _userDataManager.GetUserData(user, item);
                        if (data is null || (!data.Played && data.PlaybackPositionTicks <= 0))
                        {
                            continue;
                        }

                        var runtime = item.RunTimeTicks ?? 0;
                        entry.Watched.Add(new WatchInfo
                        {
                            User = user.Username,
                            Played = data.Played,
                            Percent = data.Played ? 100 : runtime > 0 ? (int)Math.Clamp(data.PlaybackPositionTicks * 100 / runtime, 0, 99) : 0,
                            LastPlayed = data.LastPlayedDate
                        });
                    }
                }

                report.Files.Add(entry);
            }
        }

        report.Drives.AddRange(drives.Values);
        report.Files.Sort((a, b) => b.Size.CompareTo(a.Size));
        return report;
    }

    private static long SidecarSize(FileInfo media, List<FileInfo> all, string libraryFolder)
    {
        if (!MediaExtensions.Contains(media.Extension) || media.DirectoryName is null)
        {
            return 0;
        }

        var dir = media.DirectoryName;
        var baseName = System.IO.Path.GetFileNameWithoutExtension(media.Name);
        var ownFolder = OwnsFolder(media, all, libraryFolder);
        return all.Where(f => f.DirectoryName == dir && SidecarExtensions.Contains(f.Extension)
                && (ownFolder || f.Name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)))
            .Sum(f => f.Length);
    }

    private static bool OwnsFolder(FileInfo media, List<FileInfo> all, string libraryFolder)
    {
        var dir = media.DirectoryName;
        if (dir is null || string.Equals(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar), libraryFolder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return all.Count(f => f.DirectoryName == dir && MediaExtensions.Contains(f.Extension)) == 1
            && !all.Any(f => f.DirectoryName is not null && f.DirectoryName.StartsWith(dir + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Deletes files (and, for media, their posters, .nfo and subtitles, and their folder when nothing else is left in it).</summary>
    /// <param name="paths">Full paths, each inside a library folder.</param>
    /// <param name="keepRoots">Folders that are never removed, even when left empty (a user's own folders); empty folders are removed only below them.</param>
    /// <returns>One result per path.</returns>
    public List<DeleteResult> Delete(IEnumerable<string> paths, IReadOnlyList<string>? keepRoots = null)
    {
        var folders = LibraryFolders();
        var results = new List<DeleteResult>();
        var changed = false;
        foreach (var raw in paths.Distinct())
        {
            var result = new DeleteResult { Path = raw };
            results.Add(result);
            string full;
            try
            {
                full = System.IO.Path.GetFullPath(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                result.Error = "Not a valid path.";
                continue;
            }

            var lib = folders.FirstOrDefault(f => full.StartsWith(f.Folder + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (lib.Folder is null)
            {
                result.Error = "Only files inside a library folder can be deleted here.";
                continue;
            }

            if (!File.Exists(full))
            {
                result.Error = "The file is not there any more.";
                continue;
            }

            var stop = lib.Folder;
            foreach (var root in keepRoots ?? Array.Empty<string>())
            {
                var r = root.TrimEnd(System.IO.Path.DirectorySeparatorChar);
                if (r.Length > stop.Length && full.StartsWith(r + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    stop = r;
                }
            }

            var dir = System.IO.Path.GetDirectoryName(full)!;
            var baseName = System.IO.Path.GetFileNameWithoutExtension(full);
            var isMedia = MediaExtensions.Contains(System.IO.Path.GetExtension(full));
            var before = FolderBytes(dir);
            try
            {
                var item = isMedia ? _libraryManager.FindByPath(full, false) : null;
                if (item is not null)
                {
                    _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
                }

                if (File.Exists(full))
                {
                    File.Delete(full);
                }

                if (isMedia && Directory.Exists(dir))
                {
                    foreach (var side in Directory.GetFiles(dir).Where(f => SidecarExtensions.Contains(System.IO.Path.GetExtension(f))
                                 && System.IO.Path.GetFileName(f).StartsWith(baseName, StringComparison.OrdinalIgnoreCase)))
                    {
                        File.Delete(side);
                    }

                    var isRoot = string.Equals(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar), stop, StringComparison.OrdinalIgnoreCase);
                    if (!isRoot && Directory.GetDirectories(dir).Length == 0
                        && Directory.GetFiles(dir).All(f => SidecarExtensions.Contains(System.IO.Path.GetExtension(f))))
                    {
                        Directory.Delete(dir, true);

                        // Also remove parent folders left empty, up to (not including) the library folder or the user's own folder.
                        var parent = System.IO.Path.GetDirectoryName(dir);
                        while (parent is not null
                            && parent.StartsWith(stop + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            && Directory.Exists(parent)
                            && !Directory.EnumerateFileSystemEntries(parent).Any())
                        {
                            Directory.Delete(parent);
                            parent = System.IO.Path.GetDirectoryName(parent);
                        }
                    }
                }

                result.Deleted = !File.Exists(full);
                result.Freed = Math.Max(0, before - (Directory.Exists(dir) ? FolderBytes(dir) : 0));
                changed |= result.Deleted;
                _logger.LogInformation("Media Downloader storage: deleted {Path} ({Bytes} bytes freed)", full, result.Freed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                result.Deleted = !File.Exists(full);
                result.Error = result.Deleted ? string.Empty : $"Jellyfin's service account ({Environment.UserName}) could not delete it: {ex.Message} Give it Modify permission on the library folder.";
                _logger.LogWarning(ex, "Could not delete {Path}", full);
            }
        }

        if (changed)
        {
            _libraryManager.QueueLibraryScan();
        }

        return results;
    }

    /// <summary>Gets the total size of a folder's files, sub folders included.</summary>
    /// <param name="dir">The folder.</param>
    /// <returns>Bytes (0 when it does not exist or cannot be read).</returns>
    public static long FolderBytes(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
