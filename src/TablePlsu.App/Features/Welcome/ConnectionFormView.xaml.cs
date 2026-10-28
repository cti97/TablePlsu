using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TablePlsu.App.Features.Welcome;

/// <summary>The connection form (used inline on the welcome screen and in the Edit dialog). DataContext: <see cref="ConnectionFormViewModel"/>.</summary>
public partial class ConnectionFormView : UserControl
{
    private Action? _detachDatabaseBox;

    public ConnectionFormView()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => NameBox.Focus(), DispatcherPriority.Input);
        DataContextChanged += (_, _) =>
        {
            _detachDatabaseBox?.Invoke();
            _detachDatabaseBox = DataContext is ConnectionFormViewModel vm ? AttachDatabaseBox(DatabaseBox, vm) : null;
        };
    }

    /// <summary>
    /// Wires the editable Database ComboBox to the form: opening the dropdown lists the server's databases (lazily, once
    /// per server), and the typed or picked text survives the list being replaced or cleared (WPF clears an editable
    /// ComboBox's text when its selected item leaves the list). Returns the detach action.
    /// </summary>
    internal static Action AttachDatabaseBox(ComboBox box, ConnectionFormViewModel vm)
    {
        void Opened(object? sender, EventArgs e) => _ = vm.OpenDatabaseList();
        void Changing()
        {
            if (box.SelectedIndex < 0) return;
            var text = box.Text;
            box.SelectedIndex = -1;
            box.Text = text;
        }
        box.DropDownOpened += Opened;
        vm.DatabaseItemsChanging += Changing;
        return () =>
        {
            box.DropDownOpened -= Opened;
            vm.DatabaseItemsChanging -= Changing;
        };
    }
}
