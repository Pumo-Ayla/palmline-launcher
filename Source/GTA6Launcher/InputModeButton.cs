using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

internal sealed class InputModeButton
{
    const int HoldIntentMs=300, HoldMs=720;
    readonly Button button;
    readonly FrameworkElement gamepad, keyboard, selection, hover;
    readonly Ellipse ring;
    readonly DispatcherTimer hold=new DispatcherTimer(DispatcherPriority.Input);
    readonly Stopwatch pressedFor=new Stopwatch();
    readonly Action<bool> modeChanged;
    bool keyboardMode, inViewer, pressed, switched, byMouse;

    public bool KeyboardMode { get { return keyboardMode; } }
    public InputModeButton(Window window, Action togglePage, Action<bool> modeChanged) {
        this.modeChanged=modeChanged;
        button=(Button)window.FindName("ControllerArea");
        gamepad=(FrameworkElement)window.FindName("PadGlyph");
        keyboard=(FrameworkElement)window.FindName("KeyboardGlyph");
        selection=(FrameworkElement)window.FindName("ViewerDisc");
        hover=(FrameworkElement)window.FindName("ModeHover");
        ring=(Ellipse)window.FindName("ModeRing");
        button.Click+=delegate { togglePage(); };
        button.PreviewMouseLeftButtonDown+=delegate(object s,MouseButtonEventArgs e) {
            button.Focus();
            if(button.CaptureMouse()) Begin(true);
            e.Handled=true;
        };
        button.PreviewMouseLeftButtonUp+=delegate(object s,MouseButtonEventArgs e) {
            if(!pressed || !byMouse) return;
            bool tap=button.IsMouseOver && FinishPress();
            Cancel(); if(tap) togglePage(); e.Handled=true;
        };
        button.MouseEnter+=delegate { if(!inViewer) hover.Visibility=Visibility.Visible; };
        button.MouseLeave+=delegate { if(byMouse) Cancel(); };
        button.LostMouseCapture+=delegate { if(byMouse) Cancel(); };
        button.PreviewKeyDown+=delegate(object s,KeyEventArgs e) {
            if(e.Key!=Key.Space) return;
            if(!e.IsRepeat) Begin(false);
            e.Handled=true;
        };
        button.PreviewKeyUp+=delegate(object s,KeyEventArgs e) {
            if(!pressed || byMouse || e.Key!=Key.Space) return;
            bool tap=FinishPress(); Cancel(); if(tap) togglePage(); e.Handled=true;
        };
        button.LostKeyboardFocus+=delegate { if(!byMouse) Cancel(); };
        window.Deactivated+=delegate { Cancel(); };
        window.Closed+=delegate { Cancel(); };
        hold.Tick+=delegate { AdvanceHold(); };
        Update();
    }
    public void SetViewer(bool value) {
        Cancel(); inViewer=value;
        hover.Visibility=Visibility.Collapsed;
        ControllerMotion.Fade(selection,selection.Opacity,value ? 1 : 0,value ? 170 : 100);
        ControllerMotion.Reveal(keyboardMode ? keyboard : gamepad,value ? .87 : 1.07,value ? 1.07 : .975,0,240,.93);
        Update();
    }
    public void RefreshLanguage() { Update(); }
    void Update() {
        string open=Ui.Text(inViewer ? "返回主页" : keyboardMode ? "打开键鼠映射" : "查看手柄");
        string title=Ui.Text(keyboardMode ? "键鼠模式" : "手柄模式")+" · "+open;
        AutomationProperties.SetName(button,title);
        AutomationProperties.SetHelpText(button,Ui.Text(inViewer ? "单击返回主页，长按切换输入模式" :
            keyboardMode ? "单击打开键鼠映射，长按切换输入模式" : "单击查看手柄，长按切换输入模式"));
    }
    void Begin(bool mouse) {
        Cancel(); byMouse=mouse; pressed=true;
        pressedFor.Restart();
        hold.Interval=TimeSpan.FromMilliseconds(HoldIntentMs);
        hold.Start();
    }
    void AdvanceHold() {
        hold.Stop();
        if(!pressed || switched) return;
        double elapsed=pressedFor.Elapsed.TotalMilliseconds;
        if(elapsed>=HoldMs) { SwitchMode(); return; }
        double remaining=(elapsed<HoldIntentMs ? HoldIntentMs : HoldMs)-elapsed;
        if(elapsed>=HoldIntentMs && ring.Visibility!=Visibility.Visible) {
            ring.Visibility=Visibility.Visible;
            double offset=105*remaining/(HoldMs-HoldIntentMs);
            ring.BeginAnimation(Shape.StrokeDashOffsetProperty,new DoubleAnimation(offset,0,TimeSpan.FromMilliseconds(remaining)));
        }
        hold.Interval=TimeSpan.FromMilliseconds(Math.Max(1,remaining));
        hold.Start();
    }
    bool FinishPress() {
        double elapsed=pressedFor.Elapsed.TotalMilliseconds;
        if(!switched && elapsed>=HoldMs) SwitchMode();
        return !switched && elapsed<HoldIntentMs;
    }
    void SwitchMode() {
        switched=true; keyboardMode=!keyboardMode;
        ring.Visibility=Visibility.Collapsed;
        var incoming=keyboardMode ? keyboard : gamepad;
        var outgoing=keyboardMode ? gamepad : keyboard;
        incoming.Visibility=Visibility.Visible;
        ControllerMotion.Dismiss(outgoing,.74,85,0,delegate {
            if(outgoing!=(keyboardMode ? keyboard : gamepad)) outgoing.Visibility=Visibility.Collapsed;
        });
        ControllerMotion.Reveal(incoming,.71,1.12,0,300,.1);
        Update();
        modeChanged(keyboardMode);
    }
    void Cancel() {
        hold.Stop();
        pressedFor.Reset();
        bool release=byMouse;
        pressed=switched=byMouse=false;
        ring.BeginAnimation(Shape.StrokeDashOffsetProperty,null);
        ring.Visibility=Visibility.Collapsed;
        if(release && button.IsMouseCaptured) button.ReleaseMouseCapture();
    }
}
