using System.Windows;
using System.Windows.Controls;

namespace TCast.Desktop.Controls;

/// <summary>
/// 最短列瀑布流面板：每张卡片独立测量高度，放入当前最矮的列；相同高度优先左侧列。
/// </summary>
/// <remarks>
/// 普通 WrapPanel 只能按行换行，不满足需求。本面板专用于每页 2–4 张业务卡片，
/// <b>没有虚拟化</b>，不要用它承载几千个设备项（设备列表另用虚拟化 ListView）。
/// 列数由实际可用宽度决定，不按屏幕分辨率猜测。
/// </remarks>
public sealed class MasonryPanel : Panel
{
    private const FrameworkPropertyMetadataOptions LayoutFlags =
        FrameworkPropertyMetadataOptions.AffectsMeasure |
        FrameworkPropertyMetadataOptions.AffectsArrange;

    /// <summary>卡片最小目标宽度（DIP）。</summary>
    public static readonly DependencyProperty MinCardWidthProperty = DependencyProperty.Register(
        nameof(MinCardWidth),
        typeof(double),
        typeof(MasonryPanel),
        new FrameworkPropertyMetadata(380d, LayoutFlags),
        value => value is double width && double.IsFinite(width) && width > 0);

    /// <summary>卡片间隔（DIP）。间隔只在面板计算一次，卡片外层不要再加同值 Margin。</summary>
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap),
        typeof(double),
        typeof(MasonryPanel),
        new FrameworkPropertyMetadata(16d, LayoutFlags),
        value => value is double gap && double.IsFinite(gap) && gap >= 0);

    /// <summary>最大列数。</summary>
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns),
        typeof(int),
        typeof(MasonryPanel),
        new FrameworkPropertyMetadata(3, LayoutFlags),
        value => value is int columns && columns is >= 1 and <= 12);

    /// <summary>卡片最小目标宽度（DIP）。</summary>
    public double MinCardWidth
    {
        get => (double)GetValue(MinCardWidthProperty);
        set => SetValue(MinCardWidthProperty, value);
    }

    /// <summary>卡片间隔（DIP）。</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>最大列数。</summary>
    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width)
            ? Math.Max(0, availableSize.Width)
            : MinCardWidth;
        return new Size(width, LayoutCards(width, arrange: false));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        LayoutCards(finalSize.Width, arrange: true);
        return finalSize;
    }

    private double LayoutCards(double width, bool arrange)
    {
        int columns = (int)Math.Clamp(Math.Floor((width + Gap) / (MinCardWidth + Gap)), 1, MaxColumns);
        double cardWidth = Math.Max(0, (width - (Gap * (columns - 1))) / columns);
        var heights = new double[columns];

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            // 同宽重复 Measure 由 WPF 缓存；宽度变化时才真正重测。
            child.Measure(new Size(cardWidth, double.PositiveInfinity));
            int column = Array.IndexOf(heights, heights.Min());
            double height = child.DesiredSize.Height;
            if (arrange)
            {
                child.Arrange(new Rect(column * (cardWidth + Gap), heights[column], cardWidth, height));
            }

            heights[column] += height + Gap;
        }

        return Math.Max(0, heights.Max() - Gap);
    }
}
