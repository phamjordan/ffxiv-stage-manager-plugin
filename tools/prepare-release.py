#!/usr/bin/env python3
"""Package a built plugin and stage its Dalamud feed for publication."""
import hashlib
import json
import re
import time
import xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = "https://github.com/phamjordan/ffxiv-stage-manager-plugin"
BUILD = ROOT / "Plugin/bin/Release"
OUT = ROOT / "artifacts"


def main():
    version = ET.parse(ROOT / "Plugin/StageManager.csproj").findtext(".//Version")
    if not version or not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise SystemExit("Use a three-part numeric project version before packaging.")
    manifest = json.loads((BUILD / "StageManager.json").read_text())
    if manifest["AssemblyVersion"] != version + ".0":
        raise SystemExit("The build is stale. Rebuild Release before packaging.")
    if manifest["InternalName"] != "StageManager" or manifest["DalamudApiLevel"] != 15:
        raise SystemExit("Unexpected plugin identity or API; review release compatibility.")
    if manifest.get("RepoUrl") != REPOSITORY:
        raise SystemExit("The built manifest has an unexpected repository URL.")

    OUT.mkdir(exist_ok=True)
    archive = OUT / f"StageManager-{version}-win-x64.zip"
    names = ["StageManager.dll", "StageManager.Core.dll", "StageManager.json", "StageManager.deps.json"]
    for name in names:
        if not (BUILD / name).is_file():
            raise SystemExit(f"Missing build output: {name}")
    with ZipFile(archive, "w", ZIP_DEFLATED) as package:
        for name in names:
            package.write(BUILD / name, name)
        package.write(ROOT / "README.md", "README.md")
    with ZipFile(archive) as package:
        if package.testzip() is not None:
            raise SystemExit("Archive integrity check failed.")
        if set(package.namelist()) != set(names + ["README.md"]):
            raise SystemExit("Unexpected files in the plugin archive.")
        if json.loads(package.read("StageManager.json")) != manifest:
            raise SystemExit("Packaged manifest differs from the build.")

    download = f"{REPOSITORY}/releases/download/v{version}/{archive.name}"
    entry = dict(manifest)
    entry.update({
        "LastUpdate": int(time.time()),
        "IsHide": False,
        "IsTestingExclusive": False,
        "DownloadLinkInstall": download,
        "DownloadLinkUpdate": download,
        "DownloadLinkTesting": download,
        "Changelog": (ROOT / "RELEASE-NOTES.md").read_text().strip(),
    })
    # Stage only: publish the release asset before committing the public feed.
    (OUT / "repo.json").write_text(json.dumps([entry], indent=2) + "\n")
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    (OUT / "SHA256SUMS.txt").write_text(f"{digest}  {archive.name}\n")
    print(f"{archive.name}: {archive.stat().st_size:,} bytes; SHA-256 {digest}")
    print("Staged artifacts/repo.json. Publish the release asset, then copy it to repo.json.")


if __name__ == "__main__":
    main()
