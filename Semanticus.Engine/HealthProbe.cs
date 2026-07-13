using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>
    /// Computes the post-commit health delta: readiness GRADE movement (a SCOPED incremental rescan rolled
    /// forward on a per-session memo vs the memoized "before" scorecard — only touched objects re-evaluate for
    /// the scopable rules; the grade/gates/tallies rebuild from the rolled state, proven equal to a full rescan
    /// by ReadinessScopedScanTests), net-new BPA/lint findings on TOUCHED objects only (BPA via the scoped
    /// <see cref="BpaAnalyzer.Analyze(Model, IReadOnlyList{BpaRule}, ICollection{string})"/> overload; DAX lint
    /// arrives through the readiness BestPractice rules), and a blast-radius count via ONE multi-source
    /// <c>LineageGraph.ImpactFrom</c> BFS over ALL of the commit's SEMANTIC changes (no root cap — a truncated
    /// walk could suppress a real blast radius).
    ///
    /// The probe is installed on EVERY session; the Pro gate is checked LAZILY per invocation (SOFT gate — the
    /// free tier costs one bool check and never throws; a license activated mid-session starts reporting on the
    /// next edit). <see cref="ComputeOrNull"/> returns null below the threshold (no grade-letter move, no
    /// net-new Warning+ finding, blast radius 0) so BOTH sinks — the didChange chip and the MCP tool-result
    /// block — key off null.
    ///
    /// Rides Session's tracked-commit path as an <see cref="ISessionObserver"/> (registered by the engine host):
    /// <see cref="EnsureBaseline"/> before the mutation (memoize the pre-edit state once per session),
    /// <see cref="OnCommit"/> after it, BEFORE the broadcast — so the delta rides the same <c>model/didChange</c>
    /// the commit publishes. Dry-runs never reach it (MutateAsync short-circuits rehearsals away from TrackAsync).
    ///
    /// Finding IDENTITY is rename-stable: keys use the object's LineageTag where one exists (see
    /// <see cref="StableRef"/>), so renaming an object moves neither its readiness nor its BPA findings into
    /// "net-new"; tag-less objects fall back to the name path (guarded by the count heuristic below).
    ///
    /// All analysis state is touched ONLY on the dispatcher thread (TrackAsync bodies are serialized); agent
    /// deltas leave through the engine-level <see cref="AgentHealthMailbox"/> (its own lock), stamped with the
    /// producing session + the originating tool call.
    /// </summary>
    public sealed class HealthDeltaProbe : ISessionObserver
    {
        // Properties whose change can alter DOWNSTREAM results/validity — the roots we walk for blast radius.
        // Cosmetic props (Description, DisplayFolder, FormatString, synonyms, …) deliberately excluded: a
        // description edit "affects" nothing downstream, and test (b) pins that a cosmetic edit emits no block.
        // Props == null (structural adds/removes) is treated as semantic — conservative include.
        private static readonly HashSet<string> SemanticProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Expression", "Name", "DataType", "IsActive", "CrossFilteringBehavior",
            "FromCardinality", "ToCardinality", "FromColumn", "ToColumn",
            "FilterExpression", "SourceColumn", "SortByColumn",
        };

        internal const int MaxRuleIds = 12;   // the agent block stays terse; Findings carries the true count (shared with the mailbox merge)
        private const char Sep = '␟';         // RuleId␟StableId key separator (same glyph Bpa.cs dedup uses)

        private readonly Func<Model, IReadOnlyList<BpaRule>> _bpaRules;   // the session's EFFECTIVE rules (standard + model-embedded)
        private readonly Action<ActivityEvent> _publishActivity;          // Phase-0 evidence record (Kind="health_delta") — L0 tee + rich-evidence UI
        private readonly Action<string, HealthDelta> _stashAgentHealth;   // the engine-level mailbox (correlation-true; survives a model swap mid-call)
        private readonly Func<bool> _isPro;                               // LAZY entitlement read — see the class doc (P6)

        // One analyzer (and thus ONE ReadinessRuleSet.Default() materialization) per probe/session — the rule
        // set is immutable, so re-building it every commit was pure allocation churn.
        private readonly ReadinessAnalyzer _analyzer = new ReadinessAnalyzer();
        // The incremental scan memo the scoped rescan rolls forward (per-rule population + verdict carries).
        private readonly ReadinessScanState _readinessState = new ReadinessScanState();

        /// <summary>The memoized pre-commit snapshot — ONE object so the readiness card, the per-rule active
        /// counts (always derived from that same card — they can't drift) and the BPA key set are seeded and
        /// swapped ATOMICALLY. BpaKeys alone may be null when its seed scan threw: OnCommit re-seeds it on every
        /// commit until one succeeds, so a transient baseline failure can never permanently disable BPA
        /// detection for the session (it used to).</summary>
        private sealed class BeforeState
        {
            public Scorecard Card;
            public Dictionary<string, int> ActiveCountsByRule;
            public HashSet<string> ReadinessKeys;   // stable keys (see StableRef) of ALL readiness findings, waived included
            public HashSet<string> BpaKeys;         // stable keys of ALL BPA violations (waived included); null = seed pending
        }
        private BeforeState _before;

        /// <summary>Wall-clock of the last OnCommit computation — the P-Efficiency evidence (also carried on the
        /// health_delta activity event).</summary>
        public long LastComputeMs { get; private set; }

        public HealthDeltaProbe(Func<Model, IReadOnlyList<BpaRule>> bpaRules, Action<ActivityEvent> publishActivity,
            Action<string, HealthDelta> stashAgentHealth = null, Func<bool> isPro = null)
        {
            _bpaRules = bpaRules ?? throw new ArgumentNullException(nameof(bpaRules));
            _publishActivity = publishActivity;
            _stashAgentHealth = stashAgentHealth;
            _isPro = isPro;
        }

        private bool IsPro() => _isPro?.Invoke() ?? true;

        // The ISessionObserver seam: thin adapters so the descriptive baseline/commit names (and their doc
        // contracts) stay the probe's real surface.
        public void BeforeCommit(Model model) => EnsureBaseline(model);
        public void AfterCommit(SessionCommit commit)
        {
            // Always compute — OnCommit's side effects (snapshot roll-forward, mailbox stash, evidence record)
            // must run regardless — but contribute FIRST-writer-wins: an earlier-registered peer's non-null
            // Health is never silently replaced (the contribute-don't-clobber contract on SessionCommit.Health).
            var delta = OnCommit(commit.Model, commit.Revision, commit.Origin, commit.Label, commit.Deltas, commit.CorrelationId);
            if (delta != null && commit.Health == null) commit.Health = delta;
        }

        /// <summary>Memoize the pre-edit baseline if absent (no-op after the first call, and while the
        /// entitlement isn't Pro — the check is LAZY so a mid-session activation starts reporting). Dispatcher thread.</summary>
        public void EnsureBaseline(Model model)
        {
            if (_before != null || !IsPro()) return;   // free tier: one bool check, nothing else
            var card = _analyzer.Baseline(model, _readinessState);   // full scan; seeds the incremental memo
            // Stable ids resolve against the model AS OF this snapshot — pre-mutation, so every finding's ref
            // still resolves (post-rename it wouldn't).
            var memo = new Dictionary<string, string>(StringComparer.Ordinal);
            HashSet<string> bpa = null;
            // BPA seeding is fallible independently of readiness (e.g. a malformed model-embedded rules
            // annotation). Keep the snapshot ATOMIC and the failure honest: seed with BpaKeys=null and let
            // OnCommit retry the BPA seed each commit — never a permanently-dead detection path.
            try { bpa = FullBpaKeys(model, memo); } catch { /* retried in OnCommit */ }
            _before = new BeforeState
            {
                Card = card,
                ActiveCountsByRule = CountActiveByRule(card.Findings),
                ReadinessKeys = ReadinessKeysOf(model, card.Findings, memo),
                BpaKeys = bpa,
            };
        }

        /// <summary>Compute the delta this commit caused, or null when below threshold. Dispatcher thread.
        /// <paramref name="correlationId"/> is the originating tool-call identity (null outside an MCP call) —
        /// captured by Session on the caller's thread and stamped onto the stashed agent delta so the MCP filter
        /// drains exactly its own call's movement (see <see cref="AgentHealthMailbox"/>).</summary>
        public HealthDelta OnCommit(Model model, long revision, string origin, string label, IReadOnlyList<ChangeDelta> deltas, string correlationId)
        {
            if (!IsPro()) return null;   // soft gate, checked lazily — free edits stay plain "rev N" at near-zero cost
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var before = _before;   // null only if EnsureBaseline failed — then report no claims and self-heal below
            // Post-commit ref→stable-id resolutions, shared across this commit's checks and roll-forward.
            var memo = new Dictionary<string, string>(StringComparer.Ordinal);

            var touched = new HashSet<string>(
                (deltas ?? Array.Empty<ChangeDelta>()).Select(d => d.Ref).Where(r => !string.IsNullOrEmpty(r)),
                StringComparer.Ordinal);
            // Refs whose deltas carried a "Name" prop — the ONLY population the rename guard below may suppress.
            // (TOMWrapper fires ObjectChanged with PropertyName "Name" AFTER the set, so the delta ref — and the
            // after-scan finding ref — both carry the NEW name.)
            var renamed = new HashSet<string>(
                (deltas ?? Array.Empty<ChangeDelta>())
                    .Where(d => d.Props != null && d.Props.Any(p => string.Equals(p, "Name", StringComparison.OrdinalIgnoreCase)))
                    .Select(d => d.Ref).Where(r => !string.IsNullOrEmpty(r)),
                StringComparer.Ordinal);

            // The "after" readiness card: a SCOPED incremental rescan rolled forward on the session memo — only
            // touched objects re-lint/re-evaluate for the scopable rules (the recurring cost used to be a full
            // O(rules × objects) rescan per EDIT on the dispatcher thread, delaying didChange + the tool result).
            // If the baseline never seeded, or the scoped pass throws (a half-rolled memo must not drift), fall
            // back to a FULL scan that reseeds the memo consistently.
            Scorecard after;
            if (before == null)
            {
                after = _analyzer.Baseline(model, _readinessState);
            }
            else
            {
                try { after = _analyzer.Reanalyze(model, _readinessState, touched); }
                catch { after = _analyzer.Baseline(model, _readinessState); }
            }
            var afterCounts = CountActiveByRule(after.Findings);
            // The BPA lane is fallible on its own (e.g. an unreadable model-embedded rules annotation) — a BPA
            // failure must degrade to "no BPA claims this commit", never take readiness/impact reporting down.
            BpaScorecard scopedBpa = null;
            if (touched.Count > 0)
            {
                try { scopedBpa = BpaAnalyzer.Analyze(model, _bpaRules(model), touched); }
                catch { /* the reseed below (or the next commit's) re-arms detection when the lane heals */ }
            }

            // ---- net-new findings on TOUCHED objects (readiness incl. the lint-fed BestPractice rules, + BPA) ----
            var netNewIds = new List<string>();
            var netNewCount = 0;
            var warnPlus = false;
            if (before != null)
            {
                var beforeCounts = before.ActiveCountsByRule;
                var beforeKeys = before.ReadinessKeys;
                foreach (var f in after.Findings)
                {
                    // Identity is rename-stable where a LineageTag exists: a renamed object's finding resolves
                    // (via the post-commit model — the finding carries the NEW name) to the same tag key its
                    // "before" was stored under, so it can never look net-new.
                    if (f.Waived || !touched.Contains(f.ObjectRef) || HasStableKey(beforeKeys, f.RuleId, f.ObjectRef, model, memo)) continue;
                    // Rename guard, the TAG-LESS fallback — applied ONLY when the finding's own object was renamed
                    // THIS commit: a rename moves a name-path key (measure:T/Old → measure:T/New) without adding
                    // one, and a flat model-wide count for the rule confirms it merely moved. Gating on the rename
                    // is what keeps a MIXED commit honest: fix one + introduce one of the SAME rule keeps the count
                    // flat, and the real new finding on a not-renamed object must still report. (Tagged objects
                    // never reach this line — HasStableKey already matched them.)
                    if (renamed.Contains(f.ObjectRef)
                        && beforeCounts.TryGetValue(f.RuleId, out var was)
                        && afterCounts.TryGetValue(f.RuleId, out var now) && now <= was) continue;
                    netNewCount++;
                    netNewIds.Add(f.RuleId);
                    warnPlus |= f.Severity != nameof(Analysis.Severity.Info);   // Medium/High/Critical = Warning+
                }
            }
            var beforeBpa = before?.BpaKeys;
            if (scopedBpa != null && beforeBpa != null)
            {
                foreach (var v in scopedBpa.Violations)
                {
                    // Same rename-stable identity as readiness; tag-less BPA objects still re-key on rename
                    // (accepted fallback edge — no count guard on this lane).
                    if (v.Waived || HasStableKey(beforeBpa, v.RuleId, v.ObjectRef, model, memo)) continue;
                    netNewCount++;
                    netNewIds.Add(v.RuleId);
                    warnPlus |= v.Severity >= 2;   // BPA convention: 1=info, 2=warning, 3=error
                }
            }

            var impact = ComputeImpact(model, deltas);

            // ---- roll the snapshot forward BEFORE thresholding (the next commit's "before" is this "after") ----
            HashSet<string> nextBpa;
            if (beforeBpa == null)
            {
                // The BPA baseline never seeded (EnsureBaseline threw, or every retry since) — seed it NOW from a
                // full post-commit scan. This commit's own BPA movement is unknowable (no honest before), but
                // detection RESUMES on the next commit instead of staying dead for the session.
                try { nextBpa = FullBpaKeys(model, memo); } catch { nextBpa = null; /* retried next commit */ }
            }
            else
            {
                nextBpa = beforeBpa;
                // Roll only when the scoped scan actually ran — if it threw, keep the old keys (stale for the
                // touched objects, but dropping them would mint false "net-new" reports next time they're touched).
                if (scopedBpa != null && touched.Count > 0)
                {
                    // Drop BOTH id forms for the touched objects: their name-path refs AND their stable ids — a
                    // renamed object's old entries are reachable only through the tag. (A renamed object's stale
                    // OLD-name key can linger; harmless unless a tag-less same-named object re-violates the same
                    // rule — the same edge the name-keyed code had.)
                    var gone = new HashSet<string>(touched, StringComparer.Ordinal);
                    foreach (var t in touched) { var st = StableRef(model, t, memo); if (st != t) gone.Add(st); }
                    nextBpa.RemoveWhere(k => gone.Contains(RefOf(k)));
                    foreach (var v in scopedBpa.Violations) AddStableKeys(nextBpa, v.RuleId, v.ObjectRef, model, memo);
                }
            }
            _before = new BeforeState
            {
                Card = after,
                ActiveCountsByRule = afterCounts,
                ReadinessKeys = ReadinessKeysOf(model, after.Findings, memo),   // post-commit model: refs resolve NOW
                BpaKeys = nextBpa,
            };

            var delta = ComputeOrNull(before?.Card.Grade, after.Grade, netNewIds, netNewCount, warnPlus, impact);
            sw.Stop();
            LastComputeMs = sw.ElapsedMilliseconds;

            if (delta == null) return null;

            // Agent deltas go to the ENGINE-level mailbox, stamped with this tool call's identity — merge-on-
            // enqueue there, so a mega-batch call folds into one slot and parallel calls never cross-attribute.
            if (origin == "agent")
            {
                try { _stashAgentHealth?.Invoke(correlationId, delta); } catch { /* the stash must never fail an edit */ }
            }

            // Phase-0 shared evidence shape (compare_baseline precedent): publish the full delta as an
            // ActivityEvent so the Learning-Loop L0 tee and the rich-evidence UI consume it uniformly.
            // Best-effort — the evidence record must never fail the edit.
            try
            {
                _publishActivity?.Invoke(new ActivityEvent
                {
                    Kind = "health_delta",
                    Origin = origin,
                    Label = label ?? "edit",
                    Ok = true,
                    ElapsedMs = LastComputeMs,
                    Result = new HealthDeltaEvidence
                    {
                        Revision = revision,
                        Grade = delta.Grade,
                        New = delta.New,
                        Findings = delta.Findings,
                        Impact = delta.Impact,
                    },
                });
            }
            catch { /* evidence is a ride-along */ }

            return delta;
        }

        /// <summary>The single construction point + the threshold ("always-on with sub-threshold suppression",
        /// Kane 2026-07-07): return null unless the grade LETTER changed, a net-new Warning+ severity finding
        /// landed on a touched object, or the blast radius is &gt; 0. Both sinks key off null.</summary>
        internal static HealthDelta ComputeOrNull(string gradeBefore, string gradeAfter,
            List<string> netNewIds, int netNewCount, bool warnPlus, int impact)
        {
            var gradeMoved = gradeBefore != null && gradeAfter != null && gradeBefore != gradeAfter;
            if (!gradeMoved && !warnPlus && impact <= 0) return null;
            return new HealthDelta
            {
                Grade = gradeMoved ? gradeBefore + "->" + gradeAfter : null,
                New = netNewIds.Count > 0 ? netNewIds.Distinct(StringComparer.Ordinal).Take(MaxRuleIds).ToArray() : null,
                Findings = netNewCount > 0 ? netNewCount : (int?)null,
                Impact = impact > 0 ? impact : (int?)null,
                // Always a definite bool when there ARE new findings: the RPC serializer drops nulls from the
                // wire, so a null here is indistinguishable from an old engine without the field — and the chip's
                // back-compat fallback (issues > 0) would tint Info-only edits, defeating the calm-chip fix.
                Warn = netNewCount > 0 ? warnPlus : (bool?)null,
            };
        }

        // ---- rename-stable finding identity ----------------------------------------------------------------
        // Findings key on the object's LineageTag where one exists (the wrapper auto-mints tags at CL>=1540, and
        // a tag survives renames — the name path does not), falling back to the name path for tag-less objects,
        // deleted objects, and model-level findings. Key SETS store BOTH forms; LOOKUPS match the object's
        // current form only (see HasStableKey — a stale name alias must never suppress a new object reusing a
        // renamed object's old name), so a tag REMOVED mid-session still matches its stored name form while a
        // tag GAINED re-reports once. The "lt:" prefix keeps the tag namespace disjoint from name paths.
        // Resolution must run against the model AS OF the snapshot (EnsureBaseline: pre-mutation; OnCommit
        // checks/roll-forward: post-commit), when the refs still resolve.
        private static string StableRef(Model model, string objRef, Dictionary<string, string> memo)
        {
            if (memo.TryGetValue(objRef, out var hit)) return hit;
            var stable = objRef;
            try
            {
                string tag = null;
                switch (ObjectRefs.Resolve(model, objRef))
                {
                    case Measure m: tag = m.LineageTag; break;
                    case Column c: tag = c.LineageTag; break;
                    case Table t: tag = t.LineageTag; break;      // includes calculated + calc-group tables
                    case Hierarchy h: tag = h.LineageTag; break;
                    case Level l: tag = l.LineageTag; break;
                    case Function fn: tag = fn.LineageTag; break;
                    // relationships / partitions / roles / perspectives / calc items: no wrapper tag — name path
                }
                if (!string.IsNullOrEmpty(tag)) stable = "lt:" + tag;
            }
            catch { /* identity must never fail an edit — the name path is always a usable key */ }
            memo[objRef] = stable;
            return stable;
        }

        private static void AddStableKeys(HashSet<string> keys, string ruleId, string objRef, Model model, Dictionary<string, string> memo)
        {
            keys.Add(ruleId + Sep + objRef);
            var st = StableRef(model, objRef, memo);
            if (st != objRef) keys.Add(ruleId + Sep + st);
        }

        // Match by the object's CURRENT identity form ONLY. For a tagged object the tag key is the identity —
        // the name key must not be consulted, or a stale alias (stored for a since-renamed object) would
        // suppress a genuinely-NEW object that reuses the old name (Codex P2 on #90). The name key is the
        // identity only when the object has no tag (or no longer resolves). The one-way cost: an object that
        // GAINS a tag between snapshots re-reports once (rare — an explicit LineageTag set) — the honest trade
        // against silently missing a real net-new finding.
        private static bool HasStableKey(HashSet<string> keys, string ruleId, string objRef, Model model, Dictionary<string, string> memo)
        {
            var st = StableRef(model, objRef, memo);
            return keys.Contains(ruleId + Sep + st);   // st == objRef when tag-less/unresolvable — the name-path form
        }

        private static HashSet<string> ReadinessKeysOf(Model model, IEnumerable<ReadinessFinding> findings, Dictionary<string, string> memo)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in findings) AddStableKeys(keys, f.RuleId, f.ObjectRef, model, memo);
            return keys;
        }

        private HashSet<string> FullBpaKeys(Model model, Dictionary<string, string> memo)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in BpaAnalyzer.Analyze(model, _bpaRules(model)).Violations) AddStableKeys(keys, v.RuleId, v.ObjectRef, model, memo);
            return keys;
        }

        // Blast radius: ONE multi-source BFS over ALL of this commit's SEMANTIC roots (shared seen/queue — the
        // cost is O(graph) once, so there is NO root cap: a truncated walk could report impact=0 and let
        // ComputeOrNull suppress a real blast radius). Roots are excluded from their own impact (an edited/created
        // object is the change, not its impact — which also stops a created table from "impacting" its own
        // freshly-created columns, since those are roots too). A root that no longer resolves (deleted) has no
        // offline cone to walk — accepted v1 limitation, the dependents' own re-lint catches it.
        private static int ComputeImpact(Model model, IReadOnlyList<ChangeDelta> deltas)
        {
            if (deltas == null || deltas.Count == 0) return 0;
            var roots = deltas
                .Where(d => d.Props == null || d.Props.Any(p => SemanticProps.Contains(p)))
                .Select(d => d.Ref)
                .Where(r => !string.IsNullOrEmpty(r))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (roots.Count == 0) return 0;
            try { return Lineage.LineageGraph.ImpactFrom(model, roots).Length; }
            catch { return 0; /* never fail an edit for a blast-radius count */ }
        }

        private static Dictionary<string, int> CountActiveByRule(IEnumerable<ReadinessFinding> findings)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var f in findings)
            {
                if (f.Waived) continue;
                counts.TryGetValue(f.RuleId, out var c);
                counts[f.RuleId] = c + 1;
            }
            return counts;
        }

        private static string RefOf(string key)
        {
            var i = key.IndexOf(Sep);
            return i < 0 ? key : key.Substring(i + 1);
        }
    }

    /// <summary>The typed Result payload of a "health_delta" ActivityEvent — the Phase-0 canonical evidence
    /// record shape (kept typed, not anonymous, so downstream consumers can bind it).</summary>
    public sealed class HealthDeltaEvidence
    {
        public long Revision { get; set; }
        public string Grade { get; set; }
        public string[] New { get; set; }
        public int? Findings { get; set; }
        public int? Impact { get; set; }
    }
}
