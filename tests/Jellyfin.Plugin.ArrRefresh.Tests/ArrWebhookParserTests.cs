using System;
using System.IO;
using Jellyfin.Plugin.ArrRefresh.Webhooks;
using Xunit;

namespace Jellyfin.Plugin.ArrRefresh.Tests;

public class ArrWebhookParserTests
{
    private const string Movies = "/data/media/movies/Heat (1995) {tmdb-949}";
    private const string Series = "/data/media/tv/Severance (2022) {tvdb-371980}";

    private static ArrEvent ParseFixture(string name)
        => ArrWebhookParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    [Fact]
    public void Radarr_upgrade_yields_new_file_then_replaced_file()
    {
        var e = ParseFixture("radarr-download-upgrade.json");
        Assert.Equal(ArrSource.Radarr, e.Source);
        Assert.Equal(ArrEventKind.Apply, e.Kind);
        Assert.Equal("Radarr", e.InstanceName);
        Assert.Equal(
            [
                Movies + "/Heat (1995) {tmdb-949} [Bluray-2160p][DTS-HD MA 5.1][h265].mkv",
                Movies + "/Heat (1995) {tmdb-949} [Bluray-1080p][AAC 5.1][h265].mkv",
                "/data/usenet/movies/Heat.1995/heat.mkv",
            ],
            e.Paths);
        Assert.Equal(
            [
                new PathReplacement(
                    Movies + "/Heat (1995) {tmdb-949} [Bluray-1080p][AAC 5.1][h265].mkv",
                    Movies + "/Heat (1995) {tmdb-949} [Bluray-2160p][DTS-HD MA 5.1][h265].mkv"),
                new PathReplacement(
                    "/data/usenet/movies/Heat.1995/heat.mkv",
                    Movies + "/Heat (1995) {tmdb-949} [Bluray-2160p][DTS-HD MA 5.1][h265].mkv"),
            ],
            e.Replacements);
    }

    [Fact]
    public void Radarr_rename_yields_previous_and_new_path()
    {
        var e = ParseFixture("radarr-rename.json");
        Assert.Equal(ArrEventKind.Apply, e.Kind);
        Assert.Equal(
            [
                Movies + "/Heat (1995) {tmdb-949} [Bluray-2160p].mkv",
                Movies + "/Heat (1995) {tmdb-949} [Remux-2160p].mkv",
            ],
            e.Paths);
        Assert.Equal([new PathReplacement(Movies + "/Heat (1995) {tmdb-949} [Bluray-2160p].mkv", Movies + "/Heat (1995) {tmdb-949} [Remux-2160p].mkv")], e.Replacements);
    }

    [Fact]
    public void Radarr_file_delete_yields_the_file()
    {
        var e = ParseFixture("radarr-moviefiledelete.json");
        Assert.Equal([Movies + "/Heat.mkv"], e.Paths);
    }

    [Fact]
    public void Radarr_movie_delete_yields_the_folder()
    {
        var e = ParseFixture("radarr-moviedelete.json");
        Assert.Equal(ArrEventKind.Apply, e.Kind);
        Assert.Equal([Movies], e.Paths);
    }

    [Theory]
    [InlineData("radarr-test.json", ArrSource.Radarr, ArrEventKind.Test)]
    [InlineData("radarr-grab.json", ArrSource.Radarr, ArrEventKind.Ignored)]
    [InlineData("sonarr-health.json", ArrSource.Unknown, ArrEventKind.Ignored)]
    public void Events_that_change_no_files_carry_no_paths(string fixture, ArrSource source, ArrEventKind kind)
    {
        var e = ParseFixture(fixture);
        Assert.Equal(source, e.Source);
        Assert.Equal(kind, e.Kind);
        Assert.Empty(e.Paths);
    }

    [Fact]
    public void Sonarr_single_import_yields_the_episode_file()
    {
        var e = ParseFixture("sonarr-download.json");
        Assert.Equal(ArrSource.Sonarr, e.Source);
        Assert.Equal(ArrEventKind.Apply, e.Kind);
        Assert.Equal([Series + "/Season 03/Severance (2022) - S03E01 - Ep [WEBDL-2160p].mkv", "/data/usenet/tv/x.mkv"], e.Paths);
        Assert.Equal([new PathReplacement("/data/usenet/tv/x.mkv", Series + "/Season 03/Severance (2022) - S03E01 - Ep [WEBDL-2160p].mkv")], e.Replacements);
    }

    [Fact]
    public void Sonarr_import_complete_yields_every_episode_file()
    {
        var e = ParseFixture("sonarr-importcomplete.json");
        Assert.Equal([Series + "/Season 03/S03E01.mkv", Series + "/Season 03/S03E02.mkv"], e.Paths);
    }

    [Fact]
    public void Sonarr_rename_yields_previous_and_new_paths_per_file()
    {
        var e = ParseFixture("sonarr-rename.json");
        Assert.Equal(
            [
                Series + "/Season 01/S01E01.mkv",
                Series + "/Season 01/Severance (2022) - S01E01 - Good News About Hell.mkv",
                Series + "/Season 01/S01E02.mkv",
                Series + "/Season 01/Severance (2022) - S01E02 - Half Loop.mkv",
            ],
            e.Paths);
        Assert.Equal(
            [
                new PathReplacement(Series + "/Season 01/S01E01.mkv", Series + "/Season 01/Severance (2022) - S01E01 - Good News About Hell.mkv"),
                new PathReplacement(Series + "/Season 01/S01E02.mkv", Series + "/Season 01/Severance (2022) - S01E02 - Half Loop.mkv"),
            ],
            e.Replacements);
    }

    [Fact]
    public void Sonarr_episode_file_delete_yields_the_file()
    {
        Assert.Equal([Series + "/Season 03/S03E01.mkv"], ParseFixture("sonarr-episodefiledelete.json").Paths);
    }

    [Fact]
    public void Sonarr_series_delete_yields_the_series_folder()
    {
        Assert.Equal([Series], ParseFixture("sonarr-seriesdelete.json").Paths);
    }

    [Fact]
    public void Keys_are_matched_case_insensitively_and_duplicates_dropped()
    {
        var e = ArrWebhookParser.Parse("""{"EventType":"download","Movie":{"FolderPath":"/m/A"},"MovieFile":{"Path":"/m/A/a.mkv"},"DeletedFiles":[{"Path":"/m/A/a.mkv"}]}""");
        Assert.Equal(ArrSource.Radarr, e.Source);
        Assert.Equal(["/m/A/a.mkv"], e.Paths);
    }

    [Fact]
    public void Sonarr_upgrade_pairs_each_replaced_file_with_the_imported_one()
    {
        var e = ArrWebhookParser.Parse("""{"eventType":"Download","series":{"path":"/tv/S"},"episodeFile":{"path":"/tv/S/Season 01/new.mkv"},"deletedFiles":[{"path":"/tv/S/Season 01/old.mkv"},{"path":"/tv/S/Season 01/old2.mkv"}]}""");
        Assert.Equal(
            [new PathReplacement("/tv/S/Season 01/old.mkv", "/tv/S/Season 01/new.mkv"), new PathReplacement("/tv/S/Season 01/old2.mkv", "/tv/S/Season 01/new.mkv")],
            e.Replacements);
    }

    [Fact]
    public void A_manual_import_from_inside_the_library_names_the_old_file_and_pairs_it()
    {
        var e = ArrWebhookParser.Parse("""{"eventType":"Download","movie":{"folderPath":"/m/Film (2018)"},"movieFile":{"path":"/m/Film (2018)/Film (2018).mkv","sourcePath":"/m/Film (2018) {imdbid-/Film.mkv"}}""");
        Assert.Equal(["/m/Film (2018)/Film (2018).mkv", "/m/Film (2018) {imdbid-/Film.mkv"], e.Paths);
        Assert.Equal([new PathReplacement("/m/Film (2018) {imdbid-/Film.mkv", "/m/Film (2018)/Film (2018).mkv")], e.Replacements);
    }

    [Fact]
    public void A_source_path_equal_to_the_imported_path_adds_nothing()
    {
        var e = ArrWebhookParser.Parse("""{"eventType":"Download","movie":{"folderPath":"/m/A"},"movieFile":{"path":"/m/A/a.mkv","sourcePath":"/m/A/a.mkv"}}""");
        Assert.Equal(["/m/A/a.mkv"], e.Paths);
        Assert.Empty(e.Replacements);
    }

    [Fact]
    public void Sonarr_import_without_a_file_is_ignored_rather_than_refreshing_the_series()
    {
        var e = ArrWebhookParser.Parse("""{"eventType":"Download","series":{"path":"/tv/S"}}""");
        Assert.Equal(ArrEventKind.Ignored, e.Kind);
        Assert.Empty(e.Paths);
    }

    [Fact]
    public void Import_without_a_file_falls_back_to_the_movie_folder()
    {
        var e = ArrWebhookParser.Parse("""{"eventType":"Download","movie":{"folderPath":"/m/A"},"movieFile":null}""");
        Assert.Equal(["/m/A"], e.Paths);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"movie\":{}}")]
    public void Malformed_bodies_are_rejected(string body)
    {
        Assert.Throws<FormatException>(() => ArrWebhookParser.Parse(body));
    }
}
