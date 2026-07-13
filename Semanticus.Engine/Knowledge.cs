using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    // Learning Loop L1 (knowledge store) + L2 (recall) — docs/learning-loop-plan.md §3.3–3.5. This file is the
    // deterministic KERNEL: the append-only JSONL delta store (ExpeL-style ADD/EDIT/UPVOTE/DOWNVOTE, plus
    // approve/delete/purge), its materializer, and the model FINGERPRINT computation. No inference lives here
    // (golden rule 1) — the engine counts and retrieves deterministically; the user's Claude does all judgment.
    // The ops (write-gates, entitlement, activity broadcast) live in LocalEngine.Knowledge.cs.

    // ---- serialized shapes (the user's own data — plain JSONL, readable without us) --------------------

    /// <summary>The provenance envelope on every insight (MemoryGraft mitigation — §6): a record carries who/when
    /// authored it, the session, and the run(s) it was distilled from, so a poisoned or stale insight is traceable
    /// and purgeable rather than an anonymous authority.</summary>
    public sealed class InsightProvenance
    {
        public string When { get; set; }                                 // ISO-8601 UTC of creation
        public string Origin { get; set; }                               // "agent" | "human"
        public string SessionId { get; set; }
        public string[] SourceRunIds { get; set; } = Array.Empty<string>();
        public int SchemaVersion { get; set; } = KnowledgeStore.SchemaVersion;
    }

    /// <summary>One materialized insight (§3.3). Score starts at 3 (ExpeL importance counter); a downvote to &lt;= 0
    /// materializes it OUT (gone, without erasing the audit trail). Fingerprint (nullable) scopes it to matching
    /// model shapes; null = a user-level insight that travels across all models.</summary>
    public sealed class InsightRecord
    {
        public string Id { get; set; }                                   // ki-<8hex>
        public string Text { get; set; }                                 // the insight — one actionable sentence-to-paragraph
        public string Kind { get; set; }                                 // "insight" | "post-mortem"
        public string[] Keys { get; set; } = Array.Empty<string>();      // deterministic match keys (rule ids / gate sigs / error types / op names / domain tokens)
        public string Fingerprint { get; set; }                          // FingerprintKey it is scoped to, or null (all models)
        public string Status { get; set; }                               // "pending" | "approved" (SSGM write-gate)
        public int Score { get; set; }                                   // importance counter
        public int Uses { get; set; }                                    // times attached to a run/step (populated by L3+)
        public int Retrievals { get; set; }                              // times returned by recall_experience
        public string Scope { get; set; }                                // "project" | "global" (set by the reader, not stored)
        public string LastUsedUtc { get; set; }                          // latest touch (add/edit/vote/retrieve) — feeds temporal decay
        public InsightProvenance Provenance { get; set; }
    }

    /// <summary>A model's deterministic identity for recall (§3.4). No embeddings (golden rule 1) — a small,
    /// reproducible DTO of shape + naming style + domain vocabulary. <see cref="FingerprintKey"/> is the stable
    /// hash used for the strong "same model shape" match; DomainTokens give the weak cross-model signal.</summary>
    public sealed class ModelFingerprint
    {
        public int Tables { get; set; }
        public int Measures { get; set; }
        public int Columns { get; set; }
        public string[] SourceTypes { get; set; } = Array.Empty<string>();
        public string[] FactTables { get; set; } = Array.Empty<string>();   // has measures + many-side of a relationship
        public string[] DimTables { get; set; } = Array.Empty<string>();    // one-side of a relationship
        public string NamingHash { get; set; }                              // SHA-8 of the sorted casing/underscore/space signature (NOT the names)
        public string[] DomainTokens { get; set; } = Array.Empty<string>(); // top-12 distinct lowercase words (len>3) from table+measure names
        public double? Grade { get; set; }                                  // readiness grade IFF cheaply available (null here — no forced scan)
        public string FingerprintKey { get; set; }                          // stable shape hash (same shape → same key)
    }

    public sealed class InsightListResult
    {
        public InsightRecord[] Insights { get; set; } = Array.Empty<InsightRecord>();
        public int SkippedCorruptLines { get; set; }                        // corrupt JSONL lines skipped (surfaced, never bricks the store)
        public string Note { get; set; }
    }

    /// <summary>One recall candidate: the insight + WHY it matched (matchedKeys + a note) + its deterministic rank.
    /// The engine retrieves; the agent does the semantic ranking over this candidate set.</summary>
    public sealed class RecallCandidate
    {
        public InsightRecord Insight { get; set; }
        public string[] MatchedKeys { get; set; } = Array.Empty<string>();
        public bool FingerprintMatch { get; set; }                          // strong: same FingerprintKey
        public int DomainOverlap { get; set; }                              // weak: shared domain tokens
        public double Rank { get; set; }
        public string Why { get; set; }
    }

    public sealed class RecallResult
    {
        public RecallCandidate[] Candidates { get; set; } = Array.Empty<RecallCandidate>();
        public ModelFingerprint Fingerprint { get; set; }
        public int SkippedCorruptLines { get; set; }
        public string RankingNote { get; set; }
        public string Note { get; set; }                                    // "no prior experience for this model shape" when 0 candidates
    }

    public sealed class PurgeResult
    {
        public string Scope { get; set; }
        public int LiveCount { get; set; }                                  // insights that WOULD be / WERE erased
        public bool Purged { get; set; }                                    // false = dry-run
        public string Note { get; set; }
    }

    /// <summary>
    /// The append-only JSONL delta store (§3.3, constraint #2 — the store evolves by itemized deltas, never a
    /// wholesale rewrite). One line = one delta {op, id, ...}. The reader REPLAYS deltas in order to materialize
    /// the live set; a corrupt line is SKIPPED and counted (never bricks the store). Two scopes, same on-disk
    /// format: project (`.semanticus/knowledge/insights.jsonl`, beside the model — the ExperienceLog placement
    /// rule) and global (`%USERPROFILE%/.semanticus/knowledge/insights.jsonl`, user-level patterns that travel).
    /// </summary>
    internal static class KnowledgeStore
    {
        public const int SchemaVersion = 1;
        public const int InitialScore = 3;                 // ExpeL init (we use 3; delete/materialize-out at <= 0)
        public const string DirName = "knowledge";
        public const string FileName = "insights.jsonl";
        public const string SettingsFileName = "knowledge-settings.json";
        private const int MaxLineBytes = 64 * 1024;

        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // Delta ops. add/edit/vote/approve/delete/purge are the §3.3 kinds; `retrieve` is the L2 read-side counter
        // bump (§3.4 "each returned insight increments its retrievals counter — a delta append"). `use` is reserved
        // for L3+ (attach-to-step) and not written in this slice.
        public sealed class Delta
        {
            public string Op { get; set; }                 // add | edit | vote | approve | delete | purge | retrieve
            public string Id { get; set; }                 // null for purge
            public string When { get; set; }               // ISO-8601 UTC on EVERY delta (audit + recency)
            public string Origin { get; set; }
            // add:
            public string Text { get; set; }               // add: required; edit: null = unchanged
            public string[] Keys { get; set; }             // add / edit: null = unchanged
            public string Kind { get; set; }
            public string Fingerprint { get; set; }
            public string Status { get; set; }             // add: "pending" | "approved"
            public string SessionId { get; set; }
            public string[] SourceRunIds { get; set; }
            // vote:
            public int VoteDelta { get; set; }             // +1 | -1
            public int SchemaVersion { get; set; } = KnowledgeStore.SchemaVersion;
        }

        public static string NewId() => "ki-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

        /// <summary>Append one delta as a JSON line. Best-effort: returns false (never throws) on no-target/oversized/
        /// write-fail — a knowledge write must never crash the op that produced it, mirroring the experience log.</summary>
        public static bool Append(string file, Delta delta)
        {
            try
            {
                if (string.IsNullOrEmpty(file)) return false;
                var line = JsonSerializer.Serialize(delta, JsonOpts);
                if (Encoding.UTF8.GetByteCount(line) > MaxLineBytes) return false;
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.AppendAllText(file, line + "\n", new UTF8Encoding(false));
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Replay the scope's deltas → the live insight set. score &lt;= 0 OR a delete tombstone = gone; a
        /// `purge` delta erases everything before it in that scope. Corrupt lines are skipped and counted.</summary>
        public static (List<InsightRecord> live, int skipped) Materialize(string file, string scope)
        {
            var byId = new Dictionary<string, InsightRecord>(StringComparer.Ordinal);
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
                            byId[d.Id] = new InsightRecord
                            {
                                Id = d.Id,
                                Text = d.Text ?? "",
                                Kind = string.IsNullOrEmpty(d.Kind) ? "insight" : d.Kind,
                                Keys = d.Keys ?? Array.Empty<string>(),
                                Fingerprint = d.Fingerprint,
                                Status = d.Status == "approved" ? "approved" : "pending",
                                Score = InitialScore,
                                Scope = scope,
                                LastUsedUtc = d.When,
                                Provenance = new InsightProvenance
                                {
                                    When = d.When, Origin = d.Origin, SessionId = d.SessionId,
                                    SourceRunIds = d.SourceRunIds ?? Array.Empty<string>(),
                                    SchemaVersion = d.SchemaVersion == 0 ? SchemaVersion : d.SchemaVersion,
                                },
                            };
                            break;
                        case "edit":
                            if (d.Id != null && byId.TryGetValue(d.Id, out var er) && !tombstoned.Contains(d.Id))
                            {
                                if (d.Text != null) er.Text = d.Text;
                                if (d.Keys != null) er.Keys = d.Keys;
                                er.LastUsedUtc = d.When ?? er.LastUsedUtc;
                            }
                            else skipped += d.Id == null ? 1 : 0;
                            break;
                        case "vote":
                            if (d.Id != null && byId.TryGetValue(d.Id, out var vr) && !tombstoned.Contains(d.Id))
                            {
                                vr.Score += d.VoteDelta;
                                vr.LastUsedUtc = d.When ?? vr.LastUsedUtc;
                            }
                            break;
                        case "approve":
                            if (d.Id != null && byId.TryGetValue(d.Id, out var ar) && !tombstoned.Contains(d.Id))
                            {
                                ar.Status = "approved";
                                ar.LastUsedUtc = d.When ?? ar.LastUsedUtc;
                            }
                            break;
                        case "retrieve":
                            if (d.Id != null && byId.TryGetValue(d.Id, out var rr) && !tombstoned.Contains(d.Id))
                            {
                                rr.Retrievals++;
                                rr.LastUsedUtc = d.When ?? rr.LastUsedUtc;
                            }
                            break;
                        case "use":
                            if (d.Id != null && byId.TryGetValue(d.Id, out var ur) && !tombstoned.Contains(d.Id))
                                ur.Uses++;
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
            // materialize-out: score <= 0 is gone (ExpeL delete-at-zero), tombstones already removed.
            var live = order.Where(byId.ContainsKey).Select(id => byId[id]).Where(r => r.Score > 0).ToList();
            return (live, skipped);
        }

        // ---- fingerprint (§3.4) — deterministic, no embeddings --------------------------------------------

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
        { "the", "and", "for", "with", "from", "this", "that", "into", "has", "was", "are", "its", "all", "not", "per" };

        public static ModelFingerprint ComputeFingerprint(Model m)
        {
            var tables = m.Tables.ToList();
            int columns = tables.Sum(t => t.Columns.Count(c => c.Type != ColumnType.RowNumber));
            int measures = tables.Sum(t => t.Measures.Count);

            // one-side / many-side classification off the relationship graph.
            var oneSide = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // dim-like (PK side)
            var manySide = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // fact-like (FK side)
            foreach (var r in m.Relationships.OfType<SingleColumnRelationship>())
            {
                if (r.FromColumn?.Table != null) manySide.Add(r.FromColumn.Table.Name);
                if (r.ToColumn?.Table != null) oneSide.Add(r.ToColumn.Table.Name);
            }
            var facts = tables.Where(t => t.Measures.Count > 0 && manySide.Contains(t.Name))
                              .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var factSet = new HashSet<string>(facts, StringComparer.OrdinalIgnoreCase);
            var dims = tables.Where(t => oneSide.Contains(t.Name) && !factSet.Contains(t.Name))
                             .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

            var sourceTypes = tables.SelectMany(t => t.Partitions.Select(p => p.SourceType.ToString()))
                                    .Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();

            var namingHash = Sha8(string.Join("|",
                tables.Select(t => Signature(t.Name)).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal)));

            var domainTokens = DomainTokens(tables);

            var key = Sha8(string.Join("~",
                tables.Count, measures, columns,
                string.Join(",", sourceTypes),
                string.Join(",", facts), string.Join(",", dims),
                namingHash, string.Join(",", domainTokens)));

            return new ModelFingerprint
            {
                Tables = tables.Count,
                Measures = measures,
                Columns = columns,
                SourceTypes = sourceTypes,
                FactTables = facts,
                DimTables = dims,
                NamingHash = namingHash,
                DomainTokens = domainTokens,
                Grade = null,                    // skip — a readiness scan is not cheap; L2 does not force one
                FingerprintKey = key,
            };
        }

        /// <summary>Top-12 distinct domain words (lowercase, len>3, stopwords out) across table + measure names,
        /// ranked by frequency then alphabetically — the weak cross-model signal.</summary>
        public static string[] DomainTokens(IEnumerable<Table> tables)
        {
            var freq = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in tables)
            {
                foreach (var w in Words(t.Name)) Bump(freq, w);
                foreach (var me in t.Measures) foreach (var w in Words(me.Name)) Bump(freq, w);
            }
            return freq.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                       .Take(12).Select(kv => kv.Key).ToArray();
        }

        private static void Bump(Dictionary<string, int> freq, string w) { freq[w] = freq.TryGetValue(w, out var n) ? n + 1 : 1; }

        private static readonly Regex CamelSplit = new Regex("(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);
        private static readonly Regex NonWord = new Regex("[^A-Za-z0-9]+", RegexOptions.Compiled);

        /// <summary>Tokenize a name into domain words: split camelCase + non-alphanumeric, lowercase, keep len&gt;3
        /// non-stopwords. Shared by DomainTokens and the recall key-overlap so both tokenize identically.</summary>
        public static IEnumerable<string> Words(string s)
        {
            if (string.IsNullOrEmpty(s)) yield break;
            foreach (var raw in NonWord.Split(CamelSplit.Replace(s, " ")))
            {
                if (raw.Length <= 3) continue;
                var w = raw.ToLowerInvariant();
                if (!StopWords.Contains(w)) yield return w;
            }
        }

        /// <summary>A table name's casing/underscore/space signature (NOT its letters): each char → class
        /// (A upper, a lower, 9 digit, _ space, U underscore, . other), consecutive runs collapsed. So
        /// "Sales Order" → "Aa_Aa" — a naming CONVENTION signal that says nothing about the actual names.</summary>
        public static string Signature(string name)
        {
            var sb = new StringBuilder();
            char last = '\0';
            foreach (var ch in name ?? "")
            {
                char cls = char.IsUpper(ch) ? 'A' : char.IsLower(ch) ? 'a' : char.IsDigit(ch) ? '9'
                         : ch == ' ' ? '_' : ch == '_' ? 'U' : '.';
                if (cls != last) { sb.Append(cls); last = cls; }
            }
            return sb.ToString();
        }

        public static string Sha8(string s)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? "")), 0, 8).ToLowerInvariant();
        }
    }
}
