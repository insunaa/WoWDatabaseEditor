using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WoWDatabaseEditorCore.Avalonia.Services.FindAnywhere;

public class FindAnywhereResultsView : UserControl
{
    public FindAnywhereResultsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}