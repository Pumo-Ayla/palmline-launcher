using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Launcher.Layout;

internal sealed partial class DesktopLauncher
{
    int importStep;
    bool detailChecked;
    GameInstance importDraft;
    BitmapSource draftCover, draftLogo;
    Brush defaultPrint;
    ImageSource defaultMark;
    BitmapSource draftIcon;
    string[] draftLobby=Array.Empty<string>();
    ImageSource PlaceholderCover => (ImageSource)window.FindResource("placeholderCover");
    ImageSource PlaceholderLogo => (ImageSource)window.FindResource("placeholderLogo");
    bool CyberleekSelected => Find<CheckBox>("CyberleekPreset").IsChecked==true;

    void SetupImportCard() {
        SetupExeSourceSwitch();
        defaultPrint=Find<System.Windows.Shapes.Rectangle>("CoverPrint").Fill;
        defaultMark=Find<Image>("GameMark").Source;
        var preset=Find<CheckBox>("CyberleekPreset");
        preset.Tag=defaultMark; preset.Content=BundledVersionName;
        AutomationProperties.SetName(preset,BundledVersionName);
        var sources=Find<System.Windows.Controls.Primitives.UniformGrid>("ImportSources");
        sources.SizeChanged+=delegate { sources.Columns=sources.ActualWidth<380 ? 1 : 2; };
        AnimateWaitingDots(Find<StackPanel>("RecognitionDots"));
        Find<Grid>("ImportRailIcon").Children.Insert(1,LibraryCover(new LinearGradientBrush(
            Color.FromRgb(75,68,83),Color.FromRgb(49,45,56),90)));
        AnimateWaitingDots(Find<StackPanel>("ImportRailDots"));
        var surface=Find<Grid>("ImportSurface");
        surface.SizeChanged+=delegate {
            double radius=Math.Min(17,Math.Min(surface.ActualWidth,surface.ActualHeight)*.5);
            surface.Clip=new RectangleGeometry(new Rect(surface.RenderSize),radius,radius);
            PositionPreviewLogo();
        };
        var viewport=Find<Grid>("ImportViewport");
        viewport.SizeChanged+=delegate {
            bool compact=viewport.ActualHeight<530 || viewport.ActualWidth<360;
            if(compact==compactImport) return;
            compactImport=compact;
            archiveDestinationPage=compact && (Find<RadioButton>("KeepArchive").IsChecked==true || Find<RadioButton>("DeleteArchive").IsChecked==true);
            UpdateImportStep();
        };
        var output=Find<TextBox>("ArchiveOutputPath");
        output.TextChanged+=delegate { InvalidateFileCheck(); RefreshImportAction(); };
        Find<Button>("PickArchiveOutput").Click+=delegate {
            string folder=PickOutputFolder(output.Text);
            if(folder!=null) output.Text=folder;
        };
        AnimateWaitingDots(Find<StackPanel>("TransferWaitDots"));
        foreach(var gridName in new[]{"ArchiveModeCards","ExePresenceCards"}) {
            var cards=Find<System.Windows.Controls.Primitives.UniformGrid>(gridName);
            cards.SizeChanged+=delegate { cards.Columns=cards.ActualWidth<340 ? 1 : 2; };
        }
        var consent=Find<CheckBox>("ReleaseVolumes");
        foreach(var name in new[]{"KeepArchive","DeleteArchive"}) {
            var choice=Find<RadioButton>(name);
            choice.Checked+=delegate { InvalidateFileCheck(); consent.IsChecked=false; UpdateImportStep(); };
            choice.Click+=delegate {
                if(compactImport && !archiveDestinationPage) ShowArchiveDestination(true);
            };
        }
        RoutedEventHandler consentChanged=delegate { InvalidateFileCheck(); RefreshImportAction(); };
        consent.Checked+=consentChanged;
        consent.Unchecked+=consentChanged;
        Find<TextBox>("ImportName").TextChanged+=delegate { UpdateIconPreview(); DraftChanged(); };
        var exe=Find<TextBox>("ExePath");
        exe.TextChanged+=delegate { exe.Tag=ExeSourceCaption(exe.Text); InvalidateFileCheck(); RefreshImportAction(); };
        foreach(string option in new[]{"OwnExe","BuiltinExe","IncludesExe","SupplyExe"})
            Find<RadioButton>(option).Checked+=delegate { InvalidateFileCheck(); UpdateImportStep(); };
        Find<ListBox>("BuiltinList").SelectionChanged+=delegate { InvalidateFileCheck(); RefreshImportAction(); };
        foreach(string field in new[]{"AssetPath","MagnetLink"})
            Find<TextBox>(field).TextChanged+=delegate { if(importOpen && importStep==1) { ResetSourceCheck(); UpdateImportStep(); } };
        var link=Find<TextBox>("MagnetLink");
        link.TextChanged+=delegate { link.Tag=MagnetCaption(link.Text); };
        SetupPresentationEditor();
        Find<Button>("ChooseCover").Click+=delegate { ChooseCardArt(false); };
        Find<Button>("PickDetailCover").Click+=delegate { ChooseCardArt(false); };
        Find<Button>("ChooseLogo").Click+=delegate { ChooseCardArt(true); };

    }

    static void AnimateWaitingDots(StackPanel dots) {
        dots.IsVisibleChanged+=delegate {
            for(int i=0;i<dots.Children.Count;i++) {
                var dot=dots.Children[i];
                var pulse=new DoubleAnimation(.28,1,TimeSpan.FromMilliseconds(460)) {
                    BeginTime=TimeSpan.FromMilliseconds(145*i), AutoReverse=true, RepeatBehavior=RepeatBehavior.Forever,
                    EasingFunction=new SineEase { EasingMode=EasingMode.EaseInOut }
                };
                dot.BeginAnimation(UIElement.OpacityProperty,dots.IsVisible && SystemParameters.ClientAreaAnimation ? pulse : null);
            }
        };
    }

    void SetupExeSourceSwitch() {
        var track=Find<Grid>("ExeSourceTrack");
        var thumb=Find<Border>("ExeSourceThumb");
        var move=(TranslateTransform)thumb.RenderTransform;
        var own=Find<RadioButton>("OwnExe");
        var bundled=Find<RadioButton>("BuiltinExe");
        double start=0, origin=0;
        bool dragging=false;

        void Position(double target,bool animate) {
            double from=move.X;
            move.X=target;
            move.BeginAnimation(TranslateTransform.XProperty,animate && SystemParameters.ClientAreaAnimation && Math.Abs(from-target)>.1
                ? new DoubleAnimation(from,target,TimeSpan.FromMilliseconds(170)) {
                    EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut }, FillBehavior=FillBehavior.Stop
                } : null);
        }
        void Sync(bool animate) {
            bool selected=own.IsChecked==true || bundled.IsChecked==true;
            if(selected) Position(bundled.IsChecked==true ? track.ActualWidth/2 : 0,animate && thumb.Opacity>0);
            thumb.Opacity=selected ? 1 : 0;
        }
        own.Checked+=delegate { Sync(true); };
        bundled.Checked+=delegate { Sync(true); };
        RoutedEventHandler clear=delegate {
            track.Dispatcher.BeginInvoke(new Action(()=>{ if(own.IsChecked!=true && bundled.IsChecked!=true) Sync(false); }));
        };
        own.Unchecked+=clear; bundled.Unchecked+=clear;
        track.SizeChanged+=delegate { Sync(false); };
        track.PreviewMouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e) {
            if(!track.CaptureMouse()) return;
            start=e.GetPosition(track).X;
            origin=thumb.Opacity>0 ? move.X : start<track.ActualWidth/2 ? 0 : track.ActualWidth/2;
            dragging=false;
            e.Handled=true;
        };
        track.PreviewMouseMove+=delegate(object sender,MouseEventArgs e) {
            if(!track.IsMouseCaptured) return;
            double delta=e.GetPosition(track).X-start;
            if(!dragging && Math.Abs(delta)<SystemParameters.MinimumHorizontalDragDistance) return;
            dragging=true;
            thumb.Opacity=1;
            Position(Math.Max(0,Math.Min(track.ActualWidth/2,origin+delta)),false);
        };
        track.PreviewMouseLeftButtonUp+=delegate(object sender,MouseButtonEventArgs e) {
            if(!track.IsMouseCaptured) return;
            Point point=e.GetPosition(track);
            if(dragging || new Rect(track.RenderSize).Contains(point)) {
                bool right=dragging ? move.X>=track.ActualWidth/4 : point.X>=track.ActualWidth/2;
                var choice=right ? bundled : own;
                if(choice.IsEnabled) { choice.Focus(); choice.IsChecked=true; }
            }
            track.ReleaseMouseCapture();
            e.Handled=true;
        };
        track.LostMouseCapture+=delegate { dragging=false; Sync(true); };
    }

    bool compactImport,archiveDestinationPage;

    void ShowArchiveDestination(bool forward) {
        archiveDestinationPage=forward;
        UpdateImportStep();
        SlideImportPage(forward);
        if(forward) Find<Button>("PickArchiveOutput").Focus();
        else Find<RadioButton>(Find<RadioButton>("DeleteArchive").IsChecked==true ? "DeleteArchive" : "KeepArchive").Focus();
    }

    void SlideImportPage(bool forward) {
        var body=Find<StackPanel>("ImportPageBody");
        var offset=(TranslateTransform)body.RenderTransform;
        offset.BeginAnimation(TranslateTransform.XProperty,SystemParameters.ClientAreaAnimation ?
            new DoubleAnimation(forward ? 18 : -18,0,TimeSpan.FromMilliseconds(190)) {
                EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut },FillBehavior=FillBehavior.Stop
            } : null);
    }

    const string ConsentRequired="请先确认删除源分卷的风险。";
    bool importActionFailed;

    string ImportRequirement() {
        bool local=Find<RadioButton>("LocalSource").IsChecked==true;
        if(importStep==0) return local || Find<RadioButton>("MagnetSource").IsChecked==true ? null : "选择导入方式";
        if(importStep==1) {
            if(verifiedFiles!=null) return null;
            if(sourceStage==SourceStage.Address) return string.IsNullOrWhiteSpace(Find<TextBox>(local ? "AssetPath" : "MagnetLink").Text)
                ? (local ? "请选择游戏本体目录。" : "请输入磁力链接。") : null;
            if(sourceStage==SourceStage.Archive) {
                if(compactImport && !archiveDestinationPage) return "选择解压方式";
                if(ArchiveCommand()==null) return Find<RadioButton>("DeleteArchive").IsChecked==true ? ConsentRequired : "选择解压方式";
                return Directory.Exists(Find<TextBox>("ArchiveOutputPath").Text) ? null : "请选择解压目录";
            }
            if(sourceStage==SourceStage.Contents) return SupplyExeSelected || Find<RadioButton>("IncludesExe").IsChecked==true ? null : "本体中是否包含游戏 EXE？";
            bool selected=Find<RadioButton>("BuiltinExe").IsChecked==true ? Find<ListBox>("BuiltinList").SelectedValue!=null :
                Find<RadioButton>("OwnExe").IsChecked==true && !string.IsNullOrWhiteSpace(Find<TextBox>("ExePath").Text);
            return selected ? null : "选择游戏 EXE";
        }
        if(string.IsNullOrWhiteSpace(Find<TextBox>("ImportName").Text)) return "请输入游戏名称。";
        return importStep==2 && importDraft!=null && DraftProfile()!=null ? null : "配置启动器";
    }

    bool CanAdvanceImport() => ImportRequirement()==null;

    void RefreshImportAction() {
        var button=Find<ImportActionButton>("CheckGame");
        if(checking) {
            string help=importStep==2 ? "检查配置" : "检查游戏文件与 EXE";
            if(importStep==1 && sourceStage==SourceStage.Address && Find<RadioButton>("MagnetSource").IsChecked==true)
                help="正在检查下载源";
            button.Show(ImportAction.Checking,"检查中",help);
            return;
        }
        bool passed=importStep==2 ? detailChecked : importStep==1 && verifiedFiles!=null;
        bool finish=passed && importStep==1 && CyberleekSelected;
        bool advance=importStep==0 || (importStep==1 && NextSourcePage().HasValue);
        string label=finish ? "完成" : passed || advance ? "下一步" : "检查";
        string requirement=ImportRequirement();
        if(requirement==ConsentRequired) button.Show(ImportAction.Consent,"待确认",requirement);
        else if(requirement!=null) button.Show(ImportAction.Waiting,label,requirement);
        else if(importActionFailed) button.Show(ImportAction.Retry,finish ? "重试" : "重新检查","重试");
        else if(passed) button.Show(ImportAction.Passed,label,finish ? "完成" : "检查通过");
        else if(advance) button.Show(ImportAction.Continue,label,"下一步");
        else button.Show(ImportAction.Check,label,importStep==2 ? "检查配置" : "检查");
    }

    void UpdateImportStep() {
        bool local=Find<RadioButton>("LocalSource").IsChecked==true;
        bool magnet=Find<RadioButton>("MagnetSource").IsChecked==true;
        SetImportVisible("ImportSources",importStep==0);
        SetImportVisible("ImportPresets",importStep==0);
        SetImportVisible("ImportNameRow",importStep==2);
        bool sources=importStep==1;
        foreach(string panel in new[]{"LocalImportFields","MagnetImportFields","ContainsQuestion","ExeSources"})
            Find<FrameworkElement>(panel).IsEnabled=!checking;
        bool own=Find<RadioButton>("OwnExe").IsChecked==true;
        bool external=sources && sourceStage==SourceStage.External;
        SetImportVisible("LocalImportFields",sources && local && !external && (!compactImport || sourceStage==SourceStage.Address));
        SetImportVisible("MagnetImportFields",sources && magnet && !external && !checking && (!compactImport || sourceStage==SourceStage.Address));
        SetImportVisible("ArchiveOptions",sources && !checking && sourceStage==SourceStage.Archive);
        Find<FrameworkElement>("ArchiveOptions").IsEnabled=!checking && resolvedAssets==null;
        bool showOutput=!compactImport || archiveDestinationPage;
        SetImportVisible("ArchiveModes",!compactImport || !archiveDestinationPage);
        SetImportVisible("ArchiveDestination",showOutput);
        SetImportVisible("ArchiveRisk",showOutput && Find<RadioButton>("DeleteArchive").IsChecked==true);
        SetImportVisible("ContainsQuestion",sources && !checking && sourceStage==SourceStage.Contents);
        SetImportVisible("ExeSources",external && !checking);
        SetImportVisible("OwnExeRow",own);
        SetImportVisible("BuiltinExeRow",Find<RadioButton>("BuiltinExe").IsChecked==true);
        Find<TextBox>("ExePath").SetResourceReference(AutomationProperties.HelpTextProperty,magnet ? "ui.粘贴文件路径、下载地址或磁力链接" : "ui.输入本地文件路径");
        SetImportVisible("ImportCheckPage",checking && sources);
        SetImportVisible("CheckGame",true);
        bool detailPassed=importStep==2 && detailChecked;
        SetImportVisible("ImportStatus",!checking && !detailPassed);
        SetImportVisible("DetailCheckSuccess",detailPassed);
        SetImportVisible("ArtTools",importStep==2);
        SetImportVisible("StateTools",importStep==2);
        SetImportVisible("PreviewCardFrame",importStep>=2);
        SetImportVisible("ImportArtworkFields",importStep==2);
        SetImportVisible("RecognitionDots",importDraft==null);
        RefreshImportAction();
        Find<TextBlock>("ImportStatus").Foreground=importActionFailed ? Brushes.LightSalmon :
            (verifiedFiles!=null && importStep==1) || detailPassed ? Brushes.LightGreen : Brushes.Wheat;
        Find<TextBlock>("ImportHeading").SetResourceReference(TextBlock.TextProperty,external ? "ui.选择游戏 EXE" : importStep==2 ? "ui.详细配置" :
            importStep==1 ? (local ? "ui.本地目录导入" : "ui.磁力链接导入") : "ui.载入游戏本体");
        Find<Button>("ImportBack").SetResourceReference(FrameworkElement.ToolTipProperty,importStep==0 || transfer!=null ? "ui.返回主页" : "ui.上一步");
        Find<Button>("ImportBack").SetResourceReference(AutomationProperties.NameProperty,importStep==0 || transfer!=null ? "ui.返回主页" : "ui.上一步");
    }

    void SetImportVisible(string name,bool visible) {
        Find<FrameworkElement>(name).Visibility=visible ? Visibility.Visible : Visibility.Collapsed;
    }

    void ClearRecognition() {
        importDraft=null;
        draftCover=draftLogo=null;
        draftIcon=null; draftLobby=Array.Empty<string>(); detailChecked=false;
        importActionFailed=false;
        UpdateIconPreview();
        Find<FrameworkElement>("ResizeLogo").Visibility=Visibility.Collapsed;
        Find<Image>("ImportCoverPreview").Source=null;
        Find<Image>("PreviewLogo").Source=null;
        Find<TextBlock>("ImportStatus").Text="";
    }

    static Point CardLogoOffset(double width,double height,Size box,double x,double y) {
        return new Point(Math.Max(0,width-box.Width)*x,Math.Max(0,height-box.Height)*y);
    }
    static Size CardLogoSize(double width,double height,double fraction,ImageSource source) {
        double ratio=source!=null && source.Width>0 && source.Height>0 ? source.Width/source.Height : 113d/91;
        double w=Math.Min(width*fraction,height*ratio);
        return new Size(w,w/ratio);
    }

    void PositionPreviewLogo() {
        var area=Find<Canvas>("PreviewLogoArea");
        var logo=Find<Image>("PreviewLogo");
        var size=CardLogoSize(area.ActualWidth,area.ActualHeight,importDraft?.LogoSize ?? .44,logo.Source);
        var point=CardLogoOffset(area.ActualWidth,area.ActualHeight,size,importDraft?.LogoX ?? .5,importDraft?.LogoY ?? .5);
        logo.Width=size.Width; logo.Height=size.Height;
        Canvas.SetLeft(logo,point.X); Canvas.SetTop(logo,point.Y);
        var handle=Find<Border>("ResizeLogo"); handle.Visibility=logo.Source!=null && importDraft!=null && importStep!=3 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(handle,Math.Max(0,point.X+size.Width-12)); Canvas.SetTop(handle,Math.Max(0,point.Y+size.Height-12));
    }


    string CardArtFolder(GameInstance game) {
        return Path.Combine(Lab,"desktop","game-artwork",game.Id,game.ArtworkId);
    }

    internal static BitmapSource DecodeCardImage(string path,int limit) {
        using(var input=File.OpenRead(path)) {
            var frame=BitmapDecoder.Create(input,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None).Frames[0];
            int width=frame.PixelWidth,height=frame.PixelHeight;
            input.Position=0;
            var image=new BitmapImage();
            image.BeginInit();
            image.CacheOption=BitmapCacheOption.OnLoad;
            if(Math.Max(width,height)>limit) {
                if(width>=height) image.DecodePixelWidth=limit; else image.DecodePixelHeight=limit;
            }
            image.StreamSource=input; image.EndInit(); image.Freeze();
            return image;
        }
    }

    // Leave a small bleed on every edge so a printed rim stays outside the rounded mask.
    static BitmapSource FullBleedCover(BitmapSource source) {
        if(source==null) return null;
        int x=Math.Min((source.PixelWidth-1)/2,(int)Math.Ceiling(source.PixelWidth*Launcher.Layout.LobbyArtwork.EdgeBleed));
        int y=Math.Min((source.PixelHeight-1)/2,(int)Math.Ceiling(source.PixelHeight*Launcher.Layout.LobbyArtwork.EdgeBleed));
        if(x==0 && y==0) return source;
        var crop=new CroppedBitmap(source,new Int32Rect(x,y,source.PixelWidth-2*x,source.PixelHeight-2*y));
        crop.Freeze();
        return crop;
    }

    BitmapSource LoadCardArt(GameInstance game,bool logo) {
        if(game?.ArtworkId==null) return null;
        string path=Path.Combine(CardArtFolder(game),logo ? "logo.png" : "cover.png");
        if(!File.Exists(path)) return null;
        try { return DecodeCardImage(path,logo ? 768 : 2048); }
        catch(Exception e) when(IsCardFileError(e)) { Append("Unable to read card artwork: "+e.Message); return null; }
    }

    static bool IsCardFileError(Exception e) {
        return e is IOException || e is UnauthorizedAccessException || e is NotSupportedException || e is ArgumentException ||
               e is InvalidDataException || e is OutOfMemoryException || e is FormatException || e is System.Runtime.InteropServices.COMException;
    }

    string CyberleekIssue(string assets) {
        return !string.IsNullOrWhiteSpace(assets) && !IsGtaVi(assets)
            ? Ui.Text("独立优化配置需要 GTA VI 游戏本体。") : RuntimeIssue(OptimizedProfile);
    }

    void PrepareCardImport(string assets,string exe) {
        bool known=!string.IsNullOrWhiteSpace(assets) && File.Exists(Path.Combine(assets,"MicrosoftGame.Config"));
        bool gta=known && IsGtaVi(assets);
        var compatible=known ? EnginesForGame(gta,catalog.Keys.ToArray()) : catalog.Keys.ToArray();
        var draft=new GameInstance { Id=Guid.NewGuid().ToString("N"), Assets=assets, Exe=exe, Pending=checkedPlan,
            Presentation=GamePresentation.Custom,GtaVi=gta,Compatible=compatible };
        if(CyberleekSelected) {
            string issue=CyberleekIssue(assets);
            if(issue!=null) {
                importActionFailed=true;
                Find<TextBlock>("ImportStatus").Text=issue;
                UpdateImportStep();
                return;
            }
            draft.Name="Grand Theft Auto VI"; draft.Presentation=GamePresentation.Cyberleek;
            draft.GtaVi=true; draft.LauncherProfile=OptimizedProfile;
            SaveCardImport(draft); return;
        }
        importDraft=draft;
        draft.LogoSize=.18;
        draftCover=draftLogo=draftIcon=null; draftLobby=Array.Empty<string>();
        Find<Image>("ImportCoverPreview").Source=PlaceholderCover;
        Find<TextBlock>("DetailCoverName").Text=Ui.Text("游戏默认封面");
        Find<Image>("PreviewLogo").Source=PlaceholderLogo;
        Find<TextBox>("ImportName").Text=Ui.Text("未命名游戏");
        ConfigureDraftEngines(); UpdateIconPreview(); PositionPreviewLogo();
        Find<TextBlock>("ImportStatus").Text="";
        detailChecked=false; SetPreviewState(0);
        importStep=2;
        UpdateImportStep();
    }

    void ChooseCardArt(bool logo) {
        var dialog=new Microsoft.Win32.OpenFileDialog { Title=Ui.Text(logo ? "导入 Logo（可选）" : "导入封面"),
            Filter="PNG, JPEG, BMP|*.png;*.jpg;*.jpeg;*.bmp" };
        if(dialog.ShowDialog(window)!=true) return;
        try {
            SetCardArt(dialog.FileName,logo);
        } catch(Exception e) when(IsCardFileError(e)) { Find<TextBlock>("ImportStatus").Text=e.Message; }
    }
    void SetCardArt(string path,bool logo) {
        Launcher.Layout.LobbyArtwork.Check(path);
        var image=DecodeCardImage(path,logo ? 768 : 2048);
        if(logo) Find<Image>("PreviewLogo").Source=draftLogo=image;
        else {
            draftCover=image;
            Find<Image>("ImportCoverPreview").Source=FullBleedCover(image);
            Find<TextBlock>("DetailCoverName").Text=Path.GetFileName(path);
        }
        PositionPreviewLogo(); DraftChanged();
    }

    static void WriteCardImage(BitmapSource image,string path) {
        var encoder=new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using(var output=File.Create(path)) encoder.Save(output);
    }

    void FinishCardImport() {
        var draft=importDraft;
        draft.Name=Find<TextBox>("ImportName").Text.Trim();
        draft.LauncherProfile=DraftProfile();
        SaveCardImport(draft);
    }

    void SaveCardImport(GameInstance draft) {
        if(draft.Pending==null && !ValidFiles(draft.Assets,draft.Exe)) {
            if(importStep==3) LobbyStep(false,false);
            InvalidateFileCheck();
            ClearRecognition();
            importStep=1;
            importActionFailed=true;
            UpdateImportStep();
            Find<TextBlock>("ImportStatus").Text=Ui.Text("请检查游戏本体目录与 EXE 是否匹配。");
            return;
        }
        draft.Compatible=ProfileEngines(draft.LauncherProfile);
        if(!draft.Compatible.Contains(draft.Emulator)) draft.Emulator=draft.Compatible.First();
        bool hasArt=draft.CustomPresentation && (draftCover!=null || draftLogo!=null || draftIcon!=null || draftLobby.Length>0);
        bool needsFolder=hasArt || draft.Pending!=null;
        draft.ArtworkId=needsFolder ? Guid.NewGuid().ToString("N") : null;
        string folder=needsFolder ? CardArtFolder(draft) : null;
        try {
            if(needsFolder) Directory.CreateDirectory(folder);
            if(hasArt) {
                if(draftCover!=null) WriteCardImage(draftCover,Path.Combine(folder,"cover.png"));
                if(draftLogo!=null) WriteCardImage(draftLogo,Path.Combine(folder,"logo.png"));
                if(draftIcon!=null) WriteCardImage(draftIcon,Path.Combine(folder,"icon.png"));
                for(int i=0;i<draftLobby.Length;i++) File.Copy(draftLobby[i],Path.Combine(folder,"lobby-"+i.ToString("D4")+Path.GetExtension(draftLobby[i]).ToLowerInvariant()));
            }
            draft.Pending?.KeepMetadata(folder);
            WriteLibrary(games.Concat(new[]{draft}),draft.Id);
        } catch(Exception e) when(IsCardFileError(e)) {
            try {
                if(Directory.Exists(folder)) {
                    foreach(string file in Directory.GetFiles(folder)) File.Delete(file);
                    Directory.Delete(folder);
                }
            } catch(IOException cleanup) { Append(cleanup.Message); }
              catch(UnauthorizedAccessException cleanup) { Append(cleanup.Message); }
            Find<TextBlock>(importStep==3 ? "LobbyStatus" : "ImportStatus").Text=e.Message;
            if(importStep==1) { importActionFailed=true; UpdateImportStep(); }
            Append("Card import failed: "+e.Message); return;
        }
        draft.Pending?.UseMetadata(folder);
        var game=draft;
        games.Add(game); gameTiles.Add(CreateGameTile(game));
        try {
            if(game.Pending==null) File.WriteAllText(SavedExePath,StoreGamePath(game.Exe));
            File.Delete(ManualImportMarker);
        } catch(Exception e) when(IsCardFileError(e)) { Append(e.Message); }
        ActivateGame(game,false);
        OpenImport(false);
        DisplayGame(true);
        Schedule();
    }

    void ApplyGameCardArt(GameInstance game) {
        var cover=LoadCardArt(game,false);
        ImageSource image=FullBleedCover(cover);
        if(image==null && game.Presentation==GamePresentation.Custom) image=PlaceholderCover;
        var brush=image==null ? defaultPrint : new ImageBrush(image) { Stretch=Stretch.UniformToFill };
        if(brush.CanFreeze && !brush.IsFrozen) brush.Freeze();
        Find<System.Windows.Shapes.Rectangle>("CoverPrint").Fill=brush;
        var mark=Find<Image>("GameMark");
        bool usePlaceholder=game.Presentation==GamePresentation.Custom || (game.CustomPresentation && !game.GtaVi);
        mark.Source=LoadCardArt(game,true) ?? (usePlaceholder ? PlaceholderLogo : defaultMark);
        var markSize=game.CustomPresentation ? CardLogoSize(113,91,1,mark.Source) : new Size(113,91);
        mark.Width=markSize.Width; mark.Height=markSize.Height;
        AutomationProperties.SetName(mark,game.Name);
    }
}
