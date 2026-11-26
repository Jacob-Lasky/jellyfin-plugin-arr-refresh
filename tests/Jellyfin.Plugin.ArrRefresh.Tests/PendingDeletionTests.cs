using Jellyfin.Plugin.ArrRefresh.Planning;
using Xunit;

namespace Jellyfin.Plugin.ArrRefresh.Tests;

public class PendingDeletionTests
{
    private const string Gone = "/data/media/movies/Hoot (2006) {tmdb-18276}";
    private const string Present = "/data/media/movies/1941 (1979) {tmdb-11519}";

    private static bool Exists(string path) => path == Present;

    [Fact]
    public void A_delete_whose_path_is_still_on_disk_waits_for_it()
    {
        var (now, later) = PendingDeletion.Split([Gone, Present], deletesFiles: true, Exists);
        Assert.Equal([Gone], now);
        Assert.Equal([Present], later);
    }

    [Fact]
    public void An_event_that_deletes_nothing_is_applied_at_once_even_when_its_path_exists()
    {
        var (now, later) = PendingDeletion.Split([Gone, Present], deletesFiles: false, Exists);
        Assert.Equal([Gone, Present], now);
        Assert.Empty(later);
    }
}
