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
    /// <summary>
    /// The engine-owned REPORT SCOPE: which reports a model's impact answer was checked against, and what came of
    /// reading them. Astra's UAT found the old design kept the analysis inside the webview, so the engine was asked
    /// fresh with no reports and always said reports were not checked, while the page said they were. One scope,
    /// owned here, is the repair: the CHOICES are saved per model (settings), the CHECK RESULTS live in the session
    /// (evidence), and impact, the cleanup list, removal and the apply-time recheck all read the same one.
    /// </summary>
    public sealed class ReportScopeTests
    {
        private sealed class ProTier : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static (LocalEngine Engine, SessionManager Sessions, string Root, string Model) Make()
        {
            var root = Path.Combine(Path.GetTempPath(), "smx-report-scope-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var model = Path.Combine(root, "Model.bim");
            File.Copy(TestModels.FindBim(), model);
            var sessions = new SessionManager();
            return (new LocalEngine(sessions, new ProTier(), root), sessions, root, model);
        }

        // One local PBIR report folder whose single card visual displays the named measure.
        private static string WriteReport(string root, string folder, string table, string measure)
        {
            var entity = table.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var visual = "{ \"visual\": { \"visualType\": \"card\", \"query\": { \"queryState\": { \"Values\": { \"projections\": [ " +
                "{ \"field\": { \"Measure\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"" + entity + "\" } }, \"Property\": \"" + measure + "\" } } } ] } } } } }";
            var reportRoot = Path.Combine(root, folder);
            var def = Path.Combine(reportRoot, "R.Report", "definition");
            var visualDir = Path.Combine(def, "pages", "Overview", "visuals", "Card");
            Directory.CreateDirectory(visualDir);
            File.WriteAllText(Path.Combine(def, "report.json"), "{}");
            File.WriteAllText(Path.Combine(visualDir, "visual.json"), visual);
            return reportRoot;
        }

        [Fact]
        public async Task A_saved_choice_comes_back_as_not_checked_and_a_check_marks_it_checked_with_a_time()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    await x.Engine.CreateMeasureAsync("table:" + table, "Scope Shown", "1", "human");
                    var path = WriteReport(x.Root, "Chosen", table, "Scope Shown");

                    var saved = await x.Engine.SetReportScopeAsync(new[]
                    {
                        new ReportScopeChoice { Kind = "local", Name = "Sales files", Path = path },
                    }, "human");
                    var one = Assert.Single(saved.Reports);
                    Assert.Equal("notChecked", one.State);
                    Assert.Null(one.CheckedWhenUtc);

                    var listed = await x.Engine.ListReportScopeAsync();
                    Assert.Equal("notChecked", Assert.Single(listed.Reports).State);

                    var checkedScope = await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");
                    var after = Assert.Single(checkedScope.Reports);
                    Assert.Equal("checked", after.State);
                    Assert.False(string.IsNullOrWhiteSpace(after.CheckedWhenUtc));
                    Assert.Equal(1, checkedScope.Checked);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task A_model_edit_after_a_check_turns_the_report_state_into_needs_checking_again()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    await x.Engine.CreateMeasureAsync("table:" + table, "Scope Watched", "1", "human");
                    var path = WriteReport(x.Root, "Watched", table, "Scope Watched");
                    await x.Engine.SetReportScopeAsync(new[] { new ReportScopeChoice { Kind = "local", Name = "Watched", Path = path } }, "human");
                    await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");
                    Assert.Equal("checked", Assert.Single((await x.Engine.ListReportScopeAsync()).Reports).State);

                    await x.Engine.CreateMeasureAsync("table:" + table, "Scope Later", "2", "human");

                    var stale = await x.Engine.ListReportScopeAsync();
                    Assert.Equal("needsChecking", Assert.Single(stale.Reports).State);
                    Assert.Contains(stale.Gaps, g => g.Contains("Needs checking again", StringComparison.OrdinalIgnoreCase));
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task An_unreadable_choice_is_could_not_be_fully_checked_and_never_a_clean_result()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    await x.Engine.CreateMeasureAsync("table:" + table, "Scope Partly", "1", "human");
                    var good = WriteReport(x.Root, "Good", table, "Scope Partly");
                    var bad = Path.Combine(x.Root, "NotAReport");
                    Directory.CreateDirectory(bad);

                    await x.Engine.SetReportScopeAsync(new[]
                    {
                        new ReportScopeChoice { Kind = "local", Name = "Good", Path = good },
                        new ReportScopeChoice { Kind = "local", Name = "Broken", Path = bad },
                    }, "human");
                    var result = await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");

                    Assert.Equal(1, result.Checked);
                    Assert.Equal(1, result.CouldNotBeFullyChecked);
                    Assert.Contains(result.Reports, r => r.Name == "Broken" && r.State == "couldNotBeFullyChecked");
                    Assert.Contains(result.Gaps, g => g.Contains("could not be fully checked", StringComparison.OrdinalIgnoreCase));
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task Impact_reads_the_checked_scope_without_being_handed_report_paths()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var root = await x.Engine.CreateMeasureAsync("table:" + table, "Scope Root", "1", "human");
                    var path = WriteReport(x.Root, "Scoped", table, "Scope Root");
                    await x.Engine.SetReportScopeAsync(new[] { new ReportScopeChoice { Kind = "local", Name = "Scoped", Path = path } }, "human");
                    await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");

                    // No reportPaths on the request at all: the engine is expected to find them in its own scope.
                    var result = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = root });

                    var hit = Assert.Single(result.ReportImpact);
                    Assert.Equal("Scoped", hit.Name);
                    Assert.NotNull(result.Checked);
                    Assert.Equal("checked", Assert.Single(result.Checked.Reports).State);
                    Assert.DoesNotContain(result.Unknowns, u => u.Contains("was not checked", StringComparison.OrdinalIgnoreCase));
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task Remove_safe_objects_uses_the_checked_scope_so_a_report_used_field_is_never_swept()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var shown = await x.Engine.CreateMeasureAsync("table:" + table, "Scope Displayed", "1", "human");
                    var path = WriteReport(x.Root, "Displayed", table, "Scope Displayed");

                    // Model-only, the measure looks safe: nothing in the model references it.
                    Assert.Contains((await x.Engine.UnusedObjectsAsync()).Items, i => i.Ref == shown && i.Verdict == "safe");

                    await x.Engine.SetReportScopeAsync(new[] { new ReportScopeChoice { Kind = "local", Name = "Displayed", Path = path } }, "human");
                    await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");

                    // No reportPaths argument: the sweep must pick the SAME scope up by itself, or it deletes a
                    // field a checked report displays (the UAT's most serious finding).
                    var report = await x.Engine.RemoveSafeObjectsAsync(new[] { shown }, null, "human");
                    Assert.Equal(0, report.Count);
                    Assert.Contains(report.Skipped, s => s.Ref == shown);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task The_apply_time_recheck_of_a_proposal_uses_the_same_checked_reports()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var shown = await x.Engine.CreateMeasureAsync("table:" + table, "Scope Proposed", "1", "human");
                    var path = WriteReport(x.Root, "Proposed", table, "Scope Proposed");
                    await x.Engine.SetReportScopeAsync(new[] { new ReportScopeChoice { Kind = "local", Name = "Proposed", Path = path } }, "human");
                    await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");

                    var plan = await x.Engine.AddPlanItemAsync(shown, "delete_if_unused", null, "Remove Scope Proposed", null, null, "human");
                    var item = Assert.Single(plan.Items);
                    await x.Engine.SetPlanItemAsync(item.Id, null, approved: true, "human");
                    var applied = await x.Engine.ApplyPlanAsync(new[] { item.Id }, "human");

                    // The proposal must be SKIPPED: a checked report displays it.
                    Assert.Equal(0, applied.AppliedCount);
                    Assert.Equal(1, applied.SkippedCount);
                    await x.Engine.GetObjectAsync(shown);   // still there; throws if it was deleted
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }
    }
}
