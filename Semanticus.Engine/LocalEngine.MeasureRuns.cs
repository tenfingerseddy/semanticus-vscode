using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine.Lineage;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine;

public sealed partial class LocalEngine
{
    private readonly object measureRunLock = new();
    private readonly SemaphoreSlim measureRunGate = new(1, 1);
    private readonly Dictionary<string, MeasureCheckCache> measureCheckCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreparedMeasureRun> preparedMeasureRuns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> measureRunEdits = new(StringComparer.Ordinal);
    private HashSet<string> lastMeasureRun = new(StringComparer.Ordinal);
    private readonly HashSet<string> sweptMeasures = new(StringComparer.Ordinal);
    private CancellationTokenSource activeMeasureRun;
    private CancellationTokenSource stoppedMeasureRun;
    // Runs that are waiting their turn or reading their candidates: Stop is heard there too (fx30e K4). Stopped ones are kept
    // in stoppedStartingRuns until their run returns, so it says Stopped.
    private readonly HashSet<CancellationTokenSource> startingMeasureRuns = new();
    private readonly HashSet<CancellationTokenSource> stoppedStartingRuns = new();
    private Timer measureSweepTimer;
    private DateTimeOffset lastMeasureEdit = DateTimeOffset.UtcNow;
    private MeasureSweepOptions measureSweepOptions = new();
    private bool measureRunsStopped;
    private bool measureCacheLoaded;
    private int measureSweepTick;
    private readonly AsyncLocal<CancellationToken> measureRunCancellation = new();

    public IMeasureRunOrder MeasureRunOrder { get; set; } = new RuleMeasureRunOrder();
    public IMeasureRunExtras MeasureRunExtras { get; set; }

    private sealed record PreparedMeasureRun(Session Session, long Revision, MeasureRunPlan Plan,
        MeasureRunCandidate[] Candidates, IMeasureRunExtras Extras);

    private sealed class PlannedMeasureRunExtras : IMeasureRunExtras
    {
        private readonly LocalEngine engine;
        private MarkRun current;
        internal PlannedMeasureRunExtras(LocalEngine engine) => this.engine = engine;
        public string Inputs => "formula-contexts-v8|totals-v2|performance-v2|cached-description-v2|" + engine.DescriptionMatchActive;
        internal void Prepare(MarkRun run) => current = run;
        private MarkRun RequireRun(string reference, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var run = current;
            if (run == null || run.Reference != reference || run.Cancellation != token
                || !engine.MarkRunCurrent(run) || run.Session.Revision != run.Revision)
                throw new OperationCanceledException("The planned measure inputs changed.", token);
            return run;
        }
        public Task<MeasureMark[]> ContextsAsync(string measureRef, CancellationToken cancellationToken)
        {
            // The marks worker already made one context plan and its query set. Reuse both for timing.
            var run = RequireRun(measureRef, cancellationToken);
            return Task.FromResult(CopyMarks(run.View).Marks.Where(m => m.Name is "Filters" or "Contexts" or "Totals").ToArray());
        }
        public async Task<MeasureMark> PerformanceAsync(string measureRef, CancellationToken cancellationToken)
        {
            var run = RequireRun(measureRef, cancellationToken);
            await engine.EnterMarksWorkerAsync(run, cancellationToken).ConfigureAwait(false);
            try
            {
                RequireRun(measureRef, cancellationToken);
                if (engine.marksStructureHold?.Covers(measureRef) == true)
                    return engine.HeldMark(measureRef, "Performance");
                if (run.Live != null && !engine.MarkConnectionMatches(run.Session))
                    return MeasureMarkJudge.NotRun("Performance", "The connected model does not match this editing model.");
                using var cancellation = run.Live?.WithMeasureRunCancellation(cancellationToken);
                using var sqlCancellation = engine.WithMeasureRunToken(cancellationToken);
                await engine.TimeMeasureMarksAsync(run).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return CopyMarks(run.View).Marks.Single(m => m.Name == "Performance");
            }
            finally { engine.LeaveMarksWorker(); }
        }
        public Task<double?> FastMatchAsync(MeasureRunCandidate candidate, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = engine.CachedDescriptionMatch(engine._sessions.Require(), candidate.MeasureRef,
                new(candidate.Description ?? "", candidate.Formula ?? "", candidate.MeasureRef[(candidate.MeasureRef.IndexOf('/') + 1)..]));
            return Task.FromResult(match.Score is int score ? (double?)(score / 100.0) : null);
        }
    }

    private void AttachMeasureRuns()
    {
        MeasureRunExtras = new PlannedMeasureRunExtras(this);
        _sessions.Bus.Changed += MeasureRunEdited;
        _sessions.SessionReplaced += ReplaceMeasureRuns;
        measureSweepTimer = new Timer(_ => MeasureSweepTick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void MeasureRunEdited(ChangeNotification change)
    {
        // Marks, report readings and save notices use this bus too. Only a commit is an edit.
        if (change?.IsCommit != true || change.SessionId != _sessions.SessionId) return;
        lock (measureRunLock)
        {
            lastMeasureEdit = DateTimeOffset.UtcNow;
            activeMeasureRun?.Cancel();
            sweptMeasures.Clear();
            foreach (var delta in change.Deltas ?? Array.Empty<ChangeDelta>())
                if (delta.Ref != null) measureRunEdits[delta.Ref] = change.Revision;
            preparedMeasureRuns.Clear();
        }
    }

    private void ReplaceMeasureRuns()
    {
        lock (measureRunLock)
        {
            activeMeasureRun?.Cancel();
            measureCheckCache.Clear(); preparedMeasureRuns.Clear(); measureRunEdits.Clear(); lastMeasureRun.Clear(); sweptMeasures.Clear();
            measureCacheLoaded = false; lastMeasureEdit = DateTimeOffset.UtcNow; measureSweepOptions = new();
        }
    }

    private void StopMeasureRuns()
    {
        _sessions.Bus.Changed -= MeasureRunEdited;
        _sessions.SessionReplaced -= ReplaceMeasureRuns;
        lock (measureRunLock) { measureRunsStopped = true; activeMeasureRun?.Cancel(); foreach (var s in startingMeasureRuns) s.Cancel(); }
        measureSweepTimer?.Dispose();
    }

    private async void MeasureSweepTick()
    {
        if (Interlocked.Exchange(ref measureSweepTick, 1) != 0) return;
        try { await SweepMeasureChecksAsync(false).ConfigureAwait(false); }
        catch (Exception) { /* A background check cannot fail the editing session. */ }
        finally { Volatile.Write(ref measureSweepTick, 0); }
    }

    public async Task<MeasureRunPlan> PlanMeasureRunAsync(MeasureRunRequest request = null)
    {
        var session = _sessions.Require();
        var extras = MeasureRunExtras;
        var revision = session.Revision;
        var candidates = await MeasureRunCandidatesAsync(session, extras, estimateContexts: true).ConfigureAwait(false);
        var plan = MeasureRunPlanner.Build(candidates, request, MeasureRunOrder);
        plan = plan with { Id = MeasureRunPlanner.Digest(session.Id + plan.Id) };
        lock (measureRunLock)
        {
            if (_sessions.Current != session || session.Revision != revision)
                throw new InvalidOperationException("The model changed while planning. Read the plan again.");
            // Plans carry no model objects. Retain a small set so both doors can read and execute the same plan.
            if (preparedMeasureRuns.Count >= 16) preparedMeasureRuns.Remove(preparedMeasureRuns.Keys.First());
            preparedMeasureRuns[plan.Id] = new(session, revision, plan, candidates, extras);
        }
        return plan;
    }

    public async Task<MeasureRunResult> RunMeasurePlanAsync(string planId)
    {
        PreparedMeasureRun prepared;
        lock (measureRunLock)
            if (!preparedMeasureRuns.TryGetValue(planId ?? "", out prepared))
                throw new InvalidOperationException("Read the run plan before starting its checks. The model may have changed.");
        // Registered before the candidate read: Stop pressed in the first seconds after Run stops it (fx30e K4).
        using var stop = StartingMeasureRun();
        try
        {
            try { await measureRunGate.WaitAsync(stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return StoppedBeforeStart(stop, prepared.Plan.Id); }
            try
            {
                var current = await MeasureRunCandidatesAsync(prepared.Session, prepared.Extras).ConfigureAwait(false);
                if (stop.IsCancellationRequested) return StoppedBeforeStart(stop, prepared.Plan.Id);
                if (_sessions.Current != prepared.Session || prepared.Session.Revision != prepared.Revision
                    || prepared.Extras != MeasureRunExtras || !SameMeasureInputs(prepared.Candidates, current))
                    throw new InvalidOperationException("The check inputs changed. Read the run plan again.");
                lock (measureRunLock)
                { lastMeasureRun = prepared.Plan.Picks.Select(p => p.MeasureRef).ToHashSet(StringComparer.Ordinal); sweptMeasures.Clear(); }
                var result = await ExecuteMeasureRunAsync(prepared, true, stop).ConfigureAwait(false);
                return result with { Results = await AlignedStatesAsync(prepared.Session, result.Results).ConfigureAwait(false) };
            }
            finally { measureRunGate.Release(); }
        }
        finally { EndStartingMeasureRun(stop); }
    }

    private CancellationTokenSource StartingMeasureRun()
    {
        var stop = new CancellationTokenSource();
        lock (measureRunLock) startingMeasureRuns.Add(stop);
        return stop;
    }

    private void EndStartingMeasureRun(CancellationTokenSource stop)
    {
        lock (measureRunLock) { startingMeasureRuns.Remove(stop); stoppedStartingRuns.Remove(stop); }
    }

    private MeasureRunResult StoppedBeforeStart(CancellationTokenSource stop, string planId)
    {
        lock (measureRunLock) stoppedStartingRuns.Remove(stop);
        return new() { PlanId = planId, Stopped = true };
    }

    public Task<MeasureSweepOptions> ConfigureMeasureSweepAsync(MeasureSweepOptions options)
    {
        _sessions.Require();
        options ??= new();
        if (!double.IsFinite(options.IdleMinutes) || options.IdleMinutes < 0)
            throw new ArgumentException("The idle delay must be zero or more minutes.");
        lock (measureRunLock) measureSweepOptions = options with { BatchSize = Math.Clamp(options.BatchSize, 1, 10) };
        return Task.FromResult(measureSweepOptions);
    }

    public async Task<MeasureRunResult> SweepMeasureChecksAsync(bool onRequest = true)
    {
        if (!await measureRunGate.WaitAsync(0).ConfigureAwait(false)) return new();
        using var stop = StartingMeasureRun();
        try
        {
            var session = _sessions.Current;
            MeasureSweepOptions options; HashSet<string> excluded;
            lock (measureRunLock)
            {
                options = measureSweepOptions; excluded = new(lastMeasureRun.Concat(sweptMeasures), StringComparer.Ordinal);
                if (measureRunsStopped || session == null || (!onRequest && (!options.Enabled
                    || DateTimeOffset.UtcNow - lastMeasureEdit < TimeSpan.FromMinutes(options.IdleMinutes)))) return new();
            }
            var revision = session.Revision; var extras = MeasureRunExtras;
            var candidates = await MeasureRunCandidatesAsync(session, extras).ConfigureAwait(false);
            if (stop.IsCancellationRequested) return StoppedBeforeStart(stop, null);
            MeasureRunCandidate[] next;
            lock (measureRunLock)
                next = candidates.Where(c => !excluded.Contains(c.MeasureRef))
                    .OrderBy(c => c.LastChecked).ThenBy(c => c.MeasureRef, StringComparer.Ordinal).Take(options.BatchSize).ToArray();
            if (next.Length == 0) return new();
            var plan = MeasureRunPlanner.Build(candidates.Select(c => c with
                { IncludePerformance = options.AllowPerformance && c.IncludePerformance }).ToArray(), new()
            {
                Selection = new() { Measures = next.Select(c => c.MeasureRef).ToArray() }, IncludePerformance = options.AllowPerformance
            });
            return await ExecuteMeasureRunAsync(new(session, revision, plan, candidates, extras), false, stop).ConfigureAwait(false);
        }
        finally { EndStartingMeasureRun(stop); measureRunGate.Release(); }
    }

    public async Task<MeasureCheckCache[]> GetMeasureCheckCacheAsync()
    {
        var session = _sessions.Require();
        var current = await MeasureRunCandidatesAsync(session, MeasureRunExtras).ConfigureAwait(false);
        await session.ReadAsync(model => { RefreshCheckExpected(session, model); return 0; }).ConfigureAwait(false);
        lock (marksLock) if (marksSession == session) DropConsentLines(session, load: true);
        var stamp = MarkSavedStamp(session);
        // The records are the check state itself: a mark a run has not reached already holds its last result, pending.
        MeasureCheckCache[] states;
        lock (measureRunLock)
            states = current.Select(c =>
            {
                var stored = measureCheckCache.GetValueOrDefault(c.MeasureRef) ?? new() { MeasureRef = c.MeasureRef };
                var now = CheckedOnKey(session, session.Revision, c.InputSnapshot?.GetProperty("ExtraChecks").GetString(), stamp, _live);
                var fresh = stored.LastChecked != null && (stored.Inputs == c.Inputs || stored.CheckedOn == now);
                return FinishCheckState(stored with { Stale = !fresh });
            }).ToArray();
        return await AlignedStatesAsync(session, states).ConfigureAwait(false);
    }

    // Every door that hands out check states gives the Alignment mark as the Description score the Checks page shows
    // (fx29b B). Reading many measures asks for no new score.
    private async Task<MeasureCheckCache[]> AlignedStatesAsync(Session session, MeasureCheckCache[] states)
    {
        if (states.Length == 0 || _sessions.Current != session) return states;
        var inputs = await session.ReadAsync(model => model.AllMeasures
            .ToDictionary(m => ObjectRefs.For(m), m => DescriptionInput(m), StringComparer.Ordinal)).ConfigureAwait(false);
        // Every unscored line, the cheap pass's Description match too, reads the state now (fx29c).
        var unscored = await UnscoredReasonsAsync().ConfigureAwait(false);
        return states.Select(s => !inputs.TryGetValue(s.MeasureRef, out var input) ? s : s with {
            Marks = s.Marks.Length == 0 ? s.Marks : unscored.Settle(WithAlignment(s.Marks, CachedDescriptionMatch(session, s.MeasureRef, input),
                unscored.Description(input.Description)), input.Description),
            Cheap = s.Cheap == null ? null : unscored.Settle(s.Cheap, input.Description) }).ToArray();
    }

    private static DateTimeOffset? LatestMarkTime(MeasureMark[] marks) => marks.Where(m => m.Name != "Alignment")
        .Select(m => DateTimeOffset.TryParse(m.LastRan, out var time) ? (DateTimeOffset?)time : null).Max();

    // `stop` is the run's own, registered since Run was pressed (fx30e K4); the caller disposes it.
    private async Task<MeasureRunResult> ExecuteMeasureRunAsync(PreparedMeasureRun prepared, bool allCheap, CancellationTokenSource stop)
    {
        lock (measureRunLock)
        {
            if (measureRunsStopped || _sessions.Current != prepared.Session || prepared.Session.Revision != prepared.Revision) return new() { StoppedOnEdit = true };
            activeMeasureRun = stop;
        }
        var results = new Dictionary<string, MeasureCheckCache>(StringComparer.Ordinal);
        var token = stop.Token;
        var interrupted = false;
        try
        {
            var selected = prepared.Plan.Picks.Select(p => p.MeasureRef).ToHashSet(StringComparer.Ordinal);
            var candidatesByRef = prepared.Candidates.ToDictionary(c => c.MeasureRef, StringComparer.Ordinal);
            var cheap = allCheap ? prepared.Candidates : prepared.Candidates.Where(c => selected.Contains(c.MeasureRef)).ToArray();
            // One relationship scan supplies orphan-key checks for every measure. No per-measure scan is repeated.
            var relationships = await RelationshipScanAsync(prepared.Revision, _live, "human", token).ConfigureAwait(false);
            foreach (var candidate in cheap)
            {
                token.ThrowIfCancellationRequested();
                var cheapMarks = await CheapMeasureChecksAsync(candidate, relationships, prepared.Extras, token).ConfigureAwait(false);
                lock (measureRunLock)
                {
                    EnsureMeasureRunCurrent(prepared, token);
                    results[candidate.MeasureRef] = WriteCheckState(prepared.Session, candidate.MeasureRef, old => old with
                    { Cheap = cheapMarks.Marks, CheapInputs = candidate.Inputs, CheapInputSnapshot = candidate.InputSnapshot,
                        MatchScore = cheapMarks.Score, Stale = old.Inputs != candidate.Inputs || old.LastChecked == null });
                }
            }
            foreach (var batch in prepared.Plan.Picks.Chunk(prepared.Plan.BatchSize))
            foreach (var pick in batch)
            {
                token.ThrowIfCancellationRequested();
                var timer = Stopwatch.StartNew();
                var (worker, checkedMarks) = await PlannedMeasureMarksAsync(prepared.Session, pick.MeasureRef, token, prepared.Extras).ConfigureAwait(false);
                var marks = checkedMarks.Marks.ToList();
                if (prepared.Extras != null)
                {
                    token.ThrowIfCancellationRequested();
                    MeasureMark[] contexts;
                    try { contexts = await prepared.Extras.ContextsAsync(pick.MeasureRef, token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { contexts = new[] { MeasureMarkJudge.NotRun("Filters", "The context check could not run: " + ex.Message), MeasureMarkJudge.NotRun("Contexts", "The context check could not run: " + ex.Message) }; }
                    foreach (var name in new[] { "Filters", "Contexts", "Totals" })
                    {
                        if (name == "Totals" && contexts?.Any(m => m?.Name == name) != true) continue;
                        var mark = contexts?.FirstOrDefault(m => m?.Name == name)
                            ?? MeasureMarkJudge.NotRun(name, "The context checker did not return this mark.");
                        marks.RemoveAll(m => m.Name == name); marks.Add(mark);
                    }
                }
                marks.RemoveAll(m => m.Name == "Performance");
                var performance = prepared.Plan.IncludePerformance || prepared.Plan.PerformanceMeasures.Contains(pick.MeasureRef, StringComparer.Ordinal);
                if (performance)
                {
                    token.ThrowIfCancellationRequested();
                    try { marks.Add(prepared.Extras == null ? MeasureMarkJudge.NotRun("Performance", "The Performance checker is not connected.")
                        : await prepared.Extras.PerformanceAsync(pick.MeasureRef, token).ConfigureAwait(false)); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { marks.Add(MeasureMarkJudge.NotRun("Performance", "The Performance check could not run: " + ex.Message)); }
                }
                else marks.Add(Coded(MeasureMarkJudge.NotRun("Performance", "Include performance is off for this measure and run."), "not_timed"));
                // Keep the worker's mark slots stable when publishing the plan's completed checks.
                marks = EmptyMarks(pick.MeasureRef, prepared.Revision, "").Marks
                    .Select(slot => marks.Single(m => m.Name == slot.Name)).ToList();
                var now = DateTimeOffset.UtcNow;
                foreach (var mark in marks.Where(m => m.State != "not run")) mark.LastRan ??= now.ToString("O");
                lock (marksLock)
                    if (measureMarks.GetValueOrDefault(pick.MeasureRef) == worker && worker.Revision == prepared.Revision)
                    {
                        worker.View.Marks = CopyMarks(new() { Marks = marks.ToArray() }).Marks;
                        worker.View.LastChecked = now;
                        worker.IncludePerformance = performance;
                    }
                lock (marksLock) lock (measureRunLock)
                {
                    EnsureMeasureRunCurrent(prepared, token);
                    // The row is readable before the whole run returns: the write pushes it. A check result is not a model edit.
                    results[pick.MeasureRef] = WriteCheckState(prepared.Session, pick.MeasureRef, previous =>
                    {
                        foreach (var mark in marks) mark.LastRan ??= previous.Marks.FirstOrDefault(m => m.Name == mark.Name)?.LastRan;
                        var merged = previous.Marks;
                        foreach (var mark in marks) merged = MergeMark(merged, mark);
                        // A slot the run did not reach keeps its last result; one with no result is not shown.
                        merged = merged.Where(m => !MeasureCheckVerdict.InProgress(m)).Select(Copy).ToArray();
                        foreach (var mark in merged) { mark.Pending = false; mark.Progress = null; }
                        // An edit's comparison still running keeps Equivalence pending.
                        if (markEdits.TryGetValue(pick.MeasureRef, out var edit) && edit.Pending) merged = MergeMark(merged, Waiting("Equivalence"));
                        return previous with
                        { Inputs = pick.Inputs, InputSnapshot = candidatesByRef[pick.MeasureRef].InputSnapshot,
                            LastChecked = now, Seconds = marks.Any(m => m.State is "passed" or "suspect") ? timer.Elapsed.TotalSeconds : null,
                            Marks = merged, Stale = false, IncludedPerformance = performance };
                    });
                    // The run ends here, once, after its last extra: only now is the measure checked on this revision (v-fx27 D1).
                    FinishRunCheckState(worker, measureMarks.GetValueOrDefault(pick.MeasureRef) == worker, MarkRunCurrent(worker));
                    if (results[pick.MeasureRef] != null) results[pick.MeasureRef] = FinishCheckState(measureCheckCache[pick.MeasureRef]);
                    if (!allCheap) sweptMeasures.Add(pick.MeasureRef);
                }
            }
        }
        catch (OperationCanceledException) { interrupted = true; }
        finally
        {
            // A measure the run did not finish (Stop, an edit, a failed extra) ends now, unfinished.
            SettleOpenRuns(token);
            lock (measureRunLock)
            {
                if (activeMeasureRun == stop) activeMeasureRun = null;
                if (_sessions.Current == prepared.Session) SaveMeasureCache(prepared.Session);
            }
        }
        bool stopped;
        lock (measureRunLock) { stopped = stoppedMeasureRun == stop || stoppedStartingRuns.Remove(stop); if (stoppedMeasureRun == stop) stoppedMeasureRun = null; }
        return new() { PlanId = prepared.Plan.Id, Stopped = stopped, StoppedOnEdit = !stopped && (interrupted || token.IsCancellationRequested),
            Results = results.Values.Where(c => prepared.Plan.Picks.Any(p => p.MeasureRef == c.MeasureRef))
                .OrderBy(c => c.MeasureRef, StringComparer.Ordinal).Select(CopyMeasureCache).ToArray() };
    }

    private void EnsureMeasureRunCurrent(PreparedMeasureRun prepared, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_sessions.Current != prepared.Session || prepared.Session.Revision != prepared.Revision)
            throw new OperationCanceledException();
    }

    private async Task<(MeasureMark[] Marks, double? Score)> CheapMeasureChecksAsync(MeasureRunCandidate candidate,
        TestSuiteRunResult relationships, IMeasureRunExtras extras, CancellationToken token)
    {
        var validation = await ValidateDaxAsync(candidate.Formula).ConfigureAwait(false);
        var lint = await LintDaxAsync(candidate.Formula).ConfigureAwait(false);
        var parse = new MeasureMark { Name = "Parse", State = validation.Valid && validation.Diagnostics.Length == 0 ? "passed" : "suspect",
            Sentence = validation.Diagnostics.Length == 0 ? "The formula parses and its references resolve." : string.Join(" ", validation.Diagnostics.Select(d => d.Message)) };
        var lintMark = new MeasureMark { Name = "Lint", State = lint.Findings.Length == 0 ? "passed" : "suspect",
            Sentence = lint.Findings.Length == 0 ? "No lint findings." : string.Join(" ", lint.Findings.Select(d => d.Message)) };
        var orphan = relationships?.Relationships?.Relationships?.Where(r => candidate.FactTables.Contains(r.ManyTable, StringComparer.Ordinal)).ToArray();
        var orphanMark = orphan == null || orphan.Length == 0 ? MeasureMarkJudge.NotRun("Orphan keys", "No relationship counts apply to this measure.")
            : new MeasureMark { Name = "Orphan keys", State = orphan.Any(r => r.ReferentialIntegrity?.Verdict == Verdict.Fail) ? "suspect"
                : orphan.All(r => r.ReferentialIntegrity?.Verdict == Verdict.Pass) ? "passed" : "not run",
                Sentence = orphan.Any(r => r.ReferentialIntegrity?.Verdict == Verdict.Fail) ? "The model has orphan relationship keys."
                    : orphan.All(r => r.ReferentialIntegrity?.Verdict == Verdict.Pass) ? "No orphan relationship keys." : "Some orphan counts are unavailable." };
        double? score = null;
        if (extras != null)
            try { score = await extras.FastMatchAsync(candidate, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        if (score != null && (!double.IsFinite(score.Value) || score < 0 || score > 1)) score = null;
        var alignment = score == null ? ScoringCopy.NotScored("Description match", (await UnscoredReasonsAsync().ConfigureAwait(false)).Description(candidate.Description))
            : new MeasureMark { Name = "Description match", State = score < .6 ? "suspect" : "passed", Sentence = "Description match score: " + score.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "." };
        var marks = new[] { parse, lintMark, orphanMark, alignment };
        foreach (var mark in marks.Where(m => m.State != "not run")) mark.LastRan = DateTimeOffset.UtcNow.ToString("O");
        return (marks, score);
    }

    private async Task<MeasureRunCandidate[]> MeasureRunCandidatesAsync(Session session, IMeasureRunExtras extras, bool estimateContexts = false)
    {
        Dictionary<string, MeasureCheckCache> cache; Dictionary<string, long> edits;
        lock (measureRunLock)
        {
            if (_sessions.Current != session) throw new InvalidOperationException("The open model changed. Read its plan again.");
            LoadMeasureCache(session);
            cache = new(measureCheckCache, StringComparer.Ordinal); edits = new(measureRunEdits, StringComparer.Ordinal);
        }
        var coverage = CheckedCoverageFor(session);
        var saved = MarkSavedStamp(session);
        var extraInputs = extras?.Inputs ?? "none";
        var live = _live;
        var connection = live == null ? "offline" : live.DataSource + "|" + live.Database;
        return await session.ReadAsync(model =>
        {
            var graph = LineageGraph.Build(model);
            var markSnapshot = CaptureCheckSnapshot(model, session);
            var dependencies = graph.Edges.Where(e => e.Kind == "dependsOn").GroupBy(e => e.From)
                .ToDictionary(g => g.Key, g => g.Select(e => e.To).ToArray(), StringComparer.Ordinal);
            var reports = coverage.Parts.Count == 0 ? null : LineageGraph.AnalyzeReports(model, coverage.Parts, coverage.ChoiceIds);
            var reportUses = (reports?.Reports ?? Array.Empty<ReportUsage>()).SelectMany(r =>
                (r.Visuals?.Length > 0 ? r.Visuals.SelectMany(v => v.UsedRefs) : r.UsedRefs ?? Array.Empty<string>()))
                .GroupBy(r => r, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            // Structural inputs invalidate data checks after a relationship, source or column edit.
            var structure = JsonSerializer.Serialize(new
            {
                ContextColumns = markSnapshot.ContextModel.Columns,
                Tables = model.Tables.Select(t => new { t.Name, Formula = (t as CalculatedTable)?.Expression, Partitions = t.Partitions.Select(p => p.Expression),
                    Columns = t.Columns.Select(c => new { c.Name, Type = c.DataType.ToString(), Formula = (c as CalculatedColumn)?.Expression,
                        Source = (c as DataColumn)?.SourceColumn, SortBy = c.SortByColumn?.Name }) }),
                Relationships = graph.Edges.Where(e => e.Kind == "relationship"),
                Settings = model.Relationships.OfType<SingleColumnRelationship>().Select(r => new { r.Name, r.IsActive, Cross = r.CrossFilteringBehavior.ToString() }),
                Roles = model.Roles.Select(r => new { r.Name, Permission = r.ModelPermission.ToString(),
                    Tables = r.TablePermissions.Select(p => new { Table = p.Table.Name, p.FilterExpression }) }),
                CalculationGroups = model.Tables.OfType<CalculationGroupTable>().Select(t => new { t.Name, t.CalculationGroupPrecedence,
                    Items = t.CalculationItems.Select(i => new { i.Name, i.Expression, i.FormatStringExpression }) })
            });
            string[] Closure(string reference)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal); var pending = new Stack<string>(); pending.Push(reference);
                while (pending.Count > 0)
                { var r = pending.Pop(); if (!seen.Add(r)) continue; foreach (var dep in dependencies.GetValueOrDefault(r, Array.Empty<string>())) pending.Push(dep); }
                return seen.OrderBy(r => r, StringComparer.Ordinal).ToArray();
            }
            var closures = model.AllMeasures.ToDictionary(ObjectRefs.For, m => Closure(ObjectRefs.For(m)), StringComparer.Ordinal);
            return model.AllMeasures.Select(measure =>
            {
                var reference = ObjectRefs.For(measure); var closure = closures[reference]; cache.TryGetValue(reference, out var previous);
                var bodies = closure.Select(r =>
                {
                    var obj = ObjectRefs.Resolve(model, r);
                    return new { Ref = r, Expression = obj is Function function ? function.Expression : (obj as IExpressionObject)?.Expression,
                        Description = (obj as IDescriptionObject)?.Description, FormatString = (obj as IFormattableObject)?.FormatString,
                        FormatStringExpression = (obj as Measure)?.FormatStringExpression, DisplayFolder = (obj as Measure)?.DisplayFolder,
                        IncludePerformance = markSnapshot.TimedMeasures.Contains(r),
                        ExpectedTotals = (obj as Measure)?.GetAnnotation("Semanticus.Totals.Expected"),
                        OnPurposeFilters = (obj as Measure)?.GetAnnotation(FiltersOnPurposeAnnotation) };
                }).ToArray();
                var performanceSettings = markSnapshot.PerformanceSettings;
                var totalsSettings = markSnapshot.TotalsSettings;
                var inputs = MeasureRunPlanner.Digest(JsonSerializer.Serialize(new { bodies, structure, saved, connection, extraInputs, performanceSettings, totalsSettings }));
                var inputSnapshot = JsonSerializer.SerializeToElement(new
                { bodies, Structure = MeasureRunPlanner.Digest(structure), SavedChecks = saved, Connection = connection,
                    ExtraChecks = extraInputs, PerformanceSettings = performanceSettings, TotalsSettings = totalsSettings });
                var tokens = DaxScan.Tokenize(measure.Expression ?? "");
                var functions = tokens.Where((t, i) => t.Kind == DaxScan.Kind.Word && i + 1 < tokens.Count && tokens[i + 1].Kind == DaxScan.Kind.LParen)
                    .Select(t => t.Text.ToUpperInvariant()).ToHashSet();
                var risk = MeasureRunRisk(functions, tokens);
                var helper = tokens.Count > 1 && tokens[0].Kind == DaxScan.Kind.Word && tokens[0].Text.Equals("FORMAT", StringComparison.OrdinalIgnoreCase)
                    || tokens.Count == 1 && tokens[0].Kind is DaxScan.Kind.Name or DaxScan.Kind.Number or DaxScan.Kind.String
                        && reportUses.GetValueOrDefault(reference) == 0 && !closures.Any(p => p.Key != reference && p.Value.Contains(reference));
                // A rough first estimate uses the rule plan, without running queries or calling a scoring service.
                var contextChecks = 24;
                if (estimateContexts)
                {
                    var contextMeasure = markSnapshot.ContextModel.Measures.Single(m => m.Table == measure.Table.Name && m.Name == measure.Name);
                    var contextPlan = new MeasureContexts.MeasureContextPlanner().Plan(new(markSnapshot.ContextModel, contextMeasure));
                    contextChecks = new[] { "Filters", "Contexts", "Totals" }.Sum(name => contextPlan.ForMark(name).Count());
                }
                return new MeasureRunCandidate
                {
                    MeasureRef = reference, Table = measure.Table.Name, Folder = measure.DisplayFolder, Formula = measure.Expression,
                    Description = measure.Description, Inputs = inputs, InputSnapshot = inputSnapshot, Helper = helper,
                    IncludePerformance = markSnapshot.TimedMeasures.Contains(reference),
                    FactTables = markSnapshot.Closure(reference).SelectMany(f => f.FactTables).Distinct().ToArray(),
                    Changed = previous?.LastChecked != null ? MeasureContentChanged(previous, inputSnapshot, inputs) : closure.Any(edits.ContainsKey),
                    BaseMeasure = !closure.Any(r => r != reference && r.StartsWith("measure:", StringComparison.Ordinal)),
                    Dependents = closures.Count(p => p.Key != reference && p.Value.Contains(reference)), ReportUses = reportUses.GetValueOrDefault(reference), Risk = risk,
                    MatchScore = previous?.MatchScore, Suspect = previous?.Marks.Concat(previous.Cheap).Any(m => m.State == "suspect") == true,
                    LastChecked = previous?.LastChecked, LastSeconds = previous?.Seconds, ContextChecks = contextChecks
                };
            }).OrderBy(c => c.MeasureRef, StringComparer.Ordinal).ToArray();
        }).ConfigureAwait(false);
    }

    private static bool MeasureContentChanged(MeasureCheckCache previous, JsonElement current, string inputs)
    {
        if (previous.InputSnapshot is not { } prior) return previous.Inputs != inputs;
        // Timing still invalidates check evidence, but it is not a change to a measure's content (rc24 gap 2).
        static string Content(JsonElement snapshot)
        {
            var node = JsonNode.Parse(snapshot.GetRawText()).AsObject();
            node.Remove("PerformanceSettings");
            if (node["bodies"] is JsonArray bodies)
                foreach (var body in bodies.OfType<JsonObject>()) body.Remove("IncludePerformance");
            return node.ToJsonString();
        }
        return Content(prior) != Content(current);
    }

    private static string MeasureRunRisk(HashSet<string> functions, IReadOnlyList<DaxScan.Tok> tokens)
    {
        if (functions.Overlaps(new[] { "CALCULATE", "CALCULATETABLE", "ALL", "ALLSELECTED", "REMOVEFILTERS", "ALLEXCEPT", "KEEPFILTERS", "TREATAS", "USERELATIONSHIP", "CROSSFILTER" })) return "filter changes";
        if (functions.Overlaps(new[] { "DATEADD", "SAMEPERIODLASTYEAR", "TOTALYTD", "TOTALMTD", "TOTALQTD", "DATESYTD", "DATESMTD", "DATESQTD", "PARALLELPERIOD", "PREVIOUSYEAR" })) return "date calculations";
        if (functions.Overlaps(new[] { "SUMX", "AVERAGEX", "MINX", "MAXX", "COUNTX", "RANKX", "FILTER", "PRODUCTX", "MEDIANX" })) return "row calculations";
        if (functions.Contains("DIVIDE") || tokens.Any(t => t.Kind == DaxScan.Kind.Op && t.Text == "/")) return "ratios";
        if (functions.Contains("DISTINCTCOUNT")) return "distinct count";
        if (functions.Contains("AVERAGE")) return "average";
        if (functions.Overlaps(new[] { "LASTDATE", "FIRSTDATE", "LASTNONBLANK", "FIRSTNONBLANK", "CLOSINGBALANCEMONTH", "OPENINGBALANCEMONTH" })) return "first or last values over dates";
        return null;
    }

    private static bool SameMeasureInputs(MeasureRunCandidate[] before, MeasureRunCandidate[] after)
        => before.Length == after.Length && before.Zip(after).All(p => p.First.MeasureRef == p.Second.MeasureRef && p.First.Inputs == p.Second.Inputs);

    // The plan owns the run it starts: the worker checks the marks, and the plan ends the run after its extras.
    private async Task<(MarkRun Run, MeasureMarksResult Marks)> PlannedMeasureMarksAsync(Session session, string reference, CancellationToken token, IMeasureRunExtras extras)
    {
        var snapshot = await session.ReadAsync(m => CaptureCheckSnapshot(m, session)).ConfigureAwait(false);
        MarkRun run;
        lock (marksLock)
            run = StartMarkRun(session, null, snapshot, reference, null, session.Revision, false, null, token,
                externalContexts: extras != null && extras is not PlannedMeasureRunExtras, extraInputs: extras?.Inputs ?? "none", planned: true);
        if (extras is PlannedMeasureRunExtras planned) planned.Prepare(run);
        return (run, await run.Completion.Task.WaitAsync(token).ConfigureAwait(false));
    }

    private string MeasureCacheFile(Session session)
    {
        var place = PlaceFor(session);
        return place?.Dir == null ? null : Path.Combine(place.Dir, "measure-checks-" + MeasureRunPlanner.Digest(place.Identity ?? session.Id)[..16] + ".json");
    }

    private void LoadMeasureCache(Session session)
    {
        if (measureCacheLoaded) return;
        measureCacheLoaded = true;
        try
        {
            var file = MeasureCacheFile(session);
            if (file != null && File.Exists(file))
                foreach (var cached in JsonSerializer.Deserialize<MeasureCheckCache[]>(File.ReadAllText(file)) ?? Array.Empty<MeasureCheckCache>())
                {
                    // A record saved mid-run is not running in this process. Its pending marks keep their last result.
                    foreach (var mark in cached.Marks) { mark.Pending = false; mark.Progress = null; }
                    measureCheckCache[cached.MeasureRef] = cached with
                    { Running = false, Version = ++checkVersion, Marks = cached.Marks.Where(m => !MeasureCheckVerdict.InProgress(m)).ToArray() };
                }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }

    private void SaveMeasureCache(Session session)
    {
        if (DryRunScope.Current != null) return;
        try
        {
            var file = MeasureCacheFile(session); if (file == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var staging = file + ".new";
            File.WriteAllText(staging, JsonSerializer.Serialize(measureCheckCache.Values.OrderBy(c => c.MeasureRef, StringComparer.Ordinal)));
            File.Move(staging, file, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static MeasureCheckCache CopyMeasureCache(MeasureCheckCache cache) => cache with
    {
        Cheap = CopyMarks(new() { Marks = cache.Cheap }).Marks,
        Marks = CopyMarks(new() { Marks = cache.Marks }).Marks
    };

    private IDisposable WithMeasureRunToken(CancellationToken token)
    {
        var scope = new MeasureRunTokenScope(measureRunCancellation, measureRunCancellation.Value);
        measureRunCancellation.Value = token;
        return scope;
    }

    private sealed class MeasureRunTokenScope : IDisposable
    {
        private readonly AsyncLocal<CancellationToken> slot;
        private readonly CancellationToken previous;
        internal MeasureRunTokenScope(AsyncLocal<CancellationToken> slot, CancellationToken previous)
        { this.slot = slot; this.previous = previous; }
        public void Dispose() => slot.Value = previous;
    }
}
