using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    // D3 (launch video, 12 Oct): get_plan_history(limit 15) after a build returned 617,866 characters, because every row carried its
    // before and after values and a build row nests hundreds of edits. The assistant's door now answers with one summary line per row,
    // says how much it left out, and gives one entry's detail on request. The engine's own answer (Studio, RPC) is unchanged.
    public sealed class PlanHistoryLeanRow
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public DateTimeOffset When { get; set; }
        public string By { get; set; }
        public string Title { get; set; }
        public long Revision { get; set; }
        public bool Undoable { get; set; }
        public int LaterCount { get; set; }
        public bool Redoable { get; set; }
        public bool Undone { get; set; }
        public string Verified { get; set; }
        public bool BeforeThisSession { get; set; }
        /// <summary>How many objects this row changed. Ask for the row's id with entry to read them.</summary>
        public int Changes { get; set; }
        /// <summary>How many edits nest under this row.</summary>
        public int Steps { get; set; }
        public string Detail { get; set; }
    }

    public sealed class PlanHistoryLeanView
    {
        public string SessionId { get; set; }
        public bool Kept { get; set; }
        public PlanHistoryLeanRow[] Rows { get; set; } = Array.Empty<PlanHistoryLeanRow>();
        public bool More { get; set; }
        /// <summary>Pass this as before to read the next older rows. Null when there are none.</summary>
        public string NextBefore { get; set; }
        /// <summary>What this answer left out, in words. Null when it left out nothing.</summary>
        public string LeftOut { get; set; }
    }

    public sealed class PlanHistoryEntryView
    {
        public PlanHistoryRow Entry { get; set; }
        /// <summary>The edits nested under the entry, as summary rows. Read one with its own id.</summary>
        public PlanHistoryLeanRow[] Steps { get; set; } = Array.Empty<PlanHistoryLeanRow>();
        public string LeftOut { get; set; }
    }

    public static partial class McpTools
    {
        internal const int HistoryRowCap = 12000;     // characters for the list
        internal const int HistoryDeltaCap = 40;      // changes shown for one entry
        internal const int HistoryStepCap = 40;       // nested edits shown for one entry
        internal const int HistoryDetailCap = 15000;  // characters for one entry

        [McpServerTool(Name = "get_plan_history"), Description("The model's History, newest first, one summary line per row: id, kind, When, By (you, assistant, semanticus, workflow), title, whether it is Undoable (pass its id to undo_change toEntry), LaterCount, Redoable, Undone, Verified word, how many objects it changed (Changes) and how many edits nest under it (Steps). A build or a workflow run is one row with its edits nested. The answer says what it left out. To read the before and after values of one row, pass its id as entry (a nested edit has its own id). Filters: by, objectRef (one object), before (a row's When, or NextBefore, to page back), limit (default 20, at most 100). Kept=false means the model has nowhere to keep history yet (save it) and it lasts this session. detail=true returns every row in full, which can be very large. Free. Read-only.")]
        public static async Task<object> GetPlanHistoryOp(IEngine engine,
            [Description("Optional: the id of one row, to read its changes (before and after) and its nested edits.")] string entry = null,
            [Description("Optional: only rows by 'you', 'assistant', 'semanticus' or 'workflow'.")] string by = null,
            [Description("Optional: only rows that touched this object, e.g. 'measure:Sales/Total'.")] string objectRef = null,
            [Description("Optional: only rows older than this When (ISO time, or NextBefore from an earlier answer).")] string before = null,
            [Description("Optional: how many rows, default 20, at most 100.")] int limit = 20,
            [Description("true returns every row in full, with all changes. Can be very large.")] bool detail = false)
        {
            if (detail) return await GetPlanHistory(engine, by, objectRef, before, limit <= 0 ? 50 : Math.Min(limit, 500)).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(entry)) return await HistoryEntryAsync(engine, entry.Trim()).ConfigureAwait(false);
            var take = limit <= 0 ? 20 : Math.Min(limit, 100);
            var full = await GetPlanHistory(engine, by, objectRef, before, take).ConfigureAwait(false);
            var rows = full.Rows.Select(LeanRow).ToList();
            var asked = rows.Count;
            while (rows.Count > 1 && McpLeanResults.Chars(rows) > HistoryRowCap) rows.RemoveAt(rows.Count - 1);
            var shaved = asked - rows.Count;
            var more = full.More || shaved > 0;
            var changes = full.Rows.Sum(r => r.Deltas?.Length ?? 0);
            var left = $"Each row's {changes:N0} before and after values are left out; pass a row's id as entry to read them."
                + (shaved > 0 ? $" {shaved} more row{(shaved == 1 ? "" : "s")} fit no room here." : "")
                + (full.More ? " Older rows exist." : "");
            return new PlanHistoryLeanView
            {
                SessionId = full.SessionId, Kept = full.Kept, Rows = rows.ToArray(), More = more,
                NextBefore = more && rows.Count > 0 ? rows[^1].When.ToString("O") : null,
                LeftOut = changes > 0 || more ? left : null,
            };
        }

        private static PlanHistoryLeanRow LeanRow(PlanHistoryRow r) => new PlanHistoryLeanRow
        {
            Id = r.Id, Kind = r.Kind, When = r.When, By = r.By, Title = r.Title, Revision = r.Revision,
            Undoable = r.Undoable, LaterCount = r.LaterCount, Redoable = r.Redoable, Undone = r.Undone, Verified = r.Verified,
            BeforeThisSession = r.BeforeThisSession, Changes = r.Deltas?.Length ?? 0, Steps = r.Children?.Length ?? 0,
            Detail = r.Detail == null ? null : r.Detail.Length <= 200 ? r.Detail : r.Detail.Substring(0, 200) + " (cut)",
        };

        private static async Task<object> HistoryEntryAsync(IEngine engine, string id)
        {
            var full = await GetPlanHistory(engine, null, null, null, 500).ConfigureAwait(false);
            PlanHistoryRow found = null;
            foreach (var r in full.Rows)
            {
                if (string.Equals(r.Id, id, StringComparison.Ordinal)) { found = r; break; }
                found = r.Children?.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
                if (found != null) break;
            }
            if (found == null)
                throw new ArgumentException($"No History row has the id {id} in the newest 500 rows. Read the list with get_plan_history, or page back with before.");
            var deltas = found.Deltas ?? Array.Empty<ChangeDelta>();
            var steps = found.Children ?? Array.Empty<PlanHistoryRow>();
            var shown = new PlanHistoryRow
            {
                Id = found.Id, Kind = found.Kind, When = found.When, By = found.By, Title = found.Title, Cause = found.Cause,
                Deltas = deltas.Take(HistoryDeltaCap).ToArray(), SessionId = found.SessionId, Revision = found.Revision,
                Undoable = found.Undoable, LaterCount = found.LaterCount, Redoable = found.Redoable, Undone = found.Undone,
                Verified = found.Verified, BeforeThisSession = found.BeforeThisSession, NotSaved = found.NotSaved,
                BeforeRestore = found.BeforeRestore, Detail = found.Detail, Record = found.Record,
                Report = found.Report != null && McpLeanResults.Chars(found.Report) <= 6000 ? found.Report : null,
            };
            var view = new PlanHistoryEntryView { Entry = shown, Steps = steps.Take(HistoryStepCap).Select(LeanRow).ToArray() };
            while (shown.Deltas.Length > 1 && McpLeanResults.Chars(view) > HistoryDetailCap)
                shown.Deltas = shown.Deltas.Take(shown.Deltas.Length / 2).ToArray();
            var parts = new System.Collections.Generic.List<string>();
            if (shown.Deltas.Length < deltas.Length) parts.Add($"{deltas.Length - shown.Deltas.Length} of {deltas.Length} changes are left out; pass objectRef to find one object's rows.");
            if (view.Steps.Length < steps.Length) parts.Add($"{steps.Length - view.Steps.Length} of {steps.Length} nested edits are left out.");
            if (found.Report != null && shown.Report == null) parts.Add("The run's report is left out because it is long.");
            view.LeftOut = parts.Count == 0 ? null : string.Join(" ", parts);
            return view;
        }
    }
}
