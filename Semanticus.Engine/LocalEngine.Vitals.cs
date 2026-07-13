using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    // ================================================================================================
    // The number time-machine (feature #3) — the engine half. Ambient vital-signs capture rides the
    // Verified-Edits audit chokepoint (apply_plan / optimize_measure / deploy_live) plus an explicit
    // save_model hook; blame_value / list_value_history answer "what moved this number?" over the
    // persisted history. PRO with a SOFT gate: free tier gets Status="pro" + a plain invitation (never
    // a throw), and ambient capture silently skips. Capture is best-effort by design — it must never
    // fail or slow-fail the op it rides beyond its own time budget. Pure logic: Connect/VitalSigns.cs.
    // ================================================================================================
    public sealed partial class LocalEngine
    {
        private const int VitalsTopN = 25;            // measures per checkpoint (report-usage/centrality ranked)
        private const int VitalsConeCap = 64;         // per-measure dependency-cone walk cap
        private const int VitalsEntryCap = 80;        // total formula snapshots per record (top-N + pulled-in cone)
        private const int VitalsDimRowCap = 100;      // rows read per dominant-dim probe (only the top cell is kept)
        private const int VitalsValueBudgetMs = 15000;// wall-clock budget for the value half of one capture
        private const int VitalsOverlapRootCap = 16;  // changed refs walked per candidate when scoring overlap

        private const string VitalsProInvite =
            "See what moved a number, automatically, with Pro. You can still compare snapshots by hand: " +
            "capture a baseline before an edit and compare after — free (capture_baseline / compare_baseline).";

        /// <summary>Ambient capture is HOST-ATTACHED (the ExperienceTee precedent): only the owner host
        /// (Program.Serve / owner-mode MCP) enables it, so engine instances in tests never write sidecar
        /// files beside fixture models unless a test opts in deliberately.</summary>
        public bool AmbientVitalsEnabled { get; set; }

        // Report-usage rank cache (one PBIR parse per source path per session — parsing every checkpoint
        // would tax the very ops the capture rides). Best-effort: no reports = empty rank = centrality only.
        private string _vitalsRankKey;
        private Dictionary<string, int> _vitalsRank;

        private string VitalsFileFor(Session s)
        {
            var anchored = !ExperienceStore.IsEphemeralAnchor(s.SourcePath) ? LayoutStore.DirFor(s.SourcePath) : null;
            if (anchored != null)
                return Path.Combine(anchored, VitalsStore.SubDir, VitalsStore.FileName);
            // Pre-existing (predates this PR): the workspace fallback shares ONE vitals.jsonl across every live
            // model opened under that workspace — retention prunes across models and reads lean on the record
            // fingerprint filter. Left as-is for scope; the GLOBAL fallback below is per-fingerprint.
            if (_workspaceDir != null)
                return Path.Combine(_workspaceDir, LayoutStore.DirName, VitalsStore.SubDir, VitalsStore.FileName);
            // GLOBAL fallback — ephemeral anchor (live/XMLA/unsaved) AND no workspace open: a live model with no
            // folder, the highest-value Pro case, where capture used to be silently DROPPED (dir == null). Mirror
            // the SENSITIVE half of PR #112 (its project-insight store), NOT experience.jsonl: a PER-FINGERPRINT
            // file under the shared %USERPROFILE%/.semanticus home, so MaxRecords/MaxBytes retention stays PER-MODEL
            // (a busy model never prunes another's history) and one model's file can never be read as another's —
            // cross-contamination closed by construction, not by a read-time filter. A session with NO stable
            // identity (ephemeral + no LiveOrigin → fingerprint null) can't be keyed OR read back reliably, so it is
            // NOT written to the global store at all (skip): a no-regression (it was dropped before) that guarantees
            // zero null-fingerprint pollution of any model's history.
            var fp = VitalsFingerprintFor(s);
            return fp == null ? null
                : Path.Combine(ExperienceStore.GlobalHomeDir(), VitalsStore.SubDir, VitalsStore.GlobalFileName(fp));
        }

        private string VitalsFingerprintFor(Session s)
            => s.LiveOrigin != null ? ExperienceStore.FingerprintForLive(s.LiveOrigin)
             : !ExperienceStore.IsEphemeralAnchor(s.SourcePath) ? ExperienceStore.FingerprintFor(s.SourcePath)
             : null;

        /// <summary>Is this audit record a vital-signs checkpoint moment? Only records backed by a real
        /// change qualify: an applied plan / applied optimize (Revision&gt;0) or a COMMITTED deploy (the
        /// moment the live numbers actually move). Per-item override records and no-op verdicts don't.</summary>
        internal static bool IsVitalsCheckpoint(VerifiedEditRecord rec) =>
            (string.Equals(rec.Op, "apply_plan", StringComparison.Ordinal) && rec.Revision > 0)
            || (string.Equals(rec.Op, "optimize_measure", StringComparison.Ordinal) && rec.Revision > 0)
            || (string.Equals(rec.Op, "deploy_live", StringComparison.Ordinal) && string.Equals(rec.Verdict, "deployed", StringComparison.Ordinal));

        /// <summary>Does the live model provably reflect the session's state at this checkpoint? ONLY right
        /// after a committed deploy (the live model was just synced FROM this session). An apply/optimize
        /// checkpoint has by definition just mutated the session, so the live connection still serves the
        /// PREVIOUS deployed state — its numbers must never be paired with this record's expressions.</summary>
        internal static bool VitalsLiveReflectsSession(VerifiedEditRecord rec) =>
            string.Equals(rec.Op, "deploy_live", StringComparison.Ordinal)
            && string.Equals(rec.Verdict, "deployed", StringComparison.Ordinal);

        /// <summary>Ambient vital-signs capture at a checkpoint moment. Pro-only (SILENT skip otherwise —
        /// never an upsell mid-op), host-enabled, dry-run-suppressed, never throws. The expression half
        /// (the measure cones' DAX) is metadata and captures even OFFLINE; the value half is observed ONLY
        /// when <paramref name="liveReflectsSession"/> (a committed deploy — see VitalsLiveReflectsSession):
        /// at that moment every live number can move, so all top-N measures are read. Edit/save checkpoints
        /// record ValuesSkippedStale instead — a live model that lags the session's edits would pair this
        /// record's expressions with a different model state's numbers (review finding, PR #86).</summary>
        internal async Task CaptureVitalsAsync(Session s, string op, string origin, string[] changedRefs, bool liveReflectsSession)
        {
            try
            {
                if (!AmbientVitalsEnabled) return;
                if (_entitlement == null || !_entitlement.IsPro) return;   // soft gate: free = silently skipped
                if (DryRunScope.Current != null) return;                   // a rehearsal never leaves a record
                if (s == null) return;
                var file = VitalsFileFor(s);
                if (file == null) return;                                  // no durable anchor and no workspace — nothing to write beside

                var rank = await VitalsReportRankAsync(s).ConfigureAwait(false);
                var refs = (changedRefs ?? Array.Empty<string>()).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.Ordinal).ToArray();
                // captureAll follows liveReflectsSession: the deploy checkpoint (the only live-matching moment)
                // moves every live number, so all top-N are value targets; edit checkpoints keep the FABLE-a
                // cone-scoped target set (currently skipped as stale — ready for a future synced-live case).
                var snap = await s.ReadAsync(m => VitalSigns.Snapshot(m, refs, rank, VitalsTopN, VitalsConeCap, VitalsEntryCap, captureAllValues: liveReflectsSession)).ConfigureAwait(false);

                var rec = new VitalsRecord
                {
                    When = DateTime.UtcNow.ToString("o"),
                    SessionId = s.Id,
                    Revision = s.Revision,
                    Op = op,
                    Origin = origin ?? "system",
                    ModelFingerprint = VitalsFingerprintFor(s),
                    ChangedRefs = refs,
                    Measures = snap.Entries.ToArray(),
                    ConeTruncated = snap.ConeTruncated,
                };

                var live = _live;
                switch (VitalSigns.DecideValueCapture(live != null, liveReflectsSession, snap.ValueTargets.Count))
                {
                    case VitalsValueDecision.SkipOffline:
                        rec.ValuesSkippedOffline = true;
                        break;
                    case VitalsValueDecision.SkipStale:
                        // Live is up, but it serves the PREVIOUS deployed state (this checkpoint just mutated
                        // the session) — observing it would weld wrong numbers onto this record's expressions.
                        rec.ValuesSkippedStale = true;
                        break;
                    case VitalsValueDecision.Observe:
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var byRef = rec.Measures.ToDictionary(m => m.Ref, StringComparer.Ordinal);
                        foreach (var (tref, name) in snap.ValueTargets)
                        {
                            if (sw.ElapsedMilliseconds > VitalsValueBudgetMs) { rec.ValuesTruncated = true; break; }
                            if (!byRef.TryGetValue(tref, out var entry)) continue;
                            entry.Contexts = await CaptureVitalsCellsAsync(live, name, snap.DominantDims, sw, rec).ConfigureAwait(false);
                        }
                        break;
                }

                VitalsStore.Append(file, rec);
            }
            catch { /* ambient ride-along — never fail the checkpoint op */ }
        }

        // The observed cells for one measure: the grand total + the dominant slice of up to 3 central
        // dimensions (top |value| member — a deterministic, self-describing key like "Product[Category]=Audio").
        // Per-cell failures are skipped (an ABSENT cell means "not observed", honestly), budget-bounded.
        private async Task<VitalsCell[]> CaptureVitalsCellsAsync(LiveConnection live, string measureName,
            List<string> dims, System.Diagnostics.Stopwatch sw, VitalsRecord rec)
        {
            var cells = new List<VitalsCell>();
            var expr = Baseline.MeasureRefExpr(measureName);
            try
            {
                var rs = await live.ExecuteAsync(DaxBench.BuildProbeQuery(expr, Array.Empty<string>(), Array.Empty<string>()), 2, 60).ConfigureAwait(false);
                if (string.IsNullOrEmpty(rs.Error))
                {
                    var rows = Baseline.KeyRows(rs, 0);
                    rows.TryGetValue(VitalSigns.TotalContext, out var total);   // no row at all = BLANK total
                    cells.Add(new VitalsCell { Key = VitalSigns.TotalContext, Value = total });
                }
            }
            catch { /* skip the cell */ }

            foreach (var dim in dims.Take(3))
            {
                if (sw.ElapsedMilliseconds > VitalsValueBudgetMs) { rec.ValuesTruncated = true; break; }
                try
                {
                    var rs = await live.ExecuteAsync(DaxBench.BuildProbeQuery(expr, new[] { dim }, Array.Empty<string>()), VitalsDimRowCap, 60).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(rs.Error)) continue;
                    var rows = Baseline.KeyRows(rs, 1);
                    string bestKey = null; double bestAbs = double.NegativeInfinity;
                    foreach (var kv in rows.OrderBy(k => k.Key, StringComparer.Ordinal))
                    {
                        if (!IsNum(kv.Value)) continue;
                        var abs = Math.Abs(Convert.ToDouble(kv.Value, System.Globalization.CultureInfo.InvariantCulture));
                        if (abs > bestAbs) { bestAbs = abs; bestKey = kv.Key; }
                    }
                    if (bestKey != null) cells.Add(new VitalsCell { Key = bestKey, Value = rows[bestKey] });
                }
                catch { /* skip the cell */ }
            }
            return cells.ToArray();
        }

        // Report-usage rank for the top-N pick: discover sibling "*.Report" PBIP folders beside the open
        // model, parse them ONCE per source path, and count how many reports use each measure. Falls back
        // to an empty rank (dependency centrality decides) — the coverage caveat is inherent, never
        // overstated: rank only ever PROMOTES measures we can see reports using (gap 5).
        private async Task<Dictionary<string, int>> VitalsReportRankAsync(Session s)
        {
            var key = s.SourcePath ?? "";
            if (_vitalsRankKey == key && _vitalsRank != null) return _vitalsRank;
            var rank = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                var reportDirs = DiscoverSiblingReports(s.SourcePath);
                if (reportDirs.Count > 0)
                {
                    var parsed = await Task.Run(() => reportDirs
                        .Select(p => (path: p, result: Lineage.ReportDefinitionReader.ReadLocalPbir(p)))
                        .ToList()).ConfigureAwait(false);
                    var analysis = await s.ReadAsync(m => Lineage.LineageGraph.AnalyzeReports(m, parsed)).ConfigureAwait(false);
                    foreach (var rep in analysis.Reports.Where(r => r.Read))
                        foreach (var used in rep.UsedRefs ?? Array.Empty<string>())
                        {
                            rank.TryGetValue(used, out var c);
                            rank[used] = c + 1;
                        }
                }
            }
            catch { /* rank is an optimization, not a contract */ }
            _vitalsRank = rank;
            _vitalsRankKey = key;
            return rank;
        }

        /// <summary>PBIP layout discovery: the model's project root usually holds sibling
        /// <c>&lt;name&gt;.Report</c> folders. Anything else (flat .bim / plain TMDL) has no known reports.</summary>
        private static List<string> DiscoverSiblingReports(string sourcePath)
        {
            var dirs = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(sourcePath)) return dirs;
                var full = Path.GetFullPath(sourcePath);
                var modelDir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(modelDir)) return dirs;
                if (ModelPathResolver.IsPbipDefinitionFolder(modelDir))
                    modelDir = Directory.GetParent(modelDir)?.FullName ?? modelDir;   // definition/ → .SemanticModel
                var root = Directory.GetParent(modelDir)?.FullName;                   // .SemanticModel → project root
                if (root == null) return dirs;
                dirs.AddRange(Directory.EnumerateDirectories(root, "*.Report").OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Take(8));
            }
            catch { }
            return dirs;
        }

        // ---- the ops (both doors; SOFT Pro gate) ---------------------------------------------------

        /// <summary>"What moved this number?" — deterministic attribution over the recorded vital-signs
        /// history. Verdict semantics (honesty non-negotiable): a single recorded edit in the movement
        /// window is "attributed"; multiple are an "interval" with candidates RANKED by dependency overlap
        /// (never a causal claim); identical formulas with a moved value is "data-suspected" (proof needs
        /// the deferred shadow pre-edit instance — the Note says so); no usable history is "inconclusive".</summary>
        public async Task<BlameResult> BlameValueAsync(string measureRef, string context, string sinceCheckpoint, string origin)
        {
            var s = _sessions.Require();
            if (_entitlement == null || !_entitlement.IsPro)
                return new BlameResult { Status = "pro", Verdict = "pro", MeasureRef = measureRef, Note = VitalsProInvite };
            if (string.IsNullOrWhiteSpace(measureRef))
                return new BlameResult { Status = "error", Verdict = "inconclusive", Note = "measureRef is required — the measure whose number moved (e.g. 'measure:Sales/Total Sales')." };

            // Canonicalize against the open model when it still resolves; a DELETED measure's history is
            // still queryable by the ref it was recorded under.
            var canonical = await s.ReadAsync(m =>
            {
                try { return ObjectRefs.Resolve(m, measureRef) is Measure mm ? ObjectRefs.For(mm) : null; }
                catch { return null; }
            }) ?? measureRef.Trim();

            var (records, unreadable) = VitalsStore.Read(VitalsFileFor(s), VitalsFingerprintFor(s));
            var ctx = string.IsNullOrWhiteSpace(context) ? VitalSigns.TotalContext : context.Trim();
            var core = ValueBlame.Analyze(records, canonical, ctx, sinceCheckpoint);

            var result = new BlameResult { Status = "ok", MeasureRef = canonical, Context = ctx };
            var name = RefDisplayName(canonical);

            if (core.Inconclusive != null)
            {
                result.Verdict = "inconclusive";
                result.Note = core.Inconclusive + (unreadable > 0 ? $" ({unreadable} unreadable history line(s) were skipped.)" : "");
                return result;
            }

            result.FromCheckpoint = CheckpointOf(core.From);
            result.ToCheckpoint = CheckpointOf(core.To);
            result.Before = DaxBench.Fmt(core.Before);
            result.After = DaxBench.Fmt(core.After);
            result.ExprDiffs = core.ExprDiffs.ToArray();
            result.UntrackedEdits = Math.Max(0, core.UntrackedEdits);

            // ---- FABLE-b overlap scores on the CURRENT model: |Impact(edit.changedRefs) ∩ ({measure} ∪ cone)| ----
            var target = core.ConeRefs;
            var candidates = new List<BlameCandidate>();
            foreach (var w in core.Window)
            {
                var score = 0;
                var wrefs = (w.ChangedRefs ?? Array.Empty<string>()).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.Ordinal).Take(VitalsOverlapRootCap).ToArray();
                if (wrefs.Length > 0)
                {
                    score = await s.ReadAsync(m =>
                    {
                        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var r in wrefs)
                        {
                            reached.Add(r);
                            try { foreach (var n in Lineage.LineageGraph.Impact(m, r).Impacted) reached.Add(n.Ref); }
                            catch { /* deleted root — its recorded ref may still intersect directly */ }
                        }
                        return reached.Count(x => target.Contains(x));
                    });
                }
                candidates.Add(new BlameCandidate
                {
                    SessionId = w.SessionId,
                    Revision = w.Revision,
                    When = w.When,
                    Op = w.Op,
                    Origin = w.Origin,
                    ChangedRefs = wrefs,
                    OverlapScore = score,
                    FormulaChanged = core.CandidateFormulaChanged.TryGetValue(w, out var fc) && fc,
                });
            }
            result.Candidates = candidates
                .OrderByDescending(c => c.FormulaChanged)
                .ThenByDescending(c => c.OverlapScore)
                .ThenByDescending(c => c.When, StringComparer.Ordinal)
                .ToArray();

            // ---- the verdict (FABLE-c) ----
            string note;
            if (!core.FormulaChanged && result.Candidates.All(c => c.OverlapScore == 0 && !c.FormulaChanged))
            {
                result.Verdict = "data-suspected";
                result.Cause = "data-suspected";
                note = $"Every formula behind {name} is identical at both points and none of the recorded edits touches its dependencies — " +
                       "the data itself most likely changed (a refresh or a source change), or a structural change wasn't recorded. " +
                       "Proving a data cause needs a pre-edit shadow copy of the model, which v1 does not keep — treat this as a suspicion, not a finding.";
            }
            else if (result.Candidates.Length == 1)
            {
                result.Verdict = "attributed";
                var c = result.Candidates[0];
                result.Cause = (c.FormulaChanged || core.FormulaChanged) ? "formula" : "structural";
                note = result.Cause == "formula"
                    ? $"{name} moved from {result.Before} to {result.After}, and exactly one recorded edit sits between the two points ({c.Op}, revision {c.Revision}) — a formula in this number's dependency tree changed (see the diff)."
                    : $"{name} moved from {result.Before} to {result.After}, and exactly one recorded edit sits between the two points ({c.Op}, revision {c.Revision}) — it touched this number's dependencies without changing a formula (e.g. a relationship or structure change).";
            }
            else
            {
                result.Verdict = "interval";
                note = $"{name} moved from {result.Before} to {result.After}. {result.Candidates.Length} recorded edits sit in that window, " +
                       "ranked by how much each one touches this number's dependencies (formula changes first) — the top one is the most likely cause. " +
                       "This is a ranking over the window, NOT a causal claim.";
            }
            if (result.UntrackedEdits > 0)
                note += $" {result.UntrackedEdits} more session edit(s) happened inside this window without a recorded snapshot — they are candidates too.";
            if (core.UntrackedEdits < 0)
                note += " The window spans two sessions, so edits between them may not all be recorded.";
            if (unreadable > 0)
                note += $" ({unreadable} unreadable history line(s) were skipped.)";
            result.Note = note;

            // ---- shared evidence (the compare_baseline precedent): ONE shape → audit + L0 tee + UI ----
            await RecordVerifiedEditAsync(s, new VerifiedEditRecord
            {
                SessionId = s.Id, Revision = 0, Origin = origin ?? "agent", Op = "blame_value", ObjectRef = canonical,   // 0: analysis mutates nothing
                Verdict = result.Verdict,
                Summary = Truncate(note, 400),
                Evidence = System.Text.Json.JsonSerializer.Serialize(new
                {
                    context = ctx,
                    from = result.FromCheckpoint, to = result.ToCheckpoint,
                    before = result.Before, after = result.After,
                    cause = result.Cause,
                    formulaChanged = core.FormulaChanged,
                    untrackedEdits = result.UntrackedEdits,
                    candidates = result.Candidates.Take(8).Select(c => new { c.Revision, c.When, c.Op, c.Origin, c.OverlapScore, c.FormulaChanged, changedRefs = c.ChangedRefs.Take(8) }).ToArray(),
                    exprDiffs = result.ExprDiffs.Take(3).Select(d => new { d.Ref, before = Truncate(d.Before, 1500), after = Truncate(d.After, 1500) }).ToArray(),
                }),
            });
            try
            {
                // The Label surfaces in the UI's live activity chip — plain words only (the raw verdict
                // token stays on the Result for the panel/agent).
                await PublishActivityAsync(new ActivityEvent
                {
                    Kind = "blame_value", Origin = origin ?? "agent",
                    Label = $"What moved {name}? — {PlainVerdict(result.Verdict)}", Target = canonical, Ok = true, Result = result,
                });
            }
            catch { /* evidence is a ride-along */ }
            return result;
        }

        /// <summary>The observed values of one measure+context across every recorded checkpoint — the
        /// sparkline feed. Null-valued points are honest gaps (offline capture, or the edit's cone never
        /// reached the measure at that moment).</summary>
        public async Task<ValueHistoryResult> ListValueHistoryAsync(string measureRef, string context)
        {
            var s = _sessions.Require();
            if (_entitlement == null || !_entitlement.IsPro)
                return new ValueHistoryResult { Status = "pro", MeasureRef = measureRef, Note = VitalsProInvite };
            if (string.IsNullOrWhiteSpace(measureRef))
                return new ValueHistoryResult { Status = "error", Note = "measureRef is required (e.g. 'measure:Sales/Total Sales')." };

            var canonical = await s.ReadAsync(m =>
            {
                try { return ObjectRefs.Resolve(m, measureRef) is Measure mm ? ObjectRefs.For(mm) : null; }
                catch { return null; }
            }) ?? measureRef.Trim();

            var (records, unreadable) = VitalsStore.Read(VitalsFileFor(s), VitalsFingerprintFor(s));
            var ctx = string.IsNullOrWhiteSpace(context) ? VitalSigns.TotalContext : context.Trim();
            var series = ValueBlame.Series(records, canonical, ctx);
            var pruned = records.Sum(r => r.Pruned);
            var result = new ValueHistoryResult
            {
                Status = "ok",
                MeasureRef = canonical,
                Context = ctx,
                Points = series.Select(p => new ValueHistoryPoint
                {
                    SessionId = p.Rec.SessionId,
                    Revision = p.Rec.Revision,
                    When = p.Rec.When,
                    Value = p.HasValue ? DaxBench.Fmt(p.Value) : null,
                    CheckpointOp = p.Rec.Op,
                }).ToArray(),
                Truncated = pruned > 0 || unreadable > 0,
            };
            var notes = new List<string>();
            if (series.Count == 0) notes.Add("No recorded history for this measure yet — history builds up automatically at apply/optimize/deploy/save moments (Pro).");
            if (pruned > 0) notes.Add($"Older history was pruned by retention ({pruned} point(s) dropped — the store keeps the most recent {VitalsStore.MaxRecords} points / {VitalsStore.MaxBytes / (1024 * 1024)}MB).");
            if (unreadable > 0) notes.Add($"{unreadable} unreadable history line(s) were skipped.");
            if (series.Count > 0 && series.All(p => !p.HasValue)) notes.Add("Formulas were recorded, but no values have been observed yet — numbers are read at deploy moments, when the live model actually reflects the edits (or not at all without a live connection).");
            result.Note = notes.Count > 0 ? string.Join(" ", notes) : null;
            return result;
        }

        private static BlameCheckpointInfo CheckpointOf(VitalsRecord r) => r == null ? null : new BlameCheckpointInfo
        {
            SessionId = r.SessionId,
            Revision = r.Revision,
            When = r.When,
            Op = r.Op,
        };

        private static string PlainVerdict(string v) => v switch
        {
            "attributed" => "likely cause found",
            "interval" => "candidates ranked",
            "data-suspected" => "the data may have changed",
            "inconclusive" => "not enough history",
            _ => v,
        };

        private static string RefDisplayName(string objRef)
        {
            if (string.IsNullOrEmpty(objRef)) return objRef;
            var rest = objRef.Substring(objRef.IndexOf(':') + 1);
            var slash = rest.LastIndexOf('/');
            return "[" + (slash >= 0 ? rest.Substring(slash + 1) : rest) + "]";
        }

        private static string Truncate(string s, int max)
            => s == null ? null : s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
