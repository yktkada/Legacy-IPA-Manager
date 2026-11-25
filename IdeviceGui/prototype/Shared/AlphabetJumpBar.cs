using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

#if IDEVICEINDEX
namespace IdeviceIndex;
#else
using IdeviceGui.ViewModels.Library;

namespace IdeviceGui.Views;
#endif

internal enum JumpBarLayoutTier
{
    Full,
    HideEmpty,
    Compact,
    Scroll,
}

/// <summary>
/// 横向快速定位栏（0-9、A-Z、#）：主程序（资料库/设备应用）与 IdeviceIndex 共用。
/// 自适应优先级：① 空间充足全显（空桶灰置禁用）→ ② 隐藏灰色空桶 → ③ 压缩间距 → ④ 开启横向滚动（Shift+滚轮 + 2.5px 极窄浅滚动条）。
/// </summary>
internal sealed class AlphabetJumpBar : Border
{
    private readonly ScrollViewer _scrollViewer;
    private readonly StackPanel _panel;
    private readonly Button[] _buttons = new Button[AlphabetBuckets.BucketCount];
    private readonly bool[] _available = new bool[AlphabetBuckets.BucketCount];
    private readonly double[] _normalWidths = new double[AlphabetBuckets.BucketCount];
    private readonly double[] _compactWidths = new double[AlphabetBuckets.BucketCount];

    private JumpBarLayoutTier _currentTier = JumpBarLayoutTier.Full;
    private double _lastMeasuredWidth = double.NaN;

    public event Action<int>? BucketClicked;

    internal JumpBarLayoutTier CurrentTierForTests => _currentTier;
    internal IReadOnlyList<Button> ButtonsForTests => _buttons;

    public AlphabetJumpBar()
    {
        BorderThickness = new Thickness(0, 0, 0, 1);
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE2));
        Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFC));
        Padding = new Thickness(2, 2, 2, 1);
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Padding = new Thickness(0),
            Content = _panel,
        };
        _scrollViewer.Resources.Add(typeof(ScrollBar), CreateThinHorizontalScrollBarStyle());
        _scrollViewer.PreviewMouseWheel += OnPreviewMouseWheelScroll;

        Child = _scrollViewer;

        for (var i = 0; i < AlphabetBuckets.BucketCount; i++)
        {
            var bucketIndex = i;
            var btn = new Button
            {
                Content = AlphabetBuckets.Labels[i],
                Style = CreateJumpButtonStyle(),
                Focusable = false,
                IsEnabled = false,
            };
            btn.Click += (_, _) => BucketClicked?.Invoke(bucketIndex);
            _buttons[i] = btn;
            _panel.Children.Add(btn);
        }

        MeasureButtonWidths();
        SizeChanged += OnJumpBarSizeChanged;
    }

    public void UpdateAvailability(int[] counts)
    {
        var changed = false;
        var len = Math.Min(counts.Length, _available.Length);
        for (var i = 0; i < len; i++)
        {
            var has = counts[i] > 0;
            if (_available[i] != has)
            {
                _available[i] = has;
                changed = true;
            }
            _buttons[i].IsEnabled = has;
        }

        if (changed || double.IsNaN(_lastMeasuredWidth))
            ApplyResponsiveLayout(ActualWidth > 0 ? ActualWidth : RenderSize.Width);
    }

    internal void ApplyResponsiveLayoutForTests(double width) => ApplyResponsiveLayout(width);

    private void OnJumpBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - _lastMeasuredWidth) < 0.5) return;
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double containerWidth)
    {
        _lastMeasuredWidth = containerWidth;
        var usableWidth = Math.Max(0, containerWidth - Padding.Left - Padding.Right - 4);
        if (usableWidth <= 0) return;

        var fullWidth = 0.0;
        var activeNormalWidth = 0.0;
        var activeCompactWidth = 0.0;
        for (var i = 0; i < _buttons.Length; i++)
        {
            fullWidth += _normalWidths[i];
            if (_available[i])
            {
                activeNormalWidth += _normalWidths[i];
                activeCompactWidth += _compactWidths[i];
            }
        }

        JumpBarLayoutTier tier;
        if (fullWidth <= usableWidth)
            tier = JumpBarLayoutTier.Full;
        else if (activeNormalWidth <= usableWidth)
            tier = JumpBarLayoutTier.HideEmpty;
        else if (activeCompactWidth <= usableWidth)
            tier = JumpBarLayoutTier.Compact;
        else
            tier = JumpBarLayoutTier.Scroll;

        _currentTier = tier;

        var hideEmpty = tier != JumpBarLayoutTier.Full;
        var compact = tier is JumpBarLayoutTier.Compact or JumpBarLayoutTier.Scroll;

        var pad = compact ? new Thickness(3, 1, 3, 1) : new Thickness(5, 1, 5, 1);
        var margin = compact ? new Thickness(0.5, 0, 0.5, 0) : new Thickness(1.5, 0, 1.5, 0);
        var minWidth = compact ? 15.0 : 19.0;

        for (var i = 0; i < _buttons.Length; i++)
        {
            var btn = _buttons[i];
            btn.Visibility = hideEmpty && !_available[i] ? Visibility.Collapsed : Visibility.Visible;
            btn.Padding = pad;
            btn.Margin = margin;
            btn.MinWidth = minWidth;
        }

        _scrollViewer.HorizontalScrollBarVisibility = tier == JumpBarLayoutTier.Scroll
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;
        if (tier != JumpBarLayoutTier.Scroll)
            _scrollViewer.ScrollToHorizontalOffset(0);
    }

    private void MeasureButtonWidths()
    {
        var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);
        for (var i = 0; i < _buttons.Length; i++)
        {
            var btn = _buttons[i];
            btn.Padding = new Thickness(5, 1, 5, 1);
            btn.Margin = new Thickness(1.5, 0, 1.5, 0);
            btn.MinWidth = 19.0;
            btn.Measure(unlimited);
            _normalWidths[i] = btn.DesiredSize.Width + 3.0;

            btn.Padding = new Thickness(3, 1, 3, 1);
            btn.Margin = new Thickness(0.5, 0, 0.5, 0);
            btn.MinWidth = 15.0;
            btn.Measure(unlimited);
            _compactWidths[i] = btn.DesiredSize.Width + 1.0;

            btn.Padding = new Thickness(5, 1, 5, 1);
            btn.Margin = new Thickness(1.5, 0, 1.5, 0);
            btn.MinWidth = 19.0;
        }
    }

    private void OnPreviewMouseWheelScroll(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift && _scrollViewer.ComputedHorizontalScrollBarVisibility != Visibility.Visible)
            return;
        if (_scrollViewer.ScrollableWidth <= 0) return;

        var step = e.Delta > 0 ? -42.0 : 42.0;
        var next = Math.Clamp(_scrollViewer.HorizontalOffset + step, 0, _scrollViewer.ScrollableWidth);
        _scrollViewer.ScrollToHorizontalOffset(next);
        e.Handled = true;
    }

    private static Style CreateJumpButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 11.0));
        style.Setters.Add(new Setter(HeightProperty, 20.0));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x2D, 0x37, 0x48))));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));

        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        borderFactory.SetBinding(Border.PaddingProperty, new Binding(nameof(Control.Padding)) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });

        var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        cpFactory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cpFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(cpFactory);

        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Button)) { VisualTree = borderFactory }));

        var hoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0))));
        style.Triggers.Add(hoverTrigger);

        var pressedTrigger = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1))));
        style.Triggers.Add(pressedTrigger);

        var disabledTrigger = new Trigger { Property = IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xC0))));
        disabledTrigger.Setters.Add(new Setter(CursorProperty, Cursors.Arrow));
        style.Triggers.Add(disabledTrigger);

        return style;
    }

    private static Style CreateThinHorizontalScrollBarStyle()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="ScrollBar">
                <Style.Triggers>
                    <Trigger Property="Orientation" Value="Horizontal">
                        <Setter Property="Height" Value="3" />
                        <Setter Property="MinHeight" Value="2.5" />
                        <Setter Property="Margin" Value="0,1,0,0" />
                        <Setter Property="Background" Value="Transparent" />
                        <Setter Property="Template">
                            <Setter.Value>
                                <ControlTemplate TargetType="ScrollBar">
                                    <Grid UseLayoutRounding="True" SnapsToDevicePixels="True">
                                        <Border Background="#12000000" CornerRadius="1.5" />
                                        <Track x:Name="PART_Track" IsDirectionReversed="False">
                                            <Track.DecreaseRepeatButton>
                                                <RepeatButton Command="ScrollBar.PageLeftCommand" Opacity="0" Focusable="False" />
                                            </Track.DecreaseRepeatButton>
                                            <Track.IncreaseRepeatButton>
                                                <RepeatButton Command="ScrollBar.PageRightCommand" Opacity="0" Focusable="False" />
                                            </Track.IncreaseRepeatButton>
                                            <Track.Thumb>
                                                <Thumb>
                                                    <Thumb.Template>
                                                        <ControlTemplate TargetType="Thumb">
                                                            <Border Background="#4864748B" CornerRadius="1.5" />
                                                        </ControlTemplate>
                                                    </Thumb.Template>
                                                </Thumb>
                                            </Track.Thumb>
                                        </Track>
                                    </Grid>
                                </ControlTemplate>
                            </Setter.Value>
                        </Setter>
                    </Trigger>
                </Style.Triggers>
            </Style>
            """;
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }
}
