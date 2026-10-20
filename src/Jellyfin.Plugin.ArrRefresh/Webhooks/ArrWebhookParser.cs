using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.ArrRefresh.Webhooks;

/// <summary>
/// Parses the stock Sonarr v4 and Radarr v5/v6 "Webhook" connection payloads.
/// </summary>
/// <remarks>
/// Field names follow Sonarr's and Radarr's <c>Notifications/Webhook/*Payload.cs</c> (Sonarr v4.0.20, Radarr v6.4.4),
/// which serialise camelCase; matching is case-insensitive so a proxy that re-cases keys still works.
/// Only paths are extracted, plus the old-to-new pairs the arr states outright. What happened to each
/// path is decided later from the filesystem, so a payload never has to be trusted about whether a
/// file exists; the pairs only say which new item inherits an old one's history.
/// </remarks>
public static class ArrWebhookParser
{
    /// <summary>
    /// Parses a webhook body.
    /// </summary>
    /// <param name="json">The raw request body.</param>
    /// <returns>The parsed event.</returns>
    /// <exception cref="FormatException">The body is not a JSON object with an <c>eventType</c>.</exception>
    public static ArrEvent Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Webhook body is not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Webhook body is not a JSON object.");
            }

            var eventType = GetString(root, "eventType")
                ?? throw new FormatException("Webhook body has no eventType.");
            var instanceName = GetString(root, "instanceName");

            var source = Get(root, "movie") is not null
                ? ArrSource.Radarr
                : Get(root, "series") is not null ? ArrSource.Sonarr : ArrSource.Unknown;

            if (eventType.Equals("Test", StringComparison.OrdinalIgnoreCase))
            {
                return new ArrEvent(source, eventType, instanceName, ArrEventKind.Test, [], []);
            }

            var paths = new List<string>();
            var replacements = new List<PathReplacement>();
            var kind = source switch
            {
                ArrSource.Radarr => CollectRadarr(root, eventType, paths, replacements),
                ArrSource.Sonarr => CollectSonarr(root, eventType, paths, replacements),
                _ => ArrEventKind.Ignored,
            };

            return new ArrEvent(source, eventType, instanceName, kind, Dedupe(paths), replacements.Distinct().ToList());
        }
    }

    private static ArrEventKind CollectRadarr(JsonElement root, string eventType, List<string> paths, List<PathReplacement> replacements)
    {
        switch (eventType.ToUpperInvariant())
        {
            case "DOWNLOAD":
                // Import and upgrade: the new file, plus the files an upgrade replaced.
                AddPath(Get(root, "movieFile"), "path", paths);
                AddEach(Get(root, "deletedFiles"), "path", paths);
                AddUpgrades(GetPath(Get(root, "movieFile"), "path"), Get(root, "deletedFiles"), replacements);
                if (paths.Count == 0)
                {
                    // Scoped to the one movie folder, so this fallback cannot become a library walk.
                    AddPath(Get(root, "movie"), "folderPath", paths);
                }

                return ArrEventKind.Apply;
            case "RENAME":
                AddRenames(Get(root, "renamedMovieFiles"), paths, replacements);
                return ArrEventKind.Apply;
            case "MOVIEFILEDELETE":
                AddPath(Get(root, "movieFile"), "path", paths);
                return ArrEventKind.Apply;
            case "MOVIEDELETE":
                AddPath(Get(root, "movie"), "folderPath", paths);
                return ArrEventKind.Apply;
            default:
                // Grab, MovieAdded (no file yet), Health, HealthRestored, ApplicationUpdate, ManualInteractionRequired.
                return ArrEventKind.Ignored;
        }
    }

    private static ArrEventKind CollectSonarr(JsonElement root, string eventType, List<string> paths, List<PathReplacement> replacements)
    {
        switch (eventType.ToUpperInvariant())
        {
            case "DOWNLOAD":
                // Sonarr sends two Download shapes: per-file import (episodeFile + deletedFiles)
                // and "import complete" for a whole release (episodeFiles).
                AddPath(Get(root, "episodeFile"), "path", paths);
                AddEach(Get(root, "episodeFiles"), "path", paths);
                AddEach(Get(root, "deletedFiles"), "path", paths);
                AddUpgrades(GetPath(Get(root, "episodeFile"), "path"), Get(root, "deletedFiles"), replacements);

                // DO NOT fall back to the series path when no file is named: that would refresh the
                // whole series. An import with no file changes nothing worth guessing at.
                return paths.Count == 0 ? ArrEventKind.Ignored : ArrEventKind.Apply;
            case "RENAME":
                AddRenames(Get(root, "renamedEpisodeFiles"), paths, replacements);
                return ArrEventKind.Apply;
            case "EPISODEFILEDELETE":
                AddPath(Get(root, "episodeFile"), "path", paths);
                return ArrEventKind.Apply;
            case "SERIESDELETE":
                AddPath(Get(root, "series"), "path", paths);
                return ArrEventKind.Apply;
            default:
                // Grab, SeriesAdd (no files yet), Health, HealthRestored, ApplicationUpdate, ManualInteractionRequired.
                return ArrEventKind.Ignored;
        }
    }

    private static JsonElement? Get(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement obj, string name)
    {
        var value = Get(obj, name);
        return value is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
    }

    private static IEnumerable<JsonElement> Items(JsonElement? array)
    {
        if (array is { ValueKind: JsonValueKind.Array } a)
        {
            foreach (var item in a.EnumerateArray())
            {
                yield return item;
            }
        }
    }

    private static string? GetPath(JsonElement? obj, string name)
        => obj is { } o && GetString(o, name) is { } path && !string.IsNullOrWhiteSpace(path) ? path : null;

    private static void AddPath(JsonElement? obj, string name, List<string> paths)
    {
        if (GetPath(obj, name) is { } path)
        {
            paths.Add(path);
        }
    }

    private static void AddRenames(JsonElement? files, List<string> paths, List<PathReplacement> replacements)
    {
        foreach (var file in Items(files))
        {
            var previous = GetPath(file, "previousPath");
            var current = GetPath(file, "path");
            AddPath(file, "previousPath", paths);
            AddPath(file, "path", paths);
            if (previous is not null && current is not null)
            {
                replacements.Add(new PathReplacement(previous, current));
            }
        }
    }

    private static void AddUpgrades(string? imported, JsonElement? deletedFiles, List<PathReplacement> replacements)
    {
        if (imported is null)
        {
            return;
        }

        foreach (var file in Items(deletedFiles))
        {
            if (GetPath(file, "path") is { } replaced)
            {
                replacements.Add(new PathReplacement(replaced, imported));
            }
        }
    }

    private static void AddEach(JsonElement? array, string name, List<string> paths)
    {
        foreach (var item in Items(array))
        {
            AddPath(item, name, paths);
        }
    }

    private static List<string> Dedupe(List<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(paths.Count);
        foreach (var p in paths)
        {
            if (seen.Add(p))
            {
                result.Add(p);
            }
        }

        return result;
    }
}
