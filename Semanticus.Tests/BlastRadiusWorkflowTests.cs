using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>T89 pins the shipped Check blast radius workflow as a real enforced composition: T87 supplies
    /// the assessment, recorded capture ids are engine-checked, Tests replay through the coordinator, and the
    /// terminal run renders through the one shared sealed evidence path.</summary>
    public sealed class BlastRadiusWorkflowTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private static string TempCopyOfFixture()
        {
            var dir = Path.Combine(Path.GetTempPath(), "smx-blast-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "AdventureWorks.bim");
            File.Copy(TestModels.FindBim(), bim);
            return bim;
        }

        private static string Step1Answers(string target) =>
            "{\"target\":\"" + target + "\",\"changeIntent\":\"Clarify the business name\"," +
            "\"reportPaths\":{\"declined\":true,\"reason\":\"No report definitions are in this review scope\"}}";

        [Fact]
        public async Task Stock_workflow_is_admissible_and_model_only_scope_is_explicit()
        {
            var bim = TempCopyOfFixture();
            try
            {
                using var engine = new LocalEngine(new SessionManager(), new Pro());
                await engine.OpenAsync(bim);
                var check = await engine.CheckWorkflowAsync("check-blast-radius");
                Assert.True(check.Ok, string.Join(" | ", check.Findings.Select(x => x.Message)));

                var target = (await engine.ListMeasuresAsync()).First().Ref;
                var started = await engine.StartWorkflowAsync("check-blast-radius", "human");
                var next = await engine.SubmitWorkflowStepAsync(started.RunId, "step-1", Step1Answers(target), "human");

                Assert.Equal("step-2", next.CurrentStep.StepId);
                var assessment = next.Steps[0].VerifyResults.Single(x => x.Kind == "impact_assessment");
                Assert.Equal("passed", assessment.Status);
                Assert.Contains("Scope: model", assessment.Detail);
                Assert.Contains("reports=excluded", assessment.Detail);
            }
            finally { Directory.Delete(Path.GetDirectoryName(bim), true); }
        }

        [Fact]
        public async Task A_pasted_baseline_id_cannot_pass_as_measured_evidence()
        {
            var bim = TempCopyOfFixture();
            try
            {
                using var engine = new LocalEngine(new SessionManager(), new Pro());
                await engine.OpenAsync(bim);
                var target = (await engine.ListMeasuresAsync()).First().Ref;
                var started = await engine.StartWorkflowAsync("check-blast-radius", "human");
                var step2 = await engine.SubmitWorkflowStepAsync(started.RunId, "step-1", Step1Answers(target), "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                    step2.RunId, "step-2", "{\"baselineCapture\":\"bl-made-up\"}", "human"));

                Assert.Contains("not held in this model session", ex.Message);
                var failed = await engine.GetWorkflowRunAsync(step2.RunId);
                Assert.Equal("failed", failed.Steps[1].Status);
                Assert.Equal("failed", failed.Steps[1].VerifyResults.Single(x => x.Kind == "baseline_exists").Status);
            }
            finally { Directory.Delete(Path.GetDirectoryName(bim), true); }
        }

        [Fact]
        public async Task Offline_honest_run_replays_tests_and_exports_the_shared_certificate()
        {
            var bim = TempCopyOfFixture();
            try
            {
                using var engine = new LocalEngine(new SessionManager(), new Pro());
                await engine.OpenAsync(bim);
                var target = (await engine.ListMeasuresAsync()).First().Ref;
                var run = await engine.StartWorkflowAsync("check-blast-radius", "human");
                run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", Step1Answers(target), "human");
                run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-2",
                    "{\"baselineCapture\":{\"declined\":true,\"reason\":\"No live query model is connected\"}}", "human");
                run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-3",
                    "{\"replayInterview\":{\"declined\":true,\"reason\":\"No current-model Interview pack was scheduled\"}}", "human");
                var replay = run.Steps[2].VerifyResults.Single(x => x.Kind == "tests_replay");
                Assert.Equal("passed", replay.Status);
                Assert.Contains("coverage", replay.Detail);
                run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-4",
                    "{\"decision\":\"Proceed to reviewed implementation\",\"residualRisk\":\"Report bindings were explicitly excluded\"}", "human");
                run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-5",
                    "{\"certificateDelivery\":\"Save with model\"}", "human");

                Assert.Equal("completed", run.Status);
                var artifact = await engine.ExportWorkflowEvidenceAsync(run.RunId);
                Assert.Null(artifact.Error);
                Assert.False(string.IsNullOrWhiteSpace(artifact.ContentHash));
                Assert.Contains("check-blast-radius", artifact.Json);
                Assert.Contains("impact_assessment", artifact.Json);
                Assert.Contains("tests_replay", artifact.Json);
                Assert.Contains("<!doctype html>", artifact.Html, StringComparison.OrdinalIgnoreCase);
            }
            finally { Directory.Delete(Path.GetDirectoryName(bim), true); }
        }
    }
}
