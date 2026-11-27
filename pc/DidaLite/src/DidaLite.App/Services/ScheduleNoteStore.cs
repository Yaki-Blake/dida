using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DidaLite.Core;

namespace DidaLite.App.Services;

/// <summary>
/// 课表备注的存储核心：两层内容、一份文件、线程安全。
/// <para>
/// 路径由构造参数给出而不是写死 <c>DataPaths</c>，测试才能指向隔离目录，
/// 不去碰用户真实的 schedule_notes.json。生产代码走 <see cref="ScheduleNotes"/> 那个静态门面。
/// </para>
/// <para>
/// <b>长期备注</b>按课程名存：同一门课每次上课看到同一份。
/// <b>单次备注</b>按「日期 + 节次 + 课程名」存：只对某一天的某一次课生效。
/// 单次键刻意不含 uid——uid 里编了教室与周次，换教室或重新同步就会变；也不含时刻，
/// 调课改时间不算换课。
/// </para>
/// </summary>
public sealed class ScheduleNoteStore
{
    private sealed class NoteFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 2;

        /// <summary>长期备注：课程名 → 正文。字段名与旧版一致，老文件可直接读。</summary>
        [JsonPropertyName("notes")] public Dictionary<string, string> Notes { get; set; } =
            new(StringComparer.Ordinal);

        /// <summary>单次备注：「日期|节次|课程名」→ 正文。旧文件没有这个字段，读出来是空表。</summary>
        [JsonPropertyName("once")] public Dictionary<string, string> Once { get; set; } =
            new(StringComparer.Ordinal);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, string> _long = new(StringComparer.Ordinal);
    private Dictionary<string, string> _once = new(StringComparer.Ordinal);

    /// <summary>
    /// 指向指定文件。文件不存在或读坏时保持空表并记一条调试日志——备注丢了不该拦住程序启动。
    /// </summary>
    public ScheduleNoteStore(string path)
    {
        _path = path;
        (_long, _once) = Read();
    }

    public string Path => _path;

    public int LongCount { get { lock (_gate) return _long.Count; } }

    public int OnceCount { get { lock (_gate) return _once.Count; } }

    private (Dictionary<string, string> Long, Dictionary<string, string> Once) Read()
    {
        var empty = (
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

        try
        {
            if (!File.Exists(_path)) return empty;

            var file = JsonSerializer.Deserialize<NoteFile>(
                File.ReadAllText(_path, Encoding.UTF8), JsonOptions);
            if (file is null) return empty;

            // 旧文件只有 notes（长期备注），Once 缺失时就是空表——无需迁移动作
            return (
                file.Notes is { } n ? new Dictionary<string, string>(n, StringComparer.Ordinal) : empty.Item1,
                file.Once is { } o ? new Dictionary<string, string>(o, StringComparer.Ordinal) : empty.Item2);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"课表备注读取失败：{ex.Message}");
            return empty;
        }
    }

    // ---------------- 长期备注（按课程名） ----------------

    public string GetLong(string course)
    {
        if (string.IsNullOrWhiteSpace(course)) return string.Empty;
        lock (_gate) return _long.TryGetValue(course.Trim(), out var text) ? text : string.Empty;
    }

    public bool HasLong(string course) => !string.IsNullOrWhiteSpace(GetLong(course));

    /// <summary>写长期备注；空文本视为删除这一条。返回是否落盘成功。</summary>
    public bool SetLong(string course, string? text)
    {
        course = (course ?? string.Empty).Trim();
        if (course.Length == 0) return false;

        lock (_gate)
        {
            var next = new Dictionary<string, string>(_long, StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(text)) next.Remove(course);
            else next[course] = text!;

            if (!Write(next, _once)) return false;
            _long = next;
            return true;
        }
    }

    public bool RemoveLong(string course) => SetLong(course, null);

    // ---------------- 单次备注（按某一天的一节课） ----------------

    /// <summary>
    /// 单次备注的键：日期 + 节次 + 课程名。
    /// 不含 uid（uid 里编了教室与周次，换教室会变），也不含时刻（调课改时间不算换课）。
    /// </summary>
    public static string OnceKey(string date, int slot, string course) =>
        $"{date?.Trim()}|{slot}|{(course ?? string.Empty).Trim()}";

    /// <summary>单次备注的键，直接从事件取。</summary>
    public static string OnceKey(ScheduleEvent e) =>
        OnceKey(e.Date, Math.Clamp(e.Slot, 1, 12), e.Name);

    public string GetOnce(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        lock (_gate) return _once.TryGetValue(key, out var text) ? text : string.Empty;
    }

    public string GetOnce(ScheduleEvent e) => GetOnce(OnceKey(e));

    public bool HasOnce(ScheduleEvent e) => !string.IsNullOrWhiteSpace(GetOnce(e));

    /// <summary>写单次备注；空文本视为删除这一条。返回是否落盘成功。</summary>
    public bool SetOnce(string key, string? text)
    {
        key = (key ?? string.Empty).Trim();
        if (key.Length == 0) return false;

        lock (_gate)
        {
            var next = new Dictionary<string, string>(_once, StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(text)) next.Remove(key);
            else next[key] = text!;

            if (!Write(_long, next)) return false;
            _once = next;
            return true;
        }
    }

    public bool RemoveOnce(string key) => SetOnce(key, null);

    // ---------------- 落盘与标记 ----------------

    /// <summary>写入磁盘。调用方必须已持有锁。</summary>
    private bool Write(Dictionary<string, string> longNotes, Dictionary<string, string> onceNotes)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(_path, JsonSerializer.Serialize(new NoteFile
            {
                Version = 2,
                Notes = longNotes,
                Once = onceNotes,
            }, JsonOptions), Utf8NoBom);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"课表备注写入失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>把「有长期备注 / 有单次备注」两个标记写回内存事件，返回被标记的事件数。</summary>
    public int ApplyTo(IEnumerable<ScheduleEvent> events)
    {
        var marked = 0;
        lock (_gate)
        {
            foreach (var e in events)
            {
                e.HasNote = _long.TryGetValue(e.Name, out var text) && !string.IsNullOrWhiteSpace(text);
                e.HasOnceNote = _once.TryGetValue(OnceKey(e), out var once) && !string.IsNullOrWhiteSpace(once);
                if (e.HasNote || e.HasOnceNote) marked++;
            }
        }
        return marked;
    }
}

/// <summary>
/// 课表备注的静态门面：指向用户数据目录里的 schedule_notes.json。
/// 独立于 schedule.json / schedule.ics，所以导入、清空、重新同步课表都不会删掉用户写的内容。
/// 具体逻辑见 <see cref="ScheduleNoteStore"/>。
/// </summary>
public static class ScheduleNotes
{
    /// <summary>写入位置固定在用户数据目录，和课表本体一致；读取时兼容 exe 同目录的旧文件。</summary>
    public static string FilePath => DataPaths.Write("schedule_notes.json");

    private static readonly ScheduleNoteStore Store = CreateStore();

    private static ScheduleNoteStore CreateStore()
    {
        var target = FilePath;

        // 旧版本把备注写在 exe 同目录；升级后统一搬到用户数据目录。
        // 顺序很重要：先把旧内容读进来、写到新位置，再以新位置为准，
        // 否则每次保存都会写回 exe 目录——那里可能是只读的 Program Files。
        if (DataPaths.FindReadable("schedule_notes.json") is { } legacy &&
            !string.Equals(legacy, target, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    File.Copy(legacy, target, overwrite: false);
                }
            }
            catch (Exception ex)
            {
                // 搬不动就退回读旧位置：至少还能读，写入失败会由 Set* 报告
                System.Diagnostics.Debug.WriteLine($"课表备注迁移失败：{ex.Message}");
                return new ScheduleNoteStore(legacy);
            }
        }

        return new ScheduleNoteStore(target);
    }

    public static int Count => Store.LongCount;

    public static int OnceCount => Store.OnceCount;

    public static string GetLong(string course) => Store.GetLong(course);

    public static bool HasLong(string course) => Store.HasLong(course);

    public static bool SetLong(string course, string? text) => Store.SetLong(course, text);

    public static bool RemoveLong(string course) => Store.RemoveLong(course);

    public static string OnceKey(string date, int slot, string course) =>
        ScheduleNoteStore.OnceKey(date, slot, course);

    public static string OnceKey(ScheduleEvent e) => ScheduleNoteStore.OnceKey(e);

    public static string GetOnce(string key) => Store.GetOnce(key);

    public static string GetOnce(ScheduleEvent e) => Store.GetOnce(e);

    public static bool HasOnce(ScheduleEvent e) => Store.HasOnce(e);

    public static bool SetOnce(string key, string? text) => Store.SetOnce(key, text);

    public static bool RemoveOnce(string key) => Store.RemoveOnce(key);

    public static int ApplyTo(IEnumerable<ScheduleEvent> events) => Store.ApplyTo(events);
}
