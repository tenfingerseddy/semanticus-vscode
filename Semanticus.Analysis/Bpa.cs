using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Linq.Dynamic; // vendored legacy engine (DynamicLinq.cs) — DynamicQueryable.Where(IQueryable, string)
using TabularEditor.TOMWrapper;

namespace Semanticus.Analysis
{
    /// <summary>A Best Practice Analyzer rule (schema-compatible with the standard BPARules.json).</summary>
    public sealed class BpaRule
    {
        public string ID { get; set; } = "";
        public string Name { get; set; }
        public string Category { get; set; } = "";
        public int Severity { get; set; } = 2;        // 1=info, 2=warning, 3=error (BPA convention)
        public string Scope { get; set; } = "";        // e.g. "Measure" or "DataColumn, CalculatedColumn"
        public string Expression { get; set; }          // Dynamic-LINQ predicate; a match is a violation
        public string FixExpression { get; set; }       // optional deterministic fix: "Prop = value"
        public string Description { get; set; }
        public int CompatibilityLevel { get; set; }

        /// <summary>Provenance: true when this rule came from the model's BestPracticeAnalyzer annotation (a
        /// user/org rule loaded via load_rules with system='bpa'), false for the bundled standard set. Set by the engine when it
        /// composes the effective rule set; never serialized (the annotation stores the schema fields only).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool FromModelAnnotation { get; set; }

        /// <summary>An optional token-path predicate that REPLACES Dynamic-LINQ evaluation of <see cref="Expression"/>
        /// when set (the Expression string is kept for TE-compat/export and is not evaluated). Its purpose is the DAX
        /// rules whose text form false-fires — a bare <c>Expression.Contains("IFERROR")</c> trips on comments, strings,
        /// and <c>[bracketed names]</c>; the token path (<see cref="DaxScan"/>) matches only real syntax nodes. Community
        /// rulesets that never set this behave exactly as before. Supported forms:
        /// <list type="bullet">
        /// <item><c>"calls:NAME[,NAME2…]"</c> — the expression contains a real call node to ANY of the listed functions.</item>
        /// <item><c>"filter-bare-table"</c> — a FILTER whose first argument is a bare table reference.</item>
        /// </list></summary>
        public string TokenCheck { get; set; }

        /// <summary>True for a rule that misreads a field parameter, so its table and that table's columns are never
        /// flagged by it. A field parameter is what Power BI Desktop builds for a field switcher: a calculated table
        /// of NAMEOF references with a hidden Fields column nothing references by name and no relationship, which is
        /// exactly how Desktop makes it. DAC-FIELD-PARAM-COMPLEXITY in the readiness rules already covers the real
        /// cost of one. The detection is the one readiness uses (<see cref="ReadinessRuleSet.IsFieldParameterTable"/>),
        /// so the two scans never disagree about what is a field parameter. Not written when false, so a user rule
        /// saved on the model does not gain the field.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public bool ExemptFieldParameters { get; set; }
    }

    public sealed class BpaViolation
    {
        public string RuleId { get; set; }
        public string RuleName { get; set; }
        public string Category { get; set; }
        public int Severity { get; set; }
        public string ObjectRef { get; set; }
        public string ObjectName { get; set; }
        public string Message { get; set; }
        public bool CanAutoFix { get; set; }            // deterministic FixExpression that we can apply
        /// <summary>A format finding whose fix the format fill rule withholds (<see cref="BpaAnalyzer.IsFormatAdvice"/>):
        /// advice for a person or their assistant to decide, not a fault, so the deploy gate skips it (<see cref="IsAdviceTheGateSkips"/>).</summary>
        public bool Advice { get; set; }
        /// <summary>Advice the deploy gate lets pass. A model's OWN severity 3 rule is the model's policy: its finding keeps the advice
        /// words (its fix is still withheld, so the assistant is told to ask the person), but it is an error the model wrote for itself
        /// and it keeps blocking (Sol 4.3). Only a bundled rule, or the model's own below severity 3, is advice the gate skips.</summary>
        public bool IsAdviceTheGateSkips() => Advice && !(Custom && Severity >= 3);
        public bool Waived { get; set; }                // an accepted finding: surfaced but excluded from ViolationCount
        public string WaiverReason { get; set; }        // why it was accepted (null unless Waived); on a related finding, the words that say so
        public bool WaiverRuleLevel { get; set; }        // true when waived by a rule-level (model-wide) waiver, not a per-instance one
        public bool Custom { get; set; }                 // provenance: true = a model-embedded (user/org) rule fired, false = the bundled standard set
        /// <summary>True when another accepted finding covers this one's change (<see cref="DeclinedChanges"/>): every path leaves the
        /// change alone, but nobody accepted THIS finding, so it is not an accepted finding. It carries <see cref="Waived"/> for the paths
        /// that ask "is this change left alone", and it is open for everything that counts: the page lists it open with
        /// <see cref="DeclinedChanges.TwinReason"/>, the score and the counts include it, and <see cref="IsAccepted"/> is false. A finding
        /// that blocks the deploy gate is never tagged this way (see <see cref="CoveredBy"/>): it stays fully open.</summary>
        public bool WaivedAsTwin { get; set; }

        /// <summary>True only for a finding the person accepted, by its own rule id and object (or its rule for the whole model). The
        /// pages' accepted list, the score, the counts and the deploy gate read this and nothing else; what no path may WRITE is
        /// decided separately, by <see cref="DeclinedChanges"/>.</summary>
        public bool IsAccepted() => Waived && !WaivedAsTwin;
        /// <summary>True for a row that stands for a stored acceptance whose finding no longer fires (nothing to fix now, but
        /// the acceptance still keeps a change from being made), so it is never invisible while it is still declining.</summary>
        public bool NoLongerFires { get; set; }
        /// <summary>For a finding another acceptance covers (a twin, or a blocker left open beside it): the acceptance it stands beside, as
        /// "rule name (page)", and the reason the person gave for it, so the row says which acceptance is the related one.</summary>
        public string CoveredBy { get; set; }
        public string CoveredReason { get; set; }
        /// <summary>True for the one row that stands for a rule-level acceptance, listed whether or not the rule fires anywhere
        /// (<see cref="DeclinedChanges.WholeModelName"/>). It is an acceptance, not a finding: the deploy gate never counts it.</summary>
        public bool WholeRuleRow { get; set; }
    }

    /// <summary>A rule/scope pair the scan could NOT evaluate to completion — a first-class UNKNOWN, not an absence.
    /// The evaluation of a rule over a scope is lazy (<c>collection.Where(rule.Expression)</c>), so a user expression
    /// that throws part way through aborts the enumeration: whatever violations had already been collected are a
    /// PREFIX of the real answer, and the rest of the scope is simply not known. Reporting that as an ordinary
    /// (short) violation list is a scan under-reporting and reading clean, so it gets its own state instead.
    ///
    /// What we deliberately do NOT carry is "evaluated N of M objects". The throw happens inside the lazy predicate,
    /// so the number of objects visited before it is not observable from here; a count would have to be invented.
    /// <see cref="ObjectsInScope"/> and <see cref="PartialViolations"/> are both directly substantiated — the first by
    /// counting the collection without running the expression, the second by what the scan actually added.</summary>
    public sealed class BpaRuleUnknown
    {
        public string RuleId { get; set; }
        public string RuleName { get; set; }
        public string Category { get; set; }
        public int Severity { get; set; }
        public string Scope { get; set; }
        public string Reason { get; set; }
        /// <summary>ALWAYS false. Carried as a field, not dropped, so an unknown still answers the question every
        /// other finding answers, but the answer is fixed, because an unknown has nothing to fix. An unsupported
        /// scope produced no object for a fix to be applied to, and an evaluation that aborted never identified the
        /// objects in the part of the scope it did not reach. Nothing would apply one either: <c>BpaFixAllAsync</c>
        /// and plan seeding both iterate <see cref="BpaScorecard.Violations"/>, so no code path has ever read a
        /// <see cref="BpaRuleUnknown"/> and repaired unknown coverage.
        ///
        /// It used to be <c>CanAutoFix(rule.FixExpression)</c> on BOTH paths, a syntax question about a string with
        /// no object in it, and a "true" answer switched <see cref="BlocksGate"/> off. That let a parseable
        /// assignment clear a deployment block no fix pass could ever have earned, which is the same switched-off
        /// -check defect the unknown machinery exists to end.</summary>
        public bool CanAutoFix { get; set; }
        /// <summary>True when this rule, HAD it fired, would have blocked the deploy gate — the same
        /// <c>Severity &gt;= 2 &amp;&amp; !CanAutoFix</c> predicate the gate uses. <see cref="CanAutoFix"/> is always
        /// false for an unknown, so here that predicate reduces to <c>Severity &gt;= 2</c>. An unknown in that
        /// population must block, because "we could not check" is not "there is nothing to find". Both unknown paths
        /// use it: an absent evaluator mapping does not prove the population is zero, and an aborted evaluation does
        /// not prove the remainder is clean.</summary>
        public bool BlocksGate { get; set; }
        /// <summary>M: the objects the rule WOULD have been evaluated over. Counting the collection never runs the
        /// rule expression, so this is a real number even when the evaluation aborted. 0 for an unsupported scope.</summary>
        public int ObjectsInScope { get; set; }
        /// <summary>Violations this rule had already collected on this scope before the abort. They are kept in
        /// <see cref="BpaScorecard.Violations"/> because each one is a true positive the predicate really returned,
        /// and dropping real findings would make the model look CLEANER than it is — the wrong direction. Coverage of
        /// the remainder is unknown, which is what this record exists to say.</summary>
        public int PartialViolations { get; set; }
    }

    public sealed class BpaScorecard
    {
        public int RuleCount { get; set; }
        public int ViolationCount { get; set; }         // ACTIVE (un-waived) violations
        public int AutoFixable { get; set; }
        public int WaivedCount { get; set; }            // accepted findings (always surfaced, never hidden)
        public BpaViolation[] Violations { get; set; } = Array.Empty<BpaViolation>();
        public string[] RuleErrors { get; set; } = Array.Empty<string>();
        /// <summary>Rule/scope pairs that could not be evaluated. UNKNOWN is a value here, not an empty list: a
        /// caller reading <see cref="ViolationCount"/> alone would otherwise see a truncated scope as a clean one.
        /// <see cref="RuleErrors"/> is kept as the authoring-lane message channel; this is the answer.</summary>
        public BpaRuleUnknown[] Unknowns { get; set; } = Array.Empty<BpaRuleUnknown>();
        public int UnknownCount { get; set; }            // = Unknowns.Length; carried so token-light projections can report it
        /// <summary>Stored "bpa" waivers whose rule id is ABSENT from the loaded rule set — the accepted-finding
        /// decision points at a rule that no longer exists (replacing a rule corpus renames ids wholesale). An orphan
        /// suppresses nothing and is NOT part of <see cref="WaivedCount"/>; it is its own state, so a dead waiver can
        /// never read as protection that isn't there. Never auto-deleted: the user's intent is data.</summary>
        public WaiverRecord[] OrphanedWaivers { get; set; } = Array.Empty<WaiverRecord>();
        public int OrphanedWaiverCount { get; set; }     // = OrphanedWaivers.Length; carried so token-light projections can report it
        /// <summary>Distinct model objects the scan evaluated at least one rule over, partitions not counted. The letter
        /// divides by it, so a big model is not punished for being big. Partitions are left out because the table
        /// already stands for them: an incremental refresh policy splits one table into many, and the same design must
        /// not grade differently by where it was opened. 0 on a scoped (ambient) scan, which never evaluates the whole model.</summary>
        public int ObjectsScanned { get; set; }
        /// <summary>The "Best practice" letter, A to F, by <see cref="BpaGrading"/>. Null on a scoped scan: a letter is
        /// a claim about the whole model, and a scoped scan never looked at the whole model. Named BestPractice on every
        /// surface, never "grade", which is what ai_readiness_scan calls the other letter.</summary>
        public string BestPractice { get; set; }
        /// <summary>Weighted open errors and warnings per object scanned, exactly as the why sentence shows it.</summary>
        public double BestPracticeWeight { get; set; }
        /// <summary>One plain sentence saying why the model has this letter.</summary>
        public string BestPracticeWhy { get; set; }
        /// <summary>The same letter as a 0 to 100 score for the page's big number and bars, by <see cref="BpaGrading.Score"/>. 100 only when
        /// the model is clean; null-equivalent 0 on a scoped scan, which carries no letter.</summary>
        public int BestPracticeScore { get; set; }
        /// <summary>One score per rule category (Formatting, Performance, ...), by the same points rule as the letter but over the objects that
        /// category's rules checked, so each bar reads how dense that kind of problem is. Empty on a scoped scan.</summary>
        public BpaCategoryScore[] Categories { get; set; } = Array.Empty<BpaCategoryScore>();
    }

    /// <summary>One category's open errors and warnings, the objects its rules checked, and the 0 to 100 score they give.</summary>
    public sealed class BpaCategoryScore
    {
        public string Category { get; set; }
        public int Errors { get; set; }
        public int Warnings { get; set; }
        public int ObjectsChecked { get; set; }
        public int Score { get; set; }
    }

    /// <summary>
    /// The "Best practice" letter grade (Kane, 28 Sep: "AI readiness and BPA is best practice at A grade").
    ///
    /// The rule, in full:
    ///   A: no open Error and no open Warning. A waived finding (a Semanticus waiver or a Tabular Editor ignore)
    ///      counts as passed. A severity 2 or 3 rule the scan could not run also stops an A, because "could not
    ///      check" is not "clean".
    ///   Info notes carry no weight at any letter (coordinator ruling, 28 Sep): they never block an A, so they must not
    ///   decide B to F either, or one warning on a model with many notes reads as a D.
    ///   Below A, open errors weigh 5 and open warnings 2, and the total is divided by the objects scanned (partitions
    ///   not counted), so the letter reads how dense the problems are, not how big the model is:
    ///   B up to 0.10 per object, C over 0.10 up to 0.25, D over 0.25 up to 0.50, F over 0.50.
    /// The bands are compared on the exact ratio (whole-number points against whole-number objects), never on a
    /// rounded one, and the sentence prints enough places that the number shown sits inside its letter's band.
    /// The letter is computed from the scan the engine already ran; nothing else.
    /// </summary>
    public static class BpaGrading
    {
        public const double ErrorWeight = 5, WarningWeight = 2, InfoWeight = 0;
        public const double BMax = 0.10, CMax = 0.25, DMax = 0.50;

        /// <summary>The rule in plain words: the bpa_scan description embeds it, and the Model quality help carries
        /// each sentence of it (a test holds the two together).</summary>
        public const string Rule =
            "Best practice is A when the model has no open errors and no open warnings. "
            + "Info notes never count against the letter. "
            + "A finding you accepted counts as passed. "
            + "An error or warning rule that could not be checked also stops an A. "
            + "Below A, each open error counts 5 points and each open warning 2 points. "
            + "The points are divided by the number of objects checked, so a big model is not marked down for its size. "
            + "Tables, columns, measures, relationships, roles and the like are objects; partitions are not, because the table already stands for them. "
            + "B is up to 0.10 points per object, C is over 0.10 up to 0.25, D is over 0.25 up to 0.50, and F is over 0.50.";

        /// <summary>The id of the built-in rule for a formula that names an object the model does not have. A finding of it stops an A even when it is accepted:
        /// a number that cannot be computed is not made right by a reason (plan statement check 4).</summary>
        public const string BrokenFormulaRuleId = "FORMULA_REFERENCE_NOT_IN_MODEL";

        public static (string Letter, double Weight, string Why) Compute(int errors, int warnings, int info, int objects, int blockingUnknowns, int brokenFormulas = 0)
        {
            static string N(int n, string one, string many) => n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
            // Whole-number points over whole-number objects, so the band edges compare exactly, with no rounding step
            // that could pull 0.10025 down into B.
            var n = Math.Max(1, objects);
            var points = (long)errors * (long)ErrorWeight + (long)warnings * (long)WarningWeight;
            if (errors == 0 && warnings == 0 && blockingUnknowns == 0 && brokenFormulas == 0)
                return ("A", 0, info == 0 ? "Best practice A: no open errors, warnings or notes." : $"Best practice A: no open errors or warnings, {N(info, "info note", "info notes")}.");
            var (letter, low, band) = points * 100 <= 10L * n ? ("B", 0m, "B is up to 0.10")
                : points * 100 <= 25L * n ? ("C", 0.10m, "C is over 0.10 up to 0.25")
                : points * 100 <= 50L * n ? ("D", 0.25m, "D is over 0.25 up to 0.50")
                : ("F", 0.50m, "F is over 0.50");
            // Two places, or more when two would round the number down onto the band's lower edge (0.101 is a C, and
            // "0.10" beside "C is over 0.10" would contradict it).
            var exact = (decimal)points / n;
            var places = 2;
            var shown = Math.Round(exact, places, MidpointRounding.AwayFromZero);
            while (shown <= low && exact > low && places < 12) shown = Math.Round(exact, ++places, MidpointRounding.AwayFromZero);
            var text = shown.ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            var unknownClause = blockingUnknowns == 0 ? ""
                : $" {N(blockingUnknowns, "error or warning rule", "error or warning rules")} could not be checked either.";
            var brokenClause = brokenFormulas == 0 ? ""
                : $" {N(brokenFormulas, "formula refers", "formulas refer")} to an object the model does not have, so A cannot be claimed.";
            var why = errors + warnings > 0
                ? $"Best practice {letter}: {N(errors, "open error", "open errors")} and {N(warnings, "warning", "warnings")} across "
                  + $"{N(n, "object", "objects")} checked, {text} weighted per object ({band}). A needs no open errors or warnings.{unknownClause}{brokenClause}"
                : blockingUnknowns > 0
                    ? $"Best practice {letter}: {N(blockingUnknowns, "error or warning rule", "error or warning rules")} could not be checked, so A cannot be claimed.{brokenClause}"
                    : $"Best practice {letter}:{brokenClause}";
            return (letter, (double)shown, why);
        }

        /// <summary>The letter as a 0 to 100 score, so the page can draw a bar in the same bands as AI readiness (80 and up good, 60 to 79 warn,
        /// under 60 bad). The edges are the letter's: 0.10 points per object is 80 (the top of B), 0.50 is 60 (the top of D). 100 only when
        /// there is nothing open. The exact ratio is cut in decimals and the score is rounded DOWN, so a model just past a band edge never
        /// reads as the better band.</summary>
        public static int Score(int errors, int warnings, int objects)
        {
            var points = (long)errors * (long)ErrorWeight + (long)warnings * (long)WarningWeight;
            if (points == 0) return 100;
            var w = (decimal)points / Math.Max(1, objects);
            var raw = w <= 0.10m ? 100m - 200m * w
                : w <= 0.50m ? 80m - 50m * (w - 0.10m)
                : 60m - 40m * (w - 0.50m);
            return (int)Math.Max(0m, Math.Min(99m, Math.Floor(raw)));
        }

        public static void Stamp(BpaScorecard c)
        {
            // Open = not ACCEPTED. A finding set aside as a twin of another acceptance is still open (nobody accepted this one), so it
            // counts, exactly as it does in the finding count and at the deploy gate (Sol 4.1).
            var open = c.Violations.Where(v => !v.IsAccepted()).ToArray();
            var (letter, weight, why) = Compute(open.Count(v => v.Severity >= 3), open.Count(v => v.Severity == 2),
                open.Count(v => v.Severity <= 1), c.ObjectsScanned, c.Unknowns.Count(u => u.BlocksGate),
                c.Violations.Count(v => string.Equals(v.RuleId, BrokenFormulaRuleId, StringComparison.Ordinal)));   // every one, accepted or not
            c.BestPractice = letter; c.BestPracticeWeight = weight; c.BestPracticeWhy = why;
            c.BestPracticeScore = Score(open.Count(v => v.Severity >= 3), open.Count(v => v.Severity == 2), c.ObjectsScanned);
        }
    }

    /// <summary>
    /// Headless Best Practice Analyzer. Reuses the donor's two genuinely-valuable, self-contained
    /// pieces — the legacy System.Linq.Dynamic engine and the scope→collection map — with a clean
    /// rule/violation model of our own (no WinForms/NUnit baggage). Auto-fix applies simple
    /// "Prop = value" FixExpressions deterministically; everything else routes to the agent.
    /// </summary>
    public static class BpaAnalyzer
    {
        public static BpaScorecard Analyze(Model model, IReadOnlyList<BpaRule> rules) => Analyze(model, rules, null);

        /// <summary>Scoped variant (the health-delta ambient path): evaluate each rule ONLY over objects whose
        /// stable ref is in <paramref name="scopeRefs"/> — killing the per-object Dynamic-LINQ / DAX-tokenization
        /// cost of a full sweep when only a handful of objects were touched. Semantics per rule are unchanged
        /// (the same predicate runs, just over a pre-filtered population); collections with no in-scope object are
        /// skipped WITHOUT a rule error (out-of-scope is not "unsupported"). Model-scope rules only run when the
        /// model's own ref is in scope, so a scoped card never re-litigates model-wide rules. null = full scan.</summary>
        public static BpaScorecard Analyze(Model model, IReadOnlyList<BpaRule> rules, ICollection<string> scopeRefs)
        {
            var violations = new List<BpaViolation>();
            var ruleErrors = new List<string>();
            var unknowns = new List<BpaRuleUnknown>();
            var modelCl = model.Database?.CompatibilityLevel ?? 0;
            var scanned = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var scannedByCategory = new Dictionary<string, HashSet<object>>(StringComparer.OrdinalIgnoreCase);
            // The reference check reads the model's names once per scan, however many formulas it is asked about (TokenCheck "missing-objects").
            Func<string, string, string[]> missingObjects = null;
            // A scoped scan filters each scope's collection to the touched objects once, not once per rule: about 70 rules share
            // 17 scopes, and the filter walks the whole collection to find a handful of objects.
            var inScopeByScope = scopeRefs == null ? null : new Dictionary<string, IQueryable>(StringComparer.Ordinal);

            // Counting a collection never runs the rule expression, so M stays knowable even when the evaluation of
            // the expression over it did not finish. Defended anyway: this must never be the thing that throws.
            static int CountInScope(IQueryable c)
            {
                try { return c == null ? 0 : c.Cast<object>().Count(); } catch { return 0; }
            }

            foreach (var rule in rules)
            {
                // A token-only rule legitimately has no Expression (TokenCheck REPLACES it) — only skip when the
                // rule has NEITHER evaluation form, else a hand-authored token rule would silently vanish.
                if (string.IsNullOrWhiteSpace(rule.Expression) && string.IsNullOrWhiteSpace(rule.TokenCheck)) continue;
                if (rule.CompatibilityLevel > 0 && rule.CompatibilityLevel > modelCl) continue;

                foreach (var scope in ExpandScopes(rule.Scope))
                {
                    var collection = GetCollection(model, scope);
                    if (collection == null)
                    {
                        // A scope we don't map (e.g. a community rule targeting PartitionSource / Variation /
                        // TablePermission). Surface it instead of silently dropping the rule's coverage. It is an
                        // UNKNOWN too — the rule's coverage of that scope is absent, not clean. BlocksGate uses the
                        // same predicate as an evaluation failure: an absent mapping does not prove the population
                        // is zero, so a severity-2/3 rule that we could not run still blocks.
                        //
                        // CanAutoFix is FALSE here, unconditionally, and it used to be CanAutoFix(rule.FixExpression).
                        // There is no object on this path, which is what "unsupported scope" means, so a fix has
                        // nothing to be applied to, and a parseable assignment was only ever a syntax fact about a
                        // string. Letting it clear BlocksGate switched a severity-2/3 check off in exchange for a
                        // repair nothing performs: no fix pass reads Unknowns at all (see BpaRuleUnknown.CanAutoFix).
                        ruleErrors.Add($"{rule.ID} [{scope}]: unsupported scope");
                        unknowns.Add(new BpaRuleUnknown
                        {
                            RuleId = rule.ID, RuleName = rule.Name, Category = rule.Category, Severity = rule.Severity,
                            Scope = scope, CanAutoFix = false,
                            BlocksGate = rule.Severity >= 2,
                            ObjectsInScope = 0, PartialViolations = 0,
                            Reason = $"scope '{scope}' is not one Semanticus maps, so this rule could not be evaluated over it. "
                                     + $"Supported scopes: {string.Join(", ", SupportedScopeNames)}.",
                        });
                        continue;
                    }
                    if (scopeRefs != null)
                    {
                        if (!inScopeByScope.TryGetValue(scope, out var inScope)) inScopeByScope[scope] = inScope = FilterToRefs(collection, scopeRefs);
                        collection = inScope;
                        if (collection == null) continue;   // nothing touched in this collection — skip the eval entirely
                    }
                    // Partitions are scanned (their findings weigh) but not counted as objects: see ObjectsScanned.
                    if (scopeRefs == null)
                    {
                        var cat = string.IsNullOrWhiteSpace(rule.Category) ? "Other" : rule.Category;
                        if (!scannedByCategory.TryGetValue(cat, out var inCategory)) scannedByCategory[cat] = inCategory = new HashSet<object>(ReferenceEqualityComparer.Instance);
                        try { foreach (var o in collection) if (o != null && o is not Partition) { scanned.Add(o); inCategory.Add(o); } } catch { /* counting must never be what throws */ }
                    }
                    // One violation-construction block for BOTH evaluation paths (Dynamic-LINQ and token). It carries the
                    // TE per-object ignore honouring; the Semanticus waiver pass below runs on top of whatever it tags.
                    void AddViolation(ITabularNamedObject obj)
                    {
                        if (rule.ExemptFieldParameters && OnFieldParameter(obj)) return;
                        var teIgnored = WaiverStore.IsTeIgnored(obj, rule.ID);
                        violations.Add(new BpaViolation
                        {
                            RuleId = rule.ID,
                            RuleName = rule.Name,
                            Category = rule.Category,
                            Severity = rule.Severity,
                            ObjectRef = Refs.For(obj),
                            ObjectName = (obj as IDaxObject)?.DaxObjectFullName ?? obj.Name,
                            Message = Describe(rule, obj),
                            CanAutoFix = CanAutoFix(rule, obj),   // per-object: a fix that cannot convert here is not fixable here
                            Advice = IsFormatAdvice(rule, obj),
                            Custom = rule.FromModelAnnotation,
                            Waived = teIgnored,
                            WaiverReason = teIgnored ? WaiverStore.TeReason : null,
                        });
                    }

                    // Watermark the violation list so a throw can report exactly how much of this rule/scope's answer
                    // we actually got. Both evaluation paths below are LAZY over a user-supplied predicate, so either
                    // can abort part way through with a prefix already collected.
                    var collectedBefore = violations.Count;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(rule.TokenCheck))
                        {
                            // Token path: skip Dynamic-LINQ entirely for this rule+scope and match objects in C# so the
                            // DAX rules can't false-fire on text inside comments/strings/[names]/'tables' (DaxScan drops them).
                            foreach (var obj in collection.OfType<ITabularNamedObject>())
                                if (TokenMatch(rule.TokenCheck, obj, () => missingObjects ??= FormulaReferences.HostChecker(model)))
                                    AddViolation(obj);
                        }
                        else
                        {
                            // The same lazy walk as collection.Where(rule.Expression).OfType<ITabularNamedObject>(), with the
                            // predicate compiled once per process instead of parsed and compiled again on every scan.
                            var matches = CompiledPredicate(collection.ElementType, rule.Expression);
                            foreach (var o in collection)
                                if (matches(o) && o is ITabularNamedObject obj) AddViolation(obj);
                        }
                    }
                    catch (Exception ex)
                    {
                        // The enumeration aborted. Whatever is in `violations` past the watermark is a PREFIX of this
                        // rule's real answer on this scope; the remainder is unknown. Keep the prefix (each entry is a
                        // true positive the predicate really returned, and discarding real findings would flatter the
                        // model) and record the scope as UNKNOWN so nothing downstream can read the short list as complete.
                        //
                        // CanAutoFix is FALSE here, unconditionally, and it used to be CanAutoFix(rule.FixExpression).
                        // The prefix violations keep their own object-aware fixability above (AddViolation), because
                        // those objects are known; the UNKNOWN is the rest of the scope, and the abort means its
                        // violating objects were never identified. A fix cannot be applied to an object nobody named,
                        // so a parseable assignment must not switch this block off either.
                        ruleErrors.Add($"{rule.ID} [{scope}]: {ex.Message}");
                        unknowns.Add(new BpaRuleUnknown
                        {
                            RuleId = rule.ID, RuleName = rule.Name, Category = rule.Category, Severity = rule.Severity,
                            Scope = scope, CanAutoFix = false,
                            BlocksGate = rule.Severity >= 2,   // the gate's Severity >= 2 && !CanAutoFix, with CanAutoFix false
                            ObjectsInScope = CountInScope(collection),
                            PartialViolations = violations.Count - collectedBefore,
                            Reason = $"the rule expression failed part way through scope '{scope}', so this rule could not be evaluated "
                                     + "on this model: the findings shown for it are whatever was collected before the failure, and the "
                                     + $"coverage of the rest of the scope is unknown. Underlying error: {ex.Message}",
                        });
                    }
                }
            }

            // De-dup (a rule with multiple scopes can hit the same object once per scope).
            var deduped = violations
                .GroupBy(v => v.RuleId + "␟" + v.ObjectRef)
                .Select(g => g.First())
                .OrderByDescending(v => v.Severity)
                .ThenBy(v => v.Category, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Apply Semanticus waivers (rule-level + per-instance) on top of the TE per-object ignores tagged above.
            // Waived violations are kept in the list (surfaced) but drop out of the counts so the model "scores" better.
            var waivers = WaiverStore.Load(model);
            if (waivers.Count > 0)
                foreach (var v in deduped)   // a Semanticus waiver's authored reason takes precedence over the generic TE-ignore label
                {
                    var w = WaiverStore.Match(waivers, "bpa", v.RuleId, v.ObjectRef);
                    if (w != null) { v.Waived = true; v.WaiverReason = w.Reason; v.WaiverRuleLevel = WaiverStore.IsRuleLevel(w.ObjectRef); continue; }
                    // The same problem accepted on AI readiness is accepted here too (one decision, two pages; PairedAcceptances).
                    var twin = PairedAcceptances.TwinAcceptance(waivers, "bpa", v.RuleId, v.ObjectRef);
                    if (twin != null)
                    {
                        v.Waived = true; v.WaiverReason = twin.Reason; v.WaiverRuleLevel = WaiverStore.IsRuleLevel(twin.ObjectRef);
                        v.CoveredBy = "the same finding on AI readiness"; v.CoveredReason = twin.Reason;
                    }
                }

            // A waiver declines a CHANGE, not a rule id: a finding whose change another accepted finding declined (this rule's
            // twin, in either system) reads as accepted too, so Model quality, AI readiness and the change plan agree.
            var declined = DeclinedChanges.Read(model, rules);
            if (!declined.IsEmpty)
            {
                var byRule = new Dictionary<string, BpaRule>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in rules) byRule[r.ID] = r;
                foreach (var v in deduped)
                {
                    if (v.Waived || !byRule.TryGetValue(v.RuleId, out var rule)) continue;
                    var about = RuleAboutTable.ForBpa(rule);
                    if (about?.Property == null) continue;
                    var cover = declined.CoverFor("bpa", v.RuleId, v.ObjectRef, about.Property);
                    if (cover == null) continue;
                    v.CoveredBy = cover.Label; v.CoveredReason = cover.Reason;
                    // A related acceptance never lifts a deploy-gate blocker: the gate reads accepted findings by their own rule id
                    // alone, so a blocker the person did not accept stays open and blocking, with the same words on its row.
                    if (v.Severity >= 2 && !v.CanAutoFix) { v.WaiverReason = DeclinedChanges.TwinReason; continue; }
                    v.Waived = true; v.WaivedAsTwin = true; v.WaiverReason = DeclinedChanges.TwinReason;
                }
            }

            // An acceptance stays in force while it is stored, whether or not its finding still fires, so a full scan lists a
            // row for each stored per-object acceptance that declines a change and has no finding now (nothing to fix, but it
            // still keeps a change from being made). Never on a scoped scan: that one only sees part of the model.
            if (scopeRefs == null && !declined.IsEmpty)
            {
                var unknownRules = new HashSet<string>(unknowns.Select(u => u.RuleId), StringComparer.OrdinalIgnoreCase);
                var present = new HashSet<string>(deduped.Select(v => v.RuleId + "\u241f" + WaiverStore.CanonicalRef(v.ObjectRef)), StringComparer.OrdinalIgnoreCase);
                var byRule = rules.ToDictionary(r => r.ID, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> names = null;
                foreach (var src in declined.Sources.Where(x => x.System == "bpa" && !x.RuleLevel))
                {
                    if (present.Contains(src.RuleId + "\u241f" + src.ObjectRef)) continue;
                    if (!byRule.TryGetValue(src.RuleId, out var rule) || unknownRules.Contains(rule.ID)) continue;
                    if (string.IsNullOrWhiteSpace(rule.Expression) && string.IsNullOrWhiteSpace(rule.TokenCheck)) continue;
                    if (rule.CompatibilityLevel > 0 && rule.CompatibilityLevel > modelCl) continue;
                    names ??= WaiverStore.AllObjects(model).OfType<ITabularNamedObject>()
                        .GroupBy(o => WaiverStore.CanonicalRef(Refs.For(o)), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => (g.First() as IDaxObject)?.DaxObjectFullName ?? g.First().Name, StringComparer.OrdinalIgnoreCase);
                    if (!names.TryGetValue(src.ObjectRef, out var name)) continue;   // the object is gone: nothing left to keep
                    deduped.Add(new BpaViolation
                    {
                        RuleId = rule.ID, RuleName = rule.Name, Category = rule.Category, Severity = rule.Severity,
                        ObjectRef = src.ObjectRef, ObjectName = name, Message = DeclinedChanges.NoLongerFiresText,
                        Waived = true, WaiverReason = src.Reason, Custom = rule.FromModelAnnotation, NoLongerFires = true,
                    });
                    present.Add(src.RuleId + "\u241f" + src.ObjectRef);
                }
            }

            // A rule-level acceptance always has a row of its own on this page, whether or not its rule fires anywhere, so it is
            // never invisible while it keeps declining (round 7). Where the rule fires the accepted findings are listed already, each
            // tagged "whole rule", and that row is reopened there. Never on a scoped scan: that one only sees part of the model.
            if (scopeRefs == null)
            {
                var ruleById = rules.GroupBy(r => r.ID, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                foreach (var w in waivers.Where(x => string.Equals(x.System, "bpa", StringComparison.OrdinalIgnoreCase) && WaiverStore.IsRuleLevel(x.ObjectRef)))
                {
                    if (!ruleById.TryGetValue(w.RuleId, out var rule)) continue;   // a retired rule id is an orphan, reported below
                    if (deduped.Any(v => v.WaiverRuleLevel && string.Equals(v.RuleId, rule.ID, StringComparison.OrdinalIgnoreCase))) continue;
                    deduped.Add(new BpaViolation
                    {
                        RuleId = rule.ID, RuleName = rule.Name, Category = rule.Category, Severity = rule.Severity,
                        ObjectRef = WaiverStore.ModelRef, ObjectName = DeclinedChanges.WholeModelName,
                        Message = DeclinedChanges.WholeRuleMessage(RuleAboutTable.ForBpa(rule)),
                        Waived = true, WaiverReason = w.Reason, WaiverRuleLevel = true, WholeRuleRow = true, Custom = rule.FromModelAnnotation,
                    });
                }
            }

            // Reconcile the store against the LOADED rule ids. The match pass above can only ever visit waivers whose
            // rule produced a violation, so a waiver for a retired rule id would otherwise be invisible here — present
            // in the store, counted nowhere, protecting nothing. Surface it as its own state instead.
            var orphans = WaiverStore.Orphans(waivers, "bpa", rules.Select(r => r.ID), model).ToArray();

            var card = new BpaScorecard
            {
                RuleCount = rules.Count,
                ViolationCount = deduped.Count(v => !v.IsAccepted()),
                AutoFixable = deduped.Count(v => !v.Waived && v.CanAutoFix),
                WaivedCount = deduped.Count(v => v.IsAccepted()),
                Violations = deduped.ToArray(),
                RuleErrors = ruleErrors.Distinct().ToArray(),
                Unknowns = unknowns.ToArray(),
                UnknownCount = unknowns.Count,
                OrphanedWaivers = orphans,
                OrphanedWaiverCount = orphans.Length,
                ObjectsScanned = scanned.Count,
            };
            if (scopeRefs == null) BpaGrading.Stamp(card);
            if (scopeRefs == null)
                card.Categories = scannedByCategory.Select(kv =>
                {
                    var open = deduped.Where(v => !v.IsAccepted() && string.Equals(string.IsNullOrWhiteSpace(v.Category) ? "Other" : v.Category, kv.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var errors = open.Count(v => v.Severity >= 3); var warnings = open.Count(v => v.Severity == 2);
                    return new BpaCategoryScore { Category = kv.Key, Errors = errors, Warnings = warnings, ObjectsChecked = kv.Value.Count, Score = BpaGrading.Score(errors, warnings, kv.Value.Count) };
                }).OrderBy(x => x.Category, StringComparer.OrdinalIgnoreCase).ToArray();
            return card;
        }

        private static bool OnFieldParameter(ITabularNamedObject obj) => obj switch
        {
            Table t => ReadinessRuleSet.IsFieldParameterTable(t),
            Column c => ReadinessRuleSet.IsFieldParameterTable(c.Table),
            _ => false,
        };

        /// <summary>Apply a rule's deterministic FixExpression ("Prop = value") to one object. Must run
        /// inside the engine's MutateAsync batch. Returns false if the rule has no auto-fixable expression, and
        /// false WITHOUT WRITING when the property already holds the value the fix would set: "changed" has to mean
        /// the model moved, or an assistant re-running a fix is told about an edit that never happened and
        /// apply_safe_fixes counts the same no-op as work on every sweep. Not writing also keeps a no-op out of the
        /// undo timeline and off the dirty flag.
        /// A parseable assignment whose value cannot become the target property's type is refused LOUDLY:
        /// an explicit apply_fix (system='bpa') on an unapplicable fix is an error, never a silent no-op and never a
        /// half-converted write.</summary>
        public static bool ApplyFix(Model model, BpaRule rule, ITabularNamedObject obj)
        {
            if (obj == null || !CanAutoFix(rule?.FixExpression)) return false;
            var (prop, rhs) = SplitAssignment(rule.FixExpression);
            var pi = obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            if (pi == null || !pi.CanWrite) throw new InvalidOperationException($"Fix target '{prop}' is not a writable property of {obj.GetType().Name}.");
            if (!TryCoerce(rhs, pi.PropertyType, out var value))
                throw new InvalidOperationException($"Fix value {rhs} is not a valid {(Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType).Name} for '{prop}' on {obj.GetType().Name}.");
            if (Equals(pi.GetValue(obj), value)) return false;   // already there: nothing to change, so say so
            if (IsWithheldFormatFix(pi, obj, value))
                throw new InvalidOperationException(FormatRefusal(obj, rule));
            pi.SetValue(obj, value);
            return true;
        }

        /// <summary>A FORMAT FIX IS AUTOMATIC ONLY WHERE THE FORMAT FILL RULE ALLOWS IT. Eight review rounds each found a
        /// format a character grammar called "a plain number" whose meaning the house style destroys: a date or time,
        /// a currency, a number divided by a thousand (<c>##0,,</c> shows 100 million as 100), a section that hides
        /// values (<c>0.00;;</c>), a digit mask (<c>0000-00-00</c>), a static format beside a dynamic one. So a fix never
        /// writes over a format that is there. And a blank format is not safe to fill either: #,0 over a blank
        /// <c>DIVIDE ( [HRS_BILL], [HRS_TOT] )</c> shows 73% as 1, and over a SUM of money it drops the cents. The one
        /// automatic format write left is the one <see cref="FormatFill.MayFill"/> allows: a whole-number format on an
        /// object with no format at all that the engine proves holds a whole number. Every other format fix is withheld:
        /// a fix that targets any other format property (a format string expression of any kind, a KPI's target format)
        /// is never automatic, because what an expression shows cannot be proved. A rule may still RAISE the object;
        /// its finding is advice (<see cref="IsFormatAdvice"/>), whose words say what the rule suggests, and a person or
        /// their assistant makes that edit on purpose. This holds for every rule, built-in or custom, and for every
        /// door, because every door asks <see cref="CanAutoFix(BpaRule, ITabularNamedObject)"/> before it offers or
        /// seeds a fix and every write goes through <see cref="ApplyFix"/>, which refuses rather than trusts that.
        /// Re-applying a fix that already landed is not a write over anything: the value is already the fix's own,
        /// and <see cref="ApplyFix"/> answers that as a quiet no-op before it ever gets here.</summary>
        private static bool IsWithheldFormatFix(PropertyInfo pi, ITabularNamedObject obj, object value)
        {
            if (!IsFormatProperty(pi.Name)) return false;
            if (pi.Name != "FormatString") return true;
            if (Equals(pi.GetValue(obj), value) && string.IsNullOrWhiteSpace(FormatExpressionOf(obj))) return false;
            return !FormatFill.MayFill(obj, value as string);
        }

        /// <summary>Every property that holds a format: the static format string, a KPI's target format, and every
        /// format string expression (a measure's, a calculation item's, and a calculation group's two of its own).</summary>
        private static bool IsFormatProperty(string name) =>
            name == "FormatString" || name == "TargetFormatString" || name.EndsWith("FormatStringExpression", StringComparison.Ordinal);
        private static string FormatExpressionOf(ITabularNamedObject obj) =>
            obj.GetType().GetProperty("FormatStringExpression", BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj) as string;
        private static string StaticFormatOf(ITabularNamedObject obj) =>
            obj.GetType().GetProperty("FormatString", BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj) as string;

        /// <summary>True when a rule raises this object but its format fix is withheld by the format fill rule: the
        /// finding is advice, and a person or their assistant decides. get_fix_prompt uses it to say so rather than send
        /// an assistant to "fix" it, and the deploy gate never blocks on it (it is not a fault).</summary>
        public static bool IsFormatAdvice(BpaRule rule, ITabularNamedObject obj) =>
            IsFormatGapRule(rule, obj) || (TryResolveFix(rule, obj, out var pi, out var value) && IsWithheldFormatFix(pi, obj, value));

        /// <summary>The two rules that say "this visible measure (or column) has no format". Neither has a fix to quote
        /// (Semanticus fills a blank only where it can prove a whole number, and INTEGER_FORMATTING does that for measures), so
        /// what they raise is a choice for the person, and it is advice: the deploy gate never blocks on it. A guardrail must
        /// not stop a deploy over a format Semanticus itself will not write, and this rule has no fix that fills a column's format
        /// (Jev's column row on the Plan fills a proved whole-number column on Pro, and it is a line a person can see, not this rule),
        /// so the column rule is advice on EVERY column, a whole-number quantity included (addendum 3 item 14). It is the same for a
        /// model's own column-format rule under another id (addendum 4 item 28). A copy of the bundled rule embedded in the model
        /// (same id and the same behaviour in every field, severity included, as a Tabular Editor user's standard set is) IS the bundled rule and stays advice
        /// (<see cref="BpaRuleSet.ForModel"/> does not mark it custom). The one exception is a rule the model truly wrote at
        /// severity 3: that is the model's policy and keeps blocking.</summary>
        public static bool IsFormatGapRule(BpaRule rule, ITabularNamedObject obj = null) =>
            string.Equals(rule?.ID, "PROVIDE_FORMAT_STRING_FOR_MEASURES", StringComparison.OrdinalIgnoreCase)
            || ((string.Equals(rule?.ID, "SEM_COLUMN_NEEDS_FORMAT_STRING", StringComparison.OrdinalIgnoreCase) || IsColumnFormatGapShape(rule))
                && !(rule.FromModelAnnotation && rule.Severity >= 3));

        // A model's OWN rule under another id that says the same thing about a column: it applies to columns and its predicate is "this
        // column's FormatString is blank" (addendum 4 item 28, L6). Below severity 3 it is advice like the built-in rule, because the
        // model's severity 2 is a warning and a warning about a format Semanticus itself will not write must not stop a deploy. At
        // severity 3 it is the model's own policy and keeps blocking. Read as the rule's own text, never its name.
        private static readonly Regex ColumnFormatBlank = new Regex(
            @"(?i)\bstring\s*\.\s*IsNullOr(?:Whitespace|Empty)\s*\(\s*FormatString\s*\)|\bFormatString\s*==?\s*(?:""""|null\b)|\bFormatString\s*\.\s*Length\s*==?\s*0\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static bool IsColumnFormatGapShape(BpaRule rule) =>
            rule != null && rule.FromModelAnnotation
            && (rule.Scope ?? "").IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0
            && (rule.Scope ?? "").IndexOf("Measure", StringComparison.OrdinalIgnoreCase) < 0
            && ColumnFormatBlank.IsMatch(rule.Expression ?? "");

        /// <summary>The words for a withheld format fix, shared by the refusal a press gets and the advice
        /// get_fix_prompt gives, so both doors say the same true thing. <c>Now</c> is the object's OWN format (its format
        /// string expression when it has one, else its static format), not merely the value of the property the fix
        /// targets; <c>How</c> names a write that fits the object, and never a static format beside an expression.</summary>
        public static (string Now, string Suggests, string Why, string How) FormatAdvice(BpaRule rule, ITabularNamedObject obj)
        {
            if (!TryResolveFix(rule, obj, out var pi, out var value))
                return !IsFormatGapRule(rule, obj) ? (null, null, null, null)
                    : obj is Measure gap ? FormatGapAdvice(gap)
                    : obj is Column blank ? FormatGapAdvice(blank) : (null, null, null, null);
            var target = pi.Name;
            var suggested = value as string ?? value?.ToString() ?? "";
            var expr = FormatExpressionOf(obj);
            var hasStatic = obj.GetType().GetProperty("FormatString", BindingFlags.Public | BindingFlags.Instance) != null;
            var stat = StaticFormatOf(obj);
            string now;
            if (target != "FormatString" && target != "FormatStringExpression")
                now = string.IsNullOrWhiteSpace(pi.GetValue(obj) as string) ? $"Its {target} is blank" : $"Its {target} is already set ({pi.GetValue(obj)})";
            else if (!string.IsNullOrWhiteSpace(expr)) now = $"Its format is set by the format string expression {expr}";
            else if (hasStatic && !string.IsNullOrWhiteSpace(stat)) now = $"Its format is already set (\"{stat}\")";
            else now = "It has no format yet";

            var suggests = target == "FormatString" ? $"the rule suggests \"{suggested}\""
                         : target == "FormatStringExpression" ? $"the rule suggests the format string expression {suggested}"
                         : $"the rule suggests {target} {suggested}";

            string why;
            if (target != "FormatString")
                why = $"A fix only fills a blank format string on a value Semanticus can prove is a whole number, so it never writes {target} by itself: what an expression or another format shows cannot be proved.";
            else if (!string.IsNullOrWhiteSpace(expr))
                why = "A static format cannot sit beside a format string expression, so no fix sets one.";
            else if (!string.IsNullOrWhiteSpace(stat))
                why = "A fix only fills a blank format string; it never writes over one, because a format can mean more than it looks: a comma at the end or just before the decimal point divides by a thousand, an empty section hides values, and dashes or brackets between digits can lay out a code.";
            else if (!FormatFill.IsWholeNumberFormat(suggested))
                why = "A fix only fills a blank format string, and only with a whole-number format on a value Semanticus can prove is a whole number, so it never writes "
                    + (IsDateValued(obj) ? "a date format" : $"\"{suggested}\"") + " by itself. "
                    + (IsDateValued(obj) ? "The wrong date format can hide the time a date holds." : "The wrong format changes what a person reads.")
                    + " Choose the format that fits what it shows.";
            else
                why = "A fix only fills a blank format string, and only with a whole-number format on a value Semanticus can prove is a whole number: a count, a SUM of a whole-number column, or a whole-number column the model adds up (a SUM measure reads it, or its summarize is set to Sum) that is not a key, a code or a flag. "
                    + "That is not proved here, and the wrong format changes what a person reads: #,0 shows a 73% rate as 1 and drops the cents from money. Choose the format that fits what it shows.";

            var objRef = Refs.For(obj);
            string how;
            if (target != "FormatString")
                how = $"with set_property, property {target} (objRef \"{objRef}\")";
            else if (!string.IsNullOrWhiteSpace(expr))
                how = $"by first clearing FormatStringExpression with set_property and then setting the format, never a static format while the expression is there (objRef \"{objRef}\")";
            else if (obj is Measure)
                how = $"with set_measure_format, or set_property on FormatString (objRef \"{objRef}\")";
            else
                how = $"with set_property on FormatString (objRef \"{objRef}\")";
            return (now, suggests, why, how);
        }

        private static bool IsDateValued(ITabularNamedObject obj) => obj is Column { DataType: DataType.DateTime };

        // The gap rule has no fix to quote, so the words come from the object: it has no format, and only the person knows
        // what the number is.
        private static (string Now, string Suggests, string Why, string How) FormatGapAdvice(Measure m) =>
            ("It has no format yet",
             "the rule suggests giving it a format that fits what it shows",
             "A fix only fills a blank format string, and only with a whole-number format on a value Semanticus can prove is a whole number. "
             + "That is not proved here, and the wrong format changes what a person reads: #,0 shows a 73% rate as 1 and drops the cents from money. Choose the format that fits what it shows.",
             $"with set_measure_format (objRef \"{Refs.For(m)}\")");

        private static (string Now, string Suggests, string Why, string How) FormatGapAdvice(Column c) =>
            (string.IsNullOrEmpty(c.FormatString) ? "It has no format yet" : "Its format is only blank spaces",
             "the rule suggests giving it a format that fits what it shows",
             "A fix only fills a blank format string, and only with a whole-number format on a column Semanticus can prove holds a whole-number quantity. "
             + (c.DataType == DataType.DateTime ? "This one holds dates, and the wrong date format can hide the time a date holds. "
                // The words follow what the rule proves about THIS column (addendum 4 item 28, L1): "not proved here" is never said of a column it proves.
                : c.DataType == DataType.Int64 && FormatFill.ProvenWholeNumber(c) != null
                    ? "Semanticus can tell this one holds whole numbers, so a whole-number format fits it (it adds a thousands separator), but this rule has no fix that writes a column's format. "
                    : c.DataType == DataType.Int64 ? "That is not proved here. A whole-number format adds a thousands separator, so a year reads 2,024 and a code or an ID loses its shape. "
                : "That is not proved here, and the wrong format changes what a person reads: a whole-number format drops the cents from money and the decimals from a rate. ")
             + "Choose the format that fits what it shows.",
             $"with set_property on FormatString (objRef \"{Refs.For(c)}\")");

        private static string FormatRefusal(ITabularNamedObject obj, BpaRule rule)
        {
            var name = (obj as IDaxObject)?.DaxObjectFullName ?? obj.Name;
            var (now, suggests, why, how) = FormatAdvice(rule, obj);
            return $"{name}: {now}, and {suggests}. {why} So {rule.ID} has no automatic fix here. Nothing was changed. "
                 + $"If you agree with the suggestion, make the change yourself {how}.";
        }

        /// <summary>Why a fix is left out of a BULK pass (Fix all on both doors, and the Model Plan's best-practice row),
        /// in the words a person reads, or null when a bulk pass may make it. The finding stays and keeps its one-click
        /// fix, so a person can still make the change on purpose; only the sweep that nobody reads line by line skips it.
        /// Two writes change more than their finding says:
        /// a DATA TYPE change (AVOID_FLOATING_POINT_DATA_TYPES turns a Double into a Decimal, which keeps four decimal
        /// places, so the values a column shows can change), and setting a KEY on a table that already has one (a table
        /// has one key column, and the wrapper clears the others, so MARK_PRIMARY_KEYS would take the key away from the
        /// column a person chose). Asked of the property and value the fix writes (<see cref="PreviewFix"/>), so a custom
        /// rule with the same write is left out the same way.</summary>
        public static string BulkFixWithheld(ITabularNamedObject obj, string prop, string after)
        {
            if (string.Equals(prop, "DataType", StringComparison.OrdinalIgnoreCase))
                return "This fix changes the column's data type, which can change the values it shows (a Decimal keeps only "
                     + "four decimal places). Fix all leaves it as it is. Use this finding's own Fix, or change the type "
                     + "yourself, if you want it.";
            if (string.Equals(prop, "IsKey", StringComparison.OrdinalIgnoreCase) && bool.TryParse(after, out var key) && key
                && obj is Column c && c.Table != null)
            {
                var holder = c.Table.Columns.FirstOrDefault(o => !ReferenceEquals(o, c) && o.IsKey);
                if (holder != null)
                    return $"Its table already has a key column, {holder.Name}. A table has only one, so this fix would take "
                         + $"the key away from {holder.Name}. It was left as it is. Change the key yourself if {c.Name} is the right one.";
            }
            return null;
        }

        /// <summary>Preview a deterministic FixExpression without applying it: the target property, its
        /// current value, and the value the fix would set. Used to render a before→after diff in a change plan.</summary>
        public static (string prop, string before, string after) PreviewFix(BpaRule rule, ITabularNamedObject obj)
        {
            // Same object-aware answer the scan flagged the violation with: absent/read-only property, or a
            // literal that cannot become that property's type, means there is no fix to show — return nulls so
            // the caller drops the item rather than seeding a bogus diff or aborting the whole proposal.
            if (!TryResolveFix(rule, obj, out var pi, out var value)) return (null, null, null);
            var current = pi.GetValue(obj);
            var after = value is string s ? s : value?.ToString() ?? "(none)";
            return (pi.Name, current?.ToString() ?? "(none)", after);
        }

        /// <summary>The value a rule's deterministic fix writes, as text ("yyyy-mm-dd"), read from the FixExpression alone;
        /// false when the rule has no deterministic assignment. Compared case-insensitively with a plan line's After.</summary>
        internal static bool TryFixLiteral(BpaRule rule, out string literal)
        {
            literal = null;
            if (!TryParseFixAssignment(rule?.FixExpression, out _, out var rhs)) return false;
            rhs = rhs.Trim();
            literal = IsQuoted(rhs) ? DecodeQuotedLiteral(rhs.Substring(1, rhs.Length - 2)) : rhs;
            return true;
        }

        /// <summary>The ONE eligibility answer for a deterministic fix, evaluated against the object it would
        /// be applied to: parse the assignment, find the target property here, and convert the literal to that
        /// property's type — without throwing and without touching the model. Syntax alone was never enough.
        /// "IsHidden = yes" and an unknown enum member both parse as assignments, so a syntax-only answer
        /// advertised a fix that could not exist: it aborted plan seeding on the first one (PreviewFix threw
        /// inside BuildSeeds) and told the deploy gate that a blocking violation was fixable
        /// (LocalEngine.DeployGateAsync blocks on <c>Severity &gt;= 2 &amp;&amp; !CanAutoFix</c>).
        /// A format fix that THE FORMAT FILL RULE does not allow is not fixable here either
        /// (<see cref="IsWithheldFormatFix"/>): the finding stays as advice, and no door offers, seeds or applies it.
        /// <see cref="PreviewFix"/> deliberately does not ask that question, so apply_fix can still tell "this object is
        /// not flagged" apart from "this object is flagged, but its format is already there".</summary>
        public static bool CanAutoFix(BpaRule rule, ITabularNamedObject obj) =>
            TryResolveFix(rule, obj, out var pi, out var value) && !IsWithheldFormatFix(pi, obj, value);

        private static bool TryResolveFix(BpaRule rule, ITabularNamedObject obj, out PropertyInfo pi, out object value)
        {
            pi = null;
            value = null;
            if (obj == null || !TryParseFixAssignment(rule?.FixExpression, out var prop, out var rhs)) return false;
            // Case-sensitive lookup, so pi.Name IS the authored property name — callers can report it as written.
            var found = obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            if (found == null || !found.CanWrite) return false;
            if (!TryCoerce(rhs, found.PropertyType, out value)) return false;
            pi = found;
            return true;
        }

        // ---- scope → collection (string-keyed port of TE2's Analyzer.GetCollection) --------------
        // Shared with the custom-rule authoring path (CustomReadinessRules / RuleAuthoring): ONE scope vocabulary
        // for both rule kinds, so a scope that works in a BPA rule works identically in a custom readiness rule.

        /// <summary>Every scope name <see cref="GetCollection"/> maps (plus the "Column" umbrella). The teaching
        /// list an unsupported-scope refusal shows, and the dropdown source for the rule-authoring UI.</summary>
        public static readonly string[] SupportedScopeNames =
        {
            "Measure", "Column", "DataColumn", "CalculatedColumn", "CalculatedTableColumn", "Table",
            "CalculatedTable", "Hierarchy", "Level", "Relationship", "Partition", "Perspective", "Culture",
            "KPI", "NamedExpression", "CalculationGroup", "CalculationItem", "ModelRole", "Model",
        };

        internal static IEnumerable<string> ExpandScopes(string scope)
        {
            if (string.IsNullOrWhiteSpace(scope)) yield break;
            foreach (var raw in scope.Split(','))
            {
                var s = raw.Trim().Replace(" ", "");
                switch (s)
                {
                    case "Column":
                    case "Columns":
                        yield return "DataColumn"; yield return "CalculatedColumn"; yield return "CalculatedTableColumn"; break;
                    case "Hierarchies": yield return "Hierarchy"; break;
                    default:
                        // Drop a trailing plural 's' (rules sometimes write "Measures", "Tables").
                        if (s.EndsWith("s") && s != "KPI") s = s.Substring(0, s.Length - 1);
                        yield return s; break;
                }
            }
        }

        internal static IQueryable GetCollection(Model model, string scope)
        {
            switch (scope)
            {
                case "Measure": return model.AllMeasures.AsQueryable();
                case "Table": return model.Tables.Where(t => !(t is CalculatedTable) && !(t is CalculationGroupTable)).AsQueryable();
                case "DataColumn": return model.AllColumns.OfType<DataColumn>().AsQueryable();
                case "CalculatedColumn": return model.AllColumns.OfType<CalculatedColumn>().AsQueryable();
                case "CalculatedTableColumn": return model.Tables.OfType<CalculatedTable>().SelectMany(t => t.Columns).OfType<CalculatedTableColumn>().AsQueryable();
                case "CalculatedTable": return model.Tables.OfType<CalculatedTable>().AsQueryable();
                case "Hierarchy": return model.AllHierarchies.AsQueryable();
                case "Level": return model.AllLevels.AsQueryable();
                case "Relationship": return model.Relationships.OfType<SingleColumnRelationship>().AsQueryable();
                case "Model": return Enumerable.Repeat(model, 1).AsQueryable();
                case "Partition": return model.AllPartitions.AsQueryable();
                case "Perspective": return model.Perspectives.AsQueryable();
                case "Culture": return model.Cultures.AsQueryable();
                case "KPI": return model.AllMeasures.Where(m => m.KPI != null).Select(m => m.KPI).AsQueryable();
                case "NamedExpression": return model.Expressions.AsQueryable();
                case "CalculationGroup": return model.CalculationGroups.AsQueryable();
                case "CalculationItem": return model.CalculationGroups.SelectMany(cg => cg.CalculationItems).AsQueryable();
                case "ModelRole": return model.Roles.AsQueryable();
                default: return null;
            }
        }

        // Rule predicates by (element type, expression). Parsing a Dynamic-LINQ expression and compiling it costs far more than
        // running it over a few objects, and the old Where(string) path paid both on every rule of every scan, so a one-object
        // scoped scan after an edit cost about as much as the compiles. The parse depends on nothing but the type and the text
        // (the vendored parser's keywords, types and extension methods are static), so one compiled delegate per pair gives the
        // same answers. A text that does not parse is not cached: it throws the same ParseException from here on every scan.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), Func<object, bool>> Predicates = new();
        private const int MaxCachedPredicates = 4096;   // rule texts are few; this only bounds a pathological stream of distinct ones
        private static int _compiledPredicates;
        /// <summary>How many rule predicates this process has compiled (tests: a rescan compiles none).</summary>
        internal static int CompiledPredicates => System.Threading.Volatile.Read(ref _compiledPredicates);

        private static Func<object, bool> CompiledPredicate(Type elementType, string expression)
        {
            if (Predicates.TryGetValue((elementType, expression), out var cached)) return cached;
            var lambda = System.Linq.Dynamic.DynamicExpression.ParseLambda(elementType, typeof(bool), expression);
            var o = System.Linq.Expressions.Expression.Parameter(typeof(object), "o");
            var compiled = System.Linq.Expressions.Expression.Lambda<Func<object, bool>>(
                System.Linq.Expressions.Expression.Invoke(lambda, System.Linq.Expressions.Expression.Convert(o, elementType)), o).Compile();
            System.Threading.Interlocked.Increment(ref _compiledPredicates);
            if (Predicates.Count < MaxCachedPredicates) Predicates.TryAdd((elementType, expression), compiled);
            return compiled;
        }

        /// <summary>Pre-filter a scope collection to the given refs while PRESERVING its element type — the
        /// Dynamic-LINQ rule expression binds its properties against the queryable's ElementType, so filtering
        /// through <c>Cast&lt;ITabularNamedObject&gt;()</c> would break every typed rule. Materializes into a
        /// List&lt;T&gt; of the source element type instead. Returns null when nothing is in scope.</summary>
        private static IQueryable FilterToRefs(IQueryable source, ICollection<string> refs)
        {
            var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(source.ElementType));
            foreach (var o in source)
                if (o is ITabularNamedObject n && refs.Contains(Refs.For(n)))
                    list.Add(o);
            return list.Count == 0 ? null : ((System.Collections.IEnumerable)list).AsQueryable();
        }

        // ---- FixExpression helpers ---------------------------------------------------------------
        // One quote-aware parser for eligibility, preview and apply. Semicolons and extra '=' inside
        // quotes are data (percentage format strings); an unquoted ';' is still a second statement.
        // Escapes in the quoted literal are decoded once (\" , \\ and \uXXXX), so what the scanner read as
        // an escape is what the conversion undoes and PreviewFix produces the same value ApplyFix writes.
        // Whether a fix can actually APPLY is a per-object question: CanAutoFix(rule, obj). This syntax-only overload
        // is therefore never the fixability answer for a FINDING, and it is never the answer for a BpaRuleUnknown at
        // all: an unknown has no object, so its CanAutoFix is a hard false (see BpaRuleUnknown.CanAutoFix).
        internal static bool CanAutoFix(string fix) => TryParseFixAssignment(fix, out _, out _);

        private static (string prop, string rhs) SplitAssignment(string fix)
        {
            if (!TryParseFixAssignment(fix, out var prop, out var rhs))
                throw new InvalidOperationException("FixExpression is not a deterministic assignment.");
            return (prop, rhs);
        }

        internal static bool TryParseFixAssignment(string fix, out string prop, out string rhs)
        {
            prop = null;
            rhs = null;
            if (string.IsNullOrWhiteSpace(fix)) return false;

            var eq = -1;
            var inQuote = false;
            for (var i = 0; i < fix.Length; i++)
            {
                var c = fix[i];
                if (inQuote)
                {
                    if (c == '\\' && i + 1 < fix.Length) { i++; continue; }
                    if (c == '"') inQuote = false;
                    continue;
                }
                if (c == '"') { inQuote = true; continue; }
                if (c == ';') return false;                 // multi-statement — not deterministic here
                if (c == '(') return false;                 // method calls / operators
                if (c != '=') continue;
                var prev = i > 0 ? fix[i - 1] : ' ';
                var next = i + 1 < fix.Length ? fix[i + 1] : ' ';
                if (prev == '=' || prev == '!' || prev == '<' || prev == '>' || next == '=') continue;
                if (eq >= 0) return false;
                eq = i;
            }
            if (inQuote || eq <= 0) return false;
            prop = fix.Substring(0, eq).Trim();
            rhs = fix.Substring(eq + 1).Trim();
            if (prop.Length == 0 || rhs.Length == 0) return false;
            if (rhs[0] == '"') return rhs.Length >= 2 && rhs[rhs.Length - 1] == '"';
            // bare token: bool/null/number/dotted-enum-member (letters, digits, '.', '_', '-')
            return rhs.All(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-');
        }

        /// <summary>Convert a parsed right-hand literal to the target property's type. Returns false instead of
        /// throwing: eligibility, preview and apply all ask this same question, and only apply turns a false into
        /// an error. Nothing here evaluates an expression — it is a data parser, not an interpreter.</summary>
        private static bool TryCoerce(string rhs, Type target, out object value)
        {
            value = null;
            rhs = rhs.Trim();
            var nullable = Nullable.GetUnderlyingType(target);
            var underlying = nullable ?? target;
            if (rhs.Equals("null", StringComparison.OrdinalIgnoreCase))
                return !target.IsValueType || nullable != null;   // null onto a non-nullable value type is not a conversion

            var decoded = IsQuoted(rhs) ? DecodeQuotedLiteral(rhs.Substring(1, rhs.Length - 2)) : rhs;
            if (underlying == typeof(string)) { value = decoded; return true; }
            if (underlying == typeof(bool))
            {
                if (!bool.TryParse(decoded, out var b)) return false;
                value = b;
                return true;
            }
            if (underlying.IsEnum)
            {
                var member = decoded.Contains('.') ? decoded.Substring(decoded.LastIndexOf('.') + 1) : decoded;
                member = member.Trim();
                // Enum.TryParse also accepts a bare NUMBER and hands back an undefined member, which is a value the
                // model would carry and nothing would render. An unknown member is unknown either way: refuse both.
                if (member.Length == 0 || !Enum.TryParse(underlying, member, ignoreCase: true, out var e) || !Enum.IsDefined(underlying, e))
                    return false;
                value = e;
                return true;
            }
            try
            {
                value = Convert.ChangeType(decoded, underlying, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException || ex is ArgumentException)
            {
                return false;
            }
        }

        private static bool IsQuoted(string s) => s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"';

        // Decode a quoted literal's escapes in ONE left-to-right pass. The eligibility scanner already treats a
        // backslash inside quotes as an escape (that is how a semicolon-bearing format string with an embedded
        // quote stays a single statement), so the conversion has to undo the SAME escapes or the escaping
        // characters get written into the model. A decoded character is emitted and never re-scanned, so a
        // doubled backslash in front of a u-escape stays the literal text \u0041 instead of becoming 'A'.
        // Any other backslash sequence is data and travels through untouched (a Windows path keeps its slashes).
        private static string DecodeQuotedLiteral(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var chars = new char[s.Length];
            var n = 0;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    var next = s[i + 1];
                    if (next == '\\' || next == '"') { chars[n++] = next; i++; continue; }
                    if (next == 'u' && i + 5 < s.Length && IsHex(s[i + 2]) && IsHex(s[i + 3]) && IsHex(s[i + 4]) && IsHex(s[i + 5]))
                    {
                        chars[n++] = (char)int.Parse(s.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        i += 5;
                        continue;
                    }
                }
                chars[n++] = s[i];
            }
            return new string(chars, 0, n);
        }

        private static bool IsHex(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private static string Describe(BpaRule rule, ITabularNamedObject obj)
        {
            var name = (obj as IDaxObject)?.DaxObjectFullName ?? obj.Name;
            var d = rule.Description;
            if (string.IsNullOrWhiteSpace(d)) return $"{name} violates '{rule.Name}'.";
            return d.Replace("%object%", name).Replace("%objectname%", obj.Name ?? "");
        }

        // ---- TokenCheck: DAX-structural predicates over an object's Expression (the low-FP replacement for the
        // text heuristics). An unrecognized form THROWS so the loop's catch records "unsupported token check: …" —
        // fail loud (a typo'd rule never silently passes). --------------------------------------------------------
        private static bool TokenMatch(string check, ITabularNamedObject obj, Func<Func<string, string, string[]>> missingObjects)
        {
            // A formula that names an object the model does not have: the engine's own reference check (FormulaReferences), over every formula the object holds.
            if (string.Equals(check, "missing-objects", StringComparison.OrdinalIgnoreCase))
                return FormulaReferences.BrokenIn(obj, missingObjects()).Any();

            var expr = (obj as IExpressionObject)?.Expression;
            if (string.IsNullOrWhiteSpace(expr)) return false;
            var toks = DaxScan.Tokenize(expr);

            if (check.StartsWith("calls:", StringComparison.OrdinalIgnoreCase))
            {
                // "calls:A,B" → a REAL call node to any listed function (never text in a comment/string/[name]).
                var names = check.Substring("calls:".Length)
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var raw in names)
                {
                    var fn = raw.Trim();
                    if (fn.Length > 0 && DaxScan.CallsFunction(toks, fn)) return true;
                }
                return false;
            }

            if (string.Equals(check, "filter-bare-table", StringComparison.OrdinalIgnoreCase))
            {
                // A FILTER whose first arg is a BARE table reference — the whole-table-scan anti-pattern the old
                // text rule ("FILTER('") could only half-catch. A bare table is either a 'quoted' Name or a Word that
                // names a real model table AND is not a declared VAR. A qualified column head (FILTER('T'[Col]…) is two
                // tokens, so it never matches — the exact false-positive the text rule couldn't avoid.
                var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in obj.Model?.Tables ?? Enumerable.Empty<Table>())
                    tableNames.Add(t.Name);
                var varNames = new HashSet<string>(DaxScan.VarDecls(toks).Select(v => v.Name), StringComparer.OrdinalIgnoreCase);

                foreach (var open in DaxScan.AllCallOpens(toks, "FILTER"))
                {
                    var args = DaxScan.ArgRanges(toks, open);
                    if (args.Count == 0) continue;
                    var (s, e) = args[0];
                    if (e - s != 1) continue;                                    // more than one token ⇒ not a bare ref
                    var t = toks[s];
                    if (t.Kind == DaxScan.Kind.Name && t.Delim == '\'') return true;   // 'quoted' table name
                    if (t.Kind == DaxScan.Kind.Word && !varNames.Contains(t.Text) && tableNames.Contains(t.Text))
                        return true;                                              // unquoted identifier that is a real table (not a VAR)
                }
                return false;
            }

            throw new InvalidOperationException($"unsupported token check: {check}");
        }
    }
}
