using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Launcher.Layout
{
    public sealed class HistoryStatsPanel : Panel
    {
        double progressWidth, durationWidth, launchWidth;

        void ColumnWidths(double width, out double progress, out double duration) {
            // Only reserve the short clock before allocating the progress and duration.
            var launch = (FrameworkElement)InternalChildren[2];
            double budget = width - Math.Min(launch.MinWidth, width / 3);
            progress = Math.Min(progressWidth, Math.Max(budget / 2, budget - durationWidth));
            duration = Math.Min(durationWidth, budget - progress);
        }

        protected override Size MeasureOverride(Size available) {
            var children = InternalChildren;
            if (children.Count != 3) return base.MeasureOverride(available);
            var natural = new Size(double.PositiveInfinity, available.Height);
            foreach (UIElement child in children) child.Measure(natural);
            // The text and its surrounding padding round separately on scaled displays.
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            progressWidth = (Math.Ceiling(children[0].DesiredSize.Width * dpi) + 1) / dpi;
            durationWidth = (Math.Ceiling(children[1].DesiredSize.Width * dpi) + 1) / dpi;
            launchWidth = (Math.Ceiling(children[2].DesiredSize.Width * dpi) + 1) / dpi;
            double width = Math.Min(available.Width, progressWidth + durationWidth + launchWidth);
            ColumnWidths(width, out double progress, out double duration);
            children[0].Measure(new Size(progress, available.Height));
            children[1].Measure(new Size(duration, available.Height));
            children[2].Measure(new Size(Math.Max(0, width - progress - duration), available.Height));
            return new Size(width, Math.Max(children[0].DesiredSize.Height,
                Math.Max(children[1].DesiredSize.Height, children[2].DesiredSize.Height)));
        }

        protected override Size ArrangeOverride(Size finalSize) {
            var children = InternalChildren;
            if (children.Count != 3) return base.ArrangeOverride(finalSize);
            ColumnWidths(finalSize.Width, out double progress, out double duration);
            children[0].Arrange(new Rect(0, 0, progress, finalSize.Height));
            children[1].Arrange(new Rect(progress, 0, duration, finalSize.Height));
            children[2].Arrange(new Rect(progress + duration, 0,
                Math.Max(0, finalSize.Width - progress - duration), finalSize.Height));
            return finalSize;
        }
    }

    public sealed class AdaptiveLaunchTime : Panel
    {
        readonly TextBlock full = new TextBlock();
        readonly TextBlock relative = new TextBlock();
        readonly TextBlock clock = new TextBlock();
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        long timestamp;

        public AdaptiveLaunchTime() {
            ClipToBounds = true;
            Children.Add(full);
            Children.Add(relative);
            Children.Add(clock);
            foreach (TextBlock label in Children) label.TextTrimming = TextTrimming.CharacterEllipsis;
            timer.Tick += delegate { UpdateRelative(); };
            IsVisibleChanged += delegate {
                if (IsVisible && timestamp > 0) UpdateRelative();
                timer.IsEnabled = IsVisible && timestamp > 0;
            };
        }

        internal void SetTime(long value, string empty) {
            timestamp = value;
            if (timestamp > 0) {
                var local = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime();
                var culture = CultureInfo.GetCultureInfo(Ui.Language);
                full.Text = local.ToString("g", culture);
                clock.Text = local.ToString("HH:mm", culture);
                UpdateRelative();
            } else {
                full.Text = empty;
                clock.Text = relative.Text = "";
            }
            ToolTip = timestamp > 0 ? full.Text : null;
            AutomationProperties.SetName(this, full.Text);
            timer.IsEnabled = IsVisible && timestamp > 0;
        }

        void UpdateRelative() {
            long elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp;
            relative.Text = elapsed < 0 ? "" : elapsed < 60 ? Ui.Text("刚刚") :
                elapsed < 3600 ? Ui.Format("{0}分钟前", elapsed / 60) :
                elapsed < 86400 ? Ui.Format("{0}小时前", elapsed / 3600) :
                Ui.Format("{0}天前", elapsed / 86400);
        }

        protected override Size MeasureOverride(Size available) {
            foreach (TextBlock label in Children)
                label.Measure(new Size(double.PositiveInfinity, available.Height));
            return new Size(Math.Min(full.DesiredSize.Width, available.Width), full.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize) {
            TextBlock selected = full;
            if (timestamp > 0 && full.DesiredSize.Width > finalSize.Width) {
                selected = relative.Text.Length > 0 && relative.DesiredSize.Width <= finalSize.Width
                    ? relative : clock;
            }
            foreach (TextBlock label in Children) {
                label.Visibility = label == selected ? Visibility.Visible : Visibility.Hidden;
                label.Arrange(new Rect(finalSize));
            }
            return finalSize;
        }
    }
}
