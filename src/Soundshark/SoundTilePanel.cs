using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Soundshark;

// Fixed-height, responsive tiles. Only visible rows are generated, even in large libraries.
public sealed class SoundTilePanel : VirtualizingPanel, IScrollInfo
{
    int columns = 1;
    double tileWidth, tileHeight, offset;
    Size extent, viewport;
    public static readonly DependencyProperty TileSizeProperty = DependencyProperty.Register(
        nameof(TileSize), typeof(Size), typeof(SoundTilePanel), new FrameworkPropertyMetadata(new Size(248, 108), FrameworkPropertyMetadataOptions.AffectsMeasure));
    public Size TileSize { get => (Size)GetValue(TileSizeProperty); set => SetValue(TileSizeProperty, value); }
    protected override Size MeasureOverride(Size available)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner == null) return available;
        double width = double.IsFinite(available.Width) ? available.Width : TileSize.Width;
        double height = double.IsFinite(available.Height) ? available.Height : TileSize.Height;
        columns = Math.Max(1, (int)(width / TileSize.Width));
        tileWidth = width / columns; tileHeight = TileSize.Height;
        viewport = new Size(width, height);
        extent = new Size(width, Math.Ceiling(owner.Items.Count / (double)columns) * tileHeight);
        offset = Math.Clamp(offset, 0, Math.Max(0, extent.Height - height));
        ScrollOwner?.InvalidateScrollInfo();
        int first = Math.Min(owner.Items.Count, (int)(offset / tileHeight) * columns);
        int last = Math.Min(owner.Items.Count - 1, (int)Math.Ceiling((offset + height) / tileHeight) * columns - 1);
        // InternalChildren connects the ItemsHost to its generator on first use.
        // Read it before caching ItemContainerGenerator: a first tab focus can
        // measure this panel before any visual-tree traversal has initialized it.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        // Remove offscreen containers before generating replacements.
        for (int i = children.Count - 1; i >= 0; i--) {
            int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < first || index > last) { generator.Remove(new GeneratorPosition(i, 0), 1); RemoveInternalChildRange(i, 1); }
        }
        if (last >= first) {
            var position = generator.GeneratorPositionFromIndex(first);
            int childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
            using (generator.StartAt(position, GeneratorDirection.Forward, true)) {
                for (int index = first; index <= last; index++, childIndex++) {
                    var child = (UIElement)generator.GenerateNext(out bool fresh);
                    if (fresh) { InsertInternalChild(childIndex, child); generator.PrepareItemContainer(child); }
                    child.Measure(new Size(tileWidth, tileHeight));
                }
            }
        }
        return viewport;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < InternalChildren.Count; i++) {
            int index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            InternalChildren[i].Arrange(new Rect(index % columns * tileWidth, index / columns * tileHeight - offset, tileWidth, tileHeight));
        }
        return finalSize;
    }
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        if (args.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Remove or System.Collections.Specialized.NotifyCollectionChangedAction.Replace or System.Collections.Specialized.NotifyCollectionChangedAction.Move)
            RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
        InvalidateMeasure();
    }
    protected override void BringIndexIntoView(int index)
    {
        double top = index / columns * tileHeight;
        if (top < offset) SetVerticalOffset(top);
        else if (top + tileHeight > offset + viewport.Height) SetVerticalOffset(top + tileHeight - viewport.Height);
    }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        DependencyObject child = visual;
        while (VisualTreeHelper.GetParent(child) is DependencyObject parent && parent != this) child = parent;
        int index = InternalChildren.IndexOf(child as UIElement);
        if (index >= 0) BringIndexIntoView(ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(index, 0)));
        return rectangle;
    }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => extent.Width;
    public double ExtentHeight => extent.Height;
    public double ViewportWidth => viewport.Width;
    public double ViewportHeight => viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => offset;
    public ScrollViewer? ScrollOwner { get; set; }
    public void SetHorizontalOffset(double value) { }
    public void SetVerticalOffset(double value) {
        double next = Math.Clamp(value, 0, Math.Max(0, extent.Height - viewport.Height));
        if (next == offset) return;
        offset = next; InvalidateMeasure(); ScrollOwner?.InvalidateScrollInfo();
    }
    public void LineUp() => SetVerticalOffset(offset - tileHeight);
    public void LineDown() => SetVerticalOffset(offset + tileHeight);
    public void MouseWheelUp() => SetVerticalOffset(offset - tileHeight * 2);
    public void MouseWheelDown() => SetVerticalOffset(offset + tileHeight * 2);
    public void PageUp() => SetVerticalOffset(offset - viewport.Height);
    public void PageDown() => SetVerticalOffset(offset + viewport.Height);
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
}
