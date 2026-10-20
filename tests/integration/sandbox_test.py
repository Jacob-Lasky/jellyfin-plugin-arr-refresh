#!/usr/bin/env python3
"""End-to-end test against a real, throwaway Jellyfin server.

Usage: sandbox_test.py <path to Jellyfin.Plugin.ArrRefresh.dll>

Starts a Jellyfin container with no bind mounts (media, config and the plugin all live inside it,
copied in with `docker cp` and written with `docker exec`), so it runs the same from a laptop, CI,
or a container that talks to a remote Docker daemon. Then it plays the webhooks Sonarr and Radarr
send for import, upgrade, rename, file delete and whole-item delete, and checks the library after
each one. The container is removed at the end, pass or fail.

Assertions are on item types, paths, watch state and collection membership, never on names,
because names come from TMDb/TVDb and the sandbox may or may not have network access.
"""
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request

IMAGE = "jellyfin/jellyfin:12.2.20261005-225228"  # the 12.2.0 release build; matches targetAbi
NAME = "arr-refresh-it-%d" % int(time.time())
MOVIES, TV = "/data/media/movies", "/data/media/tv"
HEAT = MOVIES + "/Heat (1995) {tmdb-949}"
ALIEN = MOVIES + "/Alien (1979) {tmdb-348}"
DUNE = MOVIES + "/Dune (2021) {tmdb-438631}"
SEV = TV + "/Severance (2022) {tvdb-371980}"
SHARE = "/data/media/share-movies"  # a library whose location the test takes offline
TERM = SHARE + "/The Terminal (2004) {tmdb-594}"
PLUGIN_ID = "89c19d1d-6a41-4cc3-aad3-d72b1d32039f"
UP = MOVIES + "/Up (2009) {tmdb-14160}"  # gains an alternate version
BRAZIL = MOVIES + "/Brazil (1985)"  # no id anywhere in its path, so no shared user-data key
CLUE = MOVIES + "/Clue (1985)"  # deleted while an unrelated movie is added, in one request
ANDOR = TV + "/Andor (2022) {tvdb-393189}"

base = token = user_id = None
failures = []


def sh(*args, check=True):
    return subprocess.run(args, check=check, capture_output=True, text=True).stdout.strip()


def ex(script):
    """Run a shell script inside the sandbox as the Jellyfin user."""
    sh("docker", "exec", "-u", "99:100", NAME, "sh", "-c", script)


def make_video(path):
    ex('mkdir -p "$(dirname "%s")" && /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -y -f lavfi '
       '-i testsrc=duration=2:size=160x120:rate=24 -c:v libx264 -preset ultrafast "%s"' % (path, path))


def api(method, path, body=None, auth=True, expect=None):
    req = urllib.request.Request(base + path, method=method)
    if auth:
        req.add_header("Authorization", 'MediaBrowser Token="%s"' % token)
    else:
        req.add_header("Authorization", 'MediaBrowser Client="it", Device="it", DeviceId="it", Version="1"')
    data = None
    if body is not None:
        data = (body if isinstance(body, str) else json.dumps(body)).encode()
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, data, timeout=60) as r:
            raw, status = r.read(), r.status
    except urllib.error.HTTPError as e:
        raw, status = e.read(), e.code
    if expect is not None and status != expect:
        raise AssertionError("%s %s -> %d, expected %d: %s" % (method, path, status, expect, raw[:300]))
    return status, (json.loads(raw) if raw[:1] in (b"{", b"[") else None)


def webhook(payload):
    return api("POST", "/ArrRefresh/Webhook", payload, expect=200)[1]


def items(types):
    q = "/Items?Recursive=true&IncludeItemTypes=%s&Fields=Path&userId=%s" % (types, user_id)
    return api("GET", q, expect=200)[1]["Items"]


def wait_for(description, predicate, timeout=60):
    deadline = time.time() + timeout
    while time.time() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(1)
    failures.append(description)
    print("FAIL", description)
    return None


def check(description, condition):
    print("ok  " if condition else "FAIL", description)
    if not condition:
        failures.append(description)


def paths_of(types):
    return {i["Path"] for i in items(types)}


def root_validations():
    """Lines showing a library root being re-validated: the walk this plugin exists to prevent."""
    out = subprocess.run(["docker", "logs", NAME], capture_output=True, text=True)
    log = out.stdout + out.stderr
    return [line for line in log.splitlines() if "will be refreshed" in line or "Validating media library" in line]


def setup(dll):
    global base, token, user_id
    sh("docker", "create", "--name", NAME, "--user", "99:100", "--memory=2g", "--memory-swap=2g", IMAGE)
    sh("docker", "cp", dll, NAME + ":/tmp/plugin.dll")
    sh("docker", "start", NAME)
    ex("mkdir -p /config/plugins/ArrRefresh_it && cp /tmp/plugin.dll /config/plugins/ArrRefresh_it/")
    sh("docker", "exec", "-u", "0", NAME, "sh", "-c", "mkdir -p %s %s %s && chown -R 99:100 /data" % (MOVIES, TV, SHARE))
    for p in [HEAT + "/Heat (1995) {tmdb-949} [Bluray-1080p].mkv",
              ALIEN + "/Alien (1979) {tmdb-348} [Bluray-1080p].mkv",
              SEV + "/Season 01/Severance (2022) - S01E01 - Good News About Hell.mkv",
              SEV + "/Season 01/Severance (2022) - S01E02 - Half Loop.mkv",
              TERM + "/The Terminal (2004) {tmdb-594} [DVD].mkv",
              UP + "/Up (2009) {tmdb-14160} - 1080p.mkv",
              BRAZIL + "/Brazil (1985).mkv",
              CLUE + "/Clue (1985).mkv"]:
        make_video(p)
    sh("docker", "restart", "-t", "30", NAME)  # load the plugin copied in after the first start
    ip = sh("docker", "inspect", NAME, "--format", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}")
    base = "http://%s:8096" % ip
    wait_for("server answers", lambda: _public_ok(), timeout=120)
    api("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"}, auth=False)
    api("GET", "/Startup/User", auth=False)
    api("POST", "/Startup/User", {"Name": "admin", "Password": "it-only"}, auth=False)
    api("POST", "/Startup/Complete", auth=False)
    token = api("POST", "/Users/AuthenticateByName", {"Username": "admin", "Pw": "it-only"}, auth=False, expect=200)[1]["AccessToken"]
    user_id = api("GET", "/Users", expect=200)[1][0]["Id"]
    plugins = {p["Name"]: p["Status"] for p in api("GET", "/Plugins", expect=200)[1]}
    check("plugin loaded and active", plugins.get("Arr Refresh") == "Active")
    for name, ctype, path in [("Movies", "movies", MOVIES), ("TV", "tvshows", TV), ("Share", "movies", SHARE)]:
        api("POST", "/Library/VirtualFolders?name=%s&collectionType=%s&paths=%s&refreshLibrary=false" % (name, ctype, path),
            {"LibraryOptions": {"EnableRealtimeMonitor": False, "PathInfos": [{"Path": path}]}}, expect=204)
    api("POST", "/Library/Refresh", expect=204)
    wait_for("initial scan finds 6 movies and 2 episodes",
             lambda: len(items("Movie")) == 6 and len(items("Episode")) == 2, timeout=180)


def _public_ok():
    # 12.2 boots a temporary setup server that answers before the real one binds and then goes
    # away for a moment, so one 200 is not readiness: require three in a row.
    for _ in range(3):
        try:
            with urllib.request.urlopen(base + "/System/Info/Public", timeout=5) as r:
                if r.status != 200:
                    return False
        except OSError:
            return False
        time.sleep(1)
    return True


def run():
    status, _ = api("POST", "/ArrRefresh/Webhook", {"eventType": "Test", "movie": {}}, auth=False)
    check("webhook without credentials is refused (401)", status == 401)
    check("Test event answers test-ok", webhook({"eventType": "Test", "movie": {"folderPath": "C:\\testpath"}})["Status"] == "test-ok")
    check("Grab is ignored", webhook({"eventType": "Grab", "movie": {"folderPath": HEAT}})["Status"] == "ignored")
    for bad in ("not json", "[1]", {"movie": {}}):
        status, body = api("POST", "/ArrRefresh/Webhook", bad)
        check("malformed body %r is rejected with 400" % (bad,), status == 400 and body["Status"] == "error")
    baseline = len(root_validations())

    heat_id = next(i["Id"] for i in items("Movie") if i["Path"].startswith(HEAT))
    api("POST", "/UserPlayedItems/%s?userId=%s" % (heat_id, user_id), expect=200)
    coll = api("POST", "/Collections?name=Crime&ids=%s" % heat_id, expect=200)[1]["Id"]

    # Radarr rename: same folder, new file name. Watch state and the collection must follow.
    old, new = HEAT + "/Heat (1995) {tmdb-949} [Bluray-1080p].mkv", HEAT + "/Heat (1995) {tmdb-949} [Bluray-1080p][h264].mkv"
    ex('mv "%s" "%s"' % (old, new))
    webhook({"eventType": "Rename", "instanceName": "Radarr", "movie": {"folderPath": HEAT},
             "renamedMovieFiles": [{"path": new, "previousPath": old}]})
    check("rename: library has the new path and not the old", wait_for("rename visible", lambda: new in paths_of("Movie") and old not in paths_of("Movie")))
    renamed = next(i for i in items("Movie") if i["Path"] == new)
    check("rename: watch state carried over", renamed["UserData"]["Played"])
    check("rename: collection membership carried over",
          [i["Path"] for i in api("GET", "/Items?ParentId=%s&Fields=Path" % coll, expect=200)[1]["Items"]] == [new])

    # Radarr import of a new movie.
    dune = DUNE + "/Dune (2021) {tmdb-438631} [WEBDL-2160p].mkv"
    make_video(dune)
    webhook({"eventType": "Download", "instanceName": "Radarr", "movie": {"folderPath": DUNE}, "movieFile": {"path": dune}})
    check("import: new movie appears", wait_for("import visible", lambda: dune in paths_of("Movie")))

    # Radarr upgrade: old file deleted, new file imported, in one event.
    a_old, a_new = ALIEN + "/Alien (1979) {tmdb-348} [Bluray-1080p].mkv", ALIEN + "/Alien (1979) {tmdb-348} [Remux-2160p].mkv"
    make_video(a_new)
    ex('rm "%s"' % a_old)
    upgrade = {"eventType": "Download", "instanceName": "Radarr", "isUpgrade": True, "movie": {"folderPath": ALIEN},
               "movieFile": {"path": a_new}, "deletedFiles": [{"path": a_old}]}
    webhook(upgrade)
    check("upgrade: replaced in place", wait_for("upgrade visible", lambda: a_new in paths_of("Movie") and a_old not in paths_of("Movie")))
    steps = webhook(upgrade)["Steps"]
    check("upgrade replayed: idempotent (nothing created or removed)", all(not s["Created"] and not s["Removed"] for s in steps))

    # Radarr whole-movie delete: only the folder path is in the payload.
    ex('rm -r "%s"' % DUNE)
    webhook({"eventType": "MovieDelete", "instanceName": "Radarr", "movie": {"folderPath": DUNE}, "deletedFiles": True})
    check("movie delete: removed", wait_for("movie delete visible", lambda: dune not in paths_of("Movie")))

    # Sonarr import into an existing season, then a new season.
    e3 = SEV + "/Season 01/Severance (2022) - S01E03 - In Perpetuity.mkv"
    make_video(e3)
    steps = webhook({"eventType": "Download", "instanceName": "Sonarr", "series": {"path": SEV}, "episodeFile": {"path": e3}})["Steps"]
    check("episode import: reconciles only that file", [s["Action"] + ":" + s["Target"] for s in steps] == ["ReconcileChild:" + e3])
    check("episode import: appears", wait_for("episode visible", lambda: e3 in paths_of("Episode")))
    s2 = [SEV + "/Season 02/Severance (2022) - S02E01 - Hello Ms Cobel.mkv", SEV + "/Season 02/Severance (2022) - S02E02 - Goodbye Mrs Selvig.mkv"]
    for p in s2:
        make_video(p)
    webhook({"eventType": "Download", "instanceName": "Sonarr", "series": {"path": SEV}, "episodeFiles": [{"path": p} for p in s2]})
    check("new season: both episodes appear", wait_for("season visible", lambda: set(s2) <= paths_of("Episode")))

    # Sonarr renames two episodes in one event: each keeps its own watch state and playlist entry.
    e1_old = SEV + "/Season 01/Severance (2022) - S01E01 - Good News About Hell.mkv"
    e1_new = SEV + "/Season 01/Severance (2022) - S01E01 - Good News About Hell [WEBDL-1080p].mkv"
    e2_old = SEV + "/Season 01/Severance (2022) - S01E02 - Half Loop.mkv"
    e2_new = SEV + "/Season 01/Severance (2022) - S01E02 - Half Loop [WEBDL-1080p].mkv"
    ids = {i["Path"]: i["Id"] for i in items("Episode")}
    api("POST", "/UserPlayedItems/%s?userId=%s" % (ids[e1_old], user_id), expect=200)
    playlist = api("POST", "/Playlists", {"Name": "Queue", "Ids": [ids[e1_old], ids[e2_old]], "UserId": user_id}, expect=200)[1]["Id"]
    ex('mv "%s" "%s" && mv "%s" "%s"' % (e1_old, e1_new, e2_old, e2_new))
    webhook({"eventType": "Rename", "instanceName": "Sonarr", "series": {"path": SEV},
             "renamedEpisodeFiles": [{"path": e1_new, "previousPath": e1_old}, {"path": e2_new, "previousPath": e2_old}]})
    check("episode rename: new paths only", wait_for("episode rename visible",
          lambda: {e1_new, e2_new} <= paths_of("Episode") and not {e1_old, e2_old} & paths_of("Episode")))
    played = {i["Path"]: i["UserData"]["Played"] for i in items("Episode")}
    check("episode rename: each episode keeps its own watch state", played[e1_new] and not played[e2_new])
    in_playlist = [i["Path"] for i in api("GET", "/Playlists/%s/Items?userId=%s&Fields=Path" % (playlist, user_id), expect=200)[1]["Items"]]
    check("episode rename: both playlist entries carried over, in order", in_playlist == [e1_new, e2_new])
    ex('rm "%s"' % e3)
    webhook({"eventType": "EpisodeFileDelete", "instanceName": "Sonarr", "series": {"path": SEV}, "episodeFile": {"path": e3}})
    check("episode file delete: removed", wait_for("episode delete visible", lambda: e3 not in paths_of("Episode")))

    # Sonarr new series, then the series deleted with its files.
    k1 = ANDOR + "/Season 01/Andor (2022) - S01E01 - Kassa.mkv"
    make_video(k1)
    webhook({"eventType": "Download", "instanceName": "Sonarr", "series": {"path": ANDOR}, "episodeFile": {"path": k1}})
    check("new series: series and episode appear", wait_for("series visible", lambda: ANDOR in paths_of("Series") and k1 in paths_of("Episode")))

    # A multi-episode file replaces E01 (unwatched) and E02 (watched): the one new item has two
    # predecessors, so it inherits neither one's history through the plugin.
    k2 = ANDOR + "/Season 01/Andor (2022) - S01E02 - That Would Be Me.mkv"
    make_video(k2)
    webhook({"eventType": "Download", "instanceName": "Sonarr", "series": {"path": ANDOR}, "episodeFile": {"path": k2}})
    wait_for("second episode visible", lambda: k2 in paths_of("Episode"))
    api("POST", "/UserPlayedItems/%s?userId=%s" % (next(i["Id"] for i in items("Episode") if i["Path"] == k2), user_id), expect=200)
    k12 = ANDOR + "/Season 01/Andor (2022) - S01E01-E02 - Kassa + That Would Be Me.mkv"
    make_video(k12)
    ex('rm "%s" "%s"' % (k1, k2))
    webhook({"eventType": "Download", "instanceName": "Sonarr", "series": {"path": ANDOR}, "isUpgrade": True,
             "episodeFile": {"path": k12}, "deletedFiles": [{"path": k1}, {"path": k2}]})
    check("multi-episode upgrade: one item replaces both", wait_for("multi-episode visible",
          lambda: k12 in paths_of("Episode") and not {k1, k2} & paths_of("Episode")))
    time.sleep(5)
    check("multi-episode upgrade: E02's watch state was not merged onto the combined item",
          not next(i for i in items("Episode") if i["Path"] == k12)["UserData"]["Played"])

    # An episode renumbered into another season: the new season is not the old one.
    s1 = next(i for i in items("Season") if i["Path"] == ANDOR + "/Season 01")
    seasons = api("POST", "/Collections?name=Seasons&ids=%s" % s1["Id"], expect=200)[1]["Id"]
    k12b = ANDOR + "/Season 02/Andor (2022) - S02E01-E02 - Kassa + That Would Be Me.mkv"
    ex('mkdir -p "%s/Season 02" && mv "%s" "%s" && rmdir "%s/Season 01"' % (ANDOR, k12, k12b, ANDOR))
    webhook({"eventType": "Rename", "instanceName": "Sonarr", "series": {"path": ANDOR},
             "renamedEpisodeFiles": [{"path": k12b, "previousPath": k12}]})
    check("season renumber: new season present, old gone", wait_for("season renumber visible",
          lambda: ANDOR + "/Season 02" in paths_of("Season") and ANDOR + "/Season 01" not in paths_of("Season")))
    in_seasons = [i["Path"] for i in api("GET", "/Items?ParentId=%s&Fields=Path" % seasons, expect=200)[1]["Items"]]
    check("season renumber: Season 01's collection entry did not move to Season 02", ANDOR + "/Season 02" not in in_seasons)
    ex('rm -r "%s"' % ANDOR)
    webhook({"eventType": "SeriesDelete", "instanceName": "Sonarr", "series": {"path": ANDOR}, "deletedFiles": True})
    check("series delete: series and episodes gone", wait_for("series delete visible", lambda: ANDOR not in paths_of("Series") and k12b not in paths_of("Episode")))

    # A whole series moved to a new folder (Sonarr renames the series folder): every episode keeps
    # its watch state and its collection membership.
    # The new folder carries no {tvdb-...}, so old and new share no user-data key: only the arr's
    # own rename pairs say which new episode is which old one.
    e2 = e2_new
    api("POST", "/UserPlayedItems/%s?userId=%s" % (next(i["Id"] for i in items("Episode") if i["Path"] == e2), user_id), expect=200)
    shows = api("POST", "/Collections?name=Shows&ids=%s" % next(i["Id"] for i in items("Episode") if i["Path"] == e2), expect=200)[1]["Id"]
    sev2 = TV + "/Severance (US) (2022)"
    ex('mv "%s" "%s"' % (SEV, sev2))
    moved = {p: p.replace(SEV, sev2) for p in (e1_new, e2)}
    webhook({"eventType": "Rename", "instanceName": "Sonarr", "series": {"path": sev2},
             "renamedEpisodeFiles": [{"path": n, "previousPath": o} for o, n in moved.items()]})
    check("series move: new series folder only", wait_for("series move visible", lambda: sev2 in paths_of("Series") and SEV not in paths_of("Series")))
    check("series move: every episode keeps its watch state", wait_for("moved episodes played",
          lambda: all(any(i["Path"] == moved[p] and i["UserData"]["Played"] for i in items("Episode")) for p in moved)))
    in_shows = [i["Path"] for i in api("GET", "/Items?ParentId=%s&Fields=Path" % shows, expect=200)[1]["Items"]]
    check("series move: episode collection membership carried over", in_shows == [moved[e2]])
    in_playlist = [i["Path"] for i in api("GET", "/Playlists/%s/Items?userId=%s&Fields=Path" % (playlist, user_id), expect=200)[1]["Items"]]
    check("series move: playlist entries carried over", in_playlist == [moved[e1_new], moved[e2]])

    # A lower version dropped beside an existing movie is recorded on that (still primary) movie.
    def up_sources():
        up = [i for i in api("GET", "/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path,MediaSources", expect=200)[1]["Items"] if i["Path"].startswith(UP)]
        return len(up), sorted(m["Path"] for u in up for m in u.get("MediaSources", []))
    up1080, up720, up2160 = (UP + "/Up (2009) {tmdb-14160} - %s.mkv" % r for r in ("1080p", "720p", "2160p"))
    make_video(up720)
    webhook({"eventType": "Download", "instanceName": "Radarr", "movie": {"folderPath": UP}, "movieFile": {"path": up720}})
    check("alternate version: recorded on the existing movie", wait_for("alternate version visible", lambda: up_sources() == (1, sorted([up1080, up720]))))
    # A higher one would take over as primary; that hand-over is left to the scheduled scan.
    make_video(up2160)
    steps = webhook({"eventType": "Download", "instanceName": "Radarr", "movie": {"folderPath": UP}, "movieFile": {"path": up2160}})["Steps"]
    check("new primary version: step is skipped for the scheduled scan", [s["Outcome"] for s in steps] == ["skipped"] and "primary version" in (steps[0]["Detail"] or ""))
    time.sleep(3)
    check("new primary version: no duplicate movie was created", up_sources()[0] == 1)
    # Now the 1080p primary is deleted in the same upgrade: its item goes (Jellyfin promotes the 720p
    # version), the 2160p hand-over is still left to the scan, and there is never a second primary.
    ex('rm "%s"' % up1080)
    steps = webhook({"eventType": "Download", "instanceName": "Radarr", "isUpgrade": True, "movie": {"folderPath": UP},
                     "movieFile": {"path": up2160}, "deletedFiles": [{"path": up1080}]})["Steps"]
    check("promotion with deletion: deleted primary removed, step deferred", [s["Outcome"] for s in steps] == ["skipped"] and len(steps[0]["Removed"]) == 1)
    time.sleep(3)
    check("promotion with deletion: exactly one primary remains, on the 720p file", up_sources() == (1, [up720]))

    # Two unrelated paths in one request (one movie deleted, another added) never trade history.
    clue_id = next(i["Id"] for i in items("Movie") if i["Path"].startswith(CLUE))
    api("POST", "/UserPlayedItems/%s?userId=%s" % (clue_id, user_id), expect=200)
    ex('rm -r "%s"' % CLUE)
    tron = MOVIES + "/Tron (1982)/Tron (1982).mkv"
    make_video(tron)
    api("POST", "/ArrRefresh/Paths", {"Paths": [CLUE + "/Clue (1985).mkv", tron]}, expect=200)
    check("unrelated paths: deleted removed, new added", wait_for("unrelated visible", lambda: tron in paths_of("Movie") and not any(p.startswith(CLUE) for p in paths_of("Movie"))))
    check("unrelated paths: the new movie did not inherit the deleted one's watch state", not next(i for i in items("Movie") if i["Path"] == tron)["UserData"]["Played"])

    # A rename where neither name carries an id: no shared key, so watch state is copied over.
    b_old, b_new = BRAZIL + "/Brazil (1985).mkv", BRAZIL + "/Brazil (1985) [Criterion].mkv"
    b_id = next(i["Id"] for i in items("Movie") if i["Path"] == b_old)
    api("POST", "/UserPlayedItems/%s?userId=%s" % (b_id, user_id), expect=200)
    ex('mv "%s" "%s"' % (b_old, b_new))
    webhook({"eventType": "Rename", "instanceName": "Radarr", "movie": {"folderPath": BRAZIL},
             "renamedMovieFiles": [{"path": b_new, "previousPath": b_old}]})
    check("keyless rename: new path only", wait_for("keyless rename visible", lambda: b_new in paths_of("Movie") and b_old not in paths_of("Movie")))
    check("keyless rename: watch state copied", next(i for i in items("Movie") if i["Path"] == b_new)["UserData"]["Played"])

    # A movie moved to a new folder, through the on-demand endpoint: two steps, still paired.
    moved = MOVIES + "/Heat (1995) {tmdb-949} {edition-Directors Cut}"
    ex('mv "%s" "%s"' % (HEAT, moved))
    moved_file = moved + "/Heat (1995) {tmdb-949} [Bluray-1080p][h264].mkv"
    api("POST", "/ArrRefresh/Paths", {"Paths": [new, moved_file]}, expect=200)
    check("folder move: new path only", wait_for("move visible", lambda: moved_file in paths_of("Movie") and new not in paths_of("Movie")))
    check("folder move: watch state carried over", next(i for i in items("Movie") if i["Path"] == moved_file)["UserData"]["Played"])

    # Path mapping: the arr sees /arr/media, Jellyfin sees /data/media.
    api("POST", "/Plugins/%s/Configuration" % PLUGIN_ID, {"PathMappings": "/arr/media => /data/media"}, expect=204)
    mapped = DUNE + "/Dune (2021) {tmdb-438631} [WEBDL-1080p].mkv"
    make_video(mapped)
    webhook({"eventType": "Download", "instanceName": "Radarr", "movie": {"folderPath": DUNE.replace("/data/media", "/arr/media")},
             "movieFile": {"path": mapped.replace("/data/media", "/arr/media")}})
    check("path mapping: arr path is translated", wait_for("mapped import visible", lambda: mapped in paths_of("Movie")))
    api("POST", "/Plugins/%s/Configuration" % PLUGIN_ID, {"PathMappings": "/arr/media"}, expect=204)
    status, body = api("POST", "/ArrRefresh/Paths", {"Paths": [mapped]})
    check("invalid path mapping fails loudly (500) and changes nothing", status == 500 and mapped in paths_of("Movie"))
    api("POST", "/Plugins/%s/Configuration" % PLUGIN_ID, {"PathMappings": ""}, expect=204)

    # A library location that went offline (unmounted share) must not read as "everything deleted".
    term = TERM + "/The Terminal (2004) {tmdb-594} [DVD].mkv"
    ex('mv "%s" "%s-offline"' % (SHARE, SHARE))
    steps = webhook({"eventType": "MovieDelete", "instanceName": "Radarr", "movie": {"folderPath": TERM}, "deletedFiles": True})["Steps"]
    check("offline share: step reports an error", [s["Outcome"] for s in steps] == ["error"])
    check("offline share: nothing was removed", term in paths_of("Movie"))
    ex('mv "%s-offline" "%s"' % (SHARE, SHARE))

    steps = api("POST", "/ArrRefresh/Paths", {"Paths": [MOVIES, TV + "/", "/etc/passwd"]}, expect=200)[1]["Steps"]
    check("library roots and outside paths are skipped", [s["Action"] for s in steps] == ["Skip", "Skip", "Skip"])
    time.sleep(3)
    check("no library root was validated by any of the above", len(root_validations()) == baseline)


def main():
    dll = sys.argv[1]
    try:
        setup(dll)
        run()
    finally:
        if failures:
            print(sh("docker", "logs", "--tail", "60", NAME, check=False))
        subprocess.run(["docker", "rm", "-fv", NAME], capture_output=True)  # -v: the image's anonymous /config and /cache volumes
    print("\n%d failure(s)" % len(failures))
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
