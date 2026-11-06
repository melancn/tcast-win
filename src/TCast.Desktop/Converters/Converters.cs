using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TCast.Infrastructure.Discovery;

namespace TCast.Desktop.Converters;

/// <summary>发现状态 → 中文文案。不靠颜色单独区分状态。</summary>
public sealed class DiscoveryStateTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DiscoveryState state
            ? state switch
            {
                DiscoveryState.Scanning => "发现中",
                DiscoveryState.Available => "在线",
                DiscoveryState.Stale => "未再次发现",
                DiscoveryState.Exited => "已退出",
                DiscoveryState.Faulted => "网络错误",
                _ => "未开始",
            }
            : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>空文本（null/空白）显示为 “—”，用于 MAC、版本等可选字段。</summary>
public sealed class TextOrDashConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? "—" : value!;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → Collapsed，用于“未连接提示”等反向可见性。</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
