using System;
using System.IO;

namespace Jellyfin.Plugin.ArrRefresh.Planning;

/// <summary>
/// Path helpers shared by the mapper and the planner.
/// </summary>
internal static class PathUtil
{
    /// <summary>
    /// How paths compare on this platform: case-insensitively where the default file systems are
    /// (Windows, macOS), exactly elsewhere, matching how Jellyfin itself finds them on disk.
    /// </summary>
    public static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// A string comparer matching <see cref="Comparison"/>, for path sets and dictionaries.
    /// </summary>
    public static readonly StringComparer Comparer = StringComparer.FromComparison(Comparison);

    /// <summary>
    /// Normalises an absolute path: resolves <c>.</c>/<c>..</c> and strips a trailing separator.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The normalised path, or null when it is not an absolute path.</returns>
    public static string? Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="root"/> or lies under it, on a whole-segment boundary.
    /// </summary>
    /// <param name="path">A normalised path.</param>
    /// <param name="root">A normalised directory.</param>
    /// <returns>True when the path is inside the root.</returns>
    public static bool IsAtOrUnder(string path, string root)
    {
        if (path.Length == root.Length)
        {
            return string.Equals(path, root, Comparison);
        }

        return path.Length > root.Length
            && path.StartsWith(root, Comparison)
            && IsSeparator(path[root.Length]);
    }

    /// <summary>
    /// Whether a character is a directory separator on this platform.
    /// </summary>
    /// <param name="c">The character.</param>
    /// <returns>True for a separator.</returns>
    public static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
}
