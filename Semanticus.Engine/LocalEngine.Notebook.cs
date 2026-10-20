// The Query notebook (1.6, Kane 16 Oct: "close certain queries and retain others, pin certain queries, a full history, remove some,
// pin others"). The notebook is the model's question history with two flags on each row: pinned, and open in the notebook. Closing a
// row takes it out of the notebook and keeps it in the history; removing it deletes it; reopening brings a history row back. One
// file, one door each for the Studio (queryNotebook) and the assistant (query_notebook), so both see the same rows. Not Pro: a
// query run as written is a row too, and nothing here asks Jev or sends anything out.

using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine.Query;

namespace Semanticus.Engine
{
    /// <summary>What the page notes on a row once it knows it: the number as shown, the K21 line, and the times.</summary>
    public sealed class QueryRowNote
    {
        public string Headline { get; set; }
        public string Dropped { get; set; }
        public double? TookMs { get; set; }
        public double? ColdMs { get; set; }
        public double? WarmMs { get; set; }
    }

    public sealed partial class LocalEngine
    {
        public const string NotebookList = "list", NotebookPin = "pin", NotebookUnpin = "unpin", NotebookClose = "close", NotebookReopen = "reopen", NotebookRemove = "remove";

        /// <summary>query_notebook: list the notebook and the full history, or pin, unpin, close, reopen or remove one row.</summary>
        public Task<QueryNotebookView> QueryNotebookAsync(string action = "list", string id = null)
        {
            _sessions.Require();
            var file = DataQuestionFile(_sessions.CurrentContext);
            if (file == null)
                return Task.FromResult(new QueryNotebookView { Note = "Save this model to keep its queries. Nothing is kept for a model with no place on disk." });
            action = (action ?? NotebookList).Trim().ToLowerInvariant();
            string done = null, note = null;
            if (action != NotebookList)
            {
                if (string.IsNullOrWhiteSpace(id)) note = "Say which query: pass its id from the list.";
                else
                {
                    bool found;
                    switch (action)
                    {
                        case NotebookPin: found = DataQuestionStore.Update(file, id, r => { r.Pinned = true; r.InNotebook = true; }); done = "Pinned."; break;
                        case NotebookUnpin: found = DataQuestionStore.Update(file, id, r => r.Pinned = false); done = "Unpinned."; break;
                        // Closing a pinned row unpins it too: a pin means "stay in the notebook", and a closed row has left it.
                        case NotebookClose: found = DataQuestionStore.Update(file, id, r => { r.Pinned = false; r.InNotebook = false; }); done = "Closed. It stays in the history."; break;
                        case NotebookReopen: found = ReopenRow(file, id); done = "Back in the notebook."; break;
                        case NotebookRemove: found = DataQuestionStore.Remove(file, id); done = "Removed."; break;
                        default: found = false; done = null; note = "The actions are list, pin, unpin, close, reopen and remove."; break;
                    }
                    if (action is NotebookPin or NotebookUnpin or NotebookClose or NotebookReopen or NotebookRemove && !found) { done = null; note = "No query has that id. Read the list for the ids."; }
                }
            }
            return Task.FromResult(new QueryNotebookView { Questions = DataQuestionStore.Read(file, 0), Done = done, Note = note });
        }

        /// <summary>A history row comes back into the notebook. If the notebook already holds the same question, that row leaves it
        /// (one row per question), and the pin goes with the reopened row.</summary>
        private static bool ReopenRow(string file, string id)
        {
            var all = DataQuestionStore.Read(file, 0);
            var row = all.FirstOrDefault(r => r.Id == id);
            if (row == null) return false;
            var key = DataQuestionStore.KeyOf(row);
            var pinned = row.Pinned || all.Any(r => r.Id != id && r.Pinned && DataQuestionStore.KeyOf(r) == key);
            foreach (var twin in all.Where(r => r.Id != id && (r.Pinned || r.InNotebook) && DataQuestionStore.KeyOf(r) == key))
                DataQuestionStore.Update(file, twin.Id, r => { r.Pinned = false; r.InNotebook = false; });
            return DataQuestionStore.Update(file, id, r => { r.InNotebook = true; r.Pinned = pinned; });
        }

        /// <summary>A query run as written (by the person in the page, or by the assistant's run_dax) is a row of the notebook too.
        /// Returns its id, or null when the model keeps no history. Never fails the run.</summary>
        public Task<string> RecordQueryRunAsync(string dax, int rowCount, double elapsedMs, string headline = null, string origin = "human", string replaces = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dax)) return Task.FromResult<string>(null);
                _sessions.Require();
                var file = DataQuestionFile(_sessions.CurrentContext);
                if (file == null) return Task.FromResult<string>(null);
                var text = DataQuestionStore.Clip(dax.Trim());
                // The same query run again is the same row, brought up to date. A query a person is still shaping (fields changed, DAX
                // edited) replaces the row it came from rather than adding one row for every change.
                var probe = new DataQuestionRecord { Kind = "dax", Dax = text };
                var rowsNow = DataQuestionStore.Read(file, 0);
                // The DAX of a question run again unchanged (Run in the row's DAX view) is that question's own run, not a new query.
                // Hero 25 H1: the assistant's run is never the person's row's own run. It gets a row of its own (the same query run again by the
                // assistant brings that assistant row up to date), so it can never be drawn into, or written over, a row the person owns.
                if (origin == "agent")
                {
                    var theirs = rowsNow.FirstOrDefault(r => r.Origin == "agent" && r.Kind == "dax" && DataQuestionStore.KeyOf(r) == DataQuestionStore.KeyOf(probe));
                    if (theirs != null && DataQuestionStore.Update(file, theirs.Id, r =>
                    {
                        r.AskedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); r.RowCount = rowCount; r.TookMs = elapsedMs;
                        r.Headline = headline; r.ColdMs = null; r.WarmMs = null; r.InNotebook = true;
                    })) return Task.FromResult(theirs.Id);
                    return Task.FromResult(AppendRunRow(file, text, rowCount, elapsedMs, headline, "agent"));
                }
                var own = rowsNow.FirstOrDefault(r => (r.InNotebook || r.Pinned) && r.Kind != "dax" && !string.IsNullOrWhiteSpace(r.Dax) && DataQuestionStore.SquashDax(r.Dax) == DataQuestionStore.SquashDax(text));
                if (own != null && DataQuestionStore.Update(file, own.Id, r => { r.AskedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); r.RowCount = rowCount; r.TookMs = elapsedMs; r.Headline = headline; r.ColdMs = null; r.WarmMs = null; })) return Task.FromResult(own.Id);
                var same = rowsNow.FirstOrDefault(r => r.Kind == "dax" && DataQuestionStore.KeyOf(r) == DataQuestionStore.KeyOf(probe));
                // A pinned row is kept work: a query shaped from it is a new row and the pinned one stays as it was (UAT 17 Oct, F37).
                var target = same?.Id ?? (string.IsNullOrWhiteSpace(replaces) ? null : rowsNow.FirstOrDefault(r => r.Id == replaces && r.Kind == "dax" && !r.Pinned)?.Id);
                if (target != null && DataQuestionStore.Update(file, target, r =>
                {
                    r.Dax = text; r.AskedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); r.RowCount = rowCount; r.TookMs = elapsedMs;
                    r.Headline = headline; r.Dropped = null; r.ColdMs = null; r.WarmMs = null; r.InNotebook = true;
                })) return Task.FromResult(target);
                return Task.FromResult(AppendRunRow(file, text, rowCount, elapsedMs, headline, origin));
            }
            catch { return Task.FromResult<string>(null); }
        }

        private static string AppendRunRow(string file, string text, int rowCount, double elapsedMs, string headline, string origin)
        {
            var id = "dq-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            DataQuestionStore.Append(file, new DataQuestionRecord
            {
                Id = id,
                Kind = "dax",
                AskedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ReadBy = "you",
                Outcome = DataAnswer.Answered,
                Dax = text,
                RowCount = rowCount,
                TookMs = elapsedMs,
                Headline = headline,
                InNotebook = true,
                Origin = origin == "agent" ? "agent" : "human",
            });
            return id;
        }

        /// <summary>The page notes what it knows of a row: the number as shown, the K21 line, and the times from Time all.</summary>
        public Task<bool> NoteQueryRowAsync(string id, QueryRowNote note)
        {
            try
            {
                if (note == null || string.IsNullOrWhiteSpace(id)) return Task.FromResult(false);
                _sessions.Require();
                var file = DataQuestionFile(_sessions.CurrentContext);
                return Task.FromResult(DataQuestionStore.Update(file, id, r =>
                {
                    if (note.Headline != null) r.Headline = KeepFigure(note.Headline, 80);
                    if (note.Dropped != null) r.Dropped = note.Dropped.Length > 300 ? note.Dropped.Substring(0, 300) : note.Dropped;
                    if (note.TookMs != null) r.TookMs = note.TookMs;
                    if (note.ColdMs != null) r.ColdMs = note.ColdMs;
                    if (note.WarmMs != null) r.WarmMs = note.WarmMs;
                }));
            }
            catch { return Task.FromResult(false); }
        }

        /// <summary>Shortens a folded figure ("label: number") to <paramref name="max"/> characters by cutting the label, never the number,
        /// so a long column name can never turn $39,137,964.25 into "$39,137,".</summary>
        internal static string KeepFigure(string figure, int max)
        {
            if (figure.Length <= max) return figure;
            var i = figure.LastIndexOf(": ", StringComparison.Ordinal);
            if (i < 0) return figure.Substring(0, max);
            var value = figure.Substring(i + 2);
            var room = max - value.Length - 3;
            if (room < 1) return value;
            return figure.Substring(0, Math.Min(i, room)).TrimEnd() + "…: " + value;
        }
    }
}
