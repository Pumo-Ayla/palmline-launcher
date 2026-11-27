"""Publishable catalogs and a standalone emulator archive for a verified launcher package."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET
from zipfile import ZIP_DEFLATED, ZIP_STORED, ZipFile


ROOT = Path(__file__).resolve().parent
REPO = "Pumo-Ayla/series-relay-launcher"


def asset(path, version):
    with path.open("rb") as stream:
        checksum = hashlib.file_digest(stream, "sha256").hexdigest()
    return {"name": path.name, "size": path.stat().st_size, "sha256": checksum,
            "url": f"https://github.com/{REPO}/releases/download/v{version}/{path.name}"}


def catalog(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def release_notes(updates, version):
    history = json.loads((updates / "history.json").read_text(encoding="utf-8"))
    versions = [tuple(map(int, row["version"].split("."))) for row in history]
    if (not versions or versions != sorted(set(versions)) or any(len(v) != 3 for v in versions)
            or history[-1]["version"] != version):
        raise ValueError("Release history must be ordered, unique and end at this release")
    if any(row["type"] not in ("normal", "critical") or not row["notes"].strip() for row in history):
        raise ValueError("Every version needs a type and release notes")
    critical = [r["version"] for r in history if r["type"] == "critical"]
    minimum = critical[-1] if critical else None
    titles = {"normal": "普通更新", "critical": "关键更新"}
    lines = [f"**{version} · {titles[history[-1]['type']]}**", "", history[-1]["notes"], "",
             "## 版本历史", "", "| 版本 | 更新类型 | 更新说明 |", "| --- | --- | --- |"]
    for row in reversed(history):
        lines.append(f"| {row['version']} | {titles[row['type']]} | {row['notes']} |")
    lines.extend(["", "完整安装或升级请使用 `Launcher-" + version + ".zip`。独立模拟器包为 `SR-Emulators-" + version + ".zip`。", ""])
    if minimum:
        lines.append(f"<!-- min-launcher-version: {minimum} -->")
    lines.extend(["<!-- sr-update-history-v1", *(r["version"] + " " + r["type"] for r in history), "-->", ""])
    (updates / "RELEASE.md").write_text("\n".join(lines), encoding="utf-8")
    return minimum


def prepare():
    version = ET.parse(ROOT / "Source/GTA6Launcher/GTA6Launcher.csproj").findtext("./PropertyGroup/Version")
    updates = ROOT / "Updates"
    minimum = release_notes(updates, version)
    package = updates / f"Launcher-{version}.zip"
    details = asset(package, version)
    if (updates / (package.name + ".sha256")).read_text("ascii").strip() != f"{details['sha256']}  {package.name}":
        raise ValueError("Full package SHA-256 mismatch")
    engines = ROOT / "Emulators"
    engines.mkdir(exist_ok=True)
    target = engines / f"SR-Emulators-{version}.zip"
    partial = target.with_suffix(".partial")
    prefix = f"Launcher {version}/"
    try:
        with ZipFile(package) as src, ZipFile(partial, "w", ZIP_DEFLATED, compresslevel=3) as out:
            projects = ET.fromstring(src.read(prefix + "Community/projects.xml"))
            snapshots = {f"Sources/{node.attrib['id']}.zip" for node in projects}
            names = set()
            for entry in src.infolist():
                name = entry.filename.removeprefix(prefix)
                if entry.is_dir() or not (name.startswith("Community/") or name in snapshots):
                    continue
                names.add(name)
                meta = type(entry)(name, entry.date_time)
                meta.compress_type = ZIP_STORED if name.endswith((".zip", ".png")) else ZIP_DEFLATED
                with src.open(entry) as inp, out.open(meta, "w") as dest:
                    shutil.copyfileobj(inp, dest, 1 << 20)
            if not snapshots.issubset(names):
                raise ValueError("Missing emulator source archive")
        partial.replace(target)
    finally:
        partial.unlink(missing_ok=True)
    emulator_asset = asset(target, version)
    (engines / (target.name + ".sha256")).write_text(f"{emulator_asset['sha256']}  {target.name}\n", encoding="ascii")
    catalog(engines / "catalog.json", {"version": version, "package": emulator_asset, "projects": [
        {key: node.attrib[key] for key in ("id", "name", "repo", "commit")} for node in projects]})
    catalog(updates / "latest.json", {"tag": f"v{version}", "version": version,
        "minimumLauncherVersion": minimum, "package": details})


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()
    prepare()
