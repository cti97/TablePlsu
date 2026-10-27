using System.Windows.Threading;
using TablePlsu.App.Features.SqlEditor;
using TablePlsu.App.Infrastructure;
using TablePlsu.App.Tests.Perf;
using TablePlsu.Core.Db;
using TablePlsu.Core.Sql;

namespace TablePlsu.App.Tests.SqlEditor;

/// <summary>Producer/consumer hand-off of streamed batches: coalescing, order, first rows and completion.</summary>
[Collection(AppServicesCollection.Name)] // touches the static UiDispatch
public class ResultBatchPumpTests
{
    private static readonly ResultColumn[] Cols = [new(0, "n", "integer", 23)];

    private static ResultBatch Rows(int statement, int from, int count) =>
        new(statement, new SqlStatement(0, 1, "x"), Cols, Enumerable.Range(from, count).Select(i => new object?[] { i }).ToList(), from + count, false, null);

    private static ResultBatch Final(int statement, int total) =>
        new(statement, new SqlStatement(0, 1, "x"), Cols, [], total, true, new StatementResult { Index = statement, Statement = new SqlStatement(0, 1, "x") });

    [Fact]
    public void Coalesces_a_burst_into_few_ordered_appends_and_completes_after_the_last_row()
    {
        PerfHarness.OnUiThread(async () =>
        {
            var applied = new List<(int Statement, int First, int Count)>();
            var finals = new List<int>();
            var appendCalls = 0;
            var pump = new ResultBatchPump(
                (b, rows) => { appendCalls++; applied.Add((b.StatementIndex, (int)rows[0][0]!, rows.Count)); },
                b => finals.Add(b.StatementIndex));

            // Producer: two statements of 50 batches x 200 rows each, as fast as possible from another thread.
            await Task.Run(() =>
            {
                for (var s = 0; s < 2; s++)
                {
                    for (var i = 0; i < 50; i++) pump.Add(Rows(s, i * 200, 200));
                    pump.Add(Final(s, 10_000));
                }
            });
            await pump.CompleteAsync();

            Assert.Equal([0, 1], finals);
            foreach (var s in new[] { 0, 1 })
            {
                var mine = applied.Where(a => a.Statement == s).ToList();
                Assert.Equal(10_000, mine.Sum(a => a.Count));
                var next = 0;
                foreach (var a in mine) { Assert.Equal(next, a.First); next += a.Count; } // in order, no gaps
                Assert.True(mine[0].Count <= 200 * 50);
            }
            // 100 batches, but far fewer UI appends (each AddRange takes up to MaxRowsPerAppend rows).
            Assert.True(appendCalls < 30, $"{appendCalls} appends for 100 batches");
            Assert.True(pump.FlushCount < 30, $"{pump.FlushCount} flushes for 100 batches");
        });
    }

    [Fact]
    public void First_batch_is_applied_without_waiting_for_the_interval()
    {
        var before = ResultBatchPump.Interval;
        ResultBatchPump.Interval = TimeSpan.FromSeconds(30);
        try
        {
            PerfHarness.OnUiThread(async () =>
            {
                var rows = 0;
                var pump = new ResultBatchPump((_, r) => rows += r.Count, _ => { });
                await Task.Run(() => pump.Add(Rows(0, 0, 200)));
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(200, rows);

                // Later batches wait for the interval, but a final batch flushes everything at once.
                await Task.Run(() => pump.Add(Rows(0, 200, 200)));
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(200, rows);
                await Task.Run(() => pump.Add(Final(0, 400)));
                await pump.CompleteAsync();
                Assert.Equal(400, rows);
            });
        }
        finally
        {
            ResultBatchPump.Interval = before;
        }
    }

    [Fact]
    public async Task Without_a_dispatcher_batches_apply_inline()
    {
        UiDispatch.Initialize(null!);
        var rows = 0;
        var finals = 0;
        var pump = new ResultBatchPump((_, r) => rows += r.Count, _ => finals++);
        pump.Add(Rows(0, 0, 200));
        Assert.Equal(200, rows);
        pump.Add(Rows(0, 200, 200));
        pump.Add(Final(0, 400));
        Assert.Equal(400, rows);
        Assert.Equal(1, finals);
        await pump.CompleteAsync();
    }
}
