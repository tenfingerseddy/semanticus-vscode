using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>One tracked commit as the History remembers it (Plan-heart contract 8.5).</summary>
    public sealed class EditLedgerEntry
    {
        /// <summary>"ed-" and 8 random hex digits. Random, so an id never repeats across a restart.</summary>
        public string EntryId { get; set; }
        public string SessionId { get; set; }
        public long Revision { get; set; }
        public DateTimeOffset When { get; set; }
        /// <summary>"human" | "agent" | "system", exactly as the notification carried it.</summary>
        public string Origin { get; set; }
        /// <summary>"you" | "assistant" | "semanticus" | "workflow" (contract 8.2).</summary>
        public string By { get; set; }
        /// <summary>The plan item, run, draft or task the commit served, or null.</summary>
        public ChangeCauseInfo Cause { get; set; }
        public string Label { get; set; }
        /// <summary>What changed, with before and after capped at <see cref="ChangeValues.LedgerCap"/> characters.</summary>
        public ChangeDelta[] Deltas { get; set; }
        /// <summary>TE2's undo step count right after the commit: this entry is step <c>StepIndex</c> on the stack.
        /// Zero for a commit that changed the model but put no step on the stack, which can never be undone.</summary>
        public int StepIndex { get; set; }
        /// <summary>True while the entry sits on the redo stack.</summary>
        public bool Undone { get; set; }
        /// <summary>True once the entry is on neither stack: an undone entry a new edit cleared from redo.</summary>
        public bool Gone { get; set; }

        /// <summary>Whether the model's FILE held this entry's change when the model was last saved: false for an entry made after the last
        /// save, and for one that was taken back (or cleared from redo) when the save wrote the file. Null on an entry written before this
        /// was kept. It is what a later session reads to say whether an undone row is really absent from the model it opened: the undone
        /// flag alone cannot tell, because it moves with an undo and the file does not (UAT-J7-S4a-004).</summary>
        public bool? InFile { get; set; }

        /// <summary>The first stack step this commit made. Equal to <see cref="StepIndex"/> for every commit that
        /// goes through one batch; kept apart so a commit that ever made two steps is still undone whole.</summary>
        public int StepFirst { get; set; }

        /// <summary>"edit" for a tracked commit; "saved" and "restored" for a checkpoint made or restored, and "run"
        /// for a finished workflow run (contract 8.7, 8.8). Only an edit ever has a step.</summary>
        public string Kind { get; set; } = EditLedgerKinds.Edit;
        /// <summary>A flag row's own words: the checkpoint label, the restored target and its rescue.</summary>
        public string Detail { get; set; }
        /// <summary>A "run" row's report snapshot, written when the run finished or was aborted (contract 8.8).</summary>
        public WorkflowRunReport Report { get; set; }

        // Which file and model the entry belongs to ("file|identity"); null until the model has somewhere to keep it.
        [JsonIgnore] internal string Home { get; set; }

        internal EditLedgerEntry Copy() => (EditLedgerEntry)MemberwiseClone();
    }

    /// <summary>A finished workflow run as History shows it, captured when the run finished or was aborted (contract
    /// 8.8, Sol 24 Sep). History reads this snapshot, never the in-memory run store, which keeps 8 runs and none
    /// across a restart.</summary>
    public sealed class WorkflowRunReport
    {
        public string RunId { get; set; }
        public string Workflow { get; set; }
        public string Title { get; set; }
        /// <summary>"done" | "aborted" | the run's own terminal word.</summary>
        public string Status { get; set; }
        /// <summary>The report's OWN verdict word (Verified, NeedsReview, Broken, Unknown, Overridden), the one its
        /// evidence page opens with. "done" says the run finished; this says whether anything was proven. A list that
        /// showed "passed" for a done run read a run with 0 of 3 steps verified as a success (UAT N4). Null on a report
        /// written before this was kept: a reader then says only that the run finished.</summary>
        public string Verdict { get; set; }
        public int? StepsVerified { get; set; }
        public int? StepsTotal { get; set; }
        public WorkflowRunReportStep[] Steps { get; set; }
        /// <summary>What <c>export_workflow_evidence</c> would have given at that moment, as its JSON text.</summary>
        public string Evidence { get; set; }
    }

    public sealed class WorkflowRunReportStep
    {
        public string StepId { get; set; }
        public string Title { get; set; }
        public string Outcome { get; set; }
        public string SubmittedBy { get; set; }
        public WorkflowRunReportCheck[] Checks { get; set; }
    }

    public sealed class WorkflowRunReportCheck
    {
        public string Kind { get; set; }
        public string Verdict { get; set; }
        public string Detail { get; set; }
    }

    public static class EditLedgerKinds
    {
        public const string Edit = "edit";
        public const string Saved = "saved";
        public const string Restored = "restored";
        public const string Run = "run";
    }

    /// <summary>Where one session's ledger is kept: the <c>.semanticus/history.json</c> file beside the model, and the
    /// model's durable identity it is filed under (the same key the plan memory uses).</summary>
    public sealed class LedgerHome
    {
        public string File { get; set; }
        public string Identity { get; set; }
        internal string Key => File + "|" + Identity;
    }

    /// <summary>
    /// The edit ledger (Plan-heart contract 8.5): one entry per tracked commit, fed from the same
    /// <see cref="ChangeBus.Changed"/> subscription as <see cref="ChangeJournal"/>, and lined up with TE2's linear
    /// undo stack by step count.
    ///
    /// HOW IT STAYS ALIGNED. It never replays or guesses. Every commit's notification carries TE2's undo step count
    /// just before the commit (<c>StepsBefore</c>) and the undo and redo step counts right after it
    /// (<see cref="UndoState.Steps"/>, <see cref="UndoState.RedoSteps"/>), read on the dispatcher inside the commit.
    /// An edit that added a step becomes step <c>Steps</c>; whatever sat above <c>StepsBefore</c> was the redo
    /// branch TE2 has just cleared, so it is gone. After every commit, an entry is undone exactly when its step is
    /// above <c>Steps</c>, and gone when it is above <c>Steps + RedoSteps</c>. Undo and redo commits write no entry
    /// of their own; they only move those two counts. A commit that changed nothing and added no step writes
    /// nothing: a history row saying nothing happened is noise.
    ///
    /// Why that is enough: the only writer of TE2's stack is <c>Session.TrackAsync</c> (a failed batch rolls back
    /// and restores redo without publishing, a rehearsal never publishes), commits are published on the dispatcher
    /// thread in order, and so the counts in consecutive notifications describe every stack move there is.
    /// <c>EditLedgerAlignmentTests</c> holds it to that over a seeded 500-operation run and a 610-edit stack.
    ///
    /// Thread-safe (records arrive on the dispatcher thread, reads on any). Never throws into a commit.
    /// </summary>
    public sealed class EditLedger
    {
        /// <summary>Entries on neither stack (earlier sessions, a cleared redo branch, steps that could never be
        /// undone) are kept up to this many, oldest dropped first. An entry still on the current session's undo or
        /// redo stack is never dropped, however many there are (Sol 24 Sep): TE2's stack has no cap.</summary>
        public const int MaxOffStack = 500;

        private readonly object _gate = new object();
        private readonly List<EditLedgerEntry> _entries = new List<EditLedgerEntry>();
        private string _liveSession;   // the session of the newest commit seen; its stack entries are kept
        // The sessions this ledger recorded. Their entries are this ledger's to write; every other session's entries
        // belong to the process that recorded them, and the file's copy of those wins on a write.
        private readonly HashSet<string> _ownSessions = new HashSet<string>(StringComparer.Ordinal);
        // Entry ids this ledger dropped past the cap, so a write does not bring them back from the file.
        private readonly HashSet<string> _dropped = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The <see cref="ChangeBus.Changed"/> subscriber.</summary>
        public void Record(ChangeNotification n)
        {
            try
            {
                if (n == null || !n.IsCommit || n.Undo == null || string.IsNullOrEmpty(n.SessionId)) return;
                lock (_gate)
                {
                    _liveSession = n.SessionId;
                    _ownSessions.Add(n.SessionId);
                    var steps = n.Undo.Steps;
                    var redo = n.Undo.RedoSteps;
                    if (n.Replay == null)
                    {
                        var added = steps > n.StepsBefore;
                        if (added)
                        {
                            // TE2 clears the whole redo stack when an edit records its first action, so everything
                            // above the step count the edit started from was that redo branch.
                            foreach (var e in Live(n.SessionId))
                                if (e.StepIndex > n.StepsBefore) e.Gone = true;
                        }
                        if (added || (n.Deltas?.Length ?? 0) > 0)
                        {
                            _entries.Add(new EditLedgerEntry
                            {
                                EntryId = UniqueId(),
                                SessionId = n.SessionId,
                                Revision = n.Revision,
                                When = DateTimeOffset.UtcNow,
                                Origin = n.Origin,
                                By = n.By,
                                Cause = n.Cause,
                                Label = n.Label,
                                Deltas = ForLedger(n.Deltas),
                                StepIndex = added ? steps : 0,
                                StepFirst = added ? n.StepsBefore + 1 : 0,
                                InFile = false,
                            });
                        }
                    }
                    foreach (var e in Live(n.SessionId))
                    {
                        e.Undone = e.StepIndex > steps;
                        if (e.StepFirst > steps + redo) e.Gone = true;
                    }
                    Trim();
                    Settle(n.SessionId);
                }
            }
            catch { /* a ride-along: the ledger must never fail a commit */ }
        }

        /// <summary>The model's file was just written: remember, for every entry of this session, whether the file holds its change. An entry
        /// holds when it is on the undo stack at <paramref name="undoSteps"/> or below; one that was taken back, or cleared from redo, does not.
        /// An entry with no undo step changed the model and cannot be taken back, so the file holds it. Entries made after <paramref name="savedRevision"/>
        /// (the revision in the turn that wrote the file) are left as they are.</summary>
        public void StampSaved(string sessionId, int undoSteps, long savedRevision)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            try
            {
                lock (_gate)
                {
                    foreach (var e in _entries)
                    {
                        if (!string.Equals(e.SessionId, sessionId, StringComparison.Ordinal) || (e.Kind ?? EditLedgerKinds.Edit) != EditLedgerKinds.Edit) continue;
                        if (e.Revision > savedRevision) continue;   // made after the file was written: the file does not hold it
                        if (BesideTheModelFile(e)) continue;        // the Primer is its own file, written when it changes and when it is taken back: this save says nothing about it
                        e.InFile = e.StepIndex <= 0 || (!e.Gone && e.StepIndex <= undoSteps);
                    }
                    Settle(sessionId);
                }
            }
            catch { /* a ride-along: a save must never fail on the history */ }
        }

        /// <summary>True for an entry whose every change lives in a file of its own that is written the moment it changes (the Primer), not in the model's file. A save of the
        /// model's file cannot tell whether such a change is there, so it neither stamps the entry nor lets History call it unsaved (Sol, join 8, A2 finding 3).</summary>
        internal static bool BesideTheModelFile(EditLedgerEntry e) =>
            e?.Deltas != null && e.Deltas.Length > 0 && e.Deltas.All(d => d != null && string.Equals(d.Ref, LocalEngine.PrimerRef, StringComparison.Ordinal));

        /// <summary>Write a flag row (a checkpoint saved or restored, a run's report) for <paramref name="sessionId"/>.
        /// Flag rows have no step and are never undoable.</summary>
        public EditLedgerEntry RecordFlag(string sessionId, string kind, string label, string by, string detail = null,
            WorkflowRunReport report = null, ChangeCauseInfo cause = null)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            try
            {
                lock (_gate)
                {
                    _ownSessions.Add(sessionId);
                    var e = new EditLedgerEntry
                    {
                        EntryId = UniqueId(), SessionId = sessionId, When = DateTimeOffset.UtcNow, Kind = kind,
                        Label = label, By = by, Detail = detail, Report = report, Cause = cause,
                        Deltas = Array.Empty<ChangeDelta>(),
                    };
                    _entries.Add(e);
                    Trim();
                    Settle(sessionId);
                    return e.Copy();
                }
            }
            catch { return null; }
        }

        // The ledger keeps shorter values than the wire (contract 8.3: 400 characters). Redaction already happened
        // before the broadcast, so nothing here can un-hide a value; this only shortens.
        private static ChangeDelta[] ForLedger(ChangeDelta[] deltas) =>
            (deltas ?? Array.Empty<ChangeDelta>()).Where(d => d != null).Select(d => new ChangeDelta
            {
                Kind = d.Kind,
                Ref = d.Ref,
                Props = d.Props,
                Before = ChangeValues.Cap(Uncut(d.Before), ChangeValues.LedgerCap),
                After = ChangeValues.Cap(Uncut(d.After), ChangeValues.LedgerCap),
            }).ToArray();

        // A wire value already cut at 2000 carries "(cut)"; cut again it would carry it twice.
        private static string Uncut(string v) =>
            v != null && v.Length > ChangeValues.LedgerCap && v.EndsWith(ChangeValues.Cut, StringComparison.Ordinal)
                ? v.Substring(0, v.Length - ChangeValues.Cut.Length) : v;

        // Entries of this session still on one of its stacks.
        private IEnumerable<EditLedgerEntry> Live(string sessionId) =>
            _entries.Where(e => !e.Gone && e.StepIndex > 0 && string.Equals(e.SessionId, sessionId, StringComparison.Ordinal));

        private bool OnStack(EditLedgerEntry e) =>
            !e.Gone && e.StepIndex > 0 && string.Equals(e.SessionId, _liveSession, StringComparison.Ordinal);

        // The cap applies per model (per home), so one busy model cannot push another's history out.
        private void Trim()
        {
            var drop = new HashSet<EditLedgerEntry>();
            foreach (var group in _entries.Where(e => !OnStack(e)).GroupBy(e => e.Home ?? "session:" + e.SessionId))
            {
                var off = group.ToList();   // commit order: the first are the oldest
                if (off.Count > MaxOffStack) foreach (var e in off.Take(off.Count - MaxOffStack)) drop.Add(e);
            }
            if (drop.Count == 0) return;
            foreach (var e in drop) if (e.EntryId != null) _dropped.Add(e.EntryId);
            _entries.RemoveAll(drop.Contains);
        }

        // ---- persistence (contract 8.5: .semanticus/history.json, keyed by the model's durable identity) --------

        /// <summary>Set by the owning engine: where a session's model keeps its ledger, or null when it has nowhere
        /// (never saved, not live). Asked again until it answers, so a model saved mid-session starts being kept
        /// from then on, its earlier entries included: they are filed and written the moment the home is found.</summary>
        public Func<string, LedgerHome> Locate { get; set; }

        public const string FileName = "history.json";
        private const string ModelsKey = "models";
        // One lock for every ledger file in the process: two engines (or two models in one folder) share a file. Another
        // engine PROCESS on the same model is kept out by SidecarFileLock, taken inside this one.
        private static readonly object FileGate = new object();
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly Dictionary<string, LedgerHome> _homes = new Dictionary<string, LedgerHome>(StringComparer.Ordinal);
        private readonly HashSet<string> _loaded = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _dirty = new HashSet<string>(StringComparer.Ordinal);
        private bool _writing;
        private Task _writer = Task.CompletedTask;

        // Under _gate. Find the session's home (once it has one), read what the file already holds for that model,
        // file this session's entries under it, and schedule a write.
        private void Settle(string sessionId)
        {
            var home = HomeOf(sessionId);
            if (home != null) Adopt(sessionId, home);
        }

        // Under _gate. File every entry of the session that has no home yet under this one, and schedule the write.
        private void Adopt(string sessionId, LedgerHome home)
        {
            foreach (var e in _entries)
                if (e.Home == null && string.Equals(e.SessionId, sessionId, StringComparison.Ordinal)) e.Home = home.Key;
            _dirty.Add(home.Key);
            if (!_writing) { _writing = true; _writer = Task.Run(WriteLoop); }
        }

        // Under _gate. THE MOMENT A HOME IS FOUND, the session's entries made before it are filed there and written
        // (Codex on pull request 393, finding 3). Before that they had nowhere to go, so a new model's first save left
        // them homeless until the next edit, and closing right after the save lost them. Whoever asks first (a commit,
        // a History read, or the save itself through EnsureLoaded) settles them.
        private LedgerHome HomeOf(string sessionId)
        {
            if (_homes.TryGetValue(sessionId, out var known)) return known;
            LedgerHome home = null;
            try { home = Locate?.Invoke(sessionId); } catch { home = null; }
            if (home == null || string.IsNullOrEmpty(home.File) || string.IsNullOrEmpty(home.Identity)) return null;
            _homes[sessionId] = home;
            if (_loaded.Add(home.Key)) Load(home);
            if (_entries.Any(e => e.Home == null && string.Equals(e.SessionId, sessionId, StringComparison.Ordinal)))
                Adopt(sessionId, home);
            return home;
        }

        // Earlier sessions' entries, read once per model. Reading fails open: a missing or broken file is an empty
        // history, never a refusal to edit (the same rule as the plan memory).
        private void Load(LedgerHome home)
        {
            EditLedgerEntry[] stored = null;
            try
            {
                lock (FileGate)
                {
                    if (!System.IO.File.Exists(home.File)) return;
                    var whole = JsonNode.Parse(System.IO.File.ReadAllText(home.File)) as JsonObject;
                    var node = (whole?[ModelsKey] as JsonObject)?[home.Identity]?["entries"];
                    if (node != null) stored = node.Deserialize<EditLedgerEntry[]>(Json);
                }
            }
            catch { stored = null; }
            if (stored == null) return;
            var have = new HashSet<string>(_entries.Select(e => e.EntryId), StringComparer.Ordinal);
            var older = stored.Where(e => e != null && !string.IsNullOrEmpty(e.EntryId) && have.Add(e.EntryId)).ToList();
            foreach (var e in older) { e.Home = home.Key; e.Deltas ??= Array.Empty<ChangeDelta>(); e.Kind ??= EditLedgerKinds.Edit; }
            _entries.InsertRange(0, older.OrderBy(e => e.When));
        }

        /// <summary>Runs once inside a file write, after the file was read and before it is written back. A test seam:
        /// it is the window a second engine process writing the same history used to fall into (Sol 6, slice 7,
        /// finding 4). Keyed by the file, so a test only ever stops its own write; empty on every real path and consumed
        /// once.</summary>
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action> AfterReadForTest =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Action>(StringComparer.Ordinal);

        // Test seam: hold each write back this long, so a test can prove a dispose waits for it. Zero in production.
        internal static int WriteDelayMsForTest;

        private void WriteLoop()
        {
            var delay = WriteDelayMsForTest;
            if (delay > 0) System.Threading.Thread.Sleep(delay);
            while (true)
            {
                var batch = new List<(LedgerHome Home, EditLedgerEntry[] Entries)>();
                HashSet<string> own, dropped;
                lock (_gate)
                {
                    if (_dirty.Count == 0) { _writing = false; return; }
                    foreach (var key in _dirty)
                    {
                        var home = _homes.Values.FirstOrDefault(h => h.Key == key);
                        if (home != null) batch.Add((home, _entries.Where(e => e.Home == key).Select(e => e.Copy()).ToArray()));
                    }
                    _dirty.Clear();
                    own = new HashSet<string>(_ownSessions, StringComparer.Ordinal);
                    dropped = new HashSet<string>(_dropped, StringComparer.Ordinal);
                }
                foreach (var (home, entries) in batch)
                {
                    try { Write(home, entries, own, dropped); }
                    catch (Exception ex) { try { Console.Error.WriteLine("[ledger] writing " + home.File + " failed: " + ex.Message); } catch { } }
                }
            }
        }

        // The plan memory's write discipline: lock (this process's and every process's), read the whole file, replace
        // only this model's entry, write a temp file, move it over the old one.
        //
        // THE MODEL'S ENTRY IS MERGED BY ID, NOT REPLACED (Sol 6, slice 7, finding 4). Another engine process on the same
        // model writes its own sessions' entries into the same list, and this ledger's snapshot does not have the ones
        // written after it loaded. So: this ledger's own sessions' entries are written as it holds them (it is the only
        // writer of those), every other session's entries are taken from the file as the file has them now, and an
        // entry this ledger dropped past its cap stays dropped.
        private static void Write(LedgerHome home, EditLedgerEntry[] entries, ISet<string> ownSessions, ISet<string> dropped)
        {
            lock (FileGate)
            {
                using var crossProcess = SidecarFileLock.Acquire(home.File);
                JsonObject whole = null;
                try { if (System.IO.File.Exists(home.File)) whole = JsonNode.Parse(System.IO.File.ReadAllText(home.File)) as JsonObject; }
                catch { whole = null; }
                whole ??= new JsonObject();
                if (AfterReadForTest.TryRemove(home.File, out var afterRead)) afterRead();
                if (whole[ModelsKey] is not JsonObject models) { models = new JsonObject(); whole[ModelsKey] = models; }
                EditLedgerEntry[] onDisk = null;
                try { onDisk = models[home.Identity]?["entries"]?.Deserialize<EditLedgerEntry[]>(Json); }
                catch { onDisk = null; }
                var merged = Merge(entries, onDisk ?? Array.Empty<EditLedgerEntry>(), ownSessions, dropped);
                models[home.Identity] = new JsonObject { ["entries"] = JsonSerializer.SerializeToNode(merged, Json) };
                var dir = System.IO.Path.GetDirectoryName(home.File);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                // Keep the file out of the model's git repository (LayoutStore.EnsureRuntimeIgnore says why).
                if (string.Equals(System.IO.Path.GetFileName(dir), LayoutStore.DirName, StringComparison.Ordinal))
                    try { LayoutStore.EnsureRuntimeIgnore(dir); } catch { /* the ignore is a courtesy; the write goes on */ }
                var tmp = home.File + ".tmp";
                System.IO.File.WriteAllText(tmp, whole.ToJsonString());
                System.IO.File.Move(tmp, home.File, overwrite: true);
            }
        }

        /// <summary>This ledger's own sessions' entries as it holds them, and every other session's as the file holds
        /// them, minus what this ledger dropped past its cap; oldest first.</summary>
        internal static EditLedgerEntry[] Merge(IEnumerable<EditLedgerEntry> mine, IEnumerable<EditLedgerEntry> onDisk,
                                                ISet<string> ownSessions, ISet<string> dropped)
        {
            bool Own(EditLedgerEntry e) => e.SessionId != null && ownSessions.Contains(e.SessionId);
            var kept = new Dictionary<string, EditLedgerEntry>(StringComparer.Ordinal);
            foreach (var e in onDisk.Where(e => e != null && !string.IsNullOrEmpty(e.EntryId) && !Own(e) && !dropped.Contains(e.EntryId)))
                kept.TryAdd(e.EntryId, e);
            foreach (var e in mine.Where(e => e != null && !string.IsNullOrEmpty(e.EntryId) && Own(e)))
                kept[e.EntryId] = e;
            return kept.Values.OrderBy(e => e.When).ToArray();
        }

        /// <summary>Read what the file holds for the session's model now, if it has a home: History calls this so
        /// a fresh session shows the earlier ones before its own first edit. A save calls it too, so a model's first
        /// save files and writes the entries made before it.</summary>
        public void EnsureLoaded(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            try { lock (_gate) HomeOf(sessionId); } catch { }
        }

        // Random ids, so none repeats across a restart; checked against the entries held, so none repeats within one.
        private string UniqueId()
        {
            string id;
            do id = NewId(); while (_entries.Any(e => string.Equals(e.EntryId, id, StringComparison.Ordinal)));
            return id;
        }

        private static string NewId()
        {
            Span<byte> b = stackalloc byte[4];
            RandomNumberGenerator.Fill(b);
            return "ed-" + Convert.ToHexString(b).ToLowerInvariant();
        }

        /// <summary>Wait for every pending write of the ledger file (tests, and engine shutdown).</summary>
        public async Task FlushAsync()
        {
            while (true)
            {
                Task writer;
                lock (_gate) { if (!_writing) return; writer = _writer; }
                await writer.ConfigureAwait(false);
            }
        }

        /// <summary>True once a run's report row is written, so a run publishes its report once.</summary>
        public bool HasRun(string runId)
        {
            lock (_gate) return _entries.Any(e => e.Kind == EditLedgerKinds.Run && string.Equals(e.Report?.RunId, runId, StringComparison.Ordinal));
        }

        /// <summary>True when the session's model has somewhere to keep its history.</summary>
        public bool HasHome(string sessionId)
        {
            lock (_gate) return !string.IsNullOrEmpty(sessionId) && HomeOf(sessionId) != null;
        }

        /// <summary>Every entry kept for the model at <paramref name="home"/>, and this session's own, oldest first.</summary>
        public IReadOnlyList<EditLedgerEntry> ForModel(string sessionId)
        {
            lock (_gate)
            {
                var home = string.IsNullOrEmpty(sessionId) ? null : HomeOf(sessionId);
                return _entries.Where(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal)
                        || (home != null && e.Home == home.Key))
                    .Select(e => e.Copy()).ToList();
            }
        }

        // ---- reads ------------------------------------------------------------------------------------------

        /// <summary>Every entry of one session, oldest first, as copies.</summary>
        public IReadOnlyList<EditLedgerEntry> Entries(string sessionId)
        {
            lock (_gate) return _entries.Where(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal)).Select(e => e.Copy()).ToList();
        }

        /// <summary>One entry by id, as a copy, or null.</summary>
        public EditLedgerEntry Find(string entryId)
        {
            lock (_gate) return _entries.FirstOrDefault(e => string.Equals(e.EntryId, entryId, StringComparison.Ordinal))?.Copy();
        }

        /// <summary>Contract 8.5: undoable when it is this session's, not undone, and on the stack now.</summary>
        public static bool IsUndoable(EditLedgerEntry e, string sessionId, int undoSteps) =>
            e != null && !e.Gone && !e.Undone && e.StepIndex > 0 && e.StepIndex <= undoSteps
            && string.Equals(e.SessionId, sessionId, StringComparison.Ordinal);

        public static bool IsRedoable(EditLedgerEntry e, string sessionId, int undoSteps) =>
            e != null && !e.Gone && e.Undone && e.StepFirst > undoSteps
            && string.Equals(e.SessionId, sessionId, StringComparison.Ordinal);

        /// <summary>Whether the work a cause did is still in the model, as far as the undo timeline can say. For every
        /// cause id of <paramref name="causeKind"/> with an edit on this model's history, the NEWEST such edit that was
        /// ever on an undo stack: <c>TakenBack</c> is true when it is undone, or gone with the redo branch a later edit
        /// cleared. Undo is linear, so once the newest is taken back every older edit of that cause is too.
        ///
        /// One answer for the three records that remember a change landed (a Plan row's applied set, a draft's run, a
        /// task said done): each stands only while this says its change is still in (UAT S01-j2-007, S12-3-002). An
        /// edit that was never on a stack (StepIndex 0) cannot be undone, so it says nothing either way.</summary>
        public IReadOnlyDictionary<string, (string EntryId, bool TakenBack)> NewestByCause(string sessionId, string causeKind)
        {
            var newest = new Dictionary<string, (string EntryId, bool TakenBack)>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(causeKind)) return newest;
            var entries = ForModel(sessionId);
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if (e?.Cause == null || e.StepIndex <= 0
                    || !string.Equals(e.Kind ?? EditLedgerKinds.Edit, EditLedgerKinds.Edit, StringComparison.Ordinal)
                    || !string.Equals(e.Cause.Kind, causeKind, StringComparison.Ordinal)) continue;
                foreach (var id in e.Cause.Ids ?? Array.Empty<string>())
                    if (id != null && !newest.ContainsKey(id)) newest[id] = (e.EntryId, e.Undone || e.Gone);
            }
            return newest;
        }

        /// <summary>The undoable entries, oldest (lowest step) first.</summary>
        public IReadOnlyList<EditLedgerEntry> Undoable(string sessionId, int undoSteps)
        {
            lock (_gate) return _entries.Where(e => IsUndoable(e, sessionId, undoSteps)).OrderBy(e => e.StepIndex).Select(e => e.Copy()).ToList();
        }

        /// <summary>The redoable entries, the next to redo first.</summary>
        public IReadOnlyList<EditLedgerEntry> Redoable(string sessionId, int undoSteps, int redoSteps)
        {
            lock (_gate)
                return _entries.Where(e => IsRedoable(e, sessionId, undoSteps) && e.StepIndex <= undoSteps + redoSteps)
                    .OrderBy(e => e.StepIndex).Select(e => e.Copy()).ToList();
        }

        /// <summary>Contract 8.5: how many undoable entries are newer than this one. -1 when it is not undoable.</summary>
        public int LaterCount(string entryId, string sessionId, int undoSteps)
        {
            lock (_gate)
            {
                var e = _entries.FirstOrDefault(x => string.Equals(x.EntryId, entryId, StringComparison.Ordinal));
                if (!IsUndoable(e, sessionId, undoSteps)) return -1;
                return _entries.Count(x => IsUndoable(x, sessionId, undoSteps) && x.StepIndex > e.StepIndex);
            }
        }

        // ---- walks (contract 8.6) ---------------------------------------------------------------------------

        public const string BeforeThisSessionSentence = "That change is from before this session. Restore a saved version instead.";

        /// <summary>A History parent row's id: the cause it groups by, "kind:id,id". Null for an edit that served none.</summary>
        public static string GroupIdOf(ChangeCauseInfo c) =>
            c?.Ids == null || c.Ids.Length == 0 ? null : c.Kind + ":" + string.Join(",", c.Ids);

        /// <summary>The label a walk to <paramref name="entryId"/> is recorded under: the entry's own, or for a parent
        /// row's id the cause's title. Null when nothing in the history has that id.</summary>
        public string LabelFor(string entryId, string sessionId)
        {
            lock (_gate)
            {
                var e = _entries.FirstOrDefault(x => string.Equals(x.EntryId, entryId, StringComparison.Ordinal));
                if (e != null) return string.IsNullOrWhiteSpace(e.Label) ? "an earlier change" : e.Label;
                var member = Members(entryId).OrderByDescending(x => string.Equals(x.SessionId, sessionId, StringComparison.Ordinal)).FirstOrDefault();
                return member == null ? null : string.IsNullOrWhiteSpace(member.Cause?.Title) ? "an earlier change" : member.Cause.Title;
            }
        }

        // The edits a parent row groups: every edit whose cause is that group id. History groups them per session, so a
        // walk takes this session's only.
        private IEnumerable<EditLedgerEntry> Members(string groupId) =>
            _entries.Where(e => e.Kind == EditLedgerKinds.Edit && string.Equals(GroupIdOf(e.Cause), groupId, StringComparison.Ordinal));

        // A PARENT ROW IS ACTIONABLE BY ITS OWN ID (Codex on pull request 393, finding 2). History offers Undo to here on
        // a parent when any of its edits is undoable, and Redo to here when any is undone and still redoable. Undo to a
        // parent takes back the whole group: it walks to its oldest undoable edit, and so, like any Undo to here, every
        // later change too (the row's LaterCount says how many). Redo to a parent brings the whole group back: it walks
        // to its newest redoable edit, and so every older undone change too. No single child id can stand for both
        // walks (the oldest for one, the newest for the other), so the ledger resolves the group id here instead.
        private EditLedgerEntry ResolveForWalk(string entryId, string sessionId, int undoSteps, bool redo)
        {
            if (_entries.Any(x => string.Equals(x.EntryId, entryId, StringComparison.Ordinal))) return Require(entryId, sessionId);
            var all = Members(entryId).ToList();
            if (all.Count == 0)
                throw new InvalidOperationException("No change with that id is in the history. Read the history again and pick a row from it.");
            var mine = all.Where(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal) && !e.Gone).ToList();
            if (mine.Count == 0)
                throw new InvalidOperationException(all.Any(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
                    ? "That change is no longer on the undo history: a later edit replaced it."
                    : BeforeThisSessionSentence);
            if (redo)
                return mine.Where(e => IsRedoable(e, sessionId, undoSteps)).OrderByDescending(e => e.StepIndex).FirstOrDefault()
                    ?? throw new InvalidOperationException("That change is not undone, so there is nothing to redo.");
            return mine.Where(e => IsUndoable(e, sessionId, undoSteps)).OrderBy(e => e.StepFirst).FirstOrDefault()
                ?? throw new InvalidOperationException(mine.Any(e => e.Undone)
                    ? "That change is already undone. Redo to it instead."
                    : "That change cannot be undone on its own: it made no step on the undo stack.");
        }

        /// <summary>How many steps to undo so that <paramref name="entryId"/> and everything newer is taken back.
        /// Call on the dispatcher, inside the walk's commit, so the count matches the stack it walks. Throws a
        /// sentence when the entry cannot be undone to. A History parent row's id walks its whole group.</summary>
        public int StepsToUndo(string entryId, string sessionId, int undoSteps)
        {
            lock (_gate)
            {
                var e = ResolveForWalk(entryId, sessionId, undoSteps, redo: false);
                if (e.Undone) throw new InvalidOperationException("That change is already undone. Redo to it instead.");
                if (!IsUndoable(e, sessionId, undoSteps))
                    throw new InvalidOperationException("That change cannot be undone on its own: it made no step on the undo stack.");
                return undoSteps - e.StepFirst + 1;
            }
        }

        /// <summary>The History rows a walk of <paramref name="steps"/> takes back (or brings back), oldest first: the
        /// edits of this session that sit on the steps it crosses. An approval for "Undo to here" is bound to exactly these
        /// rows, so a longer walk to another row with the same words is a different grant. Call on the dispatcher, like
        /// <see cref="StepsToUndo"/>.</summary>
        public IReadOnlyList<EditLedgerEntry> RowsWalked(string sessionId, int undoSteps, int steps, bool redo)
        {
            var from = redo ? undoSteps + 1 : undoSteps - steps + 1;
            var to = redo ? undoSteps + steps : undoSteps;
            lock (_gate)
                return _entries.Where(e => !e.Gone && e.StepIndex > 0 && e.StepIndex >= from && e.StepIndex <= to
                        && e.Undone == redo && string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
                    .OrderBy(e => e.StepIndex).Select(e => e.Copy()).ToList();
        }

        /// <summary>How many steps to redo so that <paramref name="entryId"/> and every older undone entry comes back.
        /// A History parent row's id brings its whole group back.</summary>
        public int StepsToRedo(string entryId, string sessionId, int undoSteps)
        {
            lock (_gate)
            {
                var e = ResolveForWalk(entryId, sessionId, undoSteps, redo: true);
                if (!IsRedoable(e, sessionId, undoSteps))
                    throw new InvalidOperationException(e.Undone
                        ? "That change can no longer be redone: a later edit replaced it."
                        : "That change is not undone, so there is nothing to redo.");
                return e.StepIndex - undoSteps;
            }
        }

        private EditLedgerEntry Require(string entryId, string sessionId)
        {
            var e = _entries.FirstOrDefault(x => string.Equals(x.EntryId, entryId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("No change with that id is in the history. Read the history again and pick a row from it.");
            if (!string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
                throw new InvalidOperationException(BeforeThisSessionSentence);
            if (e.Gone)
                throw new InvalidOperationException("That change is no longer on the undo history: a later edit replaced it.");
            return e;
        }
    }
}
