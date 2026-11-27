using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

internal sealed class XclSaveLibrary : LocalSaveLibrary
{
    public readonly string DefaultNativeRoot;

    public XclSaveLibrary(string gameLibrary,string identity) : base(Path.Combine(gameLibrary,"xcl-slots"),identity) {
        // Earlier versions ran directly from xcl, or from the first local slot containing xcl.
        string native=Path.Combine(gameLibrary,"xcl");
        DefaultNativeRoot=Directory.Exists(native) ? native : Directory.EnumerateDirectories(gameLibrary)
            .Where(p=>Path.GetFileName(p)==Default || Guid.TryParseExact(Path.GetFileName(p),"N",out _))
            .OrderBy(p=>Path.GetFileName(p)==Default ? 0 : 1).ThenBy(p=>p,StringComparer.Ordinal)
            .Select(p=>Path.Combine(p,"xcl")).FirstOrDefault(Directory.Exists) ?? native;
        Directory.CreateDirectory(Data(Default));
        Directory.CreateDirectory(DefaultNativeRoot);
        LoadSelection();
    }

    public override string Saves(string slot) {
        return slot==Default ? DefaultNativeRoot : Path.Combine(Data(slot),"data");
    }

    static bool IsSavePath(string[] parts) {
        if(parts.Length<4) return false;
        if(parts[1]=="saves") return true;
        return parts[1]=="gamesave" && ((parts.Length==5 && parts[4]=="display") ||
            (parts.Length==6 && parts[4]=="b" && !parts[5].StartsWith("~xcltmp.",StringComparison.Ordinal)));
    }

    static IEnumerable<string> FilesUnder(string directory) {
        CheckLocal(directory);
        foreach(string file in Directory.EnumerateFiles(directory)) { CheckLocal(file); yield return file; }
        foreach(string child in Directory.EnumerateDirectories(directory))
            foreach(string file in FilesUnder(child)) yield return file;
    }

    IEnumerable<string> Files(string root) {
        if(!Directory.Exists(root)) yield break;
        CheckLocal(root);
        foreach(string title in Directory.EnumerateDirectories(root)) {
            CheckLocal(title);
            foreach(string kind in new[]{"gamesave","saves"}) {
                string saves=Path.Combine(title,kind);
                if(!Directory.Exists(saves)) continue;
                foreach(string file in FilesUnder(saves))
                    if(IsSavePath(file.Substring(root.Length+1).Split(Path.DirectorySeparatorChar))) yield return file;
            }
        }
    }

    public override bool CanExport(string slot) { return Files(Saves(slot)).Any(); }

    public override List<Story> Stories(string slot) {
        string root=Saves(slot);
        var stories=new List<Story>();
        string latest=null;
        DateTime latestTime=DateTime.MinValue;
        foreach(string file in Files(root)) {
            DateTime time=File.GetLastWriteTimeUtc(file);
            if(time>latestTime) { latestTime=time; latest=file; }
            var parts=file.Substring(root.Length+1).Split(Path.DirectorySeparatorChar);
            if(parts[1]!="gamesave" || parts.Length!=6 || !parts[5].Equals("Save Game Header",StringComparison.OrdinalIgnoreCase)) continue;
            string folder=Path.GetDirectoryName(file);
            try { stories.Add(ReadStoryFiles(Path.GetDirectoryName(folder),file,Path.Combine(folder,"Save Game Data"))); }
            catch(IOException) { } catch(InvalidDataException) { }
        }
        if(stories.Count==0 && latest!=null)
            stories.Add(new Story { Title=Ui.Text("已有存档"),Container=latest,Stamp=new DateTimeOffset(latestTime).ToUnixTimeSeconds() });
        return stories.OrderByDescending(s=>s.Stamp).ThenBy(s=>s.Container,StringComparer.Ordinal).ToList();
    }

    string ArchivePrefix { get { return "xcl/"+TitleId+"/"; } }

    public override void Export(string slot,string target) {
        string source=Saves(slot);
        var files=Files(source).Where(f=>!string.Equals(f,Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase)).ToList();
        if(files.Count==0) throw new InvalidDataException(Ui.Text("暂无存档"));
        string temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            using(var zip=ZipFile.Open(temp,ZipArchiveMode.Create))
                foreach(string file in files)
                    zip.CreateEntryFromFile(file,ArchivePrefix+file.Substring(source.Length+1).Replace('\\','/'),CompressionLevel.Optimal);
            if(File.Exists(target)) File.Replace(temp,target,null); else File.Move(temp,target);
        } finally { if(File.Exists(temp)) File.Delete(temp); }
    }

    static bool SafeSegment(string part) {
        return part.Length>0 && part.Length<=255 && !part.EndsWith(".",StringComparison.Ordinal) &&
            !part.EndsWith(" ",StringComparison.Ordinal) && part.IndexOfAny(Path.GetInvalidFileNameChars())<0 &&
            !Regex.IsMatch(part,@"\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[0-9¹²³]) *(?:\.|\z)",RegexOptions.IgnoreCase);
    }

    public override string Import(string archive) {
        string id=Guid.NewGuid().ToString("N");
        Stage(id,stage=>{
            string native=Path.Combine(stage,"data");
            long total=0;
            int files=0;
            using(var zip=ZipFile.OpenRead(archive)) {
                if(zip.Entries.Count>32768) throw new InvalidDataException(Ui.Text("无效的存档包"));
                foreach(var entry in zip.Entries) {
                    string name=entry.FullName.Replace('\\','/');
                    if(name.EndsWith("/",StringComparison.Ordinal)) continue;
                    if(!name.StartsWith(ArchivePrefix,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(Ui.Text("无效的存档包"));
                    var parts=name.Substring(ArchivePrefix.Length).Split('/');
                    if(!IsSavePath(parts) || !parts.All(SafeSegment) || entry.Length>(256L<<20) || (total+=entry.Length)>(512L<<20))
                        throw new InvalidDataException(Ui.Text("无效的存档包"));
                    string path=Path.Combine(native,Path.Combine(parts));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    ExtractEntry(entry,path);
                    File.SetLastWriteTimeUtc(path,entry.LastWriteTime.UtcDateTime);
                    files++;
                }
            }
            if(files==0) throw new InvalidDataException(Ui.Text("无效的存档包"));
            File.WriteAllText(Path.Combine(stage,"name.txt"),Path.GetFileNameWithoutExtension(archive));
            File.WriteAllText(Path.Combine(stage,"imported.flag"),"");
        });
        return id;
    }
}
