namespace WinLinScp.ViewModels;

/// <summary>面包屑地址栏中的一段：如「game」→ D:\game、远端「home」→ /home。</summary>
public sealed class BreadcrumbSegment
{
    public string Text { get; init; } = "";
    public string FullPath { get; init; } = "";
    /// <summary>非首段显示前置分隔符 ›。</summary>
    public bool ShowChevron { get; init; }
    /// <summary>当前所在目录（末段），加粗高亮。</summary>
    public bool IsCurrent { get; init; }
}
