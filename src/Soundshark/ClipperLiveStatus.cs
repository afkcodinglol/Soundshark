using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Soundshark;

/// <summary>Small compositor-driven activity indicator; no UI timer or audio processing.</summary>
public sealed class ClipperLiveStatus : StackPanel
{
    readonly Border[] dots = new Border[3];
    bool live;
    public ClipperLiveStatus()
    {
        Name = "ClipperLiveStatus"; Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center; Visibility = Visibility.Collapsed;
        ToolTip = "Continuously listening to PC audio. Older audio is automatically discarded.";
        System.Windows.Automation.AutomationProperties.SetName(this, "Clipper Live · continuously listening");
        for (int i = 0; i < dots.Length; ++i) {
            dots[i] = new Border { Width = 4, Height = 12, CornerRadius = new CornerRadius(2), Opacity = .3, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
            dots[i].SetResourceReference(Border.BackgroundProperty, "Live");
            Children.Add(dots[i]);
        }
        var label = Ui.Text("Clipper Live", 13); label.SetResourceReference(TextBlock.ForegroundProperty, "Live"); label.Margin = new Thickness(5, 0, 0, 0); Children.Add(label);
        IsVisibleChanged += (_, _) => Animate();
        Unloaded += (_, _) => { Theme.Changed -= Animate; StopAnimation(); };
        Loaded += (_, _) => { Theme.Changed += Animate; Animate(); };
    }
    public void SetLive(bool value)
    {
        if (live == value) return;
        live = value; Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        if (!live) StopAnimation();
    }
    void Animate()
    {
        StopAnimation();
        if (!live || !IsVisible || Theme.Current.ReduceMotion) return;
        for (int i = 0; i < dots.Length; ++i)
            dots[i].BeginAnimation(OpacityProperty, new DoubleAnimation(.3, 1, TimeSpan.FromSeconds(.65)) {
                BeginTime = TimeSpan.FromMilliseconds(i * 180), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
    }
    void StopAnimation()
    {
        foreach (var dot in dots) { dot.BeginAnimation(OpacityProperty, null); dot.Opacity = .3; }
    }
}
