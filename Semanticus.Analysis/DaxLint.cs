using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Analysis
{
    public sealed class DaxFinding
    {
        public string RuleId { get; set; }
        public string Category { get; set; }    // performance | correctness | readability | filter-semantics
        public string Severity { get; set; }    // warning | info
        public string Message { get; set; }
    }
    public sealed class DaxLintResult
    {
        public DaxFinding[] Findings { get; set; } = Array.Empty<DaxFinding>();
    }

    /// <summary>Model context that unlocks the identity-dependent rules. Without it those rules simply stand
    /// down (an expression alone can't know whether <c>Foo</c> is a table) — never guess from a name shape.</summary>
    public sealed class DaxLintContext
    {
        public HashSet<string> TableNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Deterministic DAX best-practice rules (SQLBI/DAX-Patterns) — a deterministic lint. Runs on the
    /// <see cref="DaxScan"/> token path — NOT text-<c>Contains</c> — so it does not false-fire on comments, string
    /// literals, <c>[bracketed names]</c>, or <c>'quoted tables'</c>. Every structural rule fails toward FALSE
    /// NEGATIVES (strict shapes, stand-down on ambiguity): a lint that cries wolf gets turned off. The scored
    /// model-level integration lives in ReadinessRules (BestPractice category); this class stays expression-pure.
    /// Reports; the caller/fixer decides (no blind auto-apply). Full ruleset: docs/dax-best-practice-rules.md.</summary>
    public static class DaxLint
    {
        private static readonly string[] RollupFns = { "ROLLUP", "ROLLUPGROUP", "ROLLUPADDISSUBTOTAL", "ROLLUPISSUBTOTAL", "ISSUBTOTAL" };
        // Only the UNAMBIGUOUS not-equal form: "=" can't be told from a VAR assignment (VAR x = BLANK()) on a
        // token-only scan, so flagging it would false-fire; "<>" is always a comparison. (The "=" form is deferred.)
        private static readonly string[] Cmp = { "<>" };
        private static readonly string[] CompareOps = { "=", "==", "<>", "<", ">", "<=", ">=" };
        // What makes a SUMMARIZE extension column expensive: an aggregation / CALCULATE under the per-group context
        // transition (a constant or plain row-level expression has no ADDCOLUMNS-hoisting payoff). Dotted stats
        // functions (STDEV.S …) tokenize as Word+Other and simply never match — a missed aggregation fails safe.
        private static readonly string[] AggFns =
        {
            "SUM","SUMX","COUNT","COUNTX","COUNTA","COUNTAX","COUNTROWS","DISTINCTCOUNT","DISTINCTCOUNTNOBLANK",
            "MIN","MINX","MAX","MAXX","AVERAGE","AVERAGEX","MEDIAN","MEDIANX","PRODUCT","PRODUCTX","RANKX",
            "CALCULATE","CALCULATETABLE",
        };

        public static DaxLintResult Analyze(string dax) => Analyze(dax, null);

        public static DaxLintResult Analyze(string dax, DaxLintContext ctx)
        {
            var f = new List<DaxFinding>();
            var toks = DaxScan.Tokenize(dax);
            if (toks.Count == 0) return new DaxLintResult();
            var vars = DaxScan.VarDecls(toks);
            var varNames = new HashSet<string>(vars.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);

            if (DaxScan.CallsFunction(toks, "IFERROR") || DaxScan.CallsFunction(toks, "ISERROR"))
                f.Add(New("avoid-iferror", "performance", "warning",
                    "IFERROR/ISERROR force row-by-row formula-engine evaluation and block storage-engine optimization. Avoid the error at source: DIVIDE for division, SEARCH/FIND's 4th argument for not-found, COALESCE / input validation otherwise."));

            if (DaxScan.CallsFunction(toks, "ERROR"))
                f.Add(New("no-error-function", "correctness", "info",
                    "Avoid ERROR() as control flow; return BLANK() or a sentinel, or validate upstream."));

            if (DaxScan.CallsFunction(toks, "EARLIER") || DaxScan.CallsFunction(toks, "EARLIEST"))
                f.Add(New("prefer-var-over-earlier", "readability", "info",
                    "Prefer capturing the outer row value in a VAR over EARLIER/EARLIEST; clearer and avoids nested row-context pitfalls."));

            // no-columns-inside-summarize — an extended column is a "Name", <expr> pair of top-level SUMMARIZE args
            // (after the table). Only flag when the EXTENSION EXPRESSION carries an aggregation / CALCULATE / measure
            // ref (the doc's carve-out): that's where the context-transition/cluster cost lives — a constant or plain
            // row-level extension has no ADDCOLUMNS-hoisting payoff. Stand down if any ROLLUP-family / ISSUBTOTAL is
            // used (subtotals legitimately need the extended form). Over-suppressing fails safe.
            if (!RollupFns.Any(r => DaxScan.CallsFunction(toks, r)))
            {
                foreach (var open in DaxScan.AllCallOpens(toks, "SUMMARIZE"))
                {
                    var args = DaxScan.ArgRanges(toks, open);
                    var hit = false;
                    for (var a = 1; a < args.Count; a++)   // args[0] is the table
                    {
                        var (s, e) = args[a];
                        if (s >= e || toks[s].Kind != DaxScan.Kind.String) continue;
                        if (a + 1 >= args.Count) break;                       // a trailing bare "Name" — malformed, stand down
                        var (xs, xe) = args[a + 1];
                        if (AggFns.Any(fn => RangeCallsFunction(toks, xs, xe, fn)) || ContainsBareBracketRef(toks, xs, xe))
                        {
                            f.Add(New("no-columns-inside-summarize", "performance", "warning",
                                "Don't compute extended columns inside SUMMARIZE; SUMMARIZE should only GROUP. Use ADDCOLUMNS(SUMMARIZE(t, g), \"M\", <expr>) or SUMMARIZECOLUMNS, and verify equivalence before applying."));
                            hit = true; break;
                        }
                        a++;                                                  // skip the expression of this "Name", <expr> pair
                    }
                    if (hit) break;
                }
            }

            // isblank-not-equals-blank — a comparison operator adjacent to a BLANK() call.
            for (var j = 0; j + 2 < toks.Count; j++)
            {
                if (toks[j].Kind == DaxScan.Kind.Word && string.Equals(toks[j].Text, "BLANK", StringComparison.OrdinalIgnoreCase)
                    && toks[j + 1].Kind == DaxScan.Kind.LParen && toks[j + 2].Kind == DaxScan.Kind.RParen)
                {
                    var before = j - 1 >= 0 && toks[j - 1].Kind == DaxScan.Kind.Op && Cmp.Contains(toks[j - 1].Text);
                    var after = j + 3 < toks.Count && toks[j + 3].Kind == DaxScan.Kind.Op && Cmp.Contains(toks[j + 3].Text);
                    if (before || after)
                    {
                        f.Add(New("isblank-not-equals-blank", "correctness", "warning",
                            "Prefer NOT ISBLANK(x) over x <> BLANK(). Suggestion only: <> BLANK() also treats 0 as non-blank, so verify before changing."));
                        break;
                    }
                }
            }

            // ---- IFERROR-shaped rules: the specific, fixable forms of avoid-iferror -------------------------------
            // IFERROR(<bare a/b>, alt)  and  IF(ISERROR(<bare a/b>), …)  →  DIVIDE.
            // IFERROR(SEARCH/FIND(…), alt)  and  IF(ISERROR(SEARCH/FIND(…)), …)  →  the 4th argument / CONTAINSSTRING.
            var divisionForm = false; var searchForm = false;
            foreach (var open in DaxScan.AllCallOpens(toks, "IFERROR"))
            {
                var args = DaxScan.ArgRanges(toks, open);
                if (args.Count < 1) continue;
                ClassifyTrappedExpr(toks, args[0].start, args[0].end, ref divisionForm, ref searchForm);
            }
            foreach (var open in DaxScan.AllCallOpens(toks, "IF"))
            {
                var args = DaxScan.ArgRanges(toks, open);
                if (args.Count < 1) continue;
                var (cs, ce) = args[0];
                if (DaxScan.RootCallName(toks, cs, ce) != "ISERROR") continue;
                var inner = DaxScan.ArgRanges(toks, cs + 1);
                if (inner.Count == 1)
                    ClassifyTrappedExpr(toks, inner[0].start, inner[0].end, ref divisionForm, ref searchForm);
            }
            if (divisionForm)
                f.Add(New("no-iferror-for-division", "performance", "warning",
                    "Division guarded with IFERROR/IF(ISERROR(…)); use DIVIDE(numerator, denominator [, alternate]) instead; it handles the zero-denominator case without the error-trap cost."));
            if (searchForm)
                f.Add(New("no-iferror-for-search", "performance", "warning",
                    "SEARCH/FIND wrapped in IFERROR/IF(ISERROR(…)) for 'not found'; pass the 4th (NotFoundValue) argument instead, or use CONTAINSSTRING for a boolean test."));

            // divide-instead-of-if-zero-guard — IF(E<>0, A/E') / IF(E=0, alt, A/E') / IF(ISBLANK(E), …, A/E') where
            // E' structurally equals E. STRICT subtree equality (NormText) is load-bearing: relax it and it over-fires.
            foreach (var open in DaxScan.AllCallOpens(toks, "IF"))
            {
                var args = DaxScan.ArgRanges(toks, open);
                if (args.Count < 2) continue;
                var guarded = GuardedExpr(toks, args[0].start, args[0].end);
                if (guarded == null) continue;
                var hit = false;
                for (var a = 1; a < args.Count && !hit; a++)
                {
                    var (s, e) = args[a];
                    var ops = DaxScan.TopLevelOps(toks, s, e);
                    if (ops.Count != 1 || ops[0].op != "/") continue;
                    var denom = DaxScan.NormText(toks, ops[0].index + 1, e);
                    if (denom == guarded)
                    {
                        f.Add(New("divide-instead-of-if-zero-guard", "performance", "warning",
                            "Hand-rolled zero/blank guard around a division; DIVIDE(numerator, denominator [, alternate]) is faster and clearer. Verify first: DIVIDE returns BLANK (or the alternate) where the guard may have returned something else."));
                        hit = true;
                    }
                }
                if (hit) break;
            }

            // var-not-a-live-alias-context-shift — CALCULATE(<scalar VAR ref>, <modifier…>): a VAR is a CONSTANT;
            // the context modifier cannot re-evaluate it. (CALCULATETABLE excluded; table VARs stand down.)
            if (varNames.Count > 0)
            {
                foreach (var open in DaxScan.AllCallOpens(toks, "CALCULATE"))
                {
                    var args = DaxScan.ArgRanges(toks, open);
                    if (args.Count < 2) continue;
                    var (s, e) = args[0];
                    if (e - s != 1 || toks[s].Kind != DaxScan.Kind.Word) continue;
                    var decl = vars.FirstOrDefault(v => string.Equals(v.Name, toks[s].Text, StringComparison.OrdinalIgnoreCase));
                    if (decl == null || decl.TableValued) continue;
                    f.Add(New("var-not-a-live-alias-context-shift", "correctness", "warning",
                        $"CALCULATE(<VAR {decl.Name}>, <modifier>): a VAR is evaluated ONCE where it is defined; the context modifier will NOT recompute it. Put the original measure/expression inside CALCULATE instead (e.g. VAR SalesPY = CALCULATE([Sales], SAMEPERIODLASTYEAR('Date'[Date])))."));
                    break;
                }
            }

            // var-unused — a declared VAR never referenced again (bare-identifier scan; name collisions with table
            // refs read as 'used', failing toward false negatives — the safe direction).
            if (vars.Count > 0)
            {
                var unused = new List<string>();
                foreach (var v in vars)
                {
                    var used = false;
                    for (var j = v.NameIndex + 1; j < toks.Count && !used; j++)
                        if (toks[j].Kind == DaxScan.Kind.Word && string.Equals(toks[j].Text, v.Name, StringComparison.OrdinalIgnoreCase))
                            used = true;
                    if (!used) unused.Add(v.Name);
                }
                if (unused.Count > 0)
                    f.Add(New("var-unused", "readability", "info",
                        $"Unused VAR{(unused.Count > 1 ? "s" : "")}: {string.Join(", ", unused)}; declared but never referenced, safe to delete."));
            }

            // selectedvalue-over-if-hasonevalue-values — IF(HASONEVALUE(X), VALUES(X)[, d]) ⇒ SELECTEDVALUE(X[, d]).
            // Identical column text mandatory (different columns = a deliberate construct, not the idiom).
            foreach (var open in DaxScan.AllCallOpens(toks, "IF"))
            {
                var args = DaxScan.ArgRanges(toks, open);
                if (args.Count < 2) continue;
                if (DaxScan.RootCallName(toks, args[0].start, args[0].end) != "HASONEVALUE") continue;
                var condArg = SingleArgText(toks, args[0].start + 1);
                if (condArg == null) continue;
                var thenRoot = DaxScan.RootCallName(toks, args[1].start, args[1].end);
                if (thenRoot != "VALUES" && thenRoot != "DISTINCT") continue;
                if (SingleArgText(toks, args[1].start + 1) != condArg) continue;
                f.Add(New("selectedvalue-over-if-hasonevalue-values", "readability", "info",
                    "IF(HASONEVALUE(X), VALUES(X) [, default]) is exactly SELECTEDVALUE(X [, default]); shorter and clearer."));
                break;
            }

            // distinctcount-over-countrows-distinct-values — COUNTROWS(DISTINCT/VALUES(col)) ⇒ DISTINCTCOUNT(col).
            // Column arg only (a TABLE arg has different blank-row semantics); suggestion, never auto-apply.
            foreach (var open in DaxScan.AllCallOpens(toks, "COUNTROWS"))
            {
                var args = DaxScan.ArgRanges(toks, open);
                if (args.Count != 1) continue;
                var root = DaxScan.RootCallName(toks, args[0].start, args[0].end);
                if (root != "DISTINCT" && root != "VALUES") continue;
                var inner = DaxScan.ArgRanges(toks, args[0].start + 1);
                if (inner.Count != 1 || !IsColumnRef(toks, inner[0].start, inner[0].end)) continue;
                f.Add(New("distinctcount-over-countrows-distinct-values", "correctness", "info",
                    $"COUNTROWS({root}(column)); prefer DISTINCTCOUNT(column). Note VALUES/DISTINCT can include the relationship blank row (±1 difference): verify equivalence before changing."));
                break;
            }

            // ---- CALCULATE/CALCULATETABLE filter-slot rules (args 2..N) --------------------------------------------
            var flaggedRemoveFilters = false; var flaggedBareTable = false; var flaggedMeasurePredicate = false;
            foreach (var name in new[] { "CALCULATE", "CALCULATETABLE" })
            {
                foreach (var open in DaxScan.AllCallOpens(toks, name))
                {
                    var args = DaxScan.ArgRanges(toks, open);
                    for (var a = 1; a < args.Count; a++)
                    {
                        var (s, e) = args[a];
                        if (s >= e) continue;

                        // dax-prefer-removefilters-over-all-modifier — ALL(…) used as a filter MODIFIER (directly or
                        // through KEEPFILTERS). Table-returning uses (FILTER(ALL(T),…), iterators) sit in a different
                        // parent slot and are excluded by construction.
                        var root = DaxScan.RootCallName(toks, s, e);
                        if (!flaggedRemoveFilters)
                        {
                            var allRoot = root == "ALL";
                            if (!allRoot && root == "KEEPFILTERS")
                            {
                                var kf = DaxScan.ArgRanges(toks, s + 1);
                                allRoot = kf.Count == 1 && DaxScan.RootCallName(toks, kf[0].start, kf[0].end) == "ALL";
                            }
                            if (allRoot)
                            {
                                f.Add(New("dax-prefer-removefilters-over-all-modifier", "readability", "info",
                                    "ALL used as a CALCULATE filter modifier; prefer REMOVEFILTERS, which states the intent (remove filters) without ALL's table-returning double duty."));
                                flaggedRemoveFilters = true;
                            }
                        }

                        // calculate-bare-table-filter — a BARE table reference as a filter argument (context-gated:
                        // needs real table names; a bare identifier that is a VAR stands down).
                        if (!flaggedBareTable && ctx != null && ctx.TableNames.Count > 0 && e - s == 1)
                        {
                            var bare = toks[s].Kind == DaxScan.Kind.Word && !varNames.Contains(toks[s].Text) && ctx.TableNames.Contains(toks[s].Text)
                                    || toks[s].Kind == DaxScan.Kind.Name && toks[s].Delim == '\'' && ctx.TableNames.Contains(toks[s].Inner ?? "");
                            if (bare)
                            {
                                f.Add(New("calculate-bare-table-filter", "filter-semantics", "warning",
                                    $"A bare table reference as a {name} filter applies a filter over EVERY column of the (expanded) table; almost never the intent and expensive. Filter the column instead: {name}([M], KEEPFILTERS(T[Col] = …)) or use REMOVEFILTERS/VALUES for the restore idioms."));
                                flaggedBareTable = true;
                            }
                        }

                        // dax-measure-in-calculate-boolean-predicate — a boolean filter arg referencing a MEASURE
                        // (bare [Name] with no table qualifier). The engine rewrites it as FILTER over the column's
                        // values with the measure re-evaluated per value — a real SQLBI concern, but the detector
                        // can't tell `[M] > 100` from the contested `col > [M]` direction, so this ships as INFO
                        // (surfaced, never scored — see BP-DAX-MEASURE-PREDICATE, demoted to advisory). The correct
                        // FILTER(table, [M] > x) form has no TOP-LEVEL comparison (it is call-wrapped) → excluded.
                        if (!flaggedMeasurePredicate)
                        {
                            var ops = DaxScan.TopLevelOps(toks, s, e);
                            if (ops.Any(o => CompareOps.Contains(o.op)) && ContainsBareBracketRef(toks, s, e))
                            {
                                f.Add(New("dax-measure-in-calculate-boolean-predicate", "filter-semantics", "info",
                                    $"A measure inside a {name} boolean filter predicate: the engine rewrites it as FILTER over the column's values with the measure evaluated per value (can be slow, context-surprising). Consider an explicit FILTER(VALUES(T[Col]), [Measure] > …) so the iteration scope is visible and intentional."));
                                flaggedMeasurePredicate = true;
                            }
                        }
                    }
                }
            }

            return new DaxLintResult { Findings = f.ToArray() };
        }

        /// <summary>Classify what an IFERROR/ISERROR traps: a BARE top-level division (exactly one '/', nothing
        /// else at that depth) or a root SEARCH/FIND call that is still MISSING its 4th (NotFoundValue) argument —
        /// with all 4 args, SEARCH/FIND can't raise not-found, so the IFERROR is deliberately trapping a different
        /// error class and "pass the 4th argument" would be a no-op. Anything else is the generic avoid-iferror case.</summary>
        private static void ClassifyTrappedExpr(IReadOnlyList<DaxScan.Tok> toks, int s, int e, ref bool division, ref bool search)
        {
            var ops = DaxScan.TopLevelOps(toks, s, e);
            if (ops.Count == 1 && ops[0].op == "/") { division = true; return; }
            var root = DaxScan.RootCallName(toks, s, e);
            if ((root == "SEARCH" || root == "FIND") && DaxScan.ArgRanges(toks, s + 1).Count < 4) search = true;
        }

        /// <summary>For an IF condition, the normalized text of the guarded expression E when the condition is a
        /// zero/blank guard: <c>E = 0</c> / <c>E &lt;&gt; 0</c> (either side) or <c>ISBLANK(E)</c>. Else null.</summary>
        private static string GuardedExpr(IReadOnlyList<DaxScan.Tok> toks, int s, int e)
        {
            var root = DaxScan.RootCallName(toks, s, e);
            if (root == "ISBLANK")
            {
                var inner = DaxScan.ArgRanges(toks, s + 1);
                return inner.Count == 1 ? DaxScan.NormText(toks, inner[0].start, inner[0].end) : null;
            }
            var ops = DaxScan.TopLevelOps(toks, s, e);
            if (ops.Count != 1 || (ops[0].op != "=" && ops[0].op != "<>" && ops[0].op != "==")) return null;
            var left = DaxScan.NormText(toks, s, ops[0].index);
            var right = DaxScan.NormText(toks, ops[0].index + 1, e);
            if (right == "0") return left;
            if (left == "0") return right;
            return null;
        }

        /// <summary>Is there a call node to <paramref name="name"/> anywhere WITHIN the range [s,e)?</summary>
        private static bool RangeCallsFunction(IReadOnlyList<DaxScan.Tok> toks, int s, int e, string name)
        {
            for (var k = Math.Max(0, s); k + 1 < e && k + 1 < toks.Count; k++)
                if (toks[k].Kind == DaxScan.Kind.Word && string.Equals(toks[k].Text, name, StringComparison.OrdinalIgnoreCase)
                    && toks[k + 1].Kind == DaxScan.Kind.LParen)
                    return true;
            return false;
        }

        /// <summary>Normalized text of a call's single argument (lparen at <paramref name="lparen"/>), or null.</summary>
        private static string SingleArgText(IReadOnlyList<DaxScan.Tok> toks, int lparen)
        {
            var args = DaxScan.ArgRanges(toks, lparen);
            return args.Count == 1 ? DaxScan.NormText(toks, args[0].start, args[0].end) : null;
        }

        /// <summary>Is the range exactly a COLUMN reference — optional table part (Word or 'quoted') + [bracketed]?</summary>
        private static bool IsColumnRef(IReadOnlyList<DaxScan.Tok> toks, int s, int e)
        {
            if (e - s == 1) return toks[s].Kind == DaxScan.Kind.Name && toks[s].Delim == '[';
            if (e - s == 2)
                return (toks[s].Kind == DaxScan.Kind.Word || (toks[s].Kind == DaxScan.Kind.Name && toks[s].Delim == '\''))
                    && toks[s + 1].Kind == DaxScan.Kind.Name && toks[s + 1].Delim == '[';
            return false;
        }

        /// <summary>Any [bracketed] name in the range NOT immediately preceded by a table part — i.e. a measure
        /// reference. (In a boolean filter predicate, column refs must be table-qualified, so bare [X] = measure.)</summary>
        private static bool ContainsBareBracketRef(IReadOnlyList<DaxScan.Tok> toks, int s, int e)
        {
            for (var j = s; j < e && j < toks.Count; j++)
            {
                if (toks[j].Kind != DaxScan.Kind.Name || toks[j].Delim != '[') continue;
                var qualified = j > s &&
                    (toks[j - 1].Kind == DaxScan.Kind.Word ||
                     (toks[j - 1].Kind == DaxScan.Kind.Name && toks[j - 1].Delim == '\''));
                if (!qualified) return true;
            }
            return false;
        }

        private static DaxFinding New(string id, string cat, string sev, string msg) =>
            new DaxFinding { RuleId = id, Category = cat, Severity = sev, Message = msg };
    }
}
