using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

internal sealed class ControllerAntialias
{
    sealed class ResolveEffect : ShaderEffect
    {
        public static readonly DependencyProperty SourceProperty = RegisterPixelShaderSamplerProperty(
            "Source", typeof(ResolveEffect), 0, SamplingMode.Bilinear);
        public static readonly DependencyProperty PixelSizeProperty = DependencyProperty.Register(
            "PixelSize", typeof(Point), typeof(ResolveEffect),
            new UIPropertyMetadata(new Point(1, 1), PixelShaderConstantCallback(0)));
        static readonly PixelShader shader=LoadShader();
        static PixelShader LoadShader() {
            var result=new PixelShader();
            using(var code=Assembly.GetExecutingAssembly().GetManifestResourceStream("ControllerResolve.ps"))
                result.SetStreamSource(code);
            result.Freeze();
            return result;
        }
        public ResolveEffect() {
            PixelShader=shader;
            UpdateShaderValue(SourceProperty);
            UpdateShaderValue(PixelSizeProperty);
        }
        protected override Freezable CreateInstanceCore() { return new ResolveEffect(); }
    }
    readonly Grid surface;
    readonly System.Windows.Media.Media3D.OrthographicCamera camera;
    bool wide, closeup, mappingStage;
    int maskRevision;
    readonly RadialGradientBrush aperture=new RadialGradientBrush {
        MappingMode=BrushMappingMode.Absolute,
        GradientStops=new GradientStopCollection {
            new GradientStop(Colors.Black,0), new GradientStop(Colors.Black,.32),
            new GradientStop(Color.FromArgb(224,0,0,0),.47), new GradientStop(Color.FromArgb(128,0,0,0),.65),
            new GradientStop(Color.FromArgb(30,0,0,0),.84), new GradientStop(Color.FromArgb(0,0,0,0),1)
        }
    };
    readonly Border source;
    readonly Rectangle display;
    readonly ResolveEffect resolve=new ResolveEffect();
    readonly BitmapCache cache = new BitmapCache();

    public ControllerAntialias(Grid surface, Viewport3D viewport) {
        this.surface=surface;
        camera=(System.Windows.Media.Media3D.OrthographicCamera)viewport.Camera;
        surface.Children.Remove(viewport);
        source=new Border { Background=Brushes.Transparent, Child=viewport };
        var brush=new BitmapCacheBrush(source) { BitmapCache=cache, AutoLayoutContent=false };
        resolve.SetValue(ResolveEffect.SourceProperty,brush);
        display=new Rectangle { Fill=Brushes.Transparent, Effect=resolve, IsHitTestVisible=false,
            HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center };
        surface.Children.Add(display);
        surface.SizeChanged+=delegate { Resize(); };
        surface.IsVisibleChanged+=delegate { if(surface.IsVisible) Resize(); };
        var window=Window.GetWindow(surface);
        if(window!=null) window.DpiChanged+=delegate(object sender,DpiChangedEventArgs e) { Resize(e.NewDpi); };
    }

    void Resize() { Resize(VisualTreeHelper.GetDpi(surface)); }
    public void SetCloseup(bool value,TimeSpan duration) {
        if(value==closeup) return;
        closeup=value;
        int revision=++maskRevision;
        bool wasMasked=surface.OpacityMask!=null;
        bool animate=duration>TimeSpan.Zero;
        surface.OpacityMask=value || animate ? aperture : null;
        Resize();
        // The opaque centre never moves. Reveal the surrounding model as the camera pulls back.
        for(int i=2;i<aperture.GradientStops.Count;i++) {
            var stop=aperture.GradientStops[i];
            Color start=wasMasked ? stop.Color : Colors.Black;
            Color end=value ? (Color)stop.GetAnimationBaseValue(GradientStop.ColorProperty) : Colors.Black;
            var fade=animate ? new ColorAnimation(start,end,duration) {
                EasingFunction=new SineEase {EasingMode=EasingMode.EaseInOut},
                FillBehavior=value ? FillBehavior.Stop : FillBehavior.HoldEnd
            } : null;
            if(animate && !value && i==aperture.GradientStops.Count-1) fade.Completed+=delegate {
                if(revision!=maskRevision) return;
                surface.OpacityMask=null;
                Resize();
            };
            stop.BeginAnimation(GradientStop.ColorProperty,fade);
        }
    }
    public void SetWide(bool value) {
        if(value==wide) return;
        wide=value;
        if(!mappingStage) Resize();
    }
    public void SetMappingStage(bool value) {
        if(mappingStage==value) return;
        mappingStage=value;
        Resize();
    }
    void Resize(DpiScale dpi) {
        if(surface.ActualHeight<=0 || surface.ActualWidth<=0) return;
        // Expand the resolve surface for two models; retain the same world scale.
        double size=2.36*Math.Min(surface.ActualWidth/2.82,surface.ActualHeight/2.50);
        if(wide && !mappingStage) size=Math.Min(size,surface.ActualWidth*2.36/3.46);
        // Keep the full render target through the return shot, including any intervening resize.
        bool fullStage=surface.OpacityMask!=null || mappingStage;
        double width=fullStage ? surface.ActualWidth : wide ? size*3.46/2.36 : size;
        double height=fullStage ? surface.ActualHeight : size;
        camera.Width=2.36*width/size;
        display.Width=width; display.Height=height;
        if(fullStage) {
            var center=new Point(width*.5,height*.5);
            double span=Math.Min(width,height);
            aperture.Center=aperture.GradientOrigin=center;
            aperture.RadiusX=span*.62;
            aperture.RadiusY=span*.47;
        }
        var bounds=new Size(width,height);
        source.Width=width; source.Height=height;
        source.Measure(bounds);
        source.Arrange(new Rect(bounds));
        resolve.SetValue(ResolveEffect.PixelSizeProperty,new Point(1/(width*dpi.DpiScaleX),1/(height*dpi.DpiScaleY)));
        // A detached visual is measured in DIPs. Bound the backing texture
        // on high-DPI/large windows while leaving room for the render target.
        double nativeScale=Math.Max(dpi.DpiScaleX,dpi.DpiScaleY);
        Size texture=RenderCapability.MaxHardwareTextureSize;
        bool hardware=(RenderCapability.Tier>>16)>0;
        double limit=Math.Min(hardware ? 4096 : 2048,Math.Min(texture.Width,texture.Height));
        if(limit<=0) limit=2048;
        double scale=Math.Min((hardware ? 3 : 2)*nativeScale,limit/Math.Max(width,height));
        cache.RenderAtScale=mappingStage ? Math.Min(scale,Math.Sqrt(4000000/(width*height))) : scale;
    }
}
