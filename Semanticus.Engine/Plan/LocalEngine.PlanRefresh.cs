// Refresh plan (Kane, 11 Oct 2026: "a refresh the plan button that clears out done items and checks for any new items").
//
// WHAT IT DOES. The plan is already worked out from the model on every read, and its scans are kept only while the model's
// revision stands still. Refresh drops that kept copy, so AI readiness and Model quality are scanned again NOW, and the rows
// the new scan finds join the list. Then every finished row leaves the list: its id goes into PlanMemoryState.ClearedDone
// and the compose leaves it out. Nothing is deleted. The Done record, the task and the History entries stay as they were,
// and a row that opens again leaves ClearedDone, so finishing it again shows it again. Set-aside rows stay set aside.
//
// ON BOTH DOORS (golden rule 2): refreshPlan on EngineRpcTarget and refresh_plan on the MCP door, through the gate as an edit
// to plan memory (GateRegistry, EditNoPreview), like the other Plan controls. Neither changes the model.
//
// THE SENTENCE. One line, the same on both doors: "2 new tasks, 3 done tasks cleared." or "Nothing new."

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>What refreshPlan answers: what changed, in counts and one sentence, and the whole plan after it.</summary>
    public sealed class PlanRefreshResult
    {
        /// <summary>Rows open now that were not open before the refresh.</summary>
        public int Added { get; set; }
        /// <summary>Their ids, in plan order.</summary>
        public string[] AddedIds { get; set; } = Array.Empty<string>();
        /// <summary>Finished rows taken off the list. Their record stays in the plan's history.</summary>
        public int Cleared { get; set; }
        public string Sentence { get; set; }
        public ModelPlanView Plan { get; set; }
    }

    public sealed partial class LocalEngine
    {
        /// <summary>Scan again now, add what is new, and take the finished rows off the list.</summary>
        public async Task<PlanRefreshResult> RefreshPlanAsync()
        {
            var bound = BindPlanMemory();
            var before = await ComposeAsync(null, bound).ConfigureAwait(false);
            var openBefore = new HashSet<string>(before.Items.Where(IsOpenRow).Select(i => i.Id), StringComparer.Ordinal);

            // The kept scans go, so the next compose reads AI readiness and Model quality again.
            await _modelPlanGate.WaitAsync().ConfigureAwait(false);
            try { _planFacts = null; }
            finally { _modelPlanGate.Release(); }
            var fresh = await ComposeAsync(null, bound).ConfigureAwait(false);

            var added = fresh.Items.Where(i => IsOpenRow(i) && !openBefore.Contains(i.Id)).ToList();
            var memory = PlanMemory.Read(bound.File, bound.Identity);
            var undone = await UndoneAppliesAsync(bound.Session, memory).ConfigureAwait(false);
            // The Studio keeps vanished completed tasks visible during this session. Earlier sessions have no such rows.
            // NewestByCause includes the model's older history, so also check the entry's session before counting it.
            var currentApplies = _sessions.Ledger.NewestByCause(bound.Session.Id, ChangeCauseKinds.PlanItem)
                .Where(p => !p.Value.TakenBack && string.Equals(_sessions.Ledger.Find(p.Value.EntryId)?.SessionId,
                    bound.Session.Id, StringComparison.Ordinal))
                .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var missing = memory.Changes.Where(p => p.Value?.State == ModelPlanChangeStates.Applied
                && currentApplies.Contains(p.Key)
                && !fresh.Items.Any(i => i.Id == p.Key) && !memory.ClearedDone.Contains(p.Key)
                && !undone.Contains(p.Key)).Select(p => p.Key);
            var done = fresh.Items.Where(IsDoneRow).Select(i => i.Id).Concat(missing).Distinct(StringComparer.Ordinal).ToList();
            var titles = fresh.Items.Where(IsDoneRow).Select(i => i.Title)
                .Concat(memory.History.Where(h => done.Contains(h.ItemId)).Select(h => h.Title))
                .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToArray();
            var sentence = RefreshSentence(added.Count, done.Count);

            if (added.Count == 0 && done.Count == 0)
                return new PlanRefreshResult { Sentence = sentence, Plan = fresh };

            WritePlanTasks(bound, state =>
            {
                // Only the rows finished now: an id cleared before that is not finished now has no row to hide.
                state.ClearedDone = state.ClearedDone.Union(done, StringComparer.Ordinal).ToList();   // rows cleared before stay cleared: the compose leaves them out, so they are never in `done`
                state.History.Add(new ModelPlanHistoryEntry
                {
                    When = DateTimeOffset.UtcNow,
                    Kind = ModelPlanHistoryKinds.Redrawn,
                    Actor = ModelPlanActors.Plan,
                    Revision = fresh.Revision,
                    Sentence = "Refreshed the plan: " + char.ToLowerInvariant(sentence[0]) + sentence.Substring(1),
                    Text = "Refreshed the plan: " + char.ToLowerInvariant(sentence[0]) + sentence.Substring(1),
                    Appeared = added.Select(i => i.Title).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray(),
                    Detail = titles,
                });
            });
            var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
            TellThePlanMoved(bound);
            return new PlanRefreshResult
            {
                Added = added.Count, AddedIds = added.Select(i => i.Id).ToArray(), Cleared = done.Count, Sentence = sentence, Plan = plan,
            };
        }

        private static bool IsDoneRow(ModelPlanItem i) => string.Equals(i.Status, ModelPlanStatuses.Done, StringComparison.Ordinal);

        /// <summary>A row with work left: not finished and not set aside.</summary>
        private static bool IsOpenRow(ModelPlanItem i) =>
            !IsDoneRow(i) && !string.Equals(i.Status, ModelPlanStatuses.Skipped, StringComparison.Ordinal);

        /// <summary>The rows the list shows: every row but a finished one Refresh plan cleared.</summary>
        private static IEnumerable<ModelPlanItem> ClearedOut(IEnumerable<ModelPlanItem> rows, IReadOnlyCollection<string> cleared)
        {
            if (cleared == null || cleared.Count == 0) return rows;
            var ids = new HashSet<string>(cleared, StringComparer.Ordinal);
            return rows.Where(i => !(IsDoneRow(i) && i.Id != null && ids.Contains(i.Id)));
        }

        internal static string RefreshSentence(int added, int cleared)
        {
            var parts = new List<string>();
            if (added > 0) parts.Add(added + " new " + (added == 1 ? "task" : "tasks"));
            if (cleared > 0) parts.Add(cleared + " done " + (cleared == 1 ? "task" : "tasks") + " cleared");
            if (parts.Count == 0) return "Nothing new.";
            var line = string.Join(", ", parts) + ".";
            return char.ToUpperInvariant(line[0]) + line.Substring(1);
        }
    }
}
