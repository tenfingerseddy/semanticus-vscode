using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    public sealed class PlanRowEdit
    {
        public string Id { get; set; }
        public string Action { get; set; }
        public string Text { get; set; }
        public string Doer { get; set; }
    }

    public sealed class BatchPlanRowsResult : BatchEditResult
    {
        public ModelPlanView Plan { get; set; }
    }

    public sealed partial class LocalEngine
    {
        private const string BulkPlanModelSwitchedSentence =
            "A different model is open now. Completed items remain on the original model. Open its Plan to continue.";

        private sealed class PlanRowBatch
        {
            public PlanMemoryBinding Bound;
            public ModelPlanView Before;
            public string ActiveRow;
            public bool RowChanged;
        }

        // Only this call chain defers recomposition. Other readers and writers retain their normal path.
        private readonly AsyncLocal<PlanRowBatch> _planRowBatch = new AsyncLocal<PlanRowBatch>();
        internal Func<Task> PlanRowBeforeControlForTest;

        public async Task<BatchPlanRowsResult> PlanRowsAsync(PlanRowEdit[] items, string origin)
        {
            RequireProFeature();
            if (items == null) throw new ArgumentException("Pass an items list.");
            return await UnderReadyGateAsync(async () =>
            {
                var bound = BindPlanMemory();
                var before = await ComposeAsync(null, bound).ConfigureAwait(false);
                var rejected = new List<BatchRejection>();
                var changed = new HashSet<string>(StringComparer.Ordinal);
                var wrote = false;
                _planRowBatch.Value = new PlanRowBatch { Bound = bound, Before = before };
                try
                {
                    for (var i = 0; i < items.Length; i++)
                    {
                        if (!PlanMemoryStillOpen(bound)) throw new InvalidOperationException(BulkPlanModelSwitchedSentence);
                        var item = items[i];
                        try
                        {
                            if (item == null || string.IsNullOrWhiteSpace(item.Id)) throw new ArgumentException("A plan row id is required.");
                            var key = item.Id.Trim();
                            key = ReadPlanState(bound).FindTask(key)?.Id ?? key;
                            var action = (item.Action ?? "").Trim().ToLowerInvariant();
                            if (action != "restore" && !PlanRowBatchView(_planRowBatch.Value).Items.Any(x => x.Id == key))
                                throw new ArgumentException("No row on the plan has that id. Get the plan again.");
                            var memory = ReadPlanState(bound);
                            var memoryBefore = JsonSerializer.Serialize(memory);
                            _removedPlanRows.TryGetValue(key, out var removedBefore);
                            var batch = _planRowBatch.Value;
                            batch.ActiveRow = key;
                            batch.RowChanged = false;
                            if (PlanRowBeforeControlForTest != null) await PlanRowBeforeControlForTest();
                            switch (action)
                            {
                                case "pause": await HoldPlanItemAsync(key, true); break;
                                case "resume": await HoldPlanItemAsync(key, false); break;
                                case "note": await SetPlanTaskNoteAsync(key, item.Text); break;
                                case "doer": await SetPlanItemDoerAsync(key, item.Doer, origin); break;
                                case "skip": await SkipModelPlanItemAsync(key, item.Text, origin); break;
                                case "unskip": await UnskipModelPlanItemAsync(key, origin); break;
                                case "remove": await RemovePlanRowAsync(key); break;
                                case "restore": await RestorePlanRowAsync(key); break;
                                case "done": await MarkPlanTargetDoneAsync(key, origin); break;
                                default: throw new ArgumentException("Use pause, resume, note, doer, skip, unskip, remove, restore or done. Use apply_plan_items to run many rows.");
                            }
                            var after = ReadPlanState(bound);
                            _removedPlanRows.TryGetValue(key, out var removedAfter);
                            var removalChanged = !ReferenceEquals(removedBefore, removedAfter);
                            if (batch.RowChanged || removalChanged) changed.Add(key);
                            wrote |= removalChanged || memoryBefore != JsonSerializer.Serialize(after);
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                        {
                            if (!PlanMemoryStillOpen(bound)) throw new InvalidOperationException(BulkPlanModelSwitchedSentence, ex);
                            rejected.Add(new BatchRejection { Index = i, ObjRef = item?.Id, Reason = ex.Message });
                        }
                        finally { _planRowBatch.Value.ActiveRow = null; }
                    }
                }
                finally { _planRowBatch.Value = null; }
                var final = wrote ? await ComposeAsync(null, bound).ConfigureAwait(false) : before;
                if (wrote) TellThePlanMoved(bound);
                NoteBatch(bound.Session.Id, origin, changed.Count, "changed", "plan rows", "plan", undo: false);
                return new BatchPlanRowsResult { Revision = bound.Session.Revision, Applied = changed.Count, Rejected = rejected.ToArray(), Plan = final };
            }).ConfigureAwait(false);
        }

        // Compare inside the memory write lock. Another writer can change even this same row between
        // the call's reads, so snapshots outside the write cannot establish who changed it.
        private Action<PlanMemoryState> CountPlanRowChanges(Action<PlanMemoryState> change)
        {
            var batch = _planRowBatch.Value;
            var key = batch?.ActiveRow;
            if (key == null) return change;
            return memory =>
            {
                var before = PlanRowState(memory, key);
                change(memory);
                if (before != PlanRowState(memory, key)) batch.RowChanged = true;
            };
        }

        private static string PlanRowState(PlanMemoryState memory, string key)
        {
            memory.Residue.TryGetValue(key, out var residue);
            return JsonSerializer.Serialize(new
            {
                Task = memory.FindTask(key), Residue = residue, Skip = memory.Find(key),
                Linked = memory.Tasks.Where(t => t.OriginLink?.Kind == PlanOriginKinds.Item && t.OriginLink.Ref == key)
                    .OrderBy(t => t.Id).ToArray(),
            });
        }

        private ModelPlanView PlanRowBatchView(PlanRowBatch batch)
        {
            if (!PlanMemoryStillOpen(batch.Bound)) throw new InvalidOperationException(BulkPlanModelSwitchedSentence);
            var memory = ReadPlanState(batch.Bound);
            // The model does not change in a row-control batch. Refresh only the row state needed by the
            // single controls' decisions; the full final compose refreshes evidence, grouping and blockers.
            var view = JsonSerializer.Deserialize<ModelPlanView>(JsonSerializer.Serialize(batch.Before));
            var rows = view.Items.Where(i => !PlanTasks.IsTaskId(i.Id) && !_removedPlanRows.ContainsKey(i.Id)).ToList();
            foreach (var row in rows)
            {
                row.Held = false;
                row.Note = null;
                StampDerived(row, memory);
                var skip = memory.Find(row.Id);
                if (skip != null && row.Status != ModelPlanStatuses.Done) row.Status = ModelPlanStatuses.Skipped;
                else if (row.Status == ModelPlanStatuses.Skipped && batch.Before.Items.Any(i => i.Id == row.Id && i.SkipReason != null))
                    row.Status = ModelPlanStatuses.Ready;
                row.SkipReason = skip?.Reason;
                PlanTasks.StampState(row, null, false, memory.AssistantQueue);
            }
            foreach (var task in memory.Tasks)
            {
                var row = PlanTasks.ItemFor(task, batch.Bound.Nowhere, SkipFor(memory, task));
                var checkedDone = batch.Before.Items.Any(i => i.Id == task.Id && i.StateWord == PlanStateWords.Done);
                PlanTasks.StampState(row, task, checkedDone, memory.AssistantQueue);
                rows.Add(row);
            }
            view.Items = rows.ToArray();
            return view;
        }
    }
}
