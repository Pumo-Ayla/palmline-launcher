"""Assemble a clean, relocatable Windows x64 launcher from local build outputs."""
import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys
from tempfile import TemporaryDirectory
import xml.etree.ElementTree as ET
from zipfile import ZIP_DEFLATED, ZIP_STORED, ZipFile
from component_library import build_library


PROJECT = Path(__file__).resolve().parents[1]
BASE = PROJECT.parent
LAB = BASE / "XCL-VIctory-Lab"
VERSION = ET.parse(PROJECT / "GTA6Launcher.csproj").findtext("./PropertyGroup/Version")
RELEASE = BASE.parent / f"Launcher {VERSION}"


def copy_file(source, dest):
    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, dest)


def copy_tree(source, dest):
    shutil.copytree(source, dest, dirs_exist_ok=True, ignore=shutil.ignore_patterns("*.tmp", "*.TMP", "__pycache__", "*.pyc"))


def files(root, ignored=()):
    for directory, subdirs, names in os.walk(root):
        subdirs[:] = sorted(d for d in subdirs if d not in ignored)
        for name in sorted(names):
            yield Path(directory) / name


def archive(paths, root, out, prefix="", overrides=None):
    out.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(out, "w", ZIP_DEFLATED, compresslevel=3) as dest:
        for src in paths:
            name = (Path(prefix) / src.relative_to(root)).as_posix()
            method = ZIP_STORED if src.suffix.lower() in {".zip", ".jpg", ".png", ".scene"} else ZIP_DEFLATED
            replacement = (overrides or {}).get(src.relative_to(root).as_posix())
            if replacement is None:
                dest.write(src, name, compress_type=method)
            else:
                dest.writestr(name, replacement, compress_type=method)


def bundle_runtimes(dotnet, version, python, crt):
    target = RELEASE / "Runtime"
    for component in ("host/fxr", "shared/Microsoft.NETCore.App", "shared/Microsoft.WindowsDesktop.App"):
        sub = Path(component) / version
        copy_tree(dotnet / sub, target / "DotNet" / sub)
    for name in ("dotnet.exe", "LICENSE.txt", "ThirdPartyNotices.txt"):
        copy_file(dotnet / name, target / "DotNet" / name)
    dest = target / "Python"
    for name in ("python.exe", "python3.dll", "python312.dll", "vcruntime140.dll", "vcruntime140_1.dll", "LICENSE.txt"):
        copy_file(python / name, dest / name)
    copy_tree(python / "DLLs", dest / "DLLs")
    stdlib = python / "Lib"
    ignore = {"__pycache__", "site-packages", "test", "tests", "idlelib", "tkinter", "turtledemo", "ensurepip"}
    archive((p for p in files(stdlib, ignore) if p.suffix != ".pyc"), stdlib, dest / "python312.zip")
    (dest / "python312._pth").write_text("python312.zip\nDLLs\n.\n", encoding="ascii")
    copy_tree(crt, target / "Native")
    dxc = BASE / "XCL/tools/dxc-1.9"
    for name in ("dxcompiler.dll", "dxil.dll", "dxc.exe"):
        copy_file(dxc / name, target / "Native" / name)
    with ZipFile(next(dxc.glob("*.nupkg"))) as upstream:
        for name in upstream.namelist():
            if "license" in name.lower() or "notice" in name.lower():
                if not name.endswith("/") and Path(name).suffix in {".txt", ".md", ".rst"}:
                    (target / "Native" / ("DXC-" + Path(name).name)).write_bytes(upstream.read(name))


def community():
    folder = BASE / "Community"
    for name in ("projects.xml", "adapter-info.exe"):
        copy_file(folder / name, RELEASE / "Community" / name)
    for node in ET.parse(folder / "projects.xml").getroot():
        key, rev = node.attrib["id"], node.attrib["commit"]
        src = folder / key
        runtime = Path("runtime") / rev
        binaries = {"cgbj": ("victory.exe", "mapped-vi/victory.exe"), "xcl": ("xcl.exe",),
                    "victory-nvidia": ("victory.exe", "preload.env", "LICENSE", "fidelityfx/LICENSE.md",
                                       "fidelityfx/amd_fidelityfx_loader_dx12.dll", "fidelityfx/amd_fidelityfx_framegeneration_dx12.dll")}
        for exe in binaries.get(key, ("victory.exe",)):
            if not (src / runtime / exe).is_file():
                raise FileNotFoundError(src / runtime / exe)
        dest = RELEASE / "Community" / key
        copy_tree(src / runtime, dest / runtime)
        # The first three update agents check both historical filenames for every project.
        engine = dest / runtime / ("xcl.exe" if key == "xcl" else "victory.exe")
        for legacy in ("victory.exe", "mapped-vi/victory.exe"):
            if not (dest / runtime / legacy).exists():
                copy_file(engine, dest / runtime / legacy)
        if node.get("sha256") and hashlib.sha256((dest / runtime / "victory.exe").read_bytes()).hexdigest() != node.attrib["sha256"]:
            raise ValueError(f"{key}/victory.exe: checksum mismatch")
        image = node.attrib["image"]
        copy_file(src / image, dest / image)
        snapshot = src / rev
        if key == "cgbj":
            copy_file(snapshot / "Play-VIctory.cmd", dest / rev / "Play-VIctory.cmd")
        if not (snapshot / "Cargo.toml").is_file():
            raise FileNotFoundError(snapshot / "Cargo.toml")
        archive(files(snapshot, {".git", "target", "__pycache__"}), snapshot,
                RELEASE / "Sources" / (key + ".zip"), rev)
        print("Included", key, rev[:12], flush=True)


def assemble(args):
    if RELEASE.exists():
        raise FileExistsError(f"Choose a clean release destination: {RELEASE}")
    RELEASE.mkdir()
    for name in ("SDL3.dll", "LICENSE.txt"):
        copy_file(PROJECT / "Assets/SDL3" / name, RELEASE / "Input" / name)
    transfers = PROJECT / "Transfers"
    build_options = ["-m:1"]
    if args.restore_source:
        build_options.extend([f"-p:RestoreSources={args.restore_source}", "-p:NuGetAudit=false"])
    subprocess.run(["dotnet", "build", str(transfers / "Transfers.csproj"), "-c", "Release", *build_options], check=True)
    for item in (transfers / "bin/Release/net9.0").iterdir():
        if item.suffix in {".dll", ".json", ".exe"}:
            copy_file(item, RELEASE / "Runtime/Transfers" / item.name)
    if args.runtime_from:
        for name in ("DotNet", "Python", "Native"):
            copy_tree(args.runtime_from / name, RELEASE / "Runtime" / name)
    else:
        bundle_runtimes(args.dotnet, args.version, args.python, args.crt)
    copy_tree(BASE / "Runtime" / "DLSS", RELEASE / "Runtime" / "DLSS")
    community()
    copy_tree(BASE / "Media" / "LoadingScreens", RELEASE / "Media" / "LoadingScreens")
    copy_file(BASE / "OriginalGame/game_scarlett_master.exe", RELEASE / "OriginalGame/game_scarlett_master.exe")
    for name in ("launcher.py", "desktop/session.py", "desktop/warmup_job.py", "desktop/key_prompts.py"):
        copy_file(LAB / name, RELEASE / LAB.name / name)
    # The portable console and managed runtimes can differ from the desktop build.
    # Bind this distribution's repair index to its launcher assembly.
    (PROJECT / "obj").mkdir(exist_ok=True)
    with TemporaryDirectory(dir=PROJECT / "obj") as scratch:
        index = Path(scratch) / "ComponentIndex.tsv"
        library = RELEASE / "Runtime/Components/Library.zip"
        build_library(RELEASE, RELEASE / "Runtime", index, library)
        release_index = index.read_bytes()
        subprocess.run(["dotnet", "publish", str(PROJECT / "GTA6Launcher.csproj"), "-c", "Release",
                        "-r", "win-x64", "--self-contained", "false", "-p:PortableBuild=true", *build_options,
                        f"-p:ComponentCatalogPath={index}", f"-p:ComponentLibraryPath={library}",
                        "-o", str(Path(scratch) / "publish")], check=True)
        output = Path(scratch) / "publish"
        for item in files(output):
            relative = item.relative_to(output)
            if item.suffix != ".pdb" and relative.parts[0] != "Runtime":
                copy_file(item, RELEASE / relative)
        copy_tree(output / "Runtime/Notices", RELEASE / "Runtime/Notices")
    paths = (p for p in files(PROJECT, {"obj", "bin", ".vscode", ".git", "__pycache__"})
             if not p.name.startswith("Preview") and p.name != "Design-reference.png")
    archive(paths, PROJECT, RELEASE / "Sources/Launcher.zip", "GTA6Launcher",
            {"Assets/ComponentIndex.tsv": release_index})
    readme = (PROJECT / "Tools/Portable-Readme.txt").read_text(encoding="utf-8")
    (RELEASE / "README.txt").write_text(readme.replace("{version}", VERSION), encoding="utf-8")
    names = [str(p.relative_to(RELEASE)) for p in files(RELEASE)]
    if not all(n.isascii() for n in names):
        raise ValueError([n for n in names if not n.isascii()])
    print("Assembled", len(names), "files", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--zip-only", action="store_true")
    parser.add_argument("--python", type=Path, default=Path(sys.base_prefix))
    parser.add_argument("--dotnet", type=Path, default=Path(os.environ["ProgramFiles"]) / "dotnet")
    parser.add_argument("--version", default="9.0.17")
    parser.add_argument("--crt", type=Path)
    parser.add_argument("--runtime-from", type=Path, help="reuse a verified portable Runtime directory")
    parser.add_argument("--restore-source", help="optional local NuGet cache for an offline build")
    parser.add_argument("--output-dir", type=Path, help="parent directory for the clean release tree and ZIP")
    options = parser.parse_args()
    if options.output_dir:
        options.output_dir.mkdir(parents=True, exist_ok=True)
        RELEASE = options.output_dir.resolve() / RELEASE.name
    if not options.zip_only:
        if not options.crt and not options.runtime_from:
            parser.error("supply --crt or --runtime-from")
        assemble(options)
    output = RELEASE.parent / f"SRLauncher-{VERSION}.zip"
    temporary = output.with_suffix(".zip.partial")
    archive(files(RELEASE), RELEASE, temporary, RELEASE.name)
    temporary.replace(output)
    print(output, output.stat().st_size, flush=True)
