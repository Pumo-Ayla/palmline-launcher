using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

internal class LocalSaveLibrary
{
    public const string Default = "default";
    public readonly string Root, TitleId;
    public string Selected { get; private set; }
    public sealed class Story
    {
        public string Title, Container;
        public long Stamp;
        public float? Progress;
    }

    protected LocalSaveLibrary(string root,string titleId) {
        Root=root; TitleId=titleId;
        Directory.CreateDirectory(Root);
    }

    public LocalSaveLibrary(string launcher, string instance, string titleId) {
        if(!Guid.TryParseExact(instance,"N",out _) || !Regex.IsMatch(titleId,@"\A[0-9A-Fa-f]{8}\z"))
            throw new InvalidDataException("Invalid game identity.");
        TitleId=titleId.ToUpperInvariant();
        Root=Path.Combine(launcher,"GameSaves",instance);
        Directory.CreateDirectory(Root);
        if(!Directory.Exists(Data(Default))) {
            string legacy=Path.Combine(launcher,"XCL-VIctory-Lab","profiles","cgbj","data",TitleId);
            string owner=Path.Combine(Path.GetDirectoryName(Root),"legacy-"+TitleId+".txt");
            if(Directory.Exists(Path.Combine(legacy,"saves")) && !File.Exists(owner))
                WriteNew(owner,instance);
            Stage(Default,stage=>{
                var destination=Path.Combine(stage,TitleId);
                if(File.Exists(owner) && File.ReadAllText(owner)==instance) {
                    foreach(string part in new[]{"saves","storage"}) {
                        var path=Path.Combine(legacy,part);
                        if(Directory.Exists(path)) CopyTree(path,Path.Combine(destination,part));
                    }
                }
                Directory.CreateDirectory(Path.Combine(destination,"saves"));
            });
        }
        LoadSelection();
    }
    protected void LoadSelection() {
        string marker=Path.Combine(Root,"selected.txt");
        Selected=File.Exists(marker) ? File.ReadAllText(marker).Trim() : Default;
        if(!IsSlot(Selected) || !Directory.Exists(Data(Selected))) Select(Default);
    }

    static bool IsSlot(string name) { return name==Default || Guid.TryParseExact(name,"N",out _); }
    public string Data(string slot) { return Path.Combine(Root,slot); }
    public virtual string Saves(string slot) { return Path.Combine(Data(slot),TitleId,"saves"); }
    public IEnumerable<string> Slots() {
        return Directory.GetDirectories(Root).Select(Path.GetFileName).Where(IsSlot)
            .OrderBy(id=>id==Default ? 0 : 1).ThenBy(id=>id,StringComparer.Ordinal);
    }
    public string Name(string slot) {
        if(slot==Default) return Ui.Text("本机存档");
        // Earlier releases used identical GUID directories for both imports and local branches.
        string source=Ui.Text(File.Exists(Path.Combine(Data(slot),"imported.flag")) ? "外部存档" : "旧版存档");
        string label=Path.Combine(Data(slot),"name.txt");
        return File.Exists(label) ? source+" · "+File.ReadAllText(label) : source;
    }
    static void WriteNew(string target,string text) {
        string temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { File.WriteAllText(temp,text); File.Move(temp,target); }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    public void Select(string slot) {
        if(!IsSlot(slot) || !Directory.Exists(Saves(slot))) throw new DirectoryNotFoundException(slot);
        string marker=Path.Combine(Root,"selected.txt");
        string temp=marker+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            File.WriteAllText(temp,slot);
            if(File.Exists(marker)) File.Replace(temp,marker,null); else File.Move(temp,marker);
        } finally { if(File.Exists(temp)) File.Delete(temp); }
        Selected=slot;
    }
    protected void Stage(string id,Action<string> populate) {
        string staging=Path.Combine(Root,".pending-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try { populate(staging); Directory.Move(staging,Data(id)); }
        finally { if(Directory.Exists(staging)) Directory.Delete(staging,true); }
    }
    protected static void CheckLocal(string path) {
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Unexpected file link.");
    }
    static void CopyTree(string source,string dest) {
        CheckLocal(source);
        Directory.CreateDirectory(dest);
        foreach(var path in Directory.GetFiles(source)) {
            CheckLocal(path); File.Copy(path,Path.Combine(dest,Path.GetFileName(path)));
        }
        foreach(var path in Directory.GetDirectories(source)) {
            var name=Path.GetFileName(path);
            CopyTree(path,Path.Combine(dest,name));
        }
    }
    static bool IsStoryFolder(string name) { return Regex.IsMatch(name,@"\ASARC[0-9]+~[0-9a-fA-F]{16}\z",RegexOptions.IgnoreCase); }
    static IEnumerable<string> Containers(string root) {
        if(!Directory.Exists(root)) yield break;
        CheckLocal(root);
        foreach(var user in Directory.GetDirectories(root)) {
            CheckLocal(user);
            foreach(var service in Directory.GetDirectories(user)) {
                CheckLocal(service);
                foreach(var container in Directory.GetDirectories(service)) { CheckLocal(container); yield return container; }
            }
        }
    }
    static string Version(string container) {
        return Directory.GetDirectories(container,"v*").Where(p=>Regex.IsMatch(Path.GetFileName(p),@"\Av[1-9][0-9]*\z"))
            .OrderByDescending(p=>Path.GetFileName(p).Length).ThenByDescending(p=>p,StringComparer.Ordinal)
            .FirstOrDefault(p=>File.Exists(Path.Combine(p,"manifest")));
    }
    public virtual bool CanExport(string slot) { return Containers(Saves(slot)).Any(path=>Version(path)!=null); }
    static List<string> Manifest(string version) {
        var path=Path.Combine(version,"manifest");
        CheckLocal(version); CheckLocal(path);
        var names=new List<string>();
        using(var reader=new BinaryReader(File.OpenRead(path))) {
            var stream=reader.BaseStream;
            if(stream.Length>1024*1024) throw new InvalidDataException("Invalid manifest size.");
            while(stream.Position<stream.Length) {
                int size=reader.ReadInt32();
                if(size<0 || size>stream.Length-stream.Position) throw new InvalidDataException("Invalid save manifest.");
                names.Add(Encoding.UTF8.GetString(reader.ReadBytes(size)));
            }
        }
        if(names.Count<2) throw new InvalidDataException("Incomplete save manifest.");
        return names;
    }
    static Story ReadStory(string container,string version,List<string> names) {
        int index=names.IndexOf("Save Game Header",2);
        int data=names.IndexOf("Save Game Data",2);
        if(index<0 || data<0) throw new InvalidDataException("Missing story data or header.");
        var path=Path.Combine(version,(index-2).ToString(CultureInfo.InvariantCulture));
        return ReadStoryFiles(container,path,Path.Combine(version,(data-2).ToString(CultureInfo.InvariantCulture)));
    }
    protected static Story ReadStoryFiles(string container,string path,string dataPath) {
        CheckLocal(path);
        using(var r=new BinaryReader(File.OpenRead(path))) {
            if(r.BaseStream.Length!=296 || r.ReadUInt32()!=6) throw new InvalidDataException("Unknown save header.");
            CheckLocal(dataPath);
            float? progress=null;
            using(var dataReader=new BinaryReader(File.OpenRead(dataPath))) {
                if(r.ReadUInt32()!=dataReader.BaseStream.Length || dataReader.ReadUInt32()!=0x56415341)
                    throw new InvalidDataException("Incomplete story data.");
                // ASAV v2 story metadata. Other layouts can still supply a title.
                if(dataReader.BaseStream.Length>=0x180 && dataReader.ReadUInt32()==2 &&
                   dataReader.ReadUInt32()==1 && dataReader.ReadUInt32()==3 &&
                   dataReader.ReadUInt32()==0x180 && dataReader.ReadUInt32()==0x170) {
                    dataReader.BaseStream.Position=0x158;
                    float value=dataReader.ReadSingle();
                    if(value>=0 && value<=100) progress=value;
                }
            }
            r.BaseStream.Position=16;
            var stamp=r.ReadInt64();
            r.BaseStream.Position=36;
            var title=Encoding.Unicode.GetString(r.ReadBytes(256)).Split('\0')[0].Trim();
            return new Story { Title=title, Stamp=stamp, Container=container, Progress=progress };
        }
    }
    public virtual List<Story> Stories(string slot) {
        var result=new List<Story>();
        foreach(var container in Containers(Saves(slot)).Where(p=>IsStoryFolder(Path.GetFileName(p)))) {
            try {
                var version=Version(container);
                if(version!=null) result.Add(ReadStory(container,version,Manifest(version)));
            } catch(IOException) { } catch(InvalidDataException) { }
        }
        return result.OrderByDescending(s=>s.Stamp).ThenBy(s=>s.Container,StringComparer.Ordinal).ToList();
    }
    public virtual void Export(string slot,string target) {
        string temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        string source=Saves(slot);
        try {
            using(var zip=ZipFile.Open(temp,ZipArchiveMode.Create)) {
                bool any=false;
                foreach(var container in Containers(source)) {
                    var version=Version(container);
                    if(version==null) continue;
                    var names=Manifest(version);
                    foreach(var name in new[]{"manifest"}.Concat(Enumerable.Range(0,names.Count-2).Select(i=>i.ToString(CultureInfo.InvariantCulture)))) {
                        var path=Path.Combine(version,name);
                        CheckLocal(path);
                        string entry=TitleId+"/saves/"+path.Substring(source.Length+1).Replace('\\','/');
                        zip.CreateEntryFromFile(path,entry,CompressionLevel.Optimal);
                    }
                    any=true;
                }
                if(!any) throw new InvalidDataException(Ui.Text("暂无存档"));
            }
            if(File.Exists(target)) File.Replace(temp,target,null); else File.Move(temp,target);
        } finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    public virtual string Import(string archive) {
        string id=Guid.NewGuid().ToString("N");
        Stage(id,stage=>{
            string saves=Path.Combine(stage,TitleId,"saves");
            using(var zip=ZipFile.OpenRead(archive)) {
                long total=0;
                if(zip.Entries.Count>32768) throw new InvalidDataException("Too many save files.");
                foreach(var entry in zip.Entries) {
                    var name=entry.FullName.Replace('\\','/');
                    if(name.EndsWith("/",StringComparison.Ordinal)) continue;
                    if(name.StartsWith(TitleId+"/",StringComparison.OrdinalIgnoreCase)) name=name.Substring(TitleId.Length+1);
                    if(name.StartsWith("saves/",StringComparison.OrdinalIgnoreCase)) name=name.Substring(6);
                    if(!name.StartsWith("0009000000000001/00000000-0000-0000-0000-0000"+TitleId+"/",StringComparison.OrdinalIgnoreCase)
                       || !Regex.IsMatch(name,@"\A[0-9a-fA-F]{16}/[0-9a-fA-F-]{36}/[A-Za-z0-9_%\-]+~[0-9a-fA-F]{16}/v[1-9][0-9]*/(?:manifest|0|[1-9][0-9]*)\z")
                       || entry.Length>(256L<<20) || (total+=entry.Length)>(512L<<20))
                        throw new InvalidDataException(Ui.Text("无效的存档包"));
                    string path=Path.Combine(saves,name);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    ExtractEntry(entry,path);
                }
            }
            var containers=Containers(saves).ToList();
            if(containers.Count==0) throw new InvalidDataException(Ui.Text("无效的存档包"));
            foreach(var container in containers) {
                var ver=Version(container);
                if(ver==null) throw new InvalidDataException(Ui.Text("无效的存档包"));
                var names=Manifest(ver);
                for(int i=0;i<names.Count-2;i++)
                    if(!File.Exists(Path.Combine(ver,i.ToString(CultureInfo.InvariantCulture)))) throw new InvalidDataException("Missing save blob.");
                if(IsStoryFolder(Path.GetFileName(container))) ReadStory(container,ver,names);
            }
            File.WriteAllText(Path.Combine(stage,"name.txt"),Path.GetFileNameWithoutExtension(archive));
            File.WriteAllText(Path.Combine(stage,"imported.flag"),"");
        });
        return id;
    }
    protected static void ExtractEntry(ZipArchiveEntry entry,string path) {
        using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write))
        using(var input=entry.Open()) {
            var buffer=new byte[81920]; int n;
            while((n=input.Read(buffer,0,buffer.Length))!=0) {
                if(output.Length+n>entry.Length) throw new InvalidDataException("Invalid ZIP length.");
                output.Write(buffer,0,n);
            }
            if(output.Length!=entry.Length) throw new InvalidDataException("Incomplete ZIP entry.");
        }
    }
}
