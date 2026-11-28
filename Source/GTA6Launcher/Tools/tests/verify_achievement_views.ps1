# Verify the real WPF shelf with isolated history, including interrupted view transitions.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework
$asm=[Reflection.Assembly]::LoadFrom((Resolve-Path (Join-Path $PSScriptRoot '../../bin/Release/net48/Launcher.exe')))
$app=[Windows.Application]::new()
$app.ShutdownMode='OnExplicitShutdown'
$app.Resources['InterfaceFont']=[Windows.Media.FontFamily]::new('Segoe UI')
function Read-View([string]$name) {
    $stream=$asm.GetManifestResourceStream($name)
    try { [Windows.Markup.XamlReader]::Load($stream) } finally { $stream.Dispose() }
}
function Wait-Frame([int]$ms){
    $frame=[Windows.Threading.DispatcherFrame]::new()
    $timer=[Windows.Threading.DispatcherTimer]::new()
    $timer.Interval=[TimeSpan]::FromMilliseconds($ms)
    $timer.Add_Tick({$timer.Stop(); $frame.Continue=$false}.GetNewClosure())
    $timer.Start(); [Windows.Threading.Dispatcher]::PushFrame($frame)
}
$app.Resources.MergedDictionaries.Add((Read-View 'Controls.xaml'))
$asm.GetType('Ui').GetMethod('Apply').Invoke($null,@('en-US'))
$w=Read-View 'Main.xaml'
$type=$asm.GetType('DesktopLauncher')
$flags=[Reflection.BindingFlags]'NonPublic,Instance'
$shell=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($type)
$type.GetField('window',$flags).SetValue($shell,$w)
$type.GetField('caseMotionTime',$flags).SetValue($shell,[Diagnostics.Stopwatch]::new())
$game=[Activator]::CreateInstance($asm.GetType('DesktopLauncher+GameInstance'))
$game.Id='view-test'; $game.GtaVi=$true
$type.GetField('activeGame',$flags).SetValue($shell,$game)
$type.GetMethod('SetupAchievementCase',$flags).Invoke($shell,@())
$history=[Activator]::CreateInstance($asm.GetType('AchievementJournal+History'))
$render=$type.GetMethod('ShowAchievementRows',$flags)
$view=$w.FindName('ShelfContent')
$list=$w.FindName('AchievementRows'); $grid=$w.FindName('AchievementOverview')
$toggle=$w.FindName('AchievementViewToggle')
$view.Parent.Children.Remove($view); $view.Width=372
$w.Content=$view; $w.SizeToContent='WidthAndHeight'; $w.Left=-12000; $w.Top=-12000
$w.ShowActivated=$false; $w.ShowInTaskbar=$false; $w.WindowStartupLocation='Manual'
function Toggle-View { $toggle.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent)) }
try {
    $w.Show(); $render.Invoke($shell,@($history)); Wait-Frame 60
    if($list.Items.Count -ne 0 -or $grid.Visibility -ne 'Collapsed'){throw 'Incorrect initial list'}
    Toggle-View; Wait-Frame 260
    $catalog=$asm.GetType('AchievementJournal').GetField('Catalog',[Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
    $ids=@($grid.Items | ForEach-Object {[int]($_.Detail.Split(' ')[0].Substring(1))})
    $expected=@($catalog | ForEach-Object {[int]$_.Id} | Sort-Object)
    if(($ids -join ',') -ne ($expected -join ',') -or @($grid.Items | Where-Object Earned).Count){throw 'Incorrect locked catalog or ID order'}
    $history.Progress['14']=55; $history.Progress['6']=100; $history.Progress['1000']=100
    $render.Invoke($shell,@($history)); Wait-Frame 60
    if($list.Items.Count -ne 3 -or @($grid.Items | Where-Object Earned).Count -ne 2){throw 'Lists disagree with history'}
    foreach($id in 6,14,1000) {
        $presenter=$grid.ItemContainerGenerator.ContainerFromIndex([Array]::IndexOf($ids,$id))
        $square=$grid.ItemTemplate.FindName('square',$presenter)
        if($square.ActualWidth -ne $square.ActualHeight -or $square.ActualWidth -le 0){throw "Invalid square for $id"}
        if($id -eq 14) {
            if($square.Background.Color.ToString() -ne '#FF393240'){throw 'Partial progress lit up'}
        } else {
            $color=$square.Background.GradientStops[0].Color.ToString()
            $expectedColor=if($id -eq 6){'#FFD6A342'}else{'#FF91BDE6'}
            if($color -ne $expectedColor){throw "Incorrect trophy color for $id"}
        }
    }
    if($toggle.ToolTip -ne 'Switch to achievement list'){throw 'Incorrect overview action'}
    Toggle-View; Wait-Frame 40; Toggle-View; Wait-Frame 35; Toggle-View; Wait-Frame 300
    if($grid.Visibility -ne 'Collapsed' -or !$list.IsVisible -or !$list.IsEnabled -or $list.Opacity -ne 1 -or $toggle.ToolTip -ne 'Switch to achievement overview'){throw 'Interrupted transition left stale UI'}
    Write-Host "PASS: $($ids.Count) squares, numeric ID order, empty and partial progress, trophy colors and rapid view switching."
} finally { $w.Close() }
