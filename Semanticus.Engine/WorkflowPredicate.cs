using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Semanticus.Engine
{
    // ============================================================================================
    // The workflow PREDICATE evaluator (docs/pro-mode-spec.md §9.11/§10.6 — dynamic activation + the
    // §9.11 conditional bindings). The ONE place a `when:` string is parsed AND evaluated, so activation
    // and bindings can never grow two dialects. Deliberately a HAND parser — same rationale as
    // WorkflowParser: a tiny fixed grammar (field op literal + &&/||, no parens) needs no YAML/expression
    // package, and the engine carries no inference (golden rule 1: this is pure string/fact matching).
    //
    // Grammar:
    //   expr        := orExpr
    //   orExpr      := andExpr ('||' andExpr)*
    //   andExpr     := comparison ('&&' comparison)*
    //   comparison  := factRef OP literal
    //   factRef     := IDENT ('.' IDENT)+           e.g. model.tableCount, date.monthEndOffset
    //   OP          := '==' | '!=' | '<' | '<=' | '>' | '>=' | '~'   ('~' = glob match)
    //   literal     := quoted-string | number | true | false
    //
    // `&&` binds tighter than `||` (standard); there are NO parentheses (grouping is expressed by
    // splitting into multiple activation rules, first-match). Typing is per-fact: a number fact only
    // takes a numeric literal, a bool fact only true/false, readinessGrade is compared on a grade LADDER
    // (A+ highest → F lowest), everything else is a string (==/!=/~ glob, or lexical </> if ever used).
    // ============================================================================================

    /// <summary>What a fact term evaluates as — fixes the comparison semantics (§10.6 review A1(b)).</summary>
    public enum PredicateFactType { Number, Bool, Str, Grade, Unknown }

    /// <summary>One `factRef OP literal` leaf. <see cref="Type"/> is resolved once at parse so Evaluate
    /// never re-classifies. A comparison whose term is Unknown (a not-yet-available fact like target.* /
    /// workflow.active.*, or a genuinely misspelled term) always evaluates FALSE — the rule simply doesn't
    /// fire — and Parse records it in <c>error</c> so lint can surface it.</summary>
    public sealed class PredicateComparison
    {
        public string Left { get; set; }        // full fact term, e.g. "date.monthEndOffset"
        public string Root { get; set; }         // first segment, e.g. "date" (for lazy root gathering)
        public string Op { get; set; }           // ==,!=,<,<=,>,>=,~
        public string Literal { get; set; }      // the literal's VALUE (quotes stripped)
        public PredicateFactType Type { get; set; }
    }

    /// <summary>A parsed predicate: an OR of AND-groups of comparisons (disjunctive form — the grammar has
    /// no parens). <c>null</c> expr = unconditional true (a rule with no `when:` is the fallback, §9.11).</summary>
    public sealed class PredicateExpr
    {
        public List<List<PredicateComparison>> OrGroups { get; set; } = new List<List<PredicateComparison>>();
    }

    // ---- fact snapshot -------------------------------------------------------------------------
    // Each ROOT is a nullable object so a call site fills only what it has (§10.6 review §3): a null root ⇒
    // every term under it is "unknown" ⇒ every comparison against it is FALSE (never an exception). A null
    // FIELD within a present root is the same — e.g. connection.workspace is null for a local/offline session,
    // model.readinessGrade is null until something scanned this session (D2: never force-scanned on a menu read).

    public sealed class ModelFacts
    {
        public int TableCount { get; set; }
        public int MeasureCount { get; set; }
        public string ReadinessGrade { get; set; }   // null = never scanned this session (unknown → false)
        public bool HasRls { get; set; }
        public bool HasCalcGroups { get; set; }
        public int CompatLevel { get; set; }
        public string StorageMode { get; set; }
        public string Fingerprint { get; set; }
    }

    public sealed class ConnectionFacts
    {
        public string Kind { get; set; }         // offline | local | xmla
        public string Database { get; set; }
        public string Workspace { get; set; }     // null for local/offline (derived from the XMLA endpoint)
    }

    public sealed class GitFacts
    {
        public string Branch { get; set; }
        public bool Dirty { get; set; }
    }

    public sealed class SessionFacts
    {
        public string VerifiedMode { get; set; }  // "on" | "off"
        public string Tier { get; set; }          // "free" | "pro"
        public bool PlanLoaded { get; set; }
    }

    public sealed class DateFacts
    {
        public string Iso { get; set; }           // yyyy-MM-dd (UTC)
        public int DayOfMonth { get; set; }
        public int MonthEndOffset { get; set; }    // day - DaysInMonth (0 on the last day, -1 the day before)
    }

    /// <summary>The fact snapshot a `when:` is evaluated against. Nullable roots so each call site (activation
    /// vs a §9.11 binding) fills only the roots its rules reference (the perf contract, §2.5). Target/WorkflowActive
    /// are deferred to 10-T4 — a rule referencing them lands as "unknown" (never fires) + lint.</summary>
    public sealed class PredicateFacts
    {
        public ModelFacts Model { get; set; }
        public ConnectionFacts Connection { get; set; }
        public GitFacts Git { get; set; }
        public SessionFacts Session { get; set; }
        public DateFacts Date { get; set; }
    }

    public static class WorkflowPredicate
    {
        private static readonly Regex Comparison =
            new Regex(@"^\s*(?<ref>[A-Za-z_][A-Za-z0-9_.]*)\s*(?<op>==|!=|<=|>=|<|>|~)\s*(?<lit>.+?)\s*$", RegexOptions.Compiled);

        // The grade ladder (A+ highest → F lowest). `readinessGrade < 'B'` means "ranks below B" = worse than B,
        // so the rank must run best→worst; an unknown grade ranks 0 (comparisons against it are false).
        private static readonly Dictionary<string, int> GradeRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["A+"] = 11, ["A"] = 10, ["A-"] = 9, ["B+"] = 8, ["B"] = 7, ["B-"] = 6,
            ["C+"] = 5, ["C"] = 4, ["C-"] = 3, ["D"] = 2, ["F"] = 1,
        };

        /// <summary>Classify a fact term. Unknown covers both a misspelled term and a deferred one
        /// (target.* / workflow.active.* — wired in 10-T4); either way the comparison never fires + lint warns.</summary>
        public static PredicateFactType Classify(string term) => term switch
        {
            "model.tableCount" or "model.measureCount" or "model.compatLevel"
                or "date.dayOfMonth" or "date.monthEndOffset" => PredicateFactType.Number,
            "model.hasRls" or "model.hasCalcGroups" or "git.dirty" or "session.planLoaded" => PredicateFactType.Bool,
            "model.readinessGrade" => PredicateFactType.Grade,
            "connection.kind" or "connection.database" or "connection.workspace" or "git.branch"
                or "model.storageMode" or "model.fingerprint" or "session.verifiedMode" or "session.tier"
                or "date.iso" => PredicateFactType.Str,
            _ => PredicateFactType.Unknown,
        };

        /// <summary>Parse a `when:` string. Returns the parsed expr and (out) the FIRST problem found, if any:
        /// a STRUCTURAL failure (can't tokenise / bad operator) returns a null expr; a SEMANTIC issue (an unknown
        /// fact term, or a literal that can't be the fact's type) returns a NON-null expr (so it still evaluates —
        /// the offending comparison is simply false) with <paramref name="error"/> set so lint/set_workflow_activation
        /// can refuse or surface it. A null/blank `when:` returns (null, null) = unconditional true.</summary>
        public static PredicateExpr Parse(string when, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(when)) return null;   // no condition → the fallback rule (§9.11:389)

            var expr = new PredicateExpr();
            foreach (var orPart in SplitTopLevel(when, "||"))
            {
                var group = new List<PredicateComparison>();
                foreach (var andPart in SplitTopLevel(orPart, "&&"))
                {
                    var text = andPart.Trim();
                    var m = Comparison.Match(text);
                    if (!m.Success)
                    {
                        error = $"could not read the condition '{text}' — the shape is 'fact op value', e.g. date.monthEndOffset >= -3.";
                        return null;   // structural: unusable
                    }
                    var left = m.Groups["ref"].Value;
                    var op = m.Groups["op"].Value;
                    var rawLit = m.Groups["lit"].Value.Trim();

                    if (!left.Contains('.') || left.StartsWith(".") || left.EndsWith(".") || left.Contains(".."))
                    {
                        error = $"'{left}' is not a fact — a fact reads root.field, e.g. connection.workspace or model.tableCount.";
                        return null;
                    }

                    var (lit, litKind) = ReadLiteral(rawLit);   // litKind: 'q'=quoted string, 'n'=number, 'b'=bool, '?'=malformed
                    if (litKind == '?')
                    {
                        error = $"the value in '{text}' is not readable — use a number, true/false, or a 'quoted string'.";
                        return null;
                    }

                    var type = Classify(left);
                    var cmp = new PredicateComparison
                    {
                        Left = left, Root = left.Substring(0, left.IndexOf('.')), Op = op, Literal = lit, Type = type,
                    };
                    group.Add(cmp);

                    // Semantic (soft) checks — keep the comparison (so it evaluates FALSE and the rule is inert),
                    // but record the first problem for lint / the write-op refusal.
                    if (error == null)
                        error = TypeError(text, left, op, litKind, type);
                }
                expr.OrGroups.Add(group);
            }
            return expr;
        }

        /// <summary>Evaluate a parsed expr against a fact snapshot. A null expr = unconditional true. Any comparison
        /// whose term is unknown OR whose backing root/field the caller didn't supply is FALSE — never throws.</summary>
        public static bool Evaluate(PredicateExpr expr, PredicateFacts facts)
        {
            if (expr == null) return true;
            facts ??= new PredicateFacts();
            // disjunctive form: any AND-group all-true ⇒ true.
            foreach (var group in expr.OrGroups)
                if (group.All(c => EvaluateOne(c, facts)))
                    return true;
            return false;
        }

        /// <summary>The fact ROOTS a parsed expr references (e.g. {"git","date"}) — lets a caller gather ONLY those
        /// roots (§2.5): a project with no git-referencing rule never shells out to git; zero rules ⇒ empty set.</summary>
        public static IReadOnlyCollection<string> ReferencedRoots(PredicateExpr expr)
        {
            var roots = new HashSet<string>(StringComparer.Ordinal);
            if (expr != null)
                foreach (var group in expr.OrGroups)
                    foreach (var c in group)
                        if (!string.IsNullOrEmpty(c.Root)) roots.Add(c.Root);
            return roots;
        }

        // ---- internals -------------------------------------------------------------------------

        private static bool EvaluateOne(PredicateComparison c, PredicateFacts f)
        {
            switch (c.Type)
            {
                case PredicateFactType.Number:
                {
                    var v = ResolveNumber(c.Left, f);
                    if (v == null) return false;
                    if (!double.TryParse(c.Literal, NumberStyles.Any, CultureInfo.InvariantCulture, out var lit)) return false;
                    return CompareNumbers(v.Value, c.Op, lit);
                }
                case PredicateFactType.Bool:
                {
                    var v = ResolveBool(c.Left, f);
                    if (v == null) return false;
                    if (!bool.TryParse(c.Literal, out var lit)) return false;
                    return c.Op == "==" ? v.Value == lit : c.Op == "!=" ? v.Value != lit : false;
                }
                case PredicateFactType.Grade:
                {
                    var g = ResolveString(c.Left, f);   // readinessGrade is delivered as a string
                    if (g == null || !GradeRank.TryGetValue(g, out var vr)) return false;   // unknown/unscanned ⇒ false
                    if (!GradeRank.TryGetValue(c.Literal, out var lr)) return false;         // a bad grade literal ⇒ false
                    return CompareNumbers(vr, c.Op, lr);
                }
                case PredicateFactType.Str:
                {
                    var v = ResolveString(c.Left, f);
                    if (v == null) return false;
                    return CompareStrings(v, c.Op, c.Literal);
                }
                default:
                    return false;   // Unknown term — never fires (deferred/misspelled)
            }
        }

        private static bool CompareNumbers(double a, string op, double b) => op switch
        {
            "==" => a == b, "!=" => a != b, "<" => a < b, "<=" => a <= b, ">" => a > b, ">=" => a >= b,
            _ => false,   // '~' is meaningless on numbers
        };

        private static bool CompareStrings(string a, string op, string b)
        {
            switch (op)
            {
                case "==": return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                case "!=": return !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                case "~": return GlobMatch(a, b);
                case "<": return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0;
                case "<=": return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0;
                case ">": return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) > 0;
                case ">=": return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) >= 0;
                default: return false;
            }
        }

        private static bool GlobMatch(string value, string pattern)
        {
            // `*` = any run, `?` = one char; everything else literal — a fuzzy match, so case-insensitive.
            var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(value ?? "", rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static double? ResolveNumber(string term, PredicateFacts f) => term switch
        {
            "model.tableCount" => f.Model?.TableCount,
            "model.measureCount" => f.Model?.MeasureCount,
            "model.compatLevel" => f.Model?.CompatLevel,
            "date.dayOfMonth" => f.Date?.DayOfMonth,
            "date.monthEndOffset" => f.Date?.MonthEndOffset,
            _ => null,
        };

        private static bool? ResolveBool(string term, PredicateFacts f) => term switch
        {
            "model.hasRls" => f.Model?.HasRls,
            "model.hasCalcGroups" => f.Model?.HasCalcGroups,
            "git.dirty" => f.Git?.Dirty,
            "session.planLoaded" => f.Session?.PlanLoaded,
            _ => null,
        };

        private static string ResolveString(string term, PredicateFacts f) => term switch
        {
            "model.readinessGrade" => f.Model?.ReadinessGrade,
            "model.storageMode" => f.Model?.StorageMode,
            "model.fingerprint" => f.Model?.Fingerprint,
            "connection.kind" => f.Connection?.Kind,
            "connection.database" => f.Connection?.Database,
            "connection.workspace" => f.Connection?.Workspace,
            "git.branch" => f.Git?.Branch,
            "session.verifiedMode" => f.Session?.VerifiedMode,
            "session.tier" => f.Session?.Tier,
            "date.iso" => f.Date?.Iso,
            _ => null,
        };

        /// <summary>The first typing problem for a comparison, or null. Kept SOFT (the comparison still parses +
        /// evaluates false) so a broken rule is inert, not a whole-file brick.</summary>
        private static string TypeError(string text, string left, string op, char litKind, PredicateFactType type)
        {
            switch (type)
            {
                case PredicateFactType.Unknown:
                    return $"'{left}' is not a fact this version can read (facts are model.* / connection.* / git.* / session.* / date.*) — the condition '{text}' will never match.";
                case PredicateFactType.Number:
                    if (op == "~") return $"'{left}' is a number, so '~' (pattern match) does not apply in '{text}'.";
                    if (litKind != 'n') return $"'{left}' is a number, so compare it to a number (e.g. {left} >= 28), not '{text}'.";
                    return null;
                case PredicateFactType.Bool:
                    if (op != "==" && op != "!=") return $"'{left}' is true/false, so use == or != in '{text}'.";
                    if (litKind != 'b') return $"'{left}' is true/false — write {left} == true (or false), not '{text}'.";
                    return null;
                case PredicateFactType.Grade:
                    if (op == "~") return $"a grade compares by rank, so '~' does not apply in '{text}' — use < 'B' (worse than B) or == 'A'.";
                    if (litKind != 'q' && litKind != 'b') return $"compare model.readinessGrade to a grade letter, e.g. model.readinessGrade < 'B' — not '{text}'.";
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>Read a literal: a 'quoted' / "quoted" string, a number, or true/false. Returns the value and a
        /// kind tag ('q' quoted, 'n' number, 'b' bool, '?' malformed).</summary>
        private static (string value, char kind) ReadLiteral(string raw)
        {
            if (raw.Length >= 2 && (raw[0] == '\'' || raw[0] == '"'))
            {
                var q = raw[0];
                if (raw[raw.Length - 1] != q) return (raw, '?');   // unclosed quote
                return (raw.Substring(1, raw.Length - 2), 'q');
            }
            if (string.Equals(raw, "true", StringComparison.Ordinal) || string.Equals(raw, "false", StringComparison.Ordinal))
                return (raw, 'b');
            if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                return (raw, 'n');
            // A bareword (e.g. an unquoted grade letter or branch name) — usable as a string, but flagged 'q'-like so
            // grade/string facts still work; genuinely wrong shapes are caught by TypeError.
            return (raw, raw.Length == 0 ? '?' : 'q');
        }

        /// <summary>Split on a 2-char delimiter that is NOT inside single/double quotes, so a quoted literal
        /// containing the delimiter is never torn (a small robustness win over a naive Split).</summary>
        private static List<string> SplitTopLevel(string s, string delim)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (quote != '\0') { sb.Append(c); if (c == quote) quote = '\0'; continue; }
                if (c == '\'' || c == '"') { quote = c; sb.Append(c); continue; }
                if (c == delim[0] && i + 1 < s.Length && s[i + 1] == delim[1])
                {
                    parts.Add(sb.ToString());
                    sb.Clear();
                    i++;   // consume the second delimiter char
                    continue;
                }
                sb.Append(c);
            }
            parts.Add(sb.ToString());
            return parts;
        }
    }
}
