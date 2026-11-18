using CommunityToolkit.Mvvm.ComponentModel;
using DidaLite.Core.Models;

namespace DidaLite.App.ViewModels;

/// <summary>
/// 便签列表里的一行。只做展示，真正的编辑缓冲区在 MainViewModel 上，
/// 这样切换选中项时能把上一段文字干净地落盘。
/// </summary>
public sealed class NoteViewModel : ObservableObject
{
    public NoteViewModel(NoteItem model) => Model = model;

    public NoteItem Model { get; }

    /// <summary>没写标题时自动拿正文首行顶上。</summary>
    public string DisplayTitle => Model.DisplayTitle;

    /// <summary>正文压成一行做预览；空正文返回空字符串，界面上那一行自动收起。</summary>
    public string Preview
    {
        get
        {
            var lines = Model.Body
                .Replace("\r", string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // 没写标题时列表标题取的是正文首行，预览从第二行开始，别把同一句显示两遍
            var start = string.IsNullOrWhiteSpace(Model.Title) && lines.Length > 0 ? 1 : 0;
            var text = string.Join(" ", lines.Skip(start));
            if (text.Length == 0) return string.Empty;
            return text.Length <= 90 ? text : text[..90] + "…";
        }
    }

    public bool HasPreview => Preview.Length > 0;

    public bool Pinned => Model.Pinned;

    /// <summary>「置顶」两个字，列表右侧的小标记。</summary>
    public string PinMark => Model.Pinned ? "置顶" : string.Empty;

    public string Stamp
    {
        get
        {
            if (Model.UpdatedAt <= 0) return string.Empty;

            var local = DateTimeOffset.FromUnixTimeMilliseconds(Model.UpdatedAt).ToLocalTime();
            return local.Date == DateTime.Today
                ? local.ToString("HH:mm")
                : local.Year == DateTime.Today.Year
                    ? local.ToString("M月d日")
                    : local.ToString("yyyy年M月d日");
        }
    }

    /// <summary>正文改过之后刷新列表里那几行文字，不重建整个列表（避免滚动位置跳回顶部）。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(Pinned));
        OnPropertyChanged(nameof(PinMark));
        OnPropertyChanged(nameof(Stamp));
    }
}
