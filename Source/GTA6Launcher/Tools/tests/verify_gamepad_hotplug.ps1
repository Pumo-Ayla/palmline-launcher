# Windows PowerShell x64, -STA. Virtual devices exist only inside this test process.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework
$app=[Windows.Application]::new()
$asm=[Reflection.Assembly]::LoadFrom((Resolve-Path (Join-Path $PSScriptRoot '../../bin/Release/net48/Launcher.exe')))
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class TestPads {
    [StructLayout(LayoutKind.Explicit,Size=136)]
    struct Desc {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public ushort Type;
        [FieldOffset(12)] public ushort Axes;
        [FieldOffset(14)] public ushort Buttons;
        [FieldOffset(40)] public IntPtr Name;
    }
    [DllImport("SDL3.dll")] static extern uint SDL_AttachVirtualJoystick(ref Desc desc);
    [DllImport("SDL3.dll")] static extern byte SDL_DetachVirtualJoystick(uint id);
    public static uint Attach(string name) {
        var d=new Desc { Size=136,Type=1,Axes=6,Buttons=16,Name=Marshal.StringToHGlobalAnsi(name) };
        try {
            uint id=SDL_AttachVirtualJoystick(ref d);
            if(id==0) throw new Exception("Virtual device attach failed");
            return id;
        } finally { Marshal.FreeHGlobal(d.Name); }
    }
    public static void Detach(uint id) {
        if(SDL_DetachVirtualJoystick(id)==0) throw new Exception("Virtual device detach failed");
    }
}
'@
$initial=[Activator]::CreateInstance($asm.GetType('GamepadInput'))
if(!$initial.Available){throw 'Bundled SDL3 is unavailable'}
$first=[TestPads]::Attach('Relay pad A')
$window=[Windows.Window]::new()
[Windows.NameScope]::SetNameScope($window,[Windows.NameScope]::new())
$window.Width=100; $window.Height=100; $window.Left=-12000; $window.Top=-12000
$window.ShowActivated=$false; $window.ShowInTaskbar=$false
$root=[Windows.Controls.Grid]::new(); $window.Content=$root
$controls=@{ControllerHost='Grid';ControllerArea='Button';LivePadInfo='TextBlock';PaddleInput='TextBlock';RecenterPad='Button'}
foreach($entry in $controls.GetEnumerator()){
    $control=New-Object ('Windows.Controls.'+$entry.Value)
    $window.RegisterName($entry.Key,$control); $root.Children.Add($control) | Out-Null
}
$window.RegisterName('ControllerLight',[Windows.Shapes.Ellipse]::new())
$viewport=[Windows.Controls.Viewport3D]::new(); $root.Children.Add($viewport) | Out-Null
$viewer=[Activator]::CreateInstance($asm.GetType('ControllerViewer'),@($root,$viewport))
$actual=[Collections.Generic.List[string]]::new()
$kind=$asm.GetType('LauncherNoticeKind')
$callbackType=[Action[int,string]].GetGenericTypeDefinition().MakeGenericType($kind,[string])
$callback=[Management.Automation.LanguagePrimitives]::ConvertTo({
    param($eventKind,$name)
    $actual.Add("$eventKind/$name")
},$callbackType)
$status=[Activator]::CreateInstance($asm.GetType('GamepadStatus'),@($window,$viewer,$callback))
$refresh=$status.GetType().GetMethod('Refresh',[Reflection.BindingFlags]'Instance,NonPublic')
try {
    $window.Show()
    $refresh.Invoke($status,@())
    if($actual.Count){throw "Existing controller announced on startup: $actual"}
    $status.SetMode($true,$false)
    $second=[TestPads]::Attach('Relay pad B')
    $refresh.Invoke($status,@())
    $status.SetMode($false,$false)
    $window.WindowState='Minimized'
    $timer=$status.GetType().GetField('timer',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($status)
    if(!$timer.IsEnabled){throw 'Background hotplug monitoring stopped'}
    [TestPads]::Detach($first)
    $refresh.Invoke($status,@())
    [TestPads]::Detach($second)
    $reconnected=[TestPads]::Attach('Relay pad A')
    $refresh.Invoke($status,@())
    $window.WindowState='Normal'
    $status.RefreshLanguage(); $refresh.Invoke($status,@())
    [TestPads]::Detach($reconnected)
    $refresh.Invoke($status,@()); $refresh.Invoke($status,@())
    $expected='ControllerConnected/Relay pad B,ControllerDisconnected/Relay pad A,ControllerDisconnected/Relay pad B,ControllerConnected/Relay pad A,ControllerDisconnected/Relay pad A'
    if(($actual -join ',') -ne $expected){throw "Incorrect hotplug sequence: $actual"}
    Write-Host 'PASS: baseline, per-device names, keyboard mode, minimized polling, reconnect and deduplication.'
} finally { $window.Close(); $initial.Dispose() }
