using System.Collections.Generic;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>
    /// An ambient ride-along on Session's tracked-commit chokepoint — the generalized form of what used to be a
    /// single settable health-probe slot (a second ambient consumer would have silently evicted the first; #82
    /// review follow-up). Both hooks run on the DISPATCHER thread: <see cref="BeforeCommit"/> right before the
    /// mutation, <see cref="AfterCommit"/> post-commit and pre-broadcast, so a contribution rides the same
    /// <c>model/didChange</c> the commit publishes. Session isolates every call — a throwing observer is skipped
    /// for that commit, never failing the edit or starving its peers — so implementations need not defend the
    /// commit path themselves. Dry-runs never reach observers (MutateAsync short-circuits rehearsals to
    /// RehearseAsync, which never enters TrackAsync).
    /// </summary>
    public interface ISessionObserver
    {
        /// <summary>Right before the mutation runs — memoize pre-edit state here. Dispatcher thread.</summary>
        void BeforeCommit(Model model);

        /// <summary>After the commit, before the broadcast. Dispatcher thread. Mutate <paramref name="commit"/>
        /// to contribute to the outgoing notification (today: <see cref="SessionCommit.Health"/>).</summary>
        void AfterCommit(SessionCommit commit);
    }

    /// <summary>One tracked commit as observers see it (shared by all observers of that commit), plus the
    /// observer-contributed notification payload.</summary>
    public sealed class SessionCommit
    {
        public Model Model { get; set; }
        public long Revision { get; set; }
        public string Origin { get; set; }
        public string Label { get; set; }
        public IReadOnlyList<ChangeDelta> Deltas { get; set; }
        /// <summary>The originating tool-call identity (null outside an MCP call) — captured by Session on the
        /// caller's thread before the dispatcher hop (see <see cref="HealthCorrelation"/>).</summary>
        public string CorrelationId { get; set; }
        /// <summary>Set by a health-producing observer; rides this commit's <c>model/didChange</c>. Observers run
        /// in registration order and contribute-don't-clobber: set only while still null — the FIRST non-null
        /// contribution wins, so a later-registered producer can never silently replace an earlier one's.</summary>
        public HealthDelta Health { get; set; }
    }
}
