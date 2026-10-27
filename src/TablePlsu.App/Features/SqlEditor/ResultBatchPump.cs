using System.Diagnostics;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Db;

namespace TablePlsu.App.Features.SqlEditor;

/// <summary>
/// Hands streamed <see cref="ResultBatch"/>es from the query thread to the UI thread without one UI update per batch.
/// The query thread only appends to a buffer (<see cref="Add"/>); the UI thread drains everything buffered so far in one
/// dispatcher operation, appending all rows of a statement with one <c>AddRange</c>, at most once per
/// <see cref="Interval"/>. The first batch of each statement and every final batch are flushed at once (first rows show
/// immediately, completion is never delayed). A flush that runs longer than <see cref="SliceBudget"/> yields and continues
/// in a new operation, so input and rendering keep running while a large backlog is appended.
/// </summary>
internal sealed class ResultBatchPump
{
    /// <summary>Minimum time between two coalesced flushes (one layout/render pass per flush).</summary>
    internal static TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    /// <summary>UI time one flush operation may use before it yields to input/render and continues.</summary>
    internal static TimeSpan SliceBudget = TimeSpan.FromMilliseconds(20);

    /// <summary>Rows appended per <c>AddRange</c> (the slice granularity).</summary>
    internal const int MaxRowsPerAppend = 2000;

    /// <summary>false = the old behaviour (one UI operation per batch). Only for A/B perf measurements.</summary>
    internal static bool Coalesce = true;

    private readonly Action<ResultBatch, IReadOnlyList<object?[]>> _applyRows;
    private readonly Action<ResultBatch> _applyFinal;
    private readonly Action _flush;
    private readonly object _gate = new();
    private readonly List<ResultBatch> _pending = new(64);   // producer → UI, under _gate
    private readonly List<ResultBatch> _work = new(64);      // UI thread only
    private readonly List<object?[]> _rows = new(MaxRowsPerAppend + 256); // UI thread scratch, reused
    private int _workPos;
    private int _backlog;           // batches taken by the UI but not applied yet (under _gate)
    private bool _flushPosted;      // a flush operation is queued or running
    private bool _delayScheduled;
    private bool _producerDone;
    private bool _urgent;           // a pending batch must not wait for the interval (first rows / final)
    private long _lastFlush;
    private int _lastStatement = -1; // producer thread only
    private TaskCompletionSource? _drained;

    /// <param name="applyRows">UI thread: append rows of the batch's statement (the list is reused after the call).</param>
    /// <param name="applyFinal">UI thread: a statement's final batch.</param>
    public ResultBatchPump(Action<ResultBatch, IReadOnlyList<object?[]>> applyRows, Action<ResultBatch> applyFinal)
    {
        _applyRows = applyRows;
        _applyFinal = applyFinal;
        _flush = Flush;
    }

    /// <summary>Number of UI flush operations run (diagnostics/tests).</summary>
    public int FlushCount { get; private set; }

    /// <summary>Query thread: buffers <paramref name="batch"/> and makes sure a flush will run.</summary>
    public void Add(ResultBatch batch)
    {
        if (!Coalesce)
        {
            UiDispatch.PostBackground(() => ApplyOne(batch));
            return;
        }
        var first = batch.StatementIndex != _lastStatement;
        _lastStatement = batch.StatementIndex;
        TimeSpan delay;
        lock (_gate)
        {
            _pending.Add(batch);
            if (first || batch.IsFinal) _urgent = true;
            if (_flushPosted) return;
            var since = Stopwatch.GetElapsedTime(_lastFlush);
            if (first || batch.IsFinal || since >= Interval || UiDispatch.Dispatcher is null)
            {
                _flushPosted = true;
                delay = TimeSpan.Zero;
            }
            else if (!_delayScheduled)
            {
                _delayScheduled = true;
                delay = Clamp(Interval - since);
            }
            else
            {
                return;
            }
        }
        if (delay == TimeSpan.Zero) UiDispatch.PostBackground(_flush);
        else PostAfter(delay).Forget();
    }

    /// <summary>The producer is done: completes once everything buffered has been applied on the UI thread.</summary>
    public Task CompleteAsync()
    {
        lock (_gate)
        {
            _producerDone = true;
            if (!Coalesce || IsIdle) return Task.CompletedTask;
            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    private bool IsIdle => _pending.Count == 0 && _backlog == 0 && !_flushPosted;

    private static TimeSpan Clamp(TimeSpan delay) => delay < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : delay;

    private async Task PostAfter(TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        lock (_gate)
        {
            _delayScheduled = false;
            if (_flushPosted || _pending.Count == 0) return;
            _flushPosted = true;
        }
        UiDispatch.PostBackground(_flush);
    }

    private void ApplyOne(ResultBatch batch)
    {
        FlushCount++;
        if (batch.IsFinal) _applyFinal(batch);
        else if (batch.Rows.Count > 0) _applyRows(batch, batch.Rows);
    }

    private void Flush()
    {
        FlushCount++;
        var started = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _work.AddRange(_pending);
            _pending.Clear();
            _urgent = false;
            _lastFlush = started;
        }

        while (_workPos < _work.Count)
        {
            var batch = _work[_workPos];
            if (batch.IsFinal)
            {
                _work[_workPos++] = null!;
                _applyFinal(batch);
            }
            else
            {
                _rows.Clear();
                while (_workPos < _work.Count && _rows.Count < MaxRowsPerAppend
                       && _work[_workPos] is { IsFinal: false } next && next.StatementIndex == batch.StatementIndex)
                {
                    _rows.AddRange(next.Rows);
                    _work[_workPos++] = null!;
                }
                if (_rows.Count > 0) _applyRows(batch, _rows);
                _rows.Clear();
            }
            if (_workPos < _work.Count && Stopwatch.GetElapsedTime(started) > SliceBudget) break;
        }

        if (_workPos == _work.Count)
        {
            _work.Clear();
            _workPos = 0;
        }

        TaskCompletionSource? drained = null;
        var again = false;
        lock (_gate)
        {
            _backlog = _work.Count - _workPos;
            if (_backlog > 0)
            {
                again = true; // stay posted: newer batches wait for the backlog
            }
            else if (_pending.Count > 0 && (_urgent || Stopwatch.GetElapsedTime(_lastFlush) >= Interval))
            {
                again = true;
            }
            else
            {
                _flushPosted = false;
                if (_pending.Count > 0 && !_delayScheduled)
                {
                    // Batches arrived while this flush ran: pick them up after the interval.
                    _delayScheduled = true;
                    PostAfter(Clamp(Interval - Stopwatch.GetElapsedTime(_lastFlush))).Forget();
                }
                if (_producerDone && IsIdle) { drained = _drained; _drained = null; }
            }
        }
        if (again) UiDispatch.PostBackground(_flush);
        drained?.TrySetResult();
    }
}
