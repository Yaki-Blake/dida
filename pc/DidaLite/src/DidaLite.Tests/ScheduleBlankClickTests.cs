using System.Windows;
using DidaLite.App.Views;

namespace DidaLite.Tests;

/// <summary>
/// 课表页「点空白收起右侧详情栏」的位移判定。
/// 合成鼠标事件拿不到真实光标坐标，离屏窗口上造不出「按下点 → 抬起点」的位移，
/// 所以这段守卫做成纯函数单独测：这是唯一能把它测准的层。
/// </summary>
public class ScheduleBlankClickTests
{
    [Theory]
    // 原地点击：抬起点与按下点重合
    [InlineData(0, 0, true)]
    // 手抖 2–3 像素仍算点击
    [InlineData(2, 0, true)]
    [InlineData(0, 3, true)]
    [InlineData(2, 2, true)]
    // 正好 4 像素：容差边界，仍算点击
    [InlineData(4, 0, true)]
    [InlineData(0, -4, true)]
    // 超过容差就是拖动（拖滚动条、拖选）——绝不能当成点击收起面板
    [InlineData(5, 0, false)]
    [InlineData(0, -5, false)]
    [InlineData(150, 0, false)]
    [InlineData(-150, 0, false)]
    public void IsBlankClick_OnlyAcceptsSmallMovement(double dx, double dy, bool expected)
    {
        var down = new Point(200, 300);
        var up = new Point(down.X + dx, down.Y + dy);

        Assert.Equal(expected, ScheduleView.IsBlankClick(down, up));
    }

    [Fact]
    public void IsBlankClick_IsSymmetric()
    {
        var a = new Point(120, 80);
        var b = new Point(160, 80);

        // 从哪儿到哪儿不影响判断，只看距离
        Assert.Equal(ScheduleView.IsBlankClick(a, b), ScheduleView.IsBlankClick(b, a));
    }

    [Fact]
    public void BlankClickSlop_IsFourPixels()
    {
        // 容差写死在别处的注释里（拖动 vs 点击），这里钉住数值防止被随手改掉
        Assert.Equal(4, ScheduleView.BlankClickSlop);
    }
}