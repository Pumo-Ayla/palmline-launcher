"""Desktop entry for the integrated Z renderer and its Chinese monitor."""
import argparse
from datetime import datetime
import msvcrt
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

from prepare_startup import run_tool

LAB = Path(__file__).resolve().parent
GAME = LAB.parent
RUNTIME = LAB / "upstream-research/ztrx-main/target/release/victory.exe"
SELECTOR = LAB / "target/release/xcl-select-shaders.exe"
OFFLINE = Path(r"E:\XCL-data\XCL-VIctory-Lab\dxil-cbc296ab65a2")
STATE = LAB / "zettrox-data"


def phase(run, text):
    tmp = run / "state.tmp"
    tmp.write_text(text, encoding="utf-8")
    tmp.replace(run / "progress-state.txt")


def monitor(run):
    conhost = Path(os.environ["SystemRoot"]) / "System32/conhost.exe"
    p = subprocess.Popen([conhost, sys.executable, LAB / "progress_window.py", run,
                          "--dock", "--notify-ready"], cwd=LAB, creationflags=subprocess.CREATE_NEW_CONSOLE)
    deadline = time.monotonic() + 15
    while not (run / "progress-window-ready.txt").exists():
        if p.poll() is not None or time.monotonic() >= deadline:
            raise RuntimeError("中文测试器启动失败")
        time.sleep(0.1)


def prepare(run, env, full, *, runtime=RUNTIME, pipelines=True, notify=None):
    keys = LAB / "shader-prewarm"
    with tempfile.TemporaryDirectory(dir=run, prefix="warm-") as tmp, (run / "prewarm.log").open("w", encoding="utf-8") as log:
        for bulk in ([True, False] if full else [False]):
            phase(run, "select")
            dst = Path(tmp) / ("offline" if bulk else "startup")
            flags = ["--offline-all" if bulk else "--offline-used", OFFLINE]
            if not (OFFLINE / "translated/v2").is_dir():
                if bulk:
                    raise FileNotFoundError(OFFLINE / "translated/v2")
                flags = []
            run_tool([SELECTOR, *flags, GAME / "shaders.rpf",
                      "-" if bulk else keys / "shader-keys.txt",
                      "-" if bulk else keys / "slow-compute-keys.txt", "--dxbc-dir", dst],
                     log, 0, env=env, cwd=run, on_line=notify)
            phase(run, "warm-cache")
            def progress(line):
                if line.startswith("GPU预热 0/"):
                    phase(run, "warm-gpu")
                if notify:
                    notify(line)
            run_tool([runtime, "warm-startup", dst, *(["--pipelines"] if pipelines and not bulk else [])],
                     log, 0, env=env, cwd=run, on_line=progress)


def integrated_environment(data):
    env = os.environ.copy()
    for name in ("KEYS", "CAPTURE", "CAPTURE_FRAMES", "CAPTURE_WORLD", "DUMP_TARGETS",
                 "D3D12_WARP", "SHOW_TARGET", "RELAY", "LATE_WAIT", "WIDE_BACK",
                 "SKIP_PIPELINES", "CONFIG"):
        env.pop("VICTORY_" + name, None)
    env.pop("XVZ_LIVE_SHADER_MS", None)
    env.pop("XVZ_VERIFY_FRAMES", None)
    # The desktop can export RUST_LOG=warn. Keep a user's wider filter
    # while always providing the INFO records consumed by the test monitor.
    filters = env.get("VICTORY_LOG") or env.get("RUST_LOG") or "warn"
    env["VICTORY_LOG"] = filters + ",victory_gpu::shader_cache::adaptive=info,victory_gpu::capture=info"
    env.update(VICTORY_CACHE_DIR=str(STATE / "cache"), VICTORY_ADAPTER="NVIDIA",
               VICTORY_DATA_DIR=str(data), VICTORY_GAME_DIR=str(GAME))
    env["XVZ_SHADER_REFERENCE"] = str(LAB / "runtime-images/bb1d551f2381876f2fbf382a0771533662d212df75ebb46ed390122d842e7f35/victory.exe")
    return env


def run_z(args):
    STATE.mkdir(exist_ok=True)
    with (STATE / "integrated-run.lock").open("a+b") as lock:
        lock.seek(0)
        try:
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as exc:
            raise RuntimeError("已有一轮本地 Z 在预热或运行") from exc
        title = GAME / "game_scarlett_master.exe"
        for item in (RUNTIME, SELECTOR, title):
            if not item.is_file():
                raise FileNotFoundError(item)
        run = LAB / "runs" / (datetime.now().strftime("ZETTROX-%Y%m%d-%H%M%S-%f") + f"-{os.getpid()}")
        run.mkdir()
        data = STATE / "data"
        env = integrated_environment(data)
        (run / "launcher.log").write_text(f"本地 Z 主运行程序　{RUNTIME}\nXCL 离线索引　{OFFLINE}\nZ 缓存　{env['VICTORY_CACHE_DIR']}\n垂直同步　1\n实时着色器预算　{args.LiveShaderMs} 毫秒\n", encoding="utf-8")
        try:
            phase(run, "select")
            monitor(run)
            prepare(run, env, args.PrepareOnly or args.VerifyWorld)
            if args.PrepareOnly:
                phase(run, "prepared")
                return 0
            if args.VerifyWorld:
                # Keep verification inputs and saves separate from the user's gameplay.
                profile = run / "test-profile"
                if data.is_dir():
                    shutil.copytree(data, profile, ignore=shutil.ignore_patterns("temp", "crashes.log"))
                else:
                    profile.mkdir()
                frames = run / "frames"
                frames.mkdir()
                env.update(VICTORY_DATA_DIR=str(profile), XVZ_VERIFY_FRAMES="1", VICTORY_CAPTURE=str(frames),
                           VICTORY_KEYS="12:Enter,24:Enter,40:Enter,55:Enter,75:Enter,95:Enter")
            env["XVZ_LIVE_SHADER_MS"] = str(args.LiveShaderMs)
            phase(run, "starting")
            with (run / "runtime.log").open("wb") as log:
                with subprocess.Popen([RUNTIME, "run", title, "--execute"], cwd=run, env=env,
                                      stdout=log, stderr=subprocess.STDOUT) as game:
                    (run / "game-pid.txt").write_text(str(game.pid), encoding="ascii")
                    phase(run, "launch")
                    code = game.wait()
            phase(run, f"exit {code}")
            return code
        except (OSError, RuntimeError, KeyboardInterrupt) as exc:
            phase(run, f"error {exc or '用户中断'}")
            raise


def main(argv=None):
    parser = argparse.ArgumentParser(description="三合一本地 Z 主运行程序与中文测试器")
    parser.add_argument("-Mode", choices=("ZETTROX", "Z", "XCL", "Geometry"), default="ZETTROX",
                        help="旧快捷方式名称均进入本地 Z 集成版")
    parser.add_argument("-Profile", choices=("Standard",), help=argparse.SUPPRESS)
    parser.add_argument("-Hardware", action="store_true", help=argparse.SUPPRESS)
    task = parser.add_mutually_exclusive_group()
    task.add_argument("-PrepareOnly", action="store_true", help="适配完整离线索引并预热启动计算管线")
    task.add_argument("-VerifyWorld", action="store_true", help="在测试存档上验证地图并采集连续帧")
    parser.add_argument("-LiveShaderMs", type=int, default=16, help="实时着色器等待预算（毫秒）")
    args = parser.parse_args(argv)
    if not 0 <= args.LiveShaderMs <= 1000:
        parser.error("-LiveShaderMs 范围为 0 到 1000")
    return run_z(args)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, RuntimeError) as error:
        sys.exit(str(error))
