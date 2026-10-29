using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Soundshark;
public static class Ui
{
    public static TextBlock Text(string text, double size = 14, bool muted = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Text");
        block.SetResourceReference(TextBlock.FontSizeProperty, "Font" + size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return block;
    }
    public static Button Button(string text, Action click) { var b = new Button { Content = text }; b.Click += (_, _) => click(); return b; }
    public static WrapPanel Row(params UIElement[] items) { var p = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) }; foreach (var i in items) { if (i is FrameworkElement f) f.Margin = new Thickness(0, 0, 8, 8); p.Children.Add(i); } return p; }
    public static Border Card(UIElement child)
    {
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(20), Child = child };
        card.SetResourceReference(Border.BackgroundProperty, "Surface"); card.SetResourceReference(Border.BorderBrushProperty, "Border"); return card;
    }
    public static string? Ask(Window owner, string title, string label, string initial = "")
    {
        var input = new TextBox { Text = initial, MinWidth = 330, Margin = new Thickness(0, 14, 0, 18) };
        var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(Text(label)); panel.Children.Add(input);
        var dialog = new Window { Owner = owner, Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, ShowInTaskbar = false };
        var yes = Button("Continue", () => dialog.DialogResult = true); yes.IsDefault = true;
        var no = Button("Cancel", () => dialog.DialogResult = false); no.IsCancel = true;
        panel.Children.Add(Row(yes, no)); dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }
}
