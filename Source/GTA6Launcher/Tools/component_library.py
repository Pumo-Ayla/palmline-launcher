"""Pack the release's redistributable runtime files; never inventory a user's game or profile."""
import argparse
import hashlib
from pathlib import Path
from tempfile import TemporaryDirectory
import xml.etree.ElementTree as ET
from zipfile import ZIP_DEFLATED, ZipFile


def build_library(tree, runtime, index, library, transfers=None):
    inputs = {}

    def add(group, name, path=None):
        path = path or tree / name
        if not path.is_file():
            raise FileNotFoundError(path)
        if name in inputs:
            raise ValueError(f"Duplicate component path: {name}")
        inputs[name] = group, path

    def folder(group, name, path):
        items = sorted(p for p in path.rglob("*") if p.is_file())
        if not items:
            raise FileNotFoundError(path)
        for item in items:
            add(group, name + "/" + item.relative_to(path).as_posix(), item)

    for name in ("host", "shared/Microsoft.NETCore.App", "shared/Microsoft.WindowsDesktop.App"):
        folder("desktop" if "WindowsDesktop" in name else "net", "Runtime/DotNet/" + name, runtime / "DotNet" / name)
    for name in ("dotnet.exe", "LICENSE.txt", "ThirdPartyNotices.txt"):
        add("net", "Runtime/DotNet/" + name, runtime / "DotNet" / name)
    folder("python", "Runtime/Python", runtime / "Python")
    for name in ("python.exe", "python312._pth", "python312.dll", "python312.zip", "DLLs/_ctypes.pyd"):
        if "Runtime/Python/" + name not in inputs:
            raise FileNotFoundError(name)
    for name in ("session.py", "warmup_job.py", "key_prompts.py"):
        add("python", "XCL-VIctory-Lab/desktop/" + name)
    for item in sorted((runtime / "Native").glob("*.dll")):
        if item.stem.lower().startswith(("msvcp", "vcruntime", "concrt", "vccorlib")):
            add("crt", "Runtime/Native/" + item.name, item)
    if "Runtime/Native/msvcp140.dll" not in inputs:
        raise FileNotFoundError("Missing x64 Visual C++ runtime")
    for name in ("dxc.exe", "dxil.dll", "dxcompiler.dll", "DXC-LICENSE-MS.txt", "DXC-LICENSE-LLVM.txt"):
        add("shader", "Runtime/Native/" + name)
    for name in ("SDL3.dll", "LICENSE.txt"):
        add("launcher", "Input/" + name)
    for item in sorted((transfers or tree / "Runtime/Transfers").iterdir()):
        if item.suffix in {".dll", ".json", ".exe"}:
            add("transfer", "Runtime/Transfers/" + item.name, item)
    for name in ("Relay.Transfer.exe", "Relay.Transfer.dll", "SharpCompress.dll", "Relay.Transfer.runtimeconfig.json"):
        if "Runtime/Transfers/" + name not in inputs:
            raise FileNotFoundError(name)
    add("launcher", "Community/adapter-info.exe")
    add("metadata", "Community/projects.xml")
    for project in ET.parse(tree / "Community/projects.xml").getroot():
        key, rev = project.attrib["id"], project.attrib["commit"]
        base = f"Community/{key}/runtime/{rev}/"
        add("metadata", f"Community/{key}/{project.attrib['image']}")
        add(key, base + ("xcl.exe" if key == "xcl" else "victory.exe"))
        add(key, base + "LICENSE")
        if key == "cgbj":
            for name in ("vcruntime140.dll", "mapped-vi/victory.exe", "mapped-vi/vcruntime140.dll"):
                add(key, base + name)
            add(key, f"Community/{key}/{rev}/Play-VIctory.cmd")
        if key == "victory-nvidia":
            add(key, base + "preload.env")
            for name in ("LICENSE.md", "amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll"):
                add("graphics", base + "fidelityfx/" + name)
    for name in ("CgbjDlss.dll", "DlssCapabilities.exe", "nvngx_dlss.dll", "nvngx_dlss.license.txt"):
        add("graphics", "Runtime/DLSS/" + name)
    for name in ("3rd-party-licenses.md", "CgbjFrameGeneration.dll", "license.txt", "nvngx_dlss.dll",
                 "nvngx_dlss.license.txt", "nvngx_dlssg.dll", "reflex.license.txt", "sl.common.dll",
                 "sl.dlss_g.dll", "sl.dlss.dll", "sl.interposer.dll", "sl.pcl.dll", "sl.reflex.dll"):
        add("graphics", "Runtime/DLSS/FrameGeneration/" + name)

    index.parent.mkdir(parents=True, exist_ok=True)
    library.parent.mkdir(parents=True, exist_ok=True)
    rows, seen = [], set()
    with TemporaryDirectory(dir=library.parent) as temp:
        data = Path(temp) / "Library.zip"
        with ZipFile(data, "w", ZIP_DEFLATED, compresslevel=4) as archive:
            for name, (group, path) in sorted(inputs.items()):
                payload = path.read_bytes()
                digest = hashlib.sha256(payload).hexdigest()
                if digest not in seen:
                    archive.writestr(digest, payload)
                    seen.add(digest)
                rows.append(f"{group}\t{name}\t{len(payload)}\t{digest}\n")
        data.replace(library)
    index.write_bytes("".join(rows).encode("utf-8"))
    print(f"Packed {len(rows)} files into {library} ({library.stat().st_size // 1048576} MiB)")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for arg in ("tree", "runtime", "index", "library"):
        parser.add_argument("--" + arg, required=True, type=Path)
    parser.add_argument("--transfers", type=Path)
    args = parser.parse_args()
    build_library(args.tree, args.runtime, args.index, args.library, args.transfers)
