using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Tests;

public class TabReusePolicyTests
{
    private sealed record Info(string Kind, string? Schema = null, string? Name = null, bool IsPreview = false, bool IsDirty = false, bool HasPendingChanges = false) : ITabInfo;

    private static TabRequest Table(string name, bool pinned = false, bool inNewTab = false) =>
        TabRequest.ForObject("public", name, ObjectKinds.Table, pinned, inNewTab);

    [Fact]
    public void Empty_workspace_opens_new_tab()
    {
        var d = TabReusePolicy.Decide([], -1, Table("a"));
        Assert.Equal(TabOpenAction.OpenNew, d.Action);
    }

    [Fact]
    public void Same_object_is_activated_not_reopened()
    {
        ITabInfo[] tabs = [new Info(TabKinds.SqlEditor), new Info(TabKinds.Table, "public", "a")];
        var d = TabReusePolicy.Decide(tabs, 0, Table("a"));
        Assert.Equal(new TabOpenDecision(TabOpenAction.ActivateExisting, 1), d);
    }

    [Fact]
    public void Clean_preview_active_table_tab_is_replaced()
    {
        ITabInfo[] tabs = [new Info(TabKinds.Table, "public", "a", IsPreview: true)];
        var d = TabReusePolicy.Decide(tabs, 0, Table("b"));
        Assert.Equal(new TabOpenDecision(TabOpenAction.Replace, 0), d);
    }

    [Fact]
    public void Sql_editor_active_reuses_another_clean_preview_tab()
    {
        ITabInfo[] tabs = [new Info(TabKinds.View, "public", "v", IsPreview: true), new Info(TabKinds.SqlEditor)];
        var d = TabReusePolicy.Decide(tabs, 1, Table("b"));
        Assert.Equal(new TabOpenDecision(TabOpenAction.Replace, 0), d);
    }

    [Theory]
    [InlineData(false, true, false)] // dirty
    [InlineData(false, false, true)] // pending changes
    [InlineData(true, false, false)] // pinned (not preview)
    public void Dirty_pending_or_pinned_tabs_are_never_replaced(bool pinned, bool dirty, bool pending)
    {
        ITabInfo[] tabs = [new Info(TabKinds.Table, "public", "a", IsPreview: !pinned, IsDirty: dirty, HasPendingChanges: pending)];
        var d = TabReusePolicy.Decide(tabs, 0, Table("b"));
        Assert.Equal(TabOpenAction.OpenNew, d.Action);
    }

    [Fact]
    public void Sql_editors_are_never_replaced_or_matched()
    {
        ITabInfo[] tabs = [new Info(TabKinds.SqlEditor, IsPreview: true)];
        Assert.Equal(TabOpenAction.OpenNew, TabReusePolicy.Decide(tabs, 0, Table("b")).Action);
        Assert.Equal(TabOpenAction.OpenNew, TabReusePolicy.Decide(tabs, 0, TabRequest.SqlEditor()).Action);
    }

    [Fact]
    public void Open_in_new_tab_always_opens_new()
    {
        ITabInfo[] tabs = [new Info(TabKinds.Table, "public", "a", IsPreview: true)];
        Assert.Equal(TabOpenAction.OpenNew, TabReusePolicy.Decide(tabs, 0, Table("a", inNewTab: true)).Action);
    }

    [Fact]
    public void Non_object_kinds_open_new_tab_without_replacing()
    {
        ITabInfo[] tabs = [new Info(TabKinds.Table, "public", "a", IsPreview: true)];
        var fn = TabRequest.ForObject("public", "f", ObjectKinds.Function);
        Assert.Equal(TabOpenAction.OpenNew, TabReusePolicy.Decide(tabs, 0, fn).Action);
    }

    [Fact]
    public void Same_name_in_other_schema_is_a_different_object()
    {
        ITabInfo[] tabs = [new Info(TabKinds.Table, "sales", "a")];
        Assert.Equal(TabOpenAction.OpenNew, TabReusePolicy.Decide(tabs, 0, Table("a")).Action);
    }
}
