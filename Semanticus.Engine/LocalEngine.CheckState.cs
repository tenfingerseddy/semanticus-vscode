using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine;

// The one check state per measure (L1 design, 28 Oct). Every producer writes here: a run start marks what it will check
// as pending (the last result stays), each finished mark replaces its slot, a run end clears pending, a plan run writes
// the whole record, Keep and the Totals waiver change what applies. Every write bumps one version and pushes the record.
// Every reader (both doors, every surface) gets a copy finished by MeasureCheckVerdict, so nobody assembles a verdict.
public sealed partial class LocalEngine
{
    // Process-wide, never reset: a reader can always tell an older record, also across a session swap.
    private long checkVersion;
    private readonly HashSet<string> checkExpected = new(StringComparer.Ordinal);
    private readonly HashSet<string> checkOnPurpose = new(StringComparer.Ordinal);

    private static readonly string[] FullRunMarks = { "Filters", "Contexts", "SQL", "Trusted number", "Totals" };

    /// <summary>Writes one measure's check state and pushes it. Called under no lock or under marksLock; it takes measureRunLock.</summary>
    private MeasureCheckCache WriteCheckState(Session session, string reference, Func<MeasureCheckCache, MeasureCheckCache> change)
    {
        MeasureCheckCache finished;
        lock (measureRunLock)
        {
            if (session == null || _sessions.Current != session || DryRunScope.Current != null) return null;
            LoadMeasureCache(session);
            var old = measureCheckCache.GetValueOrDefault(reference) ?? new() { MeasureRef = reference };
            var updated = change(old);
            if (updated == null) return null;
            updated = updated with { Version = ++checkVersion };
            measureCheckCache[reference] = updated;
            finished = FinishCheckState(updated);
            PublishCheckStates(session, finished);
        }
        return finished;
    }

    // The plan run going on now, as every push carries it. Written under measureRunLock.
    private MeasureRunActivity measureRunActivity = MeasureRunActivity.Idle;

    // Called under measureRunLock.
    private void PublishCheckStates(Session session, params MeasureCheckCache[] states) =>
        _sessions.Bus.PublishChecks(new CheckStateChange { SessionId = session.Id, Version = checkVersion, States = states, Run = measureRunActivity });

    // Called under measureRunLock: the person's Totals waiver and Filters word, as the last commit left them.
    private MeasureCheckCache FinishCheckState(MeasureCheckCache state) =>
        FinishCheckState(_live == null ? state : Connected(state), checkExpected.Contains(state.MeasureRef), checkOnPurpose.Contains(state.MeasureRef),
            marksStructureHold?.Covers(state.MeasureRef) == true);

    /// <summary>A record written while no model was connected (a sweep or a read while a live model was still opening) says
    /// "connect a model". On a connected model that is false, and a record with no other result is not a check of it: the tree
    /// read Unknown "connect a model" before any run while the Checks page read Not checked yet (fx30e F4).</summary>
    private static MeasureCheckCache Connected(MeasureCheckCache state)
    {
        if (!state.Marks.Any(m => m.ReasonCode == "not_connected")) return state;
        var marks = state.Marks.Where(m => m.ReasonCode != "not_connected").ToArray();
        var ran = marks.Any(m => MeasureCheckVerdict.Seven.Contains(m.Name) && m.LastRan != null);
        return state with { Marks = marks, LastChecked = ran ? state.LastChecked : null };
    }

    /// <summary>The model was connected or disconnected: each record that holds an offline line is pushed again as it reads
    /// now, so a door holding the offline copy replaces it (fx30e F4).</summary>
    private void CheckConnectionChanged()
    {
        var session = _sessions.Current;
        if (session == null) return;
        lock (measureRunLock)
        {
            if (!measureCacheLoaded || DryRunScope.Current != null) return;
            var states = measureCheckCache.Values.Where(s => s.Marks.Any(m => m.ReasonCode == "not_connected")).ToArray()
                .Select(s => { var updated = s with { Version = ++checkVersion }; measureCheckCache[s.MeasureRef] = updated; return FinishCheckState(updated); })
                .ToArray();
            if (states.Length > 0) PublishCheckStates(session, states);
        }
    }

    /// <summary>A copy for a door: the marks in their fixed slots, each with Applies and Verdict, and the worst verdict. Both doors
    /// and every surface read this one shape.</summary>
    private static MeasureCheckCache FinishCheckState(MeasureCheckCache state, bool expected, bool onPurpose, bool held = false)
    {
        var copy = CopyMeasureCache(state);
        if (copy.Marks.Length > 0)
        {
            var slots = EmptyMarks(copy.MeasureRef, 0, "This check is waiting to run.").Marks;
            copy = copy with { Marks = slots.Select(slot => copy.Marks.FirstOrDefault(m => m.Name == slot.Name) ?? slot)
                .Concat(copy.Marks.Where(m => slots.All(s => s.Name != m.Name))).ToArray() };
        }
        // A record saved before the engine set display text gets it here, in the format it was checked with.
        var format = SnapshotFormat(copy);
        copy = copy with { Marks = copy.Marks.Select(mark => HeldView(MeasureCheckVerdict.Finish(
            MeasureMarkJudge.ApplyFiltersOnPurpose(MarkNumberFormat.FillCells(mark, format), onPurpose), copy.Keep, expected), held)).ToArray() };
        return copy with { Verdict = MeasureCheckVerdict.Worst(copy.Marks) };
    }

    private static string SnapshotFormat(MeasureCheckCache state)
    {
        if (state.InputSnapshot is not { ValueKind: JsonValueKind.Object } snapshot
            || !snapshot.TryGetProperty("bodies", out var bodies) || bodies.ValueKind != JsonValueKind.Array) return null;
        foreach (var body in bodies.EnumerateArray())
            if (body.TryGetProperty("Ref", out var r) && r.GetString() == state.MeasureRef
                && body.TryGetProperty("FormatString", out var f) && f.ValueKind == JsonValueKind.String)
                return string.IsNullOrWhiteSpace(f.GetString()) ? null : f.GetString();
        return null;
    }

    /// <summary>A mark from a run, merged into the record: a mark still being produced keeps the last result, pending.</summary>
    private static MeasureMark[] MergeMark(MeasureMark[] marks, MeasureMark incoming)
    {
        var list = marks.ToList();
        var i = list.FindIndex(m => m.Name == incoming.Name);
        var previous = i < 0 ? null : list[i];
        MeasureMark next;
        if (MeasureCheckVerdict.InProgress(incoming))
        {
            // An unscored line is not a result to keep while the score is asked again (fx30e K3: "Check again to score it"
            // stood beside Checking now for the whole recheck after Turn on).
            next = previous == null || MeasureCheckVerdict.InProgress(previous) || Unscored(previous) ? Copy(incoming) : Copy(previous);
            next.Pending = true;
            next.Progress = incoming.ReasonCode == "timing" ? incoming.Sentence : null;
        }
        else { next = Copy(incoming); next.Pending = false; next.Progress = null; }
        if (i < 0) list.Add(next); else list[i] = next;
        return list.ToArray();
    }

    private static MeasureMark Copy(MeasureMark mark) => CopyMarks(new() { Marks = new[] { mark } }).Marks[0];

    private static bool Unscored(MeasureMark mark) => mark.State == "not run" && mark.ReasonCode is "totals_off" or "not_scored";

    /// <summary>A run starts: what it will check is pending, the last results stay, and the measure is running. Called under
    /// marksLock after markEdits holds this run's edit, so Equivalence always says what markEdits says (an Undo back to no edit
    /// takes it away at once, also for a measure that uses the edited one).</summary>
    private void StartCheckState(MarkRun run)
    {
        var names = run.Compare && !run.Rerun ? new List<string>() : FullRunMarks.ToList();
        if (run.IncludePerformance) names.Add("Performance");
        markEdits.TryGetValue(run.Reference, out var edit);
        WriteCheckState(run.Session, run.Reference, state =>
        {
            var marks = edit == null ? state.Marks.Where(m => m.Name != "Equivalence").ToArray()
                : MergeMark(state.Marks, edit.Pending ? Waiting("Equivalence") : edit.Mark);
            // An edit moved this measure's numbers: no last result of a number mark is a pass of the new formula (fx34).
            if (run.Stale) marks = marks.Select(m => FullRunMarks.Contains(m.Name) ? StaleMark(m) : m).ToArray();
            // Marks made by another checker are not this checker's last results: they wait with no result.
            if (state.Checker != null && state.Checker != run.ExtraInputs) marks = marks.Where(m => !names.Contains(m.Name)).ToArray();
            foreach (var name in names) marks = MergeMark(marks, Waiting(name));
            // A Keep belongs to its edit. Back to no edit (Undo), it goes with the Equivalence it hid, so the same edit made again
            // is compared and shown (fx35, rc34 tester gap 2: kept, undone, made again, and no screen showed Equivalence).
            return state with { Marks = marks, Running = true, Checker = names.Count > 0 ? run.ExtraInputs : state.Checker,
                Keep = edit == null ? null : state.Keep };
        });
    }

    internal const string EditStaleReason = "Your edit changed this measure. This result is from before the edit.";

    /// <summary>A number mark whose result is from before an edit: Unknown with the reason, never its old pass, until its rerun
    /// replaces it (fx34, rc33 tester gap 1). A mark that does not apply or has no result yet stays as it is.</summary>
    private static MeasureMark StaleMark(MeasureMark mark)
    {
        if (MeasureCheckVerdict.InProgress(mark) || !MeasureCheckVerdict.Applies(mark.ReasonCode) || mark.ReasonCode == "stale") return mark;
        return new MeasureMark { Name = mark.Name, State = "not run", Sentence = EditStaleReason, ReasonCode = "stale",
            Action = MeasureMarkJudge.ActionFor(mark.Name), LastRan = mark.LastRan, ChosenContexts = mark.ChosenContexts };
    }

    private static MeasureMark Waiting(string name) =>
        new() { Name = name, State = "not run", Sentence = "This check is waiting to run.", ReasonCode = "waiting" };

    private string CheckedOnKey(Session session, long revision, string checker, string savedStamp, LiveConnection live) =>
        session.Id + "|" + revision + "|" + checker + "|" + savedStamp + "|" + (live == null ? "offline" : live.DataSource + "|" + live.Database);

    /// <summary>A run ends, also one that Stop or an edit cancelled, and only once: a later call for the same run does nothing.
    /// The last open run of the measure says it is no longer running; the marks it did not reach keep their last result. Only
    /// the <paramref name="latest"/> run, when it <paramref name="finished"/>, counts the measure as checked on this revision,
    /// so a cancelled one is checked again. Called under marksLock.</summary>
    private void FinishRunCheckState(MarkRun run, bool latest, bool finished)
    {
        if (!openMarkRuns.Remove(run)) return;
        var checkedOn = run.Compare && !run.Rerun || !finished || !latest ? null : CheckedOnKey(run.Session, run.Revision, run.ExtraInputs, run.SavedStamp, run.Live);
        // Another open run of this measure ends the record itself, when it ends.
        var last = !openMarkRuns.Any(r => r.Reference == run.Reference && r.Session == run.Session);
        if (!last && checkedOn == null) return;
        var comparing = markEdits.TryGetValue(run.Reference, out var edit) && edit.Pending;
        bool Held(MeasureMark m) => comparing && m.Name == "Equivalence";
        WriteCheckState(run.Session, run.Reference, state =>
        {
            if (!last) return state with { CheckedOn = checkedOn };
            // A slot the run never reached and that had no result before is not shown, as before the run.
            var marks = state.Marks.Where(m => Held(m) || !MeasureCheckVerdict.InProgress(m)).Select(Copy).ToArray();
            foreach (var mark in marks.Where(m => !Held(m))) { mark.Pending = false; mark.Progress = null; }
            return state with { Marks = marks, Running = comparing, LastChecked = LatestMarkTime(marks) ?? state.LastChecked, CheckedOn = checkedOn ?? state.CheckedOn };
        });
        lock (measureRunLock) if (_sessions.Current == run.Session) SaveMeasureCache(run.Session);
    }

    /// <summary>Every run on <paramref name="token"/> that has not ended ends now, unfinished: a plan run that Stop or an edit
    /// cancelled settles each measure before it returns, whether its worker or an extra (timing) was on it, and Stop settles the
    /// checks a read started (v-rc28 D1, v-fx27 D1). Takes marksLock, then measureRunLock, as the run does.</summary>
    private void SettleOpenRuns(CancellationToken token)
    {
        lock (marksLock)
            foreach (var run in openMarkRuns.Where(r => r.Cancellation == token).ToArray())
                FinishRunCheckState(run, measureMarks.GetValueOrDefault(run.Reference) == run, finished: false);
    }

    /// <summary>The marks of one measure as both doors read them: the record, finished, in the fixed slot order.</summary>
    private MeasureMarksResult CheckStateView(MarkRun run, long revision)
    {
        MeasureCheckCache state;
        lock (measureRunLock)
            state = FinishCheckState(measureCheckCache.GetValueOrDefault(run.Reference) ?? new() { MeasureRef = run.Reference });
        var slots = EmptyMarks(run.Reference, revision, "This check is waiting to run.").Marks;
        foreach (var slot in slots) MeasureCheckVerdict.Finish(slot, null, false);
        return new MeasureMarksResult
        {
            MeasureRef = run.Reference, Revision = revision, Running = state.Running, LastChecked = state.LastChecked ?? run.View.LastChecked,
            PerformanceSettings = CopyMarks(run.View).PerformanceSettings, IncludePerformanceForMeasure = run.View.IncludePerformanceForMeasure,
            Version = state.Version,
            Marks = state.Marks.Length > 0 ? state.Marks : slots
        };
    }

    /// <summary>The Totals waiver and Filters on purpose annotations, read from the model. A change pushes the measures it touches.</summary>
    private void RefreshCheckExpected(Session session, Model model)
    {
        HashSet<string> Marked(string annotation) => model.AllMeasures.Where(m => m.GetAnnotation(annotation) == "true")
            .Select(ObjectRefs.For).ToHashSet(StringComparer.Ordinal);
        var now = Marked("Semanticus.Totals.Expected");
        var onPurpose = Marked(FiltersOnPurposeAnnotation);
        lock (measureRunLock)
        {
            var moved = now.Except(checkExpected).Concat(checkExpected.Except(now))
                .Concat(onPurpose.Except(checkOnPurpose)).Concat(checkOnPurpose.Except(onPurpose)).Distinct(StringComparer.Ordinal).ToArray();
            if (moved.Length == 0) return;
            checkExpected.Clear(); checkExpected.UnionWith(now);
            checkOnPurpose.Clear(); checkOnPurpose.UnionWith(onPurpose);
            if (_sessions.Current != session || !measureCacheLoaded) return;
            var states = moved.Where(measureCheckCache.ContainsKey).Select(r =>
            {
                var updated = measureCheckCache[r] with { Version = ++checkVersion };
                measureCheckCache[r] = updated;
                return FinishCheckState(updated);
            }).ToArray();
            if (states.Length > 0) PublishCheckStates(session, states);
        }
    }

    /// <summary>Keep on an edit (d1 4.4): the person meant this change. Equivalence stops applying while the measure and every
    /// measure it uses are as they were compared. Saved with the check cache, so every window and the assistant see it.</summary>
    public async Task<MeasureCheckCache> KeepMeasureEditAsync(string measureRef, string origin = "human")
    {
        var session = _sessions.Require();
        var reference = await session.ReadAsync(model => ObjectRefs.Resolve(model, measureRef) is Measure m ? ObjectRefs.For(m)
            : throw new ArgumentException("Choose a measure to keep its edit.")).ConfigureAwait(false);
        MeasureMark equivalence;
        lock (measureRunLock) { LoadMeasureCache(session); equivalence = measureCheckCache.GetValueOrDefault(reference)?.Marks.FirstOrDefault(m => m.Name == "Equivalence"); }
        if (equivalence?.Inputs is not { Length: > 0 } inputs || equivalence.State == "not run")
            throw new InvalidOperationException("There is no compared edit to keep for this measure.");
        var state = WriteCheckState(session, reference, s => s with { Keep = new MeasureKeep { Inputs = inputs, At = DateTimeOffset.UtcNow } });
        lock (measureRunLock) SaveMeasureCache(session);
        return state;
    }

    /// <summary>Stop on the run line: the active plan run or sweep ends at the next measure, and so does a check a card started
    /// meanwhile, so no measure still reads Checking (v-rc28 D1). Marks already checked stay. An edit's comparison is the edit's.</summary>
    public Task<bool> StopMeasureRunAsync()
    {
        lock (measureRunLock)
        {
            // A run still waiting its turn or reading its candidates is stopped too (fx30e K4).
            var starting = startingMeasureRuns.Where(s => !s.IsCancellationRequested && s != activeMeasureRun).ToArray();
            if (activeMeasureRun == null && starting.Length == 0) return Task.FromResult(false);
            foreach (var s in starting) { stoppedStartingRuns.Add(s); s.Cancel(); }
            if (activeMeasureRun != null) { stoppedMeasureRun = activeMeasureRun; activeMeasureRun.Cancel(); }
        }
        CancellationTokenSource reads;
        lock (marksLock) { reads = markReads; markReads = new(); }
        reads.Cancel();
        SettleOpenRuns(reads.Token);
        return Task.FromResult(true);
    }
}
