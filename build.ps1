$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Source/GTA6Launcher/GTA6Launcher.csproj'
& dotnet build $project -c Release @args
exit $LASTEXITCODE
