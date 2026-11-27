using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

internal sealed partial class DesktopLauncher
{
    enum GamePresentation { Classic, LegacyCustom, Custom, Cyberleek }
    sealed class GameInstance
    {
        public string Id, Name, Assets, Exe, Emulator, ArtworkId, LauncherProfile;
        public bool GtaVi;
        public double LogoX=.5, LogoY=.5, LogoSize=.44;
        public GamePresentation Presentation;
        public bool CustomPresentation => Presentation==GamePresentation.Custom || Presentation==GamePresentation.LegacyCustom;
        public InstallPlan Pending;
        public string[] Compatible;
    }

    readonly List<GameInstance> games=new List<GameInstance>();
    readonly List<RadioButton> gameTiles=new List<RadioButton>();
    GameInstance activeGame;
    bool syncingLibrary;
    string LibraryFile { get { return Path.Combine(Lab,"desktop","game-instances.xml"); } }

    // The official 150px artwork includes a four-pixel square frame.
    static readonly Int32Rect GtaViCoverArea=new Int32Rect(4,4,142,142);

    BitmapSource GameIcon(GameInstance game) {
        if(game.CustomPresentation) return LoadOptionalImage(game,"icon.png",256);
        if(game.Presentation==GamePresentation.Cyberleek) return Artwork("GtaViTile.png",GtaViCoverArea);
        bool gta=false;
        try {
            var manifest=XElement.Load(Path.Combine(game.Assets,"MicrosoftGame.Config"));
            gta=string.Equals((string)manifest.Elements().FirstOrDefault(x=>x.Name.LocalName=="TitleId"),"7952987A",StringComparison.OrdinalIgnoreCase);
            var visuals=manifest.Elements().FirstOrDefault(x=>x.Name.LocalName=="ShellVisuals");
            string name=(string)visuals?.Attribute("Square150x150Logo");
            if(!string.IsNullOrEmpty(name) && !Path.IsPathRooted(name)) {
                string root=Path.GetFullPath(game.Assets).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                string logo=Path.GetFullPath(Path.Combine(root,name));
                if(!logo.StartsWith(root,StringComparison.OrdinalIgnoreCase)) return null;
                using(var file=File.OpenRead(logo)) {
                    var icon=new BitmapImage();
                    icon.BeginInit(); icon.CacheOption=BitmapCacheOption.OnLoad; icon.StreamSource=file; icon.EndInit();
                    icon.Freeze();
                    if(gta &&
                       string.Equals(name,"xbs_logo_150.png",StringComparison.OrdinalIgnoreCase) &&
                       icon.PixelWidth==150 && icon.PixelHeight==150) {
                        var cover=new CroppedBitmap(icon,GtaViCoverArea);
                        cover.Freeze();
                        return cover;
                    }
                    return icon;
                }
            }
        } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException || e is XmlException ||
                                   e is NotSupportedException || e is ArgumentException || e is FileFormatException) { }
        return gta ? Artwork("GtaViTile.png",GtaViCoverArea) : null;
    }

    static bool GtaOnlyEngine(string id) { return id=="cgbj" || id=="victory-nvidia"; }
    const string BundledVersionName="CYBERLEEK · v1.0.2.1";
    const string OptimizedProfile="cyberleek-1.0.2.1";
    string[] ProfileEngines(string profile) {
        return catalog.Keys.Where(id=>profile==OptimizedProfile ? GtaOnlyEngine(id) : id=="xcl").ToArray();
    }
    static bool IsGtaVi(string assets) {
        try {
            return XElement.Load(Path.Combine(assets,"MicrosoftGame.Config")).Elements()
                .Any(e=>e.Name.LocalName=="TitleId" && string.Equals(e.Value.Trim(),"7952987A",StringComparison.OrdinalIgnoreCase));
        } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException || e is XmlException) { return false; }
    }
    string[] EnginesForGame(bool gta,string[] ids) {
        var allowed=ids.Where(id=>catalog.ContainsKey(id) && GtaOnlyEngine(id)==gta).Distinct().ToArray();
        // Older libraries could list only a GTA-specific engine for a different title.
        return !gta && allowed.Length==0 && ids.Any(GtaOnlyEngine) && catalog.ContainsKey("xcl") ? new[]{"xcl"} : allowed;
    }
    static string GameDisplayName(string assets,string exe) {
        var manifest=XElement.Load(Path.Combine(assets,"MicrosoftGame.Config"));
        var visuals=manifest.Elements().FirstOrDefault(x=>x.Name.LocalName=="ShellVisuals");
        var name=(string)visuals?.Attribute("DefaultDisplayName");
        if(string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:",StringComparison.OrdinalIgnoreCase))
            name=(string)manifest.Elements().FirstOrDefault(x=>x.Name.LocalName=="Identity")?.Attribute("Name") ?? Path.GetFileNameWithoutExtension(exe);
        return name;
    }
    GameInstance RegisterGame(string assets,string exe,string[] compatible,string id=null,string name=null,string emulator=null,string profile=null) {
        assets=Path.GetFullPath(assets).TrimEnd(Path.DirectorySeparatorChar);
        exe=Path.GetFullPath(exe);
        bool gta=IsGtaVi(assets);
        // Reclassify older GTA entries that were registered with only the general emulator.
        if(gta && compatible.Length==1 && compatible[0]=="xcl") compatible=catalog.Keys.ToArray();
        compatible=profile!=null ? ProfileEngines(profile) : EnginesForGame(gta,compatible);
        var existing=id!=null ? games.FirstOrDefault(g=>g.Id==id) :
            games.FirstOrDefault(g=>string.Equals(g.Assets,assets,StringComparison.OrdinalIgnoreCase)
                && string.Equals(g.Exe,exe,StringComparison.OrdinalIgnoreCase));
        if(existing!=null) {
            existing.GtaVi=gta;
            existing.Compatible=compatible;
            if(!compatible.Contains(existing.Emulator)) existing.Emulator=compatible.FirstOrDefault();
            return existing;
        }
        if(string.IsNullOrWhiteSpace(name)) name=GameDisplayName(assets,exe);
        var game=new GameInstance { Id=id ?? Guid.NewGuid().ToString("N"), Name=name, Assets=assets, Exe=exe,GtaVi=gta,
            LauncherProfile=profile,Compatible=compatible, Emulator=compatible.Contains(emulator) ? emulator : compatible.FirstOrDefault() };
        games.Add(game);
        gameTiles.Add(CreateGameTile(game));
        return game;
    }

    void LoadLibrary() {
        var data=XElement.Load(LibraryFile);
        foreach(var row in data.Elements("game")) {
            string id=(string)row.Attribute("id"), assets=(string)row.Attribute("assets"), exe=(string)row.Attribute("exe");
            Guid guid;
            if(!Guid.TryParseExact(id,"N",out guid) || games.Any(g=>g.Id==id)) continue;
            var compatible=row.Elements("emulator").Select(x=>(string)x).Where(catalog.ContainsKey).Distinct().ToArray();
            var pending=InstallPlan.Read(row.Element("install"));
            string profile=(string)row.Attribute("launcherProfile");
            if(profile!="xcl" && profile!=OptimizedProfile) profile=null;
            if(pending==null && (string.IsNullOrWhiteSpace(assets) || string.IsNullOrWhiteSpace(exe))) continue;
            GameInstance game;
            if(pending==null) game=RegisterGame(GamePath(assets),GamePath(exe),compatible,id,
                (string)row.Attribute("name") ?? Path.GetFileNameWithoutExtension(exe),(string)row.Attribute("selected"),profile);
            else {
                game=new GameInstance { Id=id,Name=(string)row.Attribute("name"),Assets=GamePath(assets),Exe=GamePath(exe),
                    LauncherProfile=profile,Pending=pending,Compatible=profile!=null ? ProfileEngines(profile) : compatible,Emulator=(string)row.Attribute("selected") };
                if(!game.Compatible.Contains(game.Emulator)) game.Emulator=game.Compatible.FirstOrDefault();
                games.Add(game); gameTiles.Add(CreateGameTile(game));
            }
            if(Guid.TryParseExact((string)row.Attribute("artwork"),"N",out guid)) game.ArtworkId=guid.ToString("N");
            if(game.ArtworkId!=null) pending?.UseMetadata(CardArtFolder(game));
            game.LogoX=ReadPosition(row,"logoX");
            game.LogoY=ReadPosition(row,"logoY");
            string appearance=(string)row.Attribute("presentation");
            game.Presentation=appearance==OptimizedProfile ? GamePresentation.Cyberleek :
                appearance=="3" ? GamePresentation.Custom : appearance=="2" ? GamePresentation.LegacyCustom : GamePresentation.Classic;
            if(game.Presentation==GamePresentation.Cyberleek && pending!=null) game.GtaVi=true;
            if(row.Attribute("logoSize")!=null) game.LogoSize=Math.Max(.12,Math.Min(.92,ReadPosition(row,"logoSize")));
            if(game.Presentation!=GamePresentation.Classic) RefreshGameTile(game);
            if(id==(string)data.Attribute("active")) ActivateGame(game,false);
        }
    }

    static double ReadPosition(XElement row,string attribute) {
        double value;
        return double.TryParse((string)row.Attribute(attribute),NumberStyles.Float,CultureInfo.InvariantCulture,out value)
            && value>=0 && value<=1 ? value : .5;
    }

    void WriteLibrary(IEnumerable<GameInstance> entries,string activeId) {
        var data=new XElement("games",new XAttribute("active",activeId ?? ""),entries.Select(g=>
            new XElement("game",new XAttribute("id",g.Id),new XAttribute("name",g.Name),
                g.ArtworkId==null ? null : new XAttribute("artwork",g.ArtworkId),
                new XAttribute("logoX",g.LogoX),new XAttribute("logoY",g.LogoY),
                g.Presentation==GamePresentation.Classic ? null : new XAttribute("presentation",
                    g.Presentation==GamePresentation.Cyberleek ? OptimizedProfile : g.Presentation==GamePresentation.Custom ? "3" : "2"),
                new XAttribute("logoSize",g.LogoSize),g.Pending?.Xml(),
                new XAttribute("assets",string.IsNullOrEmpty(g.Assets) ? "" : StoreGamePath(g.Assets)),new XAttribute("exe",string.IsNullOrEmpty(g.Exe) ? "" : StoreGamePath(g.Exe)),
                g.LauncherProfile==null ? null : new XAttribute("launcherProfile",g.LauncherProfile),
                new XAttribute("selected",g.Emulator ?? ""),g.Compatible.Select(id=>new XElement("emulator",id)))));
        var temp=LibraryFile+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            data.Save(temp);
            if(File.Exists(LibraryFile)) File.Replace(temp,LibraryFile,null); else File.Move(temp,LibraryFile);
        } finally { if(File.Exists(temp)) File.Delete(temp); }
    }

    void SaveLibrary() {
        try { WriteLibrary(games,activeGame?.Id); }
        catch(Exception e) when(e is IOException || e is UnauthorizedAccessException) { Append(e.Message); }
    }

    async Task CheckNewEngines() {
        foreach(var game in games.ToArray()) {
            if(game.Pending!=null || game.LauncherProfile!=null || !ValidFiles(game.Assets,game.Exe)) continue;
            foreach(string id in EnginesForGame(game.GtaVi,catalog.Keys.ToArray())) {
                if(closed || checking) return;
                if(game.Compatible.Contains(id)) continue;
                try {
                    if(!await Task.Run(()=>Probe(id,game.Assets,game.Exe))) continue;
                } catch(Exception e) when(e is IOException || e is System.ComponentModel.Win32Exception || e is UnauthorizedAccessException) {
                    Append(e.Message); continue;
                }
                if(closed || checking || !games.Contains(game)) return;
                game.Compatible=game.Compatible.Concat(new[]{id}).Distinct().ToArray();
                if(activeGame==game) {
                    available.Add(id);
                    projects.First(r=>(string)r.Tag==id).IsEnabled=!playing;
                    if(game.Emulator==null) {
                        game.Emulator=selected=id;
                        projects.First(r=>(string)r.Tag==id).IsChecked=true;
                        ShowLaunchAction(); Schedule();
                    }
                }
                SaveLibrary();
            }
        }
    }

    void ActivateGame(GameInstance game,bool start) {
        if(activeGame!=game) installMessage="";
        activeGame=game; gameExe=game.Exe; gameAssets=game.Assets;
        available.Clear(); available.UnionWith(game.Compatible);
        selected=game.Emulator;
        syncingLibrary=true;
        try {
            var tile=gameTiles.First(r=>r.Tag==game);
            tile.IsEnabled=true;
            tile.IsChecked=true;
            tile.BringIntoView();
            foreach(var radio in projects) radio.IsChecked=(string)radio.Tag==selected;
        } finally { syncingLibrary=false; }
        if(start) {
            OpenImport(false);
            SaveLibrary(); DisplayGame(true); Schedule();
        }
    }

    sealed class PlayHistory
    {
        public long Latest, Seconds;
        public string Date { get { return Latest==0 ? Ui.Text("尚无记录") :
            DateTimeOffset.FromUnixTimeSeconds(Latest).ToLocalTime().ToString("g",CultureInfo.GetCultureInfo(Ui.Language)); } }
        public string Duration { get { return Seconds<60 && Seconds>0 ? Ui.Format("{0} 秒",Seconds) :
            Seconds<3600 ? Ui.Format("{0} 分钟",Seconds/60) : Ui.Format("{0} 小时",Seconds/3600); } }
    }

    static PlayHistory ReadHistory(string folder) {
        long latest=0,total=0;

        if(Directory.Exists(folder)) {
            try {
                foreach(string path in Directory.GetFiles(folder,"*.time")) {
                    try {
                        string[] values=File.ReadAllLines(path);
                        long started,seconds;
                        if(values.Length==2 && long.TryParse(values[0],out started) && started>0 && started<=253402300799L
                           && long.TryParse(values[1],out seconds) && seconds>=0) {
                            latest=Math.Max(latest,started);
                            total+=Math.Min(seconds,long.MaxValue-total);
                        }
                    } catch(FileNotFoundException) { }
                }
            } catch(DirectoryNotFoundException) { }
        }
        return new PlayHistory { Latest=latest, Seconds=total };
    }

    PlayHistory LoadHistory(GameInstance game) {
        var folder=Path.Combine(Lab,"desktop","play-history",game.Id);
        try { return ReadHistory(folder); }
        catch(Exception e) when(e is IOException || e is UnauthorizedAccessException) {
            Dispatch(()=>Append(e.Message));
            return null;
        }
    }

    int historyRevision;
    async void RefreshHistory() {
        var game=activeGame;
        int revision=++historyRevision;
        if(game==null || closed) return;
        var library=saveLibrary;
        string slot=library?.Selected;
        var record=await Task.Run(()=>Tuple.Create(LoadHistory(game),LatestStory(library,slot))).ConfigureAwait(false);
        Dispatch(()=> {
            if(!closed && historyRevision==revision && activeGame==game) ShowHistory(record.Item1,record.Item2);
        });
    }

    void ShowHistory(PlayHistory history,LocalSaveLibrary.Story story) {
        if(caseExpanded) RefreshAchievementCase();
        bool played=history!=null && history.Latest!=0;
        bool hasStats=played || story!=null;
        Find<StackPanel>("HistoryEmpty").Visibility=hasStats ? Visibility.Collapsed : Visibility.Visible;
        Find<Launcher.Layout.HistoryStatsPanel>("HistoryStats").Visibility=hasStats ? Visibility.Visible : Visibility.Hidden;
        Find<Launcher.Layout.AdaptiveLaunchTime>("LastLaunch").SetTime(history?.Latest ?? 0,hasStats ? Ui.Text("尚无记录") : "—");
        Find<TextBlock>("GameProgress").Text=story!=null ? StoryChapterTitle(story) : hasStats ? Ui.Text("暂无存档") : "—";
        Find<TextBlock>("GameProgressPercent").Text=StoryProgressSuffix(story);
        Find<DockPanel>("GameProgressValue").ToolTip=story?.Title;
        Find<TextBlock>("GamePlaytime").Text=hasStats ? history?.Duration ?? "—" : "—";
        if(!hasStats) {
            Find<TextBlock>("HistoryStatus").Text=Ui.Text(history==null ? "尚无记录" : "崭新出厂");
        }
    }

    void AttachGameTip(RadioButton tile,FrameworkElement cover,GameInstance game) {
        var title=new TextBlock { Text=game.Name,FontSize=15,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,5) };
        var last=new TextBlock();
        var time=new TextBlock { Margin=new Thickness(0,2,0,0) };
        var content=new StackPanel();
        content.Children.Add(title); content.Children.Add(last); content.Children.Add(time);
        // The tooltip template leaves 5 DIPs at the sides and 4 at the top for its shadow.
        var tip=new ToolTip { Placement=PlacementMode.Right,PlacementTarget=tile,HorizontalOffset=0,VerticalOffset=-4,Content=content };
        tile.ToolTip=tip;
        tile.ToolTipOpening+=delegate {
            tip.PlacementRectangle=cover.TransformToAncestor(tile).TransformBounds(new Rect(cover.RenderSize));
        };
        ToolTipService.SetInitialShowDelay(tile,160);
        ToolTipService.SetShowOnDisabled(tile,true);
        tip.Opened+=async delegate {
            title.Text=game.Name;
            var history=await Task.Run(()=>LoadHistory(game));
            if(history==null) return;
            last.Text=Ui.Format("上次运行　{0}",history.Date);
            time.Text=Ui.Format("游玩时间　{0}",history.Duration);
        };
    }
}
