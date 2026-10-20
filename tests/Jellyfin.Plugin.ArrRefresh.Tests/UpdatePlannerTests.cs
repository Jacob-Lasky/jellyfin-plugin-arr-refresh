using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ArrRefresh.Planning;
using Xunit;

namespace Jellyfin.Plugin.ArrRefresh.Tests;

public class UpdatePlannerTests
{
    private const string Movies = "/data/media/movies";
    private const string Tv = "/data/media/tv";
    private const string Heat = Movies + "/Heat (1995) {tmdb-949}";
    private const string Show = Tv + "/Severance (2022) {tvdb-371980}";

    private sealed class FakeView : ILibraryView
    {
        public List<string> Locations { get; } = [Movies, Tv, "/data/media/iptv-recordings", "/data/media/iptv-recordings/TV_Shows"];

        public Dictionary<string, FolderRef> Folders { get; } = new(StringComparer.Ordinal);

        public HashSet<string> OnDisk { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> LibraryLocations => Locations;

        public FolderRef? FindFolderItem(string path) => Folders.GetValueOrDefault(path);

        public bool Exists(string path) => OnDisk.Contains(path);

        public FolderRef AddFolder(string path, string type, bool onDisk = true)
        {
            var f = new FolderRef(Guid.NewGuid(), path, type, System.IO.Path.GetFileName(path));
            Folders[path] = f;
            if (onDisk)
            {
                OnDisk.Add(path);
            }

            return f;
        }
    }

    private static ReconcileChildStep Reconcile(PlanStep step) => Assert.IsType<ReconcileChildStep>(step);

    [Fact]
    public void A_movie_rename_reconciles_only_that_movie_folder_under_the_library_root()
    {
        var step = Reconcile(Assert.Single(new UpdatePlanner(new FakeView()).Plan([Heat + "/old.mkv", Heat + "/new.mkv"])));
        Assert.Equal(Movies, step.ParentPath);
        Assert.Equal(Movies, step.LibraryLocation);
        Assert.Equal(Heat, step.ChildPath);
        Assert.Equal([Heat + "/old.mkv", Heat + "/new.mkv"], step.Paths);
    }

    [Fact]
    public void A_movie_moved_to_a_new_folder_reconciles_both_folders()
    {
        var plan = new UpdatePlanner(new FakeView()).Plan([Movies + "/Heat (1994)/a.mkv", Heat + "/a.mkv"]);
        Assert.Equal([Movies + "/Heat (1994)", Heat], plan.Select(s => Reconcile(s).ChildPath));
    }

    [Fact]
    public void Episode_files_in_an_existing_season_are_reconciled_one_by_one_under_that_season()
    {
        var view = new FakeView();
        view.AddFolder(Show, "Series");
        view.AddFolder(Show + "/Season 03", "Season");

        var plan = new UpdatePlanner(view).Plan([Show + "/Season 03/e1.mkv", Show + "/Season 03/e2.mkv"]);
        Assert.Equal(2, plan.Count);
        Assert.All(plan, s => Assert.Equal(Show + "/Season 03", Reconcile(s).ParentPath));
        Assert.Equal([Show + "/Season 03/e1.mkv", Show + "/Season 03/e2.mkv"], plan.Select(s => Reconcile(s).ChildPath));
    }

    [Fact]
    public void An_episode_in_a_new_season_reconciles_that_season_under_the_series_not_the_whole_series()
    {
        var view = new FakeView();
        view.AddFolder(Show, "Series");

        var step = Reconcile(Assert.Single(new UpdatePlanner(view).Plan([Show + "/Season 04/e1.mkv", Show + "/Season 04/e2.mkv"])));
        Assert.Equal(Show, step.ParentPath);
        Assert.Equal(Show + "/Season 04", step.ChildPath);
        Assert.Equal(2, step.Paths.Count);
    }

    [Fact]
    public void A_brand_new_series_reconciles_its_folder_under_the_library_root()
    {
        var step = Reconcile(Assert.Single(new UpdatePlanner(new FakeView()).Plan([Show + "/Season 01/e1.mkv"])));
        Assert.Equal(Tv, step.ParentPath);
        Assert.Equal(Show, step.ChildPath);
    }

    [Fact]
    public void A_deleted_series_whose_folder_item_remains_is_reconciled_under_the_root()
    {
        var view = new FakeView();
        view.AddFolder(Show, "Series", onDisk: false);
        view.AddFolder(Show + "/Season 01", "Season", onDisk: false);

        var step = Reconcile(Assert.Single(new UpdatePlanner(view).Plan([Show])));
        Assert.Equal(Tv, step.ParentPath);
        Assert.Equal(Show, step.ChildPath);
    }

    [Fact]
    public void A_deleted_season_is_reconciled_under_the_series_that_still_exists()
    {
        var view = new FakeView();
        view.AddFolder(Show, "Series");
        view.AddFolder(Show + "/Season 01", "Season", onDisk: false);

        var step = Reconcile(Assert.Single(new UpdatePlanner(view).Plan([Show + "/Season 01/e1.mkv"])));
        Assert.Equal(Show, step.ParentPath);
        Assert.Equal(Show + "/Season 01", step.ChildPath);
    }

    [Fact]
    public void A_named_live_folder_is_refreshed_and_a_season_inside_a_named_series_is_folded_in()
    {
        var view = new FakeView();
        var series = view.AddFolder(Show, "Series");
        view.AddFolder(Show + "/Season 01", "Season");

        var step = Assert.IsType<RefreshFolderStep>(Assert.Single(new UpdatePlanner(view).Plan([Show + "/Season 01", Show])));
        Assert.Equal(series, step.Folder);
        Assert.Equal([Show, Show + "/Season 01"], step.Paths.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Nested_library_locations_use_the_innermost()
    {
        var step = Reconcile(Assert.Single(new UpdatePlanner(new FakeView()).Plan(["/data/media/iptv-recordings/TV_Shows/News/e.ts"])));
        Assert.Equal("/data/media/iptv-recordings/TV_Shows", step.LibraryLocation);
        Assert.Equal("/data/media/iptv-recordings/TV_Shows/News", step.ChildPath);
    }

    [Theory]
    [InlineData(Movies, "is a library root")]
    [InlineData(Movies + "/", "is a library root")]
    [InlineData(Movies + "/Heat/../", "is a library root")]
    [InlineData("/data/media/moviesX/A/a.mkv", "not inside any Jellyfin library location")]
    [InlineData("/elsewhere/a.mkv", "not inside any Jellyfin library location")]
    [InlineData("relative/a.mkv", "not an absolute path")]
    [InlineData("C:\\testpath", "not an absolute path")]
    public void Paths_that_would_touch_a_whole_library_or_nothing_are_skipped(string path, string reason)
    {
        var step = Assert.IsType<SkipStep>(Assert.Single(new UpdatePlanner(new FakeView()).Plan([path])));
        Assert.StartsWith(reason, step.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Dot_dot_segments_are_resolved_before_matching()
    {
        var step = Reconcile(Assert.Single(new UpdatePlanner(new FakeView()).Plan([Tv + "/../movies/Heat/a.mkv"])));
        Assert.Equal(Movies + "/Heat", step.ChildPath);
    }

    [Fact]
    public void A_file_directly_in_the_library_root_is_its_own_entry()
    {
        var step = Reconcile(Assert.Single(new UpdatePlanner(new FakeView()).Plan([Movies + "/loose.mkv"])));
        Assert.Equal(Movies + "/loose.mkv", step.ChildPath);
    }

    [Fact]
    public void No_step_ever_targets_a_library_root()
    {
        var view = new FakeView();
        view.AddFolder(Show, "Series");
        view.AddFolder(Show + "/Season 01", "Season");
        var paths = new[] { Movies, Tv, Heat + "/a.mkv", Show + "/Season 01/e.mkv", Show, Show + "/Season 09/x.mkv", "/data/media/iptv-recordings/TV_Shows", "/data/media/iptv-recordings/x/y" };
        var plan = new UpdatePlanner(view).Plan(paths);
        Assert.NotEmpty(plan.OfType<ReconcileChildStep>());
        foreach (var step in plan)
        {
            var target = step switch
            {
                RefreshFolderStep r => r.Folder.Path,
                ReconcileChildStep t => t.ChildPath,
                _ => null,
            };
            Assert.DoesNotContain(target, view.Locations);
        }
    }
}
