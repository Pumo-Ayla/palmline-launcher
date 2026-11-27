using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

internal static class MappingHint
{
    const double Diameter=32;
    static readonly Brush ink=new SolidColorBrush(Color.FromRgb(38,35,42));

    public static string InputName(string id,string fallback) {
        switch(id) {
            case "Key.Enter": case "Key.Return": return Ui.Text("回车键");
            case "Key.Space": return Ui.Text("空格");
            case "Key.Back": return Ui.Text("退格键");
            default: return fallback;
        }
    }

    public static FrameworkElement Create(string input,string legend,string[] targets) {
        var row=new Grid();
        for(int i=0;i<3;i++) row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        var source=Badge(InputGlyph(input) ?? Letters(legend));
        row.Children.Add(source);
        var arrow=Icon();
        Draw(arrow,"M3,12 H20 M14,6 L20,12 14,18",Brushes.White);
        arrow.Margin=new Thickness(6,0,6,0);
        Grid.SetColumn(arrow,1); row.Children.Add(arrow);

        var outputs=new WrapPanel {MaxWidth=324,VerticalAlignment=VerticalAlignment.Center};
        Grid.SetColumn(outputs,2); row.Children.Add(outputs);
        foreach(string target in targets) {
            bool aiming=target.StartsWith("aim:",StringComparison.Ordinal);
            string id=aiming ? target.Substring(4) : target;
            var group=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(2)};
            if(aiming) group.Children.Add(Caption("瞄准时"));
            if(id=="PartnerRB") group.Children.Add(Caption("长按"));
            if(id=="Layer") group.Children.Add(Caption("切换步行与驾驶"));
            else group.Children.Add(Badge(PadGlyph(id)));
            outputs.Children.Add(group);
        }
        return row;
    }

    static TextBlock Caption(string label) => new TextBlock {
        Text=Ui.Text(label),Foreground=Brushes.White,FontSize=12,
        Margin=new Thickness(2,0,4,0),VerticalAlignment=VerticalAlignment.Center
    };
    static Border Badge(UIElement glyph) => new Border {
        Width=Diameter,Height=Diameter,CornerRadius=new CornerRadius(Diameter/2),
        Background=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Child=glyph
    };
    static FrameworkElement Letters(string text) => new Viewbox {
        Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,Margin=new Thickness(3.5),
        HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,
        Child=new TextBlock {Text=text,Foreground=ink,FontSize=16,FontWeight=FontWeights.Bold}
    };
    static Canvas Icon() => new Canvas {Width=24,Height=24,HorizontalAlignment=HorizontalAlignment.Center,
        VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};
    static void Draw(Canvas icon,string data,Brush stroke,Brush fill=null,double weight=1.8) {
        icon.Children.Add(new Path {Data=Geometry.Parse(data),Stroke=stroke,Fill=fill,StrokeThickness=weight,
            StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round});
    }
    static FrameworkElement InputGlyph(string id) {
        var icon=Icon();
        switch(id) {
            case "Key.Return": case "Key.Enter": Draw(icon,"M19,6 V13 H5 M10,8 L5,13 10,18",ink); break;
            case "Key.Space": Draw(icon,"M3.5,10 V15 H20.5 V10",ink); break;
            case "Key.Back": Draw(icon,"M3,12 L9,6 H21 V18 H9 Z M12,9 L17,15 M17,9 L12,15",ink); break;
            case "Mouse.Move": Draw(icon,"M2,12 H22 M5,9 L2,12 5,15 M19,9 L22,12 19,15 M12,2 V22 M9,5 L12,2 15,5 M9,19 L12,22 15,19",ink); break;
            case "Mouse.Left": case "Mouse.Right": case "Mouse.Middle":
            case "Mouse.XButton1": case "Mouse.XButton2": case "Wheel.Up": case "Wheel.Down":
                DrawMouse(icon,id); break;
            default: return null;
        }
        return icon;
    }
    static void DrawMouse(Canvas icon,string id) {
        Draw(icon,"M12,2 C7.9,2 5.5,5.1 5.5,9 V15 C5.5,19.1 8.1,22 12,22 C15.9,22 18.5,19.1 18.5,15 V9 C18.5,5.1 16.1,2 12,2 Z",ink,null,1.55);
        switch(id) {
            case "Mouse.Left":
                Draw(icon,"M11,2.6 V10 H6.1 V9 Q6.1,3.4 11,2.6 Z",null,ink); break;
            case "Mouse.Right":
                Draw(icon,"M13,2.6 V10 H17.9 V9 Q17.9,3.4 13,2.6 Z",null,ink); break;
            case "Mouse.Middle":
                Draw(icon,"M12,5.5 V10",ink,null,3.4); break;
            case "Mouse.XButton1": case "Mouse.XButton2":
                Draw(icon,"M12,3 V9",ink,null,1.2);
                bool first=id=="Mouse.XButton1";
                Draw(icon,"M3,8 V11 M3,14 V17",Brushes.Silver,null,2.3);
                Draw(icon,first ? "M3,8 V11" : "M3,14 V17",ink,null,3.2); break;
            case "Wheel.Up": Draw(icon,"M12,15 V6 M9,9 L12,6 15,9",ink); break;
            case "Wheel.Down": Draw(icon,"M12,6 V15 M9,12 L12,15 15,12",ink); break;
        }
    }
    static FrameworkElement PadGlyph(string id) {
        switch(id) {
            case "LX-": return Letters("LS←");
            case "LX+": return Letters("LS→");
            case "LY-": return Letters("LS↓");
            case "LY+": return Letters("LS↑");
            case "Look": return Letters("RS");
            case "PartnerRB": return Letters("RB");
            case "View": {
                var icon=Icon();
                Draw(icon,"M6,14 H4 V5 H15 V7 M9,9 H20 V18 H9 Z",ink);
                return icon;
            }
            default:
                var step=Array.Find(MappingStep.All,s=>s.Id==id);
                return Letters(step?.Symbol ?? id);
        }
    }
}
