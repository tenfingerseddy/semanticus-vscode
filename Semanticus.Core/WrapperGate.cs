using System;
using System.Threading;
using TabularEditor.TOMWrapper;

namespace TabularEditor.TOMWrapper.Utils
{
    /// <summary>
    /// The one gate around the vendored wrapper's process-wide <c>TabularModelHandler.Singleton</c>. Every
    /// wrapper object a model creates (a measure, a column, a table, a collection) reads that static in its
    /// constructor to learn which handler it belongs to, and the handler's own constructor SETS it. With one open
    /// model that was harmless. With a second model alive (a hidden scratch model a start plan builds on its own
    /// dispatcher, or a replacement session built before the old one retires) the two dispatcher threads race:
    /// an object made on model A binds to model B's handler, and lands on B's undo stack and B's lookup table.
    ///
    /// The gate makes the static safe without touching the vendored tree: it is a process-wide re-entrant lock,
    /// and whoever holds it has pinned the Singleton to the handler it is working on. Every dispatcher work item
    /// of a session runs as "gate, pin own handler, work", and a handler is CONSTRUCTED inside the gate too
    /// (<see cref="Construct{T}"/>), so a third model coming to life can never move the pin under a work item
    /// that is paused mid-way: it waits for the item to finish.
    ///
    /// Cost, stated honestly: two models' work items now run one at a time, never in parallel. They were never
    /// really parallel (TOM and the wrapper are not thread-safe across models that share the static), and a
    /// scratch build is a stream of short items, so the person's model waits at most one item.
    ///
    /// Deadlock rule: a work item that holds the gate must never wait, synchronously, on another dispatcher.
    /// Waiting on its own thread is fine (the lock is re-entrant). The engine has no such wait; WrapperGateTests
    /// and the verifier's search for .Result / .Wait() / GetResult inside dispatcher lambdas keep it that way.
    /// </summary>
    public static class WrapperGate
    {
        private static readonly object Gate = new object();
        [ThreadStatic] private static int _depth;
        private static int _waiting;

        /// <summary>How many threads are blocked at the gate right now. A test reads it to know a construction on
        /// another thread has reached the gate and is waiting, instead of guessing with a sleep.</summary>
        public static int WaitingCount => Volatile.Read(ref _waiting);

        /// <summary>Which handler a wrapper object belongs to (its constructor read it from the Singleton). The
        /// field is protected-internal in the vendored wrapper, so the seam names it for callers outside Core.</summary>
        public static TabularModelHandler HandlerOf(TabularObject obj) => obj?.Handler;

        /// <summary>Enter the gate and pin the Singleton to <paramref name="handler"/> until the scope is disposed.
        /// Re-entrant on one thread; a nested pin puts the outer pin back when it ends.</summary>
        public static Scope Pin(TabularModelHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var s = Enter();
            TabularModelHandler.Singleton = handler;
            return s;
        }

        /// <summary>Run <paramref name="work"/> with the Singleton pinned to <paramref name="handler"/>.</summary>
        public static T Run<T>(TabularModelHandler handler, Func<T> work)
        {
            using (Pin(handler)) return work();
        }

        public static void Run(TabularModelHandler handler, Action work)
        {
            using (Pin(handler)) work();
        }

        /// <summary>Build a handler (or anything that constructs one) inside the gate. The handler's constructor sets
        /// the Singleton to itself; when this runs nested inside another model's work item, that item's pin is put
        /// back afterwards, so the item carries on bound to its own model.</summary>
        public static T Construct<T>(Func<T> build)
        {
            using (Enter()) return build();
        }

        private static Scope Enter()
        {
            if (!Monitor.TryEnter(Gate))
            {
                Interlocked.Increment(ref _waiting);
                try { Monitor.Enter(Gate); }
                finally { Interlocked.Decrement(ref _waiting); }
            }
            var outer = _depth++ > 0 ? TabularModelHandler.Singleton : null;
            return new Scope(outer);
        }

        /// <summary>The held gate. Dispose leaves it.</summary>
        public readonly struct Scope : IDisposable
        {
            private readonly TabularModelHandler _outer;
            internal Scope(TabularModelHandler outer) { _outer = outer; }

            public void Dispose()
            {
                // An outermost exit leaves the Singleton where the last pin put it: nothing outside the gate may
                // rely on it, and the next work item pins its own. A nested exit gives the outer item its pin back.
                if (_outer != null) TabularModelHandler.Singleton = _outer;
                _depth--;
                Monitor.Exit(Gate);
            }
        }
    }
}
