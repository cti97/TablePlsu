using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using static TablePlsu.UiFlows.Ui;

namespace TablePlsu.UiFlows;

public sealed partial class Flows
{
    public void RunAll(HashSet<string> only)
    {
        _only = only;
        Launch();

        Step("welcome.createConnectionForm+Test", CreateConnectionViaForm);
        Step("welcome.importFromUrl", ImportFromUrl);
        Step("welcome.connect", () => { Activate(Main); CloseFormIfOpen(); ConnectFromList(); });
        if (!Title().Contains("/ " + _pg.Database))
        {
            Results.Add(new FlowResult("(rest)", false, "not connected; remaining flows skipped"));
            return;
        }

        Step("Ctrl+P open table", CtrlPOpenTable);
        Step("Ctrl+T new SQL tab", CtrlTNewTab);
        Step("editor autocomplete accept", AutocompleteAccept);
        Step("Ctrl+Shift+Enter multi-statement result tabs", RunAllResultTabs);
        Step("Ctrl+Enter run current", RunCurrent);
        Step("error jump", ErrorJump);
        Step("cancel pg_sleep", CancelSleep);
        Step("row limit change", RowLimitChange);
        Step("Ctrl+W / Ctrl+Shift+T", CloseReopen);
        Step("Ctrl+Tab / Ctrl+1..9", TabNavigation);
        Step("Ctrl+0 sidebar toggle", SidebarToggle);
        Step("Ctrl+Shift+F sidebar search + open table", SidebarSearchOpen);
        Step("paging Ctrl+Right", Paging);
        Step("sort header click", SortHeader);
        Step("Ctrl+F filter bar + apply", FilterApply);
        Step("edit cell + Ctrl+Shift+P preview + Ctrl+S commit", EditPreviewCommit);
        Step("Ctrl+Alt+2 structure", Structure);
        Step("Ctrl+Alt+C console", ConsoleToggle);
        Step("Ctrl+, settings save changes font live", SettingsFontLive);
        Step("theme switch dark", ThemeDark);
        Step("Ctrl+K switch database", SwitchDatabase);
        Step("Ctrl+Shift+K open connection", OpenConnectionPalette);
        Step("autosave survives kill + restore", AutosaveSurvivesKill);
    }

    // ================= welcome =================

    void CreateConnectionViaForm()
    {
        Activate(Main);
        var create = FirstVisible(Main, And(Type(ControlType.Button), Named("Create Connection…"))) ?? throw new FlowFailed("no Create Connection button");
        Click(create);
        var name = WaitFor(() => FirstVisible(Main, Id("NameBox"))) ?? throw new FlowFailed("form did not open");
        Fill(name, "uiflows-form");
        Fill(FirstVisible(Main, Id("HostBox")), _pg.Host!);
        Fill(FirstVisible(Main, Id("PortBox")), _pg.Port.ToString());
        Fill(FirstVisible(Main, Id("UserBox")), _pg.Username!);
        Fill(FirstVisible(Main, Id("DatabaseBox")), _pg.Database!);
        SelectComboItem(FirstVisible(Main, Id("PasswordModeBox")), "No password");
        Click(FirstVisible(Main, Id("TestButton")) ?? throw new FlowFailed("no Test button"));
        var ok = WaitTrue(() => Name(First(Main, Id("FormStatus"))).Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase), 10000);
        Check(ok, "Test did not report success: '" + Name(First(Main, Id("FormStatus"))) + "'");
        Click(FirstVisible(Main, Id("SaveButton")) ?? throw new FlowFailed("no Save button"));
        var listed = WaitFor(() => FirstVisible(Main, And(Type(ControlType.ListItem), Named("uiflows-form"))), 5000);
        Check(listed != null, "saved connection not in the list");
        var json = File.ReadAllText(Path.Combine(_root, "roaming", "connections.json"));
        Check(json.Contains("uiflows-form") && json.Contains(_pg.Port.ToString()), "connections.json missing the new connection");
    }

    /// <summary>Opens a combo box with a click and clicks the item (popup items live under the main window in UIA).</summary>
    void SelectComboItem(AutomationElement? combo, params string[] labels)
    {
        if (combo == null) throw new FlowFailed("combo box not found");
        Click(combo);
        var item = WaitFor(() => U.Windows().SelectMany(w => All(w, Type(ControlType.ListItem)))
            .FirstOrDefault(i => (labels.Contains(Name(i)) || All(i, Type(ControlType.Text)).Any(t => labels.Contains(Name(t)))) && !Safe(() => i.Current.IsOffscreen)), 3000);
        if (item == null)
        {
            U.Keys("Esc");
            throw new FlowFailed($"combo has no item {labels[0]}");
        }
        Click(item);
        Check(WaitTrue(() => labels.Contains(ComboText(combo)), 2000), $"combo shows '{ComboText(combo)}' after picking '{labels[0]}'");
    }

    static string ComboText(AutomationElement combo)
    {
        if (combo.TryGetCurrentPattern(SelectionPattern.Pattern, out var p))
        {
            var sel = ((SelectionPattern)p).Current.GetSelection();
            if (sel.Length > 0) return All(sel[0], Type(ControlType.Text)).Select(Name).FirstOrDefault(n => n.Length > 0) ?? Name(sel[0]);
        }
        return Value(combo);
    }

    void CloseFormIfOpen()
    {
        if (FirstVisible(Main, Id("HostBox")) != null && FirstVisible(Main, And(Type(ControlType.Button), Named("Cancel"))) is { } cancel)
        {
            Click(cancel);
            WaitTrue(() => FirstVisible(Main, Id("HostBox")) == null, 2000);
        }
    }

    void Fill(AutomationElement? box, string text)
    {
        if (box == null) throw new FlowFailed("field not found");
        Click(box);
        U.Keys("Ctrl+A");
        U.Type(text, 2);
        Check(Value(box) == text, $"field shows '{Value(box)}' after typing '{text}'");
    }

    /// <summary>A window other than the main windows: top-level, or an owned dialog (UIA lists those under their owner).</summary>
    AutomationElement? OtherWindow(Func<AutomationElement, bool>? pred = null)
    {
        foreach (var w in U.Windows())
        {
            if (Safe(() => w.Current.AutomationId) != "MainWindow")
            {
                if (pred == null || pred(w)) return w;
                continue;
            }
            foreach (var owned in Safe(() => w.FindAll(TreeScope.Children, Type(ControlType.Window)).Cast<AutomationElement>().ToList()) ?? [])
                if (pred == null || pred(owned)) return owned;
        }
        return null;
    }

    void ImportFromUrl()
    {
        Activate(Main);
        CloseFormIfOpen();
        var btn = FirstVisible(Main, And(Type(ControlType.Button), Named("Import from URL…"))) ?? throw new FlowFailed("no Import from URL button");
        Click(btn);
        var prompt = WaitFor(() => OtherWindow(w => First(w, Id("InputBox")) != null)) ?? throw new FlowFailed("URL prompt did not open");
        var input = First(prompt, Id("InputBox"))!;
        Check(HasFocus(input), "URL prompt input is not focused");
        U.Type($"postgresql://{_pg.Username}@{_pg.Host}:{_pg.Port}/{_pg.Database}?sslmode=disable&name=from-url", 2);
        U.Keys("Enter");
        var host = WaitFor(() => FirstVisible(Main, Id("HostBox")), 5000) ?? throw new FlowFailed("form did not open after import");
        Check(Value(host) == _pg.Host, "host not prefilled: " + Value(host));
        Check(Value(FirstVisible(Main, Id("PortBox"))) == _pg.Port.ToString(), "port not prefilled");
        Check(Value(FirstVisible(Main, Id("DatabaseBox"))) == _pg.Database, "database not prefilled");
        Check(Value(FirstVisible(Main, Id("NameBox"))) == "from-url", "name not prefilled: " + Value(FirstVisible(Main, Id("NameBox"))));
        Click(FirstVisible(Main, And(Type(ControlType.Button), Named("Cancel")))!);
        Check(WaitTrue(() => FirstVisible(Main, Id("HostBox")) == null, 3000), "form did not close on Cancel");
    }

    // ================= workspace helpers =================

    List<AutomationElement> Tabs() =>
        (First(Main, Id("TabStrip")) is { } strip ? All(strip, Type(ControlType.ListItem)) : []);

    AutomationElement? SelectedTab() => Tabs().FirstOrDefault(IsSelected);
    string SelectedTabName() => Name(SelectedTab());

    AutomationElement Editor() => WaitFor(() => FirstVisible(Main, Id("Editor")), 5000) ?? throw new FlowFailed("no visible SQL editor");
    string EditorText() => Value(Editor());

    AutomationElement? Palette() => OtherWindow(w => First(w, Id("Input")) != null);

    void NewSqlTab()
    {
        Activate(Main);
        var before = Tabs().Count;
        U.Keys("Ctrl+T");
        Check(WaitTrue(() => Tabs().Count == before + 1, 5000), "Ctrl+T did not add a tab");
        Editor();
        Thread.Sleep(300);
    }

    /// <summary>Replaces the editor text by typing (Esc closes the completion popup that typing opens).</summary>
    void SetEditorText(string text)
    {
        var ed = Editor();
        Click(ed);
        U.Keys("Ctrl+A Delete");
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) U.Keys("Enter");
            U.Type(lines[i], 4);
            Thread.Sleep(150);
            CloseCompletion();
        }
        Check(WaitTrue(() => Norm(EditorText()) == Norm(text), 2000), $"editor text is '{EditorText()}', expected '{text}'");
    }

    static string Norm(string s) => s.Replace("\r\n", "\n").TrimEnd();

    AutomationElement? CompletionPopup() => OtherWindow(w => First(w, Id("PART_ListBox")) != null);

    void CloseCompletion()
    {
        if (CompletionPopup() != null) { U.Keys("Esc"); WaitTrue(() => CompletionPopup() == null, 1000); }
    }

    string Summary() => Name(FirstVisible(Main, Id("ResultSummary")));

    List<AutomationElement> ResultTabs() =>
        FirstVisible(Main, Id("ResultTabs")) is { } l ? All(l, Type(ControlType.ListItem)) : [];

    string RunButtonText() => Name(FirstVisible(Main, Id("RunButton")));

    bool WaitIdle(int ms = 15000) => WaitTrue(() => RunButtonText().Contains("Run"), ms);

    // ================= workspace flows =================

    void CtrlPOpenTable()
    {
        Activate(Main);
        U.Keys("Ctrl+P");
        var pal = WaitFor(Palette, 3000) ?? throw new FlowFailed("Ctrl+P did not open the palette");
        Check(HasFocus(First(pal, Id("Input"))), "palette input not focused");
        U.Type("orders", 20);
        // Like a user: press Enter once the list shows the table.
        Check(WaitTrue(() => PaletteRowTitles(pal).FirstOrDefault() == "orders", 5000),
            "palette top hit for 'orders' is '" + PaletteRowTitles(pal).FirstOrDefault() + "' (status '" + Name(First(pal, Id("Status"))) + "')");
        U.Keys("Enter");
        Check(WaitTrue(() => Palette() == null, 3000), "palette did not close on Enter");
        Check(WaitTrue(() => SelectedTabName().Contains("orders"), 5000), "selected tab is '" + SelectedTabName() + "', expected orders");
        var count = WaitFor(() => FirstVisible(Main, Id("RowCountButton")) is { } c && Name(c).Contains("row") ? c : null, 10000);
        Check(count != null, "orders tab shows no row count");
    }

    /// <summary>The bold title of each palette row (its last text element).</summary>
    static List<string> PaletteRowTitles(AutomationElement pal) =>
        All(pal, Type(ControlType.ListItem)).Select(i => All(i, Type(ControlType.Text)).Select(Name).Where(n => n.Length > 0).ToList())
            .Select(t => t.FirstOrDefault() ?? "").ToList();

    void CtrlTNewTab()
    {
        NewSqlTab();
        Check(SelectedTabName().StartsWith("Query", StringComparison.Ordinal), "new tab is '" + SelectedTabName() + "'");
        // Focus must land in the editor: typing goes straight into it.
        U.Type("select 1", 10);
        CloseCompletion();
        Check(WaitTrue(() => EditorText().Contains("select 1"), 2000), "typing after Ctrl+T did not reach the editor: '" + EditorText() + "'");
    }

    void AutocompleteAccept()
    {
        var ed = Editor();
        Click(ed);
        U.Keys("Ctrl+A Delete");
        U.Type("select * from sales.ord", 30);
        var popup = WaitFor(CompletionPopup, 3000) ?? throw new FlowFailed("no completion popup after 'sales.ord'");
        U.Keys("Enter");
        Check(WaitTrue(() => Norm(EditorText()).StartsWith("select * from sales.orders", StringComparison.Ordinal), 2000), "accept did not complete: '" + EditorText() + "'");
        Check(!EditorText().Contains('\n'), "Enter inserted a newline instead of only accepting: '" + EditorText() + "'");
        CloseCompletion();
    }

    void RunAllResultTabs()
    {
        SetEditorText("select 1 as a; select 2 as b; select 3 as c");
        U.Keys("Ctrl+Shift+Enter");
        Check(WaitTrue(() => ResultTabs().Count == 3, 10000), $"expected 3 result tabs, got {ResultTabs().Count}");
        Check(WaitIdle(), "run did not finish");
    }

    void RunCurrent()
    {
        // Caret is at the end (statement 3): Ctrl+Enter runs only that statement.
        Click(Editor());
        U.Keys("Ctrl+End");
        U.Keys("Ctrl+Enter");
        Check(WaitTrue(() => ResultTabs().Count == 1 && Summary().StartsWith("1 row", StringComparison.Ordinal), 10000),
            $"Ctrl+Enter: {ResultTabs().Count} result tabs, summary '{Summary()}'");
    }

    void ErrorJump()
    {
        SetEditorText("select 1;\nselect * from no_such_tbl_uiflows;");
        U.Keys("Ctrl+Home");
        U.Keys("Ctrl+Shift+Enter");
        Check(WaitIdle(), "run did not finish");
        Thread.Sleep(300);
        var msgs = Value(FirstVisible(Main, Id("MessagesBox")));
        Check(msgs.Contains("no_such_tbl_uiflows"), "message view does not show the error: '" + msgs + "'");
        // The caret must have jumped to the error: type a marker and see where it lands.
        U.Type("@", 0);
        var text = Norm(EditorText());
        U.Keys("Ctrl+Z");
        var line2 = text.Split('\n').ElementAtOrDefault(1) ?? "";
        Check(line2.Contains('@'), "caret did not jump to the error line (text after marker: '" + text.Replace("\n", "\\n") + "')");
        Check(line2.IndexOf('@') == line2.Replace("@", "").IndexOf("no_such_tbl_uiflows", StringComparison.Ordinal),
            "caret not at the error position: '" + line2 + "'");
    }

    void CancelSleep()
    {
        SetEditorText("select pg_sleep(30)");
        U.Keys("Ctrl+Enter");
        Check(WaitTrue(() => RunButtonText().Contains("Cancel"), 5000), "Run did not become Cancel: '" + RunButtonText() + "'");
        Thread.Sleep(500);
        var sw = Stopwatch.StartNew();
        U.Keys("Ctrl+Shift+.");
        Check(WaitIdle(5000), "query was not cancelled within 5 s (button '" + RunButtonText() + "')");
        Check(sw.ElapsedMilliseconds < 3000, $"cancel took {sw.ElapsedMilliseconds} ms");
        var msgs = Value(FirstVisible(Main, Id("MessagesBox"))) + " " + Summary();
        Check(Regex.IsMatch(msgs, "cancel", RegexOptions.IgnoreCase), "no cancel message: '" + msgs + "'");
    }

    void RowLimitChange()
    {
        var box = FirstVisible(Main, Id("RowLimitBox")) ?? throw new FlowFailed("no row limit box");
        SelectComboItem(box, "100 rows");
        SetEditorText("select g from generate_series(1,5000) g");
        U.Keys("Ctrl+Enter");
        Check(WaitTrue(() => Summary().StartsWith("100 rows", StringComparison.Ordinal), 10000), "row limit 100 not applied: '" + Summary() + "'");
        // Back to 1000 for the other flows.
        SelectComboItem(box, "1,000 rows");
    }

    void CloseReopen()
    {
        var marker = "select 42 as reopened_" + Guid.NewGuid().ToString("N")[..6];
        SetEditorText(marker);
        var before = Tabs().Count;
        U.Keys("Ctrl+W");
        Check(WaitTrue(() => Tabs().Count == before - 1, 3000), "Ctrl+W did not close the tab");
        U.Keys("Ctrl+Shift+T");
        Check(WaitTrue(() => Tabs().Count == before, 3000), "Ctrl+Shift+T did not reopen a tab");
        Check(WaitTrue(() => EditorText().Contains(marker), 3000), "reopened tab lost its text: '" + EditorText() + "'");
    }

    void TabNavigation()
    {
        while (Tabs().Count < 3) NewSqlTab();
        var names = Tabs().Select(Name).ToList();
        Click(Editor());
        U.Keys("Ctrl+1");
        Check(WaitTrue(() => IsSelected(Tabs()[0]), 2000), "Ctrl+1 did not select tab 1 (selected: " + SelectedTabName() + ")");
        U.Keys("Ctrl+2");
        Check(WaitTrue(() => IsSelected(Tabs()[1]), 2000), "Ctrl+2 did not select tab 2 (selected: " + SelectedTabName() + ")");
        U.Keys("Ctrl+Tab");
        Check(WaitTrue(() => IsSelected(Tabs()[2]), 2000), "Ctrl+Tab did not move to tab 3 (selected: " + SelectedTabName() + ")");
        U.Keys("Ctrl+Shift+Tab");
        Check(WaitTrue(() => IsSelected(Tabs()[1]), 2000), "Ctrl+Shift+Tab did not move back to tab 2 (selected: " + SelectedTabName() + ")");
        U.Keys("Ctrl+9");
        Check(WaitTrue(() => IsSelected(Tabs()[^1]), 2000), "Ctrl+9 did not select the last tab (selected: " + SelectedTabName() + ")");
        // From inside the editor too (focus in AvalonEdit must not swallow Ctrl+Tab).
        Click(Editor());
        U.Keys("Ctrl+1");
        Check(WaitTrue(() => IsSelected(Tabs()[0]), 2000), "Ctrl+1 from the editor did not select tab 1");
    }

    void SidebarToggle()
    {
        Activate(Main);
        Check(FirstVisible(Main, Id("ItemsSearch")) != null, "sidebar not visible to start with");
        U.Keys("Ctrl+0");
        Check(WaitTrue(() => FirstVisible(Main, Id("ItemsSearch")) == null, 2000), "Ctrl+0 did not hide the sidebar");
        U.Keys("Ctrl+0");
        Check(WaitTrue(() => FirstVisible(Main, Id("ItemsSearch")) != null, 2000), "Ctrl+0 did not show the sidebar again");
    }

    void SidebarSearchOpen()
    {
        NewSqlTab();
        U.Keys("Ctrl+Shift+F");
        var search = FirstVisible(Main, Id("ItemsSearch")) ?? throw new FlowFailed("no sidebar search");
        Check(WaitTrue(() => HasFocus(search), 2000), "Ctrl+Shift+F did not focus the sidebar search");
        U.Keys("Ctrl+A");
        U.Type("customers", 20);
        Check(WaitTrue(() => Visible(Main, Id("ItemText")).Any(t => Name(t) == "customers") && !Visible(Main, Id("ItemText")).Any(t => Name(t) == "no_pk"), 3000),
            "sidebar search did not filter to customers");
        // Arrow down to the exact "customers" row (fuzzy search also lists active_customers), then Enter.
        U.Keys("Down");
        for (int i = 0; i < 6 && !SidebarSelectedIs("customers"); i++) U.Keys("Down");
        Check(SidebarSelectedIs("customers"), "could not select customers with the arrow keys");
        U.Keys("Enter");
        Check(WaitTrue(() => SelectedTabName() == "customers", 5000), "Enter did not open customers (selected: '" + SelectedTabName() + "')");
        Check(WaitTrue(() => Name(FirstVisible(Main, Id("RowCountButton"))).Contains("5,000"), 10000),
            "customers row count: '" + Name(FirstVisible(Main, Id("RowCountButton"))) + "'");
        // Clear the search for later flows.
        Click(search);
        U.Keys("Ctrl+A Delete Esc");
    }

    bool SidebarSelectedIs(string name) =>
        FirstVisible(Main, Id("ItemsList")) is { } list && All(list, Type(ControlType.ListItem)).Any(i => IsSelected(i) && Name(i).EndsWith("/" + name, StringComparison.Ordinal));

    AutomationElement DataGrid() => WaitFor(() => FirstVisible(Main, Type(ControlType.DataGrid)), 5000) ?? throw new FlowFailed("no visible data grid");

    List<AutomationElement> GridRows() => Visible(DataGrid(), Type(ControlType.DataItem));

    /// <summary>Clicks a cell of the first visible row and copies it with Ctrl+C (the user's way of reading a value).</summary>
    string CopyFirstRow()
    {
        var row = WaitFor(() => GridRows().FirstOrDefault(), 5000) ?? throw new FlowFailed("grid has no rows");
        var cell = Visible(row, Type(ControlType.Custom)).FirstOrDefault() ?? Visible(row, Type(ControlType.Text)).FirstOrDefault() ?? row;
        ClearClipboard();
        Click(cell);
        U.Keys("Ctrl+C");
        Thread.Sleep(300);
        return ClipboardText();
    }

    string PageText() => Name(FirstVisible(Main, Id("PageButton")));

    void Paging()
    {
        Check(SelectedTabName() == "customers", "customers tab not active");
        var p1 = PageText();
        CopyFirstRow(); // focuses the grid
        U.Keys("Ctrl+Right");
        Check(WaitTrue(() => PageText() != p1 && PageText().Contains("301"), 5000), $"Ctrl+Right: page '{p1}' -> '{PageText()}'");
        var first = CopyFirstRow();
        Check(first.StartsWith("301", StringComparison.Ordinal), "page 2 first row is '" + first + "'");
        U.Keys("Ctrl+Left");
        Check(WaitTrue(() => PageText() == p1, 5000), $"Ctrl+Left did not return to page 1 ('{PageText()}')");
    }

    void SortHeader()
    {
        var header = WaitFor(() => Visible(DataGrid(), Type(ControlType.HeaderItem)).FirstOrDefault(h => Name(h) == "id" || Name(h).StartsWith("id", StringComparison.Ordinal)), 3000)
                     ?? throw new FlowFailed("no 'id' header");
        Click(header);
        Check(WaitTrue(() => CopyFirstRow().StartsWith("5000", StringComparison.Ordinal), 5000), "first click did not sort DESC (first row '" + CopyFirstRow() + "')");
        Click(Visible(DataGrid(), Type(ControlType.HeaderItem)).First(h => Name(h).StartsWith("id", StringComparison.Ordinal)));
        Check(WaitTrue(() => CopyFirstRow().StartsWith("1\t", StringComparison.Ordinal), 5000), "second click did not sort ASC (first row '" + CopyFirstRow() + "')");
        Click(Visible(DataGrid(), Type(ControlType.HeaderItem)).First(h => Name(h).StartsWith("id", StringComparison.Ordinal)));
        Thread.Sleep(800);
    }

    void FilterApply()
    {
        CopyFirstRow();
        U.Keys("Ctrl+F");
        var bar = WaitFor(() => FirstVisible(Main, Id("FilterBar")), 3000) ?? throw new FlowFailed("Ctrl+F did not show the filter bar");
        Thread.Sleep(200);
        U.Type("42", 20);
        U.Keys("Enter");
        Check(WaitTrue(() => Name(FirstVisible(Main, Id("RowCountButton"))).StartsWith("1 row", StringComparison.Ordinal), 5000),
            "filter id = 42 did not give 1 row: '" + Name(FirstVisible(Main, Id("RowCountButton"))) + "'");
        Check(CopyFirstRow().StartsWith("42\t", StringComparison.Ordinal), "filtered row is not id 42");
        // Clear and hide.
        var clear = FirstVisible(Main, And(Type(ControlType.Button), Named("Clear")));
        if (clear != null) Click(clear);
        Check(WaitTrue(() => Name(FirstVisible(Main, Id("RowCountButton"))).Contains("5,000"), 5000), "Clear did not remove the filter");
    }

    void EditPreviewCommit()
    {
        using (var c = Open())
        {
            Exec(c, $"create table public.{_scratch}(id int primary key, name text); insert into public.{_scratch} values (1,'one'),(2,'two'),(3,'three')");
        }
        Activate(Main);
        U.Keys("Ctrl+R"); // refresh metadata so the new table is known
        Thread.Sleep(1500);
        U.Keys("Ctrl+P");
        WaitFor(Palette, 3000);
        U.Type(_scratch, 10);
        Check(WaitTrue(() => Palette() is { } p && PaletteRowTitles(p).FirstOrDefault() == _scratch, 5000), "palette does not list the scratch table");
        U.Keys("Enter");
        Check(WaitTrue(() => SelectedTabName() == _scratch, 5000), "scratch table did not open (selected '" + SelectedTabName() + "')");
        var row = WaitFor(() => GridRows().FirstOrDefault(r => Visible(r, Type(ControlType.Custom)).Count >= 2 || Visible(r, Type(ControlType.Text)).Count >= 2), 5000)
                  ?? throw new FlowFailed("scratch grid has no rows");
        var cells = Visible(row, Type(ControlType.Custom));
        if (cells.Count < 2) cells = Visible(row, Type(ControlType.Text));
        Click(cells[1], dbl: true);
        Thread.Sleep(300);
        U.Keys("Ctrl+A");
        U.Type("edited", 10);
        U.Keys("Enter");
        Thread.Sleep(300);
        U.Keys("Ctrl+Shift+P");
        var preview = WaitFor(() => OtherWindow(w => Name(w).Contains("Preview", StringComparison.OrdinalIgnoreCase) || Name(w).Contains("Changes", StringComparison.OrdinalIgnoreCase)), 3000)
                      ?? throw new FlowFailed("Ctrl+Shift+P did not open the preview");
        var text = string.Join("\n", All(preview, Type(ControlType.Edit)).Concat(All(preview, Type(ControlType.Document))).Select(Value)) + Name(preview);
        Check(text.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) && text.Contains("edited"), "preview does not show the UPDATE: '" + text + "'");
        U.Keys("Esc");
        Check(WaitTrue(() => OtherWindow(w => w == preview) == null, 2000), "preview did not close on Esc");
        Activate(Main);
        U.Keys("Ctrl+S");
        string? v = null;
        var ok = WaitTrue(() =>
        {
            using var c = Open();
            v = Scalar(c, $"select name from public.{_scratch} where id = 1") as string;
            return v == "edited";
        }, 5000);
        Check(ok, "Ctrl+S did not commit (db value '" + v + "')");
    }

    void Structure()
    {
        Activate(Main);
        CopyFirstRow();
        U.Keys("Ctrl+Alt+2");
        var radio = WaitFor(() => FirstVisible(Main, And(Type(ControlType.RadioButton), Named("Structure"))), 2000) ?? throw new FlowFailed("no Structure sub-tab");
        Check(WaitTrue(() => ((SelectionItemPattern)radio.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected, 3000), "Ctrl+Alt+2 did not select Structure");
        Check(WaitTrue(() => Visible(Main, Type(ControlType.DataItem)).Any(r => Visible(r, Named("name")).Count > 0) || Visible(Main, Named("name")).Count > 0, 5000),
            "structure view does not list the 'name' column");
        U.Keys("Ctrl+Alt+1");
    }

    void ConsoleToggle()
    {
        Activate(Main);
        var wasVisible = FirstVisible(Main, Id("ConsoleList")) != null;
        U.Keys("Ctrl+Alt+C");
        Check(WaitTrue(() => (FirstVisible(Main, Id("ConsoleList")) != null) != wasVisible, 2000), "Ctrl+Alt+C did not toggle the console");
        if (!wasVisible)
        {
            var list = FirstVisible(Main, Id("ConsoleList"))!;
            Check(WaitTrue(() => All(list, Type(ControlType.ListItem)).Count > 0, 2000), "console has no entries");
            U.Keys("Ctrl+Alt+C");
            Check(WaitTrue(() => FirstVisible(Main, Id("ConsoleList")) == null, 2000), "Ctrl+Alt+C did not hide the console");
        }
    }

    /// <summary>Width in pixels of the dark "ink" in the first text line of the visible editor.</summary>
    int EditorInkHeight()
    {
        var area = First(Editor(), Type(ControlType.Document)) ?? Editor();
        var r = area.Current.BoundingRectangle;
        using var bmp = Capture(new System.Windows.Rect(r.Left + 40, r.Top, Math.Min(400, r.Width - 40), Math.Min(200, r.Height)));
        var bg = bmp.GetPixel(bmp.Width - 1, bmp.Height - 1);
        int top = -1, bottom = -1;
        for (int y = 0; y < bmp.Height; y++)
        {
            bool ink = false;
            for (int x = 0; x < bmp.Width && !ink; x++)
            {
                var c = bmp.GetPixel(x, y);
                ink = Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) > 200;
            }
            if (ink) { if (top < 0) top = y; bottom = y; }
            else if (top >= 0 && y - bottom > 4) break;
        }
        return top < 0 ? 0 : bottom - top + 1;
    }

    void SaveSettingsViaEditor(string json)
    {
        Activate(Main);
        U.Keys("Ctrl+,");
        var text = WaitFor(() => FirstVisible(Main, Id("SettingsText")), 5000) ?? throw new FlowFailed("Ctrl+, did not open settings.json");
        Click(text);
        U.Keys("Ctrl+A Delete");
        U.Type(json, 2);
        U.Keys("Ctrl+S");
        Check(WaitTrue(() => Norm(File.ReadAllText(SettingsPath)).Contains("\"fontSize\"") == json.Contains("\"fontSize\"") && File.ReadAllText(SettingsPath).Replace(" ", "").Replace("\r", "").Replace("\n", "").Contains(json.Replace(" ", "").Replace("\n", "")), 3000),
            "Ctrl+S did not write settings.json: '" + File.ReadAllText(SettingsPath) + "' / editor '" + Value(text) + "'");
    }

    void SettingsFontLive()
    {
        // Measure an editor line, change editor.fontSize in the settings tab, check the same editor got bigger.
        NewSqlTab();
        SetEditorText("WHWHWHWHWH select");
        var sqlTabIndex = Tabs().FindIndex(IsSelected);
        var before = EditorInkHeight();
        SaveSettingsViaEditor("{\"theme\": {\"mode\": \"light\"}, \"editor\": {\"fontSize\": 26}}");
        Click(Tabs()[sqlTabIndex]);
        Thread.Sleep(500);
        var after = EditorInkHeight();
        Check(before > 0 && after > before * 1.6, $"editor ink height {before}px -> {after}px after fontSize 12 -> 26");
    }

    void ThemeDark()
    {
        var r = Main.Current.BoundingRectangle;
        var probe = new System.Windows.Rect(r.Left + r.Width / 2, r.Bottom - 60, 80, 20);
        var before = Brightness(probe);
        SaveSettingsViaEditor("{\"theme\": {\"mode\": \"dark\"}, \"editor\": {\"fontSize\": 26}}");
        Thread.Sleep(500);
        var after = Brightness(probe);
        Check(before > 150 && after < 90, $"window brightness {before:0} -> {after:0} after theme.mode dark");
    }

    void SwitchDatabase()
    {
        using (var c = Open())
        {
            Exec(c, $"drop database if exists {Db2} with (force)");
            Exec(c, $"create database {Db2}");
        }
        Activate(Main);
        U.Keys("Ctrl+K");
        var pal = WaitFor(Palette, 3000) ?? throw new FlowFailed("Ctrl+K did not open the database list");
        Check(WaitTrue(() => Visible(pal, Type(ControlType.ListItem)).Any(i => Name(i).Contains(Db2) || Visible(i, Named(Db2)).Count > 0), 5000),
            "database list does not show " + Db2);
        U.Type(Db2, 10);
        Thread.Sleep(400);
        U.Keys("Enter");
        WaitConnected(Db2);
        U.Keys("Ctrl+K");
        WaitFor(Palette, 3000);
        U.Type(_pg.Database!, 10);
        Thread.Sleep(400);
        // tableplsu_test is a prefix of tableplsu_test_2; anchor it.
        U.Keys("Ctrl+A");
        U.Type("^" + _pg.Database + "$", 10);
        Thread.Sleep(400);
        U.Keys("Enter");
        WaitConnected(_pg.Database!);
    }

    void OpenConnectionPalette()
    {
        Activate(Main);
        var before = U.MainWindows().Count;
        U.Keys("Ctrl+Shift+K");
        var pal = WaitFor(Palette, 3000) ?? throw new FlowFailed("Ctrl+Shift+K did not open the connection list");
        U.Type(ConnName, 10);
        Thread.Sleep(400);
        U.Keys("Enter");
        var w2 = WaitFor(() => U.MainWindows().Count > before ? U.MainWindows().FirstOrDefault(w => !Automation.Compare(w, Main)) : null, 8000)
                 ?? throw new FlowFailed("Enter did not open a new window");
        var ok = WaitTrue(() => Name(w2).Contains("/ " + _pg.Database) && Name(First(w2, Id("ConnectionStatus"))) == "connected", 15000);
        var title = Name(w2);
        ((WindowPattern)w2.GetCurrentPattern(WindowPattern.Pattern)).Close();
        Check(ok, "new window did not connect (title '" + title + "')");
        Check(WaitTrue(() => U.MainWindows().Count == before, 5000), "second window did not close");
    }

    void AutosaveSurvivesKill()
    {
        NewSqlTab();
        var marker = "autosave_" + Guid.NewGuid().ToString("N")[..8];
        SetEditorText("select '" + marker + "'");
        Thread.Sleep(1200); // > autosave debounce (500 ms)
        Kill();
        var editors = Path.Combine(_root, "local", "editors");
        var onDisk = Directory.Exists(editors) && Directory.EnumerateFiles(editors, "*.sql", SearchOption.AllDirectories).Any(f => File.ReadAllText(f).Contains(marker));
        Check(onDisk, "marker not autosaved to disk within 1.2 s");
        Launch();
        // The workspace reopens; find the tab holding the marker.
        Check(WaitTrue(() => Tabs().Count > 0, 10000), "workspace/tabs not restored after relaunch");
        bool found = false;
        for (int i = 0; i < Tabs().Count && !found; i++)
        {
            Click(Tabs()[i]);
            Thread.Sleep(300);
            found = Safe(() => EditorText().Contains(marker));
        }
        Check(found, "restored tabs do not contain the autosaved text");
    }
}
