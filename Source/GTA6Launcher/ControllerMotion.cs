using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

internal static class ControllerMotion
{
    public static void Fade(UIElement element, double from, double to, int ms, Action done=null) {
        element.Opacity=to;
        if(!SystemParameters.ClientAreaAnimation) {
            element.BeginAnimation(UIElement.OpacityProperty,null);
            if(done!=null) done();
            return;
        }
        var fade=new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(ms)) {
            EasingFunction=new QuadraticEase { EasingMode=EasingMode.EaseOut }, FillBehavior=FillBehavior.Stop
        };
        if(done!=null) fade.Completed+=delegate { done(); };
        element.BeginAnimation(UIElement.OpacityProperty,fade);
    }

    // The fast first stroke crosses the resting size once; the longer tail settles it.
    public static void Reveal(FrameworkElement target, double start, double peak, double offset, int ms, double alpha) {
        target.RenderTransformOrigin=new Point(.5,.5);
        if(!SystemParameters.ClientAreaAnimation) {
            target.RenderTransform=Transform.Identity;
            Fade(target,1,1,0);
            return;
        }
        var scale=new ScaleTransform();
        var slide=new TranslateTransform();
        var transform=new TransformGroup();
        transform.Children.Add(scale); transform.Children.Add(slide);
        target.RenderTransform=transform;
        var size=Spring(start,peak,1,ms);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,size);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,size);
        if(offset!=0) slide.BeginAnimation(TranslateTransform.XProperty,Spring(offset,-offset*.09,0,ms));
        Fade(target,alpha,1,ms/2);
    }
    internal static DoubleAnimationUsingKeyFrames Spring(double start,double peak,double rest,int ms,int delayMs=0) {
        var keys=new DoubleAnimationUsingKeyFrames { FillBehavior=FillBehavior.Stop };
        keys.KeyFrames.Add(new DiscreteDoubleKeyFrame(start,TimeSpan.Zero));
        if(delayMs>0) keys.KeyFrames.Add(new DiscreteDoubleKeyFrame(start,TimeSpan.FromMilliseconds(delayMs)));
        keys.KeyFrames.Add(new SplineDoubleKeyFrame(peak,TimeSpan.FromMilliseconds(delayMs+ms*.49),new KeySpline(.2,.73,.28,1)));
        keys.KeyFrames.Add(new SplineDoubleKeyFrame(rest,TimeSpan.FromMilliseconds(delayMs+ms),new KeySpline(.24,.1,.24,1)));
        return keys;
    }
    public static void Dismiss(FrameworkElement target,double to,int ms,double alpha,Action done) {
        double initial=target.RenderTransform.Value.M11;
        if(SystemParameters.ClientAreaAnimation) {
            target.RenderTransformOrigin=new Point(.5,.5);
            var scale=new ScaleTransform(to,to);
            target.RenderTransform=scale;
            var size=new DoubleAnimation(initial,to,TimeSpan.FromMilliseconds(ms)) {
                EasingFunction=new QuadraticEase { EasingMode=EasingMode.EaseIn }, FillBehavior=FillBehavior.Stop
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,size);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,size);
        } else target.RenderTransform=Transform.Identity;
        Fade(target,target.Opacity,alpha,ms,done);
    }
}
