using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Semanticus.Analysis
{
    /// <summary>A minimal, self-contained DAX STRUCTURAL scanner — the token path the DAX best-practice rules need so
    /// they match REAL function/operator nodes, never text inside a comment, string, <c>[Column]</c>, or <c>'Table'</c>.
    /// It is NOT a full DAX parser (there is no grammar/AST): it tokenizes + tracks paren depth, which is enough for the
    /// enforceable rules (function-node presence, arg-position walks, comparison-to-BLANK). This is why the rules are
    /// low-false-positive: a naive <c>Expression.Contains("IFERROR")</c> trips on <c>// use IFERROR</c>, <c>"IFERROR"</c>,
    /// and <c>[IfError Flag]</c>; the scanner does not, because those are lexed as Comment (dropped) / String / Name.</summary>
    public static class DaxScan
    {
        public enum Kind { Word, Name, Number, String, Op, LParen, RParen, LBrace, RBrace, Comma, Other }

        public sealed class Tok
        {
            public Kind Kind;
            public string Text;   // Word/Op text; empty for String/Name (rules must never keyword-match inside them)
            public string Inner;  // the CONTENT of a Name/String (unescaped) — for identity checks only, never keyword matching
            public char Delim;    // '[' bracketed name, '\'' quoted table name, '"' string, '\0' otherwise
            public int Depth;     // paren/brace nesting depth AT this token (top level = 0)
        }

        /// <summary>Tokenize DAX, dropping comments and collapsing strings / bracketed [names] / 'quoted' names to a
        /// single opaque token (so a function/operator inside them can never match a rule). The delimited content is
        /// preserved in <see cref="Tok.Inner"/> for IDENTITY checks (same column? a real table name?) only.</summary>
        public static List<Tok> Tokenize(string dax)
        {
            var toks = new List<Tok>();
            if (string.IsNullOrEmpty(dax)) return toks;
            int i = 0, n = dax.Length, depth = 0;
            void Add(Kind k, string t, string inner = null, char delim = '\0') =>
                toks.Add(new Tok { Kind = k, Text = t, Inner = inner, Delim = delim, Depth = depth });
            // Reads to the closing delimiter, unescaping the doubled form ("" '' ]]); returns the content.
            string Delimited(char close)
            {
                var sb = new StringBuilder();
                while (i < n)
                {
                    if (dax[i] == close)
                    {
                        if (i + 1 < n && dax[i + 1] == close) { sb.Append(close); i += 2; continue; }
                        i++; break;
                    }
                    sb.Append(dax[i]); i++;
                }
                return sb.ToString();
            }
            while (i < n)
            {
                char c = dax[i];
                // whitespace
                if (char.IsWhiteSpace(c)) { i++; continue; }
                // line comments // and --
                if ((c == '/' && i + 1 < n && dax[i + 1] == '/') || (c == '-' && i + 1 < n && dax[i + 1] == '-'))
                { i += 2; while (i < n && dax[i] != '\n') i++; continue; }
                // block comment /* */
                if (c == '/' && i + 1 < n && dax[i + 1] == '*')
                { i += 2; while (i + 1 < n && !(dax[i] == '*' && dax[i + 1] == '/')) i++; i = Math.Min(n, i + 2); continue; }
                // double-quoted string ("" escapes ")
                if (c == '"') { i++; Add(Kind.String, "", Delimited('"'), '"'); continue; }
                // 'quoted table name' ('' escapes ')
                if (c == '\'') { i++; Add(Kind.Name, "", Delimited('\''), '\''); continue; }
                // [bracketed column/measure name] (]] escapes ])
                if (c == '[') { i++; Add(Kind.Name, "", Delimited(']'), '['); continue; }
                // number
                if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(dax[i + 1])))
                { int s = i; while (i < n && (char.IsLetterOrDigit(dax[i]) || dax[i] == '.')) i++; Add(Kind.Number, dax.Substring(s, i - s)); continue; }
                // word / identifier / function name / keyword
                if (char.IsLetter(c) || c == '_')
                { int s = i; while (i < n && (char.IsLetterOrDigit(dax[i]) || dax[i] == '_')) i++; Add(Kind.Word, dax.Substring(s, i - s)); continue; }
                // punctuation + operators
                switch (c)
                {
                    case '(': Add(Kind.LParen, "("); depth++; i++; break;
                    case ')': depth = Math.Max(0, depth - 1); Add(Kind.RParen, ")"); i++; break;
                    case '{': Add(Kind.LBrace, "{"); depth++; i++; break;
                    case '}': depth = Math.Max(0, depth - 1); Add(Kind.RBrace, "}"); i++; break;
                    case ',': Add(Kind.Comma, ","); i++; break;
                    default:
                        // multi-char operators first
                        if ((c == '<' && i + 1 < n && (dax[i + 1] == '=' || dax[i + 1] == '>')) ||
                            (c == '>' && i + 1 < n && dax[i + 1] == '=') ||
                            (c == '=' && i + 1 < n && dax[i + 1] == '=') ||
                            (c == '&' && i + 1 < n && dax[i + 1] == '&') ||
                            (c == '|' && i + 1 < n && dax[i + 1] == '|'))
                        { Add(Kind.Op, dax.Substring(i, 2)); i += 2; break; }
                        if ("+-*/=<>&^|".IndexOf(c) >= 0) { Add(Kind.Op, c.ToString()); i++; break; }
                        Add(Kind.Other, c.ToString()); i++; break;
                }
            }
            return toks;
        }

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Is there a call to <paramref name="name"/> — a Word equal to it (case-insensitive) immediately
        /// followed by '('? (Not a bracketed [name], not inside a string/comment — those aren't Word tokens.)</summary>
        public static bool CallsFunction(IReadOnlyList<Tok> toks, string name)
        {
            for (int k = 0; k + 1 < toks.Count; k++)
                if (toks[k].Kind == Kind.Word && Eq(toks[k].Text, name) && toks[k + 1].Kind == Kind.LParen)
                    return true;
            return false;
        }

        /// <summary>Index of the '(' that opens the first call to <paramref name="name"/>, or -1.</summary>
        public static int CallOpenIndex(IReadOnlyList<Tok> toks, string name)
        {
            for (int k = 0; k + 1 < toks.Count; k++)
                if (toks[k].Kind == Kind.Word && Eq(toks[k].Text, name) && toks[k + 1].Kind == Kind.LParen)
                    return k + 1;
            return -1;
        }

        /// <summary>'(' indices of EVERY call to <paramref name="name"/> (a rule that stops at the first call
        /// silently misses the second — fail-safe is fine, silently is not).</summary>
        public static List<int> AllCallOpens(IReadOnlyList<Tok> toks, string name)
        {
            var opens = new List<int>();
            for (int k = 0; k + 1 < toks.Count; k++)
                if (toks[k].Kind == Kind.Word && Eq(toks[k].Text, name) && toks[k + 1].Kind == Kind.LParen)
                    opens.Add(k + 1);
            return opens;
        }

        /// <summary>Index of the ')' matching the '(' at <paramref name="lparenIndex"/>, or -1. (An LParen and its
        /// matching RParen carry the same recorded Depth — the depth OUTSIDE the pair.)</summary>
        public static int MatchingRParen(IReadOnlyList<Tok> toks, int lparenIndex)
        {
            if (lparenIndex < 0 || lparenIndex >= toks.Count || toks[lparenIndex].Kind != Kind.LParen) return -1;
            int outer = toks[lparenIndex].Depth;
            for (int k = lparenIndex + 1; k < toks.Count; k++)
                if (toks[k].Kind == Kind.RParen && toks[k].Depth == outer)
                    return k;
            return -1;
        }

        /// <summary>Top-level argument token-index ranges [start,end) for a call whose '(' is at <paramref name="lparenIndex"/>.
        /// Splits on commas at the call's inner depth only (nested commas ignored).</summary>
        public static List<(int start, int end)> ArgRanges(IReadOnlyList<Tok> toks, int lparenIndex)
        {
            var ranges = new List<(int, int)>();
            if (lparenIndex < 0 || lparenIndex >= toks.Count || toks[lparenIndex].Kind != Kind.LParen) return ranges;
            int outer = toks[lparenIndex].Depth;     // the '(' sits at this depth; its args are at outer+1
            int argStart = lparenIndex + 1, k = lparenIndex + 1;
            for (; k < toks.Count; k++)
            {
                var t = toks[k];
                if (t.Kind == Kind.RParen && t.Depth == outer) break;                 // matching close paren
                if (t.Kind == Kind.Comma && t.Depth == outer + 1) { ranges.Add((argStart, k)); argStart = k + 1; }
            }
            if (k > argStart || (ranges.Count == 0 && k > lparenIndex + 1)) ranges.Add((argStart, k));
            return ranges;
        }

        /// <summary>If the range [start,end) is EXACTLY one call — <c>NAME ( … )</c> spanning the whole range —
        /// return the (uppercased) function name; else null. This is how a rule asks "is this argument's ROOT a
        /// FILTER/ALL/SEARCH call?" without being fooled by the same call nested deeper.</summary>
        public static string RootCallName(IReadOnlyList<Tok> toks, int start, int end)
        {
            if (start < 0 || end > toks.Count || end - start < 3) return null;
            if (toks[start].Kind != Kind.Word || toks[start + 1].Kind != Kind.LParen) return null;
            return MatchingRParen(toks, start + 1) == end - 1 ? toks[start].Text.ToUpperInvariant() : null;
        }

        /// <summary>Operator tokens at the TOP paren-depth of the range [start,end) — the range's own operators,
        /// not those nested inside sub-calls/parens. (The base depth is the first token's depth: an LParen records
        /// the depth OUTSIDE itself, so a parenthesized head still yields the correct base.)</summary>
        public static List<(int index, string op)> TopLevelOps(IReadOnlyList<Tok> toks, int start, int end)
        {
            var ops = new List<(int, string)>();
            if (start < 0 || start >= end || end > toks.Count) return ops;
            int baseDepth = toks[start].Depth;
            for (int k = start; k < end; k++)
                if (toks[k].Kind == Kind.Op && toks[k].Depth == baseDepth)
                    ops.Add((k, toks[k].Text));
            return ops;
        }

        /// <summary>A canonical text form of the range [start,end) for STRICT structural equality (the
        /// divide-guard rule's E ≡ E′ check). Identifiers uppercase; names keep their delimiter class so
        /// <c>Sales[Amt]</c> vs <c>'Sales'[Amt]</c> compare UNEQUAL — a false negative, the safe direction.</summary>
        public static string NormText(IReadOnlyList<Tok> toks, int start, int end)
        {
            var sb = new StringBuilder();
            for (int k = Math.Max(0, start); k < end && k < toks.Count; k++)
            {
                var t = toks[k];
                if (sb.Length > 0) sb.Append(' ');
                switch (t.Kind)
                {
                    case Kind.Word: sb.Append(t.Text.ToUpperInvariant()); break;
                    case Kind.Name: sb.Append(t.Delim).Append((t.Inner ?? "").ToUpperInvariant()); break;
                    case Kind.String: sb.Append('"').Append(t.Inner ?? ""); break;
                    default: sb.Append(t.Text); break;
                }
            }
            return sb.ToString();
        }

        public sealed class VarDecl
        {
            public string Name;
            public int NameIndex;              // token index of the VAR's name
            public int BodyStart, BodyEnd;     // [start,end) token range of the assigned expression
            public bool TableValued;           // classified table-ish (fail-safe: unknown ⇒ table, so scalar-only rules stand down)
        }

        // Table-returning function heads for VAR classification. Deliberately moderate: an unknown root call
        // classifies as SCALAR, which only matters for CALCULATE(<var>, …) — where a table VAR is a compile
        // error anyway, so misclassification cannot mint a false positive on valid DAX.
        private static readonly HashSet<string> TableFns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FILTER","VALUES","ALL","ALLEXCEPT","ALLSELECTED","ALLNOBLANKROW","DISTINCT","SUMMARIZE","SUMMARIZECOLUMNS",
            "ADDCOLUMNS","SELECTCOLUMNS","CALCULATETABLE","CROSSJOIN","UNION","INTERSECT","EXCEPT","TOPN","GENERATE",
            "GENERATEALL","GENERATESERIES","ROW","TREATAS","NATURALINNERJOIN","NATURALLEFTOUTERJOIN","RELATEDTABLE",
            "DATESYTD","DATESQTD","DATESMTD","DATESBETWEEN","DATESINPERIOD","SAMEPERIODLASTYEAR","PARALLELPERIOD",
            "DATEADD","PREVIOUSYEAR","PREVIOUSQUARTER","PREVIOUSMONTH","PREVIOUSDAY","NEXTYEAR","NEXTQUARTER",
            "NEXTMONTH","NEXTDAY","CALENDAR","CALENDARAUTO","SAMPLE",
        };

        /// <summary>All <c>VAR name = body</c> declarations. A body ends at the next VAR/RETURN at the SAME depth,
        /// at a same-depth comma (the VAR sat inside a call argument), or when the depth drops below the
        /// declaration's (the enclosing paren closed). Lexer-only scoping — good enough because DAX requires
        /// declaration-before-use and forbids same-scope shadowing.</summary>
        public static List<VarDecl> VarDecls(IReadOnlyList<Tok> toks)
        {
            var decls = new List<VarDecl>();
            for (int k = 0; k + 2 < toks.Count; k++)
            {
                if (toks[k].Kind != Kind.Word || !Eq(toks[k].Text, "VAR")) continue;
                if (toks[k + 1].Kind != Kind.Word || toks[k + 2].Kind != Kind.Op || toks[k + 2].Text != "=") continue;
                int d = toks[k].Depth, s = k + 3, e = s;
                while (e < toks.Count)
                {
                    var t = toks[e];
                    if (t.Depth < d) break;
                    if (t.Depth == d && t.Kind == Kind.Comma) break;
                    if (t.Depth == d && t.Kind == Kind.Word && (Eq(t.Text, "VAR") || Eq(t.Text, "RETURN"))) break;
                    e++;
                }
                decls.Add(new VarDecl
                {
                    Name = toks[k + 1].Text,
                    NameIndex = k + 1,
                    BodyStart = s,
                    BodyEnd = e,
                    TableValued = IsTableValued(toks, s, e),
                });
            }
            return decls;
        }

        private static bool IsTableValued(IReadOnlyList<Tok> toks, int s, int e)
        {
            if (s >= e) return true;                                   // empty/unparseable ⇒ unknown ⇒ table (stand down)
            if (toks[s].Kind == Kind.LBrace) return true;              // {…} table constructor
            var root = RootCallName(toks, s, e);
            if (root != null) return TableFns.Contains(root);
            if (e - s == 1 && toks[s].Kind == Kind.Name && toks[s].Delim == '\'') return true;   // bare 'Table' ref
            if (e - s == 1 && toks[s].Kind == Kind.Word) return true;  // bare identifier: another VAR or a table — unknown ⇒ stand down
            return false;                                              // operator expressions / literals / measure refs: scalar
        }
    }
}
