"""Serve one desktop selection. EOF after warmup never starts the title."""
import argparse
import base64
import filecmp
from contextlib import contextmanager, ExitStack
from datetime import datetime, timezone
import json
import shutil
import tempfile
import xml.etree.ElementTree as ET
import msvcrt
import os
from pathlib import Path
import re
import subprocess
import sys
import threading
import time

LAB = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
GAME = LAB.parent.parent / "Game"
COMMUNITY = LAB.parent / "Community"
GAME_ASSETS = Path(os.environ.get("PALMLINE_GAME_ASSETS", GAME))
GAME_EXE = Path(os.environ.get("PALMLINE_GAME_EXE", GAME_ASSETS / "game_scarlett_master.exe"))
ENGINES = {p.attrib["id"]: p.attrib for p in ET.parse(COMMUNITY / "projects.xml").getroot()}
PROMPT_PROJECTS = {"cgbj"}


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


def phase(run, value):
    temporary = run / "state.tmp"
    temporary.write_text(value, encoding="utf-8")
    temporary.replace(run / "progress-state.txt")


def environment(state, project=None):
    if project is None:
        project = ENGINES["cgbj"]
    env = {k: v for k, v in os.environ.items() if not k.startswith(("VICTORY_", "XCL_", "BELOBUG_", "XVZ_", "LAUNCHER_Z_"))}
    env.pop("PALMLINE_PAD_V1", None)
    env.pop("PALMLINE_GTA6_PRESET", None)
    env.pop("PALMLINE_CUSTOM_PAD", None)
    env.pop("PALMLINE_KEYBOARD_WINDOW", None)
    env.pop("PALMLINE_GAME_WINDOW", None)
    env.pop("PALMLINE_PRELOAD_PROMPTS", None)
    env.pop("PALMLINE_PROMPT_MODULE", None)
    env.pop("PALMLINE_DLSS_SR", None)
    env.pop("PALMLINE_DLSS_QUALITY", None)
    env.pop("PALMLINE_DLSSG", None)
    libraries = LAB.parent / "Runtime" / "Native"
    if libraries.is_dir():
        env["PATH"] = str(libraries) + os.pathsep + env.get("PATH", "")
    if project["id"] == "xcl":
        env.update(XCL_DATA_ROOT=str(state / "data"), XCL_DXIL_CACHE=str(state / "cache" / "shaders"),
                   XCL_DXC_DIR=str(libraries), XCL_PSO_PRELOAD="1", XCL_RELAY_SESSION="1")
        return env
    if libraries.is_dir():
        env["VICTORY_DXIL"] = str(libraries / "dxil.dll")
    env.update(VICTORY_CACHE_DIR=str(state / "cache"), VICTORY_DATA_DIR=str(state / "data"),
               VICTORY_GAME_DIR=str(GAME_ASSETS), VICTORY_PIPELINE_BOOK="1", VICTORY_AMBIENT="0",
               VICTORY_LOG="warn,victory_gpu::queue=info,victory_gpu::host=info,title=info")
    if project["id"] == "victory-nvidia":
        del env["VICTORY_AMBIENT"]
        streamline = LAB.parent / "Runtime" / "DLSS" / "FrameGeneration"
        for name in ("VICTORY_STREAMLINE_DIR", "VICTORY_FIDELITYFX_DIR"):
            if os.environ.get(name):
                env[name] = os.environ[name]
        if "VICTORY_STREAMLINE_DIR" not in env and (streamline / "sl.interposer.dll").is_file():
            env["VICTORY_STREAMLINE_DIR"] = str(streamline)
        return env
    # Use the pinned Windows profile for both executable variants. Keep the
    # native process as our child so the launcher can track its actual window.
    script = COMMUNITY / project["id"] / project["commit"] / "Play-VIctory.cmd"
    for key, value in re.findall(r'^if not defined (VICTORY_\w+) set "\1=([^"]+)"$',
                                 script.read_text(encoding="utf-8"), re.MULTILINE):
        env.setdefault(key, value)
    return env


class ShaderError(RuntimeError):
    pass


_ANSI_STYLE = re.compile(rb"\x1b\[[0-9;:]*m")


def log_text(raw):
    # The same CGBJ binary can colour its tracing fields even through a pipe.
    return _ANSI_STYLE.sub(b"", raw).decode("utf-8", "replace").rstrip("\r\n")


def host_frame(raw):
    line = log_text(raw)
    return "victory_gpu::host:" in line and " fps, " in line and " ms/frame (" in line


_ACHIEVEMENT_URL = re.compile(
    r'https://achievements\.xboxlive\.com/users/[^/"\s]+/achievements'
    r'(?P<path>(?:/[^?"\s]*)?)(?:\?[^"\s]*)?(?=["\s]|$)', re.I)
_ACHIEVEMENT_ID = re.compile(
    r'\bachievement_?id\b["\']?\s*[:=]\s*["\']?([\w.-]+)', re.I)
_UNLOCK_EVENT = re.compile(
    r'(?:^|:\s+)(?:achievement[ _]?unlocked)\b|'
    r'\b(?:event|eventName)["\']?\s*[:=]\s*["\']achievement[ _]?unlocked["\']', re.I)


_ACHIEVEMENT_LINE = re.compile(r"^(\S+) \[[A-Z0-9_-]+ achievement\] (.+)$")


def achievement_record(raw, engine="cgbj"):
    if b"achievement" not in raw.lower():
        return None
    line = log_text(raw).strip()
    stamp = re.match(r'\d{4}-\d\d-\d\dT[\d:.]+(?:Z|[+-]\d\d:\d\d)\b', line)
    when = stamp[0] if stamp else datetime.now(timezone.utc).isoformat(timespec="milliseconds")
    _, marker, payload = line.partition("victory_hle::modules::xuser: local achievement update ")
    if marker:
        try:
            update = json.loads(payload)
        except (ValueError, RecursionError):
            return None
        if not isinstance(update, dict):
            return None
        key, percent = update.get("achievementId"), update.get("percentComplete")
        if not isinstance(key, str) or not 0 < len(key) <= 256 or type(percent) is not int or not 0 <= percent <= 100:
            return None
        fields = json.dumps({"achievementId": key, "percentComplete": percent}, ensure_ascii=True)
        return f"{when} [{engine.upper()} achievement] local-progress {fields}"
    url = _ACHIEVEMENT_URL.search(line)
    offline = "token and signature: offline" in line
    achievement_id = None
    if url and (offline or re.search(r'\b(?:GET|POST)\s+https://', line, re.I)):
        parts = url["path"].strip("/").split("/")
        if parts[-1].lower() == "update":
            event = "progress-update-request"
        else:
            event = "query"
            if len(parts) == 2 and re.fullmatch(r"[\w.-]+", parts[1]):
                achievement_id = parts[1]
    elif _UNLOCK_EVENT.search(line) and not re.search(
            r'\b(?:failed|failure|error|false|denied|pending|attempt|requested)\b', line, re.I):
        event = "unlocked"
    else:
        return None
    field = _ACHIEVEMENT_ID.search(line)
    if field and field[1].lower() not in {"null", "none"}:
        achievement_id = field[1]
    result = f"{when} [{engine.upper()} achievement] {event}"
    if achievement_id is not None:
        result += f" achievementId={achievement_id}"
    if offline:
        result += " result=offline"
    return result


def read_achievements(run):
    """Return this game's observed progress, including what older logs could not identify."""
    items = {}
    queries = []
    unknown = 0
    log = run / "achievements.log" if run is not None else None
    if log is not None and log.is_file():
        with log.open(encoding="utf-8", errors="replace") as stream:
            for line in stream:
                record = _ACHIEVEMENT_LINE.fullmatch(line.rstrip("\r\n"))
                if not record:
                    continue
                when, message = record.groups()
                kind, _, fields = message.partition(" ")
                percent = None
                if kind == "query":
                    field = _ACHIEVEMENT_ID.search(fields)
                    queries.append({"observedAt": when, "achievementId": field[1] if field else None})
                    continue
                if kind == "local-progress":
                    try:
                        entry = json.loads(fields)
                    except (ValueError, RecursionError):
                        continue
                    if not isinstance(entry, dict):
                        continue
                    key, percent = entry.get("achievementId"), entry.get("percentComplete")
                    if not isinstance(key, str) or not key or type(percent) is not int or not 0 <= percent <= 100:
                        continue
                elif kind in ("progress-update-request", "unlocked"):
                    field = _ACHIEVEMENT_ID.search(fields)
                    if not field:
                        unknown += kind == "progress-update-request"
                        continue
                    key = field[1]
                else:
                    continue
                item = items.setdefault(key, {"achievementId": key, "percentComplete": None, "state": "requested"})
                if percent is not None and (item["percentComplete"] is None or item["percentComplete"] < percent):
                    item["percentComplete"] = percent
                    if item["state"] != "unlock-event":
                        item["state"] = "local-complete" if percent == 100 else "in-progress"
                if kind == "unlocked":
                    item["percentComplete"] = 100
                    item["state"] = "unlock-event"
                item["lastObservedAt"] = when
    return {"run": run.name if run else None, "achievements": list(items.values()), "queries": queries,
            "unidentifiedUpdateRequests": unknown}


_TITLE_TRACE = re.compile(r"^(?:\d{4}-\d\d-\d\dT\S+\s+)?(?:TRACE|DEBUG|INFO|WARN|ERROR)\s+title:")


def native_source(line):
    # CGBJ's OutputDebugStringA/W traces guest messages with the exact target "title".
    return "GAME" if _TITLE_TRACE.match(line.lstrip()) else "EMULATOR"


def forward_runtime_logs(lines, run, engine="cgbj"):
    if not lines:
        return
    try:
        output = (log_text(raw) for raw in lines)
        sys.stdout.write("".join(f"{native_source(line)}\t{line}\n" for line in output))
        sys.stdout.flush()
    except OSError:
        pass  # The native runtime.log remains active when the launcher closes.
    for raw in lines:
        entry = achievement_record(raw, engine)
        if entry:
            send("LOG", entry)
            try:
                with (run / "achievements.log").open("a", encoding="utf-8") as log:
                    print(entry, file=log)
            except OSError as exc:
                send("LOG", f"Achievement log: {exc}")


class ShaderStall(ShaderError):
    pass


def shader_output(child, idle_seconds):
    # The compiler only prints every thousand shaders. Disk I/O also counts
    # as activity while it scans an archive or writes a new cache.
    import ctypes
    from ctypes import wintypes
    from queue import Empty, Queue
    counters = (ctypes.c_ulonglong * 6)()
    io_counters = ctypes.windll.kernel32.GetProcessIoCounters
    def transferred():
        if io_counters(wintypes.HANDLE(int(child._handle)), ctypes.byref(counters)):
            return counters[3], counters[4]
        return None

    lines = Queue()
    def read():
        try:
            for raw in child.stdout:
                lines.put(raw)
        finally:
            lines.put(None)
    reader = threading.Thread(target=read, daemon=True)
    reader.start()
    last_io, last_progress = None, time.monotonic()
    try:
        while True:
            try:
                raw = lines.get(timeout=1)
            except Empty:
                activity = transferred()
                if activity != last_io:
                    last_io, last_progress = activity, time.monotonic()
                elif time.monotonic() - last_progress >= idle_seconds and child.poll() is None:
                    raise ShaderStall("着色器预热长时间无进展")
                continue
            if raw is None:
                break
            last_progress = time.monotonic()
            yield raw
    finally:
        if child.poll() is None and reader.is_alive():
            child.kill()
            child.wait()
        reader.join()


def native_lines(runtime, args, env, run, log, idle_seconds=None):
    command = [runtime, *args]
    print(subprocess.list2cmdline(command), file=log, flush=True)
    send("LOG", "Running: " + subprocess.list2cmdline(command))
    with subprocess.Popen(command, cwd=run, env=env, stdin=subprocess.DEVNULL,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                          creationflags=subprocess.CREATE_NO_WINDOW) as child:
        for raw in shader_output(child, idle_seconds) if idle_seconds else child.stdout:
            line = log_text(raw)
            print(line, file=log)
            send("EMULATOR", line)
            if line:
                yield line
        code = child.wait()
        if code:
            raise ShaderError(f"{runtime.name} {args[0]} (0x{code & 0xffffffff:08x}) · prewarm.log")


def clear_shader_cache(state):
    profiles = (LAB / "profiles").resolve()
    folders = ["shaders", "pipelines"] if state.name == "victory-nvidia" else ["shaders"]
    for folder in folders:
        path = state / "cache" / folder
        if path.resolve() != profiles / state.name / "cache" / folder or state.parent.resolve() != profiles:
            raise ShaderError("着色器缓存路径异常")
        if path.exists():
            shutil.rmtree(path)


def prepare_nvidia(runtime, run, env):
    # preload.env is the release's play::SETTINGS. Match the F1 shader switches
    # (notably early depth, part of the DXIL cache key) before the native preload.
    env = env.copy()
    defaults = dict(line.split("=", 1) for line in runtime.with_name("preload.env").read_text().splitlines())
    env.update(defaults)
    menu = Path(env["LOCALAPPDATA"]) / "victory" / "victory-settings.txt"
    if menu.is_file():
        for line in menu.read_text(encoding="utf-8-sig").splitlines():
            key, sep, value = line.partition("=")
            key, value = key.strip(), value.strip()
            if sep and (key in defaults or key in {"VICTORY_BVH_BUILDER", "VICTORY_WATER_LIGHT"}):
                if value:
                    env[key] = value
                else:
                    env.pop(key, None)

    assets = GAME_ASSETS.resolve()
    shared = assets == Path(assets.anchor) or LAB.resolve().is_relative_to(assets)
    paths = [assets]
    if shared:
        paths = [*assets.glob("*.rpf"), *assets.glob("*.dll"),
                 *(p for p in (assets / "pack", assets / "data") if p.is_dir())]
    if shared or not GAME_EXE.resolve().is_relative_to(assets):
        paths.append(GAME_EXE)
    shaders = library = None
    send("STAGE", "正在扫描游戏目录中的着色器")
    with (run / "prewarm.log").open("w", encoding="utf-8", buffering=1) as log:
        for line in native_lines(runtime, ["preload", *paths], env, run, log):
            count = re.match(r"(\d+) shaders in the cache, (?:(\d+) do not translate, )?", line)
            if count:
                send("COUNT", count[1])
                if count[2] is not None:
                    shaders = int(count[1]), int(count[2])
            elif " pipelines known," in line:
                send("STAGE", "正在预热已记录的渲染管线")
            elif line.startswith("into the pipeline library:"):
                send("STAGE", "正在写入原生管线库")
            else:
                progress = re.match(r"(\d+) of (\d+)( in the pipeline library| in the library|,)", line)
                if progress:
                    if int(progress[2]):
                        send("PROGRESS", f"{progress[1]}/{progress[2]}")
                    if progress[3] == " in the pipeline library":
                        library = int(progress[1]), int(progress[2])
    if not shaders or not shaders[0] or library is None:
        raise ShaderError("VIctory-NVIDIA · 原生预载未完成，请查看 prewarm.log")
    return f"VIctory-NVIDIA · shaders {shaders[0]} (+{shaders[1]} skipped) · PSO {library[0]}/{library[1]}"


def prepare(runtime, run, env, full=False):
    runtime = Path(runtime)
    archive = GAME_ASSETS / "shaders.rpf"
    if runtime.name == "xcl.exe":
        loose = GAME_ASSETS / "shaders"
        sources = ([archive] if archive.is_file() else []) + ([loose] if loose.is_dir() else [])
        if not sources:
            return "XCL · " + "首次运行将记录渲染管线"
        cached = failed = refused = 0
        options = ["--precompile", "--pipelines"]
        used = Path(env["XCL_DXIL_CACHE"]) / "used-pipelines.txt"
        if not full and used.is_file() and used.stat().st_size:
            options.append("--used-only")
        with (run / "prewarm.log").open("w", encoding="utf-8", buffering=1) as log:
            for source in sources:
                send("STAGE", "正在准备着色器")
                result = None
                for line in native_lines(runtime, ["shaders", source, *options], env, run, log):
                    match = re.search(r"compiled (\d+), already cached (\d+), errors (\d+)", line)
                    if match:
                        result = tuple(map(int, match.groups()))
                    pipeline = re.search(r"pipelines made on the GPU (\d+), refused (\d+)", line)
                    if pipeline:
                        refused += int(pipeline[2])
                if result is None:
                    raise ShaderError("XCL · prewarm.log")
                cached += sum(result[:2])
                failed += result[2]
                send("COUNT", cached)
        if failed and not cached:
            raise ShaderError("XCL · prewarm.log")
        parts = ["XCL", "着色器缓存预热完成" if cached else "首次运行将记录渲染管线"]
        if failed:
            parts.append(f"{failed} 个着色器跳过")
        if refused:
            parts.append(f"{refused} 条管线未通过")
        return " · ".join(parts)
    pipelines = Path(env["VICTORY_CACHE_DIR"]) / "pipelines"
    # warm opens its ledgers for append even when a fresh install has no records.
    pipelines.mkdir(parents=True, exist_ok=True)
    with (run / "prewarm.log").open("w", encoding="utf-8", buffering=1) as log:
        paths = [archive]
        if full:
            assets = GAME_ASSETS.resolve()
            shared_folder = assets == Path(assets.anchor) or LAB.resolve().is_relative_to(assets)
            if shared_folder:
                # Game imports can share a folder with the launcher. Scan only title assets there.
                paths = [*assets.glob("*.rpf"), *(d for d in (assets / "pack", assets / "data") if d.is_dir())]
            else:
                paths = [assets]
            if GAME_EXE.is_file() and (shared_folder or not GAME_EXE.resolve().is_relative_to(assets)):
                paths.append(GAME_EXE)
        send("STAGE", "正在扫描游戏目录中的着色器" if full else "正在预编译完整着色器归档")
        translated = skipped = 0
        for jobs in ([], ["--jobs", "1"]):
            try:
                for line in native_lines(runtime, ["shaders", *paths, *jobs], env, run, log, idle_seconds=180):
                    count = re.fullmatch(r"(\d+) shaders in the cache, (?:(\d+) do not translate, )?[\d.]+ s", line)
                    if count:
                        send("COUNT", int(count[1]))
                        if count[2] is not None:
                            translated, skipped = int(count[1]), int(count[2])
                break
            except ShaderStall:
                if jobs:
                    raise
                translated = skipped = 0
                send("STAGE", "预热停滞，正在以单线程重试")
                print("Shader compiler stalled; retrying with --jobs 1", file=log)
        if not translated:
            raise ShaderError("未收到有效的着色器缓存统计，请查看 prewarm.log")

        send("STAGE", "正在读取管线记录")
        known = made = None
        bad = failed = 0
        for line in native_lines(runtime, ["warm", "--used"], env, run, log):
            inventory = re.match(r"(\d+) pipelines known, \d+ made before .*?, (\d+) bad; (\d+) to make", line)
            if inventory:
                known, bad, pending = map(int, inventory.groups())
                if pending:
                    send("STAGE", "正在预热已记录的渲染管线")
                    send("PROGRESS", f"0/{pending}")
                else:
                    send("STAGE", "已无待预热管线" if known else "首次运行将记录渲染管线")
            progress = re.match(r"(\d+) of (\d+),", line)
            if progress:
                send("PROGRESS", f"{progress[1]}/{progress[2]}")
            summary = re.match(r"(\d+) made in .*? \((\d+) have no host pipeline, (\d+) ended their process\)", line)
            if summary:
                made, refused, crashed = map(int, summary.groups())
                failed = refused + crashed
        if known is None or made is None or made + crashed != pending:
            raise ShaderError("管线预热未完成，请查看 prewarm.log")
        if pending:
            send("PROGRESS", f"{made + crashed}/{pending}")
        parts = ["着色器缓存预热完成"]
        if skipped:
            parts.append(f"{skipped} 个着色器跳过")
        if bad + failed:
            parts.append(f"{bad + failed} 条管线未通过")
        if not pending:
            parts.append("已无待预热管线" if known else "首次运行将记录渲染管线")
        return " · ".join(parts)


def request_window_close(pid):
    import ctypes
    from ctypes import wintypes

    user = ctypes.windll.user32
    callback = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user.GetWindowThreadProcessId.argtypes = (wintypes.HWND, ctypes.POINTER(wintypes.DWORD))
    user.PostMessageW.argtypes = (wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)
    user.EnumWindows.argtypes = (callback, wintypes.LPARAM)

    @callback
    def close(hwnd, unused):
        owner = wintypes.DWORD()
        user.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid:
            user.PostMessageW(hwnd, 0x0010, 0, 0)  # WM_CLOSE
        return True

    user.EnumWindows(close, 0)


def title_files():
    files = [p for p in GAME_ASSETS.iterdir() if p.is_file() and p.name.lower() != GAME_EXE.name.lower()]
    for directory in ("pack", "data"):
        files.extend(p for p in (GAME_ASSETS / directory).rglob("*") if p.is_file())
    return files


def populate_title(view, files, prompts=None, expected_name=None):
    image = view / (expected_name or GAME_EXE.name)
    for source in files:
        dest = view / source.relative_to(GAME_ASSETS)
        if dest == image:
            continue
        dest.parent.mkdir(parents=True, exist_ok=True)
        if prompts and source == GAME_ASSETS / "data.rpf":
            module = os.environ.get("PALMLINE_PROMPT_MODULE")
            if module:
                import importlib.util
                spec = importlib.util.spec_from_file_location("launcher_key_prompts", module)
                helper = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(helper)
            else:
                import key_prompts as helper
            text_count, glyph_count = helper.create_copy(source, dest, prompts)
            if not glyph_count:
                raise RuntimeError("No compatible HUD button glyphs found in data.rpf.")
            send("LOG", f"Keyboard prompts: {glyph_count} HUD button glyphs and {text_count} text entries replaced in the session copy.")
        else:
            os.link(source, dest)
    try:
        os.link(GAME_EXE, image)
    except OSError:
        shutil.copyfile(GAME_EXE, image)
    return image


@contextmanager
def title_image(expected_name=None, prefer_asset_image=False):
    name = expected_name or GAME_EXE.name
    if GAME_EXE.parent == GAME_ASSETS and GAME_EXE.name.lower() == name.lower():
        yield GAME_EXE
        return
    # Native F1 restarts refer to this path after the launcher session closes.
    # Prefer the permanent, byte-identical title beside the assets when available.
    beside = GAME_ASSETS / name
    if prefer_asset_image and beside.is_file() and filecmp.cmp(beside, GAME_EXE, shallow=False):
        yield beside
        return
    files = title_files()
    # The original installation may already be a complete hard-linked view.
    original = GAME / name
    if original.is_file() and original.samefile(GAME_EXE) and all(
            (GAME / p.relative_to(GAME_ASSETS)).is_file() and
            (GAME / p.relative_to(GAME_ASSETS)).samefile(p) for p in files):
        yield original
        return
    with tempfile.TemporaryDirectory(prefix="pal-title-", dir=GAME_ASSETS.parent) as folder:
        view = Path(folder)
        yield populate_title(view, files, expected_name=name)


def history_folder():
    instance = os.environ.get("PALMLINE_INSTANCE_ID", "")
    if re.fullmatch(r"[0-9a-f]{32}", instance):
        return LAB / "desktop" / "play-history" / instance


def write_playtime(folder, name, started, seconds):
    folder.mkdir(parents=True, exist_ok=True)
    target = folder / (name + ".time")
    pending = target.with_suffix(f".{os.getpid()}.tmp")
    pending.write_text(f"{int(started)}\n{int(seconds)}\n", encoding="ascii")
    pending.replace(target)


@contextmanager
def track_playtime(run, launched):
    folder = history_folder()
    clock = None
    next_update = 0

    def update(final=False):
        nonlocal clock, next_update
        if folder is None or (final and clock is None):
            return
        now = time.monotonic()
        if clock is None:
            clock = now
        if final or now >= next_update:
            next_update = now + 10
            try:
                write_playtime(folder, run.name, launched, now - clock)
            except OSError as exc:
                send("LOG", f"Play history: {exc}")
    try:
        yield update
    finally:
        update(final=True)


def import_legacy_history():
    folder = history_folder()
    if not folder or not (folder / "import-legacy").is_file():
        return
    # The old launcher saved one installation. Only its completed, identifiable
    # CGBJ sessions with a frame timestamp can be attributed to that instance.
    for log in (LAB / "runs").glob("Desktop-*-cgbj/runtime.log"):
        try:
            if (folder / (log.parent.name + ".time")).exists():
                continue
            with log.open("rb") as stream:
                command = stream.readline(8192).decode("utf-8", "replace")
                match = re.search(r' run (?:"([^"]+)"|(\S+)) --console ', command)
                if not match:
                    continue
                image = Path(match.group(1) or match.group(2))
                if image.name.lower() != GAME_EXE.name.lower():
                    continue
                if image.parent != GAME_ASSETS and not (
                        image.parent.name.startswith("pal-title-") and image.parent.parent == GAME_ASSETS.parent):
                    continue
                # Read a bounded prefix. Leave ambiguous or incomplete runs out.
                for raw in stream.read(1024 * 1024).splitlines():
                    if not host_frame(raw):
                        continue
                    stamp = log_text(raw).split(maxsplit=1)[0]
                    started = datetime.fromisoformat(stamp.replace("Z", "+00:00")).timestamp()
                    seconds = max(0, log.stat().st_mtime - started)
                    write_playtime(folder, log.parent.name, started, seconds)
                    break
        except (OSError, ValueError):
            continue
    (folder / "import-legacy").unlink(missing_ok=True)


def configure_dlss(render, env, prefix="PALMLINE_"):
    if render.get("dlssSuperResolution", "").lower() != "true":
        return
    env[prefix+"DLSS_SR"] = "1"
    quality = render.get("dlssQuality")
    env[prefix+"DLSS_QUALITY"] = quality if quality in ("balanced", "performance") else "quality"
    requested = render.get("dlssFrameMultiplier", "")
    if requested not in ("2", "3", "4", "5", "6"):
        return
    probe = LAB.parent / "Runtime" / "DLSS" / "DlssCapabilities.exe"
    try:
        maximum = subprocess.run([probe, "--frame-generation"], cwd=probe.parent, env={**env, "PALMLINE_DLSS_SR": "1"},
                                 stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                 creationflags=subprocess.CREATE_NO_WINDOW, timeout=30).returncode
    except (OSError, subprocess.TimeoutExpired) as exc:
        send("LOG", f"Frame generation capability check failed: {exc}")
        return
    if 2 <= maximum <= 6:
        env[prefix+"DLSSG"] = str(min(int(requested), maximum))


def play(exe, env, run, game, console, engine):
    if engine == "victory-nvidia":
        env["VICTORY_GAME_DIR"] = str(game.parent)
        env["VICTORY_LOG"] += ",victory_hle::modules::xuser=info"
        return run_game([exe, "play", game], env, run, engine, "VICTORY_")
    command = [exe, "run", game]
    if engine != "xcl":
        command += ["--console", console]
        command.append("--execute")
        env["VICTORY_GAME_DIR"] = str(game.parent)
        env["VICTORY_LOG"] += ",victory_hle::modules::xuser=info"
    # A session can remain ready for hours. Read the choice at PLAY, after warm-up.
    settings = LAB / "desktop" / "display-settings.xml"
    if engine != "cgbj":
        settings = LAB / "profiles" / engine / "render.xml"
    prefix = "XCL_" if engine == "xcl" else "PALMLINE_"
    render = ET.Element("display")
    try:
        render = ET.parse(settings).getroot()
        if engine == "cgbj":
            configure_dlss(render, env, prefix)
    except FileNotFoundError:
        pass
    except (OSError, ET.ParseError) as exc:
        send("LOG", f"{engine} render profile: {exc}")
    return run_game(command, env, run, engine, prefix)


def run_game(command, env, run, engine, prefix):
    runtime_log = run / "runtime.log"
    with runtime_log.open("wb") as log:
        log.write((subprocess.list2cmdline(command) + "\n").encode("utf-8"))
        log.write(f"VICTORY_AMBIENT={env.get('VICTORY_AMBIENT', 'unset')}\n".encode("utf-8"))
        for setting in ("DLSS_SR", "DLSS_QUALITY", "DLSSG"):
            log.write(f"{prefix}{setting}={env.get(prefix+setting, 'off')}\n".encode())
        log.flush()
        p = subprocess.Popen(command, cwd=run,
                             env=env, stdout=log, stderr=subprocess.STDOUT,
                             creationflags=subprocess.CREATE_NO_WINDOW)
        launched = time.time()
        if env.get("PALMLINE_GAME_WINDOW") == "1":
            import ctypes
            ctypes.windll.user32.AllowSetForegroundWindow(p.pid)
        (run / "game-pid.txt").write_text(str(p.pid), encoding="ascii")
        phase(run, "render-init")
        send("RENDER_INIT", p.pid)
        stop_request = run / "exit-request"
        deadline = None
        presented = False
        device_ready = False
        partial = b""
        window_pid = p.pid
        with runtime_log.open("rb") as tail, track_playtime(run, launched) as update_playtime:
            while True:
                code = p.poll()
                lines = (partial + tail.read(262144)).split(b"\n")
                partial = lines.pop()
                at_end = tail.tell() >= runtime_log.stat().st_size
                if code is not None and at_end and partial:
                    lines.append(partial)
                    partial = b""
                if engine == "xcl":
                    for raw in lines:
                        host = re.fullmatch(r"XCL host pid=(\d+)", log_text(raw))
                        if host:
                            window_pid = int(host[1])
                            (run / "game-pid.txt").write_text(str(window_pid), encoding="ascii")
                if not presented and not device_ready and any(
                        "victory_gpu::host: host D3D12 device ready" in log_text(line) for line in lines):
                    device_ready = True
                    send("DEVICE_READY", p.pid)
                # The host frame meter runs after DXGI Present. Queue present events
                # can also be emitted by the title before the window has any frames.
                if not presented and any((b"present: first game frame shown (" in line if engine == "xcl"
                                          else host_frame(line)) for line in lines):
                    presented = True
                    phase(run, "rendering")
                    update_playtime()
                    send("PLAYING", window_pid)
                elif presented:
                    update_playtime()
                forward_runtime_logs(lines, run, engine)
                if code is not None:
                    if not at_end:
                        continue
                    if stop_request.exists():
                        return 0
                    if not presented:
                        raise RuntimeError("首帧提交前游戏已退出，请查看 runtime.log")
                    return code
                if deadline is None and stop_request.exists():
                    request_window_close(window_pid)
                    deadline = time.monotonic() + 8
                elif deadline is not None and time.monotonic() >= deadline:
                    if engine == "xcl" and window_pid != p.pid:
                        # XCL's launcher waits on its own host child. Close the complete
                        # session if the game window didn't acknowledge WM_CLOSE.
                        subprocess.run(["taskkill", "/PID", str(p.pid), "/T", "/F"],
                                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                       creationflags=subprocess.CREATE_NO_WINDOW)
                    if p.poll() is None:
                        p.terminate()
                    p.wait()
                    continue  # Drain the final native output before returning.
                if tail.tell() >= runtime_log.stat().st_size:
                    time.sleep(.25)


def selected_save_data(library):
    marker = library / "selected.txt"
    slot = marker.read_text(encoding="utf-8").strip() if marker.exists() else "default"
    if slot != "default" and not re.fullmatch(r"[0-9a-f]{32}", slot):
        raise RuntimeError("Invalid local save selection.")
    data = library / slot
    if not data.is_dir():
        raise RuntimeError("The selected local save folder is missing.")
    return data


def decode_prompts(value):
    if len(value) > 16384:
        raise ValueError("Keyboard prompt profile is too large.")
    prompts = {}
    for line in base64.b64decode(value, validate=True).splitlines():
        match = re.fullmatch(rb"(INPUT_[A-Z0-9_]+)=([A-Za-z0-9 ]{1,32})", line)
        if not match:
            raise ValueError("Invalid keyboard prompt binding.")
        prompts[match[1]] = b"[" + match[2] + b"]"
    if value and not prompts:
        raise ValueError("Empty keyboard prompt profile.")
    return prompts


def input_for_play(command, runtime, key="cgbj"):
    fields = command.split("\t")
    mode = fields[1] if len(fields) > 1 else ""
    if fields[0] != "PLAY" or mode not in ("", "GTA6_PRESET", "KEYBOARD"):
        return None
    options = dict(field.split("=", 1) for field in fields[2:] if mode and "=" in field)
    prompts = decode_prompts(options.get("PROMPTS", "")) if key in PROMPT_PROJECTS else {}
    if key != "cgbj":
        return runtime, {}, prompts
    mapped = runtime.parent / "mapped-vi" / runtime.name
    if not mapped.is_file():
        raise RuntimeError("未找到该项目的游戏窗口运行组件。")
    settings = {"PALMLINE_GAME_WINDOW": "1"}
    if mode:
        settings["PALMLINE_KEYBOARD_WINDOW"] = "1"
    if mode == "GTA6_PRESET":
        settings["PALMLINE_GTA6_PRESET"] = "1"
    pad = options.get("PAD", "")
    if mode == "KEYBOARD" and re.fullmatch(r"(?:[0-9A-F]{2},){15}[0-9A-F]{2}", pad):
        settings["PALMLINE_CUSTOM_PAD"] = pad
    return mapped, settings, prompts


def session(key, full=False, clear=False):
    with ExitStack() as lifetime:
        return serve_session(key, lifetime, full, clear)


def runtime_path(project):
    name = "xcl.exe" if project["id"] == "xcl" else "victory.exe"
    return COMMUNITY / project["id"] / "runtime" / project["commit"] / name


def gta_prompt_title(assets):
    try:
        manifest = ET.parse(assets / "MicrosoftGame.Config").getroot()
        return any(e.tag.split("}")[-1] == "TitleId" and (e.text or "").strip().upper() == "7952987A"
                   for e in manifest)
    except (OSError, ET.ParseError):
        return False


def prepare_session(runtime, run, env, prompt_view, full=False, key="cgbj"):
    if key == "victory-nvidia":
        if not gta_prompt_title(GAME_ASSETS):
            raise ValueError("VIctory-NVIDIA 仅支持 GTA6。")
        return prepare_nvidia(runtime, run, env)
    if prompt_view is not None:
        if key not in PROMPT_PROJECTS or not gta_prompt_title(GAME_ASSETS):
            raise ValueError("此游戏或项目不支持键鼠提示替换。")
        send("STAGE", "正在准备键位提示")
        if not (GAME_ASSETS / "data.rpf").is_file():
            raise FileNotFoundError(GAME_ASSETS / "data.rpf")
        prompts = decode_prompts(os.environ["PALMLINE_PRELOAD_PROMPTS"])
        populate_title(prompt_view, title_files(), prompts, GAME_EXE.name)
    return prepare(runtime, run, env, full)


def serve_session(key, lifetime, full=False, clear=False):
    project = ENGINES[key]
    runtime = runtime_path(project)
    state = LAB / "profiles" / project["profile"]
    run = LAB / "runs" / (datetime.now().strftime("Desktop-%Y%m%d-%H%M%S-%f") + "-" + key)
    run.mkdir(parents=True)
    send("RUN", run)
    try:
        import_legacy_history()
    except OSError as exc:
        send("LOG", f"Play history: {exc}")
    try:
        state.mkdir(parents=True, exist_ok=True)
        lock = state / "integrated-run.lock"
        if clear:
            with exclusive(lock):
                clear_shader_cache(state)
            send("LOG", "着色器缓存已清理")
            phase(run, "cache-cleared")
            return 0
        required = [runtime, GAME_EXE]
        if key != "xcl":
            required.append(GAME_ASSETS / "shaders.rpf")
        for path in required:
            if not path.is_file():
                raise FileNotFoundError(path)
        payload = os.environ.get("PALMLINE_PRELOAD_PROMPTS", "")
        prompts = decode_prompts(payload) if payload and key in PROMPT_PROJECTS and gta_prompt_title(GAME_ASSETS) else {}
        view = None
        if prompts:
            # Keep the session view on the assets' volume so unchanged archives can be linked.
            parent = run if run.stat().st_dev == GAME_ASSETS.stat().st_dev else GAME_ASSETS.parent
            view = Path(lifetime.enter_context(tempfile.TemporaryDirectory(prefix="pal-title-", dir=parent)))
        with exclusive(lock), tempfile.TemporaryDirectory(dir=run) as temp:
            phase(run, "prepare")
            send("PRELOADING", key)
            send("LOG", f'{project["repo"]} @ {project["commit"][:12]}')
            work = Path(temp)
            cancel_event = os.environ.get("PALMLINE_WARMUP_EVENT")
            if cancel_event:
                import warmup_job
                command = [sys.executable, "-X", "utf8", "-B", "-u", __file__, key, "--warm-worker", work]
                if full:
                    command.append("--full-warmup")
                if view is not None:
                    command.extend(["--prompt-view", view])
                code = warmup_job.run(command, cancel_event)
                if code is None:
                    phase(run, "cancelled")
                    return 0
                result = work / "result.txt"
                if code:
                    error = ShaderError if code == 2 and result.is_file() else RuntimeError
                    raise error(result.read_text(encoding="utf-8") if result.is_file()
                                       else f"预热进程退出码 {code}，请查看 prewarm.log")
                summary = result.read_text(encoding="utf-8")
            else:
                summary = prepare_session(runtime, run, environment(state, project), view, full, key)
        phase(run, "prepared")
        send("READY", summary)
        selection = input_for_play(sys.stdin.readline().rstrip("\r\n"), runtime, key)
        if selection is None:
            return 0
        exe, input_env, requested_prompts = selection
        if requested_prompts != prompts:
            raise RuntimeError("Keyboard bindings changed after preload. Prepare the selected profile again.")
        with exclusive(lock), ExitStack() as images:
            if key == "xcl" and GAME_EXE.resolve().is_relative_to(GAME_ASSETS.resolve()):
                image = GAME_EXE  # XCL finds the title manifest above nested executables.
            else:
                if view is not None:
                    image = view / GAME_EXE.name
                else:
                    image = images.enter_context(title_image(GAME_EXE.name, prefer_asset_image=key=="victory-nvidia"))
            env = environment(state, project)
            env.update(input_env)
            library = os.environ.get("PALMLINE_SAVE_LIBRARY")
            if key == "xcl":
                if os.environ.get("PALMLINE_XCL_DATA_ROOT"):
                    env["XCL_DATA_ROOT"] = os.environ["PALMLINE_XCL_DATA_ROOT"]
                xcl_library = os.environ.get("PALMLINE_XCL_SLOT_LIBRARY")
                if xcl_library:
                    slot = selected_save_data(Path(xcl_library))
                    images.enter_context(exclusive(slot / "game-session.lock"))
                    if slot.name != "default":
                        env["XCL_DATA_ROOT"] = str(slot / "data")
                send("LOG", "XCL_DATA_ROOT="+env["XCL_DATA_ROOT"])
            elif library:
                slot = selected_save_data(Path(library))
                variable = "VICTORY_DATA_DIR"
                if key in ("cgbj", "victory-nvidia"):
                    images.enter_context(exclusive(slot / "game-session.lock"))
                    env[variable] = str(slot)
                else:
                    env[variable] = str(slot / key)
                send("LOG", variable+"="+env[variable])
            if "PALMLINE_GTA6_PRESET" in input_env:
                send("LOG", "GTA6 定制 · F6 切换步行和驾驶。长按 Alt 切换角色。")
            code = play(exe, env, run, image, project.get("console", "series-x"), key)
            phase(run, f"exit {code}")
            send("EXIT", code)
            return 0 if code == 0 else 1
    except (OSError, RuntimeError, ValueError) as exc:
        phase(run, f"error {exc}")
        send("SHADER_ERROR" if isinstance(exc, ShaderError) else "ERROR", exc)
        return 1


def warm_worker(key, work, prompt_view, full=False):
    if sys.stdin.buffer.readline().strip() != b"PREPARE":
        return 1
    project = ENGINES[key]
    runtime = runtime_path(project)
    state = LAB / "profiles" / project["profile"]
    result = work / "result.txt"
    try:
        summary = prepare_session(runtime, work.parent, environment(state, project), prompt_view, full, key)
    except (OSError, RuntimeError, ValueError) as exc:
        result.write_text(str(exc), encoding="utf-8")
        return 2 if isinstance(exc, ShaderError) else 1
    result.write_text(summary, encoding="utf-8")
    return 0


def probe_project(key):
    project = ENGINES[key]
    runtime = runtime_path(project)
    state = LAB / "profiles" / project["profile"]
    state.mkdir(parents=True, exist_ok=True)
    with (state / "compatibility.log").open("w", encoding="utf-8") as log:
        try:
            if key == "victory-nvidia" and not gta_prompt_title(GAME_ASSETS):
                return 1
            if key == "xcl":
                with exclusive(state / "integrated-run.lock"):
                    result = subprocess.run([runtime, "inspect", GAME_EXE], env=environment(state, project),
                                            cwd=state, capture_output=True, timeout=120,
                                            creationflags=subprocess.CREATE_NO_WINDOW)
                log.write(log_text(result.stdout+result.stderr))
                return 0 if not result.returncode and re.search(rb"(?m)^Console game:\s+yes\b", result.stdout) else 1
            if not (GAME_ASSETS / "shaders.rpf").is_file():
                return 1
            with exclusive(state / "integrated-run.lock"), title_image(prefer_asset_image=key=="victory-nvidia") as image:
                env = environment(state, project)
                env["VICTORY_GAME_DIR"] = str(image.parent)
                options = ["--console", project.get("console", "series-x")]
                with subprocess.Popen([runtime, "run", image, *options],
                                        env=env, cwd=state,
                                        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                        creationflags=subprocess.CREATE_NO_WINDOW) as child:
                    def capture():
                        for raw in child.stdout:
                            line = log_text(raw)
                            print(line, file=log)
                            send(native_source(line), line)
                    reader = threading.Thread(target=capture)
                    reader.start()
                    try:
                        code = child.wait(timeout=120)
                    finally:
                        if child.poll() is None:
                            child.kill()
                        reader.join()
                if code:
                    print(f"Game load check exited with code {code & 0xffffffff:#010x}.", file=log)
                    return code
                return 0
        except (OSError, RuntimeError, subprocess.TimeoutExpired) as exc:
            print(exc, file=log)
            send("ERROR", exc)
            return 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("engine", choices=ENGINES)
    parser.add_argument("--warm-worker", type=Path)
    parser.add_argument("--prompt-view", type=Path)
    parser.add_argument("--probe", action="store_true")
    shader_action = parser.add_mutually_exclusive_group()
    shader_action.add_argument("--full-warmup", action="store_true")
    shader_action.add_argument("--clear-shaders", action="store_true")
    parser.add_argument("--achievements", nargs="?", const="", metavar="RUN",
                        help="Read observed achievements as JSON from a run directory (default: latest game session)")
    args = parser.parse_args()
    if args.achievements is not None:
        if args.achievements:
            run = Path(args.achievements)
            if not (run / "runtime.log").is_file():
                parser.error("RUN must be a game session directory containing runtime.log")
        else:
            logs = (LAB / "runs").glob("Desktop-*-"+args.engine+"/runtime.log")
            latest = max(logs, key=lambda p: p.parent.name, default=None)
            run = latest.parent if latest else None
        print(json.dumps(read_achievements(run), ensure_ascii=True, indent=2))
        sys.exit(0)
    if args.probe:
        sys.exit(probe_project(args.engine))
    sys.exit(warm_worker(args.engine, args.warm_worker, args.prompt_view, args.full_warmup) if args.warm_worker
             else session(args.engine, args.full_warmup, args.clear_shaders))
