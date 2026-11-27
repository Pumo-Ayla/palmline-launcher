using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

internal sealed partial class DesktopLauncher
{
    bool repositoryNoticeBusy, repositoryNoticeExpanded;

    void AttachRepositoryNotice(RoutedEventHandler openRepository) {
        var button=Find<Button>("Repository");
        if(displaySettings.RepositoryNoticeSeen) {
            button.Click+=openRepository;
            return;
        }

        var glow=Find<Ellipse>("RepositoryGlow");
        glow.Visibility=Visibility.Visible;
        glow.Opacity=.7;
        if(SystemParameters.ClientAreaAnimation) {
            var pulse=new DoubleAnimation(.36,.95,TimeSpan.FromSeconds(1.6)) {
                AutoReverse=true, RepeatBehavior=RepeatBehavior.Forever,
                EasingFunction=new SineEase { EasingMode=EasingMode.EaseInOut }
            };
            glow.BeginAnimation(UIElement.OpacityProperty,pulse);
        }
        button.SetResourceReference(FrameworkElement.ToolTipProperty,"ui.查看免费开源说明");
        button.SetResourceReference(AutomationProperties.NameProperty,"ui.查看免费开源说明");

        RoutedEventHandler firstClick=null;
        firstClick=async delegate {
            if(repositoryNoticeBusy) return;
            repositoryNoticeBusy=true;
            displaySettings.RepositoryNoticeSeen=true;
            try { displaySettings.Save(DisplayPreferences); }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException) { Append(ex.Message); }

            var banner=Find<Border>("RepositoryNotice");
            var message=Find<TextBlock>("RepositoryMessage");
            glow.BeginAnimation(UIElement.OpacityProperty,null);
            glow.Visibility=Visibility.Collapsed;
            banner.Visibility=Visibility.Visible;
            repositoryNoticeExpanded=true;
            int enter=SystemParameters.ClientAreaAnimation ? 360 : 0;
            SetRepositoryNoticeWidth(RepositoryMessageWidth(),enter);
            ControllerMotion.Fade(message,0,1,enter);
            await Task.Delay(enter);
            if(closed) return;
            await Task.Delay(TimeSpan.FromSeconds(5));
            if(closed) return;

            repositoryNoticeExpanded=false;
            int exit=SystemParameters.ClientAreaAnimation ? 260 : 0;
            ControllerMotion.Fade(message,1,0,exit);
            SetRepositoryNoticeWidth(48,exit);
            await Task.Delay(exit);
            if(closed) return;
            banner.Visibility=Visibility.Collapsed;
            button.SetResourceReference(FrameworkElement.ToolTipProperty,"ui.启动器仓库 · Pumo-Ayla/series-relay-launcher");
            button.SetResourceReference(AutomationProperties.NameProperty,"ui.打开启动器的 GitHub 仓库");
            button.Click-=firstClick;
            button.Click+=openRepository;
            repositoryNoticeBusy=false;
        };
        button.Click+=firstClick;
    }

    double RepositoryMessageWidth() {
        var message=Find<TextBlock>("RepositoryMessage");
        message.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        return 48+Math.Ceiling(message.DesiredSize.Width);
    }

    void SetRepositoryNoticeWidth(double target,int ms) {
        var banner=Find<Border>("RepositoryNotice");
        double from=banner.Width;
        banner.Width=target;
        banner.BeginAnimation(FrameworkElement.WidthProperty,ms==0 ? null :
            new DoubleAnimation(from,target,TimeSpan.FromMilliseconds(ms)) {
                EasingFunction=new CubicEase { EasingMode=EasingMode.EaseInOut },FillBehavior=FillBehavior.Stop
            });
    }
}
