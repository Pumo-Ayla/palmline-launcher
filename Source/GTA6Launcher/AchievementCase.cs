using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

internal sealed partial class DesktopLauncher
{
    // Horizontal balance point of the opaque artwork in GameTitle.png.
    const double DefaultMarkCenter=.589;
    bool caseExpanded;
    double caseProgress, caseVelocity, caseMotionStart, caseStartVelocity;
    double caseExtraHeight;
    readonly Stopwatch caseMotionTime=new Stopwatch();
    int caseReadRevision;
    string caseSignature;
    bool showingAchievementOverview;

    void SetupAchievementCase() {
        var cover=Find<System.Windows.Shapes.Rectangle>("CoverPrint");
        var print=new ImageBrush(FullBleedCover(Artwork("GtaViBoxIllustration.png",Int32Rect.Empty))) { Stretch=Stretch.UniformToFill };
        print.Freeze();
        cover.Fill=print;
        RenderOptions.SetBitmapScalingMode(cover,BitmapScalingMode.HighQuality);
        cover.SizeChanged+=delegate { PositionCoverLogo(); };
        var reveal=Find<Grid>("CaseReveal");
        reveal.Visibility=Visibility.Collapsed;
        var area=Find<Grid>("CardArea");
        var summary=Find<StackPanel>("CaseSummary");
        Action sizeCase=delegate {
            caseExtraHeight=Math.Max(0,area.ActualHeight-summary.ActualHeight-summary.Margin.Top-summary.Margin.Bottom);
            SizeCasePanels();
            PositionCoverLogo();
        };
        area.SizeChanged+=delegate { sizeCase(); };
        summary.SizeChanged+=delegate { sizeCase(); };
        Find<StackPanel>("ShelfContent").SizeChanged+=delegate { SizeCasePanels(); };
        sizeCase();
        var rows=Find<ItemsControl>("AchievementRows");
        rows.PreviewMouseWheel+=(sender,e)=> {
            var scroll=(ScrollViewer)rows.Template.FindName("CaseScroll",rows);
            double offset=Math.Max(0,Math.Min(scroll.ScrollableWidth,scroll.HorizontalOffset-e.Delta));
            scroll.ScrollToHorizontalOffset(offset);
            e.Handled=true;
        };
        var dock=Find<Grid>("Dock");
        dock.RenderTransform.Changed+=delegate { PositionCoverLogo(); };
        dock.MouseEnter+=delegate { if(caseExpanded) SetCoverZoom(true); };
        dock.MouseLeave+=delegate { if(caseExpanded) SetCoverZoom(false); };
        Find<Button>("CaseToggle").Click+=delegate { SetAchievementCase(!caseExpanded,true); };
        Find<Button>("AchievementViewToggle").Click+=delegate { SwitchAchievementView(); };
        window.Closed+=delegate { StopCaseMotion(); };
    }

    void SwitchAchievementView() {
        showingAchievementOverview=!showingAchievementOverview;
        bool overview=showingAchievementOverview;
        var frame=Find<Grid>("AchievementViews");
        var list=Find<ItemsControl>("AchievementRows");
        var matrix=Find<ItemsControl>("AchievementOverview");
        var incoming=overview ? matrix : list;
        var outgoing=overview ? list : matrix;
        var toggle=Find<Button>("AchievementViewToggle");
        string action=overview ? "ui.切换到成就列表" : "ui.切换到成就总览";
        toggle.SetResourceReference(FrameworkElement.ToolTipProperty,action);
        toggle.SetResourceReference(AutomationProperties.NameProperty,action);
        var gridGlyph=Find<UIElement>("OverviewGlyph");
        var listGlyph=Find<UIElement>("ListGlyph");
        ControllerMotion.Fade(gridGlyph,gridGlyph.Opacity,overview ? 0 : 1,150);
        ControllerMotion.Fade(listGlyph,listGlyph.Opacity,overview ? 1 : 0,150);

        double from=frame.ActualHeight;
        incoming.Visibility=Visibility.Visible;
        outgoing.IsEnabled=false;
        incoming.IsEnabled=true;
        // Keep the header and cover moving with the shelf when the two views have different heights.
        if(SystemParameters.ClientAreaAnimation && frame.ActualWidth>0) {
            incoming.Measure(new Size(frame.ActualWidth,double.PositiveInfinity));
            frame.BeginAnimation(FrameworkElement.HeightProperty,new DoubleAnimation(from,incoming.DesiredSize.Height,TimeSpan.FromMilliseconds(210)) {
                EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut },FillBehavior=FillBehavior.Stop
            });
        } else frame.BeginAnimation(FrameworkElement.HeightProperty,null);
        ControllerMotion.Fade(incoming,incoming.Opacity,1,200);
        ControllerMotion.Fade(outgoing,outgoing.Opacity,0,140,delegate {
            if(showingAchievementOverview==overview) outgoing.Visibility=Visibility.Collapsed;
        });
    }

    void SetCoverZoom(bool hovered) {
        var zoom=(ScaleTransform)Find<Grid>("Dock").RenderTransform;
        double start=zoom.ScaleX;
        double end=hovered && SystemParameters.ClientAreaAnimation ? 1.015 : 1;
        zoom.BeginAnimation(ScaleTransform.ScaleXProperty,null);
        zoom.BeginAnimation(ScaleTransform.ScaleYProperty,null);
        zoom.ScaleX=zoom.ScaleY=end;
        if(start==end || !SystemParameters.ClientAreaAnimation) return;
        var motion=new DoubleAnimation(start,end,TimeSpan.FromMilliseconds(175)) {
            EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut },FillBehavior=FillBehavior.Stop
        };
        zoom.BeginAnimation(ScaleTransform.ScaleXProperty,motion);
        zoom.BeginAnimation(ScaleTransform.ScaleYProperty,motion);
    }

    void SetAchievementCase(bool expand,bool animate) {
        var reveal=Find<Grid>("CaseReveal");
        var toggle=Find<Button>("CaseToggle");
        StopCaseMotion();
        if(!animate || !SystemParameters.ClientAreaAnimation)
            Find<TranslateTransform>("CaseLanding").BeginAnimation(TranslateTransform.YProperty,null);
        caseExpanded=expand;
        SetCoverZoom(expand && Find<Grid>("Dock").IsMouseOver);
        ++caseReadRevision;
        reveal.Visibility=Visibility.Visible;
        var shelf=Find<Canvas>("CaseShelf");
        shelf.Visibility=activeGame?.GtaVi==true ? Visibility.Visible : Visibility.Collapsed;
        shelf.IsEnabled=expand;
        toggle.SetResourceReference(FrameworkElement.ToolTipProperty,expand ? "ui.收起封面" : "ui.展开封面");
        toggle.SetResourceReference(AutomationProperties.NameProperty,expand ? "ui.收起封面" : "ui.展开封面");
        double target=expand ? 1 : 0;
        if(animate && SystemParameters.ClientAreaAnimation && (caseProgress!=target || caseVelocity!=0)) {
            caseMotionStart=caseProgress;
            caseStartVelocity=caseVelocity;
            caseMotionTime.Restart();
            CompositionTarget.Rendering+=AdvanceCaseMotion;
        } else {
            caseVelocity=0;
            ApplyCaseProgress(target);
        }
        if(expand) RefreshAchievementCase();
    }

    void StopCaseMotion() {
        if(!caseMotionTime.IsRunning) return;
        CompositionTarget.Rendering-=AdvanceCaseMotion;
        caseMotionTime.Stop();
    }

    void AdvanceCaseMotion(object sender,EventArgs args) {
        double end=caseExpanded ? 1 : 0;
        // Exact damped spring, carrying the visible velocity into a reversed flight.
        const double damping=13, frequency=12;
        double t=caseMotionTime.Elapsed.TotalSeconds;
        double offset=caseMotionStart-end;
        double sineWeight=(caseStartVelocity+damping*offset)/frequency;
        double decay=Math.Exp(-damping*t), cos=Math.Cos(frequency*t), sin=Math.Sin(frequency*t);
        double displacement=decay*(offset*cos+sineWeight*sin);
        caseVelocity=decay*frequency*(sineWeight*cos-offset*sin)-damping*displacement;
        // The closed card has a minimum height. Stop at first contact so the next
        // spring lobe cannot lift its border again, even if a render frame was missed.
        bool reachedClosedEdge=!caseExpanded && (displacement<=0 || t>=Math.PI/frequency);
        if(reachedClosedEdge || (Math.Abs(displacement)<.0005 && Math.Abs(caseVelocity)<.009)) {
            double closingSpeed=-caseVelocity*caseExtraHeight;
            StopCaseMotion();
            caseVelocity=0;
            ApplyCaseProgress(end);
            if(reachedClosedEdge && closingSpeed>0) SettleClosedCard(closingSpeed);
            return;
        }
        ApplyCaseProgress(end+displacement);
    }

    void SettleClosedCard(double speed) {
        var slide=Find<TranslateTransform>("CaseLanding");
        double start=slide.Y;
        double travel=Math.Min(6,speed*.012);
        if(travel<.25 || !SystemParameters.ClientAreaAnimation) return;
        var settle=new DoubleAnimationUsingKeyFrames { FillBehavior=FillBehavior.Stop };
        settle.KeyFrames.Add(new DiscreteDoubleKeyFrame(start,TimeSpan.Zero));
        settle.KeyFrames.Add(new SplineDoubleKeyFrame(Math.Min(6,start+travel),TimeSpan.FromMilliseconds(55),
            new KeySpline(.18,.57,.48,1)));
        settle.KeyFrames.Add(new SplineDoubleKeyFrame(0,TimeSpan.FromMilliseconds(235),new KeySpline(.32,0,.24,1)));
        slide.BeginAnimation(TranslateTransform.YProperty,settle);
    }

    void ApplyCaseProgress(double progress) {
        caseProgress=progress;
        var reveal=Find<Grid>("CaseReveal");
        SizeCasePanels();
        var arrow=(FrameworkElement)Find<Button>("CaseToggle").Content;
        ((RotateTransform)arrow.RenderTransform).Angle=180*progress;
        Find<Grid>("CaseBackdrop").Opacity=Math.Max(0,Math.Min(1,progress));
        if(progress==0 && !caseExpanded) {
            reveal.Visibility=Visibility.Collapsed;
            Find<Canvas>("CaseShelf").Visibility=Visibility.Collapsed;
        }
        PositionCoverLogo();
    }

    void SizeCasePanels() {
        double shelfHeight=activeGame?.GtaVi==true ? Math.Min(Find<StackPanel>("ShelfContent").ActualHeight+12,caseExtraHeight) : 0;
        double layoutProgress=Math.Max(0,Math.Min(1,caseProgress));
        Find<Launcher.Layout.GameCoverPanel>("Dock").Expansion=layoutProgress;
        double rebound=Math.Min(18,caseExtraHeight*Math.Max(0,caseProgress-1));
        Find<Grid>("CaseReveal").Height=(caseExtraHeight-shelfHeight)*layoutProgress+rebound;
        var shelf=Find<Canvas>("CaseShelf");
        shelf.Height=shelfHeight*layoutProgress;
        shelf.Opacity=layoutProgress;
    }

    void PositionCoverLogo() {
        double fraction=caseProgress;
        var logo=Find<Image>("GameMark");
        var cover=Find<System.Windows.Shapes.Rectangle>("CoverPrint");
        var group=(TransformGroup)logo.RenderTransform;
        var scale=(ScaleTransform)group.Children[0];
        var movement=(TranslateTransform)group.Children[1];
        if(fraction!=0 && cover.ActualWidth>0) {
            // The expanded card fills CardArea vertically. Its destination stays fixed while the reveal grows.
            var area=Find<Grid>("CardArea");
            var target=area.TranslatePoint(new Point(cover.ActualWidth/2,area.ActualHeight/2),Find<Grid>("HeroContent"));
            bool custom=activeGame?.CustomPresentation==true;
            if(custom) {
                var box=CardLogoSize(cover.ActualWidth,cover.ActualHeight,activeGame.LogoSize,logo.Source);
                var point=CardLogoOffset(cover.ActualWidth,cover.ActualHeight,box,activeGame.LogoX,activeGame.LogoY);
                target=cover.TranslatePoint(new Point(point.X+box.Width/2,point.Y+box.Height/2),Find<Grid>("HeroContent"));
            }
            var origin=VisualTreeHelper.GetOffset(logo);
            double expandedWidth=(custom ? CardLogoSize(cover.ActualWidth,cover.ActualHeight,activeGame.LogoSize,logo.Source).Width : cover.ActualWidth*.44)
                *((ScaleTransform)Find<Grid>("Dock").RenderTransform).ScaleX;
            double opticalOffset=custom ? 0 : expandedWidth*(DefaultMarkCenter-.5);
            movement.X=fraction*(target.X-origin.X-logo.ActualWidth/2-opticalOffset);
            movement.Y=fraction*(target.Y-origin.Y-logo.ActualHeight/2);
            scale.ScaleX=scale.ScaleY=1+fraction*(expandedWidth/logo.Width-1);
        } else {
            movement.X=movement.Y=0;
            scale.ScaleX=scale.ScaleY=1;
        }
    }

    async void RefreshAchievementCase() {
        var game=activeGame;
        if(!caseExpanded || game?.GtaVi!=true) return;
        int revision=++caseReadRevision;
        try {
            var observed=await Task.Run(()=>AchievementJournal.ReadHistory(Lab,game.Id)).ConfigureAwait(false);
            Dispatch(()=> {
                if(!closed && revision==caseReadRevision && activeGame==game) ShowAchievementRows(observed);
            });
        } catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException) {
            Dispatch(()=> {
                if(closed || revision!=caseReadRevision) return;
                Find<TextBlock>("CaseCount").Text=Ui.Text("成就记录读取失败");
                Find<ItemsControl>("AchievementRows").ItemsSource=null;
                Find<ItemsControl>("AchievementOverview").ItemsSource=null;
                caseSignature=null;
                Append(ex.Message);
            });
        }
    }

    void ShowAchievementRows(AchievementJournal.History observed) {
        var catalog=AchievementJournal.Catalog;
        var entries=catalog.Select(entry=>new {
            Item=entry, Percent=observed.Progress.TryGetValue(entry.Id,out var value) ? value : -1,
            Completion=observed.FirstCompletion.TryGetValue(entry.Id,out var time)
                ? Ui.Format(observed.UnlockEvents.Contains(entry.Id) ? "达成时间 · {0:g}" : "首次记录达成 · {0:g}",time.ToLocalTime())
                : null
        }).Where(row=>row.Percent>0)
          .OrderByDescending(row=>row.Percent)
          .ThenByDescending(row=>row.Percent==100 ? GtaViTrophyPalette.GradeOf(row.Item.Id) : GtaViTrophyPalette.Grade.Bronze)
          .ToArray();
        var signature=activeGame.Id+Ui.Language+string.Join(",",entries.Select(row=>row.Item.Id+"="+row.Percent+":"+row.Completion));
        if(caseSignature==signature) return;
        caseSignature=signature;
        int unlocked=entries.Count(row=>row.Percent==100);
        Find<TextBlock>("CaseCount").Text=catalog.Length==0 ? "—" :
            Ui.Format("已解锁 {0}/{1} · {2:0.##}%",unlocked,catalog.Length,100m*unlocked/catalog.Length);
        Find<ItemsControl>("AchievementOverview").ItemsSource=catalog
            .OrderBy(entry=>int.Parse(entry.Id,CultureInfo.InvariantCulture))
            .Select(entry=> {
                observed.Progress.TryGetValue(entry.Id,out int percent);
                bool earned=percent==100;
                string status=earned ? Ui.Text("已解锁") : percent>0 ? Ui.Format("{0}%",percent) : Ui.Text("尚未达成");
                return new {
                    Earned=earned, Finish=earned ? AchievementFinish(entry.Id,out _) : null,
                    Detail="#"+entry.Id+" · "+entry.Name+" · "+status
                };
            }).ToArray();
        Find<ItemsControl>("AchievementRows").ItemsSource=entries.Select(row=> {
            bool earned=row.Percent==100;
            string status=earned ? Ui.Text("已解锁") : Ui.Format("{0}%",row.Percent);
            var cover=new Lazy<Brush>(()=>AchievementCover(row.Item.Id,earned));
            var finish=AchievementFinish(row.Item.Id,out var tone);
            // Dim the same metal tone on records that are still in progress.
            var background=earned ? tone : Color.FromRgb((byte)(34+tone.R*.16),(byte)(33+tone.G*.16),(byte)(37+tone.B*.16));
            var surface=new SolidColorBrush(background);
            surface.Freeze();
            double luminance=.2126*background.ScR+.7152*background.ScG+.0722*background.ScB;
            var ink=(luminance+.05)/.05 >= 1.05/(luminance+.05) ? Brushes.Black : Brushes.White;
            return new { Finish=finish,DetailSurface=surface,DetailInk=ink,Cover=cover,Title=row.Item.Name,Status=status,Earned=earned,
                Description=row.Item.Description,Completion=row.Completion ?? Ui.Text(earned ? "达成时间未记录" : "尚未达成"),
                ProgressArc=earned ? null : AchievementArc(row.Percent),Detail=row.Item.Name+" · "+status };
        }).ToArray();
    }

    static LinearGradientBrush AchievementFinish(string id,out Color tone) {
        GtaViTrophyPalette.ForAchievement(id,out int metal,out int highlight);
        Color Rgb(int value) => Color.FromRgb((byte)(value>>16),(byte)(value>>8),(byte)value);
        tone=Rgb(metal);
        var finish=new LinearGradientBrush(tone,Rgb(highlight),18);
        finish.Freeze();
        return finish;
    }

    static Brush AchievementCover(string id,bool earned) {
        BitmapSource image=Artwork("Achievements/"+id+".png",Int32Rect.Empty);
        if(!earned) image=new FormatConvertedBitmap(image,PixelFormats.Gray8,null,0);
        // Extend the decorative edges of the landscape artwork equally. The central
        // illustration keeps its proportions and the caption sits on the same artwork.
        int w=image.PixelWidth, h=image.PixelHeight, edge=h/12;
        double pad=h*.22;
        var art=new DrawingGroup();
        using(var draw=art.Open()) {
            void Band(int y,int rows,double top,double height) {
                var band=new CroppedBitmap(image,new Int32Rect(0,y,w,rows));
                draw.DrawImage(band,new Rect(0,top,w,height));
            }
            draw.DrawImage(image,new Rect(0,pad,w,h));
            Band(0,edge,0,edge+pad);
            Band(h-edge,edge,h-edge+pad,edge+pad);
        }
        var brush=new DrawingBrush(art) { Stretch=Stretch.UniformToFill,AlignmentX=AlignmentX.Center,AlignmentY=AlignmentY.Center };
        brush.Freeze();
        return brush;
    }

    static Geometry AchievementArc(int percent) {
        // The 24-DIP badge has a 1-DIP inset and a 2-DIP stroke.
        const double center=12, radius=10;
        double angle=percent*Math.PI/50;
        var end=new Point(center+radius*Math.Sin(angle),center-radius*Math.Cos(angle));
        var path=new StreamGeometry();
        using(var draw=path.Open()) {
            draw.BeginFigure(new Point(center,center-radius),false,false);
            draw.ArcTo(end,new Size(radius,radius),0,percent>50,SweepDirection.Clockwise,true,false);
        }
        path.Freeze();
        return path;
    }
}

namespace Launcher.Layout
{
    public sealed class GameCoverPanel : Grid
    {
        // Normal cover size in WPF units, independent of DPI and save history.
        const double CoverWidth=408;
        double expansion;
        internal double Expansion {
            set { if(expansion!=value) { expansion=value; InvalidateMeasure(); } }
        }

        protected override Size MeasureOverride(Size available) {
            double cover=Math.Min(CoverWidth,available.Width);
            double width=cover;
            if(expansion<1) {
                var natural=base.MeasureOverride(available);
                double closed=Math.Min(available.Width,Math.Max(cover,natural.Width));
                width=closed+(cover-closed)*expansion;
                if(width==natural.Width) return natural;
            }
            var content=base.MeasureOverride(new Size(width,available.Height));
            return new Size(width,content.Height);
        }
    }
}
