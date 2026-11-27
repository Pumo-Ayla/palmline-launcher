Series Relay Launcher {version} for Windows x64

Extract the entire folder to a location where your account can write. Open SRLauncher.exe.
The desktop runtime, Python, SDL, shader tools and the CGBJ runtime are included.
CGBJ includes controller and keyboard/mouse builds. The CGBJ source snapshot
and the launcher's source are in Sources.

To detect a game automatically, arrange the folders like this:

    Games/
        Launcher {version}/SRLauncher.exe
        Game/MicrosoftGame.Config
        Game/common.rpf
        Game/shaders.rpf
        Game/levels.rpf
        Game/textures.rpf

Keep the complete game installation, including its other archives and subdirectories,
in Game. GameData and "Grand Theft Auto VI Original/GameData" are also detected alongside
the launcher. For another location, use + and select the game directory. The launcher
validates the game and runs the CGBJ image-load check.
For a failed load check, see XCL-VIctory-Lab/desktop/gpu-check.log for detected adapters
and the CGBJ result. Detailed results are in XCL-VIctory-Lab/profiles/cgbj/compatibility.log.
The included title executable is in OriginalGame. For split installations use a writable
NTFS game location so the launcher can construct a temporary hard-linked game view.

CGBJ scans the full shaders.rpf, then warms previously used graphics pipelines.
A first run records pipelines for the next warmup. Shader translation counts and
pipeline results are in the current session's prewarm.log. Play starts CGBJ's
renderer. The launcher confirms rendering from the host's DXGI frame statistics.

CGBJ requires Windows x64, an AVX2 CPU, a D3D12 driver supporting Shader Model
6.6 and mesh shaders, and matching game data.
The launcher saves settings, shader caches and logs inside its extracted folder.
