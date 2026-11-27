using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

internal sealed class ControllerViewer
{
    static readonly TimeSpan ShotDuration=TimeSpan.FromMilliseconds(390);
    readonly Viewport3D viewport;
    readonly Grid surface;
    readonly OrthographicCamera camera = new OrthographicCamera(
        new Point3D(0, 0, 4), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 3);
    enum Angle { Front, UpperRear, Both }
    readonly QuaternionRotation3D sensor = new QuaternionRotation3D(Quaternion.Identity);
    readonly ControllerAntialias antialias;
    readonly Shot primary, secondary;
    readonly Dictionary<string, MovingControl> controls = new Dictionary<string, MovingControl>();
    Model3DGroup model;
    Angle angle;
    bool overview;
    MovingControl guided;
    uint previous;
    readonly long[] lastPress=new long[22];

    sealed class Shot {
        public readonly ModelVisual3D Visual = new ModelVisual3D();
        readonly ScaleTransform3D scale;
        readonly TranslateTransform3D position=new TranslateTransform3D();
        readonly QuaternionRotation3D heading=new QuaternionRotation3D(Quaternion.Identity);
        public Shot(QuaternionRotation3D sensor, double size) {
            scale=new ScaleTransform3D(size,size,size);
            var transform=new Transform3DGroup();
            transform.Children.Add(new RotateTransform3D(sensor));
            transform.Children.Add(new RotateTransform3D(heading));
            transform.Children.Add(scale); transform.Children.Add(position);
            Visual.Transform=transform;
        }
        public static Quaternion Orientation(double yaw,double tilt) {
            return new Quaternion(new Vector3D(1,0,0),tilt)*new Quaternion(new Vector3D(0,1,0),yaw);
        }
        public void Move(double yaw,double tilt,double x,double size,bool animate,Action done=null,double y=0) {
            var target=Orientation(yaw,tilt);
            var start=heading.Quaternion;
            heading.Quaternion=target;
            var ease=new BackEase { EasingMode=EasingMode.EaseOut, Amplitude=.27 };
            var rotation=animate ? new QuaternionAnimation(start,target,ShotDuration) {
                EasingFunction=ease, FillBehavior=FillBehavior.Stop } : null;
            if(animate && done!=null) rotation.Completed+=delegate { done(); };
            heading.BeginAnimation(QuaternionRotation3D.QuaternionProperty,rotation);
            Action<Animatable,DependencyProperty,double> slide=(part,prop,to)=>{
                double from=(double)part.GetValue(prop);
                part.SetValue(prop,to);
                part.BeginAnimation(prop,animate ? new DoubleAnimation(from,to,ShotDuration) {
                    EasingFunction=ease, FillBehavior=FillBehavior.Stop } : null);
            };
            slide(position,TranslateTransform3D.OffsetXProperty,x);
            slide(position,TranslateTransform3D.OffsetYProperty,y);
            slide(scale,ScaleTransform3D.ScaleXProperty,size); slide(scale,ScaleTransform3D.ScaleYProperty,size);
            slide(scale,ScaleTransform3D.ScaleZProperty,size);
            if(!animate && done!=null) done();
        }
    }

    public ControllerViewer(Grid surface, Viewport3D viewport)
    {
        this.viewport = viewport;
        this.surface = surface;
        viewport.Camera = camera;
        camera.Width=2.36;
        antialias=new ControllerAntialias(surface,viewport);
        primary=new Shot(sensor,1); secondary=new Shot(sensor,0);
        secondary.Move(154,29,.83,0,false);
        surface.SizeChanged+=delegate { if(overview) SetShot(Angle.Both,false); };
    }

    public void Show()
    {
        if (viewport.Children.Count == 0) {
            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(125, 130, 136)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(244, 237, 228), new Vector3D(.45, -.8, -1.0)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(152, 178, 197), new Vector3D(-1, .22, -.65)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(210, 222, 235), new Vector3D(-.35, -.7, 1)));
            lights.Freeze();
            model = LoadModel();
            viewport.Children.Add(new ModelVisual3D { Content = lights });
            primary.Visual.Content=model;
            viewport.Children.Add(primary.Visual);
            viewport.Children.Add(secondary.Visual);
            if(angle==Angle.Both) secondary.Visual.Content=model;
        }
        surface.Focus();
    }

    public void Sync(PadState state, Quaternion pose)
    {
        if(!overview) UpdateShot(state);
        // Retain live gyro feedback while keeping the tested surfaces in view.
        var axis=new Vector3D(pose.X,pose.Y,pose.Z)*(2*pose.W);
        var tilt=axis.Length<.00001 ? Quaternion.Identity :
            new Quaternion(axis,24*Math.Tanh(axis.Length*180/Math.PI/24));
        if(sensor.Quaternion!=tilt) sensor.Quaternion=tilt;
        foreach (var pair in controls) pair.Value.Apply(pair.Key, state);
    }
    public void Recenter() { sensor.Quaternion=Quaternion.Identity; }
    bool closeup;
    void StopHighlight() {
        if(guided!=null) {
            guided.Glow.BeginAnimation(Brush.OpacityProperty,null);
            guided.Glow.Opacity=0;
            guided=null;
        }
    }
    public void FinishGuide() {
        StopHighlight();
        bool animate=SystemParameters.ClientAreaAnimation && surface.IsVisible;
        antialias.SetCloseup(false,animate ? ShotDuration : TimeSpan.Zero);
        foreach(var part in controls) part.Value.Apply(part.Key,new PadState());
        primary.Move(-32,10,0,1.10,animate);
    }
    public void Guide(MappingStep step) {
        bool wasGuiding=closeup;
        StopHighlight();
        closeup=step!=null;
        bool animate=SystemParameters.ClientAreaAnimation && surface.IsVisible;
        antialias.SetCloseup(closeup,animate ? ShotDuration : TimeSpan.Zero);
        if(step==null) {
            if(wasGuiding) primary.Move(0,0,0,1,animate);
            else ResetView();
        }
        var demo=step==null ? new PadState() : step.Demo;
        foreach(var part in controls) part.Value.Apply(part.Key,demo);
        if(step==null || !controls.TryGetValue(step.Joint,out guided)) return;
        var bounds=guided.Bounds;
        double x=bounds.X+bounds.SizeX/2;
        double yaw=step.Shoulder ? (x<0 ? 148 : -148) : -x*24;
        double tilt=step.Shoulder ? 49 : 8;
        double zoom=step.Shoulder ? 1.82 : 1.68;
        var center=new Point3D(x,bounds.Y+bounds.SizeY/2,bounds.Z+bounds.SizeZ/2);
        var facing=Matrix3D.Identity; facing.Rotate(Shot.Orientation(yaw,tilt));
        center=facing.Transform(guided.Motion.Transform(center));
        primary.Move(yaw,tilt,-center.X*zoom,zoom,animate,null,-center.Y*zoom);
        guided.Glow.Opacity=.42;
        if(SystemParameters.ClientAreaAnimation) {
            var breath=new DoubleAnimationUsingKeyFrames { RepeatBehavior=RepeatBehavior.Forever };
            breath.KeyFrames.Add(new LinearDoubleKeyFrame(.34,TimeSpan.Zero));
            breath.KeyFrames.Add(new SplineDoubleKeyFrame(.54,TimeSpan.FromMilliseconds(1050),new KeySpline(.42,0,.58,1)));
            breath.KeyFrames.Add(new SplineDoubleKeyFrame(.34,TimeSpan.FromMilliseconds(2450),new KeySpline(.42,0,.58,1)));
            breath.KeyFrames.Add(new LinearDoubleKeyFrame(.34,TimeSpan.FromMilliseconds(2700)));
            guided.Glow.BeginAnimation(Brush.OpacityProperty,breath);
        }
    }
    public void ResetView() {
        previous=0; Array.Clear(lastPress,0,lastPress.Length); Recenter();
        SetShot(overview ? Angle.Both : Angle.Front,false);
    }
    public void SetOverview(bool mappingStage,bool enabled,bool animate) {
        antialias.SetMappingStage(mappingStage);
        if(overview==enabled) return;
        overview=enabled;
        previous=0; Array.Clear(lastPress,0,lastPress.Length); Recenter();
        SetShot(enabled ? Angle.Both : Angle.Front,animate,true);
    }
    uint AxisEdge(double value,int bit,double threshold) {
        uint flag=1u<<bit;
        return value>((previous&flag)==0 ? threshold : threshold*.65) ? flag : 0;
    }
    void UpdateShot(PadState state) {
        const uint paddles=0xF0000, shoulders=(3u<<9)|(3u<<20);
        uint current=state.Buttons | AxisEdge(state.LT,20,.12) | AxisEdge(state.RT,21,.12)
            | AxisEdge(Math.Max(Math.Abs(state.LX),Math.Abs(state.LY)),22,.27)
            | AxisEdge(Math.Max(Math.Abs(state.RX),Math.Abs(state.RY)),23,.27);
        uint rising=current & ~previous;
        previous=current;
        if(rising==0) return;
        long now=Stopwatch.GetTimestamp();
        // Drivers can deliver the paddle and its mapped outputs on adjacent polls.
        uint combined=0;
        for(int bit=0;bit<lastPress.Length;bit++) {
            uint flag=1u<<bit;
            if((rising&flag)!=0) lastPress[bit]=now;
            if((current&flag)!=0 && now-lastPress[bit]<=Stopwatch.Frequency*.075) combined|=flag;
        }
        const uint outputs=0xFFFF|(3u<<20);
        uint mapped=combined&outputs;
        bool paddleActive=(combined&paddles)!=0;
        bool paddleChord=paddleActive && (mapped&(mapped-1))!=0;
        // On remap-only hardware only the simultaneous logical outputs are observable.
        uint logical=rising & outputs;
        bool simultaneous=!state.ReportsPaddles && (logical & (logical-1))!=0;
        if(paddleChord || simultaneous) SetShot(Angle.Both,true);
        else if(paddleActive || (rising&shoulders)!=0) SetShot(Angle.UpperRear,true);
        else SetShot(Angle.Front,true);
    }
    void SetShot(Angle target,bool animate,bool force=false) {
        if(target==angle && animate && !force) return;
        angle=target;
        animate=animate && SystemParameters.ClientAreaAnimation && surface.IsVisible && model!=null;
        if(target==Angle.Both) {
            antialias.SetWide(true);
            secondary.Visual.Content=model;
            double scale=1, y=0;
            if(overview && surface.ActualWidth>0 && surface.ActualHeight>0) {
                var rows=((Grid)surface.Parent).RowDefinitions;
                double upperHeight=surface.ActualHeight*rows[0].Height.Value/(rows[0].Height.Value+rows[1].Height.Value);
                double unitsPerDip=camera.Width/surface.ActualWidth;
                scale=unitsPerDip*Math.Min(surface.ActualWidth/3.46,upperHeight/1.42);
                y=(surface.ActualHeight-upperHeight)*.5*unitsPerDip;
            }
            primary.Move(overview ? 0 : -28,overview ? 0 : 11,-.83*scale,.76*scale,animate,null,y);
            secondary.Move(overview ? 180 : 154,overview ? 36 : 29,.83*scale,.76*scale,animate,null,y);
        } else {
            bool rear=target==Angle.UpperRear;
            primary.Move(rear ? 158 : 0,rear ? 53 : 0,0,1,animate);
            if(secondary.Visual.Content!=null || !animate)
                secondary.Move(154,29,.83,0,animate,delegate {
                    if(angle!=Angle.Both) { secondary.Visual.Content=null; antialias.SetWide(false); }
                });
        }
    }

    sealed class MovingControl
    {
        public readonly SolidColorBrush Glow = new SolidColorBrush(Color.FromRgb(129, 217, 205)) { Opacity=0 };
        public readonly Transform3DGroup Motion = new Transform3DGroup();
        readonly QuaternionRotation3D rotation = new QuaternionRotation3D(Quaternion.Identity);
        readonly TranslateTransform3D travel = new TranslateTransform3D();
        readonly RotateTransform3D hinge;
        public Rect3D Bounds = Rect3D.Empty;
        double lastX, lastY, lastPress;
        public MovingControl() {
            hinge = new RotateTransform3D(rotation);
            Motion.Children.Add(hinge);
            Motion.Children.Add(travel);
        }
        public void SetPivot(string name) {
            bool trigger=name=="LT" || name=="RT";
            // The short-travel bumper rocks on its inboard end next to the fixed bridge.
            double pivotX = name=="LB" ? .96 : name=="RB" ? .04 : .5;
            hinge.CenterX = Bounds.X + Bounds.SizeX * pivotX;
            // The Gen3 trigger pivots under the bumper, near the face of the
            // controller. A pivot in the rear cap swings it out of its socket.
            hinge.CenterY = Bounds.Y + Bounds.SizeY * (trigger ? .45 : .5);
            hinge.CenterZ = Bounds.Z + Bounds.SizeZ * (trigger ? .70 : .28);
        }
        public void Apply(string name, PadState pad) {
            double x=0, y=0, press;
            switch(name) {
                case "LS": case "RS":
                    bool left = name=="LS";
                    x=left ? pad.LX : pad.RX; y=left ? pad.LY : pad.RY;
                    press=pad.Button(left ? 7 : 8); break;
                case "Dpad":
                    x=pad.Button(14)-pad.Button(13); y=pad.Button(11)-pad.Button(12);
                    press=(x!=0 || y!=0) ? 1 : 0; break;
                case "LT": press=pad.LT; break;
                case "RT": press=pad.RT; break;
                default: press=pad.Button(Array.IndexOf(PadState.ButtonNames,name)); break;
            }
            if (x==lastX && y==lastY && press==lastPress) return;
            lastX=x; lastY=y; lastPress=press;
            bool trigger = name=="LT" || name=="RT";
            bool bumper = name=="LB" || name=="RB";
            var axis = new Vector3D(-y,x,0);
            if (trigger) rotation.Quaternion=new Quaternion(new Vector3D(1,0,0), -11*press);
            else if (bumper) rotation.Quaternion=new Quaternion(new Vector3D(0,0,1), (name=="LB" ? 1.5 : -1.5)*press);
            else if (axis.LengthSquared>0) rotation.Quaternion=new Quaternion(axis, Math.Min(1,axis.Length)*(name=="Dpad" ? 12 : 23));
            else rotation.Quaternion=Quaternion.Identity;
            bool faceButton = name=="A" || name=="B" || name=="X" || name=="Y";
            travel.OffsetZ = trigger || bumper || name=="Dpad" ? 0 : -(faceButton ? .016 : .032)*press;
            Glow.Opacity=(faceButton ? .42 : .32)*press;
        }
    }

    static string ReadName(BinaryReader reader) {
        string name = "";
        for (byte c; (c=reader.ReadByte())!=0;) name+=(char)c;
        return name;
    }

    Model3DGroup LoadModel()
    {
        var model = new Model3DGroup();
        var clearParts = new List<GeometryModel3D>();
        var textures = new Dictionary<string, Brush>();
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("XboxControllerGen3.zip"))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        using (var reader = new BinaryReader(new BufferedStream(archive.GetEntry("mesh.bin").Open()))) {
            int subsets = reader.ReadInt32();
            for (int part = 0; part < subsets; part++) {
                int count = reader.ReadInt32(), indices = reader.ReadInt32();
                var tint = Color.FromRgb(reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                byte alpha = reader.ReadByte();
                float power = reader.ReadSingle();
                string name = ReadName(reader), control = ReadName(reader);
                Brush diffuse;
                if (name.Length == 0) diffuse = new SolidColorBrush(tint) { Opacity = alpha / 255.0 };
                else if (!textures.TryGetValue(name, out diffuse)) {
                    using (var image = archive.GetEntry(name).Open())
                    using (var png = new MemoryStream()) {
                        // WIC needs a seekable stream to decode synchronously before the ZIP closes.
                        image.CopyTo(png);
                        png.Position = 0;
                        var source = BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        source.Freeze();
                        diffuse = new ImageBrush(source) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0,0,1,1) };
                        diffuse.Freeze();
                        textures.Add(name, diffuse);
                    }
                }
                var finish = new MaterialGroup();
                finish.Children.Add(new DiffuseMaterial(diffuse));
                // The transparent face-button caps get a softer reflection so their letters stay visible.
                byte shine = alpha < 255 ? (byte)22 : (byte)67;
                finish.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(shine, shine, shine)),
                    Math.Max(14, Math.Min(110, power))));
                var positions = new Point3DCollection(count);
                var normals = new Vector3DCollection(count);
                var uv = new PointCollection(count);
                for (int i = 0; i < count; i++) {
                    positions.Add(new Point3D(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
                    normals.Add(new Vector3D(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
                    uv.Add(new Point(reader.ReadSingle(), reader.ReadSingle()));
                }
                var faces = new Int32Collection(indices);
                for (int i = 0; i < indices; i++) faces.Add(reader.ReadInt32());
                var mesh = new MeshGeometry3D { Positions=positions, Normals=normals, TextureCoordinates=uv, TriangleIndices=faces };
                var geometry = new GeometryModel3D(mesh, finish);
                mesh.Freeze();
                if (control.Length == 0) geometry.Freeze();
                else {
                    MovingControl joint;
                    if (!controls.TryGetValue(control, out joint)) controls.Add(control, joint=new MovingControl());
                    joint.Bounds.Union(mesh.Bounds);
                    finish.Children.Add(new EmissiveMaterial(joint.Glow));
                    geometry.Transform=joint.Motion;
                }
                if (alpha < 255) clearParts.Add(geometry);
                else model.Children.Add(geometry);
            }
        }
        foreach (var cap in clearParts) model.Children.Add(cap);
        foreach (var pair in controls) pair.Value.SetPivot(pair.Key);
        return model;
    }
}
