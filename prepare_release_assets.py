"""Publishable catalogs and a standalone emulator archive for a verified launcher package."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
from zipfile import ZIP_STORED, ZipFile


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
    if (not versions or versions != sorted(set(versions)) or any(len(v) not in (3, 4) or (len(v) == 4 and not v[-1]) for v in versions)
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
    lines.extend(["", "全新安装请解压 `SRLauncher-" + version + ".zip` 并运行 `SRLauncher.exe`。", "", "从 1.0.4 或更早版本升级，请将 `SRLauncher-" + version + "-offline.zip` 解压到单独的临时文件夹。关闭旧启动器和游戏，运行包内的 `Upgrade-Launcher.exe`，输入旧启动器所在目录。完成后从原目录启动。", "", "GitHub 的 Source code (zip) 用于再开发。", ""])
    if minimum:
        lines.append(f"<!-- min-launcher-version: {minimum} -->")
    lines.extend(["<!-- sr-update-history-v1", *(r["version"] + " " + r["type"] for r in history), "-->", ""])
    (updates / "RELEASE.md").write_text("\n".join(lines), encoding="utf-8")
    return minimum


def prepare():
    version = ET.parse(ROOT / "Source/GTA6Launcher/GTA6Launcher.csproj").findtext("./PropertyGroup/Version")
    updates = ROOT / "Updates"
    minimum = release_notes(updates, version)
    package = updates / f"SRLauncher-{version}.zip"
    details = asset(package, version)
    if (updates / (package.name + ".sha256")).read_text("ascii").strip() != f"{details['sha256']}  {package.name}":
        raise ValueError("Full package SHA-256 mismatch")
    offline = updates / f"SRLauncher-{version}-offline.zip"
    partial = offline.with_suffix(".partial")
    try:
        with ZipFile(partial, "w", ZIP_STORED) as out:
            for file in (package, updates / (package.name + ".sha256"), updates / "Upgrade-Launcher.exe", updates / "Upgrade-Launcher.exe.config"):
                out.write(file, file.name)
        partial.replace(offline)
    finally:
        partial.unlink(missing_ok=True)
    catalog(updates / "latest.json", {"tag": f"v{version}", "version": version,
        "minimumLauncherVersion": minimum, "package": details, "offline": asset(offline, version)})


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()
    prepare()
