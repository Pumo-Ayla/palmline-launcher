import base64
import subprocess
import tempfile
from pathlib import Path
from zipfile import ZipFile

engine = Path(__file__).resolve().parents[2] / "Transfers/bin/Release/net9.0/Relay.Transfer.exe"
def transfer(command, path, out, success=True):
    with subprocess.Popen([engine, command], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, text=True, encoding="utf8") as p:
        for arg in (path, out):
            p.stdin.write(base64.b64encode(str(arg).encode()).decode()+"\n")
        p.stdin.flush()
        try:
            code = p.wait(30)
        finally:
            p.kill()
        stdout, stderr = p.stdout.read(), p.stderr.read()
        assert (code == 0) == success, (command, code, stderr, stdout)
        if success:
            return next(x[5:] for x in stdout.splitlines() if x.startswith("DONE "))

with tempfile.TemporaryDirectory() as temp:
    root = Path(temp)
    game = root/"game.zip"
    with ZipFile(game, "w") as z:
        z.writestr("title/MicrosoftGame.Config", '<Game><ExecutableList><Executable Name="game_scarlett_master.exe"/></ExecutableList></Game>')
        z.writestr("title/game_scarlett_master.exe", b"MZfixture")
        z.writestr("title/asset", "fixture")
    assert transfer("--check-archive", game, root) == "1"
    out = Path(transfer("--unpack-game", game, root))
    assert game.exists() and (out/"title/asset").is_file()
    exe = Path(transfer("--unpack-exe", game, root))
    assert [p.name for p in exe.iterdir()] == ["game_scarlett_master.exe"]
    out = Path(transfer("--unpack-release", game, root))
    assert not game.exists() and (out/"title/asset").is_file()
    for name, entry in (("missing", "data.bin"), ("escape", "../outside")):
        bad = root/(name+".zip")
        with ZipFile(bad, "w") as z:
            z.writestr(entry, "fixture")
        transfer("--unpack-release", bad, root, success=False)
        assert bad.exists() and not (root/"outside").exists()
    print("PASS: inspect, keep source, EXE-only, release, retain on failure, contain paths")
