using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace TablePlsu.App.Features.Structure;

public partial class CreateTableView : UserControl
{
    public CreateTableView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(NameBox.Text)) NameBox.Focus();
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is CreateTableTabViewModel old) old.ColumnAdded -= OnColumnAdded;
            if (e.NewValue is CreateTableTabViewModel vm) vm.ColumnAdded += OnColumnAdded;
        };
    }

    /// <summary>Focuses and selects the new column's name cell.</summary>
    private void OnColumnAdded(object? sender, NewColumnRow row)
    {
        ColumnsGrid.ScrollIntoView(row);
        Dispatcher.BeginInvoke(() =>
        {
            if (ColumnsGrid.ItemContainerGenerator.ContainerFromItem(row) is not DataGridRow container) return;
            var box = FindChild<TextBox>(container, "ColName");
            box?.Focus();
            box?.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t && t.Name == name) return t;
            if (FindChild<T>(child, name) is { } found) return found;
        }
        return null;
    }
}

/// <summary>Negates a bool (PK columns cannot be nullable).</summary>
public sealed class InverseBool : IValueConverter
{
    public static InverseBool Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
