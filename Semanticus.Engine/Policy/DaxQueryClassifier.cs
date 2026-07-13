using System.Text;

namespace Semanticus.Engine
{
    /// <summary>
    /// Classifies a DAX query as SCALAR-shaped (a calculation — <see cref="AgentCapability.QueryCalc"/>, ungated) or
    /// ROW-returning (reads source rows — <see cref="AgentCapability.QueryData"/>, gated), for the run_dax permissions
    /// gate (#129). run_dax maps to QueryCalc because a scalar evaluation is a calculation allowed everywhere, but
    /// <c>EVALUATE &lt;tableExpr&gt;</c> returns real rows and IS the QueryData exfiltration surface preview_table
    /// already gates. The call site routes a row-returning query through the same GuardAgent(QueryData, …) call.
    ///
    /// FAIL CLOSED — a query is scalar ONLY when every <c>EVALUATE</c> is immediately followed (modulo whitespace and
    /// leading parens) by a single-row/constant scalar wrapper we recognise:
    ///   • <c>EVALUATE ROW(...)</c>  — the row constructor: named scalar cells, one row (the verified-measure/probe shape).
    ///   • <c>EVALUATE { ... }</c>   — the table constructor: constant scalar members, never a source scan.
    /// EVERY other shape is treated as row-returning and gated: a bare table reference (<c>EVALUATE 'Sales'</c>), a table
    /// function (SUMMARIZECOLUMNS / FILTER / VALUES / TOPN / SELECTCOLUMNS / …), a <c>VAR … RETURN</c> we can't see past,
    /// a query with no readable EVALUATE, anything ambiguous. Over-gating a rare scalar shape only costs a query the
    /// human (or a dev/allow target) can still run; under-gating leaks rows. Deterministic + offline-unit-testable, the
    /// same shape as <see cref="AgentPolicyGuard"/>.
    /// </summary>
    public static class DaxQueryClassifier
    {
        /// <summary>True when the query returns source ROWS (⇒ gate it as QueryData). The inverse of <see cref="IsScalar"/>
        /// — so an unclassifiable query is row-returning (fail closed).</summary>
        public static bool IsRowReturning(string query) => !IsScalar(query);

        /// <summary>True ONLY for a query we can confidently read as pure scalar evaluation (EVALUATE ROW(...) /
        /// EVALUATE { ... }). Comments and string/identifier literals are masked first so a keyword inside them can never
        /// be mistaken for query structure.</summary>
        public static bool IsScalar(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return false;   // nothing to run — cannot confirm scalar ⇒ fail closed
            var s = Strip(query);

            int i = 0, found = 0;
            while (true)
            {
                int e = NextEvaluate(s, i);
                if (e < 0) break;
                found++;
                if (!ScalarShapedAfter(s, e + Evaluate.Length)) return false;   // one row-returning EVALUATE ⇒ gate the whole query
                i = e + Evaluate.Length;
            }
            return found > 0;   // no readable EVALUATE ⇒ cannot confirm scalar ⇒ fail closed
        }

        private const string Evaluate = "EVALUATE";

        // The next standalone EVALUATE keyword at/after `from`, or -1. Word-bounded on both sides so EVALUATEANDLOG (a
        // scalar DAX function) and any identifier containing the letters never register as the query statement.
        private static int NextEvaluate(string s, int from)
        {
            int idx = s.IndexOf(Evaluate, from, System.StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                bool leftOk = idx == 0 || !IsIdentChar(s[idx - 1]);
                int after = idx + Evaluate.Length;
                bool rightOk = after >= s.Length || !IsIdentChar(s[after]);
                if (leftOk && rightOk) return idx;
                idx = s.IndexOf(Evaluate, idx + 1, System.StringComparison.OrdinalIgnoreCase);
            }
            return -1;
        }

        // Does the expression starting at `pos` (just past an EVALUATE) begin with a recognised scalar wrapper? Skips
        // whitespace and leading '(' — EVALUATE (ROW(...)) is still scalar; EVALUATE ('Sales') peels to a table ref and
        // is not. A '{' opens the constant table constructor. "ROW" counts only when the next significant char is '('
        // (so ROWNUMBER(... ) — different function — falls through to gated).
        private static bool ScalarShapedAfter(string s, int pos)
        {
            int k = pos;
            while (k < s.Length && (char.IsWhiteSpace(s[k]) || s[k] == '(')) k++;
            if (k >= s.Length) return false;
            if (s[k] == '{') return true;
            if (k + 3 <= s.Length && string.Compare(s, k, "ROW", 0, 3, System.StringComparison.OrdinalIgnoreCase) == 0)
            {
                int m = k + 3;
                while (m < s.Length && char.IsWhiteSpace(s[m])) m++;
                return m < s.Length && s[m] == '(';
            }
            return false;
        }

        private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        // Blank out comments and the INTERIOR of string ("…") / quoted-identifier ('…') literals — keeping the delimiters
        // and every char position — so structural scanning above never trips on a keyword or brace inside a literal or
        // comment (e.g. EVALUATE ROW("EVALUATE 'Sales'", [M]) must stay scalar). Handles DAX line comments (// and --),
        // block comments (/* … */), and doubled-quote escapes ("" and '').
        private static string Strip(string q)
        {
            var sb = new StringBuilder(q.Length);
            int i = 0, n = q.Length;
            while (i < n)
            {
                char c = q[i];
                if ((c == '/' && i + 1 < n && q[i + 1] == '/') || (c == '-' && i + 1 < n && q[i + 1] == '-'))
                {
                    while (i < n && q[i] != '\n') { sb.Append(' '); i++; }   // to end of line
                    continue;
                }
                if (c == '/' && i + 1 < n && q[i + 1] == '*')
                {
                    // Terminates at the FIRST */ — deliberately non-nesting, matching the DAX lexer (block comments do
                    // not nest). Fail-closed either way: relative to a nesting reading, first-*/ termination can only
                    // UN-mask text, and revealed text can only add EVALUATEs or change what follows one — pushing the
                    // verdict toward gated, never toward scalar.
                    sb.Append("  "); i += 2;
                    while (i < n && !(q[i] == '*' && i + 1 < n && q[i + 1] == '/')) { sb.Append(q[i] == '\n' ? '\n' : ' '); i++; }
                    if (i < n) { sb.Append("  "); i += 2; }
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    sb.Append(c); i++;   // keep the opening delimiter (marks a string/table-ref start for the scanner)
                    while (i < n)
                    {
                        if (q[i] == c)
                        {
                            if (i + 1 < n && q[i + 1] == c) { sb.Append("  "); i += 2; continue; }   // doubled = escaped
                            break;
                        }
                        sb.Append(q[i] == '\n' ? '\n' : ' '); i++;   // blank the interior
                    }
                    if (i < n) { sb.Append(c); i++; }   // keep the closing delimiter
                    continue;
                }
                sb.Append(c); i++;
            }
            return sb.ToString();
        }
    }
}
