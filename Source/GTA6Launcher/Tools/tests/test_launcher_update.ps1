# Run with Windows PowerShell against the built .NET Framework helper.
param([Parameter(Mandatory)][string]$Helper, [string]$TempRoot = [IO.Path]::GetTempPath(), [string]$LauncherDll)
$ErrorActionPreference = 'Stop'
$Helper = (Resolve-Path -LiteralPath $Helper).Path
Add-Type -AssemblyName System.Net.Http,System.IO.Compression,System.IO.Compression.FileSystem
Add-Type -ReferencedAssemblies System.Net.Http -TypeDefinition @'
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
public class ReleaseFixture : HttpMessageHandler {
    public string Json;
    public byte[] Zip;
    public int Downloads;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (r.RequestUri.Host == "api.github.com") response.Content = new StringContent(Json);
        else { Downloads++; response.Content = new ByteArrayContent(Zip); }
        return Task.FromResult(response);
    }
}
'@
function Assert($ok, $message) { if (!$ok) { throw $message } }
function WriteFile($path, $value) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $value)
}
$base = [IO.Path]::GetFullPath((Join-Path $TempRoot ('palmline-update-test-' + [guid]::NewGuid().ToString('N'))))
$root = Join-Path $base 'My Launcher'
$state = Join-Path $root '.launcher-update'
$payload = Join-Path $base 'payload/Launcher 1.0.2'
$source = Join-Path $base 'release.zip'
$agent = Join-Path $state 'agent.exe'
function Pack {
    [IO.File]::Delete($source)
    [IO.Compression.ZipFile]::CreateFromDirectory((Split-Path $payload), $source)
    LoadAsset
}
function LoadAsset {
    $handler.Zip = [IO.File]::ReadAllBytes($source)
    $asset.size = $handler.Zip.Length
    $asset.digest = 'sha256:' + (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $handler.Json = $release | ConvertTo-Json -Depth 4
}
function Fetch($version) {
    $candidate = $checkMethod.Invoke($null, @($client.PSObject.BaseObject,[Version]$version))
    if (!$candidate) { return $false }
    $progress.Clear()
    $downloadMethod.Invoke($null, @($client.PSObject.BaseObject,$state.PSObject.BaseObject,$candidate,[Action[int]]{param($n) $progress.Add($n)}))
    return $true
}
function RunAgent([string]$confirmation = 'INSTALL') {
    $psi = [Diagnostics.ProcessStartInfo]::new($agent, 'install 2147483646 1.0.2.0')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $child = [Diagnostics.Process]::Start($psi)
    $line = $child.StandardOutput.ReadLine()
    if ($line -eq 'READY') {
        if ($confirmation) { $child.StandardInput.WriteLine($confirmation) }
        $child.StandardInput.Close()
    }
    return $child
}
function Result($process, $code) {
    Assert ($process.WaitForExit(30000)) 'Updater timed out'
    if ($process.ExitCode -ne $code) { throw "Exit $($process.ExitCode), expected $code. $(Get-Content (Join-Path $state 'update.log') -Tail 6 -ErrorAction SilentlyContinue)" }
    $process.Dispose()
}
try {
    [void][IO.Directory]::CreateDirectory($state)
    [void][IO.Directory]::CreateDirectory($payload)
    Copy-Item -LiteralPath $Helper -Destination $agent
    Copy-Item -LiteralPath ($Helper + '.config') -Destination ($agent + '.config')
    Copy-Item -LiteralPath $Helper -Destination (Join-Path $root 'Launcher.exe')
    if ($LauncherDll) {
        Copy-Item -LiteralPath $LauncherDll -Destination (Join-Path $payload 'Launcher.dll')
    } else {
        $file = Join-Path $base 'fixture.cs'
        WriteFile $file '[assembly:System.Reflection.AssemblyVersion("1.0.2.0")] public class Fixture {}'
        $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
        & $compiler /nologo /target:library "/out:$payload/Launcher.dll" $file
        Assert ($LASTEXITCODE -eq 0) 'Failed to build the fixture assembly'
    }
    foreach ($name in @('Launcher.exe','Launcher.runtimeconfig.json','Launcher.deps.json','Input/SDL3.dll',
        'Input/Palmline.Log.exe','Runtime/Python/python.exe','Runtime/DotNet/dotnet.exe','Community/adapter-info.exe',
        'Community/sample/pic.png','Community/sample/runtime/abc/victory.exe','Community/sample/runtime/abc/mapped-vi/victory.exe',
        'XCL-VIctory-Lab/desktop/session.py','XCL-VIctory-Lab/desktop/warmup_job.py',
        'XCL-VIctory-Lab/desktop/display-settings.xml','XCL-VIctory-Lab/desktop/game-selection.txt')) {
        WriteFile (Join-Path $payload $name) 'new'
    }
    if ($LauncherDll) {
        foreach ($name in @('Launcher.exe','Launcher.runtimeconfig.json','Launcher.deps.json')) {
            Copy-Item -LiteralPath (Join-Path (Split-Path $LauncherDll) $name) -Destination (Join-Path $payload $name) -Force
        }
    }
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $stub = Join-Path $base 'restart.cs'
    WriteFile $stub 'class LauncherFixture { static void Main() { System.IO.File.WriteAllText("restarted.txt","yes"); } }'
    & $compiler /nologo /target:winexe "/out:$payload/Launcher.exe" $stub
    Assert ($LASTEXITCODE -eq 0) 'Failed to build restart fixture'
    $data = New-Object byte[] (768 * 1024)
    ([Random]::new(42)).NextBytes($data)
    [IO.File]::WriteAllBytes((Join-Path $payload 'Community/sample/pic.png'), $data)
    WriteFile (Join-Path $payload 'Community/projects.xml') '<projects><project id="sample" commit="abc" image="pic.png"/></projects>'
    $scriptFile = Join-Path $root 'XCL-VIctory-Lab/desktop/session.py'
    $preferences = Join-Path $root 'XCL-VIctory-Lab/desktop/display-settings.xml'
    $selection = Join-Path $root 'XCL-VIctory-Lab/desktop/game-selection.txt'
    foreach ($f in @($scriptFile,$preferences,$selection)) { WriteFile $f 'original' }
    WriteFile (Join-Path $root 'Community/old.exe') 'old'
    WriteFile (Join-Path $state 'installed-files') 'Community/old.exe'
    $handler = New-Object ReleaseFixture
    $client = New-Object Net.Http.HttpClient($handler)
    $asset = @{ name='Launcher.1.0.2.zip'; browser_download_url='https://github.com/Pumo-Ayla/series-relay-launcher/releases/download/v1.0.2/Launcher.1.0.2.zip' }
    $release = @{ tag_name='v1.0.2'; draft=$false; prerelease=$false; assets=@($asset) }
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($Helper))
    $flags = [Reflection.BindingFlags]'NonPublic,Static'
    $checkMethod = $assembly.GetType('ReleasePackage').GetMethod('Check', $flags)
    $downloadMethod = $assembly.GetType('ReleasePackage').GetMethod('Download', $flags)
    $progress = [Collections.Generic.List[int]]::new()
    Pack
    $newer = $checkMethod.Invoke($null, @($client.PSObject.BaseObject,[Version]'1.0.0.0'))
    Assert ($newer -and $handler.Downloads -eq 0 -and !(Test-Path (Join-Path $state 'ready'))) 'Discovery downloaded an archive'
    $minimum = $newer.GetType().GetField('MinimumLauncher',[Reflection.BindingFlags]'NonPublic,Instance')
    Assert ($null -eq $minimum.GetValue($newer)) 'Unmarked release became mandatory'
    $release.body = "Notes`r`n<!-- min-launcher-version: 1.0.1 -->"
    $handler.Json = $release | ConvertTo-Json -Depth 4
    $critical = $checkMethod.Invoke($null,@($client.PSObject.BaseObject,[Version]'1.0.0.0'))
    Assert ($minimum.GetValue($critical) -eq [Version]'1.0.1.0') 'Lost the minimum supported version'
    $release.body = '<!-- min-launcher-version: 9.0.0 -->'
    $handler.Json = $release | ConvertTo-Json -Depth 4
    $invalid = $checkMethod.Invoke($null,@($client.PSObject.BaseObject,[Version]'1.0.0.0'))
    Assert ($null -eq $minimum.GetValue($invalid)) 'Uninstallable minimum version accepted'
    $release.body = "<!-- sr-update-history-v1`n0.9.9 critical`n1.0.0 normal`n1.0.1 critical`n1.0.2 normal`n-->"
    $handler.Json = $release | ConvertTo-Json -Depth 4
    $older = $checkMethod.Invoke($null,@($client.PSObject.BaseObject,[Version]'0.9.8.0'))
    $normalCount = $older.GetType().GetField('NormalUpdates',[Reflection.BindingFlags]'NonPublic,Instance')
    $criticalCount = $older.GetType().GetField('CriticalUpdates',[Reflection.BindingFlags]'NonPublic,Instance')
    Assert ($criticalCount.GetValue($older) -eq 2 -and $normalCount.GetValue($older) -eq 2) 'Lost intermediate releases'
    Assert ($minimum.GetValue($older) -eq [Version]'1.0.1.0') 'A later normal update masked a critical update'
    $atFloor = $checkMethod.Invoke($null,@($client.PSObject.BaseObject,[Version]'1.0.1.0'))
    Assert ($criticalCount.GetValue($atFloor) -eq 0 -and $normalCount.GetValue($atFloor) -eq 1) 'Installed release counted again'
    $release.Remove('body')
    $handler.Json = $release | ConvertTo-Json -Depth 4
    Assert (!(Fetch '1.0.2.0') -and $handler.Downloads -eq 0) 'Same version downloaded'
    $release.prerelease = $true
    $handler.Json = $release | ConvertTo-Json -Depth 4
    Assert (!(Fetch '1.0.0.0') -and $handler.Downloads -eq 0) 'Prerelease downloaded'
    $release.prerelease = $false
    $asset.digest = 'sha256:' + ('0' * 64)
    $handler.Json = $release | ConvertTo-Json -Depth 4
    $rejected = $false
    try { Fetch '1.0.0.0' } catch { $rejected = $true }
    Assert ($rejected -and !(Test-Path (Join-Path $state 'ready')) -and !(Test-Path (Join-Path $state 'download.part'))) 'Bad digest accepted'
    Write-Output 'PASS: release selection and download integrity'
    $zip = [IO.Compression.ZipFile]::Open($source, [IO.Compression.ZipArchiveMode]::Update)
    try { [void]$zip.CreateEntry('Launcher 1.0.2/Community/../../outside.txt') } finally { $zip.Dispose() }
    LoadAsset
    Assert (Fetch '1.0.0.0') 'Tampered test archive did not reach validation'
    Result (RunAgent) 1
    Assert (([IO.File]::ReadAllText((Join-Path $state 'update.log'))) -match 'Invalid update path') 'Unsafe ZIP path was not rejected'
    Pack
    Assert (Fetch '1.0.0.0') 'Valid release not downloaded'
    $held = [IO.File]::Open($scriptFile,'Open','Read','None')
    try { Result (RunAgent) 1 } finally { $held.Dispose() }
    Assert (([IO.File]::ReadAllText($scriptFile) -eq 'original') -and (Test-Path (Join-Path $root 'Community/old.exe'))) 'Rollback did not restore old files'
    Assert (!(Test-Path (Join-Path $root 'Launcher.dll')) -and !(Test-Path (Join-Path $state 'transaction'))) 'Partial update after rollback'
    Assert (!(Test-Path (Join-Path $root 'Community/projects.xml'))) 'Catalog not rolled back'
    $failure = [IO.File]::ReadAllText((Join-Path $state 'update.log'))
    Assert ($failure -match 'File\.(?:Internal)?Replace') $failure
    Write-Output 'PASS: interrupted file replacement rolled back'
    Assert (Fetch '1.0.0.0') 'Retry failed'
    Assert ($progress.Count -gt 3 -and $progress[0] -gt 0 -and $progress[0] -lt 50 -and $progress[-1] -eq 100) 'Missing download progress'
    Result (RunAgent '') 2
    Assert ((Test-Path (Join-Path $state 'ready')) -and !(Test-Path (Join-Path $root 'Launcher.dll'))) 'Installed without confirmation'
    $lock = Join-Path $root 'XCL-VIctory-Lab/profiles/sample/integrated-run.lock'
    WriteFile $lock ''
    $held = [IO.File]::Open($lock,'Open','ReadWrite','None')
    try {
        Result (RunAgent) 3
        Assert (!(Test-Path (Join-Path $root 'Launcher.dll'))) 'Replaced files during game'
    } finally { $held.Dispose() }
    Assert ((Test-Path (Join-Path $state 'package.zip')) -and !(Test-Path (Join-Path $root 'Launcher.dll'))) 'Blocked installation was queued'
    Result (RunAgent) 0
    $timeout = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path (Join-Path $root 'restarted.txt')) -and $timeout.Elapsed.TotalSeconds -lt 5) { Start-Sleep -Milliseconds 50 }
    Assert (Test-Path (Join-Path $root 'restarted.txt')) 'Launcher did not restart'
    Assert (([IO.File]::ReadAllText($scriptFile) -eq 'new') -and !(Test-Path (Join-Path $root 'Community/old.exe'))) 'New package not installed'
    Assert (([IO.File]::ReadAllText($preferences) -eq 'original') -and ([IO.File]::ReadAllText($selection) -eq 'original')) 'User data overwritten'
    foreach ($name in @('ready','package.zip','stage','rollback','transaction','download.part')) {
        Assert (!(Test-Path (Join-Path $state $name))) "Update leftover: $name"
    }
    # Simulate process loss after moving one new file and replacing an existing file.
    WriteFile (Join-Path $state 'rollback/README.txt') 'previous'
    WriteFile (Join-Path $root 'README.txt') 'partial'
    WriteFile (Join-Path $root 'Input/interrupted.dll') 'partial'
    [IO.File]::WriteAllLines((Join-Path $state 'transaction'), @('=README.txt','+Input/interrupted.dll'))
    $flags = [Reflection.BindingFlags]'NonPublic,Instance'
    $install = $assembly.GetType('PackageInstall')
    $instance = $install.GetConstructor($flags,$null,@([string]),$null).Invoke(@($root.PSObject.BaseObject))
    $install.GetMethod('Recover',$flags).Invoke($instance,$null)
    Assert (([IO.File]::ReadAllText((Join-Path $root 'README.txt'))) -eq 'previous') 'Recovery lost original file'
    Assert (!(Test-Path (Join-Path $root 'Input/interrupted.dll')) -and !(Test-Path (Join-Path $state 'transaction'))) 'Recovery left a partial install'
    Write-Output 'PASS: check-only discovery, click-to-download progress, explicit install, live game rejection, restart, preferences and ZIP cleanup.'
} finally {
    if ($client) { $client.Dispose() }
    if ((Split-Path $base) -eq [IO.Path]::GetFullPath($TempRoot).TrimEnd('\') -and (Split-Path $base -Leaf) -match '^palmline-update-test-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $base -Recurse -Force
    }
}
