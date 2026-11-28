# Series Relay Launcher Further Development Notes

English · [简体中文](DEVELOPMENT.zh-CN.md) · [繁體中文](DEVELOPMENT.zh-TW.md) · [日本語](DEVELOPMENT.ja.md) · [한국어](DEVELOPMENT.ko.md) · [User guide](../README.md)

These notes cover the code paths, data contracts and regression tests for extending **v1.0.4.2**. The WPF process owns the library, presentation and user decisions. The transfer helper inspects and materializes sources. A Python session worker prepares the selected engine, watches its output and reports lifecycle events. A separate update agent can replace a closed launcher. In a report, include the launcher version, title ID, engine ID, pinned revision and run directory. A display name alone cannot identify a session.

## Import from a folder, archive or magnet

| Step | Contract | Source |
| --- | --- | --- |
| Classify the source | Local sources prefer a manifest in the selected directory or its immediate `GameData` child. Otherwise enumerate archive candidates at that level. Magnet inspection reads the torrent file list and persists the metadata. | [ImportSource.cs](../Source/GTA6Launcher/ImportSource.cs), [transfer CLI](../Source/GTA6Launcher/Transfers/Program.cs) |
| Resolve the EXE | `Contents` asks where the game program lives. `External` collects the supplied source or selected bundled item. `Archive` records destination and extraction policy. Input changes invalidate a previously verified result. | [ImportCard.cs](../Source/GTA6Launcher/ImportCard.cs), [DeferredInstall.cs](../Source/GTA6Launcher/DeferredInstall.cs) |
| Validate local data | `ValidateImportFiles` reads `MicrosoftGame.Config`. Declared EXE paths must resolve inside the manifest root. The optimized title also permits an external file with the declared basename. The file must have an AMD64 PE header; the four required RPFs are opened and their headers checked. | [GameImport.cs](../Source/GTA6Launcher/GameImport.cs) |
| Register or defer | An already materialized pair of paths can be saved immediately. An archive, remote body or separate EXE package becomes an `InstallPlan` with body mode, EXE mode, sources and destination. The plan and any torrent metadata survive a launcher restart. | [DeferredInstall.cs](../Source/GTA6Launcher/DeferredInstall.cs), [GameLibrary.cs](../Source/GTA6Launcher/GameLibrary.cs) |
| Materialize | The library action executes the EXE job if needed, then the body job. Successful output paths are saved between stages. The final file check precedes replacing the pending instance with a ready one under the same ID. | [LocalImport.cs](../Source/GTA6Launcher/Transfers/LocalImport.cs), [MagnetArchives.cs](../Source/GTA6Launcher/Transfers/MagnetArchives.cs) |

A local `.part01.exe` can be an SFX archive. Archive classification, format checks and volume continuity are separate from game EXE validation. Standard extraction retains source files. In release mode a fully consumed earlier RAR stream may be retired when the next volume opens. Final retirement follows the extracted manifest check. A later failure cannot restore volumes already released. For a magnet archive, the worker first downloads the set, then extracts it. The metadata stage alone cannot certify an EXE hidden inside an archive.

**Example.** An instance created from a data-only magnet archive and a local EXE can appear in the rail while it still has a pending body job. The first library action is download, followed by extraction and validation. Emit the installation notification only after the pending plan commits successfully. The ready-to-play notification belongs to the later warmup transition.

## Session lifecycle and state notifications

The controller starts the worker with the selected engine, asset and EXE paths. The protocol uses a record type and tab-separated payload on standard output. Route protocol messages separately from engine and game text so a line in a guest log cannot accidentally drive the UI. [DesktopLauncher.cs](../Source/GTA6Launcher/DesktopLauncher.cs) · [session.py](../Source/XCL-VIctory-Lab/desktop/session.py)

| Evidence | Meaning | Desktop behavior |
| --- | --- | --- |
| `PRELOADING` / `STAGE` | Preparation started / current stage changed | Queue the preload notification once the worker begins preparing in the warming state. Update the button detail with stage text. |
| `COUNT` / `PROGRESS` | Cached shaders / processed pipelines and total | Keep the counters distinct. Only the progress pair supplies the pipeline fraction. |
| `READY` | The worker finished preparation and can accept `PLAY` | Enable Play. Transition from warming to ready produces a ready notification. |
| `DEVICE_READY` | D3D12 device reported ready | Keep waiting for frame evidence. This stage can matter when diagnosing a black screen. |
| `PLAYING` | The worker observed first-frame evidence and supplied the window process ID | Attach the achievement monitor, mark the instance running and notify once. |
| `EXIT` / `ERROR` / `SHADER_ERROR` | Game exit or preparation failure | End or fail the corresponding session. Examine the run log and keep the error stage when reproducing it. |

The four launcher notices are `Installed`, `Preloading`, `Ready` and `Started`. `LauncherNotices` displays queued items with a dispatcher timer. The started notice uses the game monitor when its window is available. The achievement stack reserves the notice inset only on the same monitor. `ResetSession` clears obsolete session notices while preserving queued installation notices. These events have their own artwork and do not create achievement entries. [LauncherNotice.cs](../Source/GTA6Launcher/LauncherNotice.cs) · [AchievementToast.cs](../Source/GTA6Launcher/AchievementToast.cs)

## How achievement IDs and progress are detected

**SR Achievement Event Monitoring System** builds a local per-instance journal. The component receives runtime output, normalizes evidence, records it once per captured line, then uses the same event format for the live overlay and historical reconstruction.

1. `forward_runtime_logs` receives raw lines from the current run. `achievement_record(raw, engine)` strips terminal formatting, preserves a recognized timestamp with offset and falls back to the current UTC time. The engine identifier is part of the normalized record.
2. For `victory_hle::modules::xuser: local achievement update`, decode the JSON object. Require `achievementId` to be a nonempty string of at most 256 characters and `percentComplete` to be an integer from 0 through 100. The type check excludes booleans. Keep the ID as text, including numeric-looking IDs.
3. Separate `query` and `progress-update-request` diagnostics from `local-progress` and explicit `unlocked` evidence. A query can name a real achievement and an outgoing update can be offline. Neither establishes that progress changed. Failure, pending or request wording is filtered from the explicit-unlock path.
4. Append the normalized record to `runs/<run>/achievements.log` and send it via `LOG`. The desktop parser recognizes supported engine tags and parses the JSON or explicit ID. A known ID maps to a catalog entry and its artwork.
5. `AchievementJournal.ReadHistory` follows `desktop/play-history/<instance>/Desktop-*.time` to the corresponding run journals. Keep the maximum percentage for each ID and the earliest valid completion timestamp; on a timestamp tie favor the explicit unlock.
6. `AchievementToast.Session.Observe` initializes its high-water marks from history. Only a strict increase for a known ID queues a notification. Pending updates for the same ID coalesce. Completed items take priority; positioning uses the game window's monitor.

### Worked replay

The following synthetic lines use catalog ID `1`. Send them through a test input or parser fixture. They are examples of normalized events, not a live gameplay recording.

```text
2026-01-01T12:00:00Z [CGBJ achievement] query achievementId=1 result=offline
2026-01-01T12:00:01Z [CGBJ achievement] local-progress {"achievementId":"1","percentComplete":42}
2026-01-01T12:00:02Z [CGBJ achievement] local-progress {"achievementId":"1","percentComplete":12}
2026-01-01T12:00:03Z [CGBJ achievement] unlocked achievementId=1
```

After the first line, the achievement remains unearned. After the second, the maximum is 42. The third preserves 42. The last records 100 and the first completion time. Replaying it should not display a second unlock. An unknown ID can be captured before a catalog entry exists, but the live overlay requires an entry. When an expected card is missing, check the raw run log, its normalized journal, the instance-to-run marker and the catalog **in that order**.

The catalog has 55 rows. Each TSV row has 12 fields: ID, points, five names, then five descriptions. Both language groups are ordered Simplified Chinese, English, Traditional Chinese, Japanese, Korean. Use stable IDs when updating translations. The public engine support indicator currently lists CGBJ and VIctory-NVIDIA. The list of tags a parser accepts for historical journals is a separate compatibility contract. [Catalog](../Source/GTA6Launcher/Assets/GtaViAchievements.tsv) · [journal](../Source/GTA6Launcher/AchievementJournal.cs) · [tile](../Source/GTA6Launcher/AchievementTile.cs) · [engine card](../Source/GTA6Launcher/ProjectHoverCard.cs)

## Input mapping and HUD rendering

### Producing the input profile

`MappingStep.All` supplies the 18-step editor order. Custom slots persist user bindings. The built-in preset uses a table of context, Windows virtual key and Xbox control. `GtaViPreset` switches foot and driving context on F6, and enables aiming bindings while aiming on foot. For example, W maps to movement on foot and the right trigger in the driving layer. A configured custom profile serializes the first 16 control mappings as two-digit hexadecimal virtual keys. Share and Guide remain editor steps outside that packet. [MappingSteps.cs](../Source/GTA6Launcher/MappingSteps.cs) · [bindings](../Source/GTA6Launcher/Assets/GtaVi.bindings) · [BindingSlots.cs](../Source/GTA6Launcher/BindingSlots.cs)

### Passing the same action to the visible UI

On CGBJ, `CurrentPlayCommand` chooses `PLAY` for a controller, `PLAY<TAB>KEYBOARD` for custom keyboard mapping, or `PLAY<TAB>GTA6_PRESET` for the built-in profile. `<TAB>` denotes a single tab. The optional `PAD=` field carries the custom input profile. `PROMPTS=` carries a Base64-encoded ASCII list of action-to-keycap bindings. For example, the decoded list can contain `INPUT_AIM=RMB` and `INPUT_JUMP=Space` on separate lines. The prompt decoder validates field names and labels. An encoded payload over 16,384 characters is rejected. [GameKeyPrompts.cs](../Source/GTA6Launcher/GameKeyPrompts.cs) · [session decoder](../Source/XCL-VIctory-Lab/desktop/session.py)

### Updating text and vector symbols

| Layer | Operation | Invariant |
| --- | --- | --- |
| Action lookup | Resolve the action against the custom binding or built-in context. Foot attack and vehicle acceleration can both use RT while displaying different keys. | Input and legends refer to the same selected profile. |
| Text resources | `replace_labels` replaces recognized `~INPUT_…~` tokens in supported string pools. | Unknown tokens remain intact. Replacements must fit the original entry so later indexed strings retain their positions. |
| Scaleform symbols | `replace_button_glyphs` maps exported button symbols to action labels. Build the outline, face and key legend as vector layers and reuse shared shapes. Enter and Space have different drawn marks. | Only recognized sprite layouts qualify. The result must fit the resource and its compressed slot. |
| Session assembly | `create_copy` reads the original `data.rpf`, prepares a modified session copy and returns separate text and glyph counts. | Require at least one compatible HUD glyph before declaring success. Recheck the selected payload on Play. |

Enablement requires a recognized title (`7952987A`), CGBJ, keyboard mode, a configured slot and the user's in-game prompts option. If input works but the HUD looks unchanged, check those five conditions, the `Keyboard prompts:` log entry and its two counts. A zero text count can coexist with valid glyph replacements. Regression cases include disabled prompts, other engines, an unsupported title, an oversized string, a missing icon library and a profile changed after preload. [Resource implementation](../Source/XCL-VIctory-Lab/desktop/key_prompts.py) · [Regression suite](../Source/GTA6Launcher/Tools/tests/test_cgbj_log.py)

## Rendering, data ownership and diagnostics

| Subsystem | What to inspect |
| --- | --- |
| VIctory-NVIDIA warmup | `prepare_nvidia` loads the pinned runtime's `preload.env`, merges supported F1 menu values and runs native preload. Read `prewarm.log` for shader cache counts, untranslated entries and pipeline library counts. Some switches, including early depth, affect cache identity. An existing shader count does not predict pipeline work or duration. |
| Frame generation | The UI obtains capabilities and bounds the permitted multiplier by frame-generation type, console mode and upscaling state. The NVIDIA menu adapter persists supported settings without discarding unrelated lines. [RenderSettings.cs](../Source/GTA6Launcher/RenderSettings.cs) · [VictoryNvidiaMenu.cs](../Source/GTA6Launcher/VictoryNvidiaMenu.cs) |
| Instances and saves | `desktop/game-instances.xml` records instance ID, assets, EXE, profile, artwork and any pending install. Library writes replace through a temporary file. `GameSaves/<instance>` holds local slots. XCL has separate slot handling. A save import is staged before selection. Per-instance save access is locked while playing. [LocalSaveLibrary.cs](../Source/GTA6Launcher/LocalSaveLibrary.cs) · [XclSaveLibrary.cs](../Source/GTA6Launcher/XclSaveLibrary.cs) |
| Component repair | The embedded index identifies a group, destination path, byte count and SHA-256. Content is restored from the matching digest in the bundled component library or an embedded recovery resource. Python gets an import smoke test and the transfer host a self-test. Repair scope covers distributed components; user game data and vendor drivers have separate ownership. [ComponentLibrary.cs](../Source/GTA6Launcher/ComponentLibrary.cs) · [ComponentSettings.cs](../Source/GTA6Launcher/ComponentSettings.cs) |
| Logs | The session launcher writes timestamped, source-prefixed files in `Logs`. Export copies a consistent length while the log is open. Run-level `runtime.log`, `prewarm.log` and `achievements.log` answer different questions. [SessionLog.cs](../Source/GTA6Launcher/SessionLog.cs) |

## Build and verify

Use Windows x64, .NET 9 SDK, .NET Framework 4.8 and 4.6.2 targeting packs, and Python 3.11 or later. Place the matching complete release ZIP in `Updates`. Run from the repository root.

```powershell
py -3 prepare_inputs.py Updates/SRLauncher-1.0.4.2.zip --portable
dotnet build Source/GTA6Launcher/GTA6Launcher.csproj -c Release
py -3 -m unittest discover -s Source/GTA6Launcher/Tools/tests -p test_cgbj_log.py
py -3 Source/GTA6Launcher/Tools/tests/test_import_archive.py
powershell -NoProfile -File Source/GTA6Launcher/Tools/tests/test_launcher_update.ps1 -Helper Source/GTA6Launcher/Updater/bin/Release/net462/Palmline.Update.exe
```

The restore script verifies the release digest. Without `--portable` it restores the offline library for the desktop build; the engine tests and packaging step also need the portable inputs. The session tests exercise mocked engine output, input and prompt handling, achievements and warmup. The archive test creates its own temporary ZIPs to exercise inspection, retention, extraction and failure. The updater test uses a controlled release response and verifies integrity, explicit installation and rollback. For the rendered overlay, use the [STA verification script](../Source/GTA6Launcher/Tools/tests/verify_achievement_overlay.ps1) against a Release net48 build.

## Ship a full update

1. Increment the launcher and log console project versions. Append exactly one row to [history.json](../Updates/history.json) with its version, `normal` or `critical` classification and human-readable change notes.
2. From a clean staging tree, use `py -3 Source/GTA6Launcher/Tools/package_portable.py --runtime-from Source/Runtime --output-dir Build`. Use the release's versioned outer directory. Copy the resulting full ZIP into `Updates` as `SRLauncher-<version>.zip`. Write its SHA-256 in ASCII as lowercase hex, two spaces and the file name. Synchronize `Assets/ComponentIndex.tsv` from the package's `Sources/Launcher.zip`.
3. Put the full ZIP, its SHA-256 file, the matching `Upgrade-Launcher.exe` and `Upgrade-Launcher.exe.config` into `Updates/`. Run `py -3 prepare_release_assets.py` and `py -3 verify_release.py`. Attach only the full and offline ZIPs to the tagged release. GitHub generates the source archive. Use `Updates/RELEASE.md` as the release body so update clients can count intermediate critical and normal releases.

### Offline upgrade

**Upgrading 1.0.4 or earlier.** The existing updater recognizes three-part tags and cannot discover this four-part version. Download [SRLauncher-1.0.4.2-offline.zip](https://github.com/Pumo-Ayla/series-relay-launcher/releases/download/v1.0.4.2/SRLauncher-1.0.4.2-offline.zip) and extract it into a temporary folder. Leave the full launcher ZIP inside it packed. Close the game and launcher, run `Upgrade-Launcher.exe` and enter the existing folder that contains `SRLauncher.exe` or `Launcher.exe`. When the tool reports completion, reopen the launcher from that original folder. Your registered games, settings and saves are preserved.
