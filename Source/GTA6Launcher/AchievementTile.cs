using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Launcher.Layout
{
    public sealed class AchievementTile : Decorator
    {
        public const double CompactWidth=78;
        const double TopInset=6;
        const double LiftDistance=2;
        const double ScaleGain=.06;
        static readonly DependencyProperty RevealWidthProperty=DependencyProperty.Register(
            "RevealWidth",typeof(double),typeof(AchievementTile),new FrameworkPropertyMetadata(CompactWidth,FrameworkPropertyMetadataOptions.AffectsMeasure));
        static readonly DependencyProperty HoverAmountProperty=DependencyProperty.Register(
            "HoverAmount",typeof(double),typeof(AchievementTile),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsMeasure));
        static readonly DependencyPropertyKey CaptionOpacityKey=DependencyProperty.RegisterReadOnly(
            nameof(CaptionOpacity),typeof(double),typeof(AchievementTile),new PropertyMetadata(1d));
        public static readonly DependencyProperty CaptionOpacityProperty=CaptionOpacityKey.DependencyProperty;
        public double CaptionOpacity => (double)GetValue(CaptionOpacityProperty);
        static readonly DependencyPropertyKey IsExpandedKey=DependencyProperty.RegisterReadOnly(
            nameof(IsExpanded),typeof(bool),typeof(AchievementTile),new PropertyMetadata(false));
        public static readonly DependencyProperty IsExpandedProperty=IsExpandedKey.DependencyProperty;
        public bool IsExpanded => (bool)GetValue(IsExpandedProperty);
        static readonly DependencyPropertyKey BadgeMarginKey=DependencyProperty.RegisterReadOnly(
            nameof(BadgeMargin),typeof(Thickness),typeof(AchievementTile),new PropertyMetadata(new Thickness(0,55,4,0)));
        public static readonly DependencyProperty BadgeMarginProperty=BadgeMarginKey.DependencyProperty;
        public Thickness BadgeMargin => (Thickness)GetValue(BadgeMarginProperty);

        readonly ScaleTransform scale=new ScaleTransform();
        readonly RectangleGeometry outline=new RectangleGeometry { RadiusX=10,RadiusY=10 };
        double expandedWidth=CompactWidth;

        public AchievementTile() {
            MouseEnter+=delegate { HoldShelfOffset(); Hover(true); };
            MouseLeave+=delegate { Close(true); };
            Unloaded+=delegate { Close(false); };
            DataContextChanged+=delegate { Close(false); };
            IsEnabledChanged+=delegate { if(!IsEnabled) Close(false); };
        }

        // Keep the point under the pointer active while the card lifts away from it.
        protected override HitTestResult HitTestCore(PointHitTestParameters hit) {
            return new Rect(RenderSize).Contains(hit.HitPoint) ? new PointHitTestResult(this,hit.HitPoint) : null;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
            base.OnMouseLeftButtonDown(e);
            if(IsExpanded || Child==null) return;
            HoldShelfOffset();
            Child.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
            expandedWidth=Math.Max(CompactWidth,Math.Ceiling(Child.DesiredSize.Width));
            SetValue(IsExpandedKey,true);
            Hover(true);
            Move(RevealWidthProperty,expandedWidth,220);
            e.Handled=true;
        }

        protected override Size MeasureOverride(Size availableSize) {
            double width=(double)GetValue(RevealWidthProperty);
            double factor=1+ScaleGain*(double)GetValue(HoverAmountProperty);
            Child?.Measure(new Size(width,CompactWidth));
            return new Size(width*factor,CompactWidth+2*TopInset);
        }

        protected override Size ArrangeOverride(Size finalSize) {
            if(Child==null) return finalSize;
            double width=(double)GetValue(RevealWidthProperty);
            double hover=(double)GetValue(HoverAmountProperty);
            scale.ScaleX=scale.ScaleY=1+ScaleGain*hover;
            outline.Rect=new Rect(0,0,width,CompactWidth);
            if(Child.Clip!=outline) Child.Clip=outline;
            if(Child.RenderTransform!=scale) Child.RenderTransform=scale;
            Child.Arrange(new Rect(0,TopInset-LiftDistance*hover,width,CompactWidth));
            SetValue(CaptionOpacityKey,Math.Min(1,Math.Max(0,1-(width-CompactWidth)/54)));
            double progress=Math.Min(1,Math.Max(0,(width-CompactWidth)/Math.Max(1,expandedWidth-CompactWidth)));
            SetValue(BadgeMarginKey,new Thickness(0,55-45*progress,4+6*progress,0));
            return finalSize;
        }

        void Close(bool animate) {
            SetValue(IsExpandedKey,false);
            Move(RevealWidthProperty,CompactWidth,animate ? 175 : 0);
            Hover(false,animate);
        }

        void Hover(bool over,bool animate=true) {
            Move(HoverAmountProperty,over ? 1 : 0,animate ? 135 : 0);
        }

        void HoldShelfOffset() {
            // Replace a pending scroll-to-end with the current pixel position before the extent grows.
            for(DependencyObject parent=VisualTreeHelper.GetParent(this);parent!=null;parent=VisualTreeHelper.GetParent(parent)) {
                if(parent is ScrollViewer shelf) {
                    shelf.ScrollToHorizontalOffset(shelf.HorizontalOffset);
                    break;
                }
            }
        }

        void Move(DependencyProperty property,double target,int duration) {
            double current=(double)GetValue(property);
            SetValue(property,target);
            if(duration==0 || !SystemParameters.ClientAreaAnimation || Math.Abs(current-target)<.001) {
                BeginAnimation(property,null);
                return;
            }
            BeginAnimation(property,new DoubleAnimation(current,target,TimeSpan.FromMilliseconds(duration)) {
                EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut },FillBehavior=FillBehavior.Stop
            });
        }
    }
}
