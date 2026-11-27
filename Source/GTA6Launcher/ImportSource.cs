using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Xml.Linq;
using Forms = System.Windows.Forms;

internal sealed partial class DesktopLauncher
{
    enum SourceStage { Address, Contents, External, Archive }
    SourceStage sourceStage;
    string metadataFolder, gameMetadata, resolvedAssets;
    string volumeCount;
    bool archiveInput;
    string archivePath;
    Tuple<string,string> verifiedFiles;
    bool SupplyExeSelected => Find<RadioButton>("SupplyExe").IsChecked==true;

    SourceStage? NextSourcePage() {
        if(sourceStage==SourceStage.Contents && SupplyExeSelected) return SourceStage.External;
        if(archiveInput && resolvedAssets==null && (sourceStage==SourceStage.Contents || sourceStage==SourceStage.External))
            return SourceStage.Archive;
        return null;
    }

    void InvalidateFileCheck() {
        checkedPlan=null;
        if(verifiedFiles==null && !importActionFailed) return;
        importActionFailed=false;
        verifiedFiles=null;
        Find<TextBlock>("ImportStatus").Text="";
    }

    string ArchiveCommand(bool magnet=false) {
        if(Find<RadioButton>("KeepArchive").IsChecked==true) return magnet ? "--fetch-archive-keep" : "--unpack-game";
        return Find<RadioButton>("DeleteArchive").IsChecked==true && Find<CheckBox>("ReleaseVolumes").IsChecked==true
            ? (magnet ? "--fetch-archive-release" : "--unpack-release") : null;
    }

    void ClearImportMetadata() {
        if(metadataFolder==null) return;
        try { Directory.Delete(metadataFolder,true); }
        catch(IOException) { }
        catch(UnauthorizedAccessException) { }
        metadataFolder=null;
    }

    void ResetSourceCheck() {
        InvalidateFileCheck();
        ClearImportMetadata();
        archivePath=null;
        archiveDestinationPage=false;
        Find<TextBox>("ArchiveOutputPath").Clear();
        sourceStage=SourceStage.Address;
        resolvedAssets=gameMetadata=volumeCount=null;
        archiveInput=false;
        Find<CheckBox>("ReleaseVolumes").IsChecked=false;
        foreach(string name in new[]{"IncludesExe","SupplyExe","OwnExe","BuiltinExe","KeepArchive","DeleteArchive"})
            Find<RadioButton>(name).IsChecked=false;
        Find<ListBox>("BuiltinList").SelectedIndex=-1;
        Find<TextBox>("ExePath").Clear();
        Find<TextBlock>("ImportStatus").Text="";
    }

    void LoadBundledChoices() {
        var choices=new List<KeyValuePair<string,string>>();
        if(File.Exists(BundledGameExe)) choices.Add(new KeyValuePair<string,string>(BundledVersionName,BundledGameExe));
        Find<ListBox>("BuiltinList").ItemsSource=choices;
        Find<RadioButton>("BuiltinExe").IsEnabled=choices.Count!=0;
    }

    static bool IsArchive(string path) {
        return new[]{".rar",".7z",".zip"}.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase);
    }

    static string[] ArchiveCandidates(string folder) {
        if(!Directory.Exists(folder) || File.Exists(Path.Combine(folder,"MicrosoftGame.Config")) ||
            File.Exists(Path.Combine(folder,"GameData","MicrosoftGame.Config"))) return Array.Empty<string>();
        var candidates=new List<string>();
        bool sawVolume=false;
        foreach(var path in Directory.EnumerateFiles(folder)) {
            var match=Regex.Match(Path.GetFileName(path),@"\.part(\d+)\.(rar|exe)$",RegexOptions.IgnoreCase);
            if(match.Success) {
                sawVolume=true;
                if(match.Groups[1].Value.TrimStart('0')=="1") candidates.Add(path);
            } else if(IsArchive(path)) candidates.Add(path);
        }
        if(sawVolume && candidates.Count==0) throw new InvalidDataException(Ui.Text("缺少分卷主文件（part01.exe 或 part01.rar）。"));
        return candidates.ToArray();
    }

    static string CheckAssets(string folder) {
        if(!Directory.Exists(folder)) throw new DirectoryNotFoundException(Ui.Format("无法访问游戏目录：{0}",folder));
        if(!File.Exists(Path.Combine(folder,"MicrosoftGame.Config")) && Directory.Exists(Path.Combine(folder,"GameData")))
            folder=Path.Combine(folder,"GameData");
        var manifest=XElement.Load(Path.Combine(folder,"MicrosoftGame.Config"));
        bool gta=manifest.Elements().Any(e=>e.Name.LocalName=="TitleId" && string.Equals(e.Value,"7952987A",StringComparison.OrdinalIgnoreCase));
        CheckAssetArchives(folder,gta);
        if(!DeclaredExecutables(folder,manifest).Any())
            throw new InvalidDataException(Ui.Text("游戏清单未声明可用的 EXE。"));
        return Path.GetFullPath(folder);
    }

    static string IncludedExe(string folder) {
        return DeclaredExecutables(folder,XElement.Load(Path.Combine(folder,"MicrosoftGame.Config")))
            .FirstOrDefault(path=>ValidFiles(folder,path));
    }

    string MetadataDirectory() {
        if(metadataFolder==null) {
            metadataFolder=Path.Combine(Path.GetTempPath(),"Palmline-import-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(metadataFolder);
        }
        return metadataFolder;
    }

    string PickOutputFolder(string previous) {
        using(var picker=new Forms.FolderBrowserDialog { Description=Ui.Text("选择输出目录"),
            SelectedPath=Directory.Exists(previous) ? previous : "" }) {
            return picker.ShowDialog()==Forms.DialogResult.OK && !closed ? picker.SelectedPath : null;
        }
    }

    string TransferDestination() {
        if(!archiveInput) return PickOutputFolder(null) ?? throw new OperationCanceledException();
        string destination=Find<TextBox>("ArchiveOutputPath").Text;
        if(Directory.Exists(destination)) return destination;
        sourceStage=SourceStage.Archive;
        archiveDestinationPage=true;
        throw new DirectoryNotFoundException(Ui.Text("请选择解压目录"));
    }

    async Task<Tuple<string,string>> ResolveImportSource(bool magnet) {
        string input=Find<TextBox>(magnet ? "MagnetLink" : "AssetPath").Text.Trim();
        if(sourceStage==SourceStage.Address) {
            if(magnet) {
                if(!ValidMagnet(input)) throw new InvalidDataException(Ui.Text("本体磁力链接格式无效。"));
                gameMetadata=await TransferOperation("--inspect-game",input,MetadataDirectory(),"正在检查下载源");
                archiveInput=File.Exists(gameMetadata+".archive");
            } else {
                var candidates=await Task.Run(()=>ArchiveCandidates(input));
                if(candidates.Length>1) {
                    var picker=new Microsoft.Win32.OpenFileDialog { Title=Ui.Text("选择本体压缩包"), InitialDirectory=input,
                        Filter="ZIP / RAR / 7Z / SFX|*.zip;*.rar;*.7z;*.exe" };
                    if(picker.ShowDialog(window)!=true) throw new OperationCanceledException();
                    archivePath=candidates.SingleOrDefault(p=>p.Equals(picker.FileName,StringComparison.OrdinalIgnoreCase));
                    if(archivePath==null) throw new InvalidDataException(Ui.Text("请选择当前目录中的压缩包主文件。"));
                } else archivePath=candidates.SingleOrDefault();
                if(archivePath!=null) {
                    volumeCount=await TransferOperation("--check-archive",archivePath,MetadataDirectory(),"检查压缩包");
                    archiveInput=true;
                } else resolvedAssets=await Task.Run(()=>CheckAssets(input));
            }
            if(archiveInput) {
                Find<TextBlock>("ArchiveDetails").Text=magnet
                    ? File.ReadAllText(gameMetadata+".archive")+" · "+Ui.Text("源归档将先下载到解压目录，再按所选方式解压。")
                    : Path.GetFileName(archivePath)+" · "+Ui.Format("本次原包共 {0} 卷。",volumeCount);
                Find<TextBlock>("ImportStatus").Text=Ui.Text("检测到本体压缩包");
            } else if(magnet) {
                Find<TextBlock>("ImportStatus").Text=Ui.Text("检测到散装游戏本体。");
            } else Find<TextBlock>("ImportStatus").Text=Ui.Text("已检查本体目录，请选择 EXE 来源。");
            sourceStage=SourceStage.Contents;
            return null;
        }

        if(resolvedAssets==null || (SupplyExeSelected && !Find<RadioButton>("BuiltinExe").IsChecked.GetValueOrDefault() &&
            (!File.Exists(Find<TextBox>("ExePath").Text.Trim()) || IsArchive(Find<TextBox>("ExePath").Text.Trim())))) {
            checkedPlan=await PlanInstallation(magnet);
            return Tuple.Create(resolvedAssets ?? "",checkedPlan.ExeMode=="" ? checkedPlan.ExeSource : "");
        }
        string exe=SupplyExeSelected ? (Find<RadioButton>("BuiltinExe").IsChecked==true ?
            Find<ListBox>("BuiltinList").SelectedValue as string : Find<TextBox>("ExePath").Text.Trim()) :
            await Task.Run(()=>IncludedExe(resolvedAssets));
        if(exe==null) {
            sourceStage=SourceStage.Contents;
            Find<RadioButton>("IncludesExe").IsChecked=false;
            Find<TextBlock>("ImportStatus").Text=Ui.Text("本体中未找到可用的游戏 EXE，请选择文件或内置版本。");
            return null;
        }
        return Tuple.Create(resolvedAssets,exe);
    }
}
