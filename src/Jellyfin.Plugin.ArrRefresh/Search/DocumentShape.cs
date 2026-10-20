using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ArrRefresh.Search;

/// <summary>
/// The document the Meilisearch plugin (arnesacnussem/jellyfin-plugin-meilisearch) stores per item.
/// </summary>
/// <remarks>
/// Mirrors that plugin's <c>MeilisearchItem</c> record and <c>EfCoreIndexer.ToMeilisearchItem</c> as of
/// 1.12.1.5. It is a copy that must agree with theirs, so it is checked against the live index before
/// every write (<see cref="Matches"/>): when their shape changes, this plugin stops writing and their
/// own reindex takes over again, instead of storing documents in a shape their search no longer reads.
/// </remarks>
public static class DocumentShape
{
    /// <summary>
    /// The Meilisearch plugin's id, from its manifest.
    /// </summary>
    public static readonly Guid MeilisearchPluginId = Guid.Parse("974395db-b31d-46a2-bc86-ef9aa5ac04dd");

    /// <summary>
    /// Gets every field of a document, as the index stores them.
    /// </summary>
    public static IReadOnlyList<string> Fields { get; } =
    [
        "guid", "type", "parentId", "name", "overview", "originalTitle", "seriesName", "productionYear",
        "artists", "albumArtists", "genres", "studios", "tags", "isFolder", "communityRating",
        "criticRating", "path", "tagline", "sortName",
    ];

    /// <summary>
    /// Whether the index's documents have exactly this shape. An empty index has no shape to contradict.
    /// </summary>
    /// <param name="indexFields">The field names the index reports (its stats' fieldDistribution keys).</param>
    /// <returns>True when writing these documents is safe.</returns>
    public static bool Matches(IEnumerable<string> indexFields)
    {
        var fields = indexFields.ToHashSet(StringComparer.Ordinal);
        return fields.Count == 0 || fields.SetEquals(Fields);
    }

    /// <summary>
    /// The value the Meilisearch plugin stores for a list column: null when empty, as a null database
    /// column splits to null there.
    /// </summary>
    /// <param name="values">The values.</param>
    /// <returns>The values, or null.</returns>
    public static IReadOnlyList<string>? List(IEnumerable<string>? values)
    {
        var list = values?.Where(v => !string.IsNullOrEmpty(v)).ToList();
        return list is { Count: > 0 } ? list : null;
    }

    /// <summary>
    /// The path the Meilisearch plugin stores: none for virtual paths (those starting with <c>%</c>).
    /// </summary>
    /// <param name="path">The item path.</param>
    /// <returns>The path, or null.</returns>
    public static string? StoredPath(string? path) => string.IsNullOrEmpty(path) || path[0] != '%' ? path : null;
}
