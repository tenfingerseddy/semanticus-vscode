using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Jev;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// M0a contract 3, the ASSISTANT HANDOFF (docs/redesign/contracts/assistant-handoff.md).
    ///
    /// Astra's both-door finding this answers: "hand to the connected assistant" has no transport today.
    /// The webview copies a prompt to the clipboard, RemoteEngine registers no incoming task handler, and
    /// RpcServer broadcasts to pipe clients rather than to the user's agent. So the engine owns a queue, the
    /// job SUSPENDS rather than holding a call open, and the assistant learns of the request on its next call
    /// exactly as it learns of a change.
    /// </summary>
    public sealed class JevAssistantHandoffTests
    {
        private static InMemoryAssistantRequestQueue Queue() => new InMemoryAssistantRequestQueue();

        private static Task<HandoffResult> Post(IAssistantRequestQueue q, string job = "job-1", string need = "a description for measure Sales[Margin]") =>
            q.PostAsync(new AssistantRequestPost
            {
                JobId = job, SessionId = "sess-1", Need = need,
                AllowedContext = new[] { "measure.name", "measure.expression", "table.name" },
            });

        [Fact]
        public async Task A_posted_request_is_listable_and_carries_only_the_allowed_context()
        {
            var q = Queue();
            var posted = await Post(q);

            var list = await q.ListAsync("sess-1");

            var one = Assert.Single(list);
            Assert.Equal(posted.Request!.RequestId, one.RequestId);
            Assert.Equal(AssistantRequestState.Queued, one.State);
            Assert.Equal(new[] { "measure.name", "measure.expression", "table.name" }, one.AllowedContext);
            // The claim token never leaves the engine on a list.
            Assert.Null(one.ClaimToken);
        }

        [Fact]
        public async Task Claiming_twice_refuses_the_second_claim_and_the_first_token_still_works()
        {
            var q = Queue();
            var posted = await Post(q);
            var id = posted.Request!.RequestId;

            var first = await q.ClaimAsync(id, "claude-a");
            var second = await q.ClaimAsync(id, "claude-b");

            Assert.True(first.Ok);
            Assert.NotNull(first.Request!.ClaimToken);
            Assert.Equal(HandoffRefusal.AlreadyClaimed, second.Refusal);
            Assert.Contains("claude-a", second.Message!);

            var done = await q.CompleteAsync(id, first.Request.ClaimToken, new AssistantAnswer { Text = "Margin as a share of sales." });
            Assert.True(done.Ok);
            Assert.Equal(AssistantRequestState.Completed, done.Request!.State);
        }

        [Fact]
        public async Task A_reply_with_the_wrong_claim_token_is_refused()
        {
            var q = Queue();
            var posted = await Post(q);
            await q.ClaimAsync(posted.Request!.RequestId, "claude-a");

            var wrong = await q.CompleteAsync(posted.Request.RequestId, "ct-not-the-one", new AssistantAnswer { Text = "words" });

            Assert.Equal(HandoffRefusal.WrongClaimToken, wrong.Refusal);
            Assert.Equal(AssistantRequestState.Claimed, (await q.ListAsync("sess-1")).Single().State);
        }

        [Fact]
        public async Task Completing_after_the_job_cancelled_the_request_is_refused_and_the_words_are_discarded()
        {
            var q = Queue();
            var posted = await Post(q);
            var claim = await q.ClaimAsync(posted.Request!.RequestId, "claude");

            var cancelled = await q.CancelForJobAsync("job-1", "The job was cancelled.");
            var late = await q.CompleteAsync(posted.Request.RequestId, claim.Request!.ClaimToken, new AssistantAnswer { Text = "too late" });

            Assert.Equal(1, cancelled);
            Assert.Equal(HandoffRefusal.AlreadyTerminal, late.Refusal);
            var closed = (await q.ListAsync("sess-1", includeClosed: true)).Single();
            Assert.Equal(AssistantRequestState.Cancelled, closed.State);
            Assert.Null(closed.Answer);                       // the words were never stored
        }

        [Fact]
        public async Task Withdrawing_consent_cancels_a_queued_request_and_suppresses_a_late_reply()
        {
            var q = Queue();
            var a = await Post(q, need: "a description for measure Sales[Margin]");
            var b = await Post(q, job: "job-2", need: "a name for the new table");
            var claim = await q.ClaimAsync(b.Request!.RequestId, "claude");

            var cancelled = await q.WithdrawConsentAsync();

            Assert.Equal(2, cancelled);                        // queued AND claimed are both cancelled
            var all = await q.ListAsync("sess-1", includeClosed: true);
            Assert.All(all, r => Assert.Equal(AssistantRequestState.Cancelled, r.State));
            Assert.Empty(await q.ListAsync("sess-1"));

            // A reply that was already in flight is suppressed rather than stored and ignored.
            var late = await q.CompleteAsync(b.Request.RequestId, claim.Request!.ClaimToken, new AssistantAnswer { Text = "too late" });
            Assert.Equal(HandoffRefusal.ConsentWithdrawn, late.Refusal);
            Assert.Null((await q.ListAsync("sess-1", includeClosed: true)).Single(r => r.RequestId == b.Request.RequestId).Answer);

            // And nothing new may be posted while consent is off.
            var refusedPost = await Post(q);
            Assert.Equal(HandoffRefusal.NoConsent, refusedPost.Refusal);
            Assert.Null(a.Request!.Answer);
        }

        [Fact]
        public async Task Pending_requests_are_folded_into_a_result_the_way_recent_changes_are_and_cost_nothing_when_empty()
        {
            var q = Queue();
            Assert.Null(q.PendingFor("sess-1"));
            Assert.Null(AssistantPendingBlock.Text(q.PendingFor("sess-1")));

            await Post(q);
            var summary = q.PendingFor("sess-1");

            Assert.NotNull(summary);
            Assert.Equal(1, summary!.Pending);
            var text = AssistantPendingBlock.Text(summary)!;
            Assert.Contains("assistant: 1 request(s) waiting for you", text);
            Assert.Contains("a description for measure Sales[Margin]", text);
            Assert.Contains("claim_request", text);

            // A CLAIMED request is not pending for anyone else, so it stops being folded in.
            var id = (await q.ListAsync("sess-1")).Single().RequestId;
            await q.ClaimAsync(id, "claude");
            Assert.Null(q.PendingFor("sess-1"));
        }

        [Fact]
        public async Task A_request_belongs_to_one_session_and_is_never_offered_to_another()
        {
            var q = Queue();
            await Post(q);

            Assert.Empty(await q.ListAsync("sess-2"));
            Assert.Null(q.PendingFor("sess-2"));
        }

        [Fact]
        public async Task Declining_closes_the_request_with_the_reason_visible()
        {
            var q = Queue();
            var posted = await Post(q);
            var claim = await q.ClaimAsync(posted.Request!.RequestId, "claude");

            var declined = await q.DeclineAsync(posted.Request.RequestId, claim.Request!.ClaimToken, "I cannot see the expression.");

            Assert.True(declined.Ok);
            Assert.Equal(AssistantRequestState.Declined, declined.Request!.State);
            Assert.Equal("I cannot see the expression.", declined.Request.DeclineReason);
            Assert.Empty(await q.ListAsync("sess-1"));
        }

        [Fact]
        public async Task Answering_a_request_nobody_claimed_is_refused()
        {
            var q = Queue();
            var posted = await Post(q);

            var result = await q.CompleteAsync(posted.Request!.RequestId, "ct-made-up", new AssistantAnswer { Text = "words" });

            Assert.Equal(HandoffRefusal.NotClaimed, result.Refusal);
        }

        [Fact]
        public async Task An_unknown_request_id_is_refused_with_the_operation_that_lists_them()
        {
            var q = Queue();
            var result = await q.ClaimAsync("ar-999", "claude");

            Assert.Equal(HandoffRefusal.NotFound, result.Refusal);
            Assert.Contains("list_assistant_requests", result.Message!);
        }

        // ---------------------------------------------------------------- the job side of the handoff

        private sealed class OneNeedCatalogue : IStepCatalogue
        {
            public string Version => "cat-1";
            public IReadOnlyList<StepDefinition> For(string family) => new[]
            {
                new StepDefinition
                {
                    Id = "ask", Title = "Ask for the words", Op = "set_description",
                    Provides = new[] { "words-exist" },
                    RequiresInputs = new[] { "assistant:a description for measure Sales[Margin]" },
                },
            };
            public IReadOnlyList<JobObligation> ObligationsFor(string family) =>
                new[] { new JobObligation("words-exist", "the words exist") };
        }

        private sealed class Evidence : IJobEvidenceSource
        {
            public JobEvidence Current() => new JobEvidence(1, "src-1", "snap-1", "cat-1");
            public string InputDigest(string key) => "d0";
        }

        private sealed class AllGranted : IFeatureGrantsView
        {
            public bool Grants(Semanticus.Engine.Entitlement.ProFeature feature) => true;
        }

        private sealed class NeedsWords : IJobStepExecutor
        {
            public Task<EngineStepResult> ExecuteAsync(IJob job, StepDefinition step,
                IReadOnlyDictionary<string, string> answers, CancellationToken ct) =>
                Task.FromResult(answers.ContainsKey("assistant:a description for measure Sales[Margin]")
                    ? new EngineStepResult { Ok = true, ObligationsMet = new[] { "words-exist" }, EvidenceDigest = "ev-1" }
                    : new EngineStepResult
                    {
                        Ok = true,
                        AssistantNeed = "a description for measure Sales[Margin]",
                        AssistantAllowedContext = new[] { "measure.name", "measure.expression" },
                    });
        }

        [Fact]
        public async Task The_job_suspends_rather_than_holding_the_call_open_and_a_decline_resumes_it_at_a_stub()
        {
            var q = Queue();
            var runner = new InMemoryJobRunner(new OneNeedCatalogue(), new NeedsWords(), new Evidence(), new AllGranted(), q);
            var job = (await runner.StartAsync(new JobStartRequest { Family = "author" }, "sess-1", 1)).Job!;

            var suspended = (await runner.NextStepAsync(new JobStepSubmission
            {
                JobId = job.JobId, SessionId = "sess-1", SessionGeneration = 1,
            })).Job!;

            // The call RETURNED. It did not wait for the same caller to take its next step.
            Assert.Equal(JobState.AwaitingAssistant, suspended.State);
            Assert.Equal("a description for measure Sales[Margin]", suspended.PendingNeed);
            var pending = Assert.Single(await q.ListAsync("sess-1"));
            Assert.Equal(job.JobId, pending.JobId);

            var claim = await q.ClaimAsync(pending.RequestId, "claude");
            await q.DeclineAsync(pending.RequestId, claim.Request!.ClaimToken, "I cannot see the expression.");

            var resumed = (await runner.ResumeAsync(job.JobId, "sess-1", 1)).Job!;

            Assert.Equal(JobState.StoppedAtStub, resumed.State);
            Assert.Contains("declined", resumed.TerminalReason!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("the words exist", resumed.TerminalReason!);
            Assert.False(resumed.Obligation("words-exist")!.Met);
        }

        [Fact]
        public async Task Cancelling_the_job_cancels_its_queued_request()
        {
            var q = Queue();
            var runner = new InMemoryJobRunner(new OneNeedCatalogue(), new NeedsWords(), new Evidence(), new AllGranted(), q);
            var job = (await runner.StartAsync(new JobStartRequest { Family = "author" }, "sess-1", 1)).Job!;
            await runner.NextStepAsync(new JobStepSubmission { JobId = job.JobId, SessionId = "sess-1", SessionGeneration = 1 });
            Assert.Single(await q.ListAsync("sess-1"));

            await runner.CancelAsync(job.JobId, "Kane clicked stop", "sess-1", 1);

            Assert.Empty(await q.ListAsync("sess-1"));
            Assert.Equal(AssistantRequestState.Cancelled, (await q.ListAsync("sess-1", includeClosed: true)).Single().State);
        }

        [Fact]
        public async Task A_completed_request_resumes_the_job_and_the_words_prove_nothing_on_their_own()
        {
            var q = Queue();
            var runner = new InMemoryJobRunner(new OneNeedCatalogue(), new NeedsWords(), new Evidence(), new AllGranted(), q);
            var job = (await runner.StartAsync(new JobStartRequest { Family = "author" }, "sess-1", 1)).Job!;
            await runner.NextStepAsync(new JobStepSubmission { JobId = job.JobId, SessionId = "sess-1", SessionGeneration = 1 });

            var pending = (await q.ListAsync("sess-1")).Single();
            var claim = await q.ClaimAsync(pending.RequestId, "claude");
            await q.CompleteAsync(pending.RequestId, claim.Request!.ClaimToken, new AssistantAnswer { Text = "Margin as a share of sales." });

            var resumed = (await runner.ResumeAsync(job.JobId, "sess-1", 1)).Job!;
            Assert.Equal(JobState.Running, resumed.State);
            Assert.False(resumed.Obligation("words-exist")!.Met);   // the reply is an input, not proof

            var after = (await runner.NextStepAsync(new JobStepSubmission
            {
                JobId = job.JobId, SessionId = "sess-1", SessionGeneration = 1,
            })).Job!;

            Assert.True(after.Obligation("words-exist")!.Met);      // the ENGINE result ticked it
            Assert.Equal(JobState.Completed, after.State);
        }

        // ------------------------------------------- presence: the engine's own composition (Astra B8)
        //
        // Plan at the heart (lane E3, contract 3.2 and the AMENDS in assistant-handoff.md) changed what presence is
        // FOR. The engine's queue is a view over the Plan's tasks, so a request always has an owner record and a post
        // waits for an assistant instead of stopping a job at a stub. Presence is still answered, for the doors that
        // ask it; it no longer gates a post.

        private static LocalEngine Engine(SessionManager sessions) =>
            new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());

        [Fact]
        public void Presence_is_answered_from_the_mcp_door_and_from_agent_role_pipe_clients()
        {
            using var sessions = new SessionManager();
            using var engine = Engine(sessions);
            var agentClients = 0;
            engine.ObserveAgentClients(() => agentClients);
            Assert.False(engine.AssistantPresence.IsAssistantAttached);

            using (engine.AttachAssistantDoor())        // the MCP door: its stdio peer IS the assistant
                Assert.True(engine.AssistantPresence.IsAssistantAttached);
            Assert.False(engine.AssistantPresence.IsAssistantAttached);

            agentClients = 1;                            // the MCP proxy attached over the named pipe
            Assert.True(engine.AssistantPresence.IsAssistantAttached);
            agentClients = 0;
            Assert.False(engine.AssistantPresence.IsAssistantAttached);
        }

        // ------------------------------------------- the plan-backed queue (Plan at the heart, lane E3)

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_post_with_no_assistant_attached_is_kept_as_a_task_on_the_plan()
        {
            using var f = await PlanQueueKit.GappyAsync("HandoffKept.bim");
            Assert.False(f.Engine.AssistantPresence.IsAssistantAttached);
            Assert.IsType<PlanTaskQueue>(f.Engine.AssistantRequests);

            var posted = await Post(f.Engine.AssistantRequests);
            Assert.True(posted.Ok, posted.Message);
            Assert.Matches("^pt-[0-9a-f]{8}$", posted.Request!.RequestId);   // the owner record's own id

            var row = PlanQueueKit.Row(await f.Engine.GetModelPlanAsync(), posted.Request.RequestId);
            Assert.Equal(PlanSources.Workflow, row.Source);
            Assert.Equal(PlanDoers.Assistant, row.Doer);
            Assert.Equal(PlanOriginKinds.Draft, row.OriginLink.Kind);
            Assert.Equal("job-1", row.OriginLink.Ref);
            Assert.Contains(await f.Engine.AssistantRequests.ListAsync("sess-1"), r => r.RequestId == posted.Request.RequestId);

            Assert.Equal(1, await f.Engine.AssistantRequests.CancelForJobAsync("job-1", "The job was cancelled."));
            Assert.Equal(PlanStateWords.SetAside, PlanQueueKit.Row(await f.Engine.GetModelPlanAsync(), posted.Request.RequestId).StateWord);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_claim_is_refused_while_paused_and_while_waiting_each_with_its_sentence()
        {
            using var f = await PlanQueueKit.GappyAsync("HandoffRefused.bim");
            var (_, obj) = PlanQueueKit.SemanticusRowWithObject(await f.Engine.GetModelPlanAsync());
            var waits = await PlanQueueKit.AskAsync(f.Engine, "Describe that object", new[] { obj });
            var ready = await PlanQueueKit.AskAsync(f.Engine, "Describe the Margin measure", PlanQueueKit.Free("Margin"));

            var notReady = await f.Engine.ClaimAssistantRequestAsync(waits);
            Assert.Equal(HandoffRefusal.NotReady, notReady.Refusal);
            Assert.StartsWith("This task is not ready yet. It waits for ", notReady.Message);

            await f.Engine.SetAssistantQueueAsync("paused");
            var paused = await f.Engine.ClaimAssistantRequestAsync(ready);
            Assert.Equal(HandoffRefusal.QueuePaused, paused.Refusal);
            Assert.Equal("The person has paused tasks for you. Carry on with what they ask you directly.", paused.Message);

            await f.Engine.SetAssistantQueueAsync("on");
            await f.Engine.HoldPlanItemAsync(ready, true);
            var held = await f.Engine.ClaimAssistantRequestAsync(ready);
            Assert.Equal(HandoffRefusal.NotReady, held.Refusal);
            Assert.Contains("paused this task", held.Message);

            await f.Engine.HoldPlanItemAsync(ready, false);
            Assert.True((await f.Engine.ClaimAssistantRequestAsync(ready)).Ok);
            Assert.Equal(HandoffRefusal.NotFound, (await f.Engine.ClaimAssistantRequestAsync("pt-00000000")).Refusal);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_claim_survives_a_request_from_a_second_client()
        {
            using var f = await PlanQueueKit.GappyAsync("HandoffSecond.bim");
            var id = await PlanQueueKit.AskAsync(f.Engine, "Describe the Margin measure", PlanQueueKit.Free("Margin"));

            var first = await f.Engine.AssistantRequests.ClaimAsync(id, "rpc:1");
            Assert.True(first.Ok, first.Message);
            var second = await f.Engine.AssistantRequests.ClaimAsync(id, "rpc:2");
            Assert.Equal(HandoffRefusal.AlreadyClaimed, second.Refusal);
            Assert.Null(second.Request!.ClaimToken);

            Assert.Equal(HandoffRefusal.WrongClaimToken,
                (await f.Engine.AssistantRequests.CompleteAsync(id, "ct-guess", new AssistantAnswer { Text = "x" })).Refusal);
            var row = PlanQueueKit.Row(await f.Engine.GetModelPlanAsync(), id);
            Assert.Equal(PlanStateWords.OnIt, row.StateWord);

            var done = await f.Engine.AssistantRequests.CompleteAsync(id, first.Request!.ClaimToken, new AssistantAnswer { Text = "Described." });
            Assert.True(done.Ok, done.Message);
            Assert.Equal(AssistantRequestState.Completed, done.Request!.State);
            // Said done is State = done on the task (E1's contract gap 5), and an assistant_says task reads Done.
            row = PlanQueueKit.Row(await f.Engine.GetModelPlanAsync(), id);
            Assert.Equal(PlanStateWords.Done, row.StateWord);
            Assert.Equal("Described.", row.Reply);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_restart_releases_a_claim_with_the_restart_line()
        {
            using var f = await PlanQueueKit.GappyAsync("HandoffRestart.bim");
            var id = await PlanQueueKit.AskAsync(f.Engine, "Describe the Margin measure", PlanQueueKit.Free("Margin"));
            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.True(claim.Ok, claim.Message);

            // A restart: the engine that made the claim is gone, and a new engine over the same workspace opens the same
            // file. Gone is what releases the claim, not a different run id (Codex on pull request 393, finding 4).
            f.Engine.Dispose();
            using var sessions = new SessionManager();
            using var again = new LocalEngine(sessions, TestEntitlements.Free, f.Workspace);
            await again.OpenAsync(System.IO.Path.Combine(f.Workspace, "HandoffRestart.bim"));
            Assert.NotEqual(f.Engine.EngineRunId, again.EngineRunId);

            var row = PlanQueueKit.Row(await again.GetModelPlanAsync(), id);
            Assert.Null(row.Claim);
            Assert.Equal(PlanStateWords.Ready, row.StateWord);
            Assert.Equal("Semanticus restarted while your assistant had this. It is offered again.", row.StateNote);
            Assert.Contains((await again.ListAssistantRequestsAsync()).Requests, r => r.RequestId == id);

            // The old token died with the claim.
            Assert.Equal(HandoffRefusal.NotClaimed, (await again.CompleteAssistantRequestAsync(id, claim.Request!.ClaimToken, "x")).Refusal);
            var reclaimed = await again.ClaimAssistantRequestAsync(id);
            Assert.True(reclaimed.Ok, reclaimed.Message);
            Assert.Equal(PlanTasks.SinceLastCallNote, PlanQueueKit.Row(await again.GetModelPlanAsync(), id).StateNote);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_claim_token_is_stored_hashed_and_never_in_clear()
        {
            using var f = await PlanQueueKit.GappyAsync("HandoffHash.bim");
            var id = await PlanQueueKit.AskAsync(f.Engine, "Describe the Margin measure", PlanQueueKit.Free("Margin"));
            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            var token = claim.Request!.ClaimToken;
            Assert.StartsWith("ct-", token);

            var files = System.IO.Directory.GetFiles(f.Workspace, PlanMemory.FileName, System.IO.SearchOption.AllDirectories);
            var text = string.Concat(files.Select(System.IO.File.ReadAllText));
            Assert.NotEmpty(files);
            Assert.DoesNotContain(token, text);
            Assert.Contains(LocalEngine.HashToken(token), text);

            // Nor on any list or plan read.
            Assert.All((await f.Engine.ListAssistantRequestsAsync()).Held, r => Assert.Null(r.ClaimToken));
            Assert.DoesNotContain(token, System.Text.Json.JsonSerializer.Serialize(await f.Engine.GetModelPlanAsync()));
        }

        // ------------------------------------------- Codex on pull request 393

        private static async Task<(JobView Job, string RequestId)> SuspendedJobAsync(InMemoryJobRunner runner, IAssistantRequestQueue q)
        {
            var job = (await runner.StartAsync(new JobStartRequest { Family = "author" }, "sess-1", 1)).Job!;
            var suspended = (await runner.NextStepAsync(new JobStepSubmission { JobId = job.JobId, SessionId = "sess-1", SessionGeneration = 1 })).Job!;
            Assert.Equal(JobState.AwaitingAssistant, suspended.State);
            var open = (await q.ListAsync("sess-1")).Where(r => r.Need == "a description for measure Sales[Margin]").ToList();
            return (suspended, Assert.Single(open).RequestId);
        }

        [Fact]
        public async Task A_job_on_the_plan_queue_reads_the_answer_and_the_decline_after_the_request_closed()
        {
            // Finding 1 (PlanTaskQueue.cs:270): ListAsync ignored includeClosed, so ResumeAsync found neither an answered
            // nor a declined request and stopped every such job at the "is gone" stub.
            using var f = await PlanQueueKit.GappyAsync("HandoffResume.bim");
            var q = f.Engine.AssistantRequests;
            var runner = new InMemoryJobRunner(new OneNeedCatalogue(), new NeedsWords(), new Evidence(), new AllGranted(), q);

            var (answered, a) = await SuspendedJobAsync(runner, q);
            var claim = await q.ClaimAsync(a, "claude");
            Assert.True(claim.Ok, claim.Message);
            Assert.True((await q.CompleteAsync(a, claim.Request!.ClaimToken, new AssistantAnswer { Text = "Margin as a share of sales." })).Ok);
            var closed = (await q.ListAsync("sess-1", includeClosed: true)).Single(r => r.RequestId == a);
            Assert.Equal(AssistantRequestState.Completed, closed.State);
            Assert.Equal("Margin as a share of sales.", closed.Answer);
            Assert.Null(closed.ClaimToken);

            var resumed = (await runner.ResumeAsync(answered.JobId, "sess-1", 1)).Job!;
            Assert.Equal(JobState.Running, resumed.State);
            var after = (await runner.NextStepAsync(new JobStepSubmission { JobId = answered.JobId, SessionId = "sess-1", SessionGeneration = 1 })).Job!;
            Assert.Equal(JobState.Completed, after.State);

            var (declined, d) = await SuspendedJobAsync(runner, q);
            var second = await q.ClaimAsync(d, "claude");
            Assert.True((await q.DeclineAsync(d, second.Request!.ClaimToken, "I cannot see the expression.")).Ok);
            var back = (await q.ListAsync("sess-1", includeClosed: true)).Single(r => r.RequestId == d);
            Assert.Equal(AssistantRequestState.Declined, back.State);
            Assert.Equal("I cannot see the expression.", back.DeclineReason);

            var stopped = (await runner.ResumeAsync(declined.JobId, "sess-1", 1)).Job!;
            Assert.Equal(JobState.StoppedAtStub, stopped.State);
            Assert.Contains("declined", stopped.TerminalReason!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("I cannot see the expression.", stopped.TerminalReason!);

            // Without the flag the list is still only what is open.
            Assert.DoesNotContain(await q.ListAsync("sess-1"), r => r.RequestId == a || r.RequestId == d);
        }

        [Fact]
        public async Task A_second_engine_on_the_model_leaves_a_live_engines_claim_and_releases_it_once_that_engine_is_gone()
        {
            // Finding 4 (LocalEngine.PlanQueue.cs:468): a claim whose run id was not this engine's was released on read,
            // so a second workspace on the same model took a live assistant's task away and could hand it out again.
            using var f = await PlanQueueKit.GappyAsync("HandoffTwoEngines.bim");
            var id = await PlanQueueKit.AskAsync(f.Engine, "Describe the Margin measure", PlanQueueKit.Free("Margin"));
            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.True(claim.Ok, claim.Message);

            using var sessions = new SessionManager();
            using var b = new LocalEngine(sessions, TestEntitlements.Free, f.Workspace);
            await b.OpenAsync(System.IO.Path.Combine(f.Workspace, "HandoffTwoEngines.bim"));
            Assert.NotEqual(f.Engine.EngineRunId, b.EngineRunId);

            var row = PlanQueueKit.Row(await b.GetModelPlanAsync(), id);
            Assert.NotNull(row.Claim);
            Assert.Equal(PlanStateWords.OnIt, row.StateWord);
            Assert.DoesNotContain((await b.ListAssistantRequestsAsync()).Requests, r => r.RequestId == id);
            Assert.Equal(HandoffRefusal.AlreadyClaimed, (await b.ClaimAssistantRequestAsync(id)).Refusal);
            Assert.Equal(PlanStateWords.OnIt, PlanQueueKit.Row(await f.Engine.GetModelPlanAsync(), id).StateWord);

            // A goes away (its process ended): now the claim is released, with the restart line.
            f.Engine.Dispose();
            row = PlanQueueKit.Row(await b.GetModelPlanAsync(), id);
            Assert.Null(row.Claim);
            Assert.Equal(PlanStateWords.Ready, row.StateWord);
            Assert.Equal(LocalEngine.RestartSentence, row.StateNote);
            Assert.True((await b.ClaimAssistantRequestAsync(id)).Ok);
        }
    }
}
