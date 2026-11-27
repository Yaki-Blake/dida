namespace DidaLite.App.Views;

/// <summary>
/// 侧栏与主区的宽度分配规则（纯计算，便于单独验证）。
/// 原则：侧栏宽度优先。主区放不下完整头部时压缩主区，绝不反过来把侧栏按到最窄。
/// </summary>
public static class HeaderWidthPolicy
{
    /// <summary>主区绝对下限：再窄界面就没法用了。</summary>
    public const double MainFloor = 300;

    /// <summary>头部可以被压到的宽度：标题与按钮组仍要完整放下。</summary>
    public const double HeaderTrimFloor = 460;

    /// <summary>
    /// 主区最小宽度：先保证侧栏拿到想显示的宽度，剩下的都给主区；
    /// 剩下的不够放头部时，头部最多压到 <see cref="HeaderTrimFloor"/>。
    /// </summary>
    public static double MainMinWidth(double available, double headerNeed, double desiredSidebar)
    {
        var target = Math.Min(headerNeed, available - desiredSidebar);
        if (target < HeaderTrimFloor) target = HeaderTrimFloor;
        if (target > headerNeed) target = headerNeed;
        return Math.Max(MainFloor, target);
    }

    /// <summary>侧栏能拖到的最大宽度：主区留够它压缩后的下限，别把它挤没。</summary>
    public static double SidebarMaxWidth(double available, double sidebarMin)
        => Math.Max(sidebarMin, available - Math.Max(MainFloor, HeaderTrimFloor));

    /// <summary>
    /// 根布局还没量出真实宽度（首帧为 0 或极小）时不要夹侧栏：
    /// 这时可用宽度会算成负数，一夹就把默认宽度永久按到下限，之后窗口再宽也回不来。
    /// </summary>
    public static bool IsMeasured(double rootWidth, double windowMinWidth) => rootWidth >= windowMinWidth;

    /// <summary>目标宽度：夹在下限与上限之间，尽量等于想显示的值。</summary>
    public static double Clamp(double desired, double min, double max)
        => Math.Max(min, Math.Min(max, desired));
}
