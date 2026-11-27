$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $root 'GTA6Launcher\GTA6Launcher.csproj'
& dotnet build $project -c Release -t:InstallLauncher --nologo
if ($LASTEXITCODE) { throw "Launcher build exited with $LASTEXITCODE" }
