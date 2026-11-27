using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

internal enum LaunchState { Warming, Ready, Starting, Rendering, Playing, Stopping, WarmupFailed, GameFailed, CacheCleared, Idle }

internal sealed class LaunchButton
{
    readonly Window window;
    readonly Button play;
    readonly Grid pill;
    string measuredLanguage;
    int warmCount=-1, warmTotal;
    int cachedShaders=-1;
    string warmStage="正在准备着色器";
    readonly Border warmHover, detailCard, launchMenu;
    readonly TextBlock detailStage, detailShaders, detailPipelines;
    bool detailOpen;

    public void WarmStage(string stage) {
        if(State!=LaunchState.Warming) return;
        if(warmStage!=stage || warmCount>=0) measuredLanguage=null;
        warmStage=stage;
        warmCount=-1;
        warmTotal=0;
        RefreshText();
    }

    public void WarmProgress(int count,int total=0) {
        if(State!=LaunchState.Warming || (count==warmCount && total==warmTotal)) return;
        warmCount=count;
        warmTotal=total;
        if(count>=0 && total==0) cachedShaders=count;
        measuredLanguage=null;
        RefreshText();
    }
    readonly SolidColorBrush warmBrush;
    readonly SolidColorBrush readyBrush=new SolidColorBrush(Color.FromRgb(120,227,158));
    readonly SolidColorBrush failureBrush=new SolidColorBrush(Color.FromRgb(255,86,98));
    readonly RunningColors runningColors;
    readonly Brush sunset;
    readonly SolidColorBrush exitTint=new SolidColorBrush(Color.FromRgb(255,86,98)) { Opacity=0 };
    readonly StackPanel playFace;
    readonly Grid busyIcon;
    readonly UIElement ring;
    readonly TranslateTransform faceShift=new TranslateTransform();
    bool pointerHeldOverLaunch;
    double tintTarget;
    Point launchPointer;
    readonly TextBlock text, warmText, exitText;
    readonly Grid captionSpace;
    readonly Dictionary<string,double> captionWidths=new Dictionary<string,double>();
    double centeredAt=double.NaN;

    readonly UIElement glyph, stop, warning, retry;
    readonly UIElement[] icons;
    readonly StackPanel bars;
    UIElement visibleGlyph;
    readonly RotateTransform spinner;
    static readonly DoubleAnimationBase zoomIn=SpringFrom(.84);
    static readonly DoubleAnimation zoomOut=Tween(1,.84,170,new SineEase { EasingMode=EasingMode.EaseInOut });
    static readonly DoubleAnimation fadeIn=Tween(0,1,180,new QuadraticEase { EasingMode=EasingMode.EaseOut });
    static readonly DoubleAnimation fadeOut=Tween(1,0,180,new QuadraticEase { EasingMode=EasingMode.EaseOut });

    static DoubleAnimationBase SpringFrom(double scale) {
        var motion=ControllerMotion.Spring(scale,1.035,1,235);
        motion.FillBehavior=FillBehavior.HoldEnd;
        motion.Freeze();
        return motion;
    }

    static DoubleAnimation Tween(double from,double to,int ms,IEasingFunction easing) {
        var motion=new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(ms)) { EasingFunction=easing };
        motion.Freeze();
        return motion;
    }

    static DoubleAnimation StartFrom(DoubleAnimation motion,double value) {
        if(motion.From==value) return motion;
        var resumed=motion.Clone();
        resumed.From=value;
        return resumed;
    }

    readonly SolidColorBrush menu=new SolidColorBrush();
    public bool CanStop { get { return State==LaunchState.Rendering || State==LaunchState.Playing; } }
    public LaunchState State { get; private set; }
    public bool Failed { get { return State==LaunchState.GameFailed || State==LaunchState.WarmupFailed; } }
    public string FailureReason { get; set; }
    bool KeyboardAction { get { return play.IsKeyboardFocused && Equals(play.TryFindResource("KeyboardCueVisibility"),Visibility.Visible); } }

    public LaunchButton(Window window) {
        this.window=window;
        play=(Button)window.FindName("Play");
        pill=(Grid)window.FindName("LaunchPill");
        warmBrush=(SolidColorBrush)play.Background;
        play.BorderBrush=exitTint;
        sunset=(Brush)window.FindResource("sunset");
        runningColors=new RunningColors(new SolidColorBrush(readyBrush.Color),sunset);
        playFace=(StackPanel)window.FindName("PlayFace");
        ring=(UIElement)window.FindName("GlyphRing");
        busyIcon=(Grid)window.FindName("BusyIcon");
        text=(TextBlock)window.FindName("PlayText");
        warmText=(TextBlock)window.FindName("WarmText");
        exitText=(TextBlock)window.FindName("ExitText");
        captionSpace=(Grid)window.FindName("CaptionSpace");
        warmHover=(Border)window.FindName("WarmHoverArea");
        detailCard=(Border)window.FindName("WarmDetailCard");
        launchMenu=(Border)window.FindName("LaunchMenu");
        detailStage=(TextBlock)window.FindName("WarmDetailStage");
        detailShaders=(TextBlock)window.FindName("WarmDetailShaders");
        detailPipelines=(TextBlock)window.FindName("WarmDetailPipelines");
        warmHover.MouseEnter+=delegate { UpdateDetailVisibility(); };
        warmHover.MouseLeave+=delegate { UpdateDetailVisibility(); };
        warmHover.IsVisibleChanged+=delegate { UpdateDetailVisibility(); };
        launchMenu.IsVisibleChanged+=delegate { UpdateDetailVisibility(); };
        window.Activated+=delegate { UpdateDetailVisibility(); };
        window.Deactivated+=delegate { SetDetailOpen(false); };

        glyph=(System.Windows.Shapes.Path)window.FindName("PlayGlyph");
        stop=(UIElement)window.FindName("StopGlyph");
        bars=(StackPanel)window.FindName("RunningGlyph");
        visibleGlyph=busyIcon;
        warning=(UIElement)window.FindName("WarningGlyph");
        retry=(UIElement)window.FindName("RetryGlyph");
        icons=new UIElement[]{glyph,stop,bars,busyIcon,warning,retry};
        foreach(var icon in icons) {
            icon.RenderTransformOrigin=new Point(.5,.5);
            double size=icon==busyIcon ? 1 : .84;
            icon.RenderTransform=new ScaleTransform(size,size);
        }
        playFace.RenderTransform=faceShift;
        foreach(UIElement bar in bars.Children) {
            bar.RenderTransformOrigin=new Point(.5,.5);
            bar.RenderTransform=new ScaleTransform();
        }
        play.MouseEnter+=delegate { UpdateFace(true); UpdateDetailVisibility(); };
        play.MouseMove+=delegate(object sender,MouseEventArgs e) {
            if(pointerHeldOverLaunch && CanStop && (e.GetPosition(play)-launchPointer).LengthSquared>=16) {
                pointerHeldOverLaunch=false;
                UpdateFace(true);
            }
        };
        play.MouseLeave+=delegate { pointerHeldOverLaunch=false; UpdateFace(false); UpdateDetailVisibility(); };
        play.IsKeyboardFocusedChanged+=delegate { RefreshText(); };
        spinner=(RotateTransform)window.FindName("WarmRotation");

        ((Button)window.FindName("More")).Background=menu;
        menu.Color=Color.FromRgb(78,57,22);
        play.IsVisibleChanged+=delegate { UpdateBackground(); RefreshText(); };
        play.IsEnabledChanged+=delegate { UpdateBackground(); };
        window.StateChanged+=delegate { UpdateBackground(); RefreshText(); };
        System.ComponentModel.PropertyChangedEventHandler motionChanged=delegate(object sender,System.ComponentModel.PropertyChangedEventArgs e) {
            if(e.PropertyName==nameof(SystemParameters.ClientAreaAnimation)) { UpdateBackground(); RefreshText(); }
        };
        SystemParameters.StaticPropertyChanged+=motionChanged;
        window.Closed+=delegate { SystemParameters.StaticPropertyChanged-=motionChanged; };
        FitLabels();
    }

    void UpdateBackground() {
        switch(State) {
            case LaunchState.Warming: play.Background=warmBrush; break;
            case LaunchState.Ready:
            case LaunchState.Starting:
            case LaunchState.Rendering: play.Background=readyBrush; break;
            case LaunchState.Playing: play.Background=runningColors.Fill; break;
            case LaunchState.WarmupFailed:
            case LaunchState.GameFailed: play.Background=failureBrush; break;
            default: play.Background=sunset; break;
        }
        runningColors.SetMoving(State==LaunchState.Playing && play.IsVisible && play.IsEnabled &&
            window.WindowState!=WindowState.Minimized && SystemParameters.ClientAreaAnimation);
    }

    void FitLabels() {
        if(measuredLanguage==Ui.Language) return;
        measuredLanguage=Ui.Language;
        var probe=new TextBlock { FontFamily=text.FontFamily, FontWeight=text.FontWeight, Language=text.Language };
        double width=205;
        double longest=0;
        foreach(string label in new[]{"启动游戏","游戏运行中","退出游戏","重试","正在退出游戏","正在启动游戏","运行失败","预热失败","会话已结束","预热着色器","着色器预热中"}) {
            bool warm=label=="着色器预热中";
            probe.FontSize=warm ? 14 : text.FontSize;
            probe.Text=warm && warmCount>=0 ? (warmTotal>0
                ? Ui.Format("管线预热 {0}%",(long)warmCount*100/warmTotal)
                : Ui.Format("着色器 {0:N0}",warmCount)) : Ui.Text(warm && (warmStage=="正在准备键位提示" || warmStage=="正在清理着色器缓存") ? warmStage : label);
            if(warm) warmText.Text=probe.Text;
            probe.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
            double captionWidth=probe.DesiredSize.Width+(warm ? 8 : 11);
            captionWidths[label]=captionWidth;
            longest=Math.Max(longest,captionWidth);
            // Reserve the menu, icon, icon-to-text gap and padding on both sides.
            width=Math.Max(width,54+(warm ? 28 : 32)+32+probe.DesiredSize.Width);
        }
        pill.Width=Math.Ceiling(width);
        captionSpace.Width=Math.Ceiling(longest);
        centeredAt=double.NaN;
    }

    bool CanAnimate { get { return SystemParameters.ClientAreaAnimation && play.IsVisible && window.WindowState!=WindowState.Minimized; } }

    void TransitionIcon(UIElement icon,bool show,bool animate) {
        var scale=(ScaleTransform)icon.RenderTransform;
        double alpha=show ? 1 : 0, size=show ? 1 : .84;
        double fromScale=scale.ScaleX, fromAlpha=icon.Opacity;
        scale.ScaleX=scale.ScaleY=animate ? fromScale : size;
        AnimationClock zoom=null;
        if(animate) {
            var sizeMotion=show ? (fromScale==.84 ? zoomIn : SpringFrom(fromScale)) : StartFrom(zoomOut,fromScale);
            zoom=sizeMotion.CreateClock();
            icon.Opacity=fromAlpha;
            icon.BeginAnimation(UIElement.OpacityProperty,StartFrom(show ? fadeIn : fadeOut,fromAlpha));
        } else {
            icon.BeginAnimation(UIElement.OpacityProperty,null);
            icon.Opacity=alpha;
        }
        scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty,zoom);
        scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty,zoom);
    }

    void UpdateIcon(UIElement next) {
        bool animate=CanAnimate;
        bool showRing=next==glyph || next==stop || next==bars;
        if(!animate) {
            foreach(var icon in icons) TransitionIcon(icon,icon==next,false);
            ring.BeginAnimation(UIElement.OpacityProperty,null);
            ring.Opacity=showRing ? 1 : 0;
        } else if(next!=visibleGlyph) {
            TransitionIcon(visibleGlyph,false,true);
            TransitionIcon(next,true,true);
            if(showRing!=(visibleGlyph==glyph || visibleGlyph==stop || visibleGlyph==bars))
                ControllerMotion.Fade(ring,ring.Opacity,showRing ? 1 : 0,180);
        }
        visibleGlyph=next;
        bool spin=animate && (next==busyIcon || State==LaunchState.Rendering);
        if(spin!=spinner.HasAnimatedProperties) {
            double angle=spinner.Angle%360;
            spinner.Angle=angle;
            spinner.BeginAnimation(RotateTransform.AngleProperty,spin
                ? new DoubleAnimation(angle,angle+360,TimeSpan.FromMilliseconds(850)) { RepeatBehavior=RepeatBehavior.Forever } : null);
        }
        for(int i=0;i<bars.Children.Count;i++) {
            var scale=(ScaleTransform)bars.Children[i].RenderTransform;
            bool pulse=animate && State==LaunchState.Playing;
            if(pulse!=scale.HasAnimatedProperties)
                scale.BeginAnimation(ScaleTransform.ScaleYProperty,pulse
                    ? new DoubleAnimation(1,.42,TimeSpan.FromMilliseconds(320+75*i)) {
                        AutoReverse=true, RepeatBehavior=RepeatBehavior.Forever } : null);
        }
    }

    public void RefreshText() {
        FitLabels();
        exitText.Text=Ui.Text(Failed ? "重试" : "退出游戏");
        if(State==LaunchState.Warming) RefreshDetail();
        if(Failed) {
            detailStage.Text=Ui.Status(FailureReason);
            detailShaders.Visibility=detailPipelines.Visibility=Visibility.Collapsed;
        }
        UpdateFace(play.IsMouseOver);
        UpdateDetailVisibility();
    }

    void RefreshDetail() {
        detailStage.Text=Ui.Status(warmStage);
        detailShaders.Visibility=cachedShaders>=0 ? Visibility.Visible : Visibility.Collapsed;
        detailPipelines.Visibility=warmTotal>0 ? Visibility.Visible : Visibility.Collapsed;
        if(cachedShaders>=0) detailShaders.Text=Ui.Format("缓存中的着色器　{0:N0}",cachedShaders);
        if(warmTotal>0) detailPipelines.Text=Ui.Format("已处理管线　{0:N0} / {1:N0}",warmCount,warmTotal);
    }

    void UpdateDetailVisibility() {
        bool requested=State==LaunchState.Warming && warmHover.IsVisible && warmHover.IsMouseOver ||
            Failed && play.IsVisible && (play.IsMouseOver || KeyboardAction);
        SetDetailOpen(requested && window.IsActive && !launchMenu.IsVisible);
    }

    void SetDetailOpen(bool open) {
        if(detailOpen==open) return;
        detailOpen=open;
        if(open) detailCard.Visibility=Visibility.Visible;
        var slide=(TranslateTransform)detailCard.RenderTransform;
        double from=slide.Y, target=open ? 0 : 7;
        slide.Y=target;
        int duration=open ? 185 : 135;
        slide.BeginAnimation(TranslateTransform.YProperty,CanAnimate ? Tween(from,target,duration,
            new CubicEase { EasingMode=EasingMode.EaseOut }) : null);
        ControllerMotion.Fade(detailCard,detailCard.Opacity,open ? 1 : 0,duration,delegate {
            if(!detailOpen) detailCard.Visibility=Visibility.Collapsed;
        });
    }

    void UpdateFace(bool hovered) {
        bool exit=CanStop && (hovered && !pointerHeldOverLaunch || KeyboardAction);
        bool retrying=Failed && (hovered || KeyboardAction);
        bool ready=State==LaunchState.Ready;
        exitTint.Color=Failed ? warmBrush.Color : ready ? Colors.White : failureBrush.Color;
        bool showExit=exit || State==LaunchState.Stopping;
        menu.Color=State==LaunchState.Warming || retrying ? Color.FromRgb(78,57,22) :
            !showExit && play.Background==readyBrush ? Color.FromRgb(29,69,48) : Color.FromRgb(80,32,54);
        bool animate=CanAnimate;
        double target=showExit || retrying ? 1 : ready && hovered ? .18 : 0;
        if(tintTarget!=target || (!animate || !play.IsEnabled) && exitTint.HasAnimatedProperties) {
            tintTarget=target;
            double from=exitTint.Opacity;
            bool fade=animate && play.IsEnabled;
            exitTint.Opacity=fade ? from : target;
            exitTint.BeginAnimation(Brush.OpacityProperty,fade
                ? new DoubleAnimation(from,target,TimeSpan.FromMilliseconds(240)) {
                    EasingFunction=new SineEase { EasingMode=EasingMode.EaseInOut }
                } : null);
        }
        bool starting=State==LaunchState.Starting || State==LaunchState.Rendering;
        bool busy=starting || State==LaunchState.Warming || State==LaunchState.Stopping;
        UpdateIcon(Failed ? (retrying ? retry : warning) : exit ? stop : busy ? busyIcon : State==LaunchState.Playing ? (UIElement)bars : glyph);
        string label=State==LaunchState.Playing ? "游戏运行中" : State==LaunchState.Rendering ? "正在启动游戏" :
            State==LaunchState.Stopping ? "正在退出游戏" : State==LaunchState.Starting ? "正在启动游戏" : State==LaunchState.WarmupFailed ? "预热失败" :
            State==LaunchState.GameFailed ? "运行失败" : State==LaunchState.CacheCleared ? "预热着色器" :
            State==LaunchState.Idle ? "会话已结束" : "启动游戏";
        string caption=Ui.Text(label);
        if(text.Text!=caption) text.Text=caption;
        double exitAlpha=exit || retrying ? 1 : 0;
        if((double)exitText.GetAnimationBaseValue(UIElement.OpacityProperty)!=exitAlpha) {
            if(Failed && animate) {
                ControllerMotion.Fade(exitText,exitText.Opacity,exitAlpha,170);
                ControllerMotion.Fade(text,text.Opacity,1-exitAlpha,170);
            } else {
                exitText.BeginAnimation(UIElement.OpacityProperty,null);
                text.BeginAnimation(UIElement.OpacityProperty,null);
                exitText.Opacity=exitAlpha;
                text.Opacity=1-exitAlpha;
            }
        }
        // Move to the same centred positions without remeasuring or reshaping text on hover.
        string shown=retrying ? "重试" : exit ? "退出游戏" : State==LaunchState.Warming ? "着色器预热中" : label;
        double center=(captionSpace.Width-captionWidths[shown])/2;
        if(center!=centeredAt || !animate && faceShift.HasAnimatedProperties) {
            bool glide=animate && !double.IsNaN(centeredAt);
            centeredAt=center;
            double from=faceShift.X;
            faceShift.X=glide ? from : center;
            faceShift.BeginAnimation(TranslateTransform.XProperty,glide
                ? new DoubleAnimation(from,center,TimeSpan.FromMilliseconds(210)) {
                    EasingFunction=new SineEase { EasingMode=EasingMode.EaseOut }
                } : null);
        }
        string action=CanStop ? exitText.Text : State==LaunchState.Warming ? warmText.Text : Ui.Text(shown);
        if(AutomationProperties.GetName(play)!=action) AutomationProperties.SetName(play,action);
    }

    public void Set(LaunchState next) {
        LaunchState previous=State;
        State=next;
        if(!Failed) FailureReason=null;
        ((TextBlock)window.FindName("LaunchDetailTitle")).SetResourceReference(TextBlock.TextProperty,Failed ? "ui.错误原因" : "ui.预热详情");
        AutomationProperties.SetHelpText(play,Failed ? Ui.Status(FailureReason) : "");
        if(warmCount>=0) { warmCount=-1; warmTotal=0; measuredLanguage=null; }
        if(next==LaunchState.Starting && previous!=next) {
            pointerHeldOverLaunch=play.IsMouseOver;
            launchPointer=Mouse.GetPosition(play);
        }
        bool warming=next==LaunchState.Warming;
        if(warmStage=="正在准备键位提示" || warmStage=="正在清理着色器缓存") measuredLanguage=null;
        warmStage="正在准备着色器";
        cachedShaders=-1;
        warmHover.Visibility=warming ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetailVisibility();
        play.IsEnabled=next==LaunchState.Ready || next==LaunchState.CacheCleared || CanStop || Failed;
        warmText.Visibility=warming ? Visibility.Visible : Visibility.Collapsed;
        text.Visibility=warming ? Visibility.Collapsed : Visibility.Visible;
        double faceAlpha=next==LaunchState.Playing ? .76 : 1;
        if((double)playFace.GetAnimationBaseValue(UIElement.OpacityProperty)!=faceAlpha)
            ControllerMotion.Fade(playFace,playFace.Opacity,faceAlpha,180);
        UpdateBackground();
        bool animate=SystemParameters.ClientAreaAnimation && play.IsVisible && window.WindowState!=WindowState.Minimized;
        if(previous==LaunchState.Warming && next==LaunchState.Ready && animate) {
            readyBrush.BeginAnimation(SolidColorBrush.ColorProperty,new ColorAnimation(warmBrush.Color,
                (Color)readyBrush.GetAnimationBaseValue(SolidColorBrush.ColorProperty),TimeSpan.FromMilliseconds(360)) {
                EasingFunction=new SineEase { EasingMode=EasingMode.EaseInOut }, FillBehavior=FillBehavior.Stop
            });
        }
        if(previous!=next && next==LaunchState.Playing && animate) runningColors.Reveal();
        RefreshText();
    }
}
