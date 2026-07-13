using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// Learning Loop L1 (knowledge store) + L2 (recall) ops (docs/learning-loop-plan.md §3.3–3.5) — dual-drive
    /// like everything else, serialized on <see cref="_knowledgeGate"/>. FREE/PRO (§5): capture + recall-VIEW are
    /// free — this whole slice is free. The store IS the user's own data (plain JSONL, readable without us); the
    /// COMPOUNDING machinery that becomes Pro (distillation orchestration, learned-workflow enforcement, outcome
    /// analytics) lands in L3+, not here. Every write is a delta-only append (constraint #2 — never a rewrite) and
    /// rides an ActivityEvent onto the bus so the experience log records knowledge changes too.
    /// </summary>
    public sealed partial class LocalEngine
    {
        private readonly System.Threading.SemaphoreSlim _knowledgeGate = new System.Threading.SemaphoreSlim(1, 1);

        // ---- scope → file (the ExperienceLog placement rule: sidecar beside the model, workspace fallback for
        //      live/unsaved sessions; global = %USERPROFILE%/.semanticus/knowledge) ------------------------------

        private string ProjectKnowledgeDir()
        {
            var anchor = _sessions.Current?.SourcePath;
            var sidecar = !ExperienceStore.IsEphemeralAnchor(anchor) ? LayoutStore.DirFor(anchor)
                        : _workspaceDir == null ? null : Path.Combine(_workspaceDir, LayoutStore.DirName);
            // Ephemeral (live/XMLA/unsaved) + no workspace → no project location. Rather than strand project-scope
            // insights as UNWRITABLE (the null case), borrow the GLOBAL knowledge DIR — but ScopeFile keeps the project
            // FILE scope-private (a per-model insights.project.<fp>.jsonl), never the shared global insights.jsonl. Scope
            // is not stored per record (the FILE is the scope) and a `purge` erases the whole file, so sharing the file
            // would let a project purge wipe the user's global memory. Distinct files keep purge/scope-labels per-scope.
            return sidecar == null ? GlobalKnowledgeDir() : Path.Combine(sidecar, KnowledgeStore.DirName);
        }

        // Honor %USERPROFILE% literally (the spec's global-scope path) — the env var, not GetFolderPath(UserProfile),
        // which on Windows reads the OS token and ignores the var (so it wouldn't be redirectable/testable). Fall back
        // to GetFolderPath only if the var is unset.
        private static string GlobalKnowledgeDir()
        {
            var home = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, LayoutStore.DirName, KnowledgeStore.DirName);
        }

        private string ScopeDir(string scope)
            => string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase) ? GlobalKnowledgeDir() : ProjectKnowledgeDir();

        private string ScopeFile(string scope)
        {
            var dir = ScopeDir(scope);
            if (dir == null) return null;
            // Global is always the shared insights.jsonl. Project is too — EXCEPT in the ephemeral-no-workspace fallback,
            // where it borrows the global DIR: there it MUST take a scope-private name so it and global stay distinct
            // files (see ProjectKnowledgeDir — otherwise a project purge would erase the shared global memory).
            var fileName = (!string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase) && ProjectFallsBackToGlobal())
                ? ProjectFallbackFileName()
                : KnowledgeStore.FileName;
            return Path.Combine(dir, fileName);
        }

        // True when the current session has no durable project location (ephemeral anchor + no workspace) — the project
        // insight store then borrows the GLOBAL knowledge dir and needs a scope-private file name (see ScopeFile).
        private bool ProjectFallsBackToGlobal()
            => ExperienceStore.IsEphemeralAnchor(_sessions.Current?.SourcePath) && _workspaceDir == null;

        // The scope-private project file name for the fallback, keyed by a stable, cheap model identity so different
        // models keep SEPARATE project stores (a project purge only erases THIS model's file, and a model's project
        // insights aren't recalled for another model). Live endpoint|database for an XMLA session (survives reconnects →
        // the store persists across sessions); else the coarse anchor hash. Unkeyable → one shared insights.project.jsonl.
        private string ProjectFallbackFileName()
        {
            var s = _sessions.Current;
            var key = s == null ? null
                    : s.LiveOrigin != null ? ExperienceStore.FingerprintForLive(s.LiveOrigin)
                    : ExperienceStore.FingerprintFor(s.SourcePath);
            return string.IsNullOrEmpty(key) ? "insights.project.jsonl" : $"insights.project.{key}.jsonl";
        }

        private static string NormalizeScope(string scope)
            => string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase) ? "global" : "project";

        /// <summary>Auto-approve setting from `.semanticus/knowledge-settings.json` {"autoApprove": true|false}.
        /// Default TRUE for single-user LOCAL mode (§3.3) — an insight lands approved immediately; set it false to
        /// force the review (pending) path (the team-mode / SSGM write-gate posture).</summary>
        private bool AutoApprove()
        {
            try
            {
                var dir = ProjectKnowledgeDir();
                var file = dir == null ? null : Path.Combine(Path.GetDirectoryName(dir), KnowledgeStore.SettingsFileName);
                if (file != null && File.Exists(file))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("autoApprove", out var v)
                        && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                        return v.GetBoolean();
                }
            }
            catch { /* a malformed settings file must not brick capture — default applies */ }
            return true;
        }

        private static InsightRecord MaterializeOne(string file, string scope, string id)
        {
            var (live, _) = KnowledgeStore.Materialize(file, scope);
            return live.FirstOrDefault(r => r.Id == id);
        }

        /// <summary>Find which scope's file holds a live insight id (project first, then global) — the single-id
        /// ops don't take a scope, so we resolve it deterministically. Throws instructively when absent.</summary>
        private (string file, string scope, InsightRecord rec) LocateInsight(string id)
        {
            foreach (var scope in new[] { "project", "global" })
            {
                var file = ScopeFile(scope);
                var rec = MaterializeOne(file, scope, id);
                if (rec != null) return (file, scope, rec);
            }
            throw new InvalidOperationException($"Insight '{id}' not found (it may be pending, purged, or downvoted out — list_insights shows the live set).");
        }

        // ---- writes (delta-only appends) -----------------------------------------------------------------

        public async Task<InsightRecord> AddInsightAsync(string text, string[] keys, string kind, string scope, bool fingerprintScoped, string origin)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("An insight needs text — one actionable sentence-to-paragraph.");
            kind = string.Equals(kind, "post-mortem", StringComparison.OrdinalIgnoreCase) ? "post-mortem" : "insight";
            scope = NormalizeScope(scope);
            var file = ScopeFile(scope);
            if (file == null)
                throw new InvalidOperationException("No project knowledge store: open or save a model, or run the engine with a workspace (or use scope='global').");

            string fp = null;
            if (fingerprintScoped)
            {
                var s = _sessions.Current ?? throw new InvalidOperationException("fingerprintScoped=true needs an open model to compute the fingerprint. Open a model, or add it unscoped.");
                fp = await s.ReadAsync(m => KnowledgeStore.ComputeFingerprint(m).FingerprintKey);
            }

            var id = KnowledgeStore.NewId();
            var status = AutoApprove() ? "approved" : "pending";
            await _knowledgeGate.WaitAsync();
            try
            {
                KnowledgeStore.Append(file, new KnowledgeStore.Delta
                {
                    Op = "add", Id = id, When = DateTime.UtcNow.ToString("o"), Origin = origin ?? "agent",
                    Text = text, Keys = keys ?? Array.Empty<string>(), Kind = kind, Fingerprint = fp, Status = status,
                    SessionId = _sessions.Current?.Id, SourceRunIds = Array.Empty<string>(),
                });
            }
            finally { _knowledgeGate.Release(); }

            await EmitKnowledgeActivity("add_insight", true, $"Added {kind} {id} ({status}, {scope})", id, origin);
            return MaterializeOne(file, scope, id);
        }

        public async Task<InsightRecord> ApproveInsightAsync(string id, string origin)
        {
            await _knowledgeGate.WaitAsync();
            try
            {
                var (file, scope, _) = LocateInsight(id);
                KnowledgeStore.Append(file, new KnowledgeStore.Delta { Op = "approve", Id = id, When = DateTime.UtcNow.ToString("o"), Origin = origin ?? "agent" });
                var rec = MaterializeOne(file, scope, id);
                _ = EmitKnowledgeActivity("approve_insight", true, $"Approved insight {id}", id, origin);
                return rec;
            }
            finally { _knowledgeGate.Release(); }
        }

        public async Task<InsightRecord> EditInsightAsync(string id, string text, string[] keys, string origin)
        {
            if (text == null && keys == null)
                throw new InvalidOperationException("Nothing to edit — provide new text and/or keys.");
            await _knowledgeGate.WaitAsync();
            try
            {
                var (file, scope, _) = LocateInsight(id);
                KnowledgeStore.Append(file, new KnowledgeStore.Delta { Op = "edit", Id = id, When = DateTime.UtcNow.ToString("o"), Origin = origin ?? "agent", Text = text, Keys = keys });
                var rec = MaterializeOne(file, scope, id);
                _ = EmitKnowledgeActivity("edit_insight", true, $"Edited insight {id}", id, origin);
                return rec;
            }
            finally { _knowledgeGate.Release(); }
        }

        public Task<InsightRecord> UpvoteInsightAsync(string id, string origin) => VoteAsync(id, +1, origin);
        public Task<InsightRecord> DownvoteInsightAsync(string id, string origin) => VoteAsync(id, -1, origin);

        // ExpeL importance counter (agent judgment, engine arithmetic): a downvote to <= 0 materializes the insight
        // OUT on the next read — the delta trail is kept, the live set drops it.
        private async Task<InsightRecord> VoteAsync(string id, int delta, string origin)
        {
            await _knowledgeGate.WaitAsync();
            try
            {
                var (file, scope, _) = LocateInsight(id);
                KnowledgeStore.Append(file, new KnowledgeStore.Delta { Op = "vote", Id = id, When = DateTime.UtcNow.ToString("o"), Origin = origin ?? "agent", VoteDelta = delta });
                var rec = MaterializeOne(file, scope, id);   // null once it drops below 1 — the caller sees it's gone
                _ = EmitKnowledgeActivity(delta > 0 ? "upvote_insight" : "downvote_insight", true, $"{(delta > 0 ? "Upvoted" : "Downvoted")} insight {id}" + (rec == null ? " (materialized out)" : $" → score {rec.Score}"), id, origin);
                return rec;
            }
            finally { _knowledgeGate.Release(); }
        }

        public async Task<SetResult> DeleteInsightAsync(string id, string origin)
        {
            await _knowledgeGate.WaitAsync();
            try
            {
                var (file, _, _) = LocateInsight(id);   // throws if it isn't live
                KnowledgeStore.Append(file, new KnowledgeStore.Delta { Op = "delete", Id = id, When = DateTime.UtcNow.ToString("o"), Origin = origin ?? "agent" });
                _ = EmitKnowledgeActivity("delete_insight", true, $"Deleted insight {id}", id, origin);
                return new SetResult { Changed = true };
            }
            finally { _knowledgeGate.Release(); }
        }

        /// <summary>Scoped one-op purge (§6 MemoryGraft mitigation). Review→Apply shape: confirm=false returns the
        /// live count that WOULD be erased and changes nothing; confirm=true appends the purge marker (deltas before
        /// it in that scope become invisible on replay — the file is not rewritten).</summary>
        public async Task<PurgeResult> PurgeKnowledgeAsync(string scope, bool confirm, string origin)
        {
            scope = NormalizeScope(scope);
            var file = ScopeFile(scope);
            await _knowledgeGate.WaitAsync();
            try
            {
                var (live, _) = KnowledgeStore.Materialize(file, scope);
                if (!confirm)
                    return new PurgeResult { Scope = scope, LiveCount = live.Count, Purged = false, Note = $"DRY RUN — {live.Count} live insight(s) in the '{scope}' scope would be purged. Re-run with confirm=true to erase them." };
                KnowledgeStore.Append(file, new KnowledgeStore.Delta { Op = "purge", When = DateTime.UtcNow.ToString("o"), Origin = origin ?? "agent" });
                _ = EmitKnowledgeActivity("purge_knowledge", true, $"Purged {live.Count} insight(s) from the '{scope}' knowledge store", scope, origin);
                return new PurgeResult { Scope = scope, LiveCount = live.Count, Purged = true, Note = $"Purged {live.Count} live insight(s) from the '{scope}' scope." };
            }
            finally { _knowledgeGate.Release(); }
        }

        // ---- reads (free) --------------------------------------------------------------------------------

        public Task<InsightListResult> ListInsightsAsync(string scope, string status)
        {
            var scopes = string.IsNullOrEmpty(scope) ? new[] { "project", "global" } : new[] { NormalizeScope(scope) };
            var all = new List<InsightRecord>();
            int skipped = 0;
            foreach (var sc in scopes)
            {
                var (live, sk) = KnowledgeStore.Materialize(ScopeFile(sc), sc);
                all.AddRange(live);
                skipped += sk;
            }
            if (!string.IsNullOrEmpty(status))
                all = all.Where(r => string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult(new InsightListResult
            {
                Insights = all.ToArray(),
                SkippedCorruptLines = skipped,
                Note = skipped > 0 ? $"{skipped} corrupt line(s) were skipped (the store is not bricked; the rest replayed cleanly)." : null,
            });
        }

        public async Task<ModelFingerprint> GetModelFingerprintAsync()
        {
            var s = _sessions.Require();
            return await s.ReadAsync(m => KnowledgeStore.ComputeFingerprint(m));
        }

        /// <summary>L2 recall (§3.4): compute the OPEN model's fingerprint, read BOTH scopes' APPROVED insights, and
        /// rank DETERMINISTICALLY — the agent does the semantic ranking over the candidate set we return. Ranking:
        ///   rank = (keyOverlap*3 + domainOverlap*1 + fingerprintBonus(5 if same FingerprintKey) + score) * 0.98^daysSinceLastUse
        /// key-term lexical overlap with the query dominates (weight 3), the strong same-shape match adds 5, the weak
        /// shared-domain-token signal adds 1 each, the ExpeL importance counter feeds in, and temporal decay (SSGM
        /// read-filtering) discounts stale insights. Each RETURNED insight gets a `retrieve` delta (counter bump).</summary>
        public async Task<RecallResult> RecallExperienceAsync(string query, int maxResults)
        {
            var s = _sessions.Require();
            var fp = await s.ReadAsync(m => KnowledgeStore.ComputeFingerprint(m));
            var cap = maxResults <= 0 ? 12 : maxResults;

            var qTokens = KnowledgeStore.Words(query).ToHashSet(StringComparer.Ordinal);
            var domainSet = new HashSet<string>(fp.DomainTokens, StringComparer.Ordinal);
            var now = DateTime.UtcNow;

            var candidates = new List<(RecallCandidate cand, string file)>();
            int skipped = 0;
            foreach (var sc in new[] { "project", "global" })
            {
                var file = ScopeFile(sc);
                var (live, sk) = KnowledgeStore.Materialize(file, sc);
                skipped += sk;
                foreach (var r in live.Where(r => string.Equals(r.Status, "approved", StringComparison.Ordinal)))
                {
                    var keyTokens = (r.Keys ?? Array.Empty<string>()).SelectMany(KnowledgeStore.Words).ToHashSet(StringComparer.Ordinal);
                    var matched = (r.Keys ?? Array.Empty<string>())
                        .Where(k => KnowledgeStore.Words(k).Any(qTokens.Contains)).ToArray();
                    int domainOverlap = keyTokens.Count(domainSet.Contains);
                    bool fpMatch = r.Fingerprint != null && string.Equals(r.Fingerprint, fp.FingerprintKey, StringComparison.Ordinal);

                    double decay = Math.Pow(0.98, DaysSince(r.LastUsedUtc, now));
                    double rank = (matched.Length * 3 + domainOverlap * 1 + (fpMatch ? 5 : 0) + r.Score) * decay;

                    var why = new List<string>();
                    if (matched.Length > 0) why.Add($"matched keys: {string.Join(", ", matched)}");
                    if (fpMatch) why.Add("same model shape (fingerprint)");
                    if (domainOverlap > 0) why.Add($"{domainOverlap} shared domain term(s)");
                    if (why.Count == 0) why.Add("general (score/recency only)");

                    candidates.Add((new RecallCandidate
                    {
                        Insight = r, MatchedKeys = matched, FingerprintMatch = fpMatch,
                        DomainOverlap = domainOverlap, Rank = Math.Round(rank, 4), Why = string.Join("; ", why),
                    }, file));
                }
            }

            var top = candidates
                .OrderByDescending(c => c.cand.Rank)
                .ThenByDescending(c => c.cand.Insight.LastUsedUtc, StringComparer.Ordinal)
                .ThenBy(c => c.cand.Insight.Id, StringComparer.Ordinal)
                .Take(cap).ToList();

            // Retrieval counter bump (a delta append) for each returned insight — reflect it in the returned record.
            await _knowledgeGate.WaitAsync();
            try
            {
                foreach (var (cand, file) in top)
                {
                    KnowledgeStore.Append(file, new KnowledgeStore.Delta { Op = "retrieve", Id = cand.Insight.Id, When = now.ToString("o"), Origin = "engine" });
                    cand.Insight.Retrievals++;
                }
            }
            finally { _knowledgeGate.Release(); }

            return new RecallResult
            {
                Candidates = top.Select(t => t.cand).ToArray(),
                Fingerprint = fp,
                SkippedCorruptLines = skipped,
                RankingNote = "Deterministic retrieval only — rank = (keyOverlap*3 + domainOverlap + fingerprintBonus(5) + score) * 0.98^daysSinceLastUse. You do the semantic ranking over these candidates.",
                Note = top.Count == 0 ? "No prior experience for this model shape — the knowledge store has no approved, matching insights yet." : null,
            };
        }

        private static double DaysSince(string isoUtc, DateTime now)
        {
            if (DateTime.TryParse(isoUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                return Math.Max(0, (now - t.ToUniversalTime()).TotalDays);
            return 0;
        }

        private Task EmitKnowledgeActivity(string kind, bool ok, string label, string target, string origin)
            => PublishActivityAsync(new ActivityEvent { Kind = kind, Origin = string.IsNullOrWhiteSpace(origin) ? "agent" : origin, Label = label, Target = target, Ok = ok });
    }
}
