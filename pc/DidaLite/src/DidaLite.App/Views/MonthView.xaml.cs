using System.Windows;
using System.Windows.Controls;
using DidaLite.App.ViewModels;

namespace DidaLite.App.Views;

/// <summary>月视图：行高按可用高度算，第一次切到「月」时才创建。</summary>
public partial class MonthView : UserControl
{
    public MonthView() => InitializeComponent();

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height <= 0) return;

        var header = WeekHeaderRow.ActualHeight > 0 ? WeekHeaderRow.ActualHeight : 22;
        var rowHeight = Math.Floor((e.NewSize.Height - header - 4) / 6.0) - 1;
        if (rowHeight <= 0) return;

        var limit = (int)Math.Floor((rowHeight - MainViewModel.MonthCellChrome) / MainViewModel.MonthItemHeight);
        if (DataContext is MainViewModel vm) vm.UpdateMonthMetrics(rowHeight, limit);
    }
}
