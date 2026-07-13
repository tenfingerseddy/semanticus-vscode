using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Semanticus.Engine.Lineage;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ImpactAssessmentTests
    {
        private sealed class ProTier : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static (LocalEngine Engine, SessionManager Sessions, string Root, string Model) Make()
        {
            var root = Path.Combine(Path.GetTempPath(), "smx-impact-assessment-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var model = Path.Combine(root, "Model.bim");
            File.Copy(TestModels.FindBim(), model);
            var sessions = new SessionManager();
            return (new LocalEngine(sessions, new ProTier(), root), sessions, root, model);
        }

        [Fact]
        public async Task Default_scope_never_turns_missing_report_coverage_green()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var orphan = await x.Engine.CreateMeasureAsync("table:" + table, "Assess Orphan", "1", "human");
                    var result = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = orphan });

                    Assert.Equal("Unknown", result.Verdict);
                    Assert.Empty(result.ModelImpact.Impacted);
                    Assert.Contains(result.Unknowns, u => u.Contains("Published-report usage"));
                    Assert.Contains(result.Coverage, c => c.Area == "reports" && c.Status == "unknown");
                    Assert.Equal("impact_assessment", result.SuggestedNextAction.Op);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task Explicit_model_scope_can_verify_no_known_change_impact_without_claiming_reports()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var orphan = await x.Engine.CreateMeasureAsync("table:" + table, "Assess Model Only", "1", "human");
                    var result = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest
                    {
                        ObjectRef = orphan, Intent = "change", Scope = "model",
                    });

                    Assert.Equal("Verified", result.Verdict);
                    Assert.Contains("model-only", result.Summary);
                    Assert.Contains(result.Coverage, c => c.Area == "reports" && c.Status == "excluded");
                    Assert.DoesNotContain(result.Unknowns, u => u.Contains("Published-report"));
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task Rename_and_remove_classify_the_same_transitive_blast_radius_honestly()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var root = await x.Engine.CreateMeasureAsync("table:" + table, "Assess Base", "1", "human");
                    var dependent = await x.Engine.CreateMeasureAsync("table:" + table, "Assess User", "[Assess Base] + 1", "human");

                    var rename = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = root, Intent = "rename", Scope = "model" });
                    Assert.Equal("NeedsReview", rename.Verdict);
                    Assert.Contains(rename.ModelImpact.Impacted, i => i.Ref == dependent);
                    Assert.Contains(rename.Unknowns, u => u.Contains("Free-form M text"));

                    var remove = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = root, Intent = "remove", Scope = "model" });
                    Assert.Equal("Broken", remove.Verdict);
                    Assert.Contains("removal is not safe", remove.Summary);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task Supplied_report_usage_is_joined_to_the_transitive_model_impact_but_remains_scoped()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var root = await x.Engine.CreateMeasureAsync("table:" + table, "Assess Report Base", "1", "human");
                    var dependent = await x.Engine.CreateMeasureAsync("table:" + table, "Assess Report User", "[Assess Report Base] + 1", "human");
                    var entity = table.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    var visual = "{ \"visual\": { \"visualType\": \"card\", \"query\": { \"queryState\": { \"Values\": { \"projections\": [ " +
                        "{ \"field\": { \"Measure\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"" + entity + "\" } }, \"Property\": \"Assess Report User\" } } } ] } } } } }";
                    var reportRoot = Path.Combine(x.Root, "Reports");
                    var def = Path.Combine(reportRoot, "R.Report", "definition");
                    var visualDir = Path.Combine(def, "pages", "Overview", "visuals", "Card");
                    Directory.CreateDirectory(visualDir);
                    File.WriteAllText(Path.Combine(def, "report.json"), "{}");
                    File.WriteAllText(Path.Combine(visualDir, "visual.json"), visual);

                    var result = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest
                    {
                        ObjectRef = root, Intent = "change", Scope = "modelAndReports", ReportPaths = new[] { reportRoot },
                    });

                    Assert.Equal("NeedsReview", result.Verdict);
                    var report = Assert.Single(result.ReportImpact);
                    Assert.Contains(dependent, report.UsedRefs);
                    Assert.Equal(1, report.Visuals);
                    Assert.Contains(result.Coverage, c => c.Area == "reports" && c.Status == "scoped" && c.Checked == 1);
                    Assert.Contains(result.Unknowns, u => u.Contains("outside that set"));
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task Relevant_saved_tests_and_the_whole_interview_pack_are_scheduled_without_guessing()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var root = await x.Engine.CreateMeasureAsync("table:" + table, "Assess Tested", "1", "human");
                    var saved = await x.Engine.SaveTestDefinitionAsync(new TestDefinition
                    {
                        Kind = TestKinds.MeasureReconcile, Title = "Assess Tested against source", TargetRef = root,
                    }, "human");
                    var question = await x.Engine.AddInterviewQuestionAsync("Can this model answer the unsupported question?", "refusal",
                        null, null, null, null, null, null, null, true, null, "user", "project", "human");

                    var result = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = root, Scope = "model" });
                    Assert.Equal("NeedsReview", result.Verdict);
                    Assert.Contains(result.ReplayChecks, c => c.Kind == "ambient-suite");
                    Assert.Contains(result.ReplayChecks, c => c.Kind == "saved-test" && c.Id == saved.Id && c.TargetRef == root);
                    Assert.Contains(result.ReplayChecks, c => c.Kind == "interview-question" && c.Id == question.Id);
                    Assert.Contains(result.Unknowns, u => u.Contains("not dependency-indexed"));
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task Invalid_scope_intent_and_missing_ref_teach_the_exact_recovery()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var noRef = await Assert.ThrowsAsync<ArgumentException>(() => x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest()));
                    Assert.Contains("search_model", noRef.Message);
                    var badIntent = await Assert.ThrowsAsync<ArgumentException>(() => x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = "model:", Intent = "guess" }));
                    Assert.Contains("change, rename, remove, or restructure", badIntent.Message);
                    var mixed = await Assert.ThrowsAsync<ArgumentException>(() => x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = "model:", Scope = "model", ReportPaths = new[] { "x" } }));
                    Assert.Contains("modelAndReports", mixed.Message);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }
    }
}
