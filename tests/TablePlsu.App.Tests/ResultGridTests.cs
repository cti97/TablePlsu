using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TablePlsu.App.Features.Results;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Db;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests;

public class CellFormatterTests
{
    private readonly CellFormatter _f = new("NULL", "yyyy-MM-dd HH:mm:ss", maxDisplayChars: 20);

    [Fact]
    public void Display_formats_nulls_dates_numbers_and_truncates()
    {
        Assert.Equal("NULL", _f.Display(null));
        Assert.Equal("NULL", _f.Display(DBNull.Value));
        Assert.Equal("2024-03-05 07:08:09", _f.Display(new DateTime(2024, 3, 5, 7, 8, 9)));
        Assert.Equal("2024-03-05", _f.Display(new DateTime(2024, 3, 5), "date"));
        Assert.Equal("2024-03-05", _f.Display(new DateOnly(2024, 3, 5)));
        Assert.Equal("1234.5", _f.Display(1234.5m));
        Assert.Equal("true", _f.Display(true));
        Assert.Equal("a↵b↵c d", _f.Display("a\r\nb\nc\td"));
        Assert.Equal(new string('x', 20) + "…", _f.Display(new string('x', 50)));
    }

    [Fact]
    public void Full_keeps_everything_and_uses_null_text()
    {
        Assert.Equal("", _f.Full(null));
        Assert.Equal("NULL", _f.Full(null, "NULL"));
        Assert.Equal("a\nb", _f.Full("a\nb"));
        Assert.Equal(new string('x', 50), _f.Full(new string('x', 50)));
    }

    [Fact]
    public void Converters_use_current_formatter()
    {
        Assert.Equal(CellFormatter.Current.NullDisplay, CellValueConverter.Instance.Convert(null, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("", CellEditConverter.Instance.Convert(null, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("2024-03-05", CellValueConverter.Instance.Convert(new DateTime(2024, 3, 5), typeof(string), "date", System.Globalization.CultureInfo.InvariantCulture));
    }
}

public class TsvTests
{
    private static readonly ResultColumn[] Cols =
    [
        new(0, "id", "integer", 23),
        new(1, "name", "text", 25),
        new(2, "note", "text", 25),
    ];

    [Fact]
    public void Formats_rows_with_header_and_escapes()
    {
        var rows = new List<object?[]> { new object?[] { 1, "a\tb", null }, new object?[] { 2, "say \"hi\"", "x\ny" } };
        var text = GridCopy.Build(Cols, [0, 1, 2], rows, null, withHeader: true, new CellFormatter());
        Assert.Equal("id\tname\tnote\r\n1\t\"a\tb\"\t\r\n2\t\"say \"\"hi\"\"\"\t\"x\ny\"", text);
    }

    [Fact]
    public void Column_order_include_filter_and_null_text()
    {
        var rows = new List<object?[]> { new object?[] { 1, "a", null }, new object?[] { 2, "b", "c" } };
        var text = GridCopy.Build(Cols, [2, 0], rows, (r, c) => !(r[0] is 2 && c == 0), withHeader: false, new CellFormatter(), "NULL");
        Assert.Equal("NULL\t1\r\nc\t", text);
    }

    [Fact]
    public void TsvFormatter_plain()
    {
        Assert.Equal("a\tb\r\nc\td", TsvFormatter.Format(null, [["a", "b"], ["c", "d"]]));
        Assert.Equal("h", TsvFormatter.Format(["h"], []));
    }
}

public class BulkObservableCollectionTests
{
    [Fact]
    public void AddRange_raises_item_adds_after_first_batch_and_reset_for_first()
    {
        var c = new BulkObservableCollection<int>();
        var events = new List<NotifyCollectionChangedEventArgs>();
        var counts = 0;
        c.CollectionChanged += (_, e) => { events.Add(e); Assert.Equal(c.Count, c.Count); };
        ((INotifyPropertyChanged)c).PropertyChanged += (_, e) => { if (e.PropertyName == "Count") counts++; };

        c.AddRange([1, 2, 3]);
        Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, events[0].Action);

        events.Clear();
        c.AddRange([4, 5]);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(NotifyCollectionChangedAction.Add, e.Action));
        Assert.Equal(3, events[0].NewStartingIndex);
        Assert.Equal(5, events[1].NewItems![0]);
        Assert.Equal(2, counts);

        events.Clear();
        c.AddRange([]);
        Assert.Empty(events);
        Assert.Equal([1, 2, 3, 4, 5], c);
    }

    [Fact]
    public void Reset_mode_and_ReplaceAll_raise_single_reset()
    {
        var c = new BulkObservableCollection<int>([1]) { Mode = BulkNotifyMode.Reset };
        var events = new List<NotifyCollectionChangedAction>();
        c.CollectionChanged += (_, e) => events.Add(e.Action);
        c.AddRange([2, 3]);
        c.ReplaceAll([9]);
        Assert.Equal([NotifyCollectionChangedAction.Reset, NotifyCollectionChangedAction.Reset], events);
        Assert.Equal([9], c);
    }
}

public class ResultGridUiTests(ITestOutputHelper output)
{
    /// <summary>Runs <paramref name="body"/> on a fresh STA thread with a dispatcher.</summary>
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

    private static void DoEvents() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static (Window Window, ResultGrid Grid) CreateWindow(int width = 1400, int height = 900)
    {
        var grid = new ResultGrid();
        var window = new Window
        {
            Width = width,
            Height = height,
            Left = -20000,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = grid,
        };
        ThemeApplier.ApplyTo(window.Resources, ThemeService.BuiltInDark);
        GridResources.ApplyTo(window.Resources, new GridSettings());
        window.Show();
        return (window, grid);
    }

    private static ResultColumn[] Columns(int n) =>
        Enumerable.Range(0, n).Select(i => new ResultColumn(i, "col_" + i, i % 3 == 0 ? "integer" : "text", 25)).ToArray();

    private static object?[] MakeRow(int r, int cols)
    {
        var row = new object?[cols];
        for (var c = 0; c < cols; c++)
            row[c] = c % 3 == 0 ? r * 31 + c : (r + c) % 17 == 0 ? null : $"value {r}-{c}";
        return row;
    }

    [Fact]
    public void Builds_columns_shows_rows_copies_and_sorts()
    {
        RunSta(() =>
        {
            var (window, grid) = CreateWindow(900, 500);
            var rows = new BulkObservableCollection<object?[]>();
            grid.ResultColumns = [new ResultColumn(0, "id", "integer", 23), new ResultColumn(1, "name", "text", 25)];
            grid.Rows = rows;
            rows.AddRange([[2, "b"], [1, null], [3, "c"]]);
            DoEvents();
            Assert.Equal(2, grid.DataGrid.Columns.Count);
            Assert.Equal(3, grid.DataGrid.Items.Count);

            // Select row 2 (index 1) cells and copy.
            grid.DataGrid.SelectedCells.Add(new DataGridCellInfo(rows[1], grid.DataGrid.Columns[1]));
            Assert.Equal("1\t", grid.GetRowsTsv(false));
            Assert.Equal("name\r\n", grid.GetSelectedCellsTsv(true));
            Assert.Equal([1], grid.GetSelectedRowIndices());
            Assert.Equal([(1, 1)], grid.GetSelectedCells());

            grid.DataGrid.SelectAllCells();
            Assert.Equal("id\tname\r\n2\tb\r\n1\t\r\n3\tc", grid.GetRowsTsv(true));

            // Client sort ascending on id: copy follows the on-screen order, indexes stay source indexes.
            var requested = -1;
            grid.SortRequested += (_, e) => requested = e.ColumnIndex;
            Click(grid, 0);
            Assert.Equal(0, requested);
            Assert.Equal(ListSortDirection.Ascending, grid.DataGrid.Columns[0].SortDirection);
            grid.DataGrid.SelectAllCells();
            Assert.Equal("1\t\r\n2\tb\r\n3\tc", grid.GetRowsTsv(false));
            Assert.Equal([1, 0, 2], grid.GetSelectedRowIndices());

            // Server sort: no reordering, only the event.
            grid.ServerSort = true;
            Click(grid, 1);
            Assert.Equal(1, requested);
            grid.SetSortIndicators([(1, ListSortDirection.Descending)]);
            Assert.Null(grid.DataGrid.Columns[0].SortDirection);
            Assert.Equal(ListSortDirection.Descending, grid.DataGrid.Columns[1].SortDirection);
            window.Close();
        });
    }

    private static void Click(ResultGrid grid, int column)
    {
        // DataGrid.Sorting is raised by the header click; invoke the same path.
        var method = typeof(DataGrid).GetMethod("OnSorting", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(grid.DataGrid, [new DataGridSortingEventArgs(grid.DataGrid.Columns[column])]);
    }

    [Fact]
    public void Editable_mode_reports_edits_and_applies_row_state()
    {
        RunSta(() =>
        {
            var (window, grid) = CreateWindow(900, 500);
            var rows = new BulkObservableCollection<object?[]>([[1, "a"], [2, "b"]]);
            var modified = new HashSet<object?[]>(ReferenceEqualityComparer.Instance);
            grid.ResultColumns = [new ResultColumn(0, "id", "integer", 23), new ResultColumn(1, "name", "text", 25)];
            grid.Rows = rows;
            grid.IsReadOnly = false;
            grid.RowStateProvider = new DelegateRowStateProvider(
                r => modified.Contains(r) ? RowState.Modified : RowState.Unchanged,
                (r, c) => modified.Contains(r) && c == 1);
            DoEvents();

            CellEditCommittedEventArgs? committed = null;
            grid.CellEditCommitted += (_, e) => { committed = e; modified.Add(e.Row); };
            grid.ScrollToRow(1, 1);
            DoEvents();
            Assert.True(grid.BeginEditCurrentCell());
            DoEvents();
            var cell = (DataGridCell)grid.DataGrid.Columns[1].GetCellContent(rows[1])!.Parent;
            var box = Assert.IsType<TextBox>(cell.Content);
            Assert.Equal("b", box.Text);
            box.Text = "bee";
            grid.DataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            DoEvents();

            Assert.NotNull(committed);
            Assert.Equal(1, committed.RowIndex);
            Assert.Equal(1, committed.ColumnIndex);
            Assert.Equal("b", committed.OldValue);
            Assert.Equal("bee", rows[1][1]);
            var container = (DataGridRow)grid.DataGrid.ItemContainerGenerator.ContainerFromItem(rows[1]);
            Assert.Equal(((SolidColorBrush)window.Resources["c.grid.pendingModifiedBackground"]).Color, ((SolidColorBrush)container.Background).Color);
            // Move selection away so the selection colour does not mask the modified-cell colour.
            grid.ScrollToRow(0, 0);
            DoEvents();
            cell = (DataGridCell)grid.DataGrid.Columns[1].GetCellContent(rows[1])!.Parent;
            Assert.Equal(((SolidColorBrush)window.Resources["c.grid.pendingModifiedBackground"]).Color, ((SolidColorBrush)cell.Background).Color);
            var other = (DataGridCell)grid.DataGrid.Columns[0].GetCellContent(rows[1])!.Parent;
            Assert.NotEqual(((SolidColorBrush)window.Resources["c.grid.pendingModifiedBackground"]).Color, (other.Background as SolidColorBrush)?.Color);

            grid.SetCellValue(rows[0], 1, null);
            DoEvents();
            var text = (CellText)grid.DataGrid.Columns[1].GetCellContent(rows[0])!;
            Assert.Equal("NULL", text.Text);
            Assert.True(text.IsNull);
            window.Close();
        });
    }

    [Fact]
    public void Perf_append_100k_x_30_and_scroll() => PerfCore(Environment.GetEnvironmentVariable("TABLEPLSU_PERF_ROWS") is { } n ? int.Parse(n) : 10_000); // set TABLEPLSU_PERF_ROWS=100000 for the full benchmark

    private void PerfCore(int total)
    {
        RunSta(() =>
        {
            const int cols = 30, batch = 200;
            var source = Enumerable.Range(0, total).Select(r => MakeRow(r, cols)).ToList();

            foreach (var mode in new[] { BulkNotifyMode.ItemAdds, BulkNotifyMode.Reset })
            {
                var (window, grid) = CreateWindow();
                var rows = new BulkObservableCollection<object?[]> { Mode = mode };
                grid.ResultColumns = Columns(cols);
                grid.Rows = rows;
                DoEvents();

                // Reset mode regenerates the visible rows on every batch: measure it on 4k rows only (it is not the default).
                var n = mode == BulkNotifyMode.Reset ? Math.Min(total, 4_000) : total;
                var sw = Stopwatch.StartNew();
                var worst = 0.0;
                double first300 = 0;
                for (var i = 0; i < n; i += batch)
                {
                    var t0 = sw.Elapsed.TotalMilliseconds;
                    rows.AddRange(source.GetRange(i, batch));
                    grid.DataGrid.UpdateLayout(); // a render pass per batch (worst case)
                    worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds - t0);
                    if (i + batch == 400) first300 = sw.Elapsed.TotalMilliseconds;
                }
                sw.Stop();
                output.WriteLine($"[{mode}] append {n:N0} rows x {cols} cols in {batch}-row batches: {sw.ElapsedMilliseconds} ms total, " +
                                 $"{sw.Elapsed.TotalMilliseconds / (n / batch):F2} ms/batch avg, worst batch {worst:F1} ms, first 400 rows {first300:F1} ms");
                Assert.Equal(n, grid.DataGrid.Items.Count);

                if (mode == BulkNotifyMode.ItemAdds)
                {
                    var sv = FindChild<ScrollViewer>(grid.DataGrid)!;
                    double Median(Action<int> step, int n)
                    {
                        var times = new List<double>();
                        for (var s = 0; s < n; s++)
                        {
                            var t0 = Stopwatch.GetTimestamp();
                            step(s);
                            grid.DataGrid.UpdateLayout();
                            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                        }
                        times.Sort();
                        return times[times.Count / 2];
                    }
                    var rnd = new Random(1);
                    var wheel = Median(_ => sv.ScrollToVerticalOffset(sv.VerticalOffset + 3), 200);
                    var hwheel = Median(s => sv.ScrollToHorizontalOffset(s % 40 < 20 ? sv.HorizontalOffset + 48 : sv.HorizontalOffset - 48), 80);
                    var page = Median(_ => sv.ScrollToVerticalOffset(sv.VerticalOffset + sv.ViewportHeight), 30);
                    var jump = Median(_ => sv.ScrollToVerticalOffset(rnd.Next(0, total)), 30);
                    output.WriteLine($"viewport {sv.ViewportHeight} rows, {CountOf<DataGridCell>(grid.DataGrid)} realized cells; layout per step, median: " +
                                     $"wheel (3 rows) {wheel:F1} ms, horizontal 48px {hwheel:F1} ms, page down {page:F1} ms, random jump {jump:F1} ms");
                    // Timing is only asserted in the explicit benchmark run: shared dev boxes run many test suites at once.
                    if (Environment.GetEnvironmentVariable("TABLEPLSU_PERF_ROWS") is not null) Assert.True(wheel < 50, "a wheel step should stay within a few frames");

                    var sel = Stopwatch.StartNew();
                    grid.DataGrid.SelectAllCells();
                    var selectMs = sel.ElapsedMilliseconds;
                    sel.Restart();
                    var tsv = grid.GetRowsTsv(true)!;
                    output.WriteLine($"select all {selectMs} ms, TSV of {total:N0} rows {sel.ElapsedMilliseconds} ms ({tsv.Length / 1024 / 1024} MB)");
                }
                window.Close();
            }
        });
    }

    private static int CountOf<T>(DependencyObject parent) where T : DependencyObject
    {
        var n = 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T) n++;
            n += CountOf<T>(child);
        }
        return n;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }
}
