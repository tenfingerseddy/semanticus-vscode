using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Semanticus.Engine.Lineage;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Astra's rejection of the Impact build at 653f741c, R1 to R5. Every one of these is a way the report scope
    /// could be lost or changed between the answer a person read and the deletion that followed it, and every one
    /// of them ends with the model-only safe set deleting a field a chosen report displays.
    ///
    /// None was visible on a screen. They are traced through the engine, so they are pinned here, and each is
    /// also driven through the RPC door and the MCP door, because a repair that holds only inside LocalEngine is
    /// a repair one of the two doors does not have.
    /// </summary>
    public sealed class ReportScopeRejectionTests
    {
        private sealed class ProTier : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static (LocalEngine Engine, SessionManager Sessions, string Root, string Model) Make()
        {
            var root = Path.Combine(Path.GetTempPath(), "smx-scope-reject-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var model = Path.Combine(root, "Model.bim");
            File.Copy(TestModels.FindBim(), model);
            var sessions = new SessionManager();
            return (new LocalEngine(sessions, new ProTier(), root), sessions, root, model);
        }

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

        private static ReportScopeChoice Local(string name, string path) =>
            new ReportScopeChoice { Kind = "local", Name = name, Path = path };

        // =========================================================================================
        // R1. A saved but never-checked scope must not fall back to model-only deletion.
        // =========================================================================================
        [Fact]
        public async Task R1_a_chosen_but_unchecked_scope_is_unknown_coverage_not_model_only_safety()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var shown = await x.Engine.CreateMeasureAsync("table:" + table, "R1 Displayed", "1", "human");
                    var path = WriteReport(x.Root, "R1", table, "R1 Displayed");

                    // Model-only, it looks safe: nothing in the model references it.
                    Assert.Contains((await x.Engine.UnusedObjectsAsync()).Items, i => i.Ref == shown && i.Verdict == "safe");

                    // A report is CHOSEN and deliberately NOT checked. The protection was intended; it must not be
                    // silently erased just because nothing has been read yet.
                    await x.Engine.SetReportScopeAsync(new[] { Local("R1", path) }, "human");

                    var unused = await x.Engine.UnusedObjectsAsync();
                    Assert.DoesNotContain(unused.Items, i => i.Ref == shown && i.Verdict == "safe");
                    Assert.Contains(unused.Items, i => i.Ref == shown && i.Verdict == "caution");
                    Assert.Contains("still needs checking", unused.Caveat, StringComparison.OrdinalIgnoreCase);

                    // Both doors refuse the sweep with the same sentence.
                    var direct = await x.Engine.RemoveSafeObjectsAsync(new[] { shown }, null, "human");
                    Assert.Equal(0, direct.Count);
                    Assert.Equal("Nothing removed. The chosen reports still need checking.", direct.Note);

                    var rpc = new EngineRpcTarget(x.Engine, "human");
                    var viaRpc = await rpc.removeSafeObjects(new[] { shown }, null, "human");
                    Assert.Equal(0, viaRpc.Count);
                    Assert.Equal("Nothing removed. The chosen reports still need checking.", viaRpc.Note);

                    var viaMcp = await McpTools.RemoveSafeObjects(x.Engine, new[] { shown }, null);
                    Assert.Equal(0, viaMcp.Count);
                    Assert.Equal("Nothing removed. The chosen reports still need checking.", viaMcp.Note);

                    await x.Engine.GetObjectAsync(shown);   // still there; throws if it was deleted
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task R1_a_partially_checked_scope_still_carries_the_unchecked_choices()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var orphan = await x.Engine.CreateMeasureAsync("table:" + table, "R1 Orphan", "1", "human");
                    var seen = await x.Engine.CreateMeasureAsync("table:" + table, "R1 Seen", "1", "human");
                    var readable = WriteReport(x.Root, "R1a", table, "R1 Seen");
                    var unreadOne = WriteReport(x.Root, "R1b", table, "R1 Orphan");

                    await x.Engine.SetReportScopeAsync(new[] { Local("Read me", readable), Local("Skip me", unreadOne) }, "human");
                    var scope = await x.Engine.CheckReportsAsync(new[] { "local:" + readable }, true, null, null, null, "human");
                    Assert.Equal(1, scope.Checked);
                    Assert.Equal(1, scope.NotChecked);

                    // The orphan is used only by the report that was NOT read, so its safety is unknown, not safe.
                    var unused = await x.Engine.UnusedObjectsAsync();
                    Assert.DoesNotContain(unused.Items, i => i.Ref == orphan && i.Verdict == "safe");
                    var sweep = await x.Engine.RemoveSafeObjectsAsync(new[] { orphan }, null, "human");
                    Assert.Equal(0, sweep.Count);
                    Assert.Equal("Nothing removed. The chosen reports still need checking.", sweep.Note);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        // =========================================================================================
        // R2. A proposal binds the scope and evidence version it was reviewed against.
        // =========================================================================================
        [Fact]
        public async Task R2_a_proposal_reviewed_against_other_reports_is_refused_at_apply()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var orphan = await x.Engine.CreateMeasureAsync("table:" + table, "R2 Orphan", "1", "human");
                    var other = await x.Engine.CreateMeasureAsync("table:" + table, "R2 Other", "2", "human");
                    var first = WriteReport(x.Root, "R2a", table, "R2 Other");
                    var second = WriteReport(x.Root, "R2b", table, "R2 Orphan");

                    await x.Engine.SetReportScopeAsync(new[] { Local("First", first) }, "human");
                    await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");

                    // Reviewed against the FIRST reading, which does not display it.
                    var plan = await x.Engine.AddPlanItemAsync(orphan, "delete_if_unused", null, "Remove R2 Orphan", null, null, "human");
                    var item = Assert.Single(plan.Items);
                    await x.Engine.SetPlanItemAsync(item.Id, null, approved: true, "human");

                    // The basis moves under it: a different report is now chosen, and nothing has been read.
                    await x.Engine.SetReportScopeAsync(new[] { Local("Second", second) }, "human");

                    var applied = await x.Engine.ApplyPlanAsync(new[] { item.Id }, "human");
                    Assert.Equal(0, applied.AppliedCount);
                    Assert.Equal(1, applied.SkippedCount);
                    Assert.Contains("The report selection changed. Check it again and review this removal.",
                        Assert.Single(applied.Items).Note);
                    await x.Engine.GetObjectAsync(orphan);   // never deleted
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task R2_an_unchanged_basis_still_applies()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var orphan = await x.Engine.CreateMeasureAsync("table:" + table, "R2 Steady", "1", "human");
                    await x.Engine.CreateMeasureAsync("table:" + table, "R2 Other Steady", "2", "human");
                    var report = WriteReport(x.Root, "R2c", table, "R2 Other Steady");
                    await x.Engine.SetReportScopeAsync(new[] { Local("Steady", report) }, "human");
                    await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");

                    var plan = await x.Engine.AddPlanItemAsync(orphan, "delete_if_unused", null, "Remove R2 Steady", null, null, "human");
                    var item = Assert.Single(plan.Items);
                    await x.Engine.SetPlanItemAsync(item.Id, null, approved: true, "human");
                    var applied = await x.Engine.ApplyPlanAsync(new[] { item.Id }, "human");

                    Assert.Equal(1, applied.AppliedCount);
                    Assert.DoesNotContain(await x.Engine.ListMeasuresAsync(), m => m.Ref == orphan);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        // =========================================================================================
        // R3. Identity and freshness per report, keyed by its stable choice id.
        // =========================================================================================
        [Fact]
        public async Task R3_rechecking_one_report_after_a_model_edit_leaves_the_other_needing_checking()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    await x.Engine.CreateMeasureAsync("table:" + table, "R3 Shown", "1", "human");
                    var a = WriteReport(x.Root, "R3a", table, "R3 Shown");
                    var b = WriteReport(x.Root, "R3b", table, "R3 Shown");
                    await x.Engine.SetReportScopeAsync(new[] { Local("A", a), Local("B", b) }, "human");
                    await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");
                    Assert.Equal(2, (await x.Engine.ListReportScopeAsync()).Checked);

                    await x.Engine.CreateMeasureAsync("table:" + table, "R3 Later", "2", "human");
                    Assert.Equal(2, (await x.Engine.ListReportScopeAsync()).NeedsChecking);

                    // Reread A only. B was NOT reread, so it must still need checking.
                    var after = await x.Engine.CheckReportsAsync(new[] { "local:" + a }, true, null, null, null, "human");
                    Assert.Equal(ReportScopeStates.Checked, after.Reports.Single(r => r.Name == "A").State);
                    Assert.Equal(ReportScopeStates.NeedsChecking, after.Reports.Single(r => r.Name == "B").State);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task R3_two_same_named_reports_in_different_workspaces_keep_their_own_evidence()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    await x.Engine.CreateMeasureAsync("table:" + table, "R3 Dup", "1", "human");
                    var good = WriteReport(x.Root, "R3good", table, "R3 Dup");
                    // Two CHOICES that share a display name, from two workspaces. Evidence keyed by name loses one.
                    x.Engine.PublishedReportReaderForTests = (ws, ids) =>
                    {
                        var pr = ReportDefinitionReader.ReadLocalPbir(good);
                        var list = ids.Select(id => (path: "Monthly report", error: (string)null, result: pr)).ToList();
                        return Task.FromResult(list);
                    };
                    await x.Engine.SetReportScopeAsync(new[]
                    {
                        new ReportScopeChoice { Kind = "published", Name = "Monthly report", WorkspaceId = "ws-a", WorkspaceName = "Workspace A", ReportId = "r-1" },
                        new ReportScopeChoice { Kind = "published", Name = "Monthly report", WorkspaceId = "ws-b", WorkspaceName = "Workspace B", ReportId = "r-2" },
                    }, "human");
                    var scope = await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");

                    Assert.Equal(2, scope.Listed);
                    Assert.Equal(2, scope.Checked);
                    Assert.Equal(2, scope.Reports.Select(r => r.Id).Distinct().Count());
                    // Both readings survive: the sweep must see two reports, not one merged by name.
                    Assert.Equal(2, x.Engine.CheckedReportPartsForTests().Count);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        // =========================================================================================
        // R4. Evidence is keyed by the session, not by the model's durable identity forever.
        // =========================================================================================
        [Fact]
        public async Task R4_reopening_the_model_restores_the_choices_as_not_checked()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    await x.Engine.CreateMeasureAsync("table:" + table, "R4 Shown", "1", "human");
                    await x.Engine.SaveAsync(x.Model, "bim", overwrite: true);
                    var path = WriteReport(x.Root, "R4", table, "R4 Shown");
                    await x.Engine.SetReportScopeAsync(new[] { Local("Reopen me", path) }, "human");
                    await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");
                    Assert.Equal(1, (await x.Engine.ListReportScopeAsync()).Checked);

                    // Close and reopen the SAME model. The choice is a setting and comes back; the reading is
                    // evidence about a session that has ended and must not come back with it.
                    await x.Engine.OpenAsync(x.Model);

                    var after = await x.Engine.ListReportScopeAsync();
                    Assert.Equal(1, after.Listed);
                    Assert.Equal(0, after.Checked);
                    Assert.Equal(ReportScopeStates.NotChecked, Assert.Single(after.Reports).State);
                    Assert.Contains("Check them again after reopening it.", after.Note ?? string.Join(" ", after.Gaps));
                    Assert.Null(x.Engine.CheckedReportPartsForTests());
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        // =========================================================================================
        // R5. A slow check must not commit over a newer selection.
        // =========================================================================================
        [Fact]
        public async Task R5_a_slow_check_does_not_commit_over_a_newer_selection()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    await x.Engine.CreateMeasureAsync("table:" + table, "R5 Shown", "1", "human");
                    var old = WriteReport(x.Root, "R5old", table, "R5 Shown");
                    var fresh = WriteReport(x.Root, "R5new", table, "R5 Shown");

                    var reading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    x.Engine.PublishedReportReaderForTests = async (ws, ids) =>
                    {
                        started.TrySetResult(true);
                        await reading.Task.ConfigureAwait(false);
                        var pr = ReportDefinitionReader.ReadLocalPbir(old);
                        return ids.Select(id => (path: id, error: (string)null, result: pr)).ToList();
                    };
                    await x.Engine.SetReportScopeAsync(new[]
                    {
                        new ReportScopeChoice { Kind = "published", Name = "Slow one", WorkspaceId = "ws-a", WorkspaceName = "Workspace A", ReportId = "slow" },
                    }, "human");

                    var slow = x.Engine.CheckReportsAsync(null, true, null, null, null, "human");
                    await started.Task;
                    // The person changes their mind while the read is in flight.
                    await x.Engine.SetReportScopeAsync(new[] { Local("Different", fresh) }, "human");
                    reading.TrySetResult(true);
                    var result = await slow;

                    // The superseded read must not repopulate the evidence a deletion would rely on.
                    Assert.Equal(0, result.Checked);
                    var after = await x.Engine.ListReportScopeAsync();
                    Assert.Equal(1, after.Listed);
                    Assert.Equal(0, after.Checked);
                    Assert.Equal("Different", Assert.Single(after.Reports).Name);
                    Assert.Null(x.Engine.CheckedReportPartsForTests());
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        // =========================================================================================
        // B2 (Astra, 2026-09-15). A scope change must not slip between the final check and the removal.
        //
        // R1 and R2 both read the scope BEFORE the mutation and validated those captured values inside it,
        // while set_report_scope changed the selection outside the dispatcher entirely. A selection chosen
        // in that window was therefore never checked by anything: all four cases (each door crossed with
        // remove_safe_objects and the apply_plan recheck) deleted the target with a chosen, never-read
        // report present. The repair is not a closer read: scope writes ride the same single-writer
        // dispatcher, and the final validation and the deletion run in ONE turn on ONE snapshot.
        //
        // The interleave is driven the way Astra drove it, through the tracked-commit observer seam, which
        // puts the scope write at the exact instant the old code could not see.
        // =========================================================================================
        private sealed class ScopeSwitchAtCommit : ISessionObserver
        {
            private readonly Func<Task> _switch;
            public bool Fired;
            public Exception? Error;
            public ScopeSwitchAtCommit(Func<Task> change) { _switch = change; }
            public void BeforeCommit(TabularEditor.TOMWrapper.Model model)
            {
                if (Fired) return;
                Fired = true;
                try { _switch().GetAwaiter().GetResult(); } catch (Exception e) { Error = e; }
            }
            public void AfterCommit(SessionCommit commit) { }
        }

        private const string SelectionChanged = "The report selection changed. Check it again and review this removal.";

        [Theory]
        [InlineData("rpc", "remove")]
        [InlineData("rpc", "plan")]
        [InlineData("mcp", "remove")]
        [InlineData("mcp", "plan")]
        public async Task B2_a_scope_chosen_between_the_final_check_and_the_delete_refuses_the_removal(string door, string operation)
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var target = await x.Engine.CreateMeasureAsync("table:" + table, "B2 Target", "1", "human");

                    // Model-only it looks safe, and the report that would protect it is not chosen yet.
                    Assert.Contains((await x.Engine.UnusedObjectsAsync()).Items, i => i.Ref == target && i.Verdict == "safe");
                    Assert.Equal(0, (await x.Engine.ListReportScopeAsync()).Listed);

                    var rpc = new EngineRpcTarget(x.Engine);
                    string itemId = null;
                    if (operation == "plan")
                    {
                        var plan = await rpc.addPlanItem(target, "delete_if_unused");
                        itemId = plan.Items.Single().Id;
                        await rpc.setPlanItem(itemId, approved: true);
                    }

                    // The report really does display the target, so a checked scope would protect it. It is
                    // chosen in the one window the old code could not see, and is therefore never read.
                    var reportPath = WriteReport(x.Root, "B2", table, "B2 Target");
                    var observer = new ScopeSwitchAtCommit(
                        () => x.Engine.SetReportScopeAsync(new[] { Local("Chosen mid-removal", reportPath) }, "human"));
                    x.Sessions.Current.RegisterObserver(observer);

                    if (operation == "remove")
                    {
                        var result = door == "rpc"
                            ? await rpc.removeSafeObjects(new[] { target })
                            : await McpTools.RemoveSafeObjects(x.Engine, new[] { target });
                        Assert.Equal(0, result.Count);
                        Assert.Empty(result.Removed ?? Array.Empty<RemovedObject>());
                        Assert.Equal(SelectionChanged, result.Note);
                        Assert.Contains(result.Skipped, k => k.Ref == target && k.Reason.Contains(SelectionChanged, StringComparison.Ordinal));
                    }
                    else
                    {
                        var result = door == "rpc"
                            ? await rpc.applyPlan(new[] { itemId })
                            : await McpTools.ApplyPlan(x.Engine, new[] { itemId });
                        Assert.Equal(0, result.AppliedCount);
                        Assert.Equal(1, result.SkippedCount);
                        var item = Assert.Single(result.Items);
                        Assert.Equal("skipped", item.Status);
                        Assert.Contains(SelectionChanged, item.Note, StringComparison.Ordinal);
                    }

                    // The interleave really happened, and it really did leave a chosen, unread report behind.
                    Assert.True(observer.Fired, "the scope switch never ran, so nothing was proven");
                    Assert.Null(observer.Error);
                    var scope = await x.Engine.ListReportScopeAsync();
                    Assert.Equal(1, scope.Listed);
                    Assert.Equal(0, scope.Checked);

                    // The one assertion the whole finding is about.
                    Assert.Contains((await x.Engine.ListMeasuresAsync()), m => m.Name == "B2 Target");
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        /// <summary>
        /// The load-bearing half of the B2 repair, and the one the test above cannot see. The interleave test
        /// drives its scope write from inside the mutation turn, so it would also pass if someone "fixed" this by
        /// only moving the read closer to the delete while leaving scope writes on the caller's own thread. That
        /// is the shape Astra named as not closing the gap: a write on another thread can still land between the
        /// final check and the delete, and no offline test can schedule a thread to prove it.
        ///
        /// So this one reads the source instead, and says so. It pins the routing itself: both scope writes, the
        /// selection replace and the reading commit, run inside a dispatcher turn. A structural guard is a weak
        /// proof of behaviour and a strong guard against a silent revert, which is what it is here for.
        /// </summary>
        [Fact]
        public void B2_both_scope_writes_run_on_the_single_writer_dispatcher()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.Engine", "LocalEngine.ReportScope.cs"));

            var setBody = Between(source, "public async Task<ReportScopeResult> SetReportScopeAsync", "PublishScopeChanged(s, origin, \"report scope changed\")");
            Assert.Contains("await s.RunAsync(", setBody, StringComparison.Ordinal);
            Assert.Contains("_reportScopeGenerations[key] =", setBody, StringComparison.Ordinal);
            Assert.True(setBody.IndexOf("await s.RunAsync(", StringComparison.Ordinal)
                        < setBody.IndexOf("_reportScopeChecks.Remove(key)", StringComparison.Ordinal),
                "the selection replace must happen INSIDE the dispatcher turn, not before it");

            var checkBody = Between(source, "var overtaken = await s.RunAsync(", "if (overtaken)");
            Assert.Contains("_reportScopeChecks[key] = evidence = new ScopeEvidence", checkBody, StringComparison.Ordinal);

            // And the basis is taken as ONE snapshot, not reassembled from separate locked reads.
            Assert.Contains("internal ScopeBasis CaptureScopeBasis(Session s)", source, StringComparison.Ordinal);
            Assert.Contains("internal ReportScopeCoverage CheckedCoverageFor(Session s) => CaptureScopeBasis(s).Coverage;", source, StringComparison.Ordinal);
            Assert.Contains("internal string ReportScopeSignature(Session s) => CaptureScopeBasis(s).Signature;", source, StringComparison.Ordinal);

            // The final validation and the deletion share one turn on both removal doors.
            foreach (var (file, marker) in new[]
                     {
                         ("LocalEngine.RemoveSafe.cs", "var atApply = CaptureScopeBasis(s);"),
                         ("LocalEngine.cs", "var applyBasis = CaptureScopeBasis(s);"),
                     })
            {
                var body = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.Engine", file));
                Assert.Contains(marker, body, StringComparison.Ordinal);
            }
        }

        private static string Between(string source, string from, string to)
        {
            var a = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(a >= 0, "could not find '" + from + "'");
            var b = source.IndexOf(to, a, StringComparison.Ordinal);
            Assert.True(b > a, "could not find '" + to + "' after '" + from + "'");
            return source.Substring(a, b - a);
        }

        /// <summary>The other half: with the selection standing still, the same removal still goes through, so
        /// the repair is a refusal of a moved basis and not a refusal of every removal.</summary>
        [Fact]
        public async Task B2_an_unchanged_selection_still_removes_the_object()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var target = await x.Engine.CreateMeasureAsync("table:" + table, "B2 Untouched", "1", "human");
                    var result = await new EngineRpcTarget(x.Engine).removeSafeObjects(new[] { target });
                    Assert.Equal(1, result.Count);
                    Assert.Contains(result.Removed, r => r.Ref == target);
                    Assert.DoesNotContain((await x.Engine.ListMeasuresAsync()), m => m.Name == "B2 Untouched");
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        // =========================================================================================
        // The two doors must mean the same thing about a stale reading, and a hit sentence must name
        // the reports that actually contain the field. Both are read from one committed fixture.
        // =========================================================================================
        [Fact]
        public void The_report_sentences_match_the_shared_fixture()
        {
            var fixturePath = Path.Combine(RepoRoot(), "docs", "report-scope-sentences.json");
            Assert.True(File.Exists(fixturePath), "the shared sentence fixture must be committed at " + fixturePath);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(fixturePath));

            foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            {
                var name = c.GetProperty("field").GetString();
                var scope = ScopeFrom(c.GetProperty("scope"));
                var hits = c.GetProperty("hits").EnumerateArray()
                    .Select(h => new ImpactReportHit { Path = h.GetString(), Name = h.GetString(), Visuals = 1 })
                    .ToArray();
                var impact = new ImpactResult { Root = "measure:Sales/" + name, RootName = name, RootKind = "measure", Impacted = Array.Empty<ImpactNode>() };
                Assert.Equal(c.GetProperty("reportSentence").GetString(),
                    ImpactAssessmentBuilder.ReportSummary(impact, hits, scope));
            }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static ReportScopeResult ScopeFrom(System.Text.Json.JsonElement e) =>
            ReportScopeResult.From(e.GetProperty("model").GetString(),
                e.GetProperty("reports").EnumerateArray().Select(r => new ReportScopeReport
                {
                    Id = r.GetProperty("id").GetString(),
                    Kind = "published",
                    Name = r.GetProperty("name").GetString(),
                    Source = r.GetProperty("source").GetString(),
                    State = r.GetProperty("state").GetString(),
                    CheckedWhenUtc = r.TryGetProperty("checkedWhenUtc", out var w) ? w.GetString() : null,
                }).ToList());
    }
}
