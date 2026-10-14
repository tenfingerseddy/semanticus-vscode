using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;

namespace Semanticus.Engine
{
    /// <summary>Conservative, offline DAX validation for validate_dax — the engine-side counterpart of the VS Code
    /// editor's daxLint.ts. Flags unbalanced () [] and a trailing operator (error), a small set of well-known
    /// function argument counts (warning), unknown table/column/measure references against the OPEN model (warning),
    /// and unknown function calls (warning). Strings/comments are masked first; an unknown *unquoted* Word[Col] table
    /// is skipped only when the expression itself declares Word (a VAR, or a parameter of a function body), and is an
    /// unknown table otherwise (F-263: skipping every unknown one let SUM ( Ghost[Amount] ) through every DAX write).
    /// No live engine needed. Works on a file model. RLS save refuses any diagnostic,
    /// so a warning here still blocks a row filter.</summary>
    internal static partial class DaxValidator
    {
        public static DaxValidation Validate(Model model, string expression) => Validate(model, expression, null);

        /// <summary>As Validate, but also treats <paramref name="pending"/> as present: the measures and calculated
        /// columns a copy batch has already accepted and will write in the same step (cherry_pick preview). A pending
        /// entry adds a name to an EXISTING table only; it never invents a table.</summary>
        public static DaxValidation Validate(Model model, string expression, IEnumerable<(string table, string name, bool isColumn)> pending)
        {
            var hits = new List<(int offset, string sev, string msg)>();
            if (!string.IsNullOrWhiteSpace(expression))
            {
                var balanced = Mask(expression, maskIdents: true);
                Balance(balanced, hits);
                // The incomplete check runs on a text where a string literal is a VALUE, not a blank: blanked,
                // "[Region] = \"North\"" ended in '=' + spaces and read as a dangling operator, so the textbook
                // row filter was refused by both doors (round-4 UAT, SEC-01/SEC-03/EDIT-02/JOURNEY-08).
                Incomplete(Mask(expression, maskIdents: true, stringFill: '.'), hits);
                // Quoted tables and string literals still occupy argument slots after masking.
                ArityCheck(Mask(expression, maskIdents: true, stringFill: '.', identFill: '.'), hits);
                var idx = BuildIndex(model);
                foreach (var (pt, pn, isColumn) in pending ?? Enumerable.Empty<(string, string, bool)>())
                {
                    if (string.IsNullOrEmpty(pn) || !idx.Tables.Contains(pt ?? "")) continue;
                    if (!isColumn) { idx.Measures.Add(pn); AddTo(idx.MeasuresByTable, pt, pn); continue; }
                    AddTo(idx.ColumnsByTable, pt, pn);
                    idx.AllColumns.Add(pn);
                }
                References(expression, idx, hits);
                Calls(balanced, idx, hits);
            }
            var diags = hits.OrderBy(h => h.offset)
                .Select(h => { var (line, col) = LineCol(expression, h.offset); return new DaxDiagnostic { Severity = h.sev, Message = h.msg, Line = line, Column = col }; })
                .ToArray();
            return new DaxValidation { Valid = !diags.Any(d => d.Severity == "error"), Diagnostics = diags };
        }

        // Replace string literals ("…", "" escapes) and // -- /* */ comments with spaces (length + newlines kept,
        // so offsets stay valid). maskIdents also blanks '…' quoted-identifier interiors (for the balance pass).
        // stringFill is the character a TERMINATED string literal is filled with: the balance/reference passes want
        // blanks (a literal is not code), the trailing-operator pass wants a value placeholder (see the call site).
        // The placeholder must be a NON-word char ('.'), or a literal abutting a trailing keyword would fuse with it
        // ("a"IN reads as one word and stops matching \bIN\b). An UNTERMINATED literal always blanks, so a broken
        // quote still reads as incomplete rather than as a value.
        private static string Mask(string text, bool maskIdents, char stringFill = ' ', char identFill = ' ')
        {
            var a = text.ToCharArray();
            int n = a.Length, i = 0;
            void Blank(int s, int e, char fill = ' ') { for (int k = s; k < e && k < n; k++) if (a[k] != '\n' && a[k] != '\r') a[k] = fill; }
            while (i < n)
            {
                char c = text[i];
                if (c == '"') { int j = i + 1; while (j < n) { if (text[j] == '"') { if (j + 1 < n && text[j + 1] == '"') { j += 2; continue; } break; } j++; } Blank(i, j + 1, j < n ? stringFill : ' '); i = j + 1; continue; }
                if (c == '\'') { int j = i + 1; while (j < n) { if (text[j] == '\'') { if (j + 1 < n && text[j + 1] == '\'') { j += 2; continue; } break; } j++; } if (maskIdents) Blank(i, j + 1, j < n ? identFill : ' '); i = j + 1; continue; }
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
            /// <summary>The measures each table holds: Table[Name] reads a measure of that table as well as a column of it.</summary>
            public Dictionary<string, HashSet<string>> MeasuresByTable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AllColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Functions = new HashSet<string>(KnownFunctions, StringComparer.OrdinalIgnoreCase);
            /// <summary>Tables whose columns come from a formula (field parameters among them): a name read through one is not judged, because the model may not list what the formula makes.</summary>
            public HashSet<string> CalculatedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Calendars: a quoted name may be a calendar, as in TOTALYTD ( [Sales], 'Fiscal Calendar' ).</summary>
            public HashSet<string> Calendars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Tables whose last column was deleted in this session: a column read through one is "no such column", where a table that never had columns is not judged.</summary>
            public HashSet<string> Emptied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // A table object keeps no history of its columns, so the delete path says so here. Weak: it never keeps a table alive, and a reopened model is a new table (not judged).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Table, object> EmptiedTables = new();

        /// <summary>Called by the delete path once a column's removal has left its table with no columns.</summary>
        public static void NoteEmptied(Table table) { if (table != null) EmptiedTables.AddOrUpdate(table, EmptiedTables); }

        private static void AddTo(Dictionary<string, HashSet<string>> map, string table, string name)
        {
            if (!map.TryGetValue(table, out var set)) map[table] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(name);
        }

        private static Index BuildIndex(Model m)
        {
            var idx = new Index();
            foreach (var t in m.Tables) { idx.Tables.Add(t.Name); if (EmptiedTables.TryGetValue(t, out _)) idx.Emptied.Add(t.Name); }
            foreach (var c in m.AllColumns.Where(c => c.Type != ColumnType.RowNumber))
            {
                AddTo(idx.ColumnsByTable, c.Table?.Name ?? "", c.Name);
                idx.AllColumns.Add(c.Name);
            }
            foreach (var me in m.AllMeasures)
            {
                idx.Measures.Add(me.Name); AddTo(idx.MeasuresByTable, me.Table?.Name ?? "", me.Name);
                // A KPI makes three measures of its own that DAX may call: _<measure> Goal, _<measure> Status and _<measure> Trend.
                if (me.KPI != null)
                    foreach (var part in new[] { "Goal", "Status", "Trend" })
                    {
                        var kpiName = "_" + me.Name + " " + part;
                        idx.Measures.Add(kpiName); AddTo(idx.MeasuresByTable, me.Table?.Name ?? "", kpiName);
                    }
            }
            foreach (var t in m.Tables.OfType<CalculatedTable>()) idx.CalculatedTables.Add(t.Name);
            foreach (var (_, cal) in CalendarOps.Enumerate(m)) idx.Calendars.Add(cal.Name);
            foreach (var f in m.Functions) idx.Functions.Add(f.Name);
            return idx;
        }

        /// <summary>A checker that answers, for any formula, which objects it names that the model does not have: an unknown table, a column the table does not have, a
        /// bracketed name that is neither a measure nor a column. These are the same reference checks validate_dax runs (<see cref="References"/>), with the model's name index
        /// built once, so a scan over every formula pays for it once. Function names, argument counts and brackets are left out: they are about how a formula is written, not
        /// about an object it points at. <see cref="References"/> reads the formula as tokens, so a bracketed name the formula makes itself (a "Net" it names in ADDCOLUMNS, the
        /// Value column of a table constructor or GENERATESERIES), a measure written with its table (Sales[Total]) and an apostrophe or ]] inside a [name] are never read as a missing object.
        /// The second argument is the table the formula is written in (null when unknown). A name this reader cannot judge with certainty is never reported: a wrong report is a wrong grade the person cannot clear.</summary>
        internal static Func<string, string, string[]> MissingObjectsChecker(Model model)
        {
            var idx = BuildIndex(model);
            return (expression, host) =>
            {
                if (string.IsNullOrWhiteSpace(expression)) return Array.Empty<string>();
                var hits = new List<(int offset, string sev, string msg)>();
                References(expression, idx, hits, host);
                if (hits.Count == 0) return Array.Empty<string>();
                return hits.OrderBy(h => h.offset).Select(h => h.msg).Distinct(StringComparer.Ordinal).ToArray();
            };
        }

        /// <summary>The tables an expression names, for the composer's refs_within check (composer contract 1.7): a
        /// quoted 'Table', an unquoted Table[Column] whose Table is a model table, a bare word that is a table name
        /// (COUNTROWS ( Sales )), and the host table of a bare [Measure]. A bare [Column] with no table names no
        /// table, and a quoted name that is not in the model is still returned, so an unknown table never reads as
        /// inside the allowed set. Strings and comments are masked first, as in Validate.</summary>
        internal static IReadOnlyCollection<string> TablesReferenced(Model model, string expression)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(expression)) return found;
            var idx = BuildIndex(model);
            var masked = Mask(expression, maskIdents: false);
            foreach (Match m in ReTbl.Matches(masked)) found.Add(m.Groups[1].Value.Replace("''", "'"));
            foreach (Match m in ReUnq.Matches(masked))
                if (idx.Tables.Contains(m.Groups[1].Value)) found.Add(m.Groups[1].Value);
            // Bare words: only a whole word that is a table and is not a call or a Table[Column] already read above.
            var words = ReBracket.Replace(Mask(expression, maskIdents: true), b => new string(' ', b.Length));   // [Return rate] holds no table word
            foreach (Match m in ReWord.Matches(words))
                if (idx.Tables.Contains(m.Groups[1].Value)) found.Add(m.Groups[1].Value);
            foreach (Match m in ReBare.Matches(masked))
            {
                var name = m.Groups[1].Value;
                var host = model.AllMeasures.FirstOrDefault(me => string.Equals(me.Name, name, StringComparison.OrdinalIgnoreCase))?.Table?.Name;
                if (host != null) found.Add(host);
            }
            // Map each hit to the model's own spelling where there is one, so the caller compares like with like.
            return found.Select(t => model.Tables.FirstOrDefault(x => string.Equals(x.Name, t, StringComparison.OrdinalIgnoreCase))?.Name ?? t)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>A DAX function name the offline validator knows. Used by the composer's no_formula_text check.</summary>
        internal static bool IsKnownFunction(string name) => name != null && KnownFunctions.Contains(name);

        private static readonly Regex ReBracket = new Regex(@"\[[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex ReWord = new Regex(@"(?<![\w.'\]\[])([A-Za-z_]\w*)(?!\s*[\[(\w])", RegexOptions.Compiled);
        private static readonly Regex ReTbl = new Regex(@"'((?:[^']|'')*)'(\s*\[([^\]]*)\])?", RegexOptions.Compiled);
        private static readonly Regex ReUnq = new Regex(@"(?<![\w.'\]])([A-Za-z_]\w*)\[([^\]]*)\]", RegexOptions.Compiled);
        private static readonly Regex ReBare = new Regex(@"(?<![\w.'\]])\[([^\]]*)\]", RegexOptions.Compiled);
        private static readonly Regex ReCall = new Regex(@"\b([A-Za-z_][\w.]*)\s*\(", RegexOptions.Compiled);
        private static readonly Regex ReTrail = new Regex(@"(?:<>|<=|>=|&&|\|\||[=<>+\-*/&]|\bIN\b|\bNOT\b)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);


        /// <summary>The names the expression declares for itself: every VAR, and the parameters of a function body
        /// "( t : TABLE, n ) =&gt; ...". Only these may stand unquoted where a table would; any other unknown name is an
        /// unknown table.</summary>
        private static HashSet<string> Declared(IReadOnlyList<DaxScan.Tok> toks)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var k = 0; k + 1 < toks.Count; k++)
                if (toks[k].Kind == DaxScan.Kind.Word && string.Equals(toks[k].Text, "VAR", StringComparison.OrdinalIgnoreCase) && toks[k + 1].Kind == DaxScan.Kind.Word)
                    names.Add(toks[k + 1].Text);
            if (toks.Count > 0 && toks[0].Kind == DaxScan.Kind.LParen)
            {
                var close = DaxScan.MatchingRParen(toks, 0);
                if (close > 0 && close + 2 < toks.Count && toks[close + 1].Kind == DaxScan.Kind.Op && toks[close + 1].Text == "=" && toks[close + 2].Kind == DaxScan.Kind.Op && toks[close + 2].Text == ">")
                    for (var k = 1; k < close; k++)
                        if (toks[k].Kind == DaxScan.Kind.Word && (toks[k - 1].Kind == DaxScan.Kind.LParen || toks[k - 1].Kind == DaxScan.Kind.Comma) && toks[k].Depth == 1)
                            names.Add(toks[k].Text);
            }
            return names;
        }

        private static readonly Regex ReValueColumn = new Regex(@"^Value\d*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The columns a formula makes for itself, which a bracketed name may mean without the model having them: every string the formula writes (the "Net" of ADDCOLUMNS ( t, "Net", ... ), the
        /// column names of DATATABLE and ROW), the Value, Value1, Value2... columns of a table constructor { ... } and of GENERATESERIES, and the Date column of CALENDAR and CALENDARAUTO. A constructor is the braces
        /// that are not the list of an IN and not the rows of a DATATABLE, whose columns carry the names the formula gives them.</summary>
        private static HashSet<string> OwnColumns(IReadOnlyList<DaxScan.Tok> toks)
        {
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dataTables = DaxScan.AllCallOpens(toks, "DATATABLE").Select(o => (open: o, close: DaxScan.MatchingRParen(toks, o))).ToList();
            var valueColumns = DaxScan.CallsFunction(toks, "GENERATESERIES");
            for (var k = 0; k < toks.Count; k++)
            {
                var t = toks[k];
                if (t.Kind == DaxScan.Kind.String) own.Add(t.Inner);
                else if (t.Kind == DaxScan.Kind.LBrace && !(k > 0 && toks[k - 1].Kind == DaxScan.Kind.Word && string.Equals(toks[k - 1].Text, "IN", StringComparison.OrdinalIgnoreCase))
                         && !dataTables.Any(d => d.close < 0 ? k > d.open : k > d.open && k < d.close))
                    valueColumns = true;
            }
            if (valueColumns) { own.Add("Value"); for (var n = 1; n <= 64; n++) own.Add("Value" + n); }
            if (DaxScan.CallsFunction(toks, "CALENDAR") || DaxScan.CallsFunction(toks, "CALENDARAUTO")) own.Add("Date");
            return own;
        }

        // Read as tokens, not as text: a quote, a bracket and a string each have one reading, so an apostrophe inside [Owner's Total] or inside "..." is not a table quote, ]] inside a [name] is a ] of the name,
        // and Table[Name] is a pair of tokens that touch.
        //
        // WHAT IS JUDGED (the shape table). Only three shapes are judged; anything else is unsure and never reported, because a report here caps the grade and the person cannot clear it:
        //   'X' or 'X'[..]   X is a table or a calendar.                                         (a VAR cannot be quoted, so a quoted name is always a model object)
        //   T[x]             T is a table that is not a calculated table: x is a column or a measure of T, or a KPI measure of T. T not a table and not declared by the formula: unknown table.
        //   [x]              only when every call in the formula has a known shape and no calculated table is named by or hosts the formula: x is a measure, a KPI measure, a column of any table
        //                    or a column the formula makes for itself.
        // The lookup rules are TE2's (FormulaFixup resolves the same names); its walk is not used, because it stops at the first RETURN.
        private static void References(string expression, Index idx, List<(int, string, string)> hits, string host = null)
        {
            if (idx.Tables.Count == 0) return;   // no model loaded — balance-only
            var toks = DaxScan.Tokenize(expression);
            var declared = Declared(toks);
            var own = OwnColumns(toks);
            var bareJudged = BareNamesJudged(toks, idx, host);
            for (var k = 0; k < toks.Count; k++)
            {
                var t = toks[k];
                var quoted = t.Kind == DaxScan.Kind.Name && t.Delim == '\'';
                var word = t.Kind == DaxScan.Kind.Word && !NotATable.Contains(t.Text);
                // A table written with a column or measure after it: 'Table' [Name] (a space is allowed) or Table[Name] (it must touch).
                var next = k + 1 < toks.Count ? toks[k + 1] : null;
                var bracket = next != null && next.Kind == DaxScan.Kind.Name && next.Delim == '[' && (quoted || next.Start == t.End) ? next : null;
                if (word && bracket == null) continue;
                if (quoted || word)
                {
                    var table = t.Kind == DaxScan.Kind.Name ? t.Inner : t.Text;
                    if (bracket != null) k++;
                    if (!idx.Tables.Contains(table))
                    {
                        if (idx.Calendars.Contains(table)) continue;   // a calendar, by its name
                        // A name the expression declares may stand here; anything else is a table the model does not have.
                        if (quoted || !declared.Contains(table)) hits.Add((t.Start, "warning", $"Unknown table '{table}'."));
                        continue;
                    }
                    if (idx.CalculatedTables.Contains(table)) continue;   // its columns come from its formula: not judged
                    var col = bracket?.Inner;
                    if (string.IsNullOrEmpty(col)) continue;
                    // Table[Name] is a column of the table or a measure of it.
                    // A table with no columns has no entry at all. Two cases: its last column was deleted in this session, which is "no such column" (judged);
                    // or it never had columns (a calculated table not yet calculated, a table just created, a live table not yet loaded), which is "not judged".
                    var hasColumns = idx.ColumnsByTable.TryGetValue(table, out var cols);
                    if (!hasColumns && !idx.Emptied.Contains(table)) continue;
                    if (!(hasColumns && cols.Contains(col)) && !(idx.MeasuresByTable.TryGetValue(table, out var ms) && ms.Contains(col)))
                        hits.Add((bracket.Start + 1, "warning", $"Table '{table}' has no column '{col}'."));
                    continue;
                }
                if (t.Kind == DaxScan.Kind.Name && t.Delim == '[')
                {
                    var name = t.Inner;
                    if (name.Length == 0 || !bareJudged) continue;
                    // [Date].[Year]: a level of a date variation, which the model does not list as a column.
                    if (k > 0 && toks[k - 1].Kind == DaxScan.Kind.Other && toks[k - 1].Text == ".") continue;
                    if (!idx.Measures.Contains(name) && !idx.AllColumns.Contains(name) && !own.Contains(name))
                        hits.Add((t.Start, "warning", $"Unknown measure or column '[{name}]'."));
                }
            }
        }

        // Functions whose result has columns this reader cannot list from the formula, or that it has no shape for.
        private static readonly HashSet<string> NoKnownShape = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LINEST", "LINESTX", "COLUMNSTATISTICS", "DETAILROWS" };

        /// <summary>Can a bare [name] be judged in this formula? Only when every call in it is a function whose shape is known (KnownFunctions less those with columns of their own that the formula
        /// does not list, and less the INFO.* functions), no calculated table is named by it, and it is not written in a calculated table. A model function (UDF) or an unknown function can return any
        /// columns, so a formula that calls one is not judged by its bare names. The test is on the fixed list, not on the model's functions.</summary>
        private static bool BareNamesJudged(IReadOnlyList<DaxScan.Tok> toks, Index idx, string host)
        {
            if (host != null && idx.CalculatedTables.Contains(host)) return false;
            for (var k = 0; k < toks.Count; k++)
            {
                var t = toks[k];
                if (t.Kind == DaxScan.Kind.Name && t.Delim == '\'') { if (idx.CalculatedTables.Contains(t.Inner)) return false; continue; }
                if (t.Kind != DaxScan.Kind.Word) continue;
                if (idx.CalculatedTables.Contains(t.Text)) return false;
                if (k + 1 >= toks.Count || toks[k + 1].Kind != DaxScan.Kind.LParen || NotATable.Contains(t.Text)) continue;
                // A call: read its whole dotted name (STDEV.P, INFO.VIEW.TABLES).
                var name = t.Text;
                for (var j = k; j >= 2 && toks[j - 1].Kind == DaxScan.Kind.Other && toks[j - 1].Text == "." && toks[j - 2].Kind == DaxScan.Kind.Word; j -= 2) name = toks[j - 2].Text + "." + name;
                if (!KnownFunctions.Contains(name) || NoKnownShape.Contains(name) || name.StartsWith("INFO.", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        // Words that can stand before a bracketed name without being a table.
        private static readonly HashSet<string> NotATable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "RETURN", "NOT", "IN", "VAR", "ASC", "DESC", "AND", "OR" };

        /// <summary>Flags an expression that ENDS in a binary operator with no right-hand operand. Runs on text
        /// whose string literals are placeholders, not blanks, so a comparison to a literal is an operand
        /// ("[Region] = \"North\"" is complete; "[Region] =" is not).</summary>
        private static void Incomplete(string masked, List<(int, string, string)> hits)
        {
            var m = ReTrail.Match(masked);
            if (m.Success) hits.Add((m.Index, "error", "This expression is incomplete."));
        }

        private static void Calls(string masked, Index idx, List<(int, string, string)> hits)
        {
            foreach (Match m in ReCall.Matches(masked))
            {
                var name = m.Groups[1].Value;
                if (idx.Functions.Contains(name)) continue;
                hits.Add((m.Index, "warning", $"Unknown function '{name}'."));
            }
        }

        // Conservative argument-count table: only functions with a well-known min/max, so a miss stays silent.
        static readonly Dictionary<string, (int Min, int Max)> KnownArity = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["SUMX"] = (2, 2), ["AVERAGEX"] = (2, 2), ["MINX"] = (2, 2), ["MAXX"] = (2, 2),
            ["COUNTX"] = (2, 2), ["COUNTAX"] = (2, 2), ["PRODUCTX"] = (2, 2), ["FILTER"] = (2, 2),
            ["SUM"] = (1, 1), ["AVERAGE"] = (1, 1), ["MIN"] = (1, 1), ["MAX"] = (1, 1),
            ["COUNT"] = (1, 1), ["COUNTA"] = (1, 1), ["COUNTROWS"] = (1, 1),
            ["RELATED"] = (1, 1), ["RELATEDTABLE"] = (1, 1), ["VALUES"] = (1, 1), ["DISTINCT"] = (1, 1),
            ["DIVIDE"] = (2, 3), ["IF"] = (2, 3), ["SELECTEDVALUE"] = (1, 2),
            ["USERELATIONSHIP"] = (2, 2), ["ISBLANK"] = (1, 1), ["HASONEVALUE"] = (1, 1),
            ["SELECTEDMEASURE"] = (0, 0), ["SELECTEDMEASUREFORMATSTRING"] = (0, 0), ["BLANK"] = (0, 0),
        };

        private static void ArityCheck(string masked, List<(int, string, string)> hits)
        {
            foreach (Match m in ReCall.Matches(masked))
            {
                var name = m.Groups[1].Value;
                if (!KnownArity.TryGetValue(name, out var spec)) continue;
                var open = m.Index + m.Length - 1;
                var (count, end) = CountArgs(masked, open);
                if (end < 0) continue;
                if (count >= spec.Min && count <= spec.Max) continue;
                var need = spec.Min == spec.Max ? spec.Min.ToString() : spec.Min + " to " + spec.Max;
                var noun = spec.Min == 1 && spec.Max == 1 ? "argument" : "arguments";
                hits.Add((m.Index, "warning", name + " needs " + need + " " + noun + ", not " + count + "."));
            }
        }

        private static (int count, int end) CountArgs(string s, int openParen)
        {
            var depth = 0;
            var count = 0;
            var any = false;
            for (var i = openParen; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '(') { depth++; continue; }
                if (c == ')')
                {
                    depth--;
                    if (depth == 0) return (any ? count : 0, i);
                    continue;
                }
                if (c == ',' && depth == 1) { count++; continue; }
                if (depth == 1 && !char.IsWhiteSpace(c) && !any) { any = true; count = 1; }
            }
            return (0, -1);
        }

        private static (int line, int col) LineCol(string text, int offset)
        {
            int line = 1, last = -1;
            for (int i = 0; i < offset && i < text.Length; i++) if (text[i] == '\n') { line++; last = i; }
            return (line, offset - last);
        }

        // Keep in step with Semanticus.VSCode/webview/src/dax.ts DAX_FUNCTIONS. Dotted names (BETA.DIST, IF.EAGER)
        // are the forms authors type; undotted TE2 token names are not needed because the call scanner reads source.
        private static readonly HashSet<string> KnownFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "APPROXIMATEDISTINCTCOUNT", "AVERAGE", "AVERAGEA", "AVERAGEX", "COUNT", "COUNTA", "COUNTAX", "COUNTBLANK", "COUNTROWS", "COUNTX", "DISTINCTCOUNT", "DISTINCTCOUNTNOBLANK", "MAX", "MAXA", "MAXX", "MIN", "MINA", "MINX", "PRODUCT", "PRODUCTX", "SUM", "SUMX",
            "CALENDAR", "CALENDARAUTO", "DATE", "DATEDIFF", "DATEVALUE", "DAY", "EDATE", "EOMONTH", "HOUR", "MINUTE", "MONTH", "NETWORKDAYS", "NOW", "QUARTER", "SECOND", "TIME", "TIMEVALUE", "TODAY", "UTCNOW", "UTCTODAY", "WEEKDAY", "WEEKNUM", "YEAR", "YEARFRAC",
            "CLOSINGBALANCEMONTH", "CLOSINGBALANCEQUARTER", "CLOSINGBALANCEYEAR", "DATEADD", "DATESBETWEEN", "DATESINPERIOD", "DATESMTD", "DATESQTD", "DATESYTD", "ENDOFMONTH", "ENDOFQUARTER", "ENDOFYEAR", "FIRSTDATE", "FIRSTNONBLANK", "LASTDATE", "LASTNONBLANK", "NEXTDAY", "NEXTMONTH", "NEXTQUARTER", "NEXTYEAR", "OPENINGBALANCEMONTH", "OPENINGBALANCEQUARTER", "OPENINGBALANCEYEAR", "PARALLELPERIOD", "PREVIOUSDAY", "PREVIOUSMONTH", "PREVIOUSQUARTER", "PREVIOUSYEAR", "SAMEPERIODLASTYEAR", "STARTOFMONTH", "STARTOFQUARTER", "STARTOFYEAR", "TOTALMTD", "TOTALQTD", "TOTALYTD",
            "ALL", "ALLCROSSFILTERED", "ALLEXCEPT", "ALLNOBLANKROW", "ALLSELECTED", "CALCULATE", "CALCULATETABLE", "EARLIER", "EARLIEST", "FILTER", "KEEPFILTERS", "LOOKUPVALUE", "REMOVEFILTERS", "SELECTEDVALUE",
            "ACCRINT", "ACCRINTM", "AMORDEGRC", "AMORLINC", "COUPDAYBS", "COUPDAYS", "COUPDAYSNC", "COUPNCD", "COUPNUM", "COUPPCD", "CUMIPMT", "CUMPRINC", "DB", "DDB", "DISC", "DOLLARDE", "DOLLARFR", "DURATION", "EFFECT", "FV", "INTRATE", "IPMT", "ISPMT", "MDURATION", "NOMINAL", "NPER", "ODDFPRICE", "ODDFYIELD", "ODDLPRICE", "ODDLYIELD", "PDURATION", "PMT", "PPMT", "PRICE", "PRICEDISC", "PRICEMAT", "PV", "RATE", "RECEIVED", "RRI", "SLN", "SYD", "TBILLEQ", "TBILLPRICE", "TBILLYIELD", "VDB", "XIRR", "XNPV", "YIELD", "YIELDDISC", "YIELDMAT",
            "COLUMNSTATISTICS", "CONTAINS", "CONTAINSROW", "CONTAINSSTRING", "CONTAINSSTRINGEXACT", "CUSTOMDATA", "HASONEFILTER", "HASONEVALUE", "ISAFTER", "ISBLANK", "ISCROSSFILTERED", "ISEMPTY", "ISERROR", "ISEVEN", "ISFILTERED", "ISINSCOPE", "ISLOGICAL", "ISNONTEXT", "ISNUMBER", "ISODD", "ISONORAFTER", "ISSELECTEDMEASURE", "ISSUBTOTAL", "ISTEXT", "SELECTEDMEASURE", "SELECTEDMEASUREFORMATSTRING", "SELECTEDMEASURENAME", "USERCULTURE", "USERNAME", "USEROBJECTID", "USERPRINCIPALNAME", "INFO.VIEW.COLUMNS", "INFO.VIEW.MEASURES", "INFO.VIEW.RELATIONSHIPS", "INFO.VIEW.TABLES",
            "AND", "BITAND", "BITLSHIFT", "BITOR", "BITRSHIFT", "BITXOR", "COALESCE", "FALSE", "IF", "IF.EAGER", "IFERROR", "NOT", "OR", "SWITCH", "TRUE",
            "ABS", "ACOS", "ACOSH", "ACOT", "ACOTH", "ASIN", "ASINH", "ATAN", "ATANH", "CEILING", "CONVERT", "COS", "COSH", "COT", "COTH", "CURRENCY", "DEGREES", "DIVIDE", "EVEN", "EXP", "FACT", "FLOOR", "GCD", "INT", "ISO.CEILING", "LCM", "LN", "LOG", "LOG10", "MOD", "MROUND", "ODD", "PI", "POWER", "QUOTIENT", "RADIANS", "RAND", "RANDBETWEEN", "ROUND", "ROUNDDOWN", "ROUNDUP", "SIGN", "SIN", "SINH", "SQRT", "SQRTPI", "TAN", "TANH", "TRUNC",
            "PATH", "PATHCONTAINS", "PATHITEM", "PATHITEMREVERSE", "PATHLENGTH",
            "CROSSFILTER", "RELATED", "RELATEDTABLE", "USERELATIONSHIP",
            "BETA.DIST", "BETA.INV", "CHISQ.DIST", "CHISQ.DIST.RT", "CHISQ.INV", "CHISQ.INV.RT", "COMBIN", "COMBINA", "CONFIDENCE.NORM", "CONFIDENCE.T", "EXPON.DIST", "GEOMEAN", "GEOMEANX", "LINEST", "LINESTX", "MEDIAN", "MEDIANX", "NORM.DIST", "NORM.INV", "NORM.S.DIST", "NORM.S.INV", "PERCENTILE.EXC", "PERCENTILE.INC", "PERCENTILEX.EXC", "PERCENTILEX.INC", "POISSON.DIST", "RANK.EQ", "RANKX", "SAMPLE", "STDEV.P", "STDEV.S", "STDEVX.P", "STDEVX.S", "T.DIST", "T.DIST.2T", "T.DIST.RT", "T.INV", "T.INV.2T", "VAR.P", "VAR.S", "VARX.P", "VARX.S",
            "ADDCOLUMNS", "ADDMISSINGITEMS", "CROSSJOIN", "CURRENTGROUP", "DATATABLE", "DETAILROWS", "DISTINCT", "EXCEPT", "FILTERS", "GENERATE", "GENERATEALL", "GENERATESERIES", "GROUPBY", "IGNORE", "INTERSECT", "NATURALINNERJOIN", "NATURALLEFTOUTERJOIN", "ROLLUP", "ROLLUPADDISSUBTOTAL", "ROLLUPGROUP", "ROLLUPISSUBTOTAL", "ROW", "SELECTCOLUMNS", "SUMMARIZE", "SUMMARIZECOLUMNS", "TOPN", "TREATAS", "UNION", "VALUES",
            "COMBINEVALUES", "CONCATENATE", "CONCATENATEX", "EXACT", "FIND", "FIXED", "FORMAT", "LEFT", "LEN", "LOWER", "MID", "REPLACE", "REPT", "RIGHT", "SEARCH", "SUBSTITUTE", "TRIM", "UNICHAR", "UNICODE", "UPPER", "VALUE",
            "INDEX", "MATCHBY", "OFFSET", "ORDERBY", "PARTITIONBY", "RANK", "ROWNUMBER", "WINDOW",
            "BLANK", "ERROR", "EVALUATEANDLOG", "TOCSV", "TOJSON",
        };
    }
}
