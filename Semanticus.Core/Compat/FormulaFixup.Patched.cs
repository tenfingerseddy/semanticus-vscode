// ---------------------------------------------------------------------------------------------------
// THIRD-PARTY FILE. This is not first-party Semanticus work.
//
// Upstream project: Tabular Editor 2
// Author/copyright: Copyright (c) Tabular Editor ApS
// License:          MIT
// Upstream URL:     https://github.com/TabularEditor/TabularEditor
// Upstream path:    TOMWrapper/Utils/FormulaFixup.cs
//
// Copied from the vendored Tabular Editor 2 submodule at external/TabularEditor, pinned at
// 7029129aa3f45d35f987d8f6ac7e5a971f28771c. Local changes, each marked "Semanticus:" in the body and
// otherwise byte-identical to upstream so a later diff stays small: (1) a keyword that is never a table
// (RETURN, NOT, IN, VAR, ASC, DESC, AND, OR, in any case) is no longer read as the table of a bracket
// that follows it; (2) a reference to a table the model does not have skips that one reference instead of
// ending the walk of the whole formula, so every reference after it is still found and a rename still
// rewrites it; (3) the full rebuild of the dependency tree now includes each measure's KPI, which upstream
// never visited, so a rename rewrites the KPI's status, target and trend expressions; (4) a bare [column] inside a row-scan function (SUMX, FILTER, ADDCOLUMNS ...) is read
// from the table that function scans, but only in the arguments that function evaluates per row (one table, RowScanSpecs, each row cited from its Microsoft Learn page: PERCENTILEX scans only its expression, CONCATENATEX its expression and order-by expressions, TOPN, SAMPLE and TOPNSKIP their order-by expressions; any other argument is read in the caller's context, CALCULATETABLE opens no row scope), searching open scans from the innermost out and then the measure's home table;
// a scan of a table the walk cannot know binds only a column name that is unique in the model and the home table's, and leaves any other name alone;
// (5) a KPI's generated helpers [_<name> Goal], [_<name> Status] and [_<name> Trend] are rewritten when the base measure is renamed, at the occurrences the walk bound to the helper and nowhere else (a column of the same name is left alone); (6) a row scan over FILTER, ALL, CALCULATETABLE, TOPN, SAMPLE or TOPNSKIP of one plain table name reads the row of that table. (7) a VAR name is visible only in its own VAR ... RETURN block, so an inner VAR that reuses an outer name hides it inside the inner block alone, and a block ends when the expression it stands in ends (the next sibling VAR, the enclosing RETURN, a closing bracket or an argument comma), giving back what it hid; (8) a VAR name is visible only in the expressions that follow its declaration, so a repeated name inside its own initializer is still the outer one. MIT requires the copyright notice AND this permission notice to travel with the file:
//
//   MIT License
//
//   Copyright (c) 2025 Tabular Editor ApS
//
//   Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
//   associated documentation files (the "Software"), to deal in the Software without restriction,
//   including without limitation the rights to use, copy, modify, merge, publish, distribute,
//   sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
//   furnished to do so, subject to the following conditions:
//
//   The above copyright notice and this permission notice shall be included in all copies or substantial
//   portions of the Software.
//
//   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
//   LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
//   IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//   LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
//   WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// ---------------------------------------------------------------------------------------------------

using Antlr4.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TabularEditor.TextServices;
using TabularEditor.TOMWrapper.Undo;

namespace TabularEditor.TOMWrapper.Utils
{
    internal static class FormulaFixup
    {
        /// <summary>
        /// Changes all references to object "obj", to reflect the new name of the object.
        /// </summary>
        /// <param name="obj"></param>
        public static void DoFixup(IDaxObject obj, bool rebuildImmediately = false)
        {
            var helperUses = KpiHelperUsesOf(obj); // Semanticus: taken first, as the rebuild below re-reads every formula under the new name
            foreach (var d in obj.ReferencedBy.ToList())
            {
                d.DependsOn.UpdateRef(obj);
                if (rebuildImmediately) BuildDependencyTree(d, true);
            }
            RewriteKpiHelperUses(obj, helperUses); // Semanticus: [_<old> Goal] follows the base measure's new name
        }

        private static TabularModelHandler Handler { get { return TabularModelHandler.Singleton; } }
        private static Model Model { get { return TabularModelHandler.Singleton.Model; } }

        /// <summary>
        /// This method can be called to obtain a DependsOnList for any DAX expression. This is useful when the
        /// user is currently editing the DAX expression of an object, but the expression has not been saved to
        /// the object.
        /// </summary>
        public static DependsOnList GetDependencies(IDaxDependantObject expressionObj, string dax, DAXProperty prop)
        {
            var dependsOn = new DependsOnList(null);
            ParseExpression(dax.Replace("\r", ""), expressionObj, prop, dependsOn);
            return dependsOn;
        }

        private static void ParseExpression(string dax, IDaxDependantObject expressionObj, DAXProperty prop, DependsOnList dependsOn = null)
        {
            var lexer = new DAXLexer(new DAXCharStream(dax, false));
            lexer.RemoveErrorListeners();
            var tokens = lexer.GetDefaultTokens().ToList();

            IToken lastTableRef = null;
            int startTableIndex = 0;
            var scopes = new List<ScanScope>();                                   // Semanticus: the open parentheses and braces, innermost last
            var variables = new Dictionary<string, Table>(StringComparer.OrdinalIgnoreCase); // Semanticus: names a VAR declares (which may shadow a table), each with the one table its expression stands for, or null
            var blocks = new List<VarBlock>();                                     // Semanticus: the open VAR ... RETURN blocks, innermost last: each restores what its names hid when its RETURN expression ends
            var nameOrdinal = -1;                                                  // Semanticus: how many [bracketed names] the walk has passed in this formula (the helper's binding is kept by it)

            for (var i = 0; i < tokens.Count; i++)
            {
                var tok = tokens[i];
                TrackScopes(dax, tokens, i, scopes, variables, blocks); // Semanticus

                var isTableOrVariableRef = tok.Type == DAXLexer.TABLE
                    || tok.Type == DAXLexer.TABLE_OR_VARIABLE
                    || (tok.Channel == DAXLexer.KEYWORD_CHANNEL && i < tokens.Count - 1 && tokens[i + 1].Type != DAXLexer.OPEN_PARENS
                        && !IsNeverATable(tok)); // Semanticus: RETURN [x], NOT [x], VAR x ... are not a table before a bracket

                if (isTableOrVariableRef)
                {
                    if (i < tokens.Count - 1 && tokens[i + 1].Type == DAXLexer.OPEN_PARENS)
                    {
                        // function call
                        if (Model.Functions.FindByName(tok.Text) is Function f)
                        {
                            // user-defined function
                            if (dependsOn != null) dependsOn.Add(f, DAXProperty.Expression, tok.StartIndex, tok.StopIndex, false);
                            else expressionObj.AddDep(f, DAXProperty.Expression, tok.StartIndex, tok.StopIndex, false);
                        }
                    }
                    else
                    {
                        if (i < tokens.Count - 1 && tokens[i + 1].Type == DAXLexer.COLUMN_OR_MEASURE)
                        {
                            // Keep the token reference, as the next token should be column (fully qualified).
                            lastTableRef = tok;
                            startTableIndex = tok.StartIndex;
                        }
                        else
                        {
                            // Table referenced directly, don't save the reference for the next token.
                            lastTableRef = null;
                        }

                        var tableOrCalendar = Model.FindTableOrCalendar(tok.Text);
                        if (tableOrCalendar is Table table)
                        {
                            if (dependsOn != null)
                                dependsOn.Add(table, prop, tok.StartIndex, tok.StopIndex, true);
                            else
                                expressionObj.AddDep(table, prop, tok.StartIndex, tok.StopIndex, true);
                        }
                        else if (tableOrCalendar is Calendar calendar)
                        {
                            if (dependsOn != null)
                                dependsOn.Add(calendar, prop, tok.StartIndex, tok.StopIndex, true);
                            else
                                expressionObj.AddDep(calendar, prop, tok.StartIndex, tok.StopIndex, true);
                        }
                        else
                        {
                            // Invalid reference (no table with that name) or possibly a variable or function ref
                        }
                    }
                }
                else if (tok.Type == DAXLexer.COLUMN_OR_MEASURE)
                {
                    nameOrdinal++;
                    // Referencing a table just before the object reference
                    if (lastTableRef != null)
                    {
                        var tableName = lastTableRef.Text;
                        lastTableRef = null;
                        if (!Model.Tables.Contains(tableName)) continue; // Invalid reference (no table with that name). Semanticus: skip this one reference; upstream returned here and dropped every reference after it

                        var table = Model.Tables[tableName];
                        // Referencing a column on a specific table
                        if (table.Columns.Contains(tok.Text))
                        {
                            if (dependsOn != null) dependsOn.Add(table.Columns[tok.Text], prop, startTableIndex, tok.StopIndex, true);
                            else expressionObj.AddDep(table.Columns[tok.Text], prop, startTableIndex, tok.StopIndex, true);
                        }
                        // Referencing a measure on a specific table
                        else if (table.Measures.Contains(tok.Text))
                        {
                            if (dependsOn != null) dependsOn.Add(table.Measures[tok.Text], prop, startTableIndex, tok.StopIndex, true);
                            else expressionObj.AddDep(table.Measures[tok.Text], prop, startTableIndex, tok.StopIndex, true);
                        }
                        // Semanticus: a generated KPI helper of a measure on that table
                        else if (dependsOn == null) NoteKpiHelper(expressionObj, new[] { table }, tok.Text, prop, nameOrdinal);
                    }
                    // No table reference before the object reference
                    else
                    {
                        var table = (expressionObj as ITabularTableObject)?.Table ?? (expressionObj as TablePermission)?.Table;
                        // Semanticus: inside a row scan the row is the scanned table's. Open scans are searched from the innermost out, then the home table. A scan whose
                        // table cannot be known (a variable, a table expression) binds a column name only when the home table is the one table in the model that has it and no
                        // measure shares it (the usual scan of a table expression over its own table); any other name it might supply is left alone (no dependency, a rename does not touch it).
                        var preferred = table;
                        Column bound = null;
                        var unresolved = false;
                        foreach (var scan in OpenRowScans(scopes))
                        {
                            if (scan.GroupNames != null)
                            {
                                // Semanticus: SUMMARIZE's expression row holds the groupBy columns only; any other bare name is the caller's, so look further out
                                if (!scan.GroupNames.TryGetValue(tok.Text, out var qualifier)) continue;
                                if (qualifier != null && qualifier != scan.Scanned)
                                {
                                    if (qualifier.Columns.Contains(tok.Text)) { bound = qualifier.Columns[tok.Text]; break; }
                                    continue;
                                }
                            }
                            if (scan.Scanned != null)
                            {
                                if (preferred == table) preferred = scan.Scanned;
                                if (scan.Scanned.Columns.Contains(tok.Text)) { bound = scan.Scanned.Columns[tok.Text]; break; }
                                continue;
                            }
                            var owners = Model.Tables.Where(t => t.Columns.Contains(tok.Text)).Take(2).ToList();
                            if (owners.Count == 0) continue;
                            if (owners.Count == 1 && owners[0] == table && !Model.Tables.Any(t => t.Measures.Contains(tok.Text))) bound = owners[0].Columns[tok.Text];
                            else unresolved = true;
                            break;
                        }
                        if (unresolved) continue;
                        if (bound == null && table != null && table.Columns.Contains(tok.Text)) bound = table.Columns[tok.Text];
                        // Referencing a column without specifying a table
                        if (bound != null)
                        {
                            if (dependsOn != null) dependsOn.Add(bound, prop, tok.StartIndex, tok.StopIndex, false);
                            else expressionObj.AddDep(bound, prop, tok.StartIndex, tok.StopIndex, false);
                        }
                        // Referencing a measure without specifying a table
                        else
                        {
                            Measure m = null;
                            if (table != null && table.Measures.Contains(tok.Text)) m = table.Measures[tok.Text];
                            else
                                m = Model.Tables.FirstOrDefault(t => t.Measures.Contains(tok.Text))?.Measures[tok.Text];

                            if (m != null)
                            {
                                if (dependsOn != null) dependsOn.Add(m, prop, tok.StartIndex, tok.StopIndex, false);
                                else expressionObj.AddDep(m, prop, tok.StartIndex, tok.StopIndex, false);
                            }
                            // Semanticus: a generated KPI helper of a measure on any table
                            else if (dependsOn == null) NoteKpiHelper(expressionObj, preferred != null ? new[] { preferred }.Concat(Model.Tables) : Model.Tables, tok.Text, prop, nameOrdinal);
                        }
                    }
                }
                else
                {
                    lastTableRef = null;
                }
            }
        }

        // Semanticus: the keywords that stand before a bracket or a name without being a table (the lexer matches in any case).
        private static bool IsNeverATable(IToken tok)
        {
            switch (tok.Type)
            {
                case DAXLexer.RETURN:
                case DAXLexer.NOT:
                case DAXLexer.IN:
                case DAXLexer.VAR:
                case DAXLexer.ASC:
                case DAXLexer.DESC:
                case DAXLexer.AND:
                case DAXLexer.OR:
                    return true;
                default:
                    return false;
            }
        }

        // ---- Semanticus: row-scan scopes ----------------------------------------------------------------------------------------------
        // Microsoft's DAX syntax reference: inside a function that scans a table, a bare [Column] is read from the current row of THAT table.
        // Upstream read it from the measure's home table, so renaming Metrics[Revenue] rewrote SUMX ( Sales, [Revenue] ), which binds to Sales' column.
        private sealed class ScanScope
        {
            public bool RowScan;    // a function that scans a table
            public Table Scanned;   // the table its table argument names, or null when that argument is anything but one table (a variable, a table expression)
            public int Arg;         // the argument the walk is in
            public Func<int, bool> IsRowArg; // which arguments the function evaluates in the scanned row (RowScanSpecs); every other argument is evaluated in the caller's context
            public Dictionary<string, Table> GroupNames; // SUMMARIZE only: the columns its groupBy arguments name (the table that qualifies each, or null), the only bare names its expression row holds
            public bool IsGroupBy;  // GROUPBY: not a scan itself, but the table its CURRENTGROUP () stands for is GroupTable
            public Table GroupTable;
            public bool InRows => RowScan && IsRowArg(Arg);
        }

        // ONE table: for each row-scan function, the position (from 0) of its table argument and which arguments are evaluated in the row of that table. Every argument that is
        // not (the table itself, a scalar, a sort direction, a name) is evaluated in the CALLER's context, so a bare [name] there is read against the enclosing scan or the home table.
        // Each row is taken from the function's Microsoft Learn page (https://learn.microsoft.com/en-us/dax/<function>-function-dax):
        //   SUMX, AVERAGEX, COUNTX, COUNTAX, PRODUCTX, MEDIANX, GEOMEANX, STDEVX.S/.P, VARX.S/.P ( table, expression ): "expression: the expression to be evaluated for each row of the table".
        //   MINX, MAXX ( table, expression [, variant] ): only expression is per row; variant is a TRUE/FALSE switch, read in the caller's context.
        //   PERCENTILEX.INC, PERCENTILEX.EXC ( table, expression, k ): only expression is per row; k, "the desired percentile value in the range 0 to 1", is a scalar of the caller.
        //   RANKX ( table, expression [, value [, order [, ties]]] ): expression "is evaluated for each row of table"; value is "whose rank is to be found", the caller's value (order and ties are scalars).
        //   CONCATENATEX ( table, expression [, delimiter [, orderBy_expression [, order]]...] ): expression and each orderBy_expression "is evaluated for each row of table"; the delimiter and each order are not.
        //   TOPN ( n, table, orderBy_expression [, order [, orderBy_expression [, order]]...] ), SAMPLE ( n, table, orderBy_expression ... ), TOPNSKIP ( n, skip, table, orderBy_expression ... ):
        //       each orderBy_expression is "evaluated for each row of table"; n and skip are scalars of the caller (the page says "evaluated multiple times (for each row/context)", which names no row of table).
        //   FILTER ( table, filter ): filter "is to be evaluated for each row of the table".
        //   ADDCOLUMNS ( table, name, expression ... ), SELECTCOLUMNS ( table, name, expression ... ): each expression is "evaluated for each row of table"; a name is a quoted string (it holds no reference), so every argument after the table is read as a row one.
        //   GENERATE, GENERATEALL ( table1, table2 ): table2 is evaluated "for the current row in table1" (the Remarks).
        //   SUMMARIZE ( table, groupBy_columnName ..., name, expression ... ): one row per group (https://learn.microsoft.com/en-us/dax/summarize-function-dax; dax.guide/summarize; SQLBI "All the secrets
        //       of SUMMARIZE": the expression's row context holds "the cluster headers"). So a bare [name] in an expression is a column only when a groupBy argument names it (Rede-DSBR/DocPBI2,
        //       PNP_Publicada_dev: SUMMARIZE ( dimCurso, .., 'dimCurso'[fatorEsforcoCurso], "x", [m] * [fatorEsforcoCurso] ) reads that groupBy column bare); any other bare name is read in the caller's scope.
        //       Names are strings and groupBy columns are qualified, so every argument after the table is read as a row one (GroupNames filters it).
        //   GROUPBY ( table, groupBy_columnName ..., name, expression ... ): not a scan. Its expression must be an X aggregator over CURRENTGROUP (), "the set of rows from the table argument of GROUPBY"
        //       (https://learn.microsoft.com/en-us/dax/groupby-function-dax, https://learn.microsoft.com/en-us/dax/currentgroup-function-dax), so that scan's table is GROUPBY's table argument.
        //   CALCULATETABLE ( expression [, filter1 ...] ): the filters are Boolean or table filters evaluated in the caller's filter context, never in a row of the table expression: not a row scan.
        // Position rules below count arguments from 0.
        private struct RowScanSpec
        {
            public int TableArg;
            public Func<int, bool> IsRowArg;
            public RowScanSpec(int tableArg, Func<int, bool> isRowArg) { TableArg = tableArg; IsRowArg = isRowArg; }
        }

        private static readonly Func<int, bool> OnlyArg1 = a => a == 1;
        private static readonly Func<int, bool> EveryArgAfterTable = a => a >= 1;

        private static readonly Dictionary<string, RowScanSpec> RowScanSpecs = new Dictionary<string, RowScanSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["SUMX"] = new RowScanSpec(0, OnlyArg1), ["AVERAGEX"] = new RowScanSpec(0, OnlyArg1), ["MINX"] = new RowScanSpec(0, OnlyArg1), ["MAXX"] = new RowScanSpec(0, OnlyArg1),
            ["COUNTX"] = new RowScanSpec(0, OnlyArg1), ["COUNTAX"] = new RowScanSpec(0, OnlyArg1), ["PRODUCTX"] = new RowScanSpec(0, OnlyArg1), ["MEDIANX"] = new RowScanSpec(0, OnlyArg1),
            ["GEOMEANX"] = new RowScanSpec(0, OnlyArg1), ["STDEVX.S"] = new RowScanSpec(0, OnlyArg1), ["STDEVX.P"] = new RowScanSpec(0, OnlyArg1),
            ["VARX.S"] = new RowScanSpec(0, OnlyArg1), ["VARX.P"] = new RowScanSpec(0, OnlyArg1), ["PERCENTILEX.INC"] = new RowScanSpec(0, OnlyArg1), ["PERCENTILEX.EXC"] = new RowScanSpec(0, OnlyArg1),
            ["RANKX"] = new RowScanSpec(0, OnlyArg1),
            ["CONCATENATEX"] = new RowScanSpec(0, a => a == 1 || (a >= 3 && a % 2 == 1)),
            ["TOPN"] = new RowScanSpec(1, a => a >= 2 && a % 2 == 0), ["SAMPLE"] = new RowScanSpec(1, a => a >= 2 && a % 2 == 0), ["TOPNSKIP"] = new RowScanSpec(2, a => a >= 3 && a % 2 == 1),
            ["FILTER"] = new RowScanSpec(0, OnlyArg1),
            ["ADDCOLUMNS"] = new RowScanSpec(0, EveryArgAfterTable), ["SELECTCOLUMNS"] = new RowScanSpec(0, EveryArgAfterTable),
            ["GENERATE"] = new RowScanSpec(0, OnlyArg1), ["GENERATEALL"] = new RowScanSpec(0, OnlyArg1), ["SUMMARIZE"] = new RowScanSpec(0, EveryArgAfterTable),
        };

        // A VAR name is visible from its declaration to the end of its block, the RETURN expression (Microsoft's VAR page; SQLBI, "When are variables evaluated in DAX?": an inner VAR of the same
        // name hides the outer one only inside the inner block). Upstream has no variables at all; one flat map would let an inner block's name replace the outer table for the rest of the formula.
        private sealed class VarBlock
        {
            public int Depth;                 // scopes.Count where the block's VAR keywords stand
            public bool Returned;             // its RETURN has been seen: what follows is the block's result expression
            public (string Name, Table Held)? Pending;   // the VAR whose initializer is being walked: it becomes visible only when that expression ends (the next VAR or RETURN of this block)
            public readonly List<(string Name, bool Had, Table Held)> Hid = new List<(string, bool, Table)>();
        }

        // A VAR is visible only in the expressions that FOLLOW its declaration (dax.guide, dax.guide/st/var: "visible in all the expressionN+1 of following variables"; Microsoft's VAR page):
        // inside its own initializer a repeated name is still the outer one. So the name is recorded at the VAR keyword and made visible here, when the initializer ends.
        private static void ShowPending(VarBlock block, Dictionary<string, Table> variables)
        {
            if (block.Pending is { } p) variables[p.Name] = p.Held;
            block.Pending = null;
        }

        private static void EndTopBlock(List<VarBlock> blocks, Dictionary<string, Table> variables)
        {
            var b = blocks[blocks.Count - 1];
            blocks.RemoveAt(blocks.Count - 1);
            for (var h = b.Hid.Count - 1; h >= 0; h--)
            {
                if (b.Hid[h].Had) variables[b.Hid[h].Name] = b.Hid[h].Held; else variables.Remove(b.Hid[h].Name);
            }
        }

        // Semanticus: VAR scope, one end rule. A VAR's block (VAR ... RETURN) is visible only inside the expression it stands in (Microsoft, "VAR" page, https://learn.microsoft.com/en-us/dax/var-dax; dax.guide, dax.guide/st/var),
        // so it ends when that expression ends: at the next VAR or RETURN of the block that declares it, or at a closing bracket or argument comma (EndBlocksAt). Every completed block above the declaring one ends then, each giving back what it hid.
        private static VarBlock EndInnerBlocks(List<VarBlock> blocks, int depth, Dictionary<string, Table> variables)
        {
            var owner = blocks.FindLastIndex(b => b.Depth == depth && !b.Returned);
            if (owner < 0) return null;
            while (blocks.Count - 1 > owner) EndTopBlock(blocks, variables);
            return blocks[owner];
        }

        private static void EndBlocksAt(List<VarBlock> blocks, int depth, Dictionary<string, Table> variables)
        {
            while (blocks.Count > 0 && blocks[blocks.Count - 1].Depth >= depth) EndTopBlock(blocks, variables);
        }

        private static void TrackScopes(string dax, List<IToken> tokens, int i, List<ScanScope> scopes, Dictionary<string, Table> variables, List<VarBlock> blocks)
        {
            var tok = tokens[i];
            switch (tok.Type)
            {
                case DAXLexer.OPEN_PARENS:
                    scopes.Add(OpenScope(dax, tokens, i, variables, scopes));
                    break;
                case DAXLexer.OPEN_CURLY:
                    scopes.Add(new ScanScope());
                    break;
                case DAXLexer.CLOSE_PARENS:
                case DAXLexer.CLOSE_CURLY:
                    EndBlocksAt(blocks, scopes.Count, variables);   // a block opened inside these brackets ends with them
                    if (scopes.Count > 0) scopes.RemoveAt(scopes.Count - 1);
                    break;
                case DAXLexer.COMMA:
                    EndBlocksAt(blocks, scopes.Count, variables);   // and at the argument it stands in
                    if (scopes.Count > 0) scopes[scopes.Count - 1].Arg++;
                    break;
                case DAXLexer.RETURN:
                {
                    var declaring = EndInnerBlocks(blocks, scopes.Count, variables);   // a RETURN ends the declaring block's last initializer, then opens its result
                    if (declaring != null) { ShowPending(declaring, variables); declaring.Returned = true; }
                    break;
                }
                case DAXLexer.VAR:
                {
                    // A VAR after another declaration's expression is a sibling in the same block (that expression's own blocks end first); a VAR straight after "=" or RETURN (or in a fresh bracket) opens a block of its own.
                    var after = i > 0 ? tokens[i - 1] : null;
                    var afterExpression = after != null && after.Text != "=" && after.Type != DAXLexer.RETURN;
                    var owner = afterExpression ? EndInnerBlocks(blocks, scopes.Count, variables) : null;
                    if (owner != null) ShowPending(owner, variables);   // the previous VAR's initializer ended here
                    else blocks.Add(new VarBlock { Depth = scopes.Count });
                    if (i < tokens.Count - 1)
                    {
                        var name = tokens[i + 1].Text;
                        var held = i + 3 < tokens.Count && tokens[i + 2].Text == "=" ? TableAt(tokens, i + 3, variables, scopes) : null;   // VAR name = expression: a scan of the name reads the table the expression stands for, as the same expression inline would (null when it is not one plain table)
                        var top = blocks[blocks.Count - 1];
                        if (!top.Hid.Exists(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))) top.Hid.Add((name, variables.TryGetValue(name, out var prior), prior));
                        top.Pending = (name, held);   // visible when this initializer ends, not inside it
                    }
                    break;
                }
            }
        }

        private static ScanScope OpenScope(string dax, List<IToken> tokens, int i, Dictionary<string, Table> variables, List<ScanScope> scopes)
        {
            var scope = new ScanScope();
            // The name sits before the parenthesis; read it from the text, since the lexer has no token for the dot of STDEVX.S.
            var end = tokens[i].StartIndex;
            while (end > 0 && char.IsWhiteSpace(dax[end - 1])) end--;
            var start = end;
            while (start > 0 && (char.IsLetterOrDigit(dax[start - 1]) || dax[start - 1] == '_' || dax[start - 1] == '.')) start--;
            var name = dax.Substring(start, end - start);
            if (string.Equals(name, "GROUPBY", StringComparison.OrdinalIgnoreCase))
            {
                scope.IsGroupBy = true;
                scope.GroupTable = TableOfArgument(tokens, i, 0, variables, scopes);
                return scope;
            }
            if (!RowScanSpecs.TryGetValue(name, out var spec)) return scope;

            scope.RowScan = true;
            scope.IsRowArg = spec.IsRowArg;
            scope.Scanned = TableOfArgument(tokens, i, spec.TableArg, variables, scopes);
            if (string.Equals(name, "SUMMARIZE", StringComparison.OrdinalIgnoreCase)) scope.GroupNames = GroupByNames(tokens, i);
            return scope;
        }

        /// <summary>The columns named by the groupBy arguments of the SUMMARIZE whose parenthesis is tokens[open]: every bracketed name between the table argument and the first name/expression pair
        /// (a string), with the table that qualifies it (null when bare). ROLLUP and ROLLUPGROUP arguments count, as they group by their columns.</summary>
        private static Dictionary<string, Table> GroupByNames(List<IToken> tokens, int open)
        {
            var names = new Dictionary<string, Table>(StringComparer.OrdinalIgnoreCase);
            var depth = 0;
            var arg = 0;
            for (var k = open + 1; k < tokens.Count; k++)
            {
                var t = tokens[k];
                if (t.Type == DAXLexer.OPEN_PARENS || t.Type == DAXLexer.OPEN_CURLY) depth++;
                else if (t.Type == DAXLexer.CLOSE_PARENS || t.Type == DAXLexer.CLOSE_CURLY) { if (depth == 0) break; depth--; }
                else if (t.Type == DAXLexer.COMMA && depth == 0) arg++;
                else if (arg >= 1 && depth == 0 && t.Type == DAXLexer.STRING_LITERAL) break; // the first name of a name/expression pair ends the groupBy arguments
                else if (arg >= 1 && t.Type == DAXLexer.COLUMN_OR_MEASURE)
                {
                    var qualifier = tokens[k - 1].Type == DAXLexer.COMMA || tokens[k - 1].Type == DAXLexer.OPEN_PARENS || !IsTableToken(tokens[k - 1]) ? null : Model.FindTableOrCalendar(tokens[k - 1].Text) as Table;
                    names.TryAdd(t.Text, qualifier);
                }
            }
            return names;
        }

        // The functions whose result holds rows of ONE table: the table named by their own table argument. Microsoft: FILTER "returns a table that represents a subset of another table or expression"
        // (https://learn.microsoft.com/en-us/dax/filter-function-dax); ALL "returns all the rows in a table" (all-function-dax); CALCULATETABLE, TOPN, SAMPLE and TOPNSKIP return rows of their table argument.
        // So SUMX ( FILTER ( Sales, .. ), [Revenue] ) reads the row of Sales, however many tables name a Revenue column.
        private static readonly Dictionary<string, int> TableKeepingFunctionArg = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["FILTER"] = 0, ["ALL"] = 0, ["CALCULATETABLE"] = 0, ["TOPN"] = 1, ["SAMPLE"] = 1, ["TOPNSKIP"] = 2,
        };

        /// <summary>The one table the argument at <paramref name="argIndex"/> of the call whose parenthesis is tokens[open] stands for, or null when it is anything else
        /// (a variable, a column, any other table expression). A plain table name, or a table-keeping call over one, counts; the argument must be nothing more than that.</summary>
        private static Table TableOfArgument(List<IToken> tokens, int open, int argIndex, Dictionary<string, Table> variables, List<ScanScope> scopes)
        {
            // Walk to the first token of the argument, passing the commas of this call only.
            var at = open + 1;
            for (int depth = 0, arg = 0; at < tokens.Count && arg < argIndex; at++)
            {
                var t = tokens[at].Type;
                if (t == DAXLexer.OPEN_PARENS || t == DAXLexer.OPEN_CURLY) depth++;
                else if (t == DAXLexer.CLOSE_PARENS || t == DAXLexer.CLOSE_CURLY) { if (depth == 0) return null; depth--; }
                else if (t == DAXLexer.COMMA && depth == 0) arg++;
            }
            return TableAt(tokens, at, variables, scopes);
        }

        // What can follow a table expression: the next argument, the end of the call, or (for a VAR's expression) the next VAR or the RETURN.
        private static bool EndsTableExpression(IToken tok) =>
            tok.Type == DAXLexer.COMMA || tok.Type == DAXLexer.CLOSE_PARENS || tok.Type == DAXLexer.RETURN || tok.Type == DAXLexer.VAR;

        /// <summary>The one table the expression that starts at tokens[at] stands for, or null when it is anything else. The same reading serves an argument and the expression of a VAR.
        /// CURRENTGROUP () inside GROUPBY is the table argument of that GROUPBY.</summary>
        private static Table TableAt(List<IToken> tokens, int at, Dictionary<string, Table> variables, List<ScanScope> scopes)
        {
            if (at + 1 >= tokens.Count) return null;
            if (tokens[at + 1].Type == DAXLexer.OPEN_PARENS)
            {
                if (string.Equals(tokens[at].Text, "CURRENTGROUP", StringComparison.OrdinalIgnoreCase))
                {
                    if (at + 3 >= tokens.Count || tokens[at + 2].Type != DAXLexer.CLOSE_PARENS || !EndsTableExpression(tokens[at + 3])) return null;
                    for (var j = scopes.Count - 1; j >= 0; j--)
                        if (scopes[j].IsGroupBy) return scopes[j].GroupTable;
                    return null;
                }
                if (!TableKeepingFunctionArg.TryGetValue(tokens[at].Text, out var inner)) return null;
                var table = TableOfArgument(tokens, at + 1, inner, variables, scopes);
                if (table == null) return null;
                // Nothing may follow the call inside this argument (SUMX ( FILTER ( .. ) & .. ) is not a table of one name).
                var close = at + 2;
                for (var depth = 0; close < tokens.Count; close++)
                {
                    var t = tokens[close].Type;
                    if (t == DAXLexer.OPEN_PARENS || t == DAXLexer.OPEN_CURLY) depth++;
                    else if (t == DAXLexer.CLOSE_PARENS || t == DAXLexer.CLOSE_CURLY) { if (depth == 0) break; depth--; }
                }
                return close + 1 < tokens.Count && EndsTableExpression(tokens[close + 1]) ? table : null;
            }
            if (!IsTableToken(tokens[at]) || !EndsTableExpression(tokens[at + 1])) return null;
            return variables.TryGetValue(tokens[at].Text, out var held) ? held : Model.FindTableOrCalendar(tokens[at].Text) as Table;
        }

        private static bool IsTableToken(IToken tok) =>
            tok.Type == DAXLexer.TABLE || tok.Type == DAXLexer.TABLE_OR_VARIABLE || (tok.Channel == DAXLexer.KEYWORD_CHANNEL && !IsNeverATable(tok));

        /// <summary>The scans the walk is inside the rows of, innermost first. The table argument of a scan is not in that scan's rows.</summary>
        private static IEnumerable<ScanScope> OpenRowScans(List<ScanScope> scopes)
        {
            for (var i = scopes.Count - 1; i >= 0; i--)
                if (scopes[i].InRows) yield return scopes[i];
        }

        // ---- Semanticus: a KPI's generated helpers ------------------------------------------------------------------------------------
        // The server adds the measures [_<name> Goal], [_<name> Status] and [_<name> Trend] for a KPI, so a formula may read them although the model lists no such
        // measure. The wrapper's dependency list cannot hold them (the rewrite would write the base measure's own name), so each use is kept beside it: which formula,
        // which measure's helper, and the name it had when the formula was read. A use is kept only where the walk BOUND the name to the helper (not a column of a scanned or home
        // table, not a column of another table, not an unknown reference), and by its place: the property and the count of [bracketed names] before it, so the rewrite never
        // matches on text (a column called _Revenue Goal reads the same as the helper does).
        private sealed class KpiHelperUse
        {
            public Measure Base;
            public string OldName;
            public DAXProperty Prop;
            public int Ordinal;
        }

        private static readonly string[] KpiHelperSuffixes = { "Goal", "Status", "Trend" };
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IDaxDependantObject, List<KpiHelperUse>> KpiHelperUses =
            new System.Runtime.CompilerServices.ConditionalWeakTable<IDaxDependantObject, List<KpiHelperUse>>();

        private static void NoteKpiHelper(IDaxDependantObject holder, IEnumerable<Table> tables, string name, DAXProperty prop, int ordinal)
        {
            if (name.Length < 3 || name[0] != '_') return; // every generated helper starts with an underscore; most names are not looked up at all
            foreach (var t in tables)
                foreach (var m in t.Measures)
                {
                    if (m.KPI == null || name.Length <= m.Name.Length + 2) continue;
                    foreach (var suffix in KpiHelperSuffixes)
                    {
                        if (!string.Equals(name, "_" + m.Name + " " + suffix, StringComparison.OrdinalIgnoreCase)) continue;
                        KpiHelperUses.GetOrCreateValue(holder).Add(new KpiHelperUse { Base = m, OldName = name, Prop = prop, Ordinal = ordinal });
                        return;
                    }
                }
        }

        private static List<(IDaxDependantObject Holder, KpiHelperUse Use)> KpiHelperUsesOf(IDaxObject renamed)
        {
            var found = new List<(IDaxDependantObject, KpiHelperUse)>();
            if (!(renamed is Measure m) || m.KPI == null) return found;
            var live = new HashSet<IDaxDependantObject>(DependencyHolders());
            foreach (var entry in KpiHelperUses)
                if (live.Contains(entry.Key))
                    foreach (var use in entry.Value.Where(u => u.Base == m)) found.Add((entry.Key, use));
            return found;
        }

        private static void RewriteKpiHelperUses(IDaxObject renamed, List<(IDaxDependantObject Holder, KpiHelperUse Use)> uses)
        {
            foreach (var group in uses.GroupBy(u => u.Holder))
            {
                foreach (var prop in group.Key.GetDAXProperties())
                {
                    var dax = group.Key.GetDAX(prop);
                    if (string.IsNullOrEmpty(dax)) continue;
                    var lexer = new DAXLexer(new DAXCharStream(dax, false));
                    lexer.RemoveErrorListeners();
                    var sb = new StringBuilder();
                    var pos = 0;
                    var ordinal = -1;
                    foreach (var tok in lexer.GetDefaultTokens().Where(t => t.Type == DAXLexer.COLUMN_OR_MEASURE))
                    {
                        ordinal++;
                        // only the occurrence the walk bound to the helper; the name must still be the one it noted (a guard, not the test)
                        var use = group.Select(u => u.Use).FirstOrDefault(u => u.Prop.Equals(prop) && u.Ordinal == ordinal && string.Equals(u.OldName, tok.Text, StringComparison.OrdinalIgnoreCase));
                        if (use == null) continue;
                        var suffix = use.OldName.Substring(use.OldName.LastIndexOf(' ') + 1);
                        sb.Append(dax, pos, tok.StartIndex - pos).Append('[').Append(("_" + ((Measure)renamed).Name + " " + suffix).Replace("]", "]]")).Append(']');
                        pos = tok.StopIndex + 1;
                    }
                    if (pos == 0) continue;
                    group.Key.SetDAX(prop, sb.Append(dax, pos, dax.Length - pos).ToString());
                }
            }
        }

        public static void ClearDependsOn(IDaxDependantObject expressionObj)
        {
            KpiHelperUses.Remove(expressionObj); // Semanticus: read again below
            foreach (var d in expressionObj.DependsOn.Keys) d.ReferencedBy.Remove(expressionObj);
            expressionObj.DependsOn.Clear();
        }

        /// <summary>
        /// Rebuilds the dependency tree for the provided object. If within a BeginUpdate / EndUpdate batch,
        /// this operation is postponed until the batch ends unless force is set to true.
        /// </summary>
        /// <param name="expressionObj"></param>
        /// <param name="force"></param>
        public static void BuildDependencyTree(IDaxDependantObject expressionObj, bool force = false)
        {
            if (Handler.EoB_PostponeOperations && !force) { Handler.EoB_RequireRebuildDependencyTree = true; return; }

            ClearDependsOn(expressionObj);

            foreach (var prop in expressionObj.GetDAXProperties())
            {
                var dax = expressionObj.GetDAX(prop) ?? "";  
                ParseExpression(dax.Replace("\r", ""), expressionObj, prop); // Carriage Returns are excluded in the Dependency Tree
            }
        }

        // Semanticus: every formula the full rebuild reads, as one list (the objects whose helper uses are still live)
        private static IEnumerable<IDaxDependantObject> DependencyHolders() =>
            Model.Tables.SelectMany(t => t.GetChildren()).Concat(Model.Tables).Concat(Model.AllMeasures.Select(m => m.KPI).Where(k => k != null)).OfType<IDaxDependantObject>()
                .Concat(Model.CalculationGroups.SelectMany(cg => cg.CalculationItems))
                .Concat(Model.AllPartitions.Where(p => p.DataCoverageDefinition != null && !string.IsNullOrEmpty(p.DataCoverageDefinition.Expression)))
                .Concat(Model.Functions.Where(f => !string.IsNullOrEmpty(f.Expression)))
                .Concat(Model.Roles.SelectMany(r => r.TablePermissions));

        public static void BuildDependencyTree()
        {
            if (Handler.EoB_PostponeOperations) { Handler.EoB_RequireRebuildDependencyTree = true; return; }

            var sw = new Stopwatch();
            sw.Start();

            foreach (var eo in Model.Tables.SelectMany(t => t.GetChildren()).Concat(Model.Tables).Concat(Model.AllMeasures.Select(m => m.KPI).Where(k => k != null)).OfType<IDaxDependantObject>()) // Semanticus: a measure's KPI is a child of the measure, which Table.GetChildren does not reach
            {
                BuildDependencyTree(eo);
            }
            foreach (var ci in Model.CalculationGroups.SelectMany(cg => cg.CalculationItems))
            {
                BuildDependencyTree(ci);
            }
            foreach (var p in Model.AllPartitions.Where(p => p.DataCoverageDefinition != null && !string.IsNullOrEmpty(p.DataCoverageDefinition.Expression)))
            {
                BuildDependencyTree(p);
            }

            foreach (var f in Model.Functions.Where(f => !string.IsNullOrEmpty(f.Expression)))
            {
                BuildDependencyTree(f);
            }
            foreach (var role in Model.Roles)
            {
                foreach(var tp in role.TablePermissions)
                {
                    BuildDependencyTree(tp);
                }
            }

            sw.Stop();
        }
    }
}
