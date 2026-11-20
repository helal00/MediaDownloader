using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.YtDlp.Api;

/// <summary>One folder a user may use.</summary>
public class AllowedFolder
{
    /// <summary>Gets the folder's full path.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the library folder it is in.</summary>
    public string LibraryFolder { get; init; } = string.Empty;

    /// <summary>Gets the library's name.</summary>
    public string Library { get; init; } = string.Empty;

    /// <summary>Gets a short name for the page: "Library" or "Library / sub folder".</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Checks whether a full path is inside this folder.</summary>
    /// <param name="fullPath">The full path.</param>
    /// <returns>True when inside (not the folder itself).</returns>
    public bool Contains(string fullPath) =>
        fullPath.StartsWith(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the signed-in user may do with the downloader.</summary>
public class UserAccessInfo
{
    /// <summary>Jellyfin's claim with the user's id.</summary>
    public const string UserIdClaim = "Jellyfin-UserId";

    /// <summary>Gets the user's id ("N" format).</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>Gets the user's name.</summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the user is an administrator.</summary>
    public bool IsAdmin { get; init; }

    /// <summary>Gets a value indicating whether the user may use the downloader.</summary>
    public bool Allowed { get; init; }

    /// <summary>Gets a value indicating whether the user may delete files in their folders.</summary>
    public bool CanDelete { get; init; }

    /// <summary>Gets the folders the user may use.</summary>
    public List<AllowedFolder> Folders { get; init; } = new();

    /// <summary>Gets the signed-in user's id from the request, or null.</summary>
    /// <param name="principal">The request's user.</param>
    /// <returns>The id.</returns>
    public static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var value = principal.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;
    }

    /// <summary>Works out the signed-in user's access.</summary>
    /// <param name="principal">The request's user.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="queue">The queue (for the library folders).</param>
    /// <returns>The access, or null when nobody is signed in.</returns>
    public static UserAccessInfo? Resolve(ClaimsPrincipal principal, IUserManager userManager, DownloadQueue queue)
    {
        if (GetUserId(principal) is not { } id)
        {
            return null;
        }

        var user = userManager.GetUserById(id);
        if (user is null)
        {
            return null;
        }

        var libraries = queue.GetLibraryFolders()
            .Select(f => (f.Library, Folder: NormalizePath(f.Folder)))
            .Where(f => f.Folder.Length > 0)
            .ToList();

        var isAdmin = principal.IsInRole("Administrator");
        if (isAdmin)
        {
            return new UserAccessInfo
            {
                UserId = id.ToString("N"),
                UserName = user.Username,
                IsAdmin = true,
                Allowed = true,
                CanDelete = true,
                Folders = libraries.Select(l => new AllowedFolder { Path = l.Folder, LibraryFolder = l.Folder, Library = l.Library, Label = l.Library }).ToList()
            };
        }

        var entry = Plugin.Instance?.Configuration.UserAccess
            .FirstOrDefault(u => Guid.TryParse(u.UserId, out var g) && g == id);
        var folders = new List<AllowedFolder>();
        foreach (var raw in entry?.Folders ?? new List<string>())
        {
            var path = NormalizePath(raw);
            if (path.Length == 0 || folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // The folder must be a library folder or inside one; the deepest library folder wins.
            var lib = libraries
                .Where(l => string.Equals(l.Folder, path, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(l.Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(l => l.Folder.Length)
                .FirstOrDefault();
            if (lib.Folder is null)
            {
                continue;
            }

            var relative = Path.GetRelativePath(lib.Folder, path);
            folders.Add(new AllowedFolder
            {
                Path = path,
                LibraryFolder = lib.Folder,
                Library = lib.Library,
                Label = relative == "." ? lib.Library : lib.Library + " / " + relative.Replace(Path.DirectorySeparatorChar, '/')
            });
        }

        return new UserAccessInfo
        {
            UserId = id.ToString("N"),
            UserName = user.Username,
            IsAdmin = false,
            Allowed = entry is not null && folders.Count > 0,
            CanDelete = entry?.CanDelete ?? false,
            Folders = folders
        };
    }

    /// <summary>Finds one of the user's folders by its path.</summary>
    /// <param name="path">The path the page sent.</param>
    /// <returns>The folder, or null.</returns>
    public AllowedFolder? FindFolder(string? path)
    {
        var normalized = NormalizePath(path);
        return Folders.FirstOrDefault(f => string.Equals(f.Path, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finds the user's folder that is a path or contains it.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The folder, or null when the path is outside the user's folders.</returns>
    public AllowedFolder? FindContaining(string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized.Length == 0)
        {
            return null;
        }

        return Folders
            .Where(f => string.Equals(f.Path, normalized, StringComparison.OrdinalIgnoreCase) || f.Contains(normalized))
            .OrderByDescending(f => f.Path.Length)
            .FirstOrDefault();
    }

    /// <summary>Checks whether a path is one of the user's folders themselves.</summary>
    /// <param name="path">The path.</param>
    /// <returns>True for a user's own folder.</returns>
    public bool IsRoot(string? path)
    {
        var normalized = NormalizePath(path);
        return Folders.Any(f => string.Equals(f.Path, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Makes a full path without a trailing separator, or "" when it is not a valid path.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The full path.</returns>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var full = System.IO.Path.GetFullPath(path.Trim());
            var root = System.IO.Path.GetPathRoot(full) ?? string.Empty;
            return full.Length > root.Length ? full.TrimEnd(System.IO.Path.DirectorySeparatorChar) : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }
}
