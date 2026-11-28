using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DidaLite.App.Behaviors;

/// <summary>
/// 滚轮（中键）自动滚动，手感对齐桌面浏览器：
/// 点一下中键进入模式 → 鼠标离开按下点就开始滚，离得越远越快；
/// 左键点一下 / 再按中键 / 拨滚轮 / Esc / 窗口失活 才退出。
///
/// 用类处理器挂在 <see cref="ScrollViewer"/> 类型上，而不是逐个 XAML 标注附加属性，
/// 所以任何窗口里任何 ScrollViewer（含以后新加的页面）都自动生效。
/// 只对「鼠标下最内层且真的能滚」的那个生效，嵌套 ScrollViewer 不会一起滚。
/// </summary>
public static class MiddleDragScroll
{
    // 手感参数：一趟都由 TargetVelocity 的纯函数决定，方便单测钉住曲线
    internal const double DeadZone = 8.0;              // 死区，手抖不滚
    internal const double TopSpeedDistance = 280.0;    // 到这个偏移量就到顶速
    internal const double SpeedExponent = 1.7;         // 越大 → 近处越细腻、远处越冲
    internal const double ViewportSpeedFactor = 2.6;   // 顶速 = 视口高 × 2.6 / 秒
    internal const double MinTopSpeed = 420.0;         // 视口很矮时的保底顶速
    internal const double HorizontalDamping = 0.45;    // 横向故意给弱，避免斜着甩

    private const double EaseRate = 18.0;              // 速度插值，避免一帧跳变

    private static readonly DispatcherTimer Timer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(15),
    };

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static ScrollViewer? _viewer;
    private static Window? _window;
    private static Point _anchor;
    private static Point _pointer;
    private static Vector _velocity;
    private static long _lastTick;
    private static ScrollIndicator? _indicator;

    /// <summary>在 App.OnStartup 里调一次即可；静态构造负责把类处理器挂上去。</summary>
    public static void Install()
    {
    }

    static MiddleDragScroll()
    {
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer), Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown), true);
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer), Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnPreviewMouseMove), true);
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer), Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel), true);
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer), UIElement.LostMouseCaptureEvent, new MouseEventHandler(OnLostMouseCapture), true);

        Timer.Tick += OnTimerTick;
    }

    /// <summary>测试用：当前是否处于滚动模式。</summary>
    internal static bool IsActive => _viewer is not null;

    /// <summary>
    /// 由鼠标相对按下点的偏移算出这一帧想要的速度（px/s）。
    /// 死区内为 0；外部按 (偏移 / TopSpeedDistance)^SpeedExponent 放大，到顶封住。
    /// 顶速跟视口高度挂钩，窗口大一点不会显得拖沓。
    /// </summary>
    internal static Vector TargetVelocity(double dx, double dy, double viewportHeight)
    {
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (double.IsNaN(distance) || distance <= DeadZone) return default;

        var top = Math.Max(MinTopSpeed, viewportHeight * ViewportSpeedFactor);
        var t = Math.Min(1.0, (distance - DeadZone) / TopSpeedDistance);
        var speed = top * Math.Pow(t, SpeedExponent);

        return new Vector(dx / distance * speed * HorizontalDamping, dy / distance * speed);
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;

        if (e.ChangedButton == MouseButton.Middle)
        {
            // 再按一次中键 = 撤销
            if (ReferenceEquals(viewer, _viewer))
            {
                Stop();
                e.Handled = true;
                return;
            }

            if (!IsInnermostAt(viewer, e.OriginalSource)) return;
            if (!CanScroll(viewer)) return;

            Start(viewer, e.GetPosition(viewer));
            e.Handled = true;
            return;
        }

        // 模式里左键只负责退出，不落到下面的按钮 / 卡片上——浏览器也是这个行为
        if (e.ChangedButton == MouseButton.Left && ReferenceEquals(viewer, _viewer))
        {
            Stop();
            e.Handled = true;
        }
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_viewer is null || !ReferenceEquals(sender, _viewer)) return;

        _pointer = e.GetPosition(_viewer);
        e.Handled = true;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 一旦拨滚轮就交还控制权，让 ScrollViewer 照常滚动这一下
        if (ReferenceEquals(sender, _viewer)) Stop();
    }

    private static void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_viewer is null || !ReferenceEquals(sender, _viewer)) return;
        Stop();
    }

    private static void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Stop();
    }

    private static void OnWindowDeactivated(object? sender, EventArgs e) => Stop();

    private static void OnViewerUnloaded(object sender, RoutedEventArgs e) => Stop();

    private static void OnTimerTick(object? sender, EventArgs e)
    {
        if (_viewer is null)
        {
            Timer.Stop();
            return;
        }

        // 页面切走 / 窗口收起来时挂着模式没有意义，停掉免得下次进来莫名滚动
        if (!_viewer.IsVisible || !_viewer.IsLoaded)
        {
            Stop();
            return;
        }

        var now = Clock.ElapsedTicks;
        var dt = (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;
        if (dt <= 0) return;
        if (dt > 0.05) dt = 0.05;

        var target = TargetVelocity(_pointer.X - _anchor.X, _pointer.Y - _anchor.Y, _viewer.ViewportHeight);
        _velocity += (target - _velocity) * (1 - Math.Exp(-EaseRate * dt));

        if (Math.Abs(_velocity.X) < 0.5 && Math.Abs(_velocity.Y) < 0.5)
        {
            _velocity = default;
            return;
        }

        ApplyScroll(_viewer, _velocity, dt);
    }

    /// <summary>
    /// 把一帧的速度落到 ScrollViewer 上。抽出来是为了让离屏测试能直接驱动，
    /// 不必真的去移动用户鼠标。
    /// </summary>
    private static void ApplyScroll(ScrollViewer viewer, Vector velocity, double dt)
    {
        if (viewer.ScrollableHeight > 0)
        {
            var to = viewer.VerticalOffset + velocity.Y * dt;
            viewer.ScrollToVerticalOffset(Math.Clamp(to, 0, viewer.ScrollableHeight));
        }

        if (viewer.ScrollableWidth > 0)
        {
            var to = viewer.HorizontalOffset + velocity.X * dt;
            viewer.ScrollToHorizontalOffset(Math.Clamp(to, 0, viewer.ScrollableWidth));
        }
    }

    /// <summary>
    /// 测试用：按给定锚点 / 指针位置推进若干帧，走的是和 Timer 完全相同的速度曲线与落点逻辑。
    /// 返回最终垂直偏移，方便断言方向与幅度。
    /// </summary>
    internal static double AdvanceForTest(ScrollViewer viewer, Point anchor, Point pointer, int frames, double frameSeconds)
    {
        var velocity = default(Vector);
        for (var i = 0; i < frames; i++)
        {
            var target = TargetVelocity(pointer.X - anchor.X, pointer.Y - anchor.Y, viewer.ViewportHeight);
            velocity += (target - velocity) * (1 - Math.Exp(-EaseRate * frameSeconds));
            ApplyScroll(viewer, velocity, frameSeconds);

            // 真实运行时每帧都有布局/渲染把 ScrollToVerticalOffset 落到 ScrollContentPresenter 上；
            // 离屏窗口没有渲染循环，必须手动催一次，否则 VerticalOffset 一直读回 0。
            viewer.UpdateLayout();
        }

        return viewer.VerticalOffset;
    }

    private static void Start(ScrollViewer viewer, Point anchor)
    {
        Stop();

        _viewer = viewer;
        _anchor = anchor;
        _pointer = anchor;
        _velocity = default;
        _lastTick = Clock.ElapsedTicks;

        // 捕获鼠标：哪怕指针滑出窗口也继续收得到移动，模式不会莫名断掉
        viewer.CaptureMouse();
        viewer.Unloaded += OnViewerUnloaded;

        _window = Window.GetWindow(viewer);
        if (_window is not null)
        {
            _window.PreviewKeyDown += OnWindowPreviewKeyDown;
            _window.Deactivated += OnWindowDeactivated;
        }

        _indicator = ScrollIndicator.Attach(viewer, anchor);
        Timer.Start();
    }

    private static void Stop()
    {
        // 先把引用摘掉：ReleaseMouseCapture 会同步回调 OnLostMouseCapture，
        // 那条路上再进 Stop 就成递归了
        var viewer = _viewer;
        var window = _window;
        var indicator = _indicator;
        _viewer = null;
        _window = null;
        _indicator = null;
        _velocity = default;

        Timer.Stop();

        if (viewer is not null)
        {
            viewer.Unloaded -= OnViewerUnloaded;
            if (viewer.IsMouseCaptured) viewer.ReleaseMouseCapture();
        }

        if (window is not null)
        {
            window.PreviewKeyDown -= OnWindowPreviewKeyDown;
            window.Deactivated -= OnWindowDeactivated;
        }

        indicator?.Detach();
    }

    private static bool CanScroll(ScrollViewer viewer) =>
        viewer.ScrollableHeight > 0.5 || viewer.ScrollableWidth > 0.5;

    /// <summary>
    /// 从鼠标下的元素往上找第一个"真能滚"的 ScrollViewer，判断它是不是本次事件的那个。
    /// 嵌套时只让最内层响应，外层不动。
    /// </summary>
    private static bool IsInnermostAt(ScrollViewer viewer, object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = ParentOf(node))
        {
            if (node is ScrollViewer candidate && CanScroll(candidate))
                return ReferenceEquals(candidate, viewer);
        }

        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual || node is System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    /// <summary>
    /// 按下点上的小圆环提示，滚远了会跟指示器一样留在原处。
    /// 拿不到 AdornerLayer 就不画，功能不受影响。
    /// </summary>
    private sealed class ScrollIndicator : Adorner
    {
        private static readonly Brush Body = Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        private static readonly Brush Dot = Frozen(Color.FromArgb(0xE6, 0x37, 0x41, 0x51));
        private static readonly Pen Ring = FrozenPen(Color.FromArgb(0x66, 0x0F, 0x17, 0x2A), 1.2);
        private static readonly Brush Arrow = Frozen(Color.FromArgb(0xB0, 0x6B, 0x72, 0x80));

        private readonly Point _center;

        private ScrollIndicator(UIElement adorned, Point center) : base(adorned)
        {
            _center = center;
            IsHitTestVisible = false;
        }

        public static ScrollIndicator? Attach(ScrollViewer viewer, Point center)
        {
            var layer = AdornerLayer.GetAdornerLayer(viewer);
            if (layer is null) return null;

            var indicator = new ScrollIndicator(viewer, center);
            layer.Add(indicator);
            return indicator;
        }

        public void Detach() => AdornerLayer.GetAdornerLayer(AdornedElement)?.Remove(this);

        protected override void OnRender(DrawingContext dc)
        {
            const double radius = 10;

            dc.DrawEllipse(Body, Ring, _center, radius, radius);
            dc.DrawEllipse(Dot, null, _center, 2.2, 2.2);

            // 上下两个小箭头：说明还能往哪边滚
            dc.DrawGeometry(Arrow, null, Triangle(_center.X, _center.Y - radius - 6, _center.Y - radius - 1.6, 4.2));
            dc.DrawGeometry(Arrow, null, Triangle(_center.X, _center.Y + radius + 6, _center.Y + radius + 1.6, 4.2));
        }

        private static Geometry Triangle(double cx, double tipY, double baseY, double half)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(cx, tipY), true, true);
                ctx.LineTo(new Point(cx - half, baseY), true, false);
                ctx.LineTo(new Point(cx + half, baseY), true, false);
            }

            geometry.Freeze();
            return geometry;
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen FrozenPen(Color color, double thickness)
        {
            var pen = new Pen(Frozen(color), thickness);
            pen.Freeze();
            return pen;
        }
    }
}