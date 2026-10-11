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
    /// Verifier attack on m11/fixup (the patched TE2 FormulaFixup). FormulaFixupTests proves the shapes the builder named; these add
    /// the places and shapes it did not: detail rows expressions (a measure's and a table's default), a calculated table, a function
    /// body, multi-line formulas with line and block comments, nested VAR bodies, lowercase NOT and IN, a string holding references,
    /// an escaped ]] name, and tables NAMED like the excluded keywords ('Return', 'Not') renamed while the keyword itself stands in
    /// the formula. Doors: rename_object one by one, all five renames in one batch, and undo then redo. apply_tmdl then rename is in
    /// VerifyFixupTmdlDefectTests: it fails on the base too, for a reason outside the fixup.
    /// </summary>
    public sealed class VerifyFixupTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        // Every name in canonical form so the expected text is exact: 'Sales'[Qty], [Total], 'Sales'[Total], 'Return'[Qty], 'Not'.
        // TE2 writes a renamed measure's full name unqualified, so 'Sales'[Total] becomes [Total Sales]: the same measure.
        public static IEnumerable<object[]> Shapes() => new[]
        {
            new object[] { "multi-line with comments",
                "VAR a =\n    SUM ( 'Sales'[Qty] )\n-- RETURN [Total] 'Sales'[Qty]\nRETURN\n    a + [Total] /* NOT [Total] */ + 'Sales'[Total] // IN [Total]",
                "VAR a =\n    SUM ( 'Sales Facts'[Quantity] )\n-- RETURN [Total] 'Sales'[Qty]\nRETURN\n    a + [Total Sales] /* NOT [Total] */ + [Total Sales] // IN [Total]" },
            new object[] { "nested var body",
                "VAR x = VAR y = [Total] RETURN NOT y RETURN x + SUM('Sales'[Qty]) + VAR z = 'Sales'[Total] RETURN z",
                "VAR x = VAR y = [Total Sales] RETURN NOT y RETURN x + SUM('Sales Facts'[Quantity]) + VAR z = [Total Sales] RETURN z" },
            new object[] { "lowercase not and in",
                "if(not [Total], 0, sum('Sales'[Qty])) + if(1 in {[Total]}, 1, 0) + if(1 and [Total] or [Total], 'Sales'[Total])",
                "if(not [Total Sales], 0, sum('Sales Facts'[Quantity])) + if(1 in {[Total Sales]}, 1, 0) + if(1 and [Total Sales] or [Total Sales], [Total Sales])" },
            new object[] { "string holding references",
                "\"RETURN [Total] 'Sales'[Qty]\" & NOT [Total] & SUM('Sales'[Qty])",
                "\"RETURN [Total] 'Sales'[Qty]\" & NOT [Total Sales] & SUM('Sales Facts'[Quantity])" },
            new object[] { "escaped bracket name",
                "SUM('Sales'[Odd]]Name]) + NOT [Total] + SUM('Sales'[Qty])",
                "SUM('Sales Facts'[Odd]]Name]) + NOT [Total Sales] + SUM('Sales Facts'[Quantity])" },
            new object[] { "table named Return beside the RETURN keyword",
                "VAR a = 1 RETURN SUM('Return'[Qty]) + a + [Total]",
                "VAR a = 1 RETURN SUM('Returns Facts'[Qty]) + a + [Total Sales]" },
            new object[] { "table named Not beside the NOT keyword",
                "IF(NOT [Total], COUNTROWS('Not'), SUM('Sales'[Qty]))",
                "IF(NOT [Total Sales], COUNTROWS('Nots'), SUM('Sales Facts'[Quantity]))" },
        };

        private static readonly (string Ref, string To)[] Renames =
        {
            ("table:Sales", "Sales Facts"),
            ("column:Sales Facts/Qty", "Quantity"),
            ("measure:Sales Facts/Total", "Total Sales"),
            ("table:Return", "Returns Facts"),
            ("table:Not", "Nots"),
        };

        // Lean suite (8 Oct): the default run keeps two rows that carry the unusual parts (comments plus RETURN, and a table named like the
        // RETURN keyword) through the two doors a person and an agent use; the whole 7-shape matrix through all three doors, undo and redo
        // included, is the sweep.
        public static IEnumerable<object[]> BatchSample() => Shapes().Where(r => (string)r[0] == "multi-line with comments");
        public static IEnumerable<object[]> RenameObjectSample() => Shapes().Where(r => ((string)r[0]).StartsWith("table named Return"));

        private static async Task<(LocalEngine Engine, SessionManager Sessions)> ModelAsync(string formula)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Pro());
            await engine.CreateModelAsync("VerifyFixup", 1702);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable("Sales");
                sales.AddDataColumn("Qty", "Qty", null, DataType.Int64);
                sales.AddDataColumn("Odd]Name", "Odd", null, DataType.Int64);
                sales.AddMeasure("Total", "SUM('Sales'[Qty])");
                m.AddTable("Return").AddDataColumn("Qty", "Qty", null, DataType.Int64);
                m.AddTable("Not").AddDataColumn("Qty", "Qty", null, DataType.Int64);

                var host = sales.AddMeasure("Host", formula);
                host.DetailRowsExpression = formula;
                host.FormatStringExpression = formula;
                host.AddKPI().StatusExpression = formula;
                sales.DefaultDetailRowsExpression = formula;
                sales.AddCalculatedColumn("Calc", formula);
                m.AddCalculatedTable("Calc Table", formula);
                var item = m.AddCalculationGroup("Time Calc").AddCalculationItem("Current", formula);
                item.FormatStringExpression = formula;
                m.AddRole("Readers").AddOrGetTablePermission(sales).FilterExpression = formula;
            });
            return (engine, sessions);
        }

        private static Task<List<(string Where, string Text)>> FormulasAsync(SessionManager sessions) =>
            sessions.Require().ReadAsync(m =>
            {
                var sales = m.Tables.Single(t => t.Name == "Sales" || t.Name == "Sales Facts");
                var host = sales.Measures["Host"];
                var item = m.CalculationGroups.Single().CalculationItems.Single();
                return new List<(string, string)>
                {
                    ("measure", host.Expression),
                    ("measure detail rows", host.DetailRowsExpression),
                    ("measure format string", host.FormatStringExpression),
                    ("kpi status", host.KPI.StatusExpression),
                    ("table default detail rows", sales.DefaultDetailRowsExpression),
                    ("calculated column", ((CalculatedColumn)sales.Columns["Calc"]).Expression),
                    ("calculated table", ((CalculatedTable)m.Tables["Calc Table"]).Expression),
                    ("calculation item", item.Expression),
                    ("calculation item format string", item.FormatStringExpression),
                    ("role row filter", m.Roles.Single().TablePermissions.Single(tp => tp.Table == sales).FilterExpression),
                };
            });

        private static void AssertAll(string shape, string expected, List<(string Where, string Text)> formulas)
        {
            var wrong = formulas.Where(f => f.Text != expected).Select(f => $"{f.Where}: {f.Text}").ToList();
            Assert.True(wrong.Count == 0, $"shape '{shape}', expected everywhere:\n  {expected}\nwrong in {wrong.Count} place(s):\n  " + string.Join("\n  ", wrong));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Shapes))]
        public async Task Five_renames_through_rename_object_leave_every_place_whole(string shape, string formula, string expected)
        {
            var (engine, sessions) = await ModelAsync(formula);
            using (engine)
            {
                foreach (var (r, to) in Renames) Assert.True((await engine.RenameObjectAsync(r, to, "human")).Changed, r);
                AssertAll(shape, expected, await FormulasAsync(sessions));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Shapes))]
        public async Task Five_renames_in_one_batch_leave_every_place_whole(string shape, string formula, string expected)
        {
            var (engine, sessions) = await ModelAsync(formula);
            using (engine)
            {
                var s = sessions.Require();
                await s.MutateAsync("human", "script", m =>
                {
                    foreach (var (r, to) in Renames) s.Rename((TabularNamedObject)ObjectRefs.Resolve(m, r), to);
                });
                AssertAll(shape, expected, await FormulasAsync(sessions));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Shapes))]
        public async Task Undo_of_five_renames_restores_every_place_and_redo_brings_them_back(string shape, string formula, string expected)
        {
            var (engine, sessions) = await ModelAsync(formula);
            using (engine)
            {
                foreach (var (r, to) in Renames) await engine.RenameObjectAsync(r, to, "human");
                for (var i = 0; i < Renames.Length; i++) await engine.UndoAsync("human");
                AssertAll(shape, formula, await FormulasAsync(sessions));
                for (var i = 0; i < Renames.Length; i++) await engine.RedoAsync("human");
                AssertAll(shape, expected, await FormulasAsync(sessions));
            }
        }

        [Theory]
        [MemberData(nameof(RenameObjectSample))]
        public Task Five_renames_through_rename_object_leave_every_place_whole_sample(string shape, string formula, string expected) =>
            Five_renames_through_rename_object_leave_every_place_whole(shape, formula, expected);

        [Theory]
        [MemberData(nameof(BatchSample))]
        public Task Five_renames_in_one_batch_leave_every_place_whole_sample(string shape, string formula, string expected) =>
            Five_renames_in_one_batch_leave_every_place_whole(shape, formula, expected);

        /// <summary>A user-defined function's body is walked by the full rebuild too: the same shapes after RETURN and NOT.</summary>
        [Fact]
        public async Task A_function_body_follows_the_renames()
        {
            var (engine, sessions) = await ModelAsync("1");
            using (engine)
            {
                const string body = "() => VAR a = 1 RETURN IF(NOT [Total], 0, SUM('Sales'[Qty]) + a)";
                await sessions.Require().MutateAsync("human", "fn", m => m.AddFunction("Fn").Expression = body);
                foreach (var (r, to) in Renames) await engine.RenameObjectAsync(r, to, "human");
                Assert.Equal("() => VAR a = 1 RETURN IF(NOT [Total Sales], 0, SUM('Sales Facts'[Quantity]) + a)",
                    await sessions.Require().ReadAsync(m => m.Functions["Fn"].Expression));
            }
        }
    }
}
