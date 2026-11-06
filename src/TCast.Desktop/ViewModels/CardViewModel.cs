namespace TCast.Desktop.ViewModels;

/// <summary>
/// 瀑布流卡片的数据外壳：卡片标题 + 内容。
/// </summary>
/// <param name="Title">卡片标题。</param>
/// <param name="Content">卡片正文 ViewModel 或文本。</param>
/// <param name="Key">稳定键，用于测试与定位。</param>
public sealed record CardViewModel(string Title, object? Content, string Key);

/// <summary>
/// 顶部摘要项（发现页保持三项）。
/// </summary>
/// <param name="Label">标签。</param>
/// <param name="Value">值。</param>
public sealed record SummaryItemViewModel(string Label, string Value);
