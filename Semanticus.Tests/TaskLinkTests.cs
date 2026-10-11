// The link from an assistant's edits to the task it holds (Plan at the heart, lane E3; contract 8.4).
//
// While an assistant holds a claim, its edits count toward the task it claimed most recently. The engine keeps that
// as a per-reader pointer, keyed by the same writer id a commit is stamped with ("mcp" for the MCP door in this
// process, the connection id for a pipe), set only by an explicit claim, and cleared by complete and decline.
//
// What these tests hold: the pointer's rules, and that a commit made while a claim is held carries Cause = task
// on both paths (the task scope is opened in McpHealthAppender for the owner path and in RpcServer's per-request
// dispatch for the attached path, both reading WorkingTaskFor).

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using static Semanticus.Tests.PlanQueueKit;

namespace Semanticus.Tests
{
    public sealed class TaskLinkTests
    {
        [Fact]
        public async Task A_claim_points_the_readers_edits_at_the_task_and_complete_clears_it()
        {
            using var f = await GappyAsync("LinkClaim.bim");
            var id = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));
            Assert.Null(f.Engine.WorkingTaskFor(ChangeJournal.InProcessReader));

            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.True(claim.Ok, claim.Message);
            Assert.Equal(id, f.Engine.WorkingTaskFor(ChangeJournal.InProcessReader));
            Assert.Equal("Describe the Margin measure", f.Engine.WorkingTaskTitleFor(ChangeJournal.InProcessReader));

            var done = await f.Engine.CompleteAssistantRequestAsync(id, claim.Request.ClaimToken, "Described.");
            Assert.True(done.Ok, done.Message);
            Assert.Null(f.Engine.WorkingTaskFor(ChangeJournal.InProcessReader));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Claiming_again_switches_back_and_only_the_newest_token_answers()
        {
            using var f = await GappyAsync("LinkSwitch.bim");
            var a = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));
            var b = await AskAsync(f.Engine, "Describe the Cost measure", Free("Cost"));

            var first = await f.Engine.ClaimAssistantRequestAsync(a);
            var other = await f.Engine.ClaimAssistantRequestAsync(b);
            Assert.True(first.Ok && other.Ok);
            Assert.Equal(b, f.Engine.WorkingTaskFor(ChangeJournal.InProcessReader));

            var back = await f.Engine.ClaimAssistantRequestAsync(a);        // it already holds a: switch back
            Assert.True(back.Ok, back.Message);
            Assert.Equal(a, f.Engine.WorkingTaskFor(ChangeJournal.InProcessReader));
            Assert.NotEqual(first.Request.ClaimToken, back.Request.ClaimToken);

            var stale = await f.Engine.CompleteAssistantRequestAsync(a, first.Request.ClaimToken, "x");
            Assert.Equal(Semanticus.Engine.Jev.HandoffRefusal.WrongClaimToken, stale.Refusal);
            Assert.True((await f.Engine.CompleteAssistantRequestAsync(a, back.Request.ClaimToken, "x")).Ok);
            // Completing a leaves the pointer nowhere: b is still held, but the link is only ever made by a claim.
            Assert.Null(f.Engine.WorkingTaskFor(ChangeJournal.InProcessReader));
        }

        [Fact]
        public async Task Decline_clears_the_pointer_and_so_does_the_person_taking_the_task_back()
        {
            using var f = await GappyAsync("LinkDecline.bim");
            var a = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));
            var b = await AskAsync(f.Engine, "Describe the Cost measure", Free("Cost"));

            var claimA = await f.Engine.ClaimAssistantRequestAsync(a);
            Assert.True((await f.Engine.DeclineAssistantRequestAsync(a, claimA.Request.ClaimToken, "No such measure.")).Ok);
            Assert.Null(f.Engine.WorkingTaskFor(ChangeJournal.InProcessReader));

            Assert.True((await f.Engine.ClaimAssistantRequestAsync(b)).Ok);
            await f.Engine.DeclinePlanTaskAsync(b, "I will do this one myself.");   // the person's Could not
            Assert.Null(f.Engine.WorkingTaskFor(ChangeJournal.InProcessReader));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Each_reader_has_its_own_pointer()
        {
            using var f = await GappyAsync("LinkReaders.bim");
            var a = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));
            var b = await AskAsync(f.Engine, "Describe the Cost measure", Free("Cost"));

            Assert.True((await f.Engine.ClaimAssistantRequestAsync(a, "rpc:7")).Ok);
            Assert.True((await f.Engine.ClaimAssistantRequestAsync(b)).Ok);
            Assert.Equal(a, f.Engine.WorkingTaskFor("rpc:7"));
            Assert.Equal(b, f.Engine.WorkingTaskFor(ChangeJournal.InProcessReader));
            Assert.Null(f.Engine.WorkingTaskFor("rpc:8"));

            // One reader cannot claim over another's hold.
            var taken = await f.Engine.ClaimAssistantRequestAsync(a, "rpc:8");
            Assert.Equal(Semanticus.Engine.Jev.HandoffRefusal.AlreadyClaimed, taken.Refusal);
            Assert.Equal(a, f.Engine.WorkingTaskFor("rpc:7"));
        }
        // ---- the cause on the commit (needs lane E4's ChangeCause; slice 7 integration) ---------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Edits_made_while_holding_a_claim_carry_the_task_as_their_cause_on_both_paths()
        {
            using var f = await GappyAsync("LinkCause.bim");
            var a = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));
            var b = await AskAsync(f.Engine, "Describe the Cost measure", Free("Cost"));
            var seen = new List<ChangeNotification>();
            f.Sessions.Bus.Changed += n => { if (n.Deltas?.Length > 0) seen.Add(n); };

            // The owner path: the MCP door in this process.
            var claim = await f.Engine.ClaimAssistantRequestAsync(a);
            await CallAsync(f.Engine, () => McpTools.SetDescription(f.Engine, "measure:Sales/Total Sales", "Owner path."));
            var owner = seen.Last();
            Assert.Equal("assistant", owner.By);
            Assert.Equal(ChangeCauseKinds.Task, owner.Cause?.Kind);
            Assert.Equal(new[] { a }, owner.Cause.Ids);
            Assert.Equal("Describe the Margin measure", owner.Cause.Title);

            // Complete clears it: the next edit has no cause.
            await f.Engine.CompleteAssistantRequestAsync(a, claim.Request.ClaimToken, "Done.");
            await CallAsync(f.Engine, () => McpTools.SetDescription(f.Engine, "measure:Sales/Total Sales", "After complete."));
            Assert.Null(seen.Last().Cause);

            // The attached path: an assistant over the pipe claims b and edits through its RemoteEngine.
            await using var pipe = await Pipe.OpenAsync(f);
            Assert.True((await McpTools.ClaimRequest(pipe.Assistant, b)).Ok);
            await CallAsync(pipe.Assistant, () => McpTools.SetDescription(pipe.Assistant, "measure:Sales/Total Sales", "Attached path."));
            var attached = seen.Last();
            Assert.Equal(ChangeCauseKinds.Task, attached.Cause?.Kind);
            Assert.Equal(new[] { b }, attached.Cause.Ids);

            // A person's edit in Studio is never filed under the assistant's task.
            await pipe.Human.InvokeAsync<object>("setDescription", "measure:Sales/Total Sales", "The person.", "human");
            Assert.Null(seen.Last().Cause);
        }
    }
}
