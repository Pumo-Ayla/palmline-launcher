using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Xml.Linq;

internal sealed partial class DesktopLauncher {
    sealed class InstallPlan {
        public string BodyMode="",BodySource="",ExeMode="",ExeSource="",Destination="",BodyOutput="",ExeOutput="";
        public bool Download => BodyMode.StartsWith("--fetch") || ExeMode=="--http-exe" || ExeMode=="--fetch-exe";
        public XElement Xml() {
            return new XElement("install",new XAttribute("body",BodyMode),new XAttribute("source",BodySource),
                new XAttribute("exeMode",ExeMode),new XAttribute("exeSource",ExeSource),new XAttribute("to",Destination),
                new XAttribute("bodyOutput",BodyOutput),new XAttribute("exeOutput",ExeOutput));
        }
        public static InstallPlan Read(XElement node) {
            if(node==null) return null;
            string Get(string key)=>(string)node.Attribute(key) ?? "";
            var plan=new InstallPlan { BodyMode=Get("body"),BodySource=Get("source"),ExeMode=Get("exeMode"),ExeSource=Get("exeSource"),
                Destination=Get("to"),BodyOutput=Get("bodyOutput"),ExeOutput=Get("exeOutput") };
            if(!new[]{"","--fetch-game","--fetch-archive-keep","--fetch-archive-release","--unpack-game","--unpack-release"}.Contains(plan.BodyMode) ||
                !new[]{"","--unpack-exe","--http-exe","--fetch-exe"}.Contains(plan.ExeMode)) return null;
            return plan;
        }
        public void KeepMetadata(string folder) {
            if(BodyMode.StartsWith("--fetch")) File.Copy(BodySource,Path.Combine(folder,"game.torrent"));
            if(ExeMode=="--fetch-exe") File.Copy(ExeSource,Path.Combine(folder,"exe.torrent"));
        }
        public void UseMetadata(string folder) {
            if(BodyMode.StartsWith("--fetch")) BodySource=Path.Combine(folder,"game.torrent");
            if(ExeMode=="--fetch-exe") ExeSource=Path.Combine(folder,"exe.torrent");
        }
    }
    InstallPlan checkedPlan;
    GameInstance installing;
    bool extracting;
    string installMessage="";

    async Task<InstallPlan> PlanInstallation(bool magnet) {
        var plan=new InstallPlan();
        if(resolvedAssets==null) {
            plan.BodyMode=archiveInput ? ArchiveCommand(magnet) : "--fetch-game";
            if(plan.BodyMode==null) throw new InvalidDataException(Ui.Text("请选择解压目录"));
            plan.BodySource=magnet ? gameMetadata : archivePath;
        }
        if(SupplyExeSelected) {
            string source=Find<RadioButton>("BuiltinExe").IsChecked==true ? Find<ListBox>("BuiltinList").SelectedValue as string : Find<TextBox>("ExePath").Text.Trim();
            if(File.Exists(source)) { plan.ExeSource=source; if(IsArchive(source)) plan.ExeMode="--unpack-exe"; }
            else if(magnet && ValidMagnet(source)) {
                plan.ExeMode="--fetch-exe"; plan.ExeSource=await TransferOperation("--inspect-exe",source,MetadataDirectory(),"正在检查下载源");
            } else if(magnet && Uri.TryCreate(source,UriKind.Absolute,out var uri) && (uri.Scheme=="http" || uri.Scheme=="https")) {
                plan.ExeMode="--http-exe"; plan.ExeSource=source;
            } else throw new InvalidDataException(Ui.Text("请选择有效的 EXE、压缩包或链接。"));
        }
        plan.Destination=TransferDestination();
        return plan;
    }
    void SetupInstallActions() {
        Find<Button>("InstallGame").Click+=async delegate { await InstallSelected(); };
        Find<Button>("InstallOptions").Click+=delegate {
            if(activeGame?.Pending==null) return;
            var options=new ContextMenu();
            var folder=new MenuItem { Header=Ui.Text("所在文件夹") };
            string path=activeGame.Pending.Destination;
            folder.IsEnabled=Directory.Exists(path);
            folder.Click+=delegate { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path){UseShellExecute=true}); };
            options.Items.Add(folder);
            if(installing!=null) { var cancel=new MenuItem { Header=Ui.Text("取消"),IsEnabled=transfer!=null }; cancel.Click+=delegate { CancelTransfer(); }; options.Items.Add(cancel); }
            options.PlacementTarget=Find<Button>("InstallOptions"); options.IsOpen=true;
        };
    }
    void RefreshInstallAction() {
        var game=activeGame;
        bool pending=game?.Pending!=null && !updates.Required && !BrowsingDuringPlay;
        SetImportVisible("InstallActions",pending);
        if(!pending) return;
        bool busy=installing==game;
        var action=Find<Button>("InstallGame");
        action.IsEnabled=!busy && !checking;
        string label=busy ? (extracting ? "正在解压游戏本体" : "正在下载") : game.Pending.Download ? "开始下载" : "解压安装";
        Find<TextBlock>("InstallText").Text=Ui.Text(label);
        System.Windows.Automation.AutomationProperties.SetName(action,Ui.Text(label));
        SetImportVisible("InstallBubble",!string.IsNullOrEmpty(installMessage));
        Find<TextBlock>("InstallProgress").Text=installMessage;
        var arrow=Find<System.Windows.Shapes.Path>("InstallArrow"); var spinner=Find<System.Windows.Shapes.Path>("InstallSpinner");
        arrow.Visibility=busy && extracting ? Visibility.Collapsed : Visibility.Visible;
        spinner.Visibility=busy && extracting ? Visibility.Visible : Visibility.Collapsed;
        var down=(TranslateTransform)arrow.RenderTransform; var turn=(RotateTransform)spinner.RenderTransform;
        if(!busy || extracting) down.BeginAnimation(TranslateTransform.YProperty,null);
        else if(!down.HasAnimatedProperties && SystemParameters.ClientAreaAnimation) down.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-10,11,TimeSpan.FromMilliseconds(850)){RepeatBehavior=RepeatBehavior.Forever});
        if(!busy || !extracting) turn.BeginAnimation(RotateTransform.AngleProperty,null);
        else if(!turn.HasAnimatedProperties && SystemParameters.ClientAreaAnimation) turn.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0,360,TimeSpan.FromMilliseconds(760)){RepeatBehavior=RepeatBehavior.Forever});
    }
    void InstallProgressUpdate(string label,bool unpack,double? percent=null) {
        if(installing==null || closed) return;
        extracting=unpack;
        installMessage=Ui.Text(label)+(percent.HasValue ? " · "+percent.Value.ToString("0.#")+"%" : "…");
        RefreshInstallAction();
    }
    async Task<string> InstallTransfer(string mode,string source,string destination) {
        bool unpack=mode.StartsWith("--unpack");
        string stage=unpack ? "正在解压游戏本体" : "正在下载";
        InstallProgressUpdate(stage,unpack);
        return await TransferOperation(mode,source,destination,stage);
    }
    async Task InstallSelected() {
        var game=activeGame;
        if(game?.Pending==null || installing!=null || checking || playing || updates.Required || updates.Installing) return;
        var plan=game.Pending;
        installing=game; checking=true;
        SetLaunchState(LaunchState.Idle);
        try {
            string assets=game.Assets,exe=plan.ExeSource;
            if(plan.ExeMode!="") {
                if(!Directory.Exists(plan.ExeOutput)) {
                    plan.ExeOutput=await InstallTransfer(plan.ExeMode,plan.ExeSource,plan.Destination);
                    WriteLibrary(games,game.Id);
                }
                exe=Directory.EnumerateFiles(plan.ExeOutput,"game_scarlett_master.exe",SearchOption.AllDirectories).Single();
            }
            if(plan.BodyMode!="") {
                if(!Directory.Exists(plan.BodyOutput)) {
                    plan.BodyOutput=await InstallTransfer(plan.BodyMode,plan.BodySource,plan.Destination);
                    WriteLibrary(games,game.Id);
                }
                assets=await Task.Run(()=>CheckAssets(Path.GetDirectoryName(Directory.EnumerateFiles(plan.BodyOutput,"MicrosoftGame.Config",SearchOption.AllDirectories).Single())));
            }
            if(string.IsNullOrWhiteSpace(exe)) exe=await Task.Run(()=>IncludedExe(assets));
            if(string.IsNullOrEmpty(exe)) throw new InvalidDataException(Ui.Text("本体中未找到可用的游戏 EXE，请选择文件或内置版本。"));
            await Task.Run(()=>ValidateImportFiles(assets,exe));
            if(closed) return;
            var ready=new GameInstance { Id=game.Id,Name=game.Name,Assets=assets,Exe=exe,GtaVi=IsGtaVi(assets),
                ArtworkId=game.ArtworkId,Presentation=game.Presentation,LogoSize=game.LogoSize,LogoX=game.LogoX,LogoY=game.LogoY };
            if(game.LauncherProfile==OptimizedProfile && !ready.GtaVi)
                throw new InvalidDataException(Ui.Text("独立优化配置需要 GTA VI 游戏本体。"));
            ready.LauncherProfile=game.LauncherProfile;
            ready.Compatible=ready.LauncherProfile!=null ? ProfileEngines(ready.LauncherProfile) : EnginesForGame(ready.GtaVi,catalog.Keys.ToArray());
            ready.Emulator=ready.Compatible.Contains(game.Emulator) ? game.Emulator : ready.Compatible.First();
            WriteLibrary(games.Select(g=>g==game ? ready : g),game.Id);
            games[games.IndexOf(game)]=ready; RefreshGameTile(ready); ActivateGame(ready,false); installMessage="";
        } catch(Exception ex) {
            installMessage=ex is OperationCanceledException ? Ui.Text("下载已取消") : ex.Message; Append(installMessage);
        } finally {
            checking=false; installing=null;
            if(!closed) { SetLaunchState(LaunchState.Idle); DisplayGame(); if(activeGame?.Pending==null) Schedule(); }
        }
    }
}
