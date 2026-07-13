using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// Single-writer thread that exclusively owns the TOM model. Every read and every mutation —
    /// whether it arrives from the RPC (UI) door or the MCP (Claude) door — is funnelled through
    /// here, so TOM (which is not thread-safe) is only ever touched by one thread, and operations
    /// are applied in a total order. This is the literal mechanism behind "one shared live session".
    /// </summary>
    public sealed class ModelDispatcher : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>(new ConcurrentQueue<Action>());
        private readonly Thread _thread;
        // Dispose bookkeeping (Interlocked flags, not a lock — Dispose can be re-entered from the worker itself):
        // completion-requested and collection-disposed are SEPARATE states, because an abandoned (timed-out) dispose
        // requests completion but deliberately never disposes the collection (see Dispose).
        private int _completeRequested;
        private int _queueDisposed;

        public ModelDispatcher()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "semanticus-model"
            };
            _thread.Start();
        }

        /// <summary>True when the caller is already executing on the dispatcher thread.</summary>
        private bool OnDispatcherThread => Thread.CurrentThread == _thread;

        public Task<T> RunAsync<T>(Func<T> work)
        {
            // Re-entrancy: if we're already on the dispatcher thread, run inline to avoid deadlock.
            if (OnDispatcherThread)
            {
                try { return Task.FromResult(work()); }
                catch (Exception ex) { return Task.FromException<T>(ex); }
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _queue.Add(() =>
                {
                    try { tcs.SetResult(work()); }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
            }
            catch (Exception ex)
            {
                // Enqueue raced Dispose (adding completed / collection disposed). Async-consistent fail-closed:
                // the caller awaits a FAULTED task like any other failed op — never a synchronous throw from
                // what looks like a plain enqueue (a sync throw here escaped into the session-swap path).
                tcs.TrySetException(new InvalidOperationException(
                    "This session's model dispatcher has been stopped (the session was closed or replaced) — the operation did not run. Retry against the current session (re-open the model if none is open).", ex));
            }
            return tcs.Task;
        }

        public Task RunAsync(Action work) => RunAsync(() => { work(); return true; });

        private void Run()
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                try { item(); }
                catch { /* per-item exceptions are already routed to their TCS */ }
            }
        }

        public void Dispose()
        {
            // Idempotent by design: a second Dispose after a successful one used to call CompleteAdding on the
            // already-disposed collection → ObjectDisposedException escaping into the session-swap path. Only the
            // FIRST caller issues CompleteAdding; the collection is disposed at most once, and only after the
            // worker provably stopped.
            if (Interlocked.CompareExchange(ref _completeRequested, 1, 0) == 0)
            {
                try { _queue.CompleteAdding(); } catch { /* raced a concurrent teardown — completion is the intent either way */ }
            }
            // Never join the dispatcher thread from itself (a re-entrant dispose from inside a queued action), and
            // never dispose the collection out from under the running foreach — CompleteAdding lets the worker
            // unwind on its own once the current action returns. A later EXTERNAL dispose still joins + reclaims.
            if (Thread.CurrentThread == _thread) return;

            // Join the worker so it has fully drained/stopped before we return — otherwise a re-open
            // (SessionManager.OpenAsync disposes the old dispatcher then builds a new handler + the
            // process-wide Singleton) could leave the old thread still touching the old model.
            bool stopped = false;
            try { stopped = _thread.Join(TimeSpan.FromSeconds(5)); } catch { /* best-effort */ }
            if (!stopped)
            {
                // The worker is still inside a long model op. Disposing the BlockingCollection now would yank the
                // queue out from under it → ObjectDisposedException in the MIDDLE of a model operation. A bounded,
                // one-per-wedged-thread leak (GC reclaims it once the wedged thread finally exits) is strictly
                // better than crashing an in-flight model op. Leave it abandoned and say so; a later Dispose may
                // still reclaim it once the thread has unwedged (the flags make that retry safe).
                try { Console.Error.WriteLine("[dispatcher] worker did not stop within 5s — abandoning (not disposing) the queue to avoid a use-after-dispose in a running model op."); } catch { }
                return;
            }
            if (Interlocked.CompareExchange(ref _queueDisposed, 1, 0) == 0)
            {
                try { _queue.Dispose(); } catch { }
            }
        }
    }
}
