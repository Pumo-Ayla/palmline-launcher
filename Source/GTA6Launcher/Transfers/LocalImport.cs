using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Readers;
using SharpCompress.Readers.Rar;

internal static partial class Program
{
    static bool ArchiveName(string p) => new[]{".zip",".7z",".rar"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase);
    static bool ExeArchiveSet(string[] paths) {
        var archives=paths.Where(p=>ArchiveName(p) || Part.IsMatch(p)).ToArray();
        return archives.Length==1 || (archives.Length>1 && archives.All(Part.IsMatch) &&
            archives.Select(p=>Part.Match(p).Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).Count()==1);
    }

    static string[] LocalVolumes(string file) {
        file=Path.GetFullPath(file);
        if(!File.Exists(file) || !(ArchiveName(file) || Part.IsMatch(file))) throw new InvalidDataException("Invalid archive: "+file);
        var first=Part.Match(file);
        if(!first.Success) return new[]{file};
        var parts=Directory.EnumerateFiles(Path.GetDirectoryName(file))
            .Select(p=>Part.Match(p)).Where(m=>m.Success &&
                first.Groups[1].Value.Equals(m.Groups[1].Value,StringComparison.OrdinalIgnoreCase))
            .OrderBy(m=>int.Parse(m.Groups[2].Value)).ToArray();
        for(int i=0;i<parts.Length;i++)
            if(int.Parse(parts[i].Groups[2].Value)!=i+1 || (i>0 && !parts[i].Groups[3].Value.Equals("rar",StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Missing or duplicate RAR volume.");
        if(!file.Equals(parts.First().Value,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择第一个 RAR 分卷。");
        return parts.Select(m=>m.Value).ToArray();
    }

    static string FormatOf(string path) {
        using(var file=File.OpenRead(path)) {
            byte[] header=new byte[8];
            int n=file.Read(header,0,header.Length);
            if(n<6) return "";
            string hex=Convert.ToHexString(header);
            if(hex.StartsWith("504B0304") || hex.StartsWith("504B0506")) return "zip";
            if(hex.StartsWith("377ABCAF271C")) return "7z";
            if(hex.StartsWith("526172211A07")) return "rar";
            if(hex.StartsWith("4D5A")) return "exe";
            return "";
        }
    }

    static void CheckArchiveHeader(string file) {
        var format=FormatOf(file);
        if(format=="exe" && Part.IsMatch(file)) {
            using(var stream=File.OpenRead(file))
                if(SharpCompress.Archives.Rar.RarArchive.IsRarFile(stream,ReaderOptions.ForSelfExtractingArchive(null))) return;
            throw new InvalidDataException("Missing RAR header in the self-extracting volume.");
        }
        if(format!=Path.GetExtension(file).Substring(1).ToLowerInvariant())
            throw new InvalidDataException("压缩包格式与扩展名不符。");
    }

    // Only retire an earlier volume once all its bytes have been read in order.
    // A reader which seeks over data keeps that volume until extraction completes.
    sealed class RarVolume : FileStream {
        readonly Action entered;
        public long Prefix { get; private set; }
        public bool Retired { get; private set; }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
        [DllImport("kernel32.dll", SetLastError=true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,ref byte value,uint size);
        static SafeFileHandle OpenVolume(string path,bool release) {
            var handle=CreateFileW(path,0x80000000u | (release ? 0x10000u : 0u),1,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
            if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            return handle;
        }
        public RarVolume(string path,bool release,Action entered):base(OpenVolume(path,release),FileAccess.Read) { this.entered=entered; }
        int Record(long start,int n) {
            if(n>0) {
                if(start<=Prefix) Prefix=Math.Max(Prefix,start+n);
                entered();
            }
            return n;
        }
        public override int Read(byte[] buffer,int offset,int count) {
            long pos=Position;
            return Record(pos,base.Read(buffer,offset,count));
        }
        public override int Read(Span<byte> buffer) {
            long pos=Position;
            return Record(pos,base.Read(buffer));
        }
        public void RetireIfConsumed() {
            if(!Retired && CanRead && Prefix==Length) {
                Retire();
            }
        }
        public void Retire() {
            if(Retired) return;
            byte remove=1;
            // Delete the opened file identity; never reopen a potentially replaced path.
            if(!SetFileInformationByHandle(SafeFileHandle,4,ref remove,1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Retired=true;
            Dispose();
        }

    }

    static void ExtractVolumes(string[] volumes,string folder,string format,bool release,bool exeOnly,List<RarVolume> streams,CancellationToken token) {
        var group=new List<RarVolume>();
        long total=volumes.Sum(p=>new FileInfo(p).Length),lastReport=0;
        for(int i=0;i<volumes.Length;i++) {
            int index=i;
            var stream=new RarVolume(volumes[i],release,()=>{
                if(release && index>0) group[index-1].RetireIfConsumed();
                long now=Environment.TickCount64;
                if(now-lastReport<350 || total==0) return;
                lastReport=now;
                double percent=Math.Min(99.9,100d*group.Sum(v=>v.Prefix)/total);
                Console.WriteLine("PROGRESS "+percent.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture));
            });
            group.Add(stream);
            streams.Add(stream);
        }
        if(volumes.Length>1 || format=="exe") {
            var options=new ReaderOptions { LeaveStreamOpen=true, LookForHeader=format=="exe" };
            using(var reader=RarReader.OpenReader(group.Cast<Stream>(),options))
                Unpack(reader,folder,token,exeOnly);
        } else if(format=="7z") {
            using(var archive=ArchiveFactory.OpenArchive(group[0],new ReaderOptions { LeaveStreamOpen=true }))
            using(var reader=archive.ExtractAllEntries()) Unpack(reader,folder,token,exeOnly);
        } else if(format=="zip" || format=="rar") {
            using(var reader=ReaderFactory.OpenReader(group[0],new ReaderOptions { LeaveStreamOpen=true }))
                Unpack(reader,folder,token,exeOnly);
        } else throw new InvalidDataException("Unsupported file format.");
    }

    static async Task LocalImport(string mode,string source,string destination,CancellationToken token) {
        bool web=mode=="--http-exe",exeOnly=web || mode=="--unpack-exe",release=mode=="--unpack-release";
        string folder=Path.Combine(destination,"Import-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string temp=null;
        try {
            if(web) {
                var uri=new Uri(source);
                if(uri.Scheme!="http" && uri.Scheme!="https") throw new ArgumentException("Invalid URL.");
                temp=Path.Combine(folder,".download");
                using(var client=new HttpClient())
                using(var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,token)) {
                    response.EnsureSuccessStatusCode();
                    using(var output=File.Create(temp))
                    using(var input=await response.Content.ReadAsStreamAsync(token)) {
                        var buffer=new byte[131072]; int n; long last=0;
                        while((n=await input.ReadAsync(buffer,token))!=0) {
                            await output.WriteAsync(buffer.AsMemory(0,n),token);
                            long now=Environment.TickCount64;
                            if(now-last<350) continue;
                            last=now;
                            long? size=response.Content.Headers.ContentLength;
                            Console.WriteLine(size>0 ? "PROGRESS "+(100d*output.Length/size.Value).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture) : "BYTES "+output.Length);
                        }
                    }
                }
                source=temp;
            }
            string[] volumes=web ? new[]{source} : LocalVolumes(source);
            if(!web) CheckArchiveHeader(volumes[0]);
            string format=FormatOf(volumes[0]);
            if(web && format=="exe") {
                File.Move(temp,Path.Combine(folder,"game_scarlett_master.exe"));
            } else {
                var streams=new List<RarVolume>();
                try {
                    Console.WriteLine("EXTRACT");
                    ExtractVolumes(volumes,folder,format,release,exeOnly,streams,token);
                    token.ThrowIfCancellationRequested();
                    if(!exeOnly && Directory.EnumerateFiles(folder,"MicrosoftGame.Config",SearchOption.AllDirectories).Count()!=1)
                        throw new InvalidDataException("GAME_CONTENT");
                    if(release) foreach(var stream in streams) stream.Retire();
                } finally { foreach(var stream in streams) stream.Dispose(); }
            }
            Console.WriteLine("DONE "+folder);
        } catch(Exception e) {
            if(release) throw new IOException(e.Message+" · 已保留输出文件："+folder);
            Directory.Delete(folder,true);
            throw;
        } finally { if(temp!=null && File.Exists(temp)) File.Delete(temp); }
    }
}
