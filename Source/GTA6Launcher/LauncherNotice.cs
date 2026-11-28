using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Threading;

sealed partial class AchievementToast {
    const int NoticeHeight=68, NoticeWidth=358;
    static readonly Dictionary<LauncherNoticeKind,string> noticeDetails=new Dictionary<LauncherNoticeKind,string>{
        {LauncherNoticeKind.Installed,"游戏已添加到游戏库"},
        {LauncherNoticeKind.Preloading,"正在准备着色器和渲染管线"},
        {LauncherNoticeKind.Ready,"现在可以启动游戏"},
        {LauncherNoticeKind.Started,"已显示第一帧画面"}
    };
    static readonly Dictionary<LauncherNoticeKind,Bitmap> noticeArt=new Dictionary<LauncherNoticeKind,Bitmap>();
    static Rectangle noticeMonitor;
    static int noticeInset;
    readonly LauncherNoticeKind? noticeKind;

    static bool IsControllerNotice(LauncherNoticeKind kind) {
        return kind==LauncherNoticeKind.ControllerConnected || kind==LauncherNoticeKind.ControllerDisconnected;
    }
    static LauncherNoticeKind NoticeArtwork(LauncherNoticeKind kind) {
        return IsControllerNotice(kind) ? LauncherNoticeKind.Preloading : kind;
    }
    AchievementToast(LauncherNoticeKind kind,double now,string deviceName) {
        noticeKind=kind; born=now; CardWidth=NoticeWidth;
        var artwork=NoticeArtwork(kind);
        if(!noticeArt.ContainsKey(artwork)) {
            using(var stream=typeof(AchievementToast).Assembly.GetManifestResourceStream("LauncherNotice."+artwork+".png"))
            using(var image=new Bitmap(stream)) noticeArt.Add(artwork,new Bitmap(image));
        }
        int lang=Math.Max(0,Array.IndexOf(languages,Ui.Language));
        if(lang==0) EnsureFonts();
        using(var family=lang==0 ? chineseFont.Families[0] : new FontFamily(fonts[lang]))
        using(var heading=lang==0 ? chineseHeading.Families[0] : new FontFamily(fonts[lang])) {
            var style=lang!=0 && heading.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
            string detailText=IsControllerNotice(kind) ? deviceName ?? "未知手柄" : noticeDetails[kind];
            titleLine=ShapeLine(Ui.Text(NotificationSoundSettings.EventLabels[kind]),heading,TitleSize,style,219,false);
            detailLine=ShapeLine(Ui.Text(detailText),family,DetailSize,FontStyle.Regular,219,false);
        }
        FitText(titleLine,219,24);
        FitText(detailLine,219,16);
        var title=titleLine.GetBounds(); var detail=detailLine.GetBounds();
        float top=(NoticeHeight-title.Height-TextGap-detail.Height)/2;
        PlaceLine(titleLine,title,top); PlaceLine(detailLine,detail,top+title.Height+TextGap);
    }

    void PaintNotice(Graphics g,float scale,double age) {
        g.ScaleTransform(scale,scale);
        g.InterpolationMode=InterpolationMode.HighQualityBicubic;
        g.SmoothingMode=SmoothingMode.AntiAlias;
        var kind=noticeKind.Value;
        g.DrawImage(noticeArt[NoticeArtwork(kind)],new Rectangle(0,0,CardWidth,NoticeHeight));
        using(var title=new SolidBrush(Color.FromArgb(255,247,237)))
        using(var detail=new SolidBrush(Color.FromArgb(213,203,216))) {
            g.FillPath(title,titleLine); g.FillPath(detail,detailLine);
        }
        if(IsControllerNotice(kind)) PaintControllerBadge(g,kind==LauncherNoticeKind.ControllerConnected);
        else if(kind==LauncherNoticeKind.Preloading)
            using(var pen=new Pen(Color.FromArgb(206,180,252),2))
                g.DrawArc(pen,308,24,20,20,(float)(age*285-90),110);
    }

    static void PaintControllerBadge(Graphics g,bool connected) {
        var accent=connected ? Color.FromArgb(158,226,202) : Color.FromArgb(238,168,156);
        using(var fill=new SolidBrush(Color.FromArgb(43,35,50)))
        using(var pen=new Pen(accent,1.8f) { StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round })
        using(var pad=new GraphicsPath()) {
            g.FillEllipse(fill,300,16,36,36);
            pad.AddBezier(306,38,308,24,309,24,315,27);
            pad.AddLine(315,27,321,27);
            pad.AddBezier(321,27,327,24,328,24,330,38);
            pad.AddBezier(330,38,330,44,326,43,322,37);
            pad.AddLine(322,37,314,37);
            pad.AddBezier(314,37,310,43,306,44,306,38);
            g.DrawPath(pen,pad);
            g.DrawLine(pen,311,32,315,32);
            if(connected) g.DrawLine(pen,313,30,313,34);
            g.DrawEllipse(pen,323,30,1.8f,1.8f);
        }
    }

    internal sealed class LauncherNotices : IDisposable {
        readonly Queue<(LauncherNoticeKind Kind,string DeviceName)> pending=new Queue<(LauncherNoticeKind,string)>();
        readonly DispatcherTimer timer=new DispatcherTimer();
        readonly Stopwatch clock=Stopwatch.StartNew();
        readonly IntPtr launcher;
        readonly Action<string> log;
        readonly Action<LauncherNoticeKind> playSound;
        bool preloadAnnounced;
        IntPtr game;
        AchievementToast current;
        public LauncherNotices(IntPtr launcher,Action<string> log,Action<LauncherNoticeKind> playSound) {
            this.launcher=launcher; this.log=log; this.playSound=playSound;
            timer.Interval=TimeSpan.FromMilliseconds(16); timer.Tick+=Tick;
        }
        public void Show(LauncherNoticeKind kind,IntPtr gameWindow,string deviceName=null) {
            if(kind==LauncherNoticeKind.Preloading) {
                if(preloadAnnounced) return;
                preloadAnnounced=true;
            }
            if(kind==LauncherNoticeKind.Started) game=gameWindow;
            pending.Enqueue((kind,deviceName));
            if(!timer.IsEnabled) { timer.Start(); Tick(this,EventArgs.Empty); }
        }
        void ReleaseCurrent() {
            current?.window?.Dispose();
            current?.titleLine.Dispose(); current?.detailLine.Dispose();
            current=null; noticeMonitor=Rectangle.Empty; noticeInset=0;
        }
        public void ResetSession() {
            preloadAnnounced=false;
            game=IntPtr.Zero;
            int count=pending.Count;
            while(count-->0) {
                var item=pending.Dequeue();
                if(item.Kind==LauncherNoticeKind.Installed || IsControllerNotice(item.Kind)) pending.Enqueue(item);
            }
            if(current!=null && current.noticeKind!=LauncherNoticeKind.Installed && !IsControllerNotice(current.noticeKind.Value)) ReleaseCurrent();
            if(current==null && pending.Count==0) timer.Stop();
        }
        void Tick(object sender,EventArgs args) {
            try {
                double now=clock.Elapsed.TotalSeconds;
                if(current!=null && now-current.born>=3) ReleaseCurrent();
                if(current==null) {
                    if(pending.Count==0) { timer.Stop(); return; }
                    var item=pending.Dequeue();
                    current=new AchievementToast(item.Kind,now,item.DeviceName);
                }
                var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
                try {
                    bool onGame=current.noticeKind==LauncherNoticeKind.Started || IsControllerNotice(current.noticeKind.Value);
                    var area=Backdrop.MonitorWorkArea(onGame && game!=IntPtr.Zero ? game : launcher);
                    if(area.IsEmpty) return;
                    float scale=Math.Min(area.Width*.184f/NoticeWidth,area.Height*.072f/NoticeHeight);
                    int width=Math.Max(1,(int)Math.Round(NoticeWidth*scale));
                    int height=Math.Max(1,(int)Math.Round(NoticeHeight*scale));
                    int margin=(int)Math.Round(area.Height*.021);
                    int right=(int)Math.Round(area.Width*.013);
                    double age=now-current.born;
                    bool animate=System.Windows.SystemParameters.ClientAreaAnimation;
                    double entrance=animate ? Ease(age/.36) : 1;
                    double exit=animate ? Ease((age-2.62)/.38) : 0;
                    int x=area.Width-right-width+(int)Math.Round((right+width)*exit);
                    int y=area.Height-margin-height+(int)Math.Round((height+margin)*(1-entrance));
                    noticeMonitor=area; noticeInset=(int)Math.Round((height+area.Height*.006)*entrance*(1-exit));
                    var row=new Row {Card=current,Bounds=new Rectangle(x,y,width,height),Reveal=Math.Min(height,area.Height-y),Alpha=255};
                    PresentWithSound(area,new[]{row},now,playSound);
                } finally { if(previous!=IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
            } catch(Exception ex) {
                timer.Stop(); pending.Clear(); ReleaseCurrent();
                log("Launcher notification: "+ex.Message);
            }
        }
        public void Dispose() { timer.Stop(); timer.Tick-=Tick; pending.Clear(); ReleaseCurrent(); }
    }
}
