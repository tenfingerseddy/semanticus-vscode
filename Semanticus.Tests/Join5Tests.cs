// M11 join part 5: Sol's delta review left one class of lost work open, the verifier's join item 2 on m11/sol2-engine. A person's unapplied
// words on the Plan (a line with AuthoredAfter) were kept through every Jev family refresh (A1), but a NEW plan (propose_plan, propose_replace)
// and an item redraw (Work it out again) still replaced them with no trace, from either door. Only the person's explicit cancel of that item
// may remove them. Every fact here failed first, on b118c6d9 + the two sol2 merges.

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class Join5Tests
    {
        // ---- J1: a new plan keeps the words somebody wrote ---------------------------------------------------------------------------

        [Theory]
        [InlineData("agent")]
        [InlineData("human")]
        public async Task J1_propose_plan_keeps_a_persons_unapplied_words_held_with_their_reason(string door)
        {
            var (f, e, _) = await Sol2EngineTests.AuthoredDescriptionAsync("J1plan-" + door + ".bim", "human");
            using (f)
            {
                await e.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, door);

                var kept = await Sol2EngineTests.WaitingWordsAsync(e);
                Assert.True(kept.Length == 1, "propose_plan (" + door + ") took away the person's words (" + kept.Length + " lines carry them)");
                Assert.NotEqual("approved", kept[0].Status);   // held: the plan it was ticked on is gone, so it waits for a fresh tick
                Assert.False(string.IsNullOrWhiteSpace(kept[0].Reason), "a held line says why it is held");

                // The next Plan read works the family's lines out again: the words must stay on ONE line, not two.
                await e.GetModelPlanAsync();
                await e.JevSettledForTest();
                Assert.Single(await Sol2EngineTests.WaitingWordsAsync(e));
            }
        }

        [Fact]
        public async Task J1_propose_replace_keeps_a_persons_unapplied_words()
        {
            var (f, e, _) = await Sol2EngineTests.AuthoredDescriptionAsync("J1replace.bim", "human");
            using (f)
            {
                await e.ProposeReplaceAsync(new SearchOptions { Query = "Customer", Fields = new[] { "name" } }, "Client", 0, "agent");

                var kept = await Sol2EngineTests.WaitingWordsAsync(e);
                Assert.True(kept.Length == 1, "propose_replace took away the person's words (" + kept.Length + " lines carry them)");
                Assert.False(string.IsNullOrWhiteSpace(kept[0].Reason));
            }
        }

        [Fact]
        public async Task J1_the_persons_own_cancel_of_the_item_still_removes_the_words()
        {
            var (f, e, _) = await Sol2EngineTests.AuthoredDescriptionAsync("J1cancel.bim", "human");
            using (f)
            {
                var row = (await e.GetModelPlanAsync()).Items.Single(i => i.Id == "jev.column-summarize.table:Sales");
                await e.ModelPlanActionAsync(row.Id, "cancel", "human", null);
                Assert.Empty(await Sol2EngineTests.WaitingWordsAsync(e));
            }
        }

        // ---- J4: every path that holds a line says whether the object it names is still there ----------------------------------------

        [Fact]
        public async Task J4_a_line_held_because_its_point_was_switched_off_says_the_measure_it_names_is_not_there()
        {
            var (f, e, _) = await Sol2EngineTests.AuthoredDescriptionAsync("J4off.bim", "human");
            using (f)
            {
                await e.SetDecisionPointEnabledAsync(Semanticus.Decisions.DecisionRegistry.ColumnSummarize, false, "human");
                await e.GetModelPlanAsync();
                await e.JevSettledForTest();

                var held = (await Sol2EngineTests.WaitingWordsAsync(e)).Single();
                Assert.Equal(JevFamily.HeldMissingReason, held.Reason);   // the measure was only ever proposed: it is not in the model
            }
        }

        // ---- J1: an item redraw keeps the words, even when the redraw has lines of its own ------------------------------------------

        private static async Task<(DecisionFixture F, LocalEngine E, ModelPlanItem Row, ChangeItem Line)> SafeFixRowWithAuthoredLineAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            await e.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, "agent");
            var row = (await e.GetModelPlanAsync()).Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal) && i.ChangeSet?.Lines?.Length > 0);
            var ids = row.ChangeSet.Lines.Select(l => l.PlanItemId).ToHashSet();
            // Somebody wrote their own words over one of this row's lines (what set_plan_item does to a Jev line, here on the live line).
            var line = f.Sessions.CurrentContext.Plans.Current.Snapshot().First(l => ids.Contains(l.Id));
            return (f, e, row, line);
        }

        [Theory]
        [InlineData("agent")]
        [InlineData("human")]
        public async Task J1_work_it_out_again_keeps_a_line_the_redraw_does_not_offer_again(string door)
        {
            var (f, e, row, line) = await SafeFixRowWithAuthoredLineAsync("J1redraw-" + door + ".bim");
            using (f)
            {
                line.ObjectRef = "column:Sales/NoLongerThere";   // the redraw has no line for this object
                line.After = line.AuthoredAfter = "My own words.";
                var id = line.Id;

                await e.ModelPlanActionAsync(row.Id, "propose", door, null);

                var kept = f.Sessions.CurrentContext.Plans.Current.Snapshot().Where(l => l.AuthoredAfter == "My own words.").ToArray();
                Assert.True(kept.Length == 1, "the redraw (" + door + ") took away the person's words (" + kept.Length + " lines carry them)");
                Assert.False(string.IsNullOrWhiteSpace(kept[0].Reason), "a held line says why it is held");
                Assert.NotEqual("approved", kept[0].Status);
            }
        }

        [Fact]
        public async Task J1_work_it_out_again_holds_the_words_and_still_offers_its_own_line()
        {
            var (f, e, row, line) = await SafeFixRowWithAuthoredLineAsync("J1redraw-same.bim");
            using (f)
            {
                var id = line.Id;
                line.After = line.AuthoredAfter = "My own words.";

                await e.ModelPlanActionAsync(row.Id, "propose", "human", null);

                var lines = f.Sessions.CurrentContext.Plans.Current.Snapshot();
                var words = lines.Where(l => l.After == "My own words.").ToArray();
                Assert.True(words.Length == 1, "the words are on one line, not lost and not copied (" + words.Length + ")");
                Assert.NotEqual("approved", words[0].Status);   // held: it waits for a fresh tick
                Assert.False(string.IsNullOrWhiteSpace(words[0].Reason));
                Assert.Contains(lines, l => l.AuthoredAfter == null && l.Kind == line.Kind && l.ObjectRef == line.ObjectRef);   // the redraw's own line is there too
            }
        }
    }
}
