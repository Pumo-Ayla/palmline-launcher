$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$output = Join-Path $PSScriptRoot 'dist'
$lab = Join-Path $output 'XCL-VIctory-Lab'
$desktop = Join-Path $lab 'desktop'
New-Item -ItemType Directory -Force -Path $desktop | Out-Null

$refs = foreach ($name in 'PresentationFramework', 'PresentationCore', 'WindowsBase') {
    '/reference:' + (Join-Path $framework "WPF\$name.dll")
}
$resources = foreach ($name in 'Main.xaml', 'Console.xaml') {
    "/resource:$PSScriptRoot\$name,$name"
}
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'rooftop.jpg') -PathType Leaf) {
    $resources += "/resource:$PSScriptRoot\rooftop.jpg,rooftop.jpg"
}

$exe = Join-Path $output 'Palmline Launcher.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 @refs @resources '/reference:System.Xaml.dll' "/out:$exe" "$PSScriptRoot\DesktopLauncher.cs"
if ($LASTEXITCODE) { throw "csc exited with $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'session.py') -Destination $desktop -Force
foreach ($name in 'launcher.py', 'prepare_startup.py') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $lab -Force
}
Write-Output $exe
