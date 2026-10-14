// The living plan (Jev M2).
//
// What these tests are really holding, in one line each:
//   the plan is a VIEW of the model, so two reads of an unchanged model give the same ids in the same order
//   a skip is the only thing remembered, and it marks an item rather than removing it
//   an item whose gap is gone is reported done, once, and keeps saying so
//   with no service the plan is COMPLETE, in rule order, with no numbers, and it says which it is
//   exactly one action writes in this release, and everything else says plainly that it cannot yet
//   the orientation's suggestions and the plan's top three are the same list
//
// The fixture is DecisionFixture, the same one the M0a decision tests use, because a plan needs the same
// three things: a scratch home, a scratch workspace and a saved model with a durable identity (a skip is
// kept beside the model, so a model with nowhere to keep one cannot be skipped on).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ModelPlanTests : IClassFixture<ModelTemplates>
    {
        // Lean suite (10 Oct): the gappy model is built once per class (ModelTemplates) and each test opens its own copy.
        private readonly ModelTemplates _templates;
        public ModelPlanTests(ModelTemplates templates) => _templates = templates;

        /// <summary>A model with real gaps: two undescribed measures, an unhidden foreign key (a safe fix),
        /// and a measure nothing uses. Small on purpose, so a failure names one thing.</summary>
        private async Task<DecisionFixture> GappyModelAsync(string file = "Plan.bim", bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await _templates.OpenCopyAsync(f, "gappy", BuildGappyModelAsync, file);
            f.UseNull();   // the default path: no service, so every test below is the no-key path unless it says otherwise
            return f;
        }

        private static async Task BuildGappyModelAsync(DecisionFixture f)
        {
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            // A geographic column with no data category: a CAT-GEO safe fix, which sits in the FORMATTING
            // category. The model therefore has safe fixes in two categories, which is what the plan needs to
            // have two safe-fix rows and what the scoping test needs to tell them apart.
            await f.Engine.CreateColumnAsync("table:Customer", "City", "String", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Old Total", "SUM ( Sales[Amount] )", "human");
        }

        // ---- the plan is a view of the model ---------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_first_plan_names_its_gaps_and_says_it_was_drawn_for_the_first_time()
        {
            using var f = await GappyModelAsync("PlanFirst.bim");

            var plan = await f.Engine.GetModelPlanAsync();

            Assert.Equal("Contoso", plan.ModelName);
            Assert.NotEmpty(plan.Items);
            Assert.Contains("first time", plan.RefreshedWhy, StringComparison.OrdinalIgnoreCase);
            Assert.All(plan.Items, i =>
            {
                Assert.False(string.IsNullOrWhiteSpace(i.Id));
                Assert.False(string.IsNullOrWhiteSpace(i.Title));
                Assert.Contains(i.Section, ModelPlanSections.InOrder);
                Assert.DoesNotContain("—", i.Title, StringComparison.Ordinal);
            });
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_open_item_carries_a_reason_a_place_to_go_and_an_owner()
        {
            using var f = await GappyModelAsync("PlanShape.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            var open = plan.Items.Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed).ToArray();
            Assert.NotEmpty(open);

            foreach (var item in open)
            {
                Assert.NotEmpty(item.Why);
                Assert.All(item.Why, w => Assert.False(string.IsNullOrWhiteSpace(w)));
                Assert.NotNull(item.Jump);
                Assert.NotNull(AskRouteCatalog.Find(item.Jump!.Tool));   // a jump must name a real place
                Assert.Contains(item.Who, new[] { ModelPlanOwners.Auto, ModelPlanOwners.You, ModelPlanOwners.Assistant });
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Two_reads_of_an_unchanged_model_give_the_same_items_in_the_same_order()
        {
            using var f = await GappyModelAsync("PlanStable.bim");

            var first = await f.Engine.GetModelPlanAsync();
            var again = await f.Engine.GetModelPlanAsync();

            Assert.Equal(first.Items.Select(i => i.Id), again.Items.Select(i => i.Id));
            // Nothing moved, and the plan says so rather than inventing news.
            Assert.Equal("Nothing on the plan changed.", again.RefreshedWhy);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_gaps_are_not_stored_so_a_thrown_away_memory_file_changes_nothing_but_the_history()
        {
            using var f = await GappyModelAsync("PlanNotStored.bim");
            var before = await f.Engine.GetModelPlanAsync();

            foreach (var file in Directory.GetFiles(f.Workspace, PlanMemory.FileName, SearchOption.AllDirectories))
                File.Delete(file);

            var after = await f.Engine.GetModelPlanAsync();

            Assert.Equal(
                before.Items.Where(i => i.Status != ModelPlanStatuses.Done).Select(i => i.Id),
                after.Items.Where(i => i.Status != ModelPlanStatuses.Done).Select(i => i.Id));
        }

        // ---- skipping ---------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Skipping_keeps_the_item_on_the_plan_and_shows_the_reason()
        {
            using var f = await GappyModelAsync("PlanSkip.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var target = plan.Items.First(i => i.Status == ModelPlanStatuses.Ready);

            var after = await f.Engine.SkipModelPlanItemAsync(target.Id, "not until after the quarter closes", "human");

            var skipped = after.Items.Single(i => i.Id == target.Id);
            Assert.Equal(ModelPlanStatuses.Skipped, skipped.Status);
            Assert.Equal("not until after the quarter closes", skipped.SkipReason);
            Assert.NotNull(skipped.SkippedUtc);

            // It survives a refresh, because it is the one thing the plan remembers.
            var reread = await f.Engine.GetModelPlanAsync();
            Assert.Equal(ModelPlanStatuses.Skipped, reread.Items.Single(i => i.Id == target.Id).Status);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_skip_with_no_reason_is_refused()
        {
            using var f = await GappyModelAsync("PlanSkipNoReason.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var target = plan.Items.First(i => i.Status == ModelPlanStatuses.Ready);

            await Assert.ThrowsAsync<ArgumentException>(
                () => f.Engine.SkipModelPlanItemAsync(target.Id, "   ", "human"));
            await Assert.ThrowsAsync<ArgumentException>(
                () => f.Engine.SkipModelPlanItemAsync(target.Id, null, "human"));

            Assert.Equal(ModelPlanStatuses.Ready, (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == target.Id).Status);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Putting_a_skipped_item_back_makes_it_ordinary_work_again()
        {
            using var f = await GappyModelAsync("PlanUnskip.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var target = plan.Items.First(i => i.Status == ModelPlanStatuses.Ready);
            await f.Engine.SkipModelPlanItemAsync(target.Id, "not now", "human");

            var after = await f.Engine.UnskipModelPlanItemAsync(target.Id, "human");

            var back = after.Items.Single(i => i.Id == target.Id);
            Assert.NotEqual(ModelPlanStatuses.Skipped, back.Status);
            Assert.Null(back.SkipReason);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_skipped_item_sinks_below_the_open_ones_but_stays_on_the_plan()
        {
            using var f = await GappyModelAsync("PlanSkipOrder.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.True(plan.Items.Count(i => i.Status == ModelPlanStatuses.Ready) >= 2,
                "this test needs at least two open items to have an order to check");
            var target = plan.Items.First(i => i.Status == ModelPlanStatuses.Ready);

            var after = await f.Engine.SkipModelPlanItemAsync(target.Id, "later", "human");

            var open = after.Items.TakeWhile(i => i.Status != ModelPlanStatuses.Skipped).ToArray();
            Assert.DoesNotContain(open, i => i.Id == target.Id);
            Assert.Contains(after.Items, i => i.Id == target.Id);
        }

        // ---- done --------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_item_whose_gap_is_gone_is_reported_done()
        {
            using var f = await GappyModelAsync("PlanDone.bim");
            var before = await f.Engine.GetModelPlanAsync();
            var safeFix = before.Items.FirstOrDefault(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal));
            Assert.True(safeFix != null, "this model was built to have a safe fix on it");

            await f.Engine.ApplySafeFixesAsync("human");
            var after = await f.Engine.GetModelPlanAsync();

            var done = after.Items.Single(i => i.Id == safeFix!.Id);
            Assert.Equal(ModelPlanStatuses.Done, done.Status);
            Assert.Equal(safeFix!.Title, done.Title);      // the words the person read, not a reconstruction
            Assert.Contains("done", after.RefreshedWhy, StringComparison.OrdinalIgnoreCase);

            // And it keeps saying so, because a gap that is gone leaves nothing to recompute from.
            Assert.Equal(ModelPlanStatuses.Done, (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == safeFix.Id).Status);
        }

        /// <summary>UAT-R2-S10b-004: the assistant's row left the plan because ANOTHER row's change closed its gap, and the
        /// Plan said "Done by your assistant" for work the assistant never did. A row nobody's own change closed says so.</summary>
        [Fact]
        public async Task An_assistants_row_whose_gap_something_else_closed_says_no_longer_needed_and_never_claims_the_work()
        {
            using var f = await GappyModelAsync("PlanNoLongerNeeded.bim");
            await f.Engine.GetModelPlanAsync();
            Assert.True(f.Engine.SeedPlanMemoryForTest(state => state.LastItems.Add(new PlanSeenItem
            {
                ItemId = "words/ghost",
                Title = "Add 4 explicit measures",
                Section = ModelPlanSections.Trust,
                Doer = PlanDoers.Assistant,
            })));

            var row = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "words/ghost");

            Assert.Equal(ModelPlanStatuses.Done, row.Status);
            Assert.Equal(LocalEngine.NoLongerNeededNote, row.StateNote);
            Assert.Contains(row.Why, w => w.Contains("closed this", StringComparison.Ordinal));
            // It keeps saying so on the next read: the record carries the words.
            Assert.Equal(LocalEngine.NoLongerNeededNote, (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "words/ghost").StateNote);
        }

        /// <summary>The other half of UAT-R2-S10b-004: when the assistant's OWN approval does the row (the door it uses,
        /// model_plan_action with origin agent), the edit carries the row id, so the row keeps its owner's credit and
        /// never says "No longer needed".</summary>
        [Fact]
        public async Task An_assistants_row_the_assistant_did_itself_keeps_its_credit()
        {
            using var f = await GappyModelAsync("PlanOwnWork.bim");
            var row = (await f.Engine.GetModelPlanAsync()).Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal));
            Assert.True(f.Engine.SeedPlanMemoryForTest(state => state.Residue[row.Id] = new PlanRowResidue { Doer = PlanDoers.Assistant }));
            Assert.Equal(PlanDoers.Assistant, (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == row.Id).Doer);   // seen open, as the assistant's

            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "agent");
            var digest = proposed.Plan.Items.Single(i => i.Id == row.Id).ChangeSet.Digest;
            Assert.True((await f.Engine.ModelPlanActionAsync(row.Id, "approve", "agent", digest)).Performed);

            var done = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == row.Id);
            Assert.Equal(ModelPlanStatuses.Done, done.Status);
            Assert.Equal(PlanDoers.Assistant, done.Doer);
            Assert.NotEqual(LocalEngine.NoLongerNeededNote, done.StateNote);
        }

        /// <summary>The usual way an assistant does a row is with plain edit tools. An edit from the agent door on the history since the
        /// row was last seen open keeps the row's credit; the same edit from a person does not.</summary>
        [Theory, InlineData("agent", false), InlineData("human", true)]
        public async Task An_assistants_row_closed_by_plain_edits_keeps_its_credit_only_when_the_agent_made_them(string origin, bool noLongerNeeded)
        {
            using var f = await GappyModelAsync("PlanPlainEdit" + origin + ".bim");
            var row = (await f.Engine.GetModelPlanAsync()).Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal));
            Assert.True(f.Engine.SeedPlanMemoryForTest(state => state.Residue[row.Id] = new PlanRowResidue { Doer = PlanDoers.Assistant }));
            Assert.Equal(PlanDoers.Assistant, (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == row.Id).Doer);   // seen open, as the assistant's

            await f.Engine.ApplySafeFixesAsync(origin);   // no row id on the edit: a plain edit from that door

            var done = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == row.Id);
            Assert.Equal(ModelPlanStatuses.Done, done.Status);
            Assert.Equal(noLongerNeeded, done.StateNote == LocalEngine.NoLongerNeededNote);
        }

        /// <summary>A row given to the assistant is carried by its task, so closing it never says "No longer needed" (the task is done once).</summary>
        [Fact]
        public async Task An_assistants_row_closed_under_its_task_keeps_its_credit()
        {
            using var f = await GappyModelAsync("PlanClaimed.bim");
            var row = (await f.Engine.GetModelPlanAsync()).Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal));
            var given = await f.Engine.SetPlanItemDoerAsync(row.Id, PlanDoers.Assistant);
            var task = given.Items.Single(i => PlanTasks.IsTaskId(i.Id) && i.Title == row.Title);

            // While the assistant holds the task the door stamps its id on every edit (McpHealthAppender); the person's door here,
            // so only the task id can say whose work it was.
            using (ChangeCause.Begin(ChangeCauseKinds.Task, task.Title, null, task.Id))
                await f.Engine.ApplySafeFixesAsync("human");

            var after = await f.Engine.GetModelPlanAsync();
            // A given row is carried by its task: one row, the task, so the row never reaches the "closed by something else" rule.
            Assert.DoesNotContain(after.Items, i => i.Id == row.Id);
            Assert.NotEqual(LocalEngine.NoLongerNeededNote, after.Items.Single(i => i.Id == task.Id).StateNote);
        }

        // ---- the no-service path -----------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task With_no_service_the_plan_is_complete_in_rule_order_with_no_numbers_and_says_so()
        {
            using var f = await GappyModelAsync("PlanNullOracle.bim");

            var plan = await f.Engine.GetModelPlanAsync();

            Assert.False(plan.Ranked);
            Assert.NotEmpty(plan.Items);
            Assert.All(plan.Items, i => Assert.Equal(0, i.Confidence));   // zero means no number is shown at all
            Assert.False(string.IsNullOrWhiteSpace(plan.OrderedBy));
            Assert.Contains("order Semanticus found", plan.OrderedBy, StringComparison.Ordinal);
            Assert.DoesNotContain("—", plan.OrderedBy, StringComparison.Ordinal);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_free_session_gets_the_whole_plan_and_is_told_the_ordering_is_Pro()
        {
            using var f = await GappyModelAsync("PlanFree.bim", pro: false);
            f.UseEmptyRecorded();
            await f.ConsentAsync();

            var plan = await f.Engine.GetModelPlanAsync();

            Assert.NotEmpty(plan.Items);
            Assert.False(plan.Ranked);
            Assert.Contains("Pro", plan.OrderedBy, StringComparison.Ordinal);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_same_items_come_back_whether_the_order_was_bought_or_not()
        {
            using var f = await GappyModelAsync("PlanSameItems.bim");
            var unranked = await f.Engine.GetModelPlanAsync();

            using var g = await GappyModelAsync("PlanSameItems2.bim", pro: false);
            var free = await g.Engine.GetModelPlanAsync();

            Assert.Equal(
                unranked.Items.Select(i => i.Id).OrderBy(x => x, StringComparer.Ordinal),
                free.Items.Select(i => i.Id).OrderBy(x => x, StringComparer.Ordinal));
        }

        // ---- worth -------------------------------------------------------------------------------------

        [Fact]
        public async Task What_finishing_is_worth_reports_the_grade_now_and_the_grade_the_safe_fixes_reach()
        {
            using var f = await GappyModelAsync("PlanWorth.bim");

            var plan = await f.Engine.GetModelPlanAsync();

            Assert.NotNull(plan.Worth);
            Assert.False(string.IsNullOrWhiteSpace(plan.Worth.From));

            // The rehearsal is a second readiness scan, so it runs behind the first look rather than in front
            // of it. Wait for it (ModelPlanCacheTests holds the honesty of the pending state itself), then
            // check the number: it is rehearsed, so it must equal the grade apply_safe_fixes really produces.
            // If it does not, the tile is lying to the person about whether the work is worth doing.
            await f.Engine.PlanProjectionForTest;
            var projected = (await f.Engine.GetModelPlanAsync()).Worth.To;
            var applied = await f.Engine.ApplySafeFixesAsync("human");
            Assert.Equal(applied.Scorecard.Grade, projected);
        }

        /// <summary>The Overview draws "B 82 now, A 93 after the safe fixes" with a bar between the two numbers,
        /// so the worth has to carry the NUMBERS as well as the letters. Both come from a real readiness scan:
        /// the one now, and the rehearsed one. The page never works either out. While the rehearsal is still
        /// running there is no second number, and it must not arrive as a zero or as a copy of the first.
        /// Both doors return the same view, so both are read here.</summary>
        [Fact]
        public async Task The_worth_carries_the_score_now_and_the_score_the_safe_fixes_reach_on_both_doors()
        {
            using var f = await GappyModelAsync("PlanWorthScore.bim");
            var now = await f.Engine.AiReadinessScanAsync();

            var first = await McpTools.GetModelPlan(f.Engine);
            Assert.Equal((int)Math.Round(now.Overall, MidpointRounding.AwayFromZero), first.Worth.FromScore);
            Assert.True(first.Worth.Pending);
            Assert.Null(first.Worth.ToScore);   // pending is not a number, and not a zero

            await f.Engine.PlanProjectionForTest;
            var mcp = await McpTools.GetModelPlan(f.Engine);
            var rpc = await new EngineRpcTarget(f.Engine).getModelPlan();
            Assert.NotNull(mcp.Worth.ToScore);
            Assert.Equal(mcp.Worth.ToScore, rpc.Worth.ToScore);
            Assert.Equal(mcp.Worth.FromScore, rpc.Worth.FromScore);

            // Rehearsed, so it must be the score the safe fixes really produce, exactly as the letter is.
            var applied = await f.Engine.ApplySafeFixesAsync("human");
            Assert.Equal((int)Math.Round(applied.Scorecard.Overall, MidpointRounding.AwayFromZero), mcp.Worth.ToScore);
            Assert.Equal(applied.Scorecard.Grade, mcp.Worth.To);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_rehearsal_leaves_the_model_exactly_as_it_found_it()
        {
            using var f = await GappyModelAsync("PlanRehearsal.bim");
            var before = await f.Engine.SessionInfoAsync();

            await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanProjectionForTest;

            var after = await f.Engine.SessionInfoAsync();
            Assert.Equal(before.Revision, after.Revision);
            Assert.Equal(before.HasUnsavedChanges, after.HasUnsavedChanges);
        }

        // ---- acting ------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_item_names_the_tables_it_is_about()
        {
            using var f = await GappyModelAsync("PlanTables.bim");

            var plan = await f.Engine.GetModelPlanAsync();

            // The visibility safe fix is about Sales[CustomerKey], so the row names Sales. Its TITLE is the
            // rule category and its touches line is a count, which is why the Map could not find the table by
            // reading either of them.
            var visibility = plan.Items.Single(i => i.Id == "safefix/Visibility");
            Assert.Contains("Sales", visibility.Tables.Select(t => t.Name));
            Assert.DoesNotContain("Sales", visibility.Title, StringComparison.Ordinal);

            // The formatting safe fix is about Customer[City].
            var formatting = plan.Items.Single(i => i.Id == "safefix/Formatting");
            Assert.Contains("Customer", formatting.Tables.Select(t => t.Name));

            // Every name is a real table in this model, sorted, with no repeats, and nothing is about a table
            // it has no business naming.
            var real = new HashSet<string>(plan.Tables.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var item in plan.Items)
            {
                Assert.NotNull(item.Tables);
                Assert.All(item.Tables, t => Assert.Contains(t.Name, real));
                AssertTableDetail(item);
            }

            // A row about nothing in particular says so with an empty list, never with a guessed name.
            var ship = plan.Items.FirstOrDefault(i => i.Id == "ship/unsaved");
            if (ship != null) Assert.Empty(ship.Tables);
        }

        [Fact]
        public async Task An_agent_save_gets_credit_for_the_unsaved_row()
        {
            using var f = await GappyModelAsync("SaidSave.bim");
            await f.Engine.SetDescriptionAsync("table:Sales", "Save this", "human");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "ship/unsaved");
            ChangeNotification saved = null;
            f.Sessions.Bus.Changed += n => { if (n.Label == "save") saved = n; };
            await f.Engine.SaveAsync(null, null, false, "agent");
            Assert.Equal("assistant", saved.By);
            var done = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "ship/unsaved" && i.Status == ModelPlanStatuses.Done);
            Assert.Equal("assistant", done.Doer);
        }

        [Fact]
        public async Task An_item_that_comes_back_after_an_undo_is_on_the_plan_once()
        {
            using var f = await GappyModelAsync("PlanUndoDone.bim");
            var before = await f.Engine.GetModelPlanAsync();
            var safeFix = before.Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal));

            await f.Engine.ModelPlanActionAsync(safeFix.Id, "start", "human");
            Assert.Equal(ModelPlanStatuses.Done,
                (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == safeFix.Id).Status);

            await f.Engine.UndoAsync("human");
            var back = await f.Engine.GetModelPlanAsync();

            // One row, not two. The gap is back, so the completion record went with it.
            Assert.Single(back.Items, i => i.Id == safeFix.Id);
            Assert.NotEqual(ModelPlanStatuses.Done, back.Items.Single(i => i.Id == safeFix.Id).Status);
            Assert.Equal(back.Items.Length, back.Items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public async Task An_item_whose_evidence_cannot_be_read_is_kept_and_never_reported_finished()
        {
            using var f = await GappyModelAsync("PlanUnproven.bim");
            await f.Engine.GetModelPlanAsync();

            // A check item was on the plan last time. Its family is derived from the report reading and the
            // saved checks, and this fixture has read no report, so this call cannot compute it at all. That
            // is not the same as the gap being closed, and the plan must not say it is.
            Assert.True(f.Engine.SeedPlanMemoryForTest(state => state.LastItems.Add(new PlanSeenItem
            {
                ItemId = "check/measure:Sales/Total Sales",
                Title = "Add a check for Sales/Total Sales",
                Section = ModelPlanSections.Trust,
            })));

            var plan = await f.Engine.GetModelPlanAsync();

            var row = plan.Items.Single(i => i.Id == "check/measure:Sales/Total Sales");
            Assert.NotEqual(ModelPlanStatuses.Done, row.Status);
            Assert.Contains("could not be checked", row.Sub, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(row.Why);
            Assert.NotNull(row.Jump);
            Assert.NotNull(AskRouteCatalog.Find(row.Jump!.Tool));
            Assert.Contains("could not be checked", plan.Note ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("done", plan.RefreshedWhy, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The plan remembers only its own rows, and what the memory file hands back is held to the same rule
        /// (L3 of the joint verifier's plan-waits check). A file written by a build that remembered an assistant's row or
        /// a Jev row still names them as seen; neither is on the plan now, and neither may be recorded as done: their
        /// changes were dropped or permission withdrawn, which is work nobody did.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_remembered_assistant_or_Jev_row_that_has_gone_is_never_recorded_as_done()
        {
            using var f = await GappyModelAsync("PlanForeignSeen.bim");
            await f.Engine.GetModelPlanAsync();
            Assert.True(f.Engine.SeedPlanMemoryForTest(state =>
            {
                state.LastItems.Add(new PlanSeenItem { ItemId = LocalEngine.AssistantItemPrefix + "rename", Title = "Rename 1 object", Section = ModelPlanSections.Trust });
                state.LastItems.Add(new PlanSeenItem { ItemId = "jev.column-format.table:Sales", Title = "Format 1 column in Sales", Section = ModelPlanSections.Trust });
            }));

            var plan = await f.Engine.GetModelPlanAsync();

            Assert.DoesNotContain(plan.Items, i => i.Status == ModelPlanStatuses.Done
                && (i.Title == "Rename 1 object" || i.Title == "Format 1 column in Sales"));
            var entry = plan.History.Last();
            Assert.DoesNotContain("done", entry.Sentence ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Empty(entry.Completed ?? Array.Empty<string>());
            Assert.DoesNotContain("done", plan.RefreshedWhy, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The first draw counts the plan's OWN rows (L6 of the joint verifier's plan-waits check): an assistant's
        /// row on the plan at the first read is told by the change plan, not counted in "drawn for the first time: N items."
        /// The count is the rows the plan wrote down, so the next read's comparison starts from the same number.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_first_draw_counts_the_plans_own_rows_only()
        {
            using var f = await GappyModelAsync("PlanFirstCount.bim", pro: false);
            await f.Engine.AddPlanItemAsync("column:Customer/Name", "rename", "Customer Name", null, null, null, "agent");

            var plan = await f.Engine.GetModelPlanAsync();

            var asst = plan.Items.Count(i => i.Id.StartsWith(LocalEngine.AssistantItemPrefix, StringComparison.Ordinal));
            Assert.True(asst > 0, "the premise: an assistant's row is on the plan at the first read");
            var own = plan.Items.Length - asst;
            var expected = "Semanticus made the plan from the model for the first time: " + own + (own == 1 ? " task." : " tasks.");
            Assert.Equal(expected, plan.RefreshedWhy);
            Assert.Equal(expected, plan.History.Single(h => h.Kind == ModelPlanHistoryKinds.Drawn).Sentence);
        }

        [Fact]
        public async Task Every_item_the_plan_calls_auto_is_one_the_engine_will_really_start()
        {
            using var f = await GappyModelAsync("PlanNoDeadStart.bim");

            // A chosen report that nobody has read yet, so the plan carries a "read the reports again" row.
            // Without one, every open row in this fixture is a safe fix and the test would pass for the
            // wrong reason: the rows that used to carry a dead Start would not be on the plan at all.
            var reportDir = Path.Combine(f.Workspace, "Reports", "Sales.Report", "definition");
            Directory.CreateDirectory(reportDir);
            File.WriteAllText(Path.Combine(reportDir, "report.json"), "{ }");
            await f.Engine.SetReportScopeAsync(
                new[] { new Semanticus.Engine.Lineage.ReportScopeChoice
                        { Kind = "local", Name = "Sales", Path = Path.Combine(f.Workspace, "Reports") } }, "human");

            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Contains(plan.Items, i => i.Id.StartsWith("recheck/", StringComparison.Ordinal));
            var auto = plan.Items
                .Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed)
                .Where(i => i.Who == ModelPlanOwners.Auto)
                .ToArray();
            Assert.NotEmpty(auto);   // if nothing is auto this test proves nothing

            // The engine runs exactly one family today, so anything else calling itself auto is a Start the
            // engine will refuse. Asserted structurally as well as behaviourally, because an item that has
            // ALREADY gone from the plan answers "nothing has that id", which would pass the loop for the
            // wrong reason.
            // Two since lane m11/plan-rows: the readiness safe fixes, and the best-practice row (start works it out and
            // approves it, the same two steps Run the plan takes).
            Assert.All(auto, i => Assert.True(i.Id.StartsWith("safefix/", StringComparison.Ordinal) || i.Id == LocalEngine.BpaFixItemId, i.Id));
            Assert.DoesNotContain(plan.Items,
                i => i.Id.StartsWith("check/", StringComparison.Ordinal) && i.Who == ModelPlanOwners.Auto);
            Assert.DoesNotContain(plan.Items,
                i => i.Id.StartsWith("recheck/", StringComparison.Ordinal) && i.Who == ModelPlanOwners.Auto);

            foreach (var item in auto)
            {
                var result = await f.Engine.ModelPlanActionAsync(item.Id, "start", "human");
                // "auto" is a promise that pressing start runs it. A refusal here is a dead button on the page.
                Assert.False(result.Sentence.Contains("Semanticus cannot do this task for you yet", StringComparison.Ordinal),
                    "the plan called " + item.Id + " auto and then refused to start it: " + result.Sentence);
                Assert.False(result.Sentence.Contains("your assistant writes it", StringComparison.Ordinal),
                    "the plan called " + item.Id + " auto and then said it was the assistant's: " + result.Sentence);
                Assert.False(result.Sentence.Contains("you have to approve", StringComparison.Ordinal),
                    "the plan called " + item.Id + " auto and then said it needed approval: " + result.Sentence);
                Assert.False(result.Sentence.Contains("on the Plan has that id", StringComparison.OrdinalIgnoreCase),
                    "the row vanished before it could be started, so this loop proved nothing about " + item.Id);
            }
        }

        [Fact]
        public async Task Two_models_in_one_folder_do_not_share_what_was_set_aside()
        {
            using var f = await GappyModelAsync("PlanShareA.bim");
            var first = await f.Engine.GetModelPlanAsync();
            var target = first.Items.First(i => i.Status == ModelPlanStatuses.Ready);
            await f.Engine.SkipModelPlanItemAsync(target.Id, "not in this model", "human");

            // A second model, saved into the SAME folder, so both resolve to one plan-memory.json.
            await f.Engine.CreateModelAsync("Contoso Two", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.SaveAsAsync("PlanShareB.bim");
            f.UseNull();

            var second = await f.Engine.GetModelPlanAsync();

            Assert.Equal(
                Path.GetDirectoryName(Path.Combine(f.Workspace, "PlanShareA.bim")),
                Path.GetDirectoryName(Path.Combine(f.Workspace, "PlanShareB.bim")));   // the premise: one folder
            Assert.NotEqual(ModelPlanStatuses.Skipped, second.Items.Single(i => i.Id == target.Id).Status);
            Assert.Contains("first time", second.RefreshedWhy, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(second.History, h => h.Text.Contains("not in this model", StringComparison.Ordinal));

            // And the first model still remembers its own decision, so this is separation and not amnesia.
            await f.Engine.OpenAsync(Path.Combine(f.Workspace, "PlanShareA.bim"));
            f.UseNull();
            var back = await f.Engine.GetModelPlanAsync();
            Assert.Equal(ModelPlanStatuses.Skipped, back.Items.Single(i => i.Id == target.Id).Status);
            Assert.Equal("not in this model", back.Items.Single(i => i.Id == target.Id).SkipReason);
        }

        [Fact]
        public async Task A_rehearsed_skip_writes_nothing_and_refuses_with_a_reason()
        {
            using var f = await GappyModelAsync("PlanDryRunSkip.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var target = plan.Items.First(i => i.Status == ModelPlanStatuses.Ready);
            var id = System.Text.Json.JsonSerializer.Serialize(target.Id);

            // A rehearsal rolls the MODEL back. The plan's memory is a sidecar written outside that batch, so
            // a rehearsed skip used to stick while the report said nothing had changed.
            var skip = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                f.Engine.DryRunOpAsync("skip_model_plan_item", "{\"id\":" + id + ",\"reason\":\"just rehearsing\"}"));
            Assert.Contains("skip_model_plan_item", skip.Message, StringComparison.Ordinal);

            Assert.Equal(ModelPlanStatuses.Ready,
                (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == target.Id).Status);

            var unskip = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                f.Engine.DryRunOpAsync("unskip_model_plan_item", "{\"id\":" + id + "}"));
            Assert.Contains("unskip_model_plan_item", unskip.Message, StringComparison.Ordinal);

            // Starting an item is denied for the same reason plus the one apply_safe_fixes already carries.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                f.Engine.DryRunOpAsync("model_plan_action", "{\"id\":" + id + "}"));
        }

        [Fact]
        public async Task Starting_one_safe_fix_row_fixes_that_row_only_and_leaves_a_row_set_aside_alone()
        {
            using var f = await GappyModelAsync("PlanActScope.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var visibility = plan.Items.Single(i => i.Id == "safefix/Visibility");
            var formatting = plan.Items.Single(i => i.Id == "safefix/Formatting");

            await f.Engine.SkipModelPlanItemAsync(formatting.Id, "we set the formatting by hand", "human");
            var result = await f.Engine.ModelPlanActionAsync(visibility.Id, "start", "human");

            Assert.True(result.Performed);
            Assert.Equal(ModelPlanStatuses.Done, result.Plan!.Items.Single(i => i.Id == visibility.Id).Status);

            // The row nobody started is still open work, still set aside, with the reason intact. Under the
            // old behaviour it was fixed too and came back reported as done.
            var untouched = result.Plan.Items.Single(i => i.Id == formatting.Id);
            Assert.Equal(ModelPlanStatuses.Skipped, untouched.Status);
            Assert.Equal("we set the formatting by hand", untouched.SkipReason);

            // And the model itself still has that gap, which is the fact behind the row.
            var card = await f.Engine.AiReadinessScanAsync();
            Assert.Contains(card.Findings, x => string.Equals(x.RuleId, "CAT-GEO", StringComparison.Ordinal) && !x.Waived);
            Assert.DoesNotContain(card.Findings, x => string.Equals(x.RuleId, "VIS-FK", StringComparison.Ordinal) && !x.Waived);
        }

        /// <summary>Astra finding 5 (P2, 22 September 2026). The plan is FREE TO READ and PRO TO RUN, and the
        /// Studio has always drawn it that way, but the engine did not enforce it: model_plan_action is a free
        /// operation on the map, and a free session could start a safe-fix row and repeat that for every
        /// runnable row. The gate cannot live in FeatureMap, because reading the plan, setting an item aside
        /// and putting it back must stay free on the same operation family, so it is a tier check on the one
        /// path that mutates.
        ///
        /// Proven by the model, not by the sentence: the revision must not move. A refusal that still wrote
        /// would pass a message assertion.</summary>
        [Fact]
        public async Task A_free_session_is_told_the_run_is_Pro_and_the_model_is_not_touched()
        {
            using var f = await GappyModelAsync("PlanActFree.bim", pro: false);
            var plan = await f.Engine.GetModelPlanAsync();
            var safeFix = plan.Items.Single(i => i.Id == "safefix/Visibility");
            var before = (await f.Engine.SessionInfoAsync()).Revision;

            var result = await f.Engine.ModelPlanActionAsync(safeFix.Id, "start", "human");

            Assert.False(result.Performed);
            Assert.Equal("Running the plan is part of Semanticus Pro. Open the page the task names to do it yourself.",
                result.Sentence);
            Assert.Equal(before, (await f.Engine.SessionInfoAsync()).Revision);   // nothing was written

            // The way out stays on the row: the refusal points at a page, it does not just say no.
            var row = result.Plan!.Items.Single(i => i.Id == safeFix.Id);
            Assert.NotEqual(ModelPlanStatuses.Done, row.Status);
            Assert.False(string.IsNullOrWhiteSpace(row.Jump?.Label));

            // And the gap the row is about is still there, which is the fact behind all of the above.
            var card = await f.Engine.AiReadinessScanAsync();
            Assert.Contains(card.Findings, x => string.Equals(x.RuleId, "VIS-FK", StringComparison.Ordinal) && !x.Waived);
        }

        /// <summary>The other half of the same line: everything about the plan that is not RUNNING it stays
        /// free. Read it, set a row aside, put it back.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_free_session_still_reads_the_plan_and_sets_a_row_aside_and_back()
        {
            using var f = await GappyModelAsync("PlanFreeReads.bim", pro: false);

            var plan = await f.Engine.GetModelPlanAsync();
            Assert.NotEmpty(plan.Items);

            var id = plan.Items.Single(i => i.Id == "safefix/Visibility").Id;
            var skipped = await f.Engine.SkipModelPlanItemAsync(id, "we hide keys by hand", "human");
            Assert.Equal(ModelPlanStatuses.Skipped, skipped.Items.Single(i => i.Id == id).Status);
            Assert.Equal("we hide keys by hand", skipped.Items.Single(i => i.Id == id).SkipReason);

            var back = await f.Engine.UnskipModelPlanItemAsync(id, "human");
            Assert.NotEqual(ModelPlanStatuses.Skipped, back.Items.Single(i => i.Id == id).Status);
        }

        /// <summary>And Pro still runs it, so the gate above is a tier boundary and not a removal.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_pro_session_still_runs_a_safe_fix_row()
        {
            using var f = await GappyModelAsync("PlanActPro.bim", pro: true);
            var plan = await f.Engine.GetModelPlanAsync();
            var safeFix = plan.Items.Single(i => i.Id == "safefix/Visibility");

            var result = await f.Engine.ModelPlanActionAsync(safeFix.Id, "start", "human");

            Assert.True(result.Performed);
            Assert.DoesNotContain("Semanticus Pro", result.Sentence, StringComparison.Ordinal);
            Assert.Equal(ModelPlanStatuses.Done, result.Plan!.Items.Single(i => i.Id == safeFix.Id).Status);
        }

        [Fact]
        public async Task An_action_whose_model_moved_while_it_was_thinking_writes_nothing()
        {
            using var f = await GappyModelAsync("PlanActFence.bim");
            await f.ConsentAsync();

            // The action composes the plan before it acts, and that compose is the window a person can change the
            // model in. The ranking used to be awaited inside it, so an oracle that edited the model while it was
            // asked opened the window; since T268 the ranking never holds the plan (it runs in the background),
            // so the edit is made at the compose seam instead, which makes the race a fact of the test rather
            // than a matter of timing.
            var fired = 0;
            f.Engine.PlanComposePauseForTest = async () =>
            {
                if (System.Threading.Interlocked.Exchange(ref fired, 1) != 0) return;
                await f.Engine.CreateMeasureAsync("table:Sales", "Slipped In", "SUM ( Sales[Amount] )", "human");
            };

            var before = await f.Engine.AiReadinessScanAsync();
            ModelPlanActionResult result;
            try { result = await f.Engine.ModelPlanActionAsync("safefix/Visibility", "start", "human"); }
            finally { f.Engine.PlanComposePauseForTest = null; }

            Assert.Equal(1, fired);
            Assert.False(result.Performed);
            Assert.Contains("changed", result.Sentence, StringComparison.OrdinalIgnoreCase);

            // Nothing was written: the gap the row was about is still there.
            var after = await f.Engine.AiReadinessScanAsync();
            Assert.Contains(after.Findings, x => string.Equals(x.RuleId, "VIS-FK", StringComparison.Ordinal) && !x.Waived);
            Assert.Equal(
                before.Findings.Count(x => string.Equals(x.RuleId, "VIS-FK", StringComparison.Ordinal)),
                after.Findings.Count(x => string.Equals(x.RuleId, "VIS-FK", StringComparison.Ordinal)));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Starting_the_safe_fix_item_applies_the_safe_fixes()
        {
            using var f = await GappyModelAsync("PlanAct.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var safeFix = plan.Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal));

            var result = await f.Engine.ModelPlanActionAsync(safeFix.Id, "start", "human");

            Assert.True(result.Performed);
            Assert.False(string.IsNullOrWhiteSpace(result.Sentence));
            Assert.DoesNotContain("—", result.Sentence, StringComparison.Ordinal);
            Assert.NotNull(result.Plan);
            Assert.Equal(ModelPlanStatuses.Done, result.Plan!.Items.Single(i => i.Id == safeFix.Id).Status);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Starting_something_nothing_can_run_yet_says_so_plainly_and_changes_nothing()
        {
            using var f = await GappyModelAsync("PlanNotYet.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var needsYou = plan.Items.First(i => i.Who != ModelPlanOwners.Auto);
            var before = await f.Engine.SessionInfoAsync();

            var result = await f.Engine.ModelPlanActionAsync(needsYou.Id, "start", "human");

            Assert.False(result.Performed);
            Assert.False(string.IsNullOrWhiteSpace(result.Sentence));
            Assert.DoesNotContain("—", result.Sentence, StringComparison.Ordinal);
            Assert.Equal(before.Revision, (await f.Engine.SessionInfoAsync()).Revision);
        }

        /// <summary>Honesty lane: approve exists, so start on a row a person approves must not say the approval step
        /// is not built. It points at the review verbs that are built: work the changes out, then approve them.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Starting_a_row_you_approve_points_at_approve_and_never_says_it_is_not_built()
        {
            using var f = await GappyModelAsync("PlanApproveFirst.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            // The assistant owns every row a tool can do now (doer lane, 13 Oct), so no gappy row is the person's: a row Semanticus does not run is told to its
            // assistant, and a row only the person decides still points at approve.
            var yours = plan.Items.Where(i => i.Who != ModelPlanOwners.Auto && i.Status != ModelPlanStatuses.Skipped).ToArray();
            Assert.NotEmpty(yours);

            foreach (var item in yours)
            {
                var result = await f.Engine.ModelPlanActionAsync(item.Id, "start", "human");
                Assert.False(result.Performed);
                Assert.DoesNotContain("not built yet", result.Sentence, StringComparison.Ordinal);
                Assert.Contains(item.Who == ModelPlanOwners.You ? "approve" : "assistant", result.Sentence, StringComparison.Ordinal);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Starting_a_skipped_item_asks_for_it_to_be_put_back_first()
        {
            using var f = await GappyModelAsync("PlanActSkipped.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var safeFix = plan.Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal));
            await f.Engine.SkipModelPlanItemAsync(safeFix.Id, "deliberate", "human");

            var result = await f.Engine.ModelPlanActionAsync(safeFix.Id, "start", "human");

            Assert.False(result.Performed);
            Assert.Contains("set aside", result.Sentence, StringComparison.OrdinalIgnoreCase);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_id_that_is_not_on_the_plan_is_answered_rather_than_thrown_at()
        {
            using var f = await GappyModelAsync("PlanActUnknown.bim");

            var result = await f.Engine.ModelPlanActionAsync("safefix/NoSuchThing", "start", "human");

            Assert.False(result.Performed);
            Assert.Contains("id", result.Sentence, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Four verbs now, not one. This test used to pin "only start exists", which M3's change
        /// sets superseded: propose, approve and cancel are real, and only ONE of the four writes. An
        /// unknown verb still throws, and the message names all four rather than one.
        /// ModelPlanChangesTests holds what each of the three new ones actually does.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_four_verbs_are_accepted_and_anything_else_is_refused_by_name()
        {
            using var f = await GappyModelAsync("PlanActVerb.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var id = plan.Items[0].Id;

            // Approving with nothing worked out is an ANSWER, not an exception: the verb is real.
            var approve = await f.Engine.ModelPlanActionAsync(id, "approve", "human");
            Assert.False(approve.Performed);
            var cancel = await f.Engine.ModelPlanActionAsync(id, "cancel", "human");
            Assert.False(cancel.Performed);

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => f.Engine.ModelPlanActionAsync(id, "obliterate", "human"));
            foreach (var verb in new[] { "propose", "approve", "cancel", "start" })
                Assert.Contains(verb, ex.Message, StringComparison.Ordinal);
        }

        // ---- one list of what to do next -----------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_next_actions_are_the_plans_top_three()
        {
            using var f = await GappyModelAsync("PlanNext.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var expected = plan.Items
                .Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed)
                .Take(3)
                .Select(i => i.Id)
                .ToArray();

            var actions = await f.Engine.GetNextActionsAsync();

            Assert.Equal(expected.Length, actions.Length);
            Assert.True(actions.Length <= 3);
            for (var i = 0; i < expected.Length; i++)
                Assert.Contains(expected[i], actions[i].Args, StringComparison.Ordinal);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_orientations_suggestions_are_the_same_list()
        {
            using var f = await GappyModelAsync("PlanOrientation.bim");

            var summary = await f.Engine.GetOrientationAsync();
            var actions = await f.Engine.GetNextActionsAsync();

            Assert.NotNull(summary.SuggestedNextActions);
            Assert.Equal(actions.Select(a => a.Args), summary.SuggestedNextActions!.Select(a => a.Args));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task With_no_model_open_the_only_suggestion_is_to_open_one()
        {
            using var f = new DecisionFixture();

            var actions = await f.Engine.GetNextActionsAsync();

            Assert.Equal("open_model", Assert.Single(actions).Op);
        }


        // ---- per-table detail on an item (the Map) ---------------------------------------------------

        /// <summary>The count an item claims to touch, pulled back out of its own "touches" line ("4
        /// objects" gives 4), or null when that line is not a count at all (a model name, a list of report
        /// names). Reading it back is the only way to assert the per-table counts against the SAME number a
        /// person reads on the row.</summary>
        private static int? TouchedCount(string touches)
        {
            if (string.IsNullOrWhiteSpace(touches)) return null;
            var digits = new string(touches.Replace(",", "").TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) ? n : (int?)null;
        }

        /// <summary>What every item's per-table detail must hold, whatever family built it.</summary>
        private static void AssertTableDetail(ModelPlanItem item)
        {
            Assert.NotNull(item.Tables);

            // Sorted by name, one entry per table, no repeats: the Map draws one block per table.
            Assert.Equal(item.Tables.Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
                         item.Tables.Select(t => t.Name));
            Assert.Equal(item.Tables.Length,
                         item.Tables.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // The item's own diff is capped for reading; a table's slice of it is not, so the subset check
            // below is only meaningful while nothing was dropped.
            var capped = item.Diff.Any(l => l.TrimStart().StartsWith("and ", StringComparison.Ordinal));

            foreach (var table in item.Tables)
            {
                Assert.False(string.IsNullOrWhiteSpace(table.Name));
                Assert.True(table.Count > 0, item.Id + " filed " + table.Name + " with nothing in it");
                Assert.False(string.IsNullOrWhiteSpace(table.What),
                             item.Id + " says nothing about what is in " + table.Name);

                foreach (var line in table.Diff)
                {
                    // The whole point of the shape: a line shown inside a table's block names that table.
                    Assert.True(line.Contains(table.Name, StringComparison.Ordinal),
                                item.Id + " filed a line under " + table.Name + " that does not name it: " + line);
                    // and it is one of the item's own lines, not a second rendering of them.
                    if (!capped) Assert.Contains(line, item.Diff);
                }
            }

            // Nothing can be filed twice, so the parts can never exceed the whole.
            var total = TouchedCount(item.Touches);
            if (total != null)
                Assert.True(item.Tables.Sum(t => t.Count) <= total.Value,
                            item.Id + " files more objects than it says it touches");
        }

        /// <summary>The parts add up to the whole: every object this item touches is in a table, so the
        /// per-table counts sum to the count on the row.</summary>
        private static void AssertCountsSumToTheTotal(ModelPlanItem item)
        {
            AssertTableDetail(item);
            var total = TouchedCount(item.Touches);
            Assert.NotNull(total);
            // The model itself (obj:Model/Model) is on no table's block, so a row that touches it files one object fewer than it counts (coverage-fix, 13 Oct).
            var onNoTable = (item.Objects ?? Array.Empty<string>()).Count(o => o.StartsWith("obj:Model/", StringComparison.Ordinal));
            Assert.Equal(total!.Value, item.Tables.Sum(t => t.Count) + onNoTable);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_safe_fix_row_counts_and_files_its_changes_per_table()
        {
            using var f = await GappyModelAsync("PlanDetailSafeFix.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            var visibility = plan.Items.Single(i => i.Id == "safefix/Visibility");

            AssertCountsSumToTheTotal(visibility);
            var sales = visibility.Tables.Single(t => t.Name == "Sales");
            Assert.Contains("column", sales.What, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(sales.Diff);
            // The object is written the way a person writes it, which is what makes the line name its table.
            Assert.All(sales.Diff, l => Assert.Contains("Sales[", l, StringComparison.Ordinal));
        }

        [Fact]
        public async Task The_words_row_counts_and_files_its_changes_per_table()
        {
            using var f = await GappyModelAsync("PlanDetailDescribe.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            var describe = plan.Items.Single(i => i.Id == "words/model");   // the descriptions are in the one words row

            // The words row can also name the model itself (the AI instructions), which sits in no table's block, so the blocks add up to
            // the total or to one short of it for that object.
            AssertTableDetail(describe);
            var total = TouchedCount(describe.Touches);
            Assert.NotNull(total);
            var inBlocks = describe.Tables.Sum(t => t.Count);
            Assert.InRange(total!.Value - inBlocks, 0, 1);
            Assert.NotEmpty(describe.Tables);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_cleanup_row_counts_and_files_its_changes_per_table()
        {
            using var f = await GappyModelAsync("PlanDetailCleanup.bim");
            await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, "PlanDetailCleanup.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            var cleanup = plan.Items.Single(i => i.Id == "cleanup/model");

            AssertCountsSumToTheTotal(cleanup);
            // City is the unused column the fixture plants, and it lives on Customer. (Not Old Total, the unused
            // measure: since m11/plan-harm round 2 the cleanup never offers a measure.)
            var customer = cleanup.Tables.Single(t => t.Name == "Customer");
            Assert.Contains(customer.Diff, l => l.Contains("Customer[City]", StringComparison.Ordinal));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_proposal_row_counts_and_files_its_changes_per_table()
        {
            using var f = await GappyModelAsync("PlanDetailProposal.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            var proposals = plan.Items.Where(i => i.Id.StartsWith("proposal/", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(proposals);   // if the fixture raises none, this test proves nothing

            foreach (var item in proposals) AssertCountsSumToTheTotal(item);
        }

        // ---- a proposal row says the JOB, not the findings -------------------------------------------
        //
        // Kane, on the Yoga build, 23 September. The Date block on the Map carried "Look at 24 suggestions",
        // its reasons were four copies of one rule message with a different term in each, and its "exactly
        // what changes" lines restated the finding ("~ Date: object names collide under Q&A term matching").
        // These tests hold the three things that fixes: the title names the job with the count, the reasons
        // are three sentences however many findings there are, and a diff line is a change or there is no
        // diff at all.

        /// <summary>A model with a known number of Q&A name collisions and nothing else interesting: each
        /// loop makes one column and one measure whose names normalise to the same Q&A term
        /// ("Group7Key" and "Group7 Key" both become "group7 key"), which is one collision group each.
        /// Q&A is engaged first, because the rule is dormant on a model with no Q&A at all.</summary>
        private static async Task<DecisionFixture> CollidingModelAsync(string file, int groups)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateLinguisticSchemaAsync(null, "agent");
            for (var i = 1; i <= groups; i++)
            {
                await f.Engine.CreateColumnAsync("table:Sales", "Group" + i + "Key", "String", null, "human");
                await f.Engine.CreateMeasureAsync("table:Sales", "Group" + i + " Key", "SUM ( Sales[Amount] )", "human");
            }
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_collision_row_names_the_job_with_its_count_and_never_says_look_at_suggestions()
        {
            using var f = await CollidingModelAsync("PlanCollisionTitle.bim", 24);

            var plan = await f.Engine.GetModelPlanAsync();
            var item = plan.Items.Single(i => i.Id == "proposal/collisions");

            Assert.Equal("Rename or hide fields with 24 names Copilot confuses", item.Title);
            // Q&A retires in February 2027; the plan speaks about Copilot, which reads the same linguistic schema.
            Assert.All(plan.Items, i => Assert.DoesNotContain("Q&A", i.Title, StringComparison.Ordinal));
            Assert.All(plan.Items, i => Assert.All(i.Why ?? Array.Empty<string>(), w => Assert.DoesNotContain("Q&A", w, StringComparison.Ordinal)));
            // The words that sent Kane looking: no row on this plan tells a person to go and look.
            Assert.All(plan.Items, i => Assert.DoesNotContain("Look at", i.Title, StringComparison.OrdinalIgnoreCase));
            Assert.All(plan.Items, i => Assert.DoesNotContain("suggestion", i.Title, StringComparison.OrdinalIgnoreCase));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_collision_rows_reasons_are_three_sentences_whatever_the_finding_count_is()
        {
            using var f = await CollidingModelAsync("PlanCollisionWhy.bim", 24);

            var plan = await f.Engine.GetModelPlanAsync();
            var item = plan.Items.Single(i => i.Id == "proposal/collisions");

            Assert.Equal(3, item.Why.Length);

            // One: what was found and why it matters, said once, with the count.
            Assert.Contains("24 words are used by more than one visible field", item.Why[0], StringComparison.Ordinal);

            // Two: up to four examples, then a count of the rest. One line, not twenty-four.
            Assert.StartsWith("For example ", item.Why[1], StringComparison.Ordinal);
            Assert.EndsWith("; and 20 more.", item.Why[1], StringComparison.Ordinal);
            // Four examples and one tail, so four separators, however many findings there are.
            Assert.Equal(5, item.Why[1].Split(';').Length);

            // Three: what fixing it earns.
            Assert.Contains("Renaming or hiding the duplicates", item.Why[2], StringComparison.Ordinal);

            // And the regression itself: not one of the rule's own sentences reaches the row.
            Assert.All(item.Why, w => Assert.DoesNotContain("normalise to the Q&A term", w, StringComparison.Ordinal));
            Assert.All(item.Why, w => Assert.DoesNotContain("linguistic schema", w, StringComparison.Ordinal));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_collision_rows_diff_lines_are_changes_and_not_findings()
        {
            using var f = await CollidingModelAsync("PlanCollisionDiff.bim", 24);

            var plan = await f.Engine.GetModelPlanAsync();
            var item = plan.Items.Single(i => i.Id == "proposal/collisions");

            Assert.NotEmpty(item.Diff);
            foreach (var line in item.Diff)
            {
                if (line.TrimStart().StartsWith("and ", StringComparison.Ordinal)) continue;   // the cap's own tail
                Assert.StartsWith("~ ", line, StringComparison.Ordinal);
                Assert.EndsWith(": rename it or hide it", line, StringComparison.Ordinal);
            }
            // The exact sentence Kane read under "exactly what changes", which is a finding and not a change.
            Assert.All(item.Diff, l => Assert.DoesNotContain("object names collide", l, StringComparison.OrdinalIgnoreCase));

            // A row that names its change counts its objects plainly, with no "nothing happens yet" line.
            Assert.Equal("24 objects", item.Touches);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_family_whose_fix_is_your_choice_shows_no_lines_and_says_so()
        {
            using var f = await CollidingModelAsync("PlanCollisionUnnameable.bim", 24);

            var plan = await f.Engine.GetModelPlanAsync();
            // Every measure the fixture makes is the same calculation, so the duplicate-measures family is
            // raised. The engine cannot name the change: which of two identical measures survives, and what
            // the survivor is called, is the person's answer and not the rule's.
            var item = plan.Items.Single(i => i.Id == "proposal/duplicate-measures");

            Assert.Empty(item.Diff);
            Assert.Contains("no change until you choose", item.Touches, StringComparison.Ordinal);
            Assert.Contains("the page shows each case", item.Touches, StringComparison.Ordinal);
            Assert.StartsWith("24 objects", item.Touches, StringComparison.Ordinal);   // the count stays readable
            Assert.All(item.Tables, t => Assert.Empty(t.Diff));                        // and no table invents one
            Assert.NotEmpty(item.Tables);                                              // but the work is still filed
            Assert.Equal(24, item.Tables.Sum(t => t.Count));

            // And the row still says what the job is, in three sentences, with no rule message in sight.
            Assert.Equal("Merge 24 duplicate measures", item.Title);
            Assert.Equal(3, item.Why.Length);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_proposal_rows_per_table_phrase_names_that_tables_own_cases()
        {
            using var f = await CollidingModelAsync("PlanCollisionTables.bim", 24);

            var plan = await f.Engine.GetModelPlanAsync();
            var item = plan.Items.Single(i => i.Id == "proposal/collisions");

            var sales = item.Tables.Single(t => t.Name == "Sales");
            Assert.Equal(24, sales.Count);
            // The family's own word for a case, this table's count, and this table's own examples. Not the
            // generic "24 columns", which is true of every family at once.
            Assert.StartsWith("24 name collisions here: ", sales.What, StringComparison.Ordinal);
            Assert.Contains("Sales[", sales.What, StringComparison.Ordinal);
            AssertCountsSumToTheTotal(item);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_check_row_counts_and_files_its_changes_per_table()
        {
            using var f = await GappyModelAsync("PlanDetailCheck.bim");

            // A read report that really uses Sales[Total Sales], so the plan can honestly say the measure is
            // in a report and nothing checks it. Without the reading there is no check family at all.
            var visual = "{ \"visual\": { \"query\": { \"queryState\": { \"Values\": { \"projections\": [ "
                       + "{ \"field\": { \"Measure\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"Sales\" } }, "
                       + "\"Property\": \"Total Sales\" } } } ] } } } } }";
            var reports = Path.Combine(f.Workspace, "Reports");
            var visualDir = Path.Combine(reports, "Sales.Report", "definition", "pages", "p1", "visuals", "v1");
            Directory.CreateDirectory(visualDir);
            File.WriteAllText(Path.Combine(reports, "Sales.Report", "definition", "report.json"), "{}");
            File.WriteAllText(Path.Combine(visualDir, "visual.json"), visual);
            await f.Engine.SetReportScopeAsync(
                new[] { new Semanticus.Engine.Lineage.ReportScopeChoice
                        { Kind = "local", Name = "Sales", Path = reports } }, "human");
            await f.Engine.CheckReportsAsync(Array.Empty<string>(), false, null, null, null, "human", default);

            var plan = await f.Engine.GetModelPlanAsync();
            var check = plan.Items.SingleOrDefault(i => i.Id == "check/measure:Sales/Total Sales");
            Assert.NotNull(check);   // if the reading did not land, nothing below proves anything

            AssertCountsSumToTheTotal(check!);
            var sales = Assert.Single(check!.Tables);
            Assert.Equal("Sales", sales.Name);
            Assert.Equal(1, sales.Count);
            Assert.Contains("measure", sales.What, StringComparison.OrdinalIgnoreCase);
            Assert.All(sales.Diff, l => Assert.Contains("Sales[Total Sales]", l, StringComparison.Ordinal));
        }

        /// <summary>Kane, 11 Oct 2026, on 1.3.0: after an edit, the plan raised "Read 1 report again" and every "Add a check" row
        /// said Waiting beside Your assistant, waiting for that row. On a real model: a report is looked at, the model changes, and
        /// the check row is still Ready, names the report in plain words, and the report row says what to press.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task After_an_edit_a_check_row_is_ready_names_its_report_and_the_report_row_says_what_to_press()
        {
            using var f = await GappyModelAsync("PlanCheckAfterEdit.bim");
            var visual = "{ \"visual\": { \"query\": { \"queryState\": { \"Values\": { \"projections\": [ "
                       + "{ \"field\": { \"Measure\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"Sales\" } }, "
                       + "\"Property\": \"Total Sales\" } } } ] } } } } }";
            var reports = Path.Combine(f.Workspace, "Reports");
            var visualDir = Path.Combine(reports, "Sales Overview.Report", "definition", "pages", "p1", "visuals", "v1");
            Directory.CreateDirectory(visualDir);
            File.WriteAllText(Path.Combine(reports, "Sales Overview.Report", "definition", "report.json"), "{}");
            File.WriteAllText(Path.Combine(visualDir, "visual.json"), visual);
            await f.Engine.SetReportScopeAsync(
                new[] { new Semanticus.Engine.Lineage.ReportScopeChoice
                        { Kind = "local", Name = "Sales Overview", Path = reports } }, "human");
            await f.Engine.CheckReportsAsync(Array.Empty<string>(), false, null, null, null, "human", default);

            // Any edit moves the model past the report's reading, which is what raised the report row on Kane's model.
            await f.Engine.SetDescriptionAsync("table:Sales", "One row per order line.", "human");

            var plan = await f.Engine.GetModelPlanAsync();
            var recheck = plan.Items.Single(i => i.Id == "recheck/reports");
            Assert.Equal("Look at 1 report again", recheck.Title);
            Assert.Contains(recheck.Why, w => w.Contains("press Check reports", StringComparison.Ordinal));
            var check = plan.Items.Single(i => i.Id == "check/measure:Sales/Total Sales");
            Assert.Empty(check.WaitsFor);
            Assert.NotEqual(PlanStateWords.Waiting, check.StateWord);
            Assert.Equal("The report 'Sales Overview' shows Total Sales, but no check tests it.", check.Why[0]);
            // Nothing Semanticus or the assistant does waits for the person's report row.
            var held = plan.Items.Where(i => i.Doer != PlanDoers.You && i.WaitsFor.Any(w => w.Id == "recheck/reports")).Select(i => i.Id).ToArray();
            Assert.True(held.Length == 0, "waits for the report row: " + string.Join(", ", held));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_row_about_no_table_in_particular_files_nothing_rather_than_guessing()
        {
            using var f = await GappyModelAsync("PlanDetailNoTable.bim");

            // A chosen report nobody has read: the plan raises a "read the reports again" row, which is
            // about reports and about no table at all.
            var reportDir = Path.Combine(f.Workspace, "Reports", "Sales.Report", "definition");
            Directory.CreateDirectory(reportDir);
            File.WriteAllText(Path.Combine(reportDir, "report.json"), "{ }");
            await f.Engine.SetReportScopeAsync(
                new[] { new Semanticus.Engine.Lineage.ReportScopeChoice
                        { Kind = "local", Name = "Sales", Path = Path.Combine(f.Workspace, "Reports") } }, "human");

            var plan = await f.Engine.GetModelPlanAsync();
            var recheck = plan.Items.Single(i => i.Id.StartsWith("recheck/", StringComparison.Ordinal));

            Assert.Empty(recheck.Tables);
            Assert.Empty(recheck.Diff);
        }

        // ---- table facts (the Map's blocks) -----------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_table_of_nothing_but_measures_is_called_a_measure_table()
        {
            using var f = await GappyModelAsync("PlanMeasureTable.bim");

            // No columns beyond the row-number column, one measure. This is the shape Kane's screenshot
            // showed as a plain "table", which is what the new kind is for.
            await f.Engine.CreateTableAsync("Key Measures", "human");
            await f.Engine.CreateMeasureAsync("table:Key Measures", "Margin %", "0.5", "human");

            var plan = await f.Engine.GetModelPlanAsync();
            var keyMeasures = plan.Tables.Single(t => t.Name == "Key Measures");

            Assert.Equal(ModelPlanTableKinds.MeasureTable, keyMeasures.Kind);
            Assert.Equal(0, keyMeasures.Columns);
            Assert.Equal(1, keyMeasures.Measures);
            Assert.Equal(0, keyMeasures.Relationships);

            // And an ordinary table is not swept up by it.
            var sales = plan.Tables.Single(t => t.Name == "Sales");
            Assert.NotEqual(ModelPlanTableKinds.MeasureTable, sales.Kind);
            Assert.True(sales.Columns > 0);
            Assert.True(sales.Relationships > 0);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_table_that_is_nothing_but_its_join_keys_is_called_a_bridge()
        {
            using var f = await GappyModelAsync("PlanBridge.bim");

            await f.Engine.CreateTableAsync("Store", "human");
            await f.Engine.CreateColumnAsync("table:Store", "StoreKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Store Bridge", "human");
            await f.Engine.CreateColumnAsync("table:Store Bridge", "StoreKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Store Bridge", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Store Bridge/StoreKey", "column:Store/StoreKey", null, true, "human");
            await f.Engine.CreateRelationshipAsync("column:Store Bridge/CustomerKey", "column:Customer/CustomerKey", null, false, "human");

            var plan = await f.Engine.GetModelPlanAsync();
            var bridge = plan.Tables.Single(t => t.Name == "Store Bridge");

            Assert.Equal(ModelPlanTableKinds.Bridge, bridge.Kind);
            Assert.Equal(2, bridge.Columns);
            Assert.Equal(2, bridge.Relationships);
            // Store has one relationship and a column that is not an endpoint of two, so it is not a bridge.
            Assert.NotEqual(ModelPlanTableKinds.Bridge, plan.Tables.Single(t => t.Name == "Store").Kind);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_field_parameter_is_called_one_by_the_same_test_the_overview_uses()
        {
            using var f = await GappyModelAsync("PlanFieldParameter.bim");

            await f.Engine.CreateFieldParameterAsync("Pick a measure",
                new[] { new FieldParameterItem { ObjectRef = "measure:Sales/Total Sales" } }, "human");

            var plan = await f.Engine.GetModelPlanAsync();
            var parameter = plan.Tables.Single(t => t.Name == "Pick a measure");

            Assert.Equal(ModelPlanTableKinds.FieldParameter, parameter.Kind);
            // The Map and the Overview must agree about the same table, so the plan asks the SAME question
            // the block chart asks rather than a second one of its own.
            var graph = await f.Engine.GetModelGraphAsync();
            Assert.True(graph.Tables.Single(t => t.Name == "Pick a measure").IsFieldParameter);
        }

        [Fact]
        public async Task A_tables_finding_counts_are_the_readiness_findings_for_that_table()
        {
            using var f = await GappyModelAsync("PlanTableFindings.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            var card = await f.Engine.AiReadinessScanAsync();

            // The same findings the findings page counts, grouped by the same category names, filed onto the
            // table the finding's own reference names. Waived findings are accepted and are not work.
            var expected = (card.Findings ?? Array.Empty<Semanticus.Analysis.ReadinessFinding>())
                .Where(x => !x.Waived)
                .Select(x => new { Table = TableOfRefForTest(x.ObjectRef), Category = x.Category ?? "BestPractice" })
                .Where(x => x.Table != null)
                .GroupBy(x => x.Table, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key,
                              g => g.GroupBy(x => x.Category, StringComparer.Ordinal)
                                    .ToDictionary(c => c.Key, c => c.Count(), StringComparer.Ordinal),
                              StringComparer.OrdinalIgnoreCase);

            Assert.NotEmpty(expected);   // a model with no findings would prove nothing here

            foreach (var table in plan.Tables)
            {
                var want = expected.TryGetValue(table.Name, out var byCategory)
                    ? byCategory
                    : new Dictionary<string, int>(StringComparer.Ordinal);

                Assert.Equal(want.Count, table.Findings.Length);
                foreach (var finding in table.Findings)
                    Assert.Equal(want[finding.Category], finding.Count);

                // Sorted by category, so two reads draw the same block.
                Assert.Equal(table.Findings.Select(x => x.Category).OrderBy(c => c, StringComparer.Ordinal),
                             table.Findings.Select(x => x.Category));
            }
        }

        /// <summary>The table a readiness reference names, in the shape the engine reads it: "kind:Table" or
        /// "kind:Table/Member", and only for the kinds whose body really starts with a table.</summary>
        private static string TableOfRefForTest(string objectRef)
        {
            if (string.IsNullOrWhiteSpace(objectRef)) return null;
            var colon = objectRef.IndexOf(':');
            if (colon <= 0) return null;
            var kind = objectRef.Substring(0, colon);
            if (kind is not ("table" or "column" or "measure" or "hierarchy" or "level" or "partition" or "calcitem"))
                return null;
            var body = objectRef.Substring(colon + 1);
            var slash = body.IndexOf('/');
            var table = slash < 0 ? body : body.Substring(0, slash);
            return string.IsNullOrWhiteSpace(table) ? null : table;
        }

        [Fact]
        public async Task A_table_has_no_size_until_somebody_has_measured_one()
        {
            using var f = await GappyModelAsync("PlanTableSize.bim");

            var plan = await f.Engine.GetModelPlanAsync();

            // Nothing here is live, so nothing has measured a byte. Null is the honest answer; a zero would
            // say the table is empty and the file size would be a different number wearing this name.
            Assert.All(plan.Tables, t => Assert.Null(t.SizeBytes));
        }

        // ---- the history is a story, not a list of sentences ------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Each_kind_of_history_entry_is_written_by_its_own_action_with_the_right_actor()
        {
            using var f = await GappyModelAsync("PlanHistoryKinds.bim");

            var first = await f.Engine.GetModelPlanAsync();
            var drawn = Assert.Single(first.History);
            Assert.Equal(ModelPlanHistoryKinds.Drawn, drawn.Kind);
            Assert.Equal(ModelPlanActors.Engine, drawn.Actor);
            Assert.Equal(first.Revision, drawn.Revision);
            Assert.NotEmpty(drawn.Appeared);
            Assert.Equal(drawn.Sentence, drawn.Text);   // Text keeps working for anything that only knows it

            var safeFix = first.Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal));

            // A skip is the person's, and the reason is the DETAIL, not folded into the sentence.
            var afterSkip = await f.Engine.SkipModelPlanItemAsync(safeFix.Id, "not for this model", "human");
            var skipped = afterSkip.History.Last(h => h.Kind == ModelPlanHistoryKinds.Skipped);
            Assert.Equal(ModelPlanActors.You, skipped.Actor);
            Assert.Equal(safeFix.Id, skipped.ItemId);
            Assert.Equal(safeFix.Title, skipped.Title);
            Assert.Equal("not for this model", Assert.Single(skipped.Detail));

            // A start the engine declines is recorded too, with the refusal as the detail. This is the entry
            // a person needs most and can least reconstruct: "I pressed start and nothing happened".
            var refusal = await f.Engine.ModelPlanActionAsync(safeFix.Id, "start", "human");
            Assert.False(refusal.Performed);
            var refused = refusal.Plan.History.Last(h => h.Kind == ModelPlanHistoryKinds.Refused);
            Assert.Equal(ModelPlanActors.You, refused.Actor);
            Assert.Equal(safeFix.Id, refused.ItemId);
            Assert.Equal(refusal.Sentence, Assert.Single(refused.Detail));

            // The agent door is "assistant", not "you".
            var afterUnskip = await f.Engine.UnskipModelPlanItemAsync(safeFix.Id, "agent");
            var unskipped = afterUnskip.History.Last(h => h.Kind == ModelPlanHistoryKinds.Unskipped);
            Assert.Equal(ModelPlanActors.Assistant, unskipped.Actor);
            Assert.Equal(safeFix.Id, unskipped.ItemId);
            Assert.Empty(unskipped.Detail);

            // A step of a run-all is the plan's, and a start that changed something is "done".
            var run = await f.Engine.ModelPlanActionAsync(safeFix.Id, "start", "plan");
            Assert.True(run.Performed);
            var done = run.Plan.History.Last(h => h.Kind == ModelPlanHistoryKinds.Done);
            Assert.Equal(ModelPlanActors.Plan, done.Actor);
            Assert.Equal(safeFix.Id, done.ItemId);
            Assert.Equal(run.Revision, done.Revision);
            Assert.NotEmpty(done.Detail);

            // And the refresh that followed it says what went, in the words the person read.
            var redrawn = run.Plan.History.Last(h => h.Kind == ModelPlanHistoryKinds.Redrawn);
            Assert.Equal(ModelPlanActors.Engine, redrawn.Actor);
            Assert.Contains(safeFix.Title, redrawn.Completed);
            Assert.Contains(safeFix.Title, redrawn.Gone);
        }

        [Fact]
        public async Task A_start_that_changes_nothing_is_recorded_as_started_and_not_as_done()
        {
            using var f = await GappyModelAsync("PlanHistoryStarted.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            var safeFix = plan.Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal));

            await f.Engine.ModelPlanActionAsync(safeFix.Id, "start", "human");
            // The gap is gone, so the row comes back as done and a second start finds nothing to do. The
            // engine still ran, so the entry says started; calling it done would report work nobody did.
            var again = await f.Engine.ModelPlanActionAsync(safeFix.Id, "start", "human");

            Assert.False(again.Performed);
            Assert.DoesNotContain(again.Plan.History,
                h => h.Kind == ModelPlanHistoryKinds.Done && h.Revision == again.Revision
                     && h.ItemId == safeFix.Id && h.Detail.Length == 0);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void An_old_text_only_history_file_reads_back_as_a_redraw_by_the_engine()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-planold-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var file = PlanMemory.FileIn(dir);
                File.WriteAllText(file!,
                    "{ \"models\": { \"" + Identity + "\": { \"history\": [ "
                    + "{ \"when\": \"2026-09-01T10:00:00.0000000+00:00\", \"text\": \"1 item done, 1 new.\" } ] } } }");

                var entry = Assert.Single(PlanMemory.Read(file, Identity).History);

                Assert.Equal("1 item done, 1 new.", entry.Text);
                Assert.Equal("1 item done, 1 new.", entry.Sentence);
                Assert.Equal(ModelPlanHistoryKinds.Redrawn, entry.Kind);
                Assert.Equal(ModelPlanActors.Engine, entry.Actor);
                // Nothing is invented for what an old entry cannot have.
                Assert.Null(entry.ItemId);
                Assert.Null(entry.Title);
                Assert.Equal(0, entry.Revision);
                Assert.Empty(entry.Detail);
                Assert.Empty(entry.Appeared);

                // A write of anything else keeps it rather than dropping what it could not classify.
                PlanMemory.Mutate(file, Identity, _ => { });
                var again = Assert.Single(PlanMemory.Read(file, Identity).History);
                Assert.Equal("1 item done, 1 new.", again.Text);
                Assert.Equal(ModelPlanHistoryKinds.Redrawn, again.Kind);
                Assert.Equal(ModelPlanActors.Engine, again.Actor);
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- what finishing it is worth ---------------------------------------------------------------

        [Fact]
        public async Task A_grade_that_does_not_move_says_so_instead_of_saying_B_to_B()
        {
            using var f = await GappyModelAsync("PlanWorthUnchanged.bim");

            var first = await f.Engine.GetModelPlanAsync();
            // A grade still being rehearsed is not a match. There is no second number to match against, and
            // "Stays B" about a number nobody has is the same lie in nicer words.
            if (first.Worth.Pending) Assert.False(first.Worth.Unchanged);

            await f.Engine.PlanProjectionForTest;
            var settled = await f.Engine.GetModelPlanAsync();

            Assert.False(settled.Worth.Pending);
            Assert.Equal(
                settled.Worth.From != null && settled.Worth.To != null
                    && string.Equals(settled.Worth.From, settled.Worth.To, StringComparison.Ordinal),
                settled.Worth.Unchanged);
        }

        // ---- the memory file itself ------------------------------------------------------------------

        /// <summary>A stand-in for one model's durable identity, in the shape PaneIdentity produces. These
        /// tests are about the FILE, so any stable key does.</summary>
        private const string Identity = "disk:abcd1234";

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void A_malformed_memory_file_reads_as_nothing_remembered_rather_than_throwing()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-planmem-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var file = PlanMemory.FileIn(dir);
                File.WriteAllText(file!, "{ this is not json");

                var state = PlanMemory.Read(file, Identity);

                Assert.Empty(state.Skips);
                Assert.Empty(state.History);
                Assert.Empty(state.LastItems);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void The_refresh_note_counts_and_never_characterises()
        {
            Assert.Contains("first time", PlanMemory.RefreshNote(Array.Empty<string>(), new[] { "a", "b" }));
            Assert.Equal("Nothing on the plan changed.", PlanMemory.RefreshNote(new[] { "a" }, new[] { "a" }));
            Assert.Equal("1 task done, 1 new.", PlanMemory.RefreshNote(new[] { "a" }, new[] { "b" }));
            Assert.Equal("2 tasks done.", PlanMemory.RefreshNote(new[] { "a", "b" }, Array.Empty<string>()));
            Assert.Equal("1 new.", PlanMemory.RefreshNote(new[] { "a" }, new[] { "a", "b" }));
            // A rule row that stood aside for a suggestion is replaced, not done (review round 3, DT4).
            Assert.Equal("1 task replaced by a suggestion, 1 new.", PlanMemory.RefreshNote(new[] { "a" }, new[] { "b" }, new[] { "a" }));
            Assert.Equal("1 task done, 1 task replaced by a suggestion.", PlanMemory.RefreshNote(new[] { "a", "b" }, Array.Empty<string>(), new[] { "b" }));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void The_history_keeps_the_last_two_hundred_entries_and_no_more()
        {
            Assert.Equal(200, PlanMemory.MaxHistory);

            var dir = Path.Combine(Path.GetTempPath(), "sem-planhist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var file = PlanMemory.FileIn(dir);
                for (var i = 0; i < PlanMemory.MaxHistory + 5; i++)
                {
                    var n = i;
                    PlanMemory.Mutate(file, Identity, s => s.History.Add(new ModelPlanHistoryEntry
                    {
                        When = DateTimeOffset.UtcNow, Text = "note " + n,
                    }));
                }

                var state = PlanMemory.Read(file, Identity);
                Assert.Equal(PlanMemory.MaxHistory, state.History.Count);
                Assert.Equal("note " + (PlanMemory.MaxHistory + 4), state.History[^1].Text);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void A_skip_with_nowhere_to_keep_it_is_reported_rather_than_silently_lost()
        {
            Assert.False(PlanMemory.Mutate(null, Identity, _ => { }));
            // No durable identity is the same answer as no file: nothing is remembered, and the caller is told.
            Assert.False(PlanMemory.Mutate("/tmp/does-not-matter.json", null, _ => { }));
        }
    }
}
