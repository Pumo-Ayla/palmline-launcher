# Run with Windows PowerShell -STA after a Release net48 build.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,System.Drawing,System.Windows.Forms
$app=New-Object Windows.Application
$project=Resolve-Path (Join-Path $PSScriptRoot '../..')
$a=[Reflection.Assembly]::LoadFrom((Join-Path $project 'bin/Release/net48/Launcher.exe'))
$t=$a.GetType('AchievementToast');$static=[Reflection.BindingFlags]'Static,Public,NonPublic'
$t.GetMethod('Preload',$static).Invoke($null,@())
$icons=$t.GetField('artwork',$static).GetValue($null)
if($icons.Count -ne 55 -or !$t.GetField('chime',$static).GetValue($null).IsLoadCompleted){throw 'Incomplete preloaded assets'}
$lab=Join-Path $env:TEMP ('overlay-test-'+[guid]::NewGuid())
$instance='sample'
$marker=New-Item -Force -Type File (Join-Path $lab 'desktop/play-history/sample/Desktop-1-cgbj.time')
$past=New-Item -Force -Type File (Join-Path $lab 'runs/Desktop-1-cgbj/achievements.log')
Set-Content $past.FullName 'T [CGBJ achievement] unlocked achievementId=1'
$session=$a.GetType('AchievementToast+Session').GetConstructors()[0].Invoke(@([string]$lab,[string]$instance,[Action[string]]{param($s) Write-Host $s}))
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
    $ctor=$t.GetConstructors([Reflection.BindingFlags]'Instance,NonPublic')[0]
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
        $rows=$t.GetMethod('Rows',$static).Invoke($null,@($area.Width,$area.Height,[double]1))
        foreach($r in $rows){$typed.SetValue($r,0)}
        $a.GetType('AchievementToast+Toast').GetMethod('Present').Invoke($null,@($area,$typed,[double]1))
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
    'PASS: preload, sound, deduplication, 55 card descriptions, desktop bounds, styles, focus and hit testing.'
} finally {
    $session.Dispose()
    if($dpi -and $dpi -ne [IntPtr]::Zero){$null=[ToastWindowTest]::SetThreadDpiAwarenessContext($dpi)}
    $past.Delete(); $marker.Delete()
}
