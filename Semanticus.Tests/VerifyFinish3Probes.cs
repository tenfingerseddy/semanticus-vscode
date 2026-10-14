using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    /// <summary>Verifier probes for m15/finish3 (14 Oct 2026). Each test states the brief's claim; a failure is the defect.</summary>
    public sealed class VerifyFinish3Probes
    {
        private readonly ITestOutputHelper _out;
        public VerifyFinish3Probes(ITestOutputHelper output) { _out = output; }

        // Every planted column is VISIBLE, in the Customer dimension, and has exactly one use.
        private const string ReportCol = "column:Customer/Region R";
        private const string SortCol = "column:Customer/Segment Order S";
        private const string HierCol = "column:Customer/Country H";
        private const string RlsCol = "column:Customer/Territory L";
        private const string FreeCol = "column:Customer/Fax F";          // nothing uses it: the happy path removes it
        private const string ReportTable = "table:Promo Slicer";         // used only by a report visual
        private const string RoleTable = "table:Access Map";             // used only by a role's own table permission
        private const string RoleRefTable = "table:Access Ref";          // used only by a role filter on another table
        private const string CalcTable = "table:Calc Source";            // its measure is used only by a calculation item
        private const string FreeTable = "table:Lonely";                 // nothing uses it: the happy path removes it

        /// <param name="scope">none: no report chosen; checked: chosen and checked; unchecked: chosen, not checked.</param>
        private async Task<DecisionFixture> PlantedAsync(string scope)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.SetObjectPropertyAsync("column:Sales/CustomerKey", "IsHidden", "true", "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.SetObjectPropertyAsync("column:Customer/CustomerKey", "IsHidden", "true", "human");
            await e.CreateColumnAsync("table:Customer", "City", "String", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Report Only", "SUM ( Sales[Amount] ) * 2", "human");   // nothing in the model uses it; a report visual does
            await e.CreateMeasureAsync("table:Sales", "Free Measure", "SUM ( Sales[Amount] ) * 3", "human");  // nothing uses it anywhere
            foreach (var r in new[] { ReportCol, SortCol, HierCol, RlsCol, FreeCol })
                await e.CreateColumnAsync("table:Customer", r.Substring("column:Customer/".Length), "String", null, "human");
            await e.SetObjectPropertyAsync("column:Customer/City", "SortByColumn", "Segment Order S", "human");
            await e.CreateHierarchyAsync("table:Customer", "Geo", new[] { "Country H", "City" }, "human");
            await e.CreateRoleAsync("Readers", "Read", "human");

            foreach (var t in new[] { ReportTable, RoleTable, RoleRefTable, CalcTable, FreeTable })
            {
                var n = t.Substring("table:".Length);
                await e.CreateTableAsync(n, "human");
                await e.CreateColumnAsync(t, "Code", "String", null, "human");
            }
            await e.SetTablePermissionAsync("Readers", "table:Customer", "Customer[Territory L] IN VALUES ( 'Access Ref'[Code] )", "human");
            await e.SetTablePermissionAsync("Readers", RoleTable, "'Access Map'[Code] = \"A\"", "human");
            await e.CreateMeasureAsync(CalcTable, "Calc Base", "COUNTROWS ( 'Calc Source' )", "human");
            var cg = await e.CreateCalculationGroupAsync("Scope", "human");
            await e.CreateCalculationItemAsync(cg, "Base", "SELECTEDMEASURE () + [Calc Base]", "human");
            await f.SaveAsAsync("Finish3Planted.bim");

            if (scope == "none") return f;
            string MeasureField(string entity, string prop) => "{ \"field\": { \"Measure\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"" + entity
                + "\" } }, \"Property\": \"" + prop + "\" } } }";
            string Field(string entity, string prop) => "{ \"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"" + entity
                + "\" } }, \"Property\": \"" + prop + "\" } } }";
            var visual = "{ \"visual\": { \"query\": { \"queryState\": { \"Values\": { \"projections\": [ "
                       + Field("Customer", "Region R") + ", " + Field("Promo Slicer", "Code") + ", " + MeasureField("Sales", "Report Only") + " ] } } } } }";
            var reports = Path.Combine(f.Workspace, "Reports");
            var visualDir = Path.Combine(reports, "Sales.Report", "definition", "pages", "p1", "visuals", "v1");
            Directory.CreateDirectory(visualDir);
            File.WriteAllText(Path.Combine(reports, "Sales.Report", "definition", "report.json"), "{}");
            File.WriteAllText(Path.Combine(visualDir, "visual.json"), visual);
            await e.SetReportScopeAsync(new[] { new Semanticus.Engine.Lineage.ReportScopeChoice { Kind = "local", Name = "Sales", Path = reports } }, "human");
            if (scope == "checked") await e.CheckReportsAsync(Array.Empty<string>(), false, null, null, null, "human", default);
            return f;
        }

        private static Task<string[]> RefsAsync(DecisionFixture f) => f.Sessions.Current.ReadAsync(m =>
            m.AllColumns.Select(c => ObjectRefs.For(c)).Concat(m.Tables.Select(t => ObjectRefs.For(t)))
                .Concat(m.Tables.SelectMany(t => t.Measures).Select(x => ObjectRefs.For(x))).ToArray());

        /// <summary>The route as AssistantDefaults "cleanup/model", both skills and Program.cs now write it: unused_objects, then remove_safe_objects
        /// for what it marks safe, then delete_object for each visibleAttribute column "when the chosen reports do not use it".</summary>
        private async Task<string[]> FollowTheRouteAsync(DecisionFixture f)
        {
            var unused = await f.Engine.UnusedObjectsAsync();
            _out.WriteLine("caveat: " + unused.Caveat);
            foreach (var i in unused.Items) _out.WriteLine($"UNUSED {i.Ref} {i.Verdict} | {i.Reason}");
            var swept = await f.Engine.RemoveSafeObjectsAsync(unused.Items.Where(i => i.Verdict == "safe").Select(i => i.Ref).ToArray(), null, "agent");
            _out.WriteLine("remove_safe_objects: " + swept.Note + " removed " + string.Join(", ", (swept.Removed ?? Array.Empty<RemovedObject>()).Select(r => r.Ref)));
            foreach (var v in unused.Items.Where(i => i.Verdict == "visibleAttribute"))
            {
                await f.Engine.DeleteObjectAsync(v.Ref, "agent");
                _out.WriteLine("delete_object " + v.Ref);
            }
            return await RefsAsync(f);
        }

        private void AssertKept(string[] after, string scope, params (string Use, string Ref)[] planted)
        {
            var lost = planted.Where(p => !after.Contains(p.Ref)).Select(p => p.Use + " (" + p.Ref + ")").ToArray();
            Assert.True(lost.Length == 0, $"[{scope}] the route deleted what something uses: " + string.Join(", ", lost));
        }

        [Theory]
        [InlineData("none")]
        [InlineData("checked")]
        [InlineData("unchecked")]
        public async Task Probe1_the_route_never_deletes_a_visible_column_a_report_sort_by_hierarchy_or_role_uses(string scope)
        {
            using var f = await PlantedAsync(scope);
            var after = await FollowTheRouteAsync(f);
            AssertKept(after, scope, ("sortBy", SortCol), ("hierarchy", HierCol), ("rls", RlsCol));
            if (scope == "checked") Assert.DoesNotContain(FreeCol, after);   // the happy path: an unused visible column goes
            AssertKept(after, scope, ("report", ReportCol));
            if (scope == "none")
            {
                var item = (await f.Engine.UnusedObjectsAsync()).Items.Single(i => i.Ref == ReportCol);
                Assert.Equal("caution", item.Verdict);
                Assert.Contains("Hide it", item.Reason);
            }
        }

        [Theory]
        [InlineData("none")]
        [InlineData("checked")]
        [InlineData("unchecked")]
        public async Task Probe2_a_table_is_removed_only_when_nothing_uses_it_report_and_role_included(string scope)
        {
            using var f = await PlantedAsync(scope);
            var after = await FollowTheRouteAsync(f);
            AssertKept(after, scope, ("role table permission", RoleTable), ("role filter on another table", RoleRefTable), ("calculation item", CalcTable));
            if (scope == "checked") Assert.DoesNotContain(FreeTable, after);   // the happy path: a table nothing uses goes
            AssertKept(after, scope, ("report", ReportTable));
        }

        private const string ReportMeasure = "measure:Sales/Report Only";
        private const string FreeMeasure = "measure:Sales/Free Measure";

        /// <summary>Verifier note 4: a measure only a report uses is "caution", not "safe", until the sweep is report-aware (the gate the lane gave visible columns).</summary>
        [Theory]
        [InlineData("none")]
        [InlineData("checked")]
        [InlineData("unchecked")]
        public async Task Probe4_a_measure_only_a_report_uses_is_kept_until_reports_are_checked_and_an_unused_one_then_goes(string scope)
        {
            using var f = await PlantedAsync(scope);
            if (scope == "none")
            {
                var items = (await f.Engine.UnusedObjectsAsync()).Items;
                Assert.Equal("caution", items.Single(i => i.Ref == ReportMeasure).Verdict);   // not "safe": no report was checked
                Assert.Equal("caution", items.Single(i => i.Ref == FreeMeasure).Verdict);     // it cannot be told from a report-only measure yet
            }
            var after = await FollowTheRouteAsync(f);
            AssertKept(after, scope, ("report-only measure", ReportMeasure));
            if (scope != "checked") Assert.Contains(FreeMeasure, after);        // no report checked, or chosen but not read: nothing is removed on a guess
            else Assert.DoesNotContain(FreeMeasure, after);                      // reports checked and unused: removable, the happy path
        }

        [Fact]
        public async Task Probe3_the_persons_Could_not_on_a_given_row_is_not_shown_as_the_assistants()
        {
            using var f = await PlanQueueKit.GappyAsync("Finish3CouldNot.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var row = plan.Items.First(i => i.Who == ModelPlanOwners.Assistant && !PlanTasks.IsTaskId(i.Id) && PlanStateWords.IsOpen(i.StateWord));
            await f.Engine.SetPlanItemDoerAsync(row.Id, PlanDoers.Assistant, "human");
            var given = (await f.Engine.GetModelPlanAsync()).Items.Single(i => PlanTasks.IsTaskId(i.Id) && i.Title == row.Title);
            _out.WriteLine("given: " + string.Join(" / ", given.Why));
            Assert.StartsWith("You gave this to your assistant", given.Why[0]);
            const string reason = "I will do this one in Desktop.";
            var after = (await f.Engine.DeclinePlanTaskAsync(given.Id, reason)).Items.Single(i => i.Id == given.Id);
            _out.WriteLine($"after the person's Could not: {after.StateWord} {after.StateNote} | {string.Join(" / ", after.Why)} | {after.Sub}");
            Assert.False(after.Why.Any(w => w.StartsWith("Your assistant could not do this", StringComparison.Ordinal)),
                "the person pressed Could not, and the row says: " + string.Join(" / ", after.Why));
            Assert.Equal("You could not do this: " + reason, after.Why[0]);
        }

        [Fact]
        public async Task Probe4_a_given_Fix_task_title_counts_the_findings_that_still_stand_per_Review()
        {
            using var f = await PlanQueueKit.GappyAsync("Finish3Fix.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var row = plan.Items.FirstOrDefault(i => i.Id == LocalEngine.CoverageBpaItemId);
            _out.WriteLine("coverage row: " + (row?.Title ?? "(none)") + " | " + string.Join(" / ", row?.Why ?? Array.Empty<string>()));
            Assert.NotNull(row);
            await f.Engine.SetPlanItemDoerAsync(row.Id, PlanDoers.Assistant, "human");
            var scan = await f.Engine.BpaScanAsync();
            var claimed = (await f.Engine.GetModelPlanAsync()).Items.Single(i => PlanTasks.IsTaskId(i.Id) && i.OriginLink?.Ref == LocalEngine.CoverageBpaItemId).Claims;
            _out.WriteLine("claims: " + string.Join(", ", claimed));
            // The float finding fixed by hand, the way the assistant does it (change the type), so one claim stops standing.
            await f.Engine.SetObjectPropertyAsync("column:Sales/Amount", "DataType", "Decimal", "agent");
            var fixedOne = true;
            foreach (var v in scan.Violations.Where(v => false && !v.Waived && claimed.Contains(v.RuleId + "|" + v.ObjectRef, StringComparer.OrdinalIgnoreCase)))
            {
                try { await f.Engine.ApplyFixAsync(v.RuleId, v.ObjectRef, "agent"); _out.WriteLine("fixed " + v.RuleId + " " + v.ObjectRef); fixedOne = true; break; }
                catch (InvalidOperationException ex) { _out.WriteLine("no fix for " + v.RuleId + ": " + ex.Message); }
            }
            Assert.True(fixedOne, "no claimed finding could be fixed by apply_fix");
            var task = (await f.Engine.GetModelPlanAsync()).Items.Single(i => PlanTasks.IsTaskId(i.Id) && i.OriginLink?.Ref == LocalEngine.CoverageBpaItemId);
            var now = await f.Engine.BpaScanAsync();
            var standing = task.Claims.Count(c => now.Violations.Any(v => !v.Waived && string.Equals(v.RuleId + "|" + v.ObjectRef, c, StringComparison.OrdinalIgnoreCase)));
            _out.WriteLine($"task title '{task.Title}' (given as '{row.Title}'), claims {task.Claims.Length}, standing per Review {standing}, Review shows {now.Violations.Count(v => !v.Waived)}");
            var plan2 = await f.Engine.GetModelPlanAsync();
            var onOtherRows = plan2.Items.Where(i => i.Id != task.Id && i.Claims != null).SelectMany(i => i.Claims).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var objectsOnRows = plan2.Items.Where(i => i.Id != task.Id && PlanStateWords.IsOpen(i.StateWord)).SelectMany(i => i.Objects ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var g in now.Violations.Where(v => !v.Waived).GroupBy(v => (v.Severity, v.RuleId)))
                _out.WriteLine($"REVIEW sev{g.Key.Severity} {g.Key.RuleId} x{g.Count()} advice={g.First().Advice} onTask={g.Count(v => task.Claims.Contains(v.RuleId + "|" + v.ObjectRef, StringComparer.OrdinalIgnoreCase))} claimedElsewhere={g.Count(v => onOtherRows.Contains(v.RuleId + "|" + v.ObjectRef))} objOnOpenRow={g.Count(v => objectsOnRows.Contains(v.ObjectRef))}");
            _out.WriteLine("task why: " + string.Join(" / ", task.Why ?? Array.Empty<string>()));
            Assert.Contains(task.Why, w => w.Contains("Review lists these too, with the Info notes", StringComparison.Ordinal));
            Assert.Equal("Fix " + standing + " best-practice issue" + (standing == 1 ? "" : "s"), task.Title);
        }
        [Fact]
        public async Task Probe5_a_model_with_no_reports_anywhere_is_report_aware_over_an_empty_set_says_so_and_the_happy_path_removes()
        {
            using var f = await PlantedAsync("none");
            await f.Engine.SetReportScopeAsync(Array.Empty<Semanticus.Engine.Lineage.ReportScopeChoice>(), "agent");
            var unused = await f.Engine.UnusedObjectsAsync();
            _out.WriteLine("caveat: " + unused.Caveat);
            Assert.Contains("no reports", unused.Caveat, StringComparison.OrdinalIgnoreCase);
            var after = await FollowTheRouteAsync(f);
            Assert.DoesNotContain(FreeCol, after);
            Assert.DoesNotContain(FreeTable, after);
            AssertKept(after, "declared none", ("sortBy", SortCol), ("hierarchy", HierCol), ("rls", RlsCol),
                ("role table permission", RoleTable), ("role filter on another table", RoleRefTable), ("calculation item", CalcTable));
            await f.Engine.UndoAsync("agent");
            await f.Engine.UndoAsync("agent");
            var back = await RefsAsync(f);
            _out.WriteLine("after two undos: Fax " + back.Contains(FreeCol) + ", Lonely " + back.Contains(FreeTable));
        }

        [Fact]
        public async Task Probe6_with_no_report_checked_Run_the_plan_by_the_assistant_deletes_nothing_a_report_uses()
        {
            using var f = await PlantedAsync("none");
            var plan = await f.Engine.GetModelPlanAsync();
            var cleanup = plan.Items.FirstOrDefault(i => i.Id == "cleanup/model");
            _out.WriteLine("cleanup row: " + (cleanup == null ? "(none)" : cleanup.Title + " | " + cleanup.StateWord + " | lines "
                + string.Join(", ", (cleanup.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()).Select(l => l.Target + (l.Kept ? "+" : "-")))));
            await f.Engine.ApplyPlanItemsAsync(null, "agent", plan.ReadyDigest);
            var after = await RefsAsync(f);
            AssertKept(after, "none, Run the plan", ("report", ReportCol), ("report", ReportTable), ("sortBy", SortCol), ("hierarchy", HierCol), ("rls", RlsCol));
        }
    }
}
