using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DidaLite.App.Behaviors;

namespace DidaLite.Tests;

/// <summary>
/// 滚轮自动滚动的速度曲线：死区、单调、封顶、横向压制。
/// 这条曲线就是手感本身，钉住它，免得以后调参数调成「手一动画面就飞走」。
/// </summary>
public class MiddleDragScrollTests
{
    [Fact]
    public void SmallJitterDoesNotScroll()
    {
        var v = MiddleDragScroll.TargetVelocity(3, 4, 700); // 5px，还在死区里
        Assert.Equal(0, v.X, 6);
        Assert.Equal(0, v.Y, 6);
    }

    [Fact]
    public void DownwardPointerScrollsDown()
    {
        var v = MiddleDragScroll.TargetVelocity(0, 40, 700);
        Assert.True(v.Y > 0, $"向下偏移应产生向下速度，实际 {v.Y}");
        Assert.Equal(0, v.X, 6);
    }

    [Fact]
    public void SpeedGrowsWithDistance()
    {
        var near = MiddleDragScroll.TargetVelocity(0, 20, 700).Y;
        var mid = MiddleDragScroll.TargetVelocity(0, 120, 700).Y;
        var far = MiddleDragScroll.TargetVelocity(0, 260, 700).Y;

        Assert.True(near > 0, "死区外应开始滚动");
        Assert.True(mid > near, $"120px 应快于 20px：{mid} vs {near}");
        Assert.True(far > mid, $"260px 应快于 120px：{far} vs {mid}");
    }

    [Fact]
    public void SpeedIsCappedAndScalesWithViewport()
    {
        var atTop = MiddleDragScroll
            .TargetVelocity(0, MiddleDragScroll.DeadZone + MiddleDragScroll.TopSpeedDistance, 700).Y;
        var beyond = MiddleDragScroll.TargetVelocity(0, 2000, 700).Y;

        Assert.Equal(atTop, beyond, 3);
        Assert.Equal(700 * MiddleDragScroll.ViewportSpeedFactor, atTop, 3);

        var tall = MiddleDragScroll.TargetVelocity(0, 2000, 1200).Y;
        Assert.True(tall > beyond, $"视口越高顶速越快：{tall} vs {beyond}");
    }

    [Fact]
    public void TinyViewportKeepsMinimumSpeed()
    {
        var v = MiddleDragScroll.TargetVelocity(0, 2000, 40).Y; // 40 × 2.6 低于保底
        Assert.Equal(MiddleDragScroll.MinTopSpeed, v, 3);
    }

    [Fact]
    public void HorizontalIsDamped()
    {
        var vertical = MiddleDragScroll.TargetVelocity(0, 200, 700).Y;
        var horizontal = MiddleDragScroll.TargetVelocity(200, 0, 700).X;

        Assert.True(horizontal > 0);
        Assert.Equal(vertical * MiddleDragScroll.HorizontalDamping, horizontal, 3);
    }

    [Fact]
    public void DegenerateInputDoesNotThrow()
    {
        var v = MiddleDragScroll.TargetVelocity(double.NaN, 0, double.NaN);
        Assert.Equal(0, v.X, 6);
        Assert.Equal(0, v.Y, 6);
    }

    [Fact]
    public void PointerOffsetActuallyScrollsARealScrollViewer()
    {
        RunOnSta(() =>
        {
            using var host = OffscreenHost.Create(out var viewer);
            var anchor = new Point(viewer.ActualWidth / 2, viewer.ActualHeight / 2);
            var start = viewer.VerticalOffset;

            // 指针往下偏 240px 跑 0.5 秒：画面必须真的往下走
            var afterDown = MiddleDragScroll.AdvanceForTest(
                viewer, anchor, new Point(anchor.X, anchor.Y + 240), 30, 1.0 / 60);
            Assert.True(afterDown > start + 40, $"向下偏移应滚下去：{start} → {afterDown}");

            // 反向偏移：画面往回走
            var afterUp = MiddleDragScroll.AdvanceForTest(
                viewer, anchor, new Point(anchor.X, anchor.Y - 240), 20, 1.0 / 60);
            Assert.True(afterUp < afterDown - 40, $"向上偏移应滚回来：{afterDown} → {afterUp}");

            // 死区内不该动
            var still = MiddleDragScroll.AdvanceForTest(
                viewer, anchor, new Point(anchor.X + 3, anchor.Y + 3), 30, 1.0 / 60);
            Assert.Equal(afterUp, still, 1);
        });
    }

    [Fact]
    public void MiddleClickEntersModeAndLeftClickLeavesIt()
    {
        RunOnSta(() =>
        {
            using var host = OffscreenHost.Create(out var viewer);

            viewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle)
            {
                RoutedEvent = UIElement.PreviewMouseDownEvent,
            });
            Assert.True(MiddleDragScroll.IsActive, "中键按下应进入滚动模式");

            viewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseDownEvent,
            });
            Assert.False(MiddleDragScroll.IsActive, "左键应退出滚动模式");
        });
    }

    /// <summary>
    /// 离屏窗口：放在屏幕外，不激活、不进任务栏，只用来给 ScrollViewer 一个真实的
    /// 视口尺寸——不然 ScrollableHeight 永远是 0，测的是空气。
    /// </summary>
    private sealed class OffscreenHost : IDisposable
    {
        private readonly Window _window;

        private OffscreenHost(Window window, ScrollViewer viewer)
        {
            _window = window;
            Viewer = viewer;
        }

        public ScrollViewer Viewer { get; }

        public static OffscreenHost Create(out ScrollViewer viewer)
        {
            var content = new StackPanel { Height = 3000 };
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content,
            };

            var window = new Window
            {
                Width = 900,
                Height = 600,
                Left = -4000,
                Top = -4000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = scroll,
            };

            window.Show();
            window.UpdateLayout();

            Assert.True(scroll.ScrollableHeight > 100, $"需要有可滚内容，实际 {scroll.ScrollableHeight}");

            viewer = scroll;
            return new OffscreenHost(window, scroll);
        }

        public void Dispose() => _window.Close();
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}