using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Soundshark.Core;

namespace Soundshark;
public sealed class Waveform : FrameworkElement
{
    public Workspace Workspace { get; set; } = new();
    public Peaks? Peaks { get; set; }
    public float[] Live { get; set; } = [];
    public long A { get; set; }
    public long B { get; set; }
    readonly DrawingVisual playhead = new();
    long cursorFrame;
    public long CursorFrame { get => cursorFrame; set { if (cursorFrame == value) return; cursorFrame = value; DrawPlayhead(); } }
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? playhead : throw new ArgumentOutOfRangeException(nameof(index));
    double zoom = 1, scroll;
    public double Zoom { get => zoom; set { zoom = double.IsFinite(value) ? Math.Max(1, value) : 1; if (zoom == 1) scroll = 0; ViewChanged?.Invoke(); InvalidateVisual(); } }
    public double Scroll { get => scroll; set { scroll = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0; ViewChanged?.Invoke(); InvalidateVisual(); } }
    public event Action? ViewChanged;
    public event Action? SelectionChanged;
    public event Action? Seeked;
    int drag; long anchor;
    static readonly Cursor grabCursor = LoadCursor("grab"), grabbingCursor = LoadCursor("grabbing");
    static Cursor LoadCursor(string name)
    {
        try
        {
            using var stream = Application.GetResourceStream(new Uri($"/Soundshark;component/Assets/{name}.cur", UriKind.Relative))!.Stream;
            return new Cursor(stream);
        }
        catch (Exception ex) { AppLog.Warn("cursor.load", ex); return Cursors.Hand; }
    }
    int HandleAt(Point point)
    {
        if (B <= A || point.Y < 0 || point.Y > 24) return 0;
        return Math.Abs(point.X - X(A)) < 10 ? 1 : Math.Abs(point.X - X(B)) < 10 ? 2 : 0;
    }
    public Cursor PointerAt(Point point) => HandleAt(point) != 0 ? grabCursor : Cursors.Arrow;
    protected override void OnQueryCursor(QueryCursorEventArgs e)
    {
        base.OnQueryCursor(e);
        e.Cursor = IsMouseCaptured && drag is 1 or 2 ? grabbingCursor : PointerAt(e.GetPosition(this)); e.Handled = true;
    }
    public long ViewStart => (long)(Math.Max(0, Workspace.Frames - ViewFrames) * Scroll);
    public long ViewFrames => Math.Max(1, (long)(Workspace.Frames / Zoom));
    public Waveform()
    {
        AddVisualChild(playhead);
        Loaded += (_, _) => Theme.Changed += RefreshTheme;
        Unloaded += (_, _) => Theme.Changed -= RefreshTheme;
        Focusable = true; Cursor = Cursors.Arrow; MinHeight = 180;
        System.Windows.Automation.AutomationProperties.SetName(this, "Audio waveform. Drag to select, drag selection edges to adjust, double click to select all.");
        ToolTip = "Drag to highlight audio. Mouse wheel down moves later; up moves earlier.";
    }
    void RefreshTheme() { InvalidateVisual(); DrawPlayhead(); }
    public void RevealCursor()
    {
        if (CursorFrame >= ViewStart && CursorFrame < ViewStart + ViewFrames) return;
        Scroll = Workspace.Frames > ViewFrames ? Math.Clamp(CursorFrame / (double)(Workspace.Frames - ViewFrames), 0, 1) : 0;
        InvalidateVisual();
    }
    void DrawPlayhead()
    {
        using var dc = playhead.RenderOpen();
        if (ActualWidth <= 0 || ActualHeight <= 0 || Workspace.Frames == 0 && Live.Length == 0) return;
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        double x = X(CursorFrame);
        dc.DrawLine(new Pen(Theme.Brush("Text"), 1), new Point(x, 0), new Point(x, ActualHeight));
        dc.Pop();
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Workspace.Frames <= ViewFrames) { e.Handled = true; return; }
        double distance = -e.Delta / 120.0 * ViewFrames * .15;
        Scroll = Math.Clamp(Scroll + distance / (Workspace.Frames - ViewFrames), 0, 1);
        InvalidateVisual(); e.Handled = true;
    }
    double X(long frame) => (double)(frame - ViewStart) / ViewFrames * ActualWidth;
    long Frame(double x) => Math.Clamp(ViewStart + (long)(Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * ViewFrames), 0, Workspace.Frames);
    protected override void OnRender(DrawingContext dc)
    {
        DrawPlayhead();
        base.OnRender(dc); dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        Brush wave = Theme.Brush("Accent");
        dc.DrawRoundedRectangle(Theme.Brush("WaveBackground"), null, new Rect(RenderSize), 12, 12);
        var grid = new Pen(Theme.Brush("WaveGrid"), 1);
        for (int i = 1; i < 8; i++) dc.DrawLine(grid, new Point(i * ActualWidth / 8, 0), new Point(i * ActualWidth / 8, ActualHeight));
        if (Workspace.Frames == 0 && Live.Length == 0) { dc.Pop(); return; }
        dc.DrawLine(grid, new Point(0, ActualHeight / 2), new Point(ActualWidth, ActualHeight / 2));
        if (B > A) dc.DrawRectangle(Theme.Brush("WaveSelection"), null, new Rect(X(A), 0, Math.Max(0, X(B) - X(A)), ActualHeight));
        var pen = new Pen(wave, 1.6);
        for (int x = 0; x < ActualWidth; x += 2)
        {
            float p = Live.Length > 0 ? Live[Math.Min(Live.Length - 1, x * Live.Length / Math.Max(1, (int)ActualWidth))] : Peaks?.At(Workspace, Frame(x), Math.Max(1, ViewFrames * 2 / Math.Max(1, (long)ActualWidth))) ?? 0;
            double height = Math.Max(1, p * (Live.Length>0?1:Workspace.Volume) * ActualHeight * .40);
            dc.DrawLine(pen, new Point(x, ActualHeight / 2 - height), new Point(x, ActualHeight / 2 + height));
        }
        if (B > A) foreach (long boundary in new[] { A, B })
        { dc.DrawLine(new Pen(Theme.Brush("Text"), 2), new Point(X(boundary), 0), new Point(X(boundary), ActualHeight)); dc.DrawRoundedRectangle(wave, null, new Rect(X(boundary) - 4, 0, 8, 22), 3, 3); }
        dc.Pop();
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus(); if (Workspace.Frames == 0) return;
        if (e.ClickCount == 2) { A = 0; B = Workspace.Frames; SelectionChanged?.Invoke(); InvalidateVisual(); return; }
        double x = e.GetPosition(this).X;
        int handle = HandleAt(e.GetPosition(this)); drag = handle == 0 ? 3 : handle;
        anchor = Frame(x);
        if (drag == 3) { A = B = anchor; CursorFrame = anchor; Seeked?.Invoke(); }
        CaptureMouse(); Mouse.UpdateCursor(); SelectionChanged?.Invoke(); InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if (!IsMouseCaptured) return;
        long f = Frame(e.GetPosition(this).X);
        if (drag == 1) A = Math.Min(f, B); else if (drag == 2) B = Math.Max(f, A); else { A = Math.Min(anchor, f); B = Math.Max(anchor, f); }
        SelectionChanged?.Invoke(); InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); Mouse.UpdateCursor(); base.OnMouseLeftButtonUp(e); }
}
