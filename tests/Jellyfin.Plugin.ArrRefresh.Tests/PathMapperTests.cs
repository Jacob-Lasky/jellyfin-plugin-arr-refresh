using System;
using Jellyfin.Plugin.ArrRefresh.Planning;
using Xunit;

namespace Jellyfin.Plugin.ArrRefresh.Tests;

public class PathMapperTests
{
    [Fact]
    public void Empty_configuration_maps_nothing()
    {
        Assert.Equal("/data/media/movies/A", PathMapper.Parse(string.Empty).Map("/data/media/movies/A"));
        Assert.Equal("/x", PathMapper.Parse(null).Map("/x"));
    }

    [Fact]
    public void Longest_prefix_wins_and_only_on_segment_boundaries()
    {
        var m = PathMapper.Parse("""
            # arr => jellyfin
            /data => /media
            /data/movies-4k/ => /uhd

            """);
        Assert.Equal("/uhd/A/a.mkv", m.Map("/data/movies-4k/A/a.mkv"));
        Assert.Equal("/media/movies/A", m.Map("/data/movies/A"));
        Assert.Equal("/media", m.Map("/data"));
        Assert.Equal("/database/x", m.Map("/database/x"));
    }

    [Theory]
    [InlineData("/data")]
    [InlineData("/data => relative")]
    [InlineData("=> /x")]
    public void Malformed_lines_are_rejected_with_their_line_number(string text)
    {
        var ex = Assert.Throws<FormatException>(() => PathMapper.Parse("/ok => /fine\n" + text));
        Assert.Contains("line 2", ex.Message, StringComparison.Ordinal);
    }
}
