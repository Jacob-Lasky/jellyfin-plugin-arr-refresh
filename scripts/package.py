#!/usr/bin/env python3
"""Zip the built plugin and add the release to manifest.json.

Usage: package.py <build output dir> <release download base URL> <changelog text>

Reads the version from Directory.Build.props and the metadata from build.yaml (a flat subset
of YAML: top-level scalars, '>' folded blocks and one list), so it needs nothing but the
standard library. Writes dist/<slug>_<version>.zip and rewrites manifest.json in place, newest
version first. Jellyfin verifies the download against the MD5 written here.
"""
import datetime
import hashlib
import json
import pathlib
import re
import sys
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent


def read_build_yaml(path):
    meta, key, block, items = {}, None, None, None
    for raw in path.read_text().splitlines():
        if not raw.strip() or raw.lstrip().startswith("#"):
            continue
        if block is not None and raw.startswith("  ") and not raw.startswith("- "):
            block.append(raw.strip())
            meta[key] = " ".join(block)
            continue
        if items is not None and raw.startswith("- "):
            items.append(raw[2:].strip().strip('"'))
            continue
        block = items = None
        key, _, value = raw.partition(":")
        key, value = key.strip(), value.strip()
        if value == ">":
            block = []
        elif value == "":
            items = meta[key] = []
        else:
            meta[key] = value.strip('"')
    return meta


def main():
    out_dir, base_url, changelog = pathlib.Path(sys.argv[1]), sys.argv[2].rstrip("/"), sys.argv[3]
    props = (ROOT / "Directory.Build.props").read_text()
    version = re.search(r"<Version>([0-9.]+)</Version>", props).group(1)
    meta = read_build_yaml(ROOT / "build.yaml")
    timestamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    slug = meta["name"].lower().replace(" ", "-")
    dist = ROOT / "dist"
    dist.mkdir(exist_ok=True)
    zip_path = dist / f"{slug}_{version}.zip"
    plugin_meta = {
        "category": meta["category"], "changelog": changelog, "description": meta["description"],
        "guid": meta["guid"], "name": meta["name"], "overview": meta["overview"], "owner": meta["owner"],
        "targetAbi": meta["targetAbi"], "timestamp": timestamp, "version": version,
    }
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for artifact in meta["artifacts"]:
            z.write(out_dir / artifact, artifact)
        z.writestr("meta.json", json.dumps(plugin_meta, indent=2))
    checksum = hashlib.md5(zip_path.read_bytes()).hexdigest()

    manifest_path = ROOT / "manifest.json"
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else []
    entry = next((p for p in manifest if p["guid"] == meta["guid"]), None)
    if entry is None:
        entry = {"guid": meta["guid"], "versions": []}
        manifest.append(entry)
    for k in ("name", "overview", "description", "owner", "category"):
        entry[k] = meta[k]
    entry["versions"] = [v for v in entry["versions"] if v["version"] != version]
    entry["versions"].insert(0, {
        "version": version, "changelog": changelog, "targetAbi": meta["targetAbi"],
        "sourceUrl": f"{base_url}/v{version}/{zip_path.name}", "checksum": checksum, "timestamp": timestamp,
    })
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"{zip_path} md5={checksum}")


if __name__ == "__main__":
    main()
