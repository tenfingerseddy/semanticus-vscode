using System;
using System.Collections.Generic;
using System.Threading;

namespace Semanticus.Engine
{
    /// <summary>Ambient dry-run scope: while active, Session.MutateAsync rehearses (rollback, no undo entry,
    /// no revision, no broadcast) and collects would-be deltas here; audit/experience persistence stands down.
    /// The flag is READ ON THE CALLER'S THREAD at MutateAsync entry (before the dispatcher hop), so AsyncLocal
    /// flow to the dispatcher's dedicated thread is never relied on.</summary>
    internal static class DryRunScope
    {
        private static readonly AsyncLocal<DryRunCollector> _current = new();
        public static DryRunCollector Current { get => _current.Value; set => _current.Value = value; }
    }

    /// <summary>What a session's commit sink asks of the engine for an assistant's tracked edit. <see cref="Asks"/> is the cheap policy read
    /// (no side effects): does this origin have to wait for the person? <see cref="Refuse"/> is the sink's refusal when no ticket covers
    /// the commit: it registers a request for the person to see and returns the sentence the assistant is refused with.</summary>
    internal sealed class AgentWriteGate
    {
        public AgentWriteGate(Func<string, bool> asks, Func<string, string, string> refuse) { Asks = asks; Refuse = refuse; }
        public Func<string, bool> Asks { get; }
        public Func<string, string, string> Refuse { get; }
    }

    internal sealed class DryRunCollector
    {
        public List<ChangeDelta> Deltas { get; } = new();
        public List<string> MutationLabels { get; } = new();
        /// <summary>True for a rehearsal the dry_run tool started for an assistant's call, where the gate runs every write the tool makes
        /// and refuses the ones that land outside the model. The engine's own rehearsals (the Plan's, a draft's) leave it false.</summary>
        public bool FromTool { get; init; }
    }
}
