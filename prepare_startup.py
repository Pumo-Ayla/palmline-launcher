"""Prepare the recorded startup shaders before XCL creates a game window."""
import argparse
import os
from pathlib import Path
import re
import subprocess
import tempfile
import threading


def run_tool(args, log, timeout, *, env=None, cwd=None, on_line=None):
    if env is None:
        env = dict(os.environ, XCL_STARTUP_PROGRESS="1")
        env.setdefault("XCL_DXC_THREADS", "6")
        env.setdefault("XCL_PIPELINE_THREADS", "2")
    with subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                          text=True, encoding="utf-8", errors="replace", env=env, cwd=cwd,
                          creationflags=subprocess.CREATE_NO_WINDOW) as process:
        expired = threading.Event()

        def stop_at_deadline():
            if process.poll() is None:
                expired.set()
                process.kill()

        timer = threading.Timer(timeout, stop_at_deadline) if timeout else None
        if timer is not None:
            timer.start()
        lines = []
        try:
            for line in process.stdout:
                log.write(line)
                log.flush()
                lines.append(line.strip())
                if on_line:
                    on_line(line.strip())
            code = process.wait()
        finally:
            if timer is not None:
                timer.cancel()
                timer.join()
        if expired.is_set():
            raise RuntimeError(f"{Path(args[0]).name} 本阶段达到 {timeout} 秒时限，已停止")
        if code:
            raise RuntimeError(f"{Path(args[0]).name} 退出码 {code}")
        return lines


def prepare(run, runtime, timeout, timeout_log, mode="full"):
    lab = Path(__file__).resolve().parent
    with (run / "prewarm.log").open("w", encoding="utf-8", buffering=1) as log:
        log.write(f"startup mode {mode}\n")
        if mode in ("off", "ondemand"):
            log.write("startup ready\n")
            return

        def stage(name):
            log.write(f"stage {name}\n")
            tmp = run / "phase.tmp"
            tmp.write_text(name, encoding="utf-8")
            tmp.replace(run / "progress-state.txt")

        if mode == "archive":
            stage("compile")
            lines = run_tool([runtime, "shaders", lab.parent / "shaders.rpf", "--precompile", "--windows", "4"],
                             log, 0)
            result = next((re.match(r"compiled (\d+), already cached (\d+), errors (\d+),", line)
                           for line in lines if line.startswith("compiled ")), None)
            if not result or int(result[1]) + int(result[2]) == 0:
                raise RuntimeError("整包预编译未返回有效结果，请查看 prewarm.log")
            if int(result[3]):
                raise RuntimeError(f"整包扫描完成，XCL 报告 {result[3]} 项编译错误，请查看 prewarm.log")
            log.write("startup ready\n")
            return

        with tempfile.TemporaryDirectory(prefix="xcl-startup-") as temp:
            pack = Path(temp) / "startup.rpf"
            shader_keys = lab / "shader-prewarm/shader-keys.txt"
            learned_keys = lab / "shader-prewarm/slow-compute-keys.txt"
            pipeline_keys = learned_keys if learned_keys.is_file() else "-"
            if timeout_log:
                keys = set(re.findall(r"still building after \d+ ms \(pipeline key ([0-9a-f]{16})\)",
                                      timeout_log.read_text(encoding="utf-8", errors="replace")))
                keys.discard("0000000000000000")
                if not keys:
                    raise ValueError("指定的超时日志里没有计算管线标识")
                pipeline_keys = Path(temp) / "pipeline-keys.txt"
                pipeline_keys.write_text("\n".join(sorted(keys)) + "\n", encoding="ascii")

            stage("select")
            selected = run_tool([lab / "target/release/xcl-select-shaders.exe", lab.parent / "shaders.rpf",
                                 shader_keys, pipeline_keys, pack], log, timeout)
            if not any(re.match(r"[1-9]\d* selected:", line) for line in selected):
                raise RuntimeError("启动清单没有匹配到游戏包中的着色器")
            command = [runtime, "shaders", pack, "--precompile", "--windows", "4"]
            stages = [("compile", [])]
            if mode == "full":
                stages.append(("warm", ["--only", "cs", "--pipelines"]))
            for name, extra in stages:
                stage(name)
                lines = run_tool(command + extra, log, timeout)
                summary = next((re.match(r"compiled (\d+), already cached (\d+), errors (\d+),", line)
                                for line in lines if line.startswith("compiled ")), None)
                if not summary or int(summary[3]) or int(summary[1]) + int(summary[2]) == 0:
                    raise RuntimeError(f"{'着色器预编译' if name == 'compile' else '计算管线预热'}未成功，请查看预热日志")
                if name == "warm":
                    gpu = next((re.fullmatch(r"pipelines made on the GPU (\d+), refused (\d+)", line)
                                for line in lines if line.startswith("pipelines made on")), None)
                    disk = next((re.fullmatch(r"driver cache: (\d+) reused, (\d+) saved", line)
                                 for line in lines if line.startswith("driver cache:")), None)
                    if not gpu or int(gpu[2]) or int(gpu[1]) == 0:
                        raise RuntimeError("显卡未成功创建全部选中的计算管线")
                    if not disk or int(disk[1]) + int(disk[2]) != int(gpu[1]):
                        raise RuntimeError("驱动缓存未全部验证或保存，请核对预热日志与 XCL 版本")
            log.write("startup ready\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("run", type=Path)
    parser.add_argument("runtime", type=Path)
    parser.add_argument("timeout", type=int)
    parser.add_argument("--timeout-log", type=Path)
    parser.add_argument("--mode", choices=("full", "dxil", "off", "ondemand", "archive"), default="full")
    args = parser.parse_args()
    try:
        prepare(args.run, args.runtime, args.timeout, args.timeout_log, args.mode)
    except (OSError, RuntimeError, ValueError) as exc:
        with (args.run / "prewarm.log").open("a", encoding="utf-8") as log:
            log.write(f"启动准备失败　{exc}\n")
        (args.run / "progress-state.txt").write_text(f"error {exc}", encoding="utf-8")
        raise SystemExit(1)
