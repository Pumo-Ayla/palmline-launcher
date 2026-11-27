param([Parameter(Mandatory)][string]$Launcher,[Parameter(Mandatory)][string]$Library,[Parameter(Mandatory)][string]$Scratch)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.IO.Compression,System.IO.Compression.FileSystem
$stage=Join-Path $Scratch ('component-test-'+[guid]::NewGuid().ToString('N'))
$pack=Join-Path $stage 'Runtime/Components/Library.zip'
[void][IO.Directory]::CreateDirectory((Split-Path $pack))
[void][IO.Directory]::CreateDirectory((Join-Path $stage 'Community'))
Copy-Item -LiteralPath $Library -Destination $pack
Copy-Item -LiteralPath $Launcher -Destination "$stage/Launcher.exe"
$app=New-Object Windows.Application
$app.ShutdownMode='OnExplicitShutdown'
$assembly=[Reflection.Assembly]::LoadFrom("$stage/Launcher.exe")
$t=$assembly.GetType('DesktopLauncher')
$flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$shell=[Activator]::CreateInstance($t,$true)
$window=$t.GetField('window',$flags).GetValue($shell)
[Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new($window.Dispatcher))
function Elem($name){$window.FindName($name)}
function Click($name){(Elem $name).RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent))}
function Assert($ok,$msg){if(!$ok){throw $msg}; Write-Output "PASS $msg"}
function State {return $t.GetField('componentScan',$flags).GetValue($shell)}
function Wait-Scan {
    $limit=[datetime]::UtcNow.AddSeconds(90)
    while((State) -and [datetime]::UtcNow -lt $limit){
        $frame=[Windows.Threading.DispatcherFrame]::new()
        [void]$window.Dispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::ApplicationIdle,[Action]{$frame.Continue=$false})
        [Windows.Threading.Dispatcher]::PushFrame($frame)
        Start-Sleep -Milliseconds 25
    }
    Assert (!(State)) 'scan completed'
}
function Check { Click 'CheckComponents'; Wait-Scan }
function Summary {return (Elem 'ComponentSummary').Text}
Click 'ShowSettings'
(Elem 'LangChinese').IsChecked=$true
$owner=Elem 'CheckComponents'
while($owner -and $owner.Name -ne 'GeneralPage'){$owner=[Windows.LogicalTreeHelper]::GetParent($owner)}
Assert ($owner -and !(Elem 'SettingsComponents') -and !(Elem 'ComponentResults')) 'general settings contains a single environment check'
Assert ((Elem 'CheckComponents').Content -eq '检测' -and (Elem 'ComponentSummary').Visibility -eq 'Collapsed') 'idle state has only the check action'
$messages=[Collections.Generic.HashSet[string]]::new()
$textChange=[ComponentModel.DependencyPropertyDescriptor]::FromProperty([Windows.Controls.TextBlock]::TextProperty,[Windows.Controls.TextBlock])
$textChange.AddValueChanged((Elem 'ComponentSummary'),[EventHandler]{param($sender,$eventArgs) [void]$messages.Add($sender.Text)})
Click 'CheckComponents'
Assert ((State) -and (Elem 'CheckComponents').Content -eq '取消') 'scan is asynchronous and cancellable'
Click 'CheckComponents'; Wait-Scan
Assert ((Elem 'ComponentSummary').Text -eq '检测已取消') 'cancel acknowledged'
Check
Assert ((Summary) -eq '修复完成，运行环境正常。' -and !$t.GetField('activeGame',$flags).GetValue($shell)) 'offline components restored without a selected game'
Assert ($messages.Contains('正在校验本地文件…') -and $messages.Contains('正在安装或修复文件…')) 'status reflects verification and actual restoration'
Check
Assert ((Summary) -eq '检测完成，运行环境正常。') 'second scan is idempotent and runtime self-tests pass'
$target=Join-Path $stage 'Runtime/Native/dxil.dll'
$original=(Get-FileHash -LiteralPath $target).Hash
[IO.File]::WriteAllBytes($target,[byte[]](1,2,3))
$parked="$pack.hold"
[IO.File]::Move($pack,$parked)
Check
Assert ((Summary) -eq '检测发现问题，详情见日志。') 'missing offline library reported in the summary'
$logPath=$t.GetField('sessionLog',$flags).GetValue($shell).FileName
Assert (Select-String -Path $logPath -SimpleMatch 'Runtime/Native/dxil.dll' -Quiet) 'component details are available in the log'
Assert ([IO.File]::ReadAllBytes($target).Length -eq 3) 'unrepairable file preserved'
$entry=($assembly.GetType('ComponentLibrary').GetField('Index',[Reflection.BindingFlags]'NonPublic,Static').GetValue($null) | Where-Object { $_.GetType().GetField('Path',$flags).GetValue($_) -eq 'Runtime/Native/dxil.dll' })
$digest=$entry.GetType().GetField('Digest',$flags).GetValue($entry)
$zip=[IO.Compression.ZipFile]::Open($pack,[IO.Compression.ZipArchiveMode]::Create)
try {$body=$zip.CreateEntry($digest).Open();try{$body.WriteByte(0)}finally{$body.Dispose()}}finally{$zip.Dispose()}
Check
Assert ((Summary) -eq '检测发现问题，详情见日志。' -and [IO.File]::ReadAllBytes($target).Length -eq 3) 'invalid payload rejected before replacement'
[IO.File]::Replace($parked,$pack,"$pack.invalid")
Check
Assert ((Get-FileHash $target).Hash -eq $original) 'repaired file matches original SHA-256'
$gameDir=[IO.Directory]::CreateDirectory("$stage/game-fixture").FullName
$gameExe=Join-Path $gameDir 'sample.exe'
[IO.File]::WriteAllText("$gameDir/MicrosoftGame.Config",'<Game><ExecutableList><Executable Name="sample.exe"/></ExecutableList></Game>')
$game=$t.GetNestedType('GameInstance',[Reflection.BindingFlags]'NonPublic')
if(!$game){$game=$t.GetField('activeGame',$flags).FieldType}
$t.GetField('activeGame',$flags).SetValue($shell,[Activator]::CreateInstance($game,$true))
$t.GetField('gameAssets',$flags).SetValue($shell,$gameDir)
$t.GetField('gameExe',$flags).SetValue($shell,$gameExe)
Check
Assert ((Summary) -eq '检测发现问题，详情见日志。' -and !(Test-Path $gameExe)) 'missing game payload reported without writing into the game'
$sample=New-Object byte[] 128
$sample[0]=0x4d;$sample[1]=0x5a;$sample[60]=80;$sample[80]=0x50;$sample[81]=0x45;$sample[84]=0x64;$sample[85]=0x86
[IO.File]::WriteAllBytes($gameExe,$sample)
Check
Assert ((Summary) -eq '检测完成，运行环境正常。') 'installed game manifest and matching executable validated'
Write-Output "Verified $stage"
