using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

internal static class AchievementJournal
{
    internal sealed class Entry
    {
        public readonly string Id;
        readonly string[] names;
        readonly string[] descriptions;
        public string Name { get { return Ui.Localized(names); } }
        public string Description { get { return Ui.Localized(descriptions); } }
        public readonly int Points;
        public Entry(string[] columns) {
            if(columns.Length!=12) throw new InvalidDataException("Incomplete achievement translations");
            Id=columns[0]; Points=int.Parse(columns[1]);
            names=columns.Skip(2).Take(5).ToArray();
            descriptions=columns.Skip(7).ToArray();
        }
    }

    internal static readonly Entry[] Catalog=LoadCatalog();

    static Entry[] LoadCatalog() {
        using(var reader=new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("GtaViAchievements.tsv"))) {
            return reader.ReadToEnd().Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)
                .Select(row=>new Entry(row.Split('\t'))).ToArray();
        }
    }

    [DataContract]
    sealed class Progress
    {
        [DataMember(Name="achievementId",IsRequired=true)] public string Id { get; set; }
        [DataMember(Name="percentComplete",IsRequired=true)] public int Percent { get; set; }
    }

    internal static bool TryProgress(string line,out string id,out int percent) {
        id=null; percent=0;
        if(string.IsNullOrEmpty(line)) return false;
        int start=line.IndexOf(' ')+1;
        int end=line.IndexOf(" achievement] ",start,StringComparison.Ordinal);
        if(end<start || start>=line.Length || line[start]!='[') return false;
        string source=line.Substring(start+1,end-start-1);
        if(source!="CGBJ" && source!="BELOBUG" && source!="XCL" && source!="ANYXBOXX-ZCY" && source!="VICTORY-NVIDIA") return false;
        string message=line.Substring(end+" achievement] ".Length);
        if(message.StartsWith("local-progress ",StringComparison.Ordinal)) {
            try {
                using(var json=new MemoryStream(Encoding.UTF8.GetBytes(message.Substring("local-progress ".Length)))) {
                    var entry=(Progress)new DataContractJsonSerializer(typeof(Progress)).ReadObject(json);
                    if(entry==null) return false;
                    id=entry.Id; percent=entry.Percent;
                }
            } catch(SerializationException) { return false; }
            catch(System.Xml.XmlException) { return false; }
        } else if(message.StartsWith("unlocked achievementId=",StringComparison.Ordinal)) {
            id=message.Substring("unlocked achievementId=".Length).Split(' ')[0]; percent=100;
        } else return false;
        if(string.IsNullOrEmpty(id) || percent<0 || percent>100) return false;
        return true;
    }

    internal static Dictionary<string,int> Read(string lab,string instance) {
        return ReadHistory(lab,instance).Progress;
    }

    internal sealed class History
    {
        public readonly Dictionary<string,int> Progress=new Dictionary<string,int>(StringComparer.Ordinal);
        public readonly Dictionary<string,DateTimeOffset> FirstCompletion=new Dictionary<string,DateTimeOffset>(StringComparer.Ordinal);
        public readonly HashSet<string> UnlockEvents=new HashSet<string>(StringComparer.Ordinal);

        internal void RecordCompletion(string id,string line) {
            int end=line.IndexOf(' ');
            if(end<20) return;
            var stamp=line.Substring(0,end);
            if(!stamp.EndsWith("Z",StringComparison.Ordinal) && stamp[end-6]!='+' && stamp[end-6]!='-') return;
            if(!DateTimeOffset.TryParse(stamp,CultureInfo.InvariantCulture,DateTimeStyles.AdjustToUniversal,out var time)) return;
            bool explicitUnlock=line.IndexOf(" achievement] unlocked ",StringComparison.Ordinal)>=0;
            if(FirstCompletion.TryGetValue(id,out var previous)) {
                if(time>previous) return;
                if(time==previous && !explicitUnlock) return;
            }
            FirstCompletion[id]=time;
            if(explicitUnlock) UnlockEvents.Add(id); else UnlockEvents.Remove(id);
        }
    }

    internal static History ReadHistory(string lab,string instance) {
        var result=new History();
        var history=Path.Combine(lab,"desktop","play-history",instance);
        if(!Directory.Exists(history)) return result;
        var sessions=Directory.EnumerateFiles(history,"Desktop-*.time");
        foreach(var marker in sessions) {
            string run=Path.GetFileNameWithoutExtension(marker);
            string log=Path.Combine(lab,"runs",run,"achievements.log");
            try {
                using(var stream=new FileStream(log,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                using(var reader=new StreamReader(stream)) {
                    string line;
                    while((line=reader.ReadLine())!=null) {
                        if(!TryProgress(line,out string id,out int percent)) continue;
                        if(!result.Progress.TryGetValue(id,out var previous) || percent>previous) result.Progress[id]=percent;
                        if(percent==100) result.RecordCompletion(id,line);
                    }
                }
            } catch(FileNotFoundException) { }
            catch(DirectoryNotFoundException) { }
        }
        return result;
    }
}
