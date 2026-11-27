using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Input;

internal sealed class GtaViPreset
{
    sealed class Rule { public string Layer, Output; public int Key; public uint Button; }
    static readonly List<Rule> rules=Load();
    static readonly HashSet<int> keys=new HashSet<int>(rules.ConvertAll(r=>r.Key));
    readonly HashSet<int> down=new HashSet<int>();
    readonly Stopwatch partner=new Stopwatch();
    public bool Driving { get; private set; }
    public static int PromptKey(string layer,string output,int preferred) {
        var rule=rules.Find(r=>(r.Layer=="both" || r.Layer==layer) && r.Output==output && r.Key==preferred);
        return rule==null ? 0 : rule.Key;
    }
    public bool WaitingForPartner { get { return partner.IsRunning && partner.ElapsedMilliseconds<450; } }

    static List<Rule> Load() {
        var result=new List<Rule>();
        using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("GtaVi.bindings"))
        using(var reader=new StreamReader(stream)) {
            string line;
            while((line=reader.ReadLine())!=null) {
                if(line.Length==0 || line[0]=='#') continue;
                var fields=line.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);
                int bit=Array.IndexOf(PadState.ButtonNames,fields[2]=="PartnerRB" ? "RB" : fields[2]);
                result.Add(new Rule { Layer=fields[0],Key=int.Parse(fields[1],NumberStyles.HexNumber),
                    Output=fields[2],Button=bit<0 ? 0 : 1u<<bit });
            }
        }
        return result;
    }
    public void Clear() { down.Clear(); partner.Reset(); }
    static int Modifier(int vk) {
        switch(vk) {
            case 0xA0: case 0xA1: return 0x10;
            case 0xA2: case 0xA3: return 0x11;
            case 0xA4: case 0xA5: return 0x12;
            default:return vk;
        }
    }
    bool Held(int vk) {
        if(vk>=0x10 && vk<=0x12) {
            int left=0xA0+2*(vk-0x10);
            return down.Contains(left) || down.Contains(left+1) || down.Contains(vk);
        }
        return down.Contains(vk);
    }
    static int VirtualKey(string id) {
        if(id=="Mouse.Left") return 1;
        if(id=="Mouse.Right") return 2;
        if(id=="Mouse.Middle") return 4;
        Key key;
        return id.StartsWith("Key.",StringComparison.Ordinal) && Enum.TryParse(id.Substring(4),out key)
            ? KeyInterop.VirtualKeyFromKey(key) : 0;
    }
    public bool Aiming => !Driving && Held(0x02);
    bool Applies(Rule rule) => rule.Layer=="both" || rule.Layer==(Driving ? "drive" : "foot") || (rule.Layer=="aim" && Aiming);
    public string[] Targets(string id) {
        int vk=Modifier(VirtualKey(id));
        if(vk==0x75) return new[]{"Layer"};
        return rules.Where(r=>r.Key==vk && (Applies(r) || (!Driving && r.Layer=="aim")))
            .Select(r=>r.Layer=="aim" && !Aiming ? "aim:"+r.Output : r.Output).Distinct().ToArray();
    }
    public bool Press(string id,bool pressed) {
        int vk=VirtualKey(id);
        if(vk!=0x75 && !keys.Contains(Modifier(vk))) return false;
        if(pressed) {
            if(!down.Add(vk)) return true;
            if(vk==0x75) Driving=!Driving;
        } else down.Remove(vk);
        if(Held(0x12)) { if(!partner.IsRunning) partner.Start(); } else partner.Reset();
        return true;
    }
    public PadState Reading(double lookX,double lookY) {
        var pad=new PadState { RX=lookX,RY=lookY };
        foreach(var rule in rules) {
            if(!Applies(rule) || !Held(rule.Key)) continue;
            switch(rule.Output) {
                case "LX-": pad.LX--; break; case "LX+": pad.LX++; break;
                case "LY-": pad.LY--; break; case "LY+": pad.LY++; break;
                case "RX-": pad.RX--; break; case "RX+": pad.RX++; break;
                case "RY-": pad.RY--; break; case "RY+": pad.RY++; break;
                case "LT": pad.LT=1; break; case "RT": pad.RT=1; break;
                case "PartnerRB": if(!WaitingForPartner) pad.Buttons|=rule.Button; break;
                default: pad.Buttons|=rule.Button; break;
            }
        }
        var length=Math.Max(1,Math.Sqrt(pad.LX*pad.LX+pad.LY*pad.LY));
        pad.LX/=length; pad.LY/=length;
        pad.RX=Math.Max(-1,Math.Min(1,pad.RX)); pad.RY=Math.Max(-1,Math.Min(1,pad.RY));
        return pad;
    }
}
