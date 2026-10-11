// What the agent door answers when it changes the plan (verify L-f, round 4, 29 Sep; coordinator ruling for round 5).
//
// apply_plan_items, model_plan_action, skip_model_plan_item and unskip_model_plan_item used to answer with the WHOLE plan
// beside the sentence: 195,821 to 281,317 characters on a 12-table model, 3.8 million on a 160-table one. The sentence that
// names what Run the plan left out was inside it, so Claude Code cut it and saved the rest to a file. Now the agent door
// answers with the answer, first: what changed, and what was left out and why (every one, never cut), then the new state of
// the rows the call touched, the plan's digest and a pointer to get_model_plan for the rest. At most 40,000 characters of the
// JSON as it goes on the wire, on any model.
//
// It is only this door's projection. IEngine and the pipe still carry the whole plan, because the Studio reads it: the
// Studio's own path (EngineRpcTarget, the pipe) is unchanged and both doors keep every capability they had.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Semanticus.Engine
{
    /// <summary>A row the call touched, as the plan reads it after the call: its state, not its detail.</summary>
    public sealed class PlanRowNow
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string StateWord { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string StateNote { get; set; }
        public string Doer { get; set; }
        public string State { get; set; }
        public string Touches { get; set; }
        /// <summary>The state of the row's worked-out changes ("proposed", "applied"...), when it has any.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string ChangeState { get; set; }
        /// <summary>The digest of those changes, which approve and apply_plan_items make you quote.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string ChangeDigest { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string SkipReason { get; set; }
        /// <summary>The first few rows it waits for, by id (its own read lists them all).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[] WaitsFor { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? WaitsForCount { get; set; }
    }

    /// <summary>The agent door's answer to a change to the plan. The answer comes first: property order is the wire order.</summary>
    public sealed class PlanActionShort
    {
        /// <summary>What happened, or why nothing did, and what was left out.</summary>
        public string Sentence { get; set; }
        public bool Performed { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Action { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string ItemId { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Applied { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[] ItemIds { get; set; }
        /// <summary>The History entry of a run's first batch: undo(toEntry) with it takes the whole run back. The agent door sees what
        /// the window's receipt shows, so the two doors agree on what a press did.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string EntryId { get; set; }
        /// <summary>How many batches the run took, when it took more than one (a row that waited for a row in the run ran in the next).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Waves { get; set; }
        /// <summary>True when the run left the model with changes that are not saved and saving is what is meant. The run never saves by itself.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? CanSave { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string FailedItemId { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string FailedLine { get; set; }
        /// <summary>Everything Run the plan left out, and why, each named. Never cut to fit; when a run left out so many
        /// that the answer cannot carry them, <see cref="SkippedNote"/> says how many are not listed and where they are.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ApplyPlanSkip[] Skipped { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string SkippedNote { get; set; }
        public long Revision { get; set; }
        /// <summary>The new state of the rows this call touched. It is not the plan.</summary>
        public PlanRowNow[] Rows { get; set; } = Array.Empty<PlanRowNow>();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string RowsNote { get; set; }
        /// <summary>The plan's ready digest now: quote it to run the plan again.</summary>
        public string ReadyDigest { get; set; }
        /// <summary>How many rows are in each state word now, so the whole plan's shape is known without reading it.</summary>
        public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();
        /// <summary>The newest few history entries, each with its first lines: what this call added is at the end.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ModelPlanHistoryEntry[] History { get; set; }
        /// <summary>Where the rest is.</summary>
        public string More { get; set; }
    }

    internal static class PlanResultShape
    {
        /// <summary>The most characters the touched rows and the left-out list take, each; the answer as a whole stays under
        /// <see cref="PlanReadShape.ReadChars"/>.</summary>
        internal const int RowsChars = 12_000;
        internal const int SkippedChars = 20_000;

        internal const string MoreLine =
            "This is the answer, not the plan. get_model_plan reads the plan (item = a row's id reads that row's lines, item = rows pages the rows).";

        public static PlanActionShort Of(ApplyPlanItemsResult r)
        {
            if (r == null) return null;
            var touched = new List<string>(r.ItemIds ?? Array.Empty<string>());
            if (!string.IsNullOrEmpty(r.FailedItemId)) touched.Add(r.FailedItemId);
            touched.AddRange((r.Skipped ?? Array.Empty<ApplyPlanSkip>()).Where(s => s.What == null && s.ItemId != null).Select(s => s.ItemId));
            var short_ = new PlanActionShort
            {
                Sentence = r.Sentence, Performed = r.Performed, Applied = r.Performed || r.Applied > 0 ? r.Applied : (int?)null,
                ItemIds = r.ItemIds != null && r.ItemIds.Length > 0 ? r.ItemIds : null,
                FailedItemId = r.FailedItemId, FailedLine = r.FailedLine, Revision = r.Revision,
                EntryId = r.EntryId, Waves = r.Waves > 1 ? r.Waves : (int?)null, CanSave = r.Performed ? r.CanSave : (bool?)null,
            };
            Fill(short_, r.Plan, touched, r.Skipped);
            return Fitted(short_);
        }

        public static PlanActionShort Of(ModelPlanActionResult r)
        {
            if (r == null) return null;
            var short_ = new PlanActionShort
            {
                Sentence = r.Sentence, Performed = r.Performed, Action = r.Action, ItemId = r.ItemId, Revision = r.Revision,
            };
            Fill(short_, r.Plan, string.IsNullOrEmpty(r.ItemId) ? new List<string>() : new List<string> { r.ItemId }, null);
            return Fitted(short_);
        }

        /// <summary>skip_model_plan_item and unskip_model_plan_item answer with the plan; the answer here is the row.</summary>
        public static PlanActionShort OfSkip(ModelPlanView plan, string id, bool skipped)
        {
            var row = plan?.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
            var set = row != null && string.Equals(row.Status, ModelPlanStatuses.Skipped, StringComparison.Ordinal);
            var title = row?.Title == null ? id : "\"" + row.Title + "\"";
            var short_ = new PlanActionShort
            {
                Action = skipped ? "skip" : "unskip", ItemId = id, Revision = plan?.Revision ?? 0,
                Performed = row != null && set == skipped,
                Sentence = row == null ? "Nothing on the plan has that id."
                    : set == skipped ? (skipped ? "Set " + title + " aside. It stays on the plan with your reason." : "Put " + title + " back on the plan as ordinary work.")
                    : skipped && string.Equals(row.Status, ModelPlanStatuses.Done, StringComparison.Ordinal)
                        ? title + " is done, so there is nothing to set aside. Nothing was changed."
                    : (skipped ? title + " was not set aside." : title + " is still set aside."),
            };
            Fill(short_, plan, new List<string> { id }, null);
            return Fitted(short_);
        }

        /// <summary>THE CATCH-ALL the reads have (verify R5-1). The lists above are capped, but one text can still be longer
        /// than the whole budget: a reason, a title, a history line. Such an answer is cut the way a read is, every string
        /// to the same length until it fits, each cut saying how long the whole was, and 'more' says so. Works on a copy;
        /// the pipe still carries the whole result.</summary>
        private static PlanActionShort Fitted(PlanActionShort r) =>
            r == null || PlanReadShape.Chars(r) <= PlanReadShape.ReadChars ? r
                : PlanReadShape.CutLongStrings(r, "more", (cut, note) => cut.More = ((cut.More ?? "") + " " + note).Trim());

        private static void Fill(PlanActionShort r, ModelPlanView plan, List<string> touched, ApplyPlanSkip[] skipped)
        {
            r.More = MoreLine;
            if (skipped != null && skipped.Length > 0)
            {
                var kept = new List<ApplyPlanSkip>();
                var used = 0;
                foreach (var s in skipped)
                {
                    var cost = PlanReadShape.Chars(s) + 1;
                    if (kept.Count > 0 && used + cost > SkippedChars) break;
                    kept.Add(s);
                    used += cost;
                }
                r.Skipped = kept.ToArray();
                if (kept.Count < skipped.Length)
                    r.SkippedNote = UnlistedNote(skipped.Skip(kept.Count).ToList(), skipped.Length);
            }
            if (plan == null) return;
            r.ReadyDigest = plan.ReadyDigest;
            r.Counts = plan.Items.GroupBy(i => i.StateWord ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var ids = touched.Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.Ordinal).ToList();
            var rows = ids.Select(id => plan.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal))).Where(i => i != null).Select(Now).ToList();
            var shown = new List<PlanRowNow>();
            var rowsUsed = 0;
            foreach (var row in rows)
            {
                var cost = PlanReadShape.Chars(row) + 1;
                if (shown.Count > 0 && rowsUsed + cost > RowsChars) break;
                shown.Add(row);
                rowsUsed += cost;
            }
            r.Rows = shown.ToArray();
            if (shown.Count < rows.Count)
                r.RowsNote = (rows.Count - shown.Count) + " more rows were touched and are not listed here so the answer fits; get_model_plan item = rows reads the rows.";
            var history = plan.History ?? Array.Empty<ModelPlanHistoryEntry>();
            if (history.Length > 0)
                r.History = history.Select((e, n) => (e, n)).Skip(Math.Max(0, history.Length - PlanReadShape.HistoryShown))
                    .Select(x => Trim(x.e, PlanReadShape.HistoryLinesShown, x.n + 1)).ToArray();
        }

        /// <summary>What the capped list left out, counted by kind, with a pointer only where one is true (verify R5-2: the
        /// note used to send the reader to the rows' own reads, and no row's read carries what a run left out). A best-practice
        /// fix left out is still a finding on the model, so bpa_scan filtered by its rule lists every one; the rule ids are
        /// named, largest first. A row left out is still on the plan. A removal or a format left out is named only in the
        /// sentence's count, and the note says so rather than point anywhere.</summary>
        private static string UnlistedNote(List<ApplyPlanSkip> unlisted, int all)
        {
            var parts = new List<string> { unlisted.Count + " more of the " + all + " left out are not listed here so the answer fits." };
            var byRule = unlisted.Where(s => s.What != null && !string.IsNullOrEmpty(s.RuleId))
                .GroupBy(s => s.RuleId, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            if (byRule.Count > 0)
                parts.Add("Best-practice fixes among them, by rule: "
                    + string.Join(", ", byRule.Select(g => g.Count() + " " + g.Key))
                    + ". bpa_scan with ruleId = the rule lists each one, and Model quality lists them all.");
            var rows = unlisted.Count(s => s.What == null);
            if (rows > 0)
                parts.Add(rows + (rows == 1 ? " is a row" : " are rows") + " left out, still on the plan: get_model_plan reads "
                    + (rows == 1 ? "it." : "them."));
            var others = unlisted.Count(s => s.What != null && string.IsNullOrEmpty(s.RuleId));
            if (others > 0)
                parts.Add(others + (others == 1 ? " is a removal or a format" : " are removals or formats")
                    + " left as they are; the sentence counts them, and nothing was written for them.");
            return string.Join(" ", parts);
        }

        private static ModelPlanHistoryEntry Trim(ModelPlanHistoryEntry e, int lines, int number) => PlanReadShape.TrimEntry(e, lines, number);

        private static PlanRowNow Now(ModelPlanItem i) => new PlanRowNow
        {
            Id = i.Id, Title = i.Title, StateWord = i.StateWord, StateNote = i.StateNote, Doer = i.Doer, State = i.State, Touches = i.Touches,
            ChangeState = i.ChangeSet?.State, ChangeDigest = i.ChangeSet?.Digest, SkipReason = i.SkipReason,
            WaitsFor = i.WaitsFor != null && i.WaitsFor.Length > 0 ? i.WaitsFor.Take(PlanReadShape.WaitsShown).Select(w => w.Id).ToArray() : null,
            WaitsForCount = i.WaitsFor != null && i.WaitsFor.Length > PlanReadShape.WaitsShown ? i.WaitsFor.Length : (int?)null,
        };
    }
}
