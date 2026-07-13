using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// ITEM 2 (#82 review follow-up): Session's ambient probe seam is a small registration-ORDERED observer
    /// list, not a single settable slot — a second ambient consumer must not evict the first, one faulty
    /// observer must neither fail the commit nor starve its peers, and unregistering really detaches.
    /// </summary>
    public sealed class SessionObserverTests
    {
        private sealed class FreeTier : IEntitlement
        {
            public bool IsPro => false;   // keeps the engine-installed health probe inert — these tests own the seam
            public EntitlementInfo Info => new EntitlementInfo { Tier = "free" };
        }

        private sealed class Recorder : ISessionObserver
        {
            public int Befores;
            public readonly List<SessionCommit> Commits = new List<SessionCommit>();
            public bool ThrowBefore, ThrowAfter;
            public void BeforeCommit(Model model)
            {
                Befores++;   // count BEFORE throwing: the test proves the hook ran and was isolated
                if (ThrowBefore) throw new InvalidOperationException("faulty observer (before)");
            }
            public void AfterCommit(SessionCommit commit)
            {
                if (ThrowAfter) throw new InvalidOperationException("faulty observer (after)");
                Commits.Add(commit);
            }
        }

        private static async Task<(SessionManager sm, Session s)> FreshAsync()
        {
            var sm = new SessionManager();
            var engine = new LocalEngine(sm, new FreeTier());
            await engine.CreateModelAsync("ObserverTest", 1604);
            await engine.CreateTableAsync("T", "human");
            return (sm, sm.Current);
        }

        [Fact]
        public async Task Two_observers_both_see_the_same_commit()
        {
            var (_, s) = await FreshAsync();
            var a = new Recorder();
            var b = new Recorder();
            s.RegisterObserver(a);
            s.RegisterObserver(b);   // the second consumer must NOT evict the first (the old single slot did)

            var rev = await s.MutateAsync("agent", "add measure", m => m.Tables["T"].AddMeasure("M1", "1"));

            Assert.Equal(1, a.Befores);
            Assert.Equal(1, b.Befores);
            var ca = Assert.Single(a.Commits);
            var cb = Assert.Single(b.Commits);
            Assert.Same(ca, cb);                       // one shared commit context per commit
            Assert.Equal(rev, ca.Revision);
            Assert.Equal("agent", ca.Origin);
            Assert.Equal("add measure", ca.Label);
            Assert.Contains(ca.Deltas, d => d.Ref == "measure:T/M1");
        }

        [Fact]
        public async Task Throwing_observer_neither_fails_the_edit_nor_starves_its_peer()
        {
            var (sm, s) = await FreshAsync();
            var faulty = new Recorder { ThrowBefore = true, ThrowAfter = true };
            var healthy = new Recorder();
            s.RegisterObserver(faulty);    // faulty FIRST — the peer downstream in the order must still run
            s.RegisterObserver(healthy);

            var n = await CaptureAsync(sm, () => s.MutateAsync("human", "edit", m => m.Tables["T"].AddMeasure("M2", "2")));

            Assert.Equal(1, faulty.Befores);           // it ran (and threw) — isolated, not skipped silently
            Assert.Empty(faulty.Commits);
            Assert.Single(healthy.Commits);            // the peer was not starved
            Assert.NotNull(n);                         // and the commit still broadcast its didChange
        }

        private sealed class ProTier : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private sealed class Planter : ISessionObserver
        {
            public readonly HealthDelta Planted = new HealthDelta { Findings = 99 };
            public void BeforeCommit(Model model) { }
            public void AfterCommit(SessionCommit commit) => commit.Health = Planted;
        }

        // Copilot on #90: contribute-don't-clobber must hold for NON-null results too — a later-registered
        // health producer (here: the engine-installed probe, re-registered after the planter) must not silently
        // replace an earlier observer's contribution. The mailbox assertion proves the probe really computed a
        // non-null delta of its own (an undescribed measure trips Warning+ findings), so first-wins is what kept
        // the planted delta on the notification — not a probe that had nothing to say.
        [Fact]
        public async Task Earlier_health_contribution_is_not_clobbered_by_a_later_producer()
        {
            var sm = new SessionManager();
            var engine = new LocalEngine(sm, new ProTier());
            await engine.CreateModelAsync("ObserverTest", 1604);
            await engine.CreateTableAsync("T", "human");
            var s = sm.Current;

            var probe = Assert.Single(s.Observers);   // the engine-installed HealthDeltaProbe
            s.UnregisterObserver(probe);
            var planter = new Planter();
            s.RegisterObserver(planter);
            s.RegisterObserver(probe);                // the probe now runs AFTER the planter

            var n = await CaptureAsync(sm, () => engine.CreateMeasureAsync("table:T", "m1", "1", "agent"));

            Assert.Same(planter.Planted, n.Health);                  // first contribution wins on the wire
            Assert.NotNull(await engine.PullAgentHealthAsync());     // …while the probe still computed + stashed its own
        }

        [Fact]
        public async Task Unregister_detaches_without_disturbing_the_rest()
        {
            var (_, s) = await FreshAsync();
            var a = new Recorder();
            var b = new Recorder();
            s.RegisterObserver(a);
            s.RegisterObserver(b);

            await s.MutateAsync("human", "edit 1", m => m.Tables["T"].AddMeasure("M3", "3"));
            s.UnregisterObserver(a);
            await s.MutateAsync("human", "edit 2", m => m.Tables["T"].AddMeasure("M4", "4"));

            Assert.Single(a.Commits);                  // nothing after the unregister
            Assert.Equal(2, b.Commits.Count);
            Assert.DoesNotContain(a, s.Observers);
            Assert.Contains(b, s.Observers);
        }

        private static async Task<ChangeNotification> CaptureAsync(SessionManager sm, Func<Task> act)
        {
            ChangeNotification last = null;
            Action<ChangeNotification> handler = n => last = n;
            sm.Bus.Changed += handler;
            try { await act(); } finally { sm.Bus.Changed -= handler; }
            return last;
        }
    }
}
