using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Xml.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed partial class AchievementToast {
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    const double Lifetime=5.5, ExitDuration=.38, CloseGapDuration=.22;
    static readonly Dictionary<string,Bitmap> artwork=new Dictionary<string,Bitmap>();
    static readonly Dictionary<string,AchievementJournal.Entry> catalog=AchievementJournal.Catalog.ToDictionary(e=>e.Id);
    static readonly List<GCHandle> fontData=new List<GCHandle>();
    int soundedPercent=-1;
    static bool ready;
    static Stream Resource(string name) {
        return System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Launcher;component/Assets/"+name)).Stream;
    }
    static Bitmap LoadBitmap(string name) {
        using(var stream=Resource(name))
        using(var source=new Bitmap(stream)) return new Bitmap(source);
    }
    static void LoadFont(PrivateFontCollection fonts,string name) {
        using(var stream=Resource("Fonts/"+name))
        using(var data=new MemoryStream()) {
            stream.CopyTo(data);
            byte[] bytes=data.ToArray();
            var pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);
            fontData.Add(pin);
            fonts.AddMemoryFont(pin.AddrOfPinnedObject(),bytes.Length);
        }
    }
    static void EnsureFonts() {
        if(chineseHeading.Families.Length==0) LoadFont(chineseHeading,"MiSans-Semibold.ttf");
        if(chineseFont.Families.Length==0) LoadFont(chineseFont,"MiSans-Regular.ttf");
    }
    internal static void Preload() {
        if(ready) return;
        EnsureFonts();
        using(var stream=typeof(AchievementToast).Assembly.GetManifestResourceStream("AchievementVI.png"))
        using(var source=new Bitmap(stream)) logo=new Bitmap(source);
        logo.MakeTransparent(logo.GetPixel(0,0));
        foreach(var id in catalog.Keys) artwork[id]=LoadBitmap("Achievements/"+id+".png");
        ready=true;
    }
    Image icon, lockedIcon;
    static Bitmap Grayscale(Image source) {
        var result=new Bitmap(source.Width,source.Height,PixelFormat.Format32bppArgb);
        using(var graphics=Graphics.FromImage(result))
        using(var attributes=new ImageAttributes()) {
            attributes.SetColorMatrix(new ColorMatrix(new[] {
                new[]{.2126f,.2126f,.2126f,0f,0f},
                new[]{.7152f,.7152f,.7152f,0f,0f},
                new[]{.0722f,.0722f,.0722f,0f,0f},
                new[]{0f,0f,0f,1f,0f},
                new[]{0f,0f,0f,0f,1f}
            }));
            graphics.DrawImage(source,new Rectangle(0,0,result.Width,result.Height),
                0,0,source.Width,source.Height,GraphicsUnit.Pixel,attributes);
        }
        return result;
    }
    static Bitmap logo;
    Color earnedAccent;
    Color earnedLight;
    static double Ease(double x) { x=Math.Max(0,Math.Min(1,x)); return x*x*(3-2*x); }
    static GraphicsPath Round(float x,float y,float w,float h,float r) {
        var p=new GraphicsPath(); float d=r*2;
        p.AddArc(x,y,d,d,180,90); p.AddArc(x+w-d,y,d,d,270,90);
        p.AddArc(x+w-d,y+h-d,d,d,0,90); p.AddArc(x,y+h-d,d,d,90,90); p.CloseFigure(); return p;
    }
    const int CardHeight = 66, Radius = 9;
    static readonly string[] languages={"zh-CN","en-US","zh-TW","ja-JP","ko-KR"};
    static readonly string[] fonts={"Microsoft YaHei UI","Segoe UI","Microsoft JhengHei UI","Yu Gothic UI","Malgun Gothic"};
    string currentId;
    const int TextLeft=69, TitleSize=20, DetailSize=13, TextGap=5;
    static readonly PrivateFontCollection chineseFont=new PrivateFontCollection();
    static readonly PrivateFontCollection chineseHeading=new PrivateFontCollection();
    GraphicsPath titleLine, detailLine;
    string caption, achievement, description, face;
    int CardWidth;
    int maxCardWidth;
    int ArtWidth => (int)Math.Round(icon.Width*CardHeight/(double)icon.Height);
    const int ArtBlendInset=16;

    static GraphicsPath ShapeLine(string text,FontFamily family,int size,FontStyle style,float width,bool wrap) {
        var line=new GraphicsPath();
        if(text.Length==0) return line;
        using(var format=StringFormat.GenericTypographic) {
            if(wrap) line.AddString(text,family,(int)style,size,new RectangleF(0,0,width,1000),format);
            else line.AddString(text,family,(int)style,size,PointF.Empty,format);
        }
        return line;
    }
    static void PlaceLine(GraphicsPath line,RectangleF bounds,float top) {
        using(var move=new Matrix()) {
            move.Translate(TextLeft-bounds.Left,top-bounds.Top);
            line.Transform(move);
        }
    }
    int percent=100;

    void SelectLanguage(string code) {
        int col=Math.Max(0,Array.IndexOf(languages,code));
        face=fonts[col];
        description=catalog[currentId].Description;
        caption=Ui.Text("已解锁");
        achievement=catalog[currentId].Name;
        if(titleLine!=null) { titleLine.Dispose(); detailLine.Dispose(); }
        maxCardWidth=col==0 || col==2 ? 346 : 476;
        int trailingWidth=ArtWidth-ArtBlendInset+6;
        float available=maxCardWidth-TextLeft-trailingWidth;
        using(var family=col==0 ? chineseFont.Families[0] : new FontFamily(face)) {
            using(var headingFace=col==0 ? chineseHeading.Families[0] : new FontFamily(face))
                titleLine=ShapeLine(achievement,headingFace,TitleSize,col==0 || !headingFace.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Regular : FontStyle.Bold,available,false);
            FitText(titleLine,available,TitleSize+2);
            detailLine=ShapeLine(description,family,DetailSize,FontStyle.Regular,available,true);
            FitText(detailLine,available,CardHeight-12-TextGap-titleLine.GetBounds().Height);
        }
        var heading=titleLine.GetBounds();
        var detail=detailLine.GetBounds();
        CardWidth=TextLeft+(int)Math.Ceiling(Math.Max(heading.Width,detail.Width))+trailingWidth;
        float top=(CardHeight-heading.Height-(detailLine.PointCount==0 ? 0 : TextGap+detail.Height))/2;
        PlaceLine(titleLine,heading,top);
        PlaceLine(detailLine,detail,top+heading.Height+TextGap);
    }
    static void FitText(GraphicsPath line,float width,float height) {
        var bounds=line.GetBounds();
        if(bounds.Width<=width && bounds.Height<=height) return;
        float factor=Math.Min(width/bounds.Width,height/bounds.Height);
        using(var transform=new Matrix()) {
            transform.Scale(factor,factor);
            line.Transform(transform);
        }
    }
    void Card(Graphics g,float scale,double t) {
        g.ScaleTransform(scale,scale);
        g.SmoothingMode=SmoothingMode.AntiAlias;
        g.InterpolationMode=InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
        using(var shell=Round(.5f,.5f,CardWidth-1,CardHeight-1,Radius)) {
            var original=g.Save();
            g.SetClip(shell,CombineMode.Intersect);
            var plum=Color.FromArgb(46,31,50);
            using(var bg=new LinearGradientBrush(new Rectangle(0,0,CardWidth,CardHeight),
                    plum, Color.FromArgb(27,23,34),0f))
                g.FillRectangle(bg,0,0,CardWidth,CardHeight);

            // Align the full achievement illustration with the trailing edge.
            int artWidth=ArtWidth;
            int artLeft=CardWidth-artWidth;
            bool unlocked=percent==100;
            g.DrawImage(unlocked ? icon : lockedIcon,new Rectangle(artLeft,0,artWidth,CardHeight));
            using(var backing=new SolidBrush(Color.FromArgb(30,24,36)))
                g.FillRectangle(backing,artLeft,0,ArtBlendInset,CardHeight);
            using(var fade=new LinearGradientBrush(new Rectangle(artLeft+ArtBlendInset-1,0,20,CardHeight),
                    Color.FromArgb(30,24,36), Color.FromArgb(0,30,24,36),0f))
                g.FillRectangle(fade,artLeft+ArtBlendInset-1,0,20,CardHeight);
            var logoBox=new Rectangle(12,13,47,40);
            if(unlocked) g.DrawImage(logo,logoBox);
            else {
                using(var tint=new ImageAttributes()) {
                    var dim=new ColorMatrix { Matrix00=.76f, Matrix11=.76f, Matrix22=.76f };
                    tint.SetColorMatrix(dim);
                    g.DrawImage(logo,logoBox,0,0,logo.Width,logo.Height,GraphicsUnit.Pixel,tint);
                }
            }
            float lift=(float)(4*(1-Ease((t-.10)/.32)));
            var textState=g.Save();
            g.TranslateTransform(0,lift);
            using(var paper=new SolidBrush(Color.FromArgb(255,247,237)))
            using(var secondary=new SolidBrush(Color.FromArgb(202,186,201))) {
                g.FillPath(paper,titleLine);
                if(description.Length!=0) g.FillPath(secondary,detailLine);
            }
            g.Restore(textState);
            string status=unlocked ? caption : percent+"%";
            using(var label=new Font(face,12,FontStyle.Regular,GraphicsUnit.Pixel))
            using(var pill=new SolidBrush(Color.FromArgb(228,35,27,42)))
            using(var format=new StringFormat { Alignment=StringAlignment.Center,
                    LineAlignment=StringAlignment.Center, FormatFlags=StringFormatFlags.NoWrap }) {
                float width=(float)Math.Ceiling(g.MeasureString(status,label).Width)+13;
                float right=CardWidth-6;
                float extension=unlocked ? 11*(float)Ease((t-1.08)/.33) : 0;
                var box=new RectangleF(right-width-extension,CardHeight-22,width+extension,18);
                using(var shape=Round(box.X,box.Y,box.Width,box.Height,box.Height/2f))
                    g.FillPath(pill,shape);
                var words=new RectangleF(box.X,box.Y,width,box.Height);
                g.DrawString(status,label,Brushes.White,words,format);
                if(unlocked) DrawCheck(g,right-14,box.Top+9,t);
            }
            g.Restore(original);
            if(unlocked) DrawEarnedEdge(g,t);
            else using(var edge=new Pen(Color.FromArgb(122,94,119))) g.DrawPath(edge,shell);
        }
    }
    void DrawEarnedEdge(Graphics g,double t) {
        const float thickness=1.8f;
        const float inset=1.2f;
        float w=CardWidth-2*inset, h=CardHeight-2*inset, radius=Radius-inset;
        float perimeter=2*(w+h-4*radius)+(float)(2*Math.PI*radius);
        float offset=(float)(t/2.6%1)*perimeter;
        using(var contour=Round(inset,inset,w,h,radius))
        using(var pen=new Pen(earnedAccent,thickness)) {
            g.DrawPath(pen,contour);
            pen.DashCap=DashCap.Round;
            // Concentric dashes follow the same rounded path; shorter ones brighten its center.
            for(int layer=1;layer<=8;layer++) {
                float amount=layer/8f;
                float length=perimeter*(.36f-.31f*amount);
                pen.Color=Color.FromArgb(
                    (int)(earnedAccent.R+(earnedLight.R-earnedAccent.R)*amount),
                    (int)(earnedAccent.G+(earnedLight.G-earnedAccent.G)*amount),
                    (int)(earnedAccent.B+(earnedLight.B-earnedAccent.B)*amount));
                pen.DashPattern=new[]{length/thickness,(perimeter-length)/thickness};
                pen.DashOffset=(length*.5f-offset)/thickness;
                g.DrawPath(pen,contour);
            }
        }
    }
    void DrawCheck(Graphics g,float x,float y,double t) {
        // Trace the short downstroke before turning up along the longer stroke.
        float distance=(float)(Ease((t-1.30)/.23)*15);
        if(distance<=0) return;
        float down=Math.Min(1,distance/5);
        using(var path=new GraphicsPath())
        using(var shadow=new Pen(Color.FromArgb(175,29,21,34),3.8f))
        using(var ink=new Pen(earnedLight,2.2f)) {
            path.AddLine(x,y,x+3*down,y+3.5f*down);
            if(distance>5) {
                float up=(distance-5)/10;
                path.AddLine(x+3,y+3.5f,x+3+7.5f*up,y+3.5f-8*up);
            }
            shadow.StartCap=shadow.EndCap=ink.StartCap=ink.EndCap=LineCap.Round;
            shadow.LineJoin=ink.LineJoin=LineJoin.Round;
            g.DrawPath(shadow,path);
            g.DrawPath(ink,path);
        }
    }
    double born,inserted;
    long sequence;
    Toast window;
    struct Row {
        public AchievementToast Card;
        public Rectangle Bounds;
        public int Reveal;
        public byte Alpha;
    }
    static IEnumerable<Row> Rows(int w,int h,double now,int bottomInset=0) {
        int width=Math.Max(1,(int)Math.Round(w*.184));
        int margin=(int)Math.Round(h*.021);
        int bottom=h-margin-bottomInset;
        int gap=Math.Max(2,(int)Math.Round(h*.006));
        int right=w-(int)Math.Round(w*.013);
        foreach(var card in DisplayOrder()) {
            float scale=Math.Min(width/(float)card.maxCardWidth,h*.072f/CardHeight);
            int rowWidth=Math.Max(1,(int)Math.Round(card.CardWidth*scale));
            int height=Math.Max(1,(int)Math.Round(CardHeight*scale));
            double fraction=Ease((now-card.inserted)/Entrance);
            double elapsed=card==departing ? now-departureStart : -1;
            double slide=Ease(elapsed/ExitDuration);
            double keepSpace=1-Ease((elapsed-ExitDuration)/CloseGapDuration);
            int left=right-rowWidth+(int)Math.Round((w-right+rowWidth)*slide);
            // Reveal only the part that has entered the monitor's work area.
            int y=bottom-height+(int)Math.Round((height+margin+2)*(1-fraction));
            int reveal=Math.Max(0,Math.Min(height,h-y));
            double occupied=fraction*keepSpace;
            if(card==outgoing && !overflowAtTop) {
                occupied*=1-Ease((now-fadeStart)/FadeSeconds);
                reveal=Math.Min(reveal,(int)Math.Round(height*occupied));
                y=bottom-reveal;
            }
            yield return new Row { Card=card, Bounds=new Rectangle(left,y,rowWidth,height),Reveal=slide>=1 ? 0 : reveal, Alpha=Opacity(card,now) };
            bottom-=(int)Math.Round((height+gap)*occupied);
        }
    }
    sealed class Toast : NativeWindow, IDisposable {
        readonly AchievementToast card;
        public Toast(AchievementToast card) {
            this.card=card;
            // WS_POPUP, layered, transparent to mouse input, tool window, topmost, no activation.
            CreateHandle(new CreateParams { Style=unchecked((int)0x80000000),
                ExStyle=0x80000|0x20|0x80|0x8|0x08000000 });
        }
        [StructLayout(LayoutKind.Sequential)]
        struct BitmapHeader {
            public uint Size;
            public int Width,Height;
            public ushort Planes,BitCount;
            public uint Compression,SizeImage;
            public int XPelsPerMeter,YPelsPerMeter;
            public uint ColorsUsed,ColorsImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct Blend { public byte Operation,Flags,Opacity,AlphaFormat; }
        [DllImport("gdi32.dll",SetLastError=true)] static extern IntPtr CreateCompatibleDC(IntPtr source);
        [DllImport("gdi32.dll",SetLastError=true)] static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapHeader info,uint usage,out IntPtr bits,IntPtr section,uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr window,IntPtr screen,IntPtr destination,ref Size size,IntPtr source,ref Point origin,uint key,ref Blend blend,uint flags);
        [DllImport("user32.dll")] static extern IntPtr BeginDeferWindowPos(int count);
        [DllImport("user32.dll")] static extern IntPtr DeferWindowPos(IntPtr batch,IntPtr hwnd,IntPtr after,int x,int y,int w,int h,uint flags);
        [DllImport("user32.dll")] static extern bool EndDeferWindowPos(IntPtr batch);
        Bitmap surface;
        IntPtr dc,dib,original;
        void ReleaseSurface() {
            if(surface!=null) { surface.Dispose(); surface=null; }
            if(original!=IntPtr.Zero) SelectObject(dc,original);
            if(dib!=IntPtr.Zero) DeleteObject(dib);
            if(dc!=IntPtr.Zero) DeleteDC(dc);
            original=dib=dc=IntPtr.Zero;
        }
        void EnsureSurface(Size size) {
            if(surface!=null && surface.Size==size) return;
            ReleaseSurface();
            dc=CreateCompatibleDC(IntPtr.Zero);
            if(dc==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            var header=new BitmapHeader { Size=(uint)Marshal.SizeOf(typeof(BitmapHeader)),Width=size.Width,
                Height=-size.Height,Planes=1,BitCount=32 };
            dib=CreateDIBSection(dc,ref header,0,out var bits,IntPtr.Zero,0);
            if(dib==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            original=SelectObject(dc,dib);
            if(original==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            surface=new Bitmap(size.Width,size.Height,size.Width*4,PixelFormat.Format32bppPArgb,bits);
        }
        bool stationaryFrame;
        int lastPercent=-1;
        Size lastSize;
        byte lastAlpha;
        void Prepare(Row row,Size visibleSize,double now) {
            if(visibleSize.IsEmpty) return;
            bool stationary=(card.noticeKind.HasValue ? card.noticeKind!=LauncherNoticeKind.Preloading : card.percent<100) && now-card.born>=.42;
            if(stationary && stationaryFrame && lastPercent==card.percent && lastSize==visibleSize
                    && surface.Size==row.Bounds.Size && lastAlpha==row.Alpha) return;
            EnsureSurface(row.Bounds.Size);
            using(var graphics=Graphics.FromImage(surface)) {
                graphics.PageUnit=GraphicsUnit.Pixel;
                graphics.Clear(Color.Transparent);
                card.PaintRow(graphics,surface.Width/(float)card.CardWidth,now);
            }
            var origin=Point.Empty;
            var blend=new Blend { Opacity=row.Alpha,AlphaFormat=1 };
            if(!UpdateLayeredWindow(Handle,IntPtr.Zero,IntPtr.Zero,ref visibleSize,dc,ref origin,0,ref blend,2))
                throw new System.ComponentModel.Win32Exception();
            stationaryFrame=stationary;
            lastSize=visibleSize;
            lastPercent=card.percent;
            lastAlpha=row.Alpha;
        }
        public static bool Present(Rectangle area,Row[] rows,double now) {
            if(rows.Length==0 || area.IsEmpty) return false;
            var batch=BeginDeferWindowPos(rows.Length);
            foreach(var row in rows) {
                if(batch==IntPtr.Zero) return false;
                var r=row.Bounds;
                int width=Math.Min(r.Width,area.Width-r.X);
                var size=width>0 && row.Reveal>0 ? new Size(width,row.Reveal) : Size.Empty;
                if(row.Card.window==null) row.Card.window=new Toast(row.Card);
                row.Card.window.Prepare(row,size,now);
                uint flags=0x10u|(size.IsEmpty ? 0x80u : 0x40u); // NOACTIVATE, HIDE or SHOW
                batch=DeferWindowPos(batch,row.Card.window.Handle,new IntPtr(-1),area.Left+r.X,area.Top+r.Y,
                    Math.Max(1,size.Width),Math.Max(1,size.Height),flags);
            }
            return batch!=IntPtr.Zero && EndDeferWindowPos(batch);
        }
        public void Dispose() {
            DestroyHandle();
            ReleaseSurface();
        }
        protected override void WndProc(ref Message m) {
            if(m.Msg==0x84) { m.Result=new IntPtr(-1); return; }
            base.WndProc(ref m);
        }
    }
    static readonly List<AchievementToast> visible=new List<AchievementToast>();
    const double FadeSeconds=.26;
    static AchievementToast outgoing,departing;
    static double departureStart;
    static double fadeStart;
    static bool overflowAtTop;
    static readonly List<AchievementToast> physical=new List<AchievementToast>();
    static IEnumerable<AchievementToast> DisplayOrder() { return physical; }
    static readonly Dictionary<string,int> arrivals=new Dictionary<string,int>();
    const double Entrance=.48;
    static double lastEntrance=double.NegativeInfinity;
    static byte Opacity(AchievementToast card,double now) {
        return card==outgoing ? (byte)Math.Round(255*Math.Max(0,1-(now-fadeStart)/FadeSeconds)) : (byte)255;
    }
    void PaintRow(Graphics g,float scale,double now) {
        if(noticeKind.HasValue) PaintNotice(g,scale,now-born);
        else Card(g,scale,now-born);
    }
    static long serial;
    static int Grade(string id) {
        switch(id) {
            case "1000": return 4;
            case "6": case "13": return 3;
            case "14": case "16": case "24": case "29": case "30": case "39": return 2;
            default: return 1;
        }
    }
    AchievementToast(string id,int progress,double now) {
        currentId=id; percent=progress; born=inserted=now; sequence=++serial;
        icon=artwork[id];
        lockedIcon=Grayscale(icon);
        int metal,hi; GtaViTrophyPalette.ForAchievement(id,out metal,out hi);
        earnedAccent=Color.FromArgb(255,Color.FromArgb(metal));
        earnedLight=Color.FromArgb(255,Color.FromArgb(hi));
        SelectLanguage(Ui.Language);
    }
    static void Remove(AchievementToast item) {
        visible.Remove(item);
        physical.Remove(item);
        if(item==departing) departing=null;
        if(item==outgoing) outgoing=null;
        if(item.window!=null) item.window.Dispose();
        item.titleLine.Dispose(); item.detailLine.Dispose(); item.lockedIcon.Dispose();
    }
    static IEnumerable<AchievementToast> Ranked() {
        return visible.OrderByDescending(c=>c.percent==100)
            .ThenByDescending(c=>c.percent==100 ? Grade(c.currentId) : c.percent)
            .ThenByDescending(c=>c.sequence);
    }
    static void Push(string id,int progress,double now) {
        int previous;
        arrivals[id]=arrivals.TryGetValue(id,out previous) ? Math.Max(progress,previous) : progress;
    }
    static void Admit(string id,int progress,double now) {
        if(outgoing!=null && outgoing.currentId==id) {
            if(progress<=outgoing.percent) return;
            Remove(outgoing);
        }
        var card=visible.FirstOrDefault(c=>c.currentId==id);
        if(card==null) {
            card=new AchievementToast(id,progress,now);
            visible.Insert(0,card); physical.Insert(0,card);
        }
        else {
            if(progress<=card.percent) return;
            if(card==departing) departing=null;
            card.percent=progress; card.born=now;
        }
        if(visible.Count>3) {
            if(departing!=null) Remove(departing);
        }
        if(visible.Count>3) {
            var lowest=Ranked().Last();
            if(lowest==card) { Remove(lowest); return; }
            else {
                if(outgoing!=null) Remove(outgoing);
                visible.Remove(lowest);
                overflowAtTop=physical.Last()==lowest;
                outgoing=lowest;
                fadeStart=now;
            }
        }
        lastEntrance=now;


    }
    static void Expire(double now) {
        if(outgoing!=null && now-fadeStart>=FadeSeconds) Remove(outgoing);
        if(departing!=null && now-departureStart>=ExitDuration+CloseGapDuration) Remove(departing);
        if(departing!=null || outgoing!=null) return;
        var top=visible.LastOrDefault();
        if(top!=null && now-top.born>=Lifetime) {
            departing=top;
            departureStart=now;
        }

    }
    static void PresentWithSound(Rectangle area,Row[] rows,double now,Action<LauncherNoticeKind> playSound) {
        if(!Toast.Present(area,rows,now)) return;
        foreach(var row in rows) {
            var card=row.Card;
            if(row.Reveal<=0 || row.Bounds.X>=area.Width || row.Alpha==0 || card.soundedPercent==card.percent) continue;
            card.soundedPercent=card.percent;
            playSound(card.noticeKind ?? (card.percent==100 ? LauncherNoticeKind.Unlocked : LauncherNoticeKind.Progress));
        }
    }
    internal sealed class Session : IDisposable {
        internal const string SystemName = "SR Achievement Event Monitoring System";
        readonly System.Windows.Threading.DispatcherTimer timer=new System.Windows.Threading.DispatcherTimer();
        readonly Stopwatch clock=new Stopwatch();
        readonly Dictionary<string,int> highest;
        readonly Action<string> log;
        readonly Action<LauncherNoticeKind> playSound;
        IntPtr gameWindow;
        int pid;
        public Session(string lab,string instance,Action<string> log,Action<LauncherNoticeKind> playSound) {
            this.log=log;
            this.playSound=playSound;
            Preload();
            highest=AchievementJournal.Read(lab,instance);
            lastEntrance=double.NegativeInfinity;
            timer.Interval=TimeSpan.FromMilliseconds(16);
            timer.Tick+=Tick;
        }
        public void Attach(int gamePid) {
            pid=gamePid;
            log(SystemName+" attached to game process "+gamePid+".");
            clock.Start();
            timer.Start();
        }
        public void Observe(string message) {
            if(!AchievementJournal.TryProgress(message,out string id,out int progress) || !catalog.ContainsKey(id)) return;
            if(!highest.TryGetValue(id,out int before)) before=0;
            if(progress<=before) return;
            highest[id]=progress;
            Push(id,progress,clock.Elapsed.TotalSeconds);
        }
        void Tick(object sender,EventArgs args) {
            try {
                double now=clock.Elapsed.TotalSeconds;
                Expire(now);
                if(arrivals.Count>0 && departing==null && now-lastEntrance>=Entrance) {
                    var next=arrivals.OrderByDescending(p=>p.Value==100)
                        .ThenByDescending(p=>p.Value==100 ? Grade(p.Key) : p.Value).First();
                    arrivals.Remove(next.Key); Admit(next.Key,next.Value,now);
                }
                if(physical.Count!=0) {
                    var active=GetForegroundWindow();
                    GetWindowThreadProcessId(active,out int owner);
                    if(owner==pid) gameWindow=active;
                    else {
                        GetWindowThreadProcessId(gameWindow,out int previous);
                        if(previous!=pid) {
                            try { using(var game=Process.GetProcessById(pid)) gameWindow=game.MainWindowHandle; }
                            catch(ArgumentException) { gameWindow=IntPtr.Zero; }
                            catch(InvalidOperationException) { gameWindow=IntPtr.Zero; }
                        }
                    }
                    // Desktop coordinates, layered bitmap dimensions and HWND bounds must all use physical pixels.
                    var previousDpi=SetThreadDpiAwarenessContext(new IntPtr(-4));
                    try {
                        var area=Backdrop.MonitorWorkArea(gameWindow);
                        if(!area.IsEmpty) PresentWithSound(area,Rows(area.Width,area.Height,now,area==noticeMonitor ? noticeInset : 0).ToArray(),now,playSound);
                    } finally {
                        if(previousDpi!=IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi);
                    }
                }
                timer.Interval=TimeSpan.FromMilliseconds(physical.Count==0 ? 80 : 16);
            } catch(Exception ex) { log(SystemName+" overlay: "+ex.Message); Dispose(); }
        }
        public void Dispose() {
            timer.Stop(); timer.Tick-=Tick;
            foreach(var c in physical.ToArray()) Remove(c);
            arrivals.Clear(); clock.Reset();
        }
    }
}
