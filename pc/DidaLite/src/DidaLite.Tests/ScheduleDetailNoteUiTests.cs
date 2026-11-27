using System;
using System.IO;
using System.Linq;
using DidaLite.App.Services;
using DidaLite.App.ViewModels;
using Xunit;

namespace DidaLite.Tests;

/// <summary>
/// 详情面板的「看得见」这一层：只读展示文案、编辑态互斥、取消回滚。
/// 之前这里只测了存储，结果界面上根本看不到备注——所以这几条必须一起测。
/// </summary>
public sealed class ScheduleDetailNoteUiTests
{
    private static ScheduleEvent Event(string date, int slot, string name) => new()
    {
        Uid = $"uid-{date}-{slot}-{name}",
        Date = date,
        Weekday = 1,
        Slot = slot,
        Name = name,
        Room = "A101",
        Start = "08:30",
        End = "10:00",
    };

    [Fact]
    public void Empty_notes_show_a_hint_instead_of_a_blank_box()
    {
        var vm = new ScheduleViewModel();
        var course = vm.Cells.SelectMany(c => c.Items).FirstOrDefault();
        Assert.NotNull(course);

        // 先确保这门课确实没有备注
        ScheduleNotes.RemoveLong(course!.Name);
        ScheduleNotes.RemoveOnce(ScheduleNotes.OnceKey(course));

        vm.OpenCourseCommand.Execute(course);

        Assert.True(vm.LongNoteIsEmpty);
        Assert.True(vm.OnceNoteIsEmpty);
        Assert.Contains("点这里写", vm.LongNoteDisplay);
        Assert.Contains("点这里写", vm.OnceNoteDisplay);

        // 打开详情默认是「看」，不是「改」
        Assert.False(vm.IsEditingLongNote);
        Assert.False(vm.IsEditingOnceNote);
    }

    [Fact]
    public void Read_only_display_shows_the_actual_note_text()
    {
        var vm = new ScheduleViewModel();
        var course = vm.Cells.SelectMany(c => c.Items).FirstOrDefault();
        Assert.NotNull(course);

        try
        {
            ScheduleNotes.SetLong(course!.Name, "期末闭卷");
            ScheduleNotes.SetOnce(ScheduleNotes.OnceKey(course), "带计算器");

            vm.OpenCourseCommand.Execute(course);

            // 只读区直接给出正文，而不是等用户点开输入框才看得到
            Assert.False(vm.LongNoteIsEmpty);
            Assert.Equal("期末闭卷", vm.LongNoteDisplay);
            Assert.Equal("带计算器", vm.OnceNoteDisplay);
        }
        finally
        {
            ScheduleNotes.RemoveLong(course!.Name);
            ScheduleNotes.RemoveOnce(ScheduleNotes.OnceKey(course));
        }
    }

    [Fact]
    public void Two_note_cards_do_not_enter_edit_mode_at_the_same_time()
    {
        var vm = new ScheduleViewModel();
        var course = vm.Cells.SelectMany(c => c.Items).FirstOrDefault();
        Assert.NotNull(course);
        vm.OpenCourseCommand.Execute(course!);

        vm.EditLongNoteCommand.Execute(null);
        Assert.True(vm.IsEditingLongNote);
        Assert.False(vm.IsEditingOnceNote);

        // 点另一张卡要抢过编辑态，避免两个输入框同时挂着
        vm.EditOnceNoteCommand.Execute(null);
        Assert.False(vm.IsEditingLongNote);
        Assert.True(vm.IsEditingOnceNote);
    }

    [Fact]
    public void Cancel_rolls_back_unsaved_edits()
    {
        var vm = new ScheduleViewModel();
        var course = vm.Cells.SelectMany(c => c.Items).FirstOrDefault();
        Assert.NotNull(course);

        try
        {
            ScheduleNotes.SetLong(course!.Name, "原文");
            vm.OpenCourseCommand.Execute(course);

            vm.EditLongNoteCommand.Execute(null);
            vm.NoteText = "乱改的";
            vm.CancelLongNoteCommand.Execute(null);

            Assert.False(vm.IsEditingLongNote);
            Assert.Equal("原文", vm.NoteText);
            Assert.Equal("原文", vm.LongNoteDisplay);
            Assert.Equal("原文", ScheduleNotes.GetLong(course.Name));   // 磁盘没被写脏
        }
        finally
        {
            ScheduleNotes.RemoveLong(course!.Name);
        }
    }

    [Fact]
    public void Saving_leaves_edit_mode_and_refreshes_display()
    {
        var vm = new ScheduleViewModel();
        var course = vm.Cells.SelectMany(c => c.Items).FirstOrDefault();
        Assert.NotNull(course);

        try
        {
            vm.OpenCourseCommand.Execute(course!);
            vm.EditLongNoteCommand.Execute(null);
            vm.NoteText = "新的长期备注";
            vm.SaveNoteCommand.Execute(null);

            Assert.False(vm.IsEditingLongNote);          // 保存后回到只读，眼睛能看到结果
            Assert.Equal("新的长期备注", vm.LongNoteDisplay);
            Assert.Equal("新的长期备注", ScheduleNotes.GetLong(course!.Name));
            Assert.True(vm.HasNote);
        }
        finally
        {
            ScheduleNotes.RemoveLong(course!.Name);
        }
    }
}
