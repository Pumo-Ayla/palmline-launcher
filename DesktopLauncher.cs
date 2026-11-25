using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal sealed class DesktopLauncher
{
    readonly string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    readonly Window window;
    readonly RadioButton[] projects;
    readonly StringBuilder recentLog = new StringBuilder();
    Window console;
    TextBox terminal;
    Process worker;
    string selected = "local", running, logDirectory;
    bool closed, ready, playing, restart;

    T Find<T>(string name) where T : class { return (T)window.FindName(name); }
    string Lab { get { return Path.Combine(root, "XCL-VIctory-Lab"); } }
    string Preference { get { return Path.Combine(Lab, "desktop", "selected-project.txt"); } }
    string ProjectName(string id) {
        return id == "local" ? "Z · 本地集成版" : id == "stock" ? "ZETTROX" : "VIctory · Belobug AMD";
    }
    DesktopLauncher()
    {
        window = LoadWindow("Main.xaml");
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Light, FontStretches.Normal);
        var title = new FormattedText("Grand Theft\nAuto VI", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 64, Brushes.White, 1) { LineHeight = 67 };
        Find<System.Windows.Shapes.Path>("GameTitle").Data = title.BuildGeometry(new Point(0, 0));
        projects = new[] { Find<RadioButton>("Local"), Find<RadioButton>("Stock"), Find<RadioButton>("Amd") };
        LoadArtwork();
        string last = "local";
        try { if (File.Exists(Preference)) last = File.ReadAllText(Preference).Trim(); }
        catch (IOException) { }
        selected = last == "stock" || last == "amd" ? last : "local";
        Find<TextBlock>("Project").Text = ProjectName(selected);
        foreach (var radio in projects) {
            radio.IsChecked = (string)radio.Tag == selected;
            radio.Checked += delegate(object sender, RoutedEventArgs e) { Select((string)((RadioButton)sender).Tag); };
        }
        Find<Button>("Min").Click += delegate { window.WindowState = WindowState.Minimized; };
        Find<Button>("Max").Click += delegate { window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; };
        Find<Button>("Close").Click += delegate { window.Close(); };
        Find<Button>("Retry").Click += delegate { Schedule(); };
        Find<Button>("ShowConsole").Click += delegate { ShowConsole(); };
        window.PreviewKeyDown += Navigate;
        Find<Button>("Play").Click += delegate {
            if (!ready || worker == null || selected != running) return;
            SetPlaying(true); SetStatus("正在启动游戏", 100, "");
            try { worker.StandardInput.WriteLine("PLAY"); worker.StandardInput.Flush(); }
            catch (IOException ex) { Append(ex.Message); SetPlaying(false); }
        };
        Find<Button>("Play").IsEnabled = false;
        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        var clockText = Find<TextBlock>("Clock");
        clockText.Text = DateTime.Now.ToString("HH:mm");
        clock.Tick += delegate { clockText.Text = DateTime.Now.ToString("HH:mm"); };
        clock.Start();
        window.Closed += delegate { closed = true; clock.Stop(); CloseInput(); };
        window.ContentRendered += delegate {
            if (worker == null) Begin();
            foreach (var radio in projects) if (radio.IsChecked == true) radio.Focus();
        };
    }
    static Window LoadWindow(string resource) {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            return (Window)XamlReader.Load(stream);
    }
    void LoadArtwork() {
        var image = new BitmapImage();
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("rooftop.jpg")) {
            if (stream == null) return;
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
        }
        image.Freeze();
        Find<Image>("Art").Source = image;
        var crops = new[] { new Rect(.485, .015, .165, .294), new Rect(.035, .39, .23, .409), new Rect(.70, .14, .19, .338) };
        for (int i = 0; i < projects.Length; i++)
            projects[i].Background = new ImageBrush(image) { Viewbox = crops[i], ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.UniformToFill };
    }
    void Navigate(object sender, KeyEventArgs e) {
        if (playing || Keyboard.Modifiers != ModifierKeys.None) return;
        int current = Array.FindIndex(projects, radio => radio.IsChecked == true);
        if (current < 0) return;
        if (e.Key == Key.Left || e.Key == Key.Right) {
            int next = (current + (e.Key == Key.Right ? 1 : -1) + projects.Length) % projects.Length;
            projects[next].IsChecked = true;
            projects[next].Focus();
        } else if (e.Key == Key.Down && projects[current].IsKeyboardFocused) {
            var play = Find<Button>("Play");
            (play.IsEnabled ? play : Find<Button>("Retry")).Focus();
        } else if (e.Key == Key.Up) {
            projects[current].Focus();
        } else return;
        e.Handled = true;
    }
    void ShowConsole() {
        if (console != null) {
            if (console.WindowState == WindowState.Minimized) console.WindowState = WindowState.Normal;
            console.Activate();
            return;
        }
        console = LoadWindow("Console.xaml");
        console.Owner = window;
        terminal = (TextBox)console.FindName("Terminal");
        terminal.Text = recentLog.ToString();
        UpdateLogProject();
        ((Button)console.FindName("Dismiss")).Click += delegate { console.Close(); };
        ((Button)console.FindName("Logs")).Click += delegate {
            string path = logDirectory ?? Path.Combine(Lab, "runs");
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        };
        console.Closed += delegate { console = null; terminal = null; };
        console.Show();
        terminal.ScrollToEnd();
    }
    void UpdateLogProject() {
        if (console != null) ((TextBlock)console.FindName("LogProject")).Text = ProjectName(running ?? selected);
    }
    void SetPlaying(bool value) {
        playing = value; ready = false;
        foreach (var radio in projects) radio.IsEnabled = !value;
        Find<Button>("Retry").IsEnabled = !value;
        Find<Button>("Play").IsEnabled = false;
        Find<Button>("Play").Content = value ? "游戏运行中" : "启动游戏";
    }
    void Select(string id) {
        if (playing || selected == id) return;
        selected = id;
        Find<TextBlock>("Project").Text = ProjectName(id);
        try { File.WriteAllText(Preference, id); } catch (IOException) { }
        Schedule();
    }
    void CloseInput() {
        if (worker != null) try { worker.StandardInput.Close(); } catch (InvalidOperationException) { } catch (IOException) { }
    }
    void Schedule() {
        if (playing) return;
        if (worker == null) { Begin(); return; }
        restart = true; ready = false;
        Find<Button>("Play").IsEnabled = false;
        SetStatus("等待上一轮预热结束", 0, "");
        CloseInput();
    }
    void SetStatus(string phase, double percent, string fraction) {
        Find<TextBlock>("Phase").Text = phase;
        Find<TextBlock>("Fraction").Text = fraction;
        Find<ProgressBar>("Bar").Value = percent;
    }
    void Append(string text) {
        string line = DateTime.Now.ToString("HH:mm:ss") + "   " + text + Environment.NewLine;
        recentLog.Append(line);
        bool trimmed = recentLog.Length > 36000;
        if (trimmed) {
            int cut = 12000;
            while (cut < recentLog.Length && recentLog[cut - 1] != '\n') cut++;
            recentLog.Remove(0, cut);
        }
        if (terminal == null) return;
        bool follow = terminal.VerticalOffset + terminal.ViewportHeight >= terminal.ExtentHeight - 1;
        if (trimmed) terminal.Text = recentLog.ToString();
        else terminal.AppendText(line);
        if (follow) terminal.ScrollToEnd();
    }
    void Dispatch(Action action) {
        if (!window.Dispatcher.HasShutdownStarted) window.Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }
    void Begin() {
        if (closed) return;
        running = selected; restart = false; SetPlaying(false);
        recentLog.Clear(); logDirectory = null;
        if (terminal != null) terminal.Clear();
        UpdateLogProject();
        SetStatus("正在准备着色器", 0, "");
        Append("已选择 " + ProjectName(running));
        var python = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            @".cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe");
        var si = new ProcessStartInfo {
            FileName = File.Exists(python) ? python : "python.exe",
            Arguments = "-B -u \"" + Path.Combine(Lab, @"desktop\session.py") + "\" " + running,
            WorkingDirectory = Lab, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        si.EnvironmentVariables["PYTHONUTF8"] = "1";
        var p = new Process { StartInfo = si };
        try {
            p.Start(); worker = p;
            p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                if (e.Data != null) Dispatch(delegate { if (!closed && worker == p && !restart) Append(e.Data); });
            };
            p.BeginErrorReadLine();
            var reader = new Thread(delegate() {
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null) {
                    string entry = line;
                    Dispatch(delegate { if (!closed && worker == p && !restart) Receive(entry); });
                }
                p.WaitForExit(); int code = p.ExitCode;
                Dispatch(delegate {
                    if (worker != p) return;
                    worker = null; p.Dispose();
                    if (closed) return;
                    SetPlaying(false);
                    if (restart) Begin();
                    else if (code != 0) SetStatus("会话失败，请查看实时日志", 0, "");
                });
            });
            reader.IsBackground = true; reader.Start();
        } catch (Exception ex) { worker = null; p.Dispose(); Append(ex.Message); SetStatus("启动器依赖不可用", 0, ""); }
    }
    void Receive(string line) {
        int tab = line.IndexOf('\t');
        if (tab < 0) { Append(line); return; }
        string kind = line.Substring(0, tab), msg = line.Substring(tab + 1);
        if (kind == "RUN") { logDirectory = msg; return; }
        if (kind == "READY") {
            ready = true; Find<Button>("Play").IsEnabled = true;
            SetStatus(msg, 100, "100%"); Append(msg); return;
        }
        if (kind == "STAGE") SetStatus(msg, 0, "");
        if (kind == "ERROR") SetStatus("预热或启动失败", 0, "");
        if (kind == "PLAYING") { SetStatus("游戏运行中", 100, ""); Append("游戏进程 " + msg); return; }
        if (kind == "EXIT") { SetStatus("游戏已退出", 100, ""); Append("游戏退出码 " + msg); return; }
        var m = Regex.Match(msg, @"^(着色器|GPU预热) (\d+)/(\d+)");
        if (m.Success) {
            double done = double.Parse(m.Groups[2].Value), total = double.Parse(m.Groups[3].Value);
            SetStatus(m.Groups[1].Value == "着色器" ? "预加载着色器" : "预热 GPU 计算管线",
                total == 0 ? 0 : 100*done/total, m.Groups[2].Value + " / " + m.Groups[3].Value);
        }
        if (msg.Length > 640) msg = msg.Substring(0,640) + "…";
        Append(msg);
    }
    [STAThread]
    static int Main() {
        try {
            var app = new Application();
            var shell = new DesktopLauncher();
            using (var once = new Mutex(false, "Local\\GrandTheftAutoVI.DesktopLauncher." + shell.root.GetHashCode())) {
                bool owns;
                try { owns = once.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                if (!owns) { MessageBox.Show("启动器已打开。", "Grand Theft Auto VI"); return 0; }
                try { return app.Run(shell.window); } finally { once.ReleaseMutex(); }
            }
        } catch (Exception ex) {
            MessageBox.Show(ex.Message, "Grand Theft Auto VI");
            return 1;
        }
    }
}
