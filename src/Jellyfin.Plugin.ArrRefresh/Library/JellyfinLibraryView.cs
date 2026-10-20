using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ArrRefresh.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ArrRefresh.Library;

/// <summary>
/// <see cref="ILibraryView"/> over Jellyfin's database and the local filesystem.
/// </summary>
/// <remarks>
/// Every lookup is a single-path database query or a single <c>stat</c>; nothing here lists a directory.
/// </remarks>
internal sealed class JellyfinLibraryView : ILibraryView
{
    private readonly ILibraryManager _libraryManager;
    private readonly HashSet<string> _locationSet;

    public JellyfinLibraryView(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
        LibraryLocations = libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? [])
            .Select(PathUtil.Normalize)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        _locationSet = new HashSet<string>(LibraryLocations, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> LibraryLocations { get; }

    public FolderRef? FindFolderItem(string path)
    {
        if (_locationSet.Contains(path))
        {
            return null;
        }

        return _libraryManager.FindByPath(path, true) is Folder folder and not AggregateFolder and not CollectionFolder and not UserRootFolder
            ? new FolderRef(folder.Id, path, folder.GetType().Name, folder.Name, IsAttached(folder))
            : null;
    }

    public bool Exists(string path) => OnDisk(path);

    /// <summary>Whether a file or directory is at the path.</summary>
    internal static bool OnDisk(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Whether an item is in the tree: it has a parent or, for an extra or a merged version, an owner.</summary>
    internal static bool IsAttached(BaseItem item) => !item.ParentId.Equals(Guid.Empty) || !item.OwnerId.Equals(Guid.Empty);
}
