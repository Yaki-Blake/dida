using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DidaLite.App.Views;

/// <summary>
/// 便签页。界面逻辑很少：新建后把光标放正文，其余都交给 MainViewModel 的绑定与防抖保存。
/// </summary>
public partial class NotesView : UserControl
{
    public NotesView() => InitializeComponent();

    /// <summary>新建便签后调用：光标落到正文，用户可以直接开始写。</summary>
    public void FocusBody()
    {
        Dispatcher.BeginInvoke(() =>
        {
            NoteBodyBox.Focus();
            Keyboard.Focus(NoteBodyBox);
        }, DispatcherPriority.Input);
    }

    /// <summary>键盘上下换条之后调用：把选中那行滚进可视区，别让光标跑到屏幕外面。</summary>
    public void ScrollSelectedIntoView()
    {
        if (NoteList.SelectedItem is null) return;
        if (NoteList.ItemContainerGenerator.ContainerFromItem(NoteList.SelectedItem) is FrameworkElement row)
            row.BringIntoView();
    }
}
