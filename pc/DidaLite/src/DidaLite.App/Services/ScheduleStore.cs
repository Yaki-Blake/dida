using System.Globalization;
using System.IO;
using System.Text;
using DidaLite.Core;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DidaLite.App.Services;

/// <summary>
/// 一条课表事件。字段名与独立版 schedule_server.py 的 /api/schedule 契约逐字对齐，
/// 浏览器页面、日历订阅和外部调用方切到内嵌 5080 时不用改一行。
/// </summary>
public sealed class ScheduleEvent
{
    [JsonPropertyName("uid")] public string Uid { get; set; } = string.Empty;
    [JsonPropertyName("term")] public string Term { get; set; } = string.Empty;
    [JsonPropertyName("date")] public string Date { get; set; } = string.Empty;
    [JsonPropertyName("week")] public int Week { get; set; }
    [JsonPropertyName("weekday")] public int Weekday { get; set; }
    [JsonPropertyName("start")] public string Start { get; set; } = string.Empty;
    [JsonPropertyName("end")] public string End { get; set; } = string.Empty;
    [JsonPropertyName("startUTC")] public string StartUTC { get; set; } = string.Empty;
    [JsonPropertyName("endUTC")] public string EndUTC { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("room")] public string Room { get; set; } = string.Empty;
    [JsonPropertyName("teacher")] public string Teacher { get; set; } = string.Empty;
    [JsonPropertyName("weeksExpr")] public string WeeksExpr { get; set; } = string.Empty;
    [JsonPropertyName("period")] public string Period { get; set; } = string.Empty;
    [JsonPropertyName("slot")] public int Slot { get; set; }

    /// <summary>这门课是否写了长期备注；仅界面用，不进入 5080 契约与 ICS。</summary>
    [JsonIgnore] public bool HasNote { get; set; }

    /// <summary>这一节课是否写了单次备注（只对这一天的这一次有效）。</summary>
    [JsonIgnore] public bool HasOnceNote { get; set; }
}

/// <summary>随程序附带的课表快照文件（schedule.json）。</summary>
public sealed class ScheduleFile
{
    [JsonPropertyName("term")] public string Term { get; set; } = "2026-2027-1";
    [JsonPropertyName("week1Monday")] public string Week1Monday { get; set; } = "2026-09-07";
    [JsonPropertyName("timezone")] public string Timezone { get; set; } = "Asia/Shanghai (UTC+8)";
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("events")] public List<ScheduleEvent> Events { get; set; } = [];
}

/// <summary>
/// 课表数据的唯一出处：WPF 课表页和内嵌 HTTP 接口共用同一份内存数据。
/// 数据文件默认在 exe 旁的 Assets 目录；直接把同名文件丢在 exe 同目录即可覆盖，
/// 调课后不必重新编译。
/// </summary>
public sealed class ScheduleStore
{
    /// <summary>中文不转义、null 不输出，与 Python 版 json.dumps(ensure_ascii=False) 一致。</summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static ScheduleStore Instance { get; } = Load();

    /// <summary>导入 / 清空后触发；界面据此刷新，不必重启软件。</summary>
    public static event Action? DataChanged;

    public ScheduleFile Data { get; private set; }
    public IReadOnlyList<ScheduleEvent> Events => Data.Events;
    public DateOnly Week1 { get; private set; }
    public byte[] IcsBytes { get; private set; }
    public string PageHtml { get; private set; }

    private ScheduleStore(ScheduleFile data, DateOnly week1, byte[] ics, string pageHtml)
    {
        Data = data;
        Week1 = week1;
        IcsBytes = ics;
        PageHtml = pageHtml;
    }

    /// <summary>
    /// 重新读盘并就地替换字段（保持单例引用不变，已捕获 store 的接口 handler 无需重绑），
    /// 再通知界面刷新。由 ScheduleImporter 在写入文件后调用。
    /// </summary>
    public void Reload()
    {
        var fresh = Load();
        Data = fresh.Data;
        Week1 = fresh.Week1;
        IcsBytes = fresh.IcsBytes;
        PageHtml = fresh.PageHtml;
        DataChanged?.Invoke();
    }

    private static ScheduleStore Load()
    {
        var data = new ScheduleFile();

        if (Pick("schedule.json") is { } jsonPath)
        {
            try
            {
                data = JsonSerializer.Deserialize<ScheduleFile>(
                    File.ReadAllText(jsonPath, Encoding.UTF8), Json) ?? data;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"课表数据读取失败：{ex.Message}");
                data = new ScheduleFile();
            }
        }
        else
        {
            data.Source = "未找到 schedule.json";
        }

        data.Events ??= [];
        if (data.Total == 0) data.Total = data.Events.Count;
        if (data.Count == 0) data.Count = data.Events.Count;

        if (!DateOnly.TryParseExact(data.Week1Monday, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var week1))
        {
            week1 = new DateOnly(2026, 9, 7);
        }

        var ics = Pick("schedule.ics") is { } icsPath
            ? File.ReadAllBytes(icsPath)
            : [];

        var html = Pick("schedule_page.html") is { } htmlPath
            ? File.ReadAllText(htmlPath, Encoding.UTF8)
            : "<!doctype html><meta charset='utf-8'><title>课表</title><p>未找到 schedule_page.html</p>";

        // 接口服务打开的页面把 211 条事件内联进去，断网 / 静态预览也能显示
        if (html.Contains("/*__SCHEDULE_SNAPSHOT__*/", StringComparison.Ordinal))
        {
            var inline = JsonSerializer.Serialize(data, Json)
                .Replace("</", "<\\/", StringComparison.Ordinal);
            html = html.Replace("/*__SCHEDULE_SNAPSHOT__*/",
                "window.__SCHEDULE_DATA__ = " + inline + ";", StringComparison.Ordinal);
        }

        // 备注独立于课表文件，导入 / 清空后要重新把「已备注」标记贴回新事件对象
        ScheduleNotes.ApplyTo(data.Events);

        return new ScheduleStore(data, week1, ics, html);
    }

    /// <summary>exe 同目录优先，其次是随包附带的 Assets 目录。</summary>
    /// <summary>
    /// 读取课表相关文件：先看用户数据目录（导入后的结果），
    /// 再回落到 exe 同目录与 Assets（随包附带，或旧版本遗留）。
    /// </summary>
    private static string? Pick(string name) => DataPaths.FindReadable(name);

    /// <summary>今天的本地日期，格式 yyyy-MM-dd。</summary>
    public string Today => DateOnly.FromDateTime(DateTime.Now)
        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>今天落在第几教学周；学期开始前会是 0 或负数，由调用方夹取。</summary>
    public int CurrentWeek => (int)Math.Floor(
        (DateOnly.FromDateTime(DateTime.Now).DayNumber - Week1.DayNumber) / 7.0) + 1;

    /// <summary>按 date / from / to / week / weekday 过滤，输出按日期与时刻排好序。</summary>
    public List<ScheduleEvent> Query(string? date, string? from, string? to, int? week, int? weekday)
    {
        IEnumerable<ScheduleEvent> q = Events;

        if (!string.IsNullOrWhiteSpace(date))
            q = q.Where(e => string.Equals(e.Date, date, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(from))
        {
            var lower = from!;
            q = q.Where(e => string.CompareOrdinal(e.Date, lower) >= 0);
        }
        if (!string.IsNullOrWhiteSpace(to))
        {
            var upper = to!;
            q = q.Where(e => string.CompareOrdinal(e.Date, upper) <= 0);
        }
        if (week is int w) q = q.Where(e => e.Week == w);
        if (weekday is int wd) q = q.Where(e => e.Weekday == wd);

        return q.OrderBy(e => e.Date, StringComparer.Ordinal)
            .ThenBy(e => e.Start, StringComparer.Ordinal)
            .ThenBy(e => e.Slot)
            .ThenBy(e => e.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>与独立版 payload() 完全同构的返回体。</summary>
    public Dictionary<string, object?> Payload(IEnumerable<ScheduleEvent> events)
    {
        var list = events as IReadOnlyList<ScheduleEvent> ?? events.ToList();
        return new Dictionary<string, object?>
        {
            ["term"] = Data.Term,
            ["week1Monday"] = Data.Week1Monday,
            ["timezone"] = Data.Timezone,
            ["source"] = Data.Source,
            ["total"] = Events.Count,
            ["count"] = list.Count,
            ["generatedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["events"] = list,
        };
    }

    /// <summary>课程汇总：名称 / 事件数 / 教师 / 去重的教室列表。</summary>
    public object Courses()
    {
        var courses = Events
            .GroupBy(e => e.Name, StringComparer.CurrentCulture)
            // 与独立版一致：只按事件数降序（稳定排序保留首次出现顺序），
            // 教室按 Unicode 码点排序（对应 Python 的 sorted(set(...))）
            .OrderByDescending(g => g.Count())
            .Select(g => new
            {
                name = g.Key,
                count = g.Count(),
                teacher = g.First().Teacher,
                rooms = g.Select(e => e.Room).Distinct(StringComparer.Ordinal)
                    .OrderBy(r => r, StringComparer.Ordinal).ToArray(),
            })
            .ToArray();

        return new { count = courses.Length, courses };
    }
}
