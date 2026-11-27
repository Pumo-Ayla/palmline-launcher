"""Open the desktop launcher or prepare the selected engine's shaders."""
import argparse
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET


def main():
    root = Path(__file__).resolve().parent
    parser = argparse.ArgumentParser(description=__doc__)
    catalog = ET.parse(root.parent / "Community" / "projects.xml")
    parser.add_argument("-Mode", type=str.lower, choices=[p.attrib["id"] for p in catalog.getroot()], default="cgbj")
    parser.add_argument("-PrepareOnly", action="store_true")
    args = parser.parse_args()
    if args.PrepareOnly:
        return subprocess.run([sys.executable, "-X", "utf8", "-B", "-u",
                               root / "desktop/session.py", args.Mode], stdin=subprocess.DEVNULL).returncode
    entry = root.parent / "SRLauncher.exe"
    if not entry.is_file():
        entry = root.parent / "Launcher.exe"
    subprocess.Popen([entry, "--project", args.Mode], cwd=root.parent)
    return 0


if __name__ == "__main__":
    sys.exit(main())
