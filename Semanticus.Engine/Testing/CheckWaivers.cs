using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>A Validation check the person accepted as it is (UAT P38/F55). Read side, carried on the outcome: who accepted it, when, and the
    /// optional note. The check still shows its difference; it counts as accepted, never as a pass.</summary>
    public sealed class CheckWaiver
    {
        public string By { get; set; }
        public string When { get; set; }
        public string Note { get; set; }
    }

    /// <summary>The waivers of Validation checks. A waiver is a fact about a CHECK, so it lives with the checks (<see cref="CheckWaiverStore"/>:
    /// the folder's .semanticus, or the per-user home keyed by the model when there is no folder), never in the model. Accepting a difference
    /// therefore never marks the model unsaved, never reaches production on Publish, and survives Save, a restart and a reopen of a live model.
    /// The records are <see cref="WaiverRecord"/>s of the system "test", one per check, keyed by the check's id, carrying the exact difference it accepted.</summary>
    public static class CheckWaivers
    {
        public const string System = "test";

        /// <summary>The kind of the activity event the engine publishes when a check is accepted or taken back, so the page reads the waivers again.
        /// Not a model change, so it is not a model/didChange.</summary>
        public const string ActivityKind = "check_waiver";

        public static string RefOf(string defId) => "check:" + defId;

        /// <summary>A check that can be accepted: it ran and saw a difference.</summary>
        public static bool CanWaive(ReconcileOutcome o) => o != null && !o.Missing && (o.Verdict == Verdict.Fail || o.Verdict == Verdict.Suspect)
            && !(o.DefId ?? "").StartsWith("unmapped:", StringComparison.Ordinal);

        /// <summary>The difference a check saw, written so that the same data gives the same text and any other difference gives other text: the
        /// trusted and actual figures, the gap, how many contexts differ, and the contexts that differ with their gaps.</summary>
        public static string Signature(ReconcileOutcome o)
        {
            var inv = CultureInfo.InvariantCulture;
            string N(decimal? d) => d.HasValue ? d.Value.ToString("0.############################", inv) : "-";
            var b = new StringBuilder();
            b.Append("d=").Append(N(o.Difference)).Append(";e=").Append(o.Expected ?? "-").Append(";a=").Append(o.Actual ?? "-")
                .Append(";m=").Append(o.RowsTotal > 0 ? o.Mismatches.ToString(inv) : "-");
            var worst = (o.Rows ?? Array.Empty<CompareRow>()).Where(r => r != null && r.Verdict != Verdict.Pass)
                .Select(r => (r.Context ?? "") + "=" + N(r.Delta)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (worst.Count > 0)
                b.Append(";r=").Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", worst)))).Substring(0, 16));
            return b.ToString();
        }

        /// <summary>Lay the stored waivers over a run's outcomes. A check whose difference is the one that was accepted carries the waiver; one
        /// whose difference is another carries <see cref="ReconcileOutcome.WaiverLapsed"/> and differs as it did before.</summary>
        public static void Apply(IReadOnlyList<WaiverRecord> recs, IEnumerable<ReconcileOutcome> outcomes)
        {
            if (recs == null || recs.Count == 0) return;
            foreach (var o in outcomes ?? Enumerable.Empty<ReconcileOutcome>())
            {
                if (!CanWaive(o) || string.IsNullOrEmpty(o.DefId)) continue;
                var rec = recs.FirstOrDefault(r => string.Equals(r.RuleId, o.DefId, StringComparison.OrdinalIgnoreCase));
                if (rec == null) continue;
                if (string.Equals(rec.Accepted, Signature(o), StringComparison.Ordinal))
                    o.Waiver = new CheckWaiver { By = rec.By, When = rec.When, Note = string.IsNullOrWhiteSpace(rec.Reason) ? null : rec.Reason };
                else
                    o.WaiverLapsed = true;
            }
        }
    }
    /// <summary>The Validation checks' waivers on disk: JSONL at <c>&lt;dir&gt;/tests/waivers.jsonl</c>, next to the checks' own suite.jsonl, written the
    /// way that store is (atomic move, best effort never thrown into a run). One record per check id.</summary>
    public static class CheckWaiverStore
    {
        public const string FileName = "waivers.jsonl";

        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        public static string FileFor(string dir) => string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, TestSuiteStore.SubDir, FileName);

        /// <summary>Whether the store has been written for this folder. Until it has, a waiver an earlier build left in the model is read once into it.</summary>
        public static bool Exists(string dir) { var f = FileFor(dir); return f != null && File.Exists(f); }

        public static List<WaiverRecord> Load(string dir)
        {
            var list = new List<WaiverRecord>();
            try
            {
                var f = FileFor(dir);
                if (f == null || !File.Exists(f)) return list;
                lock (Gate)
                    foreach (var l in File.ReadAllLines(f))
                    {
                        if (string.IsNullOrWhiteSpace(l)) continue;
                        try
                        {
                            var r = JsonSerializer.Deserialize<WaiverRecord>(l, JsonOpts);
                            if (r != null && !string.IsNullOrWhiteSpace(r.RuleId)) { r.System = CheckWaivers.System; list.Add(r); }
                        }
                        catch { /* one unreadable line waives nothing; the check differs as it did */ }
                    }
            }
            catch { /* an unreadable store is no waivers, never a throw */ }
            return list;
        }

        /// <summary>Replace the record of this check (or add it). False when the store could not be written.</summary>
        public static bool Upsert(string dir, WaiverRecord rec)
        {
            if (string.IsNullOrEmpty(dir) || rec == null) return false;
            try { lock (Gate) { var recs = Load(dir); recs.RemoveAll(r => Same(r, rec.RuleId)); recs.Add(rec); Write(dir, recs); } return true; }
            catch { return false; }
        }

        /// <summary>Remove the record of this check. True when there was one.</summary>
        public static bool Remove(string dir, string defId)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                lock (Gate)
                {
                    var recs = Load(dir);
                    if (recs.RemoveAll(r => Same(r, defId)) == 0) return false;
                    Write(dir, recs);
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Write the records an earlier build left in the model's annotation, once: only into a store that does not exist yet, so a
        /// waiver the person took back is not read in again.</summary>
        public static void ImportOnce(string dir, IEnumerable<WaiverRecord> legacy)
        {
            if (string.IsNullOrEmpty(dir) || Exists(dir)) return;
            var recs = (legacy ?? Enumerable.Empty<WaiverRecord>()).Where(r => r != null && string.Equals(r.System, CheckWaivers.System, StringComparison.OrdinalIgnoreCase)).ToList();
            if (recs.Count == 0) return;
            try { lock (Gate) if (!Exists(dir)) Write(dir, recs); } catch { /* read again next time */ }
        }

        private static bool Same(WaiverRecord r, string defId) => string.Equals(r.RuleId, defId?.Trim(), StringComparison.OrdinalIgnoreCase);

        private static void Write(string dir, List<WaiverRecord> recs)
        {
            var file = FileFor(dir);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, recs.Count == 0 ? "" : string.Join("\n", recs.Select(r => JsonSerializer.Serialize(r, JsonOpts))) + "\n", new UTF8Encoding(false));
            File.Move(tmp, file, overwrite: true);
        }
    }
}
