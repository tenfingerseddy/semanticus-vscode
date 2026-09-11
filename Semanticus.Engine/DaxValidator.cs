using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>Conservative, offline DAX validation for validate_dax — the engine-side counterpart of the VS Code
    /// editor's daxLint.ts. Flags unbalanced () [] and a trailing operator (error), a small set of well-known
    /// function argument counts (warning), unknown table/column/measure references against the OPEN model (warning),
    /// and unknown function calls (warning). Strings/comments are masked first; unknown *unquoted* Word[Col] tables
    /// are skipped (could be a VAR). No live engine needed. Works on a file model. RLS save refuses any diagnostic,
    /// so a warning here still blocks a row filter.</summary>
    internal static class DaxValidator
    {
        public static DaxValidation Validate(Model model, string expression)
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
                References(Mask(expression, maskIdents: false), idx, hits);
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
            public HashSet<string> AllColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Functions = new HashSet<string>(KnownFunctions, StringComparer.OrdinalIgnoreCase);
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
            foreach (var f in m.Functions) idx.Functions.Add(f.Name);
            return idx;
        }

        private static readonly Regex ReTbl = new Regex(@"'((?:[^']|'')*)'(\s*\[([^\]]*)\])?", RegexOptions.Compiled);
        private static readonly Regex ReUnq = new Regex(@"(?<![\w.'\]])([A-Za-z_]\w*)\[([^\]]*)\]", RegexOptions.Compiled);
        private static readonly Regex ReBare = new Regex(@"(?<![\w.'\]])\[([^\]]*)\]", RegexOptions.Compiled);
        private static readonly Regex ReCall = new Regex(@"\b([A-Za-z_][\w.]*)\s*\(", RegexOptions.Compiled);
        private static readonly Regex ReTrail = new Regex(@"(?:<>|<=|>=|&&|\|\||[=<>+\-*/&]|\bIN\b|\bNOT\b)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
