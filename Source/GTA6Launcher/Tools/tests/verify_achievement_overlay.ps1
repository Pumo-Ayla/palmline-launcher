# Run with Windows PowerShell -STA after a Release net48 build.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,System.Drawing,System.Windows.Forms
$app=New-Object Windows.Application
$project=Resolve-Path (Join-Path $PSScriptRoot '../..')
$a=[Reflection.Assembly]::LoadFrom((Join-Path $project 'bin/Release/net48/Launcher.exe'))
$t=$a.GetType('AchievementToast');$static=[Reflection.BindingFlags]'Static,Public,NonPublic'
$t.GetMethod('Preload',$static).Invoke($null,@())
$icons=$t.GetField('artwork',$static).GetValue($null)
if($icons.Count -ne 55){throw 'Incomplete preloaded assets'}
$toneType=$a.GetType('NoticeTone')
foreach($tone in [Enum]::GetNames($toneType)){
    if($tone -eq 'Silent'){continue}
    $resource=if($tone -eq 'Achievement'){'SteamAchievement.wav'}else{"Notification.$tone.wav"}
    $stream=$a.GetManifestResourceStream($resource)
    if(!$stream){throw "Missing tone $resource"}
    $player=[System.Media.SoundPlayer]::new($stream)
    try {$player.Load(); if(!$player.IsLoadCompleted){throw "Invalid WAV: $resource"}}
    finally {$player.Dispose(); $stream.Dispose()}
}
$lab=Join-Path $env:TEMP ('overlay-test-'+[guid]::NewGuid())
$instance='sample'
$marker=New-Item -Force -Type File (Join-Path $lab 'desktop/play-history/sample/Desktop-1-cgbj.time')
$past=New-Item -Force -Type File (Join-Path $lab 'runs/Desktop-1-cgbj/achievements.log')
Set-Content $past.FullName 'T [CGBJ achievement] unlocked achievementId=1'
$kindType=$a.GetType('LauncherNoticeKind')
$soundCallbackType=[Action[int]].GetGenericTypeDefinition().MakeGenericType($kindType)
$events=[Collections.Generic.List[string]]::new()
$callback=[System.Management.Automation.LanguagePrimitives]::ConvertTo({param($kind) $events.Add($kind.ToString())},$soundCallbackType)
$session=$a.GetType('AchievementToast+Session').GetConstructors()[0].Invoke(@([string]$lab,[string]$instance,[Action[string]]{param($s) Write-Host $s},$callback))
try {
    $pending=$t.GetField('arrivals',$static).GetValue($null)
    $session.Observe('T [CGBJ achievement] unlocked achievementId=1')
    $session.Observe('T [CGBJ achievement] progress-update-request achievementId=14')
    if($pending.Count){throw 'Historical or unconfirmed event replayed'}
    $progress='T [BELOBUG achievement] local-progress {"achievementId":"14","percentComplete":75}'
    $unlock='T [BELOBUG achievement] unlocked achievementId=14'
    $session.Observe($progress)
    if($pending['14'] -ne 75){throw 'Progress lost'}
    $session.Observe($unlock)
    if($pending['14'] -ne 100){throw 'Unlock lost'}
    $pending.Clear()
    $session.Observe($progress);$session.Observe($unlock)
    if($pending.Count){throw 'Repeated achievement replayed'}
    $session.Observe('T [VICTORY-NVIDIA achievement] progress-update-request achievementId=34')
    if($pending.Count){throw 'NVIDIA request alone became progress'}
    $session.Observe('T [VICTORY-NVIDIA achievement] local-progress {"achievementId":"34","percentComplete":8}')
    if($pending['34'] -ne 8){throw 'NVIDIA progress missing'}
    $session.Observe('T [VICTORY-NVIDIA achievement] local-progress {"achievementId":"34","percentComplete":100}')
    if($pending['34'] -ne 100){throw 'NVIDIA completion missing'}
    $pending.Clear()
    $ctor=$t.GetConstructors([Reflection.BindingFlags]'Instance,NonPublic') | Where-Object {$_.GetParameters()[0].ParameterType -eq [string]}
    $instanceFields=[Reflection.BindingFlags]'Instance,NonPublic'
    $catalog=$t.GetField('catalog',$static).GetValue($null)
    foreach($id in $icons.Keys){
        $card=$ctor.Invoke(@($id,[int]65,[double]0))
        $description=$t.GetField('description',$instanceFields).GetValue($card)
        if(!$description -or $description -ne $catalog[$id].Description){throw "Missing catalog description for $id"}
        $b=New-Object Drawing.Bitmap 720,120; $g=[Drawing.Graphics]::FromImage($b)
        try { $t.GetMethod('PaintRow',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($card,@($g,[single]1,[double]2)) }
        finally { $g.Dispose();$b.Dispose();$t.GetMethod('Remove',$static).Invoke($null,@($card)) }
    }
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ToastWindowTest {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd,int index);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd,int msg,IntPtr w,IntPtr l);
}
'@
    $dpi=[ToastWindowTest]::SetThreadDpiAwarenessContext([IntPtr](-4))
    $focus=[ToastWindowTest]::GetForegroundWindow()
    $admit=$t.GetMethod('Admit',$static)
    $admit.Invoke($null,@('34',[int]8,[double]0))
    $rowType=$a.GetType('AchievementToast+Row')
    $typed=[Array]::CreateInstance($rowType,1)
    foreach($width in @(1280,3840,1920)){
        $area=[Drawing.Rectangle]::new(-12000,240,$width,[int]($width*9/16))
        $rows=$t.GetMethod('Rows',$static).Invoke($null,@($area.Width,$area.Height,[double]1,[int]0))
        foreach($r in $rows){$typed.SetValue($r,0)}
        $t.GetMethod('PresentWithSound',$static).Invoke($null,@($area,$typed,[double]1,$callback))
        $card=$t.GetField('visible',$static).GetValue($null)[0]
        $popup=$t.GetField('window',$instanceFields).GetValue($card)
        $hwnd=$popup.Handle
        $rect=New-Object ToastWindowTest+Rect
        if(![ToastWindowTest]::GetWindowRect($hwnd,[ref]$rect)){throw 'Missing desktop HWND'}
        if($rect.Left -ne $area.Left+$r.Bounds.X -or $rect.Top -ne $area.Top+$r.Bounds.Y -or
            $rect.Right-$rect.Left -ne $r.Bounds.Width -or $rect.Bottom-$rect.Top -ne $r.Bounds.Height){throw 'Desktop card misplaced or clipped'}
        $styles=[ToastWindowTest]::GetWindowLong($hwnd,-20)
        if([ToastWindowTest]::GetParent($hwnd) -ne [IntPtr]::Zero -or ($styles -band 0x80800a8) -ne 0x80800a8){throw 'Incorrect topmost desktop styles'}
        if([ToastWindowTest]::GetForegroundWindow() -ne $focus -or [ToastWindowTest]::SendMessage($hwnd,0x84,[IntPtr]::Zero,[IntPtr]::Zero).ToInt64() -ne -1){throw 'Overlay intercepted focus or hit testing'}
    }
    $surface=$popup.GetType().GetField('surface',$instanceFields).GetValue($popup)
    $w=$surface.Width-1; $h=$surface.Height-1
    foreach($x in 0,$w){foreach($y in 0,$h){
        if($surface.GetPixel($x,$y).A -ne 0){throw 'Opaque corner leaked outside the card'}
    }}
    if($surface.GetPixel(15,15).A -ne 255){throw 'Missing card interior'}
    if(($events -join ',') -ne 'Progress'){throw "Progress replayed during repaint: $events"}
    $admit.Invoke($null,@('34',[int]100,[double]2))
    foreach($now in @(3,4)){
        $rows=$t.GetMethod('Rows',$static).Invoke($null,@($area.Width,$area.Height,[double]$now,[int]0))
        foreach($r in $rows){$typed.SetValue($r,0)}
        $t.GetMethod('PresentWithSound',$static).Invoke($null,@($area,$typed,[double]$now,$callback))
    }
    if(($events -join ',') -ne 'Progress,Unlocked'){throw "Incorrect unlock notifications: $events"}
    $noticeCtor=$t.GetConstructors($instanceFields) | Where-Object {$_.GetParameters()[0].ParameterType -eq $kindType}
    foreach($name in 'Installed','Preloading','Ready','Started','ControllerConnected','ControllerDisconnected'){
        $kind=[Enum]::Parse($kindType,$name)
        $card=$noticeCtor.Invoke(@($kind,[double]0,'Sample controller'))
        $r=[Activator]::CreateInstance($rowType)
        $r.Card=$card; $r.Bounds=[Drawing.Rectangle]::new(20,20,358,68); $r.Alpha=255
        try {
            $events.Clear()
            foreach($height in 0,50,68,68){
                $r.Reveal=$height; $typed.SetValue($r,0)
                $t.GetMethod('PresentWithSound',$static).Invoke($null,@($area,$typed,[double]1,$callback))
                if(!$height -and $events.Count){throw "Hidden $name played a cue"}
            }
            if(($events -join ',') -ne $name){throw "$name rendered with incorrect sound events: $events"}
        } finally {
            foreach($field in 'window','titleLine','detailLine'){
                $owned=$t.GetField($field,$instanceFields).GetValue($card)
                if($owned){$owned.Dispose()}
            }
        }
    }
    [string]$prefs=Join-Path $lab 'display.xml'
    $display=$a.GetType('ProjectDisplaySettings')
    $settings=$display.GetMethod('Load').Invoke($null,@($prefs))
    $ready=[Enum]::Parse($kindType,'Ready')
    $progressEvent=[Enum]::Parse($kindType,'Progress')
    if($settings.Sounds.Item($progressEvent).ToString() -ne 'Silent' -or $settings.Sounds.Item($ready).ToString() -ne 'Rise'){throw 'Wrong defaults'}
    $settings.Sounds.set_Item($ready,[Enum]::Parse($toneType,'Tap'))
    $settings.Sounds.Enabled=$false
    $settings.Save($prefs)
    $restored=$display.GetMethod('Load').Invoke($null,@($prefs))
    if($restored.Sounds.Enabled -or $restored.Sounds.Item($ready).ToString() -ne 'Tap'){throw 'Sound preferences lost'}
    'PASS: embedded sounds, settings round-trip, single cue per visible update, 55 cards, desktop bounds and input.'
} finally {
    $session.Dispose()
    if($dpi -and $dpi -ne [IntPtr]::Zero){$null=[ToastWindowTest]::SetThreadDpiAwarenessContext($dpi)}
    $past.Delete(); $marker.Delete()
}
