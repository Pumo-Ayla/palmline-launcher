using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Xml;
using System.Xml.Linq;

internal sealed partial class DesktopLauncher
{
    readonly HashSet<string> available=new HashSet<string>();
    bool importOpen, checking;

    void SuspendGame() {
        if(checking || gameExe==null) return;
        try {
            if(playing) File.WriteAllText(Path.Combine(logDirectory,"exit-request"),"");
            File.Delete(GameSelection);
            File.WriteAllText(ManualImportMarker,"");
        } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException) {
            Append("Unable to clear game selection: "+e.Message);
            MessageBox.Show(window,e.Message,window.Title); return;
        }
        restart=false;
        activeGame=null; gameExe=gameAssets=null;
        foreach(var tile in gameTiles) tile.IsChecked=false;
        SaveLibrary();
        if(cancelWarmup!=null) cancelWarmup.Set();
        CloseInput();
        available.Clear();
        foreach(var tile in projects) tile.IsChecked=false;
        SetLaunchState(LaunchState.Idle);
        Find<TextBlock>("ImportStatus").Text="";
        RefreshImportAction();
        DisplayGame();
        Find<Button>("AddGame").Focus();
    }

    static IEnumerable<string> DeclaredExecutables(string assets,XElement config) {
        string root=Path.GetFullPath(assets).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        foreach(var item in config.Descendants().Where(x=>x.Name.LocalName=="Executable")) {
            string name=(string)item.Attribute("Name");
            if(string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name)) continue;
            string candidate=Path.GetFullPath(Path.Combine(root,name));
            if(candidate.StartsWith(root,StringComparison.OrdinalIgnoreCase)) yield return candidate;
        }
    }

    static bool ValidFiles(string assets,string exe) {
        try {
            ValidateImportFiles(assets,exe);
            return true;
        } catch(Exception e) when(e is InvalidDataException || e is IOException || e is UnauthorizedAccessException || e is XmlException || e is ArgumentException || e is NotSupportedException) { return false; }
    }

    static void ValidateImportFiles(string assets,string exe) {
        var config=XElement.Load(Path.Combine(assets,"MicrosoftGame.Config"));
        bool gta=config.Elements().Any(e=>e.Name.LocalName=="TitleId" && string.Equals(e.Value,"7952987A",StringComparison.OrdinalIgnoreCase));
        CheckAssetArchives(assets,gta);
        string path=Path.GetFullPath(exe);
        if(!DeclaredExecutables(assets,config).Any(p=>string.Equals(p,path,StringComparison.OrdinalIgnoreCase) ||
            (gta && string.Equals(Path.GetFileName(p),Path.GetFileName(path),StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException(Ui.Text("所选 EXE 与游戏清单不匹配。"));
        using(var r=new BinaryReader(File.OpenRead(path))) {
            if(r.BaseStream.Length>=64 && r.ReadUInt16()==0x5a4d) {
                r.BaseStream.Position=60;
                long pe=r.ReadUInt32();
                if(pe<=r.BaseStream.Length-6) {
                    r.BaseStream.Position=pe;
                    if(r.ReadUInt32()==0x4550 && r.ReadUInt16()==0x8664) return;
                }
            }
        }
        throw new InvalidDataException(Ui.Format("EXE 格式无效：{0}",Path.GetFileName(path)));
    }

    static void CheckAssetArchives(string assets,bool gta) {
        if(!gta) return;
        foreach(string archive in new[]{"common.rpf","shaders.rpf","levels.rpf","textures.rpf"}) {
            using(var r=new BinaryReader(File.OpenRead(Path.Combine(assets,archive))))
                if(r.BaseStream.Length<16 || r.ReadUInt32()!=0x52504639)
                    throw new InvalidDataException(Ui.Format("资源文件格式无效：{0}",archive));
        }
    }

    string SavedExePath { get { return Path.Combine(Lab,"desktop","preferred-game-exe.txt"); } }
    string ManualImportMarker { get { return Path.Combine(Lab,"desktop","manual-import"); } }

    string GamePath(string value) {
        return string.IsNullOrWhiteSpace(value) ? "" : Path.GetFullPath(Path.Combine(root,value));
    }

    string StoreGamePath(string path) {
        var home=new Uri(root+Path.DirectorySeparatorChar);
        var target=new Uri(Path.GetFullPath(path));
        var parent=Directory.GetParent(root);
        if(!home.IsBaseOf(target) && (parent==null || !new Uri(parent.FullName.TrimEnd('\\')+"\\").IsBaseOf(target)))
            return path;
        return Uri.UnescapeDataString(home.MakeRelativeUri(target).ToString()).Replace('/','\\');
    }

    string BundledGameExe { get { return Path.Combine(root,"OriginalGame","game_scarlett_master.exe"); } }

    string DefaultGameExe() {
        if(File.Exists(SavedExePath)) {
            var saved=GamePath(File.ReadAllText(SavedExePath).Trim());
            if(File.Exists(saved)) return saved;
        }
        return File.Exists(BundledGameExe) ? BundledGameExe : "";
    }

    void SetupImport() {
        SetupMagnetTransfer();
        SetupLicenses();
        var assets=Find<TextBox>("AssetPath");
        var exe=Find<TextBox>("ExePath");
        var magnet=Find<RadioButton>("MagnetSource");
        var local=Find<RadioButton>("LocalSource");
        SetupImportCard();
        RoutedEventHandler selectSource=delegate { RefreshImportAction(); };
        magnet.Checked+=selectSource;
        local.Checked+=selectSource;
        try {
            exe.Text=DefaultGameExe();
            if(File.Exists(LibraryFile)) LoadLibrary();
            else if(File.Exists(GameSelection)) {
                var paths=File.ReadAllLines(GameSelection);
                if(paths.Length>0 && File.Exists(GamePath(paths[0]))) {
                    exe.Text=GamePath(paths[0]);
                    var folder=Directory.GetParent(exe.Text);
                    assets.Text=paths.Length>1 ? GamePath(paths[1]) : File.Exists(Path.Combine(folder.FullName,"shaders.rpf")) ?
                        folder.FullName : Path.Combine(folder.Parent?.FullName ?? folder.FullName,"GameData");
                    if(ValidFiles(assets.Text,exe.Text)) {
                        var compatible=paths.Skip(2).Where(catalog.ContainsKey).Distinct().ToArray();
                        if(compatible.Length>0) {
                            string previous=File.Exists(Preference) ? File.ReadAllText(Preference).Trim() : null;
                            var game=RegisterGame(assets.Text,exe.Text,compatible,emulator:previous);
                            ActivateGame(game,false); SaveLibrary();
                            var history=Path.Combine(Lab,"desktop","play-history",game.Id);
                            Directory.CreateDirectory(history);
                            File.WriteAllText(Path.Combine(history,"import-legacy"),"");
                        }
                    }
                }
                else File.Delete(GameSelection);
            }
        } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException || e is XmlException) { Append(e.Message); }
        if(games.Count==0 && !File.Exists(ManualImportMarker)) {
            var parent=Directory.GetParent(root)?.FullName ?? root;
            foreach(var baseDir in new[]{root,parent}.Distinct(StringComparer.OrdinalIgnoreCase)) {
                var nearby=ValidFiles(assets.Text,exe.Text) ? assets.Text : new[]{"Game","GameData",Path.Combine("Grand Theft Auto VI Original","GameData"),""}
                    .Select(name=>Path.Combine(baseDir,name)).FirstOrDefault(path=>ImportCandidates(path,exe.Text).Length>0);
                if(nearby==null) continue;
                assets.Text=nearby;
                exe.Text=ImportCandidates(nearby,exe.Text).FirstOrDefault() ?? exe.Text;
                window.ContentRendered+=delegate {
                    if(gameExe==null && !checking && !closed) {
                        OpenImport(true);
                    }
                };
                break;
            }
        }
        Find<Button>("AddGame").Click+=delegate { if(!checking) OpenImport(!importOpen); };
        Find<Button>("ImportBack").Click+=delegate {
            if(importStep==3) { LobbyStep(false); return; }
            if(importStep==1 && sourceStage==SourceStage.Archive && compactImport && archiveDestinationPage && resolvedAssets==null) {
                ShowArchiveDestination(false);
                return;
            }
            if(importStep==1 && sourceStage!=SourceStage.Address) {
                InvalidateFileCheck();
                Find<TextBlock>("ImportStatus").Text="";
                if(sourceStage==SourceStage.Archive) sourceStage=SupplyExeSelected ? SourceStage.External : SourceStage.Contents;
                else if(sourceStage==SourceStage.External) sourceStage=SourceStage.Contents;
                else ResetSourceCheck();
                UpdateImportStep();
                SlideImportPage(false);
                return;
            }
            if(importStep==0 || transfer!=null) OpenImport(false);
            else {
                ClearRecognition();
                InvalidateFileCheck();
                --importStep;
                if(importStep==0) {
                    ResetSourceCheck();
                    var previous=local.IsChecked==true ? local : magnet;
                    UpdateImportStep();
                    previous.Focus();
                } else UpdateImportStep();
                SlideImportPage(false);
            }
        };
        Find<Button>("PickAssets").Click+=delegate {
            using(var dialog=new System.Windows.Forms.FolderBrowserDialog { Description=Ui.Text("游戏本体目录"), ShowNewFolderButton=false,
                SelectedPath=Directory.Exists(assets.Text) ? assets.Text : Directory.GetParent(root)?.FullName ?? root }) {
                if(dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK) {
                    var path=dialog.SelectedPath;
                    assets.Text=!File.Exists(Path.Combine(path,"MicrosoftGame.Config")) && File.Exists(Path.Combine(path,"GameData","MicrosoftGame.Config"))
                        ? Path.Combine(path,"GameData") : path;
                }
            }
        };
        Find<Button>("PickExe").Click+=delegate {
            var dialog=new Microsoft.Win32.OpenFileDialog { Title=Ui.Text("原版游戏 EXE"), Filter="EXE / ZIP / RAR / 7Z|*.exe;*.zip;*.rar;*.7z" };
            if(dialog.ShowDialog(window)==true) exe.Text=dialog.FileName;
        };
        Find<Button>("CheckGame").Click+=async delegate {
            if(checking || !CanAdvanceImport()) return;
            if(importStep==0) {
                importStep=1;
                UpdateImportStep();
                SlideImportPage(true);
                Find<UIElement>(magnet.IsChecked==true ? "MagnetLink" : "PickAssets").Focus();
            }
            else if(importStep==2) {
                if(detailChecked) LobbyStep(true);
                else await CheckDraftDetails();
            }
            else if(verifiedFiles!=null) {
                PrepareCardImport(verifiedFiles.Item1,verifiedFiles.Item2);
            } else if(NextSourcePage() is SourceStage nextPage) {
                sourceStage=nextPage;
                Find<TextBlock>("ImportStatus").Text="";
                UpdateImportStep();
                SlideImportPage(true);
            } else await CheckImportFiles();
        };
    }

    static double ImportBlend(double width,double start,double end) {
        double t=Math.Max(0,Math.Min(1,(width-start)/(end-start)));
        return t*t*t*(t*(6*t-15)+10);
    }

    double splitProgress;
    AnimationClock splitClock;

    static double ImportWidth(double p,bool opening) {
        double gap=14*ImportBlend(p,0,.20);
        if(!opening) return gap+402*ImportBlend(p,.40,1);
        double expand=ImportBlend(p,.40,.86);
        return gap+402*expand+7*(expand-ImportBlend(p,.86,1));
    }

    void AnimateImportSplit(Grid split,bool open,bool leavingLobby,Action finish) {
        var previous=splitClock;
        splitClock=null;
        previous?.Controller.Remove();
        var surface=Find<Grid>("ImportSurface");
        var context=Find<Grid>("ImportContext");
        var home=Find<Grid>("GameHome");
        var form=Find<Grid>("GameImport");
        var host=Find<Grid>("LauncherContent");
        var railSlot=Find<Grid>("ImportRailSlot");
        var railScale=Find<ScaleTransform>("ImportRailScale");
        const double lineStart=.21, lineEnd=.54;
        double start=splitProgress, end=open ? 1 : 0;
        double offset=split.Width-ImportWidth(start,open);
        double blendDistance=Math.Min(.22,Math.Abs(end-start));
        Action<double> draw=p=>{
            splitProgress=p;
            // Give the cover its full row before painting it. On the way back
            // it becomes transparent before a shrinking row can clip its shadow.
            double room=ImportBlend(p,0,.42);
            railSlot.Visibility=room>0 ? Visibility.Visible : Visibility.Collapsed;
            railSlot.Height=60*room;
            railSlot.Opacity=ImportBlend(p,.43,.72);
            double size=.72+.39*ImportBlend(p,.42,.72)-.135*ImportBlend(p,.72,.89)+.025*ImportBlend(p,.89,1);
            railScale.ScaleX=railScale.ScaleY=size*SelectedTileScale;
            double carry=blendDistance>0 ? offset*(1-ImportBlend(Math.Abs(p-start),0,blendDistance)) : 0;
            split.Width=leavingLobby ? 0 : Math.Max(0,ImportWidth(p,open)+carry);
            double inset=8*(1-ImportBlend(p,.42,.64));
            surface.Margin=new Thickness(0,0,8,inset);
            surface.Width=Math.Max(0,split.Width-8);
            surface.Height=6+Math.Max(0,host.ActualHeight-2*inset-6)*ImportBlend(p,lineStart,lineEnd);
            surface.Opacity=ImportBlend(p,.12,.23);
            context.Opacity=ImportBlend(p,.48,.80);
            home.Opacity=1-ImportBlend(p,.36,.62);
            form.Opacity=ImportBlend(p,.62,.88);
        };
        if(!SystemParameters.ClientAreaAnimation || start==end) {
            draw(end);
            finish();
            return;
        }
        // Spend half as long drawing the vertical line. Keep the other phases on their own timing.
        double OpenTime(double phase) => 800*phase-400*Math.Max(0,Math.Min(phase,lineEnd)-lineStart);
        double startTime=open ? OpenTime(start) : 0;
        double durationMs=open ? OpenTime(end)-startTime : (start-end)*470;
        var clock=new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(Math.Max(80,durationMs))).CreateClock();
        splitClock=clock;
        clock.CurrentTimeInvalidated+=delegate {
            if(splitClock!=clock || !clock.CurrentProgress.HasValue) return;
            double elapsed=clock.CurrentProgress.Value*durationMs;
            if(open) {
                double time=startTime+elapsed;
                double lineTime=Math.Max(0,Math.Min(time,OpenTime(lineEnd))-OpenTime(lineStart));
                draw((time+lineTime)/800);
            } else draw(start-elapsed/470);
        };
        clock.Completed+=delegate {
            if(splitClock!=clock) return;
            splitClock=null;
            clock.Controller.Remove();
            draw(end);
            finish();
        };
        clock.Controller.Begin();
    }

    void ResetLobbyEditor() {
        importStep=0;
        MountPreviewCard(false);
        SetImportVisible("LobbyEditor",false); SetImportVisible("SourceForm",true); SetImportVisible("ImportBack",true);
    }
    void OpenImport(bool open) {
        if(importOpen==open || checking || (open && playing)) return;
        if(open) {
            Menu(false);
            if(importStep==3) ResetLobbyEditor();
        }
        bool leavingLobby=!open && importStep==3;
        if(leavingLobby) {
            var previewSplit=Find<Grid>("ImportSplit");
            previewSplit.BeginAnimation(FrameworkElement.WidthProperty,null); previewSplit.Width=0;
        }
        importOpen=open;
        PositionGameMark(true);
        var panel=Find<Grid>("GameImport");
        var home=Find<Grid>("GameHome");
        var add=Find<Button>("AddGame");
        string label=open ? "ui.返回主页" : "ui.载入游戏本体";
        add.SetResourceReference(FrameworkElement.ToolTipProperty,label);
        add.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty,label);
        var icon=(UIElement)add.Content;
        var turn=icon.RenderTransform as RotateTransform;
        if(turn==null) { icon.RenderTransformOrigin=new Point(.5,.5); icon.RenderTransform=turn=new RotateTransform(); }
        double from=turn.Angle;
        turn.Angle=open ? 45 : 0;
        turn.BeginAnimation(RotateTransform.AngleProperty,SystemParameters.ClientAreaAnimation ?
            new DoubleAnimation(from,turn.Angle,TimeSpan.FromMilliseconds(165)){FillBehavior=FillBehavior.Stop} : null);
        var split=Find<Grid>("ImportSplit");
        ((TranslateTransform)Find<StackPanel>("ImportPageBody").RenderTransform).BeginAnimation(TranslateTransform.XProperty,null);
        split.Visibility=Visibility.Visible;
        split.IsHitTestVisible=open;
        KeyboardNavigation.SetTabNavigation(split,open ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
        var entering=open ? panel : home;
        var leaving=open ? home : panel;
        entering.Visibility=Visibility.Visible;
        entering.IsHitTestVisible=true;
        leaving.IsHitTestVisible=false;
        KeyboardNavigation.SetTabNavigation(entering,KeyboardNavigationMode.Continue);
        KeyboardNavigation.SetTabNavigation(leaving,KeyboardNavigationMode.None);
        Action finish=delegate {
            if(importOpen!=open) return;
            leaving.Visibility=Visibility.Collapsed;
            entering.Opacity=1;
            if(open) Find<Grid>("ImportRailSlot").BringIntoView();
            else {
                split.Visibility=Visibility.Collapsed;
                if(leavingLobby) ResetLobbyEditor();
                ResetSourceCheck();
            }
        };
        AnimateImportSplit(split,open,leavingLobby,finish);
        if(open) {
            importStep=0;
            Find<CheckBox>("CyberleekPreset").IsChecked=false;
            ResetSourceCheck();
            LoadBundledChoices();
            foreach(string name in new[]{"MagnetSource","LocalSource","OwnExe","BuiltinExe","IncludesExe","SupplyExe"})
                Find<RadioButton>(name).IsChecked=false;
            foreach(string name in new[]{"ImportName","AssetPath","ExePath","MagnetLink"})
                Find<TextBox>(name).Clear();
            ClearRecognition();
            UpdateImportStep();
            if(importStep==0) Find<RadioButton>("MagnetSource").Focus();
        }
        else {
            Find<Launcher.Layout.LobbyArtwork>("LobbyPreview").SetFiles(Array.Empty<string>());
            add.Focus();
        }
    }

    bool Probe(string id,string assets,string exe) {
        var si=SessionInfo(id+" --probe",assets,exe,true);
        Append("Checking game compatibility: "+exe);
        using(var p=Process.Start(si)) {
            p.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs e) { if(e.Data!=null) Append("Probe stderr: "+e.Data); };
            p.BeginErrorReadLine();
            string line;
            while((line=p.StandardOutput.ReadLine())!=null) RecordWorkerLine(line);
            p.WaitForExit();
            return p.ExitCode==0;
        }
    }

    string[] ImportCandidates(string assets,string chosen) {
        if(!Directory.Exists(assets)) return new string[0];
        try {
            var config=XElement.Load(Path.Combine(assets,"MicrosoftGame.Config"));
            return DeclaredExecutables(assets,config).Concat(new[]{chosen,BundledGameExe})
                .Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path=>ValidFiles(assets,path)).ToArray();
        } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException || e is XmlException || e is ArgumentException || e is NotSupportedException) { return new string[0]; }
    }

    async Task CheckImportFiles() {
        if(checking || playing || closed) return;
        bool magnet=Find<RadioButton>("MagnetSource").IsChecked==true;
        string assets,exe;
        string source=Find<TextBox>(magnet ? "MagnetLink" : "AssetPath").Text;
        sessionLog.Write("IMPORT", $"Check source={DiagnosticReport.Shareable(source)}; step={sourceStage}; archive={archiveInput}; extraction={ArchiveCommand(magnet)}; externalExe={SupplyExeSelected}; bundledExe={Find<RadioButton>("BuiltinExe").IsChecked==true}; exeSource={DiagnosticReport.Shareable(Find<TextBox>("ExePath").Text)}");
        var status=Find<TextBlock>("ImportStatus");
        var activity=Find<TextBlock>("CheckActivity");
        status.Text="";
        activity.Text=Ui.Text(magnet && sourceStage==SourceStage.Address ? "正在检查下载源" : "检查游戏文件与 EXE");
        var caption=Find<TextBlock>("CheckSource");
        if(SupplyExeSelected)
            caption.Text=Find<RadioButton>("BuiltinExe").IsChecked==true ? Path.GetFileName(Find<ListBox>("BuiltinList").SelectedValue as string) : ExeSourceCaption(Find<TextBox>("ExePath").Text);
        else caption.Text=magnet ? MagnetCaption(Find<TextBox>("MagnetLink").Text) : Find<TextBox>("AssetPath").Text;
        importActionFailed=false;
        checking=true;
        var indicatorTime=Task.Delay(300);
        Find<ProgressBar>("MagnetProgress").IsIndeterminate=true;
        UpdateImportStep();
        updates.Refresh();
        Find<Grid>("LaunchPill").IsEnabled=false;
        Find<StackPanel>("GameList").IsEnabled=false;
        foreach(string name in new[]{"ImportSources","AssetPath","ExePath","PickAssets","PickExe","AddGame","ImportBack","ControllerArea"}) Find<UIElement>(name).IsEnabled=false;
        try {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            if(closed) return;
            var files=await ResolveImportSource(magnet);
            if(files==null || closed) {
                sessionLog.Write("IMPORT",$"Awaiting selection; step={sourceStage}; archive={archiveInput}; firstVolume={archivePath}; volumes={volumeCount}");
                return;
            }
            assets=files.Item1; exe=files.Item2;
            activity.Text=Ui.Text("检查游戏文件与 EXE");
            if(checkedPlan==null) await Task.Run(()=>ValidateImportFiles(assets,exe));
            if(closed) return;
            verifiedFiles=Tuple.Create(assets,exe);
            sessionLog.Write("IMPORT","Check passed; assets="+assets+"; exe="+exe+"; deferred="+(checkedPlan!=null));
            status.Text=Ui.Text(checkedPlan==null ? "检查通过：游戏清单、必要资源和 EXE 均符合要求。" : "来源已检查，入库后安装");
        } catch(OperationCanceledException) {
            sessionLog.Write("IMPORT","Check cancelled.");
            status.Text=Ui.Text("下载已取消");
        } catch(Exception e) {
            importActionFailed=true;
            status.Text=Ui.Format("检查未通过：{0}",e.Message); Append("Import check failed: "+e.Message);
        } finally {
            await indicatorTime;
            checking=false;
            Find<ProgressBar>("MagnetProgress").IsIndeterminate=false;
            updates.Refresh();
            Find<Grid>("LaunchPill").IsEnabled=true;
            Find<StackPanel>("GameList").IsEnabled=true;
            foreach(string name in new[]{"ImportSources","AssetPath","ExePath","PickAssets","PickExe","AddGame","ImportBack","ControllerArea"}) Find<UIElement>(name).IsEnabled=true;
            UpdateImportStep();
            if(!closed) Find<UIElement>(importStep==2 ? "ImportName" : "CheckGame").Focus();
        }
    }
}
