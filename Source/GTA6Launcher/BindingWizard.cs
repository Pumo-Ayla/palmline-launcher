using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

internal sealed class BindingWizard
{
    readonly Window window;
    readonly ControllerViewer viewer;
    readonly BindingSlots slots;
    readonly MappedPadPreview preview;
    readonly KeyboardMappingView keyboardView;
    readonly Viewbox overviewHost;
    readonly Button viewSwitch;
    bool preferKeyboardView, overviewShown;
    int overviewRevision;
    readonly System.Windows.Shapes.Path keyboardGlyph, controllerGlyph;
    PadState hoverPad, livePad;
    readonly MappingFeedback feedback;
    readonly DispatcherTimer finishTimer;
    readonly Action saved;
    readonly Grid panel, surface;
    readonly StackPanel rail;
    readonly Border strip;
    readonly Button target, next, prev;
    readonly TextBlock hint;
    readonly TextBlock status;
    readonly TextBox nameEditor;
    readonly Grid caption;
    readonly ScaleTransform press=new ScaleTransform();
    readonly TranslateTransform railSlide=new TranslateTransform(), modelSlide=new TranslateTransform();
    bool active, armed, finishing;
    bool promptLocked;
    string promptProject;
    public void SetPromptContext(string project,bool locked) {
        promptProject=project;
        promptLocked=locked;
        bool available=project=="cgbj" && Configured;
        Find<Border>("PromptGroup").Visibility=Visible(available);
        var toggle=Find<CheckBox>("UseKeyPrompts");
        toggle.IsEnabled=available && !locked;
        toggle.SetResourceReference(FrameworkElement.ToolTipProperty,"ui.开启后预载键位提示，游戏退出后可更改。");
    }
    int slot=-1, renameSlot=-1, revision;
    public bool Configured { get { return slots.Active>=0; } }
    public bool UsingGtaVi { get { return slots.Active==BindingSlots.GtaVi; } }
    public string PlayCommand(bool prompts) { return GameKeyPrompts.Command(UsingGtaVi,slots.Profile,prompts); }
    public bool Editing { get { return active && slot>=0; } }
    int Index { get { return slots.Position[slot]; } }
    T Find<T>(string name) where T:class { return (T)window.FindName(name); }
    static Visibility Visible(bool value) { return value ? Visibility.Visible : Visibility.Collapsed; }

    public BindingWizard(Window window,ControllerViewer viewer,string directory,Action saved) {
        this.window=window; this.viewer=viewer; this.saved=saved;
        slots=new BindingSlots(directory);
        preview=new MappedPadPreview(state=>{ livePad=state; SyncPreview(); });
        overviewHost=Find<Viewbox>("KeyboardOverview");
        keyboardView=new KeyboardMappingView(window,overviewHost,preview,state=>{ hoverPad=state; SyncPreview(); });
        viewSwitch=Find<Button>("MappingViewSwitch");
        keyboardGlyph=Find<System.Windows.Shapes.Path>("SwitchKeyboardIcon");
        controllerGlyph=Find<System.Windows.Shapes.Path>("SwitchControllerIcon");
        viewSwitch.Click+=delegate {
            preferKeyboardView=!preferKeyboardView;
            SetOverview(true);
            FocusPreview();
        };
        finishTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(1880)};
        finishTimer.Tick+=delegate { finishTimer.Stop(); if(finishing) ShowSlots(true); };
        panel=Find<Grid>("MappingPanel"); surface=Find<Grid>("OrbitSurface");
        rail=Find<StackPanel>("SlotRail"); rail.RenderTransform=railSlide;
        var delete=Find<Button>("DeleteSlot");
        Find<Button>("RenameSlot").Click+=delegate { BeginRename(); };
        delete.Click+=delegate {
            if(!Configured || Editing) return;
            Find<TextBlock>("DeletePrompt").Text=Ui.Format("删除“{0}”的键鼠映射？",slots.Name(slots.Active));
            Find<Grid>("DeleteOverlay").Visibility=Visibility.Visible;
            Find<Grid>("LauncherContent").IsEnabled=false;
            Find<Button>("CancelDelete").Focus();
        };
        Find<Button>("CancelDelete").Click+=delegate { CloseDelete(); };
        Find<Button>("ConfirmDelete").Click+=delegate {
            if(!DeleteOpen) return;
            CloseDelete();
            try { slots.DeleteActive(); ShowSlots(false); saved(); }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException) {
                status.Text=Ui.Text("删除失败，请重试。");
            }
        };
        surface.RenderTransform=modelSlide;
        strip=Find<Border>("BindPanel"); target=Find<Button>("BindTarget");
        next=Find<Button>("NextBinding"); prev=Find<Button>("PrevBinding"); hint=Find<TextBlock>("BindHint");
        feedback=new MappingFeedback(window); MappingFeedback.AttachHover(target);
        status=Find<TextBlock>("SlotStatus"); nameEditor=Find<TextBox>("ProfileName"); caption=Find<Grid>("ProfileCaption");
        preview.LayerChanged+=delegate { RefreshNameplate(); if(overviewShown) keyboardView.ContextChanged(); };
        Find<Grid>("ProfileLabel").MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e) {
            if(e.ClickCount==2 && Configured && !UsingGtaVi) { BeginRename(); e.Handled=true; }
        };
        nameEditor.LostKeyboardFocus+=delegate { EndRename(true); };
        window.PreviewMouseDown+=delegate(object sender,MouseButtonEventArgs e) {
            if(renameSlot>=0 && !Inside(Find<Border>("NameField"),e.OriginalSource as DependencyObject)) EndRename(true);
        };
        panel.SizeChanged+=delegate { LayoutControls(); if(active) PlaceModel(Editing,false); };
        caption.SizeChanged+=delegate(object sender,SizeChangedEventArgs e) {
            if(e.HeightChanged && active && !Editing && !finishing) PlaceModel(false,false);
        };
        Find<Border>("ControlCap").RenderTransform=press;
        target.Click+=delegate { Arm(); };
        prev.Click+=delegate { if(Editing && Index>0) { slots.Position[slot]--; DisplayStep(); } };
        next.Click+=delegate { Advance(); };
        Find<Button>("ResetBindings").Click+=delegate {
            if(!Editing) return;
            Array.Clear(slots.Edit(slot),0,MappingStep.All.Length); slots.Position[slot]=0; DisplayStep();
        };
        surface.MouseMove+=delegate(object sender,MouseEventArgs e) { preview.Move(e.GetPosition(window)); };
        surface.MouseLeave+=delegate { preview.LeaveSurface(); };
        Find<Grid>("ControllerPageContent").PreviewMouseDown+=MouseDown;
        window.AddHandler(Mouse.PreviewMouseUpEvent,new MouseButtonEventHandler(delegate(object sender,MouseButtonEventArgs e) {
            string id="Mouse."+e.ChangedButton;
            if(overviewShown) keyboardView.Press(id,false);
            if(preview.Press(id,false) && (surface.IsMouseOver || overviewHost.IsMouseOver)) e.Handled=true;
        }),true);
        Find<Grid>("ControllerPageContent").PreviewMouseWheel+=delegate(object sender,MouseWheelEventArgs e) {
            if(!active || e.Delta==0) return;
            var source=e.OriginalSource as DependencyObject;
            string id=e.Delta>0 ? "Wheel.Up" : "Wheel.Down";
            if(CanRecordMouse(e)) { Capture(id); e.Handled=true; }
            else if((Inside(surface,source) || Inside(overviewHost,source)) && preview.Scroll(id)) e.Handled=true;
        };
        window.PreviewKeyUp+=delegate(object sender,KeyEventArgs e) {
            var key=e.Key==Key.System ? e.SystemKey : e.Key;
            if(overviewShown) keyboardView.Press("Key."+key,false);
            if(preview.Press("Key."+key,false) && (surface.IsKeyboardFocusWithin || overviewHost.IsKeyboardFocusWithin)) e.Handled=true;
        };
        window.Deactivated+=delegate { EndRename(true); if(overviewShown) keyboardView.ClearFeedback(); preview.Clear(); };
        window.Closed+=delegate { finishTimer.Stop(); preview.Use(null); };
    }
    void SyncPreview() {
        bool live=livePad.Buttons!=0 || livePad.LT!=0 || livePad.RT!=0 || livePad.LX!=0 || livePad.LY!=0 || livePad.RX!=0 || livePad.RY!=0;
        viewer.Sync(overviewShown && !live ? hoverPad : livePad,Quaternion.Identity);
    }
    void FocusPreview() {
        if(overviewShown) overviewHost.Focus(); else surface.Focus();
    }
    void SetOverview(bool animate) {
        bool available=active && !Editing && !finishing;
        viewSwitch.Visibility=Visible(available);
        bool show=available && preferKeyboardView;
        viewSwitch.ToolTip=Ui.Text(show ? "切换到手柄视图" : "切换到键鼠视图");
        AutomationProperties.SetName(viewSwitch,(string)viewSwitch.ToolTip);
        AutomationProperties.SetItemStatus(viewSwitch,Ui.Text(show ? "键鼠视图" : "手柄视图"));
        bool changed=overviewShown!=show;
        bool motion=changed && animate && SystemParameters.ClientAreaAnimation && surface.IsVisible;
        if(changed) {
            preview.Clear();
            keyboardView.ClearFeedback();
            overviewShown=show;
        }
        if(show) {
            keyboardView.Refresh(slots.Profile ?? Array.Empty<string>());
        }
        viewer.SetOverview(available,show,motion);
        overviewHost.IsHitTestVisible=show;
        int token=++overviewRevision;
        var slide=(TranslateTransform)overviewHost.RenderTransform;
        double offset=show ? 0 : 14;
        if(motion) {
            double alpha=overviewHost.Visibility==Visibility.Visible ? overviewHost.Opacity : 0;
            double from=overviewHost.Visibility==Visibility.Visible ? slide.Y : 14;
            overviewHost.Visibility=Visibility.Visible;
            slide.Y=offset;
            slide.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(from,offset,TimeSpan.FromMilliseconds(show ? 275 : 170)) {
                EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut }, FillBehavior=FillBehavior.Stop
            });
            ControllerMotion.Fade(overviewHost,alpha,show ? 1 : 0,show ? 240 : 155,delegate {
                if(!show && token==overviewRevision) overviewHost.Visibility=Visibility.Collapsed;
            });
            ControllerMotion.Fade(keyboardGlyph,keyboardGlyph.Opacity,show ? 0 : 1,145);
            ControllerMotion.Fade(controllerGlyph,controllerGlyph.Opacity,show ? 1 : 0,145);
        } else {
            slide.BeginAnimation(TranslateTransform.YProperty,null); slide.Y=offset;
            overviewHost.BeginAnimation(UIElement.OpacityProperty,null);
            overviewHost.Opacity=show ? 1 : 0;
            overviewHost.Visibility=Visible(show);
            keyboardGlyph.BeginAnimation(UIElement.OpacityProperty,null); keyboardGlyph.Opacity=show ? 0 : 1;
            controllerGlyph.BeginAnimation(UIElement.OpacityProperty,null); controllerGlyph.Opacity=show ? 1 : 0;
        }
    }
    public bool DeleteOpen { get { return Find<Grid>("DeleteOverlay").Visibility==Visibility.Visible; } }
    public void CloseDelete() {
        Find<Grid>("DeleteOverlay").Visibility=Visibility.Collapsed;
        Find<Grid>("LauncherContent").IsEnabled=true;
        Find<Button>("DeleteSlot").Focus();
    }
    static bool Inside(FrameworkElement element,DependencyObject source) {
        var content=source as FrameworkContentElement;
        if(content!=null) source=content.Parent;
        return source is Visual && (source==element || element.IsAncestorOf(source));
    }
    bool CanRecordMouse(MouseEventArgs e) {
        if(!Editing || !armed || !Inside(target,e.OriginalSource as DependencyObject)) return false;
        // During a drag WPF can route presses to the captured control after the pointer leaves it.
        return Mouse.Captured==null || target.InputHitTest(e.GetPosition(target))!=null;
    }
    void MouseDown(object sender,MouseButtonEventArgs e) {
        if(!active) return;
        var source=e.OriginalSource as DependencyObject;
        string id="Mouse."+e.ChangedButton;
        if(CanRecordMouse(e)) { Capture(id); e.Handled=true; }
        else if(preview.Enabled && (Inside(surface,source) || Inside(overviewHost,source))) {
            FocusPreview();
            if(overviewShown) keyboardView.Press(id,true);
            e.Handled=preview.Press(id,true);
        }
    }
    public void RefreshLanguage() {
        Find<TextBlock>("ControllerTitle").Text=Ui.Text(active ? "键鼠映射" : "控制器");
        if(!active || finishing) return;
        if(!Editing) { ShowSlots(false); return; }
        var step=MappingStep.All[Index];
        Find<TextBlock>("StepName").Text=slots.Name(slot)+"  ·  "+Ui.Text(step.Label);
        next.ToolTip=Ui.Text(Index==MappingStep.All.Length-1 ? "保存配置" : "下一步");
        AutomationProperties.SetName(next,(string)next.ToolTip);
        RefreshInput();
    }
    public void SetMode(bool show) {
        EndRename(true);
        ++revision; active=show; armed=false; slot=-1;
        finishTimer.Stop(); finishing=false; preview.Use(null);
        Find<Border>("BindingSuccess").Visibility=Visibility.Collapsed;
        panel.Visibility=Visible(show);
        Find<TextBlock>("ControllerTitle").Text=Ui.Text(show ? "键鼠映射" : "控制器");
        Find<StackPanel>("PadFooter").Visibility=Visible(!show);
        Find<TextBlock>("PaddleInput").Visibility=Visible(!show);
        if(show) {
            ShowSlots(false);
            if(overviewHost.Child==null) window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>{
                if(active && !Editing && !finishing && overviewHost.Child==null)
                    keyboardView.Refresh(slots.Profile ?? Array.Empty<string>());
            }));
        }
        else {
            SetOverview(false);
            StopDemo(); viewer.Guide(null);
            PlaceModel(false,false);
        }
    }
    void LayoutControls() {
        double bottom=Math.Min(92,panel.ActualHeight*.098);
        strip.Margin=new Thickness(0,0,0,bottom);
        caption.MaxWidth=Math.Max(1,panel.ActualWidth-88);
        Find<ScrollViewer>("SlotScroll").MaxHeight=Math.Max(55,panel.ActualHeight-175);
        Find<Border>("BindingSuccess").Margin=new Thickness(0,0,0,bottom+10);
    }
    void PlaceModel(bool editing,bool animate) {
        bool browsing=active && !editing && !finishing;
        Find<Grid>("MappingStage").Margin=browsing
            ? new Thickness(12,45,92,Math.Max(44,caption.ActualHeight)+31) : new Thickness(0);
        var old=surface.Margin;
        var margin=active && !browsing ? new Thickness(0,0,0,panel.ActualHeight*.12) : new Thickness(0);
        double x=modelSlide.X+(margin.Right-old.Right)/2;
        double y=modelSlide.Y+(old.Top-margin.Top+margin.Bottom-old.Bottom)/2;
        surface.Margin=margin;
        modelSlide.BeginAnimation(TranslateTransform.XProperty,animate && SystemParameters.ClientAreaAnimation
            ? ControllerMotion.Spring(x,-x*.04,0,300) : null);
        modelSlide.BeginAnimation(TranslateTransform.YProperty,animate && SystemParameters.ClientAreaAnimation
            ? ControllerMotion.Spring(y,-y*.04,0,300) : null);
    }
    void ShowSlots(bool animate) {
        EndRename(true);
        ++revision; slot=-1; armed=false; finishing=false; finishTimer.Stop();
        Find<Border>("BindingSuccess").Visibility=Visibility.Collapsed;
        StopDemo(); viewer.Guide(null);
        ApplyPreview();
        SetOverview(false); PlaceModel(false,animate);
        if(Configured) FocusPreview();
        strip.Visibility=Visibility.Collapsed;
        Find<Button>("ResetBindings").Visibility=Visibility.Collapsed;
        caption.Visibility=Visibility.Visible;
        var list=Find<StackPanel>("SlotList"); list.Children.Clear();
        if(slots.HasGtaVi) list.Children.Add(Tile(BindingSlots.GtaVi,false));
        int free=-1;
        for(int i=0;i<BindingSlots.Limit;i++) {
            if(slots.Saved(i)) list.Children.Add(Tile(i,false)); else if(free<0) free=i;
        }
        if(free>=0 && list.Children.Count<BindingSlots.Limit) list.Children.Add(Tile(free,true));
        UpdateSelection(animate);
        rail.Visibility=Visibility.Visible; rail.IsHitTestVisible=true;
        railSlide.BeginAnimation(TranslateTransform.XProperty,animate && SystemParameters.ClientAreaAnimation
            ? ControllerMotion.Spring(45,-3,0,290) : null);
        ControllerMotion.Fade(rail,animate ? .35 : 1,1,animate ? 165 : 0);
    }
    void UpdateSelection(bool animate) {
        SetPromptContext(promptProject,promptLocked);
        Find<Button>("DeleteSlot").Visibility=Configured ? Visibility.Visible : Visibility.Collapsed;
        Find<Button>("RenameSlot").Visibility=Visible(Configured && !UsingGtaVi);
        status.Text=Configured ? slots.Name(slots.Active) : Ui.Text("暂未配置");
        RefreshNameplate();
        Find<Grid>("ProfileLabel").Cursor=Configured && !UsingGtaVi ? Cursors.IBeam : Cursors.Arrow;
        foreach(Button button in Find<StackPanel>("SlotList").Children) {
            int index=(int)button.Tag;
            bool selected=slots.Active==index;
            bool add=!slots.Saved(index);
            string help=index==BindingSlots.GtaVi ? PresetHelp(false) : add ? Ui.Text("新建配置") : slots.Name(index)+" · "+Ui.Format(
                selected || !slots.Ready(index) ? "编辑配置 {0}" : "使用配置 {0}",index+1);
            if(!add && !slots.Ready(index)) help+=" · "+Ui.Text("旧键位待补全");
            if(add && !slots.HasGtaVi) help+="\n"+Ui.Text("右键可恢复 GTA6 定制");
            button.ToolTip=help;
            AutomationProperties.SetName(button,help);
            AutomationProperties.SetItemStatus(button,selected ? Ui.Text("当前配置") : help);
            var glow=(Border)button.Template.FindName("glow",button);
            double opacity=selected ? .94 : 0;
            ControllerMotion.Fade(glow,glow.Opacity,opacity,animate ? 235 : 0);
            MappingFeedback.Scale((ScaleTransform)button.RenderTransform,selected ? 1.065 : 1,animate ? 310 : 0);
        }
    }
    void RefreshNameplate() {
        Find<System.Windows.Shapes.Path>("PresetMark").Visibility=Visible(UsingGtaVi);
        status.ToolTip=UsingGtaVi ? PresetHelp(true) : Configured ? Ui.Text("双击修改配置名称") : null;
        AutomationProperties.SetHelpText(status,status.ToolTip as string ?? "");
    }
    string PresetHelp(bool currentLayer) {
        string foot=Ui.Text("步行 · WASD 移动，右键瞄准，左键射击");
        string drive=Ui.Text("驾驶 · W/S 油门与刹车，A/D 转向，空格手刹，左键射击");
        return Ui.Text("GTA6 定制")+" · "+Ui.Text("只读配置")+"\n"+
            (currentLayer ? (preview.Driving ? drive : foot) : foot+"\n"+drive)+"\n"+
            Ui.Text("方向键浏览菜单，Enter 确认，Backspace 返回。")+"\n"+
            Ui.Text("F6 切换步行和驾驶。长按 Alt 切换角色。");
    }
    void ApplyPreview() {
        if(UsingGtaVi) preview.UseGtaVi(); else preview.Use(slots.Profile);
    }
    Button Tile(int index,bool add) {
        var button=new Button { Width=47,Height=47,Margin=new Thickness(4,5,4,5),Padding=new Thickness(0),
            Style=(Style)window.FindResource("mappingTile"),Content=add ? "＋" : (index+1).ToString("00"),
            Tag=index,Background=new SolidColorBrush(add ? Color.FromRgb(49,48,56) : Color.FromRgb(72,66,80)),
            RenderTransform=new ScaleTransform(), RenderTransformOrigin=new Point(.5,.5) };
        if(index==BindingSlots.GtaVi) {
            // Single-colour silhouette of Assets/VI.png, kept as geometry for high DPI.
            button.Background=(Brush)window.FindResource("viceCitySlot");
            button.Content=new System.Windows.Shapes.Path { Width=29, Height=23, Stretch=Stretch.Uniform,
                Fill=Brushes.White, Data=(Geometry)window.FindResource("gtaViMark"),
                Effect=new System.Windows.Media.Effects.DropShadowEffect {
                    Color=Color.FromRgb(47,27,66),BlurRadius=3,ShadowDepth=1.2,Direction=270,Opacity=.82 } };
        }
        if(add && !slots.HasGtaVi) {
            var menu=new ContextMenu { Style=(Style)window.FindResource("profileMenu"),FontFamily=window.FontFamily };
            var restore=new MenuItem { Header=Ui.Text("恢复 GTA6 定制") };
            restore.Click+=delegate {
                try { slots.RestoreGtaVi(); ShowSlots(true); saved(); }
                catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException) {
                    status.Text=Ui.Text("恢复失败，请重试。");
                }
            };
            menu.Items.Add(restore); button.ContextMenu=menu;
        }
        MappingFeedback.AttachHover(button);
        button.Click+=delegate {
            if(index==BindingSlots.GtaVi && UsingGtaVi) { FocusPreview(); return; }
            if(add || index==slots.Active || !slots.Ready(index)) { Start(index); return; }
            try { slots.Select(index); ApplyPreview(); SetOverview(false); FocusPreview(); UpdateSelection(true); saved(); }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException) {
                Find<TextBlock>("SlotStatus").Text=Ui.Text("切换配置失败，请重试。");
            }
        };
        return button;
    }
    void BeginRename() {
        if(!Configured || UsingGtaVi) return;
        renameSlot=slots.Active; preview.Clear();
        nameEditor.Text=slots.Name(renameSlot);
        status.Visibility=Visibility.Collapsed; Find<Border>("NameField").Visibility=Visibility.Visible;
        nameEditor.Focus(); nameEditor.SelectAll();
    }
    void EndRename(bool save) {
        if(renameSlot<0) return;
        int index=renameSlot; renameSlot=-1;
        Find<Border>("NameField").Visibility=Visibility.Collapsed; status.Visibility=Visibility.Visible;
        if(!save) return;
        try { slots.Rename(index,nameEditor.Text); UpdateSelection(false); }
        catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is System.Xml.XmlException || ex is ArgumentException) {
            status.Text=Ui.Text("名称保存失败，请重试。");
        }
    }
    void Start(int index) {
        if(index==BindingSlots.GtaVi || !active || Editing || finishing) return;
        preview.Use(null);
        slot=index; slots.Edit(slot); int current=++revision;
        SetOverview(false); viewer.ResetView();
        rail.IsHitTestVisible=false;
        railSlide.BeginAnimation(TranslateTransform.XProperty,SystemParameters.ClientAreaAnimation
            ? new DoubleAnimation(railSlide.X,75,TimeSpan.FromMilliseconds(180)) {
                EasingFunction=new BackEase {EasingMode=EasingMode.EaseIn,Amplitude=.38},FillBehavior=FillBehavior.HoldEnd } : null);
        ControllerMotion.Fade(rail,rail.Opacity,0,185,delegate { if(revision==current) rail.Visibility=Visibility.Collapsed; });
        caption.Visibility=Visibility.Collapsed;
        Find<Button>("ResetBindings").Visibility=Visibility.Visible;
        strip.Visibility=Visibility.Visible; PlaceModel(true,true);
        ControllerMotion.Reveal(strip,.96,1.013,12,310,.15);
        DisplayStep();
    }
    public bool Back() {
        if(!Editing) return false;
        ShowSlots(true); return true;
    }
    void StopDemo() {
        press.BeginAnimation(ScaleTransform.ScaleXProperty,null); press.BeginAnimation(ScaleTransform.ScaleYProperty,null);
        feedback.Waiting(false);
    }
    void DisplayStep() {
        target.RenderTransform=Transform.Identity;
        var step=MappingStep.All[Index];
        Find<TextBlock>("StepName").Text=slots.Name(slot)+"  ·  "+Ui.Text(step.Label);
        Find<TextBlock>("StepCount").Text=(Index+1).ToString("00")+" / "+MappingStep.All.Length;
        var cap=Find<Border>("ControlCap"); var symbol=Find<TextBlock>("ControlSymbol");
        symbol.Text=step.Symbol; symbol.FontSize=step.Symbol.Length>2 ? 12 : 18;
        string color=step.Id=="B" ? "#FFA2AC" : step.Id=="X" ? "#99D8FC" : step.Id=="Y" ? "#FADE8B" :
                     step.Id=="A" ? "#A3ECC2" : "#D6C7ED";
        cap.BorderBrush=symbol.Foreground=(Brush)new BrushConverter().ConvertFromString(color);
        if(SystemParameters.ClientAreaAnimation) {
            var pulse=new DoubleAnimation(1,.975,TimeSpan.FromMilliseconds(1220)) { AutoReverse=true,
                RepeatBehavior=RepeatBehavior.Forever,EasingFunction=new SineEase() };
            press.BeginAnimation(ScaleTransform.ScaleXProperty,pulse); press.BeginAnimation(ScaleTransform.ScaleYProperty,pulse);
        }
        viewer.Guide(step);
        prev.Visibility=Index>0 ? Visibility.Visible : Visibility.Hidden;
        bool last=Index==MappingStep.All.Length-1;
        next.ToolTip=Ui.Text(last ? "保存配置" : "下一步");
        AutomationProperties.SetName(next,(string)next.ToolTip);
        Find<System.Windows.Shapes.Path>("NextGlyph").Data=Geometry.Parse(last ? "M1,8 L6,13 16,2" : "M2,8 H14 M8,2 L14,8 8,14");
        armed=slots.Edit(slot)[Index]==null;
        RefreshInput();
        if(armed) target.Focus(); else next.Focus();
    }
    void Arm() { if(Editing) { armed=true; RefreshInput(); target.Focus(); } }
    void RefreshInput() {
        feedback.Waiting(armed);
        string value=slots.Edit(slot)[Index];
        Find<TextBlock>("BoundText").Text=armed ? "" : Ui.Text(Label(value));
        next.IsEnabled=value!=null && !armed;
        bool reveal=next.IsEnabled && next.Visibility!=Visibility.Visible;
        next.Visibility=next.IsEnabled ? Visibility.Visible : Visibility.Hidden;
        if(reveal) ControllerMotion.Reveal(next,.72,1.08,-4,270,.15);
        hint.Text=Ui.Text(armed ? "按下键盘按键，或在白色槽内按下鼠标键" : "点击键位重新录入");
    }
    public bool HandleKey(KeyEventArgs e) {
        Key key=e.Key==Key.System ? e.SystemKey : e.Key;
        if(renameSlot>=0) {
            if(key!=Key.Return && key!=Key.Escape) return false;
            EndRename(key==Key.Return); FocusPreview(); return true;
        }
        if(active && !Editing && !finishing && Configured && key==Key.F2 && rail.IsKeyboardFocusWithin) { BeginRename(); return true; }
        if(preview.Enabled && (surface.IsKeyboardFocusWithin || e.OriginalSource==surface || overviewHost.IsKeyboardFocusWithin)) {
            if(overviewShown) keyboardView.Press("Key."+key,true);
            return preview.Press("Key."+key,true);
        }
        if(!Editing || !armed) return false;
        if(key==Key.Escape) {
            if(slots.Edit(slot)[Index]==null) return false;
            armed=false; RefreshInput(); next.Focus(); return true;
        }
        if(key==Key.None || (!target.IsKeyboardFocusWithin && !surface.IsKeyboardFocusWithin &&
                             e.OriginalSource!=target && e.OriginalSource!=surface)) return false;
        if(MappedPadPreview.Walking("Key."+key)) hint.Text=Ui.Text("WASD 已用于行走，请选择其他按键");
        else if(!e.IsRepeat) Capture("Key."+key);
        return true;
    }
    void Capture(string value) {
        slots.Edit(slot)[Index]=value; armed=false; RefreshInput(); next.Focus();
        ControllerMotion.Reveal(target,.91,1.055,0,290,1);
    }
    void Advance() {
        if(!Editing || armed || slots.Edit(slot)[Index]==null) return;
        if(Index<MappingStep.All.Length-1) { slots.Position[slot]++; DisplayStep(); return; }
        try { slots.Save(slot); }
        catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException) {
            hint.Text=Ui.Text("配置保存失败，可重试。")+" "+ex.Message; return;
        }
        slot=-1; finishing=true; ++revision; StopDemo(); viewer.FinishGuide();
        strip.Visibility=Visibility.Collapsed;
        Find<Button>("ResetBindings").Visibility=Visibility.Collapsed;
        feedback.Complete();
        saved(); finishTimer.Start();
    }
    static string Label(string value) {
        if(value==null) return "";
        switch(value) {
            case "Key.Space":return "空格"; case "Key.Return":return "Enter";
            case "Mouse.Left":return "鼠标左键"; case "Mouse.Right":return "鼠标右键"; case "Mouse.Middle":return "鼠标中键";
            case "Mouse.XButton1":return "鼠标侧键 1"; case "Mouse.XButton2":return "鼠标侧键 2";
            case "Wheel.Up":return "滚轮上滑"; case "Wheel.Down":return "滚轮下滑";
            default:return value.StartsWith("Key.") ? value.Substring(4) : value;
        }
    }
}
