using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

#if IDEVICEINDEX
namespace IdeviceIndex;
#else
namespace IdeviceGui.Views;
#endif

/// <summary>
/// 右键菜单快捷键徽标：把单字母放进最左侧空白图标格（MenuItem.Icon），不在文案末尾或右侧显示 Ctrl+Alt+。
/// 直接绑定 MenuItem.IsEnabled（Source = owner）实现禁用同步变灰且无任何视觉树 FindAncestor 绑定噪音；
/// 已勾选（IsChecked=true）项保留原生勾号。
/// </summary>
internal static class MenuShortcutIcons
{
    private static readonly SolidColorBrush NormalBrush = CreateFrozenBrush(0x64, 0x74, 0x8B);
    private static readonly SolidColorBrush DisabledBrush = CreateFrozenBrush(0xB0, 0xB0, 0xB8);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static TextBlock CreateBadge(string letter, MenuItem owner)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.FontSizeProperty, 11.0));
        style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, NormalBrush));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));

        var disabledTrigger = new DataTrigger
        {
            Binding = new Binding(nameof(UIElement.IsEnabled)) { Source = owner },
            Value = false,
        };
        disabledTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, DisabledBrush));
        style.Triggers.Add(disabledTrigger);

        return new TextBlock
        {
            Text = letter,
            Style = style,
        };
    }

    public static void ApplyFromTags(ItemsControl container)
    {
        foreach (var obj in container.Items)
        {
            if (obj is not MenuItem item) continue;
            item.InputGestureText = "";
            if (!item.IsChecked && item.Tag is string tag && !string.IsNullOrEmpty(tag))
                item.Icon = CreateBadge(tag, item);
            if (item.HasItems)
                ApplyFromTags(item);
        }
    }
}
