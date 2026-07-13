using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Semanticus.Engine
{
    /// <summary>
    /// Ambient tool-call identity for the health delta (feature #4): <see cref="McpHealthAppender"/> mints an id
    /// per tool call and sets it here around the call; <c>Session.TrackAsync</c> reads it on the CALLER's thread
    /// (AsyncLocal does not flow onto the dispatcher's dedicated thread — the same capture pattern as
    /// DryRunScope) and stamps it onto every commit the call performs, so the post-call drain collects exactly
    /// THIS call's deltas. Two concurrent calls each see their own value: an AsyncLocal write inside an async
    /// method scopes to that method's async subtree.
    /// </summary>
    internal static class HealthCorrelation
    {
        private static readonly AsyncLocal<string> Ambient = new AsyncLocal<string>();

        public static string CurrentId => Ambient.Value;

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
    /// The ENGINE-level agent-health mailbox: one MERGED slot per tool call (correlation id), stashed by the
    /// producing session's probe and drained BY ID by the MCP success filter. This replaces the old per-probe
    /// bounded queue whose "correlation by drain" mis-attributed deltas — two parallel mutating calls on one
    /// session had the first drainer take both merged (the second got none), a model swap mid-call drained the
    /// WRONG session's probe (the pull went through <c>_sessions.Current</c>), and a &gt;16-commit call silently
    /// evicted its oldest deltas (wrong grade-from endpoint + undercount). Now:
    ///   • merge-on-enqueue — a mega-batch call folds into ONE slot (grade endpoints span first→last, rule-ids
    ///     union, findings sum, impact max: per-commit cones can overlap, so summing would over-claim);
    ///   • the mailbox lives on the ENGINE, keyed by call id and stamped with the producing session — a model
    ///     swap can neither lose the slot nor hand it to another call;
    ///   • commits with NO ambient call identity (an attached MCP process whose ops crossed the RPC pipe, or a
    ///     non-MCP agent client) land in the UNSCOPED slot, which every drain also collects — that keeps
    ///     attach-mode health flowing; its two-parallel-calls race is the documented residual (ids cannot cross
    ///     the pipe per-op today).
    /// Take-once semantics per slot; thread-safe (its own lock — stashes arrive on the dispatcher thread,
    /// drains on MCP threads).
    /// </summary>
    public sealed class AgentHealthMailbox
    {
        private const string Unscoped = "";
        // Safety bound on ABANDONED slots (a client that died between commit and drain) — never hit by a live
        // call, whose commits merge into one slot. Oldest-first eviction.
        private const int MaxSlots = 64;

        private sealed class Slot
        {
            public string SessionId;
            public string GradeFrom;
            public string GradeTo;
            public HashSet<string> RuleIds;
            public int Findings;
            public int Impact;
            public bool Warn;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Slot> _slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();   // insertion order for eviction

        /// <summary>Merge <paramref name="delta"/> into the slot for <paramref name="correlationId"/> (null = the
        /// unscoped slot). Called by the probe on the dispatcher thread; must never throw into a commit.</summary>
        public void Stash(string correlationId, string sessionId, HealthDelta delta)
        {
            if (delta == null) return;
            var key = correlationId ?? Unscoped;
            lock (_gate)
            {
                if (!_slots.TryGetValue(key, out var slot))
                {
                    while (_slots.Count >= MaxSlots && _order.Count > 0)   // abandoned-slot insurance only
                    {
                        _slots.Remove(_order[0]);
                        _order.RemoveAt(0);
                    }
                    _slots[key] = slot = new Slot { SessionId = sessionId };
                    _order.Add(key);
                }
                Merge(slot, sessionId, delta);
            }
        }

        /// <summary>Drain-and-return the merged delta for <paramref name="correlationId"/> — plus the unscoped
        /// slot (identity-less commits belong to whichever call is asking; see the class doc). Take-once: a
        /// second pull returns null until the next stash. Null when nothing was stashed.</summary>
        public HealthDelta Take(string correlationId)
        {
            Slot exact, ambient;
            lock (_gate)
            {
                var key = correlationId ?? Unscoped;
                _slots.TryGetValue(key, out exact);
                if (exact != null) { _slots.Remove(key); _order.Remove(key); }
                ambient = null;
                if (key != Unscoped && _slots.TryGetValue(Unscoped, out ambient))
                {
                    _slots.Remove(Unscoped);
                    _order.Remove(Unscoped);
                }
            }
            if (exact == null && ambient == null) return null;

            // Combine the ambient slot into the exact one (chronology across slots is unknowable — the exact
            // slot's endpoints win where both carry a grade).
            var slot = exact ?? ambient;
            if (exact != null && ambient != null)
            {
                slot = new Slot
                {
                    SessionId = exact.SessionId,
                    GradeFrom = exact.GradeFrom ?? ambient.GradeFrom,
                    GradeTo = exact.GradeTo ?? ambient.GradeTo,
                    RuleIds = Union(exact.RuleIds, ambient.RuleIds),
                    Findings = exact.Findings + ambient.Findings,
                    Impact = Math.Max(exact.Impact, ambient.Impact),
                    Warn = exact.Warn || ambient.Warn,
                };
            }

            var grade = slot.GradeFrom != null && slot.GradeTo != null && slot.GradeFrom != slot.GradeTo
                ? slot.GradeFrom + "->" + slot.GradeTo : null;
            var ids = slot.RuleIds != null && slot.RuleIds.Count > 0
                ? slot.RuleIds.Take(HealthDeltaProbe.MaxRuleIds).ToArray() : null;
            if (grade == null && ids == null && slot.Findings <= 0 && slot.Impact <= 0) return null;   // everything cancelled out
            return new HealthDelta
            {
                Grade = grade,
                New = ids,
                Findings = slot.Findings > 0 ? slot.Findings : (int?)null,
                Impact = slot.Impact > 0 ? slot.Impact : (int?)null,
                Warn = slot.Warn ? true : (bool?)null,
            };
        }

        private static void Merge(Slot slot, string sessionId, HealthDelta delta)
        {
            // A model swap INSIDE one call: grade endpoints from different models can't span honestly — restart
            // the span at the new session's movement (findings/impact still accumulate; they count events).
            if (!string.Equals(slot.SessionId, sessionId, StringComparison.Ordinal))
            {
                slot.SessionId = sessionId;
                slot.GradeFrom = null;
                slot.GradeTo = null;
            }
            var g = delta.Grade;
            var arrow = g?.IndexOf("->", StringComparison.Ordinal) ?? -1;
            if (arrow > 0)
            {
                if (slot.GradeFrom == null) slot.GradeFrom = g.Substring(0, arrow);
                slot.GradeTo = g.Substring(arrow + 2);
            }
            if (delta.New != null && delta.New.Length > 0)
            {
                slot.RuleIds = slot.RuleIds ?? new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in delta.New) slot.RuleIds.Add(id);
            }
            slot.Findings += delta.Findings ?? 0;
            slot.Impact = Math.Max(slot.Impact, delta.Impact ?? 0);
            slot.Warn |= delta.Warn == true;
        }

        private static HashSet<string> Union(HashSet<string> a, HashSet<string> b)
        {
            if (a == null) return b;
            if (b == null) return a;
            a.UnionWith(b);
            return a;
        }
    }
}
