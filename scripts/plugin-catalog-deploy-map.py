#!/usr/bin/env python3
"""Emit the bash deploy map for one platform/RID from plugins/catalog.json.

plugins/catalog.json stays the authoritative plugin list. This renders the one
view the build needs — id -> project path, filtered to a platform/RID — as bash
declarations for scripts/deploy-linux-plugins.sh to source.

Deliberately Python and not PowerShell: this runs from TypeWhisper.Linux's
AfterTargets="Build" hook, so every `dotnet build` would otherwise require
PowerShell 7 on a Linux desktop. python3 is part of the base install on the
distributions this app targets. The exhaustive catalog validation still lives in
scripts/plugin-catalog.ps1 and runs in CI; the checks here are the ones a deploy
depends on being true.

usage: plugin-catalog-deploy-map.py --platform linux --rid linux-x64 [--root DIR]
"""

import argparse
import json
import os
import re
import sys

RID_PLATFORMS = {
    "linux-x64": "linux",
    "linux-arm64": "linux",
    "win-x64": "windows",
    "win-arm64": "windows",
    "osx-x64": "macos",
    "osx-arm64": "macos",
}

REQUIRED_KEYS = ("id", "projectPath", "releaseSlug", "platforms", "rids", "sdkAbi")

CANONICAL_PROJECT_PATH = re.compile(
    r"^plugins/TypeWhisper\.Plugin\.[A-Za-z0-9]+/TypeWhisper\.Plugin\.[A-Za-z0-9]+\.csproj$"
)


def fail(message):
    print(f"ERROR: {message}", file=sys.stderr)
    raise SystemExit(1)


def shell_single_quote(value):
    return "'" + value.replace("'", "'\"'\"'") + "'"


def exists_with_exact_casing(root, relative_path):
    """`os.path.exists` is case-insensitive on some mounts; walk the parts instead.

    A catalog entry whose casing differs from the file on disk builds here and then
    fails on a case-sensitive checkout, so treat a casing mismatch as absent.
    """
    current = root
    for part in relative_path.split("/"):
        try:
            entries = os.listdir(current)
        except OSError:
            return False
        if part not in entries:
            return False
        current = os.path.join(current, part)
    return True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--platform", required=True)
    parser.add_argument("--rid", required=True)
    parser.add_argument(
        "--root",
        default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    )
    args = parser.parse_args()

    if args.rid not in RID_PLATFORMS:
        fail(f"Unknown deploy RID: {args.rid}")
    if RID_PLATFORMS[args.rid] != args.platform:
        fail(f"Deploy RID '{args.rid}' does not belong to platform '{args.platform}'.")

    catalog_path = os.path.join(args.root, "plugins", "catalog.json")
    try:
        with open(catalog_path, encoding="utf-8") as handle:
            catalog = json.load(handle)
    except OSError as error:
        fail(f"Could not read {catalog_path}: {error}")
    except json.JSONDecodeError as error:
        fail(f"{catalog_path} is not valid JSON: {error}")

    plugins = catalog.get("plugins")
    if not isinstance(plugins, list) or not plugins:
        fail("Catalog has no 'plugins' array.")

    seen_ids = set()
    seen_paths = set()
    selected = []
    for plugin in plugins:
        if not isinstance(plugin, dict):
            fail("Catalog entries must be objects.")
        missing = [key for key in REQUIRED_KEYS if key not in plugin]
        if missing:
            fail(f"Catalog entry is missing {', '.join(missing)}: {plugin.get('id', '?')}")

        plugin_id = plugin["id"]
        project_path = plugin["projectPath"]
        if not isinstance(plugin_id, str) or not plugin_id.strip():
            fail("Catalog entry has an empty id.")
        if not isinstance(project_path, str) or not CANONICAL_PROJECT_PATH.match(project_path):
            fail(f"projectPath is not canonical for '{plugin_id}': {project_path}")
        if plugin_id in seen_ids:
            fail(f"Duplicate id in catalog: {plugin_id}")
        if project_path in seen_paths:
            fail(f"Duplicate projectPath in catalog: {project_path}")
        if not exists_with_exact_casing(args.root, project_path):
            fail(f"Catalog projectPath does not exist with exact casing: {project_path}")
        seen_ids.add(plugin_id)
        seen_paths.add(project_path)

        platforms = plugin["platforms"]
        rids = plugin["rids"]
        if not isinstance(platforms, list) or not isinstance(rids, list):
            fail(f"platforms and rids must be arrays for '{plugin_id}'.")
        # Case-sensitive on purpose: every consumer selects RIDs case-sensitively, so a
        # 'Linux-X64' entry would pass a lenient check and then vanish from this map.
        if args.platform in platforms and args.rid in rids:
            selected.append((plugin_id, project_path))

    if not selected:
        fail(f"No plugins support {args.platform}/{args.rid}.")

    selected.sort(key=lambda entry: entry[0])

    out = sys.stdout
    out.write("declare -A PLUGINS=(\n")
    for plugin_id, project_path in selected:
        out.write(f"  [{shell_single_quote(plugin_id)}]={shell_single_quote(project_path)}\n")
    out.write(")\n")
    out.write("declare -a PLUGIN_IDS=(\n")
    for plugin_id, _ in selected:
        out.write(f"  {shell_single_quote(plugin_id)}\n")
    out.write(")\n")


if __name__ == "__main__":
    main()
