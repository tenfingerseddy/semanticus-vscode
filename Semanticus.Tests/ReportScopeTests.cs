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

        [Trait(TestCost.Key, TestCost.Sweep)]
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

        // =========================================================================================
        // Kane, 16 September, on a real Fabric workspace: the Impact card said two reports "could not be
        // fully checked" and there was no way to find out why. The page can only say the cause if the
        // ENGINE recorded one, so every path that ends in couldNotBeFullyChecked has to leave a Note, and
        // the Note has to name the cause that actually happened.
        // =========================================================================================

        // A part that will not parse, inside a report whose definition WAS found. This is the coverage gap
        // that reads as a clean folder until you look at what it could not see.
        private static string WriteReportWithABrokenPart(string root, string folder, string table, string measure)
        {
            var reportRoot = WriteReport(root, folder, table, measure);
            var broken = Path.Combine(reportRoot, "R.Report", "definition", "pages", "Broken", "visuals", "Card");
            Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, "visual.json"), "{ this is not json");
            return reportRoot;
        }

        [Fact]
        public async Task A_report_read_only_in_part_says_which_cause_it_was_on_both_doors()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    await x.Engine.CreateMeasureAsync("table:" + table, "Scope Skipped", "1", "human");
                    var path = WriteReportWithABrokenPart(x.Root, "PartlyParsed", table, "Scope Skipped");
                    await x.Engine.SetReportScopeAsync(new[] { new ReportScopeChoice { Kind = "local", Name = "Partly parsed", Path = path } }, "human");

                    // The UI door.
                    var viaRpc = await new EngineRpcTarget(x.Engine).checkReports(null, true);
                    var rpcRow = Assert.Single(viaRpc.Reports);
                    Assert.Equal(ReportScopeStates.CouldNotBeFullyChecked, rpcRow.State);
                    Assert.False(string.IsNullOrWhiteSpace(rpcRow.Note), "a state that is not a clean check must carry its cause");
                    Assert.Contains("Part of this report could not be read", rpcRow.Note);

                    // The agent door reads the same reading, with the same per-report facts on it.
                    var viaMcp = await McpTools.ListReportScope(x.Engine);
                    var mcpRow = Assert.Single(viaMcp.Reports);
                    Assert.Equal(rpcRow.State, mcpRow.State);
                    Assert.Equal(rpcRow.Note, mcpRow.Note);

                    // And impact_assessment, which is where the page reads it from, carries it too.
                    var assessed = await McpToolsImpactAssessment.ImpactAssessment(x.Engine, "table:" + table);
                    Assert.Equal(rpcRow.Note, Assert.Single(assessed.Checked.Reports).Note);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task A_reading_whose_references_do_not_match_this_model_does_not_claim_it_could_not_be_read()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    // A visual whose field hangs off a Source alias nothing declares: the definition reads
                    // perfectly, and the reference still cannot be attributed to this model. Saying "part of
                    // this report could not be read" about that is a different fact, and a false one.
                    var reportRoot = Path.Combine(x.Root, "Aliased");
                    var visualDir = Path.Combine(reportRoot, "R.Report", "definition", "pages", "Overview", "visuals", "Card");
                    Directory.CreateDirectory(visualDir);
                    File.WriteAllText(Path.Combine(reportRoot, "R.Report", "definition", "report.json"), "{}");
                    File.WriteAllText(Path.Combine(visualDir, "visual.json"),
                        "{ \"visual\": { \"visualType\": \"card\", \"query\": { \"queryState\": { \"Values\": { \"projections\": [ " +
                        "{ \"field\": { \"Measure\": { \"Expression\": { \"SourceRef\": { \"Source\": \"undeclared\" } }, \"Property\": \"Some Measure\" } } } ] } } } } }");

                    await x.Engine.SetReportScopeAsync(new[] { new ReportScopeChoice { Kind = "local", Name = "Aliased", Path = reportRoot } }, "human");
                    var result = await new EngineRpcTarget(x.Engine).checkReports(null, true);
                    var row = Assert.Single(result.Reports);

                    Assert.Equal(ReportScopeStates.CouldNotBeFullyChecked, row.State);
                    Assert.False(string.IsNullOrWhiteSpace(row.Note));
                    Assert.DoesNotContain("could not be read", row.Note);
                    Assert.Contains("could not be matched to this model", row.Note);
                    Assert.Equal(row.Note, Assert.Single((await McpTools.ListReportScope(x.Engine)).Reports).Note);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_folder_handed_to_impact_assessment_carries_its_cause_too()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    await x.Engine.CreateMeasureAsync("table:" + table, "Ad Hoc Shown", "1", "human");
                    var path = WriteReportWithABrokenPart(x.Root, "AdHoc", table, "Ad Hoc Shown");

                    // reportPaths folds a folder into the scope for ONE call. It reached couldNotBeFullyChecked
                    // with no Note at all, so the card had a state and no cause behind it.
                    foreach (var assessed in new[]
                    {
                        await McpToolsImpactAssessment.ImpactAssessment(x.Engine, "table:" + table, "change", "modelAndReports", new[] { path }),
                        await new EngineRpcTarget(x.Engine).impactAssessment(new ImpactAssessmentRequest
                        { ObjectRef = "table:" + table, ReportPaths = new[] { path } }),
                    })
                    {
                        var row = Assert.Single(assessed.Checked.Reports);
                        Assert.Equal(ReportScopeStates.CouldNotBeFullyChecked, row.State);
                        Assert.False(string.IsNullOrWhiteSpace(row.Note), "a folder read only in part must say so");
                        Assert.Contains("Part of this report could not be read", row.Note);
                    }
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task A_transport_failure_with_no_message_still_leaves_a_cause_a_person_can_read()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    // A scrubbed exception message can come back blank. "??" does not catch an empty string, so
                    // the state said couldNotBeFullyChecked and the cause was the empty string.
                    x.Engine.PublishedReportReaderForTests = (ws, ids) => Task.FromResult(
                        ids.Select(id => (path: id, error: "   ", result: new ReportDefinitionReader.ParseResult())).ToList());
                    await x.Engine.SetReportScopeAsync(new[]
                    {
                        new ReportScopeChoice { Kind = "published", Name = "Silent failure", WorkspaceId = "ws-a", WorkspaceName = "Workspace A", ReportId = "r-1" },
                    }, "human");

                    var row = Assert.Single((await x.Engine.CheckReportsAsync(null, true, null, null, null, "human")).Reports);
                    Assert.Equal(ReportScopeStates.CouldNotBeFullyChecked, row.State);
                    Assert.False(string.IsNullOrWhiteSpace(row.Note), "an empty cause is the same as no cause");
                    Assert.Contains("could not be read", row.Note);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
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
                    await x.Engine.SetReportScopeAsync(System.Array.Empty<Semanticus.Engine.Lineage.ReportScopeChoice>(), "human");   // no reports: the sweep is report-aware over an empty set, so an unused measure is "safe" (with no report declared it is "caution")
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

        [Trait(TestCost.Key, TestCost.Sweep)]
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

        /// <summary>
        /// A chosen published report comes back carrying its OWN workspace and report id, on BOTH doors.
        ///
        /// The page rebuilds every save of the list from this shape, so a row that arrives without its
        /// coordinates is a row that gets saved without them: taking one of three chosen reports off the list
        /// used to save the two survivors with an empty workspace and their display name where the report id
        /// belongs, and the next check then looked for two reports that do not exist.
        /// </summary>
        [Fact]
        public async Task A_chosen_published_report_keeps_its_workspace_and_report_id_on_both_doors()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    await x.Engine.SetReportScopeAsync(new[]
                    {
                        new ReportScopeChoice { Kind = "published", Name = "Sales Overview", WorkspaceId = "ws-a", WorkspaceName = "Workspace A", ReportId = "r-1" },
                        new ReportScopeChoice { Kind = "published", Name = "Regional Demo 2", WorkspaceId = "ws-a", WorkspaceName = "Workspace A", ReportId = "r-2" },
                        new ReportScopeChoice { Kind = "published", Name = "Quarterly Pack", WorkspaceId = "ws-b", WorkspaceName = "Workspace B", ReportId = "r-3" },
                    }, "human");

                    // The UI door.
                    var ui = await new EngineRpcTarget(x.Engine).listReportScope();
                    // The agent door.
                    var agent = await McpTools.ListReportScope(x.Engine);

                    foreach (var door in new[] { ui, agent })
                    {
                        Assert.Equal(3, door.Reports.Length);
                        var kept = door.Reports.Single(r => r.Name == "Quarterly Pack");
                        Assert.Equal("ws-b", kept.WorkspaceId);
                        Assert.Equal("r-3", kept.ReportId);
                        Assert.All(door.Reports, r =>
                        {
                            Assert.False(string.IsNullOrWhiteSpace(r.WorkspaceId), "a published row must carry its workspace id");
                            Assert.False(string.IsNullOrWhiteSpace(r.ReportId), "a published row must carry its report id");
                        });
                    }

                    // The whole point: hand the list straight back the way the page does, minus one report, and
                    // the survivors are still the same two reports in the same two workspaces.
                    var survivors = ui.Reports.Where(r => r.Name != "Regional Demo 2")
                        .Select(r => new ReportScopeChoice
                        {
                            Id = r.Id, Kind = r.Kind, Name = r.Name,
                            WorkspaceId = r.WorkspaceId, WorkspaceName = r.Source, ReportId = r.ReportId,
                        }).ToArray();
                    var saved = await x.Engine.SetReportScopeAsync(survivors, "human");
                    Assert.Equal(2, saved.Reports.Length);
                    Assert.Equal(new[] { ("ws-a", "r-1"), ("ws-b", "r-3") },
                        saved.Reports.OrderBy(r => r.ReportId, StringComparer.Ordinal).Select(r => (r.WorkspaceId, r.ReportId)).ToArray());
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        /// <summary>
        /// Two chosen reports in different workspaces share a display name, and only one of them uses the field.
        /// Every hit names the CHOICE it came from, so a surface can put the hit on the right row. A name is not
        /// an identity: joining on it hands one report's hit to the other and then tells a person that a report
        /// which does not use the field does.
        /// </summary>
        [Fact]
        public async Task A_hit_names_the_choice_it_came_from_when_two_reports_share_a_display_name()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var root = await x.Engine.CreateMeasureAsync("table:" + table, "Twin Shown", "1", "human");
                    var uses = WriteReport(x.Root, "TwinUses", table, "Twin Shown");
                    await x.Engine.CreateMeasureAsync("table:" + table, "Twin Other", "2", "human");
                    var idle = WriteReport(x.Root, "TwinIdle", table, "Twin Other");

                    // Same display name, two workspaces. Only the report in workspace B shows the field.
                    x.Engine.PublishedReportReaderForTests = (ws, ids) =>
                    {
                        var pr = ReportDefinitionReader.ReadLocalPbir(ws == "ws-b" ? uses : idle);
                        return Task.FromResult(ids.Select(id => (path: "Monthly report", error: (string)null, result: pr)).ToList());
                    };
                    await x.Engine.SetReportScopeAsync(new[]
                    {
                        new ReportScopeChoice { Kind = "published", Name = "Monthly report", WorkspaceId = "ws-a", WorkspaceName = "Workspace A", ReportId = "r-1" },
                        new ReportScopeChoice { Kind = "published", Name = "Monthly report", WorkspaceId = "ws-b", WorkspaceName = "Workspace B", ReportId = "r-2" },
                    }, "human");
                    await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");

                    var ui = await new EngineRpcTarget(x.Engine).impactAssessment(new ImpactAssessmentRequest { ObjectRef = root });
                    var agent = await McpToolsImpactAssessment.ImpactAssessment(x.Engine, root);

                    foreach (var door in new[] { ui, agent })
                    {
                        var hit = Assert.Single(door.ReportImpact);
                        Assert.Equal("published:ws-b/r-2", hit.ChoiceId);
                        Assert.Contains(door.Checked.Reports, r => r.Id == hit.ChoiceId);
                        // The other chosen report is on the checked list and has no hit of its own.
                        Assert.Contains(door.Checked.Reports, r => r.Id == "published:ws-a/r-1");
                        Assert.DoesNotContain(door.ReportImpact, h => h.ChoiceId == "published:ws-a/r-1");
                    }
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }
    }
}
