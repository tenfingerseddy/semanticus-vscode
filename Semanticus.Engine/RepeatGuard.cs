using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// Keeps a page that re-asks the same question on its own (a render loop) from flooding a live model. Hero run 24
    /// measured about 3,500 explain queries in 13 minutes from one Studio card. The same request, while it is still
    /// running, joins the one already in flight; the same request again within <see cref="Window"/> of a good answer
    /// gets that answer back instead of a new query. A failed run is never remembered, so the next press tries again.
    /// </summary>
    public sealed class RepeatGuard
    {
        public TimeSpan Window { get; }
        private readonly Func<DateTime> _now;
        private readonly ConcurrentDictionary<string, Entry> _runs = new();

        private sealed class Entry { public Task Task; public DateTime? DoneAt; }

        public RepeatGuard(TimeSpan window, Func<DateTime> now = null) { Window = window; _now = now ?? (() => DateTime.UtcNow); }

        public async Task<T> RunAsync<T>(string key, Func<Task<T>> work, Func<T, bool> remember = null)
        {
            while (true)
            {
                var now = _now();
                if (_runs.TryGetValue(key, out var e))
                {
                    var stale = e.DoneAt is DateTime d && now - d > Window;
                    if (!stale)
                    {
                        try { return await (Task<T>)e.Task; }
                        catch { /* the run this one joined failed: fall through and run it again */ }
                        _runs.TryRemove(new System.Collections.Generic.KeyValuePair<string, Entry>(key, e));
                        continue;
                    }
                    _runs.TryRemove(new System.Collections.Generic.KeyValuePair<string, Entry>(key, e));
                }
                var mine = new Entry();
                var task = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                mine.Task = task.Task;
                if (!_runs.TryAdd(key, mine)) continue;   // another caller registered first: join theirs
                try
                {
                    var r = await work().ConfigureAwait(false);
                    mine.DoneAt = _now();
                    task.SetResult(r);
                    // An answer that says it failed is shared with the callers already waiting, then forgotten: the next press tries again.
                    if (remember != null && !remember(r)) _runs.TryRemove(new System.Collections.Generic.KeyValuePair<string, Entry>(key, mine));
                    Trim();
                    return r;
                }
                catch (Exception ex)
                {
                    _runs.TryRemove(new System.Collections.Generic.KeyValuePair<string, Entry>(key, mine));
                    task.SetException(ex);
                    _ = task.Task.Exception;   // observed: joiners rethrow it themselves
                    throw;
                }
            }
        }

        // Keeps the table small: an answer older than the window can never be served again.
        private void Trim()
        {
            if (_runs.Count < 64) return;
            var now = _now();
            foreach (var kv in _runs)
                if (kv.Value.DoneAt is DateTime d && now - d > Window) _runs.TryRemove(kv);
        }
    }
}
