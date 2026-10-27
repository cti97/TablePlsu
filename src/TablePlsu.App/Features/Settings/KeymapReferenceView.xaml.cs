using System.Windows.Controls;
using System.Windows.Input;

namespace TablePlsu.App.Features.Settings;

/// <summary>View for <see cref="KeymapReferenceViewModel"/>.</summary>
public partial class KeymapReferenceView : UserControl
{
    public KeymapReferenceView()
    {
        InitializeComponent();
        Loaded += (_, _) => Filter.Focus();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is KeymapReferenceViewModel vm && List.SelectedItem is KeymapRow row) vm.CopyEntryCommand.Execute(row);
    }
}
