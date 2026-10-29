using System.Windows;
using System.Windows.Controls;

namespace Soundshark;

public sealed class WaveNavigation : Grid
{
    readonly Waveform wave;
    readonly Slider zoom = new() { Name = "WaveZoom", Minimum = 0, Maximum = 10, SmallChange = .1, LargeChange = .5, MinWidth = 120, Height = 38, IsMoveToPointEnabled = true };
    readonly Slider scroll = new() { Name = "WaveScroll", Minimum = 0, Maximum = 1, SmallChange = .01, LargeChange = .1, Height = 38, IsMoveToPointEnabled = true };
    readonly Slider volume = new() { Name="TrackVolume",Minimum=0,Maximum=100,Value=100,SmallChange=1,LargeChange=10,Height=38,IsMoveToPointEnabled=true };
    readonly TextBlock volumeLabel=Ui.Text("100%",12,true);
    public event Action<double>? VolumeChanged;
    public double Volume {get=>volume.Value/100;set=>volume.Value=value*100;}
    readonly TextBlock range = Ui.Text("Full track", 12, true);
    bool syncing;
    public WaveNavigation(Waveform wave)
    {
        this.wave = wave; Margin = new Thickness(0, 8, 0, 0);
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        left.Children.Add(Ui.Text("Zoom", 13)); left.Children.Add(zoom); Children.Add(left);
        var right = new StackPanel {Margin=new Thickness(0,0,24,0)}; var heading = new StackPanel { Orientation = Orientation.Horizontal }; range.Margin = new Thickness(8,0,0,0); heading.Children.Add(Ui.Text("Scroll",13)); heading.Children.Add(range); right.Children.Add(heading); right.Children.Add(scroll); SetColumn(right, 1); Children.Add(right);
        var volumePanel=new StackPanel();var volumeHeading=new StackPanel {Orientation=Orientation.Horizontal};volumeLabel.Margin=new Thickness(8,0,0,0);volumeHeading.Children.Add(Ui.Text("Volume",13));volumeHeading.Children.Add(volumeLabel);volumePanel.Children.Add(volumeHeading);volumePanel.Children.Add(volume);SetColumn(volumePanel,2);Children.Add(volumePanel);
        volume.SetResourceReference(StyleProperty,"WaveSlider");volume.ToolTip="Track volume for preview and saved output. 100% uses the controlled source level; 0% mutes the track.";
        System.Windows.Automation.AutomationProperties.SetName(volume,"Track volume");
        volume.ValueChanged+=(_,_)=> {volumeLabel.Text=$"{volume.Value:0}%";VolumeChanged?.Invoke(Volume);};
        zoom.SetResourceReference(StyleProperty, "WaveSlider"); scroll.SetResourceReference(StyleProperty, "WaveSlider");
        System.Windows.Automation.AutomationProperties.SetName(zoom, "Waveform zoom");
        System.Windows.Automation.AutomationProperties.SetName(scroll, "Waveform scroll");
        zoom.ValueChanged += (_, _) => { if (syncing) return; wave.Zoom = Math.Pow(2, zoom.Value); Sync(); };
        scroll.ValueChanged += (_, _) => { if (!syncing) wave.Scroll = scroll.Value; };
        wave.ViewChanged += Sync; Loaded += (_, _) => Sync();
    }
    void Sync()
    {
        syncing = true;
        try {
            zoom.IsEnabled = wave.Workspace.Frames > 0;
            zoom.Maximum = Math.Max(.01, Math.Log2(Math.Max(1, wave.Workspace.Seconds / .25)));
            zoom.Value = Math.Log2(wave.Zoom); scroll.Value = wave.Scroll; scroll.IsEnabled = wave.Zoom > 1 && wave.Workspace.Frames > 0;
            range.Text = wave.Zoom <= 1 ? "Full track" : $"{wave.ViewStart / (double)Soundshark.Core.Workspace.Rate:F1}s – {(wave.ViewStart + wave.ViewFrames) / (double)Soundshark.Core.Workspace.Rate:F1}s";
        } finally { syncing = false; }
    }
}
