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

| Arr event | Movies | TV |
| --- | --- | --- |
| Import, upgrade | Resolves that one movie folder the way a scan would, creates the new Movie, removes the item for the replaced file | Refreshes the episode's Season (or its Series for a new season) |
| Rename | Replaces the old item with the new one and carries watch state, playlists and collection membership across | Refreshes the Season; Jellyfin carries watch state |
| File delete | Removes the item whose file is gone | Refreshes the Season |
| Movie / series delete | Removes the items under that folder | Removes the Series |
| New series | | Creates the Series, then refreshes it, which adds its seasons and episodes |
| Test, Grab, Health, ... | Answers 200, does nothing | Answers 200, does nothing |

Then it queues a metadata refresh for each new or changed item, at high priority.

Rules it keeps:

- **Library roots are never refreshed.** A path that is a library root, or that lies outside every
  library, is reported as skipped.
- **The disk decides, not the payload.** An item is removed only when its own file or folder no
  longer exists and its library location is reachable. A share that has gone offline changes
  nothing.
- **Nothing is written until the whole event is worked out.** Then
  items are created, pairs are rerouted, and only then are old items removed.
- **One event at a time**, so an import and the rename that follows it cannot interleave.

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

- An item whose file still exists but no longer resolves the same way (for example, a second file
  dropped into a movie folder becomes an alternate version) is left for the scheduled library
  scan, and the step says so.
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
was validated. CI runs both.

To release, bump `<Version>` in `Directory.Build.props`, commit to `main`, and push an annotated
tag `v<Version>`. Its message becomes the changelog. The Release workflow builds, tests, attaches
the zip to a GitHub release and adds the version to `manifest.json`.

## License

GPL-3.0, as required for code that links Jellyfin's server libraries.
