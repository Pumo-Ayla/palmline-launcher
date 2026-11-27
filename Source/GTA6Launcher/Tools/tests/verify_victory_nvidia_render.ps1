# Run with Windows PowerShell -STA after a Release net48 build.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework
$project=Resolve-Path (Join-Path $PSScriptRoot '../..')
$a=[Reflection.Assembly]::LoadFrom((Join-Path $project 'bin/Release/net48/Launcher.exe'))
$app=New-Object Windows.Application
$app.Resources['InterfaceFont']=New-Object Windows.Media.FontFamily 'Segoe UI'
$app.Resources.MergedDictionaries.Add([Windows.Markup.XamlReader]::Load($a.GetManifestResourceStream('Controls.xaml')))
$a.GetType('Ui').GetMethod('Apply').Invoke($null,@('zh-CN'))
$view=[Windows.Markup.XamlReader]::Load($a.GetManifestResourceStream('Main.xaml'))
$t=$a.GetType('DesktopLauncher')
$instance=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($t)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
function Set-Field($name,$value){$t.GetField($name,$flags).SetValue($instance,$value)}
function Pick($group,$value){
    $button=$view.FindName($group).Children | Where-Object Tag -eq $value
    if(!$button -or !$button.IsEnabled){throw "Unavailable choice $group/$value"}
    $button.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent))
}
$temp=New-Item -ItemType Directory -Path (Join-Path $env:TEMP ('victory-render-'+[guid]::NewGuid()))
$savedLocal=$env:LOCALAPPDATA
$savedStreamline=$env:VICTORY_STREAMLINE_DIR
try {
    $env:LOCALAPPDATA=$temp.FullName
    $menuDir=New-Item -ItemType Directory -Path (Join-Path $temp 'victory')
    $file=Join-Path $menuDir 'victory-settings.txt'
    [IO.File]::WriteAllLines($file,[string[]]@('VICTORY_MENU_FRAMEGEN=6','VICTORY_EARLY_DEPTH='))
    $model=$a.GetType('VictoryNvidiaMenu')
    $limit=$model.GetMethod('Limit',[Reflection.BindingFlags]'Static,NonPublic')
    foreach($row in @(@('auto','series-x','quality',$true,2,$true,2),
                       @('auto','series-x','quality',$true,0,$true,2),
                       @('fsr','series-s','off',$false,0,$false,0))){
        if($limit.Invoke($null,$row[0..5]) -ne $row[6]){throw 'Hardware or missing-provider limits failed'}
    }
    $menu=$model.GetConstructors($flags)[0].Invoke(@([string]$file))
    $apply=$model.GetMethod('ApplyDlssSupport',$flags)
    $apply.Invoke($menu,@($true))
    if((Get-Content $file) -notcontains 'VICTORY_MENU_DLSS=dlaa'){throw 'Missing supported DLAA default'}
    Set-Field window $view
    $launchType=$a.GetType('LaunchButton')
    $launch=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($launchType)
    Set-Field launchButton $launch
    $available=[Collections.Generic.HashSet[string]]::new()
    $null=$available.Add('victory-nvidia')
    $null=$available.Add('cgbj')
    Set-Field available $available
    Set-Field activeGame ([Runtime.Serialization.FormatterServices]::GetUninitializedObject($t.GetNestedType('GameInstance','NonPublic')))
    foreach($projectId in @('cgbj','victory-nvidia')) {
        Set-Field selected $projectId
        foreach($phase in @('Warming','Ready','Warming')) {
            $launchType.GetProperty('State').GetSetMethod($true).Invoke($launch,@([Enum]::Parse($a.GetType('LaunchState'),$phase)))
            if($t.GetProperty('CanOpenEmulatorSettings',$flags).GetValue($instance) -ne ($phase -eq 'Ready')){throw "Settings gate failed for $projectId/$phase"}
        }
    }
    Set-Field renderEngine 'victory-nvidia'
    Set-Field renderOpen $true
    Set-Field victoryMenu $menu
    Set-Field srAvailable $true
    Set-Field victoryDlssMax 6
    Set-Field victoryFsr $true
    $t.GetMethod('AttachRenderSettings',$flags).Invoke($instance,@())
    $t.GetMethod('ShowVictoryChoices',$flags).Invoke($instance,@())
    $choices=$view.FindName('VictoryDlss').Children
    if($choices.Count -ne 4 -or $choices[0].Tag -ne 'dlaa' -or !$choices[0].IsChecked){throw 'Incorrect DLSS options'}
    $slider=$view.FindName('FgSlider')
    if($slider.Maximum -ne 5 -or $slider.Value -ne 5){throw 'Native 6x was not restored'}
    Pick VictoryType fsr
    if($slider.Maximum -ne 1 -or $slider.Value -ne 1 -or (Get-Content $file) -notcontains 'VICTORY_MENU_FRAMEGEN=2'){throw 'FSR did not clamp to 2x'}
    $apply.Invoke($menu,@($false))
    Set-Field srAvailable $false
    $t.GetMethod('ShowVictoryChoices',$flags).Invoke($instance,@())
    if(($choices | Where-Object { $_.IsEnabled -or $_.IsChecked }) -or (Get-Content $file) -notcontains 'VICTORY_MENU_DLSS=off'){throw 'Unsupported DLSS enabled'}
    if($slider.IsEnabled){throw 'Series X without DLSS cannot run FSR 3'}
    Pick VictoryConsole series-s
    $slider.Value=1
    if(!$slider.IsEnabled -or (Get-Content $file) -notcontains 'VICTORY_EARLY_DEPTH='){throw 'Series S 2x or merge failed'}
    $runtime=Get-ChildItem (Join-Path $project '../Community/victory-nvidia/runtime/*/victory.exe') | Select-Object -First 1
    # play logs its effective native switches before validating the PE. No game is launched.
    $image=New-Item -Type File -Path (Join-Path $temp 'empty.exe')
    $env:VICTORY_STREAMLINE_DIR=(Resolve-Path (Join-Path $project '../Runtime/DLSS/FrameGeneration')).Path
    $p=Start-Process -FilePath $runtime.FullName -ArgumentList ('play "'+$image.FullName+'"') -WindowStyle Hidden -Wait -PassThru
    $log=Get-ChildItem (Join-Path $menuDir 'logs/*.log') | Select-Object -First 1
    $header=Get-Content $log.FullName -TotalCount 1
    foreach($switch in 'console=series-s','VICTORY_MENU_FG_TYPE=fsr','VICTORY_FRAMEGEN_X=2','VICTORY_FSR_FRAMEGEN=1'){
        if(!$header.Contains($switch)){throw "Native play did not consume $switch"}
    }
    Set-Field srAvailable $true
    $apply.Invoke($menu,@($true))
    $t.GetMethod('ShowVictoryChoices',$flags).Invoke($instance,@())
    if(!$choices[0].IsChecked){throw 'Legacy off did not resolve to DLAA'}
    Pick VictoryDlss quality
    $apply.Invoke($menu,@($true))
    if((Get-Content $file) -notcontains 'VICTORY_MENU_DLSS=quality'){throw 'Explicit DLSS selection lost'}
    Pick VictoryType dlss
    foreach($step in 0,1,2,3,4,5) {
        $slider.Value=$step
        $expected=if($step -eq 0){0}else{$step+1}
        if((Get-Content $file) -notcontains "VICTORY_MENU_FRAMEGEN=$expected"){throw "Missing saved multiplier $expected"}
    }
    $p=Start-Process -FilePath $runtime.FullName -ArgumentList ('play "'+$image.FullName+'"') -WindowStyle Hidden -Wait -PassThru
    $header=Get-Content (Join-Path $menuDir ('logs/*-'+$p.Id+'.log')) -TotalCount 1
    if(!$header.Contains('VICTORY_MENU_FG_TYPE=dlss') -or !$header.Contains('VICTORY_FRAMEGEN_X=6')){
        throw 'Native play did not consume DLSS 6x'
    }
    'PASS: native menu readback, all multiplier stops, FSR limits and native play settings.'
} finally {
    $env:LOCALAPPDATA=$savedLocal
    $env:VICTORY_STREAMLINE_DIR=$savedStreamline
    $view.Close()
    $parent=(Resolve-Path $env:TEMP).Path
    if($temp.Parent.FullName -eq $parent -and $temp.Name.StartsWith('victory-render-')){
        Remove-Item -LiteralPath $temp.FullName -Recurse -Force
    }
}
