using System;
using System.IO;
using System.Linq;
using DidaLite.App.Services;
using Xunit;

namespace DidaLite.Tests;

/// <summary>
/// 课表备注的两层语义：长期按课程名、单次按「日期 + 节次 + 课程名」。
/// 全部走隔离的临时文件，不碰用户真实的 schedule_notes.json。
/// </summary>
public sealed class ScheduleNoteStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly ScheduleNoteStore _store;

    public ScheduleNoteStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dida-notes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "schedule_notes.json");
        _store = new ScheduleNoteStore(_path);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 清理失败不影响断言结果
        }
    }

    private static ScheduleEvent Event(string date, int slot, string name, string room = "A101") => new()
    {
        Uid = $"uid-{date}-{slot}-{name}",
        Date = date,
        Weekday = 1,
        Slot = slot,
        Name = name,
        Room = room,
        Start = "08:30",
        End = "10:00",
    };

    // ---------------- 长期备注 ----------------

    [Fact]
    public void Long_note_is_shared_by_every_occurrence_of_the_course()
    {
        Assert.True(_store.SetLong("高等数学 I", "期末闭卷，占 70%"));

        // 同一门课的两周两次课，读到的是同一份长期备注
        var week1 = Event("2026-09-14", 1, "高等数学 I");
        var week2 = Event("2026-09-21", 1, "高等数学 I");

        Assert.Equal("期末闭卷，占 70%", _store.GetLong(week1.Name));
        Assert.Equal(_store.GetLong(week1.Name), _store.GetLong(week2.Name));
        Assert.True(_store.HasLong("高等数学 I"));

        // 长期备注不该因为「有单次备注的查询」而被误判
        Assert.False(_store.HasOnce(week1));
    }

    [Fact]
    public void Empty_text_deletes_long_note()
    {
        _store.SetLong("羽毛球", "带球拍");
        Assert.True(_store.HasLong("羽毛球"));

        Assert.True(_store.SetLong("羽毛球", "   "));
        Assert.False(_store.HasLong("羽毛球"));
        Assert.Equal(0, _store.LongCount);
    }

    // ---------------- 单次备注 ----------------

    [Fact]
    public void Once_note_only_hits_the_exact_occurrence()
    {
        var thisWeek = Event("2026-09-14", 1, "高等数学 I");
        var nextWeek = Event("2026-09-21", 1, "高等数学 I");

        Assert.True(_store.SetOnce(ScheduleNoteStore.OnceKey(thisWeek), "这次带计算器"));

        Assert.Equal("这次带计算器", _store.GetOnce(thisWeek));
        Assert.True(_store.HasOnce(thisWeek));

        // 下周同一节次的同一门课不该看到它
        Assert.Equal(string.Empty, _store.GetOnce(nextWeek));
        Assert.False(_store.HasOnce(nextWeek));
    }

    [Fact]
    public void Once_note_is_scoped_by_slot_on_the_same_day()
    {
        var first = Event("2026-09-14", 1, "高等数学 I");
        var third = Event("2026-09-14", 3, "高等数学 I");

        _store.SetOnce(ScheduleNoteStore.OnceKey(first), "第 1-2 节的事");

        Assert.Equal("第 1-2 节的事", _store.GetOnce(first));
        Assert.Equal(string.Empty, _store.GetOnce(third));
    }

    [Fact]
    public void Once_note_survives_room_change_because_key_has_no_uid()
    {
        // uid 里编着教室与周次（见 gdufe_export.js 的 stableUid），换教室它就会变
        var before = Event("2026-09-14", 1, "高等数学 I", room: "笃行楼(SJ2)211");
        before.Uid = "aaaa1111-w2@gdufe.local";
        _store.SetOnce(ScheduleNoteStore.OnceKey(before), "换教室了也别丢");

        var after = Event("2026-09-14", 1, "高等数学 I", room: "励学楼(SJ1)303");
        after.Uid = "bbbb2222-w2@gdufe.local";
        Assert.NotEqual(before.Uid, after.Uid);

        // 单次备注的键不含 uid，所以换教室后这条备注照样跟着这一次课
        Assert.Equal("换教室了也别丢", _store.GetOnce(after));
    }

    [Fact]
    public void Long_and_once_are_independent()
    {
        var e = Event("2026-09-14", 1, "离散数学");
        _store.SetLong("离散数学", "长期");
        _store.SetOnce(ScheduleNoteStore.OnceKey(e), "单次");

        // 清掉单次不影响长期
        Assert.True(_store.RemoveOnce(ScheduleNoteStore.OnceKey(e)));
        Assert.False(_store.HasOnce(e));
        Assert.Equal("长期", _store.GetLong("离散数学"));

        // 清掉长期也不影响（已清空的）单次
        Assert.True(_store.RemoveLong("离散数学"));
        Assert.False(_store.HasLong("离散数学"));
    }

    // ---------------- 落盘与重载 ----------------

    [Fact]
    public void Notes_persist_and_reload_from_disk()
    {
        var e = Event("2026-09-14", 1, "C语言程序设计");
        _store.SetLong("C语言程序设计", "机房上课");
        _store.SetOnce(ScheduleNoteStore.OnceKey(e), "这次交作业");

        var reopened = new ScheduleNoteStore(_path);

        Assert.Equal("机房上课", reopened.GetLong("C语言程序设计"));
        Assert.Equal("这次交作业", reopened.GetOnce(e));
        Assert.Equal(1, reopened.LongCount);
        Assert.Equal(1, reopened.OnceCount);
    }

    [Fact]
    public void Legacy_file_without_once_field_loads_as_long_notes_only()
    {
        // 旧版只写 notes 一个字段
        File.WriteAllText(_path, """
            {"version":1,"notes":{"大学英语 II":"四级词汇","羽毛球":"带球拍"}}
            """, System.Text.Encoding.UTF8);

        var legacy = new ScheduleNoteStore(_path);

        Assert.Equal(2, legacy.LongCount);
        Assert.Equal(0, legacy.OnceCount);
        Assert.Equal("四级词汇", legacy.GetLong("大学英语 II"));

        // 写入单次备注后，旧的长备注必须原样保留
        var e = Event("2026-09-14", 1, "大学英语 II");
        Assert.True(legacy.SetOnce(ScheduleNoteStore.OnceKey(e), "这次小测"));

        var reopened = new ScheduleNoteStore(_path);
        Assert.Equal("四级词汇", reopened.GetLong("大学英语 II"));
        Assert.Equal("这次小测", reopened.GetOnce(e));
    }

    [Fact]
    public void Missing_file_starts_empty_and_first_write_creates_it()
    {
        Assert.False(File.Exists(_path));
        Assert.Equal(0, _store.LongCount);
        Assert.Equal(0, _store.OnceCount);

        Assert.True(_store.SetLong("形势与政策 I", "开卷"));
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void Chinese_text_is_written_unescaped()
    {
        _store.SetLong("中国近现代史纲要", "考试课");

        var raw = File.ReadAllText(_path);
        Assert.Contains("中国近现代史纲要", raw);
        Assert.DoesNotContain("\\u", raw);
    }

    // ---------------- 标记回写 ----------------

    [Fact]
    public void ApplyTo_marks_both_kinds_of_notes()
    {
        var longOnly = Event("2026-09-14", 1, "大学英语 II");
        var onceOnly = Event("2026-09-14", 3, "羽毛球");
        var both = Event("2026-09-15", 1, "高等数学 I");
        var none = Event("2026-09-16", 1, "湾区财经概论");

        _store.SetLong("大学英语 II", "长期");

        _store.SetLong("高等数学 I", "长期");
        _store.SetOnce(ScheduleNoteStore.OnceKey(both), "单次");

        _store.SetOnce(ScheduleNoteStore.OnceKey(onceOnly), "单次");

        var marked = _store.ApplyTo(new[] { longOnly, onceOnly, both, none });

        Assert.Equal(3, marked);

        Assert.True(longOnly.HasNote);
        Assert.False(longOnly.HasOnceNote);

        Assert.False(onceOnly.HasNote);
        Assert.True(onceOnly.HasOnceNote);

        // 同一门课的另一节课：长期备注点亮，单次备注不亮
        var sameCourseOtherWeek = Event("2026-09-22", 1, "高等数学 I");
        _store.ApplyTo(new[] { sameCourseOtherWeek });
        Assert.True(sameCourseOtherWeek.HasNote);
        Assert.False(sameCourseOtherWeek.HasOnceNote);
        Assert.True(both.HasNote);
        Assert.True(both.HasOnceNote);

        Assert.False(none.HasNote);
        Assert.False(none.HasOnceNote);
    }

    [Fact]
    public void Once_key_contains_date_slot_and_course_only()
    {
        var e = Event("2026-09-14", 1, "高等数学 I", room: "笃行楼(SJ2)211");
        e.Uid = "aaaa1111-w2@gdufe.local";

        var key = ScheduleNoteStore.OnceKey(e);

        Assert.Equal("2026-09-14|1|高等数学 I", key);
        Assert.DoesNotContain("gdufe", key);
        Assert.DoesNotContain("笃行楼", key);
    }

    [Fact]
    public void Once_key_is_stable_across_time_changes()
    {
        var morning = Event("2026-09-14", 1, "高等数学 I");
        morning.Start = "08:30";
        morning.End = "10:00";

        var moved = Event("2026-09-14", 1, "高等数学 I");
        moved.Start = "10:20";
        moved.End = "11:50";

        Assert.Equal(ScheduleNoteStore.OnceKey(morning), ScheduleNoteStore.OnceKey(moved));
    }
}
