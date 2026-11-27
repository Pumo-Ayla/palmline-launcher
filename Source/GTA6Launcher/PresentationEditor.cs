using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Launcher.Layout;
using Microsoft.Win32;

internal sealed partial class DesktopLauncher {
    void RefreshGameTile(GameInstance game) {
        int i=gameTiles.FindIndex(t=>((GameInstance)t.Tag).Id==game.Id);
        var list=Find<StackPanel>("GameList");
        list.Children.Remove(gameTiles[i]);
        var tile=CreateGameTile(game);
        list.Children.Remove(tile); list.Children.Insert(i,tile); gameTiles[i]=tile;
    }
    BitmapSource LoadOptionalImage(GameInstance game,string name,int limit) {
        if(game?.ArtworkId==null) return null;
        string path=Path.Combine(CardArtFolder(game),name);
        try { return File.Exists(path) ? DecodeCardImage(path,limit) : null; }
        catch(Exception e) when(IsCardFileError(e)) { Append(e.Message); return null; }
    }
    string[] LobbyFiles(GameInstance game) {
        if(game?.ArtworkId==null) return Array.Empty<string>();
        string folder=CardArtFolder(game);
        return Directory.Exists(folder) ? Directory.GetFiles(folder,"lobby-*").OrderBy(p=>p,StringComparer.Ordinal).ToArray() : Array.Empty<string>();
    }
    void UpdateIconPreview() {
        string name=Find<TextBox>("ImportName").Text.Trim();
        Find<TextBlock>("IconInitial").Text=draftIcon==null && name.Length>0 ? StringInfo.GetNextTextElement(name) : "";
        Find<Image>("IconPreview").Source=draftIcon;
        bool preview=importDraft!=null && (draftIcon!=null || name.Length>0);
        SetImportVisible("DraftRailFace",preview); SetImportVisible("ImportRailDots",!preview);
    }
    void DraftChanged() {
        detailChecked=false;
        if(importStep==2) { importActionFailed=false; Find<TextBlock>("ImportStatus").Text=""; UpdateImportStep(); }
        else RefreshImportAction();
    }
    string DraftProfile() {
        return Find<RadioButton>("ChooseXcl").IsChecked==true ? "xcl" :
            Find<RadioButton>("ChooseOptimized").IsChecked==true && Find<CheckBox>("UseBundledProfile").IsChecked==true ? OptimizedProfile : null;
    }
    void ConfigureDraftEngines() {
        var general=Find<RadioButton>("ChooseXcl"); var optimized=Find<RadioButton>("ChooseOptimized");
        optimized.IsEnabled=importDraft.Compatible.Any(GtaOnlyEngine);
        optimized.IsChecked=optimized.IsEnabled && importDraft.LauncherProfile!="xcl";
        general.IsChecked=optimized.IsChecked!=true;
        Find<CheckBox>("UseBundledProfile").IsChecked=optimized.IsChecked==true && importDraft.LauncherProfile==OptimizedProfile;
        UpdateDraftOptions();
    }
    void UpdateDraftOptions() {
        bool optimized=Find<RadioButton>("ChooseOptimized").IsChecked==true;
        SetImportVisible("UseBundledProfile",optimized);
        Find<RadioButton>("ChooseXcl").IsEnabled=catalog.ContainsKey("xcl") && DraftProfile()!=OptimizedProfile;
        DraftChanged();
    }
    string RuntimeIssue(string profile) {
        var engines=ProfileEngines(profile);
        var missing=engines.Where(id=>!File.Exists(Path.Combine(root,"Community",id,"runtime",
            (string)catalog[id].Attribute("commit"),id=="xcl" ? "xcl.exe" : "victory.exe"))).ToArray();
        return engines.Length>0 && missing.Length==0 ? null :
            Ui.Text("找不到模拟器运行组件")+"　"+string.Join(" · ",missing.Select(ProjectName));
    }
    async Task CheckDraftDetails() {
        if(checking || !CanAdvanceImport()) return;
        string profile=DraftProfile();
        checking=true; importActionFailed=false;
        Find<TextBlock>("ImportStatus").Text="";
        var controls=new[]{"ImportBack","ImportArtworkFields","ImportNameRow","ArtTools","PreviewLogoArea"};
        foreach(string name in controls) Find<UIElement>(name).IsEnabled=false;
        RefreshImportAction();
        try {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            string issue=await Task.Run(()=>RuntimeIssue(profile));
            detailChecked=issue==null;
            importActionFailed=!detailChecked;
            Find<TextBlock>("ImportStatus").Text=issue ?? Ui.Text("检查通过");
        } finally {
            checking=false;
            foreach(string name in controls) Find<UIElement>(name).IsEnabled=true;
            if(!closed) UpdateImportStep();
        }
    }
    int previewState;
    bool lobbyExpanded;
    void SetPreviewState(int state) {
        previewState=state;
        string[] controls={"PreviewFresh","PreviewPlayed","PreviewAwards"};
        for(int i=0;i<controls.Length;i++) Find<Button>(controls[i]).Background=(Brush)window.FindResource(i==state ? "previewActive" : "previewTool");
        SetImportVisible("PreviewRecord",state==0);
        SetImportVisible("PreviewStats",state!=0);
        SetImportVisible("PreviewShelf",state==2 && (importStep!=3 || lobbyExpanded));
    }
    void SetupPresentationEditor() {
        string[] modes={"PreviewFresh","PreviewPlayed","PreviewAwards"};
        for(int i=0;i<modes.Length;i++) { int state=i; Find<Button>(modes[i]).Click+=delegate { SetPreviewState(state); }; }
        foreach(string name in new[]{"ChooseXcl","ChooseOptimized"}) Find<RadioButton>(name).Checked+=delegate { UpdateDraftOptions(); };
        var version=Find<CheckBox>("UseBundledProfile"); version.Content=BundledVersionName;
        version.Checked+=delegate { UpdateDraftOptions(); };
        version.Unchecked+=delegate { UpdateDraftOptions(); };
        var iconPicker=Find<Button>("ChooseGameIcon");
        iconPicker.Click+=delegate {
            var dialog=new OpenFileDialog { Filter="PNG, JPEG, BMP|*.png;*.jpg;*.jpeg;*.bmp" };
            if(dialog.ShowDialog(window)!=true) return;
            try { LobbyArtwork.Check(dialog.FileName); var image=DecodeCardImage(dialog.FileName,256);
                if(image.PixelWidth!=image.PixelHeight) throw new InvalidDataException(Ui.Text("游戏图标需要正方形图片"));
                draftIcon=image; UpdateIconPreview(); DraftChanged();
            } catch(Exception ex) when(IsCardFileError(ex)) { Find<TextBlock>("ImportStatus").Text=ex.Message; }
        };
        var resetIcon=new MenuItem();
        resetIcon.SetResourceReference(HeaderedItemsControl.HeaderProperty,"ui.使用首字");
        resetIcon.Click+=delegate { draftIcon=null; UpdateIconPreview(); DraftChanged(); };
        iconPicker.ContextMenu=new ContextMenu { Style=(Style)window.FindResource("profileMenu"),Items={resetIcon} };
        iconPicker.ContextMenuOpening+=(sender,args)=>{ if(draftIcon==null) args.Handled=true; };
        var area=Find<Canvas>("PreviewLogoArea"); var logo=Find<Image>("PreviewLogo"); var handle=Find<Border>("ResizeLogo");
        area.SizeChanged+=delegate { PositionPreviewLogo(); };
        var vertical=Find<System.Windows.Shapes.Rectangle>("SnapVertical");
        var horizontal=Find<System.Windows.Shapes.Rectangle>("SnapHorizontal");
        Point start=new Point(),origin=new Point(); double width=0; bool resize=false;
        MouseButtonEventHandler down=(sender,args)=>{
            if(importDraft==null || logo.Source==null) return;
            resize=sender==handle; start=args.GetPosition(area); origin=new Point(Canvas.GetLeft(logo),Canvas.GetTop(logo)); width=logo.Width;
            area.CaptureMouse(); args.Handled=true;
        };
        logo.MouseLeftButtonDown+=down; handle.MouseLeftButtonDown+=down;
        area.MouseMove+=delegate(object sender,MouseEventArgs args) {
            if(!area.IsMouseCaptured || importDraft==null || area.ActualWidth<1) return;
            var delta=args.GetPosition(area)-start;
            if(resize) {
                double ratio=logo.Source.Width/logo.Source.Height;
                double growth=Math.Abs(delta.X)>Math.Abs(delta.Y) ? delta.X : delta.Y*ratio;
                importDraft.LogoSize=Math.Max(.12,Math.Min(.92,(width+growth)/area.ActualWidth));
            }
            var box=CardLogoSize(area.ActualWidth,area.ActualHeight,importDraft.LogoSize,logo.Source);
            double Position(double edge,double space,double size,System.Windows.Shapes.Rectangle guide,bool x) {
                double remaining=Math.Max(0,space-size); edge=Math.Max(0,Math.Min(remaining,edge));
                guide.Visibility=Visibility.Collapsed;
                foreach(double anchor in new[]{0d,.5,1d}) {
                    if(Math.Abs(edge-remaining*anchor)>6) continue;
                    edge=remaining*anchor; guide.Visibility=Visibility.Visible;
                    if(x) { guide.Height=area.ActualHeight; Canvas.SetLeft(guide,Math.Min(space-1,space*anchor)); }
                    else { guide.Width=area.ActualWidth; Canvas.SetTop(guide,Math.Min(space-1,space*anchor)); }
                    break;
                }
                return remaining>0 ? edge/remaining : 0;
            }
            importDraft.LogoX=Position(origin.X+(resize ? 0 : delta.X),area.ActualWidth,box.Width,vertical,true);
            importDraft.LogoY=Position(origin.Y+(resize ? 0 : delta.Y),area.ActualHeight,box.Height,horizontal,false);
            PositionPreviewLogo(); if(detailChecked) DraftChanged();
        };
        area.MouseLeftButtonUp+=delegate { if(area.IsMouseCaptured) area.ReleaseMouseCapture(); };
        area.LostMouseCapture+=delegate { vertical.Visibility=horizontal.Visibility=Visibility.Collapsed; };
        RoundClip(Find<Grid>("LobbyCard"),13);
        Find<Button>("LobbyCardToggle").Click+=delegate { SetLobbyCardExpanded(!lobbyExpanded,true); };
        Find<Grid>("LobbyCardArea").SizeChanged+=delegate {
            if(importStep==3 && lobbyExpanded) SetLobbyCardExpanded(true,false);
        };
        Find<Button>("LobbyBack").Click+=delegate { LobbyStep(false); };
        Find<Button>("ClearLobby").Click+=delegate { draftLobby=Array.Empty<string>(); RefreshLobbyEditor(); };
        Find<Button>("ChooseLobby").Click+=delegate {
            var dialog=new OpenFileDialog { Multiselect=true,Filter="PNG, JPEG, BMP, GIF|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
            if(dialog.ShowDialog(window)!=true) return;
            try { foreach(var p in dialog.FileNames) LobbyArtwork.Check(p); draftLobby=dialog.FileNames; RefreshLobbyEditor(); }
            catch(Exception ex) when(IsCardFileError(ex)) { Find<TextBlock>("LobbyStatus").Text=ex.Message; }
        };
        Find<Button>("FinishLobby").Click+=delegate {
            if(importStep!=3 || importDraft==null) return;
            try { foreach(var path in draftLobby) LobbyArtwork.Check(path);
                FinishCardImport();
            } catch(Exception ex) when(IsCardFileError(ex)) { Find<TextBlock>("LobbyStatus").Text=ex.Message; }
        };
        SetupInstallActions();
    }
    void RefreshLobbyEditor() {
        Find<TextBlock>("LobbyStatus").Text=draftLobby.Length==0 ? Ui.Text("默认显示游戏图标和名称") : string.Join(" · ",draftLobby.Select(Path.GetFileName));
        Find<LobbyArtwork>("LobbyPreview").SetFiles(draftLobby);
        SetImportVisible("LobbyPlaceholder",draftLobby.Length==0);
        Find<Image>("LobbyMark").Source=Find<Image>("PreviewLogo").Source;
    }
    void SetLobbyCardExpanded(bool expanded,bool animate) {
        lobbyExpanded=expanded;
        var card=Find<Grid>("LobbyCard");
        SetPreviewState(previewState);
        var summary=Find<StackPanel>("PreviewSummary");
        bool folded=importStep==3 && !expanded;
        summary.Margin=folded ? Find<StackPanel>("CaseSummary").Margin : new Thickness(18,0,18,18);
        summary.Measure(new Size(Math.Max(1,Find<Grid>("LobbyCardArea").Width),double.PositiveInfinity));
        double height=expanded ? Math.Max(96,Find<Grid>("LobbyCardArea").ActualHeight) : summary.DesiredSize.Height;
        double from=card.Height;
        bool motion=animate && SystemParameters.ClientAreaAnimation;
        card.Height=height;
        card.BeginAnimation(FrameworkElement.HeightProperty,motion ? ControllerMotion.Spring(from,height+(expanded ? 4 : -2),height,340) : null);
        var toggle=Find<Button>("LobbyCardToggle");
        string caption=expanded ? "ui.收起封面" : "ui.展开封面";
        toggle.SetResourceReference(FrameworkElement.ToolTipProperty,caption);
        toggle.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty,caption);
        var turn=(RotateTransform)((FrameworkElement)toggle.Content).RenderTransform;
        double angle=turn.Angle;
        turn.Angle=expanded ? 180 : 0;
        turn.BeginAnimation(RotateTransform.AngleProperty,motion ? new DoubleAnimation(angle,turn.Angle,TimeSpan.FromMilliseconds(240)){FillBehavior=FillBehavior.Stop} : null);
        var art=Find<Grid>("PreviewArt");
        var mark=Find<Image>("LobbyMark");
        if(motion) {
            ControllerMotion.Fade(art,art.Opacity,expanded ? 1 : 0,200);
            ControllerMotion.Fade(mark,mark.Opacity,expanded ? 0 : 1,200);
        } else {
            art.BeginAnimation(UIElement.OpacityProperty,null); art.Opacity=expanded ? 1 : 0;
            mark.BeginAnimation(UIElement.OpacityProperty,null); mark.Opacity=expanded ? 0 : 1;
        }
    }
    void MountPreviewCard(bool inLobby) {
        var card=Find<Grid>("PreviewCardFrame");
        var host=Find<Grid>(inLobby ? "LobbyCardHost" : "DetailCardHost");
        if(card.Parent!=host) { ((Grid)card.Parent).Children.Remove(card); host.Children.Add(card); }
        SetLobbyCardExpanded(false,false);
        if(!inLobby) {
            Find<Grid>("PreviewArt").Opacity=1;
            SetImportVisible("PreviewShelf",previewState==2);
        }
        PositionPreviewLogo();
    }
    void LobbyStep(bool open,bool animate=true) {
        importStep=open ? 3 : 2;
        var split=Find<Grid>("ImportSplit"); double from=split.ActualWidth,to=open ? 0 : ImportWidth(1,true);
        split.IsHitTestVisible=!open; split.Width=to;
        split.BeginAnimation(FrameworkElement.WidthProperty,animate && SystemParameters.ClientAreaAnimation ?
            new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(270)){FillBehavior=FillBehavior.Stop,EasingFunction=new CubicEase{EasingMode=EasingMode.EaseInOut}} : null);
        SetImportVisible("LobbyEditor",open);
        MountPreviewCard(open);
        SetImportVisible("SourceForm",!open);
        SetImportVisible("ImportBack",!open);
        if(open) { RefreshLobbyEditor(); Find<Button>("ChooseLobby").Focus(); }
        else { Find<LobbyArtwork>("LobbyPreview").SetFiles(Array.Empty<string>()); UpdateImportStep(); }
    }
}
