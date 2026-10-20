using System;
using System.Linq;
using Jellyfin.Plugin.ArrRefresh.Search;
using Xunit;

namespace Jellyfin.Plugin.ArrRefresh.Tests;

public class DocumentShapeTests
{
    // The Meilisearch plugin 1.12.1.5's document fields, as Tower's live index reported them in its
    // stats' fieldDistribution on 2026-10-20. A literal on purpose: comparing Fields to itself proves nothing.
    private static readonly string[] Live1_12_1_5 =
    [
        "albumArtists", "artists", "communityRating", "criticRating", "genres", "guid", "isFolder", "name",
        "originalTitle", "overview", "parentId", "path", "productionYear", "seriesName", "sortName",
        "studios", "tagline", "tags", "type",
    ];

    [Fact]
    public void Writes_exactly_the_fields_the_live_index_holds()
    {
        Assert.True(DocumentShape.Matches(Live1_12_1_5));
        Assert.Equal(Live1_12_1_5.Order(StringComparer.Ordinal), DocumentShape.Fields.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_empty_index_has_no_shape_to_contradict()
    {
        Assert.True(DocumentShape.Matches([]));
    }

    [Theory]
    [InlineData("rating")]
    [InlineData(null)]
    public void A_field_added_or_dropped_upstream_stops_the_writes(string? extra)
    {
        var fields = extra is null ? Live1_12_1_5.Skip(1) : Live1_12_1_5.Append(extra);
        Assert.False(DocumentShape.Matches(fields));
    }

    [Fact]
    public void Empty_lists_and_virtual_paths_are_stored_as_the_meilisearch_plugin_stores_them()
    {
        Assert.Null(DocumentShape.List([]));
        Assert.Null(DocumentShape.List(null));
        Assert.Equal(["Drama"], DocumentShape.List(["Drama", ""]));
        Assert.Null(DocumentShape.StoredPath("%MetadataPath%/x"));
        Assert.Equal("/data/media/movies/A/a.mkv", DocumentShape.StoredPath("/data/media/movies/A/a.mkv"));
    }
}
