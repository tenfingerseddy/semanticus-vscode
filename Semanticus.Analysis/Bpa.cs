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

    public sealed class BpaScorecard
    {
        public int RuleCount { get; set; }
        public int ViolationCount { get; set; }         // ACTIVE (un-waived) violations
        public int AutoFixable { get; set; }
        public int WaivedCount { get; set; }            // accepted findings (always surfaced, never hidden)
        public BpaViolation[] Violations { get; set; } = Array.Empty<BpaViolation>();
        public string[] RuleErrors { get; set; } = Array.Empty<string>();
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
            var modelCl = model.Database?.CompatibilityLevel ?? 0;

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
                        // TablePermission). Surface it instead of silently dropping the rule's coverage.
                        ruleErrors.Add($"{rule.ID} [{scope}]: unsupported scope");
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
                            CanAutoFix = CanAutoFix(rule.FixExpression),
                            Custom = rule.FromModelAnnotation,
                            Waived = teIgnored,
                            WaiverReason = teIgnored ? WaiverStore.TeReason : null,
                        });
                    }

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
                        ruleErrors.Add($"{rule.ID} [{scope}]: {ex.Message}");
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

            return new BpaScorecard
            {
                RuleCount = rules.Count,
                ViolationCount = deduped.Count(v => !v.Waived),
                AutoFixable = deduped.Count(v => !v.Waived && v.CanAutoFix),
                WaivedCount = deduped.Count(v => v.Waived),
                Violations = deduped.ToArray(),
                RuleErrors = ruleErrors.Distinct().ToArray(),
            };
        }

        /// <summary>Apply a rule's deterministic FixExpression ("Prop = value") to one object. Must run
        /// inside the engine's MutateAsync batch. Returns false if the rule has no auto-fixable expression.</summary>
        public static bool ApplyFix(Model model, BpaRule rule, ITabularNamedObject obj)
        {
            if (obj == null || !CanAutoFix(rule?.FixExpression)) return false;
            var (prop, rhs) = SplitAssignment(rule.FixExpression);
            var pi = obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            if (pi == null || !pi.CanWrite) throw new InvalidOperationException($"Fix target '{prop}' is not a writable property of {obj.GetType().Name}.");
            var value = Coerce(rhs, pi.PropertyType);
            pi.SetValue(obj, value);
            return true;
        }

        /// <summary>Preview a deterministic FixExpression without applying it: the target property, its
        /// current value, and the value the fix would set. Used to render a before→after diff in a change plan.</summary>
        public static (string prop, string before, string after) PreviewFix(BpaRule rule, ITabularNamedObject obj)
        {
            if (obj == null || !CanAutoFix(rule?.FixExpression)) return (null, null, null);
            var (prop, rhs) = SplitAssignment(rule.FixExpression);
            var pi = obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            // Mirror ApplyFix's guard: if the target property is absent/read-only on this object, the fix
            // can't apply — return nulls so the caller drops the item rather than seeding a bogus diff.
            if (pi == null || !pi.CanWrite) return (null, null, null);
            var current = pi.GetValue(obj);
            var after = rhs.Contains('.') ? rhs.Substring(rhs.LastIndexOf('.') + 1) : rhs.Trim().Trim('"');
            return (prop, current?.ToString() ?? "(none)", after);
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
        internal static bool CanAutoFix(string fix)
        {
            if (string.IsNullOrWhiteSpace(fix)) return false;
            if (fix.Contains(";")) return false;                 // multi-statement — not deterministic here
            var eq = IndexOfAssignment(fix);
            if (eq <= 0) return false;
            // Only a LITERAL right-hand side is deterministically applicable (no method calls / operators
            // like Name.Trim() or x + 1). Literals: true/false/null, "string", number, EnumMember.
            var rhs = fix.Substring(eq + 1).Trim();
            if (rhs.Length == 0 || rhs.IndexOf('(') >= 0) return false;
            if (rhs[0] == '"') return rhs.Length >= 2 && rhs[rhs.Length - 1] == '"';
            // bare token: bool/null/number/dotted-enum-member (letters, digits, '.', '_', '-')
            return rhs.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-');
        }

        private static (string prop, string rhs) SplitAssignment(string fix)
        {
            var eq = IndexOfAssignment(fix);
            return (fix.Substring(0, eq).Trim(), fix.Substring(eq + 1).Trim());
        }

        // First '=' that isn't '==', '<=', '>=', '!='.
        private static int IndexOfAssignment(string s)
        {
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] != '=') continue;
                var prev = i > 0 ? s[i - 1] : ' ';
                var next = i < s.Length - 1 ? s[i + 1] : ' ';
                if (prev == '=' || prev == '!' || prev == '<' || prev == '>' || next == '=') continue;
                return i;
            }
            return -1;
        }

        private static object Coerce(string rhs, Type target)
        {
            rhs = rhs.Trim();
            var underlying = Nullable.GetUnderlyingType(target) ?? target;
            if (rhs.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
            if (underlying == typeof(string))
                return rhs.Length >= 2 && rhs[0] == '"' && rhs[rhs.Length - 1] == '"' ? rhs.Substring(1, rhs.Length - 2) : rhs;
            // For non-string targets a literal may be written quoted (e.g. a community rule `IsHidden = "true"`);
            // strip a surrounding quote pair so quoted and unquoted literals coerce identically.
            if (rhs.Length >= 2 && rhs[0] == '"' && rhs[rhs.Length - 1] == '"') rhs = rhs.Substring(1, rhs.Length - 2);
            if (underlying == typeof(bool)) return bool.Parse(rhs.ToLowerInvariant());
            if (underlying.IsEnum)
            {
                var member = rhs.Contains('.') ? rhs.Substring(rhs.LastIndexOf('.') + 1) : rhs;
                member = member.Trim().Trim('"');
                return Enum.Parse(underlying, member, ignoreCase: true);
            }
            if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(double) || underlying == typeof(decimal))
                return Convert.ChangeType(rhs.Trim('"'), underlying, CultureInfo.InvariantCulture);
            // Fallback: strip quotes and let ChangeType try.
            return Convert.ChangeType(rhs.Trim('"'), underlying, CultureInfo.InvariantCulture);
        }

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
