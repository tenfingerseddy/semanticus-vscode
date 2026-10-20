using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Semanticus.Engine
{
    /// <summary>
    /// Where a read-only analysis keeps its record (explain_value, blame_value, a baseline compare, an optimize that
    /// changed nothing). Such a record never goes into the model: writing it there made the model say it had unsaved
    /// changes after a person only asked about a number. It lives in SEMANTICUS_HOME, one small file per model, and
    /// History merges it back in as a "record" row. Real edits keep their hash-chained record inside the model.
    /// Best-effort by design: a failed write must never break the question that was asked.
    /// </summary>
    internal static class AnalysisRecordStore
    {
        private const string DirName = "analysis";
        private const int KeepLines = 300;           // the newest records kept per model
        private const int TrimAtLines = 450;         // rewrite only once the file is this long, so most appends are one line

        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        // A model with no durable identity (never saved, no live connection) keeps its records for the session only.
        private static readonly ConcurrentDictionary<string, List<VerifiedEditRecord>> Memory = new ConcurrentDictionary<string, List<VerifiedEditRecord>>(StringComparer.Ordinal);

        public static string FileFor(string identity)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var key = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity ?? "")), 0, 8).ToLowerInvariant();
            return Path.Combine(SemanticusHome.Root(), DirName, key + ".jsonl");
        }

        /// <summary>Stamp the time and keep the record. <paramref name="identity"/> is the model's durable identity, or null
        /// for a model that has none (then <paramref name="sessionId"/> keys the in-memory copy).</summary>
        public static void Append(string identity, string sessionId, VerifiedEditRecord rec)
        {
            rec.When = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            rec.PrevHash = ""; rec.Hash = "";
            if (identity == null)
            {
                var list = Memory.GetOrAdd(sessionId ?? "", _ => new List<VerifiedEditRecord>());
                lock (list) { rec.Seq = list.Count + 1; list.Add(rec); if (list.Count > TrimAtLines) list.RemoveRange(0, list.Count - KeepLines); }
                return;
            }
            try
            {
                var file = FileFor(identity);
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.AppendAllText(file, JsonSerializer.Serialize(rec, JsonOpts) + "\n");
                    var lines = File.ReadAllLines(file);
                    if (lines.Length > TrimAtLines) File.WriteAllLines(file, lines.Skip(lines.Length - KeepLines));
                }
            }
            catch { /* a ride-along: the analysis itself already answered */ }
        }

        public static VerifiedEditRecord[] Read(string identity, string sessionId)
        {
            var found = new List<VerifiedEditRecord>();
            if (Memory.TryGetValue(sessionId ?? "", out var mem)) lock (mem) found.AddRange(mem);
            if (identity != null)
                try
                {
                    var file = FileFor(identity);
                    lock (Gate)
                        if (File.Exists(file))
                            foreach (var line in File.ReadAllLines(file))
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                try { var r = JsonSerializer.Deserialize<VerifiedEditRecord>(line, JsonOpts); if (r != null) found.Add(r); } catch { /* skip a damaged line */ }
                            }
                }
                catch { /* unreadable: History reads without it */ }
            // Seq here is only an id within this list (the chain's Seq means something else): number after the merge.
            for (var i = 0; i < found.Count; i++) found[i].Seq = i + 1;
            return found.ToArray();
        }
    }
}
