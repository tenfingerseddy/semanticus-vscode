using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine;

public sealed partial class LocalEngine
{
    private readonly object marksLock = new();
    private readonly SemaphoreSlim marksWorker = new(1, 1);
    private readonly Dictionary<string, MarkRun> measureMarks = new(StringComparer.Ordinal);
    private readonly List<MarkRun> marksQueue = new();
    private MarkRun activeMarks;
    private readonly Dictionary<string, MarkEditResult> markEdits = new(StringComparer.Ordinal);
    private Session marksSession;
    private MarkEditObserver marksObserver;
    // The one relationship scan of a revision (RelationshipScanAsync). Setting it to null makes the next check start a new one;
    // a scan still running keeps serving the checks that already wait on it. Under marksLock.
    private RelationshipScan relationshipScan;
    private bool marksStopped;
    // The token of the checks a read starts (a card, the assistant). Stop cancels it and makes a new one. Under marksLock.
    private CancellationTokenSource markReads = new();
    // The runs whose end is not written yet. A run ends once (FinishRunCheckState): its worker ends it, or the plan run that
    // owns it does, after the run's last extra. Under marksLock.
    private readonly HashSet<MarkRun> openMarkRuns = new();
    // The structure the working copy has and the connected model has not (StructureHoldFor), or null. Replaced whole.
    private volatile StructureHold marksStructureHold;

    private sealed class MarkEditResult
    {
        internal long Revision;
        internal bool Pending;
        internal MeasureMark Mark;
        internal MeasureMark Performance;
        internal MeasureMarkSnapshot Before;
        internal MarkEditResult Previous;
    }

    // An input more than one check reads belongs to the engine, not to the run that asked first (fx29a). It runs under its own
    // token, so one run's Stop or edit never becomes another run's result.
    private sealed class RelationshipScan
    {
        internal readonly CancellationTokenSource Stop = new();
        internal long Revision;
        internal LiveConnection Live;
        internal Task<TestSuiteRunResult> Task;
        internal int Waiters;
    }

    private sealed class MarkRun
    {
        internal Session Session;
        internal LiveConnection Live;
        internal long Revision;
        internal string Reference;
        internal string BeforeReference;
        internal string Axis;
        internal string SavedStamp;
        internal string ExtraInputs;
        internal MeasureMarkSnapshot Before;
        internal MeasureMarkSnapshot After;
        internal string CannotCompare;
        internal bool Compare;
        // Started by an edit: the measure's number marks are from before it, so they read stale until they run again (fx34).
        internal bool Stale;
        // An edit's comparison that then reruns the measure's number marks in the same run (fx34, finish line C3).
        internal bool Rerun;
        internal bool IncludePerformance;
        internal bool FullPerformanceSweep;
        internal MeasureContexts.MeasureContextPlan ContextPlan;
        internal MarkContextQuery[] ContextQueries = Array.Empty<MarkContextQuery>();
        internal TotalProbe[] TotalsProbes = Array.Empty<TotalProbe>();
        internal MarkTimingBaseline TimingBaseline;
        internal MarkTimingBaseline BeforeTiming;
        // The worker's own scratch copy. No door reads it: the check state (LocalEngine.CheckState.cs) is what they read.
        internal MeasureMarksResult View;
        internal CancellationToken Cancellation;
        // A plan run's own run: the plan ends it after its extras (timing included), not the worker (v-fx27 D1).
        internal bool Planned;
        internal bool ExternalContexts;
        internal MarkComparisonBatch ComparisonBatch;
        internal readonly TaskCompletionSource<MeasureMarksResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void AttachMeasureMarks()
    {
        _sessions.SessionReplaced += ReplaceMarksSession;
        ReplaceMarksSession();
    }

    private void ReplaceMarksSession()
    {
        lock (marksLock)
        {
            if (marksSession != null && marksObserver != null) marksSession.UnregisterObserver(marksObserver);
            marksSession = _sessions.Current;
            measureMarks.Clear(); markEdits.Clear(); openMarkRuns.Clear(); relationshipScan = null; consentRechecks.Clear(); consentLinesStale = false;
            marksStructureHold = null;
            if (marksSession != null && !marksStopped)
            {
                marksObserver = new MarkEditObserver(this, marksSession);
                marksSession.RegisterObserver(marksObserver);
            }
        }
    }

    /// <summary>The connected model now has every edit of the session: its structure is the one the checks run on (fx31).
    /// Called on the dispatcher thread.</summary>
    private void MarksPublished(Session session)
    {
        lock (marksLock)
        {
            if (marksObserver?.Session != session) return;
            marksObserver.Rebase(session.Model);
            SetStructureHold(session, null);
        }
    }

    private void StopMeasureMarks()
    {
        _sessions.SessionReplaced -= ReplaceMarksSession;
        lock (marksLock)
        {
            marksStopped = true;
            if (marksSession != null && marksObserver != null) marksSession.UnregisterObserver(marksObserver);
            measureMarks.Clear();
            markEdits.Clear();
            openMarkRuns.Clear();
        }
    }

    private sealed class MarkEditObserver : ISessionObserver
    {
        private readonly LocalEngine engine;
        private readonly Session session;
        private MeasureMarkSnapshot before;
        // The structure the connected model has: the model before the first edit made while a matching model was connected,
        // and the model a publish back to it wrote (Rebase). Read and written on the dispatcher thread only.
        private Dictionary<string, StructurePart> connectedStructure;
        internal MarkEditObserver(LocalEngine engine, Session session) { this.engine = engine; this.session = session; }
        internal Session Session => session;
        internal void Rebase(Model model) => connectedStructure = MarkStructure(model);
        public void BeforeCommit(Model model)
        {
            before = engine.MarkConnectionMatches(session) ? engine.CaptureCheckSnapshot(model, session) : null;
            if (before != null && connectedStructure == null) connectedStructure = MarkStructure(model);
        }
        // Words about an object are not its numbers: Equivalence applies after an edit of the measure's numbers (spec section 3;
        // fx30e S1: keeping a Description added "Nothing moved." as a fourth check). An update whose properties are not named
        // is treated as one that can move numbers. Annotations hold the person's words about a measure (the Description's
        // provenance, a Totals waiver, "on purpose", Include performance), never its numbers: keeping a Description writes one.
        private static readonly HashSet<string> WordsOnly = new(StringComparer.OrdinalIgnoreCase) { "Description", "DisplayFolder", "IsHidden", "Synonyms", "Annotations" };
        private static bool NumbersMayMove(ChangeDelta delta) => delta.Kind != "update" || delta.Props is not { Length: > 0 } props || props.Any(p => !WordsOnly.Contains(p));

        public void AfterCommit(SessionCommit commit)
        {
            if (commit.Deltas.Count == 0) return;
            engine.RefreshCheckExpected(session, commit.Model);
            var hold = connectedStructure == null ? null : StructureHoldFor(connectedStructure, commit.Model);
            if (before == null)
            {
                // There are no before numbers without a matching query model. Keep the reason without
                // copying every formula or starting queries on an offline edit.
                // A measure this commit added has no before, so there is no edit of it to compare (rc28 tester gap 7).
                var added = commit.Deltas.Where(d => d.Kind == "add").Select(d => d.Ref).Where(r => r != null).ToHashSet(StringComparer.Ordinal);
                var roots = commit.Deltas.Where(d => d.Kind == "update" && !added.Contains(d.Ref) && NumbersMayMove(d)).Select(d => d.Ref).Where(r => r != null).ToArray();
                var targets = roots.Where(r => r.StartsWith("measure:", StringComparison.Ordinal))
                    .Concat(Lineage.LineageGraph.ImpactFrom(commit.Model, roots).Where(n => n.Kind == "measure").Select(n => n.Ref)).Distinct();
                lock (engine.marksLock)
                {
                    engine.SetStructureHold(session, hold);
                    foreach (var reference in targets)
                        engine.markEdits[reference] = new MarkEditResult
                        {
                            Revision = commit.Revision,
                            Mark = MeasureMarkJudge.NotRun("Equivalence", "This edit could not be compared because no matching model was connected.")
                        };
                }
                return;
            }
            var after = engine.CaptureCheckSnapshot(commit.Model, session);
            var changed = commit.Deltas.Where(NumbersMayMove).Select(d => d.Ref).Where(r => r != null).ToHashSet(StringComparer.Ordinal);
            lock (engine.marksLock)
            {
                engine.relationshipScan = null;
                // A structure change starts no comparison: the measures it touches keep their last marks, read as Unknown (fx31, fx33).
                engine.SetStructureHold(session, hold);
                var batch = new MarkComparisonBatch { Session = session };
                // The measure the person edited is compared first; the measures that use it queue behind it (rc27 tester gap 7).
                foreach (var formula in after.Formulas.Values.OrderBy(f => changed.Contains(f.Ref) ? 0 : 1))
                {
                    var previousRef = before.PreviousReference(formula);
                    if (previousRef == null)
                    {
                        // A measure this commit added is new, not renamed: it has no before, so Equivalence does not apply to
                        // it and its first checks give its marks (rc28 tester gap 7: it read "Unknown: Equivalence could not run").
                        if (commit.Deltas.Any(d => d.Ref == formula.Ref && d.Kind == "add")) continue;
                        if (commit.Deltas.Any(d => d.Ref == formula.Ref && d.Kind == "update"))
                            engine.StartMarkRun(session, before, after, formula.Ref, null, commit.Revision, true,
                                "The old measure identity could not be recovered after this rename.", comparisonBatch: batch, stale: true);
                        continue;
                    }
                    var affected = !before.SameClosure(after, formula.Ref)
                        || after.Closure(formula.Ref).Any(f => changed.Contains(f.Ref));
                    if (!affected) continue;
                    var axis = engine.measureMarks.GetValueOrDefault(formula.Ref)?.Axis;
                    engine.StartMarkRun(session, before, after, formula.Ref, axis, commit.Revision, compare: true, null,
                        restore: commit.Label is "undo" or "redo", comparisonBatch: batch, stale: true);
                }
            }
        }
    }

    private string MarkSavedStamp(Session session)
    {
        var dir = TestsDirFor(session);
        var file = dir == null ? null : Path.Combine(dir, TestSuiteStore.SuiteFile);
        if (file == null || !File.Exists(file)) return "none";
        var info = new FileInfo(file);
        return info.LastWriteTimeUtc.Ticks + ":" + info.Length;
    }

    // Measures whose Totals line permission decided, and permission has changed since: the next read checks them again (fx29b F).
    private readonly HashSet<string> consentRechecks = new(StringComparer.Ordinal);

    // Permission was given and the check records may still hold lines it decided. Cleared once they are dropped (fx29b F).
    private bool consentLinesStale;

    /// <summary>Permission for this model was given. A line it decided ("Pro advice is off", a never-saved model, a change
    /// mid-score) is no longer true (fx29b F: after Turn on, get_measure_marks and Properties kept "Pro advice is off").
    /// The Description scores it left are dropped, so the card asks again. The measure is checked again on its next read, as
    /// Check now does, and its line follows the state (SettleStoredLinesAsync): a stored "advice is on now" line was false
    /// once permission was taken back, and asked for a check while the recheck ran (fx30e K1, K3). This never looks up where the model's
    /// files live: that lookup records a live model's identity for its file, and doing it here moved the permission scope
    /// (WindowConsentTests). Records not yet read from disk are dropped by the next read, which looks it up anyway.</summary>
    private void ConsentGiven()
    {
        lock (_descriptionGate)
            foreach (var key in _descriptionMatches.Where(p => p.Value.Match.IsCompletedSuccessfully && p.Value.Match.Result.Score == null)
                .Select(p => p.Key).ToArray())
                _descriptionMatches.Remove(key);
        var session = _sessions.Current;
        if (session == null) return;
        lock (marksLock)
        {
            if (marksStopped || marksSession != session) return;
            foreach (var reference in measureMarks.Where(p => ScoringCopy.ConsentLine(p.Value.View.Marks.FirstOrDefault(m => m.Name == "Totals")?.Sentence))
                .Select(p => p.Key).ToArray())
            { measureMarks.Remove(reference); consentRechecks.Add(reference); }
            consentLinesStale = true;
            DropConsentLines(session, load: false);
        }
    }

    // Called under marksLock. Takes measureRunLock, as a run does.
    private void DropConsentLines(Session session, bool load)
    {
        if (!consentLinesStale) return;
        string[] stale;
        lock (measureRunLock)
        {
            if (!measureCacheLoaded && !load) return;
            LoadMeasureCache(session);
            consentLinesStale = false;
            stale = measureCheckCache.Where(p => ScoringCopy.ConsentLine(p.Value.Marks.FirstOrDefault(m => m.Name == "Totals")?.Sentence))
                .Select(p => p.Key).ToArray();
        }
        consentRechecks.UnionWith(stale);
    }

    private async Task<MeasureMarksResult> ReadMeasureMarksAsync(string reference, string axis, bool refresh,
        bool includePerformance, bool fullPerformanceSweep, string origin)
    {
        var session = _sessions.Current;
        if (session == null) return EmptyMarks(reference, 0, "Open a model to run these checks.");
        var snapshot = await session.ReadAsync(m =>
        {
            var measure = ObjectRefs.Resolve(m, reference) as Measure;
            if (measure == null) throw new ArgumentException("Choose a measure to read its marks.");
            RefreshCheckExpected(session, m);
            return (Reference: ObjectRefs.For(measure), Snapshot: CaptureCheckSnapshot(m, session), Description: DescriptionInput(measure));
        }).ConfigureAwait(false);
        var stamp = MarkSavedStamp(session);
        var extraInputs = MeasureRunExtras?.Inputs ?? "none";
        MeasureCheckCache saved = null;
        bool readSaved, recheck;
        lock (marksLock)
        {
            if (marksSession == session) DropConsentLines(session, load: true);
            recheck = consentRechecks.Remove(snapshot.Reference);
            readSaved = !refresh && !recheck && axis == null && !measureMarks.ContainsKey(snapshot.Reference);
        }
        if (readSaved)
            saved = (await GetMeasureCheckCacheAsync().ConfigureAwait(false)).FirstOrDefault(c => c.MeasureRef == snapshot.Reference && !c.Stale && c.Marks.Length > 0);
        MeasureMarksResult view;
        lock (marksLock)
        {
            if (marksStopped || _sessions.Current != session)
                return EmptyMarks(snapshot.Reference, session.Revision, "The open model changed; read its marks again.");
            measureMarks.TryGetValue(snapshot.Reference, out var run);
            // One checker per check: a card cannot run an outside checker's extras, so it never starts a check in that checker's
            // name. It shows the check state, and its Check now goes through the plan, which runs them.
            // A permission recheck is a Check now the person did not have to press (fx29b F), so it goes through the plan too.
            if (MeasureRunExtras is { } outside && outside is not PlannedMeasureRunExtras)
            {
                if (refresh || recheck) using (ExecutionContext.SuppressFlow()) _ = Task.Run(() => CheckThroughPlanAsync(snapshot.Reference));
                var waiting = EmptyMarks(snapshot.Reference, session.Revision, "This check is waiting to run.");
                waiting.PerformanceSettings = snapshot.Snapshot.PerformanceSettings;
                waiting.IncludePerformanceForMeasure = snapshot.Snapshot.TimedMeasures.Contains(snapshot.Reference);
                view = CheckStateView(run ?? new MarkRun { Session = session, Reference = snapshot.Reference, View = waiting }, session.Revision);
            }
            else
            {
                var timed = origin == "background" ? snapshot.Snapshot.PerformanceSettings.AllowBackground
                    : includePerformance || fullPerformanceSweep || snapshot.Snapshot.TimedMeasures.Contains(snapshot.Reference);
                if (run == null && saved != null && (!timed || saved.IncludedPerformance) && !fullPerformanceSweep)
                {
                    run = new MarkRun { Session = session, Live = _live, Revision = session.Revision, Reference = snapshot.Reference,
                        After = snapshot.Snapshot, SavedStamp = stamp, ExtraInputs = extraInputs, IncludePerformance = saved.IncludedPerformance,
                        View = new() { MeasureRef = snapshot.Reference, Revision = session.Revision, Marks = saved.Marks,
                            LastChecked = saved.LastChecked,
                            PerformanceSettings = snapshot.Snapshot.PerformanceSettings,
                            IncludePerformanceForMeasure = snapshot.Snapshot.TimedMeasures.Contains(snapshot.Reference) } };
                    measureMarks[snapshot.Reference] = run;
                    run.Completion.TrySetResult(CopyMarks(run.View));
                }
                if (run == null || run.Session != session || run.Live != _live || run.Revision != session.Revision
                    || run.Compare && !run.Rerun || refresh || recheck || (timed && !run.IncludePerformance) || (fullPerformanceSweep && !run.FullPerformanceSweep)
                    || (axis != null && axis != run.Axis) || run.SavedStamp != stamp || run.ExtraInputs != extraInputs)
                {
                    if (refresh) relationshipScan = null;
                    run = StartMarkRun(session, null, snapshot.Snapshot, snapshot.Reference, axis ?? run?.Axis, session.Revision, false, null,
                        markReads.Token, includePerformance: timed, fullPerformanceSweep: fullPerformanceSweep, extraInputs: extraInputs);
                }
                view = CheckStateView(run, session.Revision);
            }
        }
        // Outside marksLock: the Description score has its own lock. Every unscored line reads the state now (fx29c).
        var unscored = await UnscoredReasonsAsync().ConfigureAwait(false);
        view.Marks = unscored.Settle(WithAlignment(view.Marks, DescriptionMatchNow(session, snapshot.Reference, snapshot.Description),
            unscored.Description(snapshot.Description.Description)), snapshot.Description.Description);
        return view;
    }

    // Called under marksLock. No query or awaited work runs on the commit thread.
    private MarkRun StartMarkRun(Session session, MeasureMarkSnapshot before, MeasureMarkSnapshot after,
        string reference, string axis, long revision, bool compare, string cannotCompare,
        CancellationToken cancellation = default, bool externalContexts = false,
        bool includePerformance = false, bool fullPerformanceSweep = false, bool restore = false,
        MarkComparisonBatch comparisonBatch = null, string extraInputs = null, bool planned = false, bool stale = false)
    {
        var previousRef = before?.PreviousReference(after.Formulas[reference]);
        var previous = measureMarks.GetValueOrDefault(reference)
            ?? (previousRef == null ? null : measureMarks.GetValueOrDefault(previousRef));
        var timing = previous?.TimingBaseline;
        if (before != null && previousRef != null && before.TimedMeasures.Contains(previousRef) && !after.TimedMeasures.Contains(reference))
            timing = null;
        if (timing != null && (timing.Live != _live || timing.Snapshot != before
            && !timing.Snapshot.SameClosure(before ?? after, timing.Reference))) timing = null;
        var view = EmptyMarks(reference, revision, "This check is waiting to run.");
        view.PerformanceSettings = after.PerformanceSettings;
        view.IncludePerformanceForMeasure = after.TimedMeasures.Contains(reference);
        if (previous != null)
        {
            view.LastChecked = previous.View.LastChecked;
            foreach (var mark in view.Marks) mark.LastRan = previous.View.Marks.First(m => m.Name == mark.Name).LastRan;
        }
        if (restore && compare && markEdits.TryGetValue(reference, out var prior) && prior.Before?.Formulas.ContainsKey(reference) == true
            && prior.Before.SameClosure(after, reference))
        {
            // Undo returns to the evidence of that formula, rather than comparing the edit backwards.
            var restored = prior.Previous;
            if (restored?.Pending == true && restored.Before != null)
            {
                // Its first comparison may still be queued. Compare that formula from its own earlier baseline.
                before = restored.Before; timing = null;
                if (restored.Previous == null) markEdits.Remove(reference);
                else markEdits[reference] = restored.Previous;
            }
            else
            {
                if (restored == null) markEdits.Remove(reference);
                else markEdits[reference] = new MarkEditResult {
                    Revision = revision, Mark = restored.Mark, Performance = restored.Performance,
                    Before = restored.Before, Previous = restored.Previous
                };
                compare = false;
            }
        }
        if (compare) markEdits[reference] = new MarkEditResult {
            Revision = revision, Pending = true, Before = before, Previous = markEdits.GetValueOrDefault(reference),
            Mark = MeasureMarkJudge.NotRun("Equivalence", "This check is waiting to run.")
        };
        if (markEdits.TryGetValue(reference, out var edit))
        {
            view.Marks[4] = edit.Mark;
            if (edit.Performance != null && edit.Revision == revision) view.Marks[7] = edit.Performance;
        }
        var run = new MarkRun
        {
            Session = session, Live = _live, Revision = revision, Reference = reference, Axis = axis,
            BeforeReference = before?.PreviousReference(after.Formulas[reference]),
            SavedStamp = MarkSavedStamp(session), ExtraInputs = extraInputs ?? MeasureRunExtras?.Inputs ?? "none",
            Before = before, After = after, Compare = compare,
            CannotCompare = cannotCompare, View = view, Cancellation = cancellation, ExternalContexts = externalContexts, Planned = planned
        };
        // An edit's rerun timed the edit against its before already; a later check collects new samples (fx34).
        run.BeforeTiming = compare ? timing : includePerformance && previous is { Compare: true, Rerun: false } ? previous.BeforeTiming : null;
        run.TimingBaseline = timing;
        run.IncludePerformance = includePerformance || compare && timing != null;
        if (compare && comparisonBatch != null)
        {
            run.ComparisonBatch = comparisonBatch;
            comparisonBatch.Root ??= reference;
            comparisonBatch.Pending++;
        }
        run.FullPerformanceSweep = fullPerformanceSweep;
        // An edit reruns the number marks of each measure it moves, edited measure first (the observer's order), so a mark
        // never stays a pass of the old formula (fx34, rc33 tester gap 1). An outside checker's marks stay stale until its plan runs.
        run.Stale = stale;
        run.Rerun = compare && stale && MeasureRunExtras is null or PlannedMeasureRunExtras;
        view.Running = true;
        measureMarks[reference] = run;
        openMarkRuns.Add(run);
        StartCheckState(run);
        var admission = EnterMarksWorkerAsync(run);
        using (ExecutionContext.SuppressFlow()) _ = Task.Run(() => RunMeasureMarksAsync(run, admission));
        return run;
    }

    // One worker checks one measure at a time. A queued timing says how many checks are ahead of it (rc24 gap 1).
    private async Task EnterMarksWorkerAsync(MarkRun run, CancellationToken cancellation = default)
    {
        Task admission;
        lock (marksLock)
        {
            marksQueue.Add(run); UpdateQueuedTiming();
            admission = marksWorker.WaitAsync(cancellation);
        }
        try { await admission.ConfigureAwait(false); }
        catch
        {
            lock (marksLock) { marksQueue.Remove(run); UpdateQueuedTiming(); }
            throw;
        }
        lock (marksLock)
        {
            marksQueue.Remove(run); activeMarks = run;
            if (run.IncludePerformance) SetMark(run, Timing("Timing. Preparing the checks."));
            UpdateQueuedTiming();
        }
    }

    private void LeaveMarksWorker()
    {
        lock (marksLock) { activeMarks = null; UpdateQueuedTiming(); }
        marksWorker.Release();
    }

    // Called under marksLock.
    private void UpdateQueuedTiming()
    {
        for (var i = 0; i < marksQueue.Count; i++)
        {
            var run = marksQueue[i];
            if (!run.IncludePerformance) continue;
            var ahead = i + (activeMarks == null ? 0 : 1);
            SetMark(run, Timing("Timing. " + (ahead == 0
                ? "Preparing the checks." : "Waiting for " + ahead + " other check" + (ahead == 1 ? "" : "s") + " first.")));
        }
    }

    internal static MeasureMark Timing(string sentence)
    {
        var mark = MeasureMarkJudge.NotRun("Performance", sentence);
        mark.ReasonCode = "timing";
        return mark;
    }

    private static MeasureMark Coded(MeasureMark mark, string reasonCode) { mark.ReasonCode = reasonCode; return mark; }

    private static MeasureMarksResult EmptyMarks(string reference, long revision, string reason) => new()
    {
        MeasureRef = reference, Revision = revision,
        Marks = new[]
        {
            Coded(MeasureMarkJudge.NotRun("Filters", reason), "waiting"), Coded(MeasureMarkJudge.NotRun("Contexts", reason), "waiting"),
            Coded(MeasureMarkJudge.NotRun("SQL", "No SQL check is saved for this measure."), "no_sql"),
            Coded(MeasureMarkJudge.NotRun("Trusted number", "No trusted number is saved for this measure."), "no_trusted"),
            Coded(MeasureMarkJudge.NotRun("Equivalence", "No edit yet."), "no_edit"),
            // Seeds: a door settles them with the real state before handing them out (UnscoredReasons.Settle, fx29c).
            ScoringCopy.NotScored("Totals", ScoringCopy.NotChecked),
            ScoringCopy.NotScored("Alignment", ScoringCopy.NotChecked),
            Coded(MeasureMarkJudge.NotRun("Performance", "not timed"), "not_timed")
        }
    };

    private static MeasureMarksResult CopyMarks(MeasureMarksResult view) => new()
    {
        MeasureRef = view.MeasureRef, Revision = view.Revision, Running = view.Running, LastChecked = view.LastChecked,
        PerformanceSettings = view.PerformanceSettings == null ? null : new MeasurePerformanceSettings
        {
            SlowThresholdMs = view.PerformanceSettings.SlowThresholdMs, RegressionPercent = view.PerformanceSettings.RegressionPercent,
            RegressionMs = view.PerformanceSettings.RegressionMs, AllowBackground = view.PerformanceSettings.AllowBackground,
            Shared = view.PerformanceSettings.Shared
        },
        IncludePerformanceForMeasure = view.IncludePerformanceForMeasure,
        Marks = view.Marks.Select(m => new MeasureMark
        {
            Name = m.Name, State = m.State, Sentence = ScoringCopy.MarkSentence(m), Action = m.Action, LastRan = m.LastRan, Total = m.Total,
            ReasonCode = m.ReasonCode, ActionCodes = m.ActionCodes?.ToArray() ?? Array.Empty<string>(),
            Applies = m.Applies, Pending = m.Pending, Verdict = m.Verdict, Progress = m.Progress,
            BeforeFormatString = m.BeforeFormatString, AfterFormatString = m.AfterFormatString,
            BeforeFormula = m.BeforeFormula, AfterFormula = m.AfterFormula, Inputs = m.Inputs,
            ChosenContexts = m.ChosenContexts.Select(c => c with
            {
                Column = c.Column == null ? null : c.Column with { GroupByColumns = c.Column.GroupByColumns.ToArray() },
                DetailColumn = c.DetailColumn == null ? null : c.DetailColumn with { GroupByColumns = c.DetailColumn.GroupByColumns.ToArray() },
                Families = c.Families.ToArray(), RelationshipPath = c.RelationshipPath.ToArray()
            }).ToArray(),
            Timings = m.Timings.Select(CopyTiming).ToArray(),
            Cells = m.Cells.Select(c => new MeasureMarkCell { Context = MeasureMarkJudge.CellText(MeasureMarkJudge.Place(c.Context)),
                Before = MeasureMarkJudge.CellText(c.Before), After = MeasureMarkJudge.CellText(c.After),
                BeforeText = c.BeforeText, AfterText = c.AfterText, GapText = c.GapText }).ToArray()
        }).ToArray()
    };

    private bool MarkRunCurrent(MarkRun run) => !marksStopped && !run.Cancellation.IsCancellationRequested
        && _sessions.Current == run.Session && _live == run.Live;

    private bool MarkConnectionMatches(Session session)
    {
        var live = _live; var origin = session.LiveOrigin;
        return live != null && origin != null && TrustSessionSpec(origin.Endpoint, origin.Database, live.DataSource, live.Database);
    }

    private void SetMark(MarkRun run, MeasureMark mark)
    {
        MarkNumberFormat.FillCells(mark, run.After?.Formulas.GetValueOrDefault(run.Reference) is { } formula ? EquivalenceFormat(formula) : null);
        lock (marksLock)
        {
            // Each worker owns its view; only a copy crosses a door.
            var i = Array.FindIndex(run.View.Marks, m => m.Name == mark.Name);
            mark.LastRan ??= run.View.Marks[i].LastRan;
            if (mark.ChosenContexts.Length == 0) mark.ChosenContexts = run.View.Marks[i].ChosenContexts;
            run.View.Marks[i] = mark;
            if (mark.Name == "Equivalence" && run.Compare && run.Session == marksSession
                && markEdits.TryGetValue(run.Reference, out var edit) && edit.Revision == run.Revision)
            {
                edit.Mark = mark; edit.Pending = false;
                if (measureMarks.TryGetValue(run.Reference, out var latest)) latest.View.Marks[i] = mark;
            }
            if (mark.Name == "Performance" && run.Compare && run.Session == marksSession
                && markEdits.TryGetValue(run.Reference, out var timedEdit) && timedEdit.Revision == run.Revision)
            {
                timedEdit.Performance = mark;
                if (measureMarks.TryGetValue(run.Reference, out var latest) && latest.Revision == run.Revision)
                    latest.View.Marks[i] = mark;
            }
            // The check state takes the mark from the newest run of the measure, and an edit's comparison from its own run.
            var newest = measureMarks.GetValueOrDefault(run.Reference) == run;
            // An edit's comparison (Equivalence, and Performance when the edit is timed) belongs to the edit, whichever run is newest.
            var compared = run.Compare && markEdits.TryGetValue(run.Reference, out var own) && (own.Mark == mark || own.Performance == mark);
            if ((newest || compared) && MarkRunCurrent(run) && openMarkRuns.Contains(run))
                WriteCheckState(run.Session, run.Reference, state => state with { Marks = MergeMark(state.Marks, mark) });
        }
    }

    private async Task CheckThroughPlanAsync(string reference)
    {
        try { await RunMeasurePlanAsync((await PlanMeasureRunAsync(new() { Selection = new() { Measures = new[] { reference } } }).ConfigureAwait(false)).Id).ConfigureAwait(false); }
        catch (Exception) { /* An edit or a model change ends it; the measure keeps its last marks. */ }
    }

    /// <summary>The orphan-key scan for a revision and connection, shared by every check on it. The scan has its own token: each
    /// check waits with its own, and when the last one waiting leaves before the scan ends, the scan is cancelled (it must not
    /// hold the query lane for nobody) and dropped. A scan that was cancelled or failed is never served again.</summary>
    private async Task<TestSuiteRunResult> RelationshipScanAsync(long revision, LiveConnection live, string origin, CancellationToken wait)
    {
        wait.ThrowIfCancellationRequested();
        RelationshipScan scan;
        lock (marksLock)
        {
            scan = relationshipScan;
            if (scan == null || scan.Revision != revision || scan.Live != live || scan.Stop.IsCancellationRequested
                || scan.Task.IsCompleted && !scan.Task.IsCompletedSuccessfully)
            {
                var fresh = scan = relationshipScan = new RelationshipScan { Revision = revision, Live = live };
                // Started outside every run's scope: no run's token or query transform flows into it.
                using (ExecutionContext.SuppressFlow())
                    fresh.Task = Task.Run(async () =>
                    {
                        using (live?.WithMeasureRunCancellation(fresh.Stop.Token))
                            return await RunTestSuiteAsync(false, origin, sections: new[] { "relationships" }).ConfigureAwait(false);
                    });
            }
            scan.Waiters++;
        }
        try { return await scan.Task.WaitAsync(wait).ConfigureAwait(false); }
        finally
        {
            bool unwanted;
            lock (marksLock)
            {
                unwanted = --scan.Waiters == 0 && !scan.Task.IsCompleted;
                if (unwanted && relationshipScan == scan) relationshipScan = null;
            }
            if (unwanted) scan.Stop.Cancel();
        }
    }

    private Task<TestSuiteRunResult> MarkRelationshipRun(MarkRun run) => RelationshipScanAsync(run.Revision, run.Live, "agent", run.Cancellation);

    private async Task RunMeasureMarksAsync(MarkRun run, Task admission)
    {
        await admission.ConfigureAwait(false);
        try
        {
            if (!MarkRunCurrent(run)) return;
            using var cancellation = run.Live?.WithMeasureRunCancellation(run.Cancellation);
            using var sqlCancellation = WithMeasureRunToken(run.Cancellation);
            var origin = run.Session.LiveOrigin;
            if (run.Live != null && (origin == null || !TrustSessionSpec(origin.Endpoint, origin.Database, run.Live.DataSource, run.Live.Database)))
            {
                foreach (var name in new[] { "Filters", "Contexts", "SQL", "Trusted number" })
                    SetMark(run, MeasureMarkJudge.NotRun(name, "The connected model does not match this editing model."));
                if (run.Compare) SetMark(run, MeasureMarkJudge.NotRun("Equivalence", "This edit could not be compared because the connected model does not match."));
                if (!run.Compare || run.Rerun) await ScoreMarkTotalsAsync(run, Array.Empty<TotalProbe>()).ConfigureAwait(false);
                return;
            }
            if (run.Live != null && run.After.MissingReason(run.Reference) is { } missing)
            {
                foreach (var name in new[] { "Filters", "Contexts", "SQL", "Trusted number" })
                    SetMark(run, MeasureMarkJudge.NotRun(name, missing));
                if (run.Compare) SetMark(run, MeasureMarkJudge.NotRun("Equivalence", missing));
                if (run.IncludePerformance) SetMark(run, MeasureMarkJudge.NotRun("Performance", missing));
                if (!run.Compare || run.Rerun) await ScoreMarkTotalsAsync(run, Array.Empty<TotalProbe>()).ConfigureAwait(false);
                return;
            }
            if (marksStructureHold?.Covers(run.Reference) == true)
            {
                // An edit's comparison is new, so it has no last result: it says why it did not run.
                if (run.Compare) SetMark(run, Coded(MeasureMarkJudge.NotRun("Equivalence", StructureHoldReason), "held"));
                if (!run.Compare || run.Rerun) foreach (var name in new[] { "Filters", "Contexts", "SQL", "Trusted number", "Totals" })
                        if (HeldMark(run.Reference, name) is { } held) SetMark(run, held);
                if (run.IncludePerformance) SetMark(run, HeldMark(run.Reference, "Performance"));
                return;
            }
            if (run.Live == null)
            {
                SetMark(run, Coded(MeasureMarkJudge.NotRun("Filters", "Connect a model to check its filters."), "not_connected"));
                SetMark(run, Coded(MeasureMarkJudge.NotRun("Contexts", "Connect a model to check its contexts."), "not_connected"));
                if (run.Compare) SetMark(run, MeasureMarkJudge.NotRun("Equivalence", "This edit could not be compared because no model is connected."));
            }
            else
            {
                if (run.Compare) await CompareMarkEditAsync(run).ConfigureAwait(false);
                // An edit always compares numbers. Its rerun then checks the number marks of the new formula (fx34).
                if (run.Compare && !run.Rerun)
                {
                    if (run.IncludePerformance && MarkRunCurrent(run))
                        await TimeMeasureMarksAsync(run).ConfigureAwait(false);
                    return;
                }
                if (!MarkRunCurrent(run)) return;
                if (!run.ExternalContexts)
                    using (run.Live.WithMarkQueries(q => run.After.Apply(q, run.Reference)))
                        await ProbePlannedMarksAsync(run).ConfigureAwait(false);
            }
            if (!MarkRunCurrent(run)) return;
            if (run.Compare && !run.Rerun) return;
            if (!run.ExternalContexts) await ScoreMarkTotalsAsync(run, run.TotalsProbes).ConfigureAwait(false);
            if (!MarkRunCurrent(run)) return;
            using (run.Live?.WithMarkQueries(q => run.After.Apply(q, run.Reference)))
                await SavedMarksAsync(run).ConfigureAwait(false);
            if (run.IncludePerformance && MarkRunCurrent(run))
                await TimeMeasureMarksAsync(run).ConfigureAwait(false);
        }
        // A cancelled check has no result: "could not run" is for a check that ran and failed.
        catch (OperationCanceledException) when (!MarkRunCurrent(run)) { }
        catch (Exception ex)
        {
            foreach (var name in new[] { "Filters", "Contexts", "SQL", "Trusted number", "Equivalence", "Totals" })
                if (run.View.Marks.First(m => m.Name == name).Sentence == "This check is waiting to run.")
                    SetMark(run, MeasureMarkJudge.NotRun(name, "This check could not run: " + ex.Message));
        }
        finally
        {
            if (run.Compare && run.View.Marks.First(m => m.Name == "Equivalence").Sentence == "This check is waiting to run.")
                SetMark(run, MeasureMarkJudge.NotRun("Equivalence", "The model or connection changed before this edit could be compared."));
            lock (marksLock)
            {
                if (run.IncludePerformance && MeasureCheckVerdict.InProgress(run.View.Marks.First(m => m.Name == "Performance")))
                    SetMark(run, MeasureMarkJudge.NotRun("Performance", "Timing could not finish. Check this measure again."));
                run.View.Running = false;
                run.View.LastChecked = LatestMarkTime(run.View.Marks) ?? DateTimeOffset.UtcNow;
                // A run Stop or an edit cancelled still ends: its measure must not read Checking for good (v-rc28 D1).
                // A plan run's own run ends when the plan has run its extras on it (v-fx27 D1).
                if (!run.Planned) FinishRunCheckState(run, measureMarks.GetValueOrDefault(run.Reference) == run, MarkRunCurrent(run));
            }
            try { await FinishMarkComparisonAsync(run).ConfigureAwait(false); }
            catch { /* Recording a check cannot block the worker or its result. */ }
            run.Completion.TrySetResult(CopyMarks(run.View));
            if (MarkRunCurrent(run))
            {
                var eq = run.View.Marks.First(m => m.Name == "Equivalence");
                try
                {
                    // The check state push (checks/didChange) tells every surface; a check result is not a model change.
                    _ = PublishActivityAsync(new ActivityEvent
                    {
                        Kind = "measure_marks", Origin = "background", SessionId = run.Session.Id,
                        Label = run.Compare ? eq.Sentence : "The measure checks finished.",
                        Ok = true, Result = CopyMarks(run.View)
                    });
                }
                catch { /* A view listener cannot fail an edit or its worker. */ }
            }
            LeaveMarksWorker();
        }
    }

    private async Task SavedMarksAsync(MarkRun run)
    {
        var saved = await ListTestDefinitionsAsync().ConfigureAwait(false);
        var definitions = await MarkDefinitionsAsync(run, saved).ConfigureAwait(false);
        foreach (var (name, kind) in new[] { ("SQL", TestKinds.MeasureReconcile), ("Trusted number", TestKinds.MeasureValue) })
        {
            var outcomes = new List<ReconcileOutcome>();
            foreach (var def in definitions.Where(d => string.Equals(d.Kind, kind, StringComparison.OrdinalIgnoreCase)))
            {
                if (!MarkRunCurrent(run)) return;
                try { outcomes.Add(await TryMarkDefinitionAsync(def, run).ConfigureAwait(false)); }
                catch (Exception ex) { outcomes.Add(new ReconcileOutcome { Verdict = Verdict.NotVerifiable, Message = ex.Message }); }
            }
            var mark = MeasureMarkJudge.Saved(name, outcomes.ToArray());
            if (outcomes.Count == 0) mark.ReasonCode = name == "SQL" ? "no_sql" : "no_trusted";
            SetMark(run, mark);
        }
    }

    private async Task CompareMarkEditAsync(MarkRun run)
    {
        using var baselineStore = new MarkBaselineScope(this, _sessions.CurrentContext, run.ComparisonBatch);
        var reason = run.CannotCompare;
        if (run.After.HasCalcGroups) reason = "This edit uses calculation groups; the two formula snapshots cannot yet reproduce their full context.";
        if (reason != null) { SetMark(run, MeasureMarkJudge.NotRun("Equivalence", reason)); return; }
        var formula = run.After.Formulas[run.Reference];
        var before = run.Before.Formulas[run.BeforeReference];
        var axis = run.Axis ?? run.Before.RelatedAxes(run.BeforeReference).FirstOrDefault();
        var group = axis == null ? Array.Empty<string>() : new[] { axis };
        var beforeGrid = MeasureMarkSnapshot.GridQuery(before.Name, axis);
        var afterGrid = MeasureMarkSnapshot.GridQuery(formula.Name, axis);
        BaselineCaptureResult capture;
        using (run.Live.WithMarkQueries(_ => run.Before.Apply(beforeGrid, run.BeforeReference)))
            capture = await CaptureBaselineAsync(run.Reference, group, null, false, 1, 8, null, "human").ConfigureAwait(false);
        if (capture.Status != "ok")
        { SetMark(run, MeasureMarkJudge.NotRun("Equivalence", capture.Message ?? "The before numbers could not be captured.")); return; }
        if (!MarkRunCurrent(run)) return;
        BaselineCompareResult comparison;
        using (run.Live.WithMarkQueries(_ => run.After.Apply(afterGrid, run.Reference)))
            comparison = await CompareBaselineAsync(capture.CaptureId, null, "human").ConfigureAwait(false);
        var mark = comparison.Status == "ok" && comparison.Diffs.Length == 1
            ? MeasureMarkJudge.Equivalence(comparison.Diffs[0])
            : MeasureMarkJudge.NotRun("Equivalence", comparison.Message ?? "The after numbers could not be compared.");
        mark.BeforeFormatString = EquivalenceFormat(before);
        mark.AfterFormatString = EquivalenceFormat(formula);
        mark.BeforeFormula = before.Body;
        mark.AfterFormula = formula.Body;
        mark.Inputs = run.After.ClosureInputs(run.Reference);
        SetMark(run, mark);
    }

    private static string EquivalenceFormat(MeasureMarkFormula formula) =>
        string.IsNullOrWhiteSpace(formula.FormatString) ? null : formula.FormatString;
}
