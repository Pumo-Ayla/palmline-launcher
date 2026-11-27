[CmdletBinding()]
param(
    [string[]]$Project,
    [string]$VisualStudioPath,
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$launcher = Split-Path -Parent $PSScriptRoot
$community = Join-Path $launcher 'Community'
$catalog = ([xml](Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $community 'projects.xml'))).projects.project
if ($Project) {
    foreach ($id in $Project) {
        if ($id -notin $catalog.id) { throw "未知项目 $id" }
    }
    $catalog = $catalog | Where-Object id -in $Project
}
if (!$catalog.Count) { return }
if (!$VisualStudioPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $VisualStudioPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
Import-Module (Join-Path $VisualStudioPath 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $VisualStudioPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$toolDir = Join-Path $launcher 'XCL\tools'
if (Test-Path -LiteralPath (Join-Path $toolDir 'cargo\bin\cargo.exe')) {
    $env:CARGO_HOME = Join-Path $toolDir 'cargo'
    $env:RUSTUP_HOME = Join-Path $toolDir 'rustup'
    $env:PATH = (Join-Path $env:CARGO_HOME 'bin') + ';' + $env:PATH
}
$obj = Join-Path $PSScriptRoot 'obj'
[void][IO.Directory]::CreateDirectory($obj)
& cl.exe /nologo /EHsc /O2 /MT (Join-Path $PSScriptRoot 'Native\adapter-info.cpp') "/Fo$obj\adapter-info.obj" "/Fe$community\adapter-info.exe" /link d3d12.lib
if ($LASTEXITCODE) { throw '显卡能力探针构建失败' }
foreach ($entry in $catalog) {
    $base = Join-Path $community $entry.id
    $src = Join-Path $base $entry.commit
    $env:CARGO_TARGET_DIR = Join-Path $obj ('engines\' + $entry.id)
    $flags = @('--locked','--release','-p','victory','--features','diagnostics','--bins')
    if ($entry.id -eq 'xcl') { $flags = @('--locked','--release','-p','xcl','--bin','xcl') }
    if ($Offline) { $flags += '--offline' }
    Push-Location -LiteralPath $src
    try {
        & cargo +stable build @flags
        if ($LASTEXITCODE) { throw "$($entry.repo) 构建失败" }
        $bin = Join-Path $base ('runtime\' + $entry.commit)
        [void][IO.Directory]::CreateDirectory($bin)
        Copy-Item -LiteralPath (Join-Path $src 'LICENSE') -Destination $bin -Force
        $engineExe = if ($entry.id -eq 'xcl') { 'xcl.exe' } else { 'victory.exe' }
        Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR "release/$engineExe") -Destination $bin -Force
    } finally { Pop-Location }
}
