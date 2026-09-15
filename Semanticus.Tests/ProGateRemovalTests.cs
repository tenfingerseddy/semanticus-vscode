using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The 27 gates Kane removed on 2026-09-15: 17 hard and 10 soft. Every bulk apply, Verified Mode, the
    /// agent policy matrix, vitals and blame, optimize auto-apply, primer suggestions and interview questions are
    /// free now. Each test here failed before the removal and passes after, so "it is free" is proven rather than
    /// asserted from the source.</summary>
    public sealed class ProGateRemovalTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static LocalEngine Free() => new LocalEngine(new SessionManager(), new Fake(false));

        private static async Task<LocalEngine> FreeWithModelAsync()
        {
            var e = Free();
            await e.OpenAsync(TestModels.FindBim());
            return e;
        }

        /// <summary>A removal is proven when the entitlement gate is gone. Everything else an operation may still
        /// refuse for (no live connection, nothing to do, a bad ref) is not this test's business.</summary>
        private static async Task NoEntitlementRefusalAsync(Func<Task> call, string what)
        {
            try { await call(); }
            catch (EntitlementException ex) { Assert.Fail(what + " is still Pro-gated on free: " + ex.Message); }
            catch { /* any other failure is out of scope for the gate removal */ }
        }

        // ---- the 17 hard gates ---------------------------------------------------------------------------------

        [Fact] // 1
        public async Task Free_may_apply_a_plan_with_more_than_one_item()
        {
            using var e = await FreeWithModelAsync();
            var two = (await e.ListMeasuresAsync()).Take(2).Select(m => m.Ref).ToArray();
            Assert.Equal(2, two.Length);
            foreach (var r in two)
            {
                var view = await e.AddPlanItemAsync(r, "description", "A description authored for this plan.", "describe", null, null, "human");
                var item = view.Items.Last();
                await e.SetPlanItemAsync(item.Id, null, true, "human");
            }
            var report = await e.ApplyPlanAsync(null, "human");
            Assert.NotNull(report);
        }

        [Fact] // 2
        public async Task Free_may_bpa_fix_all()
        {
            using var e = await FreeWithModelAsync();
            Assert.NotNull(await e.BpaFixAllAsync("human"));
        }

        [Fact] // 3
        public async Task Free_may_apply_safe_fixes()
        {
            using var e = await FreeWithModelAsync();
            Assert.NotNull(await e.ApplySafeFixesAsync("human"));
        }

        [Fact] // 4
        public async Task Free_may_make_the_model_ai_ready()
        {
            using var e = await FreeWithModelAsync();
            Assert.NotNull(await e.MakeAiReadyAsync("human", 50));
        }

        [Fact] // 5
        public async Task Free_may_remove_more_than_one_safe_object()
        {
            using var e = await FreeWithModelAsync();
            var report = await e.RemoveSafeObjectsAsync(null, null, "human");
            Assert.True(report.Count > 1, "the fixture must offer more than one verified-safe object to sweep");
        }

        [Fact] // 6 and 7
        public async Task Free_may_apply_a_multi_object_model_diff()
        {
            using var e = await FreeWithModelAsync();
            // Two real differences, so the gate's "more than one object" condition is actually reached.
            foreach (var r in (await e.ListMeasuresAsync()).Take(2).Select(m => m.Ref).ToArray())
                await e.DeleteObjectAsync(r, "human");

            var file = new ModelRef { Kind = "file", Path = TestModels.FindBim() };
            var session = new ModelRef { Kind = "session" };
            var diff = await e.CompareModelsAsync(file, session, false, "human");
            Assert.True(diff.Items.Count(i => i.Action != "Equal") > 1, "the fixture must differ by more than one object");

            var into = await e.ApplyDiffAsync(file, session, null, true, "human");
            Assert.NotNull(into);

            var copy = Path.Combine(AppContext.BaseDirectory, "semanticus-diff-" + Guid.NewGuid().ToString("N") + ".bim");
            File.Copy(TestModels.FindBim(), copy);
            try
            {
                var onto = await e.ApplyDiffAsync(session, new ModelRef { Kind = "file", Path = copy }, null, true, "human");
                Assert.NotNull(onto);
            }
            finally { try { File.Delete(copy); } catch { } }
        }

        [Fact] // 8
        public async Task Free_may_cherry_pick_more_than_one_object()
        {
            using var e = await FreeWithModelAsync();
            var two = (await e.ListMeasuresAsync()).Take(2).Select(m => m.Ref).ToArray();
            foreach (var r in two) await e.DeleteObjectAsync(r, "human");
            var source = new ModelRef { Kind = "file", Path = TestModels.FindBim() };
            var picked = await e.CherryPickAsync(source, two, false, true, "human");
            Assert.NotNull(picked);
        }

        [Fact] // 9
        public async Task Free_may_apply_a_multi_object_dax_script()
        {
            using var e = await FreeWithModelAsync();
            var two = (await e.ListMeasuresAsync()).Take(2).Select(m => m.Ref).ToArray();
            var script = await e.ScriptObjectsAsync(two, "dax");
            var result = await e.ApplyDaxScriptAsync(script, "human");
            Assert.NotNull(result);
        }

        [Fact] // 10
        public async Task Free_may_apply_a_multi_object_tmdl_script()
        {
            using var e = await FreeWithModelAsync();
            var two = (await e.ListMeasuresAsync()).Take(2).Select(m => m.Ref).ToArray();
            var script = await e.ScriptObjectsAsync(two, "tmdl");
            var result = await e.ApplyTmdlScriptAsync(script, "human");
            Assert.NotNull(result);
        }

        [Fact] // 11
        public async Task Free_may_waive_a_whole_rule()
        {
            using var e = await FreeWithModelAsync();
            var waived = await e.WaiveFindingAsync("bpa", "MEASURES_SHOULD_HAVE_DESCRIPTION", "*", "not for this model", "human");
            Assert.NotNull(waived);
        }

        [Fact] // 12
        public void Free_may_choose_an_agent_policy_preset()
        {
            var policy = AgentPolicyStore.SetPreset("balanced", "human");
            Assert.NotNull(policy);
        }

        [Fact] // 13
        public void Free_may_edit_one_agent_policy_cell()
        {
            var policy = AgentPolicyStore.SetCell("DeployLive", "prod", "ask", "human");
            Assert.NotNull(policy);
        }

        /// <summary>Removing the payment check must not remove the authority check. Only a human may rewrite the
        /// matrix that gates the agent, on any tier.</summary>
        [Fact]
        public void An_agent_still_may_not_change_the_agent_policy()
        {
            Assert.Throws<InvalidOperationException>(() => AgentPolicyStore.SetPreset("balanced", "agent"));
            Assert.Throws<InvalidOperationException>(() => AgentPolicyStore.SetCell("DeployLive", "prod", "ask", "agent"));
        }

        [Fact] // 14
        public async Task Free_may_turn_Verified_Mode_on()
        {
            using var e = Free();
            var state = await e.SetVerifiedModeAsync(true, "human");
            Assert.True(state.Enabled);
        }

        [Fact] // 15
        public async Task Free_may_export_the_verified_edits_trail()
        {
            using var e = await FreeWithModelAsync();
            Assert.NotNull(await e.ExportVerifiedEditsAsync("md"));
        }

        [Fact] // 16
        public async Task Free_may_add_an_interview_question()
        {
            using var e = await FreeWithModelAsync();
            var q = await e.AddInterviewQuestionAsync("What were total sales?", "value",
                "EVALUATE ROW(\"v\", 1)", null, null, null, null, "1", null, false, null, "human", "project", "human");
            Assert.NotNull(q);
        }

        [Fact] // 17
        public async Task Free_may_push_more_than_one_object_to_a_published_model()
        {
            using var e = await FreeWithModelAsync();
            var right = new ModelRef { Kind = "workspace", Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/Contoso", Database = "Contoso" };
            // Stated as its own assertion (the hygiene gate in Semanticus.Dax.Tests requires one per test), not only
            // through the helper: the push has no live destination here, so any refusal other than the entitlement
            // gate is expected and out of scope.
            var refusedByEntitlement = false;
            try { await e.ApplyDiffAsync(new ModelRef { Kind = "session" }, right, new[] { "measure:Sales/A", "measure:Sales/B" }, true, "human"); }
            catch (EntitlementException) { refusedByEntitlement = true; }
            catch { /* no live destination: out of scope for the gate removal */ }
            Assert.False(refusedByEntitlement, "selective push with 2 refs is still Pro-gated on free");
        }

        // ---- the 10 soft gates ---------------------------------------------------------------------------------

        [Fact] // soft 2
        public async Task Free_gets_a_real_blame_answer_not_a_Pro_note()
        {
            using var e = await FreeWithModelAsync();
            var r = await e.BlameValueAsync("measure:Internet Sales/Internet Total Sales", null, null, "human");
            Assert.NotEqual("pro", r.Status);
            Assert.NotEqual("pro", r.Verdict);
        }

        [Fact] // soft 3
        public async Task Free_gets_a_real_value_history_not_a_Pro_note()
        {
            using var e = await FreeWithModelAsync();
            var r = await e.ListValueHistoryAsync("measure:Internet Sales/Internet Total Sales", null);
            Assert.NotEqual("pro", r.Status);
        }

        [Fact] // soft 6
        public async Task Free_sees_its_primer_suggestions()
        {
            using var e = await FreeWithModelAsync();
            var r = await e.ListPrimerSuggestionsAsync();
            Assert.DoesNotContain("Pro feature", r.Note ?? string.Empty);
        }

        [Fact] // soft 7
        public async Task Free_may_accept_and_reject_a_primer_suggestion()
        {
            using var e = await FreeWithModelAsync();
            // Free now reaches the real lookup and gets the real "no such suggestion" answer, where it used to be
            // turned away at a tier check before the id was ever read.
            var accepted = await Assert.ThrowsAsync<InvalidOperationException>(
                () => e.AcceptPrimerSuggestionAsync("no-such-suggestion", "human"));
            Assert.DoesNotContain("Pro feature", accepted.Message);
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
                () => e.RejectPrimerSuggestionAsync("no-such-suggestion", "human"));
            Assert.DoesNotContain("Pro feature", rejected.Message);
        }

        [Fact] // soft 10
        public async Task Verified_Mode_is_available_on_free()
        {
            using var e = Free();
            Assert.True((await e.GetVerifiedModeAsync()).Available);
        }

        /// <summary>Soft 8 and 9, the health delta. The probe used to sit behind a lazy tier check, so a free edit
        /// reported a plain "rev N" and nothing else. It reports for everyone now.</summary>
        [Fact]
        public async Task Free_edits_carry_a_health_delta()
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Fake(false));
            await e.CreateModelAsync("HealthOnFree", 1604);
            var t = await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");

            ChangeNotification last = null;
            Action<ChangeNotification> handler = n => last = n;
            sm.Bus.Changed += handler;
            try { await e.CreateMeasureAsync(t, "undescribed measure", "1", "agent"); }
            finally { sm.Bus.Changed -= handler; }

            Assert.NotNull(last);
            Assert.NotNull(last.Health);
        }

        // ---- the removals that need a live endpoint, guarded at the source -------------------------------------

        /// <summary>Soft 4 and 5, optimize. Both sit past a benchmark that needs a real XMLA endpoint, so no test
        /// here can reach them on this machine. What CAN be proven is that the tier decision is gone from the
        /// source: no "paused-free" verdict, and no entitlement term deciding whether the outcome is recorded.
        /// This is a source guard, not a behaviour proof, and the report says so.</summary>
        [Fact]
        public void Optimize_no_longer_decides_anything_by_tier()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.Engine", "LocalEngine.cs"));
            Assert.DoesNotContain("paused-free", src);
            Assert.DoesNotContain("var recordOutcome = apply && (_entitlement?.IsPro ?? false);", src);
        }

        /// <summary>The obsolete tier plumbing Astra listed: the HealthProbe delegate, the two AgentPolicyStore
        /// isPro arguments and the no-model Primer display flag.</summary>
        [Fact]
        public void The_obsolete_tier_plumbing_is_gone()
        {
            var engine = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.Engine", "LocalEngine.cs"));
            var primer = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.Engine", "LocalEngine.Primer.cs"));
            var probe = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.Engine", "HealthProbe.cs"));
            var vitals = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.Engine", "LocalEngine.Vitals.cs"));
            Assert.DoesNotContain("() => _entitlement?.IsPro == true);", engine);
            Assert.DoesNotContain("_entitlement?.IsPro ?? false));", engine);
            Assert.DoesNotContain("IsPro = _entitlement?.IsPro == true", primer);
            Assert.DoesNotContain("private bool IsPro()", probe);
            Assert.DoesNotContain("_entitlement.IsPro", vitals);
        }

        /// <summary>Nothing in the engine reaches for the tier any more except the one feature gate and the one
        /// place effective grants are computed. A stray IsPro check is a gate nobody wrote down.</summary>
        [Fact]
        public void Only_the_feature_gate_consults_the_tier()
        {
            var root = Path.Combine(RepoRoot(), "Semanticus.Engine");
            var strays = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/Entitlement/"))
                .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (file: Path.GetFileName(f), no: i + 1, line)))
                .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.line, @"(?<![A-Za-z0-9])_entitlement")
                         && !x.line.TrimStart().StartsWith("//"))
                .Where(x => !x.line.Contains("RequirePro(_entitlement, feature, tab)")
                         && !x.line.Contains("private readonly Entitlement.IEntitlement _entitlement")
                         && !x.line.Contains("_entitlement = entitlement")
                         && !x.line.Contains("_entitlement.Info")                 // get_entitlement reads its own answer
                         && !x.line.Contains("_entitlement?.Info")                // the workflow 'tier' fact, not a gate
                         && !x.line.Contains("FeatureGrants.Stamp(_entitlement.Info, _entitlement)")
                         && !x.line.Contains("FeatureGrants.Grants(_entitlement"))
                .Select(x => x.file + ":" + x.no + " " + x.line.Trim())
                .ToArray();
            Assert.True(strays.Length == 0, "The tier is consulted outside the feature gate:\n" + string.Join("\n", strays));
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "Could not find the repo root above " + AppContext.BaseDirectory);
            return dir.FullName;
        }
    }
}
