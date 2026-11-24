# SPDX-License-Identifier: AGPL-3.0-or-later
"""Checks that the plugin's version and its manifest agree.

    check-release.py            the version in the project, build.yaml and the manifest
    check-release.py --zips     also downloads each release zip the manifest lists and
                                compares its MD5 with the manifest's checksum

The version is in three places: Jellyfin.Plugin.ArcfinSync.csproj (Version, AssemblyVersion,
FileVersion), build.yaml (version) and the newest entry of manifest.json. They have to match,
and every manifest entry has to point at its own v<major.minor.patch> release.
"""

import hashlib
import json
import re
import sys
import urllib.request
import xml.etree.ElementTree as ET

REPO = "https://git.psmattas.com/ArcFin/Arcfin-Sync"
VERSION = re.compile(r"^\d+\.\d+\.\d+\.\d+$")
errors: list[str] = []


def error(message: str) -> None:
    errors.append(message)
    print(f"::error::{message}")


def key(version: str) -> tuple[int, ...]:
    return tuple(int(p) for p in version.split("."))


def project() -> str:
    group = ET.parse("Jellyfin.Plugin.ArcfinSync.csproj").getroot().find("PropertyGroup")
    found = {name: (group.findtext(name) or "").strip() for name in ("Version", "AssemblyVersion", "FileVersion")}
    if len(set(found.values())) != 1:
        error(f"Jellyfin.Plugin.ArcfinSync.csproj: Version, AssemblyVersion and FileVersion differ: {found}")
    return found["Version"]


def build_yaml() -> dict[str, str]:
    values = {}
    for line in open("build.yaml", encoding="utf-8"):
        match = re.match(r'^(name|guid|version|targetAbi):\s*"?([^"\n]*)"?\s*$', line)
        if match:
            values[match.group(1)] = match.group(2)
    return values


def manifest(build: dict[str, str]) -> list[dict]:
    try:
        plugins = json.load(open("manifest.json", encoding="utf-8"))
    except json.JSONDecodeError as e:
        error(f"manifest.json isn't valid JSON: {e}")
        return []
    if not isinstance(plugins, list) or len(plugins) != 1:
        error("manifest.json should list exactly one plugin")
        return []
    plugin = plugins[0]
    if plugin.get("guid") != build.get("guid"):
        error(f"manifest.json guid {plugin.get('guid')} isn't build.yaml's {build.get('guid')}")
    if plugin.get("name") != build.get("name"):
        error(f"manifest.json name {plugin.get('name')!r} isn't build.yaml's {build.get('name')!r}")
    versions = plugin.get("versions") or []
    seen = set()
    for entry in versions:
        v = entry.get("version", "")
        where = f"manifest.json {v or '(no version)'}"
        if not VERSION.match(v):
            error(f"{where}: version isn't four numbers")
            continue
        if v in seen:
            error(f"{where}: listed twice")
        seen.add(v)
        for field in ("changelog", "targetAbi", "sourceUrl", "checksum", "timestamp"):
            if not entry.get(field):
                error(f"{where}: no {field}")
        if not re.fullmatch(r"[0-9a-f]{32}", entry.get("checksum", "")):
            error(f"{where}: checksum isn't an MD5 (32 lowercase hex)")
        tag = "v" + ".".join(v.split(".")[:3])
        expected = f"{REPO}/releases/download/{tag}/arcfin-sync-{v}.zip"
        if entry.get("sourceUrl") != expected:
            error(f"{where}: sourceUrl should be {expected}")
        if not re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ", entry.get("timestamp", "")):
            error(f"{where}: timestamp isn't like 2026-11-24T05:25:00Z")
    order = [e.get("version", "0.0.0.0") for e in versions if VERSION.match(e.get("version", ""))]
    if order != sorted(order, key=key, reverse=True):
        error("manifest.json versions aren't newest first")
    return versions


def check_zips(versions: list[dict]) -> None:
    for entry in versions:
        url, want = entry.get("sourceUrl", ""), entry.get("checksum", "")
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "arcfin-sync-ci"})
            with urllib.request.urlopen(request, timeout=60) as response:
                got = hashlib.md5(response.read()).hexdigest()
        except Exception as e:
            error(f"{entry.get('version')}: couldn't download {url} ({e})")
            continue
        if got != want:
            error(f"{entry.get('version')}: the zip's MD5 is {got}, the manifest says {want}")
        else:
            print(f"{entry.get('version')}: zip matches its checksum")


def main() -> int:
    version = project()
    build = build_yaml()
    if build.get("version") != version:
        error(f"build.yaml version {build.get('version')} isn't the project's {version}")
    versions = manifest(build)
    if versions and versions[0].get("version") != version:
        error(f"the newest manifest entry is {versions[0].get('version')}, the project is {version}: "
              "a release PR raises all three together")
    if versions and versions[0].get("targetAbi") != build.get("targetAbi"):
        error(f"the newest manifest entry targets {versions[0].get('targetAbi')}, build.yaml {build.get('targetAbi')}")
    if "--zips" in sys.argv:
        check_zips(versions)
    if not errors:
        print(f"Version {version} matches in the project, build.yaml and the manifest ({len(versions)} releases listed).")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
