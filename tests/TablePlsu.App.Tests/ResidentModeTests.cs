using System.IO;
using Microsoft.Win32;
using TablePlsu.App.Features.SqlEditor;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.App.Shell;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Settings;

namespace TablePlsu.App.Tests;

#pragma warning disable xUnit1031 // AvalonEdit documents are thread-affine: stay synchronous.

[Collection(AppServicesCollection.Name)]
public class ResidentModeTests
{
    [Fact]
    public void Resident_mode_is_opt_in()
    {
        Assert.False(ResidentMode.IsEnabledBy(new AppSettings()));
        Assert.True(ResidentMode.IsEnabledBy(new AppSettings { General = new GeneralSettings { KeepRunningInTray = true } }));
        // Starting hidden at sign-in without a tray icon would leave an unreachable process.
        Assert.True(ResidentMode.IsEnabledBy(new AppSettings { General = new GeneralSettings { StartWithWindows = true } }));
    }

    [Theory]
    [InlineData(true, false, 0, true)]   // last window, resident → hide
    [InlineData(false, false, 0, false)] // plain mode → close (exit) as before
    [InlineData(true, true, 0, false)]   // Quit → really close
    [InlineData(true, false, 1, false)]  // another window remains → close this one
    public void Last_window_hides_only_in_resident_mode(bool enabled, bool quitting, int others, bool hides) =>
        Assert.Equal(hides, ResidentMode.ShouldHideOnClose(enabled, quitting, others));

    [Fact]
    public void Flush_before_hiding_writes_typed_editor_text_immediately()
    {
        TestApp.Init(new WorkspaceFeature(), new SqlEditorFeature());
        var profile = TestApp.Profile() with { Id = "res" + Guid.NewGuid().ToString("N")[..8] };
        var shell = new MainViewModel();
        shell.OpenWorkspace(profile, ConnectionSecrets.None, null, newWindow: false);
        var ws = Assert.IsType<WorkspaceViewModel>(shell.Workspace);
        var tab = (SqlEditorTabViewModel)ws.NewTab();
        tab.Document.Text = "select 'typed just before hiding';";
        var file = Path.Combine(AppServices.Paths.EditorDir(profile.Id), tab.Id + ".sql");

        shell.FlushAutosave(); // what hide-to-tray / Quit / session end call, without waiting for the debounce

        Assert.Equal("select 'typed just before hiding';", File.ReadAllText(file));
        ws.CloseAsync().Wait();
    }

    [Fact]
    public void Run_key_is_written_once_and_removed_only_when_it_is_ours()
    {
        var scratch = @"Software\TablePlsuTests\" + Guid.NewGuid().ToString("N");
        var old = RunAtLogin.KeyPath;
        RunAtLogin.KeyPath = scratch;
        try
        {
            const string exe = @"C:\Apps\TablePlsu\TablePlsu.App.exe";
            Assert.Null(RunAtLogin.Current());
            Assert.False(RunAtLogin.Sync(false, exe));
            Assert.True(RunAtLogin.Sync(true, exe));
            Assert.Equal("\"C:\\Apps\\TablePlsu\\TablePlsu.App.exe\" --background", RunAtLogin.Current());
            Assert.False(RunAtLogin.Sync(true, exe)); // idempotent

            // Another install owns the entry: leave it alone.
            Assert.False(RunAtLogin.Sync(false, @"D:\Other\TablePlsu.App.exe"));
            Assert.NotNull(RunAtLogin.Current());

            Assert.True(RunAtLogin.Sync(false, exe));
            Assert.Null(RunAtLogin.Current());
        }
        finally
        {
            RunAtLogin.KeyPath = old;
            Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKey(@"Software\TablePlsuTests", throwOnMissingSubKey: false);
        }
    }
}
