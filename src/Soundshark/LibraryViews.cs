using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Soundshark;

public sealed partial class LibraryPane
{
    Button viewSwitch = null!;
    TextBlock orderHint = null!;
    DispatcherTimer dragScroll = null!;
    SoundRow? dragSound;
    SoundRow[]? reorderOriginal;
    Point pressPoint, pointer;
    double pressOffset;
    bool refreshAfterReorder, savingOrder, savingView;
    bool performanceView;
    DragPreview? dragPreview;
    AdornerLayer? dragLayer;

    internal bool CanArrange => !disposed && main.Settings.LibraryListView && !Theme.Current.ReduceMotion && !savingOrder && !savingView;
    internal bool Arranging => reorderOriginal != null;

    void ConfigureLibraryView()
    {
        bool list = main.Settings.LibraryListView;
        performanceView = Theme.Current.ReduceMotion;
        var panel = new FrameworkElementFactory(list ? typeof(VirtualizingStackPanel) : typeof(SoundTilePanel));
        if (!list) panel.SetResourceReference(SoundTilePanel.TileSizeProperty, "LibraryTileSize");
        rows.ItemsPanel = new ItemsPanelTemplate(panel);
        VirtualizingPanel.SetScrollUnit(rows, ScrollUnit.Pixel);
        var cells = new Style(typeof(ListBoxItem), (Style)FindResource(typeof(ListBoxItem)));
        cells.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, list ? 0 : 8, list ? 6 : 8)));
        cells.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        rows.ItemContainerStyle = cells;
        rows.ItemTemplate = RowTemplate(list);
        viewSwitch.Content = list ? "▦  Grid View" : "☷  List View";
        viewSwitch.ToolTip = list ? "Switch to sound tiles" : "Switch to rows with drag ordering";
        UpdateOrderHint();
    }

    internal async Task SwitchLibraryView()
    {
        if (savingOrder || savingView) return;
        CancelReorder();
        bool previous = main.Settings.LibraryListView;
        main.Settings.LibraryListView = !previous;
        savingView = true; viewSwitch.IsEnabled = false;
        try { await main.Settings.Save(); ConfigureLibraryView(); await Refresh(); }
        catch (Exception ex) { main.Settings.LibraryListView = previous; ConfigureLibraryView(); main.Error(ex, "library.view"); }
        finally { savingView=false; viewSwitch.IsEnabled = true; }
    }

    void UpdateOrderHint()
    {
        if (orderHint == null) return;
        orderHint.Visibility = main.Settings.LibraryListView ? Visibility.Visible : Visibility.Collapsed;
        orderHint.Text = Theme.Current.ReduceMotion ? "Drag ordering is disabled in Performance Mode."
            : main.Settings.LibraryManualOrder ? "Custom order · drag the grips to arrange · choose a sorting option to sort again."
            : "Drag the grips to arrange your sounds · Escape cancels a drag.";
    }

    void InitializeReorder()
    {
        dragScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        dragScroll.Tick += (_, _) => ScrollReorderAtEdge();
        rows.PreviewMouseDown += (_, e) => {
            if (e.ChangedButton != MouseButton.Left || !CanArrange || e.OriginalSource is not DependencyObject source) return;
            var grip = Ancestor<Border>(source, b => b.Name == "SoundDragHandle");
            if (grip == null || grip.DataContext is not SoundRow sound || Ancestor<ListBoxItem>(grip) is not { } item) return;
            dragSound = sound; pressPoint = pointer = e.GetPosition(rows); pressOffset = e.GetPosition(item).Y;
            rows.Focus(); Mouse.Capture(rows); e.Handled = true;
        };
        rows.PreviewMouseMove += (_, e) => {
            if (dragSound == null) return;
            if (e.LeftButton != MouseButtonState.Pressed || !CanArrange) { CancelReorder(); return; }
            pointer = e.GetPosition(rows);
            if (!Arranging && (Math.Abs(pointer.Y - pressPoint.Y) >= SystemParameters.MinimumVerticalDragDistance || Math.Abs(pointer.X - pressPoint.X) >= SystemParameters.MinimumHorizontalDragDistance)) {
                if (!BeginReorder(rows.Items.IndexOf(dragSound))) { CancelReorder(); return; }
                ShowDragPreview();
            }
            if (Arranging) { MoveAtPointer(); e.Handled = true; }
        };
        rows.PreviewMouseUp += async (_, e) => {
            if (e.ChangedButton != MouseButton.Left || dragSound == null) return;
            e.Handled = true;
            var drop = e.GetPosition(rows);
            if (Arranging && CanArrange && new Rect(rows.RenderSize).Contains(drop)) await CompleteReorder(); else CancelReorder();
        };
        rows.PreviewKeyDown += (_, e) => { if (dragSound != null && e.Key == Key.Escape) { CancelReorder(); e.Handled = true; } };
        rows.LostMouseCapture += (_, _) => { if (dragSound != null && Mouse.Captured != rows) CancelReorder(); };
        rows.IsVisibleChanged += (_, _) => { if (!rows.IsVisible) CancelReorder(); };
        main.Deactivated += CancelOnDeactivate;
    }

    void CancelOnDeactivate(object? sender, EventArgs e) => CancelReorder();

    internal void ScrollReorderAtEdge()
    {
        if (!Arranging || !CanArrange || !rows.IsVisible) { CancelReorder(); return; }
        if (FindVisual<ScrollViewer>(rows).FirstOrDefault() is not { } scroll) return;
        double direction = pointer.Y < 38 ? -1 : pointer.Y > rows.ActualHeight - 38 ? 1 : 0;
        if (direction == 0) return;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + direction * 18);
        rows.UpdateLayout(); MoveAtPointer();
    }

    internal bool BeginReorder(int index)
    {
        if (!CanArrange || Arranging || index < 0 || index >= rows.Items.Count) return false;
        reorderOriginal = rows.Items.Cast<SoundRow>().ToArray(); dragSound = reorderOriginal[index];
        return true;
    }

    internal void ShowDragPreview()
    {
        if (dragSound == null || rows.ItemContainerGenerator.ContainerFromItem(dragSound) is not ListBoxItem item) return;
        pressOffset=Math.Clamp(pressOffset,0,item.ActualHeight);
        dragLayer = AdornerLayer.GetAdornerLayer(rows);
        if (dragLayer == null) return;
        var dpi = VisualTreeHelper.GetDpi(item);
        var image = new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(item.ActualWidth*dpi.DpiScaleX)), Math.Max(1,(int)Math.Ceiling(item.ActualHeight*dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(item); image.Freeze();
        dragPreview = new DragPreview(rows, image, item.RenderSize) { IsHitTestVisible=false }; dragLayer.Add(dragPreview);
        MoveAtPointer(); dragScroll.Start();
    }

    internal void UpdateReorderPointer(Point position) { pointer=position; MoveAtPointer(); }

    void MoveAtPointer()
    {
        if (!CanArrange || dragSound == null || !Arranging) return;
        int current = rows.Items.IndexOf(dragSound), target = current;
        foreach (var item in FindVisual<ListBoxItem>(rows)) {
            int index = rows.ItemContainerGenerator.IndexFromContainer(item);
            if (index < 0 || index == current) continue;
            double top = item.TranslatePoint(new Point(), rows).Y - ((item.RenderTransform as TranslateTransform)?.Y ?? 0);
            double middle = top + item.ActualHeight / 2;
            if (index > current && pointer.Y > middle) target = Math.Max(target,index);
            if (index < current && pointer.Y < middle) target = Math.Min(target,index);
        }
        if (target != current) MoveReorder(target);
        foreach (var item in FindVisual<ListBoxItem>(rows)) {
            bool dragged = item.DataContext == dragSound;
            item.Opacity = dragged && dragPreview != null ? .2 : 1;
            Panel.SetZIndex(item,dragged ? 2 : 0);
        }
        if (dragPreview != null) { dragPreview.Top = pointer.Y - pressOffset; dragPreview.InvalidateVisual(); }
    }

    internal bool MoveReorder(int target)
    {
        if (!CanArrange || !Arranging || dragSound == null || rows.ItemsSource is not ObservableCollection<SoundRow> items || target < 0 || target >= items.Count) return false;
        int source = items.IndexOf(dragSound);
        if (source < 0 || source == target) return false;
        // Only realized rows participate in FLIP animation; large collections remain virtualized.
        var before = FindVisual<ListBoxItem>(rows).Where(i=>i.DataContext is SoundRow).ToDictionary(i=>((SoundRow)i.DataContext).Path, i=>i.TranslatePoint(new Point(),rows).Y,StringComparer.OrdinalIgnoreCase);
        ResetRowVisuals();
        items.Move(source,target); rows.UpdateLayout();
        foreach (var item in FindVisual<ListBoxItem>(rows)) {
            if (item.DataContext is not SoundRow row || row == dragSound || !before.TryGetValue(row.Path,out double oldY)) continue;
            double delta = oldY - item.TranslatePoint(new Point(),rows).Y;
            if (Math.Abs(delta)<.5) continue;
            var transform = new TranslateTransform(); item.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(delta,0,TimeSpan.FromMilliseconds(160)) { EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut }, FillBehavior=FillBehavior.Stop });
        }
        return true;
    }

    internal void CancelReorder()
    {
        var original = reorderOriginal;
        EndDragVisuals();
        if (original != null) rows.ItemsSource = new ObservableCollection<SoundRow>(original);
        RefreshAfterDrag();
    }

    internal async Task CompleteReorder()
    {
        if (!CanArrange || reorderOriginal == null) { CancelReorder(); return; }
        var original = reorderOriginal;
        var names = rows.Items.Cast<SoundRow>().Select(r=>Path.GetFileName(r.Path)).ToArray();
        bool changed = !original.Select(r=>Path.GetFileName(r.Path)).SequenceEqual(names,StringComparer.OrdinalIgnoreCase);
        EndDragVisuals();
        if (!changed) { RefreshAfterDrag(); return; }
        var previousOrder = main.Settings.LibraryOrder;
        bool previousManual = main.Settings.LibraryManualOrder, previousGroup = main.Settings.GroupLibraryByColor;
        var appearance = main.Settings.Appearance;
        savingOrder = true; SetOrderControlsEnabled(false);
        try {
            // Replace visible slots only, so filtering never loses or shuffles hidden sounds.
            var all = Directory.EnumerateFiles(Paths.Library).Where(AudioConversion.IsLibraryAudio).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var baseline = previousOrder.Where(all.Contains).Concat(all.OrderBy(n=>n,StringComparer.CurrentCultureIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var visible = names.Where(all.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var replacement = new Queue<string>(names.Where(all.Contains));
            main.Settings.LibraryOrder = baseline.Select(n=>visible.Contains(n) ? replacement.Dequeue() : n).ToList();
            main.Settings.LibraryManualOrder = true; main.Settings.GroupLibraryByColor = false;
            main.Settings.Appearance = appearance with { NewestFirst = false };
            await main.Settings.Save();
            foreach (var check in FindVisual<CheckBox>(this)) {
                if (check.Name is "GroupLibraryByColor" or "NewestSoundsFirst") check.IsChecked = false;
            }
            Theme.Apply(main.Settings.Appearance); UpdateOrderHint();
        }
        catch (Exception ex) {
            main.Settings.LibraryOrder = previousOrder; main.Settings.LibraryManualOrder = previousManual;
            main.Settings.GroupLibraryByColor = previousGroup; main.Settings.Appearance = appearance;
            rows.ItemsSource = new ObservableCollection<SoundRow>(original);
            main.Error(ex,"library.order.save");
        }
        finally { savingOrder = false; SetOrderControlsEnabled(true); await Refresh(); refreshAfterReorder = false; }
    }

    void SetOrderControlsEnabled(bool enabled)
    {
        viewSwitch.IsEnabled=enabled;
        foreach(var check in FindVisual<CheckBox>(this))
            if(check.Name is "GroupLibraryByColor" or "NewestSoundsFirst" or "CompactLibraryTiles") check.IsEnabled=enabled;
    }

    void EndDragVisuals()
    {
        reorderOriginal = null; dragSound = null; dragScroll?.Stop();
        if (dragPreview != null) dragLayer?.Remove(dragPreview);
        dragPreview = null; dragLayer = null;
        ResetRowVisuals();
        if (Mouse.Captured == rows) Mouse.Capture(null);
    }

    void ResetRowVisuals()
    {
        foreach (var item in FindVisual<ListBoxItem>(rows)) { item.RenderTransform = Transform.Identity; item.Opacity = 1; Panel.SetZIndex(item,0); }
    }

    void RefreshAfterDrag()
    {
        if (!refreshAfterReorder || disposed) return;
        refreshAfterReorder = false; _ = Refresh();
    }

    void RenameOrderedSound(string previous, string next)
    {
        int index = main.Settings.LibraryOrder.FindIndex(n=>n.Equals(previous,StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        main.Settings.LibraryOrder[index] = next;
        main.Settings.LibraryOrder = main.Settings.LibraryOrder.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static T? Ancestor<T>(DependencyObject? source, Func<T,bool>? match = null) where T:DependencyObject
    {
        while(source != null) {
            if(source is T result && (match == null || match(result))) return result;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    static IEnumerable<T> FindVisual<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) {
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is T result) yield return result;
            foreach(var descendant in FindVisual<T>(child)) yield return descendant;
        }
    }

    sealed class DragPreview(UIElement target, ImageSource image, Size size) : Adorner(target)
    {
        public double Top { get; set; }
        protected override void OnRender(DrawingContext context)
        {
            context.PushClip(new RectangleGeometry(new Rect(AdornedElement.RenderSize)));
            context.PushOpacity(.95);
            context.DrawRoundedRectangle(Theme.Brush("Surface"),new Pen(Theme.Brush("Accent"),2),new Rect(2,Top,size.Width-4,size.Height),9,9);
            context.DrawImage(image,new Rect(2,Top,size.Width-4,size.Height));
            context.Pop();
            context.Pop();
        }
    }
}
