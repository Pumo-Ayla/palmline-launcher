using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;

internal sealed partial class DesktopLauncher
{
    const string LauncherRepository = "https://github.com/" + UpdateIdentity.Repository;
    const double SelectedTileScale = 1.15, SelectedTileLift = -3;
    readonly string root = FindLauncherDirectory();
    readonly Window window;
    readonly RadioButton[] projects;
    readonly Dictionary<string, XElement> catalog;
    readonly ProjectDisplaySettings displaySettings;
    readonly ProjectHoverCard projectCard;
    readonly LaunchButton launchButton;
    readonly LauncherUpdateControls updates;
    readonly ControllerViewer controller;
    readonly InputModeButton inputMode;
    readonly BindingWizard bindings;
    readonly GamepadStatus gamepad;
    bool viewingController;
    int viewRevision, modeRevision;
    bool launchAfterWarmup;
    string workerShaderAction="";
    readonly SessionLog sessionLog;
    NativeLogConsole console;
    readonly DispatcherTimer historyTimer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(10) };
    Process worker;
    GameInstance sessionGame;
    bool BrowsingDuringPlay => playing && sessionGame!=null && activeGame!=sessionGame;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
    EventWaitHandle cancelWarmup;
    string selected = "cgbj", running, logDirectory;
    bool closed, restart;
    bool playing { get { return launchButton.State==LaunchState.Starting || launchButton.CanStop || launchButton.State==LaunchState.Stopping; } }

    T Find<T>(string name) where T : class { return (T)window.FindName(name); }
    string Lab { get { return Path.Combine(root, "XCL-VIctory-Lab"); } }
    string Preference { get { return Path.Combine(Lab, "desktop", "selected-project.txt"); } }
    string DisplayPreferences { get { return Path.Combine(Lab, "desktop", "display-settings.xml"); } }
    string GameSelection { get { return Path.Combine(Lab,"desktop","game-selection.txt"); } }
    string gameExe, gameAssets;
    void DisplayGame(bool animate=false) {
        bool loaded=gameExe!=null;
        bindings?.SetPromptContext(PromptProject,playing);
        if(caseExpanded || Find<Grid>("CaseReveal").Visibility==Visibility.Visible || Find<TranslateTransform>("CaseLanding").HasAnimatedProperties)
            SetAchievementCase(false,false);
        caseSignature=null;
        Find<ItemsControl>("AchievementRows").ItemsSource=null;
        Find<TextBlock>("CaseCount").Text="";
        foreach(string name in new[]{"SceneViewport","GameMark","CardArea"})
            Find<FrameworkElement>(name).Visibility=loaded ? Visibility.Visible : Visibility.Collapsed;
        ShowLaunchAction();
        Find<Button>("AddGame").IsEnabled=!checking && !playing;
        if(loaded) {
            ApplyGameCardArt(activeGame);
            Find<TextBlock>("GameTitle").Text=activeGame.Name;
            if(activeGame.Pending==null) OpenGameSaveLibrary(); else { saveLibrary=null; saveError=null; }
            // Populate the first frame before showing a newly selected game's card.
            ++historyRevision;
            ShowHistory(LoadHistory(activeGame),LatestStory(saveLibrary,saveLibrary?.Selected));
        }
        Find<StackPanel>("GtaProjectGroup").Visibility=loaded && activeGame.GtaVi && projects.Any(r=>GtaOnlyEngine((string)r.Tag)) ? Visibility.Visible : Visibility.Collapsed;
        bool hasGeneral=projects.Any(r=>!GtaOnlyEngine((string)r.Tag));
        Find<StackPanel>("GeneralProjectGroup").Visibility=hasGeneral ? Visibility.Visible : Visibility.Collapsed;
        Find<Border>("ProjectGroupDivider").Visibility=hasGeneral ? Visibility.Visible : Visibility.Collapsed;
        UpdateSaveMenuLabel();
        Find<Button>("CaseToggle").Visibility=loaded && (activeGame.GtaVi || activeGame.CustomPresentation) ? Visibility.Visible : Visibility.Collapsed;
        string[] lobby=loaded && activeGame.CustomPresentation ? LobbyFiles(activeGame) : Array.Empty<string>();
        bool custom=loaded && activeGame.CustomPresentation;
        SetImportVisible("SceneViewport",loaded && !custom);
        SetImportVisible("CustomLobby",custom && lobby.Length>0);
        Find<Launcher.Layout.LobbyArtwork>("CustomLobby").SetFiles(lobby);
        SetImportVisible("EmptyGame",!loaded || (custom && lobby.Length==0));
        SetImportVisible("EmptyControllerGlyph",!loaded);
        var emptyLabel=Find<TextBlock>("EmptyCaption");
        if(custom) emptyLabel.Text=activeGame.Name; else emptyLabel.SetResourceReference(TextBlock.TextProperty,"ui.暂无游戏实例");
        foreach(var radio in projects) {
            radio.IsEnabled=available.Contains((string)radio.Tag) && !playing;
            if(loaded) projectCard.UpdateHelp(radio);
            else radio.SetResourceReference(AutomationProperties.HelpTextProperty,"ui.请先载入 Xbox 游戏文件和对应的可执行文件（.exe）。");
        }
        PositionGameMark(animate);
        Menu(false);
    }
    static string FindLauncherDirectory() {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
        while (dir != null) {
            if (File.Exists(Path.Combine(dir.FullName, @"XCL-VIctory-Lab\desktop\session.py")) ||
                (Directory.Exists(Path.Combine(dir.FullName,"Community")) && File.Exists(Path.Combine(dir.FullName, @"Runtime\Components\Library.zip"))))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(Ui.Text("请从包含 XCL-VIctory-Lab 的 Launcher 文件夹打开启动器。"));
    }
    string ProjectName(string id) {
        var project = catalog[id];
        if(id=="victory-nvidia") return (string)project.Attribute("name");
        return (string)project.Attribute("label") + " · " + (string)project.Attribute("name");
    }
    RadioButton CreateProjectOption(XElement project) {
        string id=(string)project.Attribute("id");
        var avatar=new BitmapImage();
        using(var file=File.OpenRead(Path.Combine(root,"Community",id,(string)project.Attribute("image")))) {
            avatar.BeginInit();
            avatar.CacheOption=BitmapCacheOption.OnLoad;
            avatar.StreamSource=file;
            avatar.EndInit();
            avatar.Freeze();
        }
        var option=new RadioButton { Tag=id, GroupName="Emulator", Style=(Style)window.FindResource("emulatorOption"),
            Background=new ImageBrush(avatar) { Stretch=Stretch.UniformToFill },
            Content=new TextBlock { Text=ProjectName(id), Foreground=Brushes.White } };
        if(id=="xcl") {
            var normal=option.Background;
            var gray=new ImageBrush(new FormatConvertedBitmap(avatar,PixelFormats.Gray8,null,0)) { Stretch=Stretch.UniformToFill };
            option.IsEnabledChanged+=delegate { option.Background=option.IsEnabled ? normal : gray; };
        }
        AutomationProperties.SetName(option,ProjectName(id));
        projectCard.Register(option,project);
        Find<StackPanel>(GtaOnlyEngine(id) ? "GtaProjectOptions" : "GeneralProjectOptions").Children.Add(option);
        return option;
    }
    const double LibraryCoverSize=44, LibraryCoverRadius=8;
    static System.Windows.Shapes.Rectangle LibraryCover(Brush art) {
        const double rim=1.2;
        return new System.Windows.Shapes.Rectangle {
            Width=LibraryCoverSize, Height=LibraryCoverSize, RadiusX=LibraryCoverRadius-rim/2, RadiusY=LibraryCoverRadius-rim/2,
            Fill=art, Stroke=new SolidColorBrush(Color.FromRgb(28,19,32)), StrokeThickness=rim,
            Effect=new System.Windows.Media.Effects.DropShadowEffect { BlurRadius=8, ShadowDepth=3, Opacity=.12 }
        };
    }

    sealed class LibraryTile : RadioButton {
        public Action<bool> ShowSelection;
    }

    RadioButton CreateGameTile(GameInstance game) {
        var bitmap=GameIcon(game);
        const double glowInset=1;
        Brush art=bitmap==null ? (Brush)new LinearGradientBrush(Color.FromRgb(79,67,91),Color.FromRgb(44,39,53),90)
            : new ImageBrush(bitmap) { Stretch=Stretch.UniformToFill };
        var cover=LibraryCover(art);
        var icon = new Grid { Width=LibraryCoverSize, Height=LibraryCoverSize };
        var glowScale=new ScaleTransform(.90,.90);
        var glow=new Border { Margin=new Thickness(glowInset), CornerRadius=new CornerRadius(LibraryCoverRadius-glowInset), Background=Brushes.White,
            Effect=new System.Windows.Media.Effects.BlurEffect { Radius=5 }, Opacity=0,
            RenderTransformOrigin=new Point(.5,.5), RenderTransform=glowScale, IsHitTestVisible=false };
        icon.Children.Add(glow); icon.Children.Add(cover);
        if(bitmap==null && !string.IsNullOrWhiteSpace(game.Name)) {
            icon.Children.Add(new TextBlock { Text=System.Globalization.StringInfo.GetNextTextElement(game.Name.Trim()),
                FontSize=20, Foreground=Brushes.White, HorizontalAlignment=HorizontalAlignment.Center,
                VerticalAlignment=VerticalAlignment.Center, IsHitTestVisible=false });
        }
        var idleFinish=new System.Windows.Shapes.Rectangle {
            Width=cover.Width, Height=cover.Height, RadiusX=cover.RadiusX, RadiusY=cover.RadiusY,
            Fill=new SolidColorBrush(Color.FromArgb(132,61,58,65)),
            Stroke=new SolidColorBrush(Color.FromRgb(97,91,102)), StrokeThickness=cover.StrokeThickness,
            IsHitTestVisible=false
        };
        icon.Children.Add(idleFinish);
        var radio = new LibraryTile { Tag=game, GroupName="GameInstance", Content=icon, Background=art,
            Style=(Style)window.FindResource(typeof(RadioButton)) };
        var scale = new ScaleTransform();
        var lift = new TranslateTransform();
        var transform = new TransformGroup();
        transform.Children.Add(scale);
        transform.Children.Add(lift);
        icon.RenderTransformOrigin = new Point(.5, .5);
        icon.RenderTransform = transform;
        RenderOptions.SetBitmapScalingMode(cover, BitmapScalingMode.HighQuality);
        var shadow=(System.Windows.Media.Effects.DropShadowEffect)cover.Effect;
        bool active=false;
        Action update = delegate {
            bool hover = radio.IsMouseOver && radio.IsEnabled;
            double size = active ? SelectedTileScale : hover ? 1.055 : 1;
            Tween(scale, ScaleTransform.ScaleXProperty, size);
            Tween(scale, ScaleTransform.ScaleYProperty, size);
            Tween(lift, TranslateTransform.YProperty, active ? SelectedTileLift : hover ? -1.5 : 0);
            Tween(shadow, System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, active ? .06 : hover ? .36 : .12);
            double finish=active ? 0 : hover ? .55 : 1;
            if((double)idleFinish.GetAnimationBaseValue(UIElement.OpacityProperty)!=finish)
                ControllerMotion.Fade(idleFinish,idleFinish.Opacity,finish,160);
        };
        radio.ShowSelection = on => {
            if(active==on) return;
            active=on;
            update();
            double size=on ? 1.09 : .90, opacity=on ? .94 : 0;
            double start=glowScale.ScaleX, alpha=glow.Opacity;
            glowScale.ScaleX=glowScale.ScaleY=size;
            glow.Opacity=opacity;
            var pulse=SystemParameters.ClientAreaAnimation ? ControllerMotion.Spring(start,on ? 1.13 : .86,size,315) : null;
            glowScale.BeginAnimation(ScaleTransform.ScaleXProperty,pulse);
            glowScale.BeginAnimation(ScaleTransform.ScaleYProperty,pulse);
            glow.BeginAnimation(UIElement.OpacityProperty,SystemParameters.ClientAreaAnimation
                ? ControllerMotion.Spring(alpha,on ? 1 : .055,opacity,300) : null);
        };
        radio.MouseEnter += delegate { update(); };
        radio.MouseLeave += delegate { update(); };
        radio.IsEnabledChanged+=delegate { update(); };
        radio.Checked+=delegate { if(!syncingLibrary && activeGame!=game) ActivateGame(game,true); };
        radio.Click+=delegate { if(importOpen && !checking && activeGame==game) OpenImport(false); };
        radio.Loaded+=delegate { if(game==activeGame) PositionGameMark(true); };
        AttachRemovalMenu(radio,game);
        AutomationProperties.SetName(radio,game.Name);
        AutomationProperties.SetAutomationId(radio,game.Id);
        AttachGameTip(radio,cover,game);
        Find<StackPanel>("GameList").Children.Add(radio);
        return radio;
    }
    bool markQueued,markGlide;
    readonly SolidColorBrush railStatusBrush=new SolidColorBrush(Colors.White);
    Color RailStatusColor() {
        if(importOpen && !playing) return Colors.White;
        if(installing!=null && installing==activeGame) return Color.FromRgb(72,187,247);
        if(playing) return Color.FromRgb(128,236,164);
        if(!updates.Required && activeGame?.Pending==null && available.Contains(selected)) {
            if(launchButton.State==LaunchState.Ready) return Color.FromRgb(128,236,164);
            if(launchButton.State==LaunchState.Warming) return Color.FromRgb(255,214,94);
        }
        return Color.FromRgb(253,108,117);
    }
    void PositionGameMark(bool animate) {
        markGlide|=animate;
        if(markQueued) return;
        markQueued=true;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{
            bool glide=markGlide;
            markQueued=markGlide=false;
            AlignGameMark(glide);
        }));
    }
    void AlignGameMark(bool animate) {
        var mark=Find<Border>("SelectionMark");
        foreach(LibraryTile item in gameTiles) item.ShowSelection(!importOpen && item.Tag==activeGame);
        var owner=playing && sessionGame!=null ? sessionGame : activeGame;
        var tile=gameTiles.FirstOrDefault(r=>r.Tag==owner);
        bool draft=importOpen && !playing;
        var list=Find<StackPanel>("GameList");
        FrameworkElement targetItem=draft ? (FrameworkElement)Find<Grid>("ImportRailSlot") : tile;
        if(!list.IsLoaded || targetItem==null || (!draft && !targetItem.IsVisible)) {
            mark.Visibility=Visibility.Collapsed; return;
        }
        Color color=RailStatusColor();
        mark.Background=railStatusBrush;
        if((Color)railStatusBrush.GetAnimationBaseValue(SolidColorBrush.ColorProperty)!=color) {
            Color from=railStatusBrush.Color;
            railStatusBrush.Color=color;
            railStatusBrush.BeginAnimation(SolidColorBrush.ColorProperty,mark.IsVisible && SystemParameters.ClientAreaAnimation
                ? new ColorAnimation(from,color,TimeSpan.FromMilliseconds(250)){FillBehavior=FillBehavior.Stop} : null);
        }
        var move=(TranslateTransform)mark.RenderTransform;
        var parent=(Grid)mark.Parent;
        Point center=draft ? new Point(parent.ActualWidth/2,list.ActualHeight+30)
            : tile.TranslatePoint(new Point(tile.ActualWidth/2,tile.ActualHeight/2),parent);
        bool highlighted=draft || owner==activeGame;
        double edge=center.X-LibraryCoverSize*(highlighted ? SelectedTileScale : 1)/2;
        move.X=edge+2-mark.Width;
        double target=center.Y+(highlighted ? SelectedTileLift : 0)-mark.Height/2;
        if(mark.DataContext==targetItem && mark.Visibility==Visibility.Visible &&
            Math.Abs((double)move.GetAnimationBaseValue(TranslateTransform.YProperty)-target)<.01) return;
        double start=move.Y, distance=target-start;
        bool glide=animate && mark.IsVisible && SystemParameters.ClientAreaAnimation && Math.Abs(distance)>.5;
        mark.DataContext=targetItem;
        mark.Visibility=Visibility.Visible;
        move.BeginAnimation(TranslateTransform.YProperty,null);
        move.Y=target;
        if(glide) {
            double peak=target+Math.Sign(distance)*Math.Min(4,Math.Abs(distance)*.06);
            move.BeginAnimation(TranslateTransform.YProperty,ControllerMotion.Spring(start,peak,target,290,45));
        }
    }
    void SettingsPanel(bool show) {
        var overlay = Find<Grid>("SettingsPanel");
        if ((overlay.Visibility == Visibility.Visible) == show) return;
        bool keyboard = InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        var blur = show ? new System.Windows.Media.Effects.BlurEffect { Radius=9 } : null;
        if (blur != null) blur.Freeze();
        Find<Grid>("RailContent").Effect = blur;
        Find<Grid>("HeroContent").Effect = blur;
        Find<Grid>("LauncherContent").IsEnabled = !show;
        overlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) {
            projectCard.Hide();
            Menu(false);
            var tab = Find<RadioButton>("SettingsGeneral");
            tab.IsChecked = true;
            if (keyboard) tab.Focus();
            else overlay.Focus();
        } else Find<Button>("ShowSettings").Focus();
    }
    void AttachKeyboardCues() {
        Action<bool> cues=show=>window.Resources["KeyboardCueVisibility"]=show ? Visibility.Visible : Visibility.Collapsed;
        cues(false);
        window.Activated+=delegate { cues(false); };
        window.Deactivated+=delegate { cues(false); };
        window.PreviewMouseDown+=delegate { cues(false); };
        window.PreviewMouseMove+=delegate { if((Visibility)window.Resources["KeyboardCueVisibility"]==Visibility.Visible) cues(false); };
        window.PreviewKeyDown+=delegate(object sender,KeyEventArgs e) {
            if((Keyboard.Modifiers & (ModifierKeys.Alt|ModifierKeys.Windows))!=0) return;
            if(e.Key==Key.Tab || e.Key==Key.Left || e.Key==Key.Right || e.Key==Key.Up || e.Key==Key.Down || e.Key==Key.F1)
                cues(true);
        };
    }
    void ChangeLanguage(string code) {
        displaySettings.Language=Ui.Normalize(code); Ui.Apply(code);
        window.Language=XmlLanguage.GetLanguage(Ui.Language);
        if(repositoryNoticeExpanded) SetRepositoryNoticeWidth(RepositoryMessageWidth(),0);
        launchButton.RefreshText();
        RefreshImportAction();
        updates?.Refresh();
        RefreshHistory();
        inputMode.RefreshLanguage(); gamepad.RefreshLanguage(); bindings.RefreshLanguage();
        SaveDisplaySettings();
    }
    void SaveDisplaySettings() {
        displaySettings.ShowFps = Find<CheckBox>("ShowFps").IsChecked == true;
        foreach (var radio in projects) projectCard.UpdateHelp(radio);
        var notice = Find<TextBlock>("SettingsNotice");
        try {
            displaySettings.Save(DisplayPreferences);
            notice.Visibility = Visibility.Collapsed;
        } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
            notice.Text = Ui.Text("设置本次已生效，无法保存到磁盘。");
            notice.Visibility = Visibility.Visible;
        }
    }
    void ShowLauncherName() {
        window.Title=displaySettings.LauncherName;
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Find<TextBlock>("AboutLauncherName").Text = ProjectDisplaySettings.FullLauncherName
            + " · v" + version.ToString(version.Revision > 0 ? 4 : 3);
    }
    static void Tween(Animatable target, DependencyProperty property, double value) {
        var animation = new DoubleAnimation((double)target.GetValue(property), value,
            TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 160 : 0)) {
            EasingFunction = new CubicEase { EasingMode=EasingMode.EaseOut }, FillBehavior=FillBehavior.Stop
        };
        target.SetValue(property, value);
        target.BeginAnimation(property, animation);
    }
    bool menuOpen;
    int menuRevision;
    bool shaderOpen;
    int shaderRevision;
    void ShaderPage(bool open) {
        if(shaderOpen==open) return;
        shaderOpen=open;
        int revision=++shaderRevision;
        var overlay=Find<Grid>("ShaderOverlay");
        var card=Find<Border>("ShaderCard");
        overlay.IsHitTestVisible=open;
        if(open) {
            Menu(false);
            overlay.Visibility=Visibility.Visible;
            ControllerMotion.Reveal(card,.96,1.012,8,235,.45);
            Find<Button>("CloseShader").Focus();
        } else {
            Find<Button>("More").Focus();
            ControllerMotion.Dismiss(card,.96,130,0,delegate {
                if(revision==shaderRevision) overlay.Visibility=Visibility.Collapsed;
            });
        }
    }
    void Menu(bool open) {
        if(menuOpen==open) return;
        menuOpen=open;
        int revision=++menuRevision;
        var panel=Find<Border>("LaunchMenu");
        var scale=(ScaleTransform)panel.RenderTransform;
        bool hidden=panel.Visibility!=Visibility.Visible;
        double from=hidden ? .88 : scale.ScaleX, alpha=hidden ? 0 : panel.Opacity;
        double to=open ? 1 : .88;
        int ms=open ? 240 : 175;
        panel.Visibility=Visibility.Visible;
        panel.IsEnabled=open;
        if(open) {
            var renderSettings=Find<Button>("RenderSettings");
            renderSettings.IsEnabled=CanOpenEmulatorSettings;
        }
        if(open && importOpen) OpenImport(false);
        if(!open) projectCard.Hide();
        scale.ScaleX=scale.ScaleY=to;
        var spring=SystemParameters.ClientAreaAnimation ? ControllerMotion.Spring(from,open ? 1.025 : Math.Min(1.015,from+.012),to,ms) : null;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,spring);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,spring);
        ControllerMotion.Fade(panel,alpha,open ? 1 : 0,ms,delegate {
            if(!open && menuRevision==revision) panel.Visibility=Visibility.Collapsed;
        });
        if(open && InputManager.Current.MostRecentInputDevice is KeyboardDevice)
            (projects.FirstOrDefault(r=>r.IsEnabled && r.IsChecked==true) as Control ?? Find<Button>("ShaderMenu")).Focus();
    }
    DesktopLauncher() : this(null) { }
    DesktopLauncher(string requested) : this(requested,new SessionLog(FindLauncherDirectory())) { }
    DesktopLauncher(string requested,SessionLog log)
    {
        sessionLog=log;
        Application.Current.Resources["InterfaceFont"] = new FontFamily(AssetUri("Assets/Fonts/"), "./#MiSans");
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Controls.xaml")) {
            Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
        }
        displaySettings = ProjectDisplaySettings.Load(DisplayPreferences);
        Ui.Apply(displaySettings.Language);
        Application.Current.Resources["ImportPressDuration"]=new Duration(TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 65 : 0));
        Application.Current.Resources["ImportHoverDuration"]=new Duration(TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 125 : 0));
        window = LoadWindow("Main.xaml");
        ShowLauncherName();
        window.Language=XmlLanguage.GetLanguage(Ui.Language);
        AttachKeyboardCues();
        SetupRemovalDialog();
        launchButton = new LaunchButton(window);
        updates = new LauncherUpdateControls(window, root, () => playing, Append, () => checking || transfer!=null || componentScan!=null);
        updates.RequirementChanged+=delegate {
            Menu(false);
            ShowLaunchAction();
            PositionGameMark(true);
            if(!playing && worker!=null) {
                restart=false;
                cancelWarmup?.Set();
                CloseInput();
            }
        };
        historyTimer.Tick+=delegate { RefreshHistory(); };
        projectCard = new ProjectHoverCard(window,displaySettings);
        // The settings UI needs the project list and its rail artwork to open.
        using (var library = new ComponentLibrary(root))
            library.RestoreStartupFiles();
        var entries = XElement.Load(Path.Combine(root, "Community", "projects.xml")).Elements("project").ToArray();
        catalog = entries.ToDictionary(e => (string)e.Attribute("id"));
        // An older launcher may have held these icons open during the uninstall.
        foreach(string retired in new[]{"belobug","anyxboxx-zcy"}) {
            string folder=Path.Combine(root,"Community",retired);
            if(catalog.ContainsKey(retired) || !Directory.Exists(folder)) continue;
            try {
                if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0) continue;
                var icon=Path.Combine(folder,"github-avatar.png");
                if(Directory.EnumerateFileSystemEntries(folder).All(entry=>entry==icon)) {
                    File.Delete(icon);
                    Directory.Delete(folder,false);
                }
            } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException) { Append(e.Message); }
        }
        projects = entries.Select(CreateProjectOption).ToArray();
        var list=Find<StackPanel>("GameList");
        var empty=Find<StackPanel>("EmptyGame");
        empty.IsVisibleChanged+=delegate {
            Find<TranslateTransform>("EmptyGameFloat").BeginAnimation(TranslateTransform.YProperty,
                empty.IsVisible && SystemParameters.ClientAreaAnimation ? new DoubleAnimation(0,-4,TimeSpan.FromSeconds(1.5)) {
                    EasingFunction=new SineEase { EasingMode=EasingMode.EaseInOut }, AutoReverse=true, RepeatBehavior=RepeatBehavior.Forever } : null);
        };
        list.Loaded+=delegate { PositionGameMark(false); };
        list.SizeChanged+=delegate { PositionGameMark(false); };
        Find<Grid>("ImportRailSlot").SizeChanged+=delegate { PositionGameMark(false); };
        controller = new ControllerViewer(Find<Grid>("OrbitSurface"), Find<Viewport3D>("ControllerViewport"));
        inputMode = new InputModeButton(window, () => ShowController(!viewingController), SwitchControllerMode);
        gamepad = new GamepadStatus(window, controller);
        bindings = new BindingWizard(window,controller,Path.Combine(Lab,"desktop","keyboard-slots"),
            delegate { gamepad.SetMode(inputMode.KeyboardMode,bindings.Configured); RefreshPromptPreparation(); });
        Find<Button>("ControllerBack").Click += delegate { if(!bindings.Back()) ShowController(false); };
        LoadArtwork();
        SetupAchievementCase();
        SetupImport();
        if(requested!=null && available.Contains(requested)) {
            selected=requested; activeGame.Emulator=selected; SaveLibrary();
        }
        DisplayGame();
        foreach(var radio in projects) {
            radio.IsChecked=activeGame!=null && (string)radio.Tag==selected;
            radio.Checked+=delegate { if(!syncingLibrary) Select((string)radio.Tag); };
            radio.Click+=delegate { Menu(false); Find<Button>("More").Focus(); };
        }
        Find<Button>("Min").Click += delegate { window.WindowState = WindowState.Minimized; };
        Find<Button>("Close").Click += delegate { window.Close(); };
        RoutedEventHandler openRepository = delegate {
            Process.Start(new ProcessStartInfo(LauncherRepository) { UseShellExecute = true });
        };
        AttachRepositoryNotice(openRepository);
        Find<Button>("AboutRepository").Click += openRepository;
        foreach(RadioButton choice in Find<WrapPanel>("LanguageOptions").Children) {
            choice.IsChecked=(string)choice.Tag==displaySettings.Language;
            choice.Checked+=delegate { ChangeLanguage((string)choice.Tag); };
        }
        Find<CheckBox>("ShowFps").IsChecked = displaySettings.ShowFps;
        var keyPrompts=Find<CheckBox>("UseKeyPrompts");
        keyPrompts.IsChecked=displaySettings.UseKeyPrompts;
        keyPrompts.Click+=delegate {
            if(playing || PromptProject==null) { keyPrompts.IsChecked=displaySettings.UseKeyPrompts; return; }
            displaySettings.UseKeyPrompts=keyPrompts.IsChecked==true;
            SaveDisplaySettings();
            RefreshPromptPreparation();
        };
        Find<CheckBox>("ShowFps").Checked += delegate { SaveDisplaySettings(); };
        Find<CheckBox>("ShowFps").Unchecked += delegate { SaveDisplaySettings(); };
        Find<Button>("ShowSettings").Click += delegate { SettingsPanel(true); };
        AttachComponentCheck();
        Find<Button>("CloseSettings").Click += delegate { SettingsPanel(false); };
        Find<Border>("SettingsScrim").MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
            SettingsPanel(false); e.Handled=true;
        };
        Find<Button>("More").Click += delegate { Menu(!menuOpen); };
        Find<Button>("ShaderMenu").Click += delegate { ShaderPage(true); };
        Find<Button>("CloseShader").Click += delegate { ShaderPage(false); };
        Find<Border>("ShaderScrim").MouseLeftButtonDown += delegate(object sender,MouseButtonEventArgs e) {
            ShaderPage(false); e.Handled=true;
        };
        Find<Button>("Retry").Click += delegate { ShaderPage(false); Schedule("--full-warmup"); };
        Find<Button>("ClearShaders").Click += delegate { ShaderPage(false); Schedule("--clear-shaders"); };
        AttachSaveCard();
        AttachRenderSettings();
        window.PreviewMouseDown += delegate(object sender,MouseButtonEventArgs e) {
            if(shaderOpen && Find<Grid>("Rail").IsMouseOver) { ShaderPage(false); e.Handled=true; return; }
            if (menuOpen && !Find<Border>("ProjectCard").IsMouseOver && !Find<Border>("LaunchMenu").IsMouseOver && !Find<Button>("More").IsMouseOver) Menu(false);
        };
        AttachLogMenu();
        window.PreviewKeyDown += Navigate;
        Find<Button>("Play").Click += async delegate {
            if (BrowsingDuringPlay || updates.Installing || SaveCardOpen || renderOpen || shaderOpen) return;
            if (launchButton.CanStop && logDirectory!=null) {
                try {
                    File.WriteAllText(Path.Combine(logDirectory,"exit-request"),"");
                    Append("Game shutdown requested.");
                    SetLaunchState(LaunchState.Stopping);
                } catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException) { Append(ex.Message); }
                return;
            }
            var requestedWorker=worker;
            var requestedGame=activeGame;
            await updates.FirstCheck;
            if(closed || updates.Required || updates.Installing || checking || BrowsingDuringPlay || requestedGame!=activeGame || SaveCardOpen || renderOpen || shaderOpen) return;
            if(launchButton.Failed) {
                Schedule(workerShaderAction,launchButton.State==LaunchState.GameFailed || launchAfterWarmup);
                return;
            }
            if(launchButton.State==LaunchState.CacheCleared && worker==null) { Schedule(); return; }
            if(requestedWorker==worker) StartPreparedGame();
        };
        Find<Button>("Play").IsEnabled = false;
        window.Closed += delegate {
            componentScan?.Cancel();
            EndAchievementSession();
            historyTimer.Stop();
            closed=true;
            cancelWarmup?.Set();
            CloseInput();
            // Let the preloader release its project lock before the next launcher opens.
            if(worker!=null && !playing) worker.WaitForExit(5000);
            sessionLog.Dispose();
            console?.Finish();
        };
        window.ContentRendered += async delegate {
            if (gameExe!=null && worker==null && available.Contains(selected)) Begin();
            await CheckNewEngines();
        };
    }
    static Window LoadWindow(string resource) {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)) {
            var view = (Window)XamlReader.Load(stream);
            view.FontFamily = (FontFamily)Application.Current.Resources["InterfaceFont"];
            Backdrop.Attach(view);
            return view;
        }
    }
    static Uri AssetUri(string path) {
        string name = Assembly.GetExecutingAssembly().GetName().Name;
        return new Uri("pack://application:,,,/" + Uri.EscapeDataString(name) + ";component/" + path);
    }
    static BitmapSource Artwork(string path, Int32Rect crop) {
        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.UriSource = AssetUri("Assets/" + path);
        source.EndInit();
        source.Freeze();
        if (crop.IsEmpty) return source;
        var sprite = new CroppedBitmap(source, crop);
        sprite.Freeze();
        return sprite;
    }
    static void RoundClip(FrameworkElement panel, double radius) {
        panel.SizeChanged += delegate {
            panel.Clip = new RectangleGeometry(new Rect(panel.RenderSize), radius, radius);
        };
    }
    void LoadArtwork() {
        var hero = Find<Grid>("Hero");
        var dock = Find<Grid>("Dock");
        var scene = Find<Grid>("SceneViewport");
        RoundClip(Find<Grid>("Rail"), Find<Border>("RailShadow").CornerRadius.TopLeft);
        RoundClip(hero, Find<Border>("HeroShadow").CornerRadius.TopLeft);
        RoundClip(dock, 13);
        var sample = new VisualBrush(scene) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        var frost=Find<System.Windows.Shapes.Rectangle>("FrostSample");
        var summary=Find<StackPanel>("CaseSummary");
        frost.Fill=sample;
        Action sampleDock = delegate {
            if (dock.ActualWidth <= 0 || scene.ActualWidth <= 0) return;
            frost.Height=summary.ActualHeight+summary.Margin.Top+summary.Margin.Bottom+18;
            Point point = dock.TranslatePoint(new Point(-9, dock.ActualHeight+9-frost.Height), scene);
            sample.Viewbox = new Rect(point.X, point.Y, dock.ActualWidth + 18, frost.Height);
        };
        hero.SizeChanged += delegate { sampleDock(); };
        dock.SizeChanged += delegate { sampleDock(); };
        summary.SizeChanged += delegate { sampleDock(); };
        window.Loaded += delegate { sampleDock(); };
        Find<Image>("GameMark").Source = Artwork("GameTitle.png", new Int32Rect(5, 0, 698, 540));

        var poster = Find<Image>("ScenePoster");
        poster.Source = Artwork("LoadingPoster.jpg", Int32Rect.Empty);
        scene.Children.Add(new LoadingScreens(window, Path.Combine(root, "Media", "LoadingScreens"), Append));
    }
    void ShowController(bool show) {
        if(viewingController==show) return;
        viewingController=show;
        int revision=++viewRevision;
        ++modeRevision;
        var home = Find<Grid>("HeroContent");
        var host = Find<Grid>("ControllerHost");
        var entering=show ? host : home;
        var leaving=show ? home : host;
        Menu(false);
        entering.Visibility=Visibility.Visible;
        entering.IsEnabled=true;
        leaving.IsEnabled=false;
        Find<DockPanel>("LibraryRail").IsEnabled = !show;
        if(show) OpenImport(false);
        inputMode.SetViewer(show);
        if(show) {
            projectCard.Hide();
            var content=Find<Grid>("ControllerPageContent");
            content.BeginAnimation(UIElement.OpacityProperty,null);
            content.Opacity=1; content.RenderTransform=Transform.Identity;
            controller.Show();
            bindings.SetMode(inputMode.KeyboardMode);
        } else {
            bindings.SetMode(false);
            Find<Button>("ControllerArea").Focus();
        }
        ControllerMotion.Dismiss(leaving,show ? 1.018 : .95,115,0,delegate {
            if(viewRevision==revision) leaving.Visibility=Visibility.Collapsed;
        });
        ControllerMotion.Reveal(entering,show ? .95 : 1.025,show ? 1.012 : .994,show ? 9 : -6,320,.55);
    }
    void SwitchControllerMode(bool keyboard) {
        gamepad.SetMode(keyboard,bindings.Configured);
        RefreshPromptPreparation();
        if(!viewingController) return;
        int revision=++modeRevision;
        if(!keyboard) bindings.SetMode(false);
        var content=Find<Grid>("ControllerPageContent");
        ControllerMotion.Dismiss(content,.967,85,.6,delegate {
            if(revision!=modeRevision || !viewingController) return;
            if(keyboard) bindings.SetMode(true);
            ControllerMotion.Reveal(content,.955,1.012,keyboard ? 14 : -14,290,.6);
        });
    }
    void Navigate(object sender, KeyEventArgs e) {
        if(removalReply!=null) {
            if(e.Key==Key.Escape) { CloseRemovalDialog(false); e.Handled=true; }
            return;
        }
        if(shaderOpen) {
            if(e.Key==Key.Escape || e.Key==Key.Back || e.Key==Key.Left) {
                ShaderPage(false);
                if(e.Key!=Key.Escape) { Menu(true); Find<Button>("ShaderMenu").Focus(); }
                e.Handled=true;
            }
            return;
        }
        if(renderOpen) {
            if(e.Key==Key.Escape) { CloseRenderSettings(); e.Handled=true; }
            return;
        }
        if(SaveCardOpen) {
            if(e.Key==Key.Escape) { CloseSaves(); e.Handled=true; }
            return;
        }
        if(bindings.DeleteOpen) {
            if(e.Key==Key.Escape) { bindings.CloseDelete(); e.Handled=true; }
            return;
        }
        if (Find<Grid>("SettingsPanel").IsVisible) {
            if (e.Key == Key.Escape) { SettingsPanel(false); e.Handled=true; }
            return;
        }
        if (viewingController) {
            if(bindings.HandleKey(e)) { e.Handled=true; return; }
            if (e.Key == Key.Escape) { if(!bindings.Back()) ShowController(false); e.Handled=true; }
            return;
        }
        if (e.Key == Key.Escape && menuOpen) { Menu(false); Find<Button>("More").Focus(); e.Handled=true; return; }
        if (e.Key==Key.Escape && projectCard.Escape()) { e.Handled=true; return; }
        if(importOpen) {
            if(e.Key==Key.Escape && !checking) { OpenImport(false); Find<Button>("AddGame").Focus(); e.Handled=true; }
            return;
        }
        if(e.Key==Key.Escape && caseExpanded) { SetAchievementCase(false,true); Find<Button>("CaseToggle").Focus(); e.Handled=true; return; }
        if(checking || menuOpen || gameTiles.Count==0 || Keyboard.Modifiers!=ModifierKeys.None) return;
        if(e.Key!=Key.Left && e.Key!=Key.Right) return;
        int current=gameTiles.FindIndex(r=>r.IsChecked==true);
        int next=current<0 ? 0 : (current+(e.Key==Key.Right ? 1 : -1)+gameTiles.Count)%gameTiles.Count;
        if(!gameTiles[next].IsEnabled) return;
        gameTiles[next].IsChecked=true;
        gameTiles[next].Focus();
        e.Handled = true;
    }
    void ShowConsole() {
        if(console!=null && console.Open) { console.Activate(); return; }
        try {
            console=new NativeLogConsole(sessionLog.FileName);
        } catch(Exception ex) when(ex is IOException || ex is System.ComponentModel.Win32Exception || ex is UnauthorizedAccessException) {
            MessageBox.Show(window,Ui.Text("无法打开日志控制台。")+"\n"+ex.Message,Ui.Text("实时日志"));
        }
    }
    void AttachLogMenu() {
        var button=Find<Button>("ShowConsole");
        button.Click+=delegate { ShowConsole(); };
        var menu=new ContextMenu { Style=(Style)window.FindResource("profileMenu"), FontFamily=window.FontFamily };
        button.ContextMenu=menu;
        var folder=new MenuItem();
        folder.SetResourceReference(HeaderedItemsControl.HeaderProperty,"ui.打开日志目录");
        folder.Click+=delegate {
            try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(sessionLog.FileName)) { UseShellExecute=true }); }
            catch(Exception e) when(e is IOException || e is System.ComponentModel.Win32Exception || e is UnauthorizedAccessException) { LogActionFailed(e); }
        };
        menu.Items.Add(folder);
        var export=new MenuItem();
        export.SetResourceReference(HeaderedItemsControl.HeaderProperty,"ui.导出当前日志");
        export.Click+=async delegate {
            var save=new Microsoft.Win32.SaveFileDialog { Title=Ui.Text("导出当前日志"), DefaultExt=".log",
                Filter="Log (*.log)|*.log", FileName=Path.GetFileName(sessionLog.FileName) };
            if(save.ShowDialog(window)!=true) return;
            string destination=save.FileName;
            try { await System.Threading.Tasks.Task.Run(()=>sessionLog.Export(destination)); }
            catch(Exception e) when(e is IOException || e is UnauthorizedAccessException) { LogActionFailed(e); }
        };
        menu.Items.Add(export);
    }
    void LogActionFailed(Exception e) {
        Append("Log operation failed: "+e.Message);
        MessageBox.Show(window,e.Message,Ui.Text("实时日志"));
    }
    void ShowLaunchAction() {
        bool needsUpdate=updates.Required && !playing;
        bool blocked=BrowsingDuringPlay || (activeGame!=null && activeGame.Pending==null && !available.Contains(selected));
        SetImportVisible("BlockedLaunch",blocked && !needsUpdate);
        Find<Button>("CriticalUpdate").Visibility=needsUpdate ? Visibility.Visible : Visibility.Collapsed;
        Find<Grid>("LaunchPill").Visibility=!blocked && !needsUpdate && gameExe!=null && activeGame?.Pending==null ? Visibility.Visible : Visibility.Collapsed;
        RefreshInstallAction();
    }
    void SetLaunchState(LaunchState state) {
        bool wasTracking=historyTimer.IsEnabled;
        launchButton.Set(state);
        Find<Button>("RenderSettings").IsEnabled=CanOpenEmulatorSettings;
        if(renderOpen && !CanOpenEmulatorSettings) CloseRenderSettings();
        ShowLaunchAction();
        bindings.SetPromptContext(PromptProject,playing);
        historyTimer.IsEnabled=state==LaunchState.Playing || state==LaunchState.Stopping;
        if(wasTracking!=historyTimer.IsEnabled || state==LaunchState.Ready) RefreshHistory();
        if(saveOpen && !saveBusy) ReloadSaveChoices();
        updates.Refresh();
        foreach (var radio in projects) radio.IsEnabled = available.Contains((string)radio.Tag) && !playing;
        Find<Button>("Retry").IsEnabled=!playing && !checking;
        Find<Button>("ClearShaders").IsEnabled=!playing && !checking;
        Find<Button>("AddGame").IsEnabled=!playing && !checking;
        Find<StackPanel>("GameList").IsEnabled=!checking;
        PositionGameMark(true);
    }
    LaunchState FailureState() {
        return playing || launchButton.State==LaunchState.GameFailed ? LaunchState.GameFailed : LaunchState.WarmupFailed;
    }
    void FailLaunch(LaunchState state,string reason) {
        launchButton.FailureReason=string.IsNullOrWhiteSpace(reason) ? Ui.Text("启动失败，请查看实时日志") : reason.Trim();
        Append("Launch failure: "+Ui.EnglishStatus(launchButton.FailureReason));
        SetLaunchState(state);
    }
    void StartPreparedGame() {
        if(closed || checking || updates.Required || updates.Installing || SaveCardOpen || renderOpen || shaderOpen ||
            launchButton.State!=LaunchState.Ready || restart || worker==null || selected!=running || activeGame!=sessionGame) return;
        try {
            if(saveLibrary==null) {
                OpenGameSaveLibrary();
                if(saveLibrary==null) { FailLaunch(LaunchState.GameFailed,saveError); return; }
            }
            string command=CurrentPlayCommand();
            if(PromptPayload(command)!=workerPromptPayload) { Schedule(launchWhenReady:true); return; }
            SetLaunchState(LaunchState.Starting);
            AllowSetForegroundWindow(worker.Id);
            worker.StandardInput.WriteLine(command);
            worker.StandardInput.Flush();
            Append("Game launch requested: "+(inputMode.KeyboardMode ? "keyboard and mouse" : "controller"));
        } catch(Exception ex) when(ex is IOException || ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception) {
            FailLaunch(LaunchState.GameFailed,ex.Message);
        }
    }
    void Select(string id) {
        if (checking || !available.Contains(id) || playing || selected == id) return;
        selected = id;
        bindings.SetPromptContext(PromptProject,playing);
        activeGame.Emulator=id;
        OpenGameSaveLibrary();
        UpdateSaveMenuLabel();
        RefreshHistory();
        SaveLibrary();
        try { File.WriteAllText(Preference, id); } catch (IOException) { }
        Schedule();
    }
    void CloseInput() {
        if (worker != null) try { worker.StandardInput.Close(); } catch (InvalidOperationException) { } catch (IOException) { }
    }
    string workerPromptPayload="";
    string PromptProject { get { return activeGame?.GtaVi==true && selected=="cgbj" ? selected : null; } }
    string CurrentPlayCommand() {
        if(!inputMode.KeyboardMode) return "PLAY";
        bool show=PromptProject!=null && displaySettings.UseKeyPrompts;
        return selected=="cgbj" ? bindings.PlayCommand(show && bindings.Configured) : "PLAY\tKEYBOARD";
    }
    static string PromptPayload(string command) {
        const string marker="\tPROMPTS=";
        int start=command.IndexOf(marker,StringComparison.Ordinal);
        return start<0 ? "" : command.Substring(start+marker.Length);
    }
    void RefreshPromptPreparation() {
        if(!checking && !playing && activeGame!=null && gameExe!=null && available.Contains(selected) &&
            PromptPayload(CurrentPlayCommand())!=workerPromptPayload) Schedule();
    }
    string nextShaderAction="", shaderActionGame, shaderActionProject;
    void Schedule(string shaderAction="",bool launchWhenReady=false) {
        if (playing || updates.Required) return;
        launchAfterWarmup=launchWhenReady;
        nextShaderAction=shaderAction;
        shaderActionGame=activeGame?.Id;
        shaderActionProject=selected;
        if (worker == null) { Begin(); return; }
        restart = true;
        SetLaunchState(LaunchState.Warming);
        if(PromptPayload(CurrentPlayCommand()).Length!=0) launchButton.WarmStage("正在准备键位提示");
        cancelWarmup.Set();
        CloseInput();
    }
    void Append(string text) {
        sessionLog.Write("LAUNCHER",text);
    }
    void Dispatch(Action action) {
        if (!window.Dispatcher.HasShutdownStarted) window.Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }
    ProcessStartInfo SessionInfo(string arguments,string assets,string exe,bool redirect=false) {
        var python = Path.Combine(root, "Runtime", "Python", "python.exe");
#if NETFRAMEWORK
        if (!File.Exists(python)) {
            python = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @".cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe");
            if (!File.Exists(python)) python = "python.exe";
        }
#endif
        var si = new ProcessStartInfo {
            FileName = python,
            Arguments = "-X utf8 -B -u \"" + Path.Combine(Lab, @"desktop\session.py") + "\" " + arguments,
            WorkingDirectory = Lab, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = redirect, RedirectStandardOutput = redirect, RedirectStandardError = redirect,
            StandardOutputEncoding = redirect ? Encoding.UTF8 : null, StandardErrorEncoding = redirect ? Encoding.UTF8 : null
        };
        si.EnvironmentVariables["PYTHONUTF8"] = "1";
        si.EnvironmentVariables["PALMLINE_GAME_EXE"]=exe;
        si.EnvironmentVariables["PALMLINE_GAME_ASSETS"]=assets;
        return si;
    }
    AchievementToast.Session achievementSession;
    void EndAchievementSession() { achievementSession?.Dispose(); achievementSession=null; }
    void Begin() {
        if (closed || updates.Required) return;
        sessionGame=activeGame;
        if(gameExe==null || activeGame?.Pending!=null || !available.Contains(selected)) {
            restart=false; SetLaunchState(LaunchState.Idle); return;
        }
        running = selected; restart = false;
        logDirectory = null;
        Append("Selected emulator: "+ProjectName(running));
        string action=shaderActionGame==activeGame.Id && shaderActionProject==running ? nextShaderAction : "";
        workerShaderAction=action;
        nextShaderAction="";
        if(!ValidFiles(gameAssets,gameExe)) {
            FailLaunch(LaunchState.WarmupFailed,Ui.Text("请检查游戏本体目录与 EXE 是否匹配。")+"\n"+gameAssets+"\n"+gameExe);
            return;
        }
        SetLaunchState(LaunchState.Warming);
        bool clearing=action=="--clear-shaders";
        EndAchievementSession();
        if(!clearing && activeGame.GtaVi) {
            try { achievementSession=new AchievementToast.Session(Lab,activeGame.Id,Append); }
            catch(Exception ex) { Append(AchievementToast.Session.SystemName+" initialization: "+ex.Message); }
        }
        var p=new Process();
        try {
            var si=SessionInfo(running+" "+action,gameAssets,gameExe,true);
            p.StartInfo=si;
            si.EnvironmentVariables["PALMLINE_INSTANCE_ID"]=activeGame.Id;
            if(running=="xcl") {
                string gameLibrary=Path.Combine(root,"GameSaves",activeGame.Id);
                si.EnvironmentVariables["PALMLINE_XCL_DATA_ROOT"]=(saveLibrary as XclSaveLibrary)?.DefaultNativeRoot ?? Path.Combine(gameLibrary,"xcl");
                si.EnvironmentVariables["PALMLINE_XCL_SLOT_LIBRARY"]=Path.Combine(gameLibrary,"xcl-slots");
            }
            else si.EnvironmentVariables["PALMLINE_SAVE_LIBRARY"]=Path.Combine(root,"GameSaves",activeGame.Id);
            si.EnvironmentVariables["PALMLINE_LAUNCHER_HWND"]=new System.Windows.Interop.WindowInteropHelper(window).Handle.ToInt64().ToString();
            workerPromptPayload=PromptPayload(CurrentPlayCommand());
            si.EnvironmentVariables["PALMLINE_PRELOAD_PROMPTS"]=workerPromptPayload;
            si.EnvironmentVariables.Remove("PALMLINE_PROMPT_MODULE");
            if(clearing) launchButton.WarmStage("正在清理着色器缓存");
            else if(workerPromptPayload.Length!=0) {
                launchButton.WarmStage("正在准备键位提示");
                si.EnvironmentVariables["PALMLINE_PROMPT_MODULE"]=GameKeyPrompts.InstallModule(root);
            }
            string eventName="Local\\Palmline.Warmup."+Guid.NewGuid().ToString("N");
            cancelWarmup=new EventWaitHandle(false,EventResetMode.ManualReset,eventName);
            si.EnvironmentVariables["PALMLINE_WARMUP_EVENT"]=eventName;
            p.Start(); worker = p;
            string lastStderr=null;
            p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                if (!string.IsNullOrWhiteSpace(e.Data)) {
                    Interlocked.Exchange(ref lastStderr,e.Data.Trim());
                    Append("Session worker stderr: "+e.Data);
                }
            };
            p.BeginErrorReadLine();
            var reader = new Thread(delegate() {
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null) {
                    string entry = line;
                    if(!RecordWorkerLine(entry)) continue;
                    Dispatch(delegate { if (!closed && !checking && worker==p && !restart) Receive(entry); });
                }
                p.WaitForExit(); int code = p.ExitCode;
                Append("Session worker exited with code "+code+".");
                Dispatch(delegate {
                    if (worker != p) return;
                    EndAchievementSession();
                    worker = null; p.Dispose();
                    cancelWarmup.Dispose(); cancelWarmup=null;
                    if (closed || checking) return;
                    if (updates.Required) {
                        restart=false; sessionGame=null; SetLaunchState(LaunchState.Idle);
                    } else if (restart || sessionGame!=activeGame) Begin();
                    else if (gameExe==null) UpdateImportStep();
                    else if(!launchButton.Failed) {
                        if(clearing && code==0) SetLaunchState(LaunchState.CacheCleared);
                        else FailLaunch(FailureState(),Ui.Format("启动会话意外结束（退出码 {0}）",code)+
                            (string.IsNullOrEmpty(lastStderr) ? "" : "\n"+lastStderr));
                    }
                });
            });
            reader.IsBackground = true; reader.Start();
        } catch (Exception ex) {
            if(cancelWarmup!=null) { cancelWarmup.Set(); cancelWarmup.Dispose(); cancelWarmup=null; }
            EndAchievementSession(); worker=null; p.Dispose(); FailLaunch(LaunchState.WarmupFailed,ex.Message);
        }
    }
    bool RecordWorkerLine(string line) {
        int tab=line.IndexOf('\t');
        if(tab<0) { Append(line); return false; }
        string kind=line.Substring(0,tab), message=line.Substring(tab+1);
        if(kind=="GAME" || kind=="EMULATOR") { sessionLog.Write(kind,message); return false; }
        Append(kind+" | "+Ui.EnglishStatus(message));
        return kind!="LOG" || message.IndexOf(" achievement] ",StringComparison.Ordinal)>=0;
    }
    async void Receive(string line) {
        int tab = line.IndexOf('\t');
        if (tab < 0) return;
        string kind = line.Substring(0, tab), msg = line.Substring(tab + 1);
        if (kind == "LOG") { achievementSession?.Observe(msg); return; }
        if (kind == "RUN") { logDirectory = msg; return; }
        if(launchButton.Failed) return;
        if (kind == "READY") {
            if(playing || restart) return;
            if(running=="victory-nvidia") {
                var preparedWorker=worker;
                bool supported=HasGameUpscaler() && await ProbeSuperResolution();
                if(closed || checking || restart || worker==null || worker!=preparedWorker || selected!=running ||
                    activeGame!=sessionGame || launchButton.State!=LaunchState.Warming) return;
                try {
                    ReadVictoryMenu().ApplyDlssSupport(supported);
                } catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException) {
                    FailLaunch(LaunchState.WarmupFailed,ex.Message); return;
                }
            }
            workerShaderAction="";
            SetLaunchState(LaunchState.Ready);
            if(launchAfterWarmup) { launchAfterWarmup=false; StartPreparedGame(); }
            return;
        }
        if (kind == "STAGE") { if(!restart) launchButton.WarmStage(msg); return; }
        if (kind == "SHADER_ERROR") { FailLaunch(LaunchState.WarmupFailed,msg); return; }
        if (kind == "COUNT" || kind == "PROGRESS") {
            if(restart) return;
            if(kind=="COUNT") {
                if(int.TryParse(msg,out int count) && count>=0) launchButton.WarmProgress(count);
            } else {
                var parts=msg.Split('/');
                if(parts.Length==2 && int.TryParse(parts[0],out int done) && int.TryParse(parts[1],out int total) &&
                    total>0 && done>=0 && done<=total) launchButton.WarmProgress(done,total);
            }
            return;
        }
        if ((kind=="ERROR" || kind=="EXIT") && BrowsingDuringPlay) {
            // This worker exits after reporting the result. Prepare the browsed game on exit.
            restart=true; SetLaunchState(LaunchState.Warming); return;
        }
        if (kind == "ERROR") { FailLaunch(FailureState(),msg); return; }
        if (kind == "RENDER_INIT") {
            if (launchButton.State != LaunchState.Stopping) {
                SetLaunchState(LaunchState.Rendering);
            }
            return;
        }
        if (kind == "PLAYING" && launchButton.State==LaunchState.Stopping) return;
        if (kind == "PLAYING") { if(int.TryParse(msg,out int gamePid)) achievementSession?.Attach(gamePid); SetLaunchState(LaunchState.Playing); return; }
        if (kind == "EXIT") {
            EndAchievementSession();
            if(msg!="0") { FailLaunch(LaunchState.GameFailed,Ui.Format("游戏退出码 {0}",msg)); return; }
            if(launchButton.State==LaunchState.Starting || launchButton.State==LaunchState.Rendering) {
                FailLaunch(LaunchState.GameFailed,Ui.Text("游戏在启动完成前退出")); return;
            }
            restart=true;
            SetLaunchState(LaunchState.Warming);
            return;
        }
    }
    [STAThread]
    static int Main(string[] args) {
        SessionLog log=null;
        try {
            string root = FindLauncherDirectory();
            log=new SessionLog(root);
            var app = new Application { ShutdownMode=ShutdownMode.OnLastWindowClose };
            using (var once = new Mutex(false, UpdateIdentity.MutexName(root))) {
                bool owns;
                try { owns = once.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                if (!owns) { log.Write("LAUNCHER","Another launcher instance is open."); MessageBox.Show(Ui.Text("启动器已打开。"), "Grand Theft Auto VI"); return 0; }
                try {
                    if (LauncherUpdate.Start(root)) { log.Write("LAUNCHER","Handed off to the updater."); return 0; }
                    var shell = new DesktopLauncher(args.Length == 2 && args[0] == "--project" ? args[1] : null,log);
                    log.Write("LAUNCHER","Main window initialized.");
                    // An open log can outlive this launcher session.
                    shell.window.Closed += delegate { once.ReleaseMutex(); owns=false; };
                    return app.Run(shell.window);
                } finally { if(owns) once.ReleaseMutex(); }
            }
        } catch (Exception ex) {
            log?.Write("LAUNCHER","Startup or application failure: "+ex);
            MessageBox.Show(ex.Message, "Grand Theft Auto VI");
            return 1;
        } finally { log?.Dispose(); }
    }
}
