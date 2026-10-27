using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Themes;

namespace TablePlsu.App.Tests;

public class CommandRegistryTests
{
    private static CommandContext Ctx(string id) => new() { CommandId = id };

    [Fact]
    public void Latest_handler_that_can_execute_wins()
    {
        var r = new CommandRegistry();
        var log = new List<string>();
        r.Register("x", _ => log.Add("first"));
        var second = r.Register("x", _ => log.Add("second"));
        r.Register("x", _ => log.Add("third"), _ => false);

        Assert.True(r.TryExecute("x", Ctx("x")));
        Assert.Equal(["second"], log);

        second.Dispose();
        Assert.True(r.TryExecute("x", Ctx("x")));
        Assert.Equal(["second", "first"], log);
    }

    [Fact]
    public void Unknown_or_blocked_commands_are_not_executed()
    {
        var r = new CommandRegistry();
        Assert.False(r.TryExecute("nope", Ctx("nope")));
        r.Register("y", _ => throw new InvalidOperationException(), _ => false);
        Assert.False(r.CanExecute("y", Ctx("y")));
        Assert.False(r.TryExecute("y", Ctx("y")));
    }

    [Fact]
    public void Handler_scope_defaults_to_command_definition()
    {
        var r = new CommandRegistry();
        r.Register(CommandIds.QueryRunAll, _ => { });
        r.Register("custom.thing", _ => { }, scope: CommandScope.Sidebar);
        Assert.Equal(CommandScope.Editor, r.ScopeOf(CommandIds.QueryRunAll));
        Assert.Equal(CommandScope.Sidebar, r.ScopeOf("custom.thing"));
    }
}

public class ToolbarRegistryTests
{
    private static ToolbarItemDefinition Item(string id) => new(id, _ => throw new NotSupportedException());

    [Fact]
    public void Layout_follows_settings_order_then_appends_feature_items()
    {
        var r = new ToolbarRegistry();
        foreach (var id in new[] { ToolbarItemIds.Reload, ToolbarItemIds.Status, "myFeature.export", ToolbarItemIds.ToggleSidebar })
            r.Register(Item(id));

        var layout = r.Layout([ToolbarItemIds.ToggleSidebar, ToolbarItemIds.Status, ToolbarItemIds.Notifications]);
        Assert.Equal([ToolbarItemIds.ToggleSidebar, ToolbarItemIds.Status, "myFeature.export"], layout.Select(i => i.Id));
    }

    [Fact]
    public void Registering_same_id_replaces()
    {
        var r = new ToolbarRegistry();
        r.Register(Item("a"));
        r.Register(Item("a") with { Stretch = true });
        Assert.Single(r.Items);
        Assert.True(r.Items[0].Stretch);
    }
}

public class OtherRegistryTests
{
    [Fact]
    public void Sidebar_menu_contributors_run_in_order()
    {
        var r = new SidebarMenuRegistry();
        r.Register(new Contributor("late", 200));
        r.Register(new Contributor("base", 0));
        var items = r.Build(new SidebarMenuContext(null!, [new SidebarItemRef("public", "t", ObjectKinds.Table)]));
        Assert.Equal(["base:t", "late:t"], items.Cast<string>());
    }

    [Fact]
    public void Palette_registry_concatenates_providers()
    {
        var r = new PaletteRegistry();
        r.Register(new Provider("a", "b"));
        r.Register(new Provider("c"));
        var items = r.GetItems(new PaletteQuery(PaletteMode.Anything, null, null)).Select(i => i.Title);
        Assert.Equal(["a", "b", "c"], items);
    }

    [Fact]
    public void Tab_factory_registry_creates_by_kind()
    {
        var r = new TabFactoryRegistry();
        r.Register(TabKinds.Table, req => new FakeTab(req.Kind, req.Schema, req.Name));
        Assert.True(r.IsRegistered(TabKinds.Table));
        var tab = r.Create(TabRequest.ForObject("s", "t", ObjectKinds.Table), null!);
        Assert.Equal("t", tab!.Name);
        Assert.Null(r.Create(TabRequest.SqlEditor(), null!));
    }

    [Fact]
    public void Workspace_services_bag_disposes_items()
    {
        var bag = new WorkspaceServices();
        var d = bag.GetOrAdd(() => new Disposable());
        Assert.Same(d, bag.Get<Disposable>());
        Assert.Same(d, bag.GetOrAdd(() => new Disposable()));
        bag.Dispose();
        Assert.True(d.Disposed);
        Assert.Null(bag.Get<Disposable>());
    }

    [Fact]
    public void Tab_request_for_object_maps_kinds()
    {
        Assert.Equal(TabKinds.View, TabRequest.ForObject("s", "v", ObjectKinds.View).Kind);
        Assert.Equal(TabKinds.MaterializedView, TabRequest.ForObject("s", "m", ObjectKinds.MaterializedView).Kind);
        Assert.Equal(TabKinds.Function, TabRequest.ForObject("s", "f", ObjectKinds.Procedure).Kind);
        Assert.Equal(TabKinds.Table, TabRequest.ForObject("s", "t", ObjectKinds.Table).Kind);
    }

    private sealed class Contributor(string name, int order) : ISidebarMenuContributor
    {
        public int Order => order;
        public void Contribute(SidebarMenuContext context, IList<object> items) => items.Add($"{name}:{context.Items[0].Name}");
    }

    private sealed class Provider(params string[] titles) : IPaletteItemProvider
    {
        public IEnumerable<PaletteItem> GetItems(PaletteQuery query) => titles.Select(t => new PaletteItem(t, "test", _ => { }));
    }

    private sealed class Disposable : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}

public class ThemeApplierTests
{
    [Fact]
    public void Every_theme_key_becomes_a_frozen_brush_and_color()
    {
        var rd = new ResourceDictionary();
        ThemeApplier.ApplyTo(rd, ThemeService.BuiltInDark);
        foreach (var key in ThemeKeys.All)
        {
            var brush = Assert.IsType<SolidColorBrush>(rd["c." + key]);
            Assert.True(brush.IsFrozen);
            Assert.IsType<Color>(rd["color." + key]);
        }
    }

    [Fact]
    public void Applying_another_theme_replaces_values()
    {
        var rd = new ResourceDictionary();
        ThemeApplier.ApplyTo(rd, ThemeService.BuiltInLight);
        var light = ((SolidColorBrush)rd["c.app.window"]).Color;
        ThemeApplier.ApplyTo(rd, ThemeService.BuiltInDark);
        Assert.NotEqual(light, ((SolidColorBrush)rd["c.app.window"]).Color);
    }

    [Theory]
    [InlineData("#FF0000", true)]
    [InlineData("#80FF0000", true)]
    [InlineData("red", false)]
    [InlineData(null, false)]
    public void Parse_brush(string? text, bool valid) => Assert.Equal(valid, ThemeApplier.ParseBrush(text) is not null);
}

public class ConverterTests
{
    [Fact]
    public void Bool_and_null_visibility()
    {
        var b = BoolToVisibilityConverter.Instance;
        Assert.Equal(Visibility.Visible, b.Convert(true, typeof(Visibility), null, null!));
        Assert.Equal(Visibility.Collapsed, b.Convert(true, typeof(Visibility), "invert", null!));
        var n = NullToVisibilityConverter.Instance;
        Assert.Equal(Visibility.Collapsed, n.Convert("", typeof(Visibility), null, null!));
        Assert.Equal(Visibility.Visible, n.Convert("x", typeof(Visibility), null, null!));
        Assert.Equal(Visibility.Visible, n.Convert(null, typeof(Visibility), "invert", null!));
    }

    [Fact]
    public void Contrasting_foreground_picks_readable_text()
    {
        Assert.Equal(Brushes.Black, Features.Workspace.WorkspaceViewModel.ContrastingForeground(new SolidColorBrush(Colors.Yellow)));
        Assert.Equal(Brushes.White, Features.Workspace.WorkspaceViewModel.ContrastingForeground(new SolidColorBrush(Colors.DarkRed)));
        Assert.Null(Features.Workspace.WorkspaceViewModel.ContrastingForeground(null));
    }
}
