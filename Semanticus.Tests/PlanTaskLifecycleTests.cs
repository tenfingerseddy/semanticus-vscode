// A task's lifecycle against the task's own state and the model it names (UAT S12-5 and S12-6, Sol's chaos round).
//
// What these tests hold, one line each:
//   S12-5-002  completing a task that was set aside while it was claimed is refused, and it stays set aside
//   S12-5-006  a task whose object was deleted cannot be claimed or completed; decline still hands it back
//   S12-6-002  a task typed with an object's name is grounded to that object, and reads Needs you when it goes;
//              a rename is not a deletion
//   S12-6-003  a repeat made from a task is tied to that task: its row shows the repeat, and it does not come due
//              while the task is set aside or its object is gone
//   S12-5-004  a draft does not overwrite a value the person changed after the draft was made: it stops and asks,
//              keep yours (the step is dropped) or apply the draft

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Jev;
using Xunit;
using static Semanticus.Tests.PlanQueueKit;

namespace Semanticus.Tests
{
    public sealed class PlanTaskLifecycleTests
    {
        private const string Total = "measure:Sales/Total Sales";

        private static int DoneCount(ModelPlanView plan) => plan.Strip?.Tracks?.Sum(t => t.Done) ?? 0;

        // ---- S12-5-002 --------------------------------------------------------------------------------------

        [Fact]
        public async Task Completing_a_task_set_aside_while_claimed_is_refused_and_it_stays_set_aside()
        {
            using var f = await GappyAsync("LcSetAside.bim");
            var id = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));
            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.True(claim.Ok, claim.Message);
            var doneBefore = DoneCount(await f.Engine.GetModelPlanAsync());

            await f.Engine.SkipModelPlanItemAsync(id, "Not needed any more.", "human");
            var answer = await f.Engine.CompleteAssistantRequestAsync(id, claim.Request.ClaimToken, "Done it.");

            Assert.Equal(HandoffRefusal.AlreadyTerminal, answer.Refusal);
            Assert.Contains("set aside", answer.Message);
            var plan = await f.Engine.GetModelPlanAsync();
            var row = Row(plan, id);
            Assert.Equal(PlanStateWords.SetAside, row.StateWord);
            Assert.Null(row.Reply);
            Assert.Equal(doneBefore, DoneCount(plan));

            // Nor can it be claimed again, or marked done by the person, while it is set aside.
            Assert.Equal(HandoffRefusal.AlreadyTerminal, (await f.Engine.ClaimAssistantRequestAsync(id, "other")).Refusal);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.CompletePlanTaskAsync(id, null));
        }

        // ---- S12-5-006 --------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_task_whose_object_was_deleted_cannot_be_claimed_or_completed()
        {
            using var f = await GappyAsync("LcDeleted.bim");
            // Claimed first: the second task names the same measure, so it waits for the first while both are open.
            var claimed = await AskAsync(f.Engine, "Check the Total Sales format", new[] { Total });
            var before = await AskAsync(f.Engine, "Describe Total Sales for the report authors", new[] { Total });
            var claim = await f.Engine.ClaimAssistantRequestAsync(claimed);
            Assert.True(claim.Ok, claim.Message);

            await f.Engine.DeleteObjectAsync(Total, "human");

            var refused = await f.Engine.ClaimAssistantRequestAsync(before);
            Assert.Equal(HandoffRefusal.NotReady, refused.Refusal);
            Assert.Contains("Sales[Total Sales] is no longer in the model", refused.Message);

            var answer = await f.Engine.CompleteAssistantRequestAsync(claimed, claim.Request.ClaimToken, "No description was applied.");
            Assert.Equal(HandoffRefusal.NotReady, answer.Refusal);
            Assert.Contains("Sales[Total Sales] is no longer in the model", answer.Message);

            var plan = await f.Engine.GetModelPlanAsync();
            var row = Row(plan, before);
            Assert.Equal(PlanStateWords.NeedsYou, row.StateWord);
            Assert.Equal("Sales[Total Sales] is no longer in the model.", row.StateNote);
            Assert.NotEqual(PlanStateWords.Done, Row(plan, claimed).StateWord);
            Assert.DoesNotContain((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == before);

            // Handing it back with a reason still works: the person decides what to do with it.
            var back = await f.Engine.DeclineAssistantRequestAsync(claimed, claim.Request.ClaimToken, "The measure is gone.");
            Assert.True(back.Ok, back.Message);
        }

        // ---- S12-6-002 --------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_task_that_names_an_object_is_grounded_to_it_and_says_so_when_it_goes()
        {
            using var f = await GappyAsync("LcGround.bim");
            var bracket = await AskAsync(f.Engine, "Review Sales[Total Sales] every week");
            var bare = await AskAsync(f.Engine, "Review Total Sales every week");
            var nothing = await AskAsync(f.Engine, "Tidy up the model");

            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Equal(new[] { Total }, Row(plan, bracket).Objects);
            Assert.Equal(new[] { Total }, Row(plan, bare).Objects);
            Assert.Empty(Row(plan, nothing).Objects);

            // A rename is not a deletion: the task still has its object.
            await f.Engine.RenameObjectAsync(Total, "Revenue", "human");
            plan = await f.Engine.GetModelPlanAsync();
            Assert.NotEqual(PlanStateWords.NeedsYou, Row(plan, bare).StateWord);
            Assert.Equal(new[] { "measure:Sales/Revenue" }, Row(plan, bare).Objects);

            await f.Engine.DeleteObjectAsync("measure:Sales/Revenue", "human");
            plan = await f.Engine.GetModelPlanAsync();
            Assert.Equal(PlanStateWords.NeedsYou, Row(plan, bare).StateWord);
            Assert.Equal("Sales[Revenue] is no longer in the model.", Row(plan, bare).StateNote);
            Assert.Equal(PlanStateWords.NeedsYou, Row(plan, bracket).StateWord);
            Assert.NotEqual(PlanStateWords.NeedsYou, Row(plan, nothing).StateWord);
        }

        // ---- S12-6-003 --------------------------------------------------------------------------------------

        private static readonly DateTimeOffset Friday = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

        private static async Task<DecisionFixture> ProAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            Clock(f.Engine, Friday);
            return f;
        }

        private static void Clock(LocalEngine engine, DateTimeOffset now)
        {
            engine.PlanRepeatClock = () => now;
            engine.PlanRepeatZone = TimeZoneInfo.Utc;
        }

        private static PlanRepeatCadence Monday => new PlanRepeatCadence { Kind = PlanCadences.Weekly, Day = "monday", Every = 1 };

        [Fact]
        public async Task A_repeat_made_from_a_task_is_tied_to_it_and_rests_while_it_is_set_aside()
        {
            using var f = await ProAsync("LcRepeat.bim");
            var id = await AskAsync(f.Engine, "Review Total Sales every week", doer: PlanDoers.You);
            var saved = await f.Engine.AddPlanRepeatAsync(new PlanRepeatRequest
            {
                Words = "Review Total Sales every week", Doer = PlanDoers.You, Cadence = Monday, FromTask = id,
            });
            var repeat = Assert.Single(saved.Plan.Repeats);
            // The row it was made from shows it at once: that is where Save leaves the person.
            Assert.Equal(id, repeat.FromItemId);
            Assert.Null(repeat.Resting);

            await f.Engine.SkipModelPlanItemAsync(id, "Not this quarter.", "human");
            repeat = Assert.Single((await f.Engine.GetModelPlanAsync()).Repeats);
            Assert.Equal("Its task is set aside, so it does not come back. Put the task back, or stop the repeat.", repeat.Resting);
            Clock(f.Engine, new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero));
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(plan.Items, i => i.Source == PlanSources.Repeat);

            // Put back, it comes due again.
            await f.Engine.UnskipModelPlanItemAsync(id, "human");
            plan = await f.Engine.GetModelPlanAsync();
            Assert.Null(Assert.Single(plan.Repeats).Resting);
            var card = Assert.Single(plan.Items.Where(i => i.Source == PlanSources.Repeat));
            Assert.Equal(new[] { Total }, card.Objects);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_repeat_whose_object_is_gone_does_not_come_due_and_says_why()
        {
            using var f = await ProAsync("LcRepeatGone.bim");
            var id = await AskAsync(f.Engine, "Review Total Sales every week");
            await f.Engine.AddPlanRepeatAsync(new PlanRepeatRequest
            {
                Words = "Review Total Sales every week", Doer = PlanDoers.Assistant, Cadence = Monday, FromTask = id,
            });
            await f.Engine.DeleteObjectAsync(Total, "human");

            Clock(f.Engine, new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero));
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(plan.Items, i => i.Source == PlanSources.Repeat);
            Assert.Equal("Sales[Total Sales] is no longer in the model, so it does not come back.", Assert.Single(plan.Repeats).Resting);
        }

        // ---- S12-5-004 --------------------------------------------------------------------------------------

        private static async Task<(LocalEngine engine, SessionManager sm)> DraftModelAsync()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "sem-lc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            return await ComposerFrame.OpenAsync(pro: true, workspace: workspace);
        }

        private static string DescribeAmount(string text) => JsonSerializer.Serialize(new object[]
        {
            new { id = "describe", title = "Describe Amount", op = "set_description",
                  args = new { objRef = "column:Sales/Amount", text } },
        });

        private static Task<string> AmountDescriptionAsync(SessionManager sm) =>
            sm.Current.ReadAsync(m => m.Tables["Sales"].Columns["Amount"].Description);

        [Fact]
        public async Task A_draft_does_not_overwrite_a_value_changed_after_it_was_made()
        {
            var (engine, sm) = await DraftModelAsync();
            using (sm)
            {
                var v = await engine.ComposeTaskAsync(null, null, null, DescribeAmount("What the line sold for."), "Describe Amount", "human");
                await engine.SetDescriptionAsync("column:Sales/Amount", "Finance review pending.", "human");

                var ran = await engine.RunDraftAsync(v.DraftId, "human");

                Assert.Equal("Finance review pending.", await AmountDescriptionAsync(sm));
                Assert.Equal(DraftStates.NeedsYou, ran.State);
                Assert.Equal(DraftCopy.NothingChangedYet, ran.Summary);
                Assert.Equal("overwrite", ran.Banner?.Kind);
                Assert.NotNull(ran.Overwrite);
                Assert.Equal("Sales[Amount] has a new description since this draft was made. Keep yours, or apply the draft?", ran.Overwrite.Sentence);
                Assert.Equal("Finance review pending.", ran.Overwrite.Now);
                Assert.Equal(PlanStateWords.NeedsYou, await DraftFrame.WordAsync(engine, v.DraftId));

                // Keep yours: the step is dropped and nothing is written.
                var kept = await engine.EditDraftAsync(v.DraftId, "keep_theirs", null, null, null, null, "human");
                Assert.Null(kept.Overwrite);
                Assert.Equal("Finance review pending.", await AmountDescriptionAsync(sm));
                Assert.False(kept.Steps.Single(s => s.Id == "describe").Kept);
            }
        }

        [Fact]
        public async Task Apply_the_draft_writes_it_over_the_newer_value_when_the_person_says_so()
        {
            var (engine, sm) = await DraftModelAsync();
            using (sm)
            {
                var v = await engine.ComposeTaskAsync(null, null, null, DescribeAmount("What the line sold for."), "Describe Amount", "agent");
                await engine.SetDescriptionAsync("column:Sales/Amount", "Finance review pending.", "human");
                var ran = await engine.RunDraftAsync(v.DraftId, "agent");
                Assert.NotNull(ran.Overwrite);

                // Kane, 10 Oct 2026: the person's word in chat is enough, so the assistant can write over their edit too (Undo brings theirs back).
                var applied = await engine.EditDraftAsync(v.DraftId, "apply_draft", null, null, null, null, "agent");
                Assert.Null(applied.Overwrite);
                Assert.Equal(DraftStates.Done, applied.State);
                Assert.Equal("What the line sold for.", await AmountDescriptionAsync(sm));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_draft_with_no_change_under_it_runs_as_before()
        {
            var (engine, sm) = await DraftModelAsync();
            using (sm)
            {
                var v = await engine.ComposeTaskAsync(null, null, null, DescribeAmount("What the line sold for."), "Describe Amount", "human");
                var ran = await engine.RunDraftAsync(v.DraftId, "human");
                Assert.Null(ran.Overwrite);
                Assert.Equal(DraftStates.Done, ran.State);
                Assert.Equal("What the line sold for.", await AmountDescriptionAsync(sm));
            }
        }
    }
}
