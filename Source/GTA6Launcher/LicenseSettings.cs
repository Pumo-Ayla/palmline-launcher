using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

internal sealed partial class DesktopLauncher
{
    void SetupLicenses() {
        var page=Find<StackPanel>("LicenseList");
        Find<RadioButton>("SettingsLicenses").Checked+=delegate {
            if(page.Children.Count!=0) return;
            var list=LicenseGroup(page,"开源组件");
            AddNotice(list,"MonoTorrent","MIT","MonoTorrent.txt","3.0.2");
            AddNotice(list,"Mono.Nat","MIT","Mono.Nat.txt","3.0.0");
            AddNotice(list,"ReusableTasks","MIT","ReusableTasks.txt","4.0.0");
            AddNotice(list,"SharpCompress","MIT","SharpCompress.txt","0.50.12");
            AddNotice(list,"ZstdSharp","MIT","ZstdSharp.txt");
            AddNotice(list,"Zstandard","BSD","Zstandard.txt");
            AddNotice(list,"SDL","zlib","SDL3.txt","3.4.20");
            AddNotice(list,"GitHub Octicons","MIT","Octicons-LICENSE.txt");
            AddNotice(list,"Xbox GDK Samples","MIT","GDK-Samples.txt");
            foreach(var project in catalog.Values) {
                var id=(string)project.Attribute("id");
                var revision=(string)project.Attribute("commit");
                var path=Path.Combine(root,"Community",id,"runtime",revision,"LICENSE");
                if(File.Exists(path))
                    AddNotice(list,(string)project.Attribute("name"),"",path,revision.Substring(0,Math.Min(8,revision.Length)));
            }
            AddNotice(list,".NET Runtime","MIT","DotNetRuntime.txt");
            var python=Path.Combine(root,"Runtime","Python","LICENSE.txt");
            AddNotice(list,"CPython","PSF",File.Exists(python) ? python : "CPython.txt");
            AddRuntimeNotice(list,".NET","Third-party notices","DotNet","ThirdPartyNotices.txt");
            AddRuntimeNotice(list,"DirectX Shader Compiler","LLVM","Native","DXC-LICENSE-LLVM.txt");
            list=LicenseGroup(page,"其他第三方许可");
            AddNotice(list,"UnRAR","RARLAB","UnRAR.txt");
            AddRuntimeNotice(list,"Microsoft .NET","Windows","DotNet","LICENSE.txt");
            AddRuntimeNotice(list,"DirectX Shader Compiler","Microsoft","Native","DXC-LICENSE-MS.txt");
            AddRuntimeNotice(list,"NVIDIA DLSS","","DLSS","nvngx_dlss.license.txt");
            AddRuntimeNotice(list,"NVIDIA Streamline","",Path.Combine("DLSS","FrameGeneration"),"license.txt");
            AddRuntimeNotice(list,"NVIDIA Streamline","Third-party notices",Path.Combine("DLSS","FrameGeneration"),"3rd-party-licenses.md");
            AddRuntimeNotice(list,"NVIDIA Reflex","",Path.Combine("DLSS","FrameGeneration"),"reflex.license.txt");
            var pdf=Path.Combine(root,"Runtime","Notices","MiSans-License.pdf");
            if(File.Exists(pdf)) {
                var font=new Button {Content=new {Name="MiSans", Version="", Label="PDF", External=true},
                    Style=(Style)list.FindResource("licenseRowButton")};
                AutomationProperties.SetName(font,"MiSans PDF");
                font.Click+=delegate {
                    try { Process.Start(new ProcessStartInfo(pdf){UseShellExecute=true}); }
                    catch(System.ComponentModel.Win32Exception e) { MessageBox.Show(window,e.Message,window.Title); }
                };
                AddRow(list,font);
            }
        };
    }

    static StackPanel LicenseGroup(StackPanel page,string title) {
        var heading=new TextBlock { FontWeight=FontWeights.SemiBold,FontSize=12,Margin=new Thickness(2,page.Children.Count==0 ? 2 : 16,0,6),
            Foreground=new SolidColorBrush(Color.FromRgb(195,185,204)) };
        heading.SetResourceReference(TextBlock.TextProperty,"ui."+title);
        page.Children.Add(heading);
        var list=new StackPanel();
        page.Children.Add(new Border { Child=list,CornerRadius=new CornerRadius(8),Padding=new Thickness(3),
            Background=new SolidColorBrush(Color.FromRgb(36,33,40)),BorderThickness=new Thickness(1),
            BorderBrush=new SolidColorBrush(Color.FromRgb(56,51,61)) });
        return list;
    }

    static void AddRow(StackPanel list,Control row) {
        if(list.Children.Count>0)
            list.Children.Add(new Border {Height=1,Margin=new Thickness(10,0,10,0),Background=new SolidColorBrush(Color.FromRgb(49,45,54))});
        list.Children.Add(row);
    }

    void AddRuntimeNotice(StackPanel list,string name,string label,string component,string file) {
        string path=Path.Combine(root,"Runtime",component,file);
        if(File.Exists(path)) AddNotice(list,name,label,path);
    }

    static void AddNotice(StackPanel list,string name,string label,string file,string version="") {
        var entry=new Expander {Header=new {Name=name,Version=version.Length==0 ? "" : "   "+version,Label=label,External=false},
            Style=(Style)list.FindResource("licenseEntry")};
        AutomationProperties.SetName(entry,(name+" "+version+" "+label).Trim());
        entry.Expanded+=delegate {
            if(entry.Content!=null) return;
            string text;
            try {
                using(var stream=Path.IsPathRooted(file) ? File.OpenRead(file) :
                    Assembly.GetExecutingAssembly().GetManifestResourceStream("Notices."+file))
                using(var reader=new StreamReader(stream)) text=reader.ReadToEnd();
            } catch(IOException e) { text=e.Message; }
            catch(UnauthorizedAccessException e) { text=e.Message; }
            entry.Content=new TextBox { Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FontSize=12,
                Foreground=Brushes.WhiteSmoke,Background=Brushes.Transparent,BorderThickness=new Thickness(0),
                Padding=new Thickness(2),MaxHeight=170,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden };
        };
        AddRow(list,entry);
    }
}
