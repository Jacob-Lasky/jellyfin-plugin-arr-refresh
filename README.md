# Arr Refresh for Jellyfin

A Jellyfin server plugin that takes Sonarr's and Radarr's stock **Webhook** connection and updates
only the movie or episode that changed. It never refreshes a whole library.

## Why

Sonarr's and Radarr's built-in Emby/Jellyfin connection calls `POST /Library/Media/Updated` with a
folder path. Jellyfin's `FileRefresher` then looks for the nearest item stored at that path. In a
movies library that walk always reaches the library root, because a movie folder is not an item;
the Movie item's path is its video file
([jellyfin/jellyfin#16172](https://github.com/jellyfin/jellyfin/issues/16172)). After a rename the
new file is not in the database yet and the old one is gone, so that climbs to the root too.
Validating the root re-lists every movie folder. On a large library, or one on a union filesystem
such as Unraid's `/mnt/user`, one import or rename becomes a walk over the whole library, and a
night of upgrades becomes a hundred of them.

This plugin resolves each path the arr reports on its own and touches nothing else.

## What it does per event

Every path in the payload is reconciled under its **nearest live library folder**: the episode's
Season, the Series for a new season, or the library root for a movie folder or a new series. Only
that one entry is resolved, the way a library scan would resolve it, and compared with what the
database holds for it:

| Arr event | What changes in Jellyfin |
| --- | --- |
| Import | The new Movie, Episode, Season or Series is created |
| Upgrade | The new file's item is created and the replaced file's item removed |
| Rename (file, or a whole movie or series folder) | The item at the old path is replaced by one at the new path |
| File delete | The item whose file is gone is removed |
| Movie / series delete | The items under that folder are removed |
| An extra version dropped beside a movie | Recorded on the existing movie as another version |
| A version that outranks the current primary (2160p beside 1080p) | Left for the scheduled scan, which promotes versions; files that are gone are still removed |
| Test, Grab, Health, ... | Nothing; answers 200 |

When an item is replaced, its watch state, playlist entries and collection memberships move to
the new item. For a series or season that moved folders, that includes each of its episodes.
Which new item replaces which old one comes from the arr itself (a rename's previous and new path,
an upgrade's replaced and imported file), then from a shared id in the path. An ambiguous match,
such as one multi-episode file replacing two episodes, carries nothing, and Jellyfin's own
id-based matching decides. Then
a metadata refresh is queued for each new or changed item, at high priority.

### Search (Meilisearch plugin)

The [Meilisearch plugin](https://github.com/arnesacnussem/jellyfin-plugin-meilisearch) rebuilds its
index only after a full library scan and on its own schedule. Jellyfin uses Meilisearch's hits
whenever there are any, so a new movie whose title shares a word with other titles would stay
unsearchable until that rebuild. When the Meilisearch plugin is installed, Arr Refresh therefore also
writes the search documents of exactly the items it changed (and of their new seasons and episodes)
within seconds:

- through Meilisearch's API, with the Meilisearch plugin's own URL, key and index name (including its
  `MEILI_URL`/`MEILI_MASTER_KEY` overrides and its default index name);
- in the same 19-field shape that plugin writes, and only while the live index reports that shape;
  if a new version of that plugin changes it, Arr Refresh stops writing and says so in the log;
- never as a full reindex, and never for items it did not touch;
- checked again 45 seconds, 3 minutes and 15 minutes later, so a document that a concurrent rebuild
  swapped away is written again, and a document left behind for a removed item is deleted;
- retried with backoff for an hour while Meilisearch is unreachable.

Turn it off on the plugin's settings page.

Rules it keeps:

- **Library roots are never refreshed.** A path that is a library root, or that lies outside every
  library, is reported as skipped.
- **The disk decides, not the payload.** An item is removed only when its own file or folder no
  longer exists, is checked again immediately before the removal, and the folder it sits in is
  reachable. A share that has gone offline changes nothing.
- **Nothing is written until the whole event is worked out**, and once writing starts it finishes,
  even if the arr hangs up: new items are created, replacements inherit what the old items had, and
  only then are old items removed.
- **One event at a time**, so an import and the rename that follows it cannot interleave.
- **A removed item stays removed.** Jellyfin writes an item back, with no parent, when a metadata
  refresh of it was still running as it was removed (an import followed seconds later by an upgrade
  does this). Such a row is invisible in the library but found by search. For 30 minutes after a
  removal the plugin watches for that and removes the item again, or, when a file exists at its path
  again, reconciles the path so the item gets its parent back.

## Install

1. In Jellyfin, open **Dashboard > Plugins > Repositories**, add
   `https://raw.githubusercontent.com/Jacob-Lasky/jellyfin-plugin-arr-refresh/main/manifest.json`,
   then install **Arr Refresh** from the Catalog and restart Jellyfin.
2. Create an API key under **Dashboard > API Keys**.
3. In each Sonarr and Radarr: **Settings > Connect > + > Webhook**.
   - URL: `http://<jellyfin>:8096/ArrRefresh/Webhook`
   - Method: `POST`
   - Headers: `Authorization` = `MediaBrowser Token="<API key>"`
   - Triggers: On File Import, On File Upgrade, On Rename, On Movie/Series Delete, On
     Movie/Episode File Delete. The others are accepted and ignored.
   - Leave **On Movie File Delete For Upgrade** and **On Episode File Delete For Upgrade** off.
     They fire before the new file is in place, so the item would be removed by one event and
     re-created by the next, losing its playlist and collection entries. The upgrade's own import
     event already names the files it replaced.
4. Turn off **Update Library** on any existing Emby/Jellyfin connection in the same arr, so each
   change is not sent twice.

Requires Jellyfin 12.2. If the arr and Jellyfin see the media at different paths, add mappings
on the plugin's settings page, one per line: `/path/in/arr => /path/in/jellyfin`.

## API

Both endpoints need an administrator token or an API key.

- `POST /ArrRefresh/Webhook` takes the arr's webhook body unchanged.
- `POST /ArrRefresh/Paths` with `{"Paths": ["/data/media/movies/Heat (1995)/Heat.mkv", ...]}`
  runs the same code for any paths: re-run a webhook that failed, or repair one item by hand.

Both return what each step did:

```json
{"Status": "ok", "Source": "Radarr", "EventType": "Rename", "Steps": [
  {"Action": "ReconcileChild", "Target": "/data/media/movies/Heat (1995)", "Outcome": "ok",
   "Created": ["Movie 'Heat' (.../Heat [Remux-2160p].mkv)"],
   "Removed": ["Movie 'Heat' (.../Heat [Bluray-2160p].mkv)"],
   "Refreshed": ["Movie 'Heat' (.../Heat [Remux-2160p].mkv)"]}]}
```

Every step is logged with the prefix `ArrRefresh`.

## Limits

- A file that is missing only because its disk dropped out of a union filesystem (Unraid's
  `/mnt/user`, mergerfs) while the folder above it is still there looks deleted, and an arr event
  naming it removes its item, as Jellyfin's own scan would. Jellyfin keeps its watch state for
  90 days and gives it back when the file reappears and the item is re-created.
- An item whose file still exists but no longer resolves the same way is left for the scheduled
  library scan, and the step says so. So is a new file that would take over as a movie's primary
  version.
- Paths are matched as the library locations spell them: case-insensitively on Windows and macOS,
  exactly elsewhere. A path that reaches the media through a different mount or a symlink needs a
  path mapping.
- When two libraries share one folder, Jellyfin resolves items there with the first library's
  type and options, during a scan and here alike.
- A library that was empty at its last scan has no root item yet, so the first import into it is
  reported as an error ("run a library scan once") and nothing changes; scan it once.
- **Moving a movie or series folder in the arr sends no event.** Radarr's and Sonarr's Connect
  notifications have no handler for a folder move (`MovieMovedEvent`, `SeriesMovedEvent`; checked
  in Radarr 6.4.4 and Sonarr 4.0.20), so a move made from the arr (a changed root folder or path,
  or the mass editor rebuilding a folder name) reaches neither this plugin nor any other Connect
  target. Tell the plugin yourself afterwards, with the old and new file and the pair, so the item
  keeps its watch state:
  `POST /ArrRefresh/Paths` `{"Paths": [old, new], "Replacements": [{"Old": old, "New": new}]}`.
  For a series, pass every moved episode file the same way (old and new path, and the pair).
- Lidarr and Readarr are not handled.

## Development

```sh
dotnet build -c Release
dotnet test -c Release                       # payload, mapping and planning unit tests
python3 tests/integration/sandbox_test.py \
  src/Jellyfin.Plugin.ArrRefresh/bin/Release/net10.0/Jellyfin.Plugin.ArrRefresh.dll
```

The integration test starts a throwaway Jellyfin 12.2 container, plays every webhook above
against generated media, and checks the library after each one, including that no library root
was validated. CI runs both. Its containers and network are removed however the run ends, SIGKILL included (a
reaper process watches the test), and a later run sweeps any run whose reaper also died. To list leftovers: `docker ps -a --filter label=arr-refresh-it.owner`.

To release, bump `<Version>` in `Directory.Build.props`, commit to `main`, and push an annotated
tag `v<Version>`. Its message becomes the changelog. The Release workflow builds, tests, attaches
the zip to a GitHub release and adds the version to `manifest.json`.

## License

GPL-3.0, as required for code that links Jellyfin's server libraries.
