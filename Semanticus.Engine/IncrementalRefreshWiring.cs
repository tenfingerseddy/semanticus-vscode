using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Semanticus.Engine
{
    /// <summary>The engine-side twin of the webview appendStep transform. The engine cannot execute the
    /// browser TypeScript, so this keeps the MCP door self-contained while preserving the same append-only
    /// outer-let behavior. It deliberately understands only the shape it changes.</summary>
    internal static class IncrementalRefreshWiring
    {
        private sealed class Step
        {
            public string Name { get; set; }
            public int End { get; set; }
        }

        private sealed class Shape
        {
            public List<Step> Steps { get; } = new List<Step>();
            public int ResultStart { get; set; }
            public int ResultEnd { get; set; }
            public string Result { get; set; }
        }

        public static string AppendRangeFilter(string m, string dateColumn)
        {
            if (string.IsNullOrWhiteSpace(m)) throw new InvalidOperationException("The M partition has no source expression to filter.");
            if (string.IsNullOrWhiteSpace(dateColumn)) throw new InvalidOperationException("Choose a date column before adding the RangeStart/RangeEnd filter.");

            var source = m.Trim();
            if (!TryShape(source, out var shape))
            {
                // A bare source is valid partition M. Give append-step semantics an explicit binding rather than
                // replacing the expression or asking the analyst to hand-author a let-chain.
                source = "let\n    Source = " + source + "\nin\n    Source";
                if (!TryShape(source, out shape))
                    throw new InvalidOperationException("The partition M could not be shaped as one let/in expression, so no range filter was added.");
            }

            var stepRef = QuoteIdent(UniqueName("Filtered for incremental refresh", shape.Steps));
            var field = "[" + dateColumn.Replace("]", "]]", StringComparison.Ordinal) + "]";
            var last = shape.Steps[shape.Steps.Count - 1];
            var bareRef = shape.Steps.FirstOrDefault(x => ReferenceForms(x.Name).Contains(shape.Result.Trim(), StringComparer.Ordinal));
            if (bareRef != null)
            {
                // The in-clause is a bare reference to an existing step: filter THAT step and repoint `in`.
                var expression = $"Table.SelectRows({QuoteIdent(bareRef.Name)}, each {field} >= RangeStart and {field} < RangeEnd)";
                return source.Substring(0, last.End)
                    + ",\n    " + stepRef + " = " + expression
                    + source.Substring(last.End, shape.ResultStart - last.End)
                    + stepRef + source.Substring(shape.ResultEnd);
            }
            // The in-clause is a COMPUTED expression (e.g. Table.Combine({A, B})): materialize it as its own binding
            // first, then filter that, so the expression is never silently discarded and the filter covers the real
            // final table (not just the last step).
            var modelRef = QuoteIdent(UniqueName("Model for incremental refresh", shape.Steps));
            var filterExpr = $"Table.SelectRows({modelRef}, each {field} >= RangeStart and {field} < RangeEnd)";
            return source.Substring(0, last.End)
                + ",\n    " + modelRef + " = " + shape.Result
                + ",\n    " + stepRef + " = " + filterExpr
                + source.Substring(last.End, shape.ResultStart - last.End)
                + stepRef + source.Substring(shape.ResultEnd);
        }

        // [field] (>=|>|<=|<) RangeStart|RangeEnd  and the param-on-the-left mirror. Field content bans a nested
        // '[' (a field access never opens another bracket) and un-escapes ']]' after capture.
        private static readonly Regex ReFieldOpParam = new Regex(@"\[((?:\]\]|[^\[\]])*)\]\s*(?:<=|<|>=|>)\s*(RangeStart|RangeEnd)\b", RegexOptions.Compiled);
        private static readonly Regex ReParamOpField = new Regex(@"\b(RangeStart|RangeEnd)\s*(?:<=|<|>=|>)\s*\[((?:\]\]|[^\[\]])*)\]", RegexOptions.Compiled);

        /// <summary>Parse the SOURCE field the partition's range filter compares — with parse CONFIDENCE strong
        /// enough to drive a hard rejection. A field only counts when BOTH RangeStart and RangeEnd bound the SAME
        /// field inside the SAME predicate expression (an `each` or `=>` lambda body; top level otherwise), and
        /// exactly ONE distinct pair-field exists in the whole expression. Anything else — a mixed-field pair,
        /// half comparisons (an unrelated nested query bounding another table's field), several competing pairs,
        /// or a compared field written as a quoted identifier ([#"Order Date"] — masked before matching, so
        /// decoding it here would mean a second escape grammar; UNKNOWN is safe under the tier table, a wrong
        /// field is not) — is UNKNOWN (returns false): the caller must not reject on suspicion. Comments and
        /// string literals are masked first, so a mention in either can neither convict nor pair.</summary>
        public static bool TryParseRangeField(string m, out string field)
        {
            field = null;
            if (string.IsNullOrWhiteSpace(m)) return false;
            var code = MaskNonCode(m);
            var seg = SegmentIds(code);
            var comps = new List<(string Field, string Param, int Seg)>();
            // A compared field whose captured text was MASKED (differs from the original at the same span)
            // contained a quoted identifier: stand down entirely rather than pair a name we cannot read.
            bool Masked(Group g) => !string.Equals(g.Value, m.Substring(g.Index, g.Length), StringComparison.Ordinal);
            foreach (Match x in ReFieldOpParam.Matches(code))
            {
                if (Masked(x.Groups[1])) return false;
                comps.Add((x.Groups[1].Value.Replace("]]", "]", StringComparison.Ordinal), x.Groups[2].Value, seg[x.Index]));
            }
            foreach (Match x in ReParamOpField.Matches(code))
            {
                if (Masked(x.Groups[2])) return false;
                comps.Add((x.Groups[2].Value.Replace("]]", "]", StringComparison.Ordinal), x.Groups[1].Value, seg[x.Index]));
            }
            var pairFields = comps.GroupBy(c => (c.Seg, c.Field))
                .Where(g => g.Any(c => c.Param == "RangeStart") && g.Any(c => c.Param == "RangeEnd"))
                .Select(g => g.Key.Field).Distinct().ToList();
            if (pairFields.Count != 1) return false;
            field = pairFields[0];
            return true;
        }

        // Comments, string literals, and quoted identifiers become spaces (positions preserved), so the
        // comparison regexes, the segment scanner, AND the range-filter prerequisite detector
        // (LocalEngine.MFiltersOnRange shares this) only ever see live code — a range mention inside a string
        // can neither satisfy the prerequisite nor pair a field.
        internal static string MaskNonCode(string s)
        {
            var buf = s.ToCharArray();
            var i = 0;
            while (i < s.Length)
            {
                var start = i;
                if (SkipNonCode(s, ref i)) { for (var j = start; j < i && j < buf.Length; j++) buf[j] = ' '; continue; }
                i++;
            }
            return new string(buf);
        }

        /// <summary>A predicate-segment id per character: every lambda opener — the `each` keyword or a `=>`
        /// arrow — opens a segment scoped to its enclosing bracket depth. A segment pops when that depth closes
        /// OR at a comma at the same depth (the lambda extends only to the end of its argument — without the
        /// comma-pop a following comparison in the NEXT argument would falsely share the lambda's segment).
        /// Characters outside any lambda share segment 0. Bracketed field accesses / records with ']]' escapes
        /// and no nested '[' are consumed atomically so an escaped ']' can neither corrupt the depth nor pop a
        /// live segment.</summary>
        private static int[] SegmentIds(string code)
        {
            var ids = new int[code.Length];
            var stack = new Stack<(int Id, int Depth)>();
            var next = 1;
            var depth = 0;
            var i = 0;
            int Cur() => stack.Count > 0 ? stack.Peek().Id : 0;
            while (i < code.Length)
            {
                var c = code[i];
                if (c == '[')
                {
                    var j = i + 1;
                    while (j < code.Length)
                    {
                        if (code[j] == '[') { j = -1; break; }   // nested bracket: not atomic, fall through
                        if (code[j] == ']')
                        {
                            if (j + 1 < code.Length && code[j + 1] == ']') { j += 2; continue; }   // ]] escape
                            break;
                        }
                        j++;
                    }
                    if (j > 0 && j < code.Length)
                    {
                        for (var k = i; k <= j; k++) ids[k] = Cur();
                        i = j + 1;
                        continue;
                    }
                }
                if (c == ',')
                {
                    while (stack.Count > 0 && stack.Peek().Depth >= depth) stack.Pop();
                    ids[i] = Cur();
                    i++;
                    continue;
                }
                if (c == '=' && i + 1 < code.Length && code[i + 1] == '>')
                {
                    stack.Push((next++, depth));
                    ids[i] = Cur();
                    ids[i + 1] = Cur();
                    i += 2;
                    continue;
                }
                if (c == '(' || c == '{' || c == '[') { ids[i] = Cur(); depth++; i++; continue; }
                if (c == ')' || c == '}' || c == ']')
                {
                    depth--;
                    while (stack.Count > 0 && depth < stack.Peek().Depth) stack.Pop();
                    ids[i] = Cur();
                    i++;
                    continue;
                }
                if (IsIdentStart(c))
                {
                    var end = IdentEnd(code, i);
                    if (end - i == 4 && string.CompareOrdinal(code, i, "each", 0, 4) == 0) stack.Push((next++, depth));
                    for (var k = i; k < end; k++) ids[k] = Cur();
                    i = end;
                    continue;
                }
                ids[i] = Cur();
                i++;
            }
            return ids;
        }

        private static bool TryShape(string source, out Shape shape)
        {
            shape = null;
            var i = 0;
            var depth = 0;
            var letFound = false;
            while (i < source.Length)
            {
                if (SkipNonCode(source, ref i)) continue;
                var c = source[i];
                if ("([{".IndexOf(c) >= 0) { depth++; i++; continue; }
                if (")]}".IndexOf(c) >= 0) { depth--; i++; continue; }
                if (depth == 0 && IsIdentStart(c))
                {
                    var end = IdentEnd(source, i);
                    if (source.Substring(i, end - i) == "let") { i = end; letFound = true; break; }
                    i = end; continue;
                }
                i++;
            }
            if (!letFound) return false;

            var result = new Shape();
            var segmentStart = i;
            depth = 0;
            while (i < source.Length)
            {
                if (SkipNonCode(source, ref i)) continue;
                var c = source[i];
                if ("([{".IndexOf(c) >= 0) { depth++; i++; continue; }
                if (")]}".IndexOf(c) >= 0) { depth--; i++; continue; }
                if (depth == 0)
                {
                    if (c == ',')
                    {
                        if (!FlushStep(source, segmentStart, i, result.Steps)) return false;
                        i++; segmentStart = i; continue;
                    }
                    if (IsIdentStart(c))
                    {
                        var end = IdentEnd(source, i);
                        if (source.Substring(i, end - i) == "in")
                        {
                            if (!FlushStep(source, segmentStart, i, result.Steps) || result.Steps.Count == 0) return false;
                            var a = end;
                            var b = source.Length;
                            while (a < b && char.IsWhiteSpace(source[a])) a++;
                            while (b > a && char.IsWhiteSpace(source[b - 1])) b--;
                            if (a == b) return false;
                            result.ResultStart = a;
                            result.ResultEnd = b;
                            result.Result = source.Substring(a, b - a);
                            shape = result;
                            return true;
                        }
                        i = end; continue;
                    }
                }
                i++;
            }
            return false;
        }

        private static bool FlushStep(string source, int start, int end, ICollection<Step> steps)
        {
            var raw = source.Substring(start, end - start);
            var i = 0;
            var depth = 0;
            var equals = -1;
            while (i < raw.Length)
            {
                if (SkipNonCode(raw, ref i)) continue;
                var c = raw[i];
                if ("([{".IndexOf(c) >= 0) depth++;
                else if (")]}".IndexOf(c) >= 0) depth--;
                else if (depth == 0 && c == '=' && Peek(raw, i + 1) != '=' && Peek(raw, i + 1) != '>' && Peek(raw, i - 1) != '<' && Peek(raw, i - 1) != '>')
                { equals = i; break; }
                i++;
            }
            if (equals <= 0) return false;
            var nameRaw = raw.Substring(0, equals).Trim();
            if (nameRaw.Length == 0) return false;
            var name = nameRaw.StartsWith("#\"", StringComparison.Ordinal) && nameRaw.EndsWith("\"", StringComparison.Ordinal)
                ? nameRaw.Substring(2, nameRaw.Length - 3).Replace("\"\"", "\"", StringComparison.Ordinal)
                : nameRaw;
            steps.Add(new Step { Name = name, End = LastCodeEnd(source, start, end) });
            return true;
        }

        // The end of a step's CODE within [start,end): past trailing whitespace AND any trailing // or /* */ comment,
        // so an inserted separator comma lands after the value expression and is never swallowed by a trailing comment
        // (which would leave two record bindings with no separator = invalid M).
        private static int LastCodeEnd(string source, int start, int end)
        {
            int j = start, last = start;
            while (j < end)
            {
                var c = source[j];
                if (c == '"' || (c == '#' && Peek(source, j + 1) == '"')) { SkipNonCode(source, ref j); if (j > end) j = end; last = j; continue; }
                if (c == '/' && (Peek(source, j + 1) == '/' || Peek(source, j + 1) == '*')) { SkipNonCode(source, ref j); if (j > end) j = end; continue; }
                if (!char.IsWhiteSpace(c)) last = j + 1;
                j++;
            }
            return last;
        }

        private static string UniqueName(string baseName, IEnumerable<Step> steps)
        {
            var name = baseName;
            for (var suffix = 1; steps.Any(x => string.Equals(x.Name, name, StringComparison.Ordinal)); suffix++)
                name = baseName + suffix;
            return name;
        }

        private static bool SkipNonCode(string source, ref int i)
        {
            if (i >= source.Length) return false;
            if (source[i] == '"' || (source[i] == '#' && Peek(source, i + 1) == '"'))
            {
                i += source[i] == '#' ? 2 : 1;
                while (i < source.Length)
                {
                    if (source[i] != '"') { i++; continue; }
                    if (Peek(source, i + 1) == '"') { i += 2; continue; }
                    i++; break;
                }
                return true;
            }
            if (source[i] == '/' && Peek(source, i + 1) == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                return true;
            }
            if (source[i] == '/' && Peek(source, i + 1) == '*')
            {
                i += 2;
                while (i < source.Length && !(source[i] == '*' && Peek(source, i + 1) == '/')) i++;
                if (i < source.Length) i += 2;
                return true;
            }
            return false;
        }

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';
        private static int IdentEnd(string source, int i) { while (i < source.Length && IsIdentChar(source[i])) i++; return i; }
        private static char Peek(string source, int i) => i >= 0 && i < source.Length ? source[i] : '\0';

        private static string QuoteIdent(string name)
        {
            var plain = name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_');
            return plain ? name : "#\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        private static IEnumerable<string> ReferenceForms(string name)
        {
            yield return "#\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            if (name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_'))
                yield return name;
        }
    }
}
