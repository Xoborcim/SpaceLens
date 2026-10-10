using Avalonia.Controls;
using SpaceLens.Desktop.ViewModels;

namespace SpaceLens.Desktop.Views;

public partial class FoldersView : UserControl
{
    public FoldersView()
    {
        InitializeComponent();
        ItemListBehavior.Attach(ItemList, () => (DataContext as FoldersViewModel)?.Main, item => (DataContext as FoldersViewModel)?.Activate(item));
    }
}
