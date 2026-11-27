using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Microsoft.Win32;

internal sealed partial class DesktopLauncher
{
    LocalSaveLibrary saveLibrary;
    string saveInstance, saveError;
    bool saveBusy, saveOpen;
    bool SaveCardOpen { get { return saveOpen; } }
    int saveCardRevision;

    static bool SaveFileError(Exception e) {
        return e is IOException || e is InvalidDataException || e is UnauthorizedAccessException ||
            e is ArgumentException || e is NotSupportedException || e is Win32Exception || e is System.Xml.XmlException;
    }
    void OpenGameSaveLibrary() {
        bool xcl=selected=="xcl";
        if(saveLibrary!=null && activeGame.Id==saveInstance && (saveLibrary is XclSaveLibrary)==xcl) return;
        saveLibrary=null;
        saveInstance=activeGame.Id;
        try {
            string manifest=Path.Combine(activeGame.Assets,"MicrosoftGame.Config");
            var config=xcl && !File.Exists(manifest) ? null : XElement.Load(manifest);
            string title=((string)config?.Elements().FirstOrDefault(e=>e.Name.LocalName=="TitleId") ?? "").Trim().ToUpperInvariant();
            if(xcl) {
                if(!Regex.IsMatch(title,@"\A[0-9A-F]{8}\z")) {
                    string identity=(string)config?.Elements().FirstOrDefault(e=>e.Name.LocalName=="Identity")?.Attribute("Name")
                        ?? Path.GetFileName(activeGame.Exe);
                    using(var hash=System.Security.Cryptography.SHA256.Create())
                        title="XCL-"+BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity))).Replace("-","");
                }
                saveLibrary=new XclSaveLibrary(Path.Combine(root,"GameSaves",saveInstance),title);
            } else saveLibrary=new LocalSaveLibrary(root,saveInstance,title);
            saveError=null;
        } catch(Exception e) when(SaveFileError(e)) { saveError=e.Message; Append(e.Message); }
    }
    static LocalSaveLibrary.Story LatestStory(LocalSaveLibrary library,string slot) {
        if(library==null) return null;
        try { return library.Stories(slot).FirstOrDefault(); }
        catch(Exception e) when(SaveFileError(e)) { return null; }
    }
    static string StoryChapterTitle(LocalSaveLibrary.Story story) {
        string name=Regex.Replace(story.Title,@"^\s*[(（](?:自动保存|自動保存|自動儲存|Autosave|Auto Save|オートセーブ|자동\s*저장)[)）]\s*","",RegexOptions.IgnoreCase).Trim();
        return name.Length>0 ? Ui.ChapterTitle(name) : "—";
    }
    static string StoryProgressSuffix(LocalSaveLibrary.Story story) {
        return story?.Progress is float percent ? Ui.Format(" · {0:0.##}%",percent) : "";
    }
    static string StoryTitle(LocalSaveLibrary.Story story) {
        return StoryChapterTitle(story)+StoryProgressSuffix(story);
    }
    void UpdateSaveMenuLabel() {
        Find<TextBlock>("LocalSavesLabel").SetResourceReference(TextBlock.TextProperty,
            selected=="xcl" ? "ui.XCL存档" : "ui.本机存档");
    }
    void AttachSaveCard() {
        Find<Button>("LocalSaves").Click+=delegate { OpenSaves(); };
        Find<Button>("CloseSaves").Click+=delegate { CloseSaves(); };
        Find<Border>("SaveScrim").MouseLeftButtonDown+=delegate { CloseSaves(); };
        window.PreviewMouseDown+=delegate(object sender,System.Windows.Input.MouseButtonEventArgs e) {
            if(saveOpen && Find<Grid>("Rail").IsMouseOver) { CloseSaves(); e.Handled=true; }
        };
        Find<Button>("SaveFolder").Click+=delegate {
            try {
                string folder=saveLibrary.Saves(saveLibrary.Selected);
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute=true });
            } catch(Exception e) when(SaveFileError(e)) { SaveNotice(e.Message); }
        };
        Find<Button>("ImportSave").Click+=delegate {
            var dialog=new OpenFileDialog { Title=Ui.Text("导入存档"),Filter="ZIP (*.zip)|*.zip" };
            if(dialog.ShowDialog(window)!=true) return;
            var library=saveLibrary;
            SaveOperation(()=>library.Import(dialog.FileName),Ui.Text("存档已导入"));
        };
        Find<Button>("ExportSave").Click+=delegate {
            var dialog=new SaveFileDialog { Title=Ui.Text("导出存档"),Filter="ZIP (*.zip)|*.zip",DefaultExt=".zip",
                FileName=saveLibrary.TitleId+"-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip" };
            if(dialog.ShowDialog(window)!=true) return;
            var library=saveLibrary;
            string slot=library.Selected;
            SaveOperation(()=>{ library.Export(slot,dialog.FileName); return null; },Ui.Text("存档已导出"));
        };
    }
    void SaveNotice(string message) {
        var notice=Find<TextBlock>("SaveNotice");
        notice.Text=message ?? "";
        notice.Visibility=notice.Text.Length==0 ? Visibility.Collapsed : Visibility.Visible;
    }
    void OpenSaves() {
        if(activeGame==null || saveBusy) return;
        OpenGameSaveLibrary();
        Menu(false);
        saveOpen=true;
        ++saveCardRevision;
        var overlay=Find<Grid>("SaveOverlay");
        overlay.Visibility=Visibility.Visible;
        overlay.IsHitTestVisible=true;
        ControllerMotion.Reveal(Find<Border>("SaveCard"),.96,1.012,8,235,.45);
        SaveNotice(saveError ?? (playing ? Ui.Text("退出游戏后可切换或传输存档") : null));
        ReloadSaveChoices();
        Find<Button>("CloseSaves").Focus();
    }
    void CloseSaves() {
        if(saveBusy || !saveOpen) return;
        saveOpen=false;
        int revision=++saveCardRevision;
        var overlay=Find<Grid>("SaveOverlay");
        overlay.IsHitTestVisible=false;
        Find<Button>("More").Focus();
        ControllerMotion.Dismiss(Find<Border>("SaveCard"),.96,130,0,delegate {
            if(!saveOpen && revision==saveCardRevision) overlay.Visibility=Visibility.Collapsed;
        });
    }
    async void SaveOperation(Func<string> operation,string success) {
        if(saveBusy || playing || saveLibrary==null) return;
        var library=saveLibrary;
        saveBusy=true;
        var card=Find<Border>("SaveCard");
        card.IsEnabled=false;
        SaveNotice(null);
        try {
            string chosen=await Task.Run(operation);
            if(chosen!=null) library.Select(chosen);
            if(closed || library!=saveLibrary) return;
            SaveNotice(success);
        } catch(Exception e) when(SaveFileError(e)) { if(!closed) SaveNotice(e.Message); }
        finally {
            saveBusy=false;
            if(!closed) { card.IsEnabled=true; ReloadSaveChoices(); RefreshHistory(); }
        }
    }
    void ReloadSaveChoices() {
        var list=Find<StackPanel>("SaveChoices");
        list.Children.Clear();
        Find<Button>("SaveFolder").IsEnabled=saveLibrary!=null;
        Find<Button>("ImportSave").IsEnabled=saveLibrary!=null && !playing;
        var export=Find<Button>("ExportSave");
        export.IsEnabled=false;
        if(saveLibrary==null) return;
        try {
            foreach(string slot in saveLibrary.Slots()) {
                AddSaveChoice(list,slot,saveLibrary.Name(slot),LatestStory(saveLibrary,slot));
            }
            export.IsEnabled=!playing && saveLibrary.CanExport(saveLibrary.Selected);
        } catch(Exception e) when(SaveFileError(e)) { SaveNotice(e.Message); }
    }
    void AddSaveChoice(StackPanel list,string slot,string label,LocalSaveLibrary.Story story) {
        var body=new StackPanel { MaxWidth=218 };
        var heading=new TextBlock { Text=story==null ? Ui.Text("暂无存档") : StoryTitle(story),Foreground=window.Foreground,
            FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis };
        body.Children.Add(heading);
        string detail=label;
        if(story!=null && story.Stamp>0 && story.Stamp<253402214400L)
            detail+=" · "+DateTimeOffset.FromUnixTimeSeconds(story.Stamp).ToLocalTime().ToString("g",System.Globalization.CultureInfo.GetCultureInfo(Ui.Language));
        body.Children.Add(new TextBlock { Text=detail,Style=(Style)window.FindResource("cardCaption"),TextTrimming=TextTrimming.CharacterEllipsis });
        var row=new RadioButton { Style=(Style)window.FindResource("emulatorOption"),Content=body,IsEnabled=!playing,
            Background=(Brush)window.FindResource("saveIcon"),IsChecked=slot==saveLibrary.Selected,ToolTip=detail };
        row.Click+=delegate {
            if(playing || saveBusy) return;
            try { saveLibrary.Select(slot); RefreshHistory(); SaveNotice(null); }
            catch(Exception e) when(SaveFileError(e)) { SaveNotice(e.Message); }
            ReloadSaveChoices();
        };
        list.Children.Add(row);
    }
}
