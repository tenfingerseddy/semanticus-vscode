using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine.Entitlement;

namespace Semanticus.Engine.Jev
{
    /// <summary>
    /// M0a contract 1: THE JOB CONTRACT (docs/redesign/contracts/job-contract.md).
    ///
    /// Author, Fix and Build are jobs. A job is a workflow run whose plan comes from a CATALOGUE instead of a
    /// file, whose next step is picked by a CHOOSER instead of a fixed order, and whose completion is decided by
    /// an OBLIGATIONS CHECKLIST the engine alone ticks. Everything here is the contract plus an in-memory
    /// reference implementation; the later lane implements it on <see cref="WorkflowRunState"/> and
    /// <see cref="WorkflowRunner"/> against these same tests.
    ///
    /// The load-bearing refusals, each pinned by a test in Semanticus.Tests/JevJobContractTests.cs:
    ///   - a caller-supplied "verified" claim never ticks an obligation (it is recorded, never believed);
    ///   - an obligation is ticked only from an <see cref="EngineStepResult"/> the engine produced;
    ///   - a step whose declared inputs changed is re-offered even though it already ran;
    ///   - a call carrying a stale session generation is refused before anything is read or written;
    ///   - done is never reported while a declared obligation is unmet.
    /// </summary>
    public enum JobState
    {
        /// <summary>Started, nothing chosen yet.</summary>
        Created,
        /// <summary>The runner holds the job and will offer a next step.</summary>
        Running,
        /// <summary>Suspended on a posted assistant request. The call is NOT held open.</summary>
        AwaitingAssistant,
        /// <summary>Terminal, every declared obligation met.</summary>
        Completed,
        /// <summary>Terminal, useful but incomplete: the unmet obligation and the reason are named.</summary>
        StoppedAtStub,
        /// <summary>Terminal by a person or an agent asking for it.</summary>
        Cancelled,
        /// <summary>Terminal because the engine could not continue.</summary>
        Failed,
    }

    public static class JobStates
    {
        public static bool IsTerminal(JobState s) =>
            s == JobState.Completed || s == JobState.StoppedAtStub || s == JobState.Cancelled || s == JobState.Failed;
    }

    /// <summary>Why a job operation refused. Both doors report the same value, so the Studio banner and the
    /// MCP error text can never drift apart.</summary>
    public enum JobRefusal
    {
        None,
        NotFound,
        /// <summary>The caller's session id or generation is not the one that owns this job.</summary>
        StaleSession,
        AlreadyTerminal,
        /// <summary>Resume was called while the assistant request is still queued or claimed.</summary>
        RequestStillPending,
        NoEligibleStep,
        StepCapReached,
        /// <summary>The session already holds as many live jobs as it is allowed. A SUSPENDED job counts: it
        /// is holding a slot while it waits for an assistant.</summary>
        TooManyLiveJobs,
        /// <summary>A step for this job is already running. One active step per job, so two callers cannot
        /// drive the same job past each other.</summary>
        StepAlreadyRunning,
        /// <summary>The chosen step needs a Pro feature this installation does not grant.</summary>
        FeatureDenied,
        /// <summary>The model revision, source fingerprint, snapshot digest or catalogue version moved under the job.</summary>
        EvidenceMoved,
        UnknownStep,
    }

    /// <summary>
    /// The four evidence versions a job binds itself to. All four are compared on every step: a job that ran
    /// against a model revision, a source fingerprint, a snapshot digest or a catalogue version that has since
    /// moved must not report a result computed against the old one.
    /// </summary>
    public sealed class JobEvidence
    {
        public JobEvidence(long modelRevision, string sourceFingerprint, string snapshotDigest, string catalogueVersion)
        {
            ModelRevision = modelRevision;
            SourceFingerprint = sourceFingerprint ?? "";
            SnapshotDigest = snapshotDigest ?? "";
            CatalogueVersion = catalogueVersion ?? "";
        }

        public long ModelRevision { get; }
        public string SourceFingerprint { get; }
        public string SnapshotDigest { get; }
        public string CatalogueVersion { get; }

        public bool SameAs(JobEvidence other) =>
            other != null
            && ModelRevision == other.ModelRevision
            && string.Equals(SourceFingerprint, other.SourceFingerprint, StringComparison.Ordinal)
            && string.Equals(SnapshotDigest, other.SnapshotDigest, StringComparison.Ordinal)
            && string.Equals(CatalogueVersion, other.CatalogueVersion, StringComparison.Ordinal);

        /// <summary>The first field that moved, for an honest refusal sentence. Null when nothing moved.</summary>
        public string FirstDifference(JobEvidence other)
        {
            if (other == null) return "evidence";
            if (ModelRevision != other.ModelRevision) return "model revision";
            if (!string.Equals(SourceFingerprint, other.SourceFingerprint, StringComparison.Ordinal)) return "source fingerprint";
            if (!string.Equals(SnapshotDigest, other.SnapshotDigest, StringComparison.Ordinal)) return "snapshot digest";
            if (!string.Equals(CatalogueVersion, other.CatalogueVersion, StringComparison.Ordinal)) return "catalogue version";
            return null;
        }

        public override string ToString() =>
            $"rev={ModelRevision} src={SourceFingerprint} snap={SnapshotDigest} cat={CatalogueVersion}";
    }

    /// <summary>
    /// One thing the job PROMISED to achieve. <see cref="Met"/> has no public setter and no caller-facing tick:
    /// the only route is <see cref="TickFromEngineResult"/>, which is internal and takes an
    /// <see cref="EngineStepResult"/>. That is the whole of "outcomes are engine-owned" as a type rule rather
    /// than a convention.
    /// </summary>
    public sealed class JobObligation
    {
        public JobObligation(string id, string description)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Description = description ?? id;
        }

        public string Id { get; }
        public string Description { get; }
        public bool Met { get; private set; }
        /// <summary>The engine operation whose own result ticked this obligation. Empty while unmet.</summary>
        public string EvidenceOp { get; private set; } = "";
        /// <summary>The engine's evidence digest at the tick. Empty while unmet.</summary>
        public string EvidenceDigest { get; private set; } = "";

        /// <summary>The step whose result ticked this. Empty while unmet.</summary>
        public string ProvingStepId { get; private set; } = "";

        /// <summary>
        /// THE INPUTS THAT PROVED IT, digest by digest, exactly as the proving step ran against them.
        /// Astra B5: an obligation used to be a latch. Validate an expression, change the expression, finish a
        /// different obligation, and the job reported Completed while the validation it was resting on had been
        /// obsolete for two steps. A proof is only a proof of the thing it was computed from, so the thing it
        /// was computed from is recorded with it.
        /// </summary>
        public IReadOnlyDictionary<string, string> ProvingInputs { get; private set; }
            = new Dictionary<string, string>(StringComparer.Ordinal);

        internal void TickFromEngineResult(EngineStepResult result, string op, string stepId,
            IReadOnlyDictionary<string, string> provingInputs)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (!result.Ok) return;                      // a failed step proves nothing
            Met = true;
            EvidenceOp = op ?? "";
            EvidenceDigest = result.EvidenceDigest ?? "";
            ProvingStepId = stepId ?? "";
            ProvingInputs = provingInputs == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(provingInputs, StringComparer.Ordinal);
        }

        /// <summary>
        /// Un-tick this obligation if any input that proved it now reads differently. Returns true when it did.
        /// The rule is deliberately the SAME comparison the re-offer rule uses, so there is one idea and not
        /// two: a step whose inputs moved is offered again, and the obligation that step proved stops counting
        /// at exactly that moment.
        /// </summary>
        internal bool InvalidateIfInputsMoved(IReadOnlyDictionary<string, string> now)
        {
            if (!Met || ProvingInputs.Count == 0) return false;
            var moved = false;
            foreach (var kv in ProvingInputs)
            {
                if (now != null && now.TryGetValue(kv.Key, out var v)
                    && string.Equals(v, kv.Value, StringComparison.Ordinal)) continue;
                moved = true;
                break;
            }
            if (!moved) return false;
            Met = false;
            EvidenceOp = "";
            EvidenceDigest = "";
            ProvingStepId = "";
            ProvingInputs = new Dictionary<string, string>(StringComparer.Ordinal);
            return true;
        }

        internal JobObligation Copy()
        {
            var c = new JobObligation(Id, Description)
            {
                Met = Met,
                EvidenceOp = EvidenceOp,
                EvidenceDigest = EvidenceDigest,
                ProvingStepId = ProvingStepId,
                ProvingInputs = new Dictionary<string, string>(ProvingInputs, StringComparer.Ordinal),
            };
            return c;
        }
    }

    /// <summary>Progress a person and an agent both read the same way. <see cref="TotalProvisional"/> is true
    /// while the catalogue can still grow, which is the normal case for a chooser-driven job.</summary>
    public sealed class JobProgress
    {
        public int StepsTaken { get; set; }
        public int StepCap { get; set; }
        public int ObligationsMet { get; set; }
        public int ObligationsTotal { get; set; }
        public bool TotalProvisional { get; set; } = true;
    }

    /// <summary>A step the catalogue offers but this installation may not take, with the feature named.</summary>
    public sealed class DeniedAction
    {
        public string StepId { get; set; }
        public string Title { get; set; }
        public string RequiresFeature { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>One step the chooser may pick right now, with why it is on offer.</summary>
    public sealed class StepOffer
    {
        public string StepId { get; set; }
        public string Title { get; set; }
        public string Op { get; set; }
        public string[] Provides { get; set; } = Array.Empty<string>();
        /// <summary>"new" for a step that has not run, "inputs changed" for one being re-offered.</summary>
        public string Why { get; set; }
    }

    /// <summary>What actually happened on one step, kept on the job so both doors can read the history.</summary>
    public sealed class JobStepRecord
    {
        public string StepId { get; set; }
        public string Op { get; set; }
        public bool Ok { get; set; }
        public string Note { get; set; }
        public string[] ObligationsMet { get; set; } = Array.Empty<string>();
        public string EvidenceDigest { get; set; }
        /// <summary>The input digests this step ran against. A later difference re-offers the step.</summary>
        public Dictionary<string, string> InputDigests { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Everything the CALLER asserted on this submission. RECORDED, never believed. It exists so a
        /// reviewer can see that a caller claimed something and that the engine did not act on the claim.</summary>
        public Dictionary<string, string> CallerClaims { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>The read model both doors get. get_job on MCP and the Studio job panel render exactly this.</summary>
    public interface IJob
    {
        string JobId { get; }
        string Family { get; }
        string OwnerSessionId { get; }
        long OwnerSessionGeneration { get; }
        JobState State { get; }
        JobEvidence Evidence { get; }
        IReadOnlyList<string> AllowedActions { get; }
        IReadOnlyList<DeniedAction> DeniedActions { get; }
        IReadOnlyList<JobObligation> Obligations { get; }
        JobProgress Progress { get; }
        /// <summary>Non-null while the job is suspended on the assistant handoff queue.</summary>
        string PendingRequestId { get; }
        string TerminalReason { get; }
    }

    /// <summary>An immutable snapshot of a job. Every operation returns one of these, never the live object.</summary>
    public sealed class JobView : IJob
    {
        public string JobId { get; set; }
        public string Family { get; set; }
        public string OwnerSessionId { get; set; }
        public long OwnerSessionGeneration { get; set; }
        public JobState State { get; set; }
        public JobEvidence Evidence { get; set; }
        public IReadOnlyList<string> AllowedActions { get; set; } = Array.Empty<string>();
        public IReadOnlyList<DeniedAction> DeniedActions { get; set; } = Array.Empty<DeniedAction>();
        public IReadOnlyList<JobObligation> Obligations { get; set; } = Array.Empty<JobObligation>();
        public JobProgress Progress { get; set; } = new JobProgress();
        public string PendingRequestId { get; set; }
        public string TerminalReason { get; set; }
        public IReadOnlyList<StepOffer> Offers { get; set; } = Array.Empty<StepOffer>();
        public IReadOnlyList<JobStepRecord> Steps { get; set; } = Array.Empty<JobStepRecord>();
        /// <summary>Set when the job is suspended: the plain-words need the assistant was asked for.</summary>
        public string PendingNeed { get; set; }

        public JobObligation Obligation(string id) =>
            Obligations.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal));
    }

    /// <summary>Every job operation returns this. A refusal is a VALUE, not an exception, because both doors
    /// have to render it and an MCP tool result is not a stack trace.</summary>
    public sealed class JobResult
    {
        public JobView Job { get; set; }
        public JobRefusal Refusal { get; set; } = JobRefusal.None;
        public string Message { get; set; }
        public bool Ok => Refusal == JobRefusal.None;
    }

    // ---------------------------------------------------------------- the catalogue and the chooser

    /// <summary>One step a job family may take. The catalogue replaces the workflow FILE.</summary>
    public sealed class StepDefinition
    {
        public string Id { get; set; }
        public string Title { get; set; }
        /// <summary>The engine operation this step performs. The same name both doors call.</summary>
        public string Op { get; set; }
        /// <summary>Null means free on every tier. Otherwise the grant this step needs.</summary>
        public ProFeature? RequiresFeature { get; set; }
        /// <summary>Obligation ids this step can tick, if the engine's own result says so.</summary>
        public string[] Provides { get; set; } = Array.Empty<string>();
        /// <summary>Input keys this step depends on. If any of their digests move after the step ran, the step
        /// is RE-OFFERED. The catalogue shrinks only while inputs are unchanged.</summary>
        public string[] RequiresInputs { get; set; } = Array.Empty<string>();
    }

    /// <summary>The catalogue a job family draws its steps from, with the version the job binds to.</summary>
    public interface IStepCatalogue
    {
        string Version { get; }
        IReadOnlyList<StepDefinition> For(string family);
        /// <summary>The obligations a job of this family promises. Done is never reported while one is unmet.</summary>
        IReadOnlyList<JobObligation> ObligationsFor(string family);
    }

    /// <summary>
    /// The step chooser hook. The DEFAULT is deterministic and needs no provider, which is what makes the
    /// no-provider path the tested path: with no oracle the job still runs, it just takes the first eligible
    /// step every time. A Jev-backed chooser is a drop-in replacement that must clear its replay bar first.
    /// </summary>
    public interface IJobStepChooser
    {
        Task<string> ChooseAsync(IJob job, IReadOnlyList<StepOffer> offers, CancellationToken ct);
    }

    public sealed class FirstEligibleStepChooser : IJobStepChooser
    {
        public Task<string> ChooseAsync(IJob job, IReadOnlyList<StepOffer> offers, CancellationToken ct) =>
            Task.FromResult(offers != null && offers.Count > 0 ? offers[0].StepId : null);
    }

    // ---------------------------------------------------------------- execution

    /// <summary>
    /// The engine's own result for one step. This type is the ONLY thing that can tick an obligation, and it is
    /// produced by an <see cref="IJobStepExecutor"/>, never by a caller payload.
    /// </summary>
    public sealed class EngineStepResult
    {
        public bool Ok { get; set; }
        public string Note { get; set; }
        /// <summary>Obligation ids this result proves. The runner intersects them with the step's Provides, so
        /// an executor cannot tick an obligation the catalogue never said this step could prove.</summary>
        public string[] ObligationsMet { get; set; } = Array.Empty<string>();
        public string EvidenceDigest { get; set; }
        /// <summary>Non-null means the step needs words: the runner posts an assistant request and SUSPENDS.
        /// It never holds the call open waiting for the same caller to take its next step.</summary>
        public string AssistantNeed { get; set; }
        public string[] AssistantAllowedContext { get; set; } = Array.Empty<string>();
        /// <summary>The input digests the engine actually observed while running the step.</summary>
        public Dictionary<string, string> ObservedInputDigests { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public interface IJobStepExecutor
    {
        Task<EngineStepResult> ExecuteAsync(IJob job, StepDefinition step,
            IReadOnlyDictionary<string, string> answers, CancellationToken ct);
    }

    /// <summary>
    /// The engine's current session identity. A job is owned by one session GENERATION: opening, replacing or
    /// closing the model bumps the generation and every in-flight job becomes stale.
    ///
    /// <see cref="SessionManager"/> IS this source, and owns the counter. Astra's closing recommendation put
    /// ownership at the CONTEXT EXCHANGE because that is the only place in the engine where the open model is
    /// actually swapped, so it is the only place that can know the answer; everywhere else is guessing. The two
    /// sites are one exchange seen twice: the atomic publication in <c>SessionManager.CommitAsync</c> and the
    /// assignment in <c>SessionManager.ExchangeRetiredContext</c>.
    ///
    /// A job binds to the value it captured at start and every later call is compared against the captured
    /// value AND against the live one, because the sweep may not have run yet. A runner built with no source
    /// falls back to trusting the caller's handle; that is a test fixture, not a shipping configuration.
    /// </summary>
    public interface ISessionGenerationSource
    {
        string SessionId { get; }
        long Generation { get; }
    }

    /// <summary>The engine's current evidence versions, read fresh on every step.</summary>
    public interface IJobEvidenceSource
    {
        JobEvidence Current();
        /// <summary>Digest of one declared input key, for the re-offer rule.</summary>
        string InputDigest(string key);
    }

    /// <summary>Effective feature grants, so allowed actions are filtered by the REAL entitlement rather than a
    /// hard-coded free or Pro assumption.</summary>
    public interface IFeatureGrantsView
    {
        bool Grants(ProFeature feature);
    }

    public sealed class JobStartRequest
    {
        public string Family { get; set; }
        public string Intent { get; set; }
        /// <summary>Optional: the chooser to use for this job. Null takes the runner's default.</summary>
        public IJobStepChooser Chooser { get; set; }
    }

    public sealed class JobStepSubmission
    {
        public string JobId { get; set; }
        /// <summary>Null asks the chooser. A named step is the manual step-choice control Studio needs, and the
        /// same named step on MCP. Both doors reach the same operation.</summary>
        public string StepId { get; set; }
        /// <summary>The caller's session identity at submission time. Checked before anything else.</summary>
        public string SessionId { get; set; }
        public long SessionGeneration { get; set; }
        public Dictionary<string, string> Answers { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>
        /// Anything the caller asserts about its own work, INCLUDING a "verified" flag. The runner records this
        /// verbatim on the step record and never reads it when deciding an obligation. The field exists rather
        /// than being dropped because silently discarding a claim makes the refusal invisible to a reviewer.
        /// </summary>
        public Dictionary<string, string> CallerClaims { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>The engine-side job runner. Five operations, the same five on both doors:
    /// start_job, get_job, next_step, cancel_job, resume_job.</summary>
    public interface IJobRunner
    {
        Task<JobResult> StartAsync(JobStartRequest request, string sessionId, long sessionGeneration, CancellationToken ct = default);
        Task<JobResult> GetAsync(string jobId, CancellationToken ct = default);
        Task<JobResult> NextStepAsync(JobStepSubmission submission, CancellationToken ct = default);
        Task<JobResult> CancelAsync(string jobId, string reason, string sessionId, long sessionGeneration, CancellationToken ct = default);
        Task<JobResult> ResumeAsync(string jobId, string sessionId, long sessionGeneration, CancellationToken ct = default);
    }
}
