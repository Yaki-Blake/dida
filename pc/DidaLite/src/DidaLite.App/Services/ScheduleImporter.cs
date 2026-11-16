using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DidaLite.App.Services;

/// <summary>一次导入的结果，界面按钮与 HTTP 接口共用。</summary>
public sealed record ImportResult(bool Ok, int Events, int Courses, string Source, string Message);

/// <summary>
/// 课表导入：把浏览器书签导出的 .ics，或课表快照 .json，解析成 ScheduleStore 的内存数据，
/// 落盘到 exe 旁的 Assets，然后就地刷新，界面和 5080 接口立刻生效，不必重启。
/// 只处理本机文件，不联网、不碰任务库、不读取任何凭据。
/// </summary>
public static class ScheduleImporter
{
    private static readonly TimeSpan UtcOffset = TimeSpan.FromHours(8);

    /// <summary>佛山校区连排作息：上课时刻 → 节次段，与生成 ICS 时的 PERIODS 一致。</summary>
    private static readonly (TimeSpan Start, int Slot, string Period)[] Periods =
    [
        (new TimeSpan(8, 30, 0), 1, "1-2"),
        (new TimeSpan(10, 20, 0), 2, "3-4"),
        (new TimeSpan(14, 0, 0), 3, "5-6"),
        (new TimeSpan(15, 50, 0), 4, "7-8"),
        (new TimeSpan(18, 30, 0), 5, "9-10"),
        (new TimeSpan(20, 20, 0), 6, "11-12"),
    ];

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>从本地文件导入。扩展名决定解析方式；.js 快照包装也能直接吃。</summary>
    public static ImportResult ImportFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(false, 0, 0, string.Empty, "文件不存在：" + path);
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.UTF8.GetString(bytes);
            return ImportText(text, bytes, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            return new(false, 0, 0, string.Empty, "读取失败：" + ex.Message);
        }
    }

    /// <summary>从文本导入（HTTP 接口走这里）。raw 为原始字节，导入 .ics 时原样落盘。</summary>
    public static ImportResult ImportText(string text, byte[]? raw, string fileName)
    {
        try
        {
            var template = Snapshot();
            var clean = text.TrimStart('\uFEFF');
            var trimmed = clean.TrimStart();
            ScheduleFile data;
            byte[] ics;

            if (trimmed.StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            {
                data = ParseIcs(clean, template, fileName);
                ics = raw is { Length: > 0 } ? StripBom(raw) : Utf8NoBom.GetBytes(clean);
            }
            else
            {
                data = ParseJson(clean, template, fileName);
                if (data.Events.Count == 0)
                    return new(false, 0, 0, fileName, "没解析到课程事件；请选择 .ics 或含 events 的课表 .json");
                ics = BuildIcs(data);
            }

            if (data.Events.Count == 0)
                return new(false, 0, 0, fileName, "文件里没有 VEVENT；请确认选的是课表 .ics");

            Persist(data, ics);
            var courses = data.Events.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count();
            return new(true, data.Events.Count, courses, data.Source,
                $"导入成功：{data.Events.Count} 节课 · {courses} 门课");
        }
        catch (Exception ex)
        {
            return new(false, 0, 0, fileName, "解析失败：" + ex.Message);
        }
    }

    /// <summary>清空课表，回到「尚未导入」的空状态（可反复体验导入流程）。</summary>
    public static ImportResult Clear()
    {
        try
        {
            var data = Snapshot();
            data.Source = "尚未导入课表数据";
            data.Total = 0;
            data.Count = 0;
            data.Events = [];
            Persist(data, EmptyCalendar());
            return new(true, 0, 0, data.Source, "课表已清空");
        }
        catch (Exception ex)
        {
            return new(false, 0, 0, string.Empty, "清空失败：" + ex.Message);
        }
    }

    /// <summary>保留学期与校历参数，只换事件数据。</summary>
    private static ScheduleFile Snapshot()
    {
        var cur = ScheduleStore.Instance.Data;
        return new ScheduleFile
        {
            Term = cur.Term,
            Week1Monday = cur.Week1Monday,
            Timezone = cur.Timezone,
        };
    }

    private static ScheduleFile ParseJson(string text, ScheduleFile template, string fileName)
    {
        // 兼容 schedule_data.js 的 window.__SCHEDULE_DATA__ = {...}; 包装
        var body = text;
        var marker = body.IndexOf("__SCHEDULE_DATA__", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var brace = body.IndexOf('{', marker);
            var end = body.LastIndexOf('}');
            if (brace >= 0 && end > brace) body = body[brace..(end + 1)];
        }

        var parsed = JsonSerializer.Deserialize<ScheduleFile>(body, ScheduleStore.Json)
                     ?? throw new InvalidDataException("JSON 结构不是课表数据");
        parsed.Events ??= [];
        if (string.IsNullOrWhiteSpace(parsed.Term)) parsed.Term = template.Term;
        if (string.IsNullOrWhiteSpace(parsed.Week1Monday)) parsed.Week1Monday = template.Week1Monday;
        if (string.IsNullOrWhiteSpace(parsed.Timezone)) parsed.Timezone = template.Timezone;
        parsed.Source = fileName;
        parsed.Total = parsed.Events.Count;
        parsed.Count = parsed.Events.Count;
        return parsed;
    }

    private static ScheduleFile ParseIcs(string text, ScheduleFile template, string fileName)
    {
        var week1 = DateOnly.TryParseExact(template.Week1Monday, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var w1)
            ? w1
            : new DateOnly(2026, 9, 7);

        var events = new List<ScheduleEvent>();
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inEvent = false;
        var inAlarm = false;

        foreach (var line in Unfold(text))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                inEvent = true; inAlarm = false; props.Clear(); continue;
            }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (inEvent && TryBuild(props, template, week1, out var ev)) events.Add(ev);
                inEvent = false; inAlarm = false; props.Clear(); continue;
            }
            if (line.Equals("BEGIN:VALARM", StringComparison.OrdinalIgnoreCase)) { inAlarm = true; continue; }
            if (line.Equals("END:VALARM", StringComparison.OrdinalIgnoreCase)) { inAlarm = false; continue; }
            if (!inEvent || inAlarm) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon];
            var semi = key.IndexOf(';');
            if (semi > 0) key = key[..semi];
            props[key] = Unescape(line[(colon + 1)..]);
        }

        events = events
            .OrderBy(e => e.Date, StringComparer.Ordinal)
            .ThenBy(e => e.Start, StringComparer.Ordinal)
            .ThenBy(e => e.Slot)
            .ThenBy(e => e.Name, StringComparer.CurrentCulture)
            .ToList();

        return new ScheduleFile
        {
            Term = template.Term,
            Week1Monday = template.Week1Monday,
            Timezone = template.Timezone,
            Source = fileName,
            Total = events.Count,
            Count = events.Count,
            Events = events,
        };
    }

    private static bool TryBuild(Dictionary<string, string> p, ScheduleFile template,
        DateOnly week1, out ScheduleEvent ev)
    {
        ev = new ScheduleEvent();
        if (!p.TryGetValue("DTSTART", out var startUtc) || !p.TryGetValue("DTEND", out var endUtc))
            return false;
        if (!TryParseUtc(startUtc, out var startOffset) || !TryParseUtc(endUtc, out var endOffset))
            return false;

        var local = startOffset.ToOffset(UtcOffset);
        var localEnd = endOffset.ToOffset(UtcOffset);
        var date = DateOnly.FromDateTime(local.DateTime);

        var (slot, period) = MatchPeriod(local.TimeOfDay);

        // DESCRIPTION: 第N教学周 · 周次 2-16(周) · 教师
        var weeksExpr = string.Empty;
        var teacher = string.Empty;
        if (p.TryGetValue("DESCRIPTION", out var desc))
        {
            var parts = Regex.Split(desc, @"\s*·\s*");
            if (parts.Length >= 2) weeksExpr = Regex.Replace(parts[1], @"^周次\s*", string.Empty).Trim();
            if (parts.Length >= 3) teacher = parts[2].Trim();
        }

        var week = (int)Math.Floor((date.DayNumber - week1.DayNumber) / 7.0) + 1;

        ev = new ScheduleEvent
        {
            Uid = p.GetValueOrDefault("UID") ?? string.Empty,
            Term = template.Term,
            Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Week = week,
            Weekday = ((int)local.DayOfWeek + 6) % 7 + 1,
            Start = local.ToString("HH:mm", CultureInfo.InvariantCulture),
            End = localEnd.ToString("HH:mm", CultureInfo.InvariantCulture),
            StartUTC = startUtc,
            EndUTC = endUtc,
            Name = p.GetValueOrDefault("SUMMARY") ?? string.Empty,
            Room = p.GetValueOrDefault("LOCATION") ?? string.Empty,
            Teacher = teacher,
            WeeksExpr = weeksExpr,
            Period = period,
            Slot = slot,
        };
        return true;
    }

    private static bool TryParseUtc(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);

    private static (int Slot, string Period) MatchPeriod(TimeSpan time)
    {
        var best = Periods[0];
        var delta = TimeSpan.MaxValue;
        foreach (var item in Periods)
        {
            var d = (time - item.Start).Duration();
            if (d < delta) { delta = d; best = item; }
        }
        return (best.Slot, best.Period);
    }

    /// <summary>RFC 5545 行折叠还原：CRLF 后接空格/制表符的是上一行的续行。</summary>
    private static List<string> Unfold(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            if ((line[0] == ' ' || line[0] == '\t') && result.Count > 0)
                result[^1] += line[1..];
            else
                result.Add(line);
        }
        return result;
    }

    private static byte[] StripBom(byte[] raw) =>
        raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF
            ? raw[3..]
            : raw;

    private static string Unescape(string value) => value
        .Replace("\\n", "\n", StringComparison.Ordinal)
        .Replace("\\N", "\n", StringComparison.Ordinal)
        .Replace("\\,", ",", StringComparison.Ordinal)
        .Replace("\\;", ";", StringComparison.Ordinal)
        .Replace("\\\\", "\\", StringComparison.Ordinal);

    /// <summary>导入 .json 时生成等价的日历文件，保证 /schedule.ics 仍可订阅。</summary>
    private static byte[] BuildIcs(ScheduleFile data)
    {
        var sb = new StringBuilder();
        void Line(string s)
        {
            foreach (var part in Fold(s)) sb.Append(part).Append("\r\n");
        }

        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//GDUFE Schedule//CN");
        Line("CALSCALE:GREGORIAN");
        Line("METHOD:PUBLISH");
        Line("X-WR-CALNAME:课表");
        Line("X-WR-TIMEZONE:Asia/Shanghai");
        foreach (var e in data.Events)
        {
            Line("BEGIN:VEVENT");
            Line("UID:" + e.Uid);
            Line("DTSTAMP:20260906T160000Z");
            Line("DTSTART:" + e.StartUTC);
            Line("DTEND:" + e.EndUTC);
            Line("SUMMARY:" + Escape(e.Name));
            Line("LOCATION:" + Escape(e.Room));
            Line($"DESCRIPTION:第{e.Week}教学周 · 周次 {e.WeeksExpr} · {Escape(e.Teacher)}");
            Line("BEGIN:VALARM");
            Line("ACTION:DISPLAY");
            Line("TRIGGER:-PT15M");
            Line("DESCRIPTION:" + Escape(e.Name) + " 即将开始");
            Line("END:VALARM");
            Line("END:VEVENT");
        }
        Line("END:VCALENDAR");
        return Utf8NoBom.GetBytes(sb.ToString());
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>按 UTF-8 字节折叠到 75 字节以内，不切断中文字符；续行以单个空格开头。</summary>
    private static List<string> Fold(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var bytes = 0;
        var limit = 75;
        foreach (var ch in line)
        {
            var n = Utf8NoBom.GetByteCount(ch.ToString());
            if (bytes + n > limit)
            {
                result.Add(current.ToString());
                current.Clear();
                bytes = 1;                 // 续行开头的空格也占 1 字节
                limit = 74;
            }
            current.Append(ch);
            bytes += n;
        }
        result.Add(current.ToString());
        for (var i = 1; i < result.Count; i++) result[i] = " " + result[i];
        return result;
    }

    private static byte[] EmptyCalendar() => Utf8NoBom.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//GDUFE Schedule//CN\r\n" +
        "CALSCALE:GREGORIAN\r\nMETHOD:PUBLISH\r\nX-WR-CALNAME:课表\r\n" +
        "X-WR-TIMEZONE:Asia/Shanghai\r\nEND:VCALENDAR\r\n");

    private static void Persist(ScheduleFile data, byte[] ics)
    {
        // 与 ScheduleStore.Pick 保持一致：exe 同目录有同名文件就写那里，否则写 Assets
        var baseDir = AppContext.BaseDirectory;
        var direct = Path.Combine(baseDir, "schedule.json");
        var targetDir = File.Exists(direct) ? baseDir : Path.Combine(baseDir, "Assets");
        Directory.CreateDirectory(targetDir);

        File.WriteAllText(Path.Combine(targetDir, "schedule.json"),
            JsonSerializer.Serialize(data, ScheduleStore.Json), Utf8NoBom);
        File.WriteAllBytes(Path.Combine(targetDir, "schedule.ics"), ics);

        ScheduleStore.Instance.Reload();
    }
}
