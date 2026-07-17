using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Semanticus.Engine
{
    // The Model Interview (docs/product-innovation-brainstorm.md §1) — the deterministic KERNEL: the append-only
    // JSONL question store (KnowledgeStore's delta pattern), the tiered outcome scoring, and the failure→fix map.
    // No inference lives here (golden rule 1): the user's Claude authors the NL questions + DAX attempts via the
    // /interview-model skill; the engine only executes, compares, scores, and stores. The ops (entitlement gate,
    // scope resolution, live execution, activity broadcast) live in LocalEngine.Interview.cs.

    // ---- serialized shapes (the user's own data — plain JSONL, readable without us) --------------------

    /// <summary>One materialized interview question. GAP C (the DAX shape mismatch) is why the record carries BOTH
    /// <see cref="Query"/> (a full EVALUATE for the value tier) and <see cref="ScalarExpr"/>/<see cref="ParaphraseExpr"/>
    /// (scalar expressions for the equivalence tier) — they are not interchangeable.</summary>
    public sealed class InterviewQuestion
    {
        public string Id { get; set; }                                   // iq-<8hex>
        public string Question { get; set; }                             // the natural-language question, verbatim
        public string Tier { get; set; }                                 // "value" | "paraphrase" | "refusal"
        public string Query { get; set; }                                // value tier: the full EVALUATE query under test
        public string ScalarExpr { get; set; }                           // paraphrase tier: the first phrasing's scalar DAX
        public string ParaphraseExpr { get; set; }                       // paraphrase tier: the second phrasing's scalar DAX
        public string[] GroupBy { get; set; } = Array.Empty<string>();   // paraphrase tier: the equivalence matrix columns
        public string[] Filters { get; set; } = Array.Empty<string>();   // paraphrase tier: optional whole-comparison filters
        public string ExpectedValue { get; set; }                        // value tier: the trusted scalar answer
        public string[][] ExpectedMatrix { get; set; }                   // value tier: the trusted row set (order-insensitive)
        public bool ExpectRefusal { get; set; }                          // refusal tier: this question CANNOT be answered from the model
        public string FixRuleId { get; set; }                            // author-supplied readiness rule (primary failure→fix mapping)
        public string SeedSource { get; set; }                           // "user" | "claude" | "verified-answer" | ... (pre-seed hook)
        // #157: two-part binding so a pack is only ever OFFERED for, and GRADED against, the right model.
        //  • ModelIdentity — the PANE identity: which editable model's interview this belongs to (live endpoint|
        //    database, else the FULL on-disk model path, else a session-scoped id for an unsaved model). Governs the
        //    list buckets. Deliberately NOT the shape fingerprint (which changes on the very edits interview_replay
        //    guards). null = a legacy question saved before binding.
        //  • ExecIdentity — the EXECUTION identity: the LIVE connection (endpoint|database) whose data the oracle was
        //    confirmed against at authoring, or null if authored with no live connection. Grading runs against the
        //    ATTACHED live connection (_live), which is INDEPENDENT of the editable session — so this, not
        //    ModelIdentity, is what must match at run time or the trusted answer would be scored against another
        //    model's data. ExecTarget is its human label for the refusal message.
        //  • ModelWitness — a weak witness (the model NAME at authoring) so a model REPLACED IN PLACE at the same
        //    endpoint|database (which keeps ExecIdentity identical) is caught and refused rather than silently graded.
        // ModelLabel/ExecTarget/ModelWitness are display/witness only — never the match key.
        public string ModelIdentity { get; set; }
        public string ModelLabel { get; set; }
        public string ExecIdentity { get; set; }
        public string ExecTarget { get; set; }
        public string ModelWitness { get; set; }
        public string Scope { get; set; }                                // "project" | "global" (set by the reader, not stored)
        public string When { get; set; }                                 // ISO-8601 UTC of creation
        public string Origin { get; set; }                               // "agent" | "human"
        public int SchemaVersion { get; set; } = InterviewStore.SchemaVersion;
        public InterviewRunRecord LastRun { get; set; }                  // latest recorded outcome (from record-run deltas)
    }

    /// <summary>The persisted outcome of one graded run — what makes the Studio card useful across sessions
    /// (the deterministic artifact stands alone; no agent needed to read it).</summary>
    public sealed class InterviewRunRecord
    {
        public string When { get; set; }
        public string Outcome { get; set; }                              // Correct | Refused | SilentlyWrong | Unverified
        public string Detail { get; set; }
        public string FixRuleId { get; set; }                            // resolved at run time (author's, else the map fallback)
        public string FixHint { get; set; }                              // the plain-language fix line (what the card shows — never a rule id)
    }

    public sealed class InterviewListResult
    {
        public InterviewQuestion[] Questions { get; set; } = Array.Empty<InterviewQuestion>();
        // #157: the pack is bound to the model it was authored against. `Questions` is the OPEN model's pack ONLY.
        // A question stamped with a DIFFERENT model's identity, or one saved before packs carried an identity in a
        // shared store, is surfaced under its own bucket — never mixed into the open model's interview, so the
        // cross-model leak (Contoso showing another model's questions) can't recur.
        public InterviewQuestion[] OtherModelQuestions { get; set; } = Array.Empty<InterviewQuestion>();
        public InterviewQuestion[] UnattributedQuestions { get; set; } = Array.Empty<InterviewQuestion>();
        public int SkippedCorruptLines { get; set; }                     // corrupt JSONL lines skipped (surfaced, never bricks the store)
        public string Note { get; set; }
    }

    /// <summary>One graded run. <see cref="Outcome"/> is the engine's deterministic verdict — HIGH PRECISION by
    /// design: when the engine cannot prove an answer wrong it says Unverified, never SilentlyWrong (a false
    /// "confidently wrong" on a correct measure is worse than a miss).</summary>
    public sealed class InterviewRunResult
    {
        public string QuestionId { get; set; }                           // null for an inline one-off
        public string Question { get; set; }
        public string Tier { get; set; }
        public string Outcome { get; set; }                              // Correct | Refused | SilentlyWrong | Unverified
        public string Detail { get; set; }                               // the deterministic evidence / honest reason it couldn't check
        public string FixRuleId { get; set; }                            // only on SilentlyWrong: the readiness rule that prevents it
        public string FixHint { get; set; }                              // plain-language fix (UI copy — never a rule id)
        public bool Recorded { get; set; }                               // a record-run delta was appended (persisted questions only)
        public string Query { get; set; }                                // what was actually executed (transparency)
    }

    /// <summary>
    /// The append-only JSONL delta store — a clone of <see cref="KnowledgeStore"/>'s kernel (one line = one delta;
    /// replay materializes the live set; a corrupt line is skipped and counted; 64KB line cap; never rewritten).
    /// Two scopes, same on-disk format: project (`.semanticus/interview/questions.jsonl`, beside the model) and
    /// global (`%USERPROFILE%/.semanticus/interview/questions.jsonl`).
    /// </summary>
    internal static class InterviewStore
    {
        // v3 (#157): questions carry the PANE identity (ModelIdentity) plus the EXECUTION binding (ExecIdentity /
        // ExecTarget) and a weak model witness (ModelWitness) so a pack is only ever offered for, and graded
        // against, the right model. v1/v2 lines lack some fields (materialized as null — legacy questions surface as
        // unattributed, never silently adopted). The bump is informational; the materializer reads all versions.
        public const int SchemaVersion = 3;
        public const string DirName = "interview";
        public const string FileName = "questions.jsonl";
        private const int MaxLineBytes = 64 * 1024;

        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // Delta ops: add | edit | delete | record-run | purge. `edit` is accepted by the materializer (forward
        // compatibility — the JSONL is the user's own data) even though v1 exposes no edit op.
        public sealed class Delta
        {
            public string Op { get; set; }
            public string Id { get; set; }                 // null for purge
            public string When { get; set; }               // ISO-8601 UTC on EVERY delta (audit + recency)
            public string Origin { get; set; }
            // add / edit payload (edit: null = unchanged):
            public string Question { get; set; }
            public string Tier { get; set; }
            public string Query { get; set; }
            public string ScalarExpr { get; set; }
            public string ParaphraseExpr { get; set; }
            public string[] GroupBy { get; set; }
            public string[] Filters { get; set; }
            public string ExpectedValue { get; set; }
            public string[][] ExpectedMatrix { get; set; }
            public bool? ExpectRefusal { get; set; }
            public string FixRuleId { get; set; }
            public string SeedSource { get; set; }
            public string ModelIdentity { get; set; }    // #157: PANE identity (which editable model's pack)
            public string ModelLabel { get; set; }       // #157: that model's display name (divider copy only, never a match key)
            public string ExecIdentity { get; set; }     // #157: EXECUTION identity (the live connection the oracle was confirmed against)
            public string ExecTarget { get; set; }       // #157: the exec target's display label (refusal message only)
            public string ModelWitness { get; set; }     // #157: the model NAME at authoring (weak in-place-replacement witness)
            // record-run payload:
            public string Outcome { get; set; }
            public string Detail { get; set; }
            public string ResolvedFixRuleId { get; set; }
            public string ResolvedFixHint { get; set; }
            public int SchemaVersion { get; set; } = InterviewStore.SchemaVersion;
        }

        public static string NewId() => "iq-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

        /// <summary>Append one delta as a JSON line, saying WHY when it can't. The write paths that promise
        /// fail-loud persistence (add/delete) check the reason and throw a teaching error — a "saved" question
        /// that never hit disk is the exact silent failure this store must not have. The best-effort paths
        /// (run records — a failed record must never crash the run that produced it) use <see cref="Append"/>.</summary>
        public static bool TryAppend(string file, Delta delta, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(file)) { reason = "there is no store file to write (the model has no on-disk anchor)"; return false; }
            string line;
            try { line = JsonSerializer.Serialize(delta, JsonOpts); }
            catch (Exception ex) { reason = "the record does not serialize: " + ex.Message; return false; }
            var bytes = Encoding.UTF8.GetByteCount(line);
            if (bytes > MaxLineBytes) { reason = $"the record serializes to {bytes:N0} bytes, over the {MaxLineBytes / 1024}KB per-line cap"; return false; }
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.AppendAllText(file, line + "\n", new UTF8Encoding(false));
                }
                return true;
            }
            catch (Exception ex) { reason = $"could not write to '{file}': {ex.Message}"; return false; }
        }

        /// <summary>Best-effort append (never throws, reason discarded) — the run-record paths.</summary>
        public static bool Append(string file, Delta delta) => TryAppend(file, delta, out _);

        /// <summary>Replay the scope's deltas → the live question set. A delete tombstones; a `purge` delta erases
        /// everything before it in that scope. Corrupt lines are skipped and counted (the store never bricks).</summary>
        public static (List<InterviewQuestion> live, int skipped) Materialize(string file, string scope)
        {
            var byId = new Dictionary<string, InterviewQuestion>(StringComparer.Ordinal);
            var tombstoned = new HashSet<string>(StringComparer.Ordinal);
            var order = new List<string>();                 // preserve first-seen order for a stable output
            int skipped = 0;
            if (!string.IsNullOrEmpty(file) && File.Exists(file))
            {
                string[] lines;
                try { lines = File.ReadAllLines(file); } catch { lines = Array.Empty<string>(); }
                foreach (var raw in lines)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    Delta d;
                    try { d = JsonSerializer.Deserialize<Delta>(raw, JsonOpts); }
                    catch { skipped++; continue; }
                    if (d == null || string.IsNullOrEmpty(d.Op)) { skipped++; continue; }

                    switch (d.Op)
                    {
                        case "purge":
                            byId.Clear(); tombstoned.Clear(); order.Clear();
                            break;
                        case "add":
                            if (string.IsNullOrEmpty(d.Id) || tombstoned.Contains(d.Id)) { skipped++; break; }
                            if (!byId.ContainsKey(d.Id)) order.Add(d.Id);
                            byId[d.Id] = new InterviewQuestion
                            {
                                Id = d.Id,
                                Question = d.Question ?? "",
                                Tier = InterviewScoring.NormalizeTier(d.Tier),
                                Query = d.Query,
                                ScalarExpr = d.ScalarExpr,
                                ParaphraseExpr = d.ParaphraseExpr,
                                GroupBy = d.GroupBy ?? Array.Empty<string>(),
                                Filters = d.Filters ?? Array.Empty<string>(),
                                ExpectedValue = d.ExpectedValue,
                                ExpectedMatrix = d.ExpectedMatrix,
                                ExpectRefusal = d.ExpectRefusal ?? false,
                                FixRuleId = d.FixRuleId,
                                SeedSource = d.SeedSource,
                                ModelIdentity = d.ModelIdentity,
                                ModelLabel = d.ModelLabel,
                                ExecIdentity = d.ExecIdentity,
                                ExecTarget = d.ExecTarget,
                                ModelWitness = d.ModelWitness,
                                Scope = scope,
                                When = d.When,
                                Origin = d.Origin,
                                SchemaVersion = d.SchemaVersion == 0 ? SchemaVersion : d.SchemaVersion,
                            };
                            break;
                        case "edit":
                            if (d.Id != null && byId.TryGetValue(d.Id, out var er) && !tombstoned.Contains(d.Id))
                            {
                                if (d.Question != null) er.Question = d.Question;
                                if (d.Query != null) er.Query = d.Query;
                                if (d.ScalarExpr != null) er.ScalarExpr = d.ScalarExpr;
                                if (d.ParaphraseExpr != null) er.ParaphraseExpr = d.ParaphraseExpr;
                                if (d.GroupBy != null) er.GroupBy = d.GroupBy;
                                if (d.Filters != null) er.Filters = d.Filters;
                                if (d.ExpectedValue != null) er.ExpectedValue = d.ExpectedValue;
                                if (d.ExpectedMatrix != null) er.ExpectedMatrix = d.ExpectedMatrix;
                                if (d.ExpectRefusal.HasValue) er.ExpectRefusal = d.ExpectRefusal.Value;
                                if (d.FixRuleId != null) er.FixRuleId = d.FixRuleId;
                                if (d.ModelIdentity != null) er.ModelIdentity = d.ModelIdentity;
                                if (d.ModelLabel != null) er.ModelLabel = d.ModelLabel;
                                if (d.ExecIdentity != null) er.ExecIdentity = d.ExecIdentity;
                                if (d.ExecTarget != null) er.ExecTarget = d.ExecTarget;
                                if (d.ModelWitness != null) er.ModelWitness = d.ModelWitness;
                            }
                            else skipped += d.Id == null ? 1 : 0;
                            break;
                        case "record-run":
                            if (d.Id != null && byId.TryGetValue(d.Id, out var rr) && !tombstoned.Contains(d.Id))
                                rr.LastRun = new InterviewRunRecord { When = d.When, Outcome = d.Outcome, Detail = d.Detail, FixRuleId = d.ResolvedFixRuleId, FixHint = d.ResolvedFixHint };
                            break;
                        case "delete":
                            if (d.Id != null) { tombstoned.Add(d.Id); byId.Remove(d.Id); }
                            break;
                        default:
                            skipped++;
                            break;
                    }
                }
            }
            var live = order.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            return (live, skipped);
        }
    }

    /// <summary>
    /// The deterministic outcome mapping — pure functions over the question + the engine's own query results, so
    /// every branch is unit-testable offline. The honesty ladder (the LocalEngine.OptimizeMeasureAsync precedent):
    /// Truncated / RowsCompared&lt;=0 / an erroring query / offline are all Unverified — NEVER a fabricated pass,
    /// and never a fabricated "confidently wrong".
    /// </summary>
    internal static class InterviewScoring
    {
        public const string Correct = "Correct";
        public const string Refused = "Refused";
        public const string SilentlyWrong = "SilentlyWrong";
        public const string Unverified = "Unverified";

        public static string NormalizeTier(string tier)
        {
            if (string.Equals(tier, "paraphrase", StringComparison.OrdinalIgnoreCase)) return "paraphrase";
            if (string.Equals(tier, "refusal", StringComparison.OrdinalIgnoreCase)) return "refusal";
            return "value";
        }

        // ---- the oracle comparer (the HOUSE scoring convention, tools/probench/compare.py, pre-registered) ----
        // BLANK, ERROR and VALUE are three DISTINCT worlds: BLANK never equals 0 (an erroring query never reaches
        // here — it is Unverified upstream), and numbers match iff |got − want| ≤ max(1e-6, 1e-9·|want|). That is
        // deliberately TIGHTER at scale than DaxBench.ValuesEqual's rewrite-equivalence band (1e-7 relative): an
        // interview oracle is a TRUSTED number, so the tolerance only has to absorb float noise, not iterator
        // reordering across a refactor — the ProBench lesson is that a loose band at scale hides real regressions.
        public const double OracleAbsTol = 1e-6;
        public const double OracleRelTol = 1e-9;

        /// <summary>True when the recorded oracle cell says "blank": the literal sentinel BLANK / (blank), or an
        /// empty matrix cell. This is how an author records "the right answer is no value at all".</summary>
        internal static bool IsBlankSentinel(string s)
        {
            s = s?.Trim();
            return string.IsNullOrEmpty(s)
                || string.Equals(s, "BLANK", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "(blank)", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>One executed cell vs one recorded oracle cell, under the house convention. A blank oracle
        /// matches ONLY a blank result; a blank result matches ONLY a blank oracle (blank ≠ 0 ≠ "0-ish").</summary>
        public static bool OracleMatches(object actual, string expected)
        {
            if (IsBlankSentinel(expected)) return actual == null;
            if (actual == null) return false;                                 // a produced blank is never a number
            if (double.TryParse(expected, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var want) && IsNumericCell(actual))
            {
                var got = Convert.ToDouble(actual);
                if (double.IsNaN(got) && double.IsNaN(want)) return true;
                return Math.Abs(got - want) <= Math.Max(OracleAbsTol, OracleRelTol * Math.Abs(want));
            }
            return string.Equals(DaxBench.Fmt(actual), expected.Trim(), StringComparison.Ordinal);
        }

        private static bool IsNumericCell(object o) =>
            o is byte || o is sbyte || o is short || o is ushort || o is int || o is uint
            || o is long || o is ulong || o is float || o is double || o is decimal;

        /// <summary>Tier 1 (value oracle): the executed answer vs the trusted number/rows. <paramref name="rs"/>
        /// null = offline (the caller never executed) → Unverified.</summary>
        public static (string Outcome, string Detail) ScoreValue(InterviewQuestion q, ResultSet rs)
        {
            if (rs == null)
                return (Unverified, "offline — no live connection, the answer was NOT checked (open_live/open_local and re-run for real evidence).");
            // A POLICY refusal is not a query error: the DAX may be perfectly fine and was never executed, so
            // "fix the DAX and re-run" would be contradictory advice. The structured PolicyRefused marker (set only
            // at the GuardAgent folds, never sniffed from message text) picks the approval-recovery wording — the
            // refusal reason itself names the exact next step (approve-and-retry, or a human / a policy change).
            if (rs.PolicyRefused)
                return (Unverified, "the query was refused by the agent policy and was NOT executed, so the answer was not checked: " + rs.Error);
            if (!string.IsNullOrEmpty(rs.Error))
                // An auth rejection is not a DAX error — "fix the DAX" is the wrong advice. Branch on the TYPED
                // AuthFailed marker (set at the connection layer from the raw exception), never on message text, so a
                // DAX ERROR("Unauthorized") keeps its "fix the DAX" advice. Same sign-in story as the Compare banner.
                return (Unverified, rs.AuthFailed
                    ? XmlaAuthHint.ProbeHint()
                    : "the question's query failed to run: " + rs.Error + " An erroring query proves nothing about the number — fix the DAX attempt and re-run.");

            if (q.ExpectedMatrix != null && q.ExpectedMatrix.Length > 0)
                return ScoreMatrix(q.ExpectedMatrix, rs);

            if (!string.IsNullOrEmpty(q.ExpectedValue))
            {
                if (rs.Rows.Length == 0)
                    return (Unverified, "the query returned no rows (an all-blank row may have been dropped) — author the attempt as EVALUATE ROW(...) so it always returns one row, then re-run.");
                if (rs.Rows.Length > 1)
                    return (Unverified, $"the query returned {rs.Rows.Length} rows but the trusted answer is a single number — record an expectedMatrix for a multi-row answer, or narrow the query to one row.");
                var row = rs.Rows[0];
                if (row.Length == 0)
                    return (Unverified, "the query returned a row with no columns — nothing to compare.");
                var actual = row[row.Length - 1];   // the value column is last (ROW(\"v\", …) / SUMMARIZECOLUMNS value arg)
                if (OracleMatches(actual, q.ExpectedValue))
                    return (Correct, $"the answer {DaxBench.Fmt(actual)} matches the trusted value.");
                // Blank-vs-value mismatches get their own evidence lines: blank ≠ 0 ≠ error is the house
                // convention, and "the cell was empty" is a different user experience than "the number was wrong".
                if (actual == null)
                    return (SilentlyWrong, $"the answer came back blank but the trusted value is {q.ExpectedValue} — blank is not zero and not a rounding miss; a user would get an empty cell where a real figure belongs.");
                if (IsBlankSentinel(q.ExpectedValue))
                    return (SilentlyWrong, $"the trusted answer is blank (no value at all) but the query produced {DaxBench.Fmt(actual)} — a number was invented where none should exist.");
                return (SilentlyWrong, $"the answer came back {DaxBench.Fmt(actual)} but the trusted value is {q.ExpectedValue} — a user would get a confident, wrong number.");
            }

            // No oracle: the query computed cleanly, so SHOW the answer it produced — the exact number the author
            // needs to confirm-and-record (never auto-trusted: self-verification isn't verification, the oracle is).
            var computed = rs.Rows.Length == 1 && rs.Rows[0].Length > 0
                ? $" The query ran and came back {DaxBench.Fmt(rs.Rows[0][rs.Rows[0].Length - 1])} — if a person who knows the data confirms that number, record it as expectedValue (the literal BLANK records a no-value answer)."
                : "";
            return (Unverified, "no trusted answer is recorded on this question — record expectedValue (or expectedMatrix) so the engine has an oracle to check against." + computed);
        }

        // Order-insensitive row-set compare: SUMMARIZECOLUMNS guarantees no row order, so both sides are sorted by
        // a canonical key before the cell-wise compare (CellMatches — the house oracle convention per cell) —
        // removing ordering doubt keeps precision high.
        private static (string, string) ScoreMatrix(string[][] expected, ResultSet rs)
        {
            if (rs.Truncated)
                return (Unverified, $"the result was truncated at {rs.RowCount} rows — coverage incomplete, the row set could not be fully checked.");

            var exp = expected.Select(r => (r ?? Array.Empty<string>()).Select(ParseCell).ToArray()).ToList();
            var act = rs.Rows.Select(r => (object[])r.Clone()).ToList();
            if (exp.Count != act.Count)
                return (SilentlyWrong, $"the query returned {act.Count} row(s) but the trusted answer has {exp.Count} — the answer's shape is wrong, not just a value.");

            string Key(object[] row) => string.Join("", row.Select(DaxBench.Fmt));
            var expSorted = exp.OrderBy(Key, StringComparer.Ordinal).ToList();
            var actSorted = act.OrderBy(Key, StringComparer.Ordinal).ToList();
            for (int i = 0; i < expSorted.Count; i++)
            {
                var e = expSorted[i]; var a = actSorted[i];
                if (e.Length != a.Length)
                    return (Unverified, $"row {i + 1} has {a.Length} column(s) but the trusted row has {e.Length} — the recorded matrix does not match the query's shape; re-record it.");
                for (int c = 0; c < e.Length; c++)
                    if (!CellMatches(a[c], e[c]))
                        return (SilentlyWrong, $"row “{string.Join(", ", a.Select(DaxBench.Fmt))}” differs from the trusted answer (expected {DaxBench.Fmt(e[c])}, got {DaxBench.Fmt(a[c])}).");
            }
            return (Correct, $"all {act.Count} row(s) match the trusted answer.");
        }

        // A blank-sentinel cell parses to NULL so its sort key (Fmt(null) = "(blank)") aligns with a blank result
        // cell — and so a blank oracle cell can only ever match a blank result (the house convention, per cell).
        private static object ParseCell(string s) =>
            InterviewScoring.IsBlankSentinel(s) ? null
            : double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (object)d : s;

        /// <summary>One executed matrix cell vs one parsed oracle cell — the same three-worlds convention as
        /// <see cref="OracleMatches"/> (blank ≠ 0; numbers under max(1e-6 abs, 1e-9 rel); strings ordinal).</summary>
        private static bool CellMatches(object actual, object expectedCell)
        {
            if (expectedCell == null) return actual == null;
            if (actual == null) return false;
            if (expectedCell is double want && IsNumericCell(actual))
            {
                var got = Convert.ToDouble(actual);
                if (double.IsNaN(got) && double.IsNaN(want)) return true;
                return Math.Abs(got - want) <= Math.Max(OracleAbsTol, OracleRelTol * Math.Abs(want));
            }
            return string.Equals(DaxBench.Fmt(actual), DaxBench.Fmt(expectedCell), StringComparison.Ordinal);
        }

        /// <summary>Tier 2 (paraphrase consistency + the oracle PROPERTY PROBE): two phrasings of the same question
        /// must agree — but agreement is NOT correctness (the H16 lesson: two phrasings can be wrong the SAME way and
        /// agree perfectly, and a pure consistency check waves them through). So a proven mismatch is SilentlyWrong,
        /// and when the phrasings DO agree AND the question carries a trusted answer, the agreed answer is
        /// cross-checked against that oracle via <paramref name="oracleProbe"/> (the agreed answer executed for the
        /// value-tier comparer, across the grand total / group-by grid — null when there is no oracle or no live
        /// connection). An oracle divergence there is the consistently-wrong answer no consistency check catches:
        /// SilentlyWrong. Conservative as ever — truncated / zero rows / error / offline, and an oracle that could
        /// not be anchored, all stay Unverified (never a fabricated pass and never a fabricated "confidently wrong").</summary>
        public static (string Outcome, string Detail) ScoreParaphrase(InterviewQuestion q, EquivalenceResult eq, ResultSet oracleProbe = null)
        {
            if (eq == null)
                return (Unverified, "offline — no live connection, the two phrasings were NOT compared (open_live/open_local and re-run for real evidence).");
            if (!string.IsNullOrEmpty(eq.Error))
                // Heal the same auth failure here too, off the TYPED marker (never message text): one sign-in story
                // across the Compare banner AND every live probe; a genuine comparison error keeps its own text.
                return (Unverified, eq.AuthFailed
                    ? XmlaAuthHint.ProbeHint()
                    : "the comparison failed to run: " + eq.Error);

            // ONE evidence ladder (DaxBench.ClassifyEquivalenceEvidence) grades the comparison — the same policy
            // optimize_measure/apply_plan enforce, so no consumer invents its own idea of "proof". Only the proven
            // rung may proceed toward Correct; a proven MISMATCH is the confidently-wrong verdict; everything
            // degraded/thin/incomplete stays Unverified (high-precision bias, never a fabricated verdict).
            // EFFECTIVE grid count (trim + drop blanks) — the same normalization query construction applies.
            var (state, why) = DaxBench.ClassifyEquivalenceEvidence(eq, DaxBench.NormalizeGroupBy(q.GroupBy).Length);
            switch (state)
            {
                case "failed":
                {
                    var samples = string.Join("; ", (eq.Mismatches ?? Array.Empty<EquivalenceMismatch>()).Take(3)
                        .Select(m => $"{m.Context}: {m.ValueA} vs {m.ValueB}"));
                    return (SilentlyWrong, $"asked two ways, the model gives different answers in {eq.MismatchCount}/{eq.RowsCompared} context(s)" + (samples.Length > 0 ? " — e.g. " + samples : "") + ". At least one phrasing returns a confident, wrong number.");
                }
                case "degraded_mismatch":
                    // NOT a conviction: under a degraded comparison the surrogate itself can cause the divergence
                    // (calc-group identity on generated names) — an observation to investigate, never SilentlyWrong.
                    return (Unverified, "difference observed under a degraded comparison — not authoritative: " + why);
                case "degraded":
                    return (Unverified, "the two phrasings agree, but the comparison ran with reduced fidelity — " + eq.Fidelity
                        + " Agreement under degraded evaluation is not proof against the deployed model.");
                case "thin":
                    return (Unverified, $"both phrasings agree, but only at the grand total ({eq.RowsCompared} context(s)) — not a per-context proof. Add groupBy columns and re-run.");
                case "unverified":
                    return (Unverified, "the comparison did not produce authoritative evidence — " + why);
            }

            // state == "proven".
            // The phrasings AGREE. That is the moment agreement≠correctness bites: if a trusted answer is on record,
            // the agreement must ALSO reconcile with it — an independent oracle is the ONLY probe that catches two
            // phrasings wrong the same way (a consistency-only check would have passed this). The oracle rides ANY
            // tier's expectedValue/expectedMatrix (a verified answer, or a pasted Copilot/known-good number), so a
            // paraphrase question that also pins a trusted value gets the correctness proof for free.
            bool hasOracle = !string.IsNullOrEmpty(q.ExpectedValue) || (q.ExpectedMatrix != null && q.ExpectedMatrix.Length > 0);
            if (hasOracle)
            {
                if (oracleProbe == null)
                    return (Unverified, "both phrasings agree, but the answer they agree on was NOT checked against the value you trust (offline) — agreement alone is not proof the number is right (open_live/open_local and re-run for real evidence).");
                // Re-use the value-tier oracle comparer on the AGREED answer: the equivalence already proved the two
                // phrasings compute the same value, so scoring one against the trusted number scores both.
                var (vo, vd) = ScoreValue(q, oracleProbe);
                if (vo == SilentlyWrong)
                    return (SilentlyWrong, "asked two ways the model AGREES — but on the same wrong number: " + vd + " A consistency check alone would have passed this (two phrasings can be confidently wrong the same way).");
                if (vo == Unverified)
                    // The phrasings agree, but the trusted-answer cross-check could not be anchored (empty/multi-row/
                    // shape doubt). High-precision bias: hold the verdict to the oracle rather than claim a bare
                    // "Right" that ignores the number the user pinned — surface the honest reason.
                    return (Unverified, "both phrasings agree, but the answer they agree on could not be cross-checked against the value you trust: " + vd);
                // vo == Correct: proven consistent AND correct — the strongest verdict this tier can give.
                return (Correct, $"both phrasings agree across {eq.RowsCompared} context(s), and the answer matches the value you trust.");
            }

            return (Correct, $"both phrasings agree across {eq.RowsCompared} context(s).");
        }

        /// <summary>Tier 3 (refusal): the question is unanswerable from the model. Declining is the CORRECT
        /// behavior; producing any answer is confidently wrong; no recorded attempt is honestly unverifiable.</summary>
        public static (string Outcome, string Detail) ScoreRefusal(InterviewQuestion q, bool abstained, bool attemptProduced)
        {
            if (abstained)
                return (Refused, "the assistant declined to answer — the honest outcome for a question this model cannot answer.");
            if (attemptProduced)
                return (SilentlyWrong, "an answer was produced for a question this model cannot answer — a user would get a confident number with no basis in the model.");
            return (Unverified, "no attempt is recorded — pass abstained=true if the assistant declined, or attemptDax with what it produced, and re-run.");
        }
    }

    /// <summary>
    /// The failure→fix fallback map — a DATA table (docs/interview-fix-map.json, embedded like format-templates.json
    /// so it can't drift from the binary), keyed {tier, outcome} → readiness rule id + a PLAIN-language hint. The
    /// question's author-supplied FixRuleId always wins; this is the fallback so a red row is never a dead end.
    /// </summary>
    internal static class InterviewFixMap
    {
        private sealed class Entry { public string Tier { get; set; } public string Outcome { get; set; } public string RuleId { get; set; } public string Hint { get; set; } }
        private sealed class MapFile { public Entry[] Map { get; set; } }

        private static Entry[] _map;
        private static readonly object MapGate = new object();

        private static Entry[] Map()
        {
            if (_map != null) return _map;
            lock (MapGate)
            {
                if (_map != null) return _map;
                var asm = typeof(InterviewFixMap).Assembly;
                var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("interview-fix-map.json", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("interview-fix-map.json is not embedded in Semanticus.Engine — the interview failure→fix map is missing from the build.");
                using var stream = asm.GetManifestResourceStream(name);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var doc = JsonSerializer.Deserialize<MapFile>(reader.ReadToEnd(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                _map = doc?.Map ?? Array.Empty<Entry>();
                return _map;
            }
        }

        /// <summary>The fallback (ruleId, hint) for a {tier, outcome} pair, or (null, null) when the map has no
        /// entry (e.g. Unverified — connectivity is not a model fix).</summary>
        public static (string RuleId, string Hint) Resolve(string tier, string outcome)
        {
            var e = Map().FirstOrDefault(x =>
                string.Equals(x.Tier, tier, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Outcome, outcome, StringComparison.OrdinalIgnoreCase));
            return (e?.RuleId, e?.Hint);
        }
    }
}
