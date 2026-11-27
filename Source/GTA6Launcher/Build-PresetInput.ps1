[CmdletBinding()]
param([string[]]$Project)
$ErrorActionPreference='Stop'
$launcher=Split-Path $PSScriptRoot -Parent
$community=Join-Path $launcher 'Community'
$entries=([xml](Get-Content -Raw -Encoding UTF8 (Join-Path $community 'projects.xml'))).projects.project
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$env:CARGO_HOME=Join-Path $launcher 'XCL\tools\cargo'
$env:RUSTUP_HOME=Join-Path $launcher 'XCL\tools\rustup'
$env:PATH=(Join-Path $env:CARGO_HOME 'bin')+';'+$env:PATH
$utf8=New-Object Text.UTF8Encoding($false)
function Insert-Once([string]$text,[string]$pattern,[string]$replacement) {
    $matchesFound=[regex]::Matches($text,$pattern)
    if($matchesFound.Count -ne 1) { throw "Unsupported upstream GameInput host: $pattern" }
    return [regex]::Replace($text,$pattern,$replacement)
}
foreach($entry in $entries) {
    if($entry.id -ne 'cgbj') { continue }
    if($Project -and $entry.id -notin $Project) { continue }
    $base=Join-Path $community $entry.id
    $upstream=Join-Path $base $entry.commit
    $stage=Join-Path $PSScriptRoot ("obj\preset-src\"+$entry.id+"\"+$entry.commit)
    & robocopy $upstream $stage /E /XD .git target /NFL /NDL /NJH /NJS /NP | Out-Null
    if($LASTEXITCODE -ge 8) { throw "Could not stage $upstream" }
    $modules=Join-Path $stage 'crates\victory-hle\src\modules'
    $gpu=Join-Path $stage 'crates\victory-gpu'
    $gpuSource=Join-Path $upstream 'crates\victory-gpu'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Native\keyboard_window.rs') -Destination (Join-Path $gpu 'src') -Force
    $manifest=[IO.File]::ReadAllText((Join-Path $gpuSource 'Cargo.toml'))
    $sysLine=[regex]::Match($manifest,'(?m)^windows-sys = [^\r\n]+').Value
    $missing=@('Win32_Graphics_Gdi','Win32_UI_Input_KeyboardAndMouse','Win32_UI_WindowsAndMessaging') |
        Where-Object { !$sysLine.Contains($_) } | ForEach-Object { '"'+$_+'", ' }
    $manifest=Insert-Once $manifest '(?m)^(windows-sys = \{ workspace = true, features = \[)' ('$1'+($missing -join ''))
    [IO.File]::WriteAllText((Join-Path $gpu 'Cargo.toml'),$manifest,$utf8)
    $lib=[IO.File]::ReadAllText((Join-Path $gpuSource 'src\lib.rs'))
    [IO.File]::WriteAllText((Join-Path $gpu 'src\lib.rs'),$lib+ "`n#[cfg(windows)]`npub mod keyboard_window;`n",$utf8)
    $proc='(extern "system" fn window_proc\([^)]*\) -> LRESULT \{)'
    $gpuHost=[IO.File]::ReadAllText((Join-Path $gpuSource 'src\host.rs'))
    $gpuHost=Insert-Once $gpuHost '(let hwnd = match title_window\(\) \{[^}]+\};)' ('$1' + "`n        crate::keyboard_window::presented(hwnd.0);")
    $gpuHost=Insert-Once $gpuHost $proc ('$1' + "`n    if let Some(result) = crate::keyboard_window::message(hwnd.0, message, wparam.0, lparam.0) { return LRESULT(result); }")
    [IO.File]::WriteAllText((Join-Path $gpu 'src\host.rs'),$gpuHost,$utf8)
    $lifecycle=[IO.File]::ReadAllText((Join-Path $upstream 'crates\victory-hle\src\modules\lifecycle.rs'))
    $lifecycle=Insert-Once $lifecycle $proc ('$1' + "`n    if let Some(result) = victory_gpu::keyboard_window::message(hwnd, message, wparam, lparam) { return result; }")
    [IO.File]::WriteAllText((Join-Path $modules 'lifecycle.rs'),$lifecycle,$utf8)
    $hostPath=Join-Path $modules 'gameinput.rs'
    $code=[IO.File]::ReadAllText((Join-Path $upstream 'crates\victory-hle\src\modules\gameinput.rs'))
    $code=Insert-Once $code '(    #\[derive\(Default\)\]\r?\n    pub\(super\) struct Host \{)' ('    mod gta_vi_input { include!("gta_vi_input.rs"); }' + "`n" + '$1' + "`n        mapped: gta_vi_input::MappedPad,")
    $code=Insert-Once $code '(impl Source for Host \{\s+fn pad\(&mut self, slot: usize\) -> Option<PadState> \{)' ('$1' + "`n            if self.mapped.enabled { return (slot == 0).then(|| self.mapped.poll()); }")
    $code=Insert-Once $code '(fn keys\(&mut self\) -> Vec<KeyState> \{\s+)(#\[cfg\(feature = "diagnostics"\)\]|let mut down = scripted\(\);)' ('$1if self.mapped.enabled || !victory_gpu::keyboard_window::accepts_keys() { return Vec::new(); }' + "`n            " + '$2')
    $code=Insert-Once $code '(fn rumble\(&mut self, slot: usize,[^\n]+\{\s+)(let speeds = XINPUT_VIBRATION)' ('$1if self.mapped.enabled { return; }' + "`n            " + '$2')
    [IO.File]::WriteAllText($hostPath,$code,$utf8)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Native\gta_vi_input.rs'),(Join-Path $PSScriptRoot 'Assets\GtaVi.bindings') -Destination $modules -Force
    $env:CARGO_TARGET_DIR=Join-Path $PSScriptRoot ("obj\engines\"+$entry.id)
    $profile='release'
    $flags=@('--locked','--offline','--profile',$profile,'--features','diagnostics')
    Push-Location -LiteralPath $stage
    try {
        & cargo +stable test @flags -p victory-hle --lib preset_reaches_gameinput
        if($LASTEXITCODE) { throw "$($entry.id) input test failed" }
        & cargo +stable build @flags -p victory --bin victory
        if($LASTEXITCODE) { throw "$($entry.id) input build failed" }
        $out=Join-Path $base ('runtime\'+$entry.commit+'\mapped-vi')
        [void][IO.Directory]::CreateDirectory($out)
        Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR "$profile\victory.exe") -Destination $out -Force
        Get-ChildItem -LiteralPath (Split-Path -Parent $out) -File -Filter '*.dll' | Copy-Item -Destination $out -Force
    } finally { Pop-Location }
}
