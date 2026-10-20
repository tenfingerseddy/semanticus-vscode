// The model's question history for ask_data (Kane, 26 Sep: the "Ask your data" page keeps its questions). One file in
// the model's sidecar, beside the Plan's: what was asked, how it was read, when, and what came of it. NO ROWS: an
// answer's rows are the model's data and are never written here. The filter values a reading settled are kept,
// because they are part of the reading a person reads back ("Region = Coast"), and they stay on this computer: the
// sidecar's .gitignore and the product's own commit leave this file out (LayoutStore), the same as the edit ledger.
// A model opened live has no sidecar of its own, so its file sits in the workspace under a name made from its endpoint
// and database; an unsaved model keeps none.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Semanticus.Decisions;

namespace Semanticus.Engine.Query
{
    /// <summary>One question asked of the model, as the history keeps it.</summary>
    public sealed class DataQuestionRecord
    {
        public string Id { get; set; }
        /// <summary>When it was asked, UTC, ISO 8601.</summary>
        public string AskedAt { get; set; }
        /// <summary>The words, when the question was asked in words.</summary>
        public string Question { get; set; }
        public string Reading { get; set; }
        /// <summary>jev or you.</summary>
        public string ReadBy { get; set; }
        public string Outcome { get; set; }
        /// <summary>The settled parts, so the question can be asked again or edited without Jev.</summary>
        public DataAsk Parts { get; set; }
        public string Dax { get; set; }
        /// <summary>How many rows came back. The rows themselves are never kept.</summary>
        public int RowCount { get; set; }
        /// <summary>human or agent: which door asked.</summary>
        public string Origin { get; set; }

        // ---- the notebook (1.6, Kane 16 Oct): the same record is a row of the notebook and an item of the full history.
        /// <summary>"words" for a question read from words or parts, "dax" for a query a person or an assistant ran as written.
        /// Null on a record kept before the notebook: read as words.</summary>
        public string Kind { get; set; }
        /// <summary>A pinned row stays at the top of the notebook and is never dropped to make room.</summary>
        public bool Pinned { get; set; }
        /// <summary>True while the row is open in the notebook. Closing a row clears it; the row stays in the history.</summary>
        public bool InNotebook { get; set; }
        /// <summary>The number the row shows when it is folded, as the page wrote it ("$1.2M", "Blank"). Never a row of data.</summary>
        public string Headline { get; set; }
        /// <summary>The plain line a part that could not apply was said in (the K21 harm), so a folded row can still mark it.</summary>
        public string Dropped { get; set; }
        /// <summary>How long the query took, in ms, when it ran.</summary>
        public double? TookMs { get; set; }
        /// <summary>The first run with nothing kept, in ms, from the last Time all.</summary>
        public double? ColdMs { get; set; }
        /// <summary>The repeat runs, in ms, from the last Time all.</summary>
        public double? WarmMs { get; set; }
    }

    /// <summary>The notebook and the full history as one view: every question of the model, newest first, with the flags that
    /// say which are pinned and which are open. The notebook is the pinned and open ones; the history is all of them.</summary>
    public sealed class QueryNotebookView
    {
        public DataQuestionRecord[] Questions { get; set; } = Array.Empty<DataQuestionRecord>();
        /// <summary>A plain line when there is nothing to keep (an unsaved model), or when an action found no such row.</summary>
        public string Note { get; set; }
        /// <summary>What an action did, in one plain line ("Pinned.", "Removed.").</summary>
        public string Done { get; set; }
    }

    public sealed class DataQuestionHistory
    {
        /// <summary>Newest first.</summary>
        public DataQuestionRecord[] Questions { get; set; } = Array.Empty<DataQuestionRecord>();
        /// <summary>A plain line when there is no history to keep, for example an unsaved model.</summary>
        public string Note { get; set; }
    }

    internal static class DataQuestionStore
    {
        public const string FileName = "data-questions.json";
        /// <summary>The folder, in the workspace's sidecar, that holds one file per model opened live.</summary>
        public const string LiveFolder = "data-questions";
        /// <summary>The longest question kept, in characters. A longer one is kept cut, with "..." at the end.</summary>
        public const int KeepChars = 2000;
        /// <summary>The most questions kept. The oldest go first.</summary>
        public const int Keep = 200;

        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private sealed class FileShape
        {
            public int Version { get; set; } = 1;
            public List<DataQuestionRecord> Questions { get; set; } = new List<DataQuestionRecord>();
        }

        public static string FileIn(string sidecarDir) => sidecarDir == null ? null : Path.Combine(sidecarDir, FileName);

        /// <summary>The file for a model opened live: one per endpoint and database, so two live models never share.</summary>
        public static string FileForLiveModel(string workspaceSidecar, string endpoint, string database)
        {
            var key = DecisionDigest.Sha256Hex((endpoint ?? "").Trim().ToLowerInvariant() + "\n" + (database ?? "").Trim().ToLowerInvariant());
            return Path.Combine(workspaceSidecar, LiveFolder, key.Substring(0, 24) + ".json");
        }

        /// <summary>Carry the rows an older build kept in a workspace's sidecar into the model's own file in the per-user home, once. Rows
        /// already there win; the old file is renamed so a row the person later removes does not come back. Never throws: a history is a
        /// convenience.</summary>
        public static void CarryOver(string legacy, string home)
        {
            if (legacy == null || home == null || string.Equals(legacy, home, StringComparison.Ordinal)) return;
            try
            {
                lock (Gate)
                {
                    if (!File.Exists(legacy)) return;
                    var old = ReadShape(legacy);
                    var shape = ReadShape(home);
                    var have = new HashSet<string>(shape.Questions.Select(q => q.Id), StringComparer.Ordinal);
                    var carried = old.Questions.Where(q => !string.IsNullOrEmpty(q.Id) && have.Add(q.Id)).ToList();
                    if (carried.Count > 0)
                    {
                        shape.Questions.InsertRange(0, carried);
                        Trim(shape);
                        Write(home, shape);
                    }
                    File.Move(legacy, legacy + ".moved", overwrite: true);
                }
            }
            catch { /* left where it is; the next ask tries again */ }
        }

        public static string Clip(string question) =>
            question == null || question.Length <= KeepChars ? question : question.Substring(0, KeepChars).TrimEnd() + "...";

        /// <summary>What makes two records the same question: its words, else its reading, else its DAX with the spaces squashed.
        /// A query run as written is its DAX. Case does not matter. The words come first (UAT 17 Oct, round 3): two questions
        /// that read the same (one answered, one flagged, both Order Count) are two rows, so a newer twin never hides an older
        /// answered row; and the same words asked again after Allow are one row, not two.</summary>
        public static string KeyOf(DataQuestionRecord r)
        {
            var text = r.Kind == "dax" ? r.Dax : (!string.IsNullOrWhiteSpace(r.Question) ? r.Question : !string.IsNullOrWhiteSpace(r.Reading) ? r.Reading : r.Dax);
            return string.IsNullOrWhiteSpace(text) ? r.Id : string.Join(" ", text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        }

        /// <summary>DAX with its whitespace squashed, so a query run again with other spacing is the same query.</summary>
        public static string SquashDax(string dax) => string.Join(" ", (dax ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

        public static void Append(string file, DataQuestionRecord record)
        {
            if (file == null || record == null) return;
            lock (Gate)
            {
                var shape = ReadShape(file);
                // A question asked again takes the place of its earlier row in the notebook, with its pin: one row per question, and
                // the history keeps every ask.
                var key = KeyOf(record);
                foreach (var earlier in shape.Questions.Where(q => (q.Pinned || q.InNotebook) && KeyOf(q) == key))
                {
                    record.Pinned |= earlier.Pinned; record.InNotebook |= earlier.InNotebook;
                    earlier.Pinned = false; earlier.InNotebook = false;
                }
                shape.Questions.Add(record);
                Trim(shape);
                Write(file, shape);
            }
        }

        /// <summary>The oldest go first, and a pinned row is never one of them.</summary>
        private static void Trim(FileShape shape)
        {
            while (shape.Questions.Count > Keep)
            {
                var i = shape.Questions.FindIndex(q => !q.Pinned);
                if (i < 0) break;
                shape.Questions.RemoveAt(i);
            }
        }

        private static void Write(string file, FileShape shape)
        {
            var dir = Path.GetDirectoryName(file);
            Directory.CreateDirectory(dir);
            // The sidecar's .gitignore keeps this file out of the model's commits; make sure it says so before the
            // first question is written. The live-model folder sits inside a sidecar too.
            LayoutStore.EnsureRuntimeIgnore(Path.GetFileName(dir) == LiveFolder ? Path.GetDirectoryName(dir) : dir);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(shape, Json));
            File.Move(tmp, file, overwrite: true);
        }

        /// <summary>Change one record in place. False when the file holds no record with that id.</summary>
        public static bool Update(string file, string id, Action<DataQuestionRecord> change)
        {
            if (file == null || string.IsNullOrEmpty(id)) return false;
            lock (Gate)
            {
                var shape = ReadShape(file);
                var r = shape.Questions.FirstOrDefault(q => q.Id == id);
                if (r == null) return false;
                change(r);
                Write(file, shape);
                return true;
            }
        }

        /// <summary>Take one record out of the history for good. False when there is no such record.</summary>
        public static bool Remove(string file, string id)
        {
            if (file == null || string.IsNullOrEmpty(id)) return false;
            lock (Gate)
            {
                var shape = ReadShape(file);
                if (shape.Questions.RemoveAll(q => q.Id == id) == 0) return false;
                Write(file, shape);
                return true;
            }
        }

        public static DataQuestionRecord[] Read(string file, int limit)
        {
            if (file == null) return Array.Empty<DataQuestionRecord>();
            lock (Gate)
            {
                var all = ReadShape(file).Questions;
                all.Reverse();
                return all.Take(limit <= 0 ? Keep : limit).ToArray();
            }
        }

        /// <summary>Absent or unreadable reads as empty: a history is a convenience, and a damaged one must not stop a
        /// question being asked. The next write replaces it.</summary>
        private static FileShape ReadShape(string file)
        {
            try
            {
                if (!File.Exists(file)) return new FileShape();
                return JsonSerializer.Deserialize<FileShape>(File.ReadAllText(file), Json) ?? new FileShape();
            }
            catch { return new FileShape(); }
        }
    }
}
