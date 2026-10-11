using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine.Entitlement;

namespace Semanticus.Engine.Jev
{
    /// <summary>
    /// The IN-MEMORY REFERENCE implementation of <see cref="IJobRunner"/>. It exists so the behaviour is pinned
    /// by tests BEFORE the real runner is built on <see cref="WorkflowRunState"/> and <see cref="WorkflowRunner"/>.
    /// It deliberately has no TOM, no session, no entitlement and no MCP dependency: every one of those arrives
    /// through a seam, so a test can move a revision, deny a feature or fail a step without a model on disk.
    ///
    /// The mapping onto today's workflow machinery is in docs/redesign/contracts/job-contract.md. What this class
    /// encodes that the workflow runner does not have yet: a catalogue instead of a file, a step chooser hook, an
    /// obligations checklist the engine alone ticks, a suspend-and-resume state, and a session GENERATION.
    /// </summary>
    public sealed class InMemoryJobRunner : IJobRunner
    {
        private sealed class JobRecord
        {
            public string JobId;
            public string Family;
            public string Intent;
            public string OwnerSessionId;
            public long OwnerGeneration;
            public JobState State = JobState.Created;
            public JobEvidence Evidence;
            public readonly List<JobObligation> Obligations = new List<JobObligation>();
            public readonly List<JobStepRecord> Steps = new List<JobStepRecord>();
            public readonly Dictionary<string, string> Answers = new Dictionary<string, string>(StringComparer.Ordinal);
            public string PendingRequestId;
            public string PendingNeed;
            public string TerminalReason;
            public IJobStepChooser Chooser;
            /// <summary>ONE ACTIVE STEP PER JOB (Astra B6). Set inside the lock before the call leaves it to
            /// wait on a chooser or an executor, cleared in the finally. A second next_step is refused rather
            /// than allowed to drive the same job past the first.</summary>
            public bool StepInFlight;
            /// <summary>The in-flight step's own cancellation source, so cancelling the JOB cancels the WORK
            /// and not just the record of it.</summary>
            public CancellationTokenSource ActiveCts;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, JobRecord> _jobs = new Dictionary<string, JobRecord>(StringComparer.Ordinal);
        private readonly IStepCatalogue _catalogue;
        private readonly IJobStepExecutor _executor;
        private readonly IJobEvidenceSource _evidence;
        private readonly IFeatureGrantsView _grants;
        private readonly IAssistantRequestQueue _queue;
        private readonly IJobStepChooser _defaultChooser;
        private readonly ISessionGenerationSource _sessions;
        private int _seq;

        /// <summary>Eight steps at most, from the plan's Author gate. A job that reaches the cap stops at a
        /// stub and names the obligation it did not meet; it never reports done.</summary>
        public int StepCap { get; set; } = 8;

        /// <summary>Eight LIVE jobs at most, matching the engine's live-run cap (WorkflowRunStore.MaxHeld),
        /// which is what a job becomes when the later lane moves these onto WorkflowRunState. A job SUSPENDED
        /// on its assistant counts toward it: it is holding a slot while it waits, and pretending otherwise
        /// is how nine jobs become live on a cap of eight.</summary>
        public int LiveJobCap { get; set; } = 8;

        public InMemoryJobRunner(
            IStepCatalogue catalogue,
            IJobStepExecutor executor,
            IJobEvidenceSource evidence,
            IFeatureGrantsView grants,
            IAssistantRequestQueue queue,
            IJobStepChooser defaultChooser = null,
            ISessionGenerationSource sessions = null)
        {
            _sessions = sessions;
            _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
            _grants = grants ?? throw new ArgumentNullException(nameof(grants));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _defaultChooser = defaultChooser ?? new FirstEligibleStepChooser();
        }

        // ------------------------------------------------------------------ operations

        public Task<JobResult> StartAsync(JobStartRequest request, string sessionId, long sessionGeneration, CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_lock)
            {
                // WHERE THE GENERATION COMES FROM. When the engine supplies a session source, the ENGINE's
                // answer is what the job is bound to, never the caller's claim, and a caller holding an older
                // handle is told here rather than finding out on its first step. The source IS SessionManager,
                // which owns the counter and moves it at its context exchange.
                var ownerSessionId = sessionId;
                var ownerGeneration = sessionGeneration;
                if (_sessions != null)
                {
                    ownerSessionId = _sessions.SessionId;
                    ownerGeneration = _sessions.Generation;
                    if ((sessionId != null && !string.Equals(sessionId, ownerSessionId, StringComparison.Ordinal))
                        || sessionGeneration != ownerGeneration)
                        return Task.FromResult(new JobResult
                        {
                            Refusal = JobRefusal.StaleSession,
                            Message = "The model you are looking at is not the one that is open now, so no job was started. Read the session again and start the job against the model that is open.",
                        });
                }

                // The live-job cap. A SUSPENDED job is not terminal, so it counts: it is holding its slot
                // while it waits for an assistant, and a cap that let go of those is not a cap.
                if (_jobs.Values.Count(j => !JobStates.IsTerminal(j.State)) >= LiveJobCap)
                    return Task.FromResult(new JobResult
                    {
                        Refusal = JobRefusal.TooManyLiveJobs,
                        Message = $"{LiveJobCap} jobs are already running or waiting on your assistant, so no job was started. Finish or cancel one of them first.",
                    });

                var job = new JobRecord
                {
                    JobId = "job-" + (++_seq),
                    Family = request.Family,
                    Intent = request.Intent,
                    OwnerSessionId = ownerSessionId,
                    OwnerGeneration = ownerGeneration,
                    Evidence = _evidence.Current(),
                    State = JobState.Running,
                    Chooser = request.Chooser ?? _defaultChooser,
                };
                foreach (var o in _catalogue.ObligationsFor(request.Family) ?? Array.Empty<JobObligation>())
                    job.Obligations.Add(o.Copy());
                _jobs[job.JobId] = job;
                return Task.FromResult(Ok(job));
            }
        }

        public Task<JobResult> GetAsync(string jobId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (!_jobs.TryGetValue(jobId ?? "", out var job)) return Task.FromResult(NotFound(jobId));
                return Task.FromResult(Ok(job));
            }
        }

        public async Task<JobResult> NextStepAsync(JobStepSubmission submission, CancellationToken ct = default)
        {
            if (submission == null) throw new ArgumentNullException(nameof(submission));

            JobRecord job;
            StepDefinition step;
            Dictionary<string, string> answers;
            CancellationToken stepCt;
            lock (_lock)
            {
                if (!_jobs.TryGetValue(submission.JobId ?? "", out job)) return NotFound(submission.JobId);

                // The session check runs FIRST, before anything is read, chosen or written. A stale generation
                // means the model this job was computed against is not the model now open.
                var stale = CheckSession(job, submission.SessionId, submission.SessionGeneration);
                if (stale != null) return stale;

                if (JobStates.IsTerminal(job.State))
                    return Refuse(job, JobRefusal.AlreadyTerminal,
                        $"This job already finished ({Describe(job.State)}). Start a new one.");

                if (job.State == JobState.AwaitingAssistant)
                    return Refuse(job, JobRefusal.RequestStillPending,
                        $"This job is waiting for your assistant on request {job.PendingRequestId}. Resume it once the request is answered, or cancel the job.");

                // ONE ACTIVE STEP PER JOB. Two callers driving the same job at once was how a step could run
                // twice and how a second call could pick a step the first had already taken.
                if (job.StepInFlight)
                    return Refuse(job, JobRefusal.StepAlreadyRunning,
                        "This job is already taking a step. Wait for that one to finish, or cancel the job.");

                // Rebind the evidence. A moved model revision, source fingerprint or snapshot digest is NORMAL:
                // it re-offers the steps whose declared inputs moved rather than killing the job. A moved
                // CATALOGUE version is different: the job's plan was computed from a catalogue that no longer
                // exists, so nothing it already decided can be trusted and it is refused.
                var now = _evidence.Current();
                if (!string.Equals(now.CatalogueVersion, job.Evidence.CatalogueVersion, StringComparison.Ordinal))
                    return Refuse(job, JobRefusal.EvidenceMoved,
                        "The step catalogue changed while this job was running, so its plan is out of date. Nothing was applied. Start the job again.");
                job.Evidence = now;

                if (job.Steps.Count >= StepCap)
                {
                    StopAtStub(job, $"This job reached its {StepCap} step limit.");
                    return Ok(job);
                }

                var offers = Offers(job);
                if (offers.Count == 0)
                {
                    FinishByObligations(job);
                    return Ok(job);
                }

                if (!string.IsNullOrEmpty(submission.StepId))
                {
                    step = StepsFor(job.Family).FirstOrDefault(s => string.Equals(s.Id, submission.StepId, StringComparison.Ordinal));
                    if (step == null)
                        return Refuse(job, JobRefusal.UnknownStep,
                            $"There is no step named '{submission.StepId}' for a {job.Family} job.");
                    if (!Granted(step))
                        return Refuse(job, JobRefusal.FeatureDenied,
                            $"The step '{step.Title}' needs {step.RequiresFeature}. This job can finish without it, but it will stop at a stub.");
                    if (!offers.Any(o => string.Equals(o.StepId, step.Id, StringComparison.Ordinal)))
                        return Refuse(job, JobRefusal.NoEligibleStep,
                            $"The step '{step.Title}' is already done and its inputs have not changed, so there is nothing to redo.");
                }
                else
                {
                    step = null;   // chosen below, outside the lock
                }
                answers = new Dictionary<string, string>(job.Answers, StringComparer.Ordinal);
                foreach (var kv in submission.Answers ?? new Dictionary<string, string>()) answers[kv.Key] = kv.Value;

                // Claim the job. This is the LAST thing the lock does, so every refusal above leaves nothing
                // to release, and the finally below is the only way out.
                job.StepInFlight = true;
                job.ActiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stepCt = job.ActiveCts.Token;
            }

            try
            {
            if (step == null)
            {
                // The chooser runs OUTSIDE the lock: a Jev-backed chooser calls out, and no provider wait may
                // sit on a lock the model dispatcher can contend for.
                IReadOnlyList<StepOffer> offers;
                JobView view;
                lock (_lock) { offers = Offers(job); view = Snapshot(job); }
                var chosen = await (job.Chooser ?? _defaultChooser).ChooseAsync(view, offers, stepCt).ConfigureAwait(false);
                lock (_lock)
                {
                    // The job may have been cancelled, swapped or re-planned while the chooser was thinking.
                    // Nothing runs until that is ruled out.
                    var gone = StillOurs(job, submission);
                    if (gone != null) return Refuse(job, gone.Value.Refusal, gone.Value.Message);

                    step = StepsFor(job.Family).FirstOrDefault(s => string.Equals(s.Id, chosen, StringComparison.Ordinal));
                    if (step == null || !Granted(step) || !Offers(job).Any(o => string.Equals(o.StepId, step.Id, StringComparison.Ordinal)))
                    {
                        FinishByObligations(job);
                        return Ok(job);
                    }
                }
            }

            EngineStepResult result;
            JobView forExecutor;
            lock (_lock) { forExecutor = Snapshot(job); }
            try
            {
                result = await _executor.ExecuteAsync(forExecutor, step, answers, stepCt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    var gone = StillOurs(job, submission);
                    job.Steps.Add(new JobStepRecord
                    {
                        StepId = step.Id, Op = step.Op, Ok = false,
                        Note = gone != null
                            ? $"This step stopped after the job stopped, so nothing it produced was used: {ex.Message}"
                            : ex.Message,
                        CallerClaims = Copy(submission.CallerClaims),
                    });
                    // A cancelled job does not become Failed because the work it was told to abandon threw.
                    if (gone != null) return Refuse(job, gone.Value.Refusal, gone.Value.Message);
                    job.State = JobState.Failed;
                    job.TerminalReason = $"The step '{step.Title}' failed: {ex.Message}";
                    return Ok(job);
                }
            }

            lock (_lock)
            {
                // THE LATE RESULT. A reply that comes back after a cancel, a model exchange or a catalogue
                // change is recorded, because the work really did happen and hiding it would be worse, and
                // then it proves nothing: no obligation ticks, no state moves, no assistant request is posted.
                var late = StillOurs(job, submission);
                if (late != null)
                {
                    job.Steps.Add(new JobStepRecord
                    {
                        StepId = step.Id, Op = step.Op, Ok = result.Ok,
                        Note = "This step finished after the job stopped, so nothing it produced was used.",
                        CallerClaims = Copy(submission.CallerClaims),
                    });
                    return Refuse(job, late.Value.Refusal, late.Value.Message);
                }

                var record = new JobStepRecord
                {
                    StepId = step.Id,
                    Op = step.Op,
                    Ok = result.Ok,
                    Note = result.Note,
                    EvidenceDigest = result.EvidenceDigest,
                    // RECORDED, NEVER BELIEVED. A reviewer can see exactly what the caller claimed and that the
                    // obligations below were decided without reading any of it.
                    CallerClaims = Copy(submission.CallerClaims),
                    InputDigests = InputDigestsFor(job, step, answers),
                };
                // What the ENGINE says it saw wins over what the runner computed. A caller cannot reach this
                // dictionary; only an EngineStepResult can.
                foreach (var kv in result.ObservedInputDigests ?? new Dictionary<string, string>())
                    record.InputDigests[kv.Key] = kv.Value;
                job.Steps.Add(record);
                foreach (var kv in submission.Answers ?? new Dictionary<string, string>()) job.Answers[kv.Key] = kv.Value;

                if (!string.IsNullOrEmpty(result.AssistantNeed))
                {
                    var post = _queue.PostAsync(new AssistantRequestPost
                    {
                        JobId = job.JobId,
                        SessionId = job.OwnerSessionId,
                        Need = result.AssistantNeed,
                        AllowedContext = result.AssistantAllowedContext ?? Array.Empty<string>(),
                    }, ct).GetAwaiter().GetResult();

                    if (!post.Ok)
                    {
                        // With no assistant reachable the job reports a VALID STUB and names the need. It never
                        // pretends the words exist and never holds a call open.
                        StopAtStub(job, $"This job needs {result.AssistantNeed}, and your assistant was not asked. {post.Message}");
                        job.PendingNeed = result.AssistantNeed;
                        return Ok(job);
                    }
                    job.PendingRequestId = post.Request.RequestId;
                    job.PendingNeed = result.AssistantNeed;
                    job.State = JobState.AwaitingAssistant;
                    return Ok(job);
                }

                if (result.Ok)
                {
                    // The engine's own result ticks the obligations, and only the ones the CATALOGUE said this
                    // step can prove. An executor that claims more is intersected down to what it is allowed.
                    var allowed = new HashSet<string>(step.Provides ?? Array.Empty<string>(), StringComparer.Ordinal);
                    var ticked = (result.ObligationsMet ?? Array.Empty<string>()).Where(allowed.Contains).ToArray();
                    record.ObligationsMet = ticked;
                    foreach (var id in ticked)
                        job.Obligations.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal))
                            ?.TickFromEngineResult(result, step.Op, step.Id, record.InputDigests);
                }

                // Sweep BEFORE deciding done: the step that just ran may have moved an input that another
                // obligation's proof was resting on.
                RefreshObligations(job);
                if (job.Obligations.All(o => o.Met))
                {
                    job.State = JobState.Completed;
                    job.TerminalReason = "Every promise this job made is backed by an engine result.";
                }
                return Ok(job);
            }
            }
            finally
            {
                lock (_lock)
                {
                    job.StepInFlight = false;
                    var spent = job.ActiveCts;
                    job.ActiveCts = null;
                    try { spent?.Dispose(); } catch { }
                }
            }
        }

        public Task<JobResult> CancelAsync(string jobId, string reason, string sessionId, long sessionGeneration, CancellationToken ct = default)
        {
            CancellationTokenSource inFlight;
            JobResult result;
            lock (_lock)
            {
                if (!_jobs.TryGetValue(jobId ?? "", out var job)) return Task.FromResult(NotFound(jobId));
                var stale = CheckSession(job, sessionId, sessionGeneration);
                if (stale != null) return Task.FromResult(stale);

                if (JobStates.IsTerminal(job.State))
                    // Idempotent: a second cancel changes NOTHING, including the terminal reason.
                    return Task.FromResult(Refuse(job, JobRefusal.AlreadyTerminal,
                        $"This job already finished ({Describe(job.State)}). Nothing was changed."));

                _queue.CancelForJobAsync(job.JobId, "The job was cancelled.", ct).GetAwaiter().GetResult();
                job.PendingRequestId = null;
                job.State = JobState.Cancelled;
                job.TerminalReason = string.IsNullOrWhiteSpace(reason) ? "Cancelled." : reason;
                // Cancel the WORK, not just the record of it. Taken here, run below, outside the lock.
                inFlight = job.ActiveCts;
                result = Ok(job);
            }
            StopWork(inFlight);
            return Task.FromResult(result);
        }

        public Task<JobResult> ResumeAsync(string jobId, string sessionId, long sessionGeneration, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (!_jobs.TryGetValue(jobId ?? "", out var job)) return Task.FromResult(NotFound(jobId));
                var stale = CheckSession(job, sessionId, sessionGeneration);
                if (stale != null) return Task.FromResult(stale);

                if (JobStates.IsTerminal(job.State))
                    return Task.FromResult(Refuse(job, JobRefusal.AlreadyTerminal,
                        $"This job already finished ({Describe(job.State)})."));
                if (job.State != JobState.AwaitingAssistant)
                    return Task.FromResult(Ok(job));   // resume is idempotent on a running job

                var request = (_queue as InMemoryAssistantRequestQueue)?.Peek(job.PendingRequestId)
                    ?? _queue.ListAsync(job.OwnerSessionId, includeClosed: true, ct).GetAwaiter().GetResult()
                        .FirstOrDefault(r => string.Equals(r.RequestId, job.PendingRequestId, StringComparison.Ordinal));

                if (request == null)
                {
                    StopAtStub(job, $"The request for {job.PendingNeed} is gone, so this job stopped without it.");
                    return Task.FromResult(Ok(job));
                }

                switch (request.State)
                {
                    case AssistantRequestState.Queued:
                    case AssistantRequestState.Claimed:
                        return Task.FromResult(Refuse(job, JobRefusal.RequestStillPending,
                            $"Request {request.RequestId} has not been answered yet."));

                    case AssistantRequestState.Completed:
                        // The words are an INPUT, not proof. They change the step's input digest, so the step
                        // that needed them is re-offered and must produce a real engine result to tick anything.
                        job.Answers["assistant:" + job.PendingNeed] = request.Answer ?? "";
                        job.PendingRequestId = null;
                        job.PendingNeed = null;
                        job.State = JobState.Running;
                        return Task.FromResult(Ok(job));

                    case AssistantRequestState.Declined:
                        StopAtStub(job, $"Your assistant declined to supply {job.PendingNeed}"
                            + (string.IsNullOrWhiteSpace(request.DeclineReason) ? "." : $": {request.DeclineReason}"));
                        return Task.FromResult(Ok(job));

                    default:
                        StopAtStub(job, $"The request for {job.PendingNeed} was cancelled, so this job stopped without it.");
                        return Task.FromResult(Ok(job));
                }
            }
        }

        /// <summary>
        /// The engine calls this when the open model is exchanged. Every non-terminal job owned by the previous
        /// generation is cancelled, so a job can never be orphaned in a state nobody is allowed to cancel.
        /// Returns how many were swept.
        /// </summary>
        public int OnSessionGenerationChanged(string newSessionId, long newGeneration)
        {
            var swept = new List<CancellationTokenSource>();
            int n;
            lock (_lock)
            {
                n = 0;
                foreach (var job in _jobs.Values.Where(j => !JobStates.IsTerminal(j.State)))
                {
                    if (string.Equals(job.OwnerSessionId, newSessionId, StringComparison.Ordinal)
                        && job.OwnerGeneration == newGeneration) continue;
                    _queue.CancelForJobAsync(job.JobId, "The open model changed.", CancellationToken.None).GetAwaiter().GetResult();
                    job.PendingRequestId = null;
                    job.State = JobState.Cancelled;
                    job.TerminalReason = "The open model changed, so this job was cancelled. Nothing it had not already applied was applied.";
                    if (job.ActiveCts != null) swept.Add(job.ActiveCts);
                    n++;
                }
            }
            // A swept job may have a step in flight. Its work stops too, and outside the lock.
            foreach (var cts in swept) StopWork(cts);
            return n;
        }

        // ------------------------------------------------------------------ helpers

        private JobResult CheckSession(JobRecord job, string sessionId, long generation)
        {
            var problem = SessionProblem(job, sessionId, generation);
            return problem == null ? null : Refuse(job, JobRefusal.StaleSession, problem);
        }

        /// <summary>
        /// Two questions, not one. Does the CALLER still hold the identity this job was started under, and is
        /// that identity still the one the ENGINE has open? The second matters because the sweep may not have
        /// run yet: without a live comparison a job could take a step against a model that was swapped out a
        /// moment ago. Returns the sentence, or null when everything lines up.
        /// </summary>
        private string SessionProblem(JobRecord job, string sessionId, long generation)
        {
            const string stale = "The open model changed since this job started, so nothing was read or written. Start the job again against the model that is open now.";
            if (!string.Equals(job.OwnerSessionId, sessionId, StringComparison.Ordinal) || job.OwnerGeneration != generation)
                return stale;
            if (_sessions != null
                && (!string.Equals(_sessions.SessionId, job.OwnerSessionId, StringComparison.Ordinal)
                    || _sessions.Generation != job.OwnerGeneration))
                return stale;
            return null;
        }

        /// <summary>
        /// THE AFTER-EVERY-WAIT CHECK (Astra B6). A chooser call and an executor call both leave the lock, and
        /// anything can happen while they are gone: a cancel, a model exchange, a catalogue change. Nothing
        /// that came back from such a wait may act, or have its result accepted, until this says the job is
        /// still the one that asked. Null means it is. Call under <c>_lock</c>.
        /// </summary>
        private (JobRefusal Refusal, string Message)? StillOurs(JobRecord job, JobStepSubmission submission)
        {
            if (JobStates.IsTerminal(job.State))
                return (JobRefusal.AlreadyTerminal,
                    $"This job finished ({Describe(job.State)}) while that step was running, so nothing it produced was used.");

            var problem = SessionProblem(job, submission.SessionId, submission.SessionGeneration);
            if (problem != null) return (JobRefusal.StaleSession, problem);

            var now = _evidence.Current();
            if (!string.Equals(now.CatalogueVersion, job.Evidence.CatalogueVersion, StringComparison.Ordinal))
                return (JobRefusal.EvidenceMoved,
                    "The step catalogue changed while this job was running, so its plan is out of date. Nothing was applied. Start the job again.");
            job.Evidence = now;
            return null;
        }

        /// <summary>Cancel an in-flight step's work. ALWAYS called outside <c>_lock</c>: a cancellation callback
        /// can run inline, and one that reached back into the runner under the lock would deadlock. The catch
        /// covers the race with the finally that disposes the same source.</summary>
        private static void StopWork(CancellationTokenSource cts)
        {
            try { cts?.Cancel(); } catch { }
        }

        private IReadOnlyList<StepDefinition> StepsFor(string family) =>
            _catalogue.For(family) ?? Array.Empty<StepDefinition>();

        private bool Granted(StepDefinition s) =>
            !s.RequiresFeature.HasValue || _grants.Grants(s.RequiresFeature.Value);

        /// <summary>
        /// The re-offer rule, in one place. A step is on offer when it has never run, or when ANY of its declared
        /// inputs has a different digest from the one it ran against. The catalogue shrinks only while inputs are
        /// unchanged, so a permanently shrinking catalogue can never suppress a needed recheck.
        /// </summary>
        private IReadOnlyList<StepOffer> Offers(JobRecord job)
        {
            var list = new List<StepOffer>();
            foreach (var s in StepsFor(job.Family))
            {
                if (!Granted(s)) continue;
                var last = job.Steps.LastOrDefault(r => string.Equals(r.StepId, s.Id, StringComparison.Ordinal) && r.Ok);
                if (last == null)
                {
                    list.Add(new StepOffer { StepId = s.Id, Title = s.Title, Op = s.Op, Provides = s.Provides, Why = "new" });
                    continue;
                }
                var now = InputDigestsFor(job, s, job.Answers);
                var changed = now.Any(kv => !last.InputDigests.TryGetValue(kv.Key, out var was)
                                            || !string.Equals(was, kv.Value, StringComparison.Ordinal));
                if (changed)
                    list.Add(new StepOffer { StepId = s.Id, Title = s.Title, Op = s.Op, Provides = s.Provides, Why = "inputs changed" });
            }
            return list;
        }

        /// <summary>An input's digest is the job's own answer when it has one (so an assistant reply moves it),
        /// and otherwise whatever the engine reports for that key.</summary>
        private Dictionary<string, string> InputDigestsFor(JobRecord job, StepDefinition step, IReadOnlyDictionary<string, string> answers)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in step.RequiresInputs ?? Array.Empty<string>())
                d[key] = answers != null && answers.TryGetValue(key, out var v)
                    ? Sha256("answer:" + v)
                    : _evidence.InputDigest(key) ?? "";
            return d;
        }

        /// <summary>
        /// THE STALE-PROOF SWEEP (Astra B5). Every met obligation is re-checked against the inputs that proved
        /// it, and un-ticked when any of them has moved. It runs before every completion decision AND inside
        /// <see cref="Snapshot"/>, so a job that is merely READ cannot report a tick the engine would no longer
        /// stand behind. The comparison is the same one <see cref="Offers"/> uses, so "the step is offered
        /// again" and "its proof stopped counting" are one rule seen from two sides.
        ///
        /// A proving step that is no longer in the catalogue is left alone deliberately: a moved catalogue
        /// version already refuses the whole job at the top of NextStepAsync, which is the stronger answer.
        /// </summary>
        private void RefreshObligations(JobRecord job)
        {
            foreach (var o in job.Obligations)
            {
                if (!o.Met || string.IsNullOrEmpty(o.ProvingStepId)) continue;
                var step = StepsFor(job.Family).FirstOrDefault(s => string.Equals(s.Id, o.ProvingStepId, StringComparison.Ordinal));
                if (step == null) continue;
                o.InvalidateIfInputsMoved(InputDigestsFor(job, step, job.Answers));
            }
        }

        private void StopAtStub(JobRecord job, string reason)
        {
            RefreshObligations(job);
            job.State = JobState.StoppedAtStub;
            var unmet = job.Obligations.Where(o => !o.Met).Select(o => o.Description).ToArray();
            job.TerminalReason = unmet.Length == 0
                ? reason
                : reason + " Still missing: " + string.Join("; ", unmet) + ".";
        }

        private void FinishByObligations(JobRecord job)
        {
            RefreshObligations(job);
            if (job.Obligations.All(o => o.Met))
            {
                job.State = JobState.Completed;
                job.TerminalReason = "Every promise this job made is backed by an engine result.";
            }
            else
            {
                StopAtStub(job, "There is no step left that this job is allowed to take.");
            }
        }

        private static Dictionary<string, string> Copy(Dictionary<string, string> src) =>
            src == null ? new Dictionary<string, string>(StringComparer.Ordinal)
                        : new Dictionary<string, string>(src, StringComparer.Ordinal);

        private static string Describe(JobState s) => s switch
        {
            JobState.Completed => "finished",
            JobState.StoppedAtStub => "stopped at a stub",
            JobState.Cancelled => "cancelled",
            JobState.Failed => "failed",
            _ => "running",
        };

        private JobView Snapshot(JobRecord job)
        {
            // Deliberately mutating inside the read: a view that still claims a tick the engine would refuse to
            // count is the exact lie B5 was about, and both doors render this object. Always called under _lock.
            RefreshObligations(job);
            var denied = StepsFor(job.Family).Where(s => !Granted(s)).Select(s => new DeniedAction
            {
                StepId = s.Id,
                Title = s.Title,
                RequiresFeature = s.RequiresFeature?.ToString(),
                Reason = $"'{s.Title}' needs {s.RequiresFeature}. This job will name it rather than skip it quietly.",
            }).ToArray();

            return new JobView
            {
                JobId = job.JobId,
                Family = job.Family,
                OwnerSessionId = job.OwnerSessionId,
                OwnerSessionGeneration = job.OwnerGeneration,
                State = job.State,
                Evidence = job.Evidence,
                AllowedActions = StepsFor(job.Family).Where(Granted).Select(s => s.Op).ToArray(),
                DeniedActions = denied,
                Obligations = job.Obligations.Select(o => o.Copy()).ToArray(),
                Progress = new JobProgress
                {
                    StepsTaken = job.Steps.Count,
                    StepCap = StepCap,
                    ObligationsMet = job.Obligations.Count(o => o.Met),
                    ObligationsTotal = job.Obligations.Count,
                    TotalProvisional = !JobStates.IsTerminal(job.State),
                },
                PendingRequestId = job.PendingRequestId,
                PendingNeed = job.PendingNeed,
                TerminalReason = job.TerminalReason,
                Offers = JobStates.IsTerminal(job.State) ? Array.Empty<StepOffer>() : Offers(job),
                Steps = job.Steps.Select(s => new JobStepRecord
                {
                    StepId = s.StepId, Op = s.Op, Ok = s.Ok, Note = s.Note,
                    ObligationsMet = s.ObligationsMet, EvidenceDigest = s.EvidenceDigest,
                    InputDigests = new Dictionary<string, string>(s.InputDigests, StringComparer.Ordinal),
                    CallerClaims = new Dictionary<string, string>(s.CallerClaims, StringComparer.Ordinal),
                }).ToArray(),
            };
        }

        private JobResult Ok(JobRecord job) => new JobResult { Job = Snapshot(job) };

        private JobResult Refuse(JobRecord job, JobRefusal refusal, string message) =>
            new JobResult { Job = Snapshot(job), Refusal = refusal, Message = message };

        private static JobResult NotFound(string jobId) => new JobResult
        {
            Refusal = JobRefusal.NotFound,
            Message = $"No job named '{jobId}'.",
        };

        internal static string Sha256(string s)
        {
            using (var sha = SHA256.Create())
                return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""))).ToLowerInvariant();
        }
    }
}
