using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using SharpCompress.Readers;
using SharpCompress.Readers.Rar;
using SharpCompress.Archives;

internal static partial class Program
{
    static string Input() => Encoding.UTF8.GetString(Convert.FromBase64String(Console.ReadLine() ?? ""));

    static int Main(string[] args) {
        Console.OutputEncoding=new UTF8Encoding(false);
        try {
            if(args.Length==1 && args[0]=="--self-test") {
                Console.WriteLine(typeof(ClientEngine).Assembly.GetName().Name + " / " + typeof(IArchive).Assembly.GetName().Name);
                return 0;
            }
            var source=Input();
            string destination=Path.GetFullPath(Input());
            var cancel=new CancellationTokenSource();
            new Thread(()=>{ Console.ReadLine(); cancel.Cancel(); }) { IsBackground=true }.Start();
            var mode=args.SingleOrDefault();
            if(mode=="--inspect-game" || mode=="--inspect-exe")
                Inspect(source,destination,mode=="--inspect-exe",cancel.Token).GetAwaiter().GetResult();
            else if(mode=="--fetch-game" || mode=="--fetch-exe")
                Run(null,Torrent.Load(source),destination,mode=="--fetch-exe",cancel.Token).GetAwaiter().GetResult();
            else if(mode=="--fetch-archive-keep" || mode=="--fetch-archive-release")
                DownloadArchives(Torrent.Load(source),destination,mode=="--fetch-archive-release",cancel.Token).GetAwaiter().GetResult();
            else if(mode=="--check-archive") {
                var volumes=LocalVolumes(source);
                foreach(var file in volumes) CheckArchiveHeader(file);
                Console.WriteLine("DONE "+volumes.Length);
            }
            else if(mode=="--unpack-game" || mode=="--unpack-release" || mode=="--unpack-exe" || mode=="--http-exe")
                LocalImport(mode,source,destination,cancel.Token).GetAwaiter().GetResult();
            else if(mode==null) Run(MagnetLink.Parse(source),null,destination,false,cancel.Token).GetAwaiter().GetResult();
            else throw new ArgumentException("Unknown transfer command.");
            return 0;
        } catch(OperationCanceledException) { return 2; }
        catch(Exception e) { Console.Error.WriteLine(e.GetBaseException().Message); return 1; }
    }

    static readonly Regex Device=new Regex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(\.|$)",RegexOptions.IgnoreCase);
    internal static string SafePath(string root,string relative) {
        var parts=relative.Replace('/','\\').Split('\\');
        if(parts.Any(p=>p.Length==0 || p.EndsWith(".") || p.EndsWith(" ") ||
            Device.IsMatch(p) || p.IndexOfAny(Path.GetInvalidFileNameChars())>=0))
            throw new InvalidDataException("Invalid archive or torrent path: "+relative);
        return Path.Combine(root,string.Join("\\",parts));
    }

    static EngineSettings Settings(string cache) => new EngineSettingsBuilder {
        CacheDirectory=cache, DhtEndPoint=new IPEndPoint(IPAddress.Any,0),
        ListenEndPoints=new Dictionary<string,IPEndPoint>{{"ipv4",new IPEndPoint(IPAddress.Any,0)}},
        AllowPortForwarding=false
    }.ToSettings();

    static bool GameExe(string path) => string.Equals(Path.GetFileName(path),"game_scarlett_master.exe",StringComparison.OrdinalIgnoreCase);

    static async Task Inspect(string link,string folder,bool exe,CancellationToken token) {
        var magnet=MagnetLink.Parse(link);
        using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
        using(var engine=new ClientEngine(Settings(Path.Combine(folder,"engine")))) {
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            ReadOnlyMemory<byte> metadata;
            try { metadata=await engine.DownloadMetadataAsync(magnet,timeout.Token).WaitAsync(timeout.Token); }
            catch(OperationCanceledException) when(!token.IsCancellationRequested) { throw new TimeoutException("METADATA_TIMEOUT"); }
            var torrent=Torrent.Load(metadata.Span);
            SafePath(folder,torrent.Name);
            var paths=torrent.Files.Select(f=>SafePath(folder,f.Path)).ToArray();
            if(exe && paths.Count(GameExe)!=1 && !(paths.Count(GameExe)==0 && ExeArchiveSet(paths))) throw new InvalidDataException("EXE_CONTENT");
            if(!exe && !paths.Any(p=>string.Equals(Path.GetFileName(p),"MicrosoftGame.Config",StringComparison.OrdinalIgnoreCase)
                || Part.IsMatch(p) || new[]{".7z",".zip",".rar"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("GAME_CONTENT");
            var dictionary=BEncodedValue.Decode<BEncodedDictionary>(metadata.Span);
            if(magnet.AnnounceUrls.Count>0 && !dictionary.ContainsKey("announce-list")) {
                var tiers=new BEncodedList();
                foreach(var url in magnet.AnnounceUrls) tiers.Add(new BEncodedList { new BEncodedString(url) });
                dictionary["announce-list"]=tiers;
                dictionary["announce"]=new BEncodedString(magnet.AnnounceUrls[0]);
            }
            string path=Path.Combine(folder,exe ? "exe.torrent" : "game.torrent");
            File.WriteAllBytes(path,dictionary.Encode());
            if(!exe && !paths.Any(p=>Path.GetFileName(p).Equals("MicrosoftGame.Config",StringComparison.OrdinalIgnoreCase)))
                File.WriteAllText(path+".archive",torrent.Name);
            else if(File.Exists(path+".archive")) File.Delete(path+".archive");

            Console.WriteLine("DONE "+path);
        }
    }

    static async Task Run(MagnetLink magnet,Torrent torrent,string destination,bool exe,CancellationToken token) {
        if(!Directory.Exists(destination)) throw new DirectoryNotFoundException(destination);
        string work=Path.Combine(destination,".relay-"+Guid.NewGuid().ToString("N"));
        string staged=Path.Combine(work,"extracted");
        Directory.CreateDirectory(staged);
        try {
            using(var engine=new ClientEngine(Settings(Path.Combine(work,"engine")))) {
                Console.WriteLine("METADATA");
                var pieces=Path.Combine(work,"pieces");
                var manager=torrent==null ? await engine.AddStreamingAsync(magnet,pieces) : await engine.AddStreamingAsync(torrent,pieces);
                using(var lifetime=CancellationTokenSource.CreateLinkedTokenSource(token)) {
                    manager.TorrentStateChanged+=(s,e)=>{ if(e.NewState==TorrentState.Error) lifetime.Cancel(); };
                    try {
                        await manager.StartAsync().WaitAsync(token);
                        await manager.WaitForMetadataAsync(lifetime.Token);
                        SafePath(work,manager.Torrent.Name);
                        foreach(var file in manager.Files) SafePath(work,file.Path);
                        var selected=exe ? manager.Files.SingleOrDefault(f=>GameExe(f.Path)) : null;
                        if(selected!=null) foreach(var file in manager.Files.Where(f=>f!=selected)) await manager.SetFilePriorityAsync(file,Priority.DoNotDownload);
                        using(var report=new Timer(_=>Console.WriteLine("PROGRESS "+manager.PartialProgress.ToString("0.0",CultureInfo.InvariantCulture)),null,0,500)) {
                            if(selected!=null) {
                                using(var source=new TorrentStreams(manager,lifetime.Token))
                                using(var stream=source.Volume(selected))
                                    await Task.Run(()=>WriteFile(staged,Path.GetFileName(selected.Path),stream,lifetime.Token),lifetime.Token);
                            } else await Task.Run(()=>Extract(manager,staged,lifetime.Token,exe),lifetime.Token);
                        }
                    } catch(OperationCanceledException) when(manager.Error!=null && !token.IsCancellationRequested) {
                        throw new IOException(manager.Error.Exception.Message,manager.Error.Exception);
                    } finally { await manager.StopAsync(TimeSpan.FromSeconds(2)); }
                }
            }
            token.ThrowIfCancellationRequested();
            if(exe && !File.Exists(Path.Combine(staged,"game_scarlett_master.exe"))) throw new InvalidDataException("EXE_CONTENT");
            var output=Path.Combine(destination,"Import-"+Guid.NewGuid().ToString("N").Substring(0,12));
            Directory.Move(staged,output);
            Console.WriteLine("DONE "+output);
        } finally {
            try { Directory.Delete(work,true); }
            catch(IOException e) { Console.Error.WriteLine(e.Message); }
            catch(UnauthorizedAccessException e) { Console.Error.WriteLine(e.Message); }
        }
    }

    static readonly Regex Part=new Regex(@"^(.*)\.part(\d+)\.(rar|exe)$",RegexOptions.IgnoreCase);
    static void Extract(TorrentManager manager,string root,CancellationToken token,bool exeOnly) {
        var files=manager.Files.ToArray();
        using(var source=new TorrentStreams(manager,token)) {
            bool looseGame=!exeOnly && files.Any(f=>string.Equals(Path.GetFileName(f.Path),"MicrosoftGame.Config",StringComparison.OrdinalIgnoreCase));
            var groups=files.Where(f=>!looseGame && Part.IsMatch(f.Path))
                .GroupBy(f=>Part.Match(f.Path).Groups[1].Value,StringComparer.OrdinalIgnoreCase);
            foreach(var volumes in groups) {
                var order=volumes.OrderBy(f=>int.Parse(Part.Match(f.Path).Groups[2].Value,CultureInfo.InvariantCulture)).ToArray();
                for(int i=0;i<order.Length;i++) {
                    var match=Part.Match(order[i].Path);
                    if(int.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture)!=i+1)
                        throw new InvalidDataException("Missing or duplicate RAR volume.");
                }
                using(var reader=RarReader.OpenReader(order.Select(f=>(Stream)source.Volume(f)),
                    ReaderOptions.ForSelfExtractingArchive(null))) Unpack(reader,root,token,exeOnly);
            }
            foreach(var file in files) {
                if(!looseGame && Part.IsMatch(file.Path)) continue;
                using(var stream=source.Volume(file)) {
                    var extension=Path.GetExtension(file.Path).ToLowerInvariant();
                    if(!looseGame && extension==".7z") {
                        using(var package=ArchiveFactory.OpenArchive(stream))
                        using(var reader=package.ExtractAllEntries()) Unpack(reader,root,token,exeOnly);
                    } else if(!looseGame && (extension==".zip" || extension==".rar")) {
                        using(var reader=ReaderFactory.OpenReader(stream)) Unpack(reader,root,token,exeOnly);
                    } else if(!exeOnly) WriteFile(root,file.Path,stream,token);
                }
            }
        }
    }

    static void Unpack(IReader reader,string root,CancellationToken token,bool exeOnly=false) {
        int count=0;
        while(reader.MoveToNextEntry()) {
            token.ThrowIfCancellationRequested();
            var entry=reader.Entry;
            if(entry.LinkTarget!=null) throw new InvalidDataException("Archive contains a link.");
            if(exeOnly && (entry.IsDirectory || !GameExe(entry.Key))) continue;
            if(entry.IsDirectory) { Directory.CreateDirectory(SafePath(root,entry.Key.TrimEnd('/','\\'))); continue; }
            using(var input=reader.OpenEntryStream()) WriteFile(root,exeOnly ? "game_scarlett_master.exe" : entry.Key,input,token);
            count++;
        }
        if(count==0) throw new InvalidDataException(exeOnly ? "EXE_CONTENT" : "The archive contains no files.");
    }

    static void WriteFile(string root,string name,Stream input,CancellationToken token) {
        string full=SafePath(root,name);
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        using(var output=new FileStream(full,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
            var buffer=new byte[131072];
            int n;
            while((n=input.Read(buffer,0,buffer.Length))>0) {
                token.ThrowIfCancellationRequested();
                output.Write(buffer,0,n);
            }
        }
    }
}
