using Avalonia.Controls;
using SpaceLens.Desktop.ViewModels;

namespace SpaceLens.Desktop.Views;

public partial class LargeFilesView : UserControl
{
    public LargeFilesView()
    {
        InitializeComponent();
        ItemListBehavior.Attach(ItemList, () => (DataContext as LargeFilesViewModel)?.Main, item => item.RevealCommand.Execute(null));
    }
}
