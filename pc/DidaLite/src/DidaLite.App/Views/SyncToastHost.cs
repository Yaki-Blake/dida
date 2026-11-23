using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DidaLite.App.ViewModels;

namespace DidaLite.App.Views;

/// <summary>
/// 右上角的同步浮层：贴在「同步 / 添加」两个按钮下面，一条一条往下排。
/// 绿底白字、圆角块，底边一条进度条走满 4 秒；走完自己向上淡出，
/// 下面的依次往上补位。单击任意一条可以立刻送走它。
/// </summary>
internal sealed class SyncToastHost
{
    /// <summary>卡片宽度：刚好盖住「同步 + 添加」那一组按钮。</summary>
    private const double CardWidth = 196;

    /// <summary>卡片之间的间距。</summary>
    private const double CardGap = 8;

    /// <summary>卡片圆角。</summary>
    private const double CardRadius = 12;

    /// <summary>底边进度条的厚度。</summary>
    private const double BarThickness = 4;

    /// <summary>一条浮层活多久。进度条与它是同一个时长。</summary>
    private static readonly TimeSpan Life = TimeSpan.FromSeconds(4);

    /// <summary>同时最多挂几条，再多就把最老的一条送走。</summary>
    private const int MaxCards = 4;

    private readonly Canvas _layer;
    private readonly List<Card> _cards = new();

    /// <summary>当前那条「正在同步…」：结果回来时原地变成数字，不另起一条。</summary>
    private Card? _working;

    public SyncToastHost(Canvas layer)
    {
        _layer = layer;
        _layer.Width = CardWidth;
        _layer.ClipToBounds = false;
    }

    /// <summary>视图模型每次同步都会推一条过来：先 Working，再 Success / Error。</summary>
    public void Post(SyncToastInfo info)
    {
        Card card;

        if (info.Kind == SyncToastKind.Working)
        {
            // 连点同步只留一条「正在同步」，不堆一列
            if (_working is { Leaving: false }) return;

            card = Create();
            _working = card;
            _cards.Insert(0, card);
            _layer.Children.Add(card.Root);
            FadeIn(card);
        }
        else if (_working is { Leaving: false })
        {
            // 结果回来：把那条「正在同步」原地换成数字
            card = _working;
            _working = null;
        }
        else
        {
            card = Create();
            _cards.Insert(0, card);
            _layer.Children.Add(card.Root);
            FadeIn(card);
        }

        Fill(card, info);
        if (info.Kind != SyncToastKind.Working) StartLife(card);

        Relayout();

        // 堆太高就送走最老的那条，画面不会一路铺到屏幕中间
        while (_cards.Count > MaxCards) Dismiss(_cards[^1]);
    }

    // ---------------- 卡片 ----------------

    private sealed class Card
    {
        public required Border Root { get; init; }
        public required Grid Stats { get; init; }
        public required TextBlock Message { get; init; }
        public required TextBlock Extra { get; init; }
        public required TextBlock UploadValue { get; init; }
        public required TextBlock DownloadValue { get; init; }
        public required Border BarSlot { get; init; }
        public required RowDefinition BarRow { get; init; }
        public required ScaleTransform BarScale { get; init; }

        /// <summary>堆叠位置：第几条就往下让几个卡位。</summary>
        public required TranslateTransform Offset { get; init; }

        /// <summary>退出时向上滑的那一小段，和堆叠位移分开，互不打断。</summary>
        public required TranslateTransform Slide { get; init; }

        /// <summary>入场时从下方升起的一小段，独立于堆叠与退出。</summary>
        public required TranslateTransform Rise { get; init; }

        /// <summary>数字滚动的计时器，复用时先停掉上一条。</summary>
        public DispatcherTimer? CountTimer { get; set; }
        public DispatcherTimer? Timer { get; set; }
        public bool Leaving { get; set; }
    }

    private Card Create()
    {
        var stats = new Grid();
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBlock Label(string text) => new()
        {
            Text = text,
            FontSize = 12,
            Opacity = 0.82,
            VerticalAlignment = VerticalAlignment.Center,
        };

        TextBlock Value() => new()
        {
            Text = "0 条",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var uploadLabel = Label("上传");
        var uploadValue = Value();
        var downloadLabel = Label("下载");
        downloadLabel.Margin = new Thickness(0, 2, 0, 0);
        var downloadValue = Value();
        downloadValue.Margin = new Thickness(0, 2, 0, 0);

        Grid.SetRow(uploadLabel, 0);
        Grid.SetColumn(uploadLabel, 0);
        Grid.SetRow(uploadValue, 0);
        Grid.SetColumn(uploadValue, 1);
        Grid.SetRow(downloadLabel, 1);
        Grid.SetColumn(downloadLabel, 0);
        Grid.SetRow(downloadValue, 1);
        Grid.SetColumn(downloadValue, 1);
        stats.Children.Add(uploadLabel);
        stats.Children.Add(uploadValue);
        stats.Children.Add(downloadLabel);
        stats.Children.Add(downloadValue);

        var message = new TextBlock
        {
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var extra = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.82,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var content = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        content.Children.Add(stats);
        content.Children.Add(message);
        content.Children.Add(extra);

        // 底边进度条：底色一层、走时从右往左退
        var barScale = new ScaleTransform(1, 1);
        var fill = new Border
        {
            Background = new SolidColorBrush(Colors.White) { Opacity = 1.0 },
            CornerRadius = new CornerRadius(0, 0, CardRadius, CardRadius),
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = barScale,
        };
        var barSlot = new Border
        {
            Background = new SolidColorBrush(Colors.White) { Opacity = 0.3 },
            CornerRadius = new CornerRadius(0, 0, CardRadius, CardRadius),
            Child = fill,
            Visibility = Visibility.Collapsed,
        };

        var barRow = new RowDefinition { Height = new GridLength(0) };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(barRow);
        Grid.SetRow(content, 0);
        Grid.SetRow(barSlot, 1);
        layout.Children.Add(content);
        layout.Children.Add(barSlot);

        var offset = new TranslateTransform();
        var slide = new TranslateTransform();
        var rise = new TranslateTransform();

        var root = new Border
        {
            Width = CardWidth,
            CornerRadius = new CornerRadius(CardRadius),
            Child = layout,
            Cursor = Cursors.Hand,
            ToolTip = "单击关闭",
            RenderTransform = new TransformGroup { Children = { offset, slide, rise } },
        };
        root.SetResourceReference(Border.BackgroundProperty, "accent");
        if (Application.Current?.TryFindResource("OverlayShadow") is System.Windows.Media.Effects.Effect shadow)
            root.Effect = shadow;

        var card = new Card
        {
            Root = root,
            Stats = stats,
            Message = message,
            Extra = extra,
            UploadValue = uploadValue,
            DownloadValue = downloadValue,
            BarSlot = barSlot,
            BarRow = barRow,
            BarScale = barScale,
            Offset = offset,
            Slide = slide,
            Rise = rise,
        };

        // 绿底白字：文字统一用 on-accent，切主题时跟着走
        foreach (var text in new[] { uploadLabel, uploadValue, downloadLabel, downloadValue, message, extra })
            text.SetResourceReference(TextBlock.ForegroundProperty, "on-accent");

        // 单击即收：顺手把事件吃掉，别让这一下再漏到下面的内容区
        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Dismiss(card);
        };
        return card;
    }

    /// <summary>把一条浮层的内容填进去：正在同步 / 数字 / 失败原因三种形态。</summary>
    private static void Fill(Card card, SyncToastInfo info)
    {
        card.Root.SetResourceReference(
            Border.BackgroundProperty, info.Kind == SyncToastKind.Error ? "danger" : "accent");

        if (info.Kind == SyncToastKind.Working)
        {
            card.Stats.Visibility = Visibility.Collapsed;
            card.Extra.Visibility = Visibility.Collapsed;
            card.Message.Visibility = Visibility.Visible;
            card.Message.Text = "正在同步…";
            card.BarSlot.Visibility = Visibility.Collapsed;
            card.BarRow.Height = new GridLength(0);
            return;
        }

        if (info.Kind == SyncToastKind.Success)
        {
            card.Message.Visibility = Visibility.Collapsed;
            card.Stats.Visibility = Visibility.Visible;
            // 数字从 0 滚到目标：与手机端一致，结果有「走上来」的过程而不是硬跳
            RollTo(card, card.UploadValue, info.Uploaded);
            RollTo(card, card.DownloadValue, info.Downloaded);
            card.Extra.Text = info.Extra ?? string.Empty;
            card.Extra.Visibility = string.IsNullOrEmpty(info.Extra) ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            card.Stats.Visibility = Visibility.Collapsed;
            card.Message.Visibility = Visibility.Visible;
            card.Message.Text = info.Message ?? "同步失败";
            card.Extra.Visibility = Visibility.Collapsed;
        }

        card.BarSlot.Visibility = Visibility.Visible;
        card.BarRow.Height = new GridLength(BarThickness);
    }

    // ---------------- 动画 ----------------

    private static void FadeIn(Card card)
    {
        card.Root.Opacity = 0;
        card.Root.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

        // 从下方 10px 轻轻升上来，和淡入同一拍；与堆叠/退出位移各用各的 transform
        card.Rise.Y = 10;
        card.Rise.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
        card.Rise.Y = 0;
    }

    /// <summary>
    /// 统计数字从 0 滚到目标值。用 16ms 的计时器配 easeOut 曲线，
    /// 约 420ms 走完；目标为 0 时直接落位，免得空滚一次。
    /// </summary>
    private static void RollTo(Card card, TextBlock target, int to)
    {
        card.CountTimer?.Stop();
        if (to <= 0)
        {
            target.Text = "0 条";
            return;
        }

        const int durationMs = 420;
        var started = Environment.TickCount64;
        DispatcherTimer? self = null;
        self = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        self.Tick += (_, _) =>
        {
            var elapsed = Environment.TickCount64 - started;
            var t = Math.Min(1.0, elapsed / (double)durationMs);
            // easeOutCubic：起步快、落点软
            var eased = 1 - Math.Pow(1 - t, 3);
            target.Text = $"{(int)Math.Round(to * eased)} 条";
            if (t >= 1.0)
            {
                target.Text = $"{to} 条";
                self.Stop();
                if (ReferenceEquals(card.CountTimer, self)) card.CountTimer = null;
            }
        };
        card.CountTimer = self;
        self.Start();
    }

    /// <summary>开始 4 秒倒计时：进度条由满退到空，时间到就自己走。</summary>
    private void StartLife(Card card)
    {
        card.BarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        card.BarScale.ScaleX = 1;
        card.BarScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0, Life) { FillBehavior = FillBehavior.Stop });

        card.Timer?.Stop();
        var timer = new DispatcherTimer { Interval = Life };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Dismiss(card);
        };
        card.Timer = timer;
        timer.Start();
    }

    /// <summary>向上滑一段 + 淡出，然后从画布上摘掉，下面的自动补位。</summary>
    private void Dismiss(Card card)
    {
        if (card.Leaving) return;
        card.Leaving = true;

        card.Timer?.Stop();
        card.Timer = null;
        card.CountTimer?.Stop();
        card.CountTimer = null;
        if (ReferenceEquals(_working, card)) _working = null;

        _cards.Remove(card);

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) => _layer.Children.Remove(card.Root);
        card.Root.BeginAnimation(UIElement.OpacityProperty, fade);

        card.Slide.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, -20, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd,
            });

        Relayout();
    }

    /// <summary>从上到下按实际高度依次落位，新增 / 退出后都调一次。</summary>
    private void Relayout()
    {
        _layer.UpdateLayout();

        var y = 0d;
        foreach (var card in _cards)
        {
            var height = card.Root.ActualHeight;
            if (height <= 0) height = card.Root.DesiredSize.Height;
            if (height <= 0) height = 42;

            MoveTo(card.Offset, y);
            y += height + CardGap;
        }

        _layer.Height = _cards.Count == 0 ? 0 : Math.Max(0, y - CardGap);
    }

    private static void MoveTo(TranslateTransform transform, double top)
    {
        if (!transform.HasAnimatedProperties && Math.Abs(transform.Y - top) < 0.5) return;

        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(top, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd,
            });
    }
}
