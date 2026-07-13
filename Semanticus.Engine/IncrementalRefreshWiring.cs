using System;
using System.Collections.Generic;
using System.Linq;

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
