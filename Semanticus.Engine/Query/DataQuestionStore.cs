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

        public static string Clip(string question) =>
            question == null || question.Length <= KeepChars ? question : question.Substring(0, KeepChars).TrimEnd() + "...";

        public static void Append(string file, DataQuestionRecord record)
        {
            if (file == null || record == null) return;
            lock (Gate)
            {
                var shape = ReadShape(file);
                shape.Questions.Add(record);
                if (shape.Questions.Count > Keep) shape.Questions.RemoveRange(0, shape.Questions.Count - Keep);
                var dir = Path.GetDirectoryName(file);
                Directory.CreateDirectory(dir);
                // The sidecar's .gitignore keeps this file out of the model's commits; make sure it says so before the
                // first question is written. The live-model folder sits inside a sidecar too.
                LayoutStore.EnsureRuntimeIgnore(Path.GetFileName(dir) == LiveFolder ? Path.GetDirectoryName(dir) : dir);
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(shape, Json));
                File.Move(tmp, file, overwrite: true);
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
