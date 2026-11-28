using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

internal sealed partial class DesktopLauncher {
    void SetupNotificationSounds() {
        var sounds=displaySettings.Sounds;
        var enabled=Find<CheckBox>("EnableNoticeSounds");
        enabled.IsChecked=sounds.Enabled;
        enabled.Click+=delegate {
            sounds.Enabled=enabled.IsChecked==true;
            if(!sounds.Enabled) notificationSounds.Stop();
            SaveDisplaySettings();
        };

        var rows=Find<StackPanel>("NoticeSoundRows");
        foreach(LauncherNoticeKind kind in Enum.GetValues(typeof(LauncherNoticeKind))) {
            var row=new Grid { Margin=new Thickness(0,3,0,0) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var label=new TextBlock { VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,6,0) };
            label.SetResourceReference(TextBlock.TextProperty,"ui."+NotificationSoundSettings.EventLabels[kind]);
            row.Children.Add(label);
            var choose=new Button { MinWidth=100,Padding=new Thickness(9,4,9,4) };
            AutomationProperties.SetLabeledBy(choose,label);
            var selection=new DockPanel();
            var arrow=new TextBlock { Text="▾",Opacity=.7,Margin=new Thickness(8,0,0,0),VerticalAlignment=VerticalAlignment.Center };
            DockPanel.SetDock(arrow,Dock.Right); selection.Children.Add(arrow);
            var toneName=new TextBlock();
            selection.Children.Add(toneName); choose.Content=selection;
            var menu=new ContextMenu { Style=(Style)window.FindResource("profileMenu"),FontFamily=window.FontFamily,
                PlacementTarget=choose,Placement=PlacementMode.Bottom };
            choose.ContextMenu=menu;
            var preview=new Button { Margin=new Thickness(6,0,0,0),Padding=new Thickness(9,4,9,4) };
            preview.SetResourceReference(ContentControl.ContentProperty,"ui.试听");
            preview.Click+=delegate { notificationSounds.Preview(sounds[kind]); };
            Action refresh=delegate {
                var tone=sounds[kind];
                toneName.SetResourceReference(TextBlock.TextProperty,"ui."+NotificationSoundSettings.ToneLabels[tone]);
                preview.IsEnabled=tone!=NoticeTone.Silent;
            };
            foreach(var pair in NotificationSoundSettings.ToneLabels) {
                var tone=pair.Key;
                var item=new MenuItem();
                item.Click+=delegate { sounds[kind]=tone; refresh(); SaveDisplaySettings(); };
                menu.Items.Add(item);
                menu.Opened+=delegate {
                    item.Header=(tone==sounds[kind] ? "✓  " : "    ")+Ui.Text(pair.Value);
                    item.IsChecked=tone==sounds[kind];
                };
            }
            choose.Click+=delegate { menu.IsOpen=true; };
            Grid.SetColumn(choose,1); row.Children.Add(choose);
            Grid.SetColumn(preview,2); row.Children.Add(preview);
            rows.Children.Add(row);
            refresh();
        }
    }
}
