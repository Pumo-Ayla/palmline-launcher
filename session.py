"""Serve one desktop selection. EOF after warmup never starts the title."""
import argparse
from contextlib import contextmanager
from datetime import datetime
import hashlib
import msvcrt
import os
from pathlib import Path
import re
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import launcher as lab

UP = lab.LAB / "upstream-research"
STOCK = UP / "ztrx-official-90b529f878a4/target/release/victory.exe"
AMD = UP / "victory-belobug-amd/target/release/victory.exe"
ENGINES = {
    "local": (lab.RUNTIME, lab.RUNTIME, lab.STATE),
    "stock": (STOCK, UP / "ztrx-90b529f878a4-prewarm/target/release/victory.exe", lab.LAB / "profiles/zettrox"),
    "amd": (AMD, AMD, lab.LAB / "profiles/belobug"),
}


def send(kind, value):
    try:
        print(kind, str(value).replace("\r", " ").replace("\n", " "), sep="\t", flush=True)
    except OSError:
        pass  # Closing the UI during a game leaves its on-disk log running.


@contextmanager
def exclusive(path):
    with path.open("a+b") as f:
        f.seek(0)
        try:
            msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as exc:
            raise RuntimeError("该项目正在预热或运行，请查看已有会话。") from exc
        yield


def environment(key, state):
    if key == "local":
        return lab.integrated_environment(state / "data")
    env = {k: v for k, v in os.environ.items() if not k.startswith(("VICTORY_", "XVZ_", "LAUNCHER_Z_"))}
    env.update(VICTORY_CACHE_DIR=str(state / "cache"), VICTORY_DATA_DIR=str(state / "data"),
               VICTORY_GAME_DIR=str(lab.GAME), VICTORY_LOG="warn,victory_gpu::queue=info")
    if key == "stock":
        if hashlib.sha256(STOCK.read_bytes()).hexdigest() != "df2c815f86c7bd4f241fd49616b9c5634866de59bd721b180da6ee6ad07922a2":
            raise RuntimeError("ZETTROX 版本已变更，请同步更新该版本的预热工具。")
        env.update(VICTORY_ADAPTER="NVIDIA", LAUNCHER_Z_IMAGE=str(STOCK))
    if key == "amd":
        env.update(VICTORY_PRESENT_TARGET="1x1:1", VICTORY_HAIR="1")
    return env


def play(exe, env, run):
    game = lab.GAME / "game_scarlett_master.exe"
    with (run / "runtime.log").open("wb") as log:
        p = subprocess.Popen([exe, "run", game, "--execute", "--console", "series-x"], cwd=run,
                             env=env, stdout=log, stderr=subprocess.STDOUT,
                             creationflags=subprocess.CREATE_NO_WINDOW)
        (run / "game-pid.txt").write_text(str(p.pid), encoding="ascii")
        lab.phase(run, "launch")
        send("PLAYING", p.pid)
        # Disk retains every byte. Bound what the UI follows during very verbose frames.
        with (run / "runtime.log").open("rb") as tail:
            while p.poll() is None:
                end = log.tell()
                if end - tail.tell() > 32768:
                    tail.seek(end - 32768)
                    tail.readline()
                for line in tail.readlines(32768)[-6:]:
                    send("LOG", line.decode("utf-8", "replace").strip()[:550])
                time.sleep(.5)
        return p.returncode


def session(key):
    runtime, warmer, state = ENGINES[key]
    run = lab.LAB / "runs" / (datetime.now().strftime("Desktop-%Y%m%d-%H%M%S-%f") + "-" + key)
    run.mkdir(parents=True)
    send("RUN", run)
    try:
        for path in (runtime, warmer, lab.SELECTOR, lab.GAME / "shaders.rpf", lab.GAME / "game_scarlett_master.exe"):
            if not path.is_file():
                raise FileNotFoundError(path)
        state.mkdir(parents=True, exist_ok=True)
        env = environment(key, state)
        lock = state / "integrated-run.lock"
        counts = {}

        def progress(line):
            match = re.search(r"(跳过|拒绝) (\d+)$", line)
            if match:
                counts[match[1]] = int(match[2])
            send("LOG", line)

        with exclusive(lock):
            send("STAGE", "正在匹配启动着色器")
            lab.prepare(run, env, False, runtime=warmer, pipelines=key == "local", notify=progress)
        lab.phase(run, "prepared")
        details = []
        if counts.get("跳过"):
            details.append(f'{counts["跳过"]} 个着色器跳过')
        if counts.get("拒绝"):
            details.append(f'{counts["拒绝"]} 条管线被拒绝')
        send("READY", "预热完成" + (" · " + "，".join(details) if details else ""))
        if sys.stdin.readline().strip() != "PLAY":
            return 0
        with exclusive(lock):
            if key == "local":
                env["XVZ_LIVE_SHADER_MS"] = "16"
            code = play(runtime, env, run)
            lab.phase(run, f"exit {code}")
            send("EXIT", code)
            return 0 if code == 0 else 1
    except (OSError, RuntimeError, ValueError) as exc:
        lab.phase(run, f"error {exc}")
        send("ERROR", exc)
        return 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("engine", choices=ENGINES)
    sys.exit(session(parser.parse_args().engine))
