using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Soundshark;

public sealed class ColorWheel : FrameworkElement
{
    static readonly BitmapSource image = Build();
    double hue, saturation, value = 1;
    public Color SelectedColor => FromHsv(hue, saturation, value);
    public event Action<Color>? ColorChanged;
    public ColorWheel()
    {
        Width = Height = 256; Focusable = true; Cursor = Cursors.Cross;
        System.Windows.Automation.AutomationProperties.SetName(this, "Color wheel. Arrow left or right changes hue; up or down changes saturation.");
    }
    public void SetColor(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        value = max; saturation = max == 0 ? 0 : delta / max;
        if (delta > 0) hue = ((max == r ? (g - b) / delta : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4) * 60 + 360) % 360;
        InvalidateVisual();
    }
    public void SetBrightness(double brightness) { value = Math.Clamp(brightness, 0, 1); Changed(); }
    public double Brightness => value;
    void Changed() { InvalidateVisual(); ColorChanged?.Invoke(SelectedColor); }
    void Pick(Point point)
    {
        double radius = Math.Min(ActualWidth, ActualHeight) / 2 - 8, x = point.X - ActualWidth / 2, y = ActualHeight / 2 - point.Y;
        hue = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360; saturation = Math.Clamp(Math.Sqrt(x * x + y * y) / radius, 0, 1); Changed();
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { base.OnMouseLeftButtonDown(e); Focus(); CaptureMouse(); Pick(e.GetPosition(this)); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (IsMouseCaptured) Pick(e.GetPosition(this)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); base.OnMouseLeftButtonUp(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Left) hue = (hue + 358) % 360; else if (e.Key == Key.Right) hue = (hue + 2) % 360;
        else if (e.Key == Key.Up) saturation = Math.Min(1, saturation + .02); else if (e.Key == Key.Down) saturation = Math.Max(0, saturation - .02); else return;
        Changed(); e.Handled = true;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); double radius = Math.Min(ActualWidth, ActualHeight) / 2 - 8;
        var rect = new Rect(ActualWidth / 2 - radius, ActualHeight / 2 - radius, radius * 2, radius * 2);
        dc.DrawImage(image, rect);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)((1 - value) * 255), 0, 0, 0)), null, new Point(ActualWidth / 2, ActualHeight / 2), radius, radius);
        var point = new Point(ActualWidth / 2 + Math.Cos(hue * Math.PI / 180) * saturation * radius, ActualHeight / 2 - Math.Sin(hue * Math.PI / 180) * saturation * radius);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 4), point, 6, 6); dc.DrawEllipse(null, new Pen(Brushes.White, 2), point, 6, 6);
    }
    static BitmapSource Build()
    {
        const int size = 256; byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; ++y) for (int x = 0; x < size; ++x) {
            double dx = (x - 127.5) / 127.5, dy = (127.5 - y) / 127.5, s = Math.Sqrt(dx * dx + dy * dy);
            if (s > 1) continue;
            var color = FromHsv((Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360, s, 1); int at = (y * size + x) * 4;
            pixels[at] = color.B; pixels[at + 1] = color.G; pixels[at + 2] = color.R; pixels[at + 3] = 255;
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4); bitmap.Freeze(); return bitmap;
    }
    public static Color FromHsv(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360; saturation = Math.Clamp(saturation, 0, 1); value = Math.Clamp(value, 0, 1);
        double c = value * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - c;
        var (r, g, b) = hue switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
