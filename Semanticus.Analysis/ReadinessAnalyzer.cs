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
        /// rules (cardinality vs Copilot's value index ceiling) are evaluated too; offline (null) they don't run, so the
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

        /// <summary>How many score points fixing each open finding would add on its own, keyed by
        /// <see cref="LiftKey"/>, read off the same scoring core the grade comes from: the finding's rule weight
        /// inside its category, over the rule's population, times the category's share of the average. A finding of
        /// a rule that holds a gate (more than half the visible measures undescribed) is listed in
        /// <paramref name="gateHolders"/>, because clearing the gate lifts the grade more than any one point does.
        /// The words queue orders by this (Plan "Give the words to your assistant"). Nothing is written.</summary>
        public IReadOnlyDictionary<string, double> Lifts(Model model, out ISet<string> gateHolders)
        {
            Dictionary<string, double> lifts = null;
            ISet<string> held = null;
            AnalyzeCore(model, null, null, null, (evals, present, gated) =>
            {
                lifts = new Dictionary<string, double>(StringComparer.Ordinal);
                held = new HashSet<string>(StringComparer.Ordinal);
                var catWsum = present.Sum(c => c.Weight);
                foreach (var cat in present)
                {
                    var catEvals = evals.Where(x => x.rule.Category.ToString() == cat.Category && x.ev.Applicable > 0 && !x.rule.GateOnly).ToList();
                    double wsum = catEvals.Sum(x => (double)(int)x.rule.Severity);
                    if (wsum <= 0 || catWsum <= 0) continue;
                    foreach (var (rule, ev) in catEvals)
                    {
                        var points = 100.0 * (cat.Weight / catWsum) * ((int)rule.Severity / wsum) / ev.Applicable;
                        foreach (var v in ev.Violations.Where(v => !v.Waived))
                        {
                            var key = LiftKey(v.RuleId, v.ObjectRef);
                            lifts[key] = Math.Round(points, 3);
                            if (gated && rule.Id == "DESC-MEASURE") held.Add(key);
                        }
                    }
                }
            });
            gateHolders = held ?? new HashSet<string>(StringComparer.Ordinal);
            return lifts ?? new Dictionary<string, double>(StringComparer.Ordinal);
        }

        /// <summary>The key <see cref="Lifts"/> files a finding under.</summary>
        public static string LiftKey(string ruleId, string objectRef) => (ruleId ?? "") + "\u241f" + (objectRef ?? "");

        private Scorecard AnalyzeCore(Model model, ReadinessLiveStats live, ReadinessScanState state, ISet<string> touchedRefs)
            => AnalyzeCore(model, live, state, touchedRefs, null);

        private Scorecard AnalyzeCore(Model model, ReadinessLiveStats live, ReadinessScanState state, ISet<string> touchedRefs,
            Action<List<(ReadinessRule rule, RuleEvaluation ev)>, List<CategoryScore>, bool> scored)
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
            // Construct the live catalog even offline for waiver reconciliation, but only evaluate it with supplied data.
            var liveRules = ReadinessRuleSet.LiveRules(live ?? new ReadinessLiveStats());
            if (live != null) composed = composed.Concat(liveRules);
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
            foreach (var f in findings) { f.Waived = false; f.WaiverReason = null; f.WaiverRuleLevel = false; f.WaivedAsTwin = false; f.CoveredBy = null; f.CoveredReason = null; }
            if (waivers.Count > 0)
                foreach (var f in findings)
                {
                    var w = WaiverStore.Match(waivers, "air", f.RuleId, f.ObjectRef);
                    if (w != null) { f.Waived = true; f.WaiverReason = w.Reason; f.WaiverRuleLevel = WaiverStore.IsRuleLevel(w.ObjectRef); continue; }
                    // The same problem accepted on Model quality is accepted here too (one decision, two pages; PairedAcceptances).
                    var twin = PairedAcceptances.TwinAcceptance(waivers, "air", f.RuleId, f.ObjectRef);
                    if (twin != null)
                    {
                        f.Waived = true; f.WaiverReason = twin.Reason; f.WaiverRuleLevel = WaiverStore.IsRuleLevel(twin.ObjectRef);
                        f.CoveredBy = "the same finding on Model quality"; f.CoveredReason = twin.Reason;
                    }
                }

            // A waiver declines a CHANGE, not a rule id: a finding whose change another accepted finding declined (this rule's
            // twin in either system) reads as accepted too, so the Findings pages, the score and the Model Plan agree.
            var declined = DeclinedChanges.Read(model);
            if (!declined.IsEmpty)
                foreach (var f in findings)
                {
                    if (f.Waived) continue;
                    var about = RuleAboutTable.ForReadiness(f.RuleId);
                    if (about?.Property == null) continue;
                    var cover = declined.CoverFor("air", f.RuleId, f.ObjectRef, about.Property);
                    if (cover == null) continue;
                    f.Waived = true; f.WaivedAsTwin = true; f.WaiverReason = DeclinedChanges.TwinReason;
                    f.CoveredBy = cover.Label; f.CoveredReason = cover.Reason;
                }

            var categories = new List<CategoryScore>();
            const string NotScored = "Not scored: none of this category's rules found anything to check in this model, so it is not counted in the grade.";
            foreach (ReadinessCategory cat in Enum.GetValues(typeof(ReadinessCategory)))
            {
                var catEvals = evals.Where(x => x.rule.Category == cat && x.ev.Applicable > 0 && !x.rule.GateOnly).ToList();
                var cs = new CategoryScore { Category = cat.ToString(), Weight = Weights[cat], HasRules = catEvals.Count > 0 };
                if (!cs.HasRules) cs.NotScoredReason = NotScored;
                if (catEvals.Count > 0)
                {
                    double wsum = 0, wscore = 0; int appl = 0, viol = 0, waived = 0;
                    foreach (var (rule, ev) in catEvals)
                    {
                        var active = ev.Violations.Count(v => !v.IsAccepted());   // an accepted finding counts as a pass; a related one (left alone, not accepted) does not
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

            // A formula that points at an object the model does not have cannot compute, whatever the average says. Like the scale ceiling it reads the raw
            // violation count: a finding accepted for it does not lift the cap, because a number that cannot be computed is not made right by a reason.
            var broken = evals.FirstOrDefault(x => x.rule.Id == "DAX-REFERENCE");
            if (broken.ev != null && broken.ev.Violations.Count > 0)
            {
                overall = Math.Min(overall, 69);
                gatedBy.Add(broken.ev.Violations.Count + (broken.ev.Violations.Count == 1 ? " object holds" : " objects hold") + " a formula that refers to an object the model does not have; capped at D");
            }

            scored?.Invoke(evals, present, descM.ev != null && descM.ev.Applicable > 0
                && (double)descM.ev.Violations.Count / descM.ev.Applicable > 0.5);

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

            // Reconcile against known rule ids, not just this scan's evaluation list: lack of live data does not
            // retire a live-only rule. Built-in and current custom rules still come from the loaded set, so deleted
            // custom/unknown ids remain reportable without evaluating live rules or counting dormant waivers as active.
            var knownIds = rules.Select(r => r.Id).Concat(liveRules.Select(r => r.Id));
            var orphans = WaiverStore.Orphans(waivers, "air", knownIds, model).ToArray();

            // An acceptance stays in force while it is stored, whether or not its finding still fires, so it is listed either
            // way: a row for each stored per-object acceptance that declines a change and has no finding now.
            var listed = findings;
            var quiet = QuietAcceptances(model, declined, findings, rules);
            quiet.AddRange(WholeRuleRows(waivers, findings, rules));   // a rule-level acceptance always has its row (round 7)
            if (quiet.Count > 0) listed = findings.Concat(quiet).ToList();

            return new Scorecard
            {
                Overall = Math.Round(overall, 1),
                RawOverall = Math.Round(raw, 1),
                Grade = GradeFor(overall),
                GatedBy = gatedBy.ToArray(),
                Categories = categories.ToArray(),
                Coverage = coverage,
                Findings = listed.ToArray(),
                SafeFixCount = findings.Count(f => !f.Waived && f.Fix == nameof(FixKind.SafeFix)),
                WaivedCount = listed.Count(f => f.IsAccepted()),
                RuleErrors = ruleErrors.Distinct().ToArray(),
                // Named from the eval itself, so the id is the rule's real id rather than something scraped back out
                // of a message string. These are exactly the rules the dormant reset zeroed.
                UnevaluatedRules = evals.Where(x => x.ev.Errors.Count > 0).Select(x => x.rule.Id)
                                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                OrphanedWaivers = orphans,
                OrphanedWaiverCount = orphans.Length,
            };
        }

        /// <summary>One row per stored per-object AI-readiness acceptance that keeps a change from being made and whose
        /// finding does not fire now (its object still exists). Never counted in the score: the row is accepted and says so.</summary>
        private static List<ReadinessFinding> QuietAcceptances(Model model, DeclinedChanges declined, List<ReadinessFinding> findings, IReadOnlyList<ReadinessRule> rules)
        {
            var rows = new List<ReadinessFinding>();
            var stored = declined.Sources.Where(s => s.System == "air" && !s.RuleLevel && !s.TeIgnore).ToList();
            if (stored.Count == 0) return rows;
            var firing = new HashSet<string>(findings.Select(f => f.RuleId + "\u241f" + WaiverStore.CanonicalRef(f.ObjectRef)), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> names = null;
            foreach (var s in stored)
            {
                if (firing.Contains(s.RuleId + "\u241f" + s.ObjectRef)) continue;
                var rule = rules.FirstOrDefault(r => string.Equals(r.Id, s.RuleId, StringComparison.OrdinalIgnoreCase));
                if (rule == null) continue;
                names ??= WaiverStore.AllObjects(model).OfType<ITabularNamedObject>()
                    .GroupBy(o => WaiverStore.CanonicalRef(Refs.For(o)), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
                if (!names.TryGetValue(s.ObjectRef, out var name)) continue;   // the object is gone: nothing left to keep
                rows.Add(new ReadinessFinding
                {
                    RuleId = rule.Id, RuleTitle = rule.Title, Category = rule.Category.ToString(), Severity = rule.Severity.ToString(),
                    Fix = rule.Fix.ToString(), ObjectRef = s.ObjectRef, ObjectName = name,
                    Message = DeclinedChanges.NoLongerFiresText, Waived = true, WaiverReason = s.Reason, NoLongerFires = true,
                });
            }
            return rows;
        }

        /// <summary>One row per stored rule-level AI-readiness acceptance whose rule fires nowhere on the model now (where it fires, the
        /// accepted findings are listed already, each tagged "whole rule"). It says what the acceptance reaches and can be reopened
        /// from there. Never counted in the score: it is an acceptance and says so.</summary>
        private static List<ReadinessFinding> WholeRuleRows(List<WaiverRecord> waivers, List<ReadinessFinding> findings, IReadOnlyList<ReadinessRule> rules)
        {
            var rows = new List<ReadinessFinding>();
            foreach (var w in waivers.Where(x => string.Equals(x.System, "air", StringComparison.OrdinalIgnoreCase) && WaiverStore.IsRuleLevel(x.ObjectRef)))
            {
                var rule = rules.FirstOrDefault(r => string.Equals(r.Id, w.RuleId, StringComparison.OrdinalIgnoreCase));
                if (rule == null) continue;   // a retired rule id is an orphan, reported on its own
                if (findings.Any(f => f.WaiverRuleLevel && string.Equals(f.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase))) continue;
                rows.Add(new ReadinessFinding
                {
                    RuleId = rule.Id, RuleTitle = rule.Title, Category = rule.Category.ToString(), Severity = rule.Severity.ToString(),
                    Fix = rule.Fix.ToString(), ObjectRef = WaiverStore.ModelRef, ObjectName = DeclinedChanges.WholeModelName,
                    Message = DeclinedChanges.WholeRuleMessage(RuleAboutTable.ForReadiness(rule.Id)),
                    Waived = true, WaiverReason = w.Reason, WaiverRuleLevel = true, WholeRuleRow = true,
                });
            }
            return rows;
        }

        private static string GradeFor(double s) => s >= 90 ? "A" : s >= 80 ? "B" : s >= 70 ? "C" : s >= 60 ? "D" : "F";
    }
}
