using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

internal sealed class RunningColors
{
    readonly DrawingGroup colors = new DrawingGroup();
    readonly EllipseGeometry[] orbs;
    readonly Random random = new Random();
    bool moving;
    int generation;

    public DrawingBrush Fill { get; private set; }

    public RunningColors(Brush green, Brush sunset) {
        var bounds = new Rect(0, 0, 210, 54);
        var area = new RectangleGeometry(bounds);
        var layers = new DrawingGroup { ClipGeometry = area };
        layers.Children.Add(new GeometryDrawing(green, null, area));
        layers.Children.Add(colors);
        colors.Children.Add(new GeometryDrawing(sunset, null, area));
        orbs = new[] {
            AddOrb(36, 12, 49, Color.FromRgb(178, 142, 255)),
            AddOrb(108, 39, 42, Color.FromRgb(255, 77, 166)),
            AddOrb(158, 9, 45, Color.FromRgb(255, 155, 132))
        };
        Fill = new DrawingBrush(layers) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds };
    }

    EllipseGeometry AddOrb(double x, double y, double radius, Color color) {
        var light = new RadialGradientBrush();
        light.GradientStops.Add(new GradientStop(color, 0));
        color.A = 224;
        light.GradientStops.Add(new GradientStop(color, .31));
        color.A = 102;
        light.GradientStops.Add(new GradientStop(color, .67));
        color.A = 0;
        light.GradientStops.Add(new GradientStop(color, 1));
        light.Freeze();
        var orb = new EllipseGeometry(new Point(x, y), radius, radius);
        colors.Children.Add(new GeometryDrawing(light, null, orb));
        return orb;
    }

    public void Reveal() {
        colors.BeginAnimation(DrawingGroup.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360)) {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop
        });
    }

    public void SetMoving(bool value) {
        if (moving == value) return;
        moving = value;
        ++generation;
        if (moving) {
            Move(orbs[0], 5, 128, generation);
            Move(orbs[1], 38, 177, generation);
            Move(orbs[2], 86, 200, generation);
            return;
        }
        colors.BeginAnimation(DrawingGroup.OpacityProperty, null);
        foreach (var orb in orbs) {
            Point center = orb.Center;
            orb.BeginAnimation(EllipseGeometry.CenterProperty, null);
            orb.Center = center;
        }
    }

    void Move(EllipseGeometry orb, double left, double right, int version) {
        Point from = orb.Center;
        double x;
        do { x = left + random.NextDouble() * (right - left); }
        while (Math.Abs(x - from.X) < (right - left) * .26);
        var to = new Point(x, 3 + random.NextDouble() * 48);
        var bend = new Point((from.X + to.X) * .5, random.NextDouble() * 84 - 15);
        var path = new PathGeometry();
        var figure = new PathFigure { StartPoint = from };
        figure.Segments.Add(new QuadraticBezierSegment(bend, to, true));
        path.Figures.Add(figure);
        var glide = new PointAnimationUsingPath {
            PathGeometry = path, Duration = TimeSpan.FromSeconds(3.2 + random.NextDouble() * 2.2),
            AccelerationRatio = .38, DecelerationRatio = .38, FillBehavior = FillBehavior.Stop
        };
        orb.Center = to;
        glide.Completed += delegate { if (version == generation) Move(orb, left, right, version); };
        orb.BeginAnimation(EllipseGeometry.CenterProperty, glide);
    }
}
