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
                ["requirement"] = Answer("Pinned requirement"),
                ["modelFacts"] = Answer("Recorded model facts"),
                ["contextLedger"] = Answer("All contexts pinned"),
                ["clarification"] = Decline("none needed"),
            }, null);
            await WorkflowRunner.SubmitStepAsync(run, "step-2", new Dictionary<string, AnswerValue>
            {
                ["expectedValues"] = Answer(anchors),
                ["externalAnchors"] = Decline("none available"),
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
        public async Task Stock_v6_and_featured_v5_template_expose_their_verified_witness_contracts()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "smx-hard-v6-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(workspace);
            using var engine = new LocalEngine(new SessionManager(), new Free(), workspace);
            try
            {
                var canonical = await engine.GetWorkflowAsync("verified-measure");
                var template = await engine.GetWorkflowTemplateAsync("hard-measure");
                Assert.Null(canonical.Error);
                Assert.Null(template.Error);
                Assert.Equal(6, canonical.Version);
                Assert.Equal(5, template.Version);
                Assert.Equal("Author a hard DAX measure, reconciled against the requirement at every grain", canonical.Title);
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
                Assert.Equal(7, canonical.Steps.Length);
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

                    // Step 4 runs no verify and precedes the battery, so the witness is on the record before any
                    // comparison result exists.
                    var witnessStep = workflow.Steps[3];
                    Assert.Empty(witnessStep.Gate.Verify);
                    Assert.Empty(witnessStep.Ops);
                    Assert.Contains(workflow.Steps[4].Gate.Inputs, i => i.Name == "battery");
                    // Step 5 re-collects the CURRENT witness under the same name (run-wide answers are
                    // last-answered-wins) as a required input, so a decline can never clobber the locked value.
                    var recollected = workflow.Steps[4].Gate.Inputs.Single(i => i.Name == "witnessDax");
                    Assert.Equal("required", recollected.Required);

                    // Step 6 probes the locked witness with a per-run open-shape partition and fires unconditionally.
                    var equality = Assert.Single(workflow.Steps[5].Gate.Verify);
                    Assert.Equal("dax_equivalence", equality.Kind);
                    Assert.Null(equality.When);
                    Assert.Equal("witnessDax", equality.Probe);
                    Assert.Equal("openShapes", equality.OpenShapesFrom);
                    // the machine partition field and the human certificate are SEPARATE inputs
                    Assert.Equal("answer-or-decline", workflow.Steps[5].Gate.Inputs.Single(i => i.Name == "openShapes").Required);
                    Assert.Equal("required", workflow.Steps[5].Gate.Inputs.Single(i => i.Name == "certificate").Required);

                    Assert.DoesNotContain(workflow.Steps[6].Gate.Inputs, i => i.Name == "openShapes");
                }

                var canonicalInstructions = string.Join("\n", canonical.Steps.Select(x => x.Instructions));
                Assert.Contains("SARGable", canonicalInstructions, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("bare FILTER over ALL", canonicalInstructions, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("GROUPED row extract", canonicalInstructions, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("OUTSIDE DAX", canonicalInstructions, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("MODEL FLOOR", canonicalInstructions, StringComparison.OrdinalIgnoreCase);

                var anchorInput = canonical.Steps[1].Gate.Inputs.Single(i => i.Name == "expectedValues");
                Assert.Contains("fenced JSON array", anchorInput.Question, StringComparison.OrdinalIgnoreCase);
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

                Assert.Equal(new[] { "witnessDax", "witnessTiming" }, canonical.Steps[3].Gate.Inputs.Select(i => i.Name).ToArray());
                var gateWitness = canonical.Steps[5].Gate.Inputs.Single(i => i.Name == "witnessDax");
                Assert.Equal("required", gateWitness.Required);
                Assert.Contains("Restate the Step-5 witness verbatim", gateWitness.Question, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("may not be declined", gateWitness.Question, StringComparison.OrdinalIgnoreCase);

                // Step 7 re-proves the latest receipted anchors and witness equality. The equality check inherits
                // the Step 6 partition, so a performance rewrite cannot quietly re-open a shape it broke.
                var finalRevision = canonical.Steps[6].Gate.Inputs.Single(i => i.Name == "expectedValues");
                Assert.Equal("optional", finalRevision.Required);
                Assert.Contains("REQUIRED RECEIPT", finalRevision.Question, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("leave unanswered", finalRevision.Question, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("originalExpect", finalRevision.Question, StringComparison.Ordinal);
                Assert.Contains("correctedExpect", finalRevision.Question, StringComparison.Ordinal);
                Assert.Contains("extractQuery", finalRevision.Question, StringComparison.Ordinal);
                Assert.Equal(2, canonical.Steps[6].Gate.Verify.Length);
                var perfAnchors = canonical.Steps[6].Gate.Verify.Single(v => v.Kind == "expected_values");
                Assert.Null(perfAnchors.When);
                Assert.Equal("expectedValues", perfAnchors.Anchors);
                var perfEquality = canonical.Steps[6].Gate.Verify.Single(v => v.Kind == "dax_equivalence");
                Assert.Equal("inputs.perfPass.answered", perfEquality.When);
                Assert.Equal("witnessDax", perfEquality.Probe);
                Assert.Equal("openShapes", perfEquality.OpenShapesFrom);

                // The featured template remains the separately versioned v5 authoring template.
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

        [Fact]
        public async Task Stock_step6_rejects_decline_then_omission_evaluates_the_step5_witness()
        {
            var def = StockDefinition();
            Assert.Equal("required", def.Steps[5].Gate.Inputs.Single(i => i.Name == "witnessDax").Required);
            var run = await RunThroughLockedAnchors(def, "[{\"context\":{},\"expect\":100}]");
            WorkflowVerifyExecutor pass = (spec, step, state, all) =>
                Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed", Detail = "passed for setup" });

            await WorkflowRunner.SubmitStepAsync(run, "step-3", new Dictionary<string, AnswerValue>
            {
                ["candidate"] = Answer("100"),
                ["target"] = Answer("measure:Sales/Candidate"),
            }, pass);
            await WorkflowRunner.SubmitStepAsync(run, "step-4", new Dictionary<string, AnswerValue>
            {
                ["witnessDax"] = Answer("EVALUATE ROW(\"v\", 42)"),
                ["witnessTiming"] = Answer("representative query: 0.2 seconds"),
            }, null);
            await WorkflowRunner.SubmitStepAsync(run, "step-5", new Dictionary<string, AnswerValue>
            {
                ["battery"] = Answer("candidate and witness agree across the full battery"),
                ["witnessDax"] = Answer("EVALUATE ROW(\"v\", 42)"),
                ["adjudications"] = Decline("no disagreements"),
            }, null);

            var calls = 0;
            string resolvedWitness = null;
            WorkflowVerifyExecutor capture = (spec, step, state, all) =>
            {
                if (!all.TryGetValue(spec.Probe, out var probe) || !probe.Answered)
                    return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "unavailable", Missing = "an answered witness", Detail = "probe unanswered" });
                calls++;
                resolvedWitness = probe.Value;
                return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed", Detail = "witness evaluated" });
            };
            var common = new Dictionary<string, AnswerValue>
            {
                ["equivalenceGrid"] = Answer("'Product'[Category]"),
                ["openShapes"] = Decline("all evaluated shapes are pinned"),
                ["certificate"] = Answer("FULL"),
            };
            var withDecline = new Dictionary<string, AnswerValue>(common)
            {
                ["witnessDax"] = Decline("unchanged"),
            };

            var declined = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WorkflowRunner.SubmitStepAsync(run, "step-6", withDecline, capture));
            Assert.Contains("may not be declined", declined.Message);
            Assert.Equal(0, calls);

            await WorkflowRunner.SubmitStepAsync(run, "step-6", common, capture);
            Assert.Equal("passed", run.Results[5].Status);
            Assert.Equal(1, calls);
            Assert.Equal("EVALUATE ROW(\"v\", 42)", resolvedWitness);
            Assert.False(run.Results[5].Answers.ContainsKey("witnessDax"));
            Assert.Equal("EVALUATE ROW(\"v\", 42)", WorkflowRunner.AllAnswers(run)["witnessDax"].Value);
        }
    }
}
