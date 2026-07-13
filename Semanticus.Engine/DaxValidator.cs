using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>Conservative, offline DAX validation for validate_dax — the engine-side counterpart of the VS Code
    /// editor's daxLint.ts. Flags unbalanced () [] (error) and unknown table/column/measure references against the
    /// OPEN model (warning). Deliberately conservative so a "valid" verdict is trustworthy: strings/comments are
    /// masked first; unknown *functions* are NOT flagged (no exhaustive list at this layer); unknown *unquoted*
    /// Word[Col] tables are skipped (could be a VAR). No live engine needed — works on a file model.</summary>
    internal static class DaxValidator
    {
        public static DaxValidation Validate(Model model, string expression)
        {
            var hits = new List<(int offset, string sev, string msg)>();
            if (!string.IsNullOrWhiteSpace(expression))
            {
                Balance(Mask(expression, maskIdents: true), hits);
                References(Mask(expression, maskIdents: false), BuildIndex(model), hits);
            }
            var diags = hits.OrderBy(h => h.offset)
                .Select(h => { var (line, col) = LineCol(expression, h.offset); return new DaxDiagnostic { Severity = h.sev, Message = h.msg, Line = line, Column = col }; })
                .ToArray();
            return new DaxValidation { Valid = !diags.Any(d => d.Severity == "error"), Diagnostics = diags };
        }

        // Replace string literals ("…", "" escapes) and // -- /* */ comments with spaces (length + newlines kept,
        // so offsets stay valid). maskIdents also blanks '…' quoted-identifier interiors (for the balance pass).
        private static string Mask(string text, bool maskIdents)
        {
            var a = text.ToCharArray();
            int n = a.Length, i = 0;
            void Blank(int s, int e) { for (int k = s; k < e && k < n; k++) if (a[k] != '\n' && a[k] != '\r') a[k] = ' '; }
            while (i < n)
            {
                char c = text[i];
                if (c == '"') { int j = i + 1; while (j < n) { if (text[j] == '"') { if (j + 1 < n && text[j + 1] == '"') { j += 2; continue; } break; } j++; } Blank(i, j + 1); i = j + 1; continue; }
                if (c == '\'') { int j = i + 1; while (j < n) { if (text[j] == '\'') { if (j + 1 < n && text[j + 1] == '\'') { j += 2; continue; } break; } j++; } if (maskIdents) Blank(i, j + 1); i = j + 1; continue; }
                if (c == '/' && i + 1 < n && text[i + 1] == '/') { int j = i + 2; while (j < n && text[j] != '\n') j++; Blank(i, j); i = j; continue; }
                if (c == '-' && i + 1 < n && text[i + 1] == '-') { int j = i + 2; while (j < n && text[j] != '\n') j++; Blank(i, j); i = j; continue; }
                if (c == '/' && i + 1 < n && text[i + 1] == '*') { int j = i + 2; while (j < n && !(text[j] == '*' && j + 1 < n && text[j + 1] == '/')) j++; Blank(i, Math.Min(j + 2, n)); i = j + 2; continue; }
                i++;
            }
            return new string(a);
        }

        private static void Balance(string masked, List<(int, string, string)> hits)
        {
            var stack = new Stack<(char ch, int pos)>();
            for (int i = 0; i < masked.Length; i++)
            {
                char c = masked[i];
                if (c == '(' || c == '[') stack.Push((c, i));
                else if (c == ')' || c == ']')
                {
                    char want = c == ')' ? '(' : '[';
                    if (stack.Count == 0 || stack.Peek().ch != want) hits.Add((i, "error", $"Unmatched '{c}'."));
                    else stack.Pop();
                }
            }
            foreach (var s in stack) hits.Add((s.pos, "error", $"Unclosed '{s.ch}'."));
        }

        private sealed class Index
        {
            public HashSet<string> Tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> ColumnsByTable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Measures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AllColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static Index BuildIndex(Model m)
        {
            var idx = new Index();
            foreach (var t in m.Tables) idx.Tables.Add(t.Name);
            foreach (var c in m.AllColumns.Where(c => c.Type != ColumnType.RowNumber))
            {
                var tn = c.Table?.Name ?? "";
                if (!idx.ColumnsByTable.TryGetValue(tn, out var set)) idx.ColumnsByTable[tn] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(c.Name);
                idx.AllColumns.Add(c.Name);
            }
            foreach (var me in m.AllMeasures) idx.Measures.Add(me.Name);
            return idx;
        }

        private static readonly Regex ReTbl = new Regex(@"'((?:[^']|'')*)'(\s*\[([^\]]*)\])?", RegexOptions.Compiled);
        private static readonly Regex ReUnq = new Regex(@"(?<![\w.'\]])([A-Za-z_]\w*)\[([^\]]*)\]", RegexOptions.Compiled);
        private static readonly Regex ReBare = new Regex(@"(?<![\w.'\]])\[([^\]]*)\]", RegexOptions.Compiled);

        private static void References(string masked, Index idx, List<(int, string, string)> hits)
        {
            if (idx.Tables.Count == 0) return;   // no model loaded — balance-only
            foreach (Match m in ReTbl.Matches(masked))
            {
                var table = m.Groups[1].Value.Replace("''", "'");
                if (!idx.Tables.Contains(table)) { hits.Add((m.Index, "warning", $"Unknown table '{table}'.")); continue; }
                if (m.Groups[2].Success && m.Groups[3].Success && m.Groups[3].Value.Length > 0)
                {
                    var col = m.Groups[3].Value;
                    if (idx.ColumnsByTable.TryGetValue(table, out var cols) && !cols.Contains(col))
                        hits.Add((m.Groups[3].Index, "warning", $"Table '{table}' has no column '{col}'."));
                }
            }
            foreach (Match m in ReUnq.Matches(masked))
            {
                var table = m.Groups[1].Value;
                if (!idx.Tables.Contains(table)) continue;   // unknown unquoted → may be a VAR; skip
                var col = m.Groups[2].Value;
                if (col.Length > 0 && idx.ColumnsByTable.TryGetValue(table, out var cols) && !cols.Contains(col))
                    hits.Add((m.Groups[2].Index, "warning", $"Table '{table}' has no column '{col}'."));
            }
            foreach (Match m in ReBare.Matches(masked))
            {
                var name = m.Groups[1].Value;
                if (name.Length == 0) continue;
                if (!idx.Measures.Contains(name) && !idx.AllColumns.Contains(name))
                    hits.Add((m.Index, "warning", $"Unknown measure or column '[{name}]'."));
            }
        }

        private static (int line, int col) LineCol(string text, int offset)
        {
            int line = 1, last = -1;
            for (int i = 0; i < offset && i < text.Length; i++) if (text[i] == '\n') { line++; last = i; }
            return (line, offset - last);
        }
    }
}
