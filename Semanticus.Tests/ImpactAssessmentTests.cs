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

        [Trait(TestCost.Key, TestCost.Sweep)]
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
                    Assert.Contains(result.Unknowns, u => u.Contains("Reports have not been checked"));
                    Assert.Contains(result.Coverage, c => c.Area == "reports" && c.Status == "unknown");
                    Assert.Equal("check_reports", result.SuggestedNextAction.Op);
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
                    Assert.Equal("Nothing in the model uses Assess Model Only.", result.Summary);
                    Assert.Contains(result.Coverage, c => c.Area == "reports" && c.Status == "excluded");
                    Assert.DoesNotContain(result.Unknowns, u => u.Contains("report", StringComparison.OrdinalIgnoreCase));
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
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
                    Assert.Contains(rename.Unknowns, u => u.Contains("Data-loading steps written by hand"));

                    var remove = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = root, Intent = "remove", Scope = "model" });
                    Assert.Equal("Broken", remove.Verdict);
                    Assert.StartsWith("Deleting Assess Base would break 1 measure.", remove.Summary);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
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
                    Assert.Contains(result.Unknowns, u => u.Contains("A report that is not on this list can still use this field"));
                    Assert.Equal("checked", Assert.Single(result.Checked.Reports).State);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Relevant_saved_tests_are_scheduled_by_their_bound_target_without_guessing()
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
                    var result = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = root, Scope = "model" });
                    Assert.Equal("NeedsReview", result.Verdict);
                    Assert.Contains(result.ReplayChecks, c => c.Kind == "ambient-suite");
                    Assert.Contains(result.ReplayChecks, c => c.Kind == "saved-test" && c.Id == saved.Id && c.TargetRef == root);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_assessment_leaves_the_interview_alone_and_never_promises_a_security_check()
        {
            // Astra's UAT: the Interview left the app, but every assessment still carried an interview coverage
            // cell, a replay entry per saved question, a "not dependency-indexed" warning, and those entries
            // moved the verdict. Saved questions stay saved; they are simply not this result's business. The
            // ambient replay line also promised a security check the automatic suite does not perform.
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var root = await x.Engine.CreateMeasureAsync("table:" + table, "Assess No Interview", "1", "human");
                    var question = await x.Engine.AddInterviewQuestionAsync("Can this model answer the unsupported question?", "refusal",
                        null, null, null, null, null, null, null, true, null, "user", "project", "human");

                    var result = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = root, Scope = "model" });

                    Assert.DoesNotContain(result.Coverage, c => c.Area == "interview");
                    Assert.DoesNotContain(result.ReplayChecks, c => c.Kind == "interview-question");
                    Assert.DoesNotContain(result.Unknowns, u => u.Contains("Interview", StringComparison.OrdinalIgnoreCase));
                    Assert.DoesNotContain(result.ReplayChecks, c => c.Title != null && c.Title.Contains("security", StringComparison.OrdinalIgnoreCase));

                    // The saved question is untouched where it lives.
                    Assert.Contains((await x.Engine.ListInterviewQuestionsAsync(null)).Questions, q => q.Id == question.Id);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_summary_names_what_uses_it_and_a_removal_says_what_it_would_break()
        {
            // "The requested remove has 1 known impact(s); removal is not safe as proposed" is engine speak and a
            // verdict word ("Broken") a person reads as damage they already did. The sentence has to be the answer.
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var root = await x.Engine.CreateMeasureAsync("table:" + table, "Assess Sentence", "1", "human");
                    await x.Engine.CreateMeasureAsync("table:" + table, "Assess Sentence User", "[Assess Sentence] + 1", "human");

                    var change = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = root, Intent = "change", Scope = "model" });
                    Assert.Equal("Assess Sentence is used by 1 measure.", change.Summary);

                    var remove = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = root, Intent = "remove", Scope = "model" });
                    Assert.StartsWith("Deleting Assess Sentence would break 1 measure.", remove.Summary);
                    Assert.DoesNotContain("known impact", remove.Summary);

                    var orphan = await x.Engine.CreateMeasureAsync("table:" + table, "Assess Sentence Orphan", "1", "human");
                    var none = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = orphan, Intent = "change", Scope = "model" });
                    Assert.Equal("Nothing in the model uses Assess Sentence Orphan.", none.Summary);
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

        // A PBIR report whose ONLY reference to the measure sits in report.json - a report-level filter, the
        // shape a bookmark or a page-level setting also takes. Nothing on any page shows it.
        private static string WriteReportLevelOnly(string root, string folder, string table, string measure)
        {
            var entity = table.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var reportJson = "{ \"filterConfig\": { \"filters\": [ { \"field\": { \"Measure\": { " +
                "\"Expression\": { \"SourceRef\": { \"Entity\": \"" + entity + "\" } }, \"Property\": \"" + measure + "\" } } } ] } }";
            var def = Path.Combine(root, folder, "R.Report", "definition");
            Directory.CreateDirectory(def);
            File.WriteAllText(Path.Combine(def, "report.json"), reportJson);
            return Path.Combine(root, folder);
        }

        // A PBIR report with `count` card visuals, every one of them showing the measure.
        private static string WriteBusyReport(string root, string folder, string table, string measure, int count)
        {
            var entity = table.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var visual = "{ \"visual\": { \"visualType\": \"card\", \"query\": { \"queryState\": { \"Values\": { \"projections\": [ " +
                "{ \"field\": { \"Measure\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"" + entity + "\" } }, \"Property\": \"" + measure + "\" } } } ] } } } } }";
            var def = Path.Combine(root, folder, "R.Report", "definition");
            Directory.CreateDirectory(def);
            File.WriteAllText(Path.Combine(def, "report.json"), "{}");
            for (var i = 1; i <= count; i++)
            {
                var dir = Path.Combine(def, "pages", "Sheet", "visuals", "Tile" + i.ToString("D4"));
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "visual.json"), visual);
            }
            return Path.Combine(root, folder);
        }

        /// <summary>
        /// A report that uses the field only at report level - a filter, a bookmark, a page-level setting - is a
        /// HIT with no page-stamped visual behind it. Both doors have to say so: the report summary counts it as a
        /// report the field appears in, and the hit itself carries zero visuals and no details, which is what tells
        /// a surface to say "used, but not on any page" rather than "no use found in this report".
        /// </summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_report_level_only_use_is_a_hit_with_no_visuals_behind_it()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var root = await x.Engine.CreateMeasureAsync("table:" + table, "Bookmark Only", "1", "human");
                    var path = WriteReportLevelOnly(x.Root, "BookmarkOnly", table, "Bookmark Only");
                    await x.Engine.SetReportScopeAsync(new[] { new ReportScopeChoice { Kind = "local", Name = "Bookmark only", Path = path } }, "human");
                    await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");

                    var ui = await new EngineRpcTarget(x.Engine).impactAssessment(new ImpactAssessmentRequest { ObjectRef = root });
                    var agent = await McpToolsImpactAssessment.ImpactAssessment(x.Engine, root);

                    foreach (var door in new[] { ui, agent })
                    {
                        var hit = Assert.Single(door.ReportImpact);
                        Assert.Contains(root, hit.UsedRefs);
                        Assert.Equal(0, hit.Visuals);
                        Assert.Empty(hit.Details);
                        Assert.Equal(0, hit.DetailsOmitted);
                        Assert.Equal(1, door.ReportsImpacted);
                        // The summary says the field turns up in the report, so a surface that then says nothing
                        // was found in that same report is contradicting the answer above it.
                        Assert.Contains("appears in 1 checked report", door.ReportSummary);
                    }
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        /// <summary>
        /// A report with more matching visuals than one answer may carry says how many it left out. The detail
        /// list is capped at 200; the count was the full 201 and the cap was silent, so a surface paging through
        /// the details ran out at 200 with nothing to tell a person the rest existed.
        /// </summary>
        [Fact]
        public async Task A_report_past_the_detail_cap_says_how_many_matches_are_not_listed()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var root = await x.Engine.CreateMeasureAsync("table:" + table, "Busy Tile", "1", "human");
                    var path = WriteBusyReport(x.Root, "Busy", table, "Busy Tile", 201);
                    await x.Engine.SetReportScopeAsync(new[] { new ReportScopeChoice { Kind = "local", Name = "Busy", Path = path } }, "human");
                    await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");

                    var ui = await new EngineRpcTarget(x.Engine).impactAssessment(new ImpactAssessmentRequest { ObjectRef = root });
                    var agent = await McpToolsImpactAssessment.ImpactAssessment(x.Engine, root);

                    foreach (var door in new[] { ui, agent })
                    {
                        var hit = Assert.Single(door.ReportImpact);
                        Assert.Equal(201, hit.Visuals);
                        Assert.Equal(200, hit.Details.Length);
                        Assert.Equal(1, hit.DetailsOmitted);
                        Assert.Equal(hit.Visuals, hit.Details.Length + hit.DetailsOmitted);
                    }
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }
    }
}
