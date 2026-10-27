using System.Windows.Controls;
using System.Windows.Threading;

namespace TablePlsu.App.Features.Welcome;

/// <summary>The connection form (used inline on the welcome screen and in the Edit dialog). DataContext: <see cref="ConnectionFormViewModel"/>.</summary>
public partial class ConnectionFormView : UserControl
{
    public ConnectionFormView()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => NameBox.Focus(), DispatcherPriority.Input);
    }
}
