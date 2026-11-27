using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Launcher.Layout
{
    internal enum ImportAction { Waiting, Consent, Continue, Check, Checking, Passed, Retry }

    public sealed class ImportActionButton : Button
    {
        sealed class Face
        {
            internal readonly Color Fill, Ink;
            internal readonly Geometry Icon;
            internal Face(string fill,string ink,string icon) {
                Fill=(Color)ColorConverter.ConvertFromString(fill);
                Ink=(Color)ColorConverter.ConvertFromString(ink);
                Icon=Geometry.Parse(icon); Icon.Freeze();
            }
        }

        static readonly Face waiting=new Face("#34323D","#C3BDCC","M4,8 H12 V15 H4 Z M5.5,8 V5.5 A2.5,2.5 0 0 1 10.5,5.5 V8");
        static readonly Face consent=new Face("#493B29","#FFDA93","M8,1 L14,3.5 V8 C14,11 11,14 8,15 C5,14 2,11 2,8 V3.5 Z M8,5 V8 M8,11 v0.1");
        static readonly Face forward=new Face("#D8CBE6","#30233D","M2,8 H14 M9,3 L14,8 9,13");
        static readonly Face check=new Face("#CEB9E4","#2D1D3C","M11.5,6.8 A4.7,4.7 0 1 1 2.1,6.8 A4.7,4.7 0 1 1 11.5,6.8 M10.3,10.3 L14.3,14.3");
        static readonly Face busy=new Face("#ACCFFC","#142E50","M8,2 A6,6 0 1 1 2,8");
        static readonly Face passed=new Face("#80DFAC","#122E23","M2.4,8.1 L6.1,11.8 13.7,4.2");
        static readonly Face retry=new Face("#F0AC9D","#43231F","M13.5,7 A5.5,5.5 0 1 0 13,11 M13.5,2.5 V7 H9");
        static readonly DoubleCollection tickDashes=new DoubleCollection {12,12};
        readonly SolidColorBrush fill=new SolidColorBrush(waiting.Fill), ink=new SolidColorBrush(waiting.Ink);
        Face face=waiting;
        Path glyph;
        ImportAction? current;

        public ImportActionButton() {
            Background=fill; Foreground=ink;
            IsVisibleChanged+=delegate { UpdateIcon(false); };
        }

        internal void Show(ImportAction action,string label,string help) {
            IsEnabled=action!=ImportAction.Waiting && action!=ImportAction.Consent && action!=ImportAction.Checking;
            string caption=Ui.Text(label), hint=Ui.Text(help);
            if(!Equals(Content,caption)) {
                Content=caption;
                AutomationProperties.SetName(this,caption);
            }
            if(!Equals(ToolTip,hint)) {
                ToolTip=hint;
                AutomationProperties.SetHelpText(this,hint);
            }
            if(current==action) return;
            bool animate=current.HasValue && IsVisible && SystemParameters.ClientAreaAnimation;
            current=action;
            switch(action) {
                case ImportAction.Consent: face=consent; break;
                case ImportAction.Continue: face=forward; break;
                case ImportAction.Check: face=check; break;
                case ImportAction.Checking: face=busy; break;
                case ImportAction.Passed: face=passed; break;
                case ImportAction.Retry: face=retry; break;
                default: face=waiting; break;
            }
            Tint(fill,face.Fill,animate); Tint(ink,face.Ink,animate);
            UpdateIcon(animate);
        }

        static void Tint(SolidColorBrush brush,Color target,bool animate) {
            var from=brush.Color;
            brush.Color=target;
            brush.BeginAnimation(SolidColorBrush.ColorProperty,animate ?
                new ColorAnimation(from,target,TimeSpan.FromMilliseconds(160)) { FillBehavior=FillBehavior.Stop } : null);
        }

        public override void OnApplyTemplate() {
            base.OnApplyTemplate();
            glyph=GetTemplateChild("actionGlyph") as Path;
            if(glyph!=null) glyph.RenderTransform=new RotateTransform();
            UpdateIcon(false);
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e) {
            if(e.ClickCount>1) { e.Handled=true; return; }
            base.OnPreviewMouseLeftButtonDown(e);
        }

        void UpdateIcon(bool entering) {
            if(glyph==null) return;
            var turn=(RotateTransform)glyph.RenderTransform;
            turn.BeginAnimation(RotateTransform.AngleProperty,null);
            glyph.BeginAnimation(Shape.StrokeDashOffsetProperty,null);
            glyph.Data=face.Icon;
            glyph.StrokeDashArray=face==passed ? tickDashes : null;
            if(!IsVisible || !SystemParameters.ClientAreaAnimation) return;
            if(face==busy)
                turn.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromMilliseconds(820)) { RepeatBehavior=RepeatBehavior.Forever });
            else if(entering && face==passed)
                glyph.BeginAnimation(Shape.StrokeDashOffsetProperty,new DoubleAnimation(12,0,TimeSpan.FromMilliseconds(240)) { FillBehavior=FillBehavior.Stop });
            else if(entering && face==retry)
                turn.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(-32,0,TimeSpan.FromMilliseconds(200)) {
                    EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut },FillBehavior=FillBehavior.Stop });
        }
    }
}
