// The coverage step (Kane, 13 Oct 2026: "BPA is at C but doesnt appear in the plan tasks", "Semanticus is not able to progress but we dont
// have a perfect score"). Every row builder claims the findings it can act on, and a finding no builder claims used to make no row at all,
// so the Plan could end at 100% while a grade stood at C. This step runs last in BuildItems. Every open, unwaived finding that SCORES and
// that no row claims joins ONE assistant row for its page: "Fix N best-practice issues" and "Fix N AI readiness issues". The row groups the
// findings by rule, gives each rule its fix brief and its points, and says in words why a finding cannot be fixed. It never asks the person
// to approve anything before the assistant works it: the assistant fixes, and Undo is the safety net.
//
// What scores: a best-practice finding of severity 2 or 3 (an Info note costs nothing, Bpa.cs), and a readiness finding of a rule that found
// something to check on this model (Scorecard.ScoredRuleIds) or that holds a cap on the grade (CapRules).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        internal const string CoverageBpaItemId = "proposal/coverage-bpa";
        internal const string CoverageAiItemId = "proposal/coverage-ai";

        /// <summary>True for the two coverage rows (and for nothing else).</summary>
        internal static bool IsCoverageItem(string id) =>
            string.Equals(id, CoverageBpaItemId, StringComparison.Ordinal) || string.Equals(id, CoverageAiItemId, StringComparison.Ordinal);

        /// <summary>Readiness rules that cap the grade whatever the average says. Their findings score through the cap.</summary>
        private static readonly HashSet<string> CapRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LIMIT-SCALE", "DAX-REFERENCE", "DESC-MEASURE",
        };

        /// <summary>The words for a readiness rule that no tool can fix: a trade-off about the person's data or who sees it. The row keeps the
        /// finding, says why, and leaves the choice to the person. Everything else with no exact fix is the assistant's to read and fix.</summary>
        private static string PersonOnlyReason(ReadinessFinding x)
        {
            var rule = x.RuleId ?? "";
            if (string.Equals(rule, "DAC-FIELD-PARAM-COMPLEXITY", StringComparison.OrdinalIgnoreCase))
                return "A what-if or field-parameter table is a design choice. Removing it changes how reports work, so it is yours to choose.";
            if (string.Equals(rule, "SEC-OLS-AWARENESS", StringComparison.OrdinalIgnoreCase))
                return "Object-level security decides who may see data, so it is yours to choose.";
            if (string.Equals(rule, "DAC-COPILOT-TOOLING-FORMAT", StringComparison.OrdinalIgnoreCase))
                return "This is a note about how the model is stored. There is nothing to change in it.";
            return null;
        }

        /// <summary>Best-practice rules whose problem an AI readiness rule names too, without a stored pairing (PairedAcceptances): fixing the
        /// readiness finding fixes this one, so a row that has the readiness finding has this one.</summary>
        private static readonly Dictionary<string, string> CoverageTwins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AVOID_USING_THE_IFERROR_FUNCTION"] = "BP-DAX-IFERROR",
            ["PROVIDE_FORMAT_STRING_FOR_MEASURES"] = "FMT-MEASURE",
        };

        /// <summary>What a row hands over with it when it becomes a task: the coverage rows their finding keys, the cleanup the columns it removes.</summary>
        internal static string[] ClaimsOfRow(ModelPlanItem row)
        {
            if (row == null) return Array.Empty<string>();
            if (IsCoverageItem(row.Id)) return row.Claims ?? Array.Empty<string>();
            if (IsCleanupItem(row.Id)) return (row.Objects ?? Array.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).ToArray();
            return Array.Empty<string>();
        }

        /// <summary>True while a task's claim still stands: the finding is still scored, or the column it was to remove is still unused in the
        /// model. A claim that cannot be read is live ("not known to be gone"), so a task is never done on a scan that did not run.</summary>
        private static bool ClaimStands(PlanFacts f, string claim)
        {
            if (string.IsNullOrWhiteSpace(claim)) return false;
            if (claim.IndexOf('|') < 0)     // an object a removal was to take away
                return f.Unused == null || (f.Unused.Items ?? Array.Empty<UnusedItem>()).Any(i => string.Equals(i.Ref, claim, StringComparison.OrdinalIgnoreCase));
            if (!f.BpaCoverKnown || f.Readiness == null) return true;
            if (f.BpaCover.Any(c => string.Equals(ClaimKey(c.RuleId, c.ObjectRef), claim, StringComparison.OrdinalIgnoreCase))) return true;
            var scored = f.Readiness.ScoredRuleIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return (f.Readiness.Findings ?? Array.Empty<ReadinessFinding>()).Any(x => !x.Waived && !x.NoLongerFires
                && (scored.Contains(x.RuleId ?? "") || CapRules.Contains(x.RuleId ?? ""))
                && string.Equals(ClaimKey(x.RuleId, x.ObjectRef), claim, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A task handed over from a coverage row keeps the title the row had when it was given ("Fix 73 best-practice issues"), and
        /// the count fell as the assistant worked while Review showed the true one (hero run 21, N5: 73 against 36). The title now counts the
        /// claims that still stand, the same findings Review lists. A task whose claims are all gone keeps its last title.</summary>
        private static void RetitleCoverageTask(ModelPlanItem item, PlanTask task, PlanFacts f)
        {
            if (item == null || task?.OriginLink?.Kind != PlanOriginKinds.Item || task.Claims is not { Length: > 0 }) return;
            if (task.State == PlanTaskStates.Done || !IsCoverageItem(task.OriginLink.Ref)) return;
            var standing = task.Claims.Count(c => ClaimStands(f, c));
            if (standing == 0) return;
            item.Title = string.Equals(task.OriginLink.Ref, CoverageBpaItemId, StringComparison.Ordinal)
                ? "Fix " + CountOf(standing, "best-practice issue", "best-practice issues")
                : "Fix " + CountOf(standing, "AI readiness issue", "AI readiness issues");
        }

        /// <summary>The key a finding is claimed under.</summary>
        internal static string ClaimKey(string ruleId, string objectRef) => (ruleId ?? "") + "|" + (objectRef ?? "");

        private static string[] ClaimsOf(IEnumerable<ReadinessFinding> findings) =>
            (findings ?? Array.Empty<ReadinessFinding>()).Where(x => x != null).Select(x => ClaimKey(x.RuleId, x.ObjectRef)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // ---- what the gather keeps of the best-practice scan -------------------------------------------------

        /// <summary>One scored best-practice finding, as the coverage step needs it. Worked out on the model while it is read.</summary>
        internal sealed class BpaCoverFinding
        {
            public string RuleId, RuleName, ObjectRef, ObjectName, Brief;
            public int Severity;
            /// <summary>True when a row (the fix row, the words row, the relationships row, a readiness row with the same write) already acts on it.</summary>
            public bool Claimed;
            /// <summary>For a fix the bulk pass withholds: "datatype", "rename", "expression", "key" or "other". Null when the rule has no such fix.</summary>
            public string DecisionKind;
            /// <summary>The sentence the withheld fix gave (BpaLineRefusal), kept for the kinds this step does not word itself.</summary>
            public string Decision;
            /// <summary>The AI readiness rule that raises the same problem on this object, when it fires there.</summary>
            public string Twin;
            public int Points => Severity >= 3 ? 5 : 2;
        }

        /// <summary>The scored best-practice findings on the model, each marked claimed or not. Called under the model's read lock.</summary>
        private static List<BpaCoverFinding> BpaCoverFindings(Model m, BpaScorecard card, List<BpaRule> rules,
            IReadOnlyList<WordsQueueItem> readinessWords, IReadOnlyList<WordsQueueItem> formats, IReadOnlyList<ReadinessFinding> relationshipsOnly,
            ISet<string> columnsOnFormatRow, IReadOnlyList<ReadinessFinding> readiness, ISet<string> cleanupRefs)
        {
            var found = new List<BpaCoverFinding>();
            if (card == null) return found;
            foreach (var v in card.Violations.Where(v => !v.Waived && v.Severity >= 2 && !v.WholeRuleRow && !v.NoLongerFires))
            {
                try
                {
                    // The scan and this read share one model read, so a finding is never about an object that is gone. A ref the resolver has no
                    // kind for (a KPI, obj:KPI/...) resolves to nothing and still costs points: the finding stays on a line, with no fix mapped
                    // (D3, the verifier's AdventureWorks drive: DAX_MEASURES_UNQUALIFIED on a KPI was on no row).
                    var obj = ObjectRefs.Resolve(m, v.ObjectRef);
                    var rule = rules.FirstOrDefault(r => string.Equals(r.ID, v.RuleId, StringComparison.OrdinalIgnoreCase));
                    var c = new BpaCoverFinding { RuleId = v.RuleId, RuleName = v.RuleName, ObjectRef = v.ObjectRef, ObjectName = v.ObjectName, Severity = v.Severity };
                    if (obj != null && (v.CanAutoFix || v.Advice))
                    {
                        var line = MapBpaFix(m, rules, v);
                        if (line != null)
                        {
                            var why = BpaLineRefusal(line, obj);
                            if (why == null) c.Claimed = true;
                            else if (IsFormatProperty(line.Target)
                                     && (formats.Any(x => SameRef(x.ObjectRef, v.ObjectRef))
                                         || readinessWords.Any(x => x.Tool == "set_measure_format" && SameRef(x.ObjectRef, v.ObjectRef))))
                                c.Claimed = true;       // the words row asks for the format
                            else
                            {
                                c.Decision = why;
                                var prop = line.Target ?? "";
                                c.DecisionKind = string.Equals(prop, "datatype", StringComparison.OrdinalIgnoreCase) ? "datatype"
                                    : string.Equals(prop, "name", StringComparison.OrdinalIgnoreCase) ? "rename"
                                    : string.Equals(prop, "expression", StringComparison.OrdinalIgnoreCase) ? "expression"
                                    : string.Equals(prop, "iskey", StringComparison.OrdinalIgnoreCase) ? "key" : "other";
                            }
                        }
                    }
                    if (!c.Claimed && string.Equals(v.RuleId, ColumnFormatRuleId, StringComparison.OrdinalIgnoreCase)
                        && ((columnsOnFormatRow != null && columnsOnFormatRow.Contains(v.ObjectRef ?? ""))
                            || formats.Any(x => SameRef(x.ObjectRef, v.ObjectRef))))
                        c.Claimed = true;
                    if (!c.Claimed && relationshipsOnly.Any(o => string.Equals(o.RuleId, v.RuleId, StringComparison.OrdinalIgnoreCase) && SameRef(o.ObjectRef, v.ObjectRef)))
                        c.Claimed = true;
                    // The cleanup row lists this column (its sweep read the lineage): removing it there removes this finding.
                    if (!c.Claimed && string.Equals(v.RuleId, "UNNECESSARY_COLUMNS", StringComparison.OrdinalIgnoreCase)
                        && cleanupRefs != null && cleanupRefs.Contains(v.ObjectRef ?? "")) c.Claimed = true;
                    var air = PairedAcceptances.AirTwinOf(v.RuleId) ?? (CoverageTwins.TryGetValue(v.RuleId ?? "", out var coverTwin) ? coverTwin : null);
                    if (air != null && readiness.Any(x => string.Equals(x.RuleId, air, StringComparison.OrdinalIgnoreCase) && SameRef(x.ObjectRef, v.ObjectRef)))
                        c.Twin = air;
                    c.Brief = BpaBrief(rule, v);
                    found.Add(c);
                }
                catch { /* one finding that cannot be read is left to the scan, not the whole step */ }
            }
            return found;
        }

        private static bool SameRef(string a, string b) => WaiverStore.SameRef(a, b);

        /// <summary>The one-line brief for a best-practice rule: the rule's reason, then the tool that makes the change.</summary>
        private static string BpaBrief(BpaRule rule, BpaViolation v)
        {
            var why = (rule?.Description ?? "").Replace("%object%", "the object").Trim();
            var dot = why.IndexOf(". ", StringComparison.Ordinal);
            if (dot > 0) why = why.Substring(0, dot + 1);
            if (why.Length > 160) why = why.Substring(0, 157).TrimEnd() + "...";
            var fix = (rule?.FixExpression ?? "").Trim();
            string how;
            if (fix.StartsWith("Delete", StringComparison.OrdinalIgnoreCase))
                how = "Check what uses it first (get_lineage, unused_objects), then remove it with delete_objects.";
            else if (v.CanAutoFix)
                how = "apply_fix (system bpa) makes it.";
            else
                how = "get_fix_prompt (system bpa) gives the exact steps for each object.";
            return (why.Length > 0 ? why + " " : "") + how;
        }

        /// <summary>Test seam: the keys (object|property) of the lines the best-practice fix row would hold, when <paramref name="decidedByJev"/> says which
        /// readiness findings a Jev family decided (null: none).</summary>
        internal async Task<string[]> BpaFixRowKeysForTest(Func<string, string, bool> decidedByJev)
        {
            var readiness = await AiReadinessScanAsync().ConfigureAwait(false);
            return await _sessions.Require().ReadAsync(m =>
                BpaPlanLines(m, BpaAnalyzer.Analyze(m, GetBpaRules(m)), ReadinessSafeLines(m, readiness, decidedByJev)).Lines.Select(BpaKey).ToArray()).ConfigureAwait(false);
        }

        /// <summary>Test seam: the proposal rows for these findings.</summary>
        internal static IEnumerable<ModelPlanItem> ProposalItemsForTest(IReadOnlyList<ReadinessFinding> findings) => ProposalItems(findings);

        // ---- raise compatibility level -------------------------------------------------------------------------

        /// <summary>What a compatibility level blocks, one named capability at a time (the page used to keep this list). A level below a floor
        /// is not "held back": the classic date table works below 1701, and the one thing refused is authoring a calendar.</summary>
        private static readonly (string Capability, int Floor)[] LevelBlocks =
        {
            ("Custom calendars for time calculations", 1701),
        };

        internal const string CompatItemId = "compat/raise";

        /// <summary>The first capability this level blocks, or null. An unknown level (0) blocks nothing: "not known to be blocking".</summary>
        internal static (string Capability, int Floor)? BlockedCapability(int level)
        {
            if (level <= 0) return null;
            foreach (var b in LevelBlocks) if (level < b.Floor) return b;
            return null;
        }

        /// <summary>"Raise compatibility level to N" while the level blocks a named capability. The assistant's: the assistant raises it with
        /// set_compatibility_level and says so; Undo takes it back (Kane, 13 Oct 2026). Gone once raised.</summary>
        private static ModelPlanItem CompatibilityItem(PlanFacts f)
        {
            var blocked = BlockedCapability(f.CompatibilityLevel);
            if (blocked == null || f.Tables_ == 0) return null;
            var (capability, floor) = blocked.Value;
            return new ModelPlanItem
            {
                Id = CompatItemId,
                Section = ModelPlanSections.Structure,
                Title = "Raise compatibility level to " + floor,
                Sub = "this model uses level " + f.CompatibilityLevel,
                Value = capability.ToLowerInvariant() + " work",
                Goal = PlanGoals.Quality,
                Who = ModelPlanOwners.Assistant,
                Status = ModelPlanStatuses.Ready,
                Why = new[]
                {
                    "This model uses compatibility level " + f.CompatibilityLevel + " (the Power BI feature level). " + capability + " need level " + floor + " or higher.",
                    "Your assistant raises it with set_compatibility_level and says so in chat. One Undo takes it back in this session.",
                },
                Diff = new[] { "~ compatibility level: " + f.CompatibilityLevel + " to " + floor },
                Touches = "the model",
                Jump = new ModelPlanJump { Tool = "advmodels", Label = "Advanced modelling" },
            };
        }

        // ---- the step ------------------------------------------------------------------------------------------

        private static IEnumerable<ModelPlanItem> CoverageItems(PlanFacts f, IReadOnlyList<ReadinessFinding> open,
            Func<string, string, bool> decidedByJev, IReadOnlyList<ModelPlanItem> built)
        {
            // A model with no tables has one next step, the starts. Nothing here is its work.
            if (f.Tables_ == 0) yield break;

            var claimed = new HashSet<string>(built.Where(i => i.Claims != null).SelectMany(i => i.Claims), StringComparer.OrdinalIgnoreCase);
            var scored = f.Readiness?.ScoredRuleIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool ClaimedAir(ReadinessFinding x)
            {
                var rule = x.RuleId ?? "";
                if (decidedByJev != null && decidedByJev(x.RuleId, x.ObjectRef)) return true;      // a Jev row decides it
                if (claimed.Contains(ClaimKey(x.RuleId, x.ObjectRef))) return true;
                if (f.DateTableByJev && string.Equals(rule, "DATE-MARK", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(rule, "DAC-IMPLICIT-MEASURE", StringComparison.OrdinalIgnoreCase)
                    && (f.StarterByJev || (f.StarterColumns != null && f.StarterColumns.Contains(x.ObjectRef ?? "")))) return true;
                if (rule.StartsWith("FMT-", StringComparison.OrdinalIgnoreCase) && f.JevFormatted != null && f.JevFormatted.Contains(x.ObjectRef ?? "")) return true;
                return false;
            }

            var held = f.HeldClaims ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A finding an open task holds is that task's work; one that is not held is this row's, whenever it appeared (D1).
            bool Held(string rule, string obj) => held.Count > 0 && (held.Contains(ClaimKey(rule, obj)) || held.Contains(obj ?? "\u0001"));

            var air = open
                .Where(x => !x.WholeRuleRow && !x.NoLongerFires && (scored.Contains(x.RuleId ?? "") || CapRules.Contains(x.RuleId ?? "")))
                .Where(x => !ClaimedAir(x))
                .Where(x => !Held(x.RuleId, x.ObjectRef))
                .ToList();

            var bpa = (f.BpaCover ?? new List<BpaCoverFinding>())
                .Where(c => !c.Claimed && !Held(c.RuleId, c.ObjectRef))
                // The twin on AI readiness is the same problem: when a row has it, or the AI coverage row will, fixing it fixes this one too.
                .Where(c => c.Twin == null
                            || !open.Any(x => string.Equals(x.RuleId, c.Twin, StringComparison.OrdinalIgnoreCase) && SameRef(x.ObjectRef, c.ObjectRef)
                                              && (ClaimedAir(x) || air.Contains(x))))
                .ToList();
            // A finding on an object the cleanup row already lists is the cleanup's to remove, once the person opens it.
            // (The cleanup is optional and carries no rule ids, so the finding stays here unless the cleanup names the same column.)

            var bpaRow = BpaCoverageItem(bpa);
            if (bpaRow != null) yield return bpaRow;
            var airRow = AirCoverageItem(air, f);
            if (airRow != null) yield return airRow;
        }

        /// <summary>True for a readiness finding no row builder takes by its kind: no exact fix, an AI-content finding that is not words, or the like.
        /// The gather reads what each rule is worth only when one exists, so a model with every finding on a row pays no second scan.</summary>
        private static bool MayNeedCoverage(ReadinessFinding x) =>
            x != null && !x.Waived
            && !string.Equals(x.Fix, nameof(FixKind.SafeFix), StringComparison.Ordinal)
            && !IsWordsFinding(x) && !IsProposalFinding(x);

        /// <summary>The words on the open Fix row and on a given Fix task: Review lists more than the task fixes.</summary>
        internal const string ReviewAlsoListsWords = "Review lists these too, with the Info notes (they cost nothing) and the findings that sit on other tasks.";

        private static ModelPlanItem BpaCoverageItem(IReadOnlyList<BpaCoverFinding> found)
        {
            if (found.Count == 0) return null;
            var byRule = found.GroupBy(c => c.RuleId, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Sum(c => c.Points)).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            var points = found.Sum(c => c.Points);
            var why = new List<string>
            {
                CountOf(found.Count, "best-practice issue costs", "best-practice issues cost") + " " + points + " points of the Model quality grade, and no other task fixes "
                    + (found.Count == 1 ? "it" : "them") + ". Your assistant works through them rule by rule. " + ReviewAlsoListsWords,
            };
            foreach (var g in byRule.Take(CoverageRuleLines))
            {
                var first = g.First();
                var names = Join(g.Select(c => c.ObjectName).Where(n => !string.IsNullOrWhiteSpace(n)), 3);
                why.Add(PlainRuleTitle(first.RuleName) + ": " + CountOf(g.Count(), "object", "objects") + " (" + names + "), "
                        + g.Sum(c => c.Points) + " points. " + first.Brief);
            }
            if (byRule.Count > CoverageRuleLines)
                why.Add("And " + CountOf(byRule.Count - CoverageRuleLines, "more rule", "more rules") + ". bpa_scan lists every finding.");
            // The fixes the bulk pass withholds are decisions the assistant makes, not questions for the person (Kane, 13 Oct).
            foreach (var g in found.Where(c => c.DecisionKind != null).GroupBy(c => c.DecisionKind, StringComparer.Ordinal))
                why.Add(DecisionLine(g.Key, g.ToList()));

            var touches = found.Select(c => new PlanTouch
            {
                Table = TableOfRef(c.ObjectRef), Kind = KindOfRef(c.ObjectRef), Name = Qualified(c.ObjectRef, c.ObjectName), Line = null,
            }).ToArray();
            return new ModelPlanItem
            {
                Id = CoverageBpaItemId,
                Section = ModelPlanSections.Structure,
                Title = "Fix " + CountOf(found.Count, "best-practice issue", "best-practice issues"),
                Sub = Join(byRule.Select(g => PlainRuleTitle(g.First().RuleName)).Where(n => !string.IsNullOrWhiteSpace(n)), 4),
                Value = "fewer best-practice issues",
                Rules = byRule.Select(g => g.Key).ToArray(),
                Goal = PlanGoals.Quality,
                Who = ModelPlanOwners.Assistant,
                Status = ModelPlanStatuses.Proposed,
                Why = why.ToArray(),
                Diff = Array.Empty<string>(),
                Touches = Objects(found.Select(c => c.ObjectRef).Distinct(StringComparer.OrdinalIgnoreCase).Count()) + "; the assistant fixes each one",
                Tables = TableDetail(touches),
                Objects = found.Select(c => c.ObjectRef).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Claims = found.Select(c => ClaimKey(c.RuleId, c.ObjectRef)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Jump = new ModelPlanJump { Tool = "bpa", Label = "Model quality" },
            };
        }

        /// <summary>Most rules the coverage row names one by one; the rest are counted. Keeps a long list readable.</summary>
        private const int CoverageRuleLines = 10;

        private static string DecisionLine(string kind, IReadOnlyList<BpaCoverFinding> found)
        {
            var names = Join(found.Select(c => Qualified(c.ObjectRef, c.ObjectName)), 4);
            switch (kind)
            {
                case "datatype":
                    return CountOf(found.Count, "column holds", "columns hold") + " floating point numbers (" + names + "). Your assistant checks the values. "
                         + "If 4 decimal places are enough, it changes the type with each finding's own fix (apply_fix, system bpa), tells you so, and one Undo "
                         + "takes it back. It keeps the type when the values need more decimals.";
                case "rename":
                    return "Your assistant chooses the new name for " + names + " and renames it (rename_object). One Undo takes it back.";
                case "expression":
                    return "Your assistant rewrites the formula of " + names + " and proves the new one is equal first (verify_dax_equivalence).";
                case "key":
                    return "Your assistant picks the right key column for " + names + ". " + found[0].Decision;
                default:
                    return found[0].Decision + " (" + names + ")";
            }
        }

        private static ModelPlanItem AirCoverageItem(IReadOnlyList<ReadinessFinding> found, PlanFacts f)
        {
            if (found.Count == 0) return null;
            var fixable = found.Where(x => PersonOnlyReason(x) == null).ToList();
            var yours = found.Where(x => PersonOnlyReason(x) != null).ToList();
            var points = f.AirLifts;
            double Worth(IEnumerable<ReadinessFinding> xs) =>
                points == null ? 0 : xs.Sum(x => points.TryGetValue(ReadinessAnalyzer.LiftKey(x.RuleId, x.ObjectRef), out var p) ? p : 0);
            string Pts(IEnumerable<ReadinessFinding> xs)
            {
                var w = Worth(xs);
                return w <= 0 ? "" : ", about " + (w < 0.1 ? "0.1" : w.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)) + " of 100 points";
            }

            var why = new List<string>();
            if (fixable.Count > 0)
                why.Add(CountOf(fixable.Count, "AI readiness issue is", "AI readiness issues are") + " open and no other task fixes "
                        + (fixable.Count == 1 ? "it" : "them") + ". Your assistant works through them rule by rule.");
            var ordered = fixable.GroupBy(x => x.RuleId, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => Worth(g)).ThenByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            foreach (var g in ordered.Take(CoverageRuleLines))
            {
                var first = g.First();
                var names = Join(g.Select(x => x.ObjectName).Where(n => !string.IsNullOrWhiteSpace(n)), 3);
                why.Add(RuleTitle(first) + ": " + CountOf(g.Count(), "object", "objects") + " (" + names + ")" + Pts(g) + ". " + AirBrief(first));
            }
            if (ordered.Count > CoverageRuleLines)
                why.Add("And " + CountOf(ordered.Count - CoverageRuleLines, "more rule", "more rules") + ". ai_readiness_scan lists every finding.");
            foreach (var g in yours.GroupBy(x => x.RuleId, StringComparer.OrdinalIgnoreCase))
            {
                var first = g.First();
                why.Add("Cannot be fixed by your assistant: " + RuleTitle(first) + " (" + Join(g.Select(x => x.ObjectName).Where(n => !string.IsNullOrWhiteSpace(n)), 3) + "). "
                        + PersonOnlyReason(first) + " Accept the finding on AI readiness if it is right for your model.");
            }

            var touches = found.Select(x => new PlanTouch
            {
                Table = TableOfRef(x.ObjectRef), Kind = KindOfRef(x.ObjectRef), Name = Qualified(x.ObjectRef, x.ObjectName), Line = null,
            }).ToArray();
            var mine = fixable.Count > 0;
            return new ModelPlanItem
            {
                Id = CoverageAiItemId,
                Section = ModelPlanSections.Trust,
                Title = mine ? "Fix " + CountOf(fixable.Count, "AI readiness issue", "AI readiness issues")
                             : CountOf(yours.Count, "AI readiness issue", "AI readiness issues") + " for you to decide",
                Sub = Join(found.Select(x => RuleTitle(x)).Distinct(StringComparer.OrdinalIgnoreCase), 4),
                Value = "raises AI readiness",
                Rules = RuleIdsOf(found),
                Goal = PlanGoals.Ai,
                Who = mine ? ModelPlanOwners.Assistant : ModelPlanOwners.You,
                Status = ModelPlanStatuses.Proposed,
                Why = why.ToArray(),
                Diff = Array.Empty<string>(),
                Touches = Objects(found.Select(x => x.ObjectRef).Distinct(StringComparer.OrdinalIgnoreCase).Count()) + (mine ? "; the assistant fixes each one" : ""),
                Tables = TableDetail(touches),
                Objects = RefsOf(found),
                Claims = ClaimsOf(found),
                Jump = new ModelPlanJump { Tool = "readiness", Label = "AI readiness" },
            };
        }

        /// <summary>The brief for a readiness rule with no row of its own: what the finding says, then the tool that reads or fixes it.</summary>
        internal static string AirBrief(ReadinessFinding x)
        {
            var said = (x.DisplayMessage ?? x.Message ?? "").Trim();
            var dot = said.IndexOf(". ", StringComparison.Ordinal);
            if (dot > 0) said = said.Substring(0, dot + 1);
            if (said.Length > 200) said = said.Substring(0, 197).TrimEnd() + "...";
            if (said.Length > 0 && !said.EndsWith(".", StringComparison.Ordinal)) said += ".";
            if ((x.RuleId ?? "").StartsWith("LIMIT-", StringComparison.OrdinalIgnoreCase))
                return (said.Length > 0 ? said + " " : "") + "Do not ask which part. Trim the part with the least use: hide or remove unused fields first, then shorten long text.";
            return (said.Length > 0 ? said + " " : "") + "get_fix_prompt (system air) gives the steps for each object.";
        }
    }
}
