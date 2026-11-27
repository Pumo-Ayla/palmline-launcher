param([Parameter(Mandatory)][string]$SdkDirectory, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$sdk = (Resolve-Path -LiteralPath $SdkDirectory).Path
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
Import-Module "$vs/Common7/Tools/Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$out = [IO.Directory]::CreateDirectory($OutputDirectory).FullName
$obj = [IO.Directory]::CreateDirectory((Join-Path $PSScriptRoot 'obj/dlss')).FullName
$ngx = Join-Path $sdk 'external/ngx-sdk'
& cl.exe /nologo /LD /O2 /MD /EHsc /std:c++17 "/I$ngx/include" "$PSScriptRoot/Native/DlssSuperResolution.cpp" "/Fo$obj/sr.obj" "/Fe$out/CgbjDlss.dll" /link "/IMPLIB:$obj/CgbjDlss.lib" "$ngx/lib/Windows_x86_64/nvsdk_ngx_d.lib" d3d12.lib dxgi.lib advapi32.lib wintrust.lib crypt32.lib user32.lib
if ($LASTEXITCODE) { throw "DLSS build failed ($LASTEXITCODE)" }
& cl.exe /nologo /O2 /MD /EHsc /std:c++17 "$PSScriptRoot/Native/DlssCapabilities.cpp" "/Fo$obj/probe.obj" "/Fe$out/DlssCapabilities.exe" /link "$obj/CgbjDlss.lib" d3d12.lib
if ($LASTEXITCODE) { throw "DLSS capability probe build failed ($LASTEXITCODE)" }
Copy-Item -LiteralPath "$sdk/bin/x64/nvngx_dlss.dll","$sdk/bin/x64/nvngx_dlss.license.txt" -Destination $out
$fg=[IO.Directory]::CreateDirectory((Join-Path $out 'FrameGeneration')).FullName
& cl.exe /nologo /LD /O2 /MD /EHsc /std:c++17 "/I$sdk/include" "$PSScriptRoot/Native/DlssFrameGeneration.cpp" "/Fo$obj/fg.obj" "/Fe$fg/CgbjFrameGeneration.dll" /link "/IMPLIB:$obj/CgbjFrameGeneration.lib" wintrust.lib crypt32.lib user32.lib advapi32.lib
if ($LASTEXITCODE) { throw "Frame generation bridge build failed ($LASTEXITCODE)" }
foreach($name in 'sl.interposer.dll','sl.common.dll','sl.dlss.dll','sl.dlss_g.dll','sl.reflex.dll','sl.pcl.dll','nvngx_dlss.dll','nvngx_dlssg.dll','nvngx_dlss.license.txt','reflex.license.txt') {
    Copy-Item -LiteralPath (Join-Path "$sdk/bin/x64" $name) -Destination $fg
}
Copy-Item -LiteralPath "$sdk/license.txt","$sdk/3rd-party-licenses.md" -Destination $fg
