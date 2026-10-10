using CommunityToolkit.Mvvm.ComponentModel;

namespace SpaceLens.Desktop.ViewModels;

/// <summary>A page in the sidebar. Pages rebuild their rows when the scan changes, but only while visible.</summary>
public abstract class PageViewModel : ObservableObject
{
    private bool _dirty = true;

    protected PageViewModel(MainViewModel main, string title, string icon)
    {
        Main = main;
        Title = title;
        Icon = icon;
        main.TreeChanged += (_, _) => MarkDirty();
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPage) && IsCurrent && _dirty)
            {
                Refresh();
            }
        };
    }

    public MainViewModel Main { get; }

    public string Title { get; }

    public string Icon { get; }

    protected bool IsCurrent => ReferenceEquals(Main.CurrentPage, this);

    protected void MarkDirty()
    {
        _dirty = true;
        if (IsCurrent)
        {
            Refresh();
        }
    }

    public void Refresh()
    {
        _dirty = false;
        OnRefresh();
    }

    protected abstract void OnRefresh();

    public override string ToString() => Title;
}
