using CommunityToolkit.Mvvm.ComponentModel;
using DidaLite.Core.Models;

namespace DidaLite.App.ViewModels;

/// <summary>列表行的视图模型，包装 TaskItem 并暴露展示用属性。</summary>
public sealed class TaskViewModel : ObservableObject
{
    private readonly bool _weekMode;

    public TaskViewModel(TaskItem model, bool weekMode = false)
    {
        Model = model;
        _weekMode = weekMode;
    }

    public TaskItem Model { get; }

    public string Id => Model.Id;
    public string Title => Model.Title;
    public string? Note => Model.Note;
    public string? Location => Model.Location;
    public bool HasLocation => !string.IsNullOrWhiteSpace(Model.Location);
    public bool HasNote => !string.IsNullOrWhiteSpace(Model.Note);
    public bool Done => Model.Done;
    public bool AllDay => Model.AllDay;
    public bool HasRepeat => RepeatRules.IsRepeating(Model.RepeatRule);
    public string RepeatText => RepeatRules.DisplayName(Model.RepeatRule);

    /// <summary>行首的开始时间：全天事件显示「全天」，没有开始时间留空。</summary>
    public string StartText
    {
        get
        {
            if (Model.AllDay) return "全天";
            if (Model.StartAt is not long ms) return string.Empty;

            return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm");
        }
    }

    /// <summary>右对齐时间列文案：全天只显示日期，其他显示时刻。</summary>
    public string DueText
    {
        get
        {
            if (Model.AnchorAt is not long ms) return string.Empty;

            var local = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
            var today = DateTime.Today;

            if (Model.AllDay)
            {
                if (local.Date == today) return "全天";
                if (local.Date == today.AddDays(1)) return "明天";
                return local.ToString("M月d日");
            }

            // 周视图里每行已经归属到具体某天，只显示时刻
            if (_weekMode) return local.ToString("HH:mm");

            if (local.Date == today) return local.ToString("HH:mm");
            if (local.Date == today.AddDays(1)) return "明天 " + local.ToString("HH:mm");
            return local.ToString("M月d日");
        }
    }

    /// <summary>逾期/即将到期用不同颜色，返回资源键。</summary>
    public string DueBrushKey
    {
        get
        {
            if (Model.Done) return "text-tertiary";

            var at = Model.DueAt ?? Model.StartAt;
            if (at is not long ms) return "text-tertiary";

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 全天事件只看日期是否过期，不因为当天 00:00 已过就标红
            if (Model.AllDay)
            {
                var todayStart = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
                return ms < todayStart ? "danger" : "text-secondary";
            }

            if (ms < now) return "danger";
            if (ms - now <= 24 * 60 * 60 * 1000) return "warning";
            return "text-secondary";
        }
    }

    public DateTime? StartLocal =>
        Model.StartAt is long start
            ? DateTimeOffset.FromUnixTimeMilliseconds(start).ToLocalTime().DateTime
            : null;

    public DateTime? DueLocal =>
        Model.DueAt is long due
            ? DateTimeOffset.FromUnixTimeMilliseconds(due).ToLocalTime().DateTime
            : null;

    /// <summary>模型被外部修改后刷新全部绑定。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(HasLocation));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(Done));
        OnPropertyChanged(nameof(AllDay));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(DueText));
        OnPropertyChanged(nameof(DueBrushKey));
        OnPropertyChanged(nameof(StartLocal));
        OnPropertyChanged(nameof(DueLocal));
        OnPropertyChanged(nameof(RepeatText));
        OnPropertyChanged(nameof(HasRepeat));
    }
}
