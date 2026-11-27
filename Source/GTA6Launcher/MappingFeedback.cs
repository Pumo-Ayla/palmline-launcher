using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

internal sealed class MappingFeedback
{
    readonly StackPanel dots;
    readonly Border success;
    readonly TextBlock label;
    readonly Path ring, tick;
    public MappingFeedback(Window window) {
        dots=(StackPanel)window.FindName("WaitingDots");
        success=(Border)window.FindName("BindingSuccess");
        label=(TextBlock)window.FindName("SuccessLabel");
        ring=(Path)window.FindName("SuccessRing");
        tick=(Path)window.FindName("SuccessTick");
        foreach(Ellipse dot in dots.Children) dot.RenderTransform=new TranslateTransform();
    }

    public void Waiting(bool waiting) {
        dots.Visibility=waiting ? Visibility.Visible : Visibility.Collapsed;
        int delay=0;
        foreach(Ellipse dot in dots.Children) {
            bool animate=waiting && SystemParameters.ClientAreaAnimation;
            var lift=(TranslateTransform)dot.RenderTransform;
            lift.BeginAnimation(TranslateTransform.YProperty,animate ? Wave(0,-4.5,delay) : null);
            dot.BeginAnimation(UIElement.OpacityProperty,animate ? Wave(.42,1,delay) : null);
            delay+=135;
        }
    }
    static DoubleAnimationUsingKeyFrames Wave(double rest,double peak,int delay) {
        var animation=new DoubleAnimationUsingKeyFrames { Duration=TimeSpan.FromMilliseconds(1000),RepeatBehavior=RepeatBehavior.Forever };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(rest,TimeSpan.Zero));
        if(delay>0) animation.KeyFrames.Add(new LinearDoubleKeyFrame(rest,TimeSpan.FromMilliseconds(delay)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(peak,TimeSpan.FromMilliseconds(delay+180),new KeySpline(.22,.68,.25,1)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(rest,TimeSpan.FromMilliseconds(delay+420),new KeySpline(.4,0,.4,1)));
        return animation;
    }
    public static void AttachHover(Button button) {
        button.ApplyTemplate();
        var face=(FrameworkElement)button.Template.FindName("face",button);
        var size=new ScaleTransform();
        face.RenderTransform=size;
        button.MouseEnter+=delegate { Scale(size,1.035,190); };
        button.MouseLeave+=delegate { Scale(size,1,170); };
        button.IsVisibleChanged+=delegate { if(!button.IsVisible) Scale(size,1,0); };
    }
    public static void Scale(ScaleTransform size,double end,int ms) {
        double start=size.ScaleX;
        size.ScaleX=size.ScaleY=end;
        var motion=ms>0 && SystemParameters.ClientAreaAnimation
            ? ControllerMotion.Spring(start,end+(end-start)*.12,end,ms) : null;
        size.BeginAnimation(ScaleTransform.ScaleXProperty,motion);
        size.BeginAnimation(ScaleTransform.ScaleYProperty,motion);
    }
    public void Complete() {
        success.Visibility=Visibility.Visible;
        // Measure without the compact badge constraining the translated label.
        label.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        double width=Math.Ceiling(label.DesiredSize.Width);
        success.Width=width;
        bool motion=SystemParameters.ClientAreaAnimation;
        ring.BeginAnimation(Shape.StrokeDashOffsetProperty,motion ? Draw(45,0,320) : null);
        tick.BeginAnimation(Shape.StrokeDashOffsetProperty,motion ? Draw(10,310,230) : null);
        label.BeginAnimation(UIElement.OpacityProperty,motion ? ControllerMotion.Spring(0,1,1,280,560) : null);
        success.BeginAnimation(FrameworkElement.WidthProperty,motion ? ControllerMotion.Spring(44,width+3,width,415,540) : null);
    }
    static DoubleAnimationUsingKeyFrames Draw(double gap,int delay,int ms) {
        var animation=new DoubleAnimationUsingKeyFrames {FillBehavior=FillBehavior.Stop};
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(gap,TimeSpan.Zero));
        if(delay>0) animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(gap,TimeSpan.FromMilliseconds(delay)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(0,TimeSpan.FromMilliseconds(delay+ms),new KeySpline(.32,.08,.25,1)));
        return animation;
    }
}
