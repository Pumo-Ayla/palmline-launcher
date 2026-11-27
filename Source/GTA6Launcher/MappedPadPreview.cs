using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

// Consumes input routed to the launcher preview only.
internal sealed class MappedPadPreview
{
    readonly Action<PadState> render;
    readonly HashSet<string> held=new HashSet<string>();
    readonly DispatcherTimer decay=new DispatcherTimer(DispatcherPriority.Input);
    readonly Stopwatch motion=new Stopwatch();
    string[] bindings;
    GtaViPreset preset;
    Point? lastPoint;
    double lookX,lookY;
    string wheel;
    long wheelUntil;
    public bool Enabled { get { return bindings!=null || preset!=null; } }
    public bool IsPreset => preset!=null;
    public bool Driving { get { return preset!=null && preset.Driving; } }
    public event Action LayerChanged;

    public MappedPadPreview(Action<PadState> render) {
        this.render=render;
        decay.Interval=TimeSpan.FromMilliseconds(16);
        decay.Tick+=delegate { RefreshTransient(); };
    }
    public void Use(string[] profile) {
        Clear();
        bindings=profile; preset=null;
    }
    public void UseGtaVi() { Use(null); preset=new GtaViPreset(); }
    public void Clear() {
        decay.Stop(); held.Clear(); lastPoint=null; lookX=lookY=0; wheel=null; motion.Reset();
        bool wasAiming=preset!=null && preset.Aiming;
        if(preset!=null) preset.Clear();
        if(wasAiming) LayerChanged?.Invoke();
        if(Enabled) render(new PadState());
    }
    public static bool Walking(string id) {
        return id=="Key.W" || id=="Key.A" || id=="Key.S" || id=="Key.D";
    }
    public string[] Targets(string id) {
        if(!Enabled) return Array.Empty<string>();
        if(id=="Mouse.Move") return new[]{"Look"};
        if(preset!=null) return preset.Targets(id);
        switch(id) {
            case "Key.W": return new[]{"LY+"};
            case "Key.S": return new[]{"LY-"};
            case "Key.A": return new[]{"LX-"};
            case "Key.D": return new[]{"LX+"};
        }
        var outputs=new List<string>();
        for(int i=0;i<bindings.Length;i++)
            if(bindings[i]==id) outputs.Add(MappingStep.All[i].Id);
        return outputs.ToArray();
    }
    public bool Press(string id,bool down) {
        if(preset!=null) {
            bool wasDriving=preset.Driving, wasAiming=preset.Aiming;
            if(!preset.Press(id,down)) return false;
            if(preset.Driving!=wasDriving || preset.Aiming!=wasAiming) LayerChanged?.Invoke();
            Update();
            if(preset.WaitingForPartner) decay.Start();
            return true;
        }
        if(!Enabled || (!Walking(id) && Array.IndexOf(bindings,id)<0)) return false;
        if(down ? held.Add(id) : held.Remove(id)) Update();
        return true;
    }
    public bool Scroll(string id) {
        if(bindings==null || Array.IndexOf(bindings,id)<0) return false;
        wheel=id; wheelUntil=Stopwatch.GetTimestamp()+Stopwatch.Frequency/10;
        Update(); decay.Start(); return true;
    }
    public void Move(Point point) {
        if(!Enabled) return;
        var previous=lastPoint; lastPoint=point;
        if(!previous.HasValue) return;
        Vector delta=point-previous.Value;
        if(delta.LengthSquared<.01) return;
        double elapsed=motion.IsRunning ? Math.Min(50,Math.Max(8,motion.Elapsed.TotalMilliseconds)) : 16;
        double gain=1/(elapsed*.85);
        var direction=new Vector(delta.X*gain,-delta.Y*gain);
        if(direction.LengthSquared>1) direction.Normalize();
        lookX=direction.X; lookY=direction.Y;
        motion.Restart(); Update(); decay.Start();
    }
    public void LeaveSurface() {
        lastPoint=null;
        if(!Enabled) return;
        held.RemoveWhere(id=>id.StartsWith("Mouse.",StringComparison.Ordinal));
        if(preset!=null) {
            bool wasAiming=preset.Aiming;
            preset.Press("Mouse.Left",false); preset.Press("Mouse.Right",false); preset.Press("Mouse.Middle",false);
            if(wasAiming) LayerChanged?.Invoke();
        }
        lookX=lookY=0; motion.Reset(); Update();
    }
    void RefreshTransient() {
        if(wheel!=null && Stopwatch.GetTimestamp()>=wheelUntil) wheel=null;
        if(motion.ElapsedMilliseconds>=145) { motion.Reset(); lookX=lookY=0; }
        Update();
        if(wheel==null && !motion.IsRunning && (preset==null || !preset.WaitingForPartner)) decay.Stop();
    }
    void Update() {
        double fade=motion.IsRunning ? Math.Min(1,Math.Max(0,(145-motion.Elapsed.TotalMilliseconds)/95)) : 0;
        if(preset!=null) { render(preset.Reading(lookX*fade,lookY*fade)); return; }
        var pad=new PadState();
        for(int i=0;i<bindings.Length;i++) {
            if(!held.Contains(bindings[i]) && bindings[i]!=wheel) continue;
            var demo=MappingStep.All[i].Demo;
            pad.Buttons|=demo.Buttons; pad.LT+=demo.LT; pad.RT+=demo.RT;
        }
        var walk=new Vector((held.Contains("Key.D") ? 1 : 0)-(held.Contains("Key.A") ? 1 : 0),
                            (held.Contains("Key.W") ? 1 : 0)-(held.Contains("Key.S") ? 1 : 0));
        if(walk.LengthSquared>1) walk.Normalize();
        pad.LX=walk.X; pad.LY=walk.Y; pad.RX=lookX*fade; pad.RY=lookY*fade;
        render(pad);
    }
}
