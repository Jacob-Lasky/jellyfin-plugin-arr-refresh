#!/usr/bin/env python3
"""End-to-end test against a real, throwaway Jellyfin server.

Usage: sandbox_test.py <path to Jellyfin.Plugin.ArrRefresh.dll>

Starts a Jellyfin container with no bind mounts (media, config and the plugin all live inside it,
copied in with `docker cp` and written with `docker exec`), so it runs the same from a laptop, CI,
or a container that talks to a remote Docker daemon. Then it plays the webhooks Sonarr and Radarr
send for import, upgrade, rename, file delete and whole-item delete, and checks the library after
each one. The containers and network are removed at the end, on every exit path: pass, fail, an
exception, SIGTERM/SIGHUP/SIGINT (e.g. `timeout`), and SIGKILL, which a separate reaper process
covers. A run whose reaper also died is swept by a later run: at once when its process is provably gone
(same host and pid namespace), and after MAX_AGE when its owner cannot be judged from here.

Assertions are on item types, paths, watch state and collection membership, never on names,
because names come from TMDb/TVDb and the sandbox may or may not have network access.
"""
import hashlib
import io
import json
import os
import re
import select
import signal
import socket
import tempfile
import zipfile
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

IMAGE = "jellyfin/jellyfin:12.2.20261005-225228"  # the 12.2.0 release build; matches targetAbi
# The search backend Tower runs: Meilisearch 1.12.8 and arnesacnussem's Meilisearch plugin 1.12.1.5,
# fetched from its own release and checked against the MD5 its manifest publishes.
MEILI_IMAGE = "getmeili/meilisearch:v1.12.8"
MEILI_KEY = "it-only-master-key-0123456789"
MEILI_INDEX = "it"
# Jellyfin reaches Meilisearch by name on a network of its own, so a restart cannot move it to another
# address and a stopped server fails requests at once (nothing is left in a socket buffer to be executed
# later). The test itself reaches it on the default bridge.
MEILI_URL_FOR_JELLYFIN = "http://meili:7700"
MEILI_PLUGIN_ID = "974395db-b31d-46a2-bc86-ef9aa5ac04dd"
MEILI_PLUGIN_ZIP = ("https://github.com/arnesacnussem/jellyfin-plugin-meilisearch/releases/download/1.12.1.5/meilisearch_1.12.1.5.zip",
                    "4efd0b1c543da292a9f7c46f720cb98a")
NAME = "arr-refresh-it-%d-%s" % (os.getpid(), uuid.uuid4().hex[:8])  # unique even for runs started in the same second
# Every container and network a run creates carries these labels, so teardown and the stale sweep find them
# by label rather than by name pattern.
RUN_LABEL, OWNER_LABEL, CREATED_LABEL = "arr-refresh-it.run", "arr-refresh-it.owner", "arr-refresh-it.created"
# A run whose owner cannot be judged from here (another host, another pid namespace, no /proc) is swept once
# it is older than this. Six hours is GitHub's limit on a hosted job, so no CI run can still be alive past it;
# a whole run takes minutes. A run whose owner is provably alive is never swept, however old.
MAX_AGE = 6 * 3600


class Gone(Exception):
    """No live process has that pid here: none at all, or only its zombie."""


def process_identity(pid):
    """'<pid namespace>:<pid>:<start time>'. Raises Gone when no live process has that pid, and OSError when
    it cannot tell (no /proc).

    The namespace keeps a pid from another container sharing the daemon from being judged here, and the
    start time keeps a reused pid from passing for the run that once had it.
    """
    ns = os.stat("/proc/self/ns/pid").st_ino
    try:
        with open("/proc/%d/stat" % pid) as f:
            fields = f.read().rsplit(")", 1)[1].split()  # counted from field 3, after the parenthesised comm
    except FileNotFoundError:
        raise Gone() from None
    if fields[0] == "Z":
        raise Gone()  # exited; a parent (or an init) that never reaps it keeps the entry
    return "%d:%d:%s" % (ns, pid, fields[19])  # field 22, the start time


def proc_hides_processes():
    """True when /proc is mounted with hidepid, so a missing entry may be another user's live process."""
    with open("/proc/self/mountinfo") as f:
        for line in f:
            parts = line.split()
            if parts[4] == "/proc" and re.search(r"\bhidepid=(?!0\b|off\b)", parts[-1]):
                return True
    return False


try:
    OWNER = "%s:%s" % (socket.gethostname(), process_identity(os.getpid()))
except OSError:
    OWNER = socket.gethostname() + ":unknown"  # not Linux: only the age sweep can judge this run
LABELS = ["--label", RUN_LABEL + "=" + NAME, "--label", OWNER_LABEL + "=" + OWNER,
          "--label", "%s=%d" % (CREATED_LABEL, time.time())]
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
ALIENS = MOVIES + "/Aliens (1986) {tmdb-679}"  # holds the movie and a leftover file at the initial scan
# A movies library with every remote metadata and image fetcher off: its refresh is a local ffprobe and
# then silence, so a test can know that no later save of the item will come along and do a retry's job.
LOCAL = "/data/media/local-movies"
ANDOR = TV + "/Andor (2022) {tvdb-393189}"

base = token = user_id = meili = None
failures = []


def sh(*args, check=True):
    return subprocess.run(args, check=check, capture_output=True, text=True).stdout.strip()


def docker_ids(kind, run):
    """Ids of one run's containers or networks; None when Docker could not be asked, which is not 'none left'."""
    cmd = ["docker", "ps", "-aq"] if kind == "container" else ["docker", "network", "ls", "-q"]
    r = subprocess.run(cmd + ["--filter", "label=%s=%s" % (RUN_LABEL, run)], capture_output=True, text=True)
    return r.stdout.split() if r.returncode == 0 else None


def teardown(run, attempts=3):
    """Remove one run's containers (with their anonymous /config and /cache volumes) and network.

    Idempotent. Returns what is still there after the last attempt ("docker unavailable" when it could not
    tell), so a caller can fail rather than report a clean run that left something behind.
    """
    left = []
    for attempt in range(attempts):
        if attempt:
            time.sleep(2 * attempt)
        # By exact name, never by a filter: these three are everything a run creates (see setup). The labels
        # only confirm that nothing is left, and let the sweep find runs.
        subprocess.run(["docker", "rm", "-fv", run, run + "-meili"], capture_output=True)
        subprocess.run(["docker", "network", "rm", run + "-net"], capture_output=True)  # after its endpoints
        containers, networks = docker_ids("container", run), docker_ids("network", run)
        if containers is None or networks is None:
            left = ["docker unavailable"]
            continue
        left = containers + networks
        if not left:
            return []
    return left


def reap():
    """Reaper mode: block until the test process is gone, however it went, then tear its run down.

    The test holds the write end of this process's stdin and nothing else does (subprocess closes inherited
    descriptors), so EOF arrives exactly when the test exits, SIGKILL included. Own session: a signal sent
    to the test's process group (`timeout`, Ctrl-C) does not take the reaper with it.
    """
    print("ready", flush=True)
    if sys.stdin.buffer.read() == b"clean":
        return  # the test tore its run down itself and is waiting for this process to exit
    teardown(sys.argv[2])
    # A `docker create` the test started just before it was killed outlives it and can land after the first
    # pass, so look once more when any such request has had time to finish. Anything later still is a run
    # with no owner, which the age sweep removes.
    time.sleep(10)
    teardown(sys.argv[2])


def owner_status(owner):
    """'dead' only when provably gone (same host and pid namespace, and no process with that pid and start
    time), 'alive' only when provably running, otherwise 'unknown'."""
    host, _, ident = owner.partition(":")
    parts = ident.split(":")
    if host != socket.gethostname() or len(parts) != 3 or not parts[1].isdigit():
        return "unknown"
    try:
        # Judged only when this process can see itself and nothing hides other processes from it, so a
        # missing entry means a missing process.
        if process_identity(os.getpid()).split(":")[0] != parts[0] or proc_hides_processes():
            return "unknown"  # another pid namespace on the same daemon: its pids mean nothing here
    except (OSError, Gone):
        return "unknown"
    try:
        return "alive" if process_identity(int(parts[1])) == ident else "dead"  # a different start: reused pid
    except Gone:
        return "dead"
    except OSError:
        return "unknown"


def sweep_stale():
    """Tear down earlier runs whose test is provably gone, and runs older than MAX_AGE whose owner cannot be judged.

    Runs are found from networks as well as containers: an interrupted teardown can leave only the network.
    """
    fmt = '{{.Label "%s"}}\t{{.Label "%s"}}\t{{.Label "%s"}}' % (RUN_LABEL, OWNER_LABEL, CREATED_LABEL)
    runs = {}
    for cmd in (["docker", "ps", "-a"], ["docker", "network", "ls"]):
        out = subprocess.run(cmd + ["--filter", "label=" + RUN_LABEL, "--format", fmt], capture_output=True, text=True)
        if out.returncode:
            print("warning: could not list earlier sandboxes:", out.stderr.strip())
        for line in out.stdout.splitlines():
            run, owner, created = (line.split("\t") + ["", ""])[:3]
            runs[run] = (owner, created)
    for run, (owner, created) in runs.items():
        if run == NAME:
            continue
        status = owner_status(owner)
        old = created.isdigit() and time.time() - int(created) > MAX_AGE
        if status == "dead" or (status == "unknown" and old):
            print("removing stale sandbox", run, "(its test is gone)" if status == "dead" else "(older than %d s)" % MAX_AGE)
            left = teardown(run)
            if left:
                print("warning: stale sandbox %s left %s behind" % (run, ", ".join(left)))


def on_signal(signum, _frame):
    raise SystemExit(128 + signum)  # unwinds through main's finally, so the log is still saved


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
    return condition


def paths_of(types):
    return {i["Path"] for i in items(types)}


def root_validations():
    """Lines showing a library root being re-validated: the walk this plugin exists to prevent."""
    out = subprocess.run(["docker", "logs", NAME], capture_output=True, text=True)
    log = out.stdout + out.stderr
    return [line for line in log.splitlines() if "will be refreshed" in line or "Validating media library" in line]


def meili_api(method, path, body=None):
    req = urllib.request.Request(meili + path, method=method, headers={"Authorization": "Bearer " + MEILI_KEY, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, json.dumps(body).encode() if body is not None else None, timeout=30) as r:
            return r.status, json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e:
        return e.code, None
    except OSError:
        return 0, None


def meili_doc(item_id):
    """The search document for a Jellyfin item id, or None."""
    guid = "%s-%s-%s-%s-%s" % (item_id[:8], item_id[8:12], item_id[12:16], item_id[16:20], item_id[20:])
    status, doc = meili_api("GET", "/indexes/%s/documents/%s" % (MEILI_INDEX, guid))
    return doc if status == 200 else None


def search(term):
    """Paths Jellyfin's search (through the Meilisearch plugin) returns for a term."""
    q = "/Items?Recursive=true&searchTerm=%s&IncludeItemTypes=Movie,Episode,Series&Fields=Path&userId=%s" % (urllib.parse.quote(term), user_id)
    return {i.get("Path") for i in api("GET", q, expect=200)[1]["Items"]}


def meili_bridge_url():
    ip = sh("docker", "inspect", NAME + "-meili", "--format", '{{(index .NetworkSettings.Networks "bridge").IPAddress}}')
    return "http://%s:7700" % ip


def install_meilisearch_plugin():
    with urllib.request.urlopen(MEILI_PLUGIN_ZIP[0], timeout=120) as r:
        data = r.read()
    if hashlib.md5(data).hexdigest() != MEILI_PLUGIN_ZIP[1]:
        raise AssertionError("Meilisearch plugin download does not match its manifest checksum")
    with tempfile.TemporaryDirectory() as d:
        zipfile.ZipFile(io.BytesIO(data)).extractall(os.path.join(d, "Meilisearch_1.12.1.5"))
        sh("docker", "cp", os.path.join(d, "Meilisearch_1.12.1.5"), NAME + ":/tmp/")
    ex("cp -r /tmp/Meilisearch_1.12.1.5 /config/plugins/")


def setup(dll):
    global base, token, user_id, meili
    # Meilisearch sizes its indexing memory from the host's RAM, not the container limit, and is OOM-killed
    # under a cap unless told otherwise (Tower sets MEILI_MAX_INDEXING_MEMORY for the same reason).
    # --memory-swap equal to --memory caps RAM and swap together. A daemon without swap-limit support drops it
    # (inspect then shows MemorySwap -1 and `docker run` warns "Memory limited without swap"); on such a host
    # the swap cap has to come from the host itself, as Tower's memory-swap-cap script provides.
    sh("docker", "network", "create", *LABELS, NAME + "-net")
    sh("docker", "run", "-d", "--name", NAME + "-meili", *LABELS, "--network", NAME + "-net", "--network-alias", "meili",
       "--memory=2g", "--memory-swap=2g", "-e", "MEILI_MASTER_KEY=" + MEILI_KEY,
       "-e", "MEILI_MAX_INDEXING_MEMORY=512MiB",
       "-e", "MEILI_NO_ANALYTICS=true", "-e", "MEILI_HTTP_ADDR=0.0.0.0:7700", MEILI_IMAGE)
    sh("docker", "network", "connect", "bridge", NAME + "-meili")
    meili = meili_bridge_url()
    wait_for("Meilisearch answers", lambda: meili_api("GET", "/health")[0] == 200, timeout=60)
    sh("docker", "create", "--name", NAME, *LABELS, "--user", "99:100", "--memory=2g", "--memory-swap=2g", IMAGE)
    sh("docker", "network", "connect", NAME + "-net", NAME)
    sh("docker", "cp", dll, NAME + ":/tmp/plugin.dll")
    sh("docker", "start", NAME)
    ex("mkdir -p /config/plugins/ArrRefresh_it && cp /tmp/plugin.dll /config/plugins/ArrRefresh_it/")
    install_meilisearch_plugin()
    sh("docker", "exec", "-u", "0", NAME, "sh", "-c", "mkdir -p %s %s %s %s && chown -R 99:100 /data" % (MOVIES, TV, SHARE, LOCAL))
    for p in [HEAT + "/Heat (1995) {tmdb-949} [Bluray-1080p].mkv",
              ALIEN + "/Alien (1979) {tmdb-348} [Bluray-1080p].mkv",
              SEV + "/Season 01/Severance (2022) - S01E01 - Good News About Hell.mkv",
              SEV + "/Season 01/Severance (2022) - S01E02 - Half Loop.mkv",
              TERM + "/The Terminal (2004) {tmdb-594} [DVD].mkv",
              UP + "/Up (2009) {tmdb-14160} - 1080p.mkv",
              BRAZIL + "/Brazil (1985).mkv",
              CLUE + "/Clue (1985).mkv",
              ALIENS + "/Aliens (1986) {tmdb-679} [Bluray-1080p].mkv",
              ALIENS + "/leftover.mkv",
              LOCAL + "/Placeholder (2000)/Placeholder (2000).mkv"]:  # an empty library gets no root item
        make_video(p)
    sh("docker", "restart", "-t", "30", NAME)  # load the plugin copied in after the first start
    ip = sh("docker", "inspect", NAME, "--format", '{{(index .NetworkSettings.Networks "bridge").IPAddress}}')  # it is on two networks
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
    check("Meilisearch plugin loaded and active", plugins.get("Meilisearch") == "Active")
    api("POST", "/Plugins/%s/Configuration" % MEILI_PLUGIN_ID,
        {"ApiKey": MEILI_KEY, "Url": MEILI_URL_FOR_JELLYFIN, "Debug": False, "IndexName": MEILI_INDEX, "MatchingStrategy": "last"}, expect=204)
    wait_for("Meilisearch plugin connected", lambda: api("GET", "/meilisearch/reconnect", expect=200)[1]["meilisearchOk"], timeout=60)
    for name, ctype, path in [("Movies", "movies", MOVIES), ("TV", "tvshows", TV), ("Share", "movies", SHARE)]:
        api("POST", "/Library/VirtualFolders?name=%s&collectionType=%s&paths=%s&refreshLibrary=false" % (name, ctype, path),
            {"LibraryOptions": {"EnableRealtimeMonitor": False, "PathInfos": [{"Path": path}]}}, expect=204)
    api("POST", "/Library/VirtualFolders?name=Local&collectionType=movies&paths=%s&refreshLibrary=false" % LOCAL,
        {"LibraryOptions": {"EnableRealtimeMonitor": False, "PathInfos": [{"Path": LOCAL}], "EnableInternetProviders": False,
                            "TypeOptions": [{"Type": "Movie", "MetadataFetchers": [], "MetadataFetcherOrder": [], "ImageFetchers": [], "ImageFetcherOrder": []}]}},
        expect=204)
    api("POST", "/Library/Refresh", expect=204)
    wait_for("initial scan finds 9 movies and 2 episodes",
             lambda: len(items("Movie")) == 9 and len(items("Episode")) == 2, timeout=180)
    # Their full reindex once, as Tower's runs nightly, so the index exists with documents in their shape.
    api("GET", "/meilisearch/reindex", expect=200)
    wait_for("Meilisearch index built", lambda: (meili_api("GET", "/indexes/%s/stats" % MEILI_INDEX)[1] or {}).get("numberOfDocuments", 0) > 0, timeout=60)


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
    global MEILI_INDEX, meili
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
    check("rename: the new item is in the search index", wait_for("renamed document", lambda: meili_doc(renamed["Id"]), timeout=60) is not None)
    check("rename: search finds the new path", wait_for("renamed searchable", lambda: new in search("Heat"), timeout=60))
    check("rename: the old item's document was removed", wait_for("old document gone", lambda: meili_doc(heat_id) is None, timeout=60))
    check("rename: collection membership carried over",
          [i["Path"] for i in api("GET", "/Items?ParentId=%s&Fields=Path" % coll, expect=200)[1]["Items"]] == [new])

    # Radarr import of a new movie.
    dune = DUNE + "/Dune (2021) {tmdb-438631} [WEBDL-2160p].mkv"
    make_video(dune)
    webhook({"eventType": "Download", "instanceName": "Radarr", "movie": {"folderPath": DUNE}, "movieFile": {"path": dune}})
    check("import: new movie appears", wait_for("import visible", lambda: dune in paths_of("Movie")))
    dune_id = next(i["Id"] for i in items("Movie") if i["Path"] == dune)
    check("import: its search document is written", wait_for("dune document", lambda: meili_doc(dune_id), timeout=60) is not None)
    check("import: Jellyfin search finds it by title", wait_for("dune searchable", lambda: dune in search("Dune"), timeout=60))

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

    # A folder Jellyfin's scan found holding the movie and a leftover file (Radarr never leaves that
    # itself; it is what a hand-copied extra looks like). An upgrade deletes both and imports one file:
    # every old item hands its collection entry to the new file.
    old_paths = [p for p in paths_of("Movie") if p.startswith(ALIENS + "/")]
    by_path = {i["Path"]: i["Id"] for i in items("Movie")}
    check("two-file folder: the scan listed both files", sorted(old_paths) == sorted([ALIENS + "/Aliens (1986) {tmdb-679} [Bluray-1080p].mkv", ALIENS + "/leftover.mkv"]))
    aliens = api("POST", "/Collections?name=Aliens&ids=%s" % ",".join(by_path[p] for p in old_paths), expect=200)[1]["Id"]
    a_remux = ALIENS + "/Aliens (1986) {tmdb-679} [Remux-2160p].mkv"
    make_video(a_remux)
    ex(" && ".join('rm "%s"' % p for p in old_paths))
    webhook({"eventType": "Download", "instanceName": "Radarr", "isUpgrade": True, "movie": {"folderPath": ALIENS},
             "movieFile": {"path": a_remux}, "deletedFiles": [{"path": p} for p in old_paths]})
    check("two-file upgrade: only the new file remains", wait_for("two-file upgrade visible",
          lambda: [p for p in paths_of("Movie") if p.startswith(ALIENS + "/")] == [a_remux]))
    in_aliens = [i["Path"] for i in api("GET", "/Items?ParentId=%s&Fields=Path" % aliens, expect=200)[1]["Items"]]
    check("two-file upgrade: collection entries carried to the new file", in_aliens == [a_remux])

    # Radarr whole-movie delete: only the folder path is in the payload.
    ex('rm -r "%s"' % DUNE)
    webhook({"eventType": "MovieDelete", "instanceName": "Radarr", "movie": {"folderPath": DUNE}, "deletedFiles": True})
    check("movie delete: removed", wait_for("movie delete visible", lambda: dune not in paths_of("Movie")))
    # A delete Meilisearch accepted but did not apply: put the document back by hand. Verification
    # compares with the library and deletes it again.
    gone_doc = wait_for("dune document gone", lambda: meili_doc(dune_id) is None and {"guid": "%s-%s-%s-%s-%s" % (dune_id[:8], dune_id[8:12], dune_id[12:16], dune_id[16:20], dune_id[20:]), "name": "Dune", "type": "MediaBrowser.Controller.Entities.Movies.Movie"}, timeout=60)
    # Past this plugin's own delete (4 s after the removal), so only verification can remove it again.
    time.sleep(10)
    meili_api("POST", "/indexes/%s/documents?primaryKey=guid" % MEILI_INDEX, [gone_doc])
    wait_for("stale document present", lambda: meili_doc(dune_id) is not None, timeout=30)
    check("a document left behind for a deleted item is removed by verification", wait_for("stale document removed", lambda: meili_doc(dune_id) is None, timeout=240))

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
    k1_id = next(i["Id"] for i in items("Episode") if i["Path"] == k1)
    check("new series: its episode, created by the series refresh, is in the search index", wait_for("episode document", lambda: meili_doc(k1_id), timeout=60) is not None)

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
    steps = webhook({"eventType": "Download", "instanceName": "Radarr", "movie": {"folderPath": UP}, "movieFile": {"path": up720}})["Steps"]
    check("alternate version replayed: the primary's own version is not mistaken for a takeover", [s["Outcome"] for s in steps] == ["ok"])
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

    # Copies merged as versions of one movie (Merge Versions, or Jellyfin's own merge). FindByPath and the
    # children listing both hide a merged version, so each way of finding one gets its own witness.
    vA, vB = MOVIES + "/Twin (2010)/Twin (2010).mkv", MOVIES + "/Twin (2010) {imdbid-/Twin (2010).mkv"
    for v in (vA, vB):
        make_video(v)
        webhook({"eventType": "Download", "instanceName": "Radarr", "movie": {"folderPath": v.rsplit("/", 1)[0]}, "movieFile": {"path": v}})
    wait_for("twins visible", lambda: {vA, vB} <= paths_of("Movie"))
    def merge(a, b):
        ids = {i["Path"]: i["Id"] for i in items("Movie")}
        api("POST", "/Videos/MergeVersions?ids=%s,%s" % (ids[a], ids[b]), expect=204)
    def twin_sources():
        q = "/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path,MediaSources"
        return sorted(m["Path"] for i in api("GET", q, expect=200)[1]["Items"] if i["Path"].startswith(MOVIES + "/Twin (2010)") for m in i.get("MediaSources", []))
    def merged_alternate(a, b):
        """Merge two copies; return (primary, alternate). Only the alternate is hidden from item lists."""
        merge(a, b)
        listed = wait_for("one copy hidden", lambda: (lambda p: p if len(p & {a, b}) == 1 else None)(paths_of("Movie")), timeout=30)
        return (a, b) if a in listed else (b, a)
    # 1. The hidden copy renamed inside a folder that still exists: only a by-path lookup that includes
    #    versions finds the old item (the folder resolves, so the children listing is not consulted).
    primary, alt = merged_alternate(vA, vB)
    alt2 = alt.replace(".mkv", " [Bluray-1080p].mkv")
    ex('mv "%s" "%s"' % (alt, alt2))
    webhook({"eventType": "Rename", "instanceName": "Radarr", "movie": {"folderPath": alt.rsplit("/", 1)[0]},
             "renamedMovieFiles": [{"path": alt2, "previousPath": alt}]})
    check("merged version renamed: the old item is gone", wait_for("merged rename", lambda: alt not in twin_sources() and alt2 in twin_sources(), timeout=60))
    # 2. The hidden copy deleted with its folder, the payload naming only the folder: only a children
    #    listing that includes versions finds it.
    primary, alt = merged_alternate(primary, alt2)
    ex('rm -r "%s"' % alt.rsplit("/", 1)[0])
    webhook({"eventType": "MovieDelete", "instanceName": "Radarr", "movie": {"folderPath": alt.rsplit("/", 1)[0]}, "deletedFiles": True})
    check("merged version deleted with its folder: only the surviving copy is left", wait_for("merged version gone", lambda: twin_sources() == [primary], timeout=60))

    # A stray copy imported from inside the library (manual import with Move): the arr names the old
    # file as the import's sourcePath. Same title (shared tmdb id in the names, as Radarr writes them):
    # the imported item inherits the stray one's watch state.
    stray = MOVIES + "/Stray (2003) {imdbid-/Stray (2003) {tmdb-123401}.mkv"
    make_video(stray)
    webhook({"eventType": "Download", "instanceName": "Radarr", "movie": {"folderPath": stray.rsplit("/", 1)[0]}, "movieFile": {"path": stray}})
    wait_for("stray visible", lambda: stray in paths_of("Movie"))
    api("POST", "/UserPlayedItems/%s?userId=%s" % (next(i["Id"] for i in items("Movie") if i["Path"] == stray), user_id), expect=200)
    placed = MOVIES + "/Stray (2003) {tmdb-123401}/Stray (2003) {tmdb-123401} [Bluray-1080p].mkv"
    ex('mkdir -p "%s" && mv "%s" "%s" && rmdir "%s"' % (placed.rsplit("/", 1)[0], stray, placed, stray.rsplit("/", 1)[0]))
    webhook({"eventType": "Download", "instanceName": "Radarr", "movie": {"folderPath": placed.rsplit("/", 1)[0]},
             "movieFile": {"path": placed, "sourcePath": stray}})
    check("in-library import: moved item replaces the stray one", wait_for("stray moved", lambda: placed in paths_of("Movie") and stray not in paths_of("Movie")))
    check("in-library import: watch state carried over", next(i for i in items("Movie") if i["Path"] == placed)["UserData"]["Played"])
    # A different title's file imported (another movie's copy taken by a manual import): its item goes,
    # but its history does not move to the movie it was imported as.
    other = MOVIES + "/Other (1999) {imdbid-/Other (1999) {tmdb-123402}.mkv"
    make_video(other)
    webhook({"eventType": "Download", "instanceName": "Radarr", "movie": {"folderPath": other.rsplit("/", 1)[0]}, "movieFile": {"path": other}})
    wait_for("other visible", lambda: other in paths_of("Movie"))
    api("POST", "/UserPlayedItems/%s?userId=%s" % (next(i["Id"] for i in items("Movie") if i["Path"] == other), user_id), expect=200)
    taken = MOVIES + "/Taken (2008) {tmdb-123403}/Taken (2008) {tmdb-123403} [Bluray-1080p].mkv"
    ex('mkdir -p "%s" && mv "%s" "%s" && rmdir "%s"' % (taken.rsplit("/", 1)[0], other, taken, other.rsplit("/", 1)[0]))
    webhook({"eventType": "Download", "instanceName": "Radarr", "movie": {"folderPath": taken.rsplit("/", 1)[0]},
             "movieFile": {"path": taken, "sourcePath": other}})
    check("import of another title's file: the old item goes", wait_for("other gone", lambda: taken in paths_of("Movie") and other not in paths_of("Movie")))
    check("import of another title's file: its watch state does not move", not next(i for i in items("Movie") if i["Path"] == taken)["UserData"]["Played"])

    # A movie moved to a new folder, through the on-demand endpoint: two steps, still paired.
    moved = MOVIES + "/Heat (1995) {tmdb-949} {edition-Directors Cut}"
    ex('mv "%s" "%s"' % (HEAT, moved))
    moved_file = moved + "/Heat (1995) {tmdb-949} [Bluray-1080p][h264].mkv"
    api("POST", "/ArrRefresh/Paths", {"Paths": [new, moved_file]}, expect=200)
    check("folder move: new path only", wait_for("move visible", lambda: moved_file in paths_of("Movie") and new not in paths_of("Movie")))
    check("folder move: watch state carried over", next(i for i in items("Movie") if i["Path"] == moved_file)["UserData"]["Played"])

    # Witness. Jellyfin uses Meilisearch's hits whenever it has any and falls back to its database only
    # when it has none, so a unique title is found either way. "Heat Wave" also matches the indexed Heat
    # documents, so it is found only once its own document is in the index, which is what Tower users see.
    # Precondition, or the witness proves nothing: Meilisearch must already have a hit for "Heat Wave"
    # (the moved Heat), so the database fallback stays out of it.
    # The moved Heat's OWN document, not just any hit: the old Heat's document still answers for a few
    # seconds after its item is gone, and a write still in its batch window is dropped when the sync is
    # switched off below.
    heat_now = next(i["Id"] for i in items("Movie") if i["Path"] == moved_file)
    check("witness precondition: the moved Heat is in the index, so the control title has Meilisearch hits",
          wait_for("moved heat document", lambda: meili_doc(heat_now), timeout=60) is not None)
    api("POST", "/Plugins/%s/Configuration" % PLUGIN_ID, {"PathMappings": "", "UpdateSearchIndex": False}, expect=204)
    wave = MOVIES + "/Heat Wave (2007)/Heat Wave (2007).mkv"
    make_video(wave)
    webhook({"eventType": "Download", "instanceName": "Radarr", "movie": {"folderPath": wave.rsplit("/", 1)[0]}, "movieFile": {"path": wave}})
    wait_for("heat wave visible", lambda: wave in paths_of("Movie"))
    time.sleep(15)
    check("search update off: the new movie is not written to the index", meili_doc(next(i["Id"] for i in items("Movie") if i["Path"] == wave)) is None)
    found = search("Heat Wave")
    if not check("search update off: search for its title does not find it", wave not in found):
        print("    meili hits:", [(h.get("name"), h.get("path")) for h in (meili_api("POST", "/indexes/%s/search" % MEILI_INDEX, {"q": "Heat Wave", "limit": 10})[1] or {}).get("hits", [])])
        print("    jellyfin search:", found)
    api("POST", "/Plugins/%s/Configuration" % PLUGIN_ID, {"PathMappings": "", "UpdateSearchIndex": True}, expect=204)
    api("POST", "/ArrRefresh/Paths", {"Paths": [wave]}, expect=200)
    wave_id = next(i["Id"] for i in items("Movie") if i["Path"] == wave)
    check("search update on: its document is written", wait_for("heat wave document", lambda: meili_doc(wave_id), timeout=60) is not None)
    check("search update on: the same title is now found", wave in search("Heat Wave"))

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

    # A rebuild that read the database before a write and swapped after it discards the write. Simulate
    # the discarded document by deleting it in Meilisearch: the verification pass must write it again.
    guid = meili_doc(wave_id)["guid"]
    meili_api("DELETE", "/indexes/%s/documents/%s" % (MEILI_INDEX, guid))
    wait_for("document deleted", lambda: meili_doc(wave_id) is None, timeout=30)
    check("document lost to a rebuild is written again by the verification pass", wait_for("document restored", lambda: meili_doc(wave_id), timeout=240) is not None)

    # Meilisearch down (game mode stops it): writes fail and must be retried until it is back. Jellyfin
    # reaches it by name, so while it is stopped every request fails at once and none can be delivered
    # late. (A config change cannot be used to make it refuse: the Meilisearch plugin rebuilds its whole
    # index on every config change, which would deliver the document itself.)
    sh("docker", "stop", "-t", "10", NAME + "-meili")
    seven = LOCAL + "/Se7en (1995)/Se7en (1995).mkv"
    make_video(seven)
    webhook({"eventType": "Download", "instanceName": "Radarr", "movie": {"folderPath": seven.rsplit("/", 1)[0]}, "movieFile": {"path": seven}})
    wait_for("se7en visible", lambda: seven in paths_of("Movie"))
    # The library has no remote fetchers, so once the probe has filled RunTimeTicks no later save of the
    # item will come along and do the retry's job.
    def refreshed():
        q = "/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path"
        return any(i["Path"] == seven and i.get("RunTimeTicks") for i in api("GET", q, expect=200)[1]["Items"])
    wait_for("se7en refreshed during the outage", refreshed, timeout=120)
    time.sleep(15)
    sh("docker", "start", NAME + "-meili")
    meili = meili_bridge_url()
    wait_for("Meilisearch back", lambda: meili_api("GET", "/health")[0] == 200, timeout=60)
    seven_id = next(i["Id"] for i in items("Movie") if i["Path"] == seven)
    check("Meilisearch outage: the write is retried and lands once it is back", wait_for("se7en document", lambda: meili_doc(seven_id), timeout=180) is not None)

    steps = api("POST", "/ArrRefresh/Paths", {"Paths": [MOVIES, TV + "/", "/etc/passwd"]}, expect=200)[1]["Steps"]
    check("library roots and outside paths are skipped", [s["Action"] for s in steps] == ["Skip", "Skip", "Skip"])
    time.sleep(3)
    check("no library root was validated by any of the above", len(root_validations()) == baseline)

    # With no index name set, the Meilisearch plugin names its index after the server (spaces to dashes);
    # the writes must follow it there. Last, because it switches the index everything above used.
    server = api("GET", "/System/Info/Public", auth=False, expect=200)[1]["ServerName"].replace(" ", "-")
    api("POST", "/Plugins/%s/Configuration" % MEILI_PLUGIN_ID, {"ApiKey": MEILI_KEY, "Url": MEILI_URL_FOR_JELLYFIN, "Debug": False, "IndexName": "", "MatchingStrategy": "last"}, expect=204)
    wait_for("reconnected", lambda: api("GET", "/meilisearch/reconnect", expect=200)[1]["meilisearchOk"], timeout=60)
    api("GET", "/meilisearch/reindex", expect=200)
    MEILI_INDEX = server
    fargo = MOVIES + "/Fargo (1996)/Fargo (1996).mkv"
    make_video(fargo)
    webhook({"eventType": "Download", "instanceName": "Radarr", "movie": {"folderPath": fargo.rsplit("/", 1)[0]}, "movieFile": {"path": fargo}})
    wait_for("fargo visible", lambda: fargo in paths_of("Movie"))
    fargo_id = next(i["Id"] for i in items("Movie") if i["Path"] == fargo)
    check("default index name: the document goes to the index named after the server", wait_for("fargo document", lambda: meili_doc(fargo_id), timeout=60) is not None)


def main():
    if sys.argv[1:2] == ["--reap"]:
        return reap()
    dll = sys.argv[1]
    for sig in (signal.SIGTERM, signal.SIGHUP):
        signal.signal(sig, on_signal)
    reaper = None
    try:
        # The reaper is running and listening before anything is created, so no container or network ever
        # exists without one. Started inside the try, so a signal at any point still reaches the finally.
        reaper = subprocess.Popen([sys.executable, os.path.abspath(__file__), "--reap", NAME], stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, start_new_session=True)
        if not select.select([reaper.stdout], [], [], 30)[0] or reaper.stdout.readline().strip() != b"ready":
            reaper.kill()
            raise SystemExit("the cleanup reaper did not start, so nothing was created")
        reaper.stdout.close()
        sweep_stale()
        setup(dll)
        run()
    except Exception as e:
        failures.append("exception: %r" % e)
        raise
    finally:
        if failures:
            out = subprocess.run(["docker", "logs", NAME], capture_output=True, text=True)
            log_path = os.path.join(os.environ.get("IT_LOG_DIR", "/tmp"), NAME + ".log")
            with open(log_path, "w") as f:
                f.write(out.stdout + out.stderr)
            print("full server log:", log_path)
            print("\n".join((out.stdout + out.stderr).splitlines()[-60:]))
        left = teardown(NAME)
        if left:
            failures.append("teardown left %s behind" % ", ".join(left))
            print("FAIL teardown left %s behind" % ", ".join(left))
        if reaper:
            try:
                if not left:
                    reaper.stdin.write(b"clean")  # nothing for it to do; EOF alone sends it to try again
                reaper.stdin.close()
            except BrokenPipeError:
                pass  # it has already exited
            # Waited for, so it is not left to be adopted by an init that may never reap it (a zombie per run).
            try:
                reaper.wait(timeout=60)
            except subprocess.TimeoutExpired:
                print("warning: the cleanup reaper is still running")
    print("\n%d failure(s)" % len(failures))
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
