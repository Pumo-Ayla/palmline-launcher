"""Check a clean pre-upload tree, including the source archives contained in the release."""
import hashlib
import io
from pathlib import Path, PurePosixPath
import xml.etree.ElementTree as ET
from zipfile import ZipFile


ROOT = Path(__file__).resolve().parent
PERSONAL_DIRS = {
    "gamesaves", "xcl-data", "play-history", "keyboard-slots", "game-artwork", "profiles",
    ".launcher-update", "__pycache__", "shader-cache", "shader_cache", "pipeline-cache",
}
PERSONAL_FILES = {
    "display-settings.xml", "game-instances.xml", "selected-project.txt", "game-selection.txt",
    "preferred-game-exe.txt", "progress-state.txt", "used-pipelines.txt", "victory-settings.txt",
    "game-pid.txt", "integrated-run.lock", "render.xml",
}


def check_name(name):
    path = PurePosixPath(name.lower())
    if (PERSONAL_DIRS.intersection(path.parts) or path.name in PERSONAL_FILES
            or path.suffix in {".sav", ".save", ".log", ".tmp", ".pyc", ".dmp", ".suo", ".user"}
            or path.name.startswith("sgta")):
        raise ValueError(f"Unexpected local data: {name}")


def main():
    source = ROOT / "Source"
    for path in source.rglob("*"):
        check_name(path.relative_to(source).as_posix())
    version = ET.parse(source / "GTA6Launcher/GTA6Launcher.csproj").findtext("./PropertyGroup/Version")
    package = ROOT / "Updates" / f"SRLauncher-{version}.zip"
    with package.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if Path(str(package) + ".sha256").read_text("ascii").strip() != f"{digest}  {package.name}":
        raise ValueError("ZIP checksum mismatch")
    prefix = f"Launcher {version}/"
    with ZipFile(package) as archive:
        entries = archive.namelist()
        if len(entries) != len({n.casefold() for n in entries}):
            raise ValueError("Duplicate release entries")
        if archive.read(prefix + "SRLauncher.exe") != archive.read(prefix + "Launcher.exe"):
            raise ValueError("Public launcher entry and legacy apphost differ")
        for name in entries:
            if not name.startswith(prefix):
                raise ValueError(f"Invalid release root: {name}")
            relative = name[len(prefix):]
            check_name(relative)
            if relative.startswith("XCL-VIctory-Lab/") and relative not in {
                "XCL-VIctory-Lab/launcher.py", "XCL-VIctory-Lab/desktop/session.py",
                "XCL-VIctory-Lab/desktop/warmup_job.py", "XCL-VIctory-Lab/desktop/key_prompts.py",
            }:
                raise ValueError(f"Unexpected session file: {relative}")
            if relative.startswith("Sources/") and relative.endswith(".zip"):
                with ZipFile(io.BytesIO(archive.read(name))) as snapshot:
                    for file in snapshot.namelist():
                        check_name(file)
        kit = prefix + "Runtime/Components/Library.zip"
        with ZipFile(io.BytesIO(archive.read(kit))) as components:
            index = (source / "GTA6Launcher/Assets/ComponentIndex.tsv").read_text("utf-8")
            expected = set()
            for line in index.splitlines():
                _, path, size, sha = line.split("\t")
                check_name(path)
                expected.add(sha)
                if components.getinfo(sha).file_size != int(size):
                    raise ValueError(path)
            if expected != set(components.namelist()):
                raise ValueError("Offline component library and source index differ")
        if archive.testzip() is not None:
            raise ValueError("ZIP integrity check failed")
    print(f"Verified {package.name}, {len(entries)} files, SHA-256 {digest}")


if __name__ == "__main__":
    main()
