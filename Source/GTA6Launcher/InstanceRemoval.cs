using System;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32.SafeHandles;

internal sealed partial class DesktopLauncher
{
    TaskCompletionSource<bool> removalReply;
    IInputElement removalFocus;

    void SetupRemovalDialog() {
        Find<Button>("CancelRemoval").Click+=delegate { CloseRemovalDialog(false); };
        Find<Button>("ConfirmRemoval").Click+=delegate { CloseRemovalDialog(true); };
        window.Closed+=delegate { CloseRemovalDialog(false); };
    }

    Task<bool> ShowRemovalDialog(string title,string message,string folder,string action) {
        if(removalReply!=null) return Task.FromResult(false);
        var reply=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        removalReply=reply;
        removalFocus=Keyboard.FocusedElement;
        Find<TextBlock>("RemovalTitle").Text=title;
        Find<TextBlock>("RemovalMessage").Text=message;
        Find<TextBlock>("RemovalPath").Text=folder;
        SetImportVisible("RemovalPathBox",folder!=null);
        SetImportVisible("CancelRemoval",action!=null);
        var confirm=Find<Button>("ConfirmRemoval");
        confirm.Content=Ui.Text(action ?? "关闭");
        confirm.Background=new SolidColorBrush(folder!=null ? Color.FromRgb(144,65,76) : Color.FromRgb(87,72,102));
        Find<Grid>("LauncherContent").IsEnabled=false;
        SetImportVisible("RemovalOverlay",true);
        ControllerMotion.Reveal(Find<Border>("RemovalCard"),.97,1.008,0,195,.75);
        (action==null ? confirm : Find<Button>("CancelRemoval")).Focus();
        return reply.Task;
    }

    void CloseRemovalDialog(bool confirmed) {
        var reply=removalReply;
        if(reply==null) return;
        removalReply=null;
        SetImportVisible("RemovalOverlay",false);
        Find<Grid>("LauncherContent").IsEnabled=true;
        if(removalFocus is UIElement previous && previous.IsVisible && previous.IsEnabled) previous.Focus();
        removalFocus=null;
        reply.TrySetResult(confirmed);
    }

    void AttachRemovalMenu(RadioButton tile,GameInstance game) {
        var menu=new ContextMenu { Style=(Style)window.FindResource("profileMenu") };
        var keep=new MenuItem();
        var uninstall=new MenuItem();
        keep.SetResourceReference(HeaderedItemsControl.HeaderProperty,"ui.仅移除启动器实例");
        uninstall.SetResourceReference(HeaderedItemsControl.HeaderProperty,"ui.移除实例并删除游戏文件");
        menu.Items.Add(keep);
        menu.Items.Add(uninstall);
        tile.ContextMenu=menu;
        ContextMenuService.SetShowOnDisabled(tile,true);
        ToolTipService.SetShowOnDisabled(uninstall,true);
        menu.Opened+=delegate {
            keep.IsEnabled=CanRemoveInstance(game);
            var path=RemovableGameFolder(game,out var reason);
            uninstall.IsEnabled=keep.IsEnabled && path!=null;
            uninstall.ToolTip=path ?? reason;
        };
        keep.Click+=async delegate { await RequestInstanceRemoval(game,false); };
        uninstall.Click+=async delegate { await RequestInstanceRemoval(game,true); };
    }

    bool CanRemoveInstance(GameInstance game) {
        return games.Contains(game) && !checking && installing!=game && !(game==sessionGame && playing);
    }

    static bool InsideGamePath(string path,string directory) {
        if(string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;
        string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string parent=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        return full.Equals(parent,StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }

    // Resolve directory junctions and short-name aliases before comparing installations.
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFile(string path,uint access,FileShare share,IntPtr security,
        FileMode mode,uint flags,IntPtr template);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder buffer,uint length,uint flags);
    static string InstalledPath(string path) {
        if(string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return null;
        if(!File.Exists(path) && !Directory.Exists(path)) return Path.GetFullPath(path);
        using(var handle=CreateFile(path,0,FileShare.ReadWrite|FileShare.Delete,IntPtr.Zero,FileMode.Open,0x02000000,IntPtr.Zero)) {
            if(handle.IsInvalid) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            var buffer=new StringBuilder(32768);
            uint length=GetFinalPathNameByHandle(handle,buffer,(uint)buffer.Capacity,0);
            if(length==0 || length>=buffer.Capacity) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            string full=buffer.ToString();
            if(full.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase)) return @"\\"+full.Substring(8);
            return full.StartsWith(@"\\?\",StringComparison.Ordinal) ? full.Substring(4) : full;
        }
    }

    string RemovableGameFolder(GameInstance game,out string reason) {
        reason=Ui.Text("没有可单独删除的游戏本体目录。");
        if(string.IsNullOrWhiteSpace(game.Assets)) return null;
        try {
            var folder=new DirectoryInfo(game.Assets);
            if(!folder.Exists || folder.Parent==null || !File.Exists(Path.Combine(folder.FullName,"MicrosoftGame.Config"))) return null;
            reason=Ui.Text("此目录包含启动器、用户数据或系统文件。");
            for(var dir=folder;dir!=null;dir=dir.Parent)
                if((dir.Attributes & FileAttributes.ReparsePoint)!=0) return null;
            string target=InstalledPath(folder.FullName);
            string launcher=InstalledPath(root);
            if(InsideGamePath(launcher,target)) return null;
            if(InsideGamePath(target,Environment.GetFolderPath(Environment.SpecialFolder.Windows))) return null;
            foreach(string child in new[]{"OriginalGame","Community","GameSaves","XCL-VIctory-Lab","Runtime","Input","GTA6Launcher"})
                if(InsideGamePath(target,Path.Combine(launcher,child))) return null;
            foreach(Environment.SpecialFolder special in Enum.GetValues(typeof(Environment.SpecialFolder))) {
                string path=Environment.GetFolderPath(special);
                if(path.Length>0 && InsideGamePath(path,target)) return null;
            }
            reason=Ui.Text("其他实例正在使用此目录中的游戏文件。");
            foreach(var other in games.Where(g=>g!=game)) {
                string assets=InstalledPath(other.Assets);
                if(assets!=null && (InsideGamePath(target,assets) || InsideGamePath(assets,target))) return null;
                foreach(string path in new[]{other.Exe,other.Pending?.BodySource,other.Pending?.ExeSource,
                    other.Pending?.BodyOutput,other.Pending?.ExeOutput})
                    if(InsideGamePath(InstalledPath(path),target)) return null;
            }
            return target;
        } catch(Exception e) when(IsCardFileError(e)) { reason=e.Message; return null; }
    }

    async Task RequestInstanceRemoval(GameInstance game,bool deleteFiles) {
        if(!CanRemoveInstance(game)) return;
        string title=Ui.Text(deleteFiles ? "移除实例并删除游戏文件" : "仅移除启动器实例");
        string folder=null;
        string message=Ui.Format("从游戏库移除「{0}」？游戏文件和存档将保留。",game.Name);
        if(deleteFiles) {
            folder=RemovableGameFolder(game,out var reason);
            if(folder==null) { await ShowRemovalDialog(title,reason,null,null); return; }
            message=Ui.Format("移除「{0}」并删除以下目录及其全部内容？目录外的 EXE、压缩包和启动器存档将保留。",game.Name);
        }
        if(!await ShowRemovalDialog(title,message,folder,deleteFiles ? "移除并删除" : "移除实例")) return;
        if(closed || !CanRemoveInstance(game)) return;
        bool active=activeGame==game;
        if(importOpen && importDraft?.Id==game.Id) OpenImport(false);
        bool stopped=worker!=null && sessionGame==game;
        string error=null;
        try {
            if(stopped) {
                checking=true; window.IsEnabled=false;
                restart=false; launchAfterWarmup=false;
                cancelWarmup?.Set(); CloseInput();
                for(int i=0;worker!=null && i<120;i++) await Task.Delay(50);
                if(worker!=null) throw new IOException(Ui.Text("游戏会话仍在关闭，请稍后重试。"));
            }
            if(closed) return;
            UnregisterInstance(game,folder);
        } catch(OperationCanceledException) { }
        catch(Exception e) when(IsCardFileError(e)) { Append(e.Message); error=e.Message; }
        finally {
            if(stopped) { window.IsEnabled=true; checking=false; }
            if(!closed && (active || stopped)) {
                if(active) DisplayGame(true);
                if(activeGame!=null && (activeGame!=game || stopped)) Schedule();
            }
        }
        if(error!=null && !closed) await ShowRemovalDialog(title,error,null,null);
    }

    void UnregisterInstance(GameInstance game,string deleteFolder) {
        int index=games.IndexOf(game);
        var remaining=games.Where(g=>g!=game).ToList();
        var next=activeGame==game ? remaining.Skip(index).Concat(remaining.Take(index)).FirstOrDefault() : activeGame;
        if(remaining.Count==0) File.WriteAllText(ManualImportMarker,"");
        if(deleteFolder!=null && !string.Equals(RemovableGameFolder(game,out var reason),deleteFolder,StringComparison.OrdinalIgnoreCase))
            throw new IOException(reason);
        WriteLibrary(remaining,next?.Id);
        if(deleteFolder!=null) {
            try { FileSystem.DeleteDirectory(deleteFolder,UIOption.OnlyErrorDialogs,RecycleOption.SendToRecycleBin,UICancelOption.ThrowException); }
            catch { WriteLibrary(games,activeGame?.Id); throw; }
        }
        Find<StackPanel>("GameList").Children.Remove(gameTiles[index]);
        gameTiles.RemoveAt(index); games.RemoveAt(index);
        if(activeGame!=game) return;
        if(next!=null) ActivateGame(next,false);
        else {
            activeGame=null; gameExe=gameAssets=null; saveLibrary=null; available.Clear();
            foreach(var option in projects) option.IsChecked=false;
            SetLaunchState(LaunchState.Idle);
        }
        PositionGameMark(true);
    }
}
