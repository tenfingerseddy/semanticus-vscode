using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Semanticus.Engine
{
    /// <summary>
    /// Ambient identity of the pipe connection a request arrived on (F-246). <see cref="RpcServer"/> sets it around
    /// every dispatched request, and <c>Session.TrackAsync</c> reads it on the caller's thread and stamps it onto the
    /// commit, the same capture pattern as <see cref="HealthCorrelation"/>. Null outside a pipe request.
    /// </summary>
    internal static class ChangeWriter
    {
        private static readonly AsyncLocal<string> Ambient = new AsyncLocal<string>();

        public static string Current => Ambient.Value;

        public static Scope Begin(string id)
        {
            var prev = Ambient.Value;
            Ambient.Value = id;
            return new Scope(prev);
        }

        public readonly struct Scope : IDisposable
        {
            private readonly string _prev;
            internal Scope(string prev) { _prev = prev; }
            public void Dispose() => Ambient.Value = _prev;
        }
    }

    /// <summary>
    /// The user-edit fold (F-246): the short memory of recent commits that lets each assistant hear, on its next
    /// tool call, about the commits somebody ELSE made since its previous one. It rides the one
    /// <see cref="ChangeBus"/> every commit already goes through; there is no push, so nothing changes on the MCP
    /// wire except one terse line appended by <see cref="McpHealthAppender"/>.
    ///
    /// A READER is one assistant: <see cref="InProcessReader"/> for the MCP door hosted in this process, and one
    /// reader per agent pipe connection (an attached MCP process). Each reader has a cursor; <see cref="Take"/>
    /// returns what landed after it, minus the reader's own commits, and moves the cursor to the head. A reader's
    /// own commits are known by the writer stamp: the in-process door stamps its tool calls through the health
    /// correlation id, and a pipe connection stamps everything it sends through <see cref="ChangeWriter"/>.
    /// Thread-safe (records arrive on the dispatcher thread, takes on MCP or pipe threads); never throws into a commit.
    /// </summary>
    public sealed class ChangeJournal
    {
        /// <summary>The reader and writer id of the MCP door hosted in this process.</summary>
        public const string InProcessReader = "mcp";

        /// <summary>How many objects one line names before it says "and N more".</summary>
        public const int MaxItems = 12;

        // Enough for any gap between two tool calls; a reader that falls further behind is told so ("and earlier
        // changes") instead of being handed a silent partial list.
        private const int Capacity = 1024;

        private sealed class Entry
        {
            public long Seq;
            public string SessionId;
            public long Revision;
            public string Writer;
            public string Origin;
            public string Label;
            public string Replay;   // "undo" | "redo" for a walk (one step or to a point); null for an edit
            public string[] Refs;
        }

        // The spec-first start plan has no model revision and no session, so its changes keep a short memory of their
        // own, with their own cursor per reader. Only the newest view matters to the line, but WHO moved it matters to
        // the reader's own-change rule, so each change is kept until every reader has passed it or the memory is full.
        private sealed class StartEntry
        {
            public long Seq;
            public string Writer;
            public string Origin;
            public string Clause;
        }

        private const int StartCapacity = 256;

        private readonly object _gate = new object();
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly Dictionary<string, long> _cursors = new Dictionary<string, long>(StringComparer.Ordinal);
        private long _head;   // the Seq of the newest entry ever recorded
        private readonly List<StartEntry> _startEntries = new List<StartEntry>();
        private readonly Dictionary<string, long> _startCursors = new Dictionary<string, long>(StringComparer.Ordinal);
        private long _startHead;

        public ChangeJournal() { _cursors[InProcessReader] = 0; _startCursors[InProcessReader] = 0; }

        /// <summary>Start a reader at the current head: it hears about commits from now on.</summary>
        public void Register(string reader)
        {
            if (string.IsNullOrEmpty(reader)) return;
            lock (_gate) { _cursors[reader] = _head; _startCursors[reader] = _startHead; }
        }

        public void Unregister(string reader)
        {
            if (string.IsNullOrEmpty(reader) || reader == InProcessReader) return;
            lock (_gate) { _cursors.Remove(reader); _startCursors.Remove(reader); }
        }

        /// <summary>The <see cref="ChangeBus.Changed"/> subscriber. Only real commits count: a save or a scope
        /// change also travels the bus but changes no object in the model.</summary>
        public void Record(ChangeNotification n)
        {
            try
            {
                if (n == null || !n.IsCommit) return;
                var refs = (n.Deltas ?? Array.Empty<ChangeDelta>())
                    .Select(d => d?.Ref).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.Ordinal).ToArray();
                lock (_gate)
                {
                    _entries.Add(new Entry
                    {
                        Seq = ++_head, SessionId = n.SessionId, Revision = n.Revision, Writer = n.Writer, Origin = n.Origin, Label = n.Label, Replay = n.Replay, Refs = refs,
                    });
                    if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
                }
            }
            catch { /* the fold is a ride-along: it must never fail a commit */ }
        }

        /// <summary>The <see cref="ChangeBus.StartPlanRecorded"/> subscriber: remember that the start plan moved, and who
        /// moved it. Never throws into the plan edit that caused it.</summary>
        internal void RecordStartPlan(StartPlanChangeRecord r)
        {
            try
            {
                if (r == null) return;
                var clause = StartPlan.StartPlanDoors.FoldClause(r.View);
                lock (_gate)
                {
                    _startEntries.Add(new StartEntry { Seq = ++_startHead, Writer = r.Writer, Origin = r.Origin, Clause = clause });
                    if (_startEntries.Count > StartCapacity) _startEntries.RemoveRange(0, _startEntries.Count - StartCapacity);
                }
            }
            catch { /* the fold is a ride-along: it must never fail a plan edit */ }
        }

        /// <summary>The start plan's half of a take: one sentence when somebody ELSE moved the plan since this reader's
        /// previous take (the newest state, and who), null when nobody did, and for a reader that was never registered.
        /// Moves the reader's start cursor to the head, like <see cref="Take"/> does for the model.</summary>
        private string TakeStartPlan(string reader)
        {
            StartEntry latest;
            bool studio, assistant, engine;
            lock (_gate)
            {
                if (string.IsNullOrEmpty(reader) || !_startCursors.TryGetValue(reader, out var cursor)) return null;
                _startCursors[reader] = _startHead;
                if (cursor >= _startHead) return null;
                var foreign = _startEntries.Where(e => e.Seq > cursor && !IsOwnStart(e, reader)).ToList();
                if (foreign.Count == 0) return null;
                latest = _startEntries[_startEntries.Count - 1];
                studio = foreign.Any(e => e.Origin == "human");
                assistant = foreign.Any(e => e.Origin == "agent");
                engine = foreign.Any(e => e.Origin != "human" && e.Origin != "agent");
            }
            var who = new List<string>();
            if (studio) who.Add("in Studio");
            if (assistant) who.Add("by another assistant");
            if (engine) who.Add("in the background");
            var by = who.Count <= 1 ? (who.Count == 1 ? who[0] : "in Studio") : string.Join(", ", who.Take(who.Count - 1)) + " and " + who[who.Count - 1];
            return "start plan changed since your last call (" + by + "): " + latest.Clause;
        }

        // Same rule as the model's: a change with no writer stamp came from inside this process, and an agent-origin one
        // is the in-process MCP door. The engine's own work (no origin) is nobody's own, so every reader hears it.
        private static bool IsOwnStart(StartEntry e, string reader)
            => string.Equals(e.Writer ?? (e.Origin == "agent" ? InProcessReader : null), reader, StringComparison.Ordinal);

        /// <summary>Drain what other writers committed to the model open now (<paramref name="sessionId"/>) since this
        /// reader's previous take. Null when nothing did, which is the common case, and for a reader that was never
        /// registered.</summary>
        /// <remarks>F-247: every entry carries the session it was committed on and a take names only the open
        /// session's, so a reader whose cursor still sits before a model swap is never told about the old model's
        /// edits. Filtering on the stamp, rather than resetting at the swap, needs no hook at either exchange site
        /// (<see cref="SessionManager"/> has two) and holds whatever order the bus and the swap happen in.</remarks>
        public ForeignChanges Take(string reader, string sessionId)
        {
            var model = TakeModel(reader, sessionId);
            var plan = TakeStartPlan(reader);
            if (plan == null) return model;
            // A plan change alone still comes back as a take, with no model items, so the fold can say it.
            model ??= new ForeignChanges { Items = Array.Empty<string>() };
            model.StartPlan = plan;
            return model;
        }

        private ForeignChanges TakeModel(string reader, string sessionId)
        {
            List<Entry> foreign;
            bool truncated;
            lock (_gate)
            {
                if (string.IsNullOrEmpty(reader) || !_cursors.TryGetValue(reader, out var cursor)) return null;
                _cursors[reader] = _head;
                if (cursor >= _head) return null;
                // Sessions never come back (each open mints a fresh id), so when the oldest entry kept belongs to an
                // earlier model, everything already dropped did too, and nothing of this model's was lost.
                truncated = _entries.Count == 0
                    || (_entries[0].Seq > cursor + 1 && string.Equals(_entries[0].SessionId, sessionId, StringComparison.Ordinal));
                foreign = _entries.Where(e => e.Seq > cursor && !IsOwn(e, reader)
                    && string.Equals(e.SessionId, sessionId, StringComparison.Ordinal)).ToList();
            }
            if (foreign.Count == 0) return null;

            var items = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(string item) { if (seen.Add(item)) items.Add(item); }
            foreach (var e in foreign)
            {
                // A person's undo or redo is a change like any other; it is named, because the object it touched
                // may be one the assistant itself just wrote.
                // An undo to a point is named the same way: it is still the person walking the history.
                var walk = e.Replay ?? (e.Label == "undo" || e.Label == "redo" ? e.Label : null);
                if (walk != null) Add(walk);
                foreach (var r in e.Refs) Add(r);
                if (e.Refs.Length == 0 && walk == null && !string.IsNullOrEmpty(e.Label)) Add(e.Label);
            }
            return new ForeignChanges
            {
                Items = items.Take(MaxItems).ToArray(),
                More = Math.Max(0, items.Count - MaxItems),
                Truncated = truncated,
                Studio = foreign.Any(e => e.Origin == "human"),
                Assistant = foreign.Any(e => e.Origin != "human"),
                Revision = foreign[foreign.Count - 1].Revision,
            };
        }

        // A commit with no writer stamp came from inside this process. An agent-origin one is the in-process MCP door
        // (a flow that lost its correlation id is still its own call, so it must not be echoed back to it).
        private static bool IsOwn(Entry e, string reader)
            => string.Equals(e.Writer ?? (e.Origin == "agent" ? InProcessReader : null), reader, StringComparison.Ordinal);
    }
}
