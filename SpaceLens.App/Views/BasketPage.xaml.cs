using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SpaceLens.App.Services;
using SpaceLens.App.ViewModels;
using SpaceLens.Core.Formatting;

namespace SpaceLens.App.Views;

/// <summary>
/// Items collected from any page for removal. Removal goes through the usual Recycle Bin confirmation,
/// which shows every warning and refuses protected items.
/// </summary>
public sealed partial class BasketPage : Page
{
    public BasketPage()
    {
        InitializeComponent();
        ItemActions.AttachListBehaviors(ItemList);
        State.BasketChanged += (_, _) => Refresh();
        State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.State))
            {
                UpdateButtons();
            }
        };
    }

    public AppState State => AppState.Current;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Refresh();
    }

    private List<EntryItem> Items => ItemList.ItemsSource as List<EntryItem> ?? [];

    private void Refresh()
    {
        var tree = State.Tree;
        var items = tree is null
            ? []
            : State.Basket
                .Select(b => b.IsFile ? EntryItem.ForFile(tree, b.Index) : EntryItem.ForDirectory(tree, b.Index))
                .OrderByDescending(i => i.Size)
                .ToList();
        long max = items.Count > 0 ? items[0].Size : 0;
        foreach (var item in items)
        {
            item.Subtitle = item.ParentPath;
            item.BarWidth = SummaryItem.Bar(item.Size, max);
        }

        ItemList.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = items.Count == 0
            ? "Collect files and folders here from any page, review them, then remove them together."
            : $"{SizeFormatter.FormatCount(items.Count)} items  ·  {SizeFormatter.Format(items.Sum(i => i.Size))}";
        Details.Show(null);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool any = Items.Count > 0;
        RecycleAllButton.IsEnabled = any && State.CanModify;
        ClearButton.IsEnabled = any;
        RemoveSelectedButton.IsEnabled = ItemList.SelectedItems.Count > 0;
    }

    private async void OnRecycleAll(object sender, RoutedEventArgs e)
    {
        // The confirmation lists the items, warns about cautioned ones and stops at protected ones.
        // Removed items leave the basket automatically.
        await ItemActions.RecycleAsync(Items);
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e) =>
        State.RemoveFromBasket(ItemList.SelectedItems.OfType<EntryItem>().Select(i => (i.IsFile, i.Index)).ToList());

    private void OnClear(object sender, RoutedEventArgs e) => State.ClearBasket();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Details.Show(ItemList.SelectedItems.Count == 1 ? ItemList.SelectedItem as EntryItem : null);
        UpdateButtons();
    }
}
