using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    /// <summary>
    /// Hero run 20, J2 (13 Oct 2026): "get this model as good as it can be" ended AI readiness A 96 but Best practice F 38, because the assistant
    /// HID 210 unused generic columns (each a new "Remove unnecessary columns" finding), never read Best practice again, and left four rows
    /// with no reason on them. A scripted assistant that follows the new skill text (remove what lineage shows unused, do the tool rows, read both
    /// grades, give every row it leaves a reason on the row) must end with both grades A or B, or every leftover row must say why.
    /// </summary>
    public sealed class DriveFinishTests
    {
        private readonly ITestOutputHelper _out;
        public DriveFinishTests(ITestOutputHelper output) { _out = output; }

        private static string[] Generic => new[] { "Row ID", "Batch Key", "Source Code", "Load Id", "Etl Flag", "Hash Key" };

        /// <summary>The Wide fixture plus unused generic columns, visible and hidden: the shape the hero fixture has 210 of.</summary>
        private static async Task<DecisionFixture> MessyAsync()
        {
            var f = await CommutationFixtures.WideAsync("drivefinish-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".bim");
            foreach (var t in new[] { "Sales", "Customer", "Product" })
                foreach (var g in Generic)
                {
                    await f.Engine.CreateColumnAsync("table:" + t, g, "String", null, "human");
                    await f.Engine.SetDescriptionAsync("column:" + t + "/" + g, "Described by the fixture.", "human");
                    await f.Engine.SetObjectPropertyAsync("column:" + t + "/" + g, "IsHidden", "true", "human");
                }
            return f;
        }

        private static async Task<(string Bpa, int BpaCount, string Air)> GradesAsync(LocalEngine e)
        {
            var b = await e.BpaScanAsync();
            var a = await e.AiReadinessScanAsync();
            return (b.BestPractice, b.ViolationCount, a.Grade);
        }

        /// <summary>Skill: <c>unused_objects</c>, then <c>remove_safe_objects</c> on the columns it marks safe. A visible attribute is hidden, not deleted.</summary>
        private static async Task RemoveUnusedAsync(LocalEngine e)
        {
            var unused = await e.UnusedObjectsAsync();
            var safe = unused.Items.Where(i => i.Verdict == "safe" && i.Kind != "measure").Select(i => i.Ref).ToArray();
            if (safe.Length > 0) await e.RemoveSafeObjectsAsync(safe, null, "agent");
            foreach (var v in unused.Items.Where(i => i.Verdict == "visibleAttribute")) await e.SetObjectPropertyAsync(v.Ref, "IsHidden", "true", "agent");
        }

        /// <summary>The assistant of the new skill text, as a script: run the plan, work the queue (tool rows are done, a row it cannot do is declined with its reason),
        /// refresh and repeat, then read both grades. Returns the rows it left, with their notes.</summary>
        private async Task<(string[] Left, ModelPlanView Plan)> DriveAsync(LocalEngine e)
        {
            var plan = await e.GetModelPlanAsync();
            await e.ApplyPlanItemsAsync(null, "agent", plan.ReadyDigest);
            for (var round = 0; round < 6; round++)
            {
                // Skill steps 3 and 5: refresh, and take every Ready row a tool can do that is not on the queue yet.
                await e.RefreshPlanAsync();
                var rows = (await e.GetModelPlanAsync()).Items
                    .Where(i => i.Who == ModelPlanOwners.Assistant && !PlanTasks.IsTaskId(i.Id) && i.StateWord == PlanStateWords.Ready).ToArray();
                foreach (var r in rows) await e.SetPlanItemDoerAsync(r.Id, PlanDoers.Assistant, "agent");
                var list = await e.ListAssistantRequestsAsync();
                if (list.Requests.Length == 0 && rows.Length == 0) break;
                // Skill: remove unused columns before writing words, so no words go on a column that is about to go.
                foreach (var q in list.Requests.OrderBy(x => x.Need.Contains("unused column", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                {
                    var claim = await e.ClaimAssistantRequestAsync(q.RequestId);
                    if (!claim.Ok) continue;
                    var token = claim.Request.ClaimToken;
                    var done = false;
                    if (q.Need.Contains("unused column", StringComparison.OrdinalIgnoreCase))
                    {
                        // Skill: unused_objects, then remove_safe_objects (it re-checks every column).
                        await RemoveUnusedAsync(e);
                        done = true;
                    }
                    else if (q.Need.Contains("best-practice", StringComparison.OrdinalIgnoreCase))
                    {
                        var scan = await e.BpaScanAsync();
                        var items = scan.Violations.Where(v => v.CanAutoFix).Select(v => new FindingFixItem { System = "bpa", RuleId = v.RuleId, ObjRef = v.ObjectRef }).ToArray();
                        if (items.Length > 0) await e.ApplyFixesAsync(items, "agent");
                        done = true;
                    }
                    else if (q.Need.Contains("compatibility level", StringComparison.OrdinalIgnoreCase))
                    {
                        await e.SetCompatibilityLevelAsync(1701, "agent");
                        done = true;
                    }
                    if (done) await e.CompleteAssistantRequestAsync(q.RequestId, token, "Done.");
                    else await e.DeclineAssistantRequestAsync(q.RequestId, token, "Needs a person: " + q.Need + ". No tool can find what it needs in this model.");
                }
            }
            // Skill: a row still Waiting behind a stuck row is not waited on. The tools work without the Plan: do the work, then leave a reason on every row still open.
            foreach (var r in (await e.GetModelPlanAsync()).Items.Where(i => i.Who == ModelPlanOwners.Assistant && (i.StateWord == PlanStateWords.Waiting || i.StateWord == PlanStateWords.Ready)).ToArray())
            {
                if (r.Title.Contains("unused column", StringComparison.OrdinalIgnoreCase))
                {
                    await RemoveUnusedAsync(e);
                    // The removal may finish the row; a row still on the plan says why it keeps what it keeps.
                    if (!(await e.GetModelPlanAsync()).Items.Any(i => i.Id == r.Id)) continue;
                    await e.SetPlanTaskNoteAsync(r.Id, "Kept: lineage shows something still uses these columns, or they are visible attributes people query by.", "agent");
                }
                else await e.SetPlanTaskNoteAsync(r.Id, "Needs words first: the row ahead of this one is stuck, and no tool can finish it in this model.", "agent");
            }
            var after = await e.GetModelPlanAsync();
            var left = after.Items.Where(i => i.Who == ModelPlanOwners.Assistant && (i.StateWord == PlanStateWords.Ready || i.StateWord == PlanStateWords.Waiting))
                .Select(i => i.Title + " | " + i.StateWord + " | " + i.StateNote).ToArray();
            return (left, after);
        }

        [Fact]
        public async Task A_scripted_assistant_following_the_skill_ends_with_both_grades_A_or_B_or_a_reason_on_every_row()
        {
            using var f = await MessyAsync();
            var before = await GradesAsync(f.Engine);
            _out.WriteLine($"BEFORE  AI {before.Air}  BP {before.Bpa} ({before.BpaCount} issues)");
            var (left, plan) = await DriveAsync(f.Engine);
            var after = await GradesAsync(f.Engine);
            _out.WriteLine($"AFTER   AI {after.Air}  BP {after.Bpa} ({after.BpaCount} issues)");
            foreach (var l in left) _out.WriteLine("LEFT  " + l);
            foreach (var i in plan.Items) _out.WriteLine($"ROW {i.Who} {i.StateWord} {i.Title} :: {i.StateNote} :: waits={string.Join(",", (i.WaitsFor ?? Array.Empty<PlanWaitRef>()).Select(w => w.Title + "/" + w.Doer))}");
            foreach (var v in (await f.Engine.BpaScanAsync()).Violations) _out.WriteLine($"BPA {v.RuleId} {v.ObjectRef} fix={v.CanAutoFix}");

            // No tool row stays Ready: the run did them.
            Assert.DoesNotContain(plan.Items, i => i.StateWord == PlanStateWords.Ready && i.Who == ModelPlanOwners.Assistant
                && !(i.StateNote ?? "").StartsWith("Kept:") && (i.Title.Contains("unused column", StringComparison.OrdinalIgnoreCase) || i.Title.Contains("best-practice", StringComparison.OrdinalIgnoreCase)));
            // Hiding is not what happened to the unused generic columns: they are gone.
            var survivors = await f.Sessions.Current.ReadAsync(m => m.AllColumns.Where(c => Generic.Contains(c.Name)).Select(c => c.Table.Name + "[" + c.Name + "]").ToArray());
            Assert.Empty(survivors);
            static bool Good(string g) => g == "A" || g == "B";
            if (!(Good(after.Bpa) && Good(after.Air)))
                Assert.All(left, l => Assert.True(l.Split('|')[2].Trim().StartsWith("Kept:") || l.Split('|')[2].Trim().StartsWith("Needs"), "a row is left with no reason of its own: " + l));
        }

        [Fact]
        public async Task A_row_the_assistant_cannot_do_carries_its_reason_in_the_row_state_note()
        {
            using var f = await MessyAsync();
            var plan = await f.Engine.GetModelPlanAsync();
            var row = plan.Items.First(i => i.Who == ModelPlanOwners.Assistant && !PlanTasks.IsTaskId(i.Id) && PlanStateWords.IsOpen(i.StateWord));
            await f.Engine.SetPlanItemDoerAsync(row.Id, PlanDoers.Assistant, "agent");
            var list = await f.Engine.ListAssistantRequestsAsync();
            var q = list.Requests.Concat(list.Held ?? Array.Empty<Semanticus.Engine.Jev.AssistantRequest>()).First(x => x.Need == row.Title);
            var claim = await f.Engine.ClaimAssistantRequestAsync(q.RequestId);
            Assert.True(claim.Ok, claim.Message);
            const string reason = "Needs a date column: none exists in this model.";
            await f.Engine.DeclineAssistantRequestAsync(q.RequestId, claim.Request.ClaimToken, reason);
            var after = await f.Engine.GetModelPlanAsync();
            var shown = after.Items.Where(i => i.StateNote == reason).ToArray();
            Assert.True(shown.Length > 0, "no row shows the reason: " + string.Join(" || ", after.Items.Select(i => i.Title + " -> " + i.StateNote)));
        }

        [Fact]
        public async Task A_note_the_assistant_leaves_on_an_open_assistant_row_leads_its_state_note_and_clearing_it_takes_it_away()
        {
            using var f = await MessyAsync();
            var plan = await f.Engine.GetModelPlanAsync();
            var row = plan.Items.First(i => i.Who == ModelPlanOwners.Assistant && !PlanTasks.IsTaskId(i.Id) && PlanStateWords.IsOpen(i.StateWord)
                && (i.StateWord == PlanStateWords.Ready || i.StateWord == PlanStateWords.Waiting));
            const string why = "Needs a date column: none exists in this model.";
            var withNote = await f.Engine.SetPlanTaskNoteAsync(row.Id, why, "agent");
            Assert.StartsWith(why, withNote.Items.Single(i => i.Id == row.Id).StateNote);
            var again = await f.Engine.GetModelPlanAsync();
            Assert.StartsWith(why, again.Items.Single(i => i.Id == row.Id).StateNote);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(again.Items.Single(i => i.Id == row.Id).StateNote, "Needs a date column").Count);
            // The person's own note on the same row is a note and nothing more: it does not become the state note.
            var personal = await f.Engine.SetPlanTaskNoteAsync(row.Id, "Only the visible columns.");
            Assert.DoesNotContain("Only the visible columns", personal.Items.Single(i => i.Id == row.Id).StateNote ?? "");
            await f.Engine.SetPlanTaskNoteAsync(row.Id, why, "agent");
            var cleared = await f.Engine.SetPlanTaskNoteAsync(row.Id, "", "agent");
            Assert.DoesNotContain("Needs a date column", cleared.Items.Single(i => i.Id == row.Id).StateNote ?? "");
        }

        [Fact]
        public void The_row_briefs_remove_unused_generic_columns_and_carry_the_finishing_rule()
        {
            foreach (var id in new[] { "cleanup/model", "proposal/collisions", "proposal/visibility" })
                {
                    var choice = AssistantDefaults.For(id).Choice;
                    Assert.Contains("remove_safe_objects", choice);
                    Assert.DoesNotContain("delete_objects", choice);
                    Assert.DoesNotContain("get_lineage", choice);
                }
            Assert.Contains("REMOVE", AssistantDefaults.For("cleanup/model").Choice);
            Assert.DoesNotContain("hide each one", AssistantDefaults.For("proposal/visibility").Choice);
            var row = new ModelPlanItem { Id = "cleanup/model", Who = ModelPlanOwners.Assistant, Why = new[] { "x" } };
            AssistantDefaults.Apply(new[] { row });
            Assert.Contains(AssistantDefaults.FinishLead, row.Brief);
            Assert.Contains("Best practice", row.Brief);
            Assert.Contains("never leave it Ready", row.Brief);
        }

        [Fact]
        public void The_skill_and_the_server_instructions_state_the_finishing_rule_and_never_say_tool_names_to_the_person()
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, ".claude", "skills", "semanticus", "SKILL.md"))) root = Path.GetDirectoryName(root);
            Assert.NotNull(root);
            var skill = File.ReadAllText(Path.Combine(root, ".claude", "skills", "semanticus", "SKILL.md"));
            Assert.Contains("BOTH grades", skill);
            Assert.Contains("REMOVE", skill);
            Assert.Contains("Never say a tool name to the person", skill);
            Assert.Contains("decline_request", skill);
            var program = File.ReadAllText(Path.Combine(root, "Semanticus.Engine", "Program.cs"));
            Assert.Contains("BOTH grades", program);
            Assert.Contains("Never say a tool name to the person", program);
        }
    }
}
