using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        /// user/org rule loaded via load_bpa_rules), false for the bundled standard set. Set by the engine when it
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
        public bool Waived { get; set; }                // an accepted finding: surfaced but excluded from ViolationCount
        public string WaiverReason { get; set; }        // why it was accepted (null unless Waived)
        public bool WaiverRuleLevel { get; set; }        // true when waived by a rule-level (model-wide) waiver, not a per-instance one
        public bool Custom { get; set; }                 // provenance: true = a model-embedded (user/org) rule fired, false = the bundled standard set
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
                        collection = FilterToRefs(collection, scopeRefs);
                        if (collection == null) continue;   // nothing touched in this collection — skip the eval entirely
                    }
                    // One violation-construction block for BOTH evaluation paths (Dynamic-LINQ and token). It carries the
                    // TE per-object ignore honouring; the Semanticus waiver pass below runs on top of whatever it tags.
                    void AddViolation(ITabularNamedObject obj)
                    {
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
                                if (TokenMatch(rule.TokenCheck, obj))
                                    AddViolation(obj);
                        }
                        else
                        {
                            foreach (var obj in collection.Where(rule.Expression).OfType<ITabularNamedObject>())
                                AddViolation(obj);
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
                    if (w != null) { v.Waived = true; v.WaiverReason = w.Reason; v.WaiverRuleLevel = WaiverStore.IsRuleLevel(w.ObjectRef); }
                }

            // Reconcile the store against the LOADED rule ids. The match pass above can only ever visit waivers whose
            // rule produced a violation, so a waiver for a retired rule id would otherwise be invisible here — present
            // in the store, counted nowhere, protecting nothing. Surface it as its own state instead.
            var orphans = WaiverStore.Orphans(waivers, "bpa", rules.Select(r => r.ID)).ToArray();

            return new BpaScorecard
            {
                RuleCount = rules.Count,
                ViolationCount = deduped.Count(v => !v.Waived),
                AutoFixable = deduped.Count(v => !v.Waived && v.CanAutoFix),
                WaivedCount = deduped.Count(v => v.Waived),
                Violations = deduped.ToArray(),
                RuleErrors = ruleErrors.Distinct().ToArray(),
                Unknowns = unknowns.ToArray(),
                UnknownCount = unknowns.Count,
                OrphanedWaivers = orphans,
                OrphanedWaiverCount = orphans.Length,
            };
        }

        /// <summary>Apply a rule's deterministic FixExpression ("Prop = value") to one object. Must run
        /// inside the engine's MutateAsync batch. Returns false if the rule has no auto-fixable expression.
        /// A parseable assignment whose value cannot become the target property's type is refused LOUDLY:
        /// an explicit bpa_fix on an unapplicable fix is an error, never a silent no-op and never a
        /// half-converted write.</summary>
        public static bool ApplyFix(Model model, BpaRule rule, ITabularNamedObject obj)
        {
            if (obj == null || !CanAutoFix(rule?.FixExpression)) return false;
            var (prop, rhs) = SplitAssignment(rule.FixExpression);
            var pi = obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            if (pi == null || !pi.CanWrite) throw new InvalidOperationException($"Fix target '{prop}' is not a writable property of {obj.GetType().Name}.");
            if (!TryCoerce(rhs, pi.PropertyType, out var value))
                throw new InvalidOperationException($"Fix value {rhs} is not a valid {(Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType).Name} for '{prop}' on {obj.GetType().Name}.");
            pi.SetValue(obj, value);
            return true;
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

        /// <summary>The ONE eligibility answer for a deterministic fix, evaluated against the object it would
        /// be applied to: parse the assignment, find the target property here, and convert the literal to that
        /// property's type — without throwing and without touching the model. Syntax alone was never enough.
        /// "IsHidden = yes" and an unknown enum member both parse as assignments, so a syntax-only answer
        /// advertised a fix that could not exist: it aborted plan seeding on the first one (PreviewFix threw
        /// inside BuildSeeds) and told the deploy gate that a blocking violation was fixable
        /// (LocalEngine.DeployGateAsync blocks on <c>Severity &gt;= 2 &amp;&amp; !CanAutoFix</c>).</summary>
        internal static bool CanAutoFix(BpaRule rule, ITabularNamedObject obj) => TryResolveFix(rule, obj, out _, out _);

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
        private static bool TokenMatch(string check, ITabularNamedObject obj)
        {
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
