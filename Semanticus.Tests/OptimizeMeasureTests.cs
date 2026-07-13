using System;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Verified Edits — the <c>optimize_measure</c> enforcement invariants that are deterministic OFFLINE
    /// (CI-safe, no XMLA): it REFUSES without &gt;=2 candidates, and it NEVER mutates the model without a live
    /// connection to prove equivalence + benchmark. The prove / benchmark / apply-the-winner paths need a live
    /// endpoint — those are exercised by the McpSmoke / RpcSmoke runners, not CI (see the note at the bottom).</summary>
    public sealed class OptimizeMeasureTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static async Task<LocalEngine> OpenAsync(bool pro = true)
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(pro));
            await engine.OpenAsync(TestModels.FindBim());
            return engine;
        }

        private static async Task<string> FirstMeasureRefAsync(LocalEngine engine)
        {
            var ms = await engine.ListMeasuresAsync();
            Assert.NotEmpty(ms);
            return ms[0].Ref;
        }

        [Fact]
        public async Task Fewer_than_two_candidates_is_refused()
        {
            using var engine = await OpenAsync();
            var r = await FirstMeasureRefAsync(engine);
            // one candidate, empty, null, and (after whitespace filtering) effectively-one all trip the >=2 precondition.
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.OptimizeMeasureAsync(r, new[] { "1" }, new[] { "'Date'[Year]" }, null, true, "human"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.OptimizeMeasureAsync(r, Array.Empty<string>(), null, null, true, "human"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.OptimizeMeasureAsync(r, null, null, null, true, "human"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.OptimizeMeasureAsync(r, new[] { "1", "   " }, null, null, true, "human"));
        }

        [Fact]
        public async Task Offline_never_mutates_the_model()
        {
            using var engine = await OpenAsync();
            var r = await FirstMeasureRefAsync(engine);
            var before = await engine.GetDaxAsync(r);
            var res = await engine.OptimizeMeasureAsync(r, new[] { "1", "2" }, new[] { "'Date'[Year]" }, null, true, "human");
            // No live connection in CI ⇒ the op cannot prove/benchmark, so it must apply nothing (whatever the exact
            // verdict — unproven-offline, or insufficient-valid if the validator is strict). The invariant is: no mutation.
            Assert.False(res.Applied);
            Assert.NotEqual("applied", res.Verdict);
            Assert.Equal(before, await engine.GetDaxAsync(r));
        }

        [Fact]
        public async Task Unknown_measure_ref_is_reported_not_thrown()
        {
            using var engine = await OpenAsync();
            var res = await engine.OptimizeMeasureAsync("measure:Nope/DoesNotExist", new[] { "1", "2" }, new[] { "'Date'[Year]" }, null, true, "human");
            Assert.Equal("error", res.Verdict);
            Assert.False(res.Applied);
        }

        // LIVE-ONLY (need an XMLA endpoint — run via the smokes / SEMANTICUS_LIVE_TEST, not CI):
        //  • correctness-before-speed: a faster-but-NOT-equivalent candidate must never win (VerifyState "failed", never applied).
        //  • applied-when-faster: a proven-equivalent, strictly-faster candidate is applied (Verdict "applied", body changed, one revision).
        //  • free tier: apply=true on free returns "paused-free" with full evidence and does NOT mutate.
    }
}
