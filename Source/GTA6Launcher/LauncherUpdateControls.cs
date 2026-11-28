using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

internal sealed class LauncherUpdateControls
{
    readonly Window window;
    readonly string root, state;
    readonly Func<bool> playing;
    readonly Func<bool> importing;
    readonly Action<string> log;
    readonly Button action, check;
    readonly TextBlock status, amount;
    readonly UIElement downloadGlyph, installGlyph, meter;
    readonly FrameworkElement slot, transferArrow;
    readonly TranslateTransform arrowLight;
    bool slotOpen, cycling, pulsing;
    readonly DropShadowEffect installLight = new DropShadowEffect {
        Color = Color.FromRgb(76, 171, 255), BlurRadius = 11, ShadowDepth = 0, Opacity = .48
    };
    readonly PathGeometry arc = new PathGeometry();
    string checkedRelease;
    readonly Version installed = Assembly.GetExecutingAssembly().GetName().Version;
    Version offered, cached;
    Version minimum;
    int normalUpdates, criticalUpdates;
    bool hasUpdateCounts;
    readonly TaskCompletionSource<bool> firstCheck = new TaskCompletionSource<bool>();
    public Task FirstCheck { get { return firstCheck.Task; } }
    public bool Required { get { return minimum != null && installed < minimum; } }
    public event Action RequirementChanged;
    readonly Button critical;
    readonly TextBlock criticalLabel;
    readonly System.Windows.Shapes.Path criticalGlyph;
    readonly Geometry downloadIcon, restartIcon=Geometry.Parse("M16,7 A7,7 0 1 0 17,13 M16,2 V7 H11");
    Process checker, downloader;
    int percent;
    bool closed, sessionBusy;
    string notice;
    public bool Installing { get; private set; }

    public LauncherUpdateControls(Window window, string root, Func<bool> playing, Action<string> log, Func<bool> importing)
    {
        this.window = window; this.root = root; this.playing = playing; this.log = log; this.importing=importing;
        state = System.IO.Path.Combine(root, UpdateIdentity.StateDirectory);
        action = (Button)window.FindName("UpdateAction");
        check = (Button)window.FindName("CheckLauncherUpdate");
        status = (TextBlock)window.FindName("UpdateStatus");
        amount = (TextBlock)window.FindName("DownloadAmount");
        downloadGlyph = (UIElement)window.FindName("DownloadGlyph");
        installGlyph = (UIElement)window.FindName("InstallGlyph");
        meter = (UIElement)window.FindName("DownloadMeter");
        slot = (FrameworkElement)window.FindName("UpdateSlot");
        transferArrow = (FrameworkElement)window.FindName("TransferArrow");
        var stroke = (LinearGradientBrush)((System.Windows.Shapes.Path)transferArrow).Stroke;
        arrowLight = (TranslateTransform)stroke.RelativeTransform;
        ((System.Windows.Shapes.Path)window.FindName("DownloadArc")).Data = arc;
        critical=(Button)window.FindName("CriticalUpdate");
        criticalLabel=(TextBlock)window.FindName("CriticalLabel");
        criticalGlyph=(System.Windows.Shapes.Path)window.FindName("CriticalGlyph");
        downloadIcon=criticalGlyph.Data;
        try {
            string marker=Path.Combine(state,"minimum-version");
            if(File.Exists(marker)) Version.TryParse(File.ReadAllText(marker).Trim(),out minimum);
        } catch(IOException e) { log(e.Message); }
          catch(UnauthorizedAccessException e) { log(e.Message); }
        action.Click += async delegate { await UpdateAsync(); };
        critical.Click += async delegate { await UpdateAsync(); };
        action.MouseEnter += delegate { RefreshBusy(); Hover(true); };
        action.MouseLeave += delegate { Hover(false); };
        action.IsEnabledChanged += delegate { Hover(action.IsMouseOver); };
        action.GotKeyboardFocus += delegate { RefreshBusy(); };
        check.Click += async delegate { await CheckAsync(); };
        window.ContentRendered += FirstRender;
        window.Closed += delegate {
            closed = true;
            Stop(checker);
            Stop(downloader);
        };
    }

    async void FirstRender(object sender, EventArgs e)
    {
        window.ContentRendered -= FirstRender;
        ReadCached();
        try { await CheckAsync(); }
        finally { firstCheck.TrySetResult(true); }
    }

    async Task UpdateAsync() {
        if (Installing || downloader != null) return;
        if (ReadyToInstall) await InstallAsync();
        else if (CanDownload) await DownloadAsync(offered);
    }

    void RememberMinimum(Version value) {
        if(value<=installed || (minimum!=null && minimum>=value)) return;
        bool wasRequired=Required;
        minimum=value;
        if(!wasRequired) RequirementChanged?.Invoke();
        string path=Path.Combine(state,"minimum-version");
        string pending=path+".tmp";
        File.WriteAllText(pending,value.ToString());
        if(File.Exists(path)) File.Replace(pending,path,null);
        else File.Move(pending,path);
    }

    void ReadCached()
    {
        cached = null;
        try {
            string hash;
            var version = UpdateIdentity.Ready(state, out hash);
            if (version != null && version > installed) cached = version;
        } catch (IOException e) { log(e.Message); }
          catch (UnauthorizedAccessException e) { log(e.Message); }
    }

    static void Stop(Process child)
    {
        if (child == null) return;
        try { if (!child.HasExited) child.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    bool CanDownload => offered != null && checkedRelease != null && (!Required || offered >= minimum);

    bool ReadyToInstall { get { return cached != null && (!Required || cached >= minimum) && (offered == null || cached >= offered); } }

    void RefreshBusy()
    {
        sessionBusy = false;
        if (ReadyToInstall && !playing()) {
            var leases = UpdateIdentity.TryLockProfiles(root);
            sessionBusy = leases == null;
            if (leases != null) foreach (var lease in leases) lease.Dispose();
        }
        Refresh();
    }

    void Hover(bool over)
    {
        var scale = (ScaleTransform)action.RenderTransform;
        double from = scale.ScaleX;
        double to = over && action.IsEnabled && SystemParameters.ClientAreaAnimation ? 1.075 : 1;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = scale.ScaleY = to;
        if (!SystemParameters.ClientAreaAnimation || from == to) return;
        var transition = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(over ? 310 : 205)) {
            EasingFunction = new BackEase { Amplitude = over ? .65 : .16, EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, transition);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, transition);
    }

    void Reveal(bool show)
    {
        if (slotOpen == show) return;
        slotOpen = show;
        slot.ClipToBounds = true;
        double width = slot.Width, target = show ? 45 : 0;
        if (show) action.Visibility = Visibility.Visible;
        slot.IsHitTestVisible = show;
        slot.BeginAnimation(FrameworkElement.WidthProperty, null);
        slot.Width = target;
        if (!SystemParameters.ClientAreaAnimation) {
            slot.ClipToBounds = !show;
            slot.BeginAnimation(UIElement.OpacityProperty, null);
            action.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        var slide = new DoubleAnimation(width, target, TimeSpan.FromMilliseconds(show ? 420 : 240)) {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop
        };
        slide.Completed += delegate {
            slot.ClipToBounds = !slotOpen;
            if (!slotOpen) action.Visibility = Visibility.Collapsed;
        };
        slot.BeginAnimation(FrameworkElement.WidthProperty, slide);
        slot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(show ? 0 : 1, show ? 1 : 0,
            TimeSpan.FromMilliseconds(show ? 380 : 190)) { FillBehavior = FillBehavior.HoldEnd });
    }

    static DoubleAnimationUsingKeyFrames Loop(params (double time, double value)[] frames)
    {
        var loop = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        foreach (var frame in frames)
            loop.KeyFrames.Add(new SplineDoubleKeyFrame(frame.value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(frame.time)),
                new KeySpline(.32, 0, .22, 1)));
        return loop;
    }

    void AnimateGlyphs(bool loading, bool canInstall)
    {
        bool animate = SystemParameters.ClientAreaAnimation;
        if (cycling != (animate && loading)) {
            cycling = animate && loading;
            transferArrow.BeginAnimation(UIElement.OpacityProperty, cycling ? Loop((0,1),(1.75,1),(2.1,0),(4.85,0),(5.2,1)) : null);
            amount.BeginAnimation(UIElement.OpacityProperty, cycling ? Loop((0,0),(1.75,0),(2.1,1),(4.85,1),(5.2,0)) : null);
            arrowLight.BeginAnimation(TranslateTransform.YProperty, cycling ?
                Loop((0,-.72),(1.7,.72),(2.1,.72),(4.8,-.72),(5.2,-.72)) : null);
        }
        action.Effect = canInstall ? installLight : null;
        if (pulsing != (animate && canInstall)) {
            pulsing = animate && canInstall;
            installLight.BeginAnimation(DropShadowEffect.OpacityProperty,
                pulsing ? Loop((0,.22),(1.65,.88),(3.3,.22)) : null);
        }
    }

    public void Refresh()
    {
        bool loading = downloader != null;
        bool ready = ReadyToInstall;
        Reveal(Required || offered != null || ready || loading);
        bool busy=playing() || importing();
        action.IsEnabled = !Installing && !loading && checker == null && (ready ? !busy : CanDownload);
        action.Opacity = ready && (busy || sessionBusy) ? .62 : 1;
        downloadGlyph.Visibility = !loading && !ready ? Visibility.Visible : Visibility.Collapsed;
        installGlyph.Visibility = !loading && ready ? Visibility.Visible : Visibility.Collapsed;
        meter.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        AnimateGlyphs(loading, ready && !loading && !Installing && !busy && !sessionBusy);
        string hint = loading ? Ui.Format("正在下载更新 · {0}%", percent)
            : Installing ? Ui.Text("正在重启启动器")
            : ready && playing() ? Ui.Text("退出游戏后可安装更新")
            : ready && importing() ? Ui.Text("导入结束后可安装更新")
            : ready && sessionBusy ? Ui.Text("游戏或预热正在运行，请稍后点击安装")
            : ready ? Ui.Format("安装 {0} 并重启启动器", UpdateIdentity.DisplayVersion(cached))
            : Required ? Ui.Format("需要启动器 {0} 或更高版本才能启动游戏",UpdateIdentity.DisplayVersion(minimum))
            : offered != null ? Ui.Format("下载启动器更新 {0}", UpdateIdentity.DisplayVersion(offered)) : "";
        if(Required) {
            criticalLabel.Text=Installing ? Ui.Text("正在重启启动器") : loading ? Ui.Format("正在下载更新 · {0}%",percent) :
                Ui.Text(ready ? "安装更新并重启" : "需要关键更新");
            criticalGlyph.Data=ready ? restartIcon : downloadIcon;
            critical.ToolTip=notice!=null ? Ui.Text(notice) : hint;
            AutomationProperties.SetName(critical,criticalLabel.Text);
            critical.IsEnabled=action.IsEnabled;
        }
        action.ToolTip = hint;
        AutomationProperties.SetName(action, hint);
        check.IsEnabled = checker == null && !loading && !Installing;
        status.Text = checker != null && !loading && !Installing ? Ui.Text("正在检查更新…")
            : notice != null ? Ui.Text(notice) : hint.Length != 0 ? hint : Ui.Text("当前没有可用更新");
        if (offered != null && hasUpdateCounts && checker == null && !loading && !Installing && notice == null)
            status.Text += Environment.NewLine + Ui.Format("从 {0} 到 {1}：普通更新 {2} 个，关键更新 {3} 个",
                UpdateIdentity.DisplayVersion(installed), UpdateIdentity.DisplayVersion(offered), normalUpdates, criticalUpdates);
    }

    async Task CheckAsync()
    {
        if (closed || checker != null || downloader != null || Installing) return;
        try {
            checker = LauncherUpdate.StartAgent(root, "check", installed);
            Refresh();
            string line = await checker.StandardOutput.ReadLineAsync();
            string counts = await checker.StandardOutput.ReadLineAsync();
            string snapshot = await checker.StandardOutput.ReadLineAsync();
            await Task.Run((Action)checker.WaitForExit);
            if (closed) return;
            Version v;
            if (checker.ExitCode == 0 && line == "CURRENT") { offered = null; checkedRelease = null; hasUpdateCounts = false; }
            else {
                var fields=(line ?? "").Split(' ');
                if(checker.ExitCode!=0 || fields.Length<2 || fields.Length>3 || fields[0]!="AVAILABLE" ||
                    !Version.TryParse(fields[1],out v) || v<=installed) throw new IOException("GitHub release check failed.");
                var totals=(counts ?? "").Split(' ');
                if(counts!="NOCOUNTS" && (totals.Length!=3 || totals[0]!="COUNTS" ||
                    !int.TryParse(totals[1],out normalUpdates) || normalUpdates<0 ||
                    !int.TryParse(totals[2],out criticalUpdates) || criticalUpdates<0))
                    throw new IOException("Invalid update counts.");
                if (string.IsNullOrWhiteSpace(snapshot)) throw new IOException("Missing checked release metadata.");
                hasUpdateCounts=counts!="NOCOUNTS";
                offered=v;
                checkedRelease=snapshot;
                if(fields.Length==3) {
                    Version floor;
                    if(!Version.TryParse(fields[2],out floor) || floor>v) throw new IOException("Invalid minimum launcher version.");
                    RememberMinimum(floor);
                }
            }
            if (notice == "无法检查更新，请稍后重试") notice = null;
        } catch (Exception e) {
            if (!closed) { notice = "无法检查更新，请稍后重试"; log(e.Message); }
        } finally {
            if (checker != null) checker.Dispose();
            checker = null;
            if (!closed) RefreshBusy();
        }
    }

    void Progress(int value)
    {
        percent = value;
        amount.Text = value.ToString();
        arc.Figures.Clear();
        if (value > 0) {
            const double center = 18.5, radius = 13;
            var figure = new PathFigure { StartPoint = new Point(center, center-radius), IsFilled = false };
            double angle = Math.PI * value / 50;
            var end = new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
            if (value == 100) figure.Segments.Add(new ArcSegment(new Point(center, center+radius),
                new Size(radius,radius), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new ArcSegment(end, new Size(radius,radius), 0, value > 50 && value < 100, SweepDirection.Clockwise, true));
            arc.Figures.Add(figure);
        }
        Refresh();
    }

    async Task DownloadAsync(Version version)
    {
        try {
            notice = null;
            downloader = LauncherUpdate.StartAgent(root, "download", version);
            await downloader.StandardInput.WriteLineAsync(checkedRelease);
            downloader.StandardInput.Close();
            Progress(0);
            string line;
            while ((line = await downloader.StandardOutput.ReadLineAsync()) != null) {
                int value;
                if (!closed && line.StartsWith("PROGRESS ", StringComparison.Ordinal) && int.TryParse(line.Substring(9), out value) &&
                    value >= percent && value <= 100) Progress(value);
            }
            await Task.Run((Action)downloader.WaitForExit);
            if (closed) return;
            ReadCached();
            if (downloader.ExitCode != 0 || cached != version) throw new IOException("Launcher package download or verification failed.");
            notice = null;
        } catch (Exception e) {
            if (!closed) { notice = "下载失败，点击下载图标重试"; log(e.Message); }
        } finally {
            if (downloader != null) downloader.Dispose();
            downloader = null;
            if (!closed) RefreshBusy();
        }
    }

    async Task InstallAsync()
    {
        RefreshBusy();
        if (playing() || importing() || sessionBusy) return;
        Installing = true;
        notice = null;
        Refresh();
        try {
            using (var helper = LauncherUpdate.StartAgent(root, "install", cached)) {
                string reply = await helper.StandardOutput.ReadLineAsync();
                if (closed || playing() || importing()) { helper.StandardInput.Close(); return; }
                if (reply == "BUSY") { sessionBusy = true; return; }
                if (reply != "READY") throw new IOException("Installation handoff failed.");
                helper.StandardInput.WriteLine("INSTALL");
                helper.StandardInput.Close();
                window.Close();
            }
        } catch (Exception e) {
            if (!closed) { ReadCached(); notice = "无法安装更新，请重试"; log(e.Message); }
        } finally {
            Installing = false;
            if (!closed) Refresh();
        }
    }
}
