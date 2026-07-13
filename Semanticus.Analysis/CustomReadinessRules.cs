using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Dynamic; // the same vendored legacy engine BPA rules run on — ONE expression language, never two
using System.Text.Json;
using System.Text.Json.Serialization;
using TabularEditor.TOMWrapper;

namespace Semanticus.Analysis
{
    /// <summary>
    /// A user-authored AI-readiness rule (the custom counterpart of the compiled rules in
    /// <see cref="ReadinessRuleSet"/>). Same expression vocabulary as BPA rules (Dynamic-LINQ over the same
    /// scope→collection map), plus readiness metadata: a target <see cref="ReadinessCategory"/> (one of the
    /// EXISTING categories — custom categories are out of scope), a Severity weight, and an optional
    /// <see cref="AppliesTo"/> population filter that makes Applicable honest (the house scoring convention:
    /// Applicable = the population the rule actually evaluates; an empty population = dormant, Applicable=0,
    /// never inflating the category). Custom rules can never register hard gates and can never override a
    /// built-in rule id.
    /// </summary>
    public sealed class CustomReadinessRuleDef
    {
        public string ID { get; set; } = "";
        public string Name { get; set; }                 // short title; falls back to the ID
        public string Category { get; set; } = "";       // must parse to a ReadinessCategory
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string Severity { get; set; }             // Info | Medium | High | Critical (or 1|2|3|5); default Medium
        public string Scope { get; set; } = "";          // BPA scope vocabulary, e.g. "Measure" or "Column, Table"
        public string AppliesTo { get; set; }            // optional Dynamic-LINQ population filter; empty = whole scope
        public string Expression { get; set; }           // Dynamic-LINQ violation predicate within the population
        public string Message { get; set; }              // optional; %object% / %objectname% placeholders
        public string Description { get; set; }
        public string FixKind { get; set; }              // None (default) | AiContent | Proposal — advisory only, never SafeFix
    }

    /// <summary>Reads a JSON string OR number into a string property (community rule files write
    /// <c>"Severity": 2</c> and <c>"Severity": "2"</c> interchangeably; both must load the same).</summary>
    internal sealed class FlexibleStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Number
                ? reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetString();
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    /// <summary>One rule's validation + honest test-run preview (validate_rule / the authoring form's live check):
    /// compile errors from the REAL parser, then — when clean — the would-be Applicable/violation counts and the
    /// first few flagged objects. A preview only; nothing is saved by producing one.</summary>
    public sealed class RuleCheck
    {
        public string Id { get; set; }
        public bool Valid { get; set; }
        public string[] Errors { get; set; } = Array.Empty<string>();
        public int Applicable { get; set; }        // the population the rule would evaluate (after AppliesTo)
        public int Violations { get; set; }        // how many of those it would flag right now
        public bool Dormant { get; set; }          // Applicable == 0: the rule stays silent and never moves the score
        public string[] Sample { get; set; } = Array.Empty<string>();   // first few flagged object names
        public string Note { get; set; }
    }

    public static class CustomReadinessRuleSet
    {
        /// <summary>Where custom readiness rules live on the model — a sibling of BPA's "BestPracticeAnalyzer"
        /// annotation, so the rules travel with the model and load/reset are plain undoable annotation writes.</summary>
        public const string AnnotationName = "Semanticus_ReadinessRules";

        // Same leniency as BpaRuleSet.JsonOpts so a hand-edited rules file loads identically for both kinds.
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        // Built-in rule ids (compiled defaults + the live Dmv rules). A custom rule colliding with one is refused:
        // it could weaken a built-in or hijack a gate id (gates are wired by rule id in ReadinessAnalyzer).
        private static readonly Lazy<HashSet<string>> BuiltinIdsLazy = new Lazy<HashSet<string>>(() =>
            new HashSet<string>(
                ReadinessRuleSet.Default().Select(r => r.Id)
                    .Concat(ReadinessRuleSet.LiveRules(new ReadinessLiveStats()).Select(r => r.Id)),
                StringComparer.OrdinalIgnoreCase));
        public static bool IsBuiltinId(string id) => !string.IsNullOrWhiteSpace(id) && BuiltinIdsLazy.Value.Contains(id.Trim());

        /// <summary>Parse rules JSON — an array, or a single rule object (one-element array). Drops entries with
        /// no ID and de-dups by ID (last wins) — the storage/merge semantics, identical to BPA. Throws
        /// <see cref="JsonException"/> on malformed JSON. For validation (where a no-ID entry must be REPORTED,
        /// not dropped) use <see cref="ParseAll"/>.</summary>
        public static List<CustomReadinessRuleDef> Parse(string json)
        {
            var byId = new Dictionary<string, CustomReadinessRuleDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ParseAll(json))
                if (r != null && !string.IsNullOrWhiteSpace(r.ID)) byId[r.ID] = r;
            return byId.Values.ToList();
        }

        /// <summary>Parse preserving EVERY entry (no drop, no de-dup) so each can get a per-rule verdict.</summary>
        public static List<CustomReadinessRuleDef> ParseAll(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<CustomReadinessRuleDef>();
            var trimmed = json.TrimStart();
            if (trimmed.StartsWith("{"))
            {
                var one = JsonSerializer.Deserialize<CustomReadinessRuleDef>(trimmed, JsonOpts);
                return one == null ? new List<CustomReadinessRuleDef>() : new List<CustomReadinessRuleDef> { one };
            }
            return JsonSerializer.Deserialize<List<CustomReadinessRuleDef>>(trimmed, JsonOpts)
                   ?? new List<CustomReadinessRuleDef>();
        }

        /// <summary>Serialize a rule set for the model annotation (schema fields only, stable shape).</summary>
        public static string Serialize(IEnumerable<CustomReadinessRuleDef> rules) =>
            JsonSerializer.Serialize(rules.ToList());

        internal static bool TryParseCategory(string s, out ReadinessCategory cat) =>
            Enum.TryParse(s?.Trim(), ignoreCase: true, out cat) && Enum.IsDefined(typeof(ReadinessCategory), cat);

        internal static bool TryParseSeverity(string s, out Severity sev)
        {
            sev = Severity.Medium;                                       // the default when omitted
            if (string.IsNullOrWhiteSpace(s)) return true;
            return Enum.TryParse(s.Trim(), ignoreCase: true, out sev) && Enum.IsDefined(typeof(Severity), sev);
        }

        internal static bool TryParseFix(string s, out FixKind fix)
        {
            fix = FixKind.None;                                          // the default when omitted
            if (string.IsNullOrWhiteSpace(s)) return true;
            return Enum.TryParse(s.Trim(), ignoreCase: true, out fix) && Enum.IsDefined(typeof(FixKind), fix);
        }

        private static string CategoryList => string.Join(", ", Enum.GetNames(typeof(ReadinessCategory)));
        private static string ScopeList => string.Join(", ", BpaAnalyzer.SupportedScopeNames);

        /// <summary>Validate one rule against the open model: metadata (id / category / severity / fix kind /
        /// scope) plus a REAL compile of AppliesTo and Expression through the same Dynamic-LINQ parser a scan
        /// uses. Every refusal teaches — an invalid category lists the valid ones, a parse error points at the
        /// failing position. Empty list = valid.</summary>
        public static List<string> Validate(Model model, CustomReadinessRuleDef def)
        {
            var errors = new List<string>();
            if (def == null) { errors.Add("The rule is empty: pass a JSON object with ID, Category, Scope and Expression."); return errors; }

            if (string.IsNullOrWhiteSpace(def.ID))
                errors.Add("Every rule needs an \"ID\": a short stable id like ORG-DESC-KPI (it is how merges, waivers and findings refer to the rule).");
            else if (IsBuiltinId(def.ID))
                errors.Add($"'{def.ID.Trim()}' is a built-in readiness rule id; custom rules cannot override or weaken built-ins. Pick a new id (e.g. ORG-{def.ID.Trim()}).");

            if (!TryParseCategory(def.Category, out _))
                errors.Add($"Category '{def.Category}' is not a readiness category. Use one of: {CategoryList}.");

            if (!TryParseSeverity(def.Severity, out _))
                errors.Add($"Severity '{def.Severity}' is not recognised. Use Info, Medium, High, or Critical (or 1, 2, 3, 5).");

            if (!TryParseFix(def.FixKind, out var fix))
                errors.Add($"FixKind '{def.FixKind}' is not recognised. Use AiContent (the AI assistant authors the fix), Proposal (human review), or omit it.");
            else if (fix == FixKind.SafeFix)
                errors.Add("FixKind 'SafeFix' is reserved for built-in rules with deterministic engine fixes; custom rules are advisory. Use AiContent or Proposal, or omit FixKind.");

            if (string.IsNullOrWhiteSpace(def.Expression))
                errors.Add("An \"Expression\" is required: the condition that flags an object, e.g. string.IsNullOrEmpty(Description).");

            if (string.IsNullOrWhiteSpace(def.Scope))
                errors.Add($"A \"Scope\" is required: which objects the rule checks. Supported: {ScopeList}.");
            else
                foreach (var scope in BpaAnalyzer.ExpandScopes(def.Scope))
                {
                    var collection = BpaAnalyzer.GetCollection(model, scope);
                    if (collection == null) { errors.Add($"Scope '{scope}' is not supported. Supported: {ScopeList}."); continue; }
                    // Compile through the real parser (Where parses eagerly against the scope's element type).
                    CompileCheck(errors, collection, "AppliesTo", scope, def.AppliesTo);
                    CompileCheck(errors, collection, "Expression", scope, def.Expression);
                }
            return errors;
        }

        private static void CompileCheck(List<string> errors, IQueryable collection, string field, string scope, string expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) return;
            try { collection.Where(expr); }
            catch (ParseException pe) { errors.Add($"{field} does not compile for scope {scope} (at position {pe.Position}): {pe.Message}"); }
            catch (Exception ex) { errors.Add($"{field} does not compile for scope {scope}: {ex.Message}"); }
        }

        /// <summary>The model's custom rules as evaluable <see cref="ReadinessRule"/>s. Problems (an unparseable
        /// annotation, a hand-edited id colliding with a built-in, unusable metadata) are surfaced on
        /// <paramref name="errors"/> — fail loud on the scorecard, never a silent pass — and the offending rule is
        /// skipped so it can neither run nor block the rest of the scan.</summary>
        public static List<ReadinessRule> FromModel(Model model, ICollection<string> errors)
        {
            var result = new List<ReadinessRule>();
            string json;
            try { json = model.GetAnnotation(AnnotationName); }
            catch { return result; }
            if (string.IsNullOrWhiteSpace(json)) return result;

            List<CustomReadinessRuleDef> defs;
            try { defs = Parse(json); }
            catch (Exception ex)
            {
                errors?.Add($"The model's custom readiness rules are unparseable ({ex.Message}); none were evaluated. Reload them with load_readiness_rules or clear them with reset_readiness_rules.");
                return result;
            }
            foreach (var def in defs)
            {
                if (IsBuiltinId(def.ID))
                { errors?.Add($"Custom rule '{def.ID}' collides with a built-in readiness rule id and was skipped; built-ins cannot be overridden. Rename it and reload with load_readiness_rules."); continue; }
                if (!TryParseCategory(def.Category, out var cat))
                { errors?.Add($"Custom rule '{def.ID}': category '{def.Category}' is not a readiness category ({CategoryList}); the rule was skipped. Fix it and reload with load_readiness_rules."); continue; }
                TryParseSeverity(def.Severity, out var sev);            // unrecognised → the Medium default (load validates strictly; this is the hand-edited path)
                TryParseFix(def.FixKind, out var fix);
                if (fix == FixKind.SafeFix) fix = FixKind.None;          // never let a hand-edited rule claim the deterministic-fix affordance
                result.Add(new CustomReadinessRule(def, cat, sev, fix));
            }
            return result;
        }
    }

    /// <summary>A custom rule adapter: the BPA scope→collection map + Dynamic-LINQ evaluation wearing the
    /// readiness rule contract. Applicable = the objects matching <see cref="CustomReadinessRuleDef.AppliesTo"/>
    /// (the whole scope population when omitted); violations = Expression matches WITHIN that population; an
    /// empty population leaves the rule dormant (Applicable=0). Cross-object by nature (Dynamic-LINQ can reach
    /// Model.*), so it re-evaluates fully every scan — EvaluateScoped stays the base null (the documented
    /// fallback for custom cross-object rules; these are cheap population sweeps, not per-expression lints).</summary>
    public sealed class CustomReadinessRule : ReadinessRule
    {
        private readonly CustomReadinessRuleDef _def;
        public override string Id { get; }
        public override string Title { get; }
        public override ReadinessCategory Category { get; }
        public override Severity Severity { get; }
        public override RuleKind Kind => RuleKind.Deterministic;
        public override FixKind Fix { get; }
        public CustomReadinessRuleDef Definition => _def;

        internal CustomReadinessRule(CustomReadinessRuleDef def, ReadinessCategory category, Severity severity, FixKind fix)
        {
            _def = def;
            Id = def.ID?.Trim() ?? "";
            Title = string.IsNullOrWhiteSpace(def.Name) ? Id : def.Name.Trim();
            Category = category; Severity = severity; Fix = fix;
        }

        public override RuleEvaluation Evaluate(Model model)
        {
            var ev = new RuleEvaluation();
            var seen = new HashSet<string>(StringComparer.Ordinal);      // population refs, de-duped across expanded scopes
            var flagged = new HashSet<string>(StringComparer.Ordinal);
            foreach (var scope in BpaAnalyzer.ExpandScopes(_def.Scope))
            {
                var collection = BpaAnalyzer.GetCollection(model, scope);
                if (collection == null)
                { ev.Errors.Add($"Custom rule '{Id}': scope '{scope}' is not supported ({string.Join(", ", BpaAnalyzer.SupportedScopeNames)}). Fix it and reload with load_readiness_rules."); continue; }
                try
                {
                    var pop = string.IsNullOrWhiteSpace(_def.AppliesTo) ? collection : collection.Where(_def.AppliesTo);
                    foreach (var o in pop)
                        if (o is ITabularNamedObject n && seen.Add(Refs.For(n))) ev.Applicable++;
                    foreach (var o in pop.Where(_def.Expression))
                        if (o is ITabularNamedObject n && flagged.Add(Refs.For(n)))
                        {
                            var f = Finding(n, (n as IDaxObject)?.DaxObjectFullName ?? n.Name, MessageFor(n));
                            f.Custom = true;                              // provenance: the UI/agents can tell custom from built-in
                            ev.Violations.Add(f);
                        }
                }
                catch (Exception ex)
                {
                    ev.Errors.Add($"Custom rule '{Id}' failed to evaluate on scope {scope}: {ex.Message}. Fix the expression (validate_rule previews it) and reload with load_readiness_rules.");
                }
            }
            // Scoring integrity: a broken rule contributes NOTHING. The population loop may have counted
            // Applicable before the Expression threw — a positive Applicable with no violations would score as a
            // ~100% pass and could even activate an otherwise-dormant category. Any error ⇒ dormant
            // (Applicable=0, no findings); the failure surfaces via Scorecard.RuleErrors only.
            if (ev.Errors.Count > 0) { ev.Applicable = 0; ev.Violations.Clear(); }
            return ev;
        }

        private string MessageFor(ITabularNamedObject obj)
        {
            var name = (obj as IDaxObject)?.DaxObjectFullName ?? obj.Name;
            var m = string.IsNullOrWhiteSpace(_def.Message) ? _def.Description : _def.Message;
            if (string.IsNullOrWhiteSpace(m)) return $"{name} violates '{Title}'.";
            return m.Replace("%object%", name).Replace("%objectname%", obj.Name ?? "");
        }
    }

    /// <summary>The shared compile + honest-test-run seam behind validate_rule and the Studio authoring form —
    /// BOTH rule kinds, one vocabulary, evaluated through the exact code paths a real scan uses.</summary>
    public static class RuleAuthoring
    {
        public static RuleCheck CheckReadiness(Model model, CustomReadinessRuleDef def)
        {
            var check = new RuleCheck { Id = def?.ID };
            var errors = CustomReadinessRuleSet.Validate(model, def);
            if (errors.Count == 0)
            {
                CustomReadinessRuleSet.TryParseCategory(def.Category, out var cat);
                CustomReadinessRuleSet.TryParseSeverity(def.Severity, out var sev);
                CustomReadinessRuleSet.TryParseFix(def.FixKind, out var fix);
                var ev = new CustomReadinessRule(def, cat, sev, fix).Evaluate(model);
                errors.AddRange(ev.Errors);
                check.Applicable = ev.Applicable;
                check.Violations = ev.Violations.Count;
                check.Sample = ev.Violations.Take(5).Select(v => v.ObjectName).ToArray();
                check.Dormant = ev.Applicable == 0 && errors.Count == 0;
                // No Note when the test run errored — the errors ARE the verdict (the counts were reset to dormant).
                if (errors.Count == 0)
                    check.Note = check.Dormant
                        ? "No objects match this rule's population on the open model; it would stay dormant (Applicable=0) and never move the score."
                        : $"Test run only; nothing saved. Would evaluate {check.Applicable} object(s) and flag {check.Violations}.";
            }
            check.Errors = errors.ToArray();
            check.Valid = errors.Count == 0;
            return check;
        }

        public static RuleCheck CheckBpa(Model model, BpaRule rule)
        {
            var check = new RuleCheck { Id = rule?.ID };
            var errors = new List<string>();
            if (rule == null) { errors.Add("The rule is empty: pass a JSON object with ID, Scope and Expression."); }
            else
            {
                if (string.IsNullOrWhiteSpace(rule.ID))
                    errors.Add("Every rule needs an \"ID\": a short stable id like ORG_MEASURE_DESC (it is how merges, waivers and violations refer to the rule).");
                if (string.IsNullOrWhiteSpace(rule.Expression) && string.IsNullOrWhiteSpace(rule.TokenCheck))
                    errors.Add("An \"Expression\" is required: the condition that flags an object, e.g. string.IsNullOrEmpty(Description).");
                if (string.IsNullOrWhiteSpace(rule.Scope))
                    errors.Add($"A \"Scope\" is required: which objects the rule checks. Supported: {string.Join(", ", BpaAnalyzer.SupportedScopeNames)}.");

                var modelCl = model.Database?.CompatibilityLevel ?? 0;
                if (rule.CompatibilityLevel > 0 && rule.CompatibilityLevel > modelCl)
                    check.Note = $"This rule requires compatibility level {rule.CompatibilityLevel}; the open model is {modelCl}, so a scan would skip it here.";

                if (errors.Count == 0)
                {
                    foreach (var scope in BpaAnalyzer.ExpandScopes(rule.Scope))
                    {
                        var collection = BpaAnalyzer.GetCollection(model, scope);
                        if (collection == null) { errors.Add($"Scope '{scope}' is not supported. Supported: {string.Join(", ", BpaAnalyzer.SupportedScopeNames)}."); continue; }
                        foreach (var o in collection) if (o is ITabularNamedObject) check.Applicable++;
                    }
                    // The REAL scan path evaluates the rule (Dynamic-LINQ or TokenCheck) — its RuleErrors carry
                    // compile/eval failures with the same wording a real scan would show.
                    var card = BpaAnalyzer.Analyze(model, new[] { rule });
                    errors.AddRange(card.RuleErrors);
                    check.Violations = card.Violations.Length;
                    check.Sample = card.Violations.Take(5).Select(v => v.ObjectName).ToArray();
                    if (errors.Count == 0)
                    {
                        if (BpaRuleSet.Standard().Any(r => string.Equals(r.ID, rule.ID, StringComparison.OrdinalIgnoreCase)))
                            check.Note = $"'{rule.ID}' matches a bundled standard rule. Loading it will OVERRIDE that rule on this model (model rules win by id). Pick a new id to add alongside instead.";
                        else if (check.Note == null)
                            check.Note = $"Test run only; nothing saved. Would evaluate {check.Applicable} object(s) and flag {check.Violations}."
                                         + (!string.IsNullOrWhiteSpace(rule.FixExpression) && !BpaAnalyzer.CanAutoFix(rule.FixExpression)
                                            ? " Note: the FixExpression is not deterministically applicable (only Property = literal is); bpa_fix will not auto-apply it."
                                            : "");
                    }
                }
            }
            check.Errors = errors.ToArray();
            check.Valid = errors.Count == 0;
            return check;
        }
    }
}
