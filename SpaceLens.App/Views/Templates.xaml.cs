using Microsoft.UI.Xaml;

namespace SpaceLens.App.Views;

/// <summary>Code-behind so the shared templates can use compiled bindings (x:Bind).</summary>
public sealed partial class Templates : ResourceDictionary
{
    public Templates() => InitializeComponent();
}
