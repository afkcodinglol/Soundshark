using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Soundshark;

/// <summary>A quiet red idle light, with a slow green glow only while capture is active.</summary>
public sealed class RecordingLight : Border
{
    public bool IsRecording { get; private set; }
    bool reduced;
    public string RecordingDescription { get; set; } = "Recording microphone";
    public RecordingLight()
    {
        Name = "BoothRecordingLight";
        Width = Height = 12; CornerRadius = new CornerRadius(6);
        Margin = new Thickness(0, 0, 10, 0); VerticalAlignment = VerticalAlignment.Center;
        Background = new SolidColorBrush(Color.FromRgb(229, 79, 90));
        ToolTip = "Not recording";
    }
    public void SetRecording(bool recording)
    {
        if (IsRecording == recording && reduced == Theme.Current.ReduceMotion) return;
        reduced = Theme.Current.ReduceMotion;
        IsRecording = recording;
        BeginAnimation(OpacityProperty, null); Opacity = 1; Effect = null;
        Background = new SolidColorBrush(recording ? Color.FromRgb(61, 230, 143) : Color.FromRgb(229, 79, 90));
        ToolTip = recording ? RecordingDescription : "Not recording";
        if (!recording || reduced) return;
        var glow = new DropShadowEffect { Color = Color.FromRgb(61, 230, 143), BlurRadius = 8, ShadowDepth = 0, Opacity = .8 };
        Effect = glow;
        var pulse = new DoubleAnimation(.45, 1, TimeSpan.FromSeconds(1.5))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        BeginAnimation(OpacityProperty, pulse);
        glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(6, 18, TimeSpan.FromSeconds(1.5))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }
}
