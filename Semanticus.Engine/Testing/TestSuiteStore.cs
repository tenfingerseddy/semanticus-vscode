using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Semanticus.Engine
{
    /// <summary>The stable definition kinds (string, not enum, so a future kind round-trips through an older
    /// engine's store untouched instead of failing deserialization).</summary>
    public static class TestKinds
    {
        public const string MeasureReconcile = "measureReconcile";   // params = a ReconcileRequest (minus measureRef)
        public const string RowLevelAssertion = "rowLevelAssertion"; // E8 (impersonation) — stored now, evaluated then
    }

    /// <summary>A persisted test definition. Tagged objects bind by LineageTag; valid tagless objects bind through
    /// the tests sidecar identity. <see cref="TargetRef"/> remains display text, never the preferred identity.</summary>
    public sealed class TestDefinition
    {
        public string Id { get; set; }               // store-assigned, stable
        public string ModelIdentity { get; set; }    // durable pane identity; shared sidecars are filtered by this
        public string Kind { get; set; }             // TestKinds.*
        public string Title { get; set; }
        public string TargetTag { get; set; }        // LineageTag of the bound object (null = model-level)
        public string TargetIdentity { get; set; }   // sid:* entry in tests/identities.json for a tagless object
        public string TargetRef { get; set; }        // human ref for display; TargetTag/TargetIdentity is the identity
        public string BindingWarning { get; set; }   // present only when a legacy/name fallback is unavoidable
        public string ParamsJson { get; set; }       // kind-specific payload (e.g. the ReconcileRequest)
        public string CreatedBy { get; set; }        // "human" | "agent" — the accepted ground truth's provenance
        public string CreatedWhen { get; set; }      // ISO-8601
        public bool Enabled { get; set; } = true;

        /// <summary>Optional per-test timing budget in ms. Setting one OPTS the measure into the clear-cache
        /// single-run timing pass; absent = never timed-judged, so the Performance category cannot activate by
        /// surprise (a perf threshold must be declared, not defaulted — hard thing #5).</summary>
        public long? BudgetMs { get; set; }
    }

    /// <summary>
    /// E2 — persistence for the Tests tab, cloning the VitalsStore discipline (Connect/VitalSigns.cs): plain JSONL
    /// under `.semanticus/tests/`, best-effort (a failed write never fails the op that rides it), unreadable lines
    /// counted and disclosed, never thrown. TWO stores with different lifecycles:
    ///   • suite.jsonl — the DEFINITIONS, a mutable SET (upsert/remove by id ⇒ full rewrite). Committed with the
    ///     repo so the suite travels (project-describing state lives BESIDE the model — the carrier principle).
    ///   • runs.jsonl  — the RUN HISTORY, append-only with retention (last N / M bytes, oldest pruned) — the
    ///     drift-trend substrate (Pro).
    /// Persistence is the Pro side of the ratified line; a Free run evaluates + shows everything and simply never
    /// calls <see cref="AppendRun"/>.
    /// </summary>
    public static class TestSuiteStore
    {
        public const int SchemaVersion = 1;
        public const string SubDir = "tests";
        public const string SuiteFile = "suite.jsonl";
        public const string RunsFile = "runs.jsonl";
        public const int MaxRuns = 200;
        public const long MaxBytes = 20L * 1024 * 1024;

        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        // ---- the suite (definitions) ----------------------------------------------------------------

        public static (List<TestDefinition> Defs, int Unreadable) LoadSuite(string dir)
        {
            lock (Gate) return LoadSuiteUnlocked(dir);
        }

        /// <summary>Upsert one definition by Id (a null/empty Id gets a fresh one) and rewrite the suite.
        /// Returns the stored definition (with its assigned Id) or null on failure — never throws.</summary>
        public static TestDefinition Upsert(string dir, TestDefinition def, Func<string> newId)
        {
            if (string.IsNullOrEmpty(dir) || def == null) return null;
            try
            {
                lock (Gate)
                {
                    var (defs, _) = LoadSuiteUnlocked(dir);
                    if (string.IsNullOrEmpty(def.Id)) def.Id = newId();
                    var i = defs.FindIndex(d => string.Equals(d.Id, def.Id, StringComparison.Ordinal));
                    if (i >= 0) defs[i] = def; else defs.Add(def);
                    WriteSuiteUnlocked(dir, defs);
                }
                return def;
            }
            catch { return null; }
        }

        public static bool Remove(string dir, string id)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(id)) return false;
            try
            {
                lock (Gate)
                {
                    var (defs, _) = LoadSuiteUnlocked(dir);
                    if (defs.RemoveAll(d => string.Equals(d.Id, id, StringComparison.Ordinal)) == 0) return false;
                    WriteSuiteUnlocked(dir, defs);
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- run history (append-only, retention) ---------------------------------------------------

        /// <summary>Append one run's JSON line, pruning oldest first when the record/byte cap would be exceeded.
        /// Takes the line pre-serialized so the store stays ignorant of the run DTO (the analyzer owns it).</summary>
        public static bool AppendRun(string dir, string runJsonLine)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || string.IsNullOrWhiteSpace(runJsonLine)) return false;
                var file = Path.Combine(dir, SubDir, RunsFile);
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    var lines = File.Exists(file)
                        ? File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).ToList()
                        : new List<string>();
                    long Size(IEnumerable<string> ls) => ls.Sum(l => (long)Encoding.UTF8.GetByteCount(l) + 1);
                    while (lines.Count > 0 && (lines.Count + 1 > MaxRuns || Size(lines) + Encoding.UTF8.GetByteCount(runJsonLine) + 1 > MaxBytes))
                        lines.RemoveAt(0);
                    lines.Add(runJsonLine);
                    WriteAtomic(file, string.Join("\n", lines) + "\n");
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Read run-history lines (chronological). The caller deserializes and fingerprint-filters —
        /// the store stays DTO-ignorant. Unreadable is always 0 here (the line IS the unit); kept for symmetry.</summary>
        public static List<string> ReadRunLines(string dir)
        {
            try
            {
                var file = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, SubDir, RunsFile);
                if (file == null || !File.Exists(file)) return new List<string>();
                lock (Gate) return File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            }
            catch { return new List<string>(); }
        }

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOpts);
        public static T Deserialize<T>(string json) where T : class
        {
            try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
            catch { return null; }
        }

        // ---- unlocked internals (callers hold Gate) --------------------------------------------------

        private static (List<TestDefinition> Defs, int Unreadable) LoadSuiteUnlocked(string dir)
        {
            var defs = new List<TestDefinition>();
            var bad = 0;
            try
            {
                var file = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, SubDir, SuiteFile);
                if (file == null || !File.Exists(file)) return (defs, 0);
                foreach (var l in File.ReadAllLines(file))
                {
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    var d = Deserialize<TestDefinition>(l);
                    if (d != null && !string.IsNullOrEmpty(d.Id)) defs.Add(d); else bad++;
                }
            }
            catch { /* unreadable store ⇒ empty, never throw */ }
            return (defs, bad);
        }

        private static void WriteSuiteUnlocked(string dir, List<TestDefinition> defs)
        {
            var file = Path.Combine(dir, SubDir, SuiteFile);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var body = string.Join("\n", defs.Select(d => JsonSerializer.Serialize(d, JsonOpts)));
            WriteAtomic(file, defs.Count == 0 ? "" : body + "\n");
        }

        // Temp-then-move (sol review): File.WriteAllText truncates in place, so a crash mid-write tears the
        // store; suite.jsonl holds HUMAN-ACCEPTED ground truth, a higher-stakes payload than the vitals this
        // store clones. The move is atomic on the same volume, so readers see the old file or the new one,
        // never a torn half. (The Gate already serializes writers within this process.)
        private static void WriteAtomic(string file, string content)
        {
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            File.Move(tmp, file, overwrite: true);
        }
    }
}
