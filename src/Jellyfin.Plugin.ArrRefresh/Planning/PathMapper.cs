using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ArrRefresh.Planning;

/// <summary>
/// Translates paths as Sonarr/Radarr see them into paths as Jellyfin sees them.
/// </summary>
public sealed class PathMapper
{
    private readonly IReadOnlyList<(string From, string To)> _mappings;

    private PathMapper(IReadOnlyList<(string From, string To)> mappings)
    {
        // Longest prefix first, so /data/media/movies-4k wins over /data/media.
        _mappings = mappings.OrderByDescending(m => m.From.Length).ToList();
    }

    /// <summary>
    /// Gets the parsed mappings.
    /// </summary>
    public IReadOnlyList<(string From, string To)> Mappings => _mappings;

    /// <summary>
    /// Parses the configuration text: one <c>from =&gt; to</c> per line; blank lines and <c>#</c> comments are skipped.
    /// </summary>
    /// <param name="text">The configuration text.</param>
    /// <returns>The mapper.</returns>
    /// <exception cref="FormatException">A line is not two absolute paths separated by <c>=&gt;</c>.</exception>
    public static PathMapper Parse(string? text)
    {
        var mappings = new List<(string, string)>();
        var lineNo = 0;
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split("=>", 2, StringSplitOptions.TrimEntries);
            var from = parts.Length == 2 ? PathUtil.Normalize(parts[0]) : null;
            var to = parts.Length == 2 ? PathUtil.Normalize(parts[1]) : null;
            if (from is null || to is null)
            {
                throw new FormatException($"Path mapping line {lineNo} must be '/arr/path => /jellyfin/path': {line}");
            }

            mappings.Add((from, to));
        }

        return new PathMapper(mappings);
    }

    /// <summary>
    /// Maps one path. A path no mapping covers is returned unchanged.
    /// </summary>
    /// <param name="path">A normalised path.</param>
    /// <returns>The mapped path.</returns>
    public string Map(string path)
    {
        foreach (var (from, to) in _mappings)
        {
            if (PathUtil.IsAtOrUnder(path, from))
            {
                return to + path[from.Length..];
            }
        }

        return path;
    }
}
