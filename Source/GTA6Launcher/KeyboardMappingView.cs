using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

internal sealed class KeyboardMappingView
{
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyExW(uint code,uint mode,IntPtr layout);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]
    static extern int ToUnicodeEx(uint key,uint scan,byte[] state,StringBuilder text,int size,uint flags,IntPtr layout);

    readonly Viewbox host;
    readonly MappedPadPreview preview;
    readonly Action<PadState> highlight;
    readonly StackPanel board=new StackPanel();
    readonly WrapPanel extra=new WrapPanel { MaxWidth=790 };
    readonly TextBlock layoutName=new TextBlock(), layerName=new TextBlock();
    readonly List<Cap> caps=new List<Cap>();
    readonly byte[] neutral=new byte[256];
    IntPtr layout;
    string language;
    Cap hovered;
    bool built;

    static SolidColorBrush Ink(byte r,byte g,byte b) {
        var brush=new SolidColorBrush(Color.FromRgb(r,g,b)); brush.Freeze(); return brush;
    }
    static readonly Brush shell=Ink(32,32,39), rim=Ink(74,73,85), plain=Ink(47,47,56),
        mapped=Ink(49,69,66), accent=Ink(157,230,197), selected=Ink(66,100,87), muted=Ink(180,177,192);
    sealed class Cap {
        public string Id, Legend;
        public Border Face;
        public TextBlock Text;
        public string[] Targets;
        public bool Down;
    }

    public KeyboardMappingView(Window window,Viewbox host,MappedPadPreview preview,Action<PadState> highlight) {
        this.host=host; this.preview=preview; this.highlight=highlight;
        // WM_INPUTLANGCHANGE also covers changing between two layouts of the same language.
        HwndSource source=null;
        HwndSourceHook hook=delegate(IntPtr hwnd,int msg,IntPtr w,IntPtr l,ref bool handled) {
            if(msg==0x51 && host.IsVisible) host.Dispatcher.BeginInvoke(new Action(()=>Refresh()));
            return IntPtr.Zero;
        };
        Action attach=()=>{ source=PresentationSource.FromVisual(window) as HwndSource; source?.AddHook(hook); };
        if(PresentationSource.FromVisual(window)!=null) attach(); else window.SourceInitialized+=delegate { attach(); };
        window.Closed+=delegate { source?.RemoveHook(hook); };
    }
    public void Refresh(IEnumerable<string> custom=null) {
        var current=GetKeyboardLayout(0);
        if(!built || layout!=current || language!=Ui.Language) {
            ClearFeedback(); built=true; layout=current; language=Ui.Language; Build();
        }
        if(custom!=null) extraInputs=custom.Where(id=>id!=null).Distinct().ToArray();
        RefreshExtras();
        layerName.Text=preview.IsPreset ? Ui.Text(preview.Driving ? "驾驶" : "步行")+"  ·  F6" : "";
        UpdateCaps();
    }
    string[] extraInputs=Array.Empty<string>();
    public void ContextChanged() {
        if(!built) return;
        layerName.Text=preview.IsPreset ? Ui.Text(preview.Driving ? "驾驶" : "步行")+"  ·  F6" : "";
        UpdateCaps();
    }
    void UpdateCaps() {
        foreach(var cap in caps) {
            var targets=preview.Targets(cap.Id);
            if(cap.Targets==null || !cap.Targets.SequenceEqual(targets)) {
                cap.Targets=targets;
                var tip=cap.Face.ToolTip as ToolTip;
                if(targets.Length==0) {
                    if(tip!=null) tip.IsOpen=false;
                    cap.Face.ToolTip=null;
                    AutomationProperties.SetHelpText(cap.Face,"");
                } else {
                    if(tip==null) cap.Face.ToolTip=tip=new ToolTip { Padding=new Thickness(9,6,9,6) };
                    string detail=cap.Legend+"  →  "+string.Join("  ·  ",targets.Select(TargetName));
                    AutomationProperties.SetName(tip,detail);
                    AutomationProperties.SetHelpText(cap.Face,detail);
                    if(tip.IsOpen) tip.Content=MappingHint.Create(cap.Id,cap.Text.Text,targets);
                }
            }
            Paint(cap);
        }
        SendHover();
    }
    static string TargetName(string id) {
        if(id.StartsWith("aim:",StringComparison.Ordinal)) return Ui.Format("瞄准时：{0}",TargetName(id.Substring(4)));
        switch(id) {
            case "LX-":return Ui.Text("左摇杆")+" ←";
            case "LX+":return Ui.Text("左摇杆")+" →";
            case "LY+":return Ui.Text("左摇杆")+" ↑";
            case "LY-":return Ui.Text("左摇杆")+" ↓";
            case "Look":return Ui.Text("右摇杆");
            case "Layer":return Ui.Text("切换步行与驾驶");
            case "PartnerRB":return Ui.Text("长按触发 RB");
        }
        var step=Array.Find(MappingStep.All,s=>s.Id==id);
        if(step==null) return id;
        return step.Shoulder ? step.Id+" ("+Ui.Text(step.Label)+")" : Ui.Text(step.Label);
    }
    void SendHover() {
        var pad=new PadState();
        foreach(string target in hovered?.Targets ?? Array.Empty<string>()) {
            string id=target.StartsWith("aim:",StringComparison.Ordinal) ? target.Substring(4) : target;
            switch(id) {
                case "LX-":pad.LX=-1;break;
                case "LX+":pad.LX=1;break;
                case "LY+":pad.LY=1;break;
                case "LY-":pad.LY=-1;break;
                case "Look":pad.RX=.8;break;
                default:
                    var step=Array.Find(MappingStep.All,s=>s.Id==(id=="PartnerRB" ? "RB" : id));
                    if(step!=null) { pad.Buttons|=step.Demo.Buttons; pad.LT+=step.Demo.LT; pad.RT+=step.Demo.RT; }
                    break;
            }
        }
        highlight(pad);
    }
    void Paint(Cap cap) {
        bool assigned=cap.Targets!=null && cap.Targets.Length!=0;
        bool lit=cap.Down || (cap==hovered && assigned);
        cap.Face.Background=lit ? selected : assigned ? mapped : plain;
        cap.Face.BorderBrush=lit ? accent : rim;
        cap.Text.Foreground=assigned ? accent : muted;
    }
    public void Press(string id,bool down) {
        foreach(var cap in caps)
            if(cap.Id==id && cap.Down!=down) { cap.Down=down; Paint(cap); }
    }
    public void ClearFeedback() {
        hovered=null;
        foreach(var cap in caps) { cap.Down=false; Paint(cap); }
        highlight(new PadState());
    }
    Border KeyCap(string id,string text,double width,double height,string legend=null) {
        var label=new TextBlock { Text=text, FontSize=width<34 && text.Length>=3 ? 9.5 : 12.5, FontWeight=FontWeights.SemiBold,
            HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center };
        var face=new Border { Width=width,Height=height,CornerRadius=new CornerRadius(5),BorderThickness=new Thickness(1),Child=label };
        var cap=new Cap { Id=id,Legend=legend ?? MappingHint.InputName(id,text),Face=face,Text=label };
        caps.Add(cap); Paint(cap);
        AutomationProperties.SetName(face,cap.Legend);
        ToolTipService.SetInitialShowDelay(face,110);
        ToolTipService.SetBetweenShowDelay(face,40);
        face.ToolTipOpening+=delegate {
            if(face.ToolTip is ToolTip tip) tip.Content=MappingHint.Create(cap.Id,cap.Text.Text,cap.Targets);
        };
        face.MouseEnter+=delegate { hovered=cap; Paint(cap); SendHover(); };
        face.MouseLeave+=delegate { if(hovered==cap) { hovered=null; Paint(cap); SendHover(); } };
        return face;
    }
    static Border Case(UIElement child) => new Border { Background=shell,BorderBrush=rim,BorderThickness=new Thickness(1),
        CornerRadius=new CornerRadius(12),Padding=new Thickness(8),Child=child };
    static void Position(Canvas canvas,UIElement element,double x,double y) {
        Canvas.SetLeft(element,x); Canvas.SetTop(element,y); canvas.Children.Add(element);
    }
    void Build() {
        hovered=null; caps.Clear(); board.Children.Clear(); extra.Children.Clear();
        var devices=new StackPanel { Orientation=Orientation.Horizontal };
        var keys=new StackPanel();
        // The header is reparented on input-language changes.
        Detach(layoutName); Detach(layerName); Detach(extra);
        var header=new DockPanel { Margin=new Thickness(3,0,3,7) };
        layerName.Foreground=accent; DockPanel.SetDock(layerName,Dock.Right); header.Children.Add(layerName);
        layoutName.Foreground=muted;
        layoutName.Text=System.Windows.Forms.InputLanguage.CurrentInputLanguage.LayoutName;
        header.Children.Add(layoutName); keys.Children.Add(header);
        keys.Children.Add(Case(BuildKeyboard())); keys.Children.Add(extra); devices.Children.Add(keys);
        var mouse=new StackPanel { Margin=new Thickness(14,0,0,0) };
        var caption=new TextBlock { Foreground=muted,Margin=new Thickness(3,0,0,7) };
        caption.SetResourceReference(TextBlock.TextProperty,"ui.鼠标");
        mouse.Children.Add(caption); mouse.Children.Add(Case(BuildMouse())); devices.Children.Add(mouse);
        board.Children.Add(devices); host.Child=board;
    }
    static void Detach(FrameworkElement element) { (element.Parent as Panel)?.Children.Remove(element); }

    Canvas BuildKeyboard() {
        const double unit=32, rowHeight=30;
        var canvas=new Canvas { Width=22.6*unit,Height=6*rowHeight+3 };
        void Put(Key key,string text,double x,int row,double width=1,double height=1) {
            Position(canvas,KeyCap("Key."+key,text,unit*width-3,rowHeight*height-3),x*unit,row*rowHeight+(row>0 ? 4 : 0));
        }
        string Character(uint vk,int scan) {
            var chars=new StringBuilder(8);
            int count=ToUnicodeEx(vk,(uint)scan,neutral,chars,chars.Capacity,4,layout);
            return count!=0 && chars.Length>0 && !char.IsControl(chars[0])
                ? chars.ToString(0,Math.Min(Math.Abs(count),chars.Length)).ToUpperInvariant() : null;
        }
        bool Additional(int scan,int standard) {
            uint vk=MapVirtualKeyExW((uint)scan,3,layout);
            uint other=MapVirtualKeyExW((uint)standard,3,layout);
            if(vk==0 || vk==0xff || vk==other) return false;
            string glyph=Character(vk,scan);
            return glyph!=null && glyph!=Character(other,standard);
        }
        void Printable(int scan,double x,int row,double width=1) {
            uint vk=MapVirtualKeyExW((uint)scan,3,layout);
            Key key=KeyInterop.KeyFromVirtualKey((int)vk);
            if(key!=Key.None && vk!=0xff) Put(key,Character(vk,scan) ?? key.ToString(),x,row,width);
        }
        Put(Key.Escape,"Esc",0,0);
        for(int i=0;i<12;i++) Put(Key.F1+i,"F"+(i+1),2+i+i/4*.5,0);
        Printable(0x29,0,1);
        for(int i=0;i<12;i++) Printable(0x02+i,1+i,1);
        bool extraTop=Additional(0x7d,0x2b), extraBottom=Additional(0x73,0x2b);
        if(extraTop) Printable(0x7d,13,1);
        Put(Key.Back,"⌫",extraTop ? 14 : 13,1,extraTop ? 1 : 2);
        Put(Key.Tab,"Tab",0,2,1.5);
        for(int i=0;i<12;i++) Printable(0x10+i,1.5+i,2);
        bool iso=Additional(0x56,0x2b);
        if(iso) Printable(0x2B,12.75,3); else Printable(0x2B,13.5,2,1.5);
        Put(Key.CapsLock,"Caps",0,3,1.75);
        for(int i=0;i<11;i++) Printable(0x1E+i,1.75+i,3);
        if(iso) Put(Key.Return,"↵",13.75,2,1.25,2); else Put(Key.Return,"↵",12.75,3,2.25);
        Put(Key.LeftShift,"Shift",0,4,iso ? 1.25 : 2.25);
        if(iso) Printable(0x56,1.25,4);
        for(int i=0;i<10;i++) Printable(0x2C+i,2.25+i,4);
        if(extraBottom) Printable(0x73,12.25,4);
        Put(Key.RightShift,"Shift",extraBottom ? 13.25 : 12.25,4,extraBottom ? 1.75 : 2.75);
        Put(Key.LeftCtrl,"Ctrl",0,5,1.25); Put(Key.LWin,"Win",1.25,5,1.25);
        Put(Key.LeftAlt,"Alt",2.5,5,1.25); Put(Key.Space,"␣",3.75,5,6.25);
        Put(Key.RightAlt,"Alt",10,5,1.25); Put(Key.RWin,"Win",11.25,5,1.25);
        Put(Key.Apps,"☰",12.5,5,1.25); Put(Key.RightCtrl,"Ctrl",13.75,5,1.25);
        const double nav=15.3,num=18.6;
        Put(Key.PrintScreen,"PrtSc",nav,0); Put(Key.Scroll,"ScrLk",nav+1,0); Put(Key.Pause,"Pause",nav+2,0);
        Put(Key.Insert,"Ins",nav,1); Put(Key.Home,"Home",nav+1,1); Put(Key.PageUp,"PgUp",nav+2,1);
        Put(Key.Delete,"Del",nav,2); Put(Key.End,"End",nav+1,2); Put(Key.PageDown,"PgDn",nav+2,2);
        Put(Key.Up,"↑",nav+1,4); Put(Key.Left,"←",nav,5); Put(Key.Down,"↓",nav+1,5); Put(Key.Right,"→",nav+2,5);
        Put(Key.NumLock,"Num",num,1); Put(Key.Divide,"/",num+1,1); Put(Key.Multiply,"×",num+2,1); Put(Key.Subtract,"−",num+3,1);
        for(int i=0;i<9;i++) Put(Key.NumPad1+i,(i+1).ToString(),num+i%3,4-i/3);
        Put(Key.Add,"+",num+3,2,1,2); Put(Key.Return,"↵",num+3,4,1,2);
        Put(Key.NumPad0,"0",num,5,2); Put(Key.Decimal,".",num+2,5);
        return canvas;
    }
    Canvas BuildMouse() {
        var canvas=new Canvas { Width=100,Height=183 };
        void Put(string id,string text,string name,double x,double y,double width,double height,CornerRadius corner) {
            var face=KeyCap(id,text,width,height,Ui.Text(name)); face.CornerRadius=corner;
            Position(canvas,face,x,y);
        }
        Put("Mouse.Left","L","鼠标左键",12,12,43,59,new CornerRadius(25,5,5,5));
        Put("Mouse.Right","R","鼠标右键",57,12,39,59,new CornerRadius(5,25,5,5));
        Put("Mouse.Move","↔  ↕","鼠标移动",12,74,84,95,new CornerRadius(5,5,35,35));
        Put("Mouse.XButton1","4","鼠标侧键 1",0,83,19,27,new CornerRadius(5));
        Put("Mouse.XButton2","5","鼠标侧键 2",0,112,19,27,new CornerRadius(5));
        Put("Wheel.Up","↑","滚轮上滑",45,18,21,18,new CornerRadius(7,7,2,2));
        Put("Mouse.Middle","•","鼠标中键",45,37,21,17,new CornerRadius(3));
        Put("Wheel.Down","↓","滚轮下滑",45,55,21,18,new CornerRadius(2,2,7,7));
        return canvas;
    }
    void RefreshExtras() {
        if(hovered?.Face.Parent==extra) hovered=null;
        caps.RemoveAll(c=>c.Face.Parent==extra);
        extra.Children.Clear();
        foreach(string id in extraInputs.Where(id=>caps.All(c=>c.Id!=id))) {
            string text=id.StartsWith("Key.") ? id.Substring(4) : id;
            var cap=KeyCap(id,text,Math.Max(38,text.Length*8+10),25);
            cap.Margin=new Thickness(2,4,2,0); extra.Children.Add(cap);
        }
    }
}
