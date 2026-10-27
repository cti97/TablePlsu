using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TablePlsu.App.Features.Palette;
using TablePlsu.App.Features.Sidebar;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Themes;

namespace TablePlsu.App.Tests;

/// <summary>Real WPF windows (STA) for the sidebar view and the palette window. App-level styles are absent (DynamicResource).</summary>
[Collection(AppServicesCollection.Name)]
public class SidebarUiTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

    private void Mark(string what) { output.WriteLine($"{_sw.ElapsedMilliseconds,6} ms  {what}"); }

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
            finally { try { Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch (NullReferenceException) { } }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("STA body failed: " + error);
    }

    private void DoEvents() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private Window Host(FrameworkElement content)
    {
        var window = new Window
        {
            Width = 320, Height = 700, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = content,
        };
        ThemeApplier.ApplyTo(window.Resources, ThemeService.BuiltInDark);
        window.Show();
        DoEvents();
        return window;
    }

    [Fact]
    public void Sidebar_view_renders_tree_restores_selection_and_settles()
    {
        TestApp.Init(new WorkspaceFeature(), new FakeTabsFeature(true), new SidebarFeature());
        var ws = new WorkspaceViewModel(TestApp.Profile(), ConnectionSecrets.None, null, shell: null);
        ws.Metadata.Save(SampleMetadata.Create());
        ws.Metadata.LoadCachedAsync().GetAwaiter().GetResult();
        RunSta(() =>
        {
            var vm = SidebarFeature.For(ws);
            Mark("var view = (SidebarV"); var view = (SidebarView)AppServices.Features.Panels.Create(WorkspacePanel.Sidebar, ws)!;
            var window = Host(view);
            var list = (ListBox)view.FindName("ItemsList");
            Assert.Equal(vm.Nodes.Count, list.Items.Count);
            Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));

            // Select a table, rebuild (as a metadata refresh would): selection survives.
            var customers = vm.Nodes.Single(n => n.Key == "public/Tables/customers");
            list.SelectedItem = customers;
            DoEvents();
            Assert.Equal([customers], vm.SelectedNodes);
            Mark("vm.Rebuild();"); vm.Rebuild();
            DoEvents();
            Assert.Equal("public/Tables/customers", Assert.IsType<SidebarNode>(list.SelectedItem).Key);
            Assert.Equal("public/Tables/customers", Assert.Single(vm.SelectedNodes).Key);

            // Filtering through the bound search box.
            var search = (TextBox)view.FindName("ItemsSearch");
            Mark("search"); search.Text = "^cust";
            DoEvents();
            Assert.Contains(vm.Nodes, n => n.Key == "public/Tables/customers");
            Assert.DoesNotContain(vm.Nodes, n => n.Key == "public/Tables/no_pk");
            search.Text = "";

            // History tab swaps the list.
            vm.SelectedTabIndex = SidebarViewModel.HistoryTab;
            DoEvents();
            Assert.Equal(Visibility.Collapsed, list.Visibility);
            Assert.Equal(Visibility.Visible, ((ListBox)view.FindName("HistoryList")).Visibility);
            vm.SelectedTabIndex = SidebarViewModel.ItemsTab;
            DoEvents();

            Mark("idle"); // Idle: no layout or rebuild loop.
            var layouts = 0;
            var rebuilds = 0;
            view.LayoutUpdated += (_, _) => layouts++;
            vm.NodesRebuilt += _ => rebuilds++;
            for (var i = 0; i < 20; i++)
            {
                DoEvents();
                Thread.Sleep(10);
            }
            Assert.Equal(0, rebuilds);
            Assert.True(layouts < 5, $"layouts while idle: {layouts}");

            Mark("menu"); // Context menu builds from the registry for the selection.
            list.SelectedItem = vm.Nodes.Single(n => n.Key == "public/Tables/customers");
            DoEvents();
            var menu = AppServices.Features.SidebarMenu.Build(new SidebarMenuContext(ws, vm.SelectedRefs()));
            Assert.Contains(menu, o => o is MenuItem { Header: "Truncate…" });
            window.Close();
        });
        ws.CloseAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void Palette_window_opens_over_owner_filters_and_closes_on_escape()
    {
        TestApp.Init(new WorkspaceFeature(), new FakeTabsFeature(true), new SidebarFeature(), new PaletteFeature());
        var ws = new WorkspaceViewModel(TestApp.Profile(), ConnectionSecrets.None, null, shell: null);
        ws.Metadata.Save(SampleMetadata.Create());
        ws.Metadata.LoadCachedAsync().GetAwaiter().GetResult();
        RunSta(() =>
        {
            var owner = Host(new Grid());
            Mark("var palette = Palett"); var palette = PaletteWindow.Open(PaletteMode.Anything, ws, owner);
            DoEvents();
            Assert.Same(palette, PaletteWindow.Current);
            Assert.True(palette.IsVisible);
            var list = (ListBox)palette.FindName("List");
            Assert.True(list.Items.Count > 0); // commands on empty input

            Mark("palette.ViewModel.Te"); palette.ViewModel.Text = "ordr";
            DoEvents();
            Assert.Equal("orders", ((PaletteRow)list.Items[0]).Title);
            palette.ViewModel.MoveSelection(1);
            DoEvents();
            Assert.Equal(1, list.SelectedIndex);

            Mark("var esc = "); var esc = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(palette)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            palette.RaiseEvent(esc);
            Assert.Equal("", palette.ViewModel.Text);
            Assert.True(palette.IsVisible);
            palette.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(palette)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            DoEvents();
            Assert.False(palette.IsVisible);
            Assert.Null(PaletteWindow.Current);

            // Enter on a table opens it and closes the palette.
            Mark("var p2 = "); var p2 = PaletteWindow.Open(PaletteMode.Anything, ws, owner);
            p2.ViewModel.Text = "^customers$";
            DoEvents();
            p2.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(p2)!, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            DoEvents();
            Assert.False(p2.IsVisible);
            Assert.Equal("customers", ws.ActiveTab?.Name);

            // Opening another palette replaces the open one.
            Mark("var p3 = "); var p3 = PaletteWindow.Open(PaletteMode.Connections, ws, owner);
            var p4 = PaletteWindow.Open(PaletteMode.Databases, ws, owner);
            DoEvents();
            Assert.False(p3.IsVisible);
            Assert.Equal("Open Database", p4.Title);
            Mark("p4.Close();"); p4.Close();
            owner.Close();
        });
        ws.CloseAsync().GetAwaiter().GetResult();
    }
}
