using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The shipped verified-measure seed and the separately versioned hard-measure authoring template.
    ///
    /// UX16 shortened the STOCK file's person-facing form to four steps (requirement, expected values,
    /// candidate, reconcile) while keeping its id, version, triggers and the whole of its assistant
    /// instructions. The witness, battery, hard equivalence gate and finalize work that used to occupy
    /// Steps 4 to 7 now all land on Step 4, so the run collects the witness ONCE.
    ///
    /// WHAT THAT COST, named here rather than left for the next reader to discover: the stock file no
    /// longer re-collects `witnessDax` at a later step, so the engine's witness-revision receipt between
    /// two collection points is not exercised by the stock corpus any more, and there is no second hard
    /// equivalence gate after a performance rewrite. The FEATURED TEMPLATE still has the seven-step shape
    /// and is asserted below unchanged, so those engine mechanics keep a live definition behind them.
    /// </summary>
    public sealed class HardMeasureWorkflowTests
    {
        private sealed class Free : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "free" };
        }

        private static AnswerValue Answer(string value) => new AnswerValue { Value = value };
        private static AnswerValue Decline(string reason) => new AnswerValue { Declined = true, DeclineReason = reason };

        private static WorkflowDef StockDefinition()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "workflows", "verified-measure.md");
            Assert.True(File.Exists(path), $"stock verified-measure seed was not copied beside the test binary: {path}");
            var def = WorkflowParser.Parse(File.ReadAllText(path));
            Assert.Null(def.Error);
            return def;
        }

        private static async Task<WorkflowRunState> RunThroughLockedAnchors(WorkflowDef def, string anchors)
        {
            var run = new WorkflowRunStore().Start(def, null);
            await WorkflowRunner.SubmitStepAsync(run, "step-1", new Dictionary<string, AnswerValue>
            {
                ["requirement"] = Answer("Pinned requirement, additivity per dimension, model facts confirmed"),
                ["clarification"] = Decline("nothing was ambiguous"),
            }, null);
            await WorkflowRunner.SubmitStepAsync(run, "step-2", new Dictionary<string, AnswerValue>
            {
                ["expectedValues"] = Answer(anchors),
                ["equivalenceGrid"] = Answer("'Product'[Category]"),
                ["openGrains"] = Decline("every grain is anchored"),
                ["naiveForm"] = Answer("Naive form diverges at the pinned grand total"),
            }, null);
            return run;
        }

        private static WorkflowVerifyExecutor AnchorExecutor(double actual, List<double?> enforced) => (spec, step, run, all) =>
        {
            if (!all.TryGetValue(spec.Anchors, out var answer) || !answer.Answered)
                return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "unavailable", Missing = "an answered anchor set", Detail = "anchors unanswered" });
            var anchors = AnchorGate.Parse(answer.Value, out var error);
            if (anchors == null || anchors.Length != 1)
                return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "unavailable", Missing = "one valid anchor", Detail = error ?? "wrong anchor count" });
            enforced.Add(anchors[0].Number);
            var matches = AnchorGate.Matches(anchors[0], actual, out var actualLabel);
            return Task.FromResult(new VerifyResult
            {
                Kind = spec.Kind,
                Status = matches ? "passed" : "failed",
                Detail = matches ? "expected value matched" : $"expected {anchors[0].ExpectLabel} but got {actualLabel}",
            });
        };

        [Fact]
        public async Task Stock_four_step_form_and_featured_v5_template_expose_their_verified_witness_contracts()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "smx-hard-v6-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(workspace);
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro, workspace);
            try
            {
                var canonical = await engine.GetWorkflowAsync("verified-measure");
                var template = await engine.GetWorkflowTemplateAsync("hard-measure");
                Assert.Null(canonical.Error);
                Assert.Null(template.Error);
                Assert.Equal(7, canonical.Version);
                Assert.Equal(5, template.Version);
                Assert.Equal("Test a complex measure", canonical.Title);
                Assert.Equal(new[] { "measure_goal", "measure_pattern" }, template.Slots.Select(x => x.Name));
                Assert.DoesNotContain(template.Slots, x => x.Name.Contains("control", StringComparison.OrdinalIgnoreCase));

                var values = JsonSerializer.Serialize(new
                {
                    measure_goal = "year-on-year revenue growth percentage",
                    measure_pattern = "year-over-year over the marked date table",
                });
                await engine.InstantiateWorkflowTemplateAsync("hard-measure", "hard-measure-v5-test", values, "human");
                var featured = await engine.GetWorkflowAsync("hard-measure-v5-test");

                Assert.Null(featured.Error);
                Assert.Equal(5, featured.Version);
                // The stock file is the SHORT form now; the template keeps the long one.
                Assert.Equal(4, canonical.Steps.Length);
                Assert.Equal(7, featured.Steps.Length);

                foreach (var workflow in new[] { canonical, featured })
                {
                    var instructions = string.Join("\n", workflow.Steps.Select(x => x.Instructions));
                    Assert.Contains("raw-row witness", instructions, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("wrong-denominator", instructions, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("fully-crossed leaf", instructions, StringComparison.OrdinalIgnoreCase);
                    // the retired v4 framing must be fully gone from both surfaces
                    Assert.DoesNotContain("independent raw-row oracle", instructions, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("control total", instructions, StringComparison.OrdinalIgnoreCase);
                }

                var canonicalInstructions = string.Join("\n", canonical.Steps.Select(x => x.Instructions));
                Assert.Contains("SARGable", canonicalInstructions, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("bare FILTER over ALL", canonicalInstructions, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("GROUPED row extract", canonicalInstructions, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("OUTSIDE DAX", canonicalInstructions, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("MODEL FLOOR", canonicalInstructions, StringComparison.OrdinalIgnoreCase);
                // The context ledger moved OUT of a gate question and into the assistant instructions; if it
                // is in neither place the workflow has quietly dropped the discipline it exists to enforce.
                Assert.Contains("CONTEXT LEDGER", canonicalInstructions, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(canonical.Steps[0].Gate.Inputs, i => i.Name == "contextLedger" || i.Name == "modelFacts");

                // Step 1 asks two things and reads the model facts itself.
                Assert.Equal(new[] { "requirement", "clarification" }, canonical.Steps[0].Gate.Inputs.Select(i => i.Name).ToArray());
                Assert.Contains("get_grounding", canonical.Steps[0].Ops);

                var anchorInput = canonical.Steps[1].Gate.Inputs.Single(i => i.Name == "expectedValues");
                Assert.Contains("fenced JSON array", anchorInput.Question, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("required", canonical.Steps[1].Gate.Inputs.Single(i => i.Name == "equivalenceGrid").Required);
                Assert.Equal("answer-or-decline", canonical.Steps[1].Gate.Inputs.Single(i => i.Name == "openGrains").Required);
                var coverage = Assert.Single(canonical.Steps[1].Gate.Verify);
                Assert.Equal("anchor_coverage", coverage.Kind);
                Assert.Equal("expectedValues", coverage.Anchors);

                var candidateRevision = canonical.Steps[2].Gate.Inputs.Single(i => i.Name == "expectedValues");
                Assert.Equal("optional", candidateRevision.Required);
                Assert.Contains("REQUIRED RECEIPT", candidateRevision.Question, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("leave unanswered", candidateRevision.Question, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("originalExpect", candidateRevision.Question, StringComparison.Ordinal);
                Assert.Contains("correctedExpect", candidateRevision.Question, StringComparison.Ordinal);
                Assert.Contains("extractQuery", candidateRevision.Question, StringComparison.Ordinal);
                Assert.Contains("row-returning", candidateRevision.Question, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("without changing its contexts", candidateRevision.Question, StringComparison.OrdinalIgnoreCase);
                var candidateAnchors = Assert.Single(canonical.Steps[2].Gate.Verify);
                Assert.Equal("expected_values", candidateAnchors.Kind);
                Assert.Equal("expectedValues", candidateAnchors.Anchors);

                // Step 4 is the single reconcile-and-finish gate: the witness is collected once, purity
                // scanned, and may not be declined, and the same gate re-proves the locked anchors.
                var gateWitness = canonical.Steps[3].Gate.Inputs.Single(i => i.Name == "witnessDax");
                Assert.Equal("required", gateWitness.Required);
                Assert.Equal("no-bare-measures", gateWitness.DaxPurity);
                Assert.Contains("may not be declined", gateWitness.Question, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("hard", canonical.Steps[3].Gate.Strictness);
                Assert.DoesNotContain(canonical.Steps[3].Gate.Inputs, i => i.Name == "openShapes" || i.Name == "equivalenceGrid");
                Assert.Equal("optional", canonical.Steps[3].Gate.Inputs.Single(i => i.Name == "countersign").Required);
                Assert.Equal("required", canonical.Steps[3].Gate.Inputs.Single(i => i.Name == "certificate").Required);
                Assert.Equal(2, canonical.Steps[3].Gate.Verify.Length);
                var equality = canonical.Steps[3].Gate.Verify.Single(v => v.Kind == "dax_equivalence");
                Assert.Null(equality.When);
                Assert.Equal("witnessDax", equality.Probe);
                Assert.Equal("openGrains", equality.OpenShapesFrom);
                Assert.Equal("countersign", equality.OpenMismatch);
                var finalAnchors = canonical.Steps[3].Gate.Verify.Single(v => v.Kind == "expected_values");
                Assert.Null(finalAnchors.When);
                Assert.Equal("expectedValues", finalAnchors.Anchors);

                // The featured template remains the separately versioned v5 authoring template, and is the
                // only definition still exercising the two-collection-point witness lock.
                Assert.Empty(featured.Steps[2].Gate.Verify);
                Assert.Equal(new[] { "witnessDax" }, featured.Steps[3].Gate.Inputs.Select(i => i.Name).ToArray());
                Assert.DoesNotContain(featured.Steps[5].Gate.Inputs, i => i.Name == "witnessDax");
                var featuredPerf = Assert.Single(featured.Steps[6].Gate.Verify);
                Assert.Equal("dax_equivalence", featuredPerf.Kind);
                Assert.Equal("inputs.perfPass.answered", featuredPerf.When);
                Assert.Equal("openShapes", featuredPerf.OpenShapesFrom);
            }
            finally { Directory.Delete(workspace, true); }
        }

        [Fact]
        public async Task Stock_step3_receipted_revision_shadows_step2_anchor_for_the_hard_gate()
        {
            var def = StockDefinition();
            var revisionInput = def.Steps[2].Gate.Inputs.Single(i => i.Name == "expectedValues");
            Assert.Equal("optional", revisionInput.Required);
            Assert.Contains("REQUIRED RECEIPT", revisionInput.Question, StringComparison.OrdinalIgnoreCase);

            var run = await RunThroughLockedAnchors(def, "[{\"context\":{},\"expect\":100}]");
            var revision = "EXPECTATION REVISION RECEIPT\nOriginal grouped extract: 100. Corrected grouped extract and arithmetic: 200.\n```json\n[{\"context\":{},\"expect\":200}]\n```";
            var enforced = new List<double?>();
            await WorkflowRunner.SubmitStepAsync(run, "step-3", new Dictionary<string, AnswerValue>
            {
                ["candidate"] = Answer("200"),
                ["target"] = Answer("measure:Sales/Candidate"),
                ["expectedValues"] = Answer(revision),
            }, AnchorExecutor(200, enforced));

            Assert.Equal("passed", run.Results[2].Status);
            Assert.Equal(200d, Assert.Single(enforced));
            Assert.Equal(revision, WorkflowRunner.AllAnswers(run)["expectedValues"].Value);
        }

        [Fact]
        public async Task Stock_step3_unanswered_revision_inherits_step2_anchor_for_the_hard_gate()
        {
            var def = StockDefinition();
            Assert.Equal("optional", def.Steps[2].Gate.Inputs.Single(i => i.Name == "expectedValues").Required);
            var initial = "[{\"context\":{},\"expect\":100}]";
            var run = await RunThroughLockedAnchors(def, initial);
            var enforced = new List<double?>();

            await WorkflowRunner.SubmitStepAsync(run, "step-3", new Dictionary<string, AnswerValue>
            {
                ["candidate"] = Answer("100"),
                ["target"] = Answer("measure:Sales/Candidate"),
            }, AnchorExecutor(100, enforced));

            Assert.Equal("passed", run.Results[2].Status);
            Assert.Equal(100d, Assert.Single(enforced));
            Assert.False(run.Results[2].Answers.ContainsKey("expectedValues"));
            Assert.Equal(initial, WorkflowRunner.AllAnswers(run)["expectedValues"].Value);
        }

        /// <summary>Step 4 refuses a DECLINED witness and proves the one actually submitted.
        ///
        /// This replaces the old step-6 test, and the difference is worth stating: that test proved a
        /// decline could not clobber a witness LOCKED AT AN EARLIER STEP, because the seven-step form
        /// collected `witnessDax` three times. The four-step form collects it once, so the clobber
        /// scenario no longer exists in the stock corpus and only the decline refusal is left to prove
        /// here. The multi-collection lock is still exercised by the hard-measure template.</summary>
        [Fact]
        public async Task Stock_step4_refuses_a_declined_witness_and_proves_the_submitted_one()
        {
            var def = StockDefinition();
            Assert.Equal("required", def.Steps[3].Gate.Inputs.Single(i => i.Name == "witnessDax").Required);
            var run = await RunThroughLockedAnchors(def, "[{\"context\":{},\"expect\":100}]");
            WorkflowVerifyExecutor pass = (spec, step, state, all) =>
                Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed", Detail = "passed for setup" });

            await WorkflowRunner.SubmitStepAsync(run, "step-3", new Dictionary<string, AnswerValue>
            {
                ["candidate"] = Answer("100"),
                ["target"] = Answer("measure:Sales/Candidate"),
            }, pass);

            var calls = 0;
            string resolvedWitness = null;
            WorkflowVerifyExecutor capture = (spec, step, state, all) =>
            {
                if (spec.Kind != "dax_equivalence")
                    return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed", Detail = "not the probe under test" });
                if (!all.TryGetValue(spec.Probe, out var probe) || !probe.Answered)
                    return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "unavailable", Missing = "an answered witness", Detail = "probe unanswered" });
                calls++;
                resolvedWitness = probe.Value;
                return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed", Detail = "witness evaluated" });
            };

            var common = new Dictionary<string, AnswerValue>
            {
                ["battery"] = Answer("candidate and witness agree across the full battery"),
                ["adjudications"] = Decline("no disagreements"),
                ["perfPass"] = Decline("no comparable live environment"),
                ["finalized"] = Answer("Net Sales, #,0, description carries the pinned conventions"),
                ["certificate"] = Answer("FULL"),
            };
            var withDecline = new Dictionary<string, AnswerValue>(common)
            {
                ["witnessDax"] = Decline("unchanged"),
            };

            var declined = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WorkflowRunner.SubmitStepAsync(run, "step-4", withDecline, capture));
            Assert.Contains("may not be declined", declined.Message);
            Assert.Equal(0, calls);

            var submitted = new Dictionary<string, AnswerValue>(common)
            {
                ["witnessDax"] = Answer("EVALUATE ROW(\"v\", 42)"),
            };
            await WorkflowRunner.SubmitStepAsync(run, "step-4", submitted, capture);
            Assert.Equal("passed", run.Results[3].Status);
            Assert.Equal(1, calls);
            Assert.Equal("EVALUATE ROW(\"v\", 42)", resolvedWitness);
            Assert.Equal("EVALUATE ROW(\"v\", 42)", WorkflowRunner.AllAnswers(run)["witnessDax"].Value);
        }
    }
}
