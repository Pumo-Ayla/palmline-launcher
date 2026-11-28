param([Parameter(Mandatory)][string]$Launcher, [string]$Scratch=[IO.Path]::GetTempPath())
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,WindowsBase,System.IO.Compression
$assembly=[Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Launcher))
$sessionType=$assembly.GetType('SessionLog',$true)
$root=[IO.Directory]::CreateDirectory((Join-Path $Scratch ('report-test-'+[Guid]::NewGuid().ToString('N')))).FullName
$utf8=New-Object Text.UTF8Encoding($false)
function Put([string]$path,[string]$value) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path,$value,$utf8)
}
function Require([bool]$condition,[string]$message) { if(-not $condition){throw $message} }
$lab=Join-Path $root 'XCL-VIctory-Lab'
$old=Join-Path $lab 'runs/Desktop-20261128-080000-100000-cgbj'
$run=Join-Path $lab 'runs/Desktop-20261128-090000-200000-victory-nvidia'
Put "$root/Logs/Launcher-20000101-000000-000-12345678.log" "2000-01-01 00:00:00.000 +08:00 [LAUNCHER] RUN | $old"
Put "$root/Logs/Launcher-20000102-000000-000-12345678.log" "start$([Environment]::NewLine)2000-01-02 00:00:00.000 +08:00 [LAUNCHER] Another launcher instance is open."
Put "$old/prewarm.log" 'previous attempt: preload exited 1'
Put "$run/runtime.log" 'presented first frame'
Put "$run/engine-detail.log" 'engine-specific trace'
Put "$run/progress-state.txt" 'running'
Put "$lab/runs/Desktop-20261128-100000-300000-xcl/runtime.log" 'unrelated session payload'
Put "$run/other.txt" 'unrelated file payload'
Put "$lab/profiles/victory-nvidia/compatibility.log" 'compatibility probe result'
Put "$root/.launcher-update/update.log" 'update helper event'
Put "$root/GameSaves/instance/player.sav" 'private save payload'
Put "$root/inputs/MicrosoftGame.Config" '<Game/>'
Put "$root/inputs/test.exe" 'QA file identifier'
$session=[Activator]::CreateInstance($sessionType,@($root))
$locked=$null
try {
    $session.Write('LAUNCHER',"RUN | $run")
    $session.Write('TRANSFER','exit=0x80008083; Authorization: Bearer sample-secret')
    $session.Write('IMPORT','start magnet:?xt=urn:btih:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa&tr=tracker-secret')
    $session.Write('TRANSFER','https://sample-user:sample-pass@example.invalid/private-key?access_token=sample-token')
    Put "$run/achievements.log" 'locked payload'
    $locked=[IO.File]::Open("$run/achievements.log",'Open','ReadWrite','None')
    $report=Join-Path $root 'case.log'
    $session.Export($report,'instance=test; selected=victory-nvidia',"$root/inputs","$root/inputs/test.exe")
    $content=[IO.File]::ReadAllText($report)
    foreach($s in @('SRLauncher diagnostic report v1','Previous launcher session','presented first frame','previous attempt: preload exited 1',
        'engine-specific trace','update helper event','compatibility probe result','[not recorded]','[unavailable]','END OF REPORT','selected=victory-nvidia',
        '0x80008083','urn:btih:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','SHA256=','Physical memory')) {
        Require ($content.Contains($s)) "Missing report evidence: $s"
    }
    foreach($s in @('unrelated session payload','unrelated file payload','private save payload','sample-secret','tracker-secret','sample-pass','private-key','sample-token','locked payload')) {
        Require (-not $content.Contains($s)) "Unexpected report content: $s"
    }
    $locked.Dispose(); $locked=$null
    $session.Write('LAUNCHER','second export')
    $session.Export($report)
    Require ([IO.File]::ReadAllText($report).Contains('second export')) 'Existing report was not replaced'
    $blocked=$false
    try {$session.Export($session.FileName)} catch {$blocked=$true}
    Require $blocked 'Original launcher log could be overwritten'
    "PASS: current/previous attempts, active writer, retry, private data, unavailable inputs, original preserved"
} finally {
    if($locked){$locked.Dispose()}
    $session.Dispose()
}
