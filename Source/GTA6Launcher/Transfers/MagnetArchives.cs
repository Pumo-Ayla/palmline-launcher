using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

internal static partial class Program
{
    static async Task DownloadArchives(Torrent torrent,string destination,bool release,CancellationToken token) {
        SafePath(destination,torrent.Name);
        foreach(var file in torrent.Files) SafePath(destination,file.Path);
        if(!torrent.Files.Any(f=>ArchiveName(f.Path) || Part.IsMatch(f.Path)) ||
           torrent.Files.Any(f=>Path.GetFileName(f.Path).Equals("MicrosoftGame.Config",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("GAME_CONTENT");

        string id=Guid.NewGuid().ToString("N");
        string work=Path.Combine(destination,".relay-"+id);
        string pieces=Path.Combine(work,"download");
        string archives=Path.Combine(destination,"Archives-"+id);
        string output=Path.Combine(destination,"Import-"+id);
        Directory.CreateDirectory(pieces);
        try {
            string[] relative;
            using(var engine=new ClientEngine(Settings(Path.Combine(work,"engine")))) {
                var manager=await engine.AddAsync(torrent,pieces);
                await manager.StartAsync().WaitAsync(token);
                try {
                    long bytes=-1,lastChange=Environment.TickCount64;
                    while(!manager.Complete) {
                        if(manager.Error!=null) throw new IOException(manager.Error.Exception.Message);
                        long current=manager.Monitor.DataBytesReceived;
                        if(current!=bytes) { bytes=current; lastChange=Environment.TickCount64; }
                        if(Environment.TickCount64-lastChange>=90000) throw new TimeoutException("PEER_TIMEOUT");
                        Console.WriteLine("PROGRESS "+manager.Progress.ToString("0.0",CultureInfo.InvariantCulture));
                        await Task.Delay(250,token);
                    }
                } finally { await manager.StopAsync(TimeSpan.FromSeconds(2)); }
                relative=manager.Files.Select(f=>Path.GetRelativePath(pieces,f.FullPath)).ToArray();
            }
            token.ThrowIfCancellationRequested();
            var files=relative.Select(p=>SafePath(archives,p)).ToArray();
            Directory.Move(pieces,archives);
            Console.WriteLine("EXTRACT");
            Directory.CreateDirectory(output);
            try {
                ExtractDownloadedArchives(files,archives,output,release,token);
            } catch(Exception e) {
                throw new IOException(e.Message+" · "+archives+" · "+output);
            }
            Console.WriteLine("DONE "+output);
        } finally {
            try { Directory.Delete(work,true); }
            catch(IOException e) { Console.Error.WriteLine(e.Message); }
            catch(UnauthorizedAccessException e) { Console.Error.WriteLine(e.Message); }
        }
    }

    static void ExtractDownloadedArchives(string[] files,string archives,string output,bool release,CancellationToken token) {
        var sets=new List<string[]>();
        foreach(var group in files.Where(p=>Part.IsMatch(p)).GroupBy(p=>Part.Match(p).Groups[1].Value,StringComparer.OrdinalIgnoreCase)) {
            var volumes=group.OrderBy(p=>int.Parse(Part.Match(p).Groups[2].Value,CultureInfo.InvariantCulture)).ToArray();
            for(int i=0;i<volumes.Length;i++)
                if(int.Parse(Part.Match(volumes[i]).Groups[2].Value,CultureInfo.InvariantCulture)!=i+1 ||
                   (i>0 && !Path.GetExtension(volumes[i]).Equals(".rar",StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Missing or invalid RAR volume.");
            sets.Add(volumes);
        }
        sets.AddRange(files.Where(p=>ArchiveName(p) && !Part.IsMatch(p)).Select(p=>new[]{p}));
        foreach(var file in sets.SelectMany(set=>set)) CheckArchiveHeader(file);

        var streams=new List<RarVolume>();
        try {
            foreach(var set in sets) {
                token.ThrowIfCancellationRequested();
                ExtractVolumes(set,output,FormatOf(set[0]),release,false,streams,token);
            }
            foreach(var file in files.Where(p=>!ArchiveName(p) && !Part.IsMatch(p))) {
                using(var input=File.OpenRead(file)) WriteFile(output,Path.GetRelativePath(archives,file),input,token);
            }
            token.ThrowIfCancellationRequested();
            if(Directory.EnumerateFiles(output,"MicrosoftGame.Config",SearchOption.AllDirectories).Count()!=1)
                throw new InvalidDataException("GAME_CONTENT");
            if(release) foreach(var volume in streams) volume.Retire();
        } finally { foreach(var volume in streams) volume.Dispose(); }
    }
}
