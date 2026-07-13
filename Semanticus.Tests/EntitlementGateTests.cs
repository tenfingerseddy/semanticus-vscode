using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The Pro gate at the bulk-apply chokepoints. The FREE tier is refused the one-click BULK primitives
    /// (apply_plan with &gt;1 item, bpa_fix_all, apply_safe_fixes) but keeps the single-item paths; PRO is unrestricted.
    /// Drives the real <see cref="LocalEngine"/> with an injected entitlement, so it asserts the engine-level
    /// guarantee BOTH doors share — a refusal also leaves the model intact (thrown before any mutation).</summary>
    public sealed class EntitlementGateTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static async Task<LocalEngine> OpenAsync(bool pro)
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(pro));
            await engine.OpenAsync(TestModels.FindBim());   // AdventureWorks — rich in auto-fixable findings
            return engine;
        }

        [Fact]
        public async Task Free_tier_is_refused_bpa_fix_all()
        {
            using var engine = await OpenAsync(pro: false);
            await Assert.ThrowsAsync<EntitlementException>(() => engine.BpaFixAllAsync("human"));
        }

        [Fact]
        public async Task Pro_tier_may_bpa_fix_all()
        {
            using var engine = await OpenAsync(pro: true);
            Assert.NotNull(await engine.BpaFixAllAsync("human"));   // must NOT throw the gate
        }

        [Fact]
        public async Task Free_tier_is_refused_apply_safe_fixes()
        {
            using var engine = await OpenAsync(pro: false);
            await Assert.ThrowsAsync<EntitlementException>(() => engine.ApplySafeFixesAsync("human"));
        }

        [Fact]
        public async Task Pro_tier_may_apply_safe_fixes()
        {
            using var engine = await OpenAsync(pro: true);
            Assert.NotNull(await engine.ApplySafeFixesAsync("human"));
        }

        [Fact]
        public async Task Free_tier_may_apply_ONE_plan_item_but_not_a_bulk_apply()
        {
            using var engine = await OpenAsync(pro: false);
            var plan = await engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, "human");
            var approved = plan.Items.Where(i => i.Status == "approved").Select(i => i.Id).ToArray();
            Assert.True(approved.Length >= 2, $"need >=2 approved safe fixes to exercise the bulk gate; got {approved.Length}");

            // bulk apply (all approved, >1) is refused on free...
            await Assert.ThrowsAsync<EntitlementException>(() => engine.ApplyPlanAsync(Array.Empty<string>(), "human"));
            // ...but a single explicit item (count == 1) is allowed.
            Assert.NotNull(await engine.ApplyPlanAsync(new[] { approved[0] }, "human"));
        }

        [Fact]
        public async Task Pro_tier_may_bulk_apply_the_plan()
        {
            using var engine = await OpenAsync(pro: true);
            var plan = await engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, "human");
            var approved = plan.Items.Where(i => i.Status == "approved").Select(i => i.Id).ToArray();
            Assert.True(approved.Length >= 2);
            Assert.NotNull(await engine.ApplyPlanAsync(Array.Empty<string>(), "human"));   // bulk — must NOT throw the gate
        }

        [Fact]
        public async Task Free_tier_is_refused_make_ai_ready()
        {
            using var engine = await OpenAsync(pro: false);
            await Assert.ThrowsAsync<EntitlementException>(() => engine.MakeAiReadyAsync("human", 10));
        }

        [Fact]
        public async Task Pro_tier_may_make_ai_ready()
        {
            using var engine = await OpenAsync(pro: true);
            Assert.NotNull(await engine.MakeAiReadyAsync("human", 10));   // must NOT throw the gate
        }

        [Fact]
        public async Task Free_tier_may_apply_a_single_block_dax_script_but_not_multi()
        {
            using var engine = await OpenAsync(pro: false);
            var ms = (await engine.ListMeasuresAsync()).Take(2).ToArray();
            Assert.True(ms.Length >= 2, "need >=2 measures to exercise the multi-block gate");
            string Block(string r) => $"// @object {r}\n1\n";
            // a MULTI-object script (>1 block) is refused on free...
            await Assert.ThrowsAsync<EntitlementException>(() => engine.ApplyDaxScriptAsync(Block(ms[0].Ref) + Block(ms[1].Ref), "human"));
            // ...but a single-block script (one object) stays free.
            Assert.NotNull(await engine.ApplyDaxScriptAsync(Block(ms[0].Ref), "human"));
        }

        [Fact]
        public async Task Pro_tier_may_apply_a_multi_block_dax_script()
        {
            using var engine = await OpenAsync(pro: true);
            var ms = (await engine.ListMeasuresAsync()).Take(2).ToArray();
            string Block(string r) => $"// @object {r}\n1\n";
            Assert.NotNull(await engine.ApplyDaxScriptAsync(Block(ms[0].Ref) + Block(ms[1].Ref), "human"));   // bulk — no gate throw
        }

        [Fact]
        public async Task Free_tier_is_refused_a_multi_object_tmdl_script()
        {
            using var engine = await OpenAsync(pro: false);
            // Two top-level TMDL docs => the bulk apply_tmdl chokepoint. The gate fires on docs.Count>1 before any apply.
            var twoDocs = "table 'GateTmdlA'\n\tlineageTag: a\ntable 'GateTmdlB'\n\tlineageTag: b\n";
            await Assert.ThrowsAsync<EntitlementException>(() => engine.ApplyTmdlScriptAsync(twoDocs, "human"));
        }

        [Fact]
        public async Task Pro_tier_may_apply_a_multi_object_tmdl_script()
        {
            using var engine = await OpenAsync(pro: true);
            var twoDocs = "table 'GateTmdlA'\n\tlineageTag: a\ntable 'GateTmdlB'\n\tlineageTag: b\n";
            Assert.NotNull(await engine.ApplyTmdlScriptAsync(twoDocs, "human"));   // no gate throw (docs may individually skip)
        }

        // ---- apply_model_diff to a FILE target (the merge-arbitrage hole closed 2026-07-07): the file-write
        // commit branch gates >1 exactly like the session path, so bulk merge-to-file can't route around the
        // session gate. Preview and a single-ref commit stay free.

        private static string CopyFixtureTo(string suffix)
        {
            var target = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sem-gate-{suffix}-{Guid.NewGuid():N}.bim");
            System.IO.File.Copy(TestModels.FindBim(), target);
            return target;
        }

        [Fact]
        public async Task Free_tier_may_preview_and_single_commit_a_file_merge_but_not_bulk()
        {
            using var engine = await OpenAsync(pro: false);
            var target = CopyFixtureTo("free");
            try
            {
                // Two session-only measures → the session-vs-file diff carries (at least) two applicable items.
                var table = (await engine.ListTreeAsync(null)).First(t => t.Kind == "table");
                await engine.CreateMeasureAsync(table.Ref, "Gate Merge A", "1", "human");
                await engine.CreateMeasureAsync(table.Ref, "Gate Merge B", "2", "human");
                var file = new ModelRef { Kind = "file", Path = target };

                // The commit=false preview is read-only and free, whatever the count.
                var preview = await engine.ApplyDiffAsync(null, file, null, commit: false, "human");
                Assert.False(preview.Applied);
                Assert.True(preview.Count >= 2, $"expected >=2 applicable diff items, got {preview.Count}");

                // A bulk commit (>1 applicable) is refused BEFORE anything touches the target file...
                var before = System.IO.File.GetLastWriteTimeUtc(target);
                await Assert.ThrowsAsync<EntitlementException>(() => engine.ApplyDiffAsync(null, file, null, commit: true, "human"));
                Assert.Equal(before, System.IO.File.GetLastWriteTimeUtc(target));   // the refusal wrote nothing

                // ...but a single selected ref commits free — same rule as the session path.
                var one = await engine.ApplyDiffAsync(null, file, new[] { $"measure:{table.Name}/Gate Merge A" }, commit: true, "human");
                Assert.True(one.Applied);
                Assert.Equal(1, one.Count);
            }
            finally { System.IO.File.Delete(target); }
        }

        [Fact]
        public async Task Pro_tier_may_bulk_commit_a_file_merge()
        {
            using var engine = await OpenAsync(pro: true);
            var target = CopyFixtureTo("pro");
            try
            {
                var table = (await engine.ListTreeAsync(null)).First(t => t.Kind == "table");
                await engine.CreateMeasureAsync(table.Ref, "Gate Merge A", "1", "human");
                await engine.CreateMeasureAsync(table.Ref, "Gate Merge B", "2", "human");
                var r = await engine.ApplyDiffAsync(null, new ModelRef { Kind = "file", Path = target }, null, commit: true, "human");
                Assert.True(r.Applied);   // bulk — must NOT throw the gate
                Assert.True(r.Count >= 2);
            }
            finally { System.IO.File.Delete(target); }
        }

        [Fact]
        public async Task Free_tier_is_refused_build_model_from_spec()
        {
            using var engine = await OpenAsync(pro: false);
            await engine.AutogenerateSpecFromModelAsync("human");   // a spec must EXIST so the gate (not the no-spec throw) fires
            await Assert.ThrowsAsync<EntitlementException>(() => engine.BuildModelFromSpecAsync("human"));
        }

        [Fact]
        public async Task Pro_tier_may_build_model_from_spec()
        {
            using var engine = await OpenAsync(pro: true);
            await engine.AutogenerateSpecFromModelAsync("human");
            Assert.NotNull(await engine.BuildModelFromSpecAsync("human"));   // no gate throw (objects skip as already-existing)
        }

        [Fact]
        public async Task get_entitlement_reports_the_tier_on_both_engines()
        {
            using var free = await OpenAsync(pro: false);
            Assert.Equal("free", (await free.GetEntitlementAsync()).Tier);
            using var pro = await OpenAsync(pro: true);
            Assert.Equal("pro", (await pro.GetEntitlementAsync()).Tier);
        }
    }
}
