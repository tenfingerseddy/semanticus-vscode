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

    internal sealed class DryRunCollector
    {
        public List<ChangeDelta> Deltas { get; } = new();
        public List<string> MutationLabels { get; } = new();
    }
}
