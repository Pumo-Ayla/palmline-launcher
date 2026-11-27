import io
import base64
from contextlib import ExitStack
import os
import struct
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import MagicMock, call, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "XCL-VIctory-Lab/desktop"))
import session
import key_prompts


class NativeEngineTests(unittest.TestCase):
    def test_victory_nvidia_plays_from_the_selected_shared_slot_under_lock(self):
        with tempfile.TemporaryDirectory() as directory, ExitStack() as mocks:
            root = Path(directory)
            game = root / "game"
            game.mkdir()
            exe = game / "game_scarlett_master.exe"
            exe.touch()
            (game / "shaders.rpf").touch()
            library = root / "GameSaves" / "instance"
            mocks.enter_context(patch.dict(os.environ, {"PALMLINE_SAVE_LIBRARY": str(library),
                "PALMLINE_PRELOAD_PROMPTS": "", "PALMLINE_WARMUP_EVENT": ""}))
            mocks.enter_context(patch.object(session, "LAB", root / "lab"))
            mocks.enter_context(patch.object(session, "GAME_ASSETS", game))
            mocks.enter_context(patch.object(session, "GAME_EXE", exe))
            mocks.enter_context(patch.object(session, "prepare_session", return_value="ready"))
            mocks.enter_context(patch.object(session, "send"))
            for name in ("default", "a" * 32):
                slot = library / name
                story = slot / "7952987A" / "saves" / "sample"
                story.parent.mkdir(parents=True)
                story.write_text(name)
                if name != "default":
                    (library / "selected.txt").write_text(name)
                def check_play(runtime, env, run, image, console, engine):
                    selected = Path(env["VICTORY_DATA_DIR"])
                    self.assertEqual(selected, slot)
                    self.assertEqual((selected / story.relative_to(slot)).read_text(), name)
                    with self.assertRaises(RuntimeError), session.exclusive(slot / "game-session.lock"):
                        self.fail("Concurrent save access")
                    return 0
                with patch.object(session, "play", side_effect=check_play) as play, patch.object(sys, "stdin", io.StringIO("PLAY\n")):
                    self.assertEqual(session.session("victory-nvidia"), 0)
                    play.assert_called_once()
                with session.exclusive(slot / "game-session.lock"):
                    pass  # The next session can acquire the slot.

    def test_nvidia_preload_and_play_share_cache_and_use_native_commands(self):
        project = session.ENGINES["victory-nvidia"]
        runtime = session.runtime_path(project)
        with tempfile.TemporaryDirectory() as directory, ExitStack() as mocks:
            root = Path(directory)
            assets = root / "game"
            assets.mkdir()
            image = root / "image.exe"
            state = root / "profiles/victory-nvidia"
            menu = root / "victory/victory-settings.txt"
            menu.parent.mkdir()
            menu.write_text("VICTORY_EARLY_DEPTH=\n")
            mocks.enter_context(patch.dict(os.environ, {"LOCALAPPDATA": str(root)}))
            mocks.enter_context(patch.object(session, "GAME_ASSETS", assets))
            mocks.enter_context(patch.object(session, "GAME_EXE", image))
            env = session.environment(state, project)
            mocks.enter_context(patch.object(session, "send"))
            lines = mocks.enter_context(patch.object(session, "native_lines", return_value=[
                "8 shaders in the cache, 0 do not translate, 1 s",
                "0 of 0 in the pipeline library in 0 s",
            ]))
            session.prepare_nvidia(runtime, root, env)
            _, args, preload_env, *_ = lines.call_args.args
            self.assertEqual(args, ["preload", assets, image])
            self.assertNotIn("VICTORY_EARLY_DEPTH", preload_env)
            lines.return_value = ["8 shaders in the cache, 0 do not translate, 1 s"]
            with self.assertRaises(session.ShaderError):
                session.prepare_nvidia(runtime, root, env)
            play = mocks.enter_context(patch.object(session, "run_game"))
            session.play(runtime, env, root, image, "series-x", "victory-nvidia")
            command, play_env, *_ = play.call_args.args
            self.assertEqual(command, [runtime, "play", image])
            self.assertIn("victory_hle::modules::xuser=info", play_env["VICTORY_LOG"])
            self.assertEqual(play_env["VICTORY_CACHE_DIR"], str(state / "cache"))
            self.assertEqual(play_env["VICTORY_CACHE_DIR"], preload_env["VICTORY_CACHE_DIR"])

    def test_native_engines_select_separate_binaries_and_cache_roots(self):
        root = Path("profiles").resolve()
        with patch.dict(os.environ, {"VICTORY_CACHE_DIR": "old", "XCL_DXIL_CACHE": "old", "PALMLINE_DLSS_SR": "1"}):
            for key, project in session.ENGINES.items():
                runtime = session.runtime_path(project)
                env = session.environment(root / project["profile"], project)
                self.assertEqual(runtime.name, "xcl.exe" if key == "xcl" else "victory.exe")
                self.assertEqual(runtime.parent.parent.parent.name, key)
                play_runtime = session.input_for_play("PLAY", runtime, key)[0]
                self.assertTrue(play_runtime.is_relative_to(runtime.parent))
                self.assertNotIn("PALMLINE_DLSS_SR", env)
                if key == "xcl":
                    self.assertEqual(Path(env["XCL_DXIL_CACHE"]), root / key / "cache/shaders")
                    self.assertNotIn("VICTORY_CACHE_DIR", env)
                else:
                    self.assertEqual(Path(env["VICTORY_CACHE_DIR"]), root / key / "cache")
                    self.assertNotIn("XCL_DXIL_CACHE", env)

    def test_xcl_warmup_reports_partial_compiles_and_refused_pipelines(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(session, "send"):
            root = Path(directory)
            (root / "shaders.rpf").touch()
            lines = ["compiled 1, already cached 2, errors 3, skipped 0", "pipelines made on the GPU 1, refused 2"]
            with patch.object(session, "GAME_ASSETS", root), patch.object(session, "native_lines", return_value=lines) as run:
                env = {"XCL_DXIL_CACHE": str(root)}
                summary = session.prepare(Path("xcl.exe"), root, env)
                self.assertEqual(run.call_args.args[1], ["shaders", root / "shaders.rpf", "--precompile", "--pipelines"])
                self.assertIn("3 个着色器跳过", summary)
                self.assertIn("2 条管线未通过", summary)
                (root / "used-pipelines.txt").write_text("sample")
                session.prepare(Path("xcl.exe"), root, env)
                self.assertIn("--used-only", run.call_args.args[1])
                session.prepare(Path("xcl.exe"), root, env, full=True)
                self.assertNotIn("--used-only", run.call_args.args[1])


class KeyPromptTests(unittest.TestCase):
    def test_enter_and_space_use_distinct_drawn_legends_inside_the_circle(self):
        with patch.object(key_prompts, "_letter_paths", side_effect=AssertionError("unexpected font lookup")):
            enter = key_prompts._keycap_paths("Enter")
            space = key_prompts._keycap_paths("Space")
        self.assertNotEqual(enter, space)
        for paths in (enter, space):
            for contour in paths:
                for x, y in contour:
                    self.assertLess((x-640)**2 + (y-640)**2, 578**2)

    def exercise_session(self, enabled, command, count=1, engine="cgbj", title_id="7952987A"):
        payload = base64.b64encode(b"INPUT_CONTEXT_Y=E").decode("ascii")
        events = []
        with tempfile.TemporaryDirectory() as temp, ExitStack() as stack:
            root = Path(temp)
            assets = root / "game"
            assets.mkdir()
            game = assets / "title.exe"
            for name in (game.name, "data.rpf", "shaders.rpf"):
                (assets / name).write_bytes(b"original")
            (assets / "MicrosoftGame.Config").write_text(f"<Game><TitleId>{title_id}</TitleId></Game>")
            project = session.ENGINES[engine]
            runtime = root / engine / "runtime" / project["commit"]
            (runtime / "mapped-vi").mkdir(parents=True)
            for folder in (runtime, runtime / "mapped-vi"):
                (folder / ("xcl.exe" if engine == "xcl" else "victory.exe")).touch()
            def create_copy(source, dest, bindings):
                self.assertEqual(bindings, {b"INPUT_CONTEXT_Y": b"[E]"})
                events.append("patch")
                if count:
                    dest.write_bytes(b"prepared")
                return 1, count
            def play(exe, env, run, image, console, project_id):
                self.assertEqual(project_id, engine)
                self.assertEqual(exe.parent, runtime / "mapped-vi" if engine == "cgbj" else runtime)
                self.assertEqual(image.name, game.name)
                self.assertEqual(image.read_bytes(), b"original")
                self.assertEqual((image.parent / "data.rpf").read_bytes(), b"prepared" if "patch" in events else b"original")
                events.append("play")
                return 0
            for name, value in dict(COMMUNITY=root, LAB=root, GAME_ASSETS=assets, GAME_EXE=game).items():
                stack.enter_context(patch.object(session, name, value))
            stack.enter_context(patch.dict(os.environ, {"PALMLINE_PRELOAD_PROMPTS": payload if enabled else ""}, clear=True))
            stack.enter_context(patch.object(session.sys, "stdin", io.StringIO(command.replace("{profile}", payload))))
            stack.enter_context(patch.object(session, "import_legacy_history"))
            stack.enter_context(patch.object(session, "environment", return_value={"XCL_DATA_ROOT": str(root)}))
            stack.enter_context(patch.object(session, "prepare", side_effect=lambda *args: events.append("shaders") or "ok"))
            stack.enter_context(patch.object(key_prompts, "create_copy", side_effect=create_copy))
            stack.enter_context(patch.object(session.os, "link", session.shutil.copyfile))
            stack.enter_context(patch.object(session, "play", side_effect=play))
            stack.enter_context(patch.object(session, "send", side_effect=lambda kind, text: events.append(kind)))
            code = session.session(engine)
            self.assertEqual((assets / "data.rpf").read_bytes(), b"original")
            self.assertFalse(list(root.rglob("pal-title-*")))
            return code, [e for e in events if e in ("patch","shaders","READY","play","ERROR")]

    def test_prompts_prepared_before_ready_and_reused_on_play(self):
        self.assertEqual(self.exercise_session(True, "PLAY\tGTA6_PRESET\tPROMPTS={profile}"),
                         (0, ["patch", "shaders", "READY", "play"]))
        self.assertEqual(self.exercise_session(False, "PLAY"), (0, ["shaders", "READY", "play"]))

    def test_cancel_failure_and_stale_mapping_never_launch(self):
        self.assertEqual(self.exercise_session(True, ""), (0, ["patch", "shaders", "READY"]))
        self.assertEqual(self.exercise_session(True, "PLAY"), (1, ["patch", "shaders", "READY", "ERROR"]))
        self.assertEqual(self.exercise_session(True, "", count=0), (1, ["patch", "ERROR"]))

    def test_other_titles_and_general_project_skip_patching(self):
        self.assertEqual(self.exercise_session(True, "PLAY\tKEYBOARD\tPROMPTS={profile}", engine="xcl"),
                         (0, ["shaders", "READY", "play"]))
        self.assertEqual(self.exercise_session(True, "PLAY\tKEYBOARD", title_id="00000001"),
                         (0, ["shaders", "READY", "play"]))

    def test_replace_only_known_tokens_in_text_pool_without_moving_other_strings(self):
        text = b"Hold ~INPUT_AIM|PromptId=CTX_AIM,Gutter~.\0Use ~INPUT_UNKNOWN~.\0"
        resource = bytearray(512)
        resource[16:21] = b"0DRPz"
        struct.pack_into("<Q", resource, 0x38, 0x50000100)
        struct.pack_into("<I", resource, 0x48, 4)
        for index, field in enumerate((0xa8478889, 0x99331089, 0x101, 0x10)):
            struct.pack_into("<IIQ", resource, 256 + index * 16, field, len(text), 0x50000060)
        resource[96:96 + len(text)] = text
        changed, count = key_prompts.replace_labels(bytes(resource), {b"INPUT_AIM": b"[RMB]"})
        self.assertEqual(count, 1)
        self.assertEqual(len(resource), len(changed))
        self.assertEqual(changed[96:].split(b"\0", 1)[0], b"Hold [RMB].")
        next_string = resource.index(b"Use ")
        self.assertEqual(changed[next_string:], resource[next_string:])
        resource[16] = 0
        self.assertIsNone(key_prompts.replace_labels(bytes(resource), {b"INPUT_AIM": b"[RMB]"}))


def traced(message):
    return b" INFO \x1b[2mvictory_gpu::host\x1b[0m\x1b[2m:\x1b[0m " + message + b"\r\n"


class CgbjLogTests(unittest.TestCase):
    def test_clear_only_shader_cache_and_allow_empty_cache(self):
        with tempfile.TemporaryDirectory() as tmp, patch.object(session, "LAB", Path(tmp)):
            state = Path(tmp, "profiles", "cgbj")
            for folder in ("cache/shaders", "cache/pipelines", "data"):
                path = state / folder
                path.mkdir(parents=True)
                (path / "entry").touch()
            session.clear_shader_cache(state)
            session.clear_shader_cache(state)
            self.assertFalse((state / "cache/shaders").exists())
            self.assertTrue((state / "cache/pipelines/entry").exists())
            self.assertTrue((state / "data/entry").exists())
            with self.assertRaises(session.ShaderError):
                session.clear_shader_cache(Path(tmp) / "unrelated")

    def test_full_scan_retries_stalled_translation_once_before_pipeline_warmup(self):
        commands = []
        def lines(runtime, args, *unused, **kwargs):
            commands.append(args)
            if len(commands) == 1:
                raise session.ShaderStall("stalled")
            if args[0] == "shaders":
                yield "8 shaders in the cache, 0 do not translate, 1 s"
            else:
                yield "0 pipelines known, 0 made before (abc), 0 bad; 0 to make, 4 processes"
                yield "0 made in 0 s (0 have no host pipeline, 0 ended their process)"
        with tempfile.TemporaryDirectory() as tmp, patch.object(session, "native_lines", lines), patch.object(session, "send"):
            root = Path(tmp)
            assets = root / "game"
            assets.mkdir()
            with patch.object(session, "GAME_ASSETS", assets), patch.object(session, "GAME_EXE", assets / "title.exe"):
                session.prepare("victory.exe", root, {"VICTORY_CACHE_DIR": tmp}, full=True)
            self.assertEqual(commands, [["shaders", assets], ["shaders", assets, "--jobs", "1"], ["warm", "--used"]])

    def prepare_with(self, shader_output, pipeline_output=None, shader_exit=0):
        outputs = [
            shader_output,
            pipeline_output or b"0 pipelines known, 0 made before (abc), 0 bad; 0 to make, 8 processes\n"
            b"0 made in 0 s (0 have no host pipeline, 0 ended their process); ms each: median 0\n",
        ]
        self.commands = []
        def start(command, **kwargs):
            self.commands.append(command[1:])
            child = MagicMock()
            child.__enter__.return_value = child
            child.stdout = io.BytesIO(outputs.pop(0))
            child.wait.return_value = shader_exit if command[1] == "shaders" else 0
            return child
        with tempfile.TemporaryDirectory() as directory, patch.object(session.subprocess, "Popen", start), patch.object(session, "send") as self.events:
            return session.prepare(Path("victory.exe"), Path(directory), {"VICTORY_CACHE_DIR": directory})

    def test_coloured_device_status_allows_pipeline_warmup(self):
        result = self.prepare_with(traced(b"host D3D12 device ready") + b"25663 shaders in the cache, 226 do not translate, 5 s\n")
        self.assertIn("226 个着色器跳过", result)
        self.assertIn("首次运行将记录渲染管线", result)

    def test_cache_hit_without_device_trace_uses_documented_commands(self):
        result = self.prepare_with(b"1000 shaders in the cache, 1 s\n1234 shaders in the cache, 0 do not translate, 2 s\n",
            b"12 pipelines known, 3 made before (abc), 0 bad; 0 to make, 8 processes\n"
            b"0 made in 1 s (0 have no host pipeline, 0 ended their process)\n")
        self.assertEqual(self.commands, [["shaders", session.GAME_ASSETS / "shaders.rpf"], ["warm", "--used"]])
        self.events.assert_any_call("COUNT", 1234)
        self.assertIn("已无待预热管线", result)
        self.assertNotIn("首次运行", result)

    def test_pipeline_counts_use_this_runs_worklist(self):
        result = self.prepare_with(b"234 shaders in the cache, 0 do not translate, 1 s\n",
            b"12000 pipelines known, 8000 made before (abc), 0 bad; 1002 to make, 4 processes\n"
            b"1000 of 1002, 5 s\n1001 made in 6 s (2 have no host pipeline, 1 ended their process)\n")
        self.assertEqual([item for item in self.events.call_args_list if item.args[0] == "PROGRESS"],
                         [call("PROGRESS", val) for val in ("0/1002", "1000/1002", "1002/1002")])
        self.assertIn("3 条管线未通过", result)

    def test_native_shader_failure_never_starts_pipeline_stage(self):
        with self.assertRaisesRegex(session.ShaderError, "shaders"):
            self.prepare_with(b"device creation failed\n", shader_exit=1)
        self.assertEqual(len(self.commands), 1)

    def test_empty_or_incomplete_archive_and_pipeline_results_fail(self):
        with self.assertRaises(session.ShaderError):
            self.prepare_with(b"0 shaders in the cache, 0 do not translate, 0 s\n")
        with self.assertRaises(session.ShaderError):
            self.prepare_with(b"1000 shaders in the cache, 1 s\n")
        with self.assertRaises(session.ShaderError):
            self.prepare_with(b"100 shaders in the cache, 0 do not translate, 1 s\n",
                b"10 pipelines known, 0 made before (abc), 0 bad; 4 to make, 4 processes\n"
                b"3 made in 2 s (0 have no host pipeline, 0 ended their process)\n")

    def test_host_meter_accepts_coloured_and_plain_output(self):
        sample = traced(b"28.5 fps, 35.1 ms/frame (0.2 preparing)")
        self.assertTrue(session.host_frame(sample))
        self.assertTrue(session.host_frame(session.log_text(sample).encode()))
        self.assertFalse(session.host_frame(traced(b"presenting to a window")))
        self.assertFalse(session.host_frame(sample.replace(b"::host", b"::queue")))

    def test_achievement_service_requests_do_not_imply_unlock(self):
        base = (b'2026-11-26T21:32:04.123Z INFO victory_hle::modules::xuser: '
                b'token and signature: offline url="https://achievements.xboxlive.com/'
                b'users/xuid(123456)/achievements')
        for path, expected in (
            (b'?titleId=2035456122&maxItems=55"', "query"),
            (b'/sample-scid/27"', "query achievementId=27"),
            (b'/sample-scid/update"', "progress-update-request"),
        ):
            self.assertEqual(session.achievement_record(base + path),
                             "2026-11-26T21:32:04.123Z [CGBJ achievement] " + expected + " result=offline")
        for line in (b'resolving XblAchievementsUpdateAchievementAsync', b'achievement unlocked=false',
                     b'could not emit AchievementUnlocked', b'INFO events: AchievementUnlocked failed',
                     b'{"achievementId":"27","progressState":"Achieved"}'):
            self.assertIsNone(session.achievement_record(line))

    def test_achievement_capture_through_play_loop(self):
        stamp = b"2026-11-26T21:32:04.123Z"
        # Split the event across two native reads and end the last line without a newline.
        first = stamp + b' INFO events: AchievementUnlocked achievementId="27"'
        prefix = b"x" * (262144 - 32) + b"\n"
        payload = (prefix + first + b"\n" + b"frame noise\n" * 7 +
                   traced(b"28.5 fps, 35.1 ms/frame (0.2 preparing)") +
                   stamp + b' INFO events: {"event":"achievement_unlocked","achievementId":"B-09"}')
        def start(command, **kwargs):
            self.assertIn("victory_hle::modules::xuser=info", kwargs["env"]["VICTORY_LOG"])
            kwargs["stdout"].write(payload)
            kwargs["stdout"].flush()
            child = MagicMock(pid=1)
            child.poll.return_value = 0
            return child
        with tempfile.TemporaryDirectory() as directory, patch.object(session, "configure_dlss"), \
                patch.object(session.subprocess, "Popen", start), \
                patch.object(session, "history_folder", return_value=None), patch.object(session, "send") as events, \
                patch.object(sys, "stdout", new_callable=io.StringIO) as output:
            run = Path(directory)
            self.assertEqual(session.play(Path("victory.exe"), {"VICTORY_LOG":"warn"}, run,
                                          run / "game.exe", "test", "cgbj"), 0)
            entries = (run / "achievements.log").read_text(encoding="utf-8").splitlines()
            self.assertEqual(entries, [
                f"{stamp.decode()} [CGBJ achievement] unlocked achievementId={value}"
                for value in ("27", "B-09")])
            for entry in entries:
                self.assertEqual(events.call_args_list.count(call("LOG", entry)), 1)
            self.assertIn("EMULATOR\t" + prefix.decode(), output.getvalue())
            self.assertEqual(output.getvalue().count("EMULATOR\tframe noise\n"), 7)

    def test_guest_debug_output_uses_exact_trace_target(self):
        with patch.object(sys, "stdout", new_callable=io.StringIO) as output:
            session.forward_runtime_logs([b"2026-11-27T08:01:00Z  INFO \x1b[2mtitle\x1b[0m: Loading world",
                                          b"INFO victory_gpu::host: title: Loading world",
                                          b"INFO victory_gpu::frame: drawing"], None)
            self.assertEqual(output.getvalue().splitlines(), [
                "GAME\t2026-11-27T08:01:00Z  INFO title: Loading world",
                "EMULATOR\tINFO victory_gpu::host: title: Loading world",
                "EMULATOR\tINFO victory_gpu::frame: drawing"])

    def test_belobug_reads_new_progress_and_older_mixed_tag_journals(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(session, "send"), \
                patch.object(sys, "stdout", new_callable=io.StringIO):
            run = Path(directory)
            journal = run / "achievements.log"
            journal.write_text('2026-11-27T04:00:00Z [CGBJ achievement] local-progress '
                               '{"achievementId":"7","percentComplete":10}\n', encoding="utf-8")
            session.forward_runtime_logs([
                b'INFO victory_hle::modules::xuser: local achievement update {"achievementId":"7","percentComplete":42}',
                b'INFO events: AchievementUnlocked achievementId="8"',
                b'INFO victory_hle::modules::xuser: token and signature: offline '
                b'url="https://achievements.xboxlive.com/users/xuid(12)/achievements/scid/update"',
            ], run, "belobug")
            lines = journal.read_text(encoding="utf-8").splitlines()
            self.assertTrue(all(" [BELOBUG achievement] " in line for line in lines[1:]))
            snapshot = session.read_achievements(run)
            self.assertEqual(snapshot["unidentifiedUpdateRequests"], 1)
            self.assertEqual([(item["achievementId"], item["percentComplete"]) for item in snapshot["achievements"]],
                             [("7", 42), ("8", 100)])

    def test_zcy_offline_requests_keep_query_ids_without_awarding_progress(self):
        prefix = (b'2026-11-27T15:24:12.546597Z INFO victory_hle::modules::xuser: token and signature: offline '
                  b'url="https://achievements.xboxlive.com/users/xuid(12)/achievements')
        with tempfile.TemporaryDirectory() as directory, patch.object(sys, "stdout", new_callable=io.StringIO):
            run = Path(directory)
            session.forward_runtime_logs([prefix + tail for tail in (b'"', b'/scid/7"', b'/scid/update"')],
                                         run, "anyxboxx-zcy")
            snapshot = session.read_achievements(run)
            self.assertEqual(snapshot["achievements"], [])
            self.assertEqual([q["achievementId"] for q in snapshot["queries"]], [None, "7"])
            self.assertEqual(snapshot["queries"][1]["observedAt"], "2026-11-27T15:24:12.546597Z")
            self.assertEqual(snapshot["unidentifiedUpdateRequests"], 1)

    def test_local_achievement_api_keeps_highest_progress_and_separates_legacy_requests(self):
        prefix = b"2026-11-27T04:18:00Z INFO victory_hle::modules::xuser: local achievement update "
        reports = [prefix + (b'{"achievementId":"test-7","percentComplete":%d}' % p)
                   for p in (12, 100, 100, 18)]
        reports += [prefix + b'{"achievementId":"test-8","percentComplete":0}',
                    prefix + b'{"achievementId":"invalid","percentComplete":false}',
                    b'INFO victory_hle::modules::xuser: token and signature: offline '
                    b'url="https://achievements.xboxlive.com/users/xuid(12)/achievements/scid/update"']
        with tempfile.TemporaryDirectory() as directory, patch.object(session, "send"):
            run = Path(directory)
            session.forward_runtime_logs(reports, run)
            snapshot = session.read_achievements(run)
            self.assertEqual(snapshot["unidentifiedUpdateRequests"], 1)
            self.assertEqual([(a["achievementId"], a["state"], a["percentComplete"])
                              for a in snapshot["achievements"]],
                             [("test-7", "local-complete", 100), ("test-8", "in-progress", 0)])


if __name__ == "__main__":
    unittest.main()
