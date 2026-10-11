using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The bulk-apply chokepoints, after Kane made every one of them free on 2026-09-15. These were the
    /// sold moat: apply_plan with more than one item, apply_safe_fixes (either system), make_model_ai_ready, the
    /// multi-object DAX and TMDL scripts and the bulk merge to a file. Every one is free now, and the fixtures that
    /// used to prove the refusal are kept here to prove the opposite, because a gate removed in the source but still
    /// firing somewhere is exactly what an untested removal looks like.
    ///
    /// The Pro line moved again on 2026-09-22, down to Workflows and the ask box; <see cref="ProFeatureGateTests"/>
    /// walks every remaining Pro operation on both doors. The model spec fixture below stayed here and now proves
    /// the opposite of what it used to, because Model Spec is free.</summary>
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
            await engine.OpenAsync(TestModels.FindBim());   // AdventureWorks, rich in auto-fixable findings
            return engine;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Free_may_bpa_fix_all()
        {
            using var engine = await OpenAsync(pro: false);
            Assert.NotNull(await engine.BpaFixAllAsync("human"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Free_may_apply_safe_fixes()
        {
            using var engine = await OpenAsync(pro: false);
            Assert.NotNull(await engine.ApplySafeFixesAsync("human"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Free_may_bulk_apply_a_plan()
        {
            using var engine = await OpenAsync(pro: false);
            var plan = await engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, "human");
            var approved = plan.Items.Where(i => i.Status == "approved").Select(i => i.Id).ToArray();
            Assert.True(approved.Length >= 2, $"need >=2 approved safe fixes to exercise the old bulk gate; got {approved.Length}");
            Assert.NotNull(await engine.ApplyPlanAsync(Array.Empty<string>(), "human"));   // all approved, more than one
        }

        [Fact]
        public async Task Free_may_make_the_model_ai_ready()
        {
            using var engine = await OpenAsync(pro: false);
            Assert.NotNull(await engine.MakeAiReadyAsync("human", 10));
        }

        [Fact]
        public async Task Free_may_apply_a_multi_block_dax_script()
        {
            using var engine = await OpenAsync(pro: false);
            var ms = (await engine.ListMeasuresAsync()).Take(2).ToArray();
            Assert.True(ms.Length >= 2, "need >=2 measures to exercise the old multi-block gate");
            string Block(string r) => $"// @object {r}\n1\n";
            Assert.NotNull(await engine.ApplyDaxScriptAsync(Block(ms[0].Ref) + Block(ms[1].Ref), "human"));
        }

        [Fact]
        public async Task Free_may_apply_a_multi_object_tmdl_script()
        {
            using var engine = await OpenAsync(pro: false);
            var twoDocs = "table 'GateTmdlA'\n\tlineageTag: a\ntable 'GateTmdlB'\n\tlineageTag: b\n";
            Assert.NotNull(await engine.ApplyTmdlScriptAsync(twoDocs, "human"));
        }

        private static string CopyFixtureTo(string suffix)
        {
            var target = System.IO.Path.Combine(AppContext.BaseDirectory, $"sem-gate-{suffix}-{Guid.NewGuid():N}.bim");
            System.IO.File.Copy(TestModels.FindBim(), target);
            return target;
        }

        /// <summary>The merge-arbitrage case: the file-write branch used to gate more than one object exactly like
        /// the session path. Both are free now, and the file is really written.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Free_may_bulk_commit_a_file_merge()
        {
            using var engine = await OpenAsync(pro: false);
            var target = CopyFixtureTo("free");
            try
            {
                var table = (await engine.ListTreeAsync(null)).First(t => t.Kind == "table");
                await engine.CreateMeasureAsync(table.Ref, "Gate Merge A", "1", "human");
                await engine.CreateMeasureAsync(table.Ref, "Gate Merge B", "2", "human");
                var file = new ModelRef { Kind = "file", Path = target };

                var preview = await engine.ApplyDiffAsync(null, file, null, commit: false, "human");
                Assert.False(preview.Applied);
                Assert.True(preview.Count >= 2, $"expected >=2 applicable diff items, got {preview.Count}");

                var r = await engine.ApplyDiffAsync(null, file, null, commit: true, "human",
                    overrideReason: null, confirmToken: preview.ConfirmToken);
                Assert.True(r.Applied);
                Assert.True(r.Count >= 2);
            }
            finally { try { System.IO.File.Delete(target); } catch { } }
        }

        // ---- what is left paid, on the one fixture this file already owned --------------------------------------

        /// <summary>Model Spec went free on 22 September 2026 with the other seven tabs. This fixture used to
        /// prove the refusal and now proves there is none, for the same reason the bulk-apply fixtures above were
        /// kept: a gate removed in the source but still firing somewhere is what an untested removal looks like.</summary>
        [Fact]
        public async Task Free_builds_a_model_from_a_spec_like_anybody_else()
        {
            using var free = await OpenAsync(pro: false);
            await free.AutogenerateSpecFromModelAsync("human");
            Assert.NotNull(await free.BuildModelFromSpecAsync("human"));
        }

        /// <summary>Workflows is still whole-tab Pro, and the refusal still names the tab a person would open
        /// rather than the enum.</summary>
        [Fact]
        public async Task Free_is_refused_the_workflow_library_and_Pro_is_not()
        {
            using (var free = await OpenAsync(pro: false))
            {
                var ex = await Assert.ThrowsAsync<EntitlementException>(() => free.ListWorkflowsAsync());
                Assert.StartsWith("Workflows is a Semanticus Pro feature.", ex.Message);
            }
            using var pro = await OpenAsync(pro: true);
            Assert.NotNull(await pro.ListWorkflowsAsync());
        }

        [Fact]
        public async Task get_entitlement_reports_the_tier_and_the_grants_on_both_engines()
        {
            using var free = await OpenAsync(pro: false);
            var f = await free.GetEntitlementAsync();
            Assert.Equal("free", f.Tier);
            Assert.Empty(f.Features);

            using var pro = await OpenAsync(pro: true);
            var p = await pro.GetEntitlementAsync();
            Assert.Equal("pro", p.Tier);
            // One tab feature since 22 September 2026. The ask box is paid too and carries no wire id.
            Assert.Equal(new[] { "workflows", "askData" }, p.Features);
        }

        /// <summary>A refusal still teaches: it never leaks where the licence lives.</summary>
        [Fact]
        public async Task A_refusal_names_no_licence_plumbing()
        {
            using var engine = await OpenAsync(pro: false);
            var ex = await Assert.ThrowsAsync<EntitlementException>(() => engine.ListWorkflowsAsync());
            Assert.DoesNotContain("SEMANTICUS_LICENSE", ex.Message);
            Assert.DoesNotContain("~/.semanticus", ex.Message);
        }
    }
}
