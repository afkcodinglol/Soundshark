using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Soundshark;

public static class SliderInput
{
    static int registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) != 0) return;
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Press), true);
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseMoveEvent, new MouseEventHandler(Move), true);
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Release), true);
    }

    static void Press(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (!slider.IsEnabled || Ancestor<Thumb>(e.OriginalSource as DependencyObject) != null) return;
        slider.Focus();
        slider.CaptureMouse();
        Update(slider, e);
        e.Handled = true;
    }

    static void Move(object sender, MouseEventArgs e)
    {
        var slider = (Slider)sender;
        if (!slider.IsMouseCaptured) return;
        if (e.LeftButton != MouseButtonState.Pressed) { slider.ReleaseMouseCapture(); return; }
        Update(slider, e);
        e.Handled = true;
    }

    static void Release(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (!slider.IsMouseCaptured) return;
        Update(slider, e);
        slider.ReleaseMouseCapture();
        e.Handled = true;
    }

    static void Update(Slider slider, MouseEventArgs e)
    {
        slider.ApplyTemplate();
        var track = slider.Template.FindName("PART_Track", slider) as Track;
        Point point = e.GetPosition(track is null ? slider : track);
        slider.Value = ValueAt(slider, point, track?.ActualWidth ?? slider.ActualWidth, track?.ActualHeight ?? slider.ActualHeight,
            track?.Orientation ?? slider.Orientation, track?.IsDirectionReversed ?? slider.IsDirectionReversed);
    }

    internal static double ValueAt(Slider slider, Point point, double width, double height, Orientation orientation, bool reversed)
    {
        double ratio = orientation == Orientation.Horizontal
            ? point.X / Math.Max(1, width)
            : 1 - point.Y / Math.Max(1, height);
        if (orientation == Orientation.Horizontal && slider.FlowDirection == FlowDirection.RightToLeft) ratio = 1 - ratio;
        if (reversed) ratio = 1 - ratio;
        double value = slider.Minimum + Math.Clamp(ratio, 0, 1) * (slider.Maximum - slider.Minimum);
        if (!slider.IsSnapToTickEnabled) return value;
        if (slider.Ticks.Count > 0) return slider.Ticks.OrderBy(tick => Math.Abs(tick - value)).First();
        if (slider.TickFrequency <= 0) return value;
        return Math.Clamp(slider.Minimum + Math.Round((value - slider.Minimum) / slider.TickFrequency) * slider.TickFrequency, slider.Minimum, slider.Maximum);
    }

    static T? Ancestor<T>(DependencyObject? value) where T : DependencyObject
    {
        while (value != null) { if (value is T match) return match; value = VisualTreeHelper.GetParent(value); }
        return null;
    }
}
