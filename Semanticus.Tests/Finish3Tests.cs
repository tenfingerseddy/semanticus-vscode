using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    /// <summary>
    /// Hero run 21, J2 (14 Oct 2026): the drive ended Best practice C 78 because the assistant asked whether to keep 13 unlinked lookup tables, the
    /// closed rows said only "Needs you", a declined row said "You gave this to your assistant", the "Fix 73" row disagreed with Review, and Plan done
    /// jumped on Refresh plan. Each test below fails on the build before this lane.
    /// </summary>
    public sealed class Finish3Tests
    {
        private readonly ITestOutputHelper _out;
        public Finish3Tests(ITestOutputHelper output) { _out = output; }

        private static async Task<DecisionFixture> WithLookupsAsync()
        {
            var f = await CommutationFixtures.WideAsync("finish3-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".bim");
            var e = f.Engine;
            foreach (var n in new[] { "Lookup A", "Lookup B" })
            {
                await e.CreateTableAsync(n, "human");
                await e.CreateColumnAsync("table:" + n, "Code", "String", null, "human");
            }
            // A lookup something uses: a measure counts its rows, so it stays.
            await e.CreateTableAsync("Lookup Used", "human");
            await e.CreateColumnAsync("table:Lookup Used", "Code", "String", null, "human");
            await e.CreateMeasureAsync("table:Sales", "Lookup Rows", "COUNTROWS ( 'Lookup Used' )", "human");
            // The model has no reports (none found locally or published): the sweep is report-aware over an empty set.
            await e.SetReportScopeAsync(Array.Empty<Semanticus.Engine.Lineage.ReportScopeChoice>(), "human");
            return f;
        }

        [Fact]
        public async Task With_no_report_checked_a_table_nothing_in_the_model_uses_is_kept_and_the_sweep_skips_it()
        {
            using var f = await WithLookupsAsync();
            // Undo the declaration: the same model, but nobody has said whether it has reports.
            using var g = new DecisionFixture(TestEntitlements.Pro);
            await g.Engine.CreateModelAsync("Contoso", 1604);
            await g.Engine.CreateTableAsync("Lookup A", "human");
            await g.Engine.CreateColumnAsync("table:Lookup A", "Code", "String", null, "human");
            var unused = await g.Engine.UnusedObjectsAsync();
            Assert.DoesNotContain(unused.Items, i => i.Kind == "table" && i.Verdict == "safe");
            Assert.Contains(unused.Items, i => i.Ref == "table:Lookup A" && i.Verdict == "caution");
            var swept = await g.Engine.RemoveSafeObjectsAsync(new[] { "table:Lookup A" }, null, "agent");
            Assert.Equal(0, swept.Count);
            var declared = await f.Engine.UnusedObjectsAsync();
            Assert.Contains("no reports", declared.Caveat, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task A_table_nothing_uses_is_listed_safe_and_removed_by_the_sweep_and_a_table_something_uses_is_kept()
        {
            using var f = await WithLookupsAsync();
            var unused = await f.Engine.UnusedObjectsAsync();
            var tables = unused.Items.Where(i => i.Kind == "table").ToArray();
            _out.WriteLine(string.Join(", ", tables.Select(t => t.Ref + "=" + t.Verdict)));
            Assert.Contains(tables, t => t.Ref == "table:Lookup A" && t.Verdict == "safe");
            Assert.Contains(tables, t => t.Ref == "table:Lookup B" && t.Verdict == "safe");
            Assert.DoesNotContain(tables, t => t.Ref == "table:Lookup Used");
            Assert.DoesNotContain(tables, t => t.Ref == "table:Sales");   // a table in a relationship is never a candidate

            var report = await f.Engine.RemoveSafeObjectsAsync(new[] { "table:Lookup A", "table:Lookup B" }, null, "agent");
            Assert.Equal(2, report.Count);
            var names = await f.Sessions.Current.ReadAsync(m => m.Tables.Select(t => t.Name).ToArray());
            Assert.DoesNotContain("Lookup A", names);
            Assert.Contains("Lookup Used", names);
            await f.Engine.UndoAsync("agent");
            Assert.Contains("Lookup A", await f.Sessions.Current.ReadAsync(m => m.Tables.Select(t => t.Name).ToArray()));
        }

        [Fact]
        public async Task A_scripted_assistant_that_follows_the_skill_removes_the_unlinked_lookups_without_asking_and_the_best_practice_findings_for_them_are_gone()
        {
            using var f = await WithLookupsAsync();
            string[] Findings(Semanticus.Analysis.BpaScorecard c) => c.Violations.Where(v => !v.Waived && v.RuleId == "ENSURE_TABLES_HAVE_RELATIONSHIPS").Select(v => v.ObjectRef).ToArray();
            var before = Findings(await f.Engine.BpaScanAsync());
            _out.WriteLine("before: " + string.Join(", ", before));
            Assert.Contains("table:Lookup A", before);
            // The skill: unused_objects, then remove_safe_objects for what it marks safe (columns, measures and tables), no question.
            var unused = await f.Engine.UnusedObjectsAsync();
            var report = await f.Engine.RemoveSafeObjectsAsync(unused.Items.Where(i => i.Verdict == "safe").Select(i => i.Ref).ToArray(), null, "agent");
            _out.WriteLine(report.Note);
            var after = Findings(await f.Engine.BpaScanAsync());
            _out.WriteLine("after: " + string.Join(", ", after));
            Assert.DoesNotContain("table:Lookup A", after);
            Assert.DoesNotContain("table:Lookup B", after);
            // The lookup something uses is kept (it is still a finding; the skill leaves it out of what Copilot sees and says why).
            Assert.Contains(await f.Sessions.Current.ReadAsync(m => m.Tables.Select(t => t.Name).ToArray()), n => n == "Lookup Used");
        }

        [Fact]
        public async Task The_cleanup_row_still_counts_columns_and_measures_only()
        {
            using var f = await WithLookupsAsync();
            var plan = await f.Engine.GetModelPlanAsync();
            foreach (var i in plan.Items.Where(i => i.Id == "cleanup/model"))
                Assert.DoesNotContain(i.Objects ?? Array.Empty<string>(), o => o.StartsWith("table:", StringComparison.Ordinal));
        }

        [Fact]
        public void The_defaults_and_both_skills_remove_a_table_nothing_uses_and_never_ask_about_an_undo_able_step()
        {
            var choice = AssistantDefaults.For("cleanup/model").Choice;
            Assert.Contains("table nothing uses", choice);
            Assert.Contains("do not ask whether to keep it", choice);
            var root = AppContext.BaseDirectory;
            while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root, ".claude", "skills", "semanticus", "SKILL.md"))) root = System.IO.Path.GetDirectoryName(root);
            Assert.NotNull(root);
            foreach (var p in new[] { ".claude/skills/semanticus/SKILL.md", ".claude/skills/ai-ready/SKILL.md",
                "Semanticus.VSCode/assistant-pack/plugins/semanticus/skills/semanticus/SKILL.md",
                "Semanticus.VSCode/assistant-pack/plugins/semanticus/skills/semanticus-ai-ready/SKILL.md", "Semanticus.Engine/Program.cs" })
                Assert.Contains("table nothing uses", System.IO.File.ReadAllText(System.IO.Path.Combine(root, p.Replace('/', System.IO.Path.DirectorySeparatorChar))), StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_row_the_assistant_declines_reads_Found_by_Semanticus_with_the_assistants_reason_and_never_says_the_person_gave_it()
        {
            using var f = await WithLookupsAsync();
            var plan = await f.Engine.GetModelPlanAsync();
            var row = plan.Items.First(i => i.Who == ModelPlanOwners.Assistant && !PlanTasks.IsTaskId(i.Id) && PlanStateWords.IsOpen(i.StateWord));
            await f.Engine.SetPlanItemDoerAsync(row.Id, PlanDoers.Assistant, "agent");
            var list = await f.Engine.ListAssistantRequestsAsync();
            var q = list.Requests.Concat(list.Held ?? Array.Empty<Semanticus.Engine.Jev.AssistantRequest>()).First(x => x.Need == row.Title);
            var claim = await f.Engine.ClaimAssistantRequestAsync(q.RequestId);
            Assert.True(claim.Ok, claim.Message);
            const string reason = "Needs a date column: none exists in this model.";
            await f.Engine.DeclineAssistantRequestAsync(q.RequestId, claim.Request.ClaimToken, reason);
            var task = (await f.Engine.GetModelPlanAsync()).Items.Single(i => PlanTasks.IsTaskId(i.Id) && i.Title == row.Title);
            _out.WriteLine(task.Source + " | " + string.Join(" / ", task.Why) + " | " + task.StateNote + " | " + task.Sub);
            Assert.Equal(PlanSources.Found, task.Source);
            Assert.Equal("Your assistant could not do this: " + reason, task.Why[0]);
            Assert.DoesNotContain(task.Why, w => w.Contains("You gave", StringComparison.Ordinal) || w.Contains("You added", StringComparison.Ordinal));
            Assert.Equal(reason, task.StateNote);
        }

        [Fact]
        public async Task A_row_the_person_gives_says_so_and_a_row_the_assistant_took_does_not()
        {
            using var f = await WithLookupsAsync();
            var rows = (await f.Engine.GetModelPlanAsync()).Items
                .Where(i => i.Who == ModelPlanOwners.Assistant && !PlanTasks.IsTaskId(i.Id) && PlanStateWords.IsOpen(i.StateWord)).Take(2).ToArray();
            Assert.Equal(2, rows.Length);
            await f.Engine.SetPlanItemDoerAsync(rows[0].Id, PlanDoers.Assistant, "human");
            await f.Engine.SetPlanItemDoerAsync(rows[1].Id, PlanDoers.Assistant, "agent");
            var plan = await f.Engine.GetModelPlanAsync();
            var byYou = plan.Items.Single(i => PlanTasks.IsTaskId(i.Id) && i.Title == rows[0].Title);
            var byAssistant = plan.Items.Single(i => PlanTasks.IsTaskId(i.Id) && i.Title == rows[1].Title);
            Assert.Contains(byYou.Why, w => w.StartsWith("You gave this to your assistant", StringComparison.Ordinal));
            Assert.DoesNotContain(byAssistant.Why, w => w.Contains("You gave", StringComparison.Ordinal));
            Assert.Equal(PlanSources.Found, byYou.Source);
            Assert.Equal(PlanSources.Found, byAssistant.Source);
        }

        [Fact]
        public async Task The_title_of_a_Fix_task_counts_the_findings_that_still_stand_not_the_count_when_it_was_given()
        {
            using var f = await WithLookupsAsync();
            var plan = await f.Engine.GetModelPlanAsync();
            var row = plan.Items.FirstOrDefault(i => i.Id == LocalEngine.CoverageBpaItemId);
            if (row == null) { _out.WriteLine("no best-practice coverage row on this fixture"); return; }
            var given = Int32.Parse(row.Title.Split(' ')[1].Replace(",", ""));
            await f.Engine.SetPlanItemDoerAsync(row.Id, PlanDoers.Assistant, "agent");
            var scan = await f.Engine.BpaScanAsync();
            var items = scan.Violations.Where(v => v.CanAutoFix).Select(v => new FindingFixItem { System = "bpa", RuleId = v.RuleId, ObjRef = v.ObjectRef }).ToArray();
            if (items.Length > 0) await f.Engine.ApplyFixesAsync(items, "agent");
            var task = (await f.Engine.GetModelPlanAsync()).Items.Where(i => PlanTasks.IsTaskId(i.Id) && i.Claims != null && i.Claims.Length > 0 && i.OriginLink?.Ref == LocalEngine.CoverageBpaItemId).ToArray();
            foreach (var t in task) _out.WriteLine(t.Title + " (given as " + row.Title + ")");
            if (task.Length == 0) return;      // every claim gone: the task is done and hidden
            var now = Int32.Parse(task[0].Title.Split(' ')[1].Replace(",", ""));
            Assert.True(now < given, $"the title still says {task[0].Title} though the assistant fixed findings");
        }

        [Fact]
        public async Task Refresh_plan_with_no_change_leaves_Plan_done_and_the_total_as_they_were()
        {
            using var f = await WithLookupsAsync();
            var inv = new PlanInvariant(f.Engine, _out);
            var plan = await f.Engine.GetModelPlanAsync();
            await f.Engine.ApplyPlanItemsAsync(null, "agent", plan.ReadyDigest);
            var before = await f.Engine.GetModelPlanAsync();
            var refreshed = await f.Engine.RefreshPlanAsync();
            var after = await f.Engine.GetModelPlanAsync();
            _out.WriteLine($"before {before.Strip.TasksDone}/{before.Strip.TasksAll}, after {after.Strip.TasksDone}/{after.Strip.TasksAll}; {refreshed.Sentence}");
            Assert.Equal(before.Strip.TasksDone, after.Strip.TasksDone);
            Assert.Equal(before.Strip.TasksAll, after.Strip.TasksAll);
            var again = await f.Engine.RefreshPlanAsync();
            var third = await f.Engine.GetModelPlanAsync();
            Assert.Equal(after.Strip.TasksDone, third.Strip.TasksDone);
            Assert.Equal(after.Strip.TasksAll, third.Strip.TasksAll);
            await inv.CheckAsync("after refresh", cleared: refreshed.Cleared, findings: false);
        }

        [Fact]
        public async Task Applied_tasks_that_left_the_plan_count_in_Plan_done_before_Refresh_plan_and_Refresh_changes_neither_figure()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("Finish3Vanished.bim");
            await f.Engine.GetModelPlanAsync();
            var ids = Enumerable.Range(0, 8).Select(n => "jev.columns.table:Completed" + n).ToArray();
            using (ChangeCause.Begin(ChangeCauseKinds.PlanItem, "Completed tasks", "you", ids))
                await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "Completed", "human");
            f.Engine.SeedPlanMemoryForTest(state =>
            {
                foreach (var id in ids)
                {
                    state.Changes[id] = new PlanChangeHeader { State = ModelPlanChangeStates.Applied };
                    state.History.Add(new ModelPlanHistoryEntry { ItemId = id, Title = id, Kind = ModelPlanHistoryKinds.Approved });
                }
            });
            var before = (await f.Engine.GetModelPlanAsync()).Strip;
            var refreshed = await f.Engine.RefreshPlanAsync();
            var after = (await f.Engine.GetModelPlanAsync()).Strip;
            _out.WriteLine($"before {before.TasksDone}/{before.TasksAll}, after {after.TasksDone}/{after.TasksAll}, cleared {refreshed.Cleared}");
            Assert.Equal(8, refreshed.Cleared);
            Assert.Equal(before.TasksDone, after.TasksDone);
            Assert.Equal(before.TasksAll, after.TasksAll);
        }
    }
}
