using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

internal sealed partial class DesktopLauncher
{
    CancellationTokenSource componentScan;

    void AttachComponentCheck() {
        Find<Button>("CheckComponents").Click+=async delegate {
            if(componentScan!=null) { componentScan.Cancel(); return; }
            await CheckComponentsAsync();
        };
    }

    IEnumerable<(string Id, ComponentLibrary.Entry[] Files)> ComponentGroups() {
        var index=ComponentLibrary.Index.ToLookup(file=>file.Group);
        yield return ("launcher",index["launcher"].Concat(index["metadata"]).ToArray());
        foreach(string id in new[]{"net",
#if !NETFRAMEWORK
            "desktop",
#endif
            "python","crt","transfer","shader","graphics"}) {
            yield return (id,index[id].ToArray());
        }
        foreach(var project in catalog) {
            string revision="/"+(string)project.Value.Attribute("commit")+"/";
            yield return (project.Key,index[project.Key].Where(file=>file.Path.Contains(revision)).ToArray());
        }
    }

    async Task CheckComponentsAsync() {
        var button=Find<Button>("CheckComponents");
        var status=Find<TextBlock>("ComponentSummary");
        string assets=gameAssets, exe=gameExe;
        bool installed=activeGame!=null && activeGame.Pending==null;
        using(var cancel=new CancellationTokenSource()) {
            componentScan=cancel;
            status.Visibility=Visibility.Visible;
            status.SetResourceReference(TextBlock.TextProperty,"ui.正在校验本地文件…");
            button.SetResourceReference(ContentControl.ContentProperty,"ui.取消");
            var progress=new Progress<string>(key=> {
                if(componentScan==cancel && !closed && !cancel.IsCancellationRequested)
                    status.SetResourceReference(TextBlock.TextProperty,key);
            });
            string result;
            try {
                result=await Task.Run(()=>CheckEnvironment(installed,assets,exe,cancel.Token,progress),cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
            } catch(OperationCanceledException) {
                result="ui.检测已取消";
            } catch(Exception e) when(ComponentError(e)) {
                Append("Component check: "+e.Message);
                result="ui.检测发现问题，详情见日志。";
            } finally {
                componentScan=null;
                if(!closed) button.SetResourceReference(ContentControl.ContentProperty,"ui.检测");
            }
            if(!closed) status.SetResourceReference(TextBlock.TextProperty,result);
        }
    }

    string CheckEnvironment(bool installed,string assets,string exe,CancellationToken token,IProgress<string> progress) {
        bool repaired=false, restoring=false;
        int errors=0;
        void ReportError(string source,Exception error) {
            errors++;
            Append("Component check: "+source+" · "+error.Message);
        }
        void ReportRestore() {
            if(restoring) return;
            restoring=true;
            progress.Report("ui.正在安装或修复文件…");
        }
        void ReportCheck() {
            if(!restoring) return;
            restoring=false;
            progress.Report("ui.正在校验本地文件…");
        }
        using(var library=new ComponentLibrary(root)) {
            foreach(var group in ComponentGroups()) {
                token.ThrowIfCancellationRequested();
                ReportCheck();
                int before=errors;
                try {
                    if(group.Files.Length==0) throw new InvalidDataException(Ui.Text("内置组件库未收录此版本。"));
                    foreach(var file in group.Files) {
                        try { if(library.Ensure(file,token,ReportRestore)) repaired=true; }
                        catch(Exception e) when(ComponentError(e)) { ReportError(file.Path,e); }
                    }
                    if(errors==before) TestComponentHost(group.Id,token);
                } catch(Exception e) when(ComponentError(e)) { ReportError(group.Id,e); }
            }
        }
        token.ThrowIfCancellationRequested();
        if(installed) {
            ReportCheck();
            try { ValidateImportFiles(assets,exe); }
            catch(Exception e) when(ComponentError(e)) { ReportError("Game files",e); }
        }
        if(errors!=0) return "ui.检测发现问题，详情见日志。";
        return repaired ? "ui.修复完成，运行环境正常。" : "ui.检测完成，运行环境正常。";
    }

    static bool ComponentError(Exception e) => e is IOException || e is InvalidDataException ||
        e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception || e is System.Xml.XmlException ||
        e is ArgumentException || e is InvalidOperationException || e is NotSupportedException || e is System.Security.SecurityException;

    void TestComponentHost(string group,CancellationToken token) {
        string relative, arguments;
        switch(group) {
            case "python":
                relative=@"Runtime\Python\python.exe";
                arguments="-I -B -c \"import ctypes, msvcrt, _winapi, sqlite3, zipfile, xml.etree.ElementTree\""; break;
            case "transfer":
                relative=@"Runtime\Transfers\Relay.Transfer.exe"; arguments="--self-test"; break;
            default: return;
        }
        var path=Path.Combine(root,relative);
        var start=new ProcessStartInfo(path,arguments) {
            UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(path),
            RedirectStandardError=true,RedirectStandardOutput=true
        };
        start.EnvironmentVariables["DOTNET_DISABLE_GUI_ERRORS"]="1";
        using(var probe=Process.Start(start)) {
            probe.BeginOutputReadLine();
            var stderr=probe.StandardError.ReadToEndAsync();
            var timer=Stopwatch.StartNew();
            while(!probe.WaitForExit(100)) {
                if(!token.IsCancellationRequested && timer.Elapsed.TotalSeconds<15) continue;
                try { probe.Kill(); } catch(InvalidOperationException) {}
                token.ThrowIfCancellationRequested();
                throw new IOException(Ui.Text("组件自检超时。"));
            }
            if(probe.ExitCode!=0) throw new IOException(Path.GetFileName(path)+" · "+probe.ExitCode+" "+stderr.GetAwaiter().GetResult().Trim());
        }
    }
}
