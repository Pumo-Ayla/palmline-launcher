using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal sealed class LoadingScreens : FrameworkElement
{
    const double OpeningPlaybackRate = 1.2;
    static readonly Color OpeningColor=Color.FromRgb(32,32,44);
    sealed class Movie
    {
        public double Fps;
        public DrawingGroup[] Frames;
        public int Width, Height;
        public string Name;

        static Stream Entry(ZipArchive zip, string name) {
            var entry=zip.GetEntry(name);
            if(entry==null) throw new InvalidDataException("Missing animation entry: "+name);
            return entry.Open();
        }
        public static Movie Open(string file) {
            using (var zip = ZipFile.OpenRead(file))
            using (var reader = new BinaryReader(Entry(zip,"timeline"))) {
                if (reader.ReadUInt32() != 0x31534C50) throw new InvalidDataException("Invalid loading animation.");
                var result = new Movie { Name=Path.GetFileNameWithoutExtension(file),
                    Width=reader.ReadUInt16(), Height=reader.ReadUInt16(), Fps=reader.ReadSingle() };
                int frames=reader.ReadUInt16(), count=reader.ReadUInt16();
                if (frames==0 || result.Width==0 || result.Height==0 || !(result.Fps>0 && result.Fps<=120))
                    throw new InvalidDataException("Invalid canvas, frame rate or frame count.");
                var images=new BitmapSource[count];
                var rectangles=new Rect[count];
                for (int i=0; i<count; i++) {
                    rectangles[i]=new Rect(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                    using (var png=Entry(zip,i+".png"))
                    using (var buffer=new MemoryStream()) {
                        // WIC defers decoding a non-seekable ZIP stream even with OnLoad.
                        png.CopyTo(buffer);
                        buffer.Position=0;
                        var image=new BitmapImage();
                        image.BeginInit();
                        image.CacheOption=BitmapCacheOption.OnLoad;
                        image.StreamSource=buffer;
                        image.EndInit();
                        image.Freeze();
                        images[i]=image;
                    }
                }
                result.Frames=new DrawingGroup[frames];
                for (int i=0;i<frames;i++) {
                    var drawing=new DrawingGroup();
                    using (var dc=drawing.Open()) {
                        byte op;
                        while ((op=reader.ReadByte())!=0) {
                            if (op==4) { dc.Pop(); continue; }
                            if (op==3) { dc.PushOpacity(reader.ReadSingle()); continue; }
                            if (op!=1 && op!=2) throw new InvalidDataException("Invalid animation layer.");
                            int index=reader.ReadUInt16();
                            if(index>=count) throw new InvalidDataException("Referenced animation layer is missing.");
                            var transform=new MatrixTransform(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),
                                reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                            if (op==2) {
                                dc.PushOpacityMask(new ImageBrush(images[index]) { ViewportUnits=BrushMappingMode.Absolute,
                                    Viewport=rectangles[index], Stretch=Stretch.Fill, Transform=transform });
                            } else {
                                dc.PushTransform(transform);
                                dc.DrawImage(images[index],rectangles[index]);
                                dc.Pop();
                            }
                        }
                    }
                    drawing.Freeze();
                    result.Frames[i]=drawing;
                }
                return result;
            }
        }
    }

    readonly Window window;
    readonly string[] files;
    readonly string scooterScene;
    readonly Random sceneRandom=new Random();
    readonly int firstLoopScene;
    readonly Action<string> log;
    readonly Stopwatch clock=new Stopwatch();
    readonly DispatcherTimer timer=new DispatcherTimer(DispatcherPriority.Render);
    readonly HashSet<string> failed=new HashSet<string>();
    Movie movie, next;
    int cursor, frame=-1;
    bool closed, loading, introReleased;
    double offset, playbackFps;

    public LoadingScreens(Window owner, string directory, Action<string> log) {
        window=owner;
        this.log=log;
        var scenes=Directory.Exists(directory) ? Directory.GetFiles(directory,"*.scene") : new string[0];
        scooterScene=scenes.FirstOrDefault(p=>String.Equals(Path.GetFileName(p),"00a-scooter.scene",StringComparison.OrdinalIgnoreCase));
        files=scenes.Where(p=>p!=scooterScene).OrderBy(p=>p,StringComparer.Ordinal).ToArray();
        firstLoopScene = files.Length > 0 && Path.GetFileNameWithoutExtension(files[0]) == "00" ? 1 : 0;
        timer.Tick+=delegate { Tick(); };
        window.Loaded+=delegate { LoadNext(); };
        window.StateChanged+=delegate { Sync(); };
        window.IsVisibleChanged+=delegate { Sync(); };
        IsVisibleChanged+=delegate { Sync(); };
        SystemParameters.StaticPropertyChanged+=MotionChanged;
        window.Closed+=delegate {
            closed=true; timer.Stop(); clock.Stop();
            SystemParameters.StaticPropertyChanged-=MotionChanged;
            movie=null; next=null;
        };
    }

    void MotionChanged(object sender,System.ComponentModel.PropertyChangedEventArgs e) {
        if(e.PropertyName=="ClientAreaAnimation") Sync();
    }

    IEnumerable<string> SceneCandidates(int index) {
        if(index>=firstLoopScene && scooterScene!=null && !failed.Contains(scooterScene) && sceneRandom.Next(100)==0)
            yield return scooterScene;
        yield return files[index];
    }

    async void LoadNext() {
        if(loading || closed || next!=null || cursor>=files.Length) return;
        loading=true;
        try {
            for(int n=0; n<files.Length && cursor<files.Length; n++) {
                int index=cursor++;
                if(cursor==files.Length) cursor=firstLoopScene;
                if(failed.Contains(files[index])) continue;
                foreach(var file in SceneCandidates(index)) {
                    try {
                        var decoded=await Task.Run(()=>Movie.Open(file));
                        if(closed) return;
                        next=decoded;
                        break;
                    } catch(Exception e) when(e is IOException || e is InvalidDataException ||
                        e is FormatException || e is ArgumentException || e is NotSupportedException || e is InvalidOperationException) {
                        failed.Add(file);
                        log("Unable to read loading animation "+Path.GetFileName(file)+": "+e.Message);
                    }
                }
                if(next!=null) break;
            }
        } finally { loading=false; }
        if(movie==null && next!=null) Advance(0);
        else if(movie==null) InvalidateVisual();
    }

    void Advance(double carry) {
        movie=next; next=null;
        frame=-1; offset=carry; clock.Reset();
        introReleased=movie.Name!="00";
        playbackFps=movie.Fps*(introReleased ? 1 : OpeningPlaybackRate);
        timer.Interval=TimeSpan.FromSeconds(1/playbackFps);
        Tick();
        Sync();
        LoadNext();
    }

    void Sync() {
        if(closed || movie==null) return;
        Visibility=SystemParameters.ClientAreaAnimation ? Visibility.Visible : Visibility.Hidden;
        if(IsVisible && window.IsVisible && window.WindowState!=WindowState.Minimized && SystemParameters.ClientAreaAnimation) {
            clock.Start(); timer.Start();
        } else {
            clock.Stop(); timer.Stop();
        }
    }

    void Tick() {
        if(movie==null) return;
        double seconds=offset+clock.Elapsed.TotalSeconds;
        if(!introReleased && seconds>=119/playbackFps) {
            // The original logo clip waits at script index 119 for playEnding().
            introReleased=next!=null || !loading;
            if(!introReleased) {
                seconds=119/playbackFps;
                offset=seconds; clock.Restart();
            }
        }
        int candidate=(int)(seconds*playbackFps+0.000001);
        if(candidate>=movie.Frames.Length && next!=null) {
            // A slow disk may leave the last frame on screen. Never skip a new clip because of that wait.
            Advance(Math.Min(1/playbackFps,seconds-movie.Frames.Length/playbackFps));
            return;
        }
        candidate=Math.Min(movie.Frames.Length-1,candidate);
        if(candidate==frame) return;
        frame=candidate;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc) {
        if(movie==null || frame<0) {
            if(files.Any(p=>!failed.Contains(p)) && SystemParameters.ClientAreaAnimation)
                dc.DrawRectangle(new SolidColorBrush(OpeningColor),null,new Rect(RenderSize));
            return;
        }
        double scale=Math.Max(ActualWidth/movie.Width,ActualHeight/movie.Height);
        if(scale<=0) return;
        dc.PushTransform(new MatrixTransform(scale,0,0,scale,(ActualWidth-movie.Width*scale)/2,(ActualHeight-movie.Height*scale)/2));
        dc.DrawRectangle(Brushes.Black,null,new Rect(0,0,movie.Width,movie.Height));
        dc.DrawDrawing(movie.Frames[frame]);
        dc.Pop();
    }
}

