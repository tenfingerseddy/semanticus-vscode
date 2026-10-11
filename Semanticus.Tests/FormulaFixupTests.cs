using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Tabular Editor's rename fixup read a keyword before a bracket as a TABLE (RETURN [Total] became a table named RETURN) and then
    /// gave up on the whole formula at the first table it could not find. A rename of Qty or Total left every reference after a
    /// RETURN, a NOT, an IN, an AND, an OR or a variable-qualified column on its old name, and the formula broke without a word.
    /// These tests rename a measure, a column and a table in each shape and in each place a formula lives (a measure, its format string
    /// expression, a calculated column, a calculation item and its format string expression, a role's row filter, a KPI), through
    /// rename_object, one batch (what a script and the plan do) and the start build's own executor. Each formula must stay whole.
    /// The patched copy is Semanticus.Core/Compat/FormulaFixup.Patched.cs.
    /// </summary>
    public sealed class FormulaFixupTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        // {T} the table, {C} the column, {M} the measure. Every shape reads the column AND the measure AFTER the keyword it tests.
        public static readonly string[] ShapeNames = { "return", "not", "in", "and", "or", "var-body", "var-qualified", "lowercase", "after-return-twice" };

        private static string Shape(string name) => name switch
        {
            "return" => "VAR a = 1 RETURN [{M}] + SUM('{T}'[{C}])",
            "not" => "IF(NOT [{M}], 0, SUM('{T}'[{C}]))",
            "in" => "IF(1 IN [{M}], [{M}], SUM('{T}'[{C}]))",
            "and" => "IF(1 AND [{M}], [{M}], SUM('{T}'[{C}]))",
            "or" => "IF(1 OR [{M}], [{M}], SUM('{T}'[{C}]))",
            "var-body" => "VAR a = SUM('{T}'[{C}]) VAR b = [{M}] RETURN a + b",
            // A variable used as a table: t[Amount] is no column of the model, and must not hide what comes after it.
            "var-qualified" => "VAR t = ALL('{T}') RETURN SUMX(t, t[Amount]) + [{M}] + SUM('{T}'[{C}])",
            "lowercase" => "var a = 1 return [{M}] + SUM('{T}'[{C}])",
            "after-return-twice" => "VAR a = 1 RETURN IF(NOT [{M}], 0, VAR b = 2 RETURN [{M}] + b + SUM('{T}'[{C}]))",
            _ => throw new ArgumentException(name),
        };

        public static IEnumerable<object[]> Cases() =>
            from shape in ShapeNames
            from rename in new[] { "column", "measure", "table" }
            select new object[] { shape, rename };

        private const string Table = "Sales", Column = "Qty", Measure = "Total";
        private const string NewTable = "Sales Facts", NewColumn = "Quantity", NewMeasure = "Total Sales";

        private static string Fill(string shape, string t, string c, string m) =>
            Shape(shape).Replace("{T}", t).Replace("{C}", c).Replace("{M}", m);

        /// <summary>Every place a formula lives, each holding the same shape. Returns the places by name.</summary>
        private static async Task<(LocalEngine Engine, SessionManager Sessions)> ModelAsync(string shape)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Pro());
            await engine.CreateModelAsync("Fixup", 1601);
            var formula = Fill(shape, Table, Column, Measure);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable(Table);
                sales.AddDataColumn(Column, Column, null, DataType.Int64);
                sales.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
                sales.AddMeasure(Measure, "SUM('Sales'[Amount])");

                var host = sales.AddMeasure("Host", formula);
                host.FormatStringExpression = formula;
                sales.AddCalculatedColumn("Calc", formula);

                var kpiHost = sales.AddMeasure("Kpi Host", "1");
                var kpi = kpiHost.AddKPI();
                kpi.StatusExpression = formula;
                kpi.TargetExpression = formula;
                kpi.TrendExpression = formula;

                var cg = m.AddCalculationGroup("Time Calc");
                var item = cg.AddCalculationItem("Current", formula);
                item.FormatStringExpression = formula;

                var role = m.AddRole("Readers");
                role.AddOrGetTablePermission(sales).FilterExpression = formula;
            });
            return (engine, sessions);
        }

        /// <summary>Every formula in the model that holds the shape, as (where, text).</summary>
        private static Task<List<(string Where, string Text)>> FormulasAsync(SessionManager sessions) =>
            sessions.Require().ReadAsync(m =>
            {
                var sales = m.Tables.Single(t => t.Name == Table || t.Name == NewTable);
                var host = sales.Measures["Host"];
                var kpi = sales.Measures["Kpi Host"].KPI;
                var item = m.CalculationGroups.Single().CalculationItems.Single();
                return new List<(string, string)>
                {
                    ("measure expression", host.Expression),
                    ("measure format string expression", host.FormatStringExpression),
                    ("calculated column", ((CalculatedColumn)sales.Columns["Calc"]).Expression),
                    ("kpi status", kpi.StatusExpression),
                    ("kpi target", kpi.TargetExpression),
                    ("kpi trend", kpi.TrendExpression),
                    ("calculation item", item.Expression),
                    ("calculation item format string", item.FormatStringExpression),
                    ("role row filter", m.Roles.Single().TablePermissions.Single().FilterExpression),
                };
            });

        private static (string Ref, string To, string T, string C, string M) Plan(string rename) => rename switch
        {
            "column" => ("column:Sales/Qty", NewColumn, Table, NewColumn, Measure),
            "measure" => ("measure:Sales/Total", NewMeasure, Table, Column, NewMeasure),
            "table" => ("table:Sales", NewTable, NewTable, Column, Measure),
            _ => throw new ArgumentException(rename),
        };

        private static void AssertWhole(string shape, string expected, List<(string Where, string Text)> formulas)
        {
            Assert.NotEmpty(formulas);
            foreach (var (where, text) in formulas)
                Assert.True(expected == text, $"{where}: the formula of shape '{shape}' is not whole after the rename.\n  expected: {expected}\n  actual:   {text}");
        }

        /// <summary>Lean suite: one model holds all nine shapes in every place a formula lives (a measure, its format string expression, a
        /// calculated column, a KPI, a calculation item, a role's row filter), and a column, a measure and a table are renamed through
        /// rename_object one after another. After each rename every formula of every shape must be whole under the names so far. The full cross
        /// product of shape and rename, one model each, on the three paths, is the sweep tier.</summary>
        [Fact]
        public async Task Every_shape_stays_whole_when_a_column_a_measure_and_a_table_are_renamed_one_after_another()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Pro());
            using (engine)
            {
                await engine.CreateModelAsync("FixupAll", 1601);
                await sessions.Require().MutateAsync("human", "seed", m =>
                {
                    var sales = m.AddTable(Table);
                    sales.AddDataColumn(Column, Column, null, DataType.Int64);
                    sales.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
                    sales.AddMeasure(Measure, "SUM('Sales'[Amount])");
                    var cg = m.AddCalculationGroup("Time Calc");
                    foreach (var shape in ShapeNames)
                    {
                        var formula = Fill(shape, Table, Column, Measure);
                        var host = sales.AddMeasure("Host " + shape, formula);
                        host.FormatStringExpression = formula;
                        sales.AddCalculatedColumn("Calc " + shape, formula);
                        var kpi = sales.AddMeasure("Kpi Host " + shape, "1").AddKPI();
                        kpi.StatusExpression = formula; kpi.TargetExpression = formula; kpi.TrendExpression = formula;
                        var item = cg.AddCalculationItem("Item " + shape, formula);
                        item.FormatStringExpression = formula;
                        m.AddRole("Readers " + shape).AddOrGetTablePermission(sales).FilterExpression = formula;
                    }
                });
                var (t, c, mname) = (Table, Column, Measure);
                foreach (var rename in new[] { "column", "measure", "table" })
                {
                    var p = Plan(rename);
                    var result = await engine.RenameObjectAsync(p.Ref, p.To, "human");
                    Assert.True(result.Changed);
                    if (rename == "column") c = NewColumn; else if (rename == "measure") mname = NewMeasure; else t = NewTable;
                    var seen = await sessions.Require().ReadAsync(m =>
                    {
                        var sales = m.Tables.Single(x => x.Name == Table || x.Name == NewTable);
                        var group = m.CalculationGroups.Single();
                        var list = new List<(string Shape, string Where, string Text)>();
                        foreach (var shape in ShapeNames)
                        {
                            var host = sales.Measures["Host " + shape];
                            var kpi = sales.Measures["Kpi Host " + shape].KPI;
                            var item = group.CalculationItems["Item " + shape];
                            list.Add((shape, "measure expression", host.Expression));
                            list.Add((shape, "measure format string expression", host.FormatStringExpression));
                            list.Add((shape, "calculated column", ((CalculatedColumn)sales.Columns["Calc " + shape]).Expression));
                            list.Add((shape, "kpi status", kpi.StatusExpression));
                            list.Add((shape, "kpi target", kpi.TargetExpression));
                            list.Add((shape, "kpi trend", kpi.TrendExpression));
                            list.Add((shape, "calculation item", item.Expression));
                            list.Add((shape, "calculation item format string", item.FormatStringExpression));
                        }
                        foreach (var shape in ShapeNames)
                            list.Add((shape, "role row filter", m.Roles["Readers " + shape].TablePermissions.Single().FilterExpression));
                        return list;
                    });
                    Assert.NotEmpty(seen);
                    foreach (var (shape, where, text) in seen)
                    {
                        var expected = Fill(shape, t, c, mname);
                        Assert.True(expected == text, $"after renaming the {rename}: {where}: the formula of shape '{shape}' is not whole.\n  expected: {expected}\n  actual:   {text}");
                    }
                }
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Cases))]
        public async Task A_rename_through_rename_object_leaves_every_formula_whole(string shape, string rename)
        {
            var (engine, sessions) = await ModelAsync(shape);
            using (engine)
            {
                var p = Plan(rename);
                var result = await engine.RenameObjectAsync(p.Ref, p.To, "human");
                Assert.True(result.Changed);
                AssertWhole(shape, Fill(shape, p.T, p.C, p.M), await FormulasAsync(sessions));
            }
        }

        public static IEnumerable<object[]> CasesSample_A_rename_made_in_one_bat() { var all = Cases().ToArray(); return new[] { all[0], all[4], all[20] }; }
        [Theory]
        [MemberData(nameof(CasesSample_A_rename_made_in_one_bat))]
        public Task A_rename_made_in_one_batch_leaves_every_formula_whole_sample(string shape, string rename) => A_rename_made_in_one_batch_leaves_every_formula_whole(shape, rename);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Cases))]
        public async Task A_rename_made_in_one_batch_leaves_every_formula_whole(string shape, string rename)
        {
            var (engine, sessions) = await ModelAsync(shape);
            using (engine)
            {
                var p = Plan(rename);
                var s = sessions.Require();
                await s.MutateAsync("human", "batch rename", m => s.Rename((TabularNamedObject)ObjectRefs.Resolve(m, p.Ref), p.To));
                AssertWhole(shape, Fill(shape, p.T, p.C, p.M), await FormulasAsync(sessions));
            }
        }

        public static IEnumerable<object[]> CasesSample_A_rename_line_through_th() { var all = Cases().ToArray(); return new[] { all[0], all[4], all[20] }; }
        [Theory]
        [MemberData(nameof(CasesSample_A_rename_line_through_th))]
        public Task A_rename_line_through_the_start_builds_executor_leaves_every_formula_whole_sample(string shape, string rename) => A_rename_line_through_the_start_builds_executor_leaves_every_formula_whole(shape, rename);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Cases))]
        public async Task A_rename_line_through_the_start_builds_executor_leaves_every_formula_whole(string shape, string rename)
        {
            var (engine, sessions) = await ModelAsync(shape);
            using (engine)
            {
                var p = Plan(rename);
                var s = sessions.Require();
                var line = new ChangeItem { Id = "ci-1", Kind = "rename", ObjectRef = p.Ref, ObjectName = p.Ref, Before = p.Ref.Substring(p.Ref.LastIndexOfAny(new[] { '/', ':' }) + 1), After = p.To, Source = "deterministic", Title = "rename", Status = "approved" };
                await s.MutateAsync("human", "start build lines", m => PlanLineExecutor.ApplyInBatch(engine, m, s, new List<ChangeItem> { line }));
                Assert.Equal("applied", line.Status);
                AssertWhole(shape, Fill(shape, p.T, p.C, p.M), await FormulasAsync(sessions));
            }
        }

        [Theory]
        [InlineData("return")]
        public Task Three_renames_in_one_batch_leave_every_formula_whole_sample(string shape) => Three_renames_in_one_batch_leave_every_formula_whole(shape);

        /// <summary>All three renames in ONE batch (a script that renames the table, the column and the measure): the formula keeps every
        /// new name, in every shape.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("return")]
        [InlineData("not")]
        [InlineData("in")]
        [InlineData("and")]
        [InlineData("or")]
        [InlineData("var-body")]
        [InlineData("var-qualified")]
        [InlineData("lowercase")]
        [InlineData("after-return-twice")]
        public async Task Three_renames_in_one_batch_leave_every_formula_whole(string shape)
        {
            var (engine, sessions) = await ModelAsync(shape);
            using (engine)
            {
                var s = sessions.Require();
                await s.MutateAsync("human", "script", m =>
                {
                    s.Rename((TabularNamedObject)ObjectRefs.Resolve(m, "column:Sales/Qty"), NewColumn);
                    s.Rename((TabularNamedObject)ObjectRefs.Resolve(m, "measure:Sales/Total"), NewMeasure);
                    s.Rename((TabularNamedObject)ObjectRefs.Resolve(m, "table:Sales"), NewTable);
                });
                AssertWhole(shape, Fill(shape, NewTable, NewColumn, NewMeasure), await FormulasAsync(sessions));
            }
        }

        [Theory]
        [InlineData("return", "measure")]
        public Task Undo_and_redo_of_such_a_rename_keep_every_formula_whole_sample(string shape, string rename) => Undo_and_redo_of_such_a_rename_keep_every_formula_whole(shape, rename);

        /// <summary>The same rename, then an undo: the formulas return to the old names whole, and a redo brings the new ones back.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("return", "measure")]
        [InlineData("not", "column")]
        [InlineData("var-qualified", "table")]
        public async Task Undo_and_redo_of_such_a_rename_keep_every_formula_whole(string shape, string rename)
        {
            var (engine, sessions) = await ModelAsync(shape);
            using (engine)
            {
                var p = Plan(rename);
                await engine.RenameObjectAsync(p.Ref, p.To, "human");
                await engine.UndoAsync("human");
                AssertWhole(shape, Fill(shape, Table, Column, Measure), await FormulasAsync(sessions));
                await engine.RedoAsync("human");
                AssertWhole(shape, Fill(shape, p.T, p.C, p.M), await FormulasAsync(sessions));
            }
        }

        [Theory]
        [InlineData("table")]
        public Task A_rename_rewrites_a_kpis_expressions_in_a_formula_with_no_keyword_sample(string rename) => A_rename_rewrites_a_kpis_expressions_in_a_formula_with_no_keyword(rename);

        /// <summary>The upstream full rebuild never visited a measure's KPI (it is a child of the measure, not of the table), so a rename
        /// left its status, target and trend on the old names. No keyword is involved: this is the plainest formula.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("column")]
        [InlineData("measure")]
        [InlineData("table")]
        public async Task A_rename_rewrites_a_kpis_expressions_in_a_formula_with_no_keyword(string rename)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Pro());
            await engine.CreateModelAsync("Fixup", 1601);
            var s = sessions.Require();
            const string shape = "SUM('{T}'[{C}]) + [{M}]";
            string Make(string t, string c, string m) => shape.Replace("{T}", t).Replace("{C}", c).Replace("{M}", m);
            await s.MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable(Table);
                sales.AddDataColumn(Column, Column, null, DataType.Int64);
                sales.AddMeasure(Measure, "1");
                var kpi = sales.AddMeasure("Kpi Host", "1").AddKPI();
                kpi.StatusExpression = kpi.TargetExpression = kpi.TrendExpression = Make(Table, Column, Measure);
            });
            var p = Plan(rename);
            await engine.RenameObjectAsync(p.Ref, p.To, "human");
            var after = await s.ReadAsync(m => m.AllMeasures.Single(x => x.Name == "Kpi Host").KPI);
            Assert.Equal(Make(p.T, p.C, p.M), after.StatusExpression);
            Assert.Equal(Make(p.T, p.C, p.M), after.TargetExpression);
            Assert.Equal(Make(p.T, p.C, p.M), after.TrendExpression);
        }

        /// <summary>A table whose name is a DAX function (Date) must still be read as a table: the repair names keywords that are never
        /// tables, and Date is not one of them.</summary>
        [Fact]
        public async Task A_table_named_like_a_function_is_still_renamed_in_its_references()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Pro());
            await engine.CreateModelAsync("Fixup", 1601);
            var s = sessions.Require();
            await s.MutateAsync("human", "seed", m =>
            {
                var date = m.AddTable("Date");
                date.AddDataColumn("Day", "Day", null, DataType.DateTime);
                m.AddTable("Sales").AddMeasure("First", "VAR a = 1 RETURN MIN(Date[Day]) + a");
            });
            await engine.RenameObjectAsync("table:Date", "Calendar Dim", "human");
            Assert.Equal("VAR a = 1 RETURN MIN('Calendar Dim'[Day]) + a", await s.ReadAsync(m => m.Tables["Sales"].Measures["First"].Expression));
        }

        /// <summary>A column of a table that is the same word as a keyword the repair excludes is only ever reached through a quoted or
        /// bracketed name, so the exclusion costs nothing: 'Return'[Qty] is a TABLE token, not a keyword.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_quoted_table_whose_name_is_a_keyword_is_still_renamed()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Pro());
            await engine.CreateModelAsync("Fixup", 1601);
            var s = sessions.Require();
            await s.MutateAsync("human", "seed", m =>
            {
                var t = m.AddTable("Return");
                t.AddDataColumn("Qty", "Qty", null, DataType.Int64);
                t.AddMeasure("Sum Qty", "SUM('Return'[Qty])");
            });
            await engine.RenameObjectAsync("column:Return/Qty", "Quantity", "human");
            Assert.Equal("SUM('Return'[Quantity])", await s.ReadAsync(m => m.Tables["Return"].Measures["Sum Qty"].Expression));
        }

        /// <summary>A reference to a column of a table that does not exist used to end the walk. It is skipped now, and what follows it is
        /// still found.</summary>
        [Fact]
        public async Task A_reference_to_a_table_the_model_lacks_does_not_hide_the_references_after_it()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Pro());
            await engine.CreateModelAsync("Fixup", 1601);
            var s = sessions.Require();
            await s.MutateAsync("human", "seed", m =>
            {
                var t = m.AddTable("Sales");
                t.AddDataColumn("Qty", "Qty", null, DataType.Int64);
                t.AddMeasure("Broken", "SUM('Gone'[X]) + SUM('Sales'[Qty])");
            });
            await engine.RenameObjectAsync("column:Sales/Qty", "Quantity", "human");
            Assert.Equal("SUM('Gone'[X]) + SUM('Sales'[Quantity])", await s.ReadAsync(m => m.Tables["Sales"].Measures["Broken"].Expression));
        }

        // ---- S1 (Sol, join 10): a bare [column] inside a row-scan function binds to the iterated table's row, not the measure's home table. ----
        // Microsoft's DAX syntax reference (https://learn.microsoft.com/en-gb/dax/dax-syntax-reference, "Naming requirements" and "Table-scanning functions"):
        // inside a function that scans a table (SUMX, FILTER, ADDCOLUMNS ...), an unqualified [Column] is read from the current row of THAT table.

        /// <summary>Sales has Revenue and Net Revenue, Metrics has its own unrelated Revenue. The measures are hosted by Metrics, the iterated table is Sales.</summary>
        private static async Task<(LocalEngine Engine, SessionManager Sessions)> ScopeModelAsync(string formula, bool metricsHasRevenue = true)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Pro());
            await engine.CreateModelAsync("Scope", 1601);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable("Sales");
                sales.AddDataColumn("Revenue", "Revenue", null, DataType.Decimal);
                sales.AddDataColumn("Net Revenue", "Net Revenue", null, DataType.Decimal);
                sales.AddDataColumn("Region", "Region", null, DataType.String);
                var metrics = m.AddTable("Metrics");
                if (metricsHasRevenue) metrics.AddDataColumn("Revenue", "Revenue", null, DataType.Decimal);
                metrics.AddMeasure("Total", "1");
                metrics.AddMeasure("Host", formula);
            });
            return (engine, sessions);
        }

        private static Task<string> HostAsync(SessionManager sessions) => sessions.Require().ReadAsync(m => m.Tables["Metrics"].Measures["Host"].Expression);

        public static readonly string[] RowScanShapes =
        {
            "SUMX ( Sales, [Revenue] )", "AVERAGEX ( Sales, [Revenue] )", "MINX ( Sales, [Revenue] )", "MAXX ( Sales, [Revenue] )",
            "COUNTX ( Sales, [Revenue] )", "FILTER ( Sales, [Revenue] > 0 )", "ADDCOLUMNS ( Sales, \"x\", [Revenue] )",
            "SELECTCOLUMNS ( Sales, \"x\", [Revenue] )", "GENERATE ( Sales, ROW ( \"x\", [Revenue] ) )",
            "SUMX ( 'Sales', [Revenue] )", "CONCATENATEX ( Sales, [Revenue], \",\" )", "STDEVX.S ( Sales, [Revenue] )", "SUMX ( Sales, SUMX ( Sales, [Revenue] ) )",
        };
        public static IEnumerable<object[]> RowScanCases() => RowScanShapes.Select(x => new object[] { x });

        /// <summary>Lean suite: the read-only corpus of row-scan shapes runs on ONE model per case (a measure for each shape), not one model per
        /// shape. One rename, then every host is checked. The one-model-per-shape theories below are the sweep tier.</summary>
        [Fact]
        public async Task Every_row_scan_shape_in_one_model_keeps_a_bare_name_that_is_the_iterated_tables_and_follows_one_that_is_the_home_tables()
        {
            // Sol's first case: the home table's own column is renamed, the bare name belongs to the iterated table, so nothing changes.
            var (engine, sessions) = await ScopeModelManyAsync(RowScanShapes, metricsHasRevenue: true);
            using (engine)
            {
                await engine.RenameObjectAsync("column:Metrics/Revenue", "Net Revenue", "human");
                var hosts = await HostsAsync(sessions, RowScanShapes.Length);
                for (var i = 0; i < RowScanShapes.Length; i++) Assert.Equal(RowScanShapes[i], hosts[i]);
            }
            // Sol's second case: the iterated table's column is renamed, the bare name follows it, even though the home table has no such column.
            var (engine2, sessions2) = await ScopeModelManyAsync(RowScanShapes, metricsHasRevenue: false);
            using (engine2)
            {
                await engine2.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                var hosts = await HostsAsync(sessions2, RowScanShapes.Length);
                for (var i = 0; i < RowScanShapes.Length; i++) Assert.Equal(RowScanShapes[i].Replace("[Revenue]", "[Gross Revenue]"), hosts[i]);
            }
        }

        private static async Task<(LocalEngine Engine, SessionManager Sessions)> ScopeModelManyAsync(string[] formulas, bool metricsHasRevenue)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Pro());
            await engine.CreateModelAsync("ScopeMany", 1601);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable("Sales");
                sales.AddDataColumn("Revenue", "Revenue", null, DataType.Decimal);
                sales.AddDataColumn("Net Revenue", "Net Revenue", null, DataType.Decimal);
                sales.AddDataColumn("Region", "Region", null, DataType.String);
                var metrics = m.AddTable("Metrics");
                if (metricsHasRevenue) metrics.AddDataColumn("Revenue", "Revenue", null, DataType.Decimal);
                metrics.AddMeasure("Total", "1");
                for (var i = 0; i < formulas.Length; i++) metrics.AddMeasure("Host" + i, formulas[i]);
            });
            return (engine, sessions);
        }

        private static Task<string[]> HostsAsync(SessionManager sessions, int count) =>
            sessions.Require().ReadAsync(m => Enumerable.Range(0, count).Select(i => m.Tables["Metrics"].Measures["Host" + i].Expression).ToArray());

        /// <summary>Sol's first case: renaming Metrics[Revenue] must leave the Sales iterator alone, because the bare name is Sales' column.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(RowScanCases))]
        public async Task Renaming_a_column_of_the_home_table_leaves_a_bare_name_inside_a_row_scan_of_another_table_alone(string formula)
        {
            var (engine, sessions) = await ScopeModelAsync(formula);
            using (engine)
            {
                await engine.RenameObjectAsync("column:Metrics/Revenue", "Net Revenue", "human");
                Assert.Equal(formula, await HostAsync(sessions));
            }
        }

        /// <summary>Sol's second case: renaming the ITERATED table's column rewrites the bare name, even when the home table has no such column.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(RowScanCases))]
        public async Task Renaming_a_column_of_the_iterated_table_rewrites_a_bare_name_inside_the_row_scan(string formula)
        {
            var (engine, sessions) = await ScopeModelAsync(formula, metricsHasRevenue: false);
            using (engine)
            {
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal(formula.Replace("[Revenue]", "[Gross Revenue]"), await HostAsync(sessions));
            }
        }

        /// <summary>The innermost scan wins: the inner SUMX iterates Metrics, so [Revenue] is Metrics' column and Sales' rename does not touch it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_innermost_row_scan_decides_which_table_a_bare_name_belongs_to()
        {
            const string formula = "SUMX ( Sales, SUMX ( Metrics, [Revenue] ) )";
            var (engine, sessions) = await ScopeModelAsync(formula);
            using (engine)
            {
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal(formula, await HostAsync(sessions));
                await engine.RenameObjectAsync("column:Metrics/Revenue", "Own Revenue", "human");
                Assert.Equal("SUMX ( Sales, SUMX ( Metrics, [Own Revenue] ) )", await HostAsync(sessions));
            }
        }

        /// <summary>After the scan closes, a bare name is the home table's column again; the scan's own table (its first argument) is no row scope for itself.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_bare_name_after_a_row_scan_closes_binds_to_the_home_table_again()
        {
            const string formula = "SUMX ( Sales, 1 ) + [Revenue]";
            var (engine, sessions) = await ScopeModelAsync(formula);
            using (engine)
            {
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal(formula, await HostAsync(sessions));
                await engine.RenameObjectAsync("column:Metrics/Revenue", "Own Revenue", "human");
                Assert.Equal("SUMX ( Sales, 1 ) + [Own Revenue]", await HostAsync(sessions));
            }
        }

        /// <summary>The iterated table is not a plain table reference (a table expression, a variable), so it cannot be known: the bare name is not rewritten
        /// and no dependency is recorded, for either column. A name that only a MEASURE can carry (no column of that name anywhere) is still followed.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("SUMX ( VALUES ( Sales[Region] ), [Revenue] )")]
        [InlineData("VAR t = ADDCOLUMNS ( Sales, \"x\", 1 ) RETURN SUMX ( t, [Revenue] )")]
        public async Task A_bare_name_inside_a_row_scan_of_a_table_that_cannot_be_known_is_not_rewritten_and_records_no_dependency(string formula)
        {
            var (engine, sessions) = await ScopeModelAsync(formula);
            using (engine)
            {
                var deps = await sessions.Require().ReadAsync(m => m.Tables["Metrics"].Measures["Host"].DependsOn.Keys.OfType<Column>().Where(c => c.Name == "Revenue").Select(c => c.Table.Name).ToArray());
                Assert.DoesNotContain("Metrics", deps);
                await engine.RenameObjectAsync("column:Metrics/Revenue", "Net Revenue", "human");
                Assert.Equal(formula, await HostAsync(sessions));
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal(formula, await HostAsync(sessions));   // not knowable, so left for the person (the walk never guesses)
            }
        }

        /// <summary>Fix round 2 of solfx (Y3): a VAR whose expression is one plain table (or a table-keeping call over one) is that table, so a scan of the variable reads its
        /// row exactly as the same expression written inline does. Only Sales' column follows the rename.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("VAR t = ALL ( Sales ) RETURN SUMX ( t, [Revenue] )")]
        [InlineData("VAR Sales = ALL ( Sales ) RETURN SUMX ( Sales, [Revenue] )")]
        public async Task A_scan_of_a_variable_over_one_plain_table_reads_that_tables_row(string formula)
        {
            var (engine, sessions) = await ScopeModelAsync(formula);
            using (engine)
            {
                await engine.RenameObjectAsync("column:Metrics/Revenue", "Net Revenue", "human");
                Assert.Equal(formula, await HostAsync(sessions));
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal(formula.Replace("[Revenue]", "[Gross Revenue]"), await HostAsync(sessions));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_measure_name_inside_a_row_scan_of_a_table_expression_is_still_followed_when_no_column_shares_the_name()
        {
            const string formula = "SUMX ( VALUES ( Sales[Region] ), [Total] )";
            var (engine, sessions) = await ScopeModelAsync(formula);
            using (engine)
            {
                await engine.RenameObjectAsync("measure:Metrics/Total", "Grand Total", "human");
                Assert.Equal("SUMX ( VALUES ( Sales[Region] ), [Grand Total] )", await HostAsync(sessions));
            }
        }

        // ---- S2 (Sol, join 10): a KPI's generated helper measures [_<name> Goal], [_<name> Status], [_<name> Trend] follow the base measure's rename. ----

        private static async Task<(LocalEngine Engine, SessionManager Sessions)> KpiModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Pro());
            await engine.CreateModelAsync("Kpi", 1601);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable("Sales");
                var kpi = sales.AddMeasure("Achievement", "1").AddKPI();
                kpi.TargetExpression = "1";
                kpi.StatusExpression = "DIVIDE ( [Achievement], [_Achievement Goal] )";
                kpi.TrendExpression = "'Sales'[_Achievement Trend] + [_Achievement Status]";
                sales.AddMeasure("Reader", "[_Achievement Goal]");
                sales.AddMeasure("Qualified Reader", "Sales[_Achievement Goal] + 'Sales'[_Achievement Status]");
                m.AddTable("Other").AddMeasure("Other Reader", "[_Achievement Goal] + Sales[_Achievement Trend]");
            });
            return (engine, sessions);
        }

        [Fact]
        public async Task Renaming_a_kpis_base_measure_rewrites_references_to_its_generated_helpers_everywhere()
        {
            var (engine, sessions) = await KpiModelAsync();
            using (engine)
            {
                await engine.RenameObjectAsync("measure:Sales/Achievement", "Progress", "human");
                var (status, trend, target, reader, qualified, other) = await sessions.Require().ReadAsync(m =>
                {
                    var k = m.Tables["Sales"].Measures["Progress"].KPI;
                    return (k.StatusExpression, k.TrendExpression, k.TargetExpression, m.Tables["Sales"].Measures["Reader"].Expression,
                            m.Tables["Sales"].Measures["Qualified Reader"].Expression, m.Tables["Other"].Measures["Other Reader"].Expression);
                });
                Assert.Equal("DIVIDE ( [Progress], [_Progress Goal] )", status);
                Assert.Equal("'Sales'[_Progress Trend] + [_Progress Status]", trend);
                Assert.Equal("1", target);
                Assert.Equal("[_Progress Goal]", reader);
                Assert.Equal("Sales[_Progress Goal] + 'Sales'[_Progress Status]", qualified);
                Assert.Equal("[_Progress Goal] + Sales[_Progress Trend]", other);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_kpi_helper_rename_is_undone_and_redone_whole()
        {
            var (engine, sessions) = await KpiModelAsync();
            using (engine)
            {
                await engine.RenameObjectAsync("measure:Sales/Achievement", "Progress", "human");
                await engine.UndoAsync("human");
                Assert.Equal("[_Achievement Goal]", await sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Reader"].Expression));
                Assert.Equal("DIVIDE ( [Achievement], [_Achievement Goal] )", await sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Achievement"].KPI.StatusExpression));
                await engine.RedoAsync("human");
                Assert.Equal("[_Progress Goal]", await sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Reader"].Expression));
            }
        }

        /// <summary>A real measure that happens to carry a helper's name is a real measure: the rename of the KPI measure leaves references to it alone.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_real_measure_named_like_a_helper_is_not_taken_for_the_helper()
        {
            var (engine, sessions) = await KpiModelAsync();
            using (engine)
            {
                await sessions.Require().MutateAsync("human", "real", m => m.Tables["Sales"].AddMeasure("_Achievement Goal", "5"));
                await engine.RenameObjectAsync("measure:Sales/Achievement", "Progress", "human");
                Assert.Equal("[_Achievement Goal]", await sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Reader"].Expression));
            }
        }

        [Theory]
        [InlineData("\"RETURN [Total]\" & SUM('{T}'[{C}])")]
        public Task A_keyword_in_a_string_or_a_comment_is_left_alone_and_the_formula_still_follows_the_rename_sample(string formula) => A_keyword_in_a_string_or_a_comment_is_left_alone_and_the_formula_still_follows_the_rename(formula);

        /// <summary>Text the walk must leave alone: a keyword inside a string, a comment, or a column NAMED like a keyword.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("\"RETURN [Total]\" & SUM('{T}'[{C}])")]
        [InlineData("/* RETURN [Total] */ SUM('{T}'[{C}])")]
        [InlineData("SUM('{T}'[{C}]) // NOT [Total]")]
        public async Task A_keyword_in_a_string_or_a_comment_is_left_alone_and_the_formula_still_follows_the_rename(string formula)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Pro());
            await engine.CreateModelAsync("Fixup", 1601);
            var s = sessions.Require();
            var before = formula.Replace("{T}", Table).Replace("{C}", Column);
            await s.MutateAsync("human", "seed", m =>
            {
                var t = m.AddTable(Table);
                t.AddDataColumn(Column, Column, null, DataType.Int64);
                t.AddMeasure(Measure, "1");
                t.AddMeasure("Host", before);
            });
            await engine.RenameObjectAsync("column:Sales/Qty", NewColumn, "human");
            Assert.Equal(formula.Replace("{T}", Table).Replace("{C}", NewColumn), await s.ReadAsync(m => m.Tables[Table].Measures["Host"].Expression));
        }
    }
}
