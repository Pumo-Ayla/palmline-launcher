using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Threading;

internal enum LauncherNoticeKind { Installed, Preloading, Ready, Started }

sealed partial class AchievementToast {
    const int NoticeHeight=68, NoticeWidth=358;
    static readonly string[,] noticeCopy={
        {"安装完成","游戏已添加到游戏库"},
        {"开始预载","正在准备着色器和渲染管线"},
        {"预载完成","现在可以启动游戏"},
        {"游戏启动成功","已显示第一帧画面"}
    };
    static readonly Dictionary<LauncherNoticeKind,Bitmap> noticeArt=new Dictionary<LauncherNoticeKind,Bitmap>();
    static Rectangle noticeMonitor;
    static int noticeInset;
    readonly LauncherNoticeKind? noticeKind;

    AchievementToast(LauncherNoticeKind kind,double now) {
        noticeKind=kind; born=now; CardWidth=NoticeWidth;
        if(!noticeArt.ContainsKey(kind)) {
            using(var stream=typeof(AchievementToast).Assembly.GetManifestResourceStream("LauncherNotice."+kind+".png"))
            using(var image=new Bitmap(stream)) noticeArt.Add(kind,new Bitmap(image));
        }
        int lang=Math.Max(0,Array.IndexOf(languages,Ui.Language));
        if(lang==0) EnsureFonts();
        using(var family=lang==0 ? chineseFont.Families[0] : new FontFamily(fonts[lang]))
        using(var heading=lang==0 ? chineseHeading.Families[0] : new FontFamily(fonts[lang])) {
            var style=lang!=0 && heading.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
            titleLine=ShapeLine(Ui.Text(noticeCopy[(int)kind,0]),heading,TitleSize,style,219,false);
            detailLine=ShapeLine(Ui.Text(noticeCopy[(int)kind,1]),family,DetailSize,FontStyle.Regular,219,false);
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
        g.DrawImage(noticeArt[noticeKind.Value],new Rectangle(0,0,CardWidth,NoticeHeight));
        using(var title=new SolidBrush(Color.FromArgb(255,247,237)))
        using(var detail=new SolidBrush(Color.FromArgb(213,203,216))) {
            g.FillPath(title,titleLine); g.FillPath(detail,detailLine);
        }
        if(noticeKind==LauncherNoticeKind.Preloading)
            using(var pen=new Pen(Color.FromArgb(206,180,252),2))
                g.DrawArc(pen,308,24,20,20,(float)(age*285-90),110);
    }

    internal sealed class LauncherNotices : IDisposable {
        readonly Queue<LauncherNoticeKind> pending=new Queue<LauncherNoticeKind>();
        readonly DispatcherTimer timer=new DispatcherTimer();
        readonly Stopwatch clock=Stopwatch.StartNew();
        readonly IntPtr launcher;
        readonly Action<string> log;
        IntPtr game;
        AchievementToast current;
        public LauncherNotices(IntPtr launcher,Action<string> log) {
            this.launcher=launcher; this.log=log;
            timer.Interval=TimeSpan.FromMilliseconds(16); timer.Tick+=Tick;
        }
        public void Show(LauncherNoticeKind kind,IntPtr gameWindow) {
            if(kind==LauncherNoticeKind.Started) game=gameWindow;
            pending.Enqueue(kind);
            if(!timer.IsEnabled) { timer.Start(); Tick(this,EventArgs.Empty); }
        }
        void ReleaseCurrent() {
            current?.window?.Dispose();
            current?.titleLine.Dispose(); current?.detailLine.Dispose();
            current=null; noticeMonitor=Rectangle.Empty; noticeInset=0;
        }
        public void ResetSession() {
            game=IntPtr.Zero;
            int count=pending.Count;
            while(count-->0) {
                var kind=pending.Dequeue();
                if(kind==LauncherNoticeKind.Installed) pending.Enqueue(kind);
            }
            if(current!=null && current.noticeKind!=LauncherNoticeKind.Installed) ReleaseCurrent();
            if(current==null && pending.Count==0) timer.Stop();
        }
        void Tick(object sender,EventArgs args) {
            try {
                double now=clock.Elapsed.TotalSeconds;
                if(current!=null && now-current.born>=3) ReleaseCurrent();
                if(current==null) {
                    if(pending.Count==0) { timer.Stop(); return; }
                    current=new AchievementToast(pending.Dequeue(),now);
                }
                var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
                try {
                    var area=Backdrop.MonitorWorkArea(current.noticeKind==LauncherNoticeKind.Started && game!=IntPtr.Zero ? game : launcher);
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
                    Toast.Present(area,new[]{row},now);
                } finally { if(previous!=IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
            } catch(Exception ex) {
                timer.Stop(); pending.Clear(); ReleaseCurrent();
                log("Launcher notification: "+ex.Message);
            }
        }
        public void Dispose() { timer.Stop(); timer.Tick-=Tick; pending.Clear(); ReleaseCurrent(); }
    }
}
