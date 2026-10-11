// Run the plan (Plan at the heart, lane E5; contract 7.2, Sol 24 Sep).
//
// What these tests hold, one line each:
//   one press applies the Ready Semanticus set, and one undo to its first entry takes the whole press back
//   a held row is skipped
//   a row whose blocker is in the Ready set runs in a second batch of the SAME press (round 1, 8 Oct: one press runs every wave)
//   a model that moved after the read is refused with its sentence
//   a hold or a doer switch after the read, with no revision change, is refused by the ready digest
//   a cleanup row removes an unused measure, and a second one that the same batch started using is skipped and named
//   the strip's done counts cover today, across two sessions
//   an applier that throws leaves the model and the redo stack as they were
//   free is refused, as today

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class RunThePlanTests
    {
        // ---- fixtures ------------------------------------------------------------------------------------------

        /// <summary>ModelPlanChangesTests.GappyAsync, with the tier as an argument and a third unused measure.</summary>
        private static async Task<DecisionFixture> GappyAsync(string file, bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "City", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "ProductKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Product", "human");
            await f.Engine.CreateColumnAsync("table:Product", "ProductKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Product", "Name", "String", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, true, "human");
            await f.Engine.SetColumnMetadataAsync("column:Sales/ProductKey", null, "Sum", null, null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Old Total", "SUM ( Sales[Amount] )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Spare Total", "SUM ( Sales[Amount] )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        /// <summary>The Wide fixture of the commutation gate: a Date table the time intelligence reads and nobody
        /// has marked, key columns to hide, and an assistant line.</summary>
        private static Task<DecisionFixture> WideAsync(string tag) =>
            CommutationFixtures.All.Single(x => x.Name == "Wide").Copy(tag);

        private static ModelPlanItem Row(ModelPlanView plan, string id) =>
            plan.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

        /// <summary>Set aside every open row that is not Semanticus's (and not in <paramref name="keep"/>), the way a
        /// person clears their own rows out of the way. Under the careful rule (contract 5.1) the person's cleanup and
        /// proposal rows are population rows that every Semanticus row waits for, so nothing of Semanticus's is
        /// Ready until they are done or set aside.</summary>
        private static async Task<ModelPlanView> ClearTheWayAsync(DecisionFixture f, params string[] keep)
        {
            var plan = await f.Engine.GetModelPlanAsync();
            foreach (var i in plan.Items.Where(i => i.Doer != PlanDoers.Semanticus
                                                   && (i.Status == ModelPlanStatuses.Ready || i.Status == ModelPlanStatuses.Proposed)
                                                   && !keep.Contains(i.Id)))
                await f.Engine.SkipModelPlanItemAsync(i.Id, "out of the way for this test", "human");
            return await f.Engine.GetModelPlanAsync();
        }

        private static string[] ReadyIds(ModelPlanView plan) =>
            plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held)
                      .Select(i => i.Id).ToArray();

        // ---- one press, one step -------------------------------------------------------------------------------

        /// <summary>The finish line of lane E5, on the Wide fixture: one call from the agent door takes "Hide the key
        /// columns" and "Mark Date as the date table" as ONE undo entry by Semanticus, the time intelligence row
        /// turns Ready on the next read, and one undo takes both back.</summary>
        [Fact]
        public async Task One_press_applies_the_ready_set_as_one_undo_entry_and_one_undo_takes_it_back()
        {
            using var f = await WideAsync("RunOne");
            await f.Engine.AddPlanItemAsync("measure:Sales/Total Sales", "set_dax",
                "CALCULATE ( COUNTROWS ( Sales ), SAMEPERIODLASTYEAR ( 'Date'[Date] ) )", null, null, null, "agent");
            var plan = await ClearTheWayAsync(f, "asst/set_dax");

            var mark = Row(plan, "datetable/model");
            Assert.NotNull(mark);
            Assert.Equal(PlanDoers.Semanticus, mark.Doer);
            Assert.Equal("Mark Date as the date table", mark.Title);
            var ready = ReadyIds(plan);
            Assert.Contains("datetable/model", ready);
            Assert.Contains("safefix/Visibility", ready);
            var ti = Row(plan, "asst/set_dax");
            Assert.Contains(ti.WaitsFor, w => w.Id == "datetable/model");
            Assert.False(string.IsNullOrWhiteSpace(plan.ReadyDigest));
            var revisionBefore = f.Sessions.Current.Revision;

            var result = await McpTools.ApplyPlanItems(f.Engine, null, plan.ReadyDigest);   // the agent door

            Assert.True(result.Performed, result.Sentence);
            // Every row that was Ready is in the press; a row that waited for one of them runs behind it, in a later batch of the same press.
            Assert.All(ready, id => Assert.Contains(id, result.ItemIds));
            // ONE batch per wave: the revision moved by one mutation per batch, not one per row.
            Assert.Equal(revisionBefore + result.Waves, result.Revision);
            Assert.StartsWith("Applied ", result.Sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2014", result.Sentence);
            Assert.Equal("Time", await f.Sessions.Current.ReadAsync(m => m.Tables["Date"].DataCategory));

            // The blocker is in, so the row that waited for it reads Ready on the very next read, with nothing asked.
            var after = await f.Engine.GetModelPlanAsync();
            ti = Row(after, "asst/set_dax");
            Assert.Empty(ti.WaitsFor);
            Assert.Equal(PlanStateWords.Ready, ti.StateWord);

            // ONE undo, to the press's first entry, puts every row of the press back.
            await f.Engine.UndoToEntryAsync(result.EntryId, "human");
            Assert.NotEqual("Time", await f.Sessions.Current.ReadAsync(m => m.Tables["Date"].DataCategory));
            var back = await f.Engine.GetModelPlanAsync();
            Assert.NotEqual(PlanStateWords.Done, Row(back, "datetable/model").StateWord);
            Assert.NotEqual(PlanStateWords.Done, Row(back, "safefix/Visibility").StateWord);
        }

        /// <summary>Lane E5's second finding: after an undo the stored header still said "applied", so the rows the
        /// run applied read "checking", blocked nothing, and a second run skipped them as already in. Contract 5.2:
        /// a row whose applied changes are no longer in the model is open again.</summary>
        [Fact]
        public async Task After_undo_the_rows_a_run_applied_are_open_again_block_again_and_run_again()
        {
            using var f = await WideAsync("RunUndoOpen");
            await f.Engine.AddPlanItemAsync("measure:Sales/Total Sales", "set_dax",
                "CALCULATE ( COUNTROWS ( Sales ), SAMEPERIODLASTYEAR ( 'Date'[Date] ) )", null, null, null, "agent");
            var plan = await ClearTheWayAsync(f, "asst/set_dax");
            var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.True(ran.Performed, ran.Sentence);
            Assert.Contains("datetable/model", ran.ItemIds);

            await f.Engine.UndoToEntryAsync(ran.EntryId, "human");
            var back = await f.Engine.GetModelPlanAsync();
            foreach (var id in ran.ItemIds)
            {
                var row = Row(back, id);
                Assert.NotNull(row);
                Assert.NotEqual(PlanStateWords.Checking, row.StateWord);
                Assert.NotEqual(ModelPlanItemStates.Applied, row.State);
            }
            // The date row blocks the time intelligence row again, exactly as before the run.
            Assert.Contains(Row(back, "asst/set_dax").WaitsFor, w => w.Id == "datetable/model");
            Assert.Contains("datetable/model", ReadyIds(back));

            // And one more press applies them again, rather than skipping them as already in.
            var again = await f.Engine.ApplyPlanItemsAsync(null, "human", back.ReadyDigest);
            Assert.True(again.Performed, again.Sentence);
            Assert.Contains("datetable/model", again.ItemIds);
            Assert.Equal("Time", await f.Sessions.Current.ReadAsync(m => m.Tables["Date"].DataCategory));
        }

        [Fact]
        public async Task A_held_row_is_skipped_and_the_rest_runs()
        {
            using var f = await WideAsync("RunHeld");
            await ClearTheWayAsync(f);
            await f.Engine.HoldPlanItemAsync("datetable/model", true);
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Equal(PlanStateWords.Paused, Row(plan, "datetable/model").StateWord);
            Assert.DoesNotContain("datetable/model", ReadyIds(plan));

            var result = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);

            Assert.True(result.Performed, result.Sentence);
            Assert.DoesNotContain("datetable/model", result.ItemIds);
            Assert.Contains("safefix/Visibility", result.ItemIds);
            var after = await f.Engine.GetModelPlanAsync();
            Assert.Equal(PlanStateWords.Paused, Row(after, "datetable/model").StateWord);
            Assert.NotEqual("Time", await f.Sessions.Current.ReadAsync(m => m.Tables["Date"].DataCategory));
        }

        [Fact]
        public async Task A_row_whose_blocker_is_in_the_ready_set_runs_in_a_second_batch_of_the_same_press()
        {
            using var f = await WideAsync("RunBlocked");
            var plan = await ClearTheWayAsync(f);
            // Formatting writes a column Visibility also writes, so plan order puts it after Visibility.
            var blocked = Row(plan, "safefix/Formatting");
            Assert.NotNull(blocked);
            Assert.Equal(PlanStateWords.Waiting, blocked.StateWord);
            Assert.Contains(blocked.WaitsFor, w => ReadyIds(plan).Contains(w.Id));

            var result = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);

            Assert.True(result.Performed, result.Sentence);
            Assert.Contains("safefix/Formatting", result.ItemIds);
            Assert.True(result.Waves >= 2, "waves: " + result.Waves);
            // Nothing is left for a second press.
            var after = await f.Engine.GetModelPlanAsync();
            Assert.Empty(ReadyIds(after));
            var second = await f.Engine.ApplyPlanItemsAsync(null, "human", after.ReadyDigest);
            Assert.False(second.Performed, second.Sentence);
        }

        // ---- the fence ----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_plan_that_moved_since_the_read_is_refused_with_its_sentence()
        {
            using var f = await WideAsync("RunMoved");
            var plan = await ClearTheWayAsync(f);
            await f.Engine.SetDescriptionAsync("column:Sales/Note", "Changed after the read.", "human");
            var revision = f.Sessions.Current.Revision;

            var result = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);

            Assert.False(result.Performed);
            Assert.Equal("The plan moved since you read it. Read it again.", result.Sentence);
            Assert.Equal(revision, f.Sessions.Current.Revision);
        }

        [Fact]
        public async Task A_hold_or_a_doer_switch_after_the_read_is_refused_by_the_digest_with_no_revision_change()
        {
            using var f = await WideAsync("RunFence");
            var plan = await ClearTheWayAsync(f);
            var revision = f.Sessions.Current.Revision;

            // A hold moves no revision.
            await f.Engine.HoldPlanItemAsync("safefix/Visibility", true);
            Assert.Equal(revision, f.Sessions.Current.Revision);
            var held = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.False(held.Performed);
            Assert.Equal(LocalEngine.PlanMovedSentence, held.Sentence);
            Assert.Equal(revision, f.Sessions.Current.Revision);

            // Neither does a doer switch.
            await f.Engine.HoldPlanItemAsync("safefix/Visibility", false);
            var reread = await f.Engine.GetModelPlanAsync();
            Assert.Equal(plan.ReadyDigest, reread.ReadyDigest);   // back to the same set, same digest
            await f.Engine.SetPlanItemDoerAsync("datetable/model", PlanDoers.You);
            Assert.Equal(revision, f.Sessions.Current.Revision);
            var switched = await f.Engine.ApplyPlanItemsAsync(null, "human", reread.ReadyDigest);
            Assert.False(switched.Performed);
            Assert.Equal(LocalEngine.PlanMovedSentence, switched.Sentence);
            Assert.Equal(revision, f.Sessions.Current.Revision);
        }

        /// <summary>Sol 6, slice 7, finding 1. The digest is checked once, and the batch is worked out and applied after
        /// awaits. A pause, a doer switch or a skip used to write plan memory without the approve gate, so it could
        /// land in that window: the person was told the row was paused (or theirs, or set aside) and the run applied
        /// it anyway. Every change that affects Ready now takes the same gate, so a control that arrives mid-run
        /// waits for the run to finish and lands after it, never inside it.</summary>
        [Theory]
        [InlineData("hold")]
        [InlineData("doer")]
        [InlineData("skip")]
        public async Task A_control_that_arrives_after_the_digest_check_never_lands_before_the_apply(string control)
        {
            using var f = await WideAsync("RunRace" + control);
            var plan = await ClearTheWayAsync(f);
            Assert.Contains("datetable/model", ReadyIds(plan));

            Task? mid = null;
            var acknowledgedInsideTheRun = false;
            f.Engine.RunReadyCheckedForTest = async () =>
            {
                mid = control switch
                {
                    "hold" => f.Engine.HoldPlanItemAsync("datetable/model", true),
                    "doer" => f.Engine.SetPlanItemDoerAsync("datetable/model", PlanDoers.You),
                    _ => f.Engine.SkipModelPlanItemAsync("datetable/model", "not now", "human"),
                };
                // Long enough for an ungated write to finish several times over.
                await Task.WhenAny(mid, Task.Delay(750));
                acknowledgedInsideTheRun = mid.IsCompleted;
            };

            var result = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.NotNull(mid);
            await mid!;
            var applied = await f.Sessions.Current.ReadAsync(m => m.Tables["Date"].DataCategory) == "Time";

            // The failure Sol named: the person's control was acknowledged, and the row was applied after it.
            Assert.False(acknowledgedInsideTheRun && applied,
                "the " + control + " was acknowledged inside the run and the row was still applied");
            // Serialised: the control waited for the run, the run took the row, and the control is on the row now.
            Assert.False(acknowledgedInsideTheRun, "the " + control + " did not wait for the run's gate");
            Assert.True(result.Performed, result.Sentence);
            Assert.Contains("datetable/model", result.ItemIds);
            var after = Row(await f.Engine.GetModelPlanAsync(), "datetable/model");
            if (after != null && control == "hold") Assert.True(after.Held);
        }

        // ---- cleanup keeps its apply-time check, inside the one batch (Sol 24 Sep) -----------------------------

        /// <summary>Two removals in one Run the plan batch. The assistant's line in the SAME batch starts using one
        /// of them, so the re-check inside the batch keeps it and names it; the other is removed; and the rest of
        /// the batch applies, as one undo entry. Two unused COLUMNS: since m11/plan-harm round 2 the cleanup never
        /// offers a measure.</summary>
        [Fact]
        public async Task A_cleanup_row_removes_an_unused_column_and_skips_one_the_same_batch_started_using()
        {
            using var f = await GappyAsync("RunCleanup.bim");
            await f.Engine.CreateColumnAsync("table:Customer", "Segment", "String", null, "human");
            await f.SaveAsAsync("RunCleanup.bim");
            await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, "RunCleanup.bim");
            var proposed = await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human");
            var lines = Row(proposed.Plan, "cleanup/model").ChangeSet.Lines;
            foreach (var name in new[] { "City", "Segment" })
                await f.Engine.SetPlanItemAsync(lines.Single(l => l.Target == "column:Customer/" + name).PlanItemId, null, approved: true, "human");

            // The assistant rewrites Total Sales to read City: an edit, so it runs before every removal.
            const string reads = "SUM ( Sales[Amount] ) + 0 * DISTINCTCOUNT ( Customer[City] )";
            var added = await f.Engine.AddPlanItemAsync("measure:Sales/Total Sales", "set_dax", reads, null, null, null, "agent");
            var daxLine = added.Items.Single(i => i.Kind == "set_dax" && i.ObjectRef == "measure:Sales/Total Sales");
            await f.Engine.SetPlanItemAsync(daxLine.Id, null, approved: true, "human");

            var plan = await f.Engine.GetModelPlanAsync();
            var dax = plan.Items.Single(i => i.ChangeSet?.Lines?.Any(l => l.PlanItemId == daxLine.Id) == true);
            var cleanup = Row(plan, "cleanup/model");
            var revisionBefore = f.Sessions.Current.Revision;

            var result = await f.Engine.ApplyPlanItemsAsync(new[]
            {
                new PlanItemDigest { Id = dax.Id, Digest = dax.ChangeSet.Digest },
                new PlanItemDigest { Id = cleanup.Id, Digest = cleanup.ChangeSet.Digest },
            }, "human");

            Assert.True(result.Performed, result.Sentence);
            Assert.Equal(revisionBefore + 1, result.Revision);
            var (city, segment, total) = await f.Sessions.Current.ReadAsync(m => (
                m.Tables["Customer"].Columns.Contains("City"),
                m.Tables["Customer"].Columns.Contains("Segment"),
                m.Tables["Sales"].Measures["Total Sales"].Expression));
            Assert.True(city, "City was removed although the same batch started using it");
            Assert.False(segment, "Segment, still unused, was not removed");
            Assert.Equal(reads, total);
            var kept = Assert.Single(result.Skipped);
            Assert.Equal("cleanup/model", kept.ItemId);
            Assert.Equal("City", kept.What);
            Assert.Contains("City", result.Sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2014", result.Sentence);

            // One undo entry: one undo puts the rewrite and the removal back.
            await f.Engine.UndoAsync("human");
            var (spareBack, totalBack) = await f.Sessions.Current.ReadAsync(m => (
                m.Tables["Customer"].Columns.Contains("Segment"),
                m.Tables["Sales"].Measures["Total Sales"].Expression));
            Assert.True(spareBack);
            Assert.Equal("SUM ( Sales[Amount] )", totalBack);
        }

        // ---- done today -----------------------------------------------------------------------------------------

        [Fact]
        public async Task Done_counts_cover_today_across_two_sessions()
        {
            using var f = await WideAsync("RunToday");
            // Something finished two days ago is not today's.
            f.Engine.SeedPlanMemoryForTest(state => state.Done.Add(new PlanDone
            {
                ItemId = "safefix/Old", Title = "Fix an old thing", Section = ModelPlanSections.Structure,
                When = DateTimeOffset.UtcNow.AddDays(-2), Doer = PlanDoers.Semanticus,
            }));
            var plan = await ClearTheWayAsync(f);
            var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.True(ran.Performed, ran.Sentence);
            var ranCount = ran.ItemIds.Length;
            await f.Engine.SaveAsync(Path.Combine(f.Workspace, "RunToday.bim"), "bim", false, "human");

            var today = await f.Engine.GetModelPlanAsync();
            var semanticus = today.Strip.Tracks.Single(t => t.Doer == PlanDoers.Semanticus);
            Assert.Equal(ranCount, semanticus.Done);
            Assert.Contains(today.Items, i => i.Id == "safefix/Old" && i.StateWord == PlanStateWords.Done);

            // A second session: a new engine over the same workspace, opening the same file.
            using var sessions = new SessionManager();
            using var again = new LocalEngine(sessions, TestEntitlements.Pro, f.Workspace);
            await again.OpenAsync(Path.Combine(f.Workspace, "RunToday.bim"));
            var later = await again.GetModelPlanAsync();
            Assert.Equal(ranCount, later.Strip.Tracks.Single(t => t.Doer == PlanDoers.Semanticus).Done);
            Assert.All(ran.ItemIds, id => Assert.Contains(later.Items, i => i.Id == id && i.StateWord == PlanStateWords.Done
                                                                          && i.Doer == PlanDoers.Semanticus));
        }

        // ---- all or nothing -------------------------------------------------------------------------------------

        [Fact]
        public async Task An_applier_that_throws_leaves_the_model_and_the_redo_stack_as_they_were()
        {
            using var f = await GappyAsync("RunThrows.bim");
            // Something to redo, made before anything else, so the redo stack has one entry to lose.
            await f.Engine.SetDescriptionAsync("column:Sales/Amount", "Redo me.", "human");
            var undone = await f.Engine.UndoAsync("human");
            Assert.True(undone.CanRedo);

            var plan = await ClearTheWayAsync(f);
            // Only Formatting: set Visibility aside too, so Formatting is Ready on its own.
            await f.Engine.SkipModelPlanItemAsync("safefix/Visibility", "out of the way for this test", "human");
            var proposed = await f.Engine.ModelPlanActionAsync("safefix/Formatting", "propose", "human");
            var poison = Row(proposed.Plan, "safefix/Formatting").ChangeSet.Lines
                .First(l => string.Equals(l.Op, "set_summarize_by", StringComparison.OrdinalIgnoreCase));
            await f.Engine.SetPlanItemAsync(poison.PlanItemId, "NotAnAggregate", approved: true, "human");
            plan = await f.Engine.GetModelPlanAsync();
            Assert.Equal(new[] { "safefix/Formatting" }, ReadyIds(plan));
            var revision = f.Sessions.Current.Revision;
            var summarize = await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Columns["ProductKey"].SummarizeBy.ToString());

            var result = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);

            Assert.False(result.Performed);
            Assert.StartsWith("Nothing was changed", result.Sentence, StringComparison.Ordinal);
            Assert.Equal(revision, f.Sessions.Current.Revision);
            Assert.Equal(summarize, await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Columns["ProductKey"].SummarizeBy.ToString()));
            var redo = await f.Engine.RedoAsync("human");
            Assert.Equal("Redo me.", await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Columns["Amount"].Description));
            Assert.NotNull(redo);
        }

        // ---- free -----------------------------------------------------------------------------------------------

        [Fact]
        public async Task Free_is_refused_as_today()
        {
            using var f = await GappyAsync("RunFree.bim", pro: false);
            var plan = await f.Engine.GetModelPlanAsync();
            var revision = f.Sessions.Current.Revision;

            var result = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);

            Assert.False(result.Performed);
            Assert.Equal(LocalEngine.BulkRunIsProSentence, result.Sentence);
            Assert.Equal(revision, f.Sessions.Current.Revision);
        }

        [Fact]
        public async Task Items_and_a_ready_digest_together_are_refused_before_anything_is_read()
        {
            using var f = await GappyAsync("RunBoth.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.ApplyPlanItemsAsync(
                new[] { new PlanItemDigest { Id = "safefix/Visibility", Digest = "x" } }, "human", plan.ReadyDigest));
        }
    }
}
