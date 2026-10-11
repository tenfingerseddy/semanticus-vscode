// Remove a row from the Plan, and put it back (Kane's UAT on 25 Sep: "no way to delete a task once you add it").
//
// WHAT CAN BE REMOVED. A row a person or a page ASKED for: a task (typed in the field, sent from a page, a migrated aim,
// a repeat's card) and a composer draft ("draft/<id>"). A row the rules FOUND is recomputed from the model on every call,
// so it cannot be deleted; Set aside is its way off the list and stays exactly as it was. A part of a draft (a task whose
// link back is the draft) goes with its draft, never on its own: removing one part alone would leave the draft pointing
// at a request that no longer exists.
//
// PERSON-ONLY, like adding a task (contract 12): an assistant must not be able to take the person's work off the Plan.
// The two calls live on HumanGovernanceRpcTarget and on no MCP tool.
//
// UNDO. Removing writes the Plan's own file, not the model, so it is not a step on the model's undo timeline. The row is
// kept in this engine's memory instead, and restorePlanRow puts it back where it stood. The toast that offers Undo lives
// only as long as the page does, and so does this memory: a restart forgets it, which is the same promise a toast makes.
//
// THE ASSISTANT IS TOLD THROUGH THE NORMAL CHANNEL. A task an assistant had claimed is removed all the same (the person
// decides), and the next fold line that assistant reads says so, once. An answer it sends afterwards is refused with the
// same sentence, so it cannot mistake the silence for success.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine.Composer;

namespace Semanticus.Engine
{
    /// <summary>What removePlanRow answers: the row that went, and the whole plan after it.</summary>
    public sealed class PlanRemoveResult
    {
        /// <summary>The row id that was removed ("pt-..." or "draft/dr-..."). Pass it to restorePlanRow to undo.</summary>
        public string ItemId { get; set; }
        public string Title { get; set; }
        /// <summary>"task" or "draft".</summary>
        public string Kind { get; set; }
        /// <summary>True when an assistant held the task (or a part of the draft) and will read that it was removed.</summary>
        public bool ToldAssistant { get; set; }
        public string Sentence { get; set; }
        public ModelPlanView Plan { get; set; }
    }

    public sealed partial class LocalEngine
    {
        /// <summary>A removed row, kept so Undo can put it back exactly.</summary>
        private sealed class RemovedPlanRow
        {
            public PlanMemoryBinding Bound;
            public PlanTask Task;
            public int Index;
            public Draft Draft;
            public List<(PlanTask Task, int Index)> Parts = new List<(PlanTask, int)>();
        }

        /// <summary>Removed rows by row id, for Undo. Engine memory only (see the header).</summary>
        private readonly ConcurrentDictionary<string, RemovedPlanRow> _removedPlanRows =
            new ConcurrentDictionary<string, RemovedPlanRow>(StringComparer.Ordinal);

        /// <summary>Removed task ids by their words, so an answer sent for one later is refused with a true sentence.</summary>
        private readonly ConcurrentDictionary<string, string> _removedTaskWords =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Reader to the lines its next fold carries: "the person removed the task you held".</summary>
        private readonly ConcurrentDictionary<string, List<(string TaskId, string Line)>> _removalNotices =
            new ConcurrentDictionary<string, List<(string, string)>>(StringComparer.Ordinal);

        internal static string RemovedTaskLine(string words) =>
            "Plan: the person removed the task \"" + words + "\" from the Plan. Stop working on it; nothing you send for it is kept.";

        /// <summary>Remove one task or draft row from the Plan. Person-only.</summary>
        public async Task<PlanRemoveResult> RemovePlanRowAsync(string id)
        {
            var key = id?.Trim();
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Name the row to remove. Call get_model_plan for the ids.", nameof(id));
            return DraftRows.IsDraftRow(key) ? await RemoveDraftRowAsync(key).ConfigureAwait(false)
                                             : await RemoveTaskRowAsync(key).ConfigureAwait(false);
        }

        private async Task<PlanRemoveResult> RemoveTaskRowAsync(string id)
        {
            await _queueGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var bound = BindPlanMemory();
                var task = ReadPlanState(bound).FindTask(id);
                if (task == null)
                    throw new ArgumentException(PlanTasks.IsTaskId(id)
                        ? "No task on the plan has that id. It may have been removed already."
                        : "Only a task or a draft can be removed. A row Semanticus found comes back while the model still has the gap; set it aside instead.", nameof(id));
                if (task.OriginLink?.Kind == PlanOriginKinds.Draft)
                    throw new InvalidOperationException("This is a part of a draft. Remove the draft's row, and its parts go with it.");

                RemovedPlanRow kept = null;
                WritePlanTasks(bound, state =>
                {
                    var i = state.Tasks.FindIndex(t => string.Equals(t.Id, task.Id, StringComparison.Ordinal));
                    if (i < 0) return;
                    kept = new RemovedPlanRow { Bound = bound, Task = state.Tasks[i], Index = i };
                    state.Tasks.RemoveAt(i);
                });
                if (kept == null) throw new ArgumentException("No task on the plan has that id. It may have been removed already.", nameof(id));

                var told = TellHolderItWasRemoved(kept.Task);
                ClearPointersTo(kept.Task.Id);
                _removedPlanRows[kept.Task.Id] = kept;
                _removedTaskWords[kept.Task.Id] = kept.Task.Words;
                var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
                TellThePlanMoved(bound);
                return new PlanRemoveResult
                {
                    ItemId = kept.Task.Id, Title = kept.Task.Words, Kind = "task", ToldAssistant = told, Plan = plan,
                    Sentence = "Removed from the Plan." + (told ? " Your assistant was working on it; it reads that it was removed on its next call." : ""),
                };
            }
            finally { _queueGate.Release(); }
        }

        private async Task<PlanRemoveResult> RemoveDraftRowAsync(string rowId)
        {
            var p = BindDrafts();
            RemovedPlanRow kept;
            var told = false;
            await _draftGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var d = ReadDraft(p, rowId)
                    ?? throw new ArgumentException("No draft of this model has that id. It may have been removed already.", nameof(rowId));
                if (d.Run?.Outcome == "running" && !RunCutOff(d))
                    throw new InvalidOperationException("It is running now. Wait for it to finish, then remove it.");
                kept = new RemovedPlanRow { Bound = p.Bound, Draft = d };

                await _queueGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // The draft's parts leave with it: an open part waiting for the assistant would otherwise stay on
                    // its queue, asking for words for a draft nobody can run.
                    WritePlanTasks(p.Bound, state =>
                    {
                        for (var i = state.Tasks.Count - 1; i >= 0; i--)
                        {
                            var t = state.Tasks[i];
                            if (t.OriginLink?.Kind != PlanOriginKinds.Draft || !string.Equals(t.OriginLink.Ref, d.Id, StringComparison.Ordinal)) continue;
                            kept.Parts.Insert(0, (t, i));
                            state.Tasks.RemoveAt(i);
                        }
                    });
                    foreach (var (t, _) in kept.Parts)
                    {
                        if (PlanTasks.IsOpenTask(t)) told |= TellHolderItWasRemoved(t);
                        ClearPointersTo(t.Id);
                        _removedTaskWords[t.Id] = t.Words;
                    }
                }
                finally { _queueGate.Release(); }

                DeleteDraft(p, d.Id);
            }
            finally { _draftGate.Release(); }

            _removedPlanRows[DraftRows.Prefix + kept.Draft.Id] = kept;
            var plan = await ComposeAsync(null, p.Bound).ConfigureAwait(false);
            TellThePlanMoved(p.Bound);
            return new PlanRemoveResult
            {
                ItemId = DraftRows.Prefix + kept.Draft.Id, Title = kept.Draft.Title, Kind = "draft", ToldAssistant = told, Plan = plan,
                Sentence = "Removed the draft from the Plan." + (DraftStore.IsClosed(kept.Draft) || kept.Draft.Run?.Changed == true
                    ? " What it changed in the model stays; undo that from History."
                    : " Nothing in the model changed."),
            };
        }

        /// <summary>Undo a removal: the row goes back where it stood. Person-only.</summary>
        public async Task<ModelPlanView> RestorePlanRowAsync(string id)
        {
            var key = id?.Trim();
            if (string.IsNullOrEmpty(key) || !_removedPlanRows.TryGetValue(key, out var kept))
                throw new ArgumentException("There is nothing to put back for that row.", nameof(id));
            var bound = BindPlanMemory();
            if (!ReferenceEquals(bound.Context, kept.Bound.Context))
                throw new InvalidOperationException("That row belongs to a model that is not open now, so it cannot be put back here.");

            if (kept.Draft != null)
            {
                var p = BindDrafts();
                await _draftGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (ReadDraft(p, kept.Draft.Id) == null) WriteDraft(p, kept.Draft);
                    await PutTasksBackAsync(bound, kept.Parts).ConfigureAwait(false);
                }
                finally { _draftGate.Release(); }
            }
            else await PutTasksBackAsync(bound, new List<(PlanTask, int)> { (kept.Task, kept.Index) }).ConfigureAwait(false);

            _removedPlanRows.TryRemove(key, out _);
            var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
            TellThePlanMoved(bound);
            return plan;
        }

        private async Task PutTasksBackAsync(PlanMemoryBinding bound, List<(PlanTask Task, int Index)> tasks)
        {
            await _queueGate.WaitAsync().ConfigureAwait(false);
            try
            {
                WritePlanTasks(bound, state =>
                {
                    foreach (var (t, index) in tasks.OrderBy(x => x.Index))
                    {
                        if (state.FindTask(t.Id) != null) continue;
                        // The assistant was told it was removed and its claim is gone: the task comes back unclaimed, and
                        // is offered again on the next call like any other Ready task.
                        if (t.Claim != null && t.Claim.ClaimedBy != PlanDoers.You)
                        {
                            t.Claim = null;
                            if (t.State == PlanTaskStates.Claimed) t.State = PlanTaskStates.Open;
                        }
                        state.Tasks.Insert(Math.Min(Math.Max(index, 0), state.Tasks.Count), t);
                    }
                });
                foreach (var (t, _) in tasks)
                {
                    _removedTaskWords.TryRemove(t.Id, out _);
                    WithdrawRemovalNotice(t.Id);
                }
            }
            finally { _queueGate.Release(); }
        }

        /// <summary>When an assistant of this engine run holds the task, its next fold line says it was removed.</summary>
        private bool TellHolderItWasRemoved(PlanTask t)
        {
            var c = t?.Claim;
            if (c == null || c.ClaimedBy == PlanDoers.You || string.IsNullOrEmpty(c.Reader)) return false;
            if (!string.Equals(c.EngineRunId, EngineRunId, StringComparison.Ordinal)) return false;
            var list = _removalNotices.GetOrAdd(c.Reader, _ => new List<(string, string)>());
            lock (list) list.Add((t.Id, RemovedTaskLine(t.Words)));
            return true;
        }

        private void WithdrawRemovalNotice(string taskId)
        {
            foreach (var list in _removalNotices.Values)
                lock (list) list.RemoveAll(n => string.Equals(n.TaskId, taskId, StringComparison.Ordinal));
        }

        /// <summary>The removal lines waiting for this reader, taken once; null when there are none.</summary>
        private string TakeRemovalNotices(string reader)
        {
            if (!_removalNotices.TryGetValue(reader, out var list)) return null;
            lock (list)
            {
                if (list.Count == 0) return null;
                var text = string.Join("\n", list.Select(n => n.Line));
                list.Clear();
                return text;
            }
        }

        /// <summary>The refusal an answer for a task that was removed gets, or null when it was not removed.</summary>
        private string RemovedSentenceFor(string id) =>
            id != null && _removedTaskWords.TryGetValue(id, out var words)
                ? "The person removed the task \"" + words + "\" from the Plan. Stop working on it. Nothing was changed."
                : null;

        private void DeleteDraft(DraftPlace p, string draftId)
        {
            if (!PlanMemoryStillOpen(p.Bound)) throw new InvalidOperationException(PlanMemoryModelSwitchedSentence);
            if (p.Dir != null) { DraftStore.Delete(p.Dir, draftId); return; }
            var held = _sessionDrafts.GetOrCreateValue(p.Bound.Context);
            lock (held) held.Remove(draftId);
        }
    }
}
