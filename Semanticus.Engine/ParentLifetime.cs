using System;
using System.Threading;

namespace Semanticus.Engine
{
    /// <summary>
    /// The whole lifetime rule for one `serve` process: who this engine dies with, the GRACE that lets a
    /// reloading editor catch it, and adoption.
    ///
    /// WHY THE GRACE EXISTS. Tying the engine straight to its parent (see <see cref="ParentWatchdog"/>) fixes
    /// the orphans but breaks a promise: reloading a VS Code window restarts the extension host, so the
    /// parent dies and an engine that exits at once takes the unsaved in-memory model with it. C4.1 says a
    /// window reload must not throw work away in silence
    /// (Semanticus.VSCode/test/unsaved-work-prompt.test.mjs). So a dead parent does not end the engine, it
    /// starts a clock. A window that comes back inside the grace reconnects to the SAME engine and calls
    /// adoptParent, the watch switches to the new extension host, and the session survives. A window that
    /// does not come back leaves, which is the orphan fix.
    ///
    /// engine.json is deliberately LEFT PUBLISHED during the grace. Clearing it would make the returning
    /// window believe no engine is running, spawn a second one, and have that second one refuse the
    /// workspace lock the first still holds. The record is cleared when the grace expires.
    /// </summary>
    public sealed class ParentLifetime : IDisposable
    {
        /// <summary>Long enough for a full window reload (the extension host restart plus activation) and
        /// short enough that a closed editor's engine is gone before anyone notices it.</summary>
        public const int DefaultGraceMs = 20_000;

        /// <summary>Test hook, read once at construction. It can only SHORTEN the grace and never removes
        /// the watch, so the worst a stray value does in production is make the engine leave sooner. Nothing
        /// we ship sets it; the process tests do, because a real twenty-second wait per case is not a test.
        /// </summary>
        public const string GraceEnvironmentVariable = "SEMANTICUS_PARENT_GRACE_MS";

        private readonly object _gate = new object();
        private readonly Action _onExpired;
        private readonly Action<int> _onAdopted;     // re-stamp the published record; null when nobody cares
        private readonly TimeSpan _poll;
        private readonly Func<int, bool> _isAlive;   // test seam; null means the platform probe
        private readonly Func<bool> _holdOpen;       // true = do not leave yet; null means never held
        private IDisposable _watch;
        private CancellationTokenSource _graceStop;
        private int _generation;
        private int? _pid;
        private bool _expired;
        private bool _disposed;

        public ParentLifetime(
            int? parentPid,
            Action onExpired,
            TimeSpan? grace = null,
            TimeSpan? poll = null,
            Func<int, bool> isAlive = null,
            Action<int> onAdopted = null,
            Func<bool> holdOpen = null)
        {
            _onExpired = onExpired ?? throw new ArgumentNullException(nameof(onExpired));
            _onAdopted = onAdopted;
            Grace = grace ?? ResolveGrace();
            _poll = poll ?? ParentWatchdog.DefaultPoll;
            _isAlive = isAlive;
            _holdOpen = holdOpen;
            _pid = parentPid;
            if (parentPid.HasValue) _watch = StartWatch(parentPid.Value);
        }

        /// <summary>Asks the hold, treating a failing probe as "hold": leaving loses work, staying costs memory.</summary>
        private bool Held()
        {
            if (_holdOpen == null) return false;
            try { return _holdOpen(); } catch { return true; }
        }

        /// <summary>How long a dead parent's engine waits for a window to come back.</summary>
        public TimeSpan Grace { get; }

        /// <summary>The process this engine currently dies with, or null when it watches nobody.</summary>
        public int? WatchedPid { get { lock (_gate) return _pid; } }

        /// <summary>True between the parent dying and the grace running out.</summary>
        public bool InGrace { get { lock (_gate) return _graceStop != null; } }

        /// <summary>True once the grace ran out and the shutdown was signalled. Terminal.</summary>
        public bool Expired { get { lock (_gate) return _expired; } }

        /// <summary>A reattaching VS Code window claims this engine: the watch switches to <paramref name="pid"/>
        /// and any pending shutdown is cancelled. Refuses a pid that is not a live, watchable process, so a
        /// stale or mistaken claim cannot make an engine immortal by pointing it at init. Refuses once the
        /// grace has already expired, because that shutdown is under way and cannot be taken back.</summary>
        public bool Adopt(int pid)
        {
            if (!ParentWatchdog.IsWatchable(pid)) return false;
            if (!(_isAlive ?? ParentWatchdog.IsAlive)(pid)) return false;
            lock (_gate)
            {
                if (_disposed || _expired) return false;
                CancelGrace();
                _watch?.Dispose();
                _pid = pid;
                _watch = StartWatch(pid);
            }
            // Outside the gate: the callback republishes engine.json, which is file I/O and must not be held
            // against the watch. A reader has to see the NEW parent, or it will judge this engine by a window
            // that has already gone and stop a session that is very much alive.
            try { _onAdopted?.Invoke(pid); } catch { /* an unstamped record reads as older, which is safe */ }
            return true;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                CancelGrace();
                try { _watch?.Dispose(); } catch { }
                _watch = null;
            }
        }

        private static TimeSpan ResolveGrace()
        {
            var raw = Environment.GetEnvironmentVariable(GraceEnvironmentVariable);
            if (int.TryParse(raw, out var ms) && ms >= 100 && ms <= DefaultGraceMs)
                return TimeSpan.FromMilliseconds(ms);
            return TimeSpan.FromMilliseconds(DefaultGraceMs);
        }

        /// <summary>Caller holds the gate (or is the constructor). The generation stamp is what stops a watch
        /// that was replaced mid-callback from starting a grace for a parent nobody is watching any more.</summary>
        private IDisposable StartWatch(int pid)
        {
            var generation = ++_generation;
            return ParentWatchdog.Watch(pid, () => ParentGone(pid, generation), _poll, _isAlive);
        }

        /// <summary>Wake the grace thread. It does NOT dispose the source: the grace thread owns disposal
        /// and is the only code allowed to do it. Disposing here is what crashed CI on PR 386 — the grace
        /// thread then read <c>token.WaitHandle</c> on a disposed source, and an ObjectDisposedException on
        /// a background thread ends the whole process (the test host, on the runner). Caller holds the gate.</summary>
        private void CancelGrace()
        {
            if (_graceStop == null) return;
            try { _graceStop.Cancel(); } catch { }   // already disposed by its own thread: nothing left to wake
            _graceStop = null;
        }

        private void ParentGone(int pid, int generation)
        {
            CancellationTokenSource mine;
            lock (_gate)
            {
                if (_disposed || _expired || generation != _generation) return;
                if (_graceStop != null) return;   // already counting down
                mine = _graceStop = new CancellationTokenSource();
            }
            Console.Error.WriteLine(
                $"[engine] parent process {pid} exited; holding this session for {Grace.TotalSeconds:0}s in case the editor is reloading.");
            new Thread(() =>
            {
                try
                {
                    // This thread OWNS `mine`: it is the only code that touches the token after the source
                    // is published, and the only code that disposes it, in the finally below. Everyone else
                    // may Cancel and drop the reference. Without that ownership rule, Dispose racing this
                    // wait throws ObjectDisposedException out of a background thread.
                    // The grace only counts while nothing holds the owner open. A hold (an owner the assistant
                    // started, with unsaved changes or a client attached) pauses the countdown; once it clears the
                    // whole grace runs again, so a connection that is reconnecting gets its full window.
                    var announced = false;
                    while (true)
                    {
                        if (mine.Token.WaitHandle.WaitOne(Grace)) return;   // adopted, or the lifetime went away
                        if (!Held()) break;
                        if (!announced)
                        {
                            Console.Error.WriteLine("[engine] staying up: this session has unsaved changes or a connection attached.");
                            announced = true;
                        }
                        while (Held())
                            if (mine.Token.WaitHandle.WaitOne(_poll)) return;
                    }
                    var expire = false;
                    lock (_gate)
                    {
                        if (!_disposed && !_expired && ReferenceEquals(_graceStop, mine))
                        {
                            _expired = true;
                            expire = true;
                        }
                    }
                    if (expire) _onExpired();
                }
                catch (Exception ex)
                {
                    // A background thread in an engine host must never surface an unhandled exception: that
                    // ends the process instead of ending the watch. One line, then leave.
                    Console.Error.WriteLine("[engine] the parent grace stopped after an error: " + ex.Message);
                }
                finally
                {
                    lock (_gate) { if (ReferenceEquals(_graceStop, mine)) _graceStop = null; }
                    try { mine.Dispose(); } catch { }
                }
            })
            { IsBackground = true, Name = "semanticus-parent-grace" }.Start();
        }
    }
}
