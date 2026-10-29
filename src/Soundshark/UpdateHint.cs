using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Soundshark;
public static class UpdateHint
{
    public static void Set(Button button, bool available, bool reduceMotion)
    {
        if (available && button.Effect is DropShadowEffect existing && existing.HasAnimatedProperties == !reduceMotion && existing.Color == ((SolidColorBrush)button.FindResource("Live")).Color) return;
        if (button.Effect is DropShadowEffect old) old.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        button.ClearValue(UIElement.EffectProperty);
        button.ClearValue(Control.BackgroundProperty);
        if (!available) return;
        button.SetResourceReference(Control.BackgroundProperty, "SuccessSurface");
        var glow = new DropShadowEffect { Color = ((SolidColorBrush)button.FindResource("Live")).Color, BlurRadius = 14, ShadowDepth = 0, Opacity = .65 };
        button.Effect = glow;
        if (!reduceMotion) glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(.25, .9, TimeSpan.FromSeconds(1.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }
}
