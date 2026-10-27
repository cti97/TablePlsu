using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Features.Workspace;

public enum TabOpenAction { ActivateExisting, Replace, OpenNew }

/// <summary>The result of <see cref="TabReusePolicy.Decide"/>: what to do and with which tab index.</summary>
public readonly record struct TabOpenDecision(TabOpenAction Action, int Index);

/// <summary>
/// Preview-tab reuse rules (spec §2 "Tab reuse"):
/// <list type="number">
/// <item>"Open in new tab" (<see cref="TabRequest.InNewTab"/>) always opens a new tab.</item>
/// <item>A tab already showing the same object is activated (SQL editors never match).</item>
/// <item>For table/view requests: replace the active tab when it is a clean preview object tab; otherwise replace another
/// clean preview object tab; otherwise open a new tab.</item>
/// <item>Everything else opens a new tab. SQL editors, pinned tabs and dirty tabs are never replaced.</item>
/// </list>
/// </summary>
public static class TabReusePolicy
{
    public static TabOpenDecision Decide(IReadOnlyList<ITabInfo> tabs, int activeIndex, TabRequest request, Func<int, bool>? matches = null)
    {
        if (request.InNewTab) return new(TabOpenAction.OpenNew, -1);

        matches ??= i => DefaultMatches(tabs[i], request);
        for (var i = 0; i < tabs.Count; i++)
            if (matches(i)) return new(TabOpenAction.ActivateExisting, i);

        if (!TabKinds.IsObjectKind(request.Kind)) return new(TabOpenAction.OpenNew, -1);

        if (activeIndex >= 0 && activeIndex < tabs.Count && IsReplaceable(tabs[activeIndex]))
            return new(TabOpenAction.Replace, activeIndex);
        for (var i = 0; i < tabs.Count; i++)
            if (IsReplaceable(tabs[i])) return new(TabOpenAction.Replace, i);
        return new(TabOpenAction.OpenNew, -1);
    }

    /// <summary>A clean preview table/view tab.</summary>
    public static bool IsReplaceable(ITabInfo tab) =>
        TabKinds.IsObjectKind(tab.Kind) && tab.IsPreview && !tab.IsDirty && !tab.HasPendingChanges;

    private static bool DefaultMatches(ITabInfo tab, TabRequest request) =>
        tab.Kind == request.Kind
        && tab.Kind is not (TabKinds.SqlEditor or TabKinds.Empty)
        && string.Equals(tab.Schema, request.Schema, StringComparison.Ordinal)
        && string.Equals(tab.Name, request.Name, StringComparison.Ordinal);
}
