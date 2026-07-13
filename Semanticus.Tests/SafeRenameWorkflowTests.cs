using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Semanticus.Engine.Lineage;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>T90 proves the existing governed-rename id is now the one Safe rename funnel: one T87 assessment,
    /// one proposed Change Plan item, one FormulaFixup apply, replayed evidence and the shared sealed certificate.</summary>
    public sealed class SafeRenameWorkflowTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private static string TempCopyOfFixture()
        {
            var dir = Path.Combine(Path.GetTempPath(), "smx-rename-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "AdventureWorks.bim");
            File.Copy(TestModels.FindBim(), bim);
            return bim;
        }

        [Fact]
        public async Task Safe_rename_stages_applies_fixup_replays_and_exports_one_certificate()
        {
            var bim = TempCopyOfFixture();
            try
            {
                using var engine = new LocalEngine(new SessionManager(), new Pro());
                await engine.OpenAsync(bim);
                var admission = await engine.CheckWorkflowAsync("governed-rename");
                Assert.True(admission.Ok, string.Join(" | ", admission.Findings.Select(x => x.Message)));
                var def = await engine.GetWorkflowAsync("governed-rename");
                Assert.Equal("Safe rename", def.Title);
                Assert.Equal(6, def.Steps.Length);
                Assert.Contains(def.Steps.SelectMany(x => x.Gate?.Verify ?? Array.Empty<VerifySpec>()), x => x.Kind == "impact_assessment" && x.Intent == "rename");

                var measures = await engine.ListMeasuresAsync();
                var target = measures.First();
                ImpactAssessmentResult impact = null;
                foreach (var candidate in measures)
                {
                    var next = await engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = candidate.Ref, Intent = "change", Scope = "model" });
                    if (next.ModelImpact.Measures > 0) { target = candidate; impact = next; break; }
                }
                var dependant = impact?.ModelImpact.Impacted.FirstOrDefault(x => x.Kind == "measure");
                var newName = "Safe Rename " + Guid.NewGuid().ToString("N").Substring(0, 8);
                var reportDecline = new { declined = true, reason = "No report definitions are part of this model-only test" };
                var step1Json = JsonSerializer.Serialize(new { target = target.Ref, newName, reportPaths = reportDecline });

                var run = await engine.StartWorkflowAsync("governed-rename", "human");
                run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", step1Json, "human");
                Assert.Equal("step-2", run.CurrentStep.StepId);
                Assert.Contains(run.Steps[0].VerifyResults, x => x.Kind == "impact_assessment" && x.Status != "failed");

                var plan = await engine.AddPlanItemAsync(target.Ref, "rename", newName, "Rename " + target.Name, null, null, "human");
                var item = Assert.Single(plan.Items);
                Assert.Equal("proposed", item.Status);
                run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-2",
                    JsonSerializer.Serialize(new { planItemId = item.Id }), "human");
                Assert.Equal("passed", run.Steps[1].VerifyResults.Single(x => x.Kind == "plan_item_staged").Status);

                run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-3",
                    "{\"baselineCapture\":{\"declined\":true,\"reason\":\"No live query model is connected\"}}", "human");
                run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-4",
                    "{\"externalBindingsReviewed\":\"No M or external bindings in the declared model-only scope\",\"renameDecision\":\"Proceed\"}", "human");

                await engine.SetPlanItemAsync(item.Id, null, approved: true, "human");
                var applied = await engine.ApplyPlanAsync(new[] { item.Id }, "human");
                Assert.Equal(1, applied.AppliedCount);
                run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-5",
                    "{\"liveSyncConfirmed\":\"No baseline was captured\",\"replayInterview\":{\"declined\":true,\"reason\":\"No current-model pack was scheduled\"}}", "human");
                Assert.Equal("passed", run.Steps[4].VerifyResults.Single(x => x.Kind == "plan_item_applied").Status);
                Assert.Equal("passed", run.Steps[4].VerifyResults.Single(x => x.Kind == "tests_replay").Status);   // this model decides integrity checks offline (decided>0); the all-NotVerifiable false-safe is guarded separately

                if (dependant != null)
                {
                    var expression = (await engine.ListMeasuresAsync()).Single(x => x.Ref == dependant.Ref).Expression;
                    Assert.Contains("[" + newName + "]", expression);
                    Assert.DoesNotContain("[" + target.Name + "]", expression);
                }

                run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-6",
                    "{\"certificateDelivery\":\"Save with model\"}", "human");
                Assert.Equal("completed", run.Status);
                var artifact = await engine.ExportWorkflowEvidenceAsync(run.RunId);
                Assert.Null(artifact.Error);
                Assert.Contains("plan_item_applied", artifact.Json);
                Assert.Contains("tests_replay", artifact.Json);
                Assert.Contains("Safe rename", artifact.Html);
            }
            finally { Directory.Delete(Path.GetDirectoryName(bim), true); }
        }
    }
}
