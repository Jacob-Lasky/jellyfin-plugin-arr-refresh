using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ArrRefresh.Planning;

/// <summary>
/// The read-only questions the planner asks of Jellyfin and the filesystem.
/// </summary>
public interface ILibraryView
{
    /// <summary>
    /// Gets the configured physical locations of every library, normalised.
    /// </summary>
    IReadOnlyList<string> LibraryLocations { get; }

    /// <summary>
    /// Finds the folder item (a Series, Season, music album, ...) stored at exactly this path.
    /// Library roots are never returned.
    /// </summary>
    /// <param name="path">A normalised path.</param>
    /// <returns>The folder, or null when no folder item has this path.</returns>
    FolderRef? FindFolderItem(string path);

    /// <summary>
    /// Whether a file or directory exists at the path.
    /// </summary>
    /// <param name="path">A normalised path.</param>
    /// <returns>True when it exists.</returns>
    bool Exists(string path);
}

/// <summary>
/// A Jellyfin folder item.
/// </summary>
/// <param name="Id">The item id.</param>
/// <param name="Path">The item path.</param>
/// <param name="Type">The item type name, for logs.</param>
/// <param name="Name">The item name, for logs.</param>
public sealed record FolderRef(Guid Id, string Path, string Type, string Name);
