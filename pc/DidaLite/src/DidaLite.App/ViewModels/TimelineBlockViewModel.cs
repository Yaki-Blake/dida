namespace DidaLite.App.ViewModels;

/// <summary>日视图时间轴上的一个日程块：按开始时刻定位，块高即时长。</summary>
public sealed class TimelineBlockViewModel
{
    public TimelineBlockViewModel(TaskViewModel task, double top, double height, int lane, int laneCount)
    {
        Task = task;
        Top = top;
        Height = height;
        Lane = lane;
        LaneCount = Math.Max(1, laneCount);
    }

    public TaskViewModel Task { get; }

    /// <summary>相对时间轴顶部的偏移（像素）。</summary>
    public double Top { get; }

    public double Height { get; }

    /// <summary>同一簇里并排的第几列，从 0 开始。</summary>
    public int Lane { get; }

    public int LaneCount { get; }

    /// <summary>块足够高时才在标题下面补一行时间 / 地点，矮块只留标题。</summary>
    public bool ShowMeta => Height >= 40;

    /// <summary>块里的时间文案：09:00 – 10:30。</summary>
    public string TimeText
    {
        get
        {
            var start = Task.StartLocal;
            if (start is null) return string.Empty;

            var end = Task.DueLocal;
            return end is null || end <= start
                ? start.Value.ToString("HH:mm")
                : $"{start.Value:HH:mm} – {end.Value:HH:mm}";
        }
    }

    /// <summary>块内第二行：时间段，填了地点再跟在后面。</summary>
    public string MetaText
    {
        get
        {
            var time = TimeText;
            var location = Task.Location;

            if (string.IsNullOrWhiteSpace(location)) return time;
            return time.Length == 0 ? location : $"{time} · {location}";
        }
    }
}
