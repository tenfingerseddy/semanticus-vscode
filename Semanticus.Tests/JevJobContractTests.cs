using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Semanticus.Engine.Jev;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// M0a contract 1, the JOB CONTRACT (docs/redesign/contracts/job-contract.md), pinned before the real
    /// runner exists. Every assertion here is a rule a later lane must keep when it moves jobs onto
    /// <c>WorkflowRunState</c> and <c>WorkflowRunner</c>.
    ///
    /// The five that matter most, in Astra's words: authoritative outcomes stay in the engine; next_step must
    /// not accept caller-authored "verified" flags as proof; a step whose inputs changed is re-offered so a
    /// permanently shrinking catalogue cannot suppress a needed recheck; a stale session generation is refused;
    /// and done is never reported while a declared obligation is unmet.
    /// </summary>
    public sealed class JevJobContractTests
    {
        private const string Family = "author";

        // ---------------------------------------------------------------- fakes

        private sealed class FakeCatalogue : IStepCatalogue
        {
            public string Version { get; set; } = "cat-1";
            public List<StepDefinition> Steps { get; } = new List<StepDefinition>();
            public List<JobObligation> Obligations { get; } = new List<JobObligation>();
            public IReadOnlyList<StepDefinition> For(string family) => Steps;
            public IReadOnlyList<JobObligation> ObligationsFor(string family) => Obligations;
        }

        private sealed class FakeEvidence : IJobEvidenceSource
        {
            public long ModelRevision = 7;
            public string Source = "src-1";
            public string Snapshot = "snap-1";
            public string Catalogue = "cat-1";
            public Dictionary<string, string> Digests { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
            public JobEvidence Current() => new JobEvidence(ModelRevision, Source, Snapshot, Catalogue);
            public string InputDigest(string key) => Digests.TryGetValue(key, out var v) ? v : "d0";
        }

        private sealed class FakeSessions : ISessionGenerationSource
        {
            public string SessionId { get; set; } = "sess-1";
            public long Generation { get; set; } = 1;
        }

        private sealed class FakeGrants : IFeatureGrantsView
        {
            public HashSet<ProFeature> Granted { get; } = new HashSet<ProFeature>();
            public bool Grants(ProFeature feature) => Granted.Contains(feature);
        }

        private sealed class FakeExecutor : IJobStepExecutor
        {
            public Func<IJob, StepDefinition, IReadOnlyDictionary<string, string>, EngineStepResult> Handler { get; set; }
            /// <summary>For the interleaving tests: an executor that can be held open mid-step.</summary>
            public Func<IJob, StepDefinition, IReadOnlyDictionary<string, string>, CancellationToken, Task<EngineStepResult>> AsyncHandler { get; set; }
            private readonly List<string> _ran = new List<string>();
            /// <summary>A copy, and the adds are locked, because these tests run two calls at once on purpose.</summary>
            public IReadOnlyList<string> Ran { get { lock (_ran) return _ran.ToArray(); } }

            public Task<EngineStepResult> ExecuteAsync(IJob job, StepDefinition step,
                IReadOnlyDictionary<string, string> answers, CancellationToken ct)
            {
                lock (_ran) _ran.Add(step.Id);
                if (AsyncHandler != null) return AsyncHandler(job, step, answers, ct);
                return Task.FromResult(Handler(job, step, answers));
            }
        }

        /// <summary>A chooser the test can hold open, so a cancel can land while the job is waiting on it.</summary>
        private sealed class BlockingChooser : IJobStepChooser
        {
            private readonly TaskCompletionSource<bool> _entered;
            private readonly TaskCompletionSource<bool> _release;

            public BlockingChooser(TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release)
            {
                _entered = entered;
                _release = release;
            }

            public async Task<string> ChooseAsync(IJob job, IReadOnlyList<StepOffer> offers, CancellationToken ct)
            {
                _entered.TrySetResult(true);
                await _release.Task.ConfigureAwait(false);
                return offers.Count > 0 ? offers[0].StepId : null;
            }
        }

        private sealed class Fixture
        {
            public FakeCatalogue Catalogue = new FakeCatalogue();
            public FakeEvidence Evidence = new FakeEvidence();
            public FakeGrants Grants = new FakeGrants();
            public FakeExecutor Executor = new FakeExecutor();
            public InMemoryAssistantRequestQueue Queue = new InMemoryAssistantRequestQueue();
            public InMemoryJobRunner Runner = null!;
            public string SessionId = "sess-1";
            public long Generation = 1;

            public Fixture Build()
            {
                Runner = new InMemoryJobRunner(Catalogue, Executor, Evidence, Grants, Queue);
                return this;
            }

            public Task<JobResult> Start() =>
                Runner.StartAsync(new JobStartRequest { Family = Family, Intent = "a margin measure" }, SessionId, Generation);

            public Task<JobResult> Next(string jobId, string stepId = null,
                Dictionary<string, string> claims = null, Dictionary<string, string> answers = null) =>
                Runner.NextStepAsync(new JobStepSubmission
                {
                    JobId = jobId,
                    StepId = stepId,
                    SessionId = SessionId,
                    SessionGeneration = Generation,
                    CallerClaims = claims ?? new Dictionary<string, string>(),
                    Answers = answers ?? new Dictionary<string, string>(),
                });
        }

        private static EngineStepResult Passing(params string[] obligations) => new EngineStepResult
        {
            Ok = true, ObligationsMet = obligations, EvidenceDigest = "ev-" + Guid.NewGuid().ToString("N").Substring(0, 6),
        };

        private static Fixture OneStepJob(EngineStepResult result, params string[] requiresInputs)
        {
            var f = new Fixture();
            f.Catalogue.Steps.Add(new StepDefinition
            {
                Id = "write-expression", Title = "Write the expression", Op = "create_measure",
                Provides = new[] { "measure-exists" }, RequiresInputs = requiresInputs,
            });
            f.Catalogue.Obligations.Add(new JobObligation("measure-exists", "the measure exists"));
            f.Executor.Handler = (j, s, a) => result;
            return f.Build();
        }

        // ---------------------------------------------------------------- the rules

        [Fact]
        public async Task A_caller_supplied_verified_flag_is_recorded_and_never_ticks_an_obligation()
        {
            // The engine's own result says NOTHING was proved. The caller says it verified the work itself.
            var f = OneStepJob(new EngineStepResult { Ok = true, ObligationsMet = Array.Empty<string>(), Note = "wrote it" });
            var job = (await f.Start()).Job!;

            var after = (await f.Next(job.JobId, claims: new Dictionary<string, string>
            {
                ["verified"] = "true",
                ["equivalence"] = "proven",
            })).Job!;

            Assert.False(after.Obligation("measure-exists")!.Met);
            Assert.NotEqual(JobState.Completed, after.State);
            // Recorded verbatim, so a reviewer can see the claim was made and was not acted on.
            Assert.Equal("true", after.Steps.Single().CallerClaims["verified"]);
            Assert.Empty(after.Steps.Single().ObligationsMet);
        }

        [Fact]
        public async Task An_obligation_ticks_only_from_an_engine_result_and_only_what_the_catalogue_allows()
        {
            var f = OneStepJob(Passing("measure-exists", "validated"));   // "validated" is NOT in Provides
            f.Catalogue.Obligations.Add(new JobObligation("validated", "the expression validates"));
            var job = (await f.Start()).Job!;

            var after = (await f.Next(job.JobId)).Job!;

            Assert.True(after.Obligation("measure-exists")!.Met);
            Assert.Equal("create_measure", after.Obligation("measure-exists")!.EvidenceOp);
            Assert.NotEqual("", after.Obligation("measure-exists")!.EvidenceDigest);
            // The executor claimed an obligation this step was never declared able to prove. Intersected away.
            Assert.False(after.Obligation("validated")!.Met);
        }

        [Fact]
        public async Task A_failed_engine_result_ticks_nothing()
        {
            var f = OneStepJob(new EngineStepResult { Ok = false, Note = "invalid DAX", ObligationsMet = new[] { "measure-exists" } });
            var job = (await f.Start()).Job!;

            var after = (await f.Next(job.JobId)).Job!;

            Assert.False(after.Obligation("measure-exists")!.Met);
            Assert.NotEqual(JobState.Completed, after.State);
        }

        [Fact]
        public async Task A_step_whose_inputs_changed_is_re_offered()
        {
            var f = OneStepJob(Passing("measure-exists"), "model:expression");
            f.Evidence.Digests["model:expression"] = "d1";
            var job = (await f.Start()).Job!;

            var afterFirst = (await f.Next(job.JobId)).Job!;
            Assert.True(afterFirst.Obligation("measure-exists")!.Met);
            Assert.Equal(JobState.Completed, afterFirst.State);

            // A second actor edits the expression. Re-open the same shape of job and prove the offer comes back.
            var g = OneStepJob(new EngineStepResult { Ok = true, ObligationsMet = Array.Empty<string>() }, "model:expression");
            g.Evidence.Digests["model:expression"] = "d1";
            var job2 = (await g.Start()).Job!;
            var once = (await g.Next(job2.JobId)).Job!;
            Assert.DoesNotContain(once.Offers, o => o.StepId == "write-expression");   // done, inputs unchanged

            g.Evidence.Digests["model:expression"] = "d2";
            var reread = (await g.Runner.GetAsync(job2.JobId)).Job!;
            var offer = Assert.Single(reread.Offers.Where(o => o.StepId == "write-expression"));
            Assert.Equal("inputs changed", offer.Why);
        }

        [Fact]
        public async Task A_stale_session_generation_is_refused_before_anything_is_read_or_written()
        {
            var f = OneStepJob(Passing("measure-exists"));
            var job = (await f.Start()).Job!;

            var refused = await f.Runner.NextStepAsync(new JobStepSubmission
            {
                JobId = job.JobId, SessionId = f.SessionId, SessionGeneration = f.Generation + 1,
            });

            Assert.Equal(JobRefusal.StaleSession, refused.Refusal);
            Assert.Empty(f.Executor.Ran);                       // the executor never ran
            Assert.Equal(JobState.Running, refused.Job!.State); // and the job did not move

            var wrongSession = await f.Runner.NextStepAsync(new JobStepSubmission
            {
                JobId = job.JobId, SessionId = "sess-2", SessionGeneration = f.Generation,
            });
            Assert.Equal(JobRefusal.StaleSession, wrongSession.Refusal);
        }

        [Fact]
        public async Task A_generation_change_sweeps_every_in_flight_job_so_none_is_orphaned()
        {
            var f = OneStepJob(Passing("measure-exists"));
            var job = (await f.Start()).Job!;

            var swept = f.Runner.OnSessionGenerationChanged("sess-1", 2);

            Assert.Equal(1, swept);
            var now = (await f.Runner.GetAsync(job.JobId)).Job!;
            Assert.Equal(JobState.Cancelled, now.State);
            Assert.Contains("the open model changed", now.TerminalReason!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Cancel_works_from_every_state_and_is_idempotent_once_terminal()
        {
            foreach (var state in new[] { JobState.Running, JobState.AwaitingAssistant, JobState.Completed,
                                          JobState.StoppedAtStub, JobState.Cancelled, JobState.Failed })
            {
                var f = await JobInState(state);
                var before = (await f.Runner.GetAsync(f.JobId)).Job!;
                Assert.Equal(state, before.State);

                var result = await f.Fixture.Runner.CancelAsync(f.JobId, "Kane clicked stop", f.Fixture.SessionId, f.Fixture.Generation);
                var after = (await f.Runner.GetAsync(f.JobId)).Job!;

                Assert.True(JobStates.IsTerminal(after.State));
                if (JobStates.IsTerminal(state))
                {
                    // Already finished: the cancel says so and changes nothing, including the reason.
                    Assert.Equal(JobRefusal.AlreadyTerminal, result.Refusal);
                    Assert.Equal(state, after.State);
                    Assert.Equal(before.TerminalReason, after.TerminalReason);
                }
                else
                {
                    Assert.True(result.Ok);
                    Assert.Equal(JobState.Cancelled, after.State);
                    Assert.Equal("Kane clicked stop", after.TerminalReason);
                }
            }
        }

        [Fact]
        public async Task Resume_after_a_suspended_handoff_re_offers_the_step_that_needed_the_words()
        {
            var f = HandoffJob();
            var job = (await f.Start()).Job!;

            var suspended = (await f.Next(job.JobId, "ask-for-words")).Job!;
            Assert.Equal(JobState.AwaitingAssistant, suspended.State);
            Assert.NotNull(suspended.PendingRequestId);

            // Resuming while the request is still open is refused rather than guessed at.
            var early = await f.Runner.ResumeAsync(job.JobId, f.SessionId, f.Generation);
            Assert.Equal(JobRefusal.RequestStillPending, early.Refusal);

            var claim = await f.Queue.ClaimAsync(suspended.PendingRequestId!, "claude");
            await f.Queue.CompleteAsync(suspended.PendingRequestId!, claim.Request!.ClaimToken, new AssistantAnswer { Text = "Margin as a share of sales." });

            var resumed = (await f.Runner.ResumeAsync(job.JobId, f.SessionId, f.Generation)).Job!;
            Assert.Equal(JobState.Running, resumed.State);
            Assert.Null(resumed.PendingRequestId);
            // The words are an INPUT, not proof: the step that needed them is offered again.
            Assert.Contains(resumed.Offers, o => o.StepId == "ask-for-words" && o.Why == "inputs changed");
            Assert.False(resumed.Obligation("words-exist")!.Met);
        }

        [Fact]
        public async Task With_no_assistant_the_job_stops_at_a_valid_stub_and_names_the_need()
        {
            var f = HandoffJob(consentOff: true);
            var job = (await f.Start()).Job!;

            var after = (await f.Next(job.JobId, "ask-for-words")).Job!;

            Assert.Equal(JobState.StoppedAtStub, after.State);
            Assert.Contains("a description", after.TerminalReason!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("the words exist", after.TerminalReason!, StringComparison.OrdinalIgnoreCase);
            Assert.False(after.Obligation("words-exist")!.Met);
        }

        [Fact]
        public async Task A_denied_step_is_named_rather_than_skipped_quietly_and_done_is_never_reported()
        {
            var f = new Fixture();
            f.Catalogue.Steps.Add(new StepDefinition
            {
                // Marking a date table is free since 22 September, so the step that proves a DENIED action is
                // one of the two things still paid. The test is about how a denial reads, not about which op.
                Id = "start-workflow", Title = "Start the workflow", Op = "start_workflow",
                RequiresFeature = ProFeature.Workflows, Provides = new[] { "date-table-marked" },
            });
            f.Catalogue.Obligations.Add(new JobObligation("date-table-marked", "the date table is marked"));
            f.Executor.Handler = (j, s, a) => Passing("date-table-marked");
            f.Build();                                  // grants are EMPTY: a free session

            var job = (await f.Start()).Job!;
            Assert.Empty(job.AllowedActions);
            var denied = Assert.Single(job.DeniedActions);
            Assert.Equal("start-workflow", denied.StepId);
            Assert.Equal(nameof(ProFeature.Workflows), denied.RequiresFeature);

            var after = (await f.Next(job.JobId)).Job!;
            Assert.Equal(JobState.StoppedAtStub, after.State);
            Assert.Contains("the date table is marked", after.TerminalReason!);
            Assert.Empty(f.Executor.Ran);
        }

        [Fact]
        public async Task The_step_cap_stops_at_a_stub_and_never_reports_done()
        {
            var f = new Fixture();
            for (var i = 0; i < 12; i++)
                f.Catalogue.Steps.Add(new StepDefinition
                {
                    Id = "s" + i, Title = "Step " + i, Op = "op" + i, Provides = Array.Empty<string>(),
                });
            f.Catalogue.Obligations.Add(new JobObligation("measure-exists", "the measure exists"));
            f.Executor.Handler = (j, s, a) => new EngineStepResult { Ok = true, ObligationsMet = Array.Empty<string>() };
            f.Build();
            f.Runner.StepCap = 3;

            var job = (await f.Start()).Job!;
            JobView last = job;
            for (var i = 0; i < 6; i++) last = (await f.Next(job.JobId)).Job!;

            Assert.Equal(JobState.StoppedAtStub, last.State);
            Assert.Equal(3, last.Progress.StepsTaken);
            Assert.Contains("3 step limit", last.TerminalReason!);
            Assert.Contains("the measure exists", last.TerminalReason!);
        }

        [Fact]
        public async Task A_catalogue_version_change_refuses_the_job_rather_than_carrying_on()
        {
            var f = OneStepJob(Passing("measure-exists"));
            var job = (await f.Start()).Job!;

            f.Evidence.Catalogue = "cat-2";
            var refused = await f.Next(job.JobId);

            Assert.Equal(JobRefusal.EvidenceMoved, refused.Refusal);
            Assert.Empty(f.Executor.Ran);
        }

        [Fact]
        public async Task A_moved_model_revision_re_binds_the_job_rather_than_killing_it()
        {
            // The difference from the catalogue case above, and the reason it is a separate rule: a model
            // revision moves constantly, so treating it as fatal would make every job unusable.
            var f = OneStepJob(Passing("measure-exists"));
            var job = (await f.Start()).Job!;
            Assert.Equal(7, job.Evidence.ModelRevision);

            f.Evidence.ModelRevision = 9;
            var after = (await f.Next(job.JobId)).Job!;

            Assert.Equal(9, after.Evidence.ModelRevision);
            Assert.Equal(JobState.Completed, after.State);
        }

        [Fact]
        public async Task The_manual_step_choice_and_the_chooser_reach_the_same_operation()
        {
            var f = new Fixture();
            f.Catalogue.Steps.Add(new StepDefinition { Id = "a", Title = "A", Op = "op_a", Provides = new[] { "o" } });
            f.Catalogue.Steps.Add(new StepDefinition { Id = "b", Title = "B", Op = "op_b", Provides = new[] { "o" } });
            f.Catalogue.Obligations.Add(new JobObligation("o", "the thing exists"));
            f.Executor.Handler = (j, s, a) => Passing("o");
            f.Build();

            // Named step: the Studio manual control and the MCP argument are the same field.
            var manual = (await f.Start()).Job!;
            var afterManual = (await f.Next(manual.JobId, "b")).Job!;
            Assert.Equal("b", afterManual.Steps.Single().StepId);

            // No step named: the chooser picks, and the default chooser needs no provider.
            var chosen = (await f.Start()).Job!;
            var afterChosen = (await f.Next(chosen.JobId)).Job!;
            Assert.Equal("a", afterChosen.Steps.Single().StepId);
        }





        [Fact]
        public async Task The_engine_owns_the_generation_and_a_caller_cannot_claim_one_it_does_not_have()
        {
            // Astra's closing recommendation: ownership belongs at the context exchange, and the job carries
            // the value captured there. With a source present the CALLER's claim is checked against the
            // engine's answer, at start and on every later call, rather than believed.
            var f = OneStepJob(Passing("measure-exists"));
            var sessions = new FakeSessions { SessionId = "sess-1", Generation = 1 };
            var runner = new InMemoryJobRunner(f.Catalogue, f.Executor, f.Evidence, f.Grants, f.Queue, null, sessions);

            var started = await runner.StartAsync(new JobStartRequest { Family = Family, Intent = "a margin measure" }, "sess-1", 1);
            Assert.True(started.Ok);
            Assert.Equal(1, started.Job!.OwnerSessionGeneration);

            var invented = await runner.StartAsync(new JobStartRequest { Family = Family, Intent = "x" }, "sess-1", 99);
            Assert.Equal(JobRefusal.StaleSession, invented.Refusal);
            Assert.Null(invented.Job);                            // no job was created at all

            // The model is exchanged. NOTHING calls the sweep. The job is stale anyway, because what the
            // caller's handle is compared against is the engine's live answer.
            sessions.Generation = 2;
            var refused = await runner.NextStepAsync(new JobStepSubmission
            {
                JobId = started.Job.JobId, SessionId = "sess-1", SessionGeneration = 1,
            });

            Assert.Equal(JobRefusal.StaleSession, refused.Refusal);
            Assert.Empty(f.Executor.Ran);
        }

        [Fact]
        public async Task The_session_manager_moves_the_generation_on_every_context_exchange_and_only_forward()
        {
            // SessionManager OWNS the generation, because its context exchange is the only place the open
            // model is actually swapped. Nothing open is generation zero; opening, replacing and closing each
            // move it by one, and it never goes back.
            using var sessions = new SessionManager();
            Assert.Equal(0, sessions.Generation);
            Assert.Null(sessions.SessionId);

            await sessions.CreateAsync("Jev generation A", 1604);           // opened
            Assert.Equal(1, sessions.Generation);
            var first = sessions.SessionId;
            Assert.NotNull(first);

            await sessions.CreateAsync("Jev generation B", 1604);           // replaced
            Assert.Equal(2, sessions.Generation);
            Assert.NotEqual(first, sessions.SessionId);

            sessions.ExchangeRetiredContext(new SessionContext(null)).Dispose();    // closed
            Assert.Equal(3, sessions.Generation);
            Assert.Null(sessions.SessionId);
        }

        [Fact]
        public async Task A_job_started_before_a_context_exchange_is_refused_after_it()
        {
            // The hook end to end, with the REAL owner: a job captures the manager's generation at start, the
            // open model is then exchanged, and the job's next call is refused before anything is read or run.
            using var sessions = new SessionManager();
            await sessions.CreateAsync("Jev context exchange", 1604);

            var f = OneStepJob(Passing("measure-exists"));
            var runner = new InMemoryJobRunner(f.Catalogue, f.Executor, f.Evidence, f.Grants, f.Queue, null, sessions);

            var ownerSession = sessions.SessionId;
            var ownerGeneration = sessions.Generation;
            var started = await runner.StartAsync(
                new JobStartRequest { Family = Family, Intent = "a margin measure" }, ownerSession, ownerGeneration);
            Assert.True(started.Ok);
            Assert.Equal(ownerGeneration, started.Job!.OwnerSessionGeneration);

            await sessions.CreateAsync("Jev context exchange, the second model", 1604);
            Assert.NotEqual(ownerGeneration, sessions.Generation);

            var refused = await runner.NextStepAsync(new JobStepSubmission
            {
                JobId = started.Job.JobId, SessionId = ownerSession, SessionGeneration = ownerGeneration,
            });

            Assert.Equal(JobRefusal.StaleSession, refused.Refusal);
            Assert.Empty(f.Executor.Ran);                    // nothing was read, chosen or run
            Assert.Empty(refused.Job!.Steps);
        }

        [Fact]
        public async Task Eight_live_jobs_is_the_cap_and_a_job_suspended_on_its_assistant_still_counts()
        {
            // Astra's closing recommendation: keep the cap at eight and count suspended jobs toward it. A job
            // waiting on an assistant is holding a slot, and pretending otherwise is how nine jobs become live
            // on a cap of eight.
            var f = OneStepJob(new EngineStepResult
            {
                Ok = true, AssistantNeed = "a description for measure Sales[Margin]",
            });
            Assert.Equal(8, f.Runner.LiveJobCap);

            var first = "";
            for (var i = 0; i < 8; i++)
            {
                var started = await f.Start();
                Assert.Equal(JobRefusal.None, started.Refusal);
                if (i == 0) first = started.Job!.JobId;
                var suspended = await f.Next(started.Job!.JobId);
                Assert.Equal(JobState.AwaitingAssistant, suspended.Job!.State);      // suspended, not terminal
            }

            var ninth = await f.Start();
            Assert.Equal(JobRefusal.TooManyLiveJobs, ninth.Refusal);
            Assert.Null(ninth.Job);                                                  // no job was created at all

            // Cancelling one frees exactly one slot, and only one.
            await f.Runner.CancelAsync(first, "Kane clicked stop", f.SessionId, f.Generation);
            Assert.True((await f.Start()).Ok);
            Assert.Equal(JobRefusal.TooManyLiveJobs, (await f.Start()).Refusal);
        }

        // ------------------------------------------- Astra B6: nothing happens after the job stops

        private static Fixture BlockingStepJob(TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release)
        {
            var f = new Fixture();
            f.Catalogue.Steps.Add(new StepDefinition
            {
                Id = "write-expression", Title = "Write the expression", Op = "create_measure",
                Provides = new[] { "measure-exists" },
            });
            f.Catalogue.Obligations.Add(new JobObligation("measure-exists", "the measure exists"));
            f.Executor.AsyncHandler = async (j, s, a, ct) =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return Passing("measure-exists");
            };
            return f.Build();
        }

        [Fact]
        public async Task Cancelling_while_the_chooser_is_still_thinking_stops_the_step_from_running_at_all()
        {
            // Astra B6: the chooser runs outside the lock, and the old code came back from that wait and went
            // straight on to execute without asking whether the job was still alive.
            var f = OneStepJob(Passing("measure-exists"));
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var start = await f.Runner.StartAsync(
                new JobStartRequest { Family = Family, Intent = "a margin measure", Chooser = new BlockingChooser(entered, release) },
                f.SessionId, f.Generation);
            var jobId = start.Job!.JobId;

            var inFlight = Task.Run(() => f.Next(jobId));
            await entered.Task;
            var cancelled = await f.Runner.CancelAsync(jobId, "Kane clicked stop", f.SessionId, f.Generation);
            Assert.True(cancelled.Ok);
            release.TrySetResult(true);
            var result = await inFlight;

            Assert.Empty(f.Executor.Ran);                       // the step never ran
            Assert.Equal(JobRefusal.AlreadyTerminal, result.Refusal);
            var now = (await f.Runner.GetAsync(jobId)).Job!;
            Assert.Equal(JobState.Cancelled, now.State);
            Assert.Equal("Kane clicked stop", now.TerminalReason);
            Assert.False(now.Obligation("measure-exists")!.Met);
        }

        [Fact]
        public async Task A_step_that_finishes_after_a_cancel_cannot_turn_the_job_back_into_done()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var f = BlockingStepJob(entered, release);
            var job = (await f.Start()).Job!;

            var inFlight = Task.Run(() => f.Next(job.JobId));
            await entered.Task;
            await f.Runner.CancelAsync(job.JobId, "Kane clicked stop", f.SessionId, f.Generation);
            release.TrySetResult(true);                          // the late success arrives
            var result = await inFlight;

            Assert.Equal(JobRefusal.AlreadyTerminal, result.Refusal);
            var now = (await f.Runner.GetAsync(job.JobId)).Job!;
            Assert.Equal(JobState.Cancelled, now.State);
            Assert.Equal("Kane clicked stop", now.TerminalReason);
            Assert.False(now.Obligation("measure-exists")!.Met);
            // The work DID happen, so it stays on the record, marked as not counted. Hiding it would be worse.
            Assert.Contains(now.Steps, st => st.StepId == "write-expression"
                && st.Note != null && st.Note.Contains("after the job stopped", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(now.Steps.Single().ObligationsMet);
        }

        [Fact]
        public async Task Two_next_step_calls_at_once_run_one_step_and_the_second_is_refused()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var f = BlockingStepJob(entered, release);
            var job = (await f.Start()).Job!;

            var first = Task.Run(() => f.Next(job.JobId));
            await entered.Task;
            var second = Task.Run(() => f.Next(job.JobId));
            // One active step per job means the second call comes straight back. The bounded wait is the point:
            // if it does NOT come back, it is inside the executor alongside the first.
            await Task.WhenAny(second, Task.Delay(250));
            release.TrySetResult(true);
            var a = await first;
            var b = await second;

            Assert.True(a.Ok);
            Assert.Equal(JobRefusal.StepAlreadyRunning, b.Refusal);
            Assert.Single(f.Executor.Ran);
            var now = (await f.Runner.GetAsync(job.JobId)).Job!;
            Assert.Single(now.Steps);
            Assert.Equal(1, now.Progress.StepsTaken);
        }

        [Fact]
        public async Task A_generation_change_during_a_step_stops_its_result_being_accepted()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var f = BlockingStepJob(entered, release);
            var job = (await f.Start()).Job!;

            var inFlight = Task.Run(() => f.Next(job.JobId));
            await entered.Task;
            f.Runner.OnSessionGenerationChanged("sess-1", 2);    // the open model was exchanged mid-step
            release.TrySetResult(true);
            var result = await inFlight;

            Assert.False(result.Ok);
            var now = (await f.Runner.GetAsync(job.JobId)).Job!;
            Assert.Equal(JobState.Cancelled, now.State);
            Assert.False(now.Obligation("measure-exists")!.Met);
        }

        // ---------------------------------------------------------------- Astra B5: proof goes stale

        [Fact]
        public async Task A_validation_stops_counting_once_the_thing_it_validated_changes()
        {
            // Astra B5: validate the expression, change the expression, then finish a DIFFERENT obligation. The
            // job used to report Completed on the strength of a validation that no longer applied to anything.
            var f = new Fixture();
            f.Catalogue.Steps.Add(new StepDefinition
            {
                Id = "validate", Title = "Check the expression", Op = "validate_dax",
                Provides = new[] { "validated" }, RequiresInputs = new[] { "model:expression" },
            });
            f.Catalogue.Steps.Add(new StepDefinition
            {
                Id = "document", Title = "Write the description", Op = "set_description",
                Provides = new[] { "documented" },
            });
            f.Catalogue.Obligations.Add(new JobObligation("validated", "the expression checks out"));
            f.Catalogue.Obligations.Add(new JobObligation("documented", "the measure is described"));
            f.Executor.Handler = (j, s, a) => s.Id == "validate" ? Passing("validated") : Passing("documented");
            f.Build();
            f.Evidence.Digests["model:expression"] = "d1";

            var job = (await f.Start()).Job!;

            var validated = (await f.Next(job.JobId, "validate")).Job!;
            Assert.True(validated.Obligation("validated")!.Met);
            Assert.Equal("validate", validated.Obligation("validated")!.ProvingStepId);
            Assert.Equal("d1", validated.Obligation("validated")!.ProvingInputs["model:expression"]);

            // Somebody edits the expression the validation was about.
            f.Evidence.Digests["model:expression"] = "d2";

            var documented = (await f.Next(job.JobId, "document")).Job!;

            Assert.True(documented.Obligation("documented")!.Met);
            Assert.False(documented.Obligation("validated")!.Met);      // the old proof stopped counting
            Assert.NotEqual(JobState.Completed, documented.State);
            Assert.Contains(documented.Offers, o => o.StepId == "validate" && o.Why == "inputs changed");

            // Completed is only reachable once the validation actually runs again.
            var revalidated = (await f.Next(job.JobId, "validate")).Job!;
            Assert.True(revalidated.Obligation("validated")!.Met);
            Assert.Equal(JobState.Completed, revalidated.State);
        }

        [Fact]
        public async Task A_stale_proof_is_already_stale_when_the_job_is_merely_read()
        {
            // get_job must not report a Met obligation the engine would refuse to count. Both doors read this
            // same view, so a lie here is a lie in the Studio panel and in the MCP result at once.
            var f = OneStepJob(Passing("measure-exists"), "model:expression");
            f.Evidence.Digests["model:expression"] = "d1";
            var job = (await f.Start()).Job!;

            var after = (await f.Next(job.JobId)).Job!;
            Assert.True(after.Obligation("measure-exists")!.Met);
            Assert.Equal(JobState.Completed, after.State);

            f.Evidence.Digests["model:expression"] = "d2";
            var reread = (await f.Runner.GetAsync(job.JobId)).Job!;

            // The job is terminal, so its state does not move. What must NOT happen is the view still claiming
            // the obligation is backed by evidence that has since changed.
            Assert.False(reread.Obligation("measure-exists")!.Met);
            Assert.Equal(0, reread.Progress.ObligationsMet);
        }

        // ---------------------------------------------------------------- Astra B8: presence, not permission

        [Fact]
        public async Task With_consent_on_and_no_assistant_connected_the_job_stops_at_the_stub_instead_of_waiting()
        {
            // Astra B8: the queue checked CONSENT and nothing else, so with consent on and nobody connected the
            // job posted a request and sat in AwaitingAssistant for a reply that could never arrive. Presence is
            // a separate question from permission, and the promised stop needs both answered.
            var f = HandoffJob(noAssistant: true);
            var job = (await f.Start()).Job!;

            var after = (await f.Next(job.JobId, "ask-for-words")).Job!;

            Assert.Equal(JobState.StoppedAtStub, after.State);
            Assert.NotEqual(JobState.AwaitingAssistant, after.State);
            Assert.Null(after.PendingRequestId);                       // nothing is waiting on a queue
            Assert.Empty(await f.Queue.ListAsync(f.SessionId, includeClosed: true));
            Assert.Contains("a description", after.TerminalReason!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no assistant", after.TerminalReason!, StringComparison.OrdinalIgnoreCase);
            Assert.False(after.Obligation("words-exist")!.Met);
        }

        [Fact]
        public async Task No_assistant_and_no_consent_are_different_refusals()
        {
            var noConsent = await new InMemoryAssistantRequestQueue(new AlwaysConsented(), new AssistantAssumedPresent())
                .PostAsync(new AssistantRequestPost { JobId = "job-1", SessionId = "sess-1", Need = "words" });
            Assert.True(noConsent.Ok);          // consent on, assistant there: the request is posted

            var absent = await new InMemoryAssistantRequestQueue(new AlwaysConsented(), new NoAssistantAttached())
                .PostAsync(new AssistantRequestPost { JobId = "job-1", SessionId = "sess-1", Need = "words" });
            Assert.Equal(HandoffRefusal.NoAssistantAttached, absent.Refusal);

            var off = new InMemoryAssistantRequestQueue(new AlwaysConsented(), new AssistantAssumedPresent());
            await off.WithdrawConsentAsync();
            var withdrawn = await off.PostAsync(new AssistantRequestPost { JobId = "job-1", SessionId = "sess-1", Need = "words" });
            Assert.Equal(HandoffRefusal.NoConsent, withdrawn.Refusal);
        }

        // ---------------------------------------------------------------- state builders

        private sealed class JobInStateFixture
        {
            public Fixture Fixture = null!;
            public string JobId = null!;
            public InMemoryJobRunner Runner => Fixture.Runner;
        }

        private static Fixture HandoffJob(bool consentOff = false, bool noAssistant = false)
        {
            var f = new Fixture();
            if (consentOff)
            {
                f.Queue = new InMemoryAssistantRequestQueue();
                f.Queue.WithdrawConsentAsync().GetAwaiter().GetResult();
            }
            else if (noAssistant)
            {
                // Consent is fully ON. What is missing is the assistant itself.
                f.Queue = new InMemoryAssistantRequestQueue(new AlwaysConsented(), new NoAssistantAttached());
            }
            f.Catalogue.Steps.Add(new StepDefinition
            {
                Id = "ask-for-words", Title = "Ask for the words", Op = "set_description",
                Provides = new[] { "words-exist" }, RequiresInputs = new[] { "assistant:a description for the measure" },
            });
            f.Catalogue.Obligations.Add(new JobObligation("words-exist", "the words exist"));
            f.Executor.Handler = (j, s, a) =>
                a.ContainsKey("assistant:a description for the measure")
                    ? Passing("words-exist")
                    : new EngineStepResult
                    {
                        Ok = true,
                        AssistantNeed = "a description for the measure",
                        AssistantAllowedContext = new[] { "measure.name", "measure.expression" },
                    };
            return f.Build();
        }

        private static async Task<JobInStateFixture> JobInState(JobState state)
        {
            switch (state)
            {
                case JobState.Running:
                {
                    var f = OneStepJob(new EngineStepResult { Ok = true, ObligationsMet = Array.Empty<string>() });
                    var job = (await f.Start()).Job!;
                    return new JobInStateFixture { Fixture = f, JobId = job.JobId };
                }
                case JobState.AwaitingAssistant:
                {
                    var f = HandoffJob();
                    var job = (await f.Start()).Job!;
                    await f.Next(job.JobId, "ask-for-words");
                    return new JobInStateFixture { Fixture = f, JobId = job.JobId };
                }
                case JobState.Completed:
                {
                    var f = OneStepJob(Passing("measure-exists"));
                    var job = (await f.Start()).Job!;
                    await f.Next(job.JobId);
                    return new JobInStateFixture { Fixture = f, JobId = job.JobId };
                }
                case JobState.StoppedAtStub:
                {
                    var f = HandoffJob(consentOff: true);
                    var job = (await f.Start()).Job!;
                    await f.Next(job.JobId, "ask-for-words");
                    return new JobInStateFixture { Fixture = f, JobId = job.JobId };
                }
                case JobState.Cancelled:
                {
                    var f = OneStepJob(new EngineStepResult { Ok = true, ObligationsMet = Array.Empty<string>() });
                    var job = (await f.Start()).Job!;
                    await f.Runner.CancelAsync(job.JobId, "first cancel", f.SessionId, f.Generation);
                    return new JobInStateFixture { Fixture = f, JobId = job.JobId };
                }
                case JobState.Failed:
                {
                    var f = new Fixture();
                    f.Catalogue.Steps.Add(new StepDefinition { Id = "boom", Title = "Boom", Op = "op_boom", Provides = new[] { "o" } });
                    f.Catalogue.Obligations.Add(new JobObligation("o", "the thing exists"));
                    f.Executor.Handler = (j, s, a) => throw new InvalidOperationException("the dispatcher said no");
                    f.Build();
                    var job = (await f.Start()).Job!;
                    await f.Next(job.JobId);
                    return new JobInStateFixture { Fixture = f, JobId = job.JobId };
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(state));
            }
        }
    }
}
