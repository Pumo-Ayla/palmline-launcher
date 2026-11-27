using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

internal sealed class GamepadStatus
{
    readonly GamepadInput input = new GamepadInput();
    readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Input);
    readonly Stopwatch discovery = new Stopwatch();
    readonly Window window;
    readonly ControllerViewer viewer;
    readonly Grid page;
    readonly Button rail, recenter;
    readonly Ellipse light;
    readonly TextBlock caption, paddles;
    readonly string[] paddleLabels={"右背键 1","左背键 1","右背键 2","左背键 2"};
    uint lastPaddles;
    int count;
    uint shownDevice;
    string lastHelp;
    bool live, keyboard, configured;

    public GamepadStatus(Window owner, ControllerViewer controller) {
        window=owner; viewer=controller;
        page=(Grid)owner.FindName("ControllerHost");
        rail=(Button)owner.FindName("ControllerArea");
        light=(Ellipse)owner.FindName("ControllerLight");
        caption=(TextBlock)owner.FindName("LivePadInfo");
        paddles=(TextBlock)owner.FindName("PaddleInput");
        recenter=(Button)owner.FindName("RecenterPad");
        recenter.Click+=delegate { input.Pump(); input.Orientation.Reset(); viewer.Recenter(); Refresh(); };
        timer.Tick+=delegate { Refresh(); };
        window.Loaded+=delegate { Schedule(); };
        window.StateChanged+=delegate { Schedule(); };
        page.IsVisibleChanged+=delegate { Schedule(); };
        window.Closed+=delegate { timer.Stop(); input.Dispose(); };
    }
    public void SetMode(bool keyboardMode,bool hasProfile) {
        if(keyboard==keyboardMode && configured==hasProfile) return;
        bool changed=keyboard!=keyboardMode;
        keyboard=keyboardMode; configured=hasProfile;
        if(changed && keyboard) {
            input.ClosePad(); shownDevice=0; lastPaddles=0; paddles.Text="";
            viewer.Sync(new PadState(),input.Orientation.Pose); viewer.ResetView();
        }
        lastHelp=null;
        Schedule();
    }
    public void RefreshLanguage() {
        lastHelp=null; lastPaddles=uint.MaxValue;
        Schedule();
    }
    void Schedule() {
        timer.Stop();
        if(keyboard) {
            live=false;
            light.Fill=configured ? Brushes.LightGreen : Brushes.SandyBrown;
            AutomationProperties.SetItemStatus(rail,Ui.Text(configured ? "已配置键鼠映射" : "暂未配置"));
            return;
        }
        live=page.IsVisible && window.WindowState!=WindowState.Minimized;
        if(!live) {
            input.ClosePad();
            shownDevice=0;
            viewer.ResetView();
            viewer.Sync(new PadState(),input.Orientation.Pose);
        }
        if(!window.IsLoaded || window.WindowState==WindowState.Minimized) return;
        discovery.Reset();
        Refresh();
        timer.Interval=TimeSpan.FromMilliseconds(live ? 16 : 1100);
        timer.Start();
    }
    void Refresh() {
        if(keyboard) return;
        input.Pump();
        if(!discovery.IsRunning || discovery.ElapsedMilliseconds>=950) {
            count=input.Discover(live);
            discovery.Restart();
        }
        bool connected=live ? input.Name!=null : count>0;
        string help=!input.Available ? "手柄输入不可用" : connected ? "手柄已连接" : "未检测到手柄";
        if(help!=lastHelp) {
            light.Fill=!input.Available ? Brushes.Silver : connected ? Brushes.LightGreen : Brushes.IndianRed;
            AutomationProperties.SetItemStatus(rail,Ui.Text(help));
            lastHelp=help;
        }
        if(!live) return;
        if(shownDevice!=input.DeviceId) { viewer.ResetView(); shownDevice=input.DeviceId; }
        var state=input.Read();
        viewer.Sync(state,input.Orientation.Pose);
        uint pressed=(state.Buttons>>16)&15;
        if(pressed!=lastPaddles) {
            var names=new System.Collections.Generic.List<string>(4);
            for(int i=0;i<4;i++) if((pressed&(1u<<i))!=0) names.Add(Ui.Text(paddleLabels[i]));
            paddles.Text=string.Join("  ·  ",names);
            lastPaddles=pressed;
        }
        string text=Ui.Text(connected ? input.Name : "未连接手柄");
        if(caption.Text!=text) caption.Text=text;
        recenter.Visibility=input.GyroEnabled ? Visibility.Visible : Visibility.Collapsed;
    }
}
