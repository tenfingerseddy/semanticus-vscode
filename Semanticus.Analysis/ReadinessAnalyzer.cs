using System;
using System.Collections.Generic;
using System.Linq;
using TabularEditor.TOMWrapper;

namespace Semanticus.Analysis
{
    /// <summary>Runs the AI-readiness rule set over a model and produces a scored, gated scorecard.</summary>
    public sealed class ReadinessAnalyzer
    {
        private static readonly Dictionary<ReadinessCategory, double> Weights = new Dictionary<ReadinessCategory, double>
        {
            [ReadinessCategory.Descriptions] = 0.22,
            [ReadinessCategory.Naming] = 0.18,
            [ReadinessCategory.Synonyms] = 0.15,
            [ReadinessCategory.Relationships] = 0.15,
            [ReadinessCategory.Visibility] = 0.12,
            [ReadinessCategory.DataAgentConfig] = 0.08,
            // Weights[cat] is indexed for EVERY enum value in the scoring loop below — a missing entry throws
            // KeyNotFoundException on every scan. BestPractice only "presents" when a rule reports Applicable>0, so a
            // clean model is unchanged (normalization over PRESENT categories re-spreads the weight). See BestPractice
            // rules in ReadinessRuleSet.Default() (docs/dax-best-practice-rules.md §3).
            [ReadinessCategory.BestPractice] = 0.08,
            [ReadinessCategory.Formatting] = 0.05,
            [ReadinessCategory.CopilotLimits] = 0.05,
        };

        private readonly IReadOnlyList<ReadinessRule> _rules;
        private static readonly ISet<string> EmptyRefs = new HashSet<string>(StringComparer.Ordinal);

        public ReadinessAnalyzer(IReadOnlyList<ReadinessRule> rules = null) => _rules = rules ?? ReadinessRuleSet.Default();

        public Scorecard Analyze(Model model) => AnalyzeCore(model, null, null, null);

        /// <summary>Score the model. When <paramref name="live"/> is supplied (the live scan path), the Dmv-kind
        /// rules (cardinality vs the Q&A index ceiling) are evaluated too; offline (null) they don't run, so the
        /// offline scorecard is byte-identical to before.</summary>
        public Scorecard Analyze(Model model, ReadinessLiveStats live) => AnalyzeCore(model, live, null, null);

        /// <summary>Full scan that also (re)seeds <paramref name="state"/> — the health probe's baseline. Any
        /// existing memos are discarded first, so a reseed after a failed scoped pass can never mix stale carries
        /// into the fresh baseline. (Named, not an Analyze overload: <c>Analyze(model, null)</c> call sites must
        /// keep resolving to the live-stats overload.)</summary>
        public Scorecard Baseline(Model model, ReadinessScanState state)
        {
            state.Reset();
            return AnalyzeCore(model, null, state, null);
        }

        /// <summary>Scoped incremental rescan (the health probe's per-commit path): rules that support the
        /// <see cref="ReadinessRule.EvaluateScoped"/> seam re-evaluate ONLY the touched (or new/re-keyed) objects
        /// and roll their per-object verdicts forward on <paramref name="state"/>; the custom cross-object
        /// ModelRules re-run in full (they are the cheap sweeps — the per-expression lints are all scoped). The
        /// category tallies, gates, coverage and grade are then rebuilt from the same scoring core the full scan
        /// uses, so the result is equal to a full rescan (pinned by ReadinessScopedScanTests).</summary>
        public Scorecard Reanalyze(Model model, ReadinessScanState state, ISet<string> touchedRefs)
            => AnalyzeCore(model, null, state, touchedRefs ?? new HashSet<string>(StringComparer.Ordinal));

        private Scorecard AnalyzeCore(Model model, ReadinessLiveStats live, ReadinessScanState state, ISet<string> touchedRefs)
        {
            // Re-read the Prep-for-AI surface fresh each scan (the per-Model memo is otherwise stale after an
            // in-session LSDL write, e.g. set_ai_instructions). The memo still serves the 6 DAC rules within this scan.
            PrepForAiReader.Invalidate(model);
            // Custom (model-embedded, user-authored) rules ride along every scan — read fresh from the annotation so
            // a load/reset/undo is reflected immediately on both doors. Problems (unparseable annotation, a built-in
            // id collision from a hand-edit) surface as RuleErrors, never a silent pass. Custom rules can never
            // register gates (the gates below are wired by BUILT-IN rule ids, and built-in id collisions are refused).
            var ruleErrors = new List<string>();
            var custom = CustomReadinessRuleSet.FromModel(model, ruleErrors);
            IEnumerable<ReadinessRule> composed = custom.Count == 0 ? _rules : _rules.Concat(custom);
            if (live != null) composed = composed.Concat(ReadinessRuleSet.LiveRules(live));
            var rules = composed as IReadOnlyList<ReadinessRule> ?? composed.ToList();
            var empty = touchedRefs ?? EmptyRefs;   // the seeding scan passes null: memos are reset, so everything is fresh anyway
            var evals = rules.Select(r =>
            {
                var ev = state != null ? r.EvaluateScoped(model, empty, state.For(r.Id)) : null;
                return (rule: r, ev: ev ?? r.Evaluate(model));   // null = not scopable (custom cross-object ModelRule) or stateless scan
            }).ToList();
            var findings = evals.SelectMany(x => x.ev.Violations).ToList();
            foreach (var x in evals) if (x.ev.Errors.Count > 0) ruleErrors.AddRange(x.ev.Errors);

            // Tag accepted (waived) findings. A waiver excludes a finding from the SCORE (counts as a pass) but the
            // finding is still surfaced — never hidden — so the grade can't be silently inflated. Gates below still
            // evaluate on the RAW (pre-waiver) violation count: you can't accept your way past a physical ceiling.
            // Reset first: the scoped path CARRIES finding instances across scans, so a stale tag from the previous
            // scan (or a waiver removed mid-session) must not survive re-tagging. Fresh findings default false — no-op.
            var waivers = WaiverStore.Load(model);
            foreach (var f in findings) { f.Waived = false; f.WaiverReason = null; f.WaiverRuleLevel = false; }
            if (waivers.Count > 0)
                foreach (var f in findings)
                {
                    var w = WaiverStore.Match(waivers, "air", f.RuleId, f.ObjectRef);
                    if (w != null) { f.Waived = true; f.WaiverReason = w.Reason; f.WaiverRuleLevel = WaiverStore.IsRuleLevel(w.ObjectRef); }
                }

            var categories = new List<CategoryScore>();
            foreach (ReadinessCategory cat in Enum.GetValues(typeof(ReadinessCategory)))
            {
                var catEvals = evals.Where(x => x.rule.Category == cat && x.ev.Applicable > 0).ToList();
                var cs = new CategoryScore { Category = cat.ToString(), Weight = Weights[cat], HasRules = catEvals.Count > 0 };
                if (catEvals.Count > 0)
                {
                    double wsum = 0, wscore = 0; int appl = 0, viol = 0, waived = 0;
                    foreach (var (rule, ev) in catEvals)
                    {
                        var active = ev.Violations.Count(v => !v.Waived);   // a waived finding counts as a pass
                        var ruleScore = (double)(ev.Applicable - active) / ev.Applicable;
                        double w = (int)rule.Severity;
                        wscore += w * ruleScore; wsum += w;
                        appl += ev.Applicable; viol += active; waived += ev.Violations.Count - active;
                    }
                    cs.Score = wsum > 0 ? 100.0 * wscore / wsum : 100.0;
                    cs.Applicable = appl; cs.Violations = viol; cs.Waived = waived;
                }
                categories.Add(cs);
            }

            var present = categories.Where(c => c.HasRules).ToList();
            var catWsum = present.Sum(c => c.Weight);
            var raw = catWsum > 0 ? present.Sum(c => c.Weight * c.Score) / catWsum : 100.0;

            // Gates (override the average downward).
            var gatedBy = new List<string>();
            var overall = raw;
            var scale = evals.FirstOrDefault(x => x.rule.Id == "LIMIT-SCALE");
            if (scale.ev != null && scale.ev.Violations.Count > 0) { overall = Math.Min(overall, 60); gatedBy.Add("Copilot scale ceiling exceeded; capped at D"); }
            var descM = evals.FirstOrDefault(x => x.rule.Id == "DESC-MEASURE");
            if (descM.ev != null && descM.ev.Applicable > 0 && (double)descM.ev.Violations.Count / descM.ev.Applicable > 0.5)
            { overall = Math.Min(overall, 69); gatedBy.Add(">50% of visible measures undescribed; capped at D"); }

            var coverage = new Dictionary<string, double>();
            void Cov(string key, string ruleId)
            {
                var e = evals.FirstOrDefault(x => x.rule.Id == ruleId);
                if (e.ev != null && e.ev.Applicable > 0)
                    coverage[key] = Math.Round(100.0 * (e.ev.Applicable - e.ev.Violations.Count) / e.ev.Applicable, 1);
            }
            Cov("measuresWithDescription", "DESC-MEASURE");
            Cov("columnsWithDescription", "DESC-COLUMN");
            Cov("measuresWithFormat", "FMT-MEASURE");
            Cov("humanReadableMeasureNames", "NAME-MEASURE");
            Cov("fieldsWithSynonyms", "SYN-FIELD");

            return new Scorecard
            {
                Overall = Math.Round(overall, 1),
                RawOverall = Math.Round(raw, 1),
                Grade = GradeFor(overall),
                GatedBy = gatedBy.ToArray(),
                Categories = categories.ToArray(),
                Coverage = coverage,
                Findings = findings.ToArray(),
                SafeFixCount = findings.Count(f => !f.Waived && f.Fix == nameof(FixKind.SafeFix)),
                WaivedCount = findings.Count(f => f.Waived),
                RuleErrors = ruleErrors.Distinct().ToArray(),
            };
        }

        private static string GradeFor(double s) => s >= 90 ? "A" : s >= 80 ? "B" : s >= 70 ? "C" : s >= 60 ? "D" : "F";
    }
}
