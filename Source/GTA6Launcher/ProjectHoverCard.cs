using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;

internal sealed class ProjectHoverCard
{
    readonly Window window;
    readonly ProjectDisplaySettings settings;
    readonly Canvas overlay;
    readonly Border card;
    readonly Button source;
    readonly DispatcherTimer timer = new DispatcherTimer();
    RadioButton anchor, pending;
    T Find<T>(string name) where T : class { return (T)window.FindName(name); }

    public ProjectHoverCard(Window window, ProjectDisplaySettings settings) {
        this.window=window;
        this.settings=settings;
        overlay=Find<Canvas>("ProjectOverlay");
        card=Find<Border>("ProjectCard");
        source=Find<Button>("ProjectSource");
        source.Click+=delegate {
            var uri=source.Tag as Uri;
            if(uri!=null) Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute=true });
        };
        card.MouseEnter+=delegate { timer.Stop(); };
        card.MouseLeave+=delegate { Schedule(null); };
        source.LostKeyboardFocus+=delegate { if(!card.IsMouseOver) Schedule(null); };
        window.Deactivated+=delegate { Hide(); };
        overlay.SizeChanged+=delegate { if(anchor!=null) Place(); };
        timer.Tick+=delegate {
            timer.Stop();
            if(pending!=null && pending.IsEnabled && pending.IsVisible) Show(pending);
            else if(!card.IsMouseOver && !card.IsKeyboardFocusWithin) Hide();
        };
    }
    static XElement Benchmark(XElement project) {
        var benchmark=project.Element("performance");
        return benchmark?.Attribute("source")!=null || (string)benchmark?.Attribute("revision")==(string)project.Attribute("commit") ? benchmark : null;
    }
    static bool SupportsAchievements(XElement project) {
        string id=(string)project.Attribute("id");
        return id=="cgbj" || id=="victory-nvidia";
    }
    public void Register(RadioButton tile, XElement project) {
        tile.DataContext=project;
        tile.MouseEnter+=delegate { Schedule(tile); };
        tile.MouseLeave+=delegate { Schedule(null); };
        tile.GotKeyboardFocus+=delegate { if((Visibility)window.Resources["KeyboardCueVisibility"]==Visibility.Visible) Schedule(tile); };
        tile.LostKeyboardFocus+=delegate { if(!tile.IsMouseOver) Schedule(null); };
        tile.PreviewKeyDown+=delegate(object sender, KeyEventArgs e) {
            if(e.Key!=Key.F1) return;
            Show(tile); source.Focus(); e.Handled=true;
        };
        tile.IsEnabledChanged+=delegate { if(!tile.IsEnabled && (tile==anchor || tile==pending)) Hide(); };
        UpdateHelp(tile);
    }
    public void UpdateHelp(RadioButton tile) {
        var project=(XElement)tile.DataContext;
        var benchmark=Benchmark(project);
        string details=Ui.Text("是否支持成就")+" · "+Ui.Text(SupportsAchievements(project) ? "支持" : "不支持");
        string stop=Ui.Language=="en-US" || Ui.Language=="ko-KR" ? ". " : "。";
        if(settings.ShowFps && benchmark!=null) details+=stop+Ui.Text((string)benchmark.Attribute("rate"))+" · "+Ui.Text(benchmark.Value);
        string credit=(string)project.Attribute("credit");
        details+=stop+(credit==null ? ProjectDisplaySettings.FormatPullTime((string)project.Attribute("fetched")) : Ui.Text(credit))+stop+Ui.Text("F1 打开项目卡片");
        AutomationProperties.SetHelpText(tile,details);
        if(tile==anchor) Show(tile);
    }
    void Schedule(RadioButton tile) {
        timer.Stop();
        pending=tile;
        timer.Interval=TimeSpan.FromMilliseconds(tile==null ? 290 : 140);
        timer.Start();
    }
    public bool Escape() {
        if(anchor==null) return false;
        if(source.IsKeyboardFocused) anchor.Focus();
        Hide();
        return true;
    }
    public void Hide() {
        timer.Stop(); pending=null; anchor=null; source.Tag=null;
        card.Visibility=Visibility.Collapsed;
    }
    void Show(RadioButton tile) {
        timer.Stop();
        anchor=tile;
        var project=(XElement)tile.DataContext;
        var benchmark=Benchmark(project);
        string repo=(string)project.Attribute("repo"), revision=(string)project.Attribute("commit") ?? "";
        source.Tag=new Uri("https://github.com/"+repo);
        AutomationProperties.SetName(source,Ui.Format("打开 {0} 的 GitHub 仓库",repo));
        Find<Border>("CardArt").Background=tile.Background;
        Find<TextBlock>("CardName").Text=(string)project.Attribute("name");
        string credit=(string)project.Attribute("credit");
        Find<TextBlock>("CardRepo").Text=credit==null ? repo : repo+"\n"+Ui.Text(credit);
        bool supportsAchievements=SupportsAchievements(project);
        Find<TextBlock>("CardAchievementStatus").Text=Ui.Text(supportsAchievements ? "支持" : "不支持");
        Find<TextBlock>("CardAverage").Text=Ui.Text((string)benchmark?.Attribute("rate") ?? "未公布");
        Find<TextBlock>("CardFpsLabel").Text=Ui.Text((string)benchmark?.Attribute("source")=="群友反馈" ? "游戏内" : "游戏内 FPS");
        Find<TextBlock>("CardStability").Text=Ui.Text((string)benchmark?.Attribute("stability") ?? "暂无长期记录");
        var fps=settings.ShowFps ? Visibility.Visible : Visibility.Collapsed;
        Find<StackPanel>("CardFpsGroup").Visibility=fps;
        Find<ColumnDefinition>("CardFpsColumn").Width=settings.ShowFps ? new GridLength(1,GridUnitType.Star) : new GridLength(0);
        var sample=Find<TextBlock>("CardBenchmark");
        sample.Visibility=benchmark!=null ? fps : Visibility.Collapsed;
        string rate=(string)benchmark?.Attribute("rate");
        sample.Text=benchmark==null ? "" : Ui.Text((string)benchmark.Attribute("source") ?? "上游实测")+" · "+Ui.Text(benchmark.Value);
        Find<TextBlock>("CardConfig").Text=Ui.Text((string)benchmark?.Attribute("config") ?? (string)project.Attribute("platform"));
        string versionLabel=(string)project.Attribute("distribution")=="portable" ? "SHA-256  /  " : Ui.Text("开源仓库")+"  ·  GitHub  /  ";
        Find<TextBlock>("CardRevision").Text=versionLabel+revision.Substring(0,Math.Min(8,revision.Length));
        Find<TextBlock>("CardFetched").Text=(string)project.Attribute("distribution")=="portable" ? Ui.Text("兼容启动器本机存档") : ProjectDisplaySettings.FormatPullTime((string)project.Attribute("fetched"));
        var achievement=Find<StackPanel>("CardAchievementSupport");
        Find<TextBlock>("CardAchievementSystem").Text=AchievementToast.Session.SystemName;
        achievement.Visibility=supportsAchievements ? Visibility.Visible : Visibility.Collapsed;
        card.Visibility=Visibility.Visible;
        Place();
    }
    void Place() {
        var center=anchor.TranslatePoint(new Point(0,anchor.ActualHeight/2),overlay);
        card.Measure(new Size(card.Width,double.PositiveInfinity));
        Canvas.SetLeft(card,Math.Max(6,center.X-card.Width-14));
        Canvas.SetTop(card,Math.Max(6,Math.Min(center.Y-34,overlay.ActualHeight-card.DesiredSize.Height-6)));
    }
}
