using System;
using Jellyfin.Plugin.ArrRefresh.Planning;
using Xunit;

namespace Jellyfin.Plugin.ArrRefresh.Tests;

public class RemovedItemRuleTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Saved_back_with_no_parent_or_owner_and_its_file_gone_is_removed(bool locationOnline)
    {
        Assert.Equal(RemovedItemVerdict.Remove, RemovedItemRule.Judge(attached: false, onDisk: false, locationOnline));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Saved_back_with_no_parent_or_owner_over_a_file_that_exists_is_recreated(bool locationOnline)
    {
        Assert.Equal(RemovedItemVerdict.Recreate, RemovedItemRule.Judge(attached: false, onDisk: true, locationOnline));
    }

    [Fact]
    public void In_the_tree_with_its_file_is_a_real_item_again()
    {
        Assert.Equal(RemovedItemVerdict.Keep, RemovedItemRule.Judge(attached: true, onDisk: true, locationOnline: true));
    }

    [Fact]
    public void In_the_tree_with_its_file_gone_and_its_share_mounted_is_removed()
    {
        Assert.Equal(RemovedItemVerdict.Remove, RemovedItemRule.Judge(attached: true, onDisk: false, locationOnline: true));
    }

    [Fact]
    public void In_the_tree_with_its_share_offline_waits()
    {
        Assert.Equal(RemovedItemVerdict.Wait, RemovedItemRule.Judge(attached: true, onDisk: false, locationOnline: false));
    }

    [Fact]
    public void The_most_specific_library_location_holds_a_path()
    {
        string[] locations = ["/data/media", "/data/media/movies", "/data/media/movies-4k"];
        Assert.Equal("/data/media/movies", PathUtil.LocationOf("/data/media/movies/A/a.mkv", locations));
        Assert.Equal("/data/media/movies-4k", PathUtil.LocationOf("/data/media/movies-4k/A/a.mkv", locations));
        Assert.Equal("/data/media", PathUtil.LocationOf("/data/media/tv/S", locations));
        Assert.Null(PathUtil.LocationOf("/other/x", locations));
    }

    [Fact]
    public void A_removed_id_is_looked_at_after_one_ten_and_twenty_nine_minutes_then_dropped()
    {
        var s = new RemovedItemSchedule();
        var id = Guid.NewGuid();
        s.Removed([id], T0);
        Assert.Empty(s.TakeDue(T0.AddSeconds(59)));
        Assert.Equal(T0.AddMinutes(1), s.NextDue());
        Assert.Equal([id], s.TakeDue(T0.AddMinutes(1)));
        Assert.Empty(s.TakeDue(T0.AddMinutes(9)));
        Assert.Equal([id], s.TakeDue(T0.AddMinutes(10)));
        Assert.Equal([id], s.TakeDue(T0.AddMinutes(29)));
        Assert.Empty(s.TakeDue(T0.AddMinutes(31)));
        Assert.Null(s.NextDue());
    }

    [Fact]
    public void A_reported_save_is_due_five_seconds_later_once_however_often_it_is_reported()
    {
        var s = new RemovedItemSchedule();
        var id = Guid.NewGuid();
        s.Removed([id], T0);
        Assert.True(s.Saved(id, Guid.Empty, Guid.Empty, T0.AddSeconds(2)));
        Assert.True(s.Saved(id, Guid.Empty, Guid.Empty, T0.AddSeconds(3)));
        Assert.Equal(T0.AddSeconds(7), s.NextDue());
        Assert.Empty(s.TakeDue(T0.AddSeconds(6)));
        Assert.Equal([id], s.TakeDue(T0.AddSeconds(7)));
        Assert.Empty(s.TakeDue(T0.AddSeconds(8)));
    }

    [Fact]
    public void A_save_of_a_child_or_extra_of_a_removed_item_is_watched_and_an_unrelated_one_is_not()
    {
        var s = new RemovedItemSchedule();
        var series = Guid.NewGuid();
        var episode = Guid.NewGuid();
        var extra = Guid.NewGuid();
        s.Removed([series], T0);
        Assert.True(s.Saved(episode, series, Guid.Empty, T0.AddSeconds(1)));
        Assert.True(s.Saved(extra, Guid.Empty, series, T0.AddSeconds(1)));
        Assert.False(s.Saved(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, T0.AddSeconds(1)));
        Assert.Equal(2, s.TakeDue(T0.AddSeconds(6)).Count);
    }

    [Fact]
    public void An_expired_id_is_not_watched()
    {
        var s = new RemovedItemSchedule();
        var old = Guid.NewGuid();
        s.Removed([old], T0.AddMinutes(-31));
        Assert.False(s.Saved(old, Guid.Empty, Guid.Empty, T0.AddSeconds(1)));
    }

    [Fact]
    public void Ids_taken_but_not_judged_are_due_again_within_their_watch()
    {
        var s = new RemovedItemSchedule();
        var id = Guid.NewGuid();
        s.Removed([id], T0);
        Assert.Equal([id], s.TakeDue(T0.AddMinutes(29)));
        s.DueAgain([id], T0.AddMinutes(29).AddSeconds(30));
        Assert.Equal(T0.AddMinutes(29).AddSeconds(30), s.NextDue());
        Assert.Equal([id], s.TakeDue(T0.AddMinutes(29).AddSeconds(30)));
        s.DueAgain([id], T0.AddMinutes(31));
        Assert.Empty(s.TakeDue(T0.AddMinutes(31)));
    }
}
