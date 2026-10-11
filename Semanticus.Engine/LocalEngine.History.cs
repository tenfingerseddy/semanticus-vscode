using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    // History inside the Plan, the engine half (Plan-heart contract 8): undo and redo to a point.
    public sealed partial class LocalEngine
    {
        /// <summary>Called once from the constructor: tell the ledger where each session's model keeps its history
        /// (<c>.semanticus/history.json</c> beside the model, under the model's durable identity, contract 8.5).</summary>
        private void AttachEditLedger()
        {
            _sessions.Ledger.Locate = sessionId =>
            {
                var context = _sessions.CurrentContext;
                if (context.Session == null || !string.Equals(context.Session.Id, sessionId, StringComparison.Ordinal)) return null;
                var dir = SidecarDir(context);
                var identity = PaneIdentity(context.Session, context.Live);
                return dir == null || identity == null ? null : new LedgerHome { File = Path.Combine(dir, EditLedger.FileName), Identity = identity };
            };
        }

        /// <summary>Contract 8.6: undo every step down to and including <paramref name="entryId"/> in ONE commit,
        /// one broadcast, one revision bump, labelled "Undo to" and the entry's label. Refused with a sentence when
        /// the entry is not undoable (from before this session, already undone, or never on the stack).</summary>
        public Task<UndoState> UndoToEntryAsync(string entryId, string origin) => WalkToEntryAsync(entryId, origin, redo: false);

        /// <summary>Contract 8.6: redo every undone step up to and including <paramref name="entryId"/> in one commit.</summary>
        public Task<UndoState> RedoToEntryAsync(string entryId, string origin) => WalkToEntryAsync(entryId, origin, redo: true);

        private async Task<UndoState> WalkToEntryAsync(string entryId, string origin, bool redo)
        {
            var context = _sessions.CurrentContext;
            var s = context.RequireSession();
            var ledger = _sessions.Ledger;
            // What the walk would cross, read on the dispatcher as the stack stands now. An assistant's walk is asked about at its
            // door, bound to the model's revision, so a longer walk to an older row can never spend an approval given for a shorter
            // one (verifier on 84bba3f2, F1).
            var (label, steps, rows, _) = await ReadWalkAsync(entryId, redo).ConfigureAwait(false);
            var rowKey = string.Join(",", rows.Select(r => r.EntryId));
            // The count is worked out again on the dispatcher, inside the walk's own commit, against the stack as it stands then:
            // an edit that landed after the read above is never walked past by mistake, and a walk that no longer crosses the rows
            // that were asked about is refused rather than run.
            await s.WalkAsync(origin, redo, label, () =>
            {
                var u = s.UndoStateNow();
                var n = redo ? ledger.StepsToRedo(entryId, s.Id, u.Steps) : ledger.StepsToUndo(entryId, s.Id, u.Steps);
                if (n != steps || string.Join(",", ledger.RowsWalked(s.Id, u.Steps, n, redo).Select(r => r.EntryId)) != rowKey)
                    throw new InvalidOperationException("The history changed while this was being asked. Read the history again and pick the row again.");
                return n;
            });
            await ReconcilePlanAfterHistoryAsync(context, s);
            await RefreshStartPlanAfterHistoryAsync(origin);   // History's "undo to here" can take a start plan's build back too: the page is told, like Undo and Redo (join 8, K14)
            return await s.ReadAsync(_ => s.UndoStateNow());
        }

        /// <summary>The rows a walk to <paramref name="entryId"/> would cross, how many steps that is and the revision it was read at,
        /// without changing anything. The walk itself, and the words an assistant's walk is asked about with, both start from this.</summary>
        internal async Task<(string Label, int Steps, IReadOnlyList<EditLedgerEntry> Rows, long Revision)> ReadWalkAsync(string entryId, bool redo)
        {
            if (string.IsNullOrWhiteSpace(entryId)) throw new ArgumentException("Name the history entry to walk to.", nameof(entryId));
            var context = _sessions.CurrentContext;
            var s = context.RequireSession();
            var ledger = _sessions.Ledger;
            // An edit's id, or a parent row's (its cause), which walks the whole group (EditLedger.ResolveForWalk).
            var named = ledger.LabelFor(entryId, s.Id)
                ?? throw new InvalidOperationException("No change with that id is in the history. Read the history again and pick a row from it.");
            var label = (redo ? "Redo to " : "Undo to ") + named;
            var (steps, rows, revision) = await s.ReadAsync(_ =>
            {
                var u = s.UndoStateNow();
                var n = redo ? ledger.StepsToRedo(entryId, s.Id, u.Steps) : ledger.StepsToUndo(entryId, s.Id, u.Steps);
                return (n, ledger.RowsWalked(s.Id, u.Steps, n, redo), s.Revision);
            }).ConfigureAwait(false);
            return (label, steps, rows, revision);
        }

        // ---- the merged History (contract 8.8 and 8.9) ------------------------------------------------------

        // Plan decisions that wrote nothing. The two that wrote something (done, approved) are ledger edits already.
        private static readonly HashSet<string> DecisionKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            ModelPlanHistoryKinds.Drawn, ModelPlanHistoryKinds.Redrawn, ModelPlanHistoryKinds.Started,
            ModelPlanHistoryKinds.Skipped, ModelPlanHistoryKinds.Unskipped, ModelPlanHistoryKinds.Refused,
            ModelPlanHistoryKinds.Proposed, ModelPlanHistoryKinds.Cancelled, ModelPlanHistoryKinds.Given,
        };

        /// <summary>True when every change of this entry is a plain property edit (an "update" with one property) and the object it names holds, in
        /// the model now, the text the edit wrote. Dispatcher thread (inside a read). An entry with any other kind of change, a value the ledger cut, or a
        /// property that cannot be read is not held: its flag stands.</summary>
        private static bool ModelStillHolds(Model m, EditLedgerEntry e)
        {
            var deltas = e.Deltas;
            if (deltas == null || deltas.Length == 0) return false;
            foreach (var d in deltas)
            {
                if (d == null || d.Kind != "update" || d.Props == null || d.Props.Length != 1 || d.After == null || d.After == d.Before) return false;
                if (d.After.EndsWith(ChangeValues.Cut, StringComparison.Ordinal)) return false;
                try
                {
                    var obj = ObjectRefs.Resolve(m, d.Ref);
                    var prop = obj?.GetType().GetProperty(d.Props[0]);
                    if (prop == null) return false;
                    var now = ChangeValues.For(obj, d.Props[0], null, prop.GetValue(obj)).After;
                    if (!string.Equals(ChangeValues.Cap(now, ChangeValues.LedgerCap), d.After, StringComparison.Ordinal)) return false;
                }
                catch { return false; }
            }
            return true;
        }

        /// <summary>Contract 8.8: the History, newest first, merged by the engine so both doors read one answer.
        /// <paramref name="by"/> keeps one doer ("you", "assistant", "semanticus", "workflow"); <paramref name="objectRef"/>
        /// keeps the rows that touched one object; <paramref name="before"/> (an ISO time, a row's When) pages back.</summary>
        public async Task<PlanHistoryView> GetPlanHistoryAsync(string by = null, string objectRef = null, string before = null, int limit = 50)
        {
            var context = _sessions.CurrentContext;
            var s = context.RequireSession();
            var ledger = _sessions.Ledger;
            ledger.EnsureLoaded(s.Id);
            var undo = await s.ReadAsync(_ => s.UndoStateNow());
            var entries = ledger.ForModel(s.Id);

            // The Verified word is joined by session and revision: the record is written inside the same op as the
            // edit, so that key is explicit rather than inferred (contract 8.4).
            var verified = new Dictionary<(string, long), string>();
            var recordAt = new Dictionary<(string, long), VerifiedEditRecord>();
            VerifiedEditsChain chain = null;
            try
            {
                chain = await s.ReadAsync(m => VerifiedEditsStore.Load(m));
                foreach (var r in chain.Records.Where(r => r.Revision > 0 && !string.IsNullOrEmpty(r.SessionId)))
                {
                    verified[(r.SessionId, r.Revision)] = r.Verdict;
                    recordAt[(r.SessionId, r.Revision)] = r;
                }
            }
            catch { /* the word is a ride-along; History still reads without it */ }

            IReadOnlyList<ModelPlanHistoryEntry> decisions = Array.Empty<ModelPlanHistoryEntry>();
            try
            {
                var bound = BindPlanMemory();
                decisions = PlanMemory.Read(bound.File, bound.Identity).History;
            }
            catch { }

            // An entry of an EARLIER session that its own session left flagged undone is struck unless the file held its change when the model was last
            // saved (EditLedgerEntry.InFile: the person saved it, took it back, and closed without saving again, so the file has it). The flag alone cannot know
            // whether the undo reached the file (join 6, the U3 note). An entry written before InFile was kept has no such record, so only for those the old
            // guess stands: a plain property edit whose words the model open now holds is not struck. That guess is wrong when a LATER undone row wrote the same
            // state back (UAT-J7-S4a-004), which is why a record, not a guess, is read wherever there is one.
            HashSet<string> heldByModel;
            try { heldByModel = await s.ReadAsync(m => entries.Where(e => !string.Equals(e.SessionId, s.Id, StringComparison.Ordinal) && e.Undone && e.InFile == null && ModelStillHolds(m, e)).Select(e => e.EntryId).ToHashSet(StringComparer.Ordinal)); }
            catch { heldByModel = new HashSet<string>(StringComparer.Ordinal); }

            var undoableSteps = entries.Where(e => EditLedger.IsUndoable(e, s.Id, undo.Steps)).Select(e => e.StepIndex).ToList();
            var lastRestore = entries.Where(e => e.Kind == EditLedgerKinds.Restored).Select(e => (DateTimeOffset?)e.When).Max();

            PlanHistoryRow Row(EditLedgerEntry e)
            {
                var mine = string.Equals(e.SessionId, s.Id, StringComparison.Ordinal);
                var undoable = EditLedger.IsUndoable(e, s.Id, undo.Steps);
                return new PlanHistoryRow
                {
                    Id = e.EntryId,
                    Kind = e.Kind ?? EditLedgerKinds.Edit,
                    When = e.When,
                    By = e.By ?? ChangeBy.For(e.Cause, e.Origin),
                    Title = e.Kind == EditLedgerKinds.Run ? e.Report?.Title ?? e.Label : e.Label,
                    Cause = e.Cause,
                    Deltas = e.Deltas ?? Array.Empty<ChangeDelta>(),
                    SessionId = e.SessionId,
                    Revision = e.Revision,
                    Undoable = undoable,
                    LaterCount = undoable ? undoableSteps.Count(x => x > e.StepIndex) : 0,
                    // Read off the undo timeline as it stands NOW, not only the flag the last commit left (UAT
                    // S12-4-003). An undone edit a later edit cleared from redo (gone) stays undone: the model no
                    // longer has it and it can never come back, so it is struck and offers no Redo (UAT S12-3-003).
                    // An entry of an EARLIER session has no stack to read: its flag is what the last commit of its own
                    // session saved, and that is the truth (UAT S07a-1-002, S09-1-005: a restart or a restore showed the
                    // edits a person had taken back as active, while the model had them reverted).
                    Undone = mine ? (e.Gone ? e.Undone : e.StepIndex > 0 ? e.StepIndex > undo.Steps : e.Undone) : e.Undone && (e.InFile != true || EditLedger.BesideTheModelFile(e)) && !heldByModel.Contains(e.EntryId),
                    Redoable = EditLedger.IsRedoable(e, s.Id, undo.Steps) && e.StepIndex <= undo.Steps + undo.RedoSteps,
                    Verified = e.Kind == EditLedgerKinds.Edit && verified.TryGetValue((e.SessionId, e.Revision), out var v) ? v : null,
                    BeforeThisSession = !mine,
                    // An edit of an earlier session the file never held and nothing took back: the session ended before a save, so the model open now lacks it. InFile is
                    // false only for an entry stamped by a save or made after the last one; an entry from before the record was kept (null) claims nothing.
                    NotSaved = !mine && e.Kind == EditLedgerKinds.Edit && !e.Undone && e.InFile == false && !EditLedger.BesideTheModelFile(e),
                    BeforeRestore = lastRestore != null && e.When < lastRestore && e.Kind != EditLedgerKinds.Restored,
                    Detail = e.Detail,
                    Report = e.Report,
                };
            }

            // Grouping is by the cause, never by time or revision: a parent row per plan item, run, draft or task
            // nests the edits whose cause names it, within one session.
            var rows = new List<PlanHistoryRow>();
            var parents = new Dictionary<string, (PlanHistoryRow Parent, List<PlanHistoryRow> Kids)>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                var row = Row(e);
                var c = e.Cause;
                if (c == null || c.Ids == null || c.Ids.Length == 0) { rows.Add(row); continue; }
                var causeId = EditLedger.GroupIdOf(c);
                var key = e.SessionId + "|" + causeId;
                if (!parents.TryGetValue(key, out var group))
                {
                    group = (new PlanHistoryRow
                    {
                        Id = causeId, Kind = ParentKind(c.Kind), Title = c.Title, Cause = c, SessionId = e.SessionId,
                        BeforeThisSession = row.BeforeThisSession,
                    }, new List<PlanHistoryRow>());
                    parents[key] = group;
                    rows.Add(group.Parent);
                }
                if (e.Kind == EditLedgerKinds.Run)
                {
                    // The run's own report row becomes the parent's report, not a child of itself.
                    group.Parent.Report = e.Report;
                    group.Parent.Title = e.Report?.Title ?? group.Parent.Title;
                    if (group.Parent.When < e.When) group.Parent.When = e.When;
                    continue;
                }
                group.Kids.Add(row);
            }
            foreach (var (parent, kids) in parents.Values)
            {
                kids.Sort((a, b) => b.When.CompareTo(a.When));
                parent.Children = kids.ToArray();
                if (kids.Count == 0) { parent.By ??= ChangeBy.Workflow; continue; }
                var newest = kids[0];
                var oldest = kids[kids.Count - 1];
                if (parent.When < newest.When) parent.When = newest.When;
                parent.By = newest.By;
                parent.Revision = newest.Revision;
                // The parent's id is actionable (EditLedger.ResolveForWalk): Undo to here walks to its oldest undoable
                // edit, Redo to here to its newest redoable one, so each is offered exactly when that edit exists.
                var firstUndoable = kids.LastOrDefault(k => k.Undoable);
                parent.Undoable = firstUndoable != null;
                parent.LaterCount = firstUndoable?.LaterCount ?? 0;
                parent.Redoable = kids.Any(k => k.Redoable);
                parent.Undone = kids.All(k => k.Undone);
                parent.NotSaved = kids.Any(k => k.NotSaved);
                parent.Verified = kids.Select(k => k.Verified).FirstOrDefault(x => x != null);
                parent.BeforeRestore = kids.All(k => k.BeforeRestore);
                parent.Deltas = kids.SelectMany(k => k.Deltas).ToArray();
            }

            foreach (var d in decisions.Where(d => d != null && DecisionKinds.Contains(d.Kind ?? "")))
            {
                rows.Add(new PlanHistoryRow
                {
                    Id = "decision:" + d.When.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + d.Kind,
                    Kind = "decision",
                    When = d.When,
                    By = d.Actor == ModelPlanActors.You ? ChangeBy.You : d.Actor == ModelPlanActors.Assistant ? ChangeBy.Assistant : ChangeBy.Semanticus,
                    Title = d.Title ?? d.Sentence ?? d.Text,
                    Cause = string.IsNullOrEmpty(d.ItemId) ? null : new ChangeCauseInfo { Kind = ChangeCauseKinds.PlanItem, Ids = new[] { d.ItemId }, Title = d.Title },
                    Deltas = Array.Empty<ChangeDelta>(),
                    Detail = (d.Kind ?? "") + ": " + (d.Sentence ?? d.Text),
                    Revision = d.Revision,
                    BeforeThisSession = d.When < s.OpenedUtc,
                    BeforeRestore = lastRestore != null && d.When < lastRestore,
                });
            }

            // Contract 8.8 (lane m11/plan-gaps): the rows no edit carries come from here too, so both doors read one
            // History. A Verified Edits record with no edit to sit on (a publish, a read that proved something, an edit
            // whose ledger entry is gone), and a saved version the ledger never flagged (the rescue a restore makes
            // first, versions saved before the ledger). The restored-state commit a restore makes last is left out:
            // the "Restored" flag row already says it.
            var editKeys = new HashSet<(string, long)>(rows.SelectMany(r => new[] { r }.Concat(r.Children ?? Array.Empty<PlanHistoryRow>()))
                .Where(r => r.SessionId != null && r.Revision > 0).Select(r => (r.SessionId, r.Revision)));
            foreach (var record in chain?.Records ?? Array.Empty<VerifiedEditRecord>())
            {
                if (record.Revision > 0 && editKeys.Contains((record.SessionId, record.Revision))) continue;
                if (!DateTimeOffset.TryParse(record.When, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var when)) continue;
                rows.Add(new PlanHistoryRow
                {
                    Id = "record:" + record.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Kind = "record",
                    When = when,
                    By = ChangeBy.For(null, record.Origin),
                    Title = record.Op,
                    Deltas = string.IsNullOrEmpty(record.ObjectRef) ? Array.Empty<ChangeDelta>()
                        : new[] { new ChangeDelta { Kind = "check", Ref = record.ObjectRef } },
                    SessionId = record.SessionId,
                    Revision = record.Revision,
                    Verified = record.Verdict,
                    BeforeThisSession = !string.Equals(record.SessionId, s.Id, StringComparison.Ordinal),
                    BeforeRestore = lastRestore != null && when < lastRestore,
                    // A publish that went ahead on a red check reads its written reason, word for word.
                    Detail = !string.IsNullOrEmpty(record.OverrideReason) && (record.Op == "deploy_live" || record.Op == "deploy_stage")
                        ? record.OverrideReason : record.Summary,
                    Record = record,
                });
            }
            if (string.IsNullOrWhiteSpace(objectRef))
            {
                HistoryCheckpointList versions = null;
                try { versions = await ListHistoryCheckpointsAsync(50); } catch { versions = null; }
                if (versions?.Supported == true)
                {
                    var flagged = new HashSet<string>(entries.Where(e => e.Kind == EditLedgerKinds.Saved && !string.IsNullOrEmpty(e.Detail))
                        .Select(e => e.Detail), StringComparer.Ordinal);
                    var mineStart = rows.Where(r => !r.BeforeThisSession && string.Equals(r.SessionId, s.Id, StringComparison.Ordinal))
                        .Select(r => (DateTimeOffset?)r.When).Min();
                    var olderEnd = rows.Where(r => r.BeforeThisSession).Select(r => (DateTimeOffset?)r.When).Max();
                    foreach (var cp in versions.Checkpoints ?? Array.Empty<HistoryCheckpoint>())
                    {
                        var label = cp.Label ?? string.Empty;
                        if (flagged.Contains(cp.Hash) || label.StartsWith("Restored to ", StringComparison.Ordinal)) continue;
                        if (!DateTimeOffset.TryParse(cp.When, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.RoundtripKind, out var when)) continue;
                        var rescue = label.StartsWith("Before restore to ", StringComparison.Ordinal);
                        rows.Add(new PlanHistoryRow
                        {
                            Id = "checkpoint:" + cp.Hash,
                            Kind = EditLedgerKinds.Saved,
                            When = when,
                            By = rescue ? ChangeBy.Semanticus : ChangeBy.You,
                            // The rescue's own label carries a commit id; the person reads what it is, not which commit.
                            Title = "Saved a version: " + (rescue ? "Before the restore" : label),
                            Deltas = Array.Empty<ChangeDelta>(),
                            SessionId = cp.SessionId,
                            BeforeThisSession = !string.IsNullOrEmpty(cp.SessionId) ? !string.Equals(cp.SessionId, s.Id, StringComparison.Ordinal)
                                : mineStart != null ? when < mineStart : olderEnd != null && when <= olderEnd,
                            BeforeRestore = lastRestore != null && when < lastRestore,
                            Detail = cp.Hash,
                        });
                    }
                }
            }
            // A broken chain is never read as Verified: a word whose record sits at or after the break says so.
            if (chain != null && !chain.ChainIntact)
            {
                bool Broken(VerifiedEditRecord r) => chain.FirstBrokenSeq <= 0 || (r != null && r.Seq >= chain.FirstBrokenSeq);
                void Distrust(PlanHistoryRow r)
                {
                    foreach (var c in r.Children ?? Array.Empty<PlanHistoryRow>()) Distrust(c);
                    if (string.IsNullOrEmpty(r.Verified)) return;
                    var rec = r.Record ?? (r.SessionId != null && recordAt.TryGetValue((r.SessionId, r.Revision), out var x) ? x : null);
                    if (Broken(rec) || (r.Children ?? Array.Empty<PlanHistoryRow>()).Any(c => c.Verified == "broken")) r.Verified = "broken";
                }
                foreach (var r in rows) Distrust(r);
            }

            IEnumerable<PlanHistoryRow> kept = rows;
            if (!string.IsNullOrWhiteSpace(by))
                kept = kept.Where(r => string.Equals(r.By, by.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(objectRef))
            {
                var target = objectRef.Trim();
                bool Touches(PlanHistoryRow r) => r.Deltas != null && r.Deltas.Any(d => string.Equals(d.Ref, target, StringComparison.OrdinalIgnoreCase));
                kept = kept.Where(Touches).Select(r =>
                {
                    if (r.Children != null) r.Children = r.Children.Where(Touches).ToArray();
                    return r;
                });
            }
            if (!string.IsNullOrWhiteSpace(before))
            {
                if (!DateTimeOffset.TryParse(before, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var cursor))
                    throw new ArgumentException("'before' is a row's When, as the history gave it.", nameof(before));
                kept = kept.Where(r => r.When < cursor);
            }
            var ordered = kept.OrderByDescending(r => r.When).ToList();
            var take = limit <= 0 ? 50 : Math.Min(limit, 500);
            return new PlanHistoryView
            {
                SessionId = s.Id,
                Rows = ordered.Take(take).ToArray(),
                More = ordered.Count > take,
                Kept = ledger.HasHome(s.Id),
            };
        }

        /// <summary>Contract 1.2 and 1.6: each Plan row's Events are the ledger edits whose cause names it, filed at
        /// write time (8.4), so opening a Done row shows exactly what it changed and links to it in History. Nothing is
        /// joined by time or revision. A plan item or a task is named by its own id; a draft and a run by the id their
        /// row wraps ("draft/dr-...", "run/wfr-...").</summary>
        private void StampEvents(Session s, IEnumerable<ModelPlanItem> items)
        {
            IReadOnlyList<EditLedgerEntry> entries;
            try
            {
                _sessions.Ledger.EnsureLoaded(s.Id);
                entries = _sessions.Ledger.ForModel(s.Id);
            }
            catch { return; }   // the ledger is a ride-along here: a plan still reads without it
            var byRow = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                if (e.Kind != EditLedgerKinds.Edit || e.Gone || e.Cause?.Ids == null) continue;
                foreach (var id in e.Cause.Ids.Where(x => !string.IsNullOrEmpty(x)))
                {
                    var row = e.Cause.Kind switch
                    {
                        ChangeCauseKinds.Draft => Composer.DraftRows.Prefix + id,
                        ChangeCauseKinds.WorkflowRun => RunRowPrefix + id,
                        _ => id,
                    };
                    if (!byRow.TryGetValue(row, out var list)) byRow[row] = list = new List<string>();
                    list.Add(e.EntryId);
                }
            }
            foreach (var item in items)
                item.Events = item.Id != null && byRow.TryGetValue(item.Id, out var ids) ? ids.ToArray() : Array.Empty<string>();
        }

        private static string ParentKind(string causeKind) => causeKind switch
        {
            ChangeCauseKinds.PlanItem => "item",
            ChangeCauseKinds.WorkflowRun => "run",
            ChangeCauseKinds.Draft => "draft",
            ChangeCauseKinds.Task => "task",
            ChangeCauseKinds.StartPlan => "start_plan",   // the one step that built the model from its start plan, not a Plan line
            _ => "item",
        };

        /// <summary>Contract 8.8 (Sol 24 Sep): when a run finishes or is aborted, its report is written into the
        /// ledger, so History can open it after a restart. The in-memory run store keeps 8 runs and none across one.</summary>
        private void RecordRunReport(SessionContext context, WorkflowRunState run, WorkflowRunView view)
        {
            try
            {
                var s = context.Session;
                if (s == null || run == null || _sessions.Ledger.HasRun(run.RunId)) return;
                string evidence = null, verdict = null;
                int? verified = null, total = null;
                try
                {
                    var doc = Evidence.WorkflowEvidenceRenderer.Build(run);
                    evidence = Evidence.EvidenceArtifact.Seal(doc).Json;
                    verdict = doc.Verdict?.ToString();
                    verified = doc.Coverage?.Verified; total = doc.Coverage?.Total;
                }
                catch { evidence = null; }
                var title = string.IsNullOrWhiteSpace(view?.Title) ? run.Def?.Name : view.Title;
                var report = new WorkflowRunReport
                {
                    RunId = run.RunId,
                    Workflow = run.Def?.Name,
                    Title = title,
                    Status = run.Status,
                    Verdict = verdict, StepsVerified = verified, StepsTotal = total,
                    Steps = (view?.Steps ?? Array.Empty<StepResult>()).Select(st => new WorkflowRunReportStep
                    {
                        StepId = st.StepId,
                        Title = st.Title,
                        Outcome = st.Status,
                        SubmittedBy = st.StepId != null && run.SubmittedBy.TryGetValue(st.StepId, out var who) ? who : null,
                        Checks = (st.VerifyResults ?? Array.Empty<VerifyResult>())
                            .Select(v => new WorkflowRunReportCheck { Kind = v.Kind, Verdict = v.Status, Detail = v.Detail }).ToArray(),
                    }).ToArray(),
                    Evidence = evidence,
                };
                _sessions.Ledger.RecordFlag(s.Id, EditLedgerKinds.Run, title + " " + run.Status, ChangeBy.Workflow, null, report,
                    new ChangeCauseInfo { Kind = ChangeCauseKinds.WorkflowRun, Ids = new[] { run.RunId }, Title = title });
            }
            catch { /* the report is a ride-along: a run never fails for it */ }
        }
    }
}
