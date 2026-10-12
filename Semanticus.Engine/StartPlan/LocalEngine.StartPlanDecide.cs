// The start plan's decide loop, the ENGINE half (spec-first start, lane sf-decide; the pure half is StartPlanDecide.cs).
//
// WHAT RUNS, AND WHERE. Pro only, in the background, over a HIDDEN scratch model (never the open one): each round rebuilds the scratch
// from the plan's manifest with every kept line applied, runs the Jev families over it through the JevRunTarget seam
// (LocalEngine.JevFamilies.cs), runs the rules over it, arbitrates, folds the base measure picks into the manifest, and stops when a round
// changes nothing (S10). The cap is six rounds; reaching it, or Jev stopping, is the explicit Incomplete state with its reason, never a
// quiet "done" (S11).
//
// FREE SENDS NOTHING. The first line of the loop is the Pro check, before the scratch is built, before anything is read. A free plan
// is "off" and JevRunsStartedForTest stays 0 (K2: Free gets its own assistant's words and one build, not Jev's decisions).
//
// WHO BUILDS THE SCRATCH. The host (IStartScratchHost): sf-core's StartPlanScratch is the real one (a TMDL clone of the person's empty
// model, a private ChangeBus, the manifest's spec built into it by BuildSpecInto, the kept lines applied by the shared executor). This
// file asks only for a Session, the facts that scratch has, and the executor's facts.
//
// THE ANSWERS BOOK. A column family's answer is kept by the request's digest for the life of the plan, the way the Plan keeps its own per
// session, so a second decide asks nothing for a request it has an answer to whatever the service's cache holds. It is forgotten the moment
// nothing is live (permission withdrawn, no service), so a withdrawn permission never answers.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>The hidden model one decide round runs over, and what it knows.</summary>
    internal sealed class StartScratch : IDisposable
    {
        public Session Session;
        /// <summary>The facts the families read for this scratch (the sample's, once sf-sample serves them). Null: none measured.</summary>
        public Func<Facts.ModelFacts> Facts;
        /// <summary>The facts the plan executor's data checks read when lines are applied to this scratch.</summary>
        public Func<IJevModelFacts> ExecutorFacts;
        /// <summary>The kept lines the executor refused to apply while building this scratch, each with its plain reason. A plan must show what the
        /// build will make, so the loop leaves these out (or, for a person's own line, says why it will not land).</summary>
        public List<(StartLine Line, string Reason)> Refused = new List<(StartLine, string)>();
        public Action OnDispose;
        public void Dispose() { try { OnDispose?.Invoke(); } catch { } }
    }

    /// <summary>Builds the scratch for a plan as it stands: the baseline, the manifest built into it, every kept line applied.</summary>
    internal interface IStartScratchHost
    {
        Task<StartScratch> BuildAsync(StartPlanDoc doc, CancellationToken ct);
    }

    public sealed partial class LocalEngine
    {
        /// <summary>The cap on rounds in one decide (design 2.3). Reaching it with the plan still moving is Incomplete.</summary>
        internal const int StartDecideMaxRounds = 6;

        private readonly ConcurrentDictionary<string, DecisionOutcome> _startDecideAnswers = new ConcurrentDictionary<string, DecisionOutcome>(StringComparer.Ordinal);

        /// <summary>What the measures and column format families remember across the rounds of a plan (sf-progress), bound to the plan so a scratch
        /// rebuilt every round does not start it empty and the Plan's own memory is never touched.</summary>
        private readonly JevProgressMemory _startDecideMemory = new JevProgressMemory();

        private readonly object _startDecideGate = new object();
        private CancellationTokenSource _startDecideCts;
        private Task _startDecideTask = Task.CompletedTask;

        /// <summary>How many rounds the last decide ran, and how many requests it sent. Tests only.</summary>
        internal int StartDecideRoundsForTest { get; private set; }

        /// <summary>Start a decide in the background, cancelling the one before it first and waiting for it to stop, so two never touch
        /// the document at once. Returns the task of the new run (the caller never has to wait for it).</summary>
        internal async Task<Task> KickStartDecideAsync(StartPlanDoc doc, IStartScratchHost host, Action<StartPlanDoc> persist = null, Func<bool, string> askBar = null,
                                                       Func<bool, Task<string>> askBarAsync = null, Func<int, string, Task<DecisionSessionFence>> fenceFor = null)
        {
            await CancelStartDecideAsync().ConfigureAwait(false);
            CancellationTokenSource cts;
            Task task;
            lock (_startPlanLock) _startPlanLoadedDeciding = null;   // a decide is started for the plan now: the one the last engine ran is no longer the question
            // Cancelled by this engine's own stop (an edit) and by sf-core's park / discard token (another model opened, the plan thrown away).
            var parked = BeginStartLoop();
            lock (_startDecideGate)
            {
                cts = _startDecideCts = CancellationTokenSource.CreateLinkedTokenSource(parked);
                task = _startDecideTask = Task.Run(async () =>
                {
                    try
                    {
                        await DecideStartPlanAsync(doc, host, cts.Token, persist, askBar: askBar, askBarAsync: askBarAsync, fenceFor: fenceFor).ConfigureAwait(false);
                        // The plan has settled (stable, incomplete, waiting or off): make its preview now, on the plan as it is, so the grades after the build are real.
                        if (!cts.IsCancellationRequested) ScheduleStartPreview();
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        // An unexpected failure is never a quiet "stable": the plan says it stopped, and why.
                        doc.Decide.State = StartDecideState.Incomplete;
                        doc.Decide.Reason = StartDecideCopy.StoppedReason(ex.Message);
                        try { persist?.Invoke(doc); } catch { }
                    }
                });
            }
            return task;
        }

        /// <summary>The sample of the SOURCE read for the plan the engine holds (counts and patterns, never rows), kept for the life of the plan so every scratch
        /// round and the model the build makes read the same facts (sf-sample's BuildSampleFactsAsync lays it over a model by SOURCE table and column).
        /// Null: no sample was read, which is today's whole behaviour. The start's sample step hands it here (<see cref="ReadStartSampleAsync"/>).
        ///
        /// ONE SAMPLE STATE (the wiring step joined two). The FLAG that says a sample is on is <see cref="StartPlanDoc.SampleFacts"/>, which sits in the plan's
        /// digest and is what the grant and the fence compare; the facts themselves are this field, and the sidecar beside the saved model brings them back
        /// after a restart. Setting either one sets the flag.</summary>
        private Facts.SourceSampleResult _startSample;
        private string _startSamplePlanId;

        internal void SetStartPlanSample(string planId, Facts.SourceSampleResult sample)
        {
            lock (_startPlanLock)
            {
                _startSample = sample; _startSamplePlanId = sample == null ? null : planId;
                SetStartSampleFlagLocked(planId, sample != null && sample.Refused == null, sample);
            }
        }

        /// <summary>The facts of the sample read for this plan, or null. Not read from <paramref name="doc"/>'s flag: a caller may hold a copy made before the
        /// sample landed. After a restart the facts come back from the sidecar beside the saved model, when the plan says a sample was read.</summary>
        internal Facts.SourceSampleResult StartSampleFor(StartPlanDoc doc)
        {
            if (doc == null) return null;
            lock (_startPlanLock)
            {
                if (_startSample != null && string.Equals(_startSamplePlanId, doc.Id, StringComparison.Ordinal)) return _startSample;
                var live = _startPlan;
                if (live == null || !string.Equals(live.Id, doc.Id, StringComparison.Ordinal) || !live.SampleFacts) return null;
                var open = _sessions.Current;
                if (open == null || !IsStartDestination(open.SourcePath, live.Destination)) return null;
                var saved = LoadSourceFacts(open);
                if (saved == null) return null;
                _startSample = saved; _startSamplePlanId = live.Id;
                return saved;
            }
        }

        /// <summary>Whether facts from a sample are on for this plan: the plan's own flag, or a sample this engine holds for it.</summary>
        internal bool StartSampleOn(StartPlanDoc doc) => doc != null && (doc.SampleFacts || StartSampleFor(doc) != null);

        /// <summary>The flag in the held document, under the plan's lock. A flag that moves moves the digest, so an approval made on the old one is void and a grant
        /// the person gave on the old one is refused as stale. Returns whether anything changed.</summary>
        private bool SetStartSampleFlagLocked(string planId, bool on, Facts.SourceSampleResult sample)
        {
            var live = _startPlan;
            if (live == null || !string.Equals(live.Id, planId, StringComparison.Ordinal)) return false;
            var status = StartSampleStatusOf(sample, on);
            var changed = live.SampleFacts != on || live.Sample == null || live.Sample.State != status.State || live.Sample.Reason != status.Reason
                          || live.Sample.Measured != status.Measured || live.Sample.NotMeasured != status.NotMeasured || live.Sample.FiscalYearEnd != status.FiscalYearEnd;
            // The links guessed from names are looked at again against what the rows show: a key that repeats is not a key, one that is unique is. A built plan is not touched.
            // Only when the facts are in hand: a caller that knows a sample was read but holds none (NoteStartSampleRead) leaves the guesses as they are.
            var guessed = live.BuiltUtc == null && (sample != null || !on) && LinkGuesser.Reconsider(live, LinkGuessFacts.From(on ? sample : null));
            if (guessed) StartPlanHold.Refresh(live);
            if (!changed && !guessed) return false;
            live.SampleFacts = on;
            live.Sample = status;
            StartPlanConsent.ClearApprovalIfMoved(live, StartPlanViewBuilder.Digest(live));
            live.UpdatedUtc = DateTime.UtcNow;
            try { StartPlanStore.Write(live); } catch { }
            return true;
        }

        private static StartSampleStatus StartSampleStatusOf(Facts.SourceSampleResult sample, bool on)
        {
            if (sample == null) return new StartSampleStatus { State = on ? StartSampleStatus.Read : StartSampleStatus.None };
            if (sample.Refused != null) return new StartSampleStatus { State = StartSampleStatus.Refused, Reason = sample.Refused, NotMeasured = sample.Tables?.Length ?? 0 };
            var tables = sample.Tables ?? Array.Empty<Facts.SourceTableFacts>();
            string fiscalEnd = null;
            var end = sample.FiscalYearStart?.EndMonth;
            if (end != null && end.Value != 12) fiscalEnd = end.Value + "/" + DateTime.DaysInMonth(2001, end.Value);   // the last day of the month it ends in (2/28 is not offered: a year end must exist every year)
            if (end == 2) fiscalEnd = null;
            return new StartSampleStatus
            {
                State = StartSampleStatus.Read,
                Measured = tables.Count(t => t.NotMeasured == null), NotMeasured = tables.Count(t => t.NotMeasured != null),
                FiscalYearEnd = fiscalEnd,
            };
        }

        /// <summary>A test's own scratch host for the plan the engine holds. Null on every real path, which builds one from the person's model
        /// (<see cref="CreateStartScratchHost"/>).</summary>
        internal IStartScratchHost StartScratchHostForTest;

        /// <summary>The scratch host a decide on THE PLAN THE ENGINE HOLDS builds its scratches with.</summary>
        private IStartScratchHost CreateStartScratchHost() => StartScratchHostForTest ?? new EngineStartScratchHost(this);

        /// <summary>The build's own spec writer, for a scratch: the manifest's spec built into the model, nothing reported.</summary>
        internal static void BuildSpecIntoScratch(Model m, ModelSpec spec) =>
            BuildSpecInto(m, spec, new List<string>(), new List<string>(), new List<string>(), new List<string>());

        /// <summary>Start (or restart) the decide loop on the plan the engine holds, in the background. Called when the permission is given (the plan
        /// leaves waiting_grant) and after every edit. The loop works on a COPY of the document and publishes each round into the held one under
        /// the plan's lock, so a reader never copies a document a round is changing. Free and a parked plan do nothing. Returns the loop's task.</summary>
        /// <summary>A test that proves the consent rules by asking directly, and must not have the loop ask beside it, sets this. Production never does.</summary>
        internal bool StartDecideKickDisabledForTest;

        internal async Task<Task> KickStartDecideOnPlanAsync()
        {
            if (StartDecideKickDisabledForTest) return Task.CompletedTask;
            StartPlanDoc work;
            lock (_startPlanLock)
            {
                if (_startPlan == null || _startPlan.Parked) return Task.CompletedTask;
                work = StartPlanStore.Copy(_startPlan);
            }
            // A plan Jev never decides (Free, or a Pro plan the person chose to carry on without Jev for) has nothing to kick.
            if (string.Equals(work.Decide?.State, StartDecideState.Off, StringComparison.Ordinal) || work.JevDeclined) return Task.CompletedTask;
            // FAIL SHUT BEFORE ANYTHING IS BUILT: no kick while the plan has no grant that covers what it now discloses, whatever the model's own permission says.
            // A refusal is SAID on the plan, never left as "deciding" with no loop running (W7).
            var bar = await WhyNoStartAskAsync(StartSampleOn(work)).ConfigureAwait(false);
            if (bar != null)
            {
                SetStartDecideWaiting(work.Id, bar);
                return Task.CompletedTask;
            }
            var host = CreateStartScratchHost();
            if (host == null) return Task.CompletedTask;
            return await KickStartDecideAsync(work, host, PublishStartDecide, null, WhyNoStartAskAsync, StartFenceFor(work.Id)).ConfigureAwait(false);
        }

        /// <summary>A plan that waits for its grant leaves the wait the moment nothing is in its way any more: the model's permission came back, a table the grant
        /// did not name was taken out again, the grant is there. It is looked at after every consent change, and waiting with a new reason says the new reason.
        /// Not for a plan Jev never decides (Free, or the person chose to carry on without it). Never throws.</summary>
        internal async Task ReevaluateStartPlanAsync()
        {
            try
            {
                StartPlanDoc doc;
                lock (_startPlanLock) doc = StartPlanStore.Copy(_startPlan);
                if (doc == null || doc.Parked || doc.JevDeclined) return;
                if (!string.Equals(doc.Decide?.State, StartDecideState.WaitingGrant, StringComparison.Ordinal)) return;
                var bar = await WhyNoStartAskAsync(StartSampleOn(doc)).ConfigureAwait(false);
                if (bar != null) return;   // still waiting, and the plan already says so: a permission change that leaves the wait as it was changes nothing, and tells no one
                lock (_startPlanLock)
                {
                    var live = _startPlan;
                    if (live == null || !string.Equals(live.Id, doc.Id, StringComparison.Ordinal) || live.Decide?.State != StartDecideState.WaitingGrant) return;
                    live.Decide.State = StartDecideState.Deciding;
                    live.Decide.Reason = null;
                    live.UpdatedUtc = DateTime.UtcNow;
                    try { StartPlanStore.Write(live); } catch { }
                }
                await KickStartDecideOnPlanAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { try { Console.Error.WriteLine("[start-plan] looking at the plan again after a permission change failed: " + ex.Message); } catch { } }
        }

        /// <summary>The plan waits for its grant, with the reason, on the held document (a kick or an unpark the bar refused). Notifies both doors.</summary>
        private void SetStartDecideWaiting(string planId, string reason)
        {
            StartPlanView view = null;
            lock (_startPlanLock)
            {
                var live = _startPlan;
                if (live == null || !string.Equals(live.Id, planId, StringComparison.Ordinal) || live.Decide == null) return;
                if (live.Decide.State == StartDecideState.WaitingGrant && string.Equals(live.Decide.Reason, reason, StringComparison.Ordinal)) return;
                live.Decide.State = StartDecideState.WaitingGrant;
                live.Decide.Reason = reason;
                live.UpdatedUtc = DateTime.UtcNow;
                try { StartPlanStore.Write(live); } catch { }
                view = StartPlanViewBuilder.Build(StartPlanStore.Copy(live), null);
            }
            if (view != null) NotifyStartPlanChanged(view, null);
        }

        /// <summary>The bar the loop is checked against at its start and after each round, read from the document the engine HOLDS and from the grant kept beside the
        /// model's permission (sf-consent's rules, on every ask), never from the loop's working copy. A grant taken back while an ask waits stops it.</summary>
        private async Task<string> WhyNoStartAskAsync(bool sampleFacts)
        {
            StartPlanDoc doc;
            lock (_startPlanLock) doc = StartPlanStore.Copy(_startPlan);
            if (doc == null) return StartDecideCopy.NoGrantReason;
            if (doc.JevDeclined) return StartDecideCopy.DeclinedReason;
            var scope = await DecisionConsentScopeAsync().ConfigureAwait(false);
            return await StartAskRefusalAsync(scope, doc, new StartAskScope { SourceKey = DisclosedScope.SourceKeyOf(doc.Source), SampleFacts = sampleFacts || doc.SampleFacts }, null).ConfigureAwait(false);
        }

        /// <summary>The fence for one round of the held plan: sf-consent's <c>StartAskFenceAsync</c>, which asks the grant on EVERY ask and reads the request's own text.
        /// The families cut their asks by table, so the ask declares the source and the sample flag, and the request text is what is checked for a table the person
        /// left out (check 4 does not trust a declaration).</summary>
        private Func<int, string, Task<DecisionSessionFence>> StartFenceFor(string planId) => (round, scratchId) =>
        {
            StartPlanDoc doc;
            lock (_startPlanLock) doc = StartPlanStore.Copy(_startPlan);
            var ask = new StartAskScope { SourceKey = DisclosedScope.SourceKeyOf(doc?.Source), SampleFacts = doc != null && doc.SampleFacts };
            return StartAskFenceAsync(scratchId, round, () => (scratchId, round), ask);
        };

        /// <summary>One round's state from the loop's working copy into the held document, and to disk. What an edit owns (the approval, the pins
        /// and answers) is never overwritten: only what the loop decides moves.</summary>
        private void PublishStartDecide(StartPlanDoc work)
        {
            var snap = StartPlanStore.Copy(work);
            lock (_startPlanLock)
            {
                var live = _startPlan;
                if (live == null || !string.Equals(live.Id, snap.Id, StringComparison.Ordinal)) return;
                live.Decide = snap.Decide;
                live.Lines = snap.Lines;
                live.Manifest = snap.Manifest;
                live.Words = snap.Words;
                live.UpdatedUtc = snap.UpdatedUtc;
                try { StartPlanStore.Write(live); } catch { }
            }
            // Every writer of the plan tells both doors it moved (the loop's own work has no origin).
            try { NotifyStartPlanChanged(StartPlanViewBuilder.Build(snap, null), null); } catch { }
        }

        /// <summary>edit_start_plan, both doors (S12): one change to the structure, a pick, a line, an answer or the fiscal year end. It follows Needs
        /// (dropping a measure drops its followers, keeping a follower keeps its measure), says on each line it moved besides the one asked for why
        /// it moved, clears any approval, stops the loop in flight and starts the next one. A refused edit says why in plain words and changes nothing.</summary>
        internal async Task<StartPlanView> EditStartPlanCoreAsync(StartPlanEdit edit, string origin) =>
            (await EditStartPlansCoreAsync(new[] { edit }, origin, throwOnRejected: true)).Plan;

        public Task<BatchStartPlanResult> EditStartPlansAsync(StartPlanEdit[] items, string origin)
        {
            RequireProFeature();
            GuardOutsideWrite(origin);
            return EditStartPlansCoreAsync(items, origin);
        }

        private async Task<BatchStartPlanResult> EditStartPlansCoreAsync(StartPlanEdit[] items, string origin, bool throwOnRejected = false)
        {
            if (items == null) throw new ArgumentException("Pass an items list.");
            await _startPlanGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var existing = LoadStartPlan() ?? throw new InvalidOperationException("There is no start plan to edit.");
                // The loop is stopped first and waited for, so it can never write a round over the edit.
                await CancelStartDecideAsync().ConfigureAwait(false);
                var outcome = new StartEditOutcome();
                var rejected = new List<BatchRejection>();
                var changed = new HashSet<string>(StringComparer.Ordinal);
                var sampleChanged = false;
                lock (_startPlanLock)
                {
                    var doc = _startPlan;
                    // Apply to a copy: a refused edit, or one that fails half way, leaves the held document exactly as it was.
                    var trial = StartPlanStore.Copy(doc);
                    for (var i = 0; i < items.Length; i++)
                    {
                        var candidate = StartPlanStore.Copy(trial);
                        StartEditOutcome next;
                        try
                        {
                            if (items[i] == null) throw new ArgumentException("A start-plan edit is required.");
                            next = StartPlanEditing.Apply(candidate, items[i], origin);
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                        {
                            if (throwOnRejected) throw;
                            rejected.Add(new BatchRejection { Index = i, ObjRef = items[i]?.Target, Reason = ex.Message });
                            continue;
                        }
                        if (!next.Ok)
                        {
                            if (throwOnRejected) throw new InvalidOperationException(next.Refused);
                            rejected.Add(new BatchRejection { Index = i, ObjRef = items[i]?.Target, Reason = next.Refused });
                            continue;
                        }
                        if (!next.Changed) continue;
                        trial = candidate;
                        outcome.Changed = true;
                        outcome.KnockOns.AddRange(next.KnockOns);
                        changed.Add((items[i].Op ?? "").ToLowerInvariant() + "|" + items[i].Target);
                        sampleChanged |= string.Equals(items[i].Op, "set_table_sample", StringComparison.OrdinalIgnoreCase);
                    }
                    if (outcome.Ok && outcome.Changed)
                    {
                        trial.Version = doc.Version + 1;
                        // The loop starts again only where it will run: a plan that is off (Free) stays as it is, and one with no grant that covers what it now
                        // discloses (waiting, none, or a table put back in after it) waits for the person, with the reason, and asks nothing.
                        if (CreateStartScratchHost() != null && !string.Equals(trial.Decide.State, StartDecideState.Off, StringComparison.Ordinal))
                        {
                            var bar = StartPlanDecide.WhyNoAsk(trial, StartSampleOn(trial));
                            if (bar == null) trial.Decide.State = StartDecideState.Deciding;
                            else { trial.Decide.State = StartDecideState.WaitingGrant; trial.Decide.Reason = bar; }
                        }
                        // A table whose sample was just refused leaves the plan's own copy of the grant too (the view and the needs-you line read it).
                        if (trial.Disclosure?.SampleTables != null && trial.SampleRefused != null && trial.SampleRefused.Count > 0)
                        {
                            var refusedNow = new HashSet<string>(trial.SampleRefused.Select(DisclosedScope.Norm), StringComparer.Ordinal);
                            trial.Disclosure.SampleTables = trial.Disclosure.SampleTables.Where(t => !refusedNow.Contains(DisclosedScope.Norm(t))).ToArray();
                        }
                        StartPlanStore.Write(trial);
                        _startPlan = trial;
                    }
                }
                // A refusal takes the table out of the grant kept beside the model's permission, on disk, before the loop starts again (Sol, join 8, K6): the grant is what the
                // sample is cut against, so a grant that still names the table would send its facts after the person said no.
                if (sampleChanged)
                    await NarrowStartGrantToPlanAsync().ConfigureAwait(false);
                if (outcome.Changed) await KickStartDecideOnPlanAsync().ConfigureAwait(false);
                if (outcome.Changed) ScheduleStartPreview();   // a plan Jev is not deciding gets its preview now; one that is gets it when the decide ends
                var view = await GetStartPlanAsync().ConfigureAwait(false);
                if (outcome.Changed) NotifyStartPlanChanged(view, origin);
                // What else moved, said on this answer (and not in the broadcast: it is about this edit, and nothing stores it).
                if (outcome.KnockOns.Count > 0) view.KnockOns = outcome.KnockOns.ToList();
                if (!throwOnRejected)
                    NoteBatch(_sessions.Current?.Id, origin, changed.Count, "changed", "start-plan choices", "plan/start", undo: false);
                return new BatchStartPlanResult { Revision = _sessions.Current?.Revision ?? 0, Applied = changed.Count, Rejected = rejected.ToArray(), Plan = view };
            }
            finally { await EnsureStartDecideNotOrphanedAsync().ConfigureAwait(false); _startPlanGate.Release(); }
        }

        /// <summary>Stop the decide in flight (an edit came in, the plan was discarded or parked) and wait until it has stopped.</summary>
        internal async Task CancelStartDecideAsync()
        {
            CancellationTokenSource cts; Task task;
            lock (_startDecideGate) { cts = _startDecideCts; task = _startDecideTask; }
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            try { await task.ConfigureAwait(false); } catch { }
        }

        /// <summary>True while a decide task is running. Tests only.</summary>
        internal bool StartDecideAliveForTest { get { lock (_startDecideGate) return !_startDecideTask.IsCompleted; } }

        /// <summary>THE CLASS RULE: the plan says "deciding" only while a decide is running. A verb that holds the plan's gate may stop the loop first (words, an edit, a
        /// build) and then change nothing (every word refused, an edit that moves nothing, a build the checks refuse, an error), which left the stored round at
        /// "deciding" with no loop and Approve greyed for ever (plan statement check 3, defect 1). So every such verb ends here: when the plan is deciding and no decide
        /// is alive, the loop is started again; when it still cannot run, the plan says it stopped. A parked plan keeps its state (the read that unparks it starts the
        /// loop), and so does a built one. Never throws.</summary>
        internal async Task EnsureStartDecideNotOrphanedAsync()
        {
            if (StartDecideKickDisabledForTest) return;   // a test that holds the loop off on purpose has no loop to find missing
            try
            {
                // A decide that was cancelled but has not yet noticed is "alive" for a moment and dead the next. A build cancels the loop through the park token and
                // does not wait for it, so judge only after it has stopped.
                CancellationTokenSource cts; Task running;
                lock (_startDecideGate) { cts = _startDecideCts; running = _startDecideTask; }
                if (cts != null && cts.IsCancellationRequested) { try { await running.ConfigureAwait(false); } catch { } }
                bool Orphan()
                {
                    lock (_startPlanLock)
                    {
                        var p = _startPlan;
                        if (p == null || p.Parked || p.BuiltUtc != null || !string.Equals(p.Decide?.State, StartDecideState.Deciding, StringComparison.Ordinal)) return false;
                    }
                    return !StartDecideAliveForTest;
                }
                if (!Orphan()) return;
                await KickStartDecideOnPlanAsync().ConfigureAwait(false);
                if (!Orphan()) return;
                // The kick had nothing to start (no scratch host, a refusal it could not say): never leave "deciding" with no loop. Say that it stopped.
                StartPlanView view;
                lock (_startPlanLock)
                {
                    var live = _startPlan;
                    if (live?.Decide == null || !string.Equals(live.Decide.State, StartDecideState.Deciding, StringComparison.Ordinal)) return;
                    live.Decide.State = StartDecideState.Incomplete;
                    live.Decide.Reason = StartDecideCopy.StoppedReason("it was stopped and could not start again");
                    live.UpdatedUtc = DateTime.UtcNow;
                    try { StartPlanStore.Write(live); } catch { }
                    view = StartPlanViewBuilder.Build(StartPlanStore.Copy(live), null);
                }
                NotifyStartPlanChanged(view, null);
            }
            catch (Exception ex) { try { Console.Error.WriteLine("[start-plan] starting an orphaned decide again failed: " + ex.Message); } catch { } }
        }

        /// <summary>Wait until no decide is in flight. Tests only.</summary>
        internal async Task StartDecideSettledForTest()
        {
            // A decide that finished may have been followed by another (an edit, words), so wait until the one waited for is still the latest.
            for (var i = 0; i < 20; i++)
            {
                Task task;
                lock (_startDecideGate) task = _startDecideTask;
                try { await task.ConfigureAwait(false); } catch { }
                lock (_startDecideGate) if (ReferenceEquals(task, _startDecideTask)) return;
            }
        }

        /// <summary>Forget what the loop heard (the plan was discarded, or permission was withdrawn).</summary>
        internal void ForgetStartDecideAnswers() { _startDecideAnswers.Clear(); _startDecideMemory.Reset(); }

        /// <summary>THE LOOP. Mutates <paramref name="doc"/> in place (state, round, lines, base measures, word counts) and calls
        /// <paramref name="persist"/> after every round. Returns false when it was cancelled part way (the document then holds the last
        /// whole round), true when it finished in a state: off, waiting_grant, stable or incomplete.</summary>
        internal async Task<bool> DecideStartPlanAsync(StartPlanDoc doc, IStartScratchHost host, CancellationToken ct,
                                                       Action<StartPlanDoc> persist = null, int maxRounds = StartDecideMaxRounds, Func<bool, string> askBar = null,
                                                       Func<bool, Task<string>> askBarAsync = null, Func<int, string, Task<DecisionSessionFence>> fenceFor = null)
        {
            void Save() { try { persist?.Invoke(doc); } catch { } }
            void Finish(string state, string reason) { doc.Decide.State = state; doc.Decide.Reason = reason; doc.UpdatedUtc = DateTime.UtcNow; Save(); }

            // FIRST, before anything is read or built: a free plan decides nothing and asks nothing, not even to count (K2).
            if (!Entitlement.FeatureGrants.IsProTier(_entitlement))
            {
                doc.Decide.Round = 0;
                Finish(StartDecideState.Off, StartDecideCopy.FreeReason);
                return true;
            }
            // SECOND, still before anything is read or built: the plan's own grant (the Disclosure). The model's permission is no stand-in for it, so a
            // model that may use Jev and a plan that was never shown to the person asks nothing. The same bar is checked again on every ask (the fence below).
            // THE BAR is sf-consent's rule set (StartPlanConsent.WhyNot), from the grant kept beside the model's permission when the engine runs the loop on the plan
            // it holds (askBarAsync, fenceFor), and from the plan's own Disclosure for a plan held outside an engine (the default). Tests pass either.
            Func<bool, Task<string>> bar = askBarAsync
                ?? (askBar != null ? (Func<bool, Task<string>>)(sample => Task.FromResult(askBar(sample))) : sample => Task.FromResult(StartPlanDecide.WhyNoAsk(doc, sample)));
            var sampleOn = StartSampleOn(doc);
            var barred = await bar(sampleOn).ConfigureAwait(false);
            if (barred != null)
            {
                doc.Decide.Round = 0;
                Finish(StartDecideState.WaitingGrant, barred);
                return true;
            }
            var live = await LiveJevPointsAsync().ConfigureAwait(false);
            if (live.Count == 0)
            {
                ForgetStartDecideAnswers();
                var noService = !Decisions.Oracle.Available;
                doc.Decide.Round = 0;
                Finish(noService ? StartDecideState.Off : StartDecideState.WaitingGrant,
                       noService ? StartDecideCopy.NoServiceReason : StartDecideCopy.WaitingGrantReason);
                return true;
            }
            doc.Manifest ??= new StructureManifest();
            // The assistant's business names become renames of the manifest before the loop looks (StartPlanEditing.FoldRenames), so the loop and the plan
            // speak of every table, column and measure by the one name.
            if (StartPlanEditing.FoldRenames(doc) > 0) Save();

            // ROUND 0 BUILDS NO RULE STARTERS (S4). With the base measure family live its pick IS the base measure, so the manifest's
            // default (rule) base measures go before the first round, once: a column Jev is unsure of comes back as the rule's fallback
            // and says so in Needs you. A decide on a plan that has already been decided keeps what the last one settled.
            if (doc.Decide.Round == 0 && doc.Decide.State != StartDecideState.Stable && doc.Decide.State != StartDecideState.Incomplete
                && live.Contains(DecisionRegistry.ColumnSummarize))
            {
                var basesBefore = doc.Manifest.BaseMeasures.Select(b => (b.Table, b.Name)).ToList();
                StartPlanDecide.RemoveBases(doc.Manifest, b => b.DecidedBy == StartDecidedBy.Rule || string.IsNullOrEmpty(b.DecidedBy));
                // Words the assistant or the person wrote for a starter total are held while the total is decided again, not found missing and deleted (K8).
                if (StartPlanHold.HoldLinesOnGoneBases(doc, basesBefore) > 0) Save();
            }

            var flips = new Dictionary<string, int>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal) { StartPlanDecide.Snapshot(doc) };
            JevRun last = null;
            // THE LAST ROUND LOOKS AT THE MODEL THE WAY THE PLAN WILL (S10). A round that changes nothing is followed by one more that remembers nothing: no
            // answer kept by object, no progress kept by family, so every family composes its requests exactly as the Plan's own run composes them on
            // the built model (a table's open columns in one batch, whichever were answered before). What it sends is paid for here, once, and the
            // service's cache then answers the Plan's run after the build, so that run sends nothing new. A verifying round that changes something
            // sends the loop back to ordinary rounds.
            var verifying = false;
            for (var round = 1; round <= maxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                StartPlanHold.ClearCannotLand(doc);   // a line held because the executor would not write it is tried again, in case what blocked it is gone
                doc.Decide.State = StartDecideState.Deciding;
                doc.Decide.Round = round;
                doc.Decide.Reason = null;
                StartDecideRoundsForTest = round;
                Save();

                StartScratch scratch = null;
                var requestsBefore = Decisions.RequestsStarted;
                var answersBefore = _startDecideAnswers.Count;
                try
                {
                    scratch = await host.BuildAsync(doc, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    var s = scratch.Session;
                    // The assistant's words on an object the scratch does not have are HELD, not refused: they are not left out, and they come back with their table.
                    var present = await s.ReadAsync(m => StartPlanWords.PresentRefs(doc, m)).ConfigureAwait(false);
                    var refusedNow = StartPlanDecide.LeaveOutRefused(doc, scratch.Refused.Where(r => !StartPlanWords.IsHeld(r.Line, present)));
                    var sampleNow = sampleOn || scratch.Facts?.Invoke()?.Data == Facts.ModelFactsData.SourceSample;
                    // THE FENCE OF THIS ROUND: the plan's grant is asked on EVERY ask (sf-consent), with the scratch and the round it was made for, so a grant taken
                    // back, a table put back in or the sample switched on stops the next ask, and a left-out table's name in a request stops that ask.
                    var roundFence = fenceFor != null ? await fenceFor(round, s.Id).ConfigureAwait(false) : null;
                    var target = new JevRunTarget
                    {
                        Session = s,
                        Facts = scratch.Facts,
                        // A plan held outside an engine has no grant store, so its fence follows its own Disclosure: while the bar says no the identity it reads is
                        // never the one the ask was made for, so nothing is sent. The engine's plan uses the real fence above.
                        Fence = roundFence != null ? (Func<long, DecisionSessionFence>)(_ => roundFence)
                            : revision => new DecisionSessionFence(s.Id, revision, () => bar(sampleNow).GetAwaiter().GetResult() == null ? (s.Id, s.Revision) : (s.Id + "#no-grant", -1L)),
                        KnownAnswer = verifying ? (Func<string, DecisionOutcome>)(_ => null) : digest => _startDecideAnswers.TryGetValue(digest, out var heard) ? heard : null,
                        RememberAnswer = verifying ? (Action<string, DecisionOutcome>)((_, __) => { }) : (digest, outcome) => _startDecideAnswers[digest] = outcome,
                        RemovedMeasures = () => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        ExecutorFacts = scratch.ExecutorFacts,
                        UncappedAsks = true,
                        Memory = verifying ? new JevProgressMemory() : _startDecideMemory,
                        MemoryId = verifying ? "verify-" + (doc.Id ?? "start-plan") : doc.Id ?? "start-plan",
                    };

                    Interlocked.Increment(ref _jevRunsStarted);
                    var run = await RunFamiliesAsync(target, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (run == null) { Finish(StartDecideState.Incomplete, StartDecideCopy.StoppedReason("the plan changed while Jev was looking at it")); return true; }
                    if (run.Off)
                    {
                        ForgetStartDecideAnswers();
                        Finish(StartDecideState.WaitingGrant, StartDecideCopy.WaitingGrantReason);
                        return true;
                    }
                    // The grant was taken back (or the plan went past it) while the round ran: the asks it stopped are not "the model changed", they are the
                    // person's no. Say so, keep nothing this round heard, and wait for the grant.
                    var barredNow = await bar(sampleNow).ConfigureAwait(false);
                    if (barredNow != null)
                    {
                        ForgetStartDecideAnswers();
                        Finish(StartDecideState.WaitingGrant, barredNow);
                        return true;
                    }
                    last = run;

                    var ruleLines = await RuleLinesAsync(s).ConfigureAwait(false);
                    var resolved = await ResolvedRefsAsync(s, doc).ConfigureAwait(false);
                    var keepStamps = await PersonKeepStampsAsync(s, doc).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();

                    // From here to the end of the round nothing awaits: a cancel never leaves the document half changed.
                    var basesBeforeFold = doc.Manifest.BaseMeasures.Select(b => (b.Table, b.Name)).ToList();
                    var moved = StartPlanDecide.FoldPicks(doc.Manifest, run.SummarizePicks, flips);
                    if (moved > 0) StartPlanHold.HoldLinesOnGoneBases(doc, basesBeforeFold);   // a pick that renames or removes a measure holds the words on it (K8)
                    if (moved > 0) doc.Manifest.RefreshTimeIntelligence();
                    var input = new StartPlanDecide.RoundInput
                    {
                        Round = round,
                        JevLines = run.Lines.Where(l => !IsFoldedMeasureLine(l)).ToList(),
                        RuleLines = ruleLines,
                        Decided = run.Decided,
                        Resolves = r => r == null || resolved.Contains(r),
                        Present = present,
                    };
                    var change = StartPlanDecide.Merge(doc, input, flips);
                    if (CarryPersonKeeps(doc, keepStamps) > 0) change.Changed++;   // a keep carried across a rename moves the plan's own stamp, which is a change to save
                    change.Measures = moved;
                    change.Changed += refusedNow;
                    doc.Decide.Progress = ProgressOf(run);
                    doc.UpdatedUtc = DateTime.UtcNow;

                    var snapshot = StartPlanDecide.Snapshot(doc);
                    var cycled = !seen.Add(snapshot) && change.Any;   // back to a state already seen: nothing new will come of another round
                    if (!change.Any || cycled)
                    {
                        // A batch is still to come (a family looks at so many objects a run) and this round got further than the last: the plan is
                        // not settled, the next round asks the rest. A round that sent nothing and heard nothing new cannot get further.
                        var gotFurther = Decisions.RequestsStarted > requestsBefore || _startDecideAnswers.Count > answersBefore;
                        if (run.Partial && gotFurther && !cycled) { verifying = false; Save(); continue; }
                        if (!verifying && !cycled) { verifying = true; Save(); continue; }
                        // Nothing moved: the plan is settled. Unless Jev stopped before it had looked at everything, which is said, never hidden.
                        await RecountWordsAsync(s, doc).ConfigureAwait(false);
                        // A family that stopped with objects left (the service stopped answering) says why; so does any ask that heard nothing.
                        var stopped = run.Progress.Values.FirstOrDefault(p => p.Stopped && p.Remaining > 0);
                        var why = stopped?.StopReason ?? run.StopReason;
                        var shortOfAll = run.Partial || stopped != null || (why != null && run.Unanswered.Count > 0);
                        if (shortOfAll) Finish(StartDecideState.Incomplete, StartDecideCopy.StoppedReason(why));
                        else Finish(StartDecideState.Stable, null);
                        return true;
                    }
                    verifying = false;
                    Save();
                }
                finally { scratch?.Dispose(); }
            }

            // The cap: the plan was still moving after the last round. Said plainly, with what Jev last said stopped it when it did.
            doc.Decide.Progress = last == null ? doc.Decide.Progress : ProgressOf(last);
            Finish(StartDecideState.Incomplete, last?.StopReason != null ? StartDecideCopy.StoppedReason(last.StopReason) : StartDecideCopy.RoundCapReason);
            return true;
        }

        // ------------------------------------------------------------------ a keep survives a rename (plan-statement-check-3, defect 2)

        /// <summary>What each format line the PERSON kept computes on this round's scratch, by the object it is about: the identity digest (columns by lineage tag, so a rename does not
        /// move it) and the strict one (the digest the executor compares). Read on the scratch's own dispatcher; null for an object the scratch does not have.</summary>
        private static async Task<Dictionary<string, (string Identity, string Strict)>> PersonKeepStampsAsync(Session s, StartPlanDoc doc)
        {
            var refs = doc.Lines.Where(IsPersonKeptFormat).Select(l => l.Item.ObjectRef).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (refs.Count == 0) return new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            return await s.ReadAsync(m =>
            {
                var found = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in refs)
                {
                    try
                    {
                        var obj = ObjectRefs.Resolve(m, r);
                        if (obj == null) continue;
                        found[r] = (Semanticus.Analysis.FormatFill.ComputeIdentityDigest(obj), Semanticus.Analysis.FormatFill.ComputeDigest(obj));
                    }
                    catch { }
                }
                return found;
            }).ConfigureAwait(false);
        }

        private static bool IsPersonKeptFormat(StartLine l) =>
            l?.Item != null && l.Kept && l.DecidedBy == StartDecidedBy.Person && l.Item.KeptByPerson && IsJevFormatLine(l.Item);

        /// <summary>A KEEP BELONGS TO THE OBJECT, NOT ITS NAME. The person's keep of an unproven format was stamped with what the object computed when they kept it, and a rename (the
        /// assistant's business names come after the person's answers) changes the names a formula reads, so the stamp stopped matching and the keep was set aside with nobody told.
        /// Here the object is known by what it computes with every column by its lineage tag: while that is what the keep was made for, only names moved, so the stamp is made current and
        /// the keep stands. When it is not, the object is something else now, the stamp is left as it was, and the build sets the keep aside, which the Needs you band says
        /// (StartPlanDecide.VoidedKeeps). The first round after a keep records what the keep is for. Returns how many stamps moved.</summary>
        internal static int CarryPersonKeeps(StartPlanDoc doc, Dictionary<string, (string Identity, string Strict)> stamps)
        {
            var moved = 0;
            foreach (var l in doc.Lines.Where(IsPersonKeptFormat))
            {
                if (!stamps.TryGetValue(l.Item.ObjectRef ?? "", out var now) || now.Identity == null) continue;
                var item = l.Item;
                if (item.KeptIdentity == null) { item.KeptIdentity = now.Identity; item.KeptDigest = now.Strict; item.WorkedOutDigest = now.Strict; moved++; continue; }
                if (!string.Equals(item.KeptIdentity, now.Identity, StringComparison.Ordinal)) continue;   // it computes something else now: not carried
                if (!string.Equals(item.KeptDigest, now.Strict, StringComparison.Ordinal) || !string.Equals(item.WorkedOutDigest, now.Strict, StringComparison.Ordinal))
                { item.KeptDigest = now.Strict; item.WorkedOutDigest = now.Strict; moved++; }
            }
            return moved;
        }

        /// <summary>A SUGGESTION THE PERSON LEFT UNTICKED IN THE START PLAN IS NOT OFFERED AGAIN BY THE PLAN AFTER THE BUILD. The person approved the plan with
        /// these lines unticked (a format Jev could not prove, a pick it was unsure of), so on the model the build makes they are a decision, not a gap: each goes into
        /// the open model's Plan as a dropped line the person made, which the Plan's own sync carries onto the same line when its family proposes it again
        /// (JevFamily.CarryDecision). The person undoes it the way they undo any drop there, by keeping the row. Lines the loop left out because they do not apply
        /// are not decisions and are not carried. Called by the build once its lines have landed, on the model it made. Returns how many lines were carried.</summary>
        internal int HonourStartPlanDeclines(StartPlanDoc doc)
        {
            var context = _sessions.CurrentContext;
            var session = _sessions.Current;
            if (doc == null || context == null || session == null) return 0;
            var declined = doc.Lines.Where(l => !l.Kept && !StartPlanDecide.IsLeftOut(l) && l.Item != null && JevFamily.IsJevLine(l.Item)).ToList();
            if (declined.Count == 0) return 0;
            var plan = context.Plans.GetOrStart();
            foreach (var line in declined)
            {
                var item = StartPlanDecide.ToChangeItem(line);   // status rejected, its Needs restored
                item.DroppedBy = "person";
                plan.Add(item, session.Revision, "");
            }
            return declined.Count;
        }

        /// <summary>The create line of a base measure the manifest now holds: it is not kept as a line (S4), the manifest makes the measure.</summary>
        private static bool IsFoldedMeasureLine(ChangeItem l) =>
            string.Equals(l.Kind, "create_measure", StringComparison.Ordinal)
            && string.Equals(l.DecisionPoint, DecisionRegistry.ColumnSummarize, StringComparison.Ordinal);

        /// <summary>The rules' lines over a scratch: the AI-readiness safe fixes and the Model quality fixes the Plan's best-practice row
        /// writes (with THE FORMAT FILL RULE's refusals), on the model as it is.</summary>
        private async Task<List<ChangeItem>> RuleLinesAsync(Session s) =>
            await s.ReadAsync(m =>
            {
                var readiness = new ReadinessAnalyzer().Analyze(m);
                var safe = ReadinessSafeLines(m, readiness).ToList();
                var rules = GetBpaRules(m);
                var bpa = BpaAnalyzer.Analyze(m, rules);
                var (bpaLines, _) = BpaPlanLines(m, bpa, safe);
                return safe.Concat(bpaLines).ToList();
            }).ConfigureAwait(false);

        /// <summary>Which of the refs the plan's lines name are in the scratch (read once, on the scratch's own dispatcher).</summary>
        private static async Task<HashSet<string>> ResolvedRefsAsync(Session s, StartPlanDoc doc)
        {
            var refs = doc.Lines.Select(l => l.Item.ObjectRef).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return await s.ReadAsync(m =>
            {
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in refs)
                {
                    try { if (ObjectRefs.Resolve(m, r) != null) found.Add(r); } catch { }
                }
                return found;
            }).ConfigureAwait(false);
        }

        /// <summary>How many words are still to be written on the scratch (the Words for Copilot section's Open) and how many there are in
        /// all, counting what the assistant has already written (the kept lines that carry words). The scratch has those lines applied, so
        /// what it still finds missing is exactly what is open.</summary>
        private async Task RecountWordsAsync(Session s, StartPlanDoc doc)
        {
            var read = await s.ReadAsync(m =>
            {
                var card = new ReadinessAnalyzer().Analyze(m);
                return (Open: WordsFindings(card.Findings, m.Tables.Count).Count(), Present: StartPlanWords.PresentRefs(doc, m));
            }).ConfigureAwait(false);
            doc.Words = StartPlanWords.Tally(doc, read.Open, read.Present);   // held words (their object is out of the scratch) are in neither number
        }

        /// <summary>What each family has got through, from the progress record every live family writes (sf-progress): how many objects it looked at and
        /// heard an answer for, how many it tried without a usable answer, how many are still to be asked. A family with nothing to say is left out.</summary>
        private static List<StartFamilyProgress> ProgressOf(JevRun run) =>
            run.Progress.Where(p => p.Value.Total > 0 && (p.Value.Unusable > 0 || p.Value.Remaining > 0))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new StartFamilyProgress
                {
                    Family = p.Key, Asked = p.Value.Answered + p.Value.Unusable, Answered = p.Value.Answered,
                    Unusable = p.Value.Unusable, Remaining = p.Value.Remaining,
                }).ToList();
    }
}
