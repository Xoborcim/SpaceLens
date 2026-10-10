using Avalonia.Controls;
using SpaceLens.Desktop.ViewModels;

namespace SpaceLens.Desktop.Views;

public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
        ItemListBehavior.Attach(LargestList, () => (DataContext as OverviewViewModel)?.Main, Show);
        Map.CellInvoked += (_, cell) => Show(cell.Item);
    }

    private void Show(ItemViewModel item)
    {
        if (DataContext is OverviewViewModel page)
        {
            page.Main.ShowInFolders(item.IsFile ? item.Tree.File(item.Index).Directory : item.Index);
        }
    }
}
