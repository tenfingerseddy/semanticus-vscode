using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// One ownership boundary for everything whose meaning changes with the open model. A replacement is built
    /// off to the side and this whole object is exchanged at once, so work that captured the old context can
    /// never publish into the new model's plan, spec, baseline, workflow, readiness, or live-connection state.
    /// </summary>
    internal sealed class SessionContext : IDisposable
    {
        private LiveConnection _live;

        internal SessionContext(Session session, WorkflowRunStore workflowRuns = null,
            Dictionary<string, WorkflowRunAux> workflowAux = null)
        {
            Session = session;
            // A workflow may deliberately open or create a model as one of its steps. Runs and their start-of-run
            // verification snapshots therefore follow the workflow across a session swap; the other stores below
            // remain model-scoped and start fresh with the replacement model.
            WorkflowRuns = workflowRuns ?? new WorkflowRunStore();
            WorkflowAux = workflowAux ?? new Dictionary<string, WorkflowRunAux>(StringComparer.Ordinal);
        }

        internal Session Session { get; }
        internal LiveConnection Live => Volatile.Read(ref _live);

        internal PlanStore Plans { get; } = new PlanStore();
        internal SemaphoreSlim PlanGate { get; } = new SemaphoreSlim(1, 1);
        internal SpecStore Spec { get; } = new SpecStore();
        internal SemaphoreSlim SpecGate { get; } = new SemaphoreSlim(1, 1);
        internal BaselineStore Baselines { get; } = new BaselineStore();
        internal SemaphoreSlim BaselineGate { get; } = new SemaphoreSlim(1, 1);
        internal WorkflowRunStore WorkflowRuns { get; }
        internal SemaphoreSlim WorkflowGate { get; } = new SemaphoreSlim(1, 1);
        internal Dictionary<string, WorkflowRunAux> WorkflowAux { get; }

        internal object ReadinessGate { get; } = new object();
        internal string ReadinessGrade { get; set; }

        internal Session RequireSession() =>
            Session ?? throw new InvalidOperationException("No model is open. Call 'open' first.");

        internal LiveConnection ExchangeLive(LiveConnection next) => Interlocked.Exchange(ref _live, next);

        /// <summary>Drain state publications before this context is exchanged. The canonical nested gate order is
        /// Workflow -&gt; Plan -&gt; Baseline -&gt; Spec. Code that ever needs more than one of these gates must use this
        /// order so a context swap cannot deadlock with a normal operation.</summary>
        internal async Task<IDisposable> AcquireStateLeaseAsync()
        {
            await WorkflowGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await PlanGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await BaselineGate.WaitAsync().ConfigureAwait(false);
                    try { await SpecGate.WaitAsync().ConfigureAwait(false); }
                    catch { BaselineGate.Release(); throw; }
                }
                catch { PlanGate.Release(); throw; }
            }
            catch { WorkflowGate.Release(); throw; }
            return new StateLease(this);
        }

        internal void DisposeRetired()
        {
            try { ExchangeLive(null)?.Dispose(); } catch { }
            try { Session?.DisposeRetired(); } catch { }
        }

        public void Dispose()
        {
            try { ExchangeLive(null)?.Dispose(); } catch { }
            try { Session?.Dispose(); } catch { }
        }

        private sealed class StateLease : IDisposable
        {
            private SessionContext _owner;
            internal StateLease(SessionContext owner) => _owner = owner;
            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner == null) return;
                owner.SpecGate.Release();
                owner.BaselineGate.Release();
                owner.PlanGate.Release();
                owner.WorkflowGate.Release();
            }
        }
    }

    internal sealed class WorkflowRunAux
    {
        internal HashSet<string> BpaKeys;
        internal HashSet<string> ReadinessKeys;
        internal double ReadinessOverall;
        // E3(b) — the last WORKFLOW-AUTHORED expression hash per measure ref (canonical), recorded when
        // create_measure/update_measure runs at a step that declares that op. dax_equivalence fails as `unavailable`
        // (drift) when the target measure's current hash no longer matches — i.e. it was changed OUTSIDE the workflow.
        internal Dictionary<string, string> AuthoredMeasureHashes;
    }
}
