using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// These are the four settings read by victory play and saved by its F1 menu.
internal sealed class VictoryNvidiaMenu
{
    internal const string Type="VICTORY_MENU_FG_TYPE", Frames="VICTORY_MENU_FRAMEGEN",
        Console="VICTORY_MENU_CONSOLE", Dlss="VICTORY_MENU_DLSS";
    readonly string path;
    readonly Dictionary<string,string> values=new Dictionary<string,string>();

    internal VictoryNvidiaMenu(string path) {
        this.path=path;
        foreach(string line in ReadLines()) {
            int split=line.IndexOf('=');
            if(split>=0) values[line.Substring(0,split).Trim()]=line.Substring(split+1).Trim();
        }
    }
    string[] ReadLines() {
        try { return File.ReadAllLines(path); }
        catch(FileNotFoundException) { return new string[0]; }
        catch(DirectoryNotFoundException) { return new string[0]; }
    }
    internal string Get(string key) {
        if(values.TryGetValue(key,out var value)) return value;
        switch(key) {
            case Type: return "auto";
            case Console: return "series-x";
            case Dlss: return "off";
            default: return "0";
        }
    }
    internal void ApplyDlssSupport(bool supported) {
        values.TryGetValue(Dlss,out string saved);
        string mode=supported ? (saved=="quality" || saved=="balanced" || saved=="performance" ? saved : "dlaa") : "off";
        if(saved!=mode) Save(new Dictionary<string,string> { [Dlss]=mode });
    }
    internal int Multiplier {
        get { return int.TryParse(Get(Frames),out int n) && n>=1 && n<=6 ? Math.Max(2,n) : 0; }
    }
    internal static int Limit(string type,string console,string dlss,bool sr,int dlssMax,bool fsr) {
        bool upscaling=sr && dlss!="off";
        if(type!="fsr" && upscaling && dlssMax>=2) return Math.Min(dlssMax,6);
        return type!="dlss" && fsr && (upscaling || console=="series-s") ? 2 : 0;
    }
    internal void Save(Dictionary<string,string> edits) {
        // Merge only this gesture into the latest F1 file. Keep other native switches and comments.
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        using(var mutex=new System.Threading.Mutex(false,@"Local\Palmline.VIctoryNvidia.Menu")) {
            try { mutex.WaitOne(); } catch(System.Threading.AbandonedMutexException) { }
            try {
                var lines=ReadLines().Where(line=>{
                    int i=line.IndexOf('=');
                    return i<0 || !edits.ContainsKey(line.Substring(0,i).Trim());
                }).Concat(edits.Select(pair=>pair.Key+"="+pair.Value));
                File.WriteAllLines(temp,lines,new UTF8Encoding(false));
                if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
                foreach(var pair in edits) values[pair.Key]=pair.Value;
            } finally {
                mutex.ReleaseMutex();
                File.Delete(temp);
            }
        }
    }
}
