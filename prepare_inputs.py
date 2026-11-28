"""Restore binary build inputs from the matching full release. Run from a source checkout."""
import argparse
import hashlib
from pathlib import Path, PurePosixPath
import shutil
from tempfile import TemporaryDirectory
import xml.etree.ElementTree as ET
from zipfile import ZipFile


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def restore(package, portable):
    root = Path(__file__).resolve().parent / "Source"
    version = ET.parse(root / "GTA6Launcher/GTA6Launcher.csproj").findtext("./PropertyGroup/Version")
    name = f"SRLauncher-{version}.zip"
    if package.name != name:
        raise ValueError(f"Expected {name}")
    checksum = Path(__file__).resolve().parent / "Updates" / (name + ".sha256")
    if checksum.read_text(encoding="ascii").strip() != f"{digest(package)}  {name}":
        raise ValueError("Release ZIP checksum mismatch")
    files = {"Runtime/Components/Library.zip"}
    trees = []
    if portable:
        files.update(("OriginalGame/game_scarlett_master.exe", "Community/adapter-info.exe"))
        trees.extend(("Media/LoadingScreens/", "Runtime/DLSS/", "Runtime/Native/", "Runtime/Python/", "Runtime/DotNet/"))
        for project in ET.parse(root / "Community/projects.xml").getroot():
            base = "Community/" + project.attrib["id"] + "/"
            trees.append(base + "runtime/" + project.attrib["commit"] + "/")
            files.add(base + project.attrib["image"])
    prefix = f"Launcher {version}/"
    found = set()
    with ZipFile(package) as archive, TemporaryDirectory(dir=root) as scratch:
        for entry in archive.infolist():
            if not entry.filename.startswith(prefix) or entry.is_dir():
                continue
            relative = entry.filename[len(prefix):]
            if relative not in files and not any(relative.startswith(tree) for tree in trees):
                continue
            parts = PurePosixPath(relative).parts
            if any(p in (".", "..", "/") or ":" in p or "\\" in p for p in parts):
                raise ValueError(relative)
            target = root.joinpath(*parts)
            if not target.resolve().is_relative_to(root.resolve()) or relative in found:
                raise ValueError(relative)
            found.add(relative)
            temp = Path(scratch) / "input"
            with archive.open(entry) as src, temp.open("wb") as out:
                shutil.copyfileobj(src, out, 1024 * 1024)
            target.parent.mkdir(parents=True, exist_ok=True)
            temp.replace(target)
    if files - found or any(not any(f.startswith(tree) for f in found) for tree in trees):
        raise ValueError("Incomplete release inputs")
    print(f"Restored {len(found)} build inputs from {name}.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("zip", type=Path)
    parser.add_argument("--portable", action="store_true", help="also restore inputs for a full release build")
    args = parser.parse_args()
    restore(args.zip, args.portable)
