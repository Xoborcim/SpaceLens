using Avalonia.Controls;
using Avalonia.Input;
using SpaceLens.Desktop.ViewModels;

namespace SpaceLens.Desktop.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        ItemListBehavior.Attach(ItemList, () => (DataContext as SearchViewModel)?.Main, item =>
        {
            if (item.IsFolder && DataContext is SearchViewModel page)
            {
                page.Main.ShowInFolders(item.Index);
            }
            else
            {
                item.RevealCommand.Execute(null);
            }
        });
        QueryBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is SearchViewModel page)
            {
                e.Handled = true;
                await page.RunNowAsync();
            }
        };
    }
}
