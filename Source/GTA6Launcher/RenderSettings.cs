using System;
using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

internal sealed partial class DesktopLauncher
{
    ProjectDisplaySettings nativeProfile;
    string renderEngine;
    ProjectDisplaySettings RenderProfile => nativeProfile ?? displaySettings;
    string RenderProfilePath => nativeProfile==null ? DisplayPreferences : Path.Combine(Lab,"profiles",renderEngine,"render.xml");
    bool renderOpen;
    int renderRevision;
    Task<bool> dlssSupport;
    Task<int> fgSupport;
    int maxMultiplier;
    bool srAvailable;
    VictoryNvidiaMenu victoryMenu;
    bool VictoryRender => renderEngine=="victory-nvidia";
    bool victoryFsr;
    int victoryDlssMax;
    bool syncingMultiplier, draggingMultiplier;
    int styledMultiplier=-1;
    static readonly string[][] frameColors={
        new[]{"#385FCD","#6477ED","#99A6FF"}, new[]{"#395EDC","#7769FA","#AB8BFF"},
        new[]{"#3856DB","#895CFA","#C092FF"}, new[]{"#454FDF","#985BF5","#CE94FF"},
        new[]{"#514ADA","#A65CEF","#DC9AFF"}
    };

    void AttachRenderSettings() {
        AddVictoryChoices("VictoryConsole",VictoryNvidiaMenu.Console,new[]{"series-x","series-s"},new[]{"Series X","Series S"});
        AddVictoryChoices("VictoryDlss",VictoryNvidiaMenu.Dlss,new[]{"dlaa","quality","balanced","performance"},
            new[]{"DLAA","ui.质量","ui.平衡","ui.性能"});
        AddVictoryChoices("VictoryType",VictoryNvidiaMenu.Type,new[]{"auto","dlss","fsr"},new[]{"ui.自动","DLSS","FSR 3"});
        Find<Button>("RenderSettings").Click+=delegate { OpenRenderSettings(); };
        Find<Button>("CloseRender").Click+=delegate { CloseRenderSettings(); };
        Find<Border>("RenderScrim").MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e) {
            CloseRenderSettings();
            e.Handled=true;
        };
        window.PreviewMouseDown+=delegate(object sender,MouseButtonEventArgs e) {
            if(renderOpen && Find<Grid>("Rail").IsMouseOver) {
                CloseRenderSettings();
                e.Handled=true;
            }
        };
        var slider=Find<Slider>("FgSlider");
        slider.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((s,e)=>{
            draggingMultiplier=true;
            var track=slider.Template.FindName("PART_Track",slider) as Track;
            track?.BeginAnimation(Track.ValueProperty,null);
        }),true);
        slider.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((s,e)=>{
            if(!draggingMultiplier) return;
            draggingMultiplier=false;
            double from=slider.Value;
            if(!e.Canceled) ChangeMultiplier(MultiplierAt(from));
            ShowMultiplier();
            SlideToStop(slider,from);
        }),true);
        slider.ValueChanged+=delegate(object s,RoutedPropertyChangedEventArgs<double> e) {
            if(syncingMultiplier || !slider.IsEnabled) return;
            int value=MultiplierAt(slider.Value);
            if(draggingMultiplier) { PaintMultiplier(slider,value); return; }
            ChangeMultiplier(value);
            ShowMultiplier();
            SlideToStop(slider,e.OldValue);
        };
        slider.IsVisibleChanged+=delegate { styledMultiplier=-1; ShowMultiplier(); };
        foreach(string name in new[]{"SrQuality","SrBalanced","SrPerformance"}) {
            var choice=Find<RadioButton>(name);
            choice.Click+=delegate {
                string previous=RenderProfile.DlssQuality;
                RenderProfile.DlssQuality=(string)choice.Tag;
                try {
                    RenderProfile.Save(RenderProfilePath);
                    ShowRenderNotice("ui.下次启动游戏时生效");
                } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException) {
                    RenderProfile.DlssQuality=previous;
                    ShowDlssQuality();
                    ShowRenderNotice("ui.无法保存模拟器设置");
                    Append(e.Message);
                }
            };
        }
        AttachRenderChoice("DlssSr", () => RenderProfile.DlssSuperResolution, v => {
            RenderProfile.DlssSuperResolution=v;
            Find<CheckBox>("DlssSr").IsEnabled=srAvailable || v;
            ShowTechnology();
        });
    }

    void ShowTechnology() {
        var sr=Find<CheckBox>("DlssSr");
        sr.IsChecked=RenderProfile.DlssSuperResolution;
        sr.IsEnabled=srAvailable || sr.IsChecked==true;
        ShowMultiplier();
    }

    void ShowRenderNotice(string key) {
        var notice=Find<TextBlock>("RenderNotice");
        notice.SetResourceReference(TextBlock.TextProperty,key);
        notice.Visibility=Visibility.Visible;
    }

    void ShowDlssQuality() {
        Find<RadioButton>(RenderProfile.DlssQuality=="balanced" ? "SrBalanced" :
            RenderProfile.DlssQuality=="performance" ? "SrPerformance" : "SrQuality").IsChecked=true;
    }

    static int MultiplierAt(double position) {
        int step=(int)Math.Round(position,MidpointRounding.AwayFromZero);
        return step==0 ? 0 : step+1;
    }

    static void SlideToStop(Slider slider,double from) {
        if(!SystemParameters.ClientAreaAnimation || !slider.IsVisible || Math.Abs(from-slider.Value)<.001) return;
        var track=slider.Template?.FindName("PART_Track",slider) as Track;
        if(track==null) return;
        if(track.HasAnimatedProperties && Math.Abs(track.Value-slider.Value)>.001) from=track.Value;
        track.BeginAnimation(Track.ValueProperty,new DoubleAnimation(from,slider.Value,TimeSpan.FromMilliseconds(145)) {
            EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut }, FillBehavior=FillBehavior.Stop
        });
    }

    void AttachRenderChoice(string name, Func<bool> read, Action<bool> write) {
        var choice=Find<CheckBox>(name);
        choice.Click+=delegate {
            bool previous=read();
            write(choice.IsChecked==true);
            try {
                RenderProfile.Save(RenderProfilePath);
                ShowRenderNotice("ui.下次启动游戏时生效");
            } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException) {
                write(previous);
                choice.IsChecked=previous;
                ShowRenderNotice("ui.无法保存模拟器设置");
                Append(e.Message);
            }
        };
    }

    void ChangeMultiplier(int next) {
        if(VictoryRender) {
            if(victoryMenu!=null && next!=victoryMenu.Multiplier && (next==0 || (next>=2 && next<=maxMultiplier)))
                SaveVictoryChoice(VictoryNvidiaMenu.Frames,next.ToString());
            return;
        }
        int before=RenderProfile.FrameMultiplier;
        if(next==before || next<0 || next==1 || next>maxMultiplier) return;
        RenderProfile.FrameMultiplier=next;
        try {
            RenderProfile.Save(RenderProfilePath);
            ShowRenderNotice("ui.下次启动游戏时生效");
        } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException) {
            RenderProfile.FrameMultiplier=before;
            ShowRenderNotice("ui.无法保存模拟器设置");
            Append(e.Message);
        }
    }

    void ShowMultiplier() {
        int maximum=maxMultiplier;
        bool supported=maximum>=2 && (VictoryRender ? victoryMenu!=null : RenderProfile.DlssSuperResolution && srAvailable);
        Find<StackPanel>("FgOptions").Visibility=VictoryRender || supported ? Visibility.Visible : Visibility.Collapsed;
        string title=VictoryRender ? "ui.帧生成倍数" : maximum>2 ? "ui.DLSS 多帧生成" : "ui.DLSS 帧生成";
        Find<TextBlock>("FgTitle").SetResourceReference(TextBlock.TextProperty,title);
        int value=supported ? Math.Min(VictoryRender ? victoryMenu.Multiplier : RenderProfile.FrameMultiplier,maximum) : 0;
        var slider=Find<Slider>("FgSlider");
        slider.SetResourceReference(AutomationProperties.NameProperty,title);
        syncingMultiplier=true;
        try {
            slider.Maximum=Math.Max(1,maximum-1);
            slider.Value=Math.Max(0,value-1);
            slider.IsEnabled=supported;
            PaintMultiplier(slider,value);
        } finally { syncingMultiplier=false; }
    }

    void PaintMultiplier(Slider slider,int value) {
        var label=Find<TextBlock>("FgValue");
        if(value==0) label.SetResourceReference(TextBlock.TextProperty,"ui.未开启");
        else label.Text=value+"×";
        AutomationProperties.SetItemStatus(slider,label.Text);
        if(styledMultiplier==value) return;
        bool animate=slider.IsVisible && SystemParameters.ClientAreaAnimation;
        double phase=0;
        if(slider.Background?.Transform is TranslateTransform old && !old.IsFrozen) {
            phase=old.X;
            old.BeginAnimation(TranslateTransform.XProperty,null);
        }
        slider.Foreground=value>=2 ? FrameGradient(value,animate,slider.Foreground as LinearGradientBrush) : Brushes.White;
        slider.Background=value>=2 ? FrameSpecks(value,animate,phase) : Brushes.Transparent;
        styledMultiplier=value;
    }

    static Brush FrameGradient(int multiplier,bool animate,LinearGradientBrush previous) {
        var colors=frameColors[multiplier-2];
        var brush=new LinearGradientBrush { StartPoint=new Point(0,0.5), EndPoint=new Point(1,0.5) };
        for(int i=0;i<colors.Length;i++) {
            var stop=new GradientStop((Color)ColorConverter.ConvertFromString(colors[i]),i*.5);
            brush.GradientStops.Add(stop);
            if(animate) stop.BeginAnimation(GradientStop.ColorProperty,new ColorAnimation(
                previous?.GradientStops[i].Color ?? Colors.White,stop.Color,TimeSpan.FromMilliseconds(240)){FillBehavior=FillBehavior.Stop});
        }
        return brush;
    }

    static Brush FrameSpecks(int multiplier,bool animate,double phase) {
        var tile=new Rect(0,0,84,20);
        var stars=new DrawingGroup();
        var random=new Random(27);
        using(var dc=stars.Open()) {
            for(int i=0;i<2+multiplier*2;i++) {
                var point=new Point(2+random.NextDouble()*80,2+random.NextDouble()*16);
                double radius=.45+random.NextDouble()*.75;
                dc.PushOpacity(.35+random.NextDouble()*.5);
                dc.DrawEllipse(Brushes.White,null,point,radius,radius);
                dc.Pop();
            }
        }
        var brush=new DrawingBrush(stars) { Viewbox=tile, Viewport=tile, ViewboxUnits=BrushMappingMode.Absolute,
            ViewportUnits=BrushMappingMode.Absolute, TileMode=TileMode.Tile };
        if(animate) {
            var drift=new TranslateTransform();
            brush.Transform=drift;
            double start=phase%tile.Width;
            drift.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(start,start+tile.Width,TimeSpan.FromSeconds(10.0/(multiplier-1))) {
                RepeatBehavior=RepeatBehavior.Forever
            });
        } else brush.Freeze();
        return brush;
    }

    int ProbeDlss(string arguments) {
        string folder=Path.Combine(root,"Runtime","DLSS");
        try {
            var info=new ProcessStartInfo(Path.Combine(folder,"DlssCapabilities.exe"),arguments) {
                UseShellExecute=false, CreateNoWindow=true, WorkingDirectory=folder
            };
            info.EnvironmentVariables["PATH"]=Path.Combine(root,"Runtime","Native")+Path.PathSeparator+info.EnvironmentVariables["PATH"];
            if(arguments=="--frame-generation") info.EnvironmentVariables["PALMLINE_DLSS_SR"]="1";
            using(var probe=Process.Start(info)) {
                if(probe.WaitForExit(30000)) return probe.ExitCode;
                probe.Kill();
            }
        } catch(Win32Exception) { } catch(InvalidOperationException) { } catch(IOException) { }
        return -1;
    }

    void AddVictoryChoices(string panelName,string key,string[] values,string[] labels) {
        var panel=Find<Panel>(panelName);
        for(int i=0;i<values.Length;i++) {
            var choice=new RadioButton { GroupName=panelName,Tag=values[i],Style=(Style)window.FindResource("renderSegment") };
            if(key==VictoryNvidiaMenu.Dlss) choice.MinWidth=56;
            if(labels[i].StartsWith("ui.",StringComparison.Ordinal)) choice.SetResourceReference(ContentControl.ContentProperty,labels[i]);
            else choice.Content=labels[i];
            choice.Click+=delegate { SaveVictoryChoice(key,(string)choice.Tag); };
            panel.Children.Add(choice);
        }
    }

    void SaveVictoryChoice(string key,string value) {
        if(victoryMenu==null || !VictoryRender || !renderOpen) return;
        var edits=new Dictionary<string,string> { [key]=value };
        string Selected(string name) => name==key ? value : victoryMenu.Get(name);
        int limit=VictoryNvidiaMenu.Limit(Selected(VictoryNvidiaMenu.Type),Selected(VictoryNvidiaMenu.Console),
            Selected(VictoryNvidiaMenu.Dlss),srAvailable,victoryDlssMax,victoryFsr);
        if(key!=VictoryNvidiaMenu.Frames && victoryMenu.Multiplier>limit) edits[VictoryNvidiaMenu.Frames]=limit.ToString();
        try {
            victoryMenu.Save(edits);
            ShowRenderNotice("ui.下次启动游戏时生效");
        } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException) {
            Append(e.Message);
            ShowRenderNotice("ui.无法保存模拟器设置");
        }
        ShowVictoryChoices();
    }

    void ShowVictoryChoices() {
        string type=victoryMenu.Get(VictoryNvidiaMenu.Type),console=victoryMenu.Get(VictoryNvidiaMenu.Console),dlss=victoryMenu.Get(VictoryNvidiaMenu.Dlss);
        foreach(var group in new[]{("VictoryType",type),("VictoryConsole",console),("VictoryDlss",dlss)}) {
            foreach(RadioButton button in Find<Panel>(group.Item1).Children) {
                string v=(string)button.Tag;
                button.IsChecked=v==group.Item2 && (group.Item1!="VictoryDlss" || srAvailable);
                button.IsEnabled=group.Item1=="VictoryDlss" ? srAvailable :
                    group.Item1!="VictoryType" || v=="auto" || (v=="fsr" ? victoryFsr : victoryDlssMax>=2 && srAvailable);
            }
        }
        maxMultiplier=VictoryNvidiaMenu.Limit(type,console,dlss,srAvailable,victoryDlssMax,victoryFsr);
        string hint=maxMultiplier>=2 ? null :
            type!="fsr" && victoryDlssMax>=2 && srAvailable && dlss=="off" && type=="dlss" ? "ui.请开启 DLSS 以使用 DLSS 帧生成" :
            type!="dlss" && victoryFsr && console=="series-x" && (!srAvailable || dlss=="off") ? "ui.无 DLSS 时，FSR 3 需要 Series S" : "ui.当前配置无法使用所选帧生成方式";
        var status=Find<TextBlock>("VictoryStatus");
        if(hint!=null) status.SetResourceReference(TextBlock.TextProperty,hint);
        status.Visibility=hint==null ? Visibility.Collapsed : Visibility.Visible;
        ShowMultiplier();
    }

    bool CanOpenEmulatorSettings => launchButton.State==LaunchState.Ready && activeGame!=null &&
        available.Contains(selected);

    Task<bool> ProbeSuperResolution() => dlssSupport ?? (dlssSupport=Task.Run(()=>ProbeDlss("")==0));
    bool HasGameUpscaler() => File.Exists(Path.Combine(gameAssets,"amd_fidelityfx_loader_xbox.dll")) &&
        File.Exists(Path.Combine(gameAssets,"amd_fidelityfx_upscaler_xbox.dll"));
    VictoryNvidiaMenu ReadVictoryMenu() {
        string local=Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new VictoryNvidiaMenu(Path.Combine(local,"victory","victory-settings.txt"));
    }

    async void OpenRenderSettings() {
        if(!CanOpenEmulatorSettings) return;
        Menu(false);
        renderEngine=selected;
        nativeProfile=renderEngine=="cgbj" || VictoryRender ? null : ProjectDisplaySettings.Load(Path.Combine(Lab,"profiles",renderEngine,"render.xml"));
        victoryMenu=null;
        var victoryOptions=Find<StackPanel>("VictoryOptions");
        victoryOptions.Visibility=VictoryRender ? Visibility.Visible : Visibility.Collapsed;
        Find<StackPanel>("NativeDlssSection").Visibility=VictoryRender ? Visibility.Collapsed : Visibility.Visible;
        Find<TextBlock>("RenderEngineName").Text=ProjectName(renderEngine)+"  ·  Direct3D 12";
        renderOpen=true;
        int revision=++renderRevision;
        bool hasUpscaler=HasGameUpscaler();
        ShowDlssQuality();
        var status=Find<TextBlock>("DlssStatus");
        maxMultiplier=-1;
        srAvailable=false;
        ShowTechnology();
        status.SetResourceReference(TextBlock.TextProperty,"ui.正在检测 DLSS 支持");
        Find<TextBlock>("RenderNotice").Visibility=Visibility.Collapsed;
        var overlay=Find<Grid>("RenderOverlay");
        overlay.IsHitTestVisible=true;
        overlay.Visibility=Visibility.Visible;
        ControllerMotion.Reveal(Find<Border>("RenderCard"),.96,1.012,8,235,.45);
        Find<Button>("CloseRender").Focus();
        if(VictoryRender) {
            victoryOptions.IsEnabled=false;
            var message=Find<TextBlock>("VictoryStatus");
            message.Visibility=Visibility.Visible;
            message.SetResourceReference(TextBlock.TextProperty,"ui.正在检测 DLSS 支持");
            try {
                victoryMenu=ReadVictoryMenu();
                string fx=Environment.GetEnvironmentVariable("VICTORY_FIDELITYFX_DIR");
                if(string.IsNullOrEmpty(fx)) fx=Path.Combine(root,"Community",renderEngine,"runtime",(string)catalog[renderEngine].Attribute("commit"),"fidelityfx");
                victoryFsr=File.Exists(Path.Combine(fx,"amd_fidelityfx_loader_dx12.dll")) && File.Exists(Path.Combine(fx,"amd_fidelityfx_framegeneration_dx12.dll"));
            } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException) {
                message.SetResourceReference(TextBlock.TextProperty,"ui.无法读取模拟器设置");
                Append(e.Message); return;
            }
        }
        if(renderEngine=="xcl") {
            srAvailable=false;
            maxMultiplier=0;
            ShowMultiplier();
            status.SetResourceReference(TextBlock.TextProperty,"ui.此引擎尚未接入 NVIDIA 时域渲染");
            Find<CheckBox>("DlssSr").IsEnabled=false;
            return;
        }
        if(fgSupport==null) fgSupport=Task.Run(()=>ProbeDlss("--frame-generation"));
        bool supported=await ProbeSuperResolution();
        if(!renderOpen || revision!=renderRevision) return;
        if(VictoryRender) {
            int deviceMax=await fgSupport;
            if(!renderOpen || revision!=renderRevision) return;
            srAvailable=supported && hasUpscaler;
            victoryDlssMax=Math.Min(6,Math.Max(0,deviceMax));
            ShowVictoryChoices();
            victoryOptions.IsEnabled=true;
            return;
        }
        srAvailable=supported && hasUpscaler;
        ShowTechnology();
        status.SetResourceReference(TextBlock.TextProperty,srAvailable
            ? "ui.使用游戏的深度和运动矢量重建画面" : "ui.当前游戏或显卡无法使用 DLSS");
        int maximum=await fgSupport;
        if(!renderOpen || revision!=renderRevision) return;
        maxMultiplier=srAvailable && maximum>=2 && maximum<=6 ? maximum : 0;
        ShowMultiplier();
    }

    void CloseRenderSettings() {
        if(!renderOpen) return;
        renderOpen=false;
        int revision=++renderRevision;
        var overlay=Find<Grid>("RenderOverlay");
        overlay.IsHitTestVisible=false;
        Find<Button>("More").Focus();
        ControllerMotion.Dismiss(Find<Border>("RenderCard"),.96,130,0,delegate {
            if(revision==renderRevision) overlay.Visibility=Visibility.Collapsed;
        });
    }
}

namespace Launcher.Layout
{
    public sealed class FrameMultiplierTicks : TickBar
    {
        protected override void OnRender(DrawingContext dc) {
            int steps=(int)(Maximum-Minimum);
            double span=ActualWidth-ReservedSpace;
            if(steps<1 || span<=0) return;
            for(int i=0;i<=steps;i++) {
                double x=ReservedSpace/2+span*i/steps;
                dc.DrawEllipse(Fill,null,new Point(x,ActualHeight/2),2.2,2.2);
            }
        }
    }
}
