using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;
using static Semanticus.Tests.PlanQueueKit;

namespace Semanticus.Tests
{
    /// <summary>Verifier probes for m15/drive-finish (13 Oct 2026). Each test states the brief's claim; a failure is the defect.</summary>
    public sealed class VerifyDriveFinishProbes
    {
        private readonly ITestOutputHelper _out;
        public VerifyDriveFinishProbes(ITestOutputHelper output) { _out = output; }

        /// <summary>One generic, hidden column per kind of use, so none is a visible attribute.</summary>
        private static async Task<(DecisionFixture F, Dictionary<string, string> Planted)> PlantedAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Customer", "City", "String", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            var planted = new Dictionary<string, string>
            {
                ["measure"] = "column:Sales/Batch Key M",
                ["relationship"] = "column:Sales/CustomerKey",
                ["hierarchy"] = "column:Customer/Hash Key H",
                ["sortBy"] = "column:Customer/Row ID S",
                ["rls"] = "column:Customer/Etl Flag R",
                ["calcItem"] = "column:Sales/Source Code C",
                ["report"] = "column:Customer/Load Id P",
            };
            foreach (var r in planted.Values.Distinct().Where(r => !r.EndsWith("CustomerKey")))
            {
                var parts = r.Substring("column:".Length).Split('/');
                await e.CreateColumnAsync("table:" + parts[0], parts[1], "String", null, "human");
                await e.SetObjectPropertyAsync(r, "IsHidden", "true", "human");
            }
            await e.CreateMeasureAsync("table:Sales", "Batch Count", "DISTINCTCOUNT ( Sales[Batch Key M] )", "human");
            await e.CreateHierarchyAsync("table:Customer", "Geo", new[] { "City", "Hash Key H" }, "human");
            await e.SetObjectPropertyAsync("column:Customer/City", "SortByColumn", "Row ID S", "human");
            await e.CreateRoleAsync("Readers", "Read", "human");
            await e.SetTablePermissionAsync("Readers", "table:Customer", "Customer[Etl Flag R] = \"Y\"", "human");
            var cg = await e.CreateCalculationGroupAsync("Scope", "human");
            await e.CreateCalculationItemAsync(cg, "Only A", "CALCULATE ( SELECTEDMEASURE (), Sales[Source Code C] = \"A\" )", "human");
            await f.SaveAsAsync("Planted.bim");

            var visual = "{ \"visual\": { \"query\": { \"queryState\": { \"Values\": { \"projections\": [ "
                       + "{ \"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"Customer\" } }, "
                       + "\"Property\": \"Load Id P\" } } } ] } } } } }";
            var reports = Path.Combine(f.Workspace, "Reports");
            var visualDir = Path.Combine(reports, "Sales.Report", "definition", "pages", "p1", "visuals", "v1");
            Directory.CreateDirectory(visualDir);
            File.WriteAllText(Path.Combine(reports, "Sales.Report", "definition", "report.json"), "{}");
            File.WriteAllText(Path.Combine(visualDir, "visual.json"), visual);
            await e.SetReportScopeAsync(new[] { new Semanticus.Engine.Lineage.ReportScopeChoice { Kind = "local", Name = "Sales", Path = reports } }, "human");
            await e.CheckReportsAsync(Array.Empty<string>(), false, null, null, null, "human", default);
            return (f, planted);
        }

        private static Task<string[]> ColumnsAsync(DecisionFixture f) =>
            f.Sessions.Current.ReadAsync(m => m.AllColumns.Select(c => ObjectRefs.For(c)).ToArray());

        [Fact]
        public async Task Probe1a_the_scripted_sweep_keeps_a_generic_column_each_kind_of_use_needs()
        {
            var (f, planted) = await PlantedAsync();
            using var _ = f;
            var all = await ColumnsAsync(f);
            foreach (var r in planted.Values) Assert.Contains(r, all);
            var unused = await f.Engine.UnusedObjectsAsync();
            foreach (var i in unused.Items) _out.WriteLine($"UNUSED {i.Ref} {i.Verdict}");
            // DriveFinishTests' scripted assistant: delete every "safe" non-measure.
            var gone = unused.Items.Where(i => i.Verdict == "safe" && i.Kind != "measure").Select(i => i.Ref).ToArray();
            if (gone.Length > 0) await f.Engine.DeleteObjectsAsync(gone, "agent");
            var after = await ColumnsAsync(f);
            var lost = planted.Where(p => !after.Contains(p.Value)).Select(p => p.Key + "=" + p.Value).ToArray();
            Assert.True(lost.Length == 0, "the sweep removed a column something uses: " + string.Join(", ", lost));
        }

        [Fact]
        public async Task Probe1b_remove_safe_objects_the_route_the_defaults_name_keeps_every_kind_of_use_even_when_named()
        {
            var (f, planted) = await PlantedAsync();
            using var _ = f;
            var rep = await f.Engine.RemoveSafeObjectsAsync(planted.Values.ToArray(), null, "agent");
            var after = await ColumnsAsync(f);
            var lost = planted.Where(p => !after.Contains(p.Value)).Select(p => p.Key).ToArray();
            Assert.True(lost.Length == 0, "remove_safe_objects removed a column something uses: " + string.Join(", ", lost));
            Assert.Equal(0, rep.Count);
        }

        [Fact]
        public async Task Probe1c_the_defaults_name_the_report_aware_sweep_never_get_lineage_with_delete_objects()
        {
            foreach (var id in new[] { "cleanup/model", "proposal/collisions", "proposal/visibility" })
            {
                var choice = AssistantDefaults.For(id).Choice;
                Assert.Contains("unused_objects", choice);
                Assert.Contains("remove_safe_objects", choice);
                Assert.DoesNotContain("delete_objects", choice);
                Assert.DoesNotContain("get_lineage", choice);
                Assert.Contains("hide", choice, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Contains("visibleAttribute", AssistantDefaults.For("cleanup/model").Choice);
            await Task.CompletedTask;
        }

        [Fact]
        public async Task Probe1d_remove_safe_objects_removes_an_unused_hidden_column_and_skips_a_visible_attribute()
        {
            var (f, planted) = await PlantedAsync();
            using var _ = f;
            var e = f.Engine;
            await e.CreateColumnAsync("table:Customer", "Spare Code", "String", null, "human");
            await e.SetObjectPropertyAsync("column:Customer/Spare Code", "IsHidden", "true", "human");
            await e.CreateColumnAsync("table:Customer", "Region Name", "String", null, "human");
            await e.CheckReportsAsync(Array.Empty<string>(), false, null, null, null, "human", default);   // the model changed, so the report is read again
            var unused = await e.UnusedObjectsAsync();
            var refs = unused.Items.Where(i => i.Kind != "measure").Select(i => i.Ref).ToArray();
            foreach (var i in unused.Items) _out.WriteLine($"UNUSED {i.Ref} {i.Kind} {i.Verdict}");
            var rep = await e.RemoveSafeObjectsAsync(refs, null, "agent");
            foreach (var k in rep.Skipped) _out.WriteLine($"SKIPPED {k.Ref}: {k.Reason}");
            var after = await ColumnsAsync(f);
            Assert.DoesNotContain("column:Customer/Spare Code", after);
            Assert.Contains("column:Customer/Region Name", after);
            foreach (var r in planted.Values) Assert.Contains(r, after);
        }

        [Fact]
        public async Task Probe3d_plan_rows_note_from_the_assistant_is_the_assistants_and_keeps_the_persons()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.SaveAsAsync("NoteBulk.bim");
            f.UseNull();
            var id = await AskAsync(f.Engine, "Describe the Total Sales measure", new[] { "measure:Sales/Total Sales" });
            await f.Engine.SetPlanTaskNoteAsync(id, "Only the visible columns, please.");
            var res = await f.Engine.PlanRowsAsync(new[] { new PlanRowEdit { Id = id, Action = "note", Text = "Needs a date column: none exists in this model." } }, "agent");
            var row = Row(res.Plan, id);
            Assert.Equal("Only the visible columns, please.", row.Note);
            Assert.Equal("Needs a date column: none exists in this model.", row.AssistantNote);
            Assert.StartsWith("Needs a date column", row.StateNote ?? "");
        }

        [Fact]
        public async Task Probe5_an_assistants_backup_copy_does_not_move_the_session()
        {
            using var f = await GappyAsync("Home.bim");
            var home = Path.Combine(f.Workspace, "Home.bim");
            var backup = Path.Combine(f.Workspace, "Backup.bim");
            await f.Engine.SaveAsync(backup, "bim", false, "agent");
            Assert.True(File.Exists(backup));
            await f.Engine.CreateMeasureAsync("table:Sales", "After Backup", "1", "agent");
            await f.Engine.SaveAsync(null, null, false, "agent");
            Assert.Contains("After Backup", File.ReadAllText(home));
            Assert.DoesNotContain("After Backup", File.ReadAllText(backup));
        }

        [Fact]
        public async Task Probe3a_an_assistant_note_does_not_overwrite_the_persons_note()
        {
            using var f = await GappyAsync("NoteKeep.bim");
            var id = await AskAsync(f.Engine, "Describe the Total Sales measure", new[] { "measure:Sales/Total Sales" });
            await f.Engine.SetPlanTaskNoteAsync(id, "Only the visible columns, please.");
            var plan = await f.Engine.SetPlanTaskNoteAsync(id, "Needs a date column: none exists in this model.", "agent");
            var row = Row(plan, id);
            _out.WriteLine($"NOTE={row.Note} | STATE={row.StateWord} {row.StateNote}");
            Assert.True((row.Note ?? "").Contains("Only the visible columns") || (row.StateNote ?? "").Contains("Only the visible columns"),
                "the person's note is gone after the assistant's set_plan_task_note: note now reads '" + row.Note + "'");
        }

        [Fact]
        public async Task Probe3b_an_assistant_note_does_not_lead_the_state_note_of_a_persons_task()
        {
            using var f = await GappyAsync("NotePerson.bim");
            var id = await AskAsync(f.Engine, "Check the Total Sales format", new[] { "measure:Sales/Total Sales" }, doer: PlanDoers.You);
            var plan = await f.Engine.SetPlanTaskNoteAsync(id, "Needs a date column: none exists in this model.", "agent");
            var row = Row(plan, id);
            _out.WriteLine($"WHO={row.Who} DOER={row.Doer} STATE={row.StateWord} {row.StateNote}");
            Assert.False((row.StateNote ?? "").StartsWith("Needs a date column"), "the assistant's note leads a person's row: " + row.StateNote);
        }

        [Fact]
        public async Task Probe3c_an_assistant_note_does_not_lead_a_found_row_the_person_took_back()
        {
            using var f = await CommutationFixtures.WideAsync("NoteTaken.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var row = plan.Items.First(i => i.Who == ModelPlanOwners.Assistant && !PlanTasks.IsTaskId(i.Id)
                && (i.StateWord == PlanStateWords.Ready || i.StateWord == PlanStateWords.Waiting));
            await f.Engine.SetPlanItemDoerAsync(row.Id, PlanDoers.You, "human");
            var after = await f.Engine.SetPlanTaskNoteAsync(row.Id, "Needs a date column: none exists in this model.", "agent");
            var r = Row(after, row.Id);
            _out.WriteLine($"ROW {row.Id} WHO={r.Who} DOER={r.Doer} STATE={r.StateWord} {r.StateNote}");
            Assert.False((r.StateNote ?? "").StartsWith("Needs a date column") && r.Doer == PlanDoers.You,
                "the assistant's note leads a row the person took: " + r.StateNote);
        }

        [Fact]
        public async Task Probe4_a_row_whose_object_is_gone_does_not_block_the_row_after_it()
        {
            using var f = await GappyAsync("GoneRow.bim");
            await f.Engine.CreateMeasureAsync("table:Sales", "Old Total", "SUM ( Sales[Amount] )", "human");
            var first = await AskAsync(f.Engine, "Describe both totals", new[] { "measure:Sales/Total Sales", "measure:Sales/Old Total" });
            var second = await AskAsync(f.Engine, "Describe Old Total for the report authors", new[] { "measure:Sales/Old Total" });
            var before = await f.Engine.GetModelPlanAsync();
            _out.WriteLine($"BEFORE second={Row(before, second).StateWord} waits={string.Join(",", (Row(before, second).WaitsFor ?? Array.Empty<PlanWaitRef>()).Select(w => w.Title))}");

            await f.Engine.DeleteObjectAsync("measure:Sales/Total Sales", "human");
            var plan = await f.Engine.GetModelPlanAsync();
            var a = Row(plan, first); var b = Row(plan, second);
            _out.WriteLine($"FIRST {a.StateWord} :: {a.StateNote}");
            _out.WriteLine($"SECOND {b.StateWord} :: {b.StateNote} :: waits={string.Join(",", (b.WaitsFor ?? Array.Empty<PlanWaitRef>()).Select(w => w.Title))}");
            Assert.Equal(PlanStateWords.NeedsYou, a.StateWord);
            var claim = await f.Engine.ClaimAssistantRequestAsync(second);
            Assert.True(b.StateWord != PlanStateWords.Waiting && claim.Ok,
                "the gone row blocks the row after it: second is " + b.StateWord + " (" + b.StateNote + "); claim: " + claim.Message);
        }
    }
}
