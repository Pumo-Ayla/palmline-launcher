using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Windows.Input;

internal sealed class BindingSlots
{
    public const int Limit=5;
    // Stored separately so installing the preset never renumbers existing XML files.
    public const int GtaVi=Limit;
    public bool HasGtaVi { get; private set; }
    string DeletedMarker { get { return Path.Combine(directory,"gta-vi.deleted"); } }
    string SelectionMarker { get { return Path.Combine(directory,"gta-vi.selected"); } }
    readonly string directory;
    readonly string[] names=new string[Limit];
    readonly string[][] saved=new string[Limit][];
    public readonly string[][] Draft=new string[Limit][];
    public readonly int[] Position=new int[Limit];
    public int Active { get; private set; }=-1;
    public bool Saved(int slot) { return slot==GtaVi ? HasGtaVi : saved[slot]!=null; }
    public string Name(int slot) { return slot==GtaVi ? Ui.Text("GTA6 定制") : names[slot] ?? Ui.Format("配置 {0}",slot+1); }
    public bool Ready(int slot) { return slot==GtaVi ? HasGtaVi : Saved(slot) && Array.TrueForAll(saved[slot],v=>v!=null); }
    public string[] Profile { get { return Active<0 || Active==GtaVi ? null : saved[Active]; } }
    string FileFor(int slot) { return Path.Combine(directory,"slot-"+(slot+1)+".xml"); }
    public BindingSlots(string directory) {
        this.directory=directory;
        DateTime newest=DateTime.MinValue;
        for(int i=0;i<Limit;i++) {
            var path=FileFor(i);
            if(!File.Exists(path)) continue;
            try {
                var xml=XElement.Load(path);
                var bindings=MappingStep.All.Select(s=>(string)xml.Element(s.Id)).ToArray();
                if(xml.Name!="bindings" || bindings.All(string.IsNullOrWhiteSpace)) continue;
                saved[i]=Array.ConvertAll(bindings,v=>Valid(v) ? v : null);
                string name=((string)xml.Attribute("name"))?.Trim();
                if(!string.IsNullOrEmpty(name)) names[i]=name;
                if(!Ready(i)) continue;
                DateTime modified=File.GetLastWriteTimeUtc(path);
                if(modified>=newest) { Active=i; newest=modified; }
            } catch(Exception ex) when(ex is XmlException || ex is IOException || ex is UnauthorizedAccessException) { }
        }
        HasGtaVi=!File.Exists(DeletedMarker);
        if(HasGtaVi && (Active<0 || File.Exists(SelectionMarker))) Active=GtaVi;
    }
    static bool Valid(string id) {
        if(string.IsNullOrEmpty(id) || MappedPadPreview.Walking(id)) return false;
        Key key;
        if(id.StartsWith("Key.",StringComparison.Ordinal))
            return Enum.TryParse(id.Substring(4),out key) && Enum.IsDefined(typeof(Key),key) && key!=Key.None && key!=Key.Escape;
        return id=="Mouse.Left" || id=="Mouse.Right" || id=="Mouse.Middle" || id=="Mouse.XButton1" ||
               id=="Mouse.XButton2" || id=="Wheel.Up" || id=="Wheel.Down";
    }
    public void Select(int slot) {
        if(!Ready(slot)) return;
        if(slot==GtaVi) {
            Directory.CreateDirectory(directory);
            File.WriteAllText(SelectionMarker,"1"); Active=slot; return;
        }
        File.SetLastWriteTimeUtc(FileFor(slot),DateTime.UtcNow);
        if(File.Exists(SelectionMarker)) File.Delete(SelectionMarker);
        Active=slot;
    }
    public void RestoreGtaVi() {
        if(HasGtaVi || saved.Count(profile=>profile!=null)>=Limit) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(SelectionMarker,"1");
        File.Delete(DeletedMarker);
        HasGtaVi=true; Active=GtaVi;
    }
    public string[] Edit(int slot) {
        if(slot==GtaVi) throw new InvalidOperationException(Ui.Text("只读配置"));
        return Draft[slot] ?? (Draft[slot]=Saved(slot) ? (string[])saved[slot].Clone() : new string[MappingStep.All.Length]);
    }
    public void Save(int slot) {
        var values=Edit(slot);
        if(!Array.TrueForAll(values,Valid)) throw new InvalidOperationException(Ui.Text("请完成所有键位。"));
        var xml=new XElement("bindings",new XAttribute("version",2),
            MappingStep.All.Select((s,i)=>new XElement(s.Id,values[i])));
        xml.SetAttributeValue("name",names[slot]);
        Directory.CreateDirectory(directory);
        Write(slot,xml);
        if(File.Exists(SelectionMarker)) File.Delete(SelectionMarker);
        saved[slot]=(string[])values.Clone(); Active=slot; Draft[slot]=null; Position[slot]=0;
        if(slot!=0 && Ready(0)) {
            Active=0;
            // The new profile is already committed even if the first file is read-only.
            try { File.SetLastWriteTimeUtc(FileFor(0),DateTime.UtcNow); }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
    public void DeleteActive() {
        int slot=Active;
        if(slot<0) return;
        int fallback=Enumerable.Range(0,Limit).Where(i=>i!=slot && Ready(i))
            .OrderByDescending(i=>File.GetLastWriteTimeUtc(FileFor(i))).ThenByDescending(i=>i).DefaultIfEmpty(-1).First();
        if(slot==GtaVi) {
            Directory.CreateDirectory(directory);
            File.WriteAllText(DeletedMarker,"1");
            HasGtaVi=false; Active=fallback; return;
        }
        File.Delete(FileFor(slot));
        saved[slot]=Draft[slot]=null;
        names[slot]=null; Position[slot]=0; Active=fallback<0 && HasGtaVi ? GtaVi : fallback;
    }
    public void Rename(int slot,string value) {
        if(slot==GtaVi || !Saved(slot)) return;
        value=value.Trim();
        if(value==Name(slot)) return;
        string name=value.Length==0 ? null : value;
        var xml=XElement.Load(FileFor(slot));
        xml.SetAttributeValue("name",name);
        Write(slot,xml);
        names[slot]=name;
    }
    void Write(int slot,XElement xml) {
        string path=FileFor(slot), temp=Path.Combine(directory,Guid.NewGuid().ToString("N")+".tmp");
        try {
            xml.Save(temp);
            if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
        } finally { if(File.Exists(temp)) File.Delete(temp); }
    }
}
