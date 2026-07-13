using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    // ============================================================================================
    // The number time-machine (feature #3). At CHECKPOINT MOMENTS (apply_plan / optimize_measure /
    // deploy_live / save_model) the engine ambiently appends one "vital signs" record to
    // `.semanticus/baselines/vitals.jsonl`: the top-N most important measures' EXPRESSION snapshots
    // (their whole DAX dependency cone — cheap metadata, captured even OFFLINE) plus, when live and
    // when the edit's impact cone actually reaches a measure (FABLE-a), a few observed VALUES per
    // measure (grand total + up to 3 dominant-dimension cells). blame_value then answers "what moved
    // this number?" deterministically over that history. Honesty rails (FABLE-c, non-negotiable):
    //  • single-edit window  → "attributed" (the one edit in the window);
    //  • multi-edit window   → "interval" — the candidates are RANKED by dependency overlap
    //    (FABLE-b), never presented as a causal proof;
    //  • expressions identical but the value moved → "data-suspected" — proving a data cause needs
    //    the deferred shadow pre-edit instance, and the Note says so;
    //  • not enough history  → "inconclusive", never a guess.
    // Values are LIVE observations: a session edit shows up in the numbers only once deployed (the
    // same disclosure compare_baseline makes) — the expression half is the session's truth.
    // ============================================================================================

    // ---- wire DTOs (both doors) -------------------------------------------------------------------

    /// <summary>One recorded edit inside the blame window, ranked by how much it plausibly touches the
    /// moved number (FABLE-b: |Impact(edit.changedRefs) ∩ ({measure} ∪ its DAX cone)|; formula-changing
    /// edits rank above data-only ones). A ranking, NEVER a causal claim when the window has >1 edit.</summary>
    public sealed class BlameCandidate
    {
        public string SessionId { get; set; }
        public long Revision { get; set; }
        public string When { get; set; }
        public string Op { get; set; }
        public string Origin { get; set; }
        public string[] ChangedRefs { get; set; } = Array.Empty<string>();
        public int OverlapScore { get; set; }
        public bool FormulaChanged { get; set; }
    }

    /// <summary>A checkpoint endpoint of the blame window.</summary>
    public sealed class BlameCheckpointInfo
    {
        public string SessionId { get; set; }
        public long Revision { get; set; }
        public string When { get; set; }
        public string Op { get; set; }
    }

    /// <summary>A formula that changed inside the measure's dependency cone between the two window
    /// endpoints — the deterministic "formula-caused" evidence (expression text capped at write time).</summary>
    public sealed class BlameExprDelta
    {
        public string Ref { get; set; }
        public string Before { get; set; }   // null = the object entered the cone at To (new dependency)
        public string After { get; set; }    // null = the object left the cone (dependency removed / deleted)
    }

    public sealed class BlameResult
    {
        public string Status { get; set; }            // ok | pro | error
        public string MeasureRef { get; set; }
        public string Context { get; set; }           // "(model context)" = the grand total
        public string Verdict { get; set; }           // attributed | interval | data-suspected | inconclusive | pro
        public BlameCheckpointInfo FromCheckpoint { get; set; }
        public BlameCheckpointInfo ToCheckpoint { get; set; }
        public string Before { get; set; }            // DaxBench.Fmt of the value at From ("(blank)" = BLANK)
        public string After { get; set; }
        public string Cause { get; set; }             // formula | structural | data-suspected (attributed/data verdicts)
        public BlameCandidate[] Candidates { get; set; } = Array.Empty<BlameCandidate>();
        public BlameExprDelta[] ExprDiffs { get; set; } = Array.Empty<BlameExprDelta>();
        public int UntrackedEdits { get; set; }       // session edits inside the window that no checkpoint recorded
        public string Note { get; set; }
    }

    public sealed class ValueHistoryPoint
    {
        public string SessionId { get; set; }
        public long Revision { get; set; }
        public string When { get; set; }
        public string Value { get; set; }             // DaxBench.Fmt; null = not observed at this point (offline / not reached)
        public string CheckpointOp { get; set; }
    }

    public sealed class ValueHistoryResult
    {
        public string Status { get; set; }            // ok | pro | error
        public string MeasureRef { get; set; }
        public string Context { get; set; }
        public ValueHistoryPoint[] Points { get; set; } = Array.Empty<ValueHistoryPoint>();
        public bool Truncated { get; set; }           // retention pruned older points, or unreadable lines were skipped
        public string Note { get; set; }
    }

    // ---- the persisted record (vitals.jsonl, one JSON line per checkpoint) ------------------------

    /// <summary>One observed cell of a measure: the probe context key ("(model context)" for the grand
    /// total, "Date[Year]=2024" for a dominant slice) and its value (null = the measure evaluated BLANK
    /// — an ABSENT cell, by contrast, means the value was not observed at this checkpoint).</summary>
    public sealed class VitalsCell
    {
        public string Key { get; set; }
        public object Value { get; set; }
    }

    public sealed class VitalsMeasure
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string ExprHash { get; set; }          // sha256 of the FULL expression (survives the text cap)
        public string Expr { get; set; }              // capped at ExprCap chars (disclosed via "…[truncated]")
        public string[] Cone { get; set; }            // the measure's transitive DAX dependencies (refs; top-N entries only)
        public VitalsCell[] Contexts { get; set; } = Array.Empty<VitalsCell>();
    }

    public sealed class VitalsRecord
    {
        public int SchemaVersion { get; set; } = VitalsStore.SchemaVersion;
        public string When { get; set; }
        public string SessionId { get; set; }
        public long Revision { get; set; }
        public string Op { get; set; }                // apply_plan | optimize_measure | deploy_live | save_model
        public string Origin { get; set; }
        public string ModelFingerprint { get; set; }
        public string[] ChangedRefs { get; set; } = Array.Empty<string>();
        public VitalsMeasure[] Measures { get; set; } = Array.Empty<VitalsMeasure>();
        public bool ConeTruncated { get; set; }       // the entry cap cut cone snapshots — formula diffs may be partial
        public bool ValuesTruncated { get; set; }     // the per-capture time budget stopped value reads early
        public bool ValuesSkippedOffline { get; set; }// no live connection — expressions only (honest sparse history)
        public bool ValuesSkippedStale { get; set; }  // live is connected but does NOT reflect the session's edits
                                                      // (undeployed local checkpoint) — reading it would pair this
                                                      // record's expressions with a DIFFERENT model state's numbers
        public int Pruned { get; set; }               // older checkpoints dropped by retention WHEN THIS record was written
    }

    /// <summary>Append-only JSONL persistence with retention (last <see cref="MaxRecords"/> checkpoints
    /// OR <see cref="MaxBytes"/>, oldest pruned first and the prune REPORTED on the record that caused
    /// it). Best-effort like ExperienceStore: a failed write must never fail the op it rides.</summary>
    public static class VitalsStore
    {
        public const int SchemaVersion = 1;
        public const string FileName = "vitals.jsonl";
        public const string SubDir = "baselines";
        public const int MaxRecords = 200;
        public const long MaxBytes = 20L * 1024 * 1024;
        public const int ExprCap = 4000;              // chars per stored expression body (hash keeps the diff signal)

        /// <summary>The GLOBAL per-fingerprint vitals file name (mirrors the insight store's
        /// insights.project.&lt;fp&gt;.jsonl, PR #112). Keying the shared %USERPROFILE% home by model fingerprint
        /// keeps <see cref="MaxRecords"/>/<see cref="MaxBytes"/> retention PER-MODEL — a busy model never prunes
        /// another's history — and makes a cross-model read impossible BY CONSTRUCTION (not a read-time filter).
        /// The fingerprint is a hex hash (ExperienceStore.Fingerprint*), so it needs no separate sanitizing.</summary>
        public static string GlobalFileName(string fingerprint) => $"vitals.{fingerprint}.jsonl";

        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>Cap an expression body for storage; the full-body hash travels separately.</summary>
        public static string CapExpr(string expr)
            => expr == null ? null : expr.Length <= ExprCap ? expr : expr.Substring(0, ExprCap) + " …[truncated]";

        /// <summary>Append <paramref name="rec"/>, pruning oldest lines first when retention would be
        /// exceeded — the prune count is stamped ONTO the record before it is written, so the file is
        /// self-describing. Returns false (never throws) when there is no target or the write fails.</summary>
        public static bool Append(string file, VitalsRecord rec)
        {
            try
            {
                if (string.IsNullOrEmpty(file) || rec == null) return false;
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    var lines = File.Exists(file)
                        ? File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).ToList()
                        : new List<string>();
                    // Serialize once without the prune stamp to measure, then re-stamp if pruning happened.
                    rec.Pruned = 0;
                    var line = JsonSerializer.Serialize(rec, JsonOpts);
                    var pruned = 0;
                    long Size(IEnumerable<string> ls) => ls.Sum(l => (long)Encoding.UTF8.GetByteCount(l) + 1);
                    while (lines.Count > 0 && (lines.Count + 1 > MaxRecords || Size(lines) + Encoding.UTF8.GetByteCount(line) + 1 > MaxBytes))
                    {
                        lines.RemoveAt(0);
                        pruned++;
                    }
                    if (pruned > 0)
                    {
                        rec.Pruned = pruned;
                        line = JsonSerializer.Serialize(rec, JsonOpts);
                    }
                    lines.Add(line);
                    File.WriteAllText(file, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Read every parseable record (chronological append order preserved), filtered to
        /// <paramref name="modelFingerprint"/> when both sides carry one (the workspace-fallback file can
        /// mix models). Unreadable lines are counted, never thrown — the caller discloses them.</summary>
        public static (List<VitalsRecord> Records, int Unreadable) Read(string file, string modelFingerprint)
        {
            var recs = new List<VitalsRecord>();
            var bad = 0;
            try
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return (recs, 0);
                string[] lines;
                lock (Gate) lines = File.ReadAllLines(file);
                foreach (var l in lines)
                {
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    VitalsRecord r = null;
                    try { r = JsonSerializer.Deserialize<VitalsRecord>(l, JsonOpts); } catch { }
                    if (r == null) { bad++; continue; }
                    if (modelFingerprint != null && r.ModelFingerprint != null
                        && !string.Equals(r.ModelFingerprint, modelFingerprint, StringComparison.Ordinal)) continue;
                    recs.Add(r);
                }
            }
            catch { /* best-effort read — an unreadable file is an empty history, disclosed by count */ }
            return (recs, bad);
        }

        /// <summary>Normalize a JSON-round-tripped cell value back to a comparable CLR value (numbers →
        /// double, strings/bools as-is, null = BLANK) so DaxBench.ValuesEqual semantics keep holding.</summary>
        public static object NormalizeValue(object v)
        {
            if (v is JsonElement je)
            {
                switch (je.ValueKind)
                {
                    case JsonValueKind.Null: return null;
                    case JsonValueKind.Number: return je.GetDouble();
                    case JsonValueKind.String: return je.GetString();
                    case JsonValueKind.True: return true;
                    case JsonValueKind.False: return false;
                    default: return je.ToString();
                }
            }
            return v;
        }
    }

    // ---- the model-side snapshot (dispatcher thread; pure over TOM) --------------------------------

    internal sealed class VitalsSnapshot
    {
        public List<VitalsMeasure> Entries = new List<VitalsMeasure>();  // top-N first, then pulled-in cone formulas
        public List<(string Ref, string Name)> ValueTargets = new List<(string, string)>();  // top-N ∩ the edit's impact cone
        public List<string> DominantDims = new List<string>();           // up to 3 "'Table'[Col]" group-by columns
        public bool ConeTruncated;
    }

    /// <summary>The value-half gate's outcome for one capture (see <see cref="VitalSigns.DecideValueCapture"/>).</summary>
    internal enum VitalsValueDecision { Observe, SkipStale, SkipOffline, NoTargets }

    internal static class VitalSigns
    {
        public const string TotalContext = "(model context)";            // Baseline.KeyRows' grand-total key — one convention

        /// <summary>The value-half gate. Values are LIVE observations, so a record may pair them with its
        /// expression snapshot ONLY when the live model provably reflects the session's state — otherwise an
        /// apply/optimize/save checkpoint on an undeployed session would store post-edit formulas next to
        /// pre-deploy numbers, and blame_value would later mis-attribute the movement to the deploy (or call
        /// a formula edit "data-suspected"). Today the only provable moment is the committed-deploy checkpoint
        /// (the live model was just synced FROM this session); every other checkpoint skips the value half and
        /// says why on the record (SkipStale / SkipOffline — disclosed, never silent).</summary>
        public static VitalsValueDecision DecideValueCapture(bool liveConnected, bool liveReflectsSession, int valueTargets)
        {
            if (valueTargets <= 0) return VitalsValueDecision.NoTargets;
            if (!liveConnected) return VitalsValueDecision.SkipOffline;
            if (!liveReflectsSession) return VitalsValueDecision.SkipStale;
            return VitalsValueDecision.Observe;
        }

        /// <summary>The metadata half of a checkpoint capture: pick the top-N measures (report-usage rank
        /// when known, dependency-centrality fallback — deterministic), snapshot each one's expression AND
        /// its DAX dependency cone's expressions (formula blame is a pure text diff between two snapshots),
        /// and decide which measures get VALUES: <paramref name="captureAllValues"/> (a deploy — the live
        /// numbers actually change) or FABLE-a cone-intersection with <paramref name="changedRefs"/>.</summary>
        public static VitalsSnapshot Snapshot(Model m, IReadOnlyList<string> changedRefs,
            IReadOnlyDictionary<string, int> reportRank, int topN, int coneCap, int entryCap, bool captureAllValues)
        {
            var snap = new VitalsSnapshot();

            // ---- top-N selection: report-usage rank ▸ dependents count ▸ visible ▸ name (stable) ----
            var ranked = m.AllMeasures
                .Select(ms => new
                {
                    Measure = ms,
                    Ref = ObjectRefs.For(ms),
                    Reports = reportRank != null && reportRank.TryGetValue(ObjectRefs.For(ms), out var c) ? c : 0,
                    Dependents = SafeRefCount(ms),
                })
                .OrderByDescending(x => x.Reports)
                .ThenByDescending(x => x.Dependents)
                .ThenBy(x => x.Measure.IsHidden ? 1 : 0)
                .ThenBy(x => x.Measure.Name, StringComparer.OrdinalIgnoreCase)
                .Take(topN)
                .ToList();

            // ---- the edit's impact cone (FABLE-a): union of Impact() over each changed ref ----
            var impacted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in (changedRefs ?? Array.Empty<string>()).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.Ordinal).Take(64))
            {
                impacted.Add(r);
                try { foreach (var n in Lineage.LineageGraph.Impact(m, r).Impacted) impacted.Add(n.Ref); }
                catch { /* a deleted/unresolvable root has no offline cone */ }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var coneExtras = new List<(string Ref, IDaxObject Obj)>();
            foreach (var x in ranked)
            {
                var cone = ConeOf(x.Measure, coneCap, out var coneTruncated);
                snap.ConeTruncated |= coneTruncated;
                var entry = new VitalsMeasure
                {
                    Ref = x.Ref,
                    Name = x.Measure.Name,
                    ExprHash = VerifiedEditsStore.Sha256(x.Measure.Expression ?? ""),
                    Expr = VitalsStore.CapExpr(x.Measure.Expression),
                    Cone = cone.Select(c => c.Ref).ToArray(),
                };
                if (seen.Add(x.Ref)) snap.Entries.Add(entry);
                foreach (var c in cone)
                    if (c.Obj != null && !seen.Contains(c.Ref)) coneExtras.Add((c.Ref, c.Obj));
                if (captureAllValues || impacted.Contains(x.Ref))
                    snap.ValueTargets.Add((x.Ref, x.Measure.Name));
            }

            // Pull in the cone members' formulas (dedup, capped) — these are what make a between-checkpoint
            // formula diff DETERMINISTIC even when the moved measure's own body never changed.
            foreach (var (cref, obj) in coneExtras)
            {
                if (snap.Entries.Count >= entryCap) { snap.ConeTruncated = true; break; }
                if (!seen.Add(cref)) continue;
                var expr = ExprOf(obj);
                if (expr == null) continue;   // plain columns/tables carry no formula — they stay cone refs only
                snap.Entries.Add(new VitalsMeasure
                {
                    Ref = cref,
                    Name = (obj as ITabularNamedObject)?.Name,
                    ExprHash = VerifiedEditsStore.Sha256(expr),
                    Expr = VitalsStore.CapExpr(expr),
                });
            }

            // ---- dominant dims: the 3 most relationship-central dimension tables' label columns ----
            snap.DominantDims = DominantDims(m, 3);
            return snap;
        }

        private static int SafeRefCount(Measure ms)
        {
            try { return ms.ReferencedBy?.Count ?? 0; } catch { return 0; }
        }

        /// <summary>The measure's transitive DAX dependency cone (DependsOn closure), deterministic order,
        /// capped. Returns (ref, expr-bearing object or null) pairs — plain columns are cone MEMBERS (the
        /// overlap ranking needs them) but carry no formula snapshot.</summary>
        private static List<(string Ref, IDaxObject Obj)> ConeOf(Measure root, int cap, out bool truncated)
        {
            truncated = false;
            var result = new List<(string, IDaxObject)>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { ObjectRefs.For(root) };
            var queue = new Queue<IDaxDependantObject>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                IEnumerable<IDaxObject> deps;
                try { deps = cur.DependsOn.Keys.ToList(); } catch { continue; }
                foreach (var d in deps.OrderBy(d => ObjectRefs.For(d), StringComparer.Ordinal))
                {
                    var dref = ObjectRefs.For(d);
                    if (string.IsNullOrEmpty(dref) || !seen.Add(dref)) continue;
                    if (result.Count >= cap) { truncated = true; return result; }
                    result.Add((dref, d));
                    if (d is IDaxDependantObject dd) queue.Enqueue(dd);
                }
            }
            return result;
        }

        private static string ExprOf(IDaxObject obj) => obj switch
        {
            Measure ms => ms.Expression,
            CalculatedColumn cc => cc.Expression,
            CalculatedTable ct => ct.Expression,
            Function f => f.Expression,
            _ => null,
        };

        /// <summary>Up to <paramref name="take"/> "'Table'[Column]" group-by columns from the most
        /// relationship-central dimension tables (most relationships pointing AT the table wins; ties by
        /// name). Label column preference: a visible string column, else the relationship key itself.</summary>
        internal static List<string> DominantDims(Model m, int take)
        {
            var byTable = new Dictionary<Table, (int Count, Column Key)>();
            foreach (var r in m.Relationships.OfType<SingleColumnRelationship>())
            {
                var t = r.ToColumn?.Table;
                if (t == null) continue;
                byTable.TryGetValue(t, out var cur);
                byTable[t] = (cur.Count + 1, cur.Key ?? r.ToColumn);
            }
            var dims = new List<string>();
            foreach (var kv in byTable.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key.Name, StringComparer.OrdinalIgnoreCase).Take(take))
            {
                var label = kv.Key.Columns
                    .Where(c => c.Type != ColumnType.RowNumber && !c.IsHidden && c.DataType == DataType.String)
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault() ?? kv.Value.Key;
                if (label == null) continue;
                dims.Add($"'{kv.Key.Name.Replace("'", "''")}'[{label.Name}]");
            }
            return dims;
        }
    }

    // ---- the pure blame analysis (deterministic over the persisted history) ------------------------

    /// <summary>Everything the blame verdict needs that is derivable from the history alone. The engine
    /// layers the CURRENT-model overlap scores (an Impact walk needs live wrapper objects) on top.</summary>
    internal sealed class BlameCore
    {
        public VitalsRecord From;
        public VitalsRecord To;
        public object Before;
        public object After;
        public List<VitalsRecord> Window = new List<VitalsRecord>();   // records after From, up to and incl. To
        public bool FormulaChanged;
        public List<BlameExprDelta> ExprDiffs = new List<BlameExprDelta>();
        public HashSet<string> ConeRefs = new HashSet<string>(StringComparer.Ordinal);  // {measure} ∪ cone at both endpoints
        public Dictionary<VitalsRecord, bool> CandidateFormulaChanged = new Dictionary<VitalsRecord, bool>();
        public int UntrackedEdits;
        public string Inconclusive;                                    // non-null ⇒ verdict "inconclusive" with this reason
    }

    internal static class ValueBlame
    {
        /// <summary>Chronological (record, cell?) series for one measure+context. A point exists for every
        /// checkpoint that SNAPSHOTTED the measure; HasValue only where a value was actually observed.</summary>
        public static List<(VitalsRecord Rec, VitalsMeasure M, bool HasValue, object Value)> Series(
            IReadOnlyList<VitalsRecord> records, string measureRef, string context)
        {
            var pts = new List<(VitalsRecord, VitalsMeasure, bool, object)>();
            foreach (var r in records)
            {
                var m = (r.Measures ?? Array.Empty<VitalsMeasure>()).FirstOrDefault(x => string.Equals(x.Ref, measureRef, StringComparison.OrdinalIgnoreCase));
                if (m == null) continue;
                var cell = (m.Contexts ?? Array.Empty<VitalsCell>()).FirstOrDefault(c => string.Equals(c.Key, context, StringComparison.Ordinal));
                pts.Add((r, m, cell != null, cell == null ? null : VitalsStore.NormalizeValue(cell.Value)));
            }
            return pts;
        }

        /// <summary>The deterministic core: find the movement window (the last two observed values that
        /// differ, or since an explicit checkpoint), list the recorded edits inside it, and diff the
        /// measure's formula cone between the endpoints. Pure — no engine, no live, fully unit-testable.</summary>
        public static BlameCore Analyze(IReadOnlyList<VitalsRecord> records, string measureRef, string context, string sinceCheckpoint)
        {
            var core = new BlameCore();
            var pts = Series(records, measureRef, context);
            if (pts.Count == 0)
            {
                core.Inconclusive = "This number has no recorded history yet — history builds up automatically at apply/optimize/deploy/save moments.";
                return core;
            }
            var valued = pts.Where(p => p.HasValue).ToList();

            if (!string.IsNullOrWhiteSpace(sinceCheckpoint))
            {
                var since = sinceCheckpoint.Trim();
                var fromIdx = valued.FindLastIndex(p =>
                    p.Rec.Revision.ToString() == since
                    || string.Equals(p.Rec.When, since, StringComparison.Ordinal)
                    || (p.Rec.When != null && p.Rec.When.StartsWith(since, StringComparison.Ordinal)));
                if (fromIdx < 0)
                {
                    core.Inconclusive = $"No recorded point matches '{since}' — pass a revision number or an ISO timestamp from list_value_history.";
                    return core;
                }
                if (fromIdx == valued.Count - 1)
                {
                    core.Inconclusive = "That point is the most recent observation — nothing newer to compare it against.";
                    return core;
                }
                var f = valued[fromIdx];
                var t = valued[valued.Count - 1];
                if (DaxBench.ValuesEqual(f.Value, t.Value))
                {
                    core.Inconclusive = $"The value is unchanged since then ({DaxBench.Fmt(t.Value)}).";
                    return core;
                }
                Fill(core, records, f.Rec, t.Rec, f.Value, t.Value, measureRef);
                return core;
            }

            if (valued.Count < 2)
            {
                core.Inconclusive = valued.Count == 1
                    ? "Only one observed value so far — a value history needs at least two live observations to compare (offline points record the formulas but not the numbers)."
                    : "The formulas were recorded, but no VALUES have been observed yet (captures ran without a live connection).";
                return core;
            }

            // Default: the most recent movement — the last adjacent pair of observed values that differ.
            for (var i = valued.Count - 1; i >= 1; i--)
            {
                if (!DaxBench.ValuesEqual(valued[i - 1].Value, valued[i].Value))
                {
                    Fill(core, records, valued[i - 1].Rec, valued[i].Rec, valued[i - 1].Value, valued[i].Value, measureRef);
                    return core;
                }
            }
            core.Inconclusive = $"This number hasn't moved across its {valued.Count} observed points (steady at {DaxBench.Fmt(valued[valued.Count - 1].Value)}).";
            return core;
        }

        private static void Fill(BlameCore core, IReadOnlyList<VitalsRecord> records,
            VitalsRecord from, VitalsRecord to, object before, object after, string measureRef)
        {
            core.From = from; core.To = to; core.Before = before; core.After = after;

            var fromIdx = IndexOf(records, from);
            var toIdx = IndexOf(records, to);
            for (var i = fromIdx + 1; i <= toIdx && i < records.Count; i++)
                core.Window.Add(records[i]);

            // ---- formula diff over {measure} ∪ cone, between the endpoints (deterministic) ----
            var fm = MeasureIn(from, measureRef);
            var tm = MeasureIn(to, measureRef);
            foreach (var r in (fm?.Cone ?? Array.Empty<string>()).Concat(tm?.Cone ?? Array.Empty<string>())) core.ConeRefs.Add(r);
            core.ConeRefs.Add(measureRef);
            var fromExprs = ExprMap(from, core.ConeRefs);
            var toExprs = ExprMap(to, core.ConeRefs);
            foreach (var r in fromExprs.Keys.Union(toExprs.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var hasF = fromExprs.TryGetValue(r, out var f);
                var hasT = toExprs.TryGetValue(r, out var t);
                if (hasF && hasT && string.Equals(f.Hash, t.Hash, StringComparison.Ordinal)) continue;
                core.FormulaChanged = true;
                if (core.ExprDiffs.Count < 6)
                    core.ExprDiffs.Add(new BlameExprDelta { Ref = r, Before = hasF ? f.Expr : null, After = hasT ? t.Expr : null });
            }

            // ---- per-candidate formula flag: did THIS checkpoint's snapshot move the cone's formulas
            //      vs the latest prior snapshot that carried the measure? ----
            var prior = fm;
            var priorRec = from;
            foreach (var w in core.Window)
            {
                var wm = MeasureIn(w, measureRef);
                var changed = false;
                if (wm != null && prior != null)
                {
                    var refs = new HashSet<string>((prior.Cone ?? Array.Empty<string>()).Concat(wm.Cone ?? Array.Empty<string>()), StringComparer.OrdinalIgnoreCase) { measureRef };
                    var a = ExprMap(priorRec, refs);
                    var b = ExprMap(w, refs);
                    changed = a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).Any(r =>
                        !(a.TryGetValue(r, out var x) && b.TryGetValue(r, out var y) && string.Equals(x.Hash, y.Hash, StringComparison.Ordinal)));
                }
                core.CandidateFormulaChanged[w] = changed;
                if (wm != null) { prior = wm; priorRec = w; }
            }

            // ---- untracked-edit honesty: revisions the window spans that no checkpoint describes ----
            if (string.Equals(from.SessionId, to.SessionId, StringComparison.Ordinal) && to.Revision > from.Revision)
            {
                var span = to.Revision - from.Revision;
                var described = core.Window.Count(w => w.Revision > 0);
                core.UntrackedEdits = (int)Math.Max(0, span - described);
            }
            else if (!string.Equals(from.SessionId, to.SessionId, StringComparison.Ordinal))
            {
                core.UntrackedEdits = -1;   // cross-session: the gap size is unknowable — disclosed, not guessed
            }
        }

        private static int IndexOf(IReadOnlyList<VitalsRecord> records, VitalsRecord r)
        {
            for (var i = 0; i < records.Count; i++) if (ReferenceEquals(records[i], r)) return i;
            return -1;
        }

        private static VitalsMeasure MeasureIn(VitalsRecord r, string measureRef)
            => (r.Measures ?? Array.Empty<VitalsMeasure>()).FirstOrDefault(x => string.Equals(x.Ref, measureRef, StringComparison.OrdinalIgnoreCase));

        private static Dictionary<string, (string Hash, string Expr)> ExprMap(VitalsRecord r, ISet<string> refs)
        {
            var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in r.Measures ?? Array.Empty<VitalsMeasure>())
                if (m.Ref != null && refs.Contains(m.Ref) && m.ExprHash != null)
                    map[m.Ref] = (m.ExprHash, m.Expr);
            return map;
        }
    }
}
