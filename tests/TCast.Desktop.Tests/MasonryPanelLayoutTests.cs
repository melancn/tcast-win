using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TCast.Desktop.Controls;
using Xunit;

namespace TCast.Desktop.Tests;

public sealed class MasonryPanelLayoutTests
{
    [Fact]
    public void NarrowWindowUsesSingleColumn()
    {
        IReadOnlyList<Rect> rects = ArrangeCards(560, 100, 120, 80);
        Assert.All(rects, rect => Assert.Equal(560, rect.Width, 1));
        Assert.Equal(0, rects[1].X, 1);
        Assert.True(rects[1].Y >= rects[0].Bottom);
    }

    [Fact]
    public void WideWindowFillsShortestColumnFirst()
    {
        IReadOnlyList<Rect> rects = ArrangeCards(1200, 200, 100, 100);
        Assert.Equal(3, rects.Select(rect => Math.Round(rect.X)).Distinct().Count());

        // 第 2 张卡片放到当前最矮列（第 2 列），不是第 1 列的下方。
        Assert.True(rects[1].X > rects[0].X);
        Assert.Equal(0, rects[1].Y, 1);
    }

    [Fact]
    public void CardsNeverOverlapAndStayInsideWidth()
    {
        IReadOnlyList<Rect> rects = ArrangeCards(980, 120, 60, 240, 80, 300, 90);
        for (int i = 0; i < rects.Count; i++)
        {
            Assert.True(rects[i].Right <= 980.1, "卡片超出可用宽度");
            for (int j = i + 1; j < rects.Count; j++)
            {
                Assert.False(rects[i].IntersectsWith(rects[j]), "卡片交叠");
            }
        }
    }

    [Fact]
    public void ReMeasureAfterWidthChangeRecomputesColumns()
    {
        IReadOnlyList<Rect> wide = ArrangeCards(1200, 200, 200);
        IReadOnlyList<Rect> narrow = ArrangeCards(560, 200, 200);

        Assert.NotEqual(Math.Round(wide[1].X), Math.Round(narrow[1].X));
        Assert.Equal(0, narrow[1].X, 1);
    }

    /// <summary>在 STA 线程中测量并排列卡片，返回每张卡片的最终位置。</summary>
    internal static IReadOnlyList<Rect> ArrangeCards(double width, params int[] heights)
    {
        IReadOnlyList<Rect>? result = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var panel = new MasonryPanel { MinCardWidth = 380, Gap = 16, MaxColumns = 3 };
                foreach (int height in heights)
                {
                    panel.Children.Add(new Border { Height = height });
                }

                panel.Measure(new Size(width, double.PositiveInfinity));
                panel.Arrange(new Rect(new Point(0, 0), panel.DesiredSize));
                panel.UpdateLayout();

                // 必须读 Arrange 后的布局位置，而不是 RenderSize（不含 X/Y）。
                result = [.. panel.Children.Cast<UIElement>()
                    .Select(child => new Rect(child.TranslatePoint(new Point(0, 0), panel), child.RenderSize))];
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("MasonryPanel 布局验证失败", failure);
        }

        return result ?? throw new InvalidOperationException("布局未产生结果");
    }
}
