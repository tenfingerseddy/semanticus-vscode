// Send to your assistant (Plan at the heart, lane U3): the finish line, end to end over the real pipe.
//
// The person presses "Give to your assistant" on an AI readiness finding. Studio sends addPlanTask on the human
// door with the request webview/src/sendtask.tsx builds for that class (findingTask('air', ...), asserted field for
// field in Semanticus.VSCode/test/send-to-assistant.test.mjs). The row reads "You added" from AI readiness, with
// your assistant as the doer. An attached assistant claims it, writes the description, and says it is done. The
// rescan the next Plan read runs finds the finding gone and closes the row to Done. The person does nothing after
// the first press.
//
// The block this test first caught (lane U3, 25 Sep): the task waited behind the Plan's own words/model and
// cleanup/model rows by plan order, so it was never offered and claim_request refused it.

using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using static Semanticus.Tests.PlanQueueKit;

namespace Semanticus.Tests
{
    public sealed class SendToAssistantTests
    {
        /// <summary>The request sendtask.tsx builds for an AI readiness finding, as it goes on the wire.</summary>
        private static PlanTaskRequest FindingSend(string rule, string objRef, string name, string brief) => new PlanTaskRequest
        {
            Words = $"Fix \"Measures have a description\" on {name}",
            Brief = brief,
            SourcePage = "readiness",
            Objects = new[] { objRef },
            DoneWhen = PlanDoneWhen.ModelCheck,
            OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Finding, Ref = rule + "|" + objRef },
        };

        [Fact]
        public async Task A_finding_given_to_the_assistant_is_claimed_fixed_and_closed_by_the_rescan()
        {
            using var f = await GappyAsync("SendFinish.bim");
            // Round 4 (THE CLEANUP OFFER RULE, version 2): this test needs the cleanup row on the plan, and the row offers from a
            // related table only when it can find that table's label. Customer had a city and nothing that says what a customer is
            // called, so the row had nothing to offer. The fixture gains a Name (the label) and a plain unused Segment; the rule is
            // unchanged.
            await f.Engine.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "Segment", "String", null, "human");
            await f.SaveAsAsync("SendFinish.bim");
            await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, "SendFinish.bim");
            await using var pipe = await Pipe.OpenAsync(f);
            const string objRef = "measure:Sales/Total Sales";

            // The press: one call on the human door.
            var added = await pipe.Human.InvokeAsync<ModelPlanActionResult>("addPlanTask",
                FindingSend("DESC-MEASURE", objRef, "Total Sales", "Write a business description for Sales[Total Sales]. Use set_description."));
            var row = Row(added.Plan, added.ItemId);
            Assert.Equal(PlanSources.You, row.Source);
            Assert.Equal("readiness", row.SourcePage);
            Assert.Equal(PlanDoers.Assistant, row.Doer);
            Assert.Equal(PlanDoneWhen.ModelCheck, row.DoneWhen);
            Assert.True(PlanStateWords.IsOpen(row.StateWord));

            // A task the person gave does not wait behind the Plan's own population rows: they wait for it
            // (contract 5.2, Coordinator 25 Sep). Before that rule the task read Waiting on both and was never offered.
            string Waits(ModelPlanItem i) => string.Join(";", (i.WaitsFor ?? System.Array.Empty<PlanWaitRef>()).Select(w => w.Id));
            Assert.True(row.StateWord == PlanStateWords.Ready, "task is " + row.StateWord + ", waits for " + Waits(row));
            foreach (var id in new[] { "words/model", "cleanup/model" })
            {
                var population = Row(added.Plan, id);
                Assert.Equal(PlanStateWords.Waiting, population.StateWord);
                Assert.Contains(population.WaitsFor, w => w.Id == added.ItemId);
            }

            // The assistant, on its next call, sees it and takes it.
            var offered = await pipe.Assistant.ListAssistantRequestsAsync();
            Assert.Contains(offered.Requests, r => r.RequestId == added.ItemId);
            var claim = await pipe.Assistant.ClaimAssistantRequestAsync(added.ItemId);
            Assert.True(claim.Ok, claim.Message);

            // It does the work through its own door and says so.
            await pipe.Assistant.SetDescriptionAsync(objRef, "All sales, in dollars, after returns.", "agent");
            var done = await pipe.Assistant.CompleteAssistantRequestAsync(added.ItemId, claim.Request.ClaimToken, "Described it.");
            Assert.True(done.Ok, done.Message);

            // The Plan read rescans: the finding is gone, so the row is Done with no person action.
            var plan = await pipe.Human.InvokeAsync<ModelPlanView>("getModelPlan");
            Assert.Equal(PlanStateWords.Done, Row(plan, added.ItemId).StateWord);
        }
    }
}
