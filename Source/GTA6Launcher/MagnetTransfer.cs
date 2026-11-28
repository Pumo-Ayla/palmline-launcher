using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

internal sealed partial class DesktopLauncher
{
    Process transfer;
    void SetupMagnetTransfer() {
        Find<Button>("CancelMagnet").Click+=delegate { CancelTransfer(); };
        window.Closed+=delegate { CancelTransfer(); if(transfer==null) ClearImportMetadata(); };
    }

    void CancelTransfer() {
        if(transfer==null) return;
        try { transfer.StandardInput.Close(); }
        catch(InvalidOperationException) { }
        catch(IOException) { }
        Find<Button>("CancelMagnet").IsEnabled=false;
    }

    static bool ValidMagnet(string link) {
        if(!Uri.TryCreate(link,UriKind.Absolute,out var uri) || !uri.Scheme.Equals("magnet",StringComparison.OrdinalIgnoreCase)) return false;
        return uri.Query.TrimStart('?').Split('&').Select(Uri.UnescapeDataString).Any(p=>
            Regex.IsMatch(p,@"\Axt=urn:(?:btih:(?:[0-9a-f]{40}|[a-z2-7]{32})|btmh:1220[0-9a-f]{64})\z",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant));
    }

    static string MagnetCaption(string link) {
        string value=link.Trim();
        if(!value.StartsWith("magnet:?",StringComparison.OrdinalIgnoreCase)) return value;
        var parameters=value.Substring(8).Split('&');
        var name=parameters.FirstOrDefault(p=>p.StartsWith("dn=",StringComparison.OrdinalIgnoreCase));
        if(name!=null) {
            var decoded=Uri.UnescapeDataString(name.Substring(3).Replace('+',' '));
            if(!string.IsNullOrWhiteSpace(decoded)) return Regex.Replace(decoded,@"\s+"," ").Trim();
        }
        var hash=parameters.FirstOrDefault(p=>p.StartsWith("xt=",StringComparison.OrdinalIgnoreCase));
        return hash==null ? value : Uri.UnescapeDataString(hash.Substring(3));
    }

    static string ExeSourceCaption(string source) {
        string value=source.Trim();
        if(value.StartsWith("magnet:?",StringComparison.OrdinalIgnoreCase)) return MagnetCaption(value);
        if(Uri.TryCreate(value,UriKind.Absolute,out var uri) && (uri.Scheme=="http" || uri.Scheme=="https")) {
            string file=Uri.UnescapeDataString(uri.Segments.Last().TrimEnd('/'));
            return string.IsNullOrEmpty(file) ? uri.Host : file;
        }
        string path=value.Replace('\\','/').TrimEnd('/');
        return path.Substring(path.LastIndexOf('/')+1);
    }

    void UseBundledTransferRuntime(ProcessStartInfo start) {
        string runtime = Path.Combine(root, "Runtime", "DotNet");
        if (File.Exists(Path.Combine(runtime, "dotnet.exe")))
            start.EnvironmentVariables["DOTNET_ROOT_X64"] = runtime;
    }

    async Task<string> TransferOperation(string command,string input,string destination,string stage) {
        if(closed) throw new OperationCanceledException();
        var activity=Find<TextBlock>("CheckActivity");
        var bar=Find<ProgressBar>("MagnetProgress");
        var cancel=Find<Button>("CancelMagnet");
        activity.Text=Ui.Text(stage);
        if(command=="--inspect-game" || command=="--inspect-exe")
            Find<TextBlock>("CheckSource").Text=MagnetCaption(input);
        else if(command=="--fetch-exe" || command=="--http-exe")
            Find<TextBlock>("CheckSource").Text=ExeSourceCaption(Find<TextBox>("ExePath").Text);
        else if(command=="--check-archive" || command.StartsWith("--unpack-",StringComparison.Ordinal))
            Find<TextBlock>("CheckSource").Text=Path.GetFileName(input);
        else if(command.StartsWith("--fetch-",StringComparison.Ordinal)) Find<TextBlock>("CheckSource").Text=MagnetCaption(Find<TextBox>("MagnetLink").Text);
        bar.IsIndeterminate=true;
        var info=new ProcessStartInfo(Path.Combine(root,"Runtime","Transfers","Relay.Transfer.exe"),command) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,
            RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
        };
        UseBundledTransferRuntime(info);
        string operation=Guid.NewGuid().ToString("N").Substring(0,8);
        var elapsed=Stopwatch.StartNew();
        sessionLog.Write("TRANSFER", $"{operation} start {command}; input={DiagnosticReport.Shareable(input)}; destination={DiagnosticReport.Shareable(destination)}; host={info.FileName}; DOTNET_ROOT_X64={info.EnvironmentVariables["DOTNET_ROOT_X64"]}");
        using(var process=Process.Start(info)) {
            transfer=process;
            cancel.Visibility=Visibility.Visible;
            cancel.IsEnabled=true;
            try {
                var errors=process.StandardError.ReadToEndAsync();
                try {
                    foreach(string value in new[]{input,destination})
                        process.StandardInput.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
                } catch(IOException ex) { sessionLog.Write("TRANSFER",operation+" stdin: "+ex.Message); }
                string line,result=null;
                string lastProgress=null;
                long progressLoggedAt=-30;
                while((line=await process.StandardOutput.ReadLineAsync())!=null) {
                    if(line.StartsWith("BYTES ",StringComparison.Ordinal) || line.StartsWith("PROGRESS ",StringComparison.Ordinal)) {
                        lastProgress=line;
                        long seconds=elapsed.ElapsedMilliseconds/1000;
                        if(seconds-progressLoggedAt>=30) {
                            sessionLog.Write("TRANSFER",operation+" "+line); progressLoggedAt=seconds;
                        }
                    }
                    else sessionLog.Write("TRANSFER",operation+" "+DiagnosticReport.Shareable(line));
                    if(!closed && line=="EXTRACT") {
                        bar.IsIndeterminate=true;
                        InstallProgressUpdate("正在解压游戏本体",true);
                        activity.Text=Ui.Text("正在解压游戏本体");
                    }
                    if(line.StartsWith("DONE ",StringComparison.Ordinal)) result=line.Substring(5);
                    if(!closed && installing!=null && line.StartsWith("BYTES ",StringComparison.Ordinal) && long.TryParse(line.Substring(6),out var bytes)) {
                        installMessage=Ui.Text("正在下载")+" · "+(bytes/1048576d).ToString("0.0")+" MiB";
                        RefreshInstallAction();
                    }
                    if(!closed && line.StartsWith("PROGRESS ",StringComparison.Ordinal) &&
                        double.TryParse(line.Substring(9),NumberStyles.Float,CultureInfo.InvariantCulture,out double progress)) {
                        bar.IsIndeterminate=false; bar.Value=progress;
                        InstallProgressUpdate(extracting ? "正在解压游戏本体" : "正在下载",extracting,progress);
                        activity.Text=Ui.Text(stage)+" · "+progress.ToString("0.#")+"%";
                    }
                }
                string error=(await errors).Trim();
                await Task.Run(()=>process.WaitForExit());
                sessionLog.Write("TRANSFER", $"{operation} exit=0x{process.ExitCode:X8}; elapsedMs={elapsed.ElapsedMilliseconds}; lastProgress={lastProgress}");
                if(error.Length>0) sessionLog.Write("TRANSFER",operation+" stderr: "+DiagnosticReport.Shareable(error));
                if(process.ExitCode==2 || closed) throw new OperationCanceledException();
                if(process.ExitCode!=0 || string.IsNullOrEmpty(result)) {
                    if(error=="METADATA_TIMEOUT") error=Ui.Text("60 秒内未取得元数据，请确认有在线来源后重试。");
                    if(error=="PEER_TIMEOUT") error=Ui.Text("90 秒内未收到所需数据，请检查在线来源后重试。");
                    if(error=="EXE_CONTENT") error=Ui.Text("链接须包含一个匹配的游戏 EXE。");
                    if(error=="GAME_CONTENT") error=Ui.Text("链接中找不到游戏清单或支持的压缩包。");
                    throw new IOException(Ui.Text(stage)+" · "+error);
                }
                return result;
            } finally {
                CancelTransfer();
                transfer=null;
                if(closed) ClearImportMetadata();
                cancel.Visibility=Visibility.Collapsed;
                bar.IsIndeterminate=true;
            }
        }
    }
}
