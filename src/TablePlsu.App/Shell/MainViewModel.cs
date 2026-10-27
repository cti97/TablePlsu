using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TablePlsu.App.Features.Welcome;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Connections;

namespace TablePlsu.App.Shell;

/// <summary>
/// Window-level view-model: shows either the <see cref="WelcomeViewModel"/> or one <see cref="WorkspaceViewModel"/>
/// (P0: one workspace per window). Implements the workspace's view of its window (<see cref="IWorkspaceShell"/>) and the
/// key router's view (<see cref="IWorkspaceHost"/>).
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IWorkspaceHost, IWorkspaceShell
{
    public MainViewModel() => ShowWelcome();

    public Window? Window { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Workspace), nameof(ContentScope), nameof(Title))]
    public partial object? Content { get; private set; }

    public IWorkspace? Workspace => Content as IWorkspace;

    public CommandScope ContentScope => Content is WorkspaceViewModel ? CommandScope.Workspace : CommandScope.Welcome;

    public string Title => Content is WorkspaceViewModel ws ? $"{ws.Title} — TablePlsu" : "TablePlsu";

    public WelcomeViewModel? Welcome => Content as WelcomeViewModel;

    public void ShowWelcome() => Content = new WelcomeViewModel(this);

    /// <summary>Resolves secrets (prompting for "Ask" passwords) and opens the workspace. False when the user cancelled.</summary>
    public async Task<bool> ConnectAsync(ConnectionProfile profile, bool newWindow = false, string? database = null)
    {
        ConnectionSecrets? secrets;
        try
        {
            secrets = await SecretResolver.ResolveAsync(profile, Window);
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex.Message, "Could not get the password", Window);
            return false;
        }
        if (secrets is null) return false;
        OpenWorkspace(profile, secrets, database, newWindow);
        return true;
    }

    public void OpenWorkspace(ConnectionProfile profile, ConnectionSecrets secrets, string? database, bool newWindow)
    {
        if (newWindow)
        {
            ShellWindows.OpenNew(vm => vm.OpenWorkspace(profile, secrets, database, newWindow: false));
            return;
        }

        if (Content is WorkspaceViewModel old)
        {
            OpenWorkspaces.Remove(old);
            old.CloseAsync().Forget();
        }
        var ws = new WorkspaceViewModel(profile, secrets, database, this);
        Content = ws;
        OpenWorkspaces.Add(ws);
        AppServices.Features.RaiseWorkspaceOpened(ws);
        ws.ConnectAsync().Forget();
    }

    public void CloseWorkspace(WorkspaceViewModel workspace)
    {
        if (!ReferenceEquals(Content, workspace)) return;
        if (workspace.HasPendingChanges && !Dialogs.Confirm("Discard the pending changes and disconnect?", "Disconnect", "Discard", owner: Window, danger: true))
            return;
        OpenWorkspaces.Remove(workspace);
        workspace.CloseAsync().Forget();
        ShowWelcome();
    }

    public ConnectionProfile? EditConnection(ConnectionProfile profile)
    {
        ConnectionProfile? saved = null;
        var form = new ConnectionFormViewModel(profile, allowConnect: false);
        var view = new ConnectionFormView { DataContext = form };
        var host = Dialogs.CreateHost(view, "Edit Connection", 720, 760, Window);
        form.Saved += p => { saved = p; host.DialogResult = true; };
        form.Cancelled += () => host.DialogResult = false;
        host.ShowDialog();
        return saved;
    }

    /// <summary>Called by the window when it is closing. False cancels the close.</summary>
    public bool OnWindowClosing(bool isLastWindow)
    {
        if (Content is not WorkspaceViewModel ws) return true;
        if (ws.HasPendingChanges && !Dialogs.Confirm("Discard the pending changes and close the window?", "Close", "Discard", owner: Window, danger: true))
            return false;
        ws.FlushAll();
        if (!isLastWindow) OpenWorkspaces.Remove(ws);
        ws.CloseAsync().Forget();
        return true;
    }

    /// <summary>Window deactivated: flush autosave state.</summary>
    public void OnWindowDeactivated() => (Content as WorkspaceViewModel)?.FlushAll();
}
