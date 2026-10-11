// Stop this engine only while it is clean, as ONE step (F-244, Sol 6's slice 5 review).
//
// A window that finds an engine from a different build replaces it, but never while it holds unsaved work. The
// window used to ask one question ("any unsaved changes?") and then kill whatever pid engine.json named a moment
// later. Two things could change in between: an edit could land in the checked engine, and another window could
// replace the checked engine with a new one holding unsaved work of its own, whose pid the fresh read then named.
//
// This closes both. The window asks the engine it CHECKED, over the connection it checked it on, to stop itself.
// The engine answers on its own dispatcher, the one thread every edit runs on: if it is clean it says so and then
// keeps that thread, so no edit queued behind the answer can ever run, and it leaves shortly after. If it is not
// clean it refuses and nothing happens. No pid is read from a record and nothing is killed from outside.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>The engine's answer to a stop-if-clean request.</summary>
    public sealed class StopIfCleanResult
    {
        /// <summary>True when the engine was clean and is now leaving. Nothing can change it after this answer.</summary>
        public bool Stopping { get; set; }
        /// <summary>This engine's process id, so the caller waits for exactly this process to go.</summary>
        public int Pid { get; set; }
        /// <summary>Why it refused, in plain words. Null when it is stopping.</summary>
        public string Reason { get; set; }
    }

    public sealed class CleanStop : IDisposable
    {
        private readonly SessionManager _sessions;
        private readonly Action _stop;
        // Holds the dispatcher once a clean answer is given. Never set in production: the process exits holding it.
        // A test releases it through Dispose so its session can be torn down.
        private readonly ManualResetEventSlim _hold = new ManualResetEventSlim(false);
        private int _stopping;

        /// <param name="stop">What leaving means for this host. Called at most once, after the clean answer is
        /// settled and while the dispatcher is held.</param>
        internal CleanStop(SessionManager sessions, Action stop)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _stop = stop ?? throw new ArgumentNullException(nameof(stop));
        }

        internal bool Stopping => Volatile.Read(ref _stopping) == 1;

        internal async Task<StopIfCleanResult> TryStopAsync()
        {
            var pid = Environment.ProcessId;
            var s = _sessions.Current;
            if (s == null)
            {
                // Nothing is open, so there is nothing to lose.
                if (Begin()) { try { _stop(); } catch { } }
                return new StopIfCleanResult { Stopping = true, Pid = pid };
            }

            // The answer is settled INSIDE the dispatcher item, and on a clean answer the item never returns: the
            // thread every edit runs on stays here until the process is gone. So "clean" cannot go stale between
            // this answer and the exit, whoever tries to edit in between.
            var decided = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = s.RunAsync(() =>
            {
                if (s.HasUnsavedChanges || !Begin()) { decided.TrySetResult(false); return false; }
                decided.TrySetResult(true);
                try { _stop(); } catch { /* leaving is best effort; the hold below still keeps every edit out */ }
                _hold.Wait();
                return true;
            });
            // A dispatcher that faulted or was disposed before the item ran settles as "not stopping".
            _ = held.ContinueWith(t => decided.TrySetResult(false), TaskScheduler.Default);

            if (await decided.Task.ConfigureAwait(false))
                return new StopIfCleanResult { Stopping = true, Pid = pid };
            return Stopping ? AlreadyStopping(pid) : new StopIfCleanResult
            {
                Stopping = false,
                Pid = pid,
                Reason = "this engine has unsaved changes, so it keeps running",
            };
        }

        private bool Begin() => Interlocked.CompareExchange(ref _stopping, 1, 0) == 0;

        private static StopIfCleanResult AlreadyStopping(int pid) =>
            new StopIfCleanResult { Stopping = true, Pid = pid };

        public void Dispose() => _hold.Set();
    }
}
