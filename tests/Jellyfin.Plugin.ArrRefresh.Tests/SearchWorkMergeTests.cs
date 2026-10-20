using System;
using Jellyfin.Plugin.ArrRefresh.Search;
using Xunit;
using Due = Jellyfin.Plugin.ArrRefresh.Search.SearchIndexSync.Due;
using Work = Jellyfin.Plugin.ArrRefresh.Search.SearchIndexSync.Work;

namespace Jellyfin.Plugin.ArrRefresh.Tests;

public class SearchWorkMergeTests
{
    private static readonly DateTime T = new(2026, 10, 20, 17, 0, 0, DateTimeKind.Utc);

    private static Due D(Work kind, int seconds = 0) => new(kind, T.AddSeconds(seconds), 0, T.AddHours(1));

    [Fact]
    public void A_write_after_a_delete_wins_because_the_item_came_back()
    {
        Assert.Equal(Work.Upsert, SearchIndexSync.Merge(D(Work.Delete, 4), D(Work.Upsert, 3)).Kind);
    }

    [Fact]
    public void A_delete_after_a_write_wins()
    {
        Assert.Equal(Work.Delete, SearchIndexSync.Merge(D(Work.Upsert, 3), D(Work.Delete, 4)).Kind);
    }

    [Fact]
    public void A_verification_never_replaces_a_pending_write_or_delete()
    {
        Assert.Equal(Work.Upsert, SearchIndexSync.Merge(D(Work.Upsert), D(Work.Verify, 45)).Kind);
        Assert.Equal(Work.Delete, SearchIndexSync.Merge(D(Work.Delete), D(Work.Verify, 45)).Kind);
    }

    [Fact]
    public void A_newer_verification_replaces_an_older_one()
    {
        var newer = D(Work.Verify, 180);
        Assert.Same(newer, SearchIndexSync.Merge(D(Work.Verify, 45), newer));
    }
}
