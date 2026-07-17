using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Linguistics;
using TabularEditor.TOMWrapper.Utils;   // CalendarOps: the raw-TOM calendar-presence seam (Calendars aren't wrapped)

namespace Semanticus.Analysis
{
    public sealed class RuleEvaluation
    {
        public int Applicable;
        public List<ReadinessFinding> Violations = new List<ReadinessFinding>();
        // Rule problems (unsupported scope, an expression that failed to evaluate) surfaced on the scorecard's
        // RuleErrors instead of a silent pass. Only custom (user-authored) rules populate this; built-ins never do.
        public List<string> Errors = new List<string>();
    }

    /// <summary>Per-rule carry-forward state for the incremental (scoped) readiness rescan — owned by the caller
    /// (one per session in the health probe), filled by <see cref="ReadinessRule.EvaluateScoped"/>. The contract:
    /// population membership is re-enumerated FRESH every scan (adds/removes/applies-flips are always exact); only
    /// the per-object VERDICT is carried, and only for objects that were neither touched this commit nor unseen on
    /// the last scan (a re-keyed ref — e.g. a table rename moving every child ref — reads as unseen and re-evaluates).</summary>
    public sealed class RuleScanMemo
    {
        internal HashSet<string> PopRefs;                               // refs evaluated on the last scan (null = never scanned → everything fresh)
        internal Dictionary<string, ReadinessFinding> ViolationsByRef;  // the last scan's finding per ref
        internal string ContextStamp;                                   // rule-defined cross-object input (e.g. the table-name set); a change voids every carry
        internal void Reset() { PopRefs = null; ViolationsByRef = null; ContextStamp = null; }
    }

    /// <summary>The session-scoped memo for <see cref="ReadinessAnalyzer"/>'s incremental path: one
    /// <see cref="RuleScanMemo"/> per rule id. Seeded by a full scan, rolled forward by each scoped rescan.</summary>
    public sealed class ReadinessScanState
    {
        internal readonly Dictionary<string, RuleScanMemo> Rules = new Dictionary<string, RuleScanMemo>(StringComparer.Ordinal);
        internal RuleScanMemo For(string ruleId)
        {
            if (!Rules.TryGetValue(ruleId, out var m)) Rules[ruleId] = m = new RuleScanMemo();
            return m;
        }
        internal void Reset() { foreach (var m in Rules.Values) m.Reset(); }
    }

    public abstract class ReadinessRule
    {
        public abstract string Id { get; }
        public abstract string Title { get; }
        public abstract ReadinessCategory Category { get; }
        public abstract Severity Severity { get; }
        public abstract RuleKind Kind { get; }
        public abstract FixKind Fix { get; }
        public abstract RuleEvaluation Evaluate(Model model);

        /// <summary>Scoped incremental re-evaluation (the health probe's per-commit path): re-evaluate ONLY the
        /// objects in <paramref name="touchedRefs"/> (plus anything new/re-keyed since the memoized scan), carrying
        /// the last scan's verdict for the untouched rest. Sound only where the per-object verdict reads nothing but
        /// the object's own state (plus a declared <see cref="RuleScanMemo.ContextStamp"/>); custom cross-object
        /// ModelRules therefore return null here and the analyzer falls back to their full <see cref="Evaluate"/> —
        /// correctness first, and those rules are the cheap population sweeps, not the per-expression lints.</summary>
        public virtual RuleEvaluation EvaluateScoped(Model model, ISet<string> touchedRefs, RuleScanMemo memo) => null;

        // Parts are literal strings plus optional out-of-band ReadinessCopy.Op(...) hints. A rule with no op passes a
        // single string (custom rules and the ~40 plain built-ins do exactly this); an op-bearing rule interleaves
        // Op(...) parts. ReadinessCopy.Compose builds Message (op rendered inline, byte-identical to the old literal)
        // and DisplayMessage (op dropped, null when there is no op) WITHOUT ever scanning or mutating a string — so an
        // object name or custom message containing parentheses or any control char flows through both fields untouched.
        protected ReadinessFinding Finding(ITabularObject obj, string objName, params object[] parts)
        {
            var (message, display) = ReadinessCopy.Compose(parts);
            return new ReadinessFinding
            {
                RuleId = Id,
                RuleTitle = Title,
                Category = Category.ToString(),
                Severity = Severity.ToString(),
                Fix = Fix.ToString(),
                ObjectRef = Refs.For(obj),
                ObjectName = objName,
                Message = message,
                DisplayMessage = display,
            };
        }
    }

    /// <summary>A per-object rule: a scope selector, an applicability filter, and a violation predicate.</summary>
    public sealed class ObjectRule<T> : ReadinessRule where T : ITabularNamedObject
    {
        private readonly Func<Model, IEnumerable<T>> _scope;
        private readonly Func<T, bool> _applies;
        private readonly Func<T, bool> _violates;
        private readonly Func<T, string> _message;

        public override string Id { get; }
        public override string Title { get; }
        public override ReadinessCategory Category { get; }
        public override Severity Severity { get; }
        public override RuleKind Kind { get; }
        public override FixKind Fix { get; }

        public ObjectRule(string id, string title, ReadinessCategory category, Severity severity, RuleKind kind, FixKind fix,
            Func<Model, IEnumerable<T>> scope, Func<T, bool> applies, Func<T, bool> violates, Func<T, string> message)
        {
            Id = id; Title = title; Category = category; Severity = severity; Kind = kind; Fix = fix;
            _scope = scope; _applies = applies; _violates = violates; _message = message;
        }

        public override RuleEvaluation Evaluate(Model model)
        {
            var pop = _scope(model).Where(_applies).ToList();
            var e = new RuleEvaluation { Applicable = pop.Count };
            foreach (var o in pop)
                if (_violates(o))
                    e.Violations.Add(Finding(o, o.Name, _message(o)));
            return e;
        }

        /// <summary>Incremental variant: population membership (scope + applies) is re-enumerated fresh, so
        /// Applicable is always exact; only _violates/_message are skipped for untouched, previously-seen objects.
        /// Every ObjectRule's violates/message reads the object's OWN state only (descriptions, names, format,
        /// own relationship endpoints, own SortByColumn, …), and any own-prop change lands the object in
        /// touchedRefs via the commit's deltas — so a carried verdict cannot be stale.</summary>
        public override RuleEvaluation EvaluateScoped(Model model, ISet<string> touchedRefs, RuleScanMemo memo)
        {
            var ev = new RuleEvaluation();
            var pop = new HashSet<string>(StringComparer.Ordinal);
            var viol = new Dictionary<string, ReadinessFinding>(StringComparer.Ordinal);
            var carry = memo.PopRefs;   // null on the seeding scan → everything evaluates fresh
            foreach (var o in _scope(model).Where(_applies))
            {
                var r = Refs.For(o);
                ev.Applicable++;
                pop.Add(r);
                ReadinessFinding f;
                if (carry != null && carry.Contains(r) && !touchedRefs.Contains(r))
                    memo.ViolationsByRef.TryGetValue(r, out f);   // set together with PopRefs — non-null when carry is
                else
                    f = _violates(o) ? Finding(o, o.Name, _message(o)) : null;
                if (f != null) { ev.Violations.Add(f); viol[r] = f; }
            }
            memo.PopRefs = pop;
            memo.ViolationsByRef = viol;
            return ev;
        }
    }

    /// <summary>A model-level rule with custom evaluation (cross-object: duplicates, date table, scale).</summary>
    public sealed class ModelRule : ReadinessRule
    {
        private readonly Func<Model, ModelRule, RuleEvaluation> _eval;
        public override string Id { get; }
        public override string Title { get; }
        public override ReadinessCategory Category { get; }
        public override Severity Severity { get; }
        public override RuleKind Kind { get; }
        public override FixKind Fix { get; }

        public ModelRule(string id, string title, ReadinessCategory category, Severity severity, RuleKind kind, FixKind fix,
            Func<Model, ModelRule, RuleEvaluation> eval)
        {
            Id = id; Title = title; Category = category; Severity = severity; Kind = kind; Fix = fix; _eval = eval;
        }

        public ReadinessFinding NewFinding(ITabularObject obj, string name, params object[] parts) => Finding(obj, name, parts);
        public override RuleEvaluation Evaluate(Model model) => _eval(model, this);
    }

    public static class ReadinessRuleSet
    {
        // Programmer-identifier "code smell" tells: underscores, camelCase humps, key/id/code suffixes, table-type prefixes.
        private static readonly Regex CodeyName = new Regex(@"(_|[a-z][A-Z]|ID$|Key$|Cd$|^(tbl|dim|fact)\b)", RegexOptions.Compiled);
        // An all-caps run (≥2 letters). Reads as cryptic UNLESS it's a recognised business acronym (see below).
        private static readonly Regex AllCapsRun = new Regex(@"\b[A-Z]{2,}\b", RegexOptions.Compiled);

        // Identifier-shaped column names are useful candidates for a focused ambiguity check. A compound name such
        // as "Product Code" is self-explanatory enough to pass; the bare "Code" / "Key" / "Number" form is not.
        // Keep this disjoint from NAME-COLUMN: cryptic ProductID / Product_Key shapes already belong there.
        private static readonly Regex IdentifierShapedColumnName = new Regex(
            @"(?i)(^|[\s\-])(id|identifier|key|code|number|num|no)$", RegexOptions.Compiled);
        private static readonly Regex BareIdentifierColumnName = new Regex(
            @"(?i)^(id|identifier|key|code|number|num|no)$", RegexOptions.Compiled);

        // Mixed-case "period-over-period" time-intelligence abbreviations (Year-over-Year, Month-over-Month, …).
        // These are standard, AI-legible metric suffixes — but the inner lowercase-o-then-uppercase reads as a
        // camelCase hump to CodeyName's [a-z][A-Z] tell, so "Sales YoY" was wrongly flagged as cryptic. We strip
        // them (as whole tokens) before the cryptic test. The ALL-CAPS forms (YOY/MOM/…) are handled separately
        // by AllCapsRun + BusinessAcronyms; this set is only the mixed-case spellings that fool the hump heuristic.
        private static readonly Regex TimeIntelAbbrev = new Regex(
            @"(?<![A-Za-z0-9])(YoY|QoQ|MoM|WoW|DoD|HoH|PoP)(?![A-Za-z0-9])", RegexOptions.Compiled);

        // Acronyms an AI/LLM already knows, so they read as legitimate field names rather than cryptic codes —
        // (a) finance metrics/ratios/time-intelligence/taxes/currencies, and (b) UNIVERSAL business/general acronyms
        // (countries, departments, well-known systems). The cryptic pre-filter exempts these so it doesn't drown the
        // LLM-judgment signal in EBITDA/YTD/IT false positives. Kept DELIBERATELY tight on the AMBIGUOUS short codes —
        // VAR/ACT/BUD/AR/AP and any company-specific 2-letter code (MA/SA/OB/GL) are NOT here: in a non-finance model
        // they are genuinely cryptic, so they flow to LLM judgment (and, where instructions exist, DAC-GLOSSARY-GAP).
        private static readonly HashSet<string> BusinessAcronyms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EBIT","EBITDA","EBITA","EBT","PBT","PAT","NPAT","NPBT","NOPAT","EPS","DPS",
            "ROE","ROA","ROI","ROIC","ROCE","COGS","CAPEX","OPEX","FCF","WACC","NPV","IRR",
            "LTV","NWC","ARR","MRR","CAC","DSO","DPO","DIO",
            "YTD","MTD","QTD","WTD","YTG","LYTD","YOY","MOM","QOQ","WOW","DOD","FY","CY","PY","FCST",
            "VAT","GST","PDP","SPV","KPI",
            "USD","AUD","EUR","GBP","NZD","CAD","JPY",
            // Universal business/general acronyms an LLM reads without a glossary:
            "US","USA","UK","EU","UAE","FX","GDP","CPI",
            "IT","HR","RD","QA","PR","CRM","ERP","SKU","B2B","B2C","SLA","API","URL","FAQ",
            "CEO","CFO","COO","CTO","CIO","VP","GAAP","IFRS","ESG","ETA",
        };

        // Text columns whose values are month/weekday labels need a Sort By Column or Q&A/Copilot sort them alphabetically.
        private static readonly Regex MonthOrWeekday = new Regex(@"(?i)\b(month\s*name|month|weekday|day\s*of\s*(the\s*)?week|day\s*name)\b", RegexOptions.Compiled);

        // A second high-confidence Sort By signal: a text label with one unambiguous numeric companion on the same
        // table (Priority + Priority Sort, Month Name + Month Number). This closes ordered business labels without
        // guessing their order from the label name alone. The readiness fix remains a Proposal because the author
        // still confirms that the companion encodes the intended business order.
        private static readonly string[] SortLabelSuffixes = { "name", "label", "text" };
        private static readonly string[] SortOrderSuffixes = { "number", "ordinal", "sequence", "index", "order", "sort", "rank", "num", "no" };

        private static string SortNameKey(string name) =>
            Regex.Replace(name ?? string.Empty, @"[^A-Za-z0-9]", string.Empty).ToLowerInvariant();

        private static string StripSuffix(string key, IEnumerable<string> suffixes)
        {
            foreach (var suffix in suffixes)
                if (key.Length > suffix.Length + 2 && key.EndsWith(suffix, StringComparison.Ordinal))
                    return key.Substring(0, key.Length - suffix.Length);
            return key;
        }

        private static Column SortCompanion(Column label)
        {
            if (label?.Table == null) return null;
            var key = SortNameKey(label.Name);
            var stem = StripSuffix(key, SortLabelSuffixes);
            var matches = label.Table.Columns
                .Where(c => !ReferenceEquals(c, label) && IsNumeric(c))
                .Select(c => new { Column = c, Key = SortNameKey(c.Name) })
                .Where(x => SortOrderSuffixes.Any(s => x.Key.EndsWith(s, StringComparison.Ordinal)))
                .Where(x =>
                {
                    var candidateStem = StripSuffix(x.Key, SortOrderSuffixes);
                    return candidateStem == key || candidateStem == stem;
                }).Select(x => x.Column).Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        // Naming conventions that mark a measure as an internal helper/intermediate, not a business metric.
        // Only underscore-anchored / leading-token conventions — NOT a free 'helper' word, which matches legit
        // business names like "Helper Desk Tickets" / "Intermediate Goods Value".
        private static readonly Regex HelperMeasure = new Regex(@"(?i)(^\s*_|_(?:base|helper|aux|tmp|temp|calc|internal)\b|^(?:tmp|temp|aux)\b)", RegexOptions.Compiled);

        // Names that mark a column as TECHNICAL/AUDIT/ETL plumbing, not a business field — surrogate/business keys,
        // GUIDs, hashes, row-version/load/batch metadata, audit "...By" columns. Boundaries are alphanumeric-aware
        // lookarounds `(?<![A-Za-z0-9])…(?![A-Za-z0-9])` NOT `\b`, because '_' is a word char in .NET so `\b` would
        // MISS the dominant underscore-joined warehouse convention (ETL_Batch_ID, DW_LoadDate, Audit_Hash). The
        // lookarounds still spare business words (guidance, workload, cash, hashtag). guid\b/uuid\b keep a trailing-only
        // boundary so "rowguid" matches; _sk\b/_bk\b intentionally require the leading underscore.
        private static readonly Regex TechColumn = new Regex(
            @"(?i)(guid\b|uuid\b|(?<![A-Za-z0-9])rowversion(?![A-Za-z0-9])|row[\s_]version(?![A-Za-z0-9])|(?<![A-Za-z0-9])hash(?![A-Za-z0-9])|(?<![A-Za-z0-9])hashbytes(?![A-Za-z0-9])|(?<![A-Za-z0-9])etl(?![A-Za-z0-9])|_sk\b|_bk\b|(?<![A-Za-z0-9])surrogate[\s_]?key(?![A-Za-z0-9])|(?<![A-Za-z0-9])load[\s_]?(id|date|time)(?![A-Za-z0-9])|(?<![A-Za-z0-9])batch[\s_]?id(?![A-Za-z0-9])|(?<![A-Za-z0-9])source[\s_]?system(?![A-Za-z0-9])|(?<![A-Za-z0-9])(created|modified|inserted|updated|loaded)[\s_]?by(?![A-Za-z0-9]))",
            RegexOptions.Compiled);

        private static bool IsNumeric(Column c) => c.DataType == DataType.Int64 || c.DataType == DataType.Double || c.DataType == DataType.Decimal;

        // A numeric column whose NAME marks it as a non-additive IDENTIFIER/label, not a real measure: a year, a
        // month/quarter/week NUMBER, a postal/zip code, or anything ending in an id/code/key/number token. Summing
        // these is meaningless (a SUM of years / postal codes), so they want SummarizeBy=None — distinct from a real
        // implicit measure (Amount, Quantity) which wants an explicit measure. Microsoft Prep-for-AI guidance #2.
        // Feeds an AUTO-APPLIED SafeFix, so it is tuned for HIGH PRECISION (a false positive silently zeroes a real
        // grand total). Tuning, battery-tested:
        //  • year is anchored to the END (\byear$) so "Fiscal/Calendar/Order Year" match but the metric phrases
        //    "Year over Year Growth" / "3 Year Return" / "Yearly Revenue" do NOT (they don't end in "year").
        //  • the trailing-identifier forms are \b-anchored so "Paid"/"Monkey"/"Decode"/"Barcode" never match, and
        //    "Number of X" (a COUNT, additive) isn't flagged because the name doesn't END in the token; the bare
        //    trailing "number" carries word-anchored negative lookbehinds so additive aggregates "Total/Count/Units
        //    Number" are spared while "Account/Order/Invoice Number" (where "count"/"total" aren't whole words) still match.
        //  • a CASE-SENSITIVE camel-junction branch catches the no-space warehouse convention (ProductID, EmployeeID,
        //    CalendarYear, ProductKey, WeekNumber, AccountNo) that the \b forms miss, without re-flagging Decode/Acid/Grid.
        //  • month/day/age/order/rank/sequence/latitude/longitude are canonical numeric dimensions whose totals have
        //    no business meaning. Each is end-anchored so additive names such as "Order Quantity" remain untouched.
        //  • zip is END-anchored / "zip code" only, so "Zip Compression Ratio" is not a postal false positive.
        private static readonly Regex NonAdditiveDimName = new Regex(
            @"(?i)(\b(year|month|quarter|week|day|age|rank|order|sort|sequence|latitude|longitude|lat|lon|lng)$|\b(month|quarter|week|day)\s*(number|num|no)\b|\b(monthnum|monthnumber|weeknum|weeknumber|quarternum|daynum|daynumber)\b|\bpin(?:\s*code)?$|\bpostal\s*code\b|\bpostcode\b|\bzip\s*code\b|\bzip$|\b(?:(?<!\btotal\s)(?<!\bcount\s)(?<!\bunits\s)number|code|id|key)$)" +
            @"|(?-i:(?<=[a-z])(ID|Id|Key|Code|No|Number|Year)$)",
            RegexOptions.Compiled);

        /// <summary>True if a column NAME reads as a non-additive identifier (year / number / code / id / postal) that
        /// should not auto-aggregate. Callers gate on the column being numeric; this is name-only. Single source of
        /// truth shared by SUMMARIZE-DIMENSION, the DAC-IMPLICIT-MEASURE partition, and the bulk SafeFixes pass.</summary>
        public static bool IsNonAdditiveDimensionName(string name) =>
            !string.IsNullOrWhiteSpace(name) && NonAdditiveDimName.IsMatch(name.Trim());

        // A visible table carrying an ETL/modelling technical prefix in its CAPITALISED, space/dash/underscore-
        // separated form ("Fact Sales", "Dim Product", "Staging Orders"). NAME-TABLE (via IsCrypticName) already owns
        // the lowercase/camelCase forms (dim_, fact_, FactSales); NAME-TECH-PREFIX owns these. Tight prefix list — no
        // "Bridge"/"Lookup" (real business nouns, esp. in finance models) — to keep false positives near zero. The
        // \S after the separator requires real content after the prefix (so a name that is JUST "Fact" is left alone).
        private static readonly Regex TechTablePrefix = new Regex(@"(?i)^(fact|dim|dimension|stg|staging)[\s_\-]\S", RegexOptions.Compiled);

        // Business nouns that legitimately FOLLOW a Fact/Dim/… token, so the table name is NOT an ETL prefix
        // ("Dim Sum", "Fact Sheet", "Fact Check", "Dimension Lumber"). Structure alone can't separate these from
        // "Dim Product"/"Fact Sales", so a small denylist of the real-world collisions covers it (extend as found).
        private static readonly HashSet<string> TechPrefixNoun = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "sum", "sheet", "check", "lumber" };

        // True when a visible table name carries an ETL/modelling prefix AND the word after it isn't a known business
        // noun — so "Fact Sales"/"Dim Product" flag but "Fact Sheet"/"Dim Sum"/"Dimension Lumber" don't.
        private static bool IsTechPrefixedTableName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var m = TechTablePrefix.Match(name);
            if (!m.Success) return false;
            // The matched \S is the first char of the noun; take the whole first token from there.
            var noun = Regex.Match(name.Substring(m.Length - 1), @"^[^\s_\-]+").Value;
            return !TechPrefixNoun.Contains(noun);
        }

        // A name that has leading/trailing whitespace, an embedded control char (tab / line break) or an emoji /
        // surrogate-pair char — Microsoft's naming guidance forbids these on visible objects (they read badly to
        // Copilot/Q&A and can break references). Deliberately NARROW (clearly-invalid only) so ordinary punctuation
        // like "Sales (USD)" or "# Orders" never false-positives.
        private static bool HasInvalidNameChars(string name) =>
            !string.IsNullOrEmpty(name) && (name != name.Trim() || name.Any(c => char.IsControl(c) || char.IsSurrogate(c)));

        // Normalise a DAX expression for duplicate detection: strip line comments, collapse whitespace, lowercase
        // (DAX identifiers/functions are case-insensitive). Two measures that normalise identically are genuine
        // duplicates ("consolidate or differentiate"); whitespace/case/comment differences don't hide a real dup.
        private static string NormExpr(string dax)
        {
            if (string.IsNullOrWhiteSpace(dax)) return "";
            var noComments = Regex.Replace(dax, @"(//|--)[^\n]*", " ");
            return Regex.Replace(noComments, @"\s+", " ").Trim().ToLowerInvariant();
        }

        // A description that is really a PLACEHOLDER (not the field's actual meaning) — useless to Copilot/Q&A. Catches
        // the LONG placeholders DESC-ECHO's "< 12 chars" test misses ("TODO: write this", "to be completed"). "todo"/
        // "fixme" and the dictionary phrases fire as the OPENER (no real description starts that way); but the ambiguous
        // finance acronyms TBD/TBA (To-Be-Announced MBS, etc.) fire ONLY when STANDALONE — so "TBA mortgage-backed
        // security notional" is NOT a false positive.
        private static readonly Regex PlaceholderDesc = new Regex(
            @"(?i)^\s*(todo\b|fix\s?me\b|(tbd|tba)\s*[:.\-]*\s*$|x{3,}|placeholder\b|add (a |the )?description|description (here|goes here)|to be (defined|added|completed|written|done)|\.{2,}\s*$|-{2,}\s*$|\?{2,}\s*$)",
            RegexOptions.Compiled);

        // A valid Roman numeral (II, III, IV, IX, XII, …) is a human-readable sequence label ("Phase II", "Tier III"),
        // not a cryptic code. Matches Roman numerals only (anchored, non-empty), so it won't exempt arbitrary letters.
        private static readonly Regex RomanNumeral = new Regex(@"^(?=[MDCLXVI])M{0,4}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3})$", RegexOptions.Compiled);

        // Common ALL-CAPS prose words that show up in AI instructions as emphasis/headers/conjunctions — never a field
        // code a glossary would define. Subtracted from DAC-GLOSSARY-GAP's "defined" set so e.g. an "AND"/"IMPORTANT"
        // in the prose can't silently credit a same-spelled field code.
        private static readonly HashSet<string> GlossaryStopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AND","OR","NOT","THE","FOR","ARE","BUT","ALL","ANY","USE","PER","VIA","ETC",
            "IMPORTANT","ALWAYS","NEVER","ONLY","MUST","NOTE","NOTES","OVERVIEW","METRICS","SUMMARY","RULES",
        };

        public static bool IsCrypticName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            // Strip standard mixed-case period-over-period abbreviations (YoY/MoM/…) first: they're legitimate,
            // AI-legible time-intelligence suffixes that would otherwise trip CodeyName's camelCase-hump tell.
            // Whole-token only (the lookarounds), so a genuinely cryptic name embedding one isn't excused.
            var probe = TimeIntelAbbrev.Replace(name, " ");
            if (CodeyName.IsMatch(probe)) return true;
            // Cryptic only if it carries an all-caps run that ISN'T a recognised acronym or a Roman-numeral label.
            foreach (Match m in AllCapsRun.Matches(probe))
                if (!BusinessAcronyms.Contains(m.Value) && !RomanNumeral.IsMatch(m.Value)) return true;
            return false;
        }

        // Collapse runs of whitespace (incl. line wraps) to single spaces so incidental spacing differences
        // between a calc-item name and how it was typed into a description don't break a text match.
        private static string NormWs(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

        // True if needle occurs in haystack as a WHOLE token — bounded by non-alphanumerics (or the string edges) —
        // so a short calc-item name like "PY"/"Var"/"Act" isn't falsely matched inside "copy"/"variance"/"actuals".
        // Both args are expected NormWs-normalised. Matching the item's actual NAME (not an expansion) is intended:
        // the name is what the user sees in the calc-group slicer, so the description should reference it by name.
        private static bool MentionsToken(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return true;
            for (int i = 0; (i = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0; i++)
            {
                bool leftOk = i == 0 || !char.IsLetterOrDigit(haystack[i - 1]);
                bool rightOk = i + needle.Length >= haystack.Length || !char.IsLetterOrDigit(haystack[i + needle.Length]);
                if (leftOk && rightOk) return true;
            }
            return false;
        }

        public static string GeoCategory(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (ContainsNameToken(name, "latitude")) return "Latitude";
            if (ContainsNameToken(name, "longitude")) return "Longitude";
            if (ContainsNameToken(name, "address") && !NonGeoAddressName.IsMatch(name)) return "Address";
            if (ContainsNameToken(name, "place")) return "Place";
            if (ContainsNameToken(name, "city")) return "City";
            if (ContainsNameToken(name, "country")) return "Country";
            if (ContainsNameToken(name, "continent")) return "Continent";
            if (ContainsNameToken(name, "county")) return "County";
            if (ContainsNameToken(name, "state") || ContainsNameToken(name, "province")) return "StateOrProvince";
            if (ContainsNameToken(name, "postal") || ContainsNameToken(name, "zip")) return "PostalCode";
            return null;
        }

        // Word and camel-case boundary matching keeps field-name heuristics precise: CustomerCity and City_Name
        // match, while Capacity (contains "city") and Statement (starts with "state") do not.
        private static bool ContainsNameToken(string name, string token)
        {
            for (var start = 0; (start = name.IndexOf(token, start, StringComparison.OrdinalIgnoreCase)) >= 0; start++)
            {
                var end = start + token.Length;
                var left = start == 0 || !char.IsLetterOrDigit(name[start - 1])
                    || (char.IsLower(name[start - 1]) && char.IsUpper(name[start]));
                var right = end == name.Length || !char.IsLetterOrDigit(name[end])
                    || (char.IsLower(name[end - 1]) && char.IsUpper(name[end]));
                if (left && right) return true;
            }
            return false;
        }

        // Read only the highest-weight USER-AUTHORED term for each visible object. Generated/Suggested terms caused
        // the rejected naive SYN-COLLIDE prototype to flag legitimate recurring thesaurus words across a curated
        // model. Missing State means Authored in the published LSDL contract; equal weights preserve array order.
        private static List<(TabularNamedObject Object, string Term)> PrimaryAuthoredSynonyms(Model model, Culture culture)
        {
            var result = new List<(TabularNamedObject, string)>();
            if (culture?.ContentType != ContentType.Json || string.IsNullOrWhiteSpace(culture.Content)) return result;

            try
            {
                using var doc = JsonDocument.Parse(culture.Content);
                if (!doc.RootElement.TryGetProperty("Entities", out var entities) || entities.ValueKind != JsonValueKind.Object)
                    return result;

                var objects = new List<TabularNamedObject>();
                objects.AddRange(model.Tables.Where(t => !t.IsHidden));
                objects.AddRange(model.AllMeasures.Where(x => !x.IsHidden));
                objects.AddRange(model.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber));

                foreach (var obj in objects)
                {
                    JsonElement entity = default;
                    var found = false;
                    foreach (var candidate in entities.EnumerateObject())
                    {
                        if (!BindingMatches(candidate.Value, obj)) continue;
                        entity = candidate.Value;
                        found = true;
                        break;
                    }
                    if (!found || !entity.TryGetProperty("Terms", out var terms) || terms.ValueKind != JsonValueKind.Array)
                        continue;

                    string primary = null;
                    var bestWeight = double.MinValue;
                    foreach (var termObject in terms.EnumerateArray())
                    {
                        if (termObject.ValueKind != JsonValueKind.Object) continue;
                        foreach (var term in termObject.EnumerateObject())
                        {
                            if (term.Value.ValueKind != JsonValueKind.Object) continue;
                            var state = term.Value.TryGetProperty("State", out var stateNode) && stateNode.ValueKind == JsonValueKind.String
                                ? stateNode.GetString() : "Authored";
                            if (!string.Equals(state, "Authored", StringComparison.OrdinalIgnoreCase)) continue;
                            var weight = term.Value.TryGetProperty("Weight", out var weightNode) && weightNode.TryGetDouble(out var parsed)
                                ? parsed : 1.0;
                            if (primary == null || weight > bestWeight)
                            {
                                primary = term.Name;
                                bestWeight = weight;
                            }
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(primary)) result.Add((obj, primary.Trim()));
                }
            }
            catch (JsonException)
            {
                // Culture.Content is TOM-validated on write. If an external edit still leaves malformed JSON, the
                // existing fail-loud Prep-for-AI advisory owns it; a collision rule has no trustworthy population.
            }
            return result;
        }

        private static bool BindingMatches(JsonElement entity, TabularNamedObject obj)
        {
            JsonElement binding;
            if (!entity.TryGetProperty("Binding", out binding))
            {
                if (!entity.TryGetProperty("Definition", out var definition)
                    || !definition.TryGetProperty("Binding", out binding)) return false;
            }
            if (binding.ValueKind != JsonValueKind.Object
                || !binding.TryGetProperty("ConceptualEntity", out var tableNode)
                || tableNode.ValueKind != JsonValueKind.String) return false;

            if (obj is Table table)
                return string.Equals(tableNode.GetString(), table.Name, StringComparison.Ordinal)
                    && !binding.TryGetProperty("ConceptualProperty", out _);

            if (obj is ITabularTableObject tableObject)
                return string.Equals(tableNode.GetString(), tableObject.Table?.Name, StringComparison.Ordinal)
                    && binding.TryGetProperty("ConceptualProperty", out var propertyNode)
                    && propertyNode.ValueKind == JsonValueKind.String
                    && string.Equals(propertyNode.GetString(), obj.Name, StringComparison.Ordinal);

            return false;
        }

        // ---- DAX best-practice shared helpers (see docs/dax-best-practice-rules.md §3) ----------------------

        // The CORE time-intelligence functions whose presence means the model needs a marked date table. Closed set
        // (token path via DaxScan.CallsFunction) — deliberately EXCLUDES DATESBETWEEN/DATESINPERIOD, which have common
        // non-TI uses (rolling windows over any table), so their presence wouldn't prove a date-table dependency.
        private static readonly string[] CoreTiFunctions =
        {
            "TOTALYTD","TOTALQTD","TOTALMTD","DATESYTD","DATESQTD","DATESMTD","SAMEPERIODLASTYEAR","DATEADD",
            "PARALLELPERIOD","PREVIOUSYEAR","PREVIOUSQUARTER","PREVIOUSMONTH","PREVIOUSDAY","NEXTYEAR","NEXTQUARTER",
            "NEXTMONTH","NEXTDAY","OPENINGBALANCEYEAR","OPENINGBALANCEQUARTER","OPENINGBALANCEMONTH","CLOSINGBALANCEYEAR",
            "CLOSINGBALANCEQUARTER","CLOSINGBALANCEMONTH","FIRSTDATE","LASTDATE","STARTOFYEAR","STARTOFQUARTER",
            "STARTOFMONTH","ENDOFYEAR","ENDOFQUARTER","ENDOFMONTH",
        };

        // CLASSIC time-intelligence functions that have a calendar-aware overload (TOTALYTD(expr, 'Calendar') etc.) —
        // their presence in a measure means the model *could* adopt calendar-based time intelligence (CL 1701+). Matched
        // on the token path (DaxScan.CallsFunction) so a comment/string/[name] can't false-fire. Distinct from
        // CoreTiFunctions (the marked-date-table signal): this set is the classic forms the calendar overloads replace,
        // so it INCLUDES DATESINPERIOD (a rolling-window form with a calendar overload) and omits the OPENING/CLOSING/
        // FIRST/LAST/START/END-OF forms that CAL-TI-NO-CALENDAR's advisory doesn't speak to.
        private static readonly string[] ClassicTiFunctions =
        {
            "TOTALYTD","TOTALQTD","TOTALMTD","DATEADD","SAMEPERIODLASTYEAR","DATESYTD","DATESQTD","DATESMTD",
            "DATESINPERIOD","PARALLELPERIOD","PREVIOUSMONTH","PREVIOUSQUARTER","PREVIOUSYEAR","NEXTMONTH",
            "NEXTQUARTER","NEXTYEAR",
        };

        // Auto date/time footprint: a hidden PBI-generated local/template date table. GUID suffix REQUIRED on the name
        // form so a user table literally named "DateTableTemplate" is NOT flagged (only the engine-generated tokens).
        private static readonly Regex AutoDateTableName = new Regex(@"^(LocalDateTable|DateTableTemplate)_[0-9a-fA-F-]{36}$", RegexOptions.Compiled);

        // Every scorable DAX-bearing object: measures + calculated columns + calc-group items, skipping empty
        // expressions. Mixed object types (a measure, a CalculatedColumn, a CalculationItem) is why the BestPractice
        // rules are ModelRule, not ObjectRule<T> — ObjectRule is single-typed.
        private static IEnumerable<(ITabularNamedObject obj, string name, string expr)> DaxObjects(Model m)
        {
            foreach (var me in m.AllMeasures)
                if (!string.IsNullOrWhiteSpace(me.Expression)) yield return (me, me.Name, me.Expression);
            foreach (var cc in m.AllColumns.OfType<CalculatedColumn>())
                if (!string.IsNullOrWhiteSpace(cc.Expression)) yield return (cc, cc.Name, cc.Expression);
            foreach (var g in m.CalculationGroups)
                foreach (var ci in g.CalculationItems)
                    if (!string.IsNullOrWhiteSpace(ci.Expression)) yield return (ci, ci.Name, ci.Expression);
        }

        // Table-name context so DaxLint's identity-dependent rules (calculate-bare-table-filter) can activate.
        private static DaxLintContext LintCtx(Model m)
        {
            var ctx = new DaxLintContext();
            foreach (var t in m.Tables) ctx.TableNames.Add(t.Name);
            return ctx;
        }

        // Factory for a SCORED per-object BestPractice rule. Applicable = the VIOLATION population (presence design):
        // a rule presents only when the smell actually exists, then scores 0 — the category is dormant on clean models
        // and can only DOCK, never lift. Why not proportional-when-present (violators / construct-users or / all DAX
        // objects)? Structurally perverse: a present category scoring above the model's weighted average would RAISE
        // the overall — one stray IFERROR improving the grade. The adversarial review proved the trigger-population
        // variant did exactly that (a benign IF activated the category at score 100). trigger() is therefore only a
        // cheap pre-filter that skips the full lint on objects that can't possibly violate — it must be a SUPERSET of
        // the violation shape, never the population. Waiver semantics stay standard: a waived finding counts as pass.
        private static ReadinessRule DaxRule(string id, string title, Severity sev, FixKind fix, string lintRuleId, Func<List<DaxScan.Tok>, bool> trigger) =>
            new DaxBestPracticeRule(id, title, sev, fix, lintRuleId, trigger);

        // Factory for an ADVISORY BestPractice rule: surfaces findings for the lint rule but keeps Applicable=0, so the
        // category is never activated by it alone (mirrors DAC-COPILOT-TOOLING-FORMAT — the analyzer only scores
        // Applicable>0 evals but always surfaces findings). Lints EVERY DaxObjects entry (no trigger). Info/None.
        private static ReadinessRule DaxAdvisory(string id, string title, string lintRuleId) =>
            new DaxBestPracticeRule(id, title, Severity.Info, FixKind.None, lintRuleId, trigger: null);

        /// <summary>The per-DAX-object BestPractice rule (was two ModelRule factory lambdas): population =
        /// <see cref="DaxObjects"/> (measures + calc columns + calc items), verdict = the token-path lint. Promoted
        /// to a first-class rule so the health probe's scoped rescan can re-lint ONLY the touched expressions and
        /// carry the rest forward — DaxLint tokenization is THE recurring cost of a full scan (15 rules × every
        /// expression), and it depends on nothing but the expression and the model's table-name set (declared as
        /// the memo's ContextStamp so a table create/rename/delete voids every carried verdict). Full-scan
        /// semantics are unchanged from the old factories, including the presence-design Applicable.</summary>
        private sealed class DaxBestPracticeRule : ReadinessRule
        {
            private readonly string _lintRuleId;
            private readonly Func<List<DaxScan.Tok>, bool> _trigger;   // null = advisory: lint everything, Applicable stays 0

            public override string Id { get; }
            public override string Title { get; }
            public override ReadinessCategory Category => ReadinessCategory.BestPractice;
            public override Severity Severity { get; }
            public override RuleKind Kind => RuleKind.Deterministic;
            public override FixKind Fix { get; }

            public DaxBestPracticeRule(string id, string title, Severity sev, FixKind fix, string lintRuleId, Func<List<DaxScan.Tok>, bool> trigger)
            {
                Id = id; Title = title; Severity = sev; Fix = fix;
                _lintRuleId = lintRuleId; _trigger = trigger;
            }

            public override RuleEvaluation Evaluate(Model model) => EvaluateCore(model, null, null);

            public override RuleEvaluation EvaluateScoped(Model model, ISet<string> touchedRefs, RuleScanMemo memo)
                => EvaluateCore(model, touchedRefs, memo);

            private RuleEvaluation EvaluateCore(Model model, ISet<string> touchedRefs, RuleScanMemo memo)
            {
                var ctx = LintCtx(model);
                // The lint's identity-dependent checks (calculate-bare-table-filter) read the TABLE-NAME SET: if it
                // changed since the memo was built, an untouched expression's verdict may have flipped — void the carry.
                var stamp = string.Join("\n", model.Tables.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal));
                var carry = memo?.PopRefs;
                if (memo != null && !string.Equals(memo.ContextStamp, stamp, StringComparison.Ordinal)) carry = null;

                var ev = new RuleEvaluation();
                var pop = memo != null ? new HashSet<string>(StringComparer.Ordinal) : null;
                var viol = memo != null ? new Dictionary<string, ReadinessFinding>(StringComparer.Ordinal) : null;
                foreach (var o in DaxObjects(model))
                {
                    var r = Refs.For(o.obj);
                    pop?.Add(r);
                    ReadinessFinding f;
                    if (carry != null && carry.Contains(r) && !touchedRefs.Contains(r))
                        memo.ViolationsByRef.TryGetValue(r, out f);
                    else
                        f = Lint(o.obj, o.name, o.expr, ctx);
                    if (f == null) continue;
                    if (_trigger != null) ev.Applicable++;   // scored: presence design (Applicable = the violation population)
                    ev.Violations.Add(f);
                    if (viol != null) viol[r] = f;
                }
                if (memo != null) { memo.PopRefs = pop; memo.ViolationsByRef = viol; memo.ContextStamp = stamp; }
                return ev;
            }

            private ReadinessFinding Lint(ITabularNamedObject obj, string name, string expr, DaxLintContext ctx)
            {
                if (_trigger != null && !_trigger(DaxScan.Tokenize(expr))) return null;   // cheap pre-filter — never the population
                var hit = DaxLint.Analyze(expr, ctx).Findings.FirstOrDefault(f => f.RuleId == _lintRuleId);
                return hit == null ? null : Finding(obj, name, hit.Message);
            }
        }

        // ---- Catalog batch 1 shared helpers (docs/ai-readiness-catalog.json gaps) ---------------------------

        // Auto-generated placeholder object names ("Table1", "Column 2", "Measure3", "col1", "Field 4"): the
        // modelling tool's default names, never business language. Anchored + digit-suffixed so real names ("Table
        // Sales", a measure literally named "Column Width") never match — only the placeholder-word + optional-space
        // + digits form. (Modelling PREFIXES dim_/fact_/tbl_ are owned by NAME-* via IsCrypticName; the !IsCrypticName
        // guard in IsGenericPlaceholderName keeps this disjoint so there's no Naming double-count.)
        private static readonly Regex GenericObjectName =
            new Regex(@"(?i)^(table|column|col|measure|field)\s*\d+$", RegexOptions.Compiled);

        public static bool IsGenericPlaceholderName(string name) =>
            !string.IsNullOrWhiteSpace(name) && GenericObjectName.IsMatch(name.Trim()) && !IsCrypticName(name);

        // A column NAME that strongly implies a DATE or a MONETARY/QUANTITY number. TIGHT + precision-tuned against
        // AdventureWorks: \bdate\b (a date column stored as text is the classic import bug) plus money/quantity nouns
        // anchored to the END of the name ("Sales Amount", "Unit Price", "Total Cost") — the END anchor is load-
        // bearing: it spares text dimensions that merely CONTAIN a money word ("Sales Territory Region", "Profit
        // Center", "Discount Type", "Tax Region"). "sales"/"revenue" as free tokens are deliberately NOT matched (they
        // pervade text dimension names); the ambiguous number/id/code/year tokens are owned by SUMMARIZE-DIMENSION.
        private static readonly Regex TypeImplyingName = new Regex(
            @"(?i)(\bdate\b|\b(amount|price|cost|quantity|qty|discount|tax|balance|revenue|profit|margin)$)",
            RegexOptions.Compiled);

        // A column NAME that marks it as a link / image URL. Requires an explicit url/uri/link token so binary
        // "Photo"/"LargePhoto" columns (images stored as data, NOT links) don't false-fire on CAT-URL.
        private static readonly Regex UrlColumnName = new Regex(
            @"(?i)(\burl\b|url$|\buri\b|image\s*url|photo\s*url|web\s*link|hyperlink)", RegexOptions.Compiled);

        // Barcode is a first-class Power BI Data Category. Require the barcode token at the end (optionally followed
        // by an identifier/value suffix) so a business metric such as "Barcode Adoption" is not auto-classified.
        private static readonly Regex BarcodeColumnName = new Regex(
            @"(?i)(^|[^a-z0-9])(bar\s*code|barcode)(\s*(id|value|number|no|text))?$", RegexOptions.Compiled);

        // These are communication/network fields, not geographic addresses. Keep them out of the CAT-GEO SafeFix.
        private static readonly Regex NonGeoAddressName = new Regex(
            @"(?i)(e-?mail|web|ip)[\s_\-]*address", RegexOptions.Compiled);

        // Whether a URL column's name reads as an IMAGE link (→ ImageUrl data category) vs a plain web link (→ WebUrl).
        private static bool ImpliesImageUrl(string name) =>
            !string.IsNullOrEmpty(name) && Regex.IsMatch(name, @"(?i)(image|photo|picture|thumbnail|logo|icon)");

        private static string LinkCategory(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (BarcodeColumnName.IsMatch(name)) return "Barcode";
            if (!UrlColumnName.IsMatch(name)) return null;
            return ImpliesImageUrl(name) ? "ImageUrl" : "WebUrl";
        }

        // Drill-down candidate detection for REL-HIERARCHY-MISSING (name/GeoCategory based — no live data needed).
        private static int GeoColumnCount(Table t) =>
            t.Columns.Count(c => c.Type != ColumnType.RowNumber && GeoCategory(c.Name) != null);
        private static readonly Regex DatePartName = new Regex(@"(?i)\b(year|quarter|month|week|day)\b", RegexOptions.Compiled);

        // A date-column name that marks it as a validity-RANGE boundary (the start/end of one event's window), not an
        // independent EVENT date. Used by DATE-AMBIGUOUS to strip Start/End (SCD-style) pairs so they aren't mistaken
        // for the multiple-event-date ambiguity (Order/Ship/Due). Deliberately tight (whole-word boundaries).
        private static readonly Regex RangeBoundaryDateName = new Regex(
            @"(?i)\b(start|begin|end|finish|expiry|expiration|expire|expired|effective|thru|through)\b", RegexOptions.Compiled);
        private static int DatePartColumnCount(Table t) =>
            t.Columns.Count(c => c.Type != ColumnType.RowNumber && DatePartName.IsMatch(c.Name ?? ""));

        public static IReadOnlyList<ReadinessRule> Default()
        {
            var rules = new List<ReadinessRule>();

            // ---- Descriptions -------------------------------------------------------------------
            rules.Add(new ObjectRule<Measure>("DESC-MEASURE", "Measure has no description", ReadinessCategory.Descriptions, Severity.High, RuleKind.LlmGenerate, FixKind.AiContent,
                m => m.AllMeasures, x => !x.IsHidden, x => string.IsNullOrWhiteSpace(x.Description),
                x => $"Measure [{x.Name}] has no description; Copilot/Q&A rely on descriptions to answer questions about it."));

            // Calc-group tables/columns are excluded here — their documentation is owned solely by DAC-CALC-GROUP
            // (otherwise one undescribed calc group is counted under DESC-TABLE, DESC-COLUMN and DAC-CALC-GROUP).
            rules.Add(new ObjectRule<Table>("DESC-TABLE", "Table has no description", ReadinessCategory.Descriptions, Severity.Medium, RuleKind.LlmGenerate, FixKind.AiContent,
                m => m.Tables, x => !x.IsHidden && !(x is CalculationGroupTable), x => string.IsNullOrWhiteSpace(x.Description),
                x => $"Table '{x.Name}' has no description."));

            rules.Add(new ObjectRule<Column>("DESC-COLUMN", "Visible column has no description", ReadinessCategory.Descriptions, Severity.Medium, RuleKind.LlmGenerate, FixKind.AiContent,
                m => m.AllColumns, x => !x.IsHidden && x.Type != ColumnType.RowNumber && !(x.Table is CalculationGroupTable), x => string.IsNullOrWhiteSpace(x.Description),
                x => $"Column [{x.Table?.Name}].[{x.Name}] has no description."));

            rules.Add(new ObjectRule<Measure>("DESC-ECHO", "Description is non-informative (echo / too short / placeholder)", ReadinessCategory.Descriptions, Severity.Info, RuleKind.LlmJudgment, FixKind.AiContent,
                m => m.AllMeasures, x => !x.IsHidden && !string.IsNullOrWhiteSpace(x.Description),
                x => string.Equals(x.Description.Trim(), x.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                     || x.Description.Trim().Length < 12
                     || PlaceholderDesc.IsMatch(x.Description.Trim()),   // a long placeholder ("TODO: …") the length test misses
                x => $"Measure [{x.Name}] description is non-informative (repeats the name, is too short, or is a placeholder)."));

            // The same placeholder check for TABLES and visible COLUMNS that DESC-ECHO applies to measures: a PRESENT
            // description that's really a placeholder ("TODO: …") is useless to Copilot/Q&A. DESC-TABLE/DESC-COLUMN own
            // the MISSING-description case (mutually exclusive — they only evaluate objects with no description); measures
            // are owned by DESC-ECHO (no double-count). Calc-group objects excluded (DAC-CALC-GROUP owns their docs).
            rules.Add(new ModelRule("DESC-PLACEHOLDER", "Table/column description is a placeholder", ReadinessCategory.Descriptions, Severity.Info, RuleKind.LlmJudgment, FixKind.AiContent,
                (m, rule) =>
                {
                    var described = new List<(ITabularObject obj, string name, string desc)>();
                    foreach (var t in m.Tables.Where(t => !t.IsHidden && !(t is CalculationGroupTable) && !string.IsNullOrWhiteSpace(t.Description)))
                        described.Add((t, t.Name, t.Description));
                    foreach (var c in m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber && !(c.Table is CalculationGroupTable) && !string.IsNullOrWhiteSpace(c.Description)))
                        described.Add((c, c.Name, c.Description));
                    var ev = new RuleEvaluation { Applicable = described.Count };
                    foreach (var d in described.Where(d => PlaceholderDesc.IsMatch(d.desc.Trim())))
                        ev.Violations.Add(rule.NewFinding(d.obj, d.name, $"Description of '{d.name}' is a placeholder, not what the field actually means; replace it with the real meaning so Copilot and Q&A can answer questions about the field."));
                    return ev;
                }));

            rules.Add(new ObjectRule<Measure>("DESC-LONG", "Description exceeds 200 chars (Copilot truncates)", ReadinessCategory.Descriptions, Severity.Medium, RuleKind.Deterministic, FixKind.AiContent,
                m => m.AllMeasures, x => !x.IsHidden && !string.IsNullOrWhiteSpace(x.Description), x => x.Description.Length > 200,
                x => $"Measure [{x.Name}] description is {x.Description.Length} chars; only the first ~200 are used."));

            // The same 200-char Copilot read-limit for visible TABLES and COLUMNS (Microsoft: "only the first 200
            // characters are read by Copilot — front-load the key info"). DESC-LONG owns measures; this owns tables +
            // columns (mutually exclusive object types → no double-count). Calc-group objects excluded (DAC-CALC-GROUP
            // owns their docs). Applicable = described visible tables/columns, so it's dormant on an undocumented model.
            rules.Add(new ModelRule("DESC-LONG-OBJECT", "Table/column description exceeds 200 chars (Copilot truncates)", ReadinessCategory.Descriptions, Severity.Medium, RuleKind.Deterministic, FixKind.AiContent,
                (m, rule) =>
                {
                    var described = new List<(ITabularObject obj, string name, int len)>();
                    foreach (var t in m.Tables.Where(t => !t.IsHidden && !(t is CalculationGroupTable) && !string.IsNullOrWhiteSpace(t.Description)))
                        described.Add((t, t.Name, t.Description.Length));
                    foreach (var c in m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber && !(c.Table is CalculationGroupTable) && !string.IsNullOrWhiteSpace(c.Description)))
                        described.Add((c, c.Name, c.Description.Length));
                    var ev = new RuleEvaluation { Applicable = described.Count };
                    foreach (var d in described.Where(d => d.len > 200))
                        ev.Violations.Add(rule.NewFinding(d.obj, d.name, $"'{d.name}' description is {d.len} chars; Copilot reads only the first ~200; front-load the key info (preferred usage, disambiguation, units)."));
                    return ev;
                }));

            // Calc items carry no metadata Copilot/Data Agents can see — their only surviving documentation is the
            // group's description. DAC-CALC-GROUP owns the *undocumented* group; this refines it: a group that IS
            // documented but whose description omits some item names still hides those items from AI. Mutually
            // exclusive with DAC-CALC-GROUP per group (undocumented → DAC-CALC-GROUP; documented-incomplete → here),
            // so no double-count. Applicable = documented calc groups with items, so it's dormant otherwise.
            rules.Add(new ModelRule("DESC-CALCGROUP-ITEMS", "Calc-group description omits its item names", ReadinessCategory.Descriptions, Severity.Medium, RuleKind.LlmJudgment, FixKind.AiContent,
                (m, rule) =>
                {
                    var ev = new RuleEvaluation { Applicable = 0 };
                    foreach (var t in m.Tables.OfType<CalculationGroupTable>())
                    {
                        var col = t.Columns.FirstOrDefault(c => c.Type != ColumnType.RowNumber);
                        var desc = NormWs((t.Description ?? "") + " " + (col?.Description ?? ""));
                        if (desc.Length == 0) continue; // undocumented → owned by DAC-CALC-GROUP, not this rule
                        var items = t.CalculationItems.Select(ci => ci.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                        if (items.Count == 0) continue; // nothing to list
                        ev.Applicable++;
                        // Whole-token, whitespace-normalised match: avoids the raw-substring false NEGATIVE (a short item
                        // name like "PY"/"Var" matching inside "copy"/"variance") and false POSITIVE (spacing/line-wrap
                        // differences reading a listed item as "omitted").
                        var missing = items.Where(n => !MentionsToken(desc, NormWs(n))).ToList();
                        if (missing.Count > 0)
                            ev.Violations.Add(rule.NewFinding(t, t.Name,
                                $"Calculation group '{t.Name}' is documented but its description omits {missing.Count} of {items.Count} item name(s) ({string.Join(", ", missing.Take(5))}); list every calc item so Copilot/Data Agents know they exist."));
                    }
                    return ev;
                }));

            // The DESC-ECHO test (description repeats the name / too short) applied to TABLES and visible COLUMNS —
            // DESC-ECHO owns measures. A description that just echoes the object name (or is too short to inform) is
            // useless grounding for Copilot/Q&A. Mutually exclusive by construction with DESC-PLACEHOLDER (placeholder
            // text) and DESC-LONG-OBJECT (>200 chars): this evaluates only echo/too-short on described, non-placeholder
            // objects. Presence design (Applicable = the offending described objects; dormant on an undocumented or
            // clean model). AiContent — the user's Claude rewrites the description.
            rules.Add(new ModelRule("DESC-ECHO-OBJECT", "Table/column description echoes the name or is too short", ReadinessCategory.Descriptions, Severity.Info, RuleKind.LlmJudgment, FixKind.AiContent,
                (m, rule) =>
                {
                    var described = new List<(ITabularObject obj, string name, string desc)>();
                    foreach (var t in m.Tables.Where(t => !t.IsHidden && !(t is CalculationGroupTable) && !string.IsNullOrWhiteSpace(t.Description)))
                        described.Add((t, t.Name, t.Description));
                    foreach (var c in m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber && !(c.Table is CalculationGroupTable) && !string.IsNullOrWhiteSpace(c.Description)))
                        described.Add((c, c.Name, c.Description));
                    var ev = new RuleEvaluation();   // presence design
                    foreach (var d in described)
                    {
                        var desc = d.desc.Trim();
                        if (PlaceholderDesc.IsMatch(desc)) continue;   // owned by DESC-PLACEHOLDER — no double-count
                        var echo = string.Equals(desc, (d.name ?? "").Trim(), StringComparison.OrdinalIgnoreCase) || desc.Length < 12;
                        if (!echo) continue;
                        ev.Applicable++;
                        ev.Violations.Add(rule.NewFinding(d.obj, d.name, $"'{d.name}' description is non-informative (repeats the name or is too short); write what it means and how to use it so Copilot/Q&A can answer questions about it."));
                    }
                    return ev;
                }));

            // ---- Naming -------------------------------------------------------------------------
            rules.Add(new ObjectRule<Measure>("NAME-MEASURE", "Cryptic measure name", ReadinessCategory.Naming, Severity.Medium, RuleKind.LlmJudgment, FixKind.AiContent,
                m => m.AllMeasures, x => !x.IsHidden, x => IsCrypticName(x.Name),
                x => $"Measure [{x.Name}] is not human-readable (acronym/code/no spaces)."));

            rules.Add(new ObjectRule<Column>("NAME-COLUMN", "Cryptic visible column name", ReadinessCategory.Naming, Severity.Medium, RuleKind.LlmJudgment, FixKind.AiContent,
                m => m.AllColumns,
                x => !x.IsHidden && x.Type != ColumnType.RowNumber
                    && (x.Table is CalculationGroupTable || IsCrypticName(x.Name) || !IdentifierShapedColumnName.IsMatch(x.Name ?? "")),
                x => IsCrypticName(x.Name),
                x => $"Column [{x.Table?.Name}].[{x.Name}] is not human-readable."));

            // A bare "Code" / "Key" / "Number" column forces a user or assistant to guess what it identifies. The
            // candidate population also contains clean compound names ("Product Code"), so this is a normal scored
            // rule rather than a presence-only penalty. NAME-COLUMN excludes these non-cryptic candidates, partitioning
            // the visible-column population instead of counting them twice. The user's assistant authors the rename.
            rules.Add(new ObjectRule<Column>("NAME-COLUMN-ID", "Bare ID/code column name", ReadinessCategory.Naming, Severity.Medium, RuleKind.Deterministic, FixKind.AiContent,
                m => m.AllColumns,
                x => !x.IsHidden && x.Type != ColumnType.RowNumber && !(x.Table is CalculationGroupTable)
                    && !IsCrypticName(x.Name) && IdentifierShapedColumnName.IsMatch(x.Name ?? ""),
                x => BareIdentifierColumnName.IsMatch((x.Name ?? "").Trim()),
                x => $"Column [{x.Table?.Name}].[{x.Name}] is named only '{x.Name?.Trim()}'; rename it to say what the identifier represents."));

            // Tables are named in every NL question Copilot grounds ("sales by region"), so a cryptic table name
            // (dim_/fact_/tbl_ prefix, camelCase, an unknown all-caps code) hurts as much as a cryptic field. Same
            // IsCrypticName pre-filter as NAME-MEASURE/NAME-COLUMN; the user's Claude confirms before the rename (so
            // legit acronyms are spared). Calc-group tables are structural, not business tables — exclude them
            // (consistent with DESC-TABLE) so "CalculationGroup"-style names don't false-positive.
            rules.Add(new ObjectRule<Table>("NAME-TABLE", "Cryptic table name", ReadinessCategory.Naming, Severity.Medium, RuleKind.LlmJudgment, FixKind.AiContent,
                m => m.Tables, x => !x.IsHidden && !(x is CalculationGroupTable), x => IsCrypticName(x.Name),
                x => $"Table [{x.Name}] is not human-readable (acronym/code or a dim_/fact_/tbl_ modelling prefix)."));

            rules.Add(new ModelRule("NAME-DUP", "Duplicate visible field name across tables", ReadinessCategory.Naming, Severity.Medium, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var visible = m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber)
                        .Select(c => (obj: (ITabularObject)c, name: c.Name, table: c.Table?.Name)).ToList();
                    var dupNames = visible.GroupBy(v => v.name, StringComparer.OrdinalIgnoreCase)
                        .Where(g => g.Select(x => x.table).Distinct().Count() > 1).Select(g => g.Key)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var ev = new RuleEvaluation { Applicable = visible.Count };
                    foreach (var v in visible.Where(v => dupNames.Contains(v.name)))
                        ev.Violations.Add(rule.NewFinding(v.obj, v.name, $"Field name '{v.name}' appears on multiple tables; ambiguous for Copilot."));
                    return ev;
                }));

            // Microsoft naming guidance: "object names must not contain emojis, tabs, line breaks, or other
            // non-standard characters, and must not start or end with a space." These read badly to Copilot/Q&A and
            // can break DAX references. Deterministic + NARROW (HasInvalidNameChars only flags clearly-invalid: edge
            // whitespace, control chars, surrogate/emoji), so ordinary punctuation never false-positives. Proposal —
            // a rename (the user/Claude cleans it). Applicable = visible measures + columns + (non-calc-group) tables.
            rules.Add(new ModelRule("NAME-INVALID-CHARS", "Object name has invalid characters (emoji / tab / edge spaces)", ReadinessCategory.Naming, Severity.Medium, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var named = new List<(ITabularObject obj, string name)>();
                    named.AddRange(m.AllMeasures.Where(x => !x.IsHidden).Select(x => ((ITabularObject)x, x.Name)));
                    named.AddRange(m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber).Select(c => ((ITabularObject)c, c.Name)));
                    named.AddRange(m.Tables.Where(t => !t.IsHidden && !(t is CalculationGroupTable)).Select(t => ((ITabularObject)t, t.Name)));
                    var ev = new RuleEvaluation { Applicable = named.Count };
                    foreach (var n in named.Where(n => HasInvalidNameChars(n.name)))
                        ev.Violations.Add(rule.NewFinding(n.obj, n.name, $"Name '{n.name}' has invalid characters (a leading/trailing space, tab, line break, or emoji); clean it so Copilot/Q&A read it correctly and references stay stable."));
                    return ev;
                }));

            // Microsoft naming guidance: visible tables should NOT carry ETL/modelling technical prefixes (Fact, Dim,
            // Dimension, Stg, Staging) — Copilot/Q&A read the table name in every NL question ("sales by region"), and
            // the prefix is modelling jargon, not business language. NAME-TABLE (via IsCrypticName) already catches the
            // lowercase/underscore/camelCase forms (dim_, fact_, FactSales); this owns the capitalised, space/dash-
            // separated forms it misses ("Fact Sales", "Dim Product"). Excludes names already flagged cryptic so there
            // is no Naming double-count. Proposal — a rename (the user/Claude drops the prefix). Applies = visible,
            // non-calc-group, non-cryptic tables (the candidates this rule actually evaluates).
            rules.Add(new ObjectRule<Table>("NAME-TECH-PREFIX", "Visible table has an ETL/modelling prefix (Fact/Dim/Staging)", ReadinessCategory.Naming, Severity.Medium, RuleKind.Deterministic, FixKind.Proposal,
                m => m.Tables, x => !x.IsHidden && !(x is CalculationGroupTable) && !IsCrypticName(x.Name),
                x => IsTechPrefixedTableName(x.Name),
                x => $"Table '{x.Name}' carries an ETL/modelling prefix (Fact/Dim/Staging); drop it so the name reads as business language; Copilot/Q&A use the table name in every question."));

            // Auto-generated placeholder names ("Table1", "Column 2", "Measure3", "col1") are the modelling tool's
            // defaults — meaningless to Copilot/Q&A, which ground on names. Deterministic + anchored, so real names
            // never match; the !IsCrypticName guard (in IsGenericPlaceholderName) keeps it disjoint from NAME-*.
            // Presence design: Applicable = the placeholder-named population (dormant on a clean model, docks when
            // present) — never an always-pass rule inflating Naming toward 100. Proposal (a rename).
            rules.Add(new ModelRule("NAME-GENERIC", "Auto-generated placeholder object name (Table1 / Column2 / Measure 3)", ReadinessCategory.Naming, Severity.Medium, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var named = new List<(ITabularObject obj, string name)>();
                    named.AddRange(m.AllMeasures.Where(x => !x.IsHidden).Select(x => ((ITabularObject)x, x.Name)));
                    named.AddRange(m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber && !(c.Table is CalculationGroupTable)).Select(c => ((ITabularObject)c, c.Name)));
                    named.AddRange(m.Tables.Where(t => !t.IsHidden && !(t is CalculationGroupTable)).Select(t => ((ITabularObject)t, t.Name)));
                    var ev = new RuleEvaluation();   // presence design: Applicable = the violation population
                    foreach (var n in named.Where(n => IsGenericPlaceholderName(n.name)))
                    {
                        ev.Applicable++;
                        ev.Violations.Add(rule.NewFinding(n.obj, n.name, $"Name '{n.name}' is an auto-generated placeholder (Table1 / Column2 / Measure 3), not business language; rename it to something meaningful so Copilot/Q&A can answer questions about the field."));
                    }
                    return ev;
                }));

            // Cryptic HIERARCHY names (tbl_/camelCase/unknown all-caps) — Q&A/Copilot surface a hierarchy as a drill
            // path, so a cryptic hierarchy name is as opaque to grounding as a cryptic column. NAME-MEASURE/COLUMN/TABLE
            // already own those object types; this extends the same IsCrypticName test to hierarchies (an uncovered
            // naming surface). Presence design (Applicable = the cryptic-named population) so it can only DOCK Naming,
            // never inflate it. Proposal (a rename via rename_object) — LlmJudgment-style regex pre-filter. Ref is the
            // safe default "obj:Hierarchy/…" (Refs.For), so this stays a Proposal, not an AI-queued content rule.
            rules.Add(new ModelRule("NAME-HIERARCHY", "Cryptic hierarchy name", ReadinessCategory.Naming, Severity.Medium, RuleKind.LlmJudgment, FixKind.Proposal,
                (m, rule) =>
                {
                    var ev = new RuleEvaluation();   // presence design: Applicable = the cryptic-named hierarchies
                    foreach (var t in m.Tables.Where(t => !t.IsHidden && !(t is CalculationGroupTable)))
                        foreach (var h in t.Hierarchies.Where(h => !h.IsHidden && IsCrypticName(h.Name)))
                        {
                            ev.Applicable++;
                            ev.Violations.Add(rule.NewFinding(h, h.Name, $"Hierarchy '{t.Name}'[{h.Name}] has a cryptic name; Q&A/Copilot offer it as a drill path, so rename it to business language", ReadinessCopy.Op("rename_object"), "."));
                        }
                    return ev;
                }));

            // ---- Synonyms / Linguistic ----------------------------------------------------------
            rules.Add(new ModelRule("SYN-SCHEMA", "Model has no Q&A linguistic schema (synonyms)", ReadinessCategory.Synonyms, Severity.High, RuleKind.Deterministic, FixKind.AiContent,
                (m, rule) =>
                {
                    if ((m.Database?.CompatibilityLevel ?? 0) < 1465) return new RuleEvaluation { Applicable = 0 }; // linguistic schema needs CL>=1465
                    var ev = new RuleEvaluation { Applicable = 1 };
                    var has = m.Cultures.Any(c => !string.IsNullOrWhiteSpace(c.Content));
                    if (!has) ev.Violations.Add(rule.NewFinding(m, m.Name, "This model has no Q&A synonyms set up. Synonyms help users' everyday words reach the right field when those words don't match the field name. Enable Q&A and add synonyms (Copilot and Q&A use the same model)."));
                    return ev;
                }));

            // The synonym writer only accepts JSON LSDL. An older XML linguistic schema is present, not missing, but
            // it cannot be patched safely through the current adapter. Presence design: only XML cultures apply.
            rules.Add(new ModelRule("SYN-LSDL-XML", "Legacy XML linguistic schema cannot be patched safely", ReadinessCategory.Synonyms, Severity.Medium, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var xml = m.Cultures.Where(c => c.ContentType == ContentType.Xml && !string.IsNullOrWhiteSpace(c.Content)).ToList();
                    var ev = new RuleEvaluation { Applicable = xml.Count };
                    foreach (var culture in xml)
                        ev.Violations.Add(rule.NewFinding(culture, culture.Name, $"Culture '{culture.Name}' uses the legacy XML linguistic schema. Semanticus cannot patch its synonyms safely; export or upgrade it to JSON LSDL before editing synonyms."));
                    return ev;
                }));

            rules.Add(new ModelRule("SYN-FIELD", "Visible field has no synonyms", ReadinessCategory.Synonyms, Severity.Medium, RuleKind.LlmGenerate, FixKind.AiContent,
                (m, rule) =>
                {
                    if ((m.Database?.CompatibilityLevel ?? 0) < 1465) return new RuleEvaluation { Applicable = 0 };
                    var culture = m.Cultures.FirstOrDefault(c => c.ContentType == ContentType.Json && !string.IsNullOrEmpty(c.Content));
                    if (culture == null) return new RuleEvaluation { Applicable = 0 };
                    var fields = new List<TabularNamedObject>();
                    fields.AddRange(m.AllMeasures.Where(x => !x.IsHidden));
                    fields.AddRange(m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber));
                    var ev = new RuleEvaluation { Applicable = fields.Count };
                    foreach (var f in fields)
                        if (string.IsNullOrWhiteSpace(SynonymHelper.GetSynonyms(f, culture)))
                            ev.Violations.Add(rule.NewFinding(f, f.Name, $"'{f.Name}' has no synonyms; users may phrase questions differently."));
                    return ev;
                }));

            // Table names are part of every natural-language path ("sales by region"), but SYN-FIELD intentionally
            // owns only columns and measures. Keep tables separate so the Applicable denominator remains the exact
            // population evaluated here. Calculation-group tables are structural rather than business entities.
            rules.Add(new ModelRule("SYN-TABLE", "Visible business table has no synonyms", ReadinessCategory.Synonyms, Severity.Medium, RuleKind.LlmGenerate, FixKind.AiContent,
                (m, rule) =>
                {
                    if ((m.Database?.CompatibilityLevel ?? 0) < 1465) return new RuleEvaluation { Applicable = 0 };
                    var culture = m.Cultures.FirstOrDefault(c => c.ContentType == ContentType.Json && !string.IsNullOrEmpty(c.Content));
                    if (culture == null) return new RuleEvaluation { Applicable = 0 };
                    var tables = m.Tables.Where(t => !t.IsHidden && !(t is CalculationGroupTable)).ToList();
                    var ev = new RuleEvaluation { Applicable = tables.Count };
                    foreach (var table in tables)
                        if (string.IsNullOrWhiteSpace(SynonymHelper.GetSynonyms(table, culture)))
                            ev.Violations.Add(rule.NewFinding(table, table.Name, $"Table '{table.Name}' has no synonyms; users may use a different business name for it."));
                    return ev;
                }));

            // Only the primary authored term participates. Generated/suggested synonyms and lower-weight family
            // modifiers are deliberately excluded, closing the 74-false-positive failure of the naive prototype.
            rules.Add(new ModelRule("SYN-COLLIDE", "Primary authored synonym is ambiguous", ReadinessCategory.Synonyms, Severity.Medium, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var ev = new RuleEvaluation();
                    foreach (var culture in m.Cultures.Where(c => c.ContentType == ContentType.Json && !string.IsNullOrWhiteSpace(c.Content)))
                    {
                        var primary = PrimaryAuthoredSynonyms(m, culture);
                        ev.Applicable += primary.Count;
                        foreach (var group in primary.GroupBy(x => x.Term, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                        {
                            var names = group.Select(x => x.Object.Name).Distinct(StringComparer.Ordinal).ToArray();
                            foreach (var item in group)
                                ev.Violations.Add(rule.NewFinding(item.Object, item.Object.Name,
                                    $"Primary authored synonym '{item.Term}' is assigned to {group.Count()} visible objects ({string.Join(", ", names)}). Give each object a unique primary synonym so Q&A restatements identify the intended field."));
                        }
                    }
                    return ev;
                }));

            // OLS is correct governance, not a readiness defect, but it deliberately narrows what Copilot/Q&A can
            // answer for restricted users. Surface that boundary once at model scope so an analyst doesn't mistake
            // a governed refusal for poor grounding. Applicable=0 keeps intentional security out of the score.
            rules.Add(new ModelRule("SEC-OLS-AWARENESS", "Object-level security narrows the AI answer surface", ReadinessCategory.Visibility, Severity.Info, RuleKind.Deterministic, FixKind.None,
                (m, rule) =>
                {
                    var restrictedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var hiddenTables = 0;
                    var hiddenColumns = 0;
                    foreach (var role in m.Roles)
                    {
                        if (role.MetadataPermission == null) continue;   // CL <1400 cannot express OLS
                        var roleHiddenTables = m.Tables.Where(t => role.MetadataPermission[t] == MetadataPermission.None).ToHashSet();
                        if (roleHiddenTables.Count > 0) restrictedRoles.Add(role.Name);
                        hiddenTables += roleHiddenTables.Count;
                        foreach (var permission in role.TablePermissions.Where(p => !roleHiddenTables.Contains(p.Table)))
                        {
                            var count = permission.Table.Columns.Count(c => permission.ColumnPermissions[c] == MetadataPermission.None);
                            if (count > 0) restrictedRoles.Add(role.Name);
                            hiddenColumns += count;
                        }
                    }

                    var ev = new RuleEvaluation { Applicable = 0 };   // advisory only; governance never lowers the grade
                    if (hiddenTables + hiddenColumns > 0)
                        ev.Violations.Add(rule.NewFinding(m, m.Name,
                            $"Object-level security restricts {hiddenTables} table assignment(s) and {hiddenColumns} column assignment(s) across {restrictedRoles.Count} role(s). Copilot/Q&A respect those boundaries, so restricted users cannot ask about the hidden objects. This is expected governance, not a readiness failure."));
                    return ev;
                }));

            // ---- Visibility (deterministic safe fixes) ------------------------------------------
            rules.Add(new ModelRule("VIS-FK", "Foreign-key column is visible", ReadinessCategory.Visibility, Severity.Medium, RuleKind.Deterministic, FixKind.SafeFix,
                (m, rule) =>
                {
                    var fkCols = m.Relationships.OfType<SingleColumnRelationship>().Select(r => r.FromColumn).Where(c => c != null)
                        .Distinct().ToList();
                    // Applicable = the population this rule actually evaluates (the FK columns), not all columns —
                    // else a few exposed FKs out of hundreds of columns score as ~99% clean and hide the problem.
                    var ev = new RuleEvaluation { Applicable = fkCols.Count };
                    foreach (var c in fkCols.Where(c => !c.IsHidden))
                        ev.Violations.Add(rule.NewFinding(c, c.Name, $"Foreign-key column [{c.Table?.Name}].[{c.Name}] is usually a join key rather than something users ask about; hide it from Copilot/Q&A unless it's a business code users genuinely ask for."));
                    return ev;
                }));

            // ---- Formatting / aggregation (deterministic safe fixes) ----------------------------
            rules.Add(new ModelRule("FMT-SUMMARIZE", "Key/relationship column aggregates by default", ReadinessCategory.Formatting, Severity.Medium, RuleKind.Deterministic, FixKind.SafeFix,
                (m, rule) =>
                {
                    var endpoints = m.Relationships.OfType<SingleColumnRelationship>()
                        .SelectMany(r => new[] { r.FromColumn, r.ToColumn }).Where(c => c != null).ToHashSet();
                    // Applicable = the key/relationship columns this rule evaluates, not every column, so the
                    // score reflects "fraction of key columns set correctly" rather than being diluted to ~100%.
                    var pop = m.AllColumns.Where(c => c.Type != ColumnType.RowNumber && (c.IsKey || endpoints.Contains(c))).ToList();
                    var ev = new RuleEvaluation { Applicable = pop.Count };
                    // AggregateFunction.Default resolves to Sum for NUMERIC columns (TOM enum), so a numeric key
                    // left at Default still auto-aggregates; only text/date keys (whose Default is None) are exempt.
                    foreach (var c in pop.Where(c => c.SummarizeBy != AggregateFunction.None
                                                      && (c.SummarizeBy != AggregateFunction.Default || IsNumeric(c))))
                        ev.Violations.Add(rule.NewFinding(c, c.Name, $"Column [{c.Table?.Name}].[{c.Name}] is a key/relationship column but auto-aggregates ({(c.SummarizeBy == AggregateFunction.Default ? "Sum (default)" : c.SummarizeBy.ToString())}); set its SummarizeBy to None so it isn't summed."));
                    return ev;
                }));

            // (Implicit-measure detection lives in DAC-IMPLICIT-MEASURE (DataAgentConfig) — same population/condition;
            //  not duplicated here. A duplicate "MEAS-IMPLICIT" Formatting rule was reverted after adversarial review
            //  flagged the cross-category double-count.)

            // A VISIBLE numeric column whose NAME is a non-additive identifier (year, month/week number, postal code,
            // or a trailing id/code/key/number token) but that still auto-aggregates. Summing it is meaningless, so set
            // SummarizeBy=None. Microsoft Prep-for-AI guidance #2 ("summarizeBy=None for non-additive numerics"). The
            // population is DISJOINT from DAC-IMPLICIT-MEASURE (which owns the *additive* implicit-measure columns and
            // excludes these), so there's no cross-category double-count. Applicable = the non-additive-named visible
            // numeric non-key/non-endpoint columns (dormant if none). SafeFix = set None (shares the FMT-SUMMARIZE path).
            rules.Add(new ModelRule("SUMMARIZE-DIMENSION", "Non-additive identifier column auto-aggregates", ReadinessCategory.Formatting, Severity.Medium, RuleKind.Deterministic, FixKind.SafeFix,
                (m, rule) =>
                {
                    var endpoints = m.Relationships.OfType<SingleColumnRelationship>()
                        .SelectMany(r => new[] { r.FromColumn, r.ToColumn }).Where(c => c != null).ToHashSet();
                    var pop = m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber
                        && IsNumeric(c) && !c.IsKey && !endpoints.Contains(c) && IsNonAdditiveDimensionName(c.Name)).ToList();
                    var ev = new RuleEvaluation { Applicable = pop.Count };
                    foreach (var c in pop.Where(c => c.SummarizeBy != AggregateFunction.None))
                        ev.Violations.Add(rule.NewFinding(c, c.Name, $"Column [{c.Table?.Name}].[{c.Name}] reads as a non-additive identifier (year / number / code / id) but auto-aggregates ({(c.SummarizeBy == AggregateFunction.Default ? "Sum (default)" : c.SummarizeBy.ToString())}); summing it is meaningless; set SummarizeBy=None so Copilot/Q&A don't offer a nonsensical total."));
                    return ev;
                }));

            rules.Add(new ObjectRule<Measure>("FMT-MEASURE", "Measure has no format string", ReadinessCategory.Formatting, Severity.Medium, RuleKind.LlmGenerate, FixKind.AiContent,
                m => m.AllMeasures, x => !x.IsHidden, x => string.IsNullOrEmpty(x.FormatString) && string.IsNullOrEmpty(x.FormatStringExpression),
                x => $"Measure [{x.Name}] has no format string."));

            rules.Add(new ObjectRule<Column>("FMT-COLUMN", "Visible numeric/date column has no format string", ReadinessCategory.Formatting, Severity.Medium, RuleKind.Deterministic, FixKind.Proposal,
                m => m.AllColumns,
                x => !x.IsHidden && x.Type != ColumnType.RowNumber && !(x.Table is CalculationGroupTable)
                    && !x.IsKey && !x.UsedInRelationships.Any() && !IsNonAdditiveDimensionName(x.Name)
                    && (IsNumeric(x) || x.DataType == DataType.DateTime),
                x => string.IsNullOrWhiteSpace(x.FormatString),
                x => $"Column [{x.Table?.Name}].[{x.Name}] has no model format string. Copilot uses column formats as grounding metadata; set a reviewed number or date format before exposing this field."));

            rules.Add(new ObjectRule<Column>("CAT-GEO", "Geographic column has no data category", ReadinessCategory.Formatting, Severity.Info, RuleKind.Deterministic, FixKind.SafeFix,
                m => m.AllColumns,
                x => !x.IsHidden && x.Type != ColumnType.RowNumber && GeoCategory(x.Name) != null,
                x => string.IsNullOrEmpty(x.DataCategory),
                x => $"Column [{x.Table?.Name}].[{x.Name}] looks geographic; set a Data Category."));

            // A visible column stored as Text whose NAME implies a date or a monetary/quantity number: Q&A/Copilot
            // won't treat a string-typed date/number correctly (sorting, filtering, aggregation all break) and it
            // signals a bad import. TIGHT name set (TypeImplyingName) keeps precision high; excludes keys (a string
            // key is fine). Proposal, NOT SafeFix — a data-type change can fail refresh / alter downstream DAX, so a
            // human/Claude confirms. Presence design: Applicable = the mistyped population (dormant-or-dock).
            rules.Add(new ModelRule("FMT-DATATYPE", "Column name implies a date/number but the data type is Text", ReadinessCategory.Formatting, Severity.Medium, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var pop = m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber && !c.IsKey
                        && c.DataType == DataType.String && TypeImplyingName.IsMatch(c.Name ?? "")).ToList();
                    var ev = new RuleEvaluation { Applicable = pop.Count };
                    foreach (var c in pop)
                        ev.Violations.Add(rule.NewFinding(c, c.Name, $"Column [{c.Table?.Name}].[{c.Name}] is stored as Text but its name implies a date or number; Q&A/Copilot won't sort, filter, or aggregate a string-typed date/number correctly; fix the data type (or rename the column if it really is text)."));
                    return ev;
                }));

            // A visible column whose name marks it as a link / image URL but that has NO Data Category — Copilot uses
            // the Data Category (WebUrl / ImageUrl) as grounding metadata, and Power BI renders links/images from it.
            // Requires an explicit url/uri/link token (UrlColumnName) so binary "Photo" columns don't false-fire.
            // Proposal (Claude/user picks WebUrl vs ImageUrl). Applicable = the visible link/image/barcode population,
            // including clean categorized fields; models without one stay dormant.
            rules.Add(new ObjectRule<Column>("CAT-URL", "URL/image/barcode column has no data category", ReadinessCategory.Formatting, Severity.Info, RuleKind.Deterministic, FixKind.Proposal,
                m => m.AllColumns,
                x => !x.IsHidden && x.Type != ColumnType.RowNumber && LinkCategory(x.Name) != null,
                x => string.IsNullOrEmpty(x.DataCategory),
                x => $"Column [{x.Table?.Name}].[{x.Name}] looks like a {(LinkCategory(x.Name) == "Barcode" ? "barcode" : "link/image URL")} but has no Data Category; set {LinkCategory(x.Name)} so Copilot and Power BI interpret it correctly."));

            // ---- Relationships ------------------------------------------------------------------
            rules.Add(new ObjectRule<SingleColumnRelationship>("REL-BIDI", "Bidirectional cross-filter", ReadinessCategory.Relationships, Severity.High, RuleKind.Deterministic, FixKind.Proposal,
                m => m.Relationships.OfType<SingleColumnRelationship>(), x => true, x => x.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections,
                x => $"Relationship {x.FromTable?.Name}->{x.ToTable?.Name} filters both ways (bidirectional cross-filter); this can make it unclear which rows are in play, so AI may return the wrong totals. Prefer single-direction filtering."));

            // ---- Date table ---------------------------------------------------------------------
            // TI-GATED (per docs/dax-best-practice-rules.md §1 `ti-no-marked-date-table`): a model with NO time-
            // intelligence DAX doesn't need a marked date table for readiness, so gate Applicable on a CORE TI
            // function actually being used (token path) — else Applicable=0 (dormant) and a date-less model isn't
            // dinged. Id/title/category/severity/fix unchanged; only the applicability was narrowed.
            rules.Add(new ModelRule("DATE-MARK", "No marked date table", ReadinessCategory.Relationships, Severity.High, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var usesTi = DaxObjects(m).Any(o =>
                    {
                        var toks = DaxScan.Tokenize(o.expr);
                        return CoreTiFunctions.Any(fn => DaxScan.CallsFunction(toks, fn));
                    });
                    if (!usesTi) return new RuleEvaluation { Applicable = 0 };   // no TI DAX ⇒ dormant
                    var hasDate = m.Tables.Any(t => string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase));
                    var ev = new RuleEvaluation { Applicable = 1 };
                    if (!hasDate) ev.Violations.Add(rule.NewFinding(m, m.Name, "No table is marked as a date table (DataCategory='Time'); this model uses time-intelligence DAX, which relies on a marked date table."));
                    return ev;
                }));

            // ADVISORY (docs/calendars-redesign-plan.md §2.2): a model at CL 1701+ whose measures use CLASSIC
            // time-intelligence DAX but that defines NO calendars could adopt calendar-based time intelligence — the
            // modern replacement. Presence design (dormant-or-dock, NEVER always-pass): Applicable=0 (dormant) unless
            // ALL of {CL≥1701, ≥1 classic-TI measure, no calendars defined} hold; when they do, it fires ONE finding at
            // Applicable=1 (Info weight, so it docks Relationships only marginally). NOT a hard gate — calendars are a
            // new/opt-in feature, so this is a pointer, not a defect. Deterministic token match (never text in a
            // string/comment/[name]) via DaxScan; calendar presence via the raw-TOM CalendarOps seam (unwrapped objects).
            rules.Add(new ModelRule("CAL-TI-NO-CALENDAR", "Classic time-intelligence DAX but no calendars (CL 1701+)", ReadinessCategory.Relationships, Severity.Info, RuleKind.Deterministic, FixKind.None,
                (m, rule) =>
                {
                    var ev = new RuleEvaluation { Applicable = 0 };   // dormant unless the advisory genuinely applies
                    var cl = m.Database?.CompatibilityLevel ?? 0;
                    if (cl < CalendarOps.MinCompatibilityLevel) return ev;   // pre-1701: calendars unavailable ⇒ dormant
                    if (CalendarOps.AnyCalendars(m)) return ev;              // already calendar-based ⇒ dormant (no always-pass)
                    var classic = m.AllMeasures.Count(me =>
                    {
                        if (string.IsNullOrWhiteSpace(me.Expression)) return false;
                        var toks = DaxScan.Tokenize(me.Expression);
                        return ClassicTiFunctions.Any(fn => DaxScan.CallsFunction(toks, fn));
                    });
                    if (classic == 0) return ev;                            // no classic TI DAX ⇒ dormant
                    ev.Applicable = 1;
                    ev.Violations.Add(rule.NewFinding(m, m.Name,
                        $"{classic} measure(s) use classic time-intelligence DAX (TOTALYTD/DATEADD/SAMEPERIODLASTYEAR/…) but this model (CL {cl}) defines no calendars; create calendar-based time intelligence", ReadinessCopy.Op("define_calendar_from_template"), " so you can author calendar-aware forms like TOTALYTD([Sales], 'Fiscal') that also drive fiscal/ISO/4-4-5 shifts."));
                    return ev;
                }));

            // ---- Copilot scale limits (hard gate) -----------------------------------------------
            rules.Add(new ModelRule("LIMIT-SCALE", "Exceeds Copilot scale ceiling", ReadinessCategory.CopilotLimits, Severity.Critical, RuleKind.Deterministic, FixKind.None,
                (m, rule) =>
                {
                    var ev = new RuleEvaluation { Applicable = 1 };
                    var issues = new List<string>();
                    var tableCount = m.Tables.Count;
                    var totalCols = m.AllColumns.Count();
                    var totalMeasures = m.AllMeasures.Count();
                    var relCount = m.Relationships.Count;
                    if (tableCount > 500) issues.Add($"{tableCount} tables (>500)");
                    if (totalCols > 10000) issues.Add($"{totalCols} columns (>10000)");
                    if (totalMeasures > 5000) issues.Add($"{totalMeasures} measures (>5000)");
                    if (relCount > 2000) issues.Add($"{relCount} relationships (>2000)");
                    foreach (var t in m.Tables)
                    {
                        if (t.Columns.Count > 1000) { issues.Add($"table '{t.Name}' has {t.Columns.Count} columns (>1000)"); break; }
                    }
                    var longName = m.AllMeasures.Cast<ITabularNamedObject>().Concat(m.AllColumns).Concat(m.Tables)
                        .FirstOrDefault(o => (o.Name?.Length ?? 0) > 256);
                    if (longName != null) issues.Add($"name >256 chars ('{longName.Name?.Substring(0, 20)}…')");
                    if (issues.Count > 0)
                        ev.Violations.Add(rule.NewFinding(m, m.Name, "Copilot may be disabled/limited: " + string.Join("; ", issues)));
                    return ev;
                }));

            // Direct Lake / Lakehouse semantic models are NOT supported by Power BI Q&A live-connect in Desktop — the
            // Desktop Q&A tooling can't index them (Microsoft). ADVISORY: Applicable=0 (never scores — it's a context
            // warning, not a fixable model defect), FixKind.None; the finding still surfaces. Detects Direct Lake via
            // the effective partition storage mode (same Effective(p) pattern as DAC-QNA-STORAGE).
            rules.Add(new ModelRule("MODE-DIRECTLAKE-QNA", "Direct Lake model: Desktop Q&A cannot index it", ReadinessCategory.CopilotLimits, Severity.Info, RuleKind.Deterministic, FixKind.None,
                (m, rule) =>
                {
                    ModeType Effective(Partition p) => p.Mode == ModeType.Default ? m.DefaultMode : p.Mode;
                    var directLake = m.AllPartitions.Any(p => Effective(p) == ModeType.DirectLake);
                    var ev = new RuleEvaluation { Applicable = 0 };   // advisory only — never scores the category
                    if (directLake)
                        ev.Violations.Add(rule.NewFinding(m, m.Name, "This is a Direct Lake / Lakehouse model; Power BI Desktop Q&A live-connect and Q&A setup linguistic teaching do not support it. Author and validate Prep-for-AI and Q&A in the Fabric service instead."));
                    return ev;
                }));

            // The Q&A index (which Copilot data questions AND the Fabric Data Agent DAX tool rely on) is built from only
            // the FIRST 1,000 model entities (tables + fields); entities beyond that are silently never indexed — a
            // correctness killer distinct from LIMIT-SCALE's raw ceilings. Deterministic entity count (tables + non-
            // RowNumber columns + measures). Presence design: Applicable=1 (High) ONLY when over 1,000 — dormant (never
            // scores) below, so it can only DOCK CopilotLimits when the ceiling is actually breached, never inflate it.
            rules.Add(new ModelRule("LIMIT-QNA-INDEX", "Exceeds the Q&A 1,000-entity index ceiling", ReadinessCategory.CopilotLimits, Severity.High, RuleKind.Deterministic, FixKind.None,
                (m, rule) =>
                {
                    var entities = m.Tables.Count + m.AllColumns.Count(c => c.Type != ColumnType.RowNumber) + m.AllMeasures.Count();
                    var ev = new RuleEvaluation { Applicable = entities > 1000 ? 1 : 0 };
                    if (entities > 1000)
                        ev.Violations.Add(rule.NewFinding(m, m.Name, $"This model has {entities} indexable entities (tables + columns + measures); Q&A/Copilot index only the FIRST 1,000, so entities past that are never indexed and become unanswerable. Trim unused objects", ReadinessCopy.Op("unused_objects"), " or scope the analytical subset via the AI data schema", ReadinessCopy.Op("set_ai_data_schema"), " so the fields you need fall inside the index."));
                    return ev;
                }));

            // A Fabric data agent works best with <=25 tables per semantic-model source (data-agent guidance). The
            // model's own table count is a proxy — the real limit is on the tables SELECTED when adding the model to an
            // agent, which we can't see offline. ADVISORY: Applicable=0 (never scores — a scoping nudge, not a model
            // defect); surfaces when the model has >25 visible tables so the workbench knows to scope the agent source.
            rules.Add(new ModelRule("LIMIT-DATAAGENT-TABLES", "More than 25 tables: scope the data-agent source", ReadinessCategory.CopilotLimits, Severity.Info, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var visTables = m.Tables.Count(t => !t.IsHidden && !(t is CalculationGroupTable));
                    var ev = new RuleEvaluation { Applicable = 0 };   // advisory only — never scores the category
                    if (visTables > 25)
                        ev.Violations.Add(rule.NewFinding(m, m.Name, $"This model exposes {visTables} visible tables; a Fabric data agent works best with <=25 tables per semantic-model source. When you add this model to a data agent, select only the tables the agent needs and scope the AI data schema", ReadinessCopy.Op("set_ai_data_schema"), " to that subset."));
                    return ev;
                }));

            // ---- DataAgentConfig (Prep-for-AI / Copilot DAX grounding) --------------------------
            // Calc items are absent from the metadata Copilot/Data Agents see; their semantics survive only
            // in the group's description. Calc groups also degrade generated DAX, so an undocumented one is a
            // real AI-readiness gap. Applicable = calc-group count, so this stays dormant on models without any.
            rules.Add(new ModelRule("DAC-CALC-GROUP", "Calculation group is undocumented for AI", ReadinessCategory.DataAgentConfig, Severity.Medium, RuleKind.Deterministic, FixKind.AiContent,
                (m, rule) =>
                {
                    var cgs = m.Tables.OfType<CalculationGroupTable>().ToList();
                    var ev = new RuleEvaluation { Applicable = cgs.Count };
                    foreach (var t in cgs)
                    {
                        var col = t.Columns.FirstOrDefault(c => c.Type != ColumnType.RowNumber);
                        var documented = !string.IsNullOrWhiteSpace(t.Description) || (col != null && !string.IsNullOrWhiteSpace(col.Description));
                        if (!documented)
                            ev.Violations.Add(rule.NewFinding(t, t.Name, $"Calculation group '{t.Name}' has no description; its calculation items are invisible to Copilot/Data Agents unless documented on the group, and calc groups can degrade generated DAX."));
                    }
                    return ev;
                }));

            // Q&A is the prerequisite for Copilot data questions AND every Prep-for-AI feature; it is NOT on by
            // default for DirectQuery/Direct Lake. ADVISORY: Applicable=0 keeps it out of the score; it only nudges,
            // and only when we COULDN'T read the actual Q&A state (DAC-QNA-DISABLED handles the readable case), so a
            // PBIP model is never double-flagged.
            rules.Add(new ModelRule("DAC-QNA-STORAGE", "DirectQuery/Direct Lake model: verify Q&A is enabled", ReadinessCategory.DataAgentConfig, Severity.Info, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    ModeType Effective(Partition p) => p.Mode == ModeType.Default ? m.DefaultMode : p.Mode;
                    var dqOrDl = m.AllPartitions.Any(p => { var md = Effective(p); return md == ModeType.DirectQuery || md == ModeType.DirectLake; });
                    var ev = new RuleEvaluation { Applicable = 0 }; // advisory only — never scores the category
                    if (dqOrDl && !PrepForAiReader.Read(m).QnaEnabled.HasValue)
                        ev.Violations.Add(rule.NewFinding(m, m.Name, $"Model uses DirectQuery/Direct Lake storage; Power BI Q&A (prerequisite for Copilot data questions and Prep-for-AI) is not on by default for these; verify it is enabled", ReadinessCopy.Op("enable_qna"), "."));
                    return ev;
                }));

            // Prep-for-AI authoring (AI data schema, verified answers, AI instructions) in Power BI DESKTOP is limited
            // to Import/DirectQuery/Composite (local) models; Direct Lake models must be prepped in the Fabric SERVICE
            // (all model types can use Prep-for-AI in the service). ADVISORY: Applicable=0 (routing note, never scores);
            // the finding surfaces so the workbench knows where the authoring must happen for this model.
            rules.Add(new ModelRule("CFG-PREP-MODE", "Direct Lake: Prep-for-AI must be authored in the service", ReadinessCategory.DataAgentConfig, Severity.Info, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    ModeType Effective(Partition p) => p.Mode == ModeType.Default ? m.DefaultMode : p.Mode;
                    var directLake = m.AllPartitions.Any(p => Effective(p) == ModeType.DirectLake);
                    var ev = new RuleEvaluation { Applicable = 0 };   // advisory only — never scores the category
                    if (directLake)
                        ev.Violations.Add(rule.NewFinding(m, m.Name, "This is a Direct Lake model; Prep data for AI (AI data schema, verified answers, AI instructions) can be authored in Power BI Desktop only for Import/DirectQuery/Composite models; do the Prep-for-AI authoring in the Fabric service for this model."));
                    return ev;
                }));

            // ---- DataAgentConfig: Prep-for-AI surface (read from the on-disk PBIP, capability-gated) ----------
            // Q&A enablement and verified answers live in FILES beside the TMDL (definition.pbism / VerifiedAnswers/),
            // not in TOM. PrepForAiReader surfaces them for an on-disk PBIP; otherwise both rules stay dormant.
            rules.Add(new ModelRule("DAC-QNA-DISABLED", "Q&A is disabled (Copilot/Prep-for-AI prerequisite)", ReadinessCategory.DataAgentConfig, Severity.High, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var cfg = PrepForAiReader.Read(m);
                    var ev = new RuleEvaluation { Applicable = cfg.QnaEnabled.HasValue ? 1 : 0 };
                    if (cfg.QnaEnabled == false)
                        ev.Violations.Add(rule.NewFinding(m, m.Name, $"Power BI Q&A is disabled on this model (definition.pbism qnaEnabled=false); it is the prerequisite for Copilot data questions and every Prep-for-AI feature. Enable it", ReadinessCopy.Op("enable_qna"), "."));
                    return ev;
                }));

            rules.Add(new ModelRule("DAC-VERIFIED-ANSWERS", "No verified answers for the data agent", ReadinessCategory.DataAgentConfig, Severity.Medium, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var cfg = PrepForAiReader.Read(m);
                    // Only meaningful for an AI-intended model (Q&A on) we can actually inspect on disk.
                    var ev = new RuleEvaluation { Applicable = (cfg.SourceReadable && cfg.QnaEnabled == true) ? 1 : 0 };
                    if (ev.Applicable == 1 && cfg.VerifiedAnswerCount == 0)
                        ev.Violations.Add(rule.NewFinding(m, m.Name, "No verified answers defined. For semantic-model data agents, verified answers are the model-level way to answer common/complex questions accurately (example queries aren't supported for semantic models); add a few for your top questions."));
                    return ev;
                }));

            // Q&A/Copilot fold object names (lower-case + camel-split + lemmatise) into ONE term dictionary: two
            // VISIBLE objects whose names normalise to the same term are indistinguishable to term matching, and
            // the AS validator refuses to commit a linguistic schema holding entities for both — which blocks EVERY
            // synonym write, model-wide (docs/bug-set_synonyms-duplicate-name-collision.md). QnaTerms is the same
            // two-tier normaliser the set_synonyms guard uses, so scan and write agree: an EXACT normalised match
            // is a certain collision (the write refuses it); a plural-fold match is our approximation of the closed
            // AS lemmatiser (the write proceeds with a warning), so its finding says "likely", same rule/severity.
            // Population mirrors the writer's guard: calc-group tables are excluded (the neighbouring DAC rules'
            // convention — their names are calc-item machinery, not Q&A vocabulary), RowNumber columns are excluded
            // (system columns nobody synonymises, though the writer can technically target one), and hierarchy
            // LEVELS are excluded (level names duplicate their source column by construction — grouping them would
            // flag every hierarchy; the writer's guard skips them for the same reason). Only meaningful when Q&A is
            // engaged: dormant without a JSON linguistic culture and without a positive qnaEnabled signal.
            // Applicable = the collision groups, every one a violation (the dormant-or-dock convention).
            rules.Add(new ModelRule("DAC-QNA-NAME-COLLISIONS", "Object names collide under Q&A term matching", ReadinessCategory.DataAgentConfig, Severity.High, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var cfg = PrepForAiReader.Read(m);
                    var ev = new RuleEvaluation();
                    if (!cfg.HasLinguisticSchema && cfg.QnaEnabled != true) return ev;   // Q&A not engaged → dormant

                    var pop = new List<(string Label, ITabularNamedObject Obj)>();
                    foreach (var t in m.Tables.Where(t => !t.IsHidden && !(t is CalculationGroupTable)))
                    {
                        pop.Add(($"table '{t.Name}'", t));
                        foreach (var c in t.Columns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber)) pop.Add(($"column '{t.Name}'[{c.Name}]", c));
                        foreach (var me in t.Measures.Where(x => !x.IsHidden)) pop.Add(($"measure '{t.Name}'[{me.Name}]", me));
                        foreach (var h in t.Hierarchies.Where(x => !x.IsHidden)) pop.Add(($"hierarchy '{t.Name}'[{h.Name}]", h));
                    }

                    List<List<(string Label, ITabularNamedObject Obj)>> GroupsBy(Func<string, string> normalize) => pop
                        .GroupBy(x => normalize(x.Obj.Name), StringComparer.Ordinal)
                        .Where(g => g.Key.Length > 0 && g.Count() > 1)
                        .Select(g => g.ToList()).ToList();

                    var exactMembers = new HashSet<ITabularNamedObject>();
                    foreach (var g in GroupsBy(QnaTerms.Normalize))
                    {
                        ev.Applicable++;
                        foreach (var x in g) exactMembers.Add(x.Obj);
                        ev.Violations.Add(rule.NewFinding(g[0].Obj, QnaTerms.Normalize(g[0].Obj.Name),
                            $"These visible objects all normalise to the Q&A term '{QnaTerms.Normalize(g[0].Obj.Name)}': {string.Join(", ", g.Select(v => v.Label))}. Q&A and Copilot treat them as the same term, and the linguistic schema cannot hold entities for more than one (every synonym write fails). Rename or hide the duplicates."));
                    }
                    // Fold-tier groups that the fold CREATED (members span 2+ exact terms) — likely, not certain.
                    foreach (var g in GroupsBy(QnaTerms.NormalizeFolded).Where(g => g.Select(x => QnaTerms.Normalize(x.Obj.Name)).Distinct(StringComparer.Ordinal).Count() > 1))
                    {
                        ev.Applicable++;
                        // Anchor on a member the exact tier didn't already flag when one exists (all-flagged is
                        // possible: the fold can merge two whole exact groups into one fold group).
                        var anchor = g.FirstOrDefault(x => !exactMembers.Contains(x.Obj)).Obj ?? g[0].Obj;
                        ev.Violations.Add(rule.NewFinding(anchor, QnaTerms.NormalizeFolded(g[0].Obj.Name),
                            $"These visible objects likely collide on the Q&A term '{QnaTerms.NormalizeFolded(g[0].Obj.Name)}' after plural folding (an approximation of the validator's lemmatiser): {string.Join(", ", g.Select(v => v.Label))}. Q&A and Copilot may treat them as the same term. Rename or hide the duplicates."));
                    }
                    return ev;
                }));

            // AI instructions live in the LSDL as the top-level "CustomInstructions" string (read from TOM, so this
            // works for any model with a linguistic schema). Recommend adding them; enforce the documented 10k cap.
            rules.Add(new ModelRule("DAC-AI-INSTRUCTIONS", "No AI instructions for Copilot/data agents", ReadinessCategory.DataAgentConfig, Severity.Medium, RuleKind.Deterministic, FixKind.AiContent,
                (m, rule) =>
                {
                    var cfg = PrepForAiReader.Read(m);
                    // Gate off when the new Copilot Tooling Format is present: the legacy LSDL CustomInstructions is
                    // then an unreliable signal (instructions may live in copilot/*.json), so don't false-fire
                    // "missing" on a migrated model — DAC-COPILOT-TOOLING-FORMAT surfaces that case instead.
                    var ev = new RuleEvaluation { Applicable = (cfg.HasLinguisticSchema && !cfg.CopilotToolingFormatPresent) ? 1 : 0 };
                    if (ev.Applicable == 1 && string.IsNullOrWhiteSpace(cfg.AiInstructions))
                        ev.Violations.Add(rule.NewFinding(m, m.Name, "No AI instructions set (Prep-for-AI). AI instructions give Copilot/data agents business context, terminology and analysis rules (stored on the model as LSDL CustomInstructions, ≤10,000 chars); add a focused set."));
                    return ev;
                }));

            rules.Add(new ModelRule("DAC-AI-INSTRUCTIONS-LEN", "AI instructions exceed the 10,000-char limit", ReadinessCategory.DataAgentConfig, Severity.High, RuleKind.Deterministic, FixKind.AiContent,
                (m, rule) =>
                {
                    var cfg = PrepForAiReader.Read(m);
                    var has = !string.IsNullOrWhiteSpace(cfg.AiInstructions);
                    var ev = new RuleEvaluation { Applicable = has ? 1 : 0 };
                    if (has && cfg.AiInstructionsLength > 10000)
                        ev.Violations.Add(rule.NewFinding(m, m.Name, $"AI instructions are {cfg.AiInstructionsLength} chars; the limit is 10,000, and content beyond that is ignored. Condense them."));
                    return ev;
                }));

            // Terse-but-documented is a VALID design: short consistent field names ("Income MA", "NPAT ACT YTD") plus
            // an AI-instructions glossary that defines the codes. The naming rules can't see that glossary, so this
            // rule measures its COMPLETENESS — the codes used in field names that the glossary does NOT define and that
            // aren't standard acronyms the AI already knows. Those are genuinely ungrounded (the agent can't expand
            // them). Only applies when AI instructions exist (no instructions ⇒ DAC-AI-INSTRUCTIONS' job, not this).
            rules.Add(new ModelRule("DAC-GLOSSARY-GAP", "Field-name codes missing from the AI instructions", ReadinessCategory.DataAgentConfig, Severity.Medium, RuleKind.LlmJudgment, FixKind.AiContent,
                (m, rule) =>
                {
                    var cfg = PrepForAiReader.Read(m);
                    if (string.IsNullOrWhiteSpace(cfg.AiInstructions)) return new RuleEvaluation { Applicable = 0 };
                    var instr = cfg.AiInstructions;

                    // Codes the glossary "defines" = the all-caps runs it contains, read both verbatim AND with
                    // punctuation stripped (so a field code "PL" is credited to a glossary entry written "P&L"), MINUS
                    // common ALL-CAPS prose words (so an "AND"/"IMPORTANT" in the prose can't silently credit a
                    // same-spelled code). Heuristic: it credits a code MENTIONED in caps, not strictly a defining clause.
                    var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Match mm in AllCapsRun.Matches(instr)) defined.Add(mm.Value);
                    foreach (Match mm in AllCapsRun.Matches(Regex.Replace(instr, @"[^A-Za-z0-9\s]", ""))) defined.Add(mm.Value);
                    defined.ExceptWith(GlossaryStopwords);

                    // Distinct all-caps codes used in visible field/table names, minus standard/known acronyms (the AI
                    // already knows EBITDA/YTD/IT/…), Roman-numeral labels (Phase II), and anything the glossary defines.
                    var undefined = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    var names = m.AllMeasures.Where(x => !x.IsHidden).Select(x => x.Name)
                        .Concat(m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber).Select(c => c.Name))
                        .Concat(m.Tables.Where(t => !t.IsHidden && !(t is CalculationGroupTable)).Select(t => t.Name));
                    foreach (var n in names)
                        foreach (Match mm in AllCapsRun.Matches(n ?? ""))
                            if (!BusinessAcronyms.Contains(mm.Value) && !RomanNumeral.IsMatch(mm.Value) && !defined.Contains(mm.Value))
                                undefined.Add(mm.Value);

                    var ev = new RuleEvaluation { Applicable = 1 };
                    if (undefined.Count > 0)
                        ev.Violations.Add(rule.NewFinding(m, m.Name,
                            $"Field names use {undefined.Count} code(s) the AI instructions don't define ({string.Join(", ", undefined.Take(12))}{(undefined.Count > 12 ? ", …" : "")}); add a short glossary line for each so Copilot/data agents know what they mean."));
                    return ev;
                }));

            // Honest scope: this detects an EMPTY AI data schema — a linguistic schema exists but excludes NOTHING.
            // It does NOT measure whether the schema is "focused" (that needs per-field judgement we don't have): Power BI
            // auto-excludes model-hidden fields (keys/RowNumber/technical) by writing Visibility:Hidden, so any model that
            // has actually been through Prep-for-AI almost always excludes >0 and passes. So the real signal here is "you
            // enabled Q&A / have a linguistic schema but never curated the AI data schema at all" — e.g. a freshly
            // enable_qna'd model (seeded Entities:{}) or one untouched by Prep-for-AI. Gate on relationships (a flat
            // single-table model may legitimately exclude nothing). Info: a soft nudge, not a hard finding.
            rules.Add(new ModelRule("DAC-AI-DATA-SCHEMA", "AI data schema is empty (no field excluded)", ReadinessCategory.DataAgentConfig, Severity.Info, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var cfg = PrepForAiReader.Read(m);
                    // Gate off when the new Copilot Tooling Format is present (the LSDL exclusion count is then an
                    // unreliable proxy for the curated schema — see DAC-COPILOT-TOOLING-FORMAT).
                    var ev = new RuleEvaluation { Applicable = (cfg.HasLinguisticSchema && !cfg.CopilotToolingFormatPresent && m.Relationships.Count > 0) ? 1 : 0 };
                    if (ev.Applicable == 1 && cfg.AiSchemaExcludedFields == 0)
                        ev.Violations.Add(rule.NewFinding(m, m.Name, "The AI data schema excludes nothing: every field is exposed to Copilot/data agents, the default before any curation. (A configured schema almost always excludes something, because model-hidden fields are auto-excluded.) Exclude keys, sort/helper columns and intermediate measures, or hide non-analytical fields in the model."));
                    return ev;
                }));

            // Fail-loud (NOT silent-pass): the new "Copilot Tooling Format" (GA ~May 2026) stores Copilot grounding
            // in a `copilot/` folder of text files beside the model, distinct from the legacy linguistic-schema (LSDL)
            // anchors the other DAC rules read. We DETECT it but don't yet PARSE it (the per-file schema isn't
            // authoritatively published). So when it's present, the LSDL-based DAC rules above stand down and THIS
            // rule surfaces an explicit advisory — the model is never silently scored as un-prepped on a stale signal.
            // Applicable=0 keeps it OUT of the score (it's informational, not pass/fail) while its finding still shows.
            rules.Add(new ModelRule("DAC-COPILOT-TOOLING-FORMAT", "Model uses the new Copilot Tooling Format (assessment pending)", ReadinessCategory.DataAgentConfig, Severity.Info, RuleKind.Deterministic, FixKind.None,
                (m, rule) =>
                {
                    var cfg = PrepForAiReader.Read(m);
                    var ev = new RuleEvaluation { Applicable = 0 };   // advisory only — never affects the score
                    if (cfg.CopilotToolingFormatPresent)
                        ev.Violations.Add(rule.NewFinding(m, m.Name, $"This model uses the new Copilot Tooling Format ({cfg.CopilotToolingFileCount} file(s) in a 'copilot/' folder, GA ~May 2026). Semanticus reads AI instructions and data schema from the older linguistic schema, so those checks stand down here; assessment of the new-format files is pending. Check the copilot/ files directly."));
                    return ev;
                }));

            // Perspectives are a common way to scope a model, but they are NOT the mechanism Copilot/Data Agents scope
            // on — the AI data schema (Prep-for-AI) is. A model that relies on perspectives for scope may wrongly assume
            // Copilot honours them. ADVISORY: Applicable=0 (a routing note, never scores); surfaces when perspectives
            // exist so the workbench points scoping at the AI data schema rather than a perspective.
            rules.Add(new ModelRule("DAC-PERSPECTIVE-NOT-SCOPE", "Perspectives are not the Copilot AI-scoping mechanism", ReadinessCategory.DataAgentConfig, Severity.Info, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var ev = new RuleEvaluation { Applicable = 0 };   // advisory only — never scores the category
                    if (m.Perspectives.Count > 0)
                        ev.Violations.Add(rule.NewFinding(m, m.Name, $"This model defines {m.Perspectives.Count} perspective(s), but Copilot/Data Agents do NOT scope on perspectives; they use the AI data schema (Prep-for-AI). Curate the AI data schema", ReadinessCopy.Op("set_ai_data_schema"), " to the analytical subset instead of relying on a perspective for AI scope."));
                    return ev;
                }));

            // Field parameters are disconnected tables driven by SELECTEDVALUE — a complexity pattern Microsoft flags as
            // more likely to yield wrong Copilot/Data-Agent results. ADVISORY: Applicable=0 (context warning, never
            // scores); detection reuses the ParameterMetadata extended-property marker (same signal REL-DISCONNECTED
            // uses to EXEMPT them from the disconnected-table rule).
            rules.Add(new ModelRule("DAC-FIELD-PARAM-COMPLEXITY", "Field-parameter tables add Copilot complexity", ReadinessCategory.DataAgentConfig, Severity.Info, RuleKind.Deterministic, FixKind.None,
                (m, rule) =>
                {
                    var fieldParams = m.Tables.Where(t => !(t is CalculationGroupTable)
                        && t.Columns.Any(c => c.HasExtendedProperty("ParameterMetadata"))).ToList();
                    var ev = new RuleEvaluation { Applicable = 0 };   // advisory only — never scores the category
                    if (fieldParams.Count > 0)
                        ev.Violations.Add(rule.NewFinding(m, m.Name, $"This model has {fieldParams.Count} field-parameter table(s) ({string.Join(", ", fieldParams.Take(3).Select(t => t.Name))}); field parameters are disconnected tables that add complexity Copilot/Data Agents can mis-handle. If AI answers are inconsistent, scope them out of the AI data schema", ReadinessCopy.Op("set_ai_data_schema"), "."));
                    return ev;
                }));

            // ---- Formatting: logical sort -------------------------------------------------------
            // Known month/weekday labels always need chronological sorting. Other text labels apply only when the
            // table exposes one explicit numeric companion (Priority + Priority Sort, Month Name + Month Number).
            // This is a ModelRule because companion-column changes are cross-object inputs; using ObjectRule here
            // would let an incremental rescan carry a stale label verdict when only the companion changed.
            rules.Add(new ModelRule("FMT-SORTBY", "Ordered text column has no Sort By Column", ReadinessCategory.Formatting, Severity.Info, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var pop = m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber && c.DataType == DataType.String)
                        .Select(c => new { Column = c, Companion = SortCompanion(c) })
                        .Where(x => MonthOrWeekday.IsMatch(x.Column.Name ?? string.Empty) || x.Companion != null)
                        .ToList();
                    var ev = new RuleEvaluation { Applicable = pop.Count };
                    foreach (var x in pop.Where(x => x.Column.SortByColumn == null))
                    {
                        var reason = x.Companion == null
                            ? "it is a month/weekday label and will sort alphabetically"
                            : $"the table has numeric order column [{x.Companion.Name}]";
                        ev.Violations.Add(rule.NewFinding(x.Column, x.Column.Name,
                            $"Column [{x.Column.Table?.Name}].[{x.Column.Name}] has no Sort By Column; {reason}. Set the intended logical order so Q&A/Copilot does not present an alphabetical sequence."));
                    }
                    return ev;
                }));

            // ---- DataAgentConfig: implicit measures ---------------------------------------------
            // A visible numeric column that auto-aggregates (and isn't a key/relationship endpoint) is an
            // implicit measure; Microsoft advises an explicit measure + SummarizeBy=None so data agents /
            // Copilot generate better DAX. Applicable = that column population (dormant if none). PARTITIONED
            // against SUMMARIZE-DIMENSION: non-additive-named columns (year/number/code/id) are NOT implicit
            // measures (you never want an explicit "Total Year") — they're owned by SUMMARIZE-DIMENSION, so we
            // exclude them here to avoid a cross-category double-count and a nonsensical "add a measure" nudge.
            rules.Add(new ModelRule("DAC-IMPLICIT-MEASURE", "Visible numeric column relies on implicit aggregation", ReadinessCategory.DataAgentConfig, Severity.Medium, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var endpoints = m.Relationships.OfType<SingleColumnRelationship>()
                        .SelectMany(r => new[] { r.FromColumn, r.ToColumn }).Where(c => c != null).ToHashSet();
                    var pop = m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber
                        && IsNumeric(c) && !c.IsKey && !endpoints.Contains(c) && !IsNonAdditiveDimensionName(c.Name)).ToList();
                    var ev = new RuleEvaluation { Applicable = pop.Count };
                    // pop is numeric-only, and Default resolves to Sum for numeric columns — so anything other than
                    // None auto-aggregates and IS an implicit measure (Default is the TOM default = the common case).
                    foreach (var c in pop.Where(c => c.SummarizeBy != AggregateFunction.None))
                        ev.Violations.Add(rule.NewFinding(c, c.Name, $"Visible numeric column [{c.Table?.Name}].[{c.Name}] auto-aggregates ({(c.SummarizeBy == AggregateFunction.Default ? "Sum (default)" : c.SummarizeBy.ToString())}), so it acts as an automatic (implicit) measure; data agents/Copilot do better with a real measure you define (set SummarizeBy=None and add one)."));
                    return ev;
                }));

            // Two visible measures on the SAME table with the SAME (normalised) DAX are duplicates — Microsoft:
            // "consolidate or clearly differentiate duplicate or overlapping measures", because they dilute the signal
            // Copilot uses to pick the right field. Grouped by (home table, expression): UNqualified column refs
            // (SUM([Amount])) resolve relative to the measure's home table, so the SAME text on two DIFFERENT tables is
            // NOT a duplicate (e.g. AdventureWorks' Internet/Reseller measure families) — same-table only avoids that
            // false positive. Deterministic, Proposal. Applicable = visible measures with an expression (dormant if none).
            rules.Add(new ModelRule("MEAS-DUP-EXPR", "Duplicate measures (identical DAX on one table)", ReadinessCategory.DataAgentConfig, Severity.Info, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var visible = m.AllMeasures.Where(x => !x.IsHidden && !string.IsNullOrWhiteSpace(x.Expression))
                        .Select(x => (m: x, key: (x.Table?.Name ?? "") + "\0" + NormExpr(x.Expression))).ToList();
                    var ev = new RuleEvaluation { Applicable = visible.Count };
                    foreach (var grp in visible.GroupBy(v => v.key).Where(g => g.Count() > 1))
                    {
                        var names = grp.Select(v => v.m.Name).ToList();
                        foreach (var v in grp)
                            ev.Violations.Add(rule.NewFinding(v.m, v.m.Name, $"Measure [{v.m.Name}] has the same DAX as {grp.Count() - 1} other visible measure(s) on the same table ({string.Join(", ", names.Where(n => n != v.m.Name).Take(4))}); consolidate to one or clearly differentiate them so Copilot/Q&A pick the right measure."));
                    }
                    return ev;
                }));

            // ---- Visibility: helper/intermediate measures ---------------------------------------
            // High-leverage for data agents: keep helper/intermediate measures out of the AI surface.
            // Detect by naming convention (true DAX-dependency detection needs a live connection — Wave 2).
            rules.Add(new ObjectRule<Measure>("VIS-HELPER", "Helper/intermediate measure is visible", ReadinessCategory.Visibility, Severity.Medium, RuleKind.Deterministic, FixKind.Proposal,
                m => m.AllMeasures, x => !x.IsHidden, x => HelperMeasure.IsMatch(x.Name ?? string.Empty),
                x => $"Measure [{x.Name}] looks like a helper/intermediate; hide it or exclude it from the AI data schema so it doesn't confuse Copilot/Data Agents."));

            // A visible technical/audit/ETL column (surrogate key, GUID, hash, load/batch metadata, "...By" audit) is
            // noise the AI may offer as a real field. VIS-FK owns the MANY-side FK endpoints (FromColumn) ONLY, so
            // exclude just those to avoid a Visibility double-count. The ONE-side PK (ToColumn) is unowned by VIS-FK —
            // keep it in scope, because a visible tech-named surrogate PK (e.g. Customer_SK) is exactly the target here.
            // Proposal (heuristic name match → human confirms before hiding).
            rules.Add(new ModelRule("VIS-TECH-COLUMN", "Technical/audit column is visible", ReadinessCategory.Visibility, Severity.Medium, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var fkEndpoints = m.Relationships.OfType<SingleColumnRelationship>()
                        .Select(r => r.FromColumn).Where(c => c != null).ToHashSet();
                    var pop = m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber && !fkEndpoints.Contains(c)).ToList();
                    var ev = new RuleEvaluation { Applicable = pop.Count };
                    foreach (var c in pop.Where(c => TechColumn.IsMatch(c.Name ?? "")))
                        ev.Violations.Add(rule.NewFinding(c, c.Name, $"Column [{c.Table?.Name}].[{c.Name}] looks like a technical/audit/ETL column (surrogate key, GUID, hash, load/batch/audit metadata); hide it from Copilot/Q&A so it isn't offered as a field."));
                    return ev;
                }));

            // A VISIBLE column flagged IsKey (a primary/business key) — keys are identifiers Copilot/Q&A shouldn't
            // offer as answerable fields (Microsoft: hide key columns). VIS-FK owns the many-side FK (relationship
            // FromColumn) and VIS-TECH-COLUMN owns technically-NAMED columns; this rule owns the remaining IsKey
            // columns (a cleanly-named PK still marked IsKey), DISJOINT from both to avoid a Visibility double-count.
            // Applicable = that key population (proportional "fraction of keys correctly hidden", mirroring VIS-FK).
            // Proposal — a natural business code is occasionally kept visible on purpose.
            rules.Add(new ModelRule("VIS-KEY", "Key column is visible", ReadinessCategory.Visibility, Severity.Medium, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var fkFrom = m.Relationships.OfType<SingleColumnRelationship>().Select(r => r.FromColumn).Where(c => c != null).ToHashSet();
                    // Exclude DateTime keys: a date table's date column is legitimately visible (it's the primary date
                    // field users query) even though it's marked IsKey — hiding it would be wrong.
                    var pop = m.AllColumns.Where(c => c.Type != ColumnType.RowNumber && c.IsKey && c.DataType != DataType.DateTime
                        && !fkFrom.Contains(c) && !TechColumn.IsMatch(c.Name ?? "")).ToList();
                    var ev = new RuleEvaluation { Applicable = pop.Count };
                    foreach (var c in pop.Where(c => !c.IsHidden))
                        ev.Violations.Add(rule.NewFinding(c, c.Name, $"Column [{c.Table?.Name}].[{c.Name}] is a key (IsKey) but visible; keys are identifiers, not answerable fields, so hide it from Copilot/Q&A (unless it's a business code users genuinely ask for)."));
                    return ev;
                }));

            // A VISIBLE table whose every data column is hidden AND that has no visible measure contributes only its
            // NAME to Copilot/Q&A grounding while exposing nothing answerable — noise (a bridge/utility table left
            // visible). Hide the table (or mark it private). Excludes calc-group tables and measure-only holders (a
            // table with visible measures is answerable; a table with no data columns is a legitimate holder).
            // Presence design; Proposal.
            rules.Add(new ModelRule("VIS-TABLE-ALL-HIDDEN", "Visible table exposes no visible field", ReadinessCategory.Visibility, Severity.Info, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var ev = new RuleEvaluation();   // presence design
                    foreach (var t in m.Tables.Where(t => !t.IsHidden && !(t is CalculationGroupTable)))
                    {
                        var dataCols = t.Columns.Where(c => c.Type != ColumnType.RowNumber).ToList();
                        if (dataCols.Count == 0) continue;                    // measure-only holder — legitimate
                        if (dataCols.Any(c => !c.IsHidden)) continue;         // has a visible field — fine
                        if (t.Measures.Any(me => !me.IsHidden)) continue;     // visible measures make it answerable
                        ev.Applicable++;
                        ev.Violations.Add(rule.NewFinding(t, t.Name, $"Table '{t.Name}' is visible but every column is hidden and it has no visible measure; it only adds noise for Copilot/Q&A. Hide the table (or mark it private)."));
                    }
                    return ev;
                }));

            // ---- Relationships: many-to-many ----------------------------------------------------
            rules.Add(new ObjectRule<SingleColumnRelationship>("REL-M2M", "Many-to-many relationship", ReadinessCategory.Relationships, Severity.High, RuleKind.Deterministic, FixKind.Proposal,
                m => m.Relationships.OfType<SingleColumnRelationship>(), x => true,
                x => x.FromCardinality == RelationshipEndCardinality.Many && x.ToCardinality == RelationshipEndCardinality.Many,
                x => $"Relationship {x.FromTable?.Name}->{x.ToTable?.Name} is many-to-many; because rows on each side can match many others, filter results are ambiguous and this degrades AI-generated DAX."));

            // A table that is the dimension (one) side of one many-to-one edge and the fact (many) side of another
            // forms an intermediate snowflake/bridge chain. This is a deterministic SHAPE signal, not a verdict that
            // the design is wrong: remediation is always a reviewed remodel/description proposal, never a safe fix.
            rules.Add(new ModelRule("REL-SNOWFLAKE", "Table forms a snowflake/bridge relationship chain", ReadinessCategory.Relationships, Severity.Info, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var manyToOne = m.Relationships.OfType<SingleColumnRelationship>()
                        .Where(r => r.FromCardinality == RelationshipEndCardinality.Many
                            && r.ToCardinality == RelationshipEndCardinality.One
                            && r.FromTable != null && r.ToTable != null).ToList();
                    var asDimension = manyToOne.GroupBy(r => r.ToTable).ToDictionary(g => g.Key, g => g.ToList());
                    var asFact = manyToOne.GroupBy(r => r.FromTable).ToDictionary(g => g.Key, g => g.ToList());
                    var pop = asDimension.Keys.Intersect(asFact.Keys).Where(t => !(t is CalculationGroupTable)).ToList();
                    var ev = new RuleEvaluation { Applicable = pop.Count };
                    foreach (var t in pop)
                    {
                        var children = asDimension[t].Select(r => r.FromTable.Name).Distinct().Take(3);
                        var parents = asFact[t].Select(r => r.ToTable.Name).Distinct().Take(3);
                        ev.Violations.Add(rule.NewFinding(t, t.Name, $"Table '{t.Name}' sits between {string.Join(", ", children)} and {string.Join(", ", parents)} in a snowflake/bridge relationship chain; AI-generated queries are more reliable on a clear fact-to-dimension star. Review whether to denormalize the chain or document why it is intentional."));
                    }
                    return ev;
                }));

            // ---- Relationships: disconnected table ----------------------------------------------
            // A single visible business table needs no join, so that population is dormant. With two or more business
            // tables, however, a table with no relationship cannot participate in cross-table natural-language
            // questions. Exclude tables disconnected BY DESIGN: field parameters (ParameterMetadata; they drive
            // selection via SELECTEDVALUE) and measure/parameter holders with no visible data columns.
            rules.Add(new ModelRule("REL-DISCONNECTED", "Visible business table participates in no relationship", ReadinessCategory.Relationships, Severity.Medium, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    bool IsFieldParameter(Table t) => t.Columns.Any(c => c.HasExtendedProperty("ParameterMetadata"));
                    var pop = m.Tables.Where(t => !t.IsHidden && !(t is CalculationGroupTable) && !IsFieldParameter(t)
                        && t.Columns.Any(c => !c.IsHidden && c.Type != ColumnType.RowNumber)).ToList();
                    if (pop.Count < 2) return new RuleEvaluation { Applicable = 0 };
                    var connected = m.Relationships.OfType<SingleColumnRelationship>()
                        .SelectMany(r => new[] { r.FromTable, r.ToTable }).Where(t => t != null).ToHashSet();
                    var ev = new RuleEvaluation { Applicable = pop.Count };
                    foreach (var t in pop.Where(t => !connected.Contains(t)))
                        ev.Violations.Add(rule.NewFinding(t, t.Name, $"Table '{t.Name}' has visible fields but no relationship; data agents and Copilot cannot join it to the other business tables. If it is intentionally disconnected, review and waive this finding."));
                    return ev;
                }));

            // An INACTIVE relationship can only be reached in DAX via USERELATIONSHIP — Q&A/Copilot cannot traverse
            // it, so the role it plays (e.g. Ship Date vs Order Date) is unanswerable in natural language. Detection
            // is deterministic (IsActive); the remediation is structural (denormalize the role column, or expose it
            // another way) → Proposal, not an auto-fix. Presence design: Applicable = the inactive relationships
            // (dormant on a model with none; a role-play-heavy model docks proportionally to Info weight).
            rules.Add(new ModelRule("REL-INACTIVE", "Inactive relationship (Q&A/Copilot cannot traverse it)", ReadinessCategory.Relationships, Severity.Info, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var inactive = m.Relationships.OfType<SingleColumnRelationship>().Where(r => !r.IsActive).ToList();
                    var ev = new RuleEvaluation { Applicable = inactive.Count };   // presence design
                    foreach (var r in inactive)
                        ev.Violations.Add(rule.NewFinding(r, r.Name, $"Relationship {r.FromTable?.Name}->{r.ToTable?.Name} is inactive; Q&A/Copilot can't use it (only DAX via USERELATIONSHIP can). If the role it plays should be answerable in natural language, copy the role column into the fact table or expose it another way."));
                    return ev;
                }));

            // A dimension that clearly supports a drill — a DATE table (>=2 year/quarter/month/week/day columns) or a
            // GEO table (>=2 recognised geographic columns) — but defines NO hierarchy. A hierarchy helps Q&A/Copilot
            // drill naturally (year->quarter->month, country->state->city). Name/GeoCategory based (no live data).
            // Presence design; Proposal (the user's Claude picks the level order). Calc-group tables excluded.
            rules.Add(new ModelRule("REL-HIERARCHY-MISSING", "Date/geo dimension has drill columns but no hierarchy", ReadinessCategory.Relationships, Severity.Info, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    bool IsDrillCandidate(Table t) => !t.IsHidden && !(t is CalculationGroupTable) && t.Hierarchies.Count == 0
                        && (GeoColumnCount(t) >= 2 || DatePartColumnCount(t) >= 2);
                    var pop = m.Tables.Where(IsDrillCandidate).ToList();
                    var ev = new RuleEvaluation { Applicable = pop.Count };   // presence design
                    foreach (var t in pop)
                    {
                        var kind = GeoColumnCount(t) >= 2 ? "geographic (country/state/city)" : "date (year/quarter/month)";
                        ev.Violations.Add(rule.NewFinding(t, t.Name, $"Table '{t.Name}' has {kind} drill columns but no hierarchy; add one so Q&A/Copilot can drill down naturally."));
                    }
                    return ev;
                }));

            // A visible table exposing >=2 visible DateTime columns (and NOT the marked date table) is the classic
            // multiple-date-fields ambiguity: Q&A/Copilot can't tell which date a time question means (Order vs Ship vs
            // Due), and only ONE date can drive a marked date table. Deterministic (DateTime type + DataCategory!=Time);
            // presence design (dormant unless a table actually has the ambiguity). Precise on star schemas whose facts
            // carry integer date KEYS (those aren't DateTime, so they don't fire — the role-play case is REL-INACTIVE's).
            rules.Add(new ModelRule("DATE-AMBIGUOUS", "Table exposes multiple date columns (ambiguous default date)", ReadinessCategory.Relationships, Severity.Info, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var ev = new RuleEvaluation();   // presence design
                    foreach (var t in m.Tables.Where(t => !t.IsHidden && !(t is CalculationGroupTable)
                        && !string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Strip validity-RANGE boundary dates (Start/End, Effective/Expiry, From/Thru): a start+end pair
                        // bounds ONE event (an SCD validity window) and is NOT the "which date does a time question use"
                        // ambiguity Microsoft warns about — that's about multiple EVENT dates (Order/Ship/Due, Birth/Hire).
                        // Precision-tuned against AdventureWorks: this spares Product/Promotion (Start/End) while keeping
                        // Internet/Reseller Sales (Order/Due/Ship), Customer (Birth/First-Purchase), Employee (Hire/Birth).
                        var dateCols = t.Columns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber
                            && c.DataType == DataType.DateTime && !RangeBoundaryDateName.IsMatch(c.Name ?? "")).ToList();
                        if (dateCols.Count < 2) continue;
                        ev.Applicable++;
                        ev.Violations.Add(rule.NewFinding(t, t.Name, $"Table '{t.Name}' exposes {dateCols.Count} visible date columns ({string.Join(", ", dateCols.Take(3).Select(c => c.Name))}{(dateCols.Count > 3 ? ", …" : "")}); Q&A/Copilot can't tell which one a time question means. Keep one canonical date and relate through it", ReadinessCopy.Op("mark_date_table"), ", hide the extra date columns", ReadinessCopy.Op("set_column_hidden"), ", or state the default date in AI instructions", ReadinessCopy.Op("set_ai_instructions"), "."));
                    }
                    return ev;
                }));

            // A visible hierarchy with a single level offers no drill path — it contributes a name to grounding but no
            // Year>Quarter>Month structure Q&A/Copilot can drill. Deterministic (Levels.Count<2); presence design
            // (dormant unless a single-level hierarchy exists). Proposal — add the missing levels or remove the stub.
            rules.Add(new ModelRule("REL-HIERARCHY-SINGLE-LEVEL", "Hierarchy has only one level (no drill path)", ReadinessCategory.Relationships, Severity.Info, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    var ev = new RuleEvaluation();   // presence design
                    foreach (var t in m.Tables.Where(t => !t.IsHidden && !(t is CalculationGroupTable)))
                        foreach (var h in t.Hierarchies.Where(h => !h.IsHidden && h.Levels.Count < 2))
                        {
                            ev.Applicable++;
                            ev.Violations.Add(rule.NewFinding(h, h.Name, $"Hierarchy '{t.Name}'[{h.Name}] has only one level, so it offers no drill path for Q&A/Copilot; add the remaining drill levels", ReadinessCopy.Op("create_hierarchy"), " or remove the single-level hierarchy."));
                        }
                    return ev;
                }));

            // ---- DAX best practice (deterministic, token-path — see docs/dax-best-practice-rules.md) ----
            // SCORED per-object rules (Applicable = the trigger population). All feed the DaxLint token-path lint so
            // they never false-fire on comments / strings / [names]; a triggered object with a matching lint finding
            // is a violation carrying that finding's message. See DaxRule (above) for the anti-inflation contract.
            rules.Add(DaxRule("BP-DAX-IFERROR", "IFERROR/ISERROR error trapping", Severity.Medium, FixKind.AiContent, "avoid-iferror",
                t => DaxScan.CallsFunction(t, "IFERROR") || DaxScan.CallsFunction(t, "ISERROR")));
            rules.Add(DaxRule("BP-DAX-IFERROR-DIV", "IFERROR guarding a division", Severity.Medium, FixKind.AiContent, "no-iferror-for-division",
                t => DaxScan.CallsFunction(t, "IFERROR") || (DaxScan.CallsFunction(t, "IF") && DaxScan.CallsFunction(t, "ISERROR"))));
            rules.Add(DaxRule("BP-DAX-IFERROR-SEARCH", "IFERROR guarding SEARCH/FIND", Severity.Medium, FixKind.AiContent, "no-iferror-for-search",
                t => DaxScan.CallsFunction(t, "IFERROR") || (DaxScan.CallsFunction(t, "IF") && DaxScan.CallsFunction(t, "ISERROR"))));
            rules.Add(DaxRule("BP-DAX-SUMMARIZE-EXT", "Extension columns inside SUMMARIZE", Severity.Medium, FixKind.Proposal, "no-columns-inside-summarize",
                t => DaxScan.CallsFunction(t, "SUMMARIZE")));
            rules.Add(DaxRule("BP-DAX-BARE-TABLE-FILTER", "Bare table as CALCULATE filter", Severity.Medium, FixKind.Proposal, "calculate-bare-table-filter",
                t => DaxScan.CallsFunction(t, "CALCULATE") || DaxScan.CallsFunction(t, "CALCULATETABLE")));
            rules.Add(DaxRule("BP-DAX-VAR-ALIAS", "VAR passed to CALCULATE under a context modifier", Severity.High, FixKind.Proposal, "var-not-a-live-alias-context-shift",
                t => DaxScan.VarDecls(t).Count > 0 && DaxScan.CallsFunction(t, "CALCULATE")));
            rules.Add(DaxRule("BP-DAX-ISBLANK-EQ", "Comparison to BLANK()", Severity.Medium, FixKind.AiContent, "isblank-not-equals-blank",
                t => DaxScan.CallsFunction(t, "BLANK")));
            rules.Add(DaxRule("BP-DAX-DIV-GUARD", "Hand-rolled zero guard around division", Severity.Medium, FixKind.AiContent, "divide-instead-of-if-zero-guard",
                t => DaxScan.CallsFunction(t, "IF")));
            rules.Add(DaxRule("BP-DAX-ERROR-FN", "ERROR() as control flow", Severity.Info, FixKind.None, "no-error-function",
                t => DaxScan.CallsFunction(t, "ERROR")));

            // ADVISORY rules — surfaced, never scored (Applicable stays 0, lint every object). Info/None.
            // BP-DAX-MEASURE-PREDICATE is advisory per the doc's "keep ADVISORY" rail: the detector can't tell
            // `[M] > 100` from the contested `col > [M]` direction, so it may not dock the score until it can.
            rules.Add(DaxAdvisory("BP-DAX-MEASURE-PREDICATE", "Measure in a boolean filter predicate", "dax-measure-in-calculate-boolean-predicate"));
            rules.Add(DaxAdvisory("BP-DAX-EARLIER", "EARLIER/EARLIEST used", "prefer-var-over-earlier"));
            rules.Add(DaxAdvisory("BP-DAX-VAR-UNUSED", "Unused VAR", "var-unused"));
            rules.Add(DaxAdvisory("BP-DAX-SELECTEDVALUE", "IF(HASONEVALUE, VALUES) idiom", "selectedvalue-over-if-hasonevalue-values"));
            rules.Add(DaxAdvisory("BP-DAX-DISTINCTCOUNT", "COUNTROWS(DISTINCT/VALUES) idiom", "distinctcount-over-countrows-distinct-values"));
            rules.Add(DaxAdvisory("BP-DAX-REMOVEFILTERS", "ALL as a CALCULATE filter modifier", "dax-prefer-removefilters-over-all-modifier"));

            // Model-level: Auto date/time is enabled (hidden PBI-generated local/template date tables). Applicable=1
            // only when the footprint exists (annotation OR a GUID-suffixed LocalDateTable_/DateTableTemplate_ name —
            // the GUID suffix is required so a user table named "DateTableTemplate" is safe); else dormant.
            rules.Add(new ModelRule("BP-AUTO-DATETIME", "Auto date/time is enabled", ReadinessCategory.BestPractice, Severity.Medium, RuleKind.Deterministic, FixKind.Proposal,
                (m, rule) =>
                {
                    bool IsFootprint(Table t) =>
                        t.GetAnnotation("__PBI_LocalDateTable") != null
                        || t.GetAnnotation("__PBI_TemplateDateTable") != null
                        || AutoDateTableName.IsMatch(t.Name ?? "");
                    var footprint = m.Tables.Any(IsFootprint);
                    var ev = new RuleEvaluation { Applicable = footprint ? 1 : 0 };
                    if (footprint)
                        ev.Violations.Add(rule.NewFinding(m, m.Name, "Auto date/time is enabled (hidden per-column local date tables detected); turn it off, delete the hidden local date tables, and use one marked date table so Copilot/Q&A and time-intelligence DAX have a single, clean date table."));
                    return ev;
                }));

            return rules;
        }

        /// <summary>Dmv-kind rules that need live per-column cardinality (COLUMNSTATISTICS). Evaluated ONLY by the
        /// analyzer's Analyze(model, liveStats) overload — so on an offline scan they don't run at all and the
        /// scorecard is unchanged. They land in CopilotLimits (the Q&A index 5M-unique-value ceiling).</summary>
        public static IReadOnlyList<ReadinessRule> LiveRules(ReadinessLiveStats stats)
        {
            // The Q&A value index covers only TEXT values (Microsoft: "Q&A indexes only text values under 100
            // characters" — see docs/ai-readiness-catalog.json + ai-readiness-plan.md). So the indexed population is
            // VISIBLE STRING columns (numeric/decimal/datetime cardinality is NOT in the 5M term index); real (not
            // RowNumber), not on a calc group, and with a cardinality reading. (Follow-up: also exclude text columns
            // whose values average >=100 chars, which COLUMNSTATISTICS could report.)
            IEnumerable<Column> Indexed(Model m) => m.AllColumns.Where(c =>
                !c.IsHidden && c.Type != ColumnType.RowNumber && !(c.Table is CalculationGroupTable)
                && c.DataType == DataType.String
                && stats.TryGet(c.Table?.Name, c.Name, out _));

            long Card(Column c) { stats.TryGet(c.Table?.Name, c.Name, out var v); return v; }

            return new ReadinessRule[]
            {
                // Per-column: a single visible TEXT column whose cardinality dominates the Q&A index.
                new ObjectRule<Column>(
                    "SCALE-HICARD-COLUMN", "Visible high-cardinality text column bloats the Q&A index",
                    ReadinessCategory.CopilotLimits, Severity.Medium, RuleKind.Dmv, FixKind.Proposal,
                    scope: Indexed,
                    applies: _ => true,
                    violates: c => Card(c) > ReadinessLiveStats.HighCardinalityColumn,
                    message: c => $"{Card(c):N0} distinct text values; a single visible column this large dominates the {ReadinessLiveStats.QnaUniqueValueCeiling:N0}-value Q&A index. Hide it or exclude it from the AI data schema."),

                // Model-level: total indexed unique TEXT values vs the documented Q&A 5M ceiling.
                new ModelRule(
                    "SCALE-QNA-INDEX", "Indexed unique text values exceed the Q&A 5M ceiling",
                    ReadinessCategory.CopilotLimits, Severity.High, RuleKind.Dmv, FixKind.Proposal,
                    (m, rule) =>
                    {
                        var indexed = Indexed(m).ToList();
                        var ev = new RuleEvaluation { Applicable = indexed.Count == 0 ? 0 : 1 };
                        long total = indexed.Sum(Card);
                        if (total > ReadinessLiveStats.QnaUniqueValueCeiling)
                            ev.Violations.Add(rule.NewFinding(m, m.Name,
                                $"~{total:N0} indexed unique text values across visible columns exceed the Q&A index ceiling of {ReadinessLiveStats.QnaUniqueValueCeiling:N0}; Q&A/Copilot drop values and lose accuracy. Hide or exclude the high-cardinality text columns."));
                        return ev;
                    }),
            };
        }
    }
}
