// What a ratio measure divides, read from its DAX on this computer (review-fix round 6, D1). A measure is a ratio only when it
// divides one aggregation of the model's data by another: DIVIDE ( [Returns Amount], [Net Sales] ), or one measure over
// another with "/". A total divided by a number (DIVIDE ( SUM ( Sales[Amount] ), 1000 ), a figure in thousands) is still a
// total, and a percent format on its own says nothing about what a number is. The parts are named by the measures and columns
// they read, never by a value, so a statement can say "Return Rate % (Returns Amount divided by Net Sales Amount)".

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Semanticus.Engine.Query
{
    internal static class RatioDax
    {
        private static readonly Regex Strings = new Regex("\"(?:[^\"]|\"\")*\"", RegexOptions.Compiled);
        private static readonly Regex Comments = new Regex(@"/\*.*?\*/|//[^\r\n]*|--[^\r\n]*", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex Divide = new Regex(@"\bDIVIDE\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MeasureRef = new Regex(@"(?<![\w'""\]])\[([^\]]+)\]", RegexOptions.Compiled);
        private static readonly Regex Aggregator = new Regex(
            @"\b(SUM|SUMX|AVERAGE|AVERAGEX|COUNT|COUNTA|COUNTX|COUNTAX|COUNTROWS|DISTINCTCOUNT|DISTINCTCOUNTNOBLANK|APPROXIMATEDISTINCTCOUNT|MIN|MAX|MINX|MAXX|MEDIAN|MEDIANX)\s*\(",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex OverColumn = new Regex(
            @"\b(SUM|AVERAGE|COUNT|COUNTA|DISTINCTCOUNT|DISTINCTCOUNTNOBLANK|APPROXIMATEDISTINCTCOUNT|MIN|MAX|MEDIAN)\s*\(\s*(?:'[^']+'|\w+)\s*\[([^\]]+)\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex OverTable = new Regex(@"\bCOUNTROWS\s*\(\s*(?:'([^']+)'|(\w+))\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The numerator and denominator (their text), or null when the expression is not one aggregation of data
        /// divided by another.</summary>
        public static (string Numerator, string Denominator)? Split(string expression)
        {
            var text = Comments.Replace(Strings.Replace(expression ?? "", "\"\""), " ");
            if (text.Trim().Length == 0) return null;
            // A ratio written with variables or behind a guard is still a ratio (verifier round 5, R14): the variables are put back
            // where RETURN uses them, and a guard whose other branch is only a blank or a number is read through to the division.
            text = Unguard(Inline(text)).Trim();
            // A ratio is the WHOLE formula: one division of one aggregation by another and nothing else around it (Sol 3.4). A growth
            // measure, DIVIDE ( [Sales], [Sales LY] ) - 1, returns 0.10 where Sales divided by Sales LY is 1.10, so saying it divides
            // one by the other is a false explanation of the number. With arithmetic around the division, or in either part, no ratio
            // is claimed at all.
            var divide = Divide.Match(text);
            (string, string)? parts = null;
            if (divide.Success)
            {
                if (divide.Index != 0) return null;
                var args = Args(text, divide.Index + divide.Length);
                if (args.Count >= 2 && text.Substring(divide.Index + divide.Length + args.Sum(a => a.Length) + args.Count).Trim().Length == 0) parts = (args[0], args[1]);
            }
            else parts = Slash(text);
            if (parts == null) return null;
            var (n, d) = parts.Value;
            return IsTerm(n) && IsTerm(d) && Aggregates(n) && Aggregates(d) ? ((string, string)?)(n.Trim(), d.Trim()) : null;
        }

        /// <summary>One term: no + - * / ^ outside brackets, names and quotes. "( [A] - [B] )" is not one, once its brackets are taken off.</summary>
        private static bool IsTerm(string part)
        {
            var t = Unwrap(part);
            var depth = 0;
            for (var i = 0; i < t.Length; i++)
            {
                var c = t[i];
                if (c == '[' || c == '\'') { var close = t.IndexOf(c == '[' ? ']' : '\'', i + 1); if (close < 0) return false; i = close; }
                else if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (depth == 0 && (c == '+' || c == '-' || c == '*' || c == '/' || c == '^')) return false;
            }
            return true;
        }

        private static readonly Regex VarHead = new Regex(@"\G\s*VAR\s+([A-Za-z_][\w.]*)\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Keyword = new Regex(@"\G(VAR|RETURN)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Guard = new Regex(@"^\s*(IF|IFERROR)\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Nothing = new Regex(@"^\s*(BLANK\s*\(\s*\)|-?\d+(\.\d+)?)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>"VAR r = [A] VAR s = [B] RETURN DIVIDE ( r, s )" as "DIVIDE ( ([A]), ([B]) )": each variable, in order, put back
        /// in brackets wherever a later definition or the RETURN names it. Text that is not one VAR block is returned as it came.</summary>
        private static string Inline(string text)
        {
            var vars = new List<(string Name, string Value)>();
            var at = 0;
            while (true)
            {
                var head = VarHead.Match(text, at);
                if (!head.Success) break;
                // The definition runs to the next VAR or RETURN at the top level, outside names in [...] and '...'.
                var depth = 0;
                var end = -1;
                for (var i = head.Index + head.Length; i < text.Length && end < 0; i++)
                {
                    var c = text[i];
                    if (c == '[' || c == '\'') { var close = text.IndexOf(c == '[' ? ']' : '\'', i + 1); if (close < 0) return text; i = close; }
                    else if (c == '(') depth++;
                    else if (c == ')') depth--;
                    else if (depth == 0 && (i == 0 || !char.IsLetterOrDigit(text[i - 1]) && text[i - 1] != '_') && Keyword.IsMatch(text, i)) end = i;
                }
                if (end < 0) return text;
                vars.Add((head.Groups[1].Value, Substitute(text.Substring(head.Index + head.Length, end - head.Index - head.Length).Trim(), vars)));
                at = end;
            }
            if (vars.Count == 0) return text;
            var ret = Keyword.Match(text, at);
            return ret.Success && ret.Value.Equals("RETURN", StringComparison.OrdinalIgnoreCase) ? Substitute(text.Substring(ret.Index + ret.Length), vars) : text;
        }

        /// <summary>Each variable name, as a whole word outside [...] and '...', replaced by its bracketed value.</summary>
        private static string Substitute(string text, List<(string Name, string Value)> vars)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < text.Length;)
            {
                var c = text[i];
                if (c == '[' || c == '\'')
                {
                    var close = text.IndexOf(c == '[' ? ']' : '\'', i + 1);
                    var stop = close < 0 ? text.Length : close + 1;
                    sb.Append(text, i, stop - i);
                    i = stop;
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    var j = i;
                    while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_' || text[j] == '.')) j++;
                    var word = text.Substring(i, j - i);
                    var hit = vars.LastOrDefault(v => string.Equals(v.Name, word, StringComparison.OrdinalIgnoreCase));
                    sb.Append(hit.Name != null ? "(" + hit.Value + ")" : word);
                    i = j;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>A guard around the division, read through: "IF ( [B] = 0, BLANK (), [A] / [B] )" and "IFERROR ( [A] / [B], BLANK () )"
        /// are "[A] / [B]". Only when the other branch is nothing (a blank or a number); a guard that picks between two figures is
        /// left as it is, and so is a call followed by more text. Brackets around the whole are dropped on the way.</summary>
        private static string Unguard(string text)
        {
            for (var guards = 0; guards < 8; guards++)
            {
                var t = Unwrap(text);
                var m = Guard.Match(t);
                if (!m.Success) return t;
                var args = Args(t, m.Index + m.Length);
                if (args.Count == 0 || t.Substring(m.Index + m.Length + args.Sum(a => a.Length) + args.Count).Trim().Length != 0) return t;
                string through = null;
                if (m.Groups[1].Value.Equals("IFERROR", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Count == 2 && Nothing.IsMatch(args[1])) through = args[0];
                }
                else if (args.Count == 3 && Nothing.IsMatch(args[1]) && !Nothing.IsMatch(args[2])) through = args[2];
                else if ((args.Count == 2 || args.Count == 3 && Nothing.IsMatch(args[2])) && !Nothing.IsMatch(args[1])) through = args[1];
                if (through == null) return t;
                text = through;
            }
            return text;
        }

        /// <summary>The text without brackets that enclose all of it.</summary>
        private static string Unwrap(string text)
        {
            var t = text.Trim();
            while (t.Length > 1 && t[0] == '(' && t[t.Length - 1] == ')')
            {
                var depth = 0;
                for (var i = 0; i < t.Length - 1; i++)
                {
                    if (t[i] == '(') depth++;
                    else if (t[i] == ')') depth--;
                    if (depth == 0) return t;
                }
                t = t.Substring(1, t.Length - 2).Trim();
            }
            return t;
        }

        /// <summary>The top-level arguments of a call whose opening bracket has just been read.</summary>
        private static List<string> Args(string text, int from)
        {
            var args = new List<string>();
            var depth = 1;
            var start = from;
            for (var i = from; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth == 0) { args.Add(text.Substring(start, i - start)); return args; } }
                else if (c == ',' && depth == 1) { args.Add(text.Substring(start, i - start)); start = i + 1; }
            }
            return new List<string>();
        }

        /// <summary>One top-level "/": what is before it and what is after it.</summary>
        private static (string, string)? Slash(string text)
        {
            var depth = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == '/' && depth == 0) return (text.Substring(0, i), text.Substring(i + 1));
            }
            return null;
        }

        /// <summary>A part that reads the model's data: a measure, or an aggregate of a column or a table. A number is not.</summary>
        private static bool Aggregates(string part) => MeasureRef.IsMatch(part) || Aggregator.IsMatch(part);

        /// <summary>A part named by the measures and columns it reads: "Returns Amount", "Net Sales Amount", "distinct count of
        /// Order ID", "rows of Store". Never a value.</summary>
        public static string Name(string part)
        {
            var measures = MeasureRef.Matches(part).Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
            if (measures.Count > 0) return JoinNames(measures);
            var reads = new List<string>();
            foreach (Match m in OverColumn.Matches(part))
            {
                var fn = m.Groups[1].Value.ToUpperInvariant();
                var column = Spaced(m.Groups[2].Value);
                reads.Add(fn.StartsWith("DISTINCTCOUNT", StringComparison.Ordinal) || fn.StartsWith("APPROXIMATE", StringComparison.Ordinal) ? "distinct count of " + column
                    : fn.StartsWith("COUNT", StringComparison.Ordinal) ? "count of " + column : column);
            }
            foreach (Match m in OverTable.Matches(part)) reads.Add("rows of " + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
            return reads.Count > 0 ? JoinNames(reads.Distinct().ToList()) : "rows of the model's data";
        }

        /// <summary>"NetSalesAmount" is "Net Sales Amount": the way a person reads a column name.</summary>
        internal static string Spaced(string name) => Regex.Replace(name ?? "", @"(?<=[a-z])(?=[A-Z])", " ");

        private static string JoinNames(IReadOnlyList<string> items) =>
            items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[items.Count - 1];
    }
}
