// Verifier, m11/psc4 fix round 2, 4 Oct. Attacks on R1 (the broken-formula check gives no finding on valid DAX, still finds broken DAX), R2/R4 (a rename after a reload or
// inside a batch keeps every formula whole) and R5 (date links on a header fact and on a staff dimension). A test left failing here names a defect in the lane; a passing one is
// evidence the attack did not land.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifyPsc4Fixes2Tests
    {
        private static async Task<(LocalEngine Engine, Session S)> ModelAsync(Action<Model> build, int cl = 1604)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Free);
            await engine.CreateModelAsync("VerifyPsc4b", cl);
            var s = sessions.Require();
            if (build != null) await s.MutateAsync("human", "build", build);
            return (engine, s);
        }

        private static void Model3(Model m)
        {
            var t = m.AddTable("Sales");
            t.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
            t.AddDataColumn("Order Date", "Order Date", null, DataType.DateTime);
            t.AddDataColumn("Region", "Region", null, DataType.String);
            t.AddDataColumn("Net -- Gross", "Net -- Gross", null, DataType.Decimal);
            t.AddMeasure("Total Amount", "SUM ( 'Sales'[Amount] )");
            t.AddMeasure("Owner's Total", "SUM ( 'Sales'[Amount] )");
            var b = m.AddTable("Budget");
            b.AddDataColumn("Plan", "Plan", null, DataType.Decimal);
            b.AddMeasure("Total Plan", "SUM ( 'Budget'[Plan] )");
            b.AddMeasure("Amount", "SUM ( 'Budget'[Plan] )");   // a measure named like a column of another table
            var bob = m.AddTable("Bob's Data");
            bob.AddDataColumn("Amt", "Amt", null, DataType.Decimal);
            bob.AddMeasure("Bob's Measure", "SUM ( 'Bob''s Data'[Amt] )");
            var g = m.AddCalculationGroup("Time");
            g.AddCalculationItem("YTD", "CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'Sales'[Order Date] ) )");
            var ratio = g.AddCalculationItem("Ratio", "IF ( ISSELECTEDMEASURE ( [Total Amount], [Owner's Total] ), DIVIDE ( SELECTEDMEASURE (), [Total Plan] ), SELECTEDMEASURE () )");
            ratio.FormatStringExpression = "IF ( ISSELECTEDMEASURE ( [Total Amount] ), \"0.0%\", SELECTEDMEASUREFORMATSTRING () )";
        }

        private static string[] Found(Model m) => FormulaReferences.Scan(m).Select(b => b.Object.Name + " (" + b.Formula + "): " + string.Join(" | ", b.Missing)).ToArray();

        // ------------------------------------------------------------------ R1: valid DAX, new shapes beyond the builder's 25 probes

        public static IEnumerable<object[]> Valid() => new (string What, string Dax)[]
        {
            ("nested VAR tables", "VAR a = SUMMARIZE ( 'Sales', 'Sales'[Region], \"Tot\", [Total Amount] ) VAR b = FILTER ( a, [Tot] > 0 ) RETURN SUMX ( b, [Tot] )"),
            ("a VAR inside an argument", "SUMX ( VAR t = FILTER ( 'Sales', 'Sales'[Amount] > 0 ) RETURN t, 'Sales'[Amount] )"),
            ("an underscore table variable", "VAR _t = TOPN ( 5, 'Sales', 'Sales'[Amount] ) RETURN SUMX ( _t, _t[Amount] )"),
            ("SUMMARIZE new column", "SUMX ( SUMMARIZE ( 'Sales', 'Sales'[Region], \"Region Total\", [Total Amount] ), [Region Total] )"),
            ("GROUPBY with CURRENTGROUP", "SUMX ( GROUPBY ( 'Sales', 'Sales'[Region], \"M\", SUMX ( CURRENTGROUP (), 'Sales'[Amount] ) ), [M] )"),
            ("ADDCOLUMNS read through the variable", "VAR t = ADDCOLUMNS ( VALUES ( 'Sales'[Region] ), \"@Net\", [Total Amount] ) RETURN MAXX ( t, t[@Net] ) + MAXX ( t, [@Net] )"),
            ("SUMMARIZECOLUMNS", "COUNTROWS ( SUMMARIZECOLUMNS ( 'Sales'[Region], \"T\", [Total Amount] ) )"),
            ("TREATAS one column", "CALCULATE ( [Total Amount], TREATAS ( { \"N\", \"S\" }, 'Sales'[Region] ) )"),
            ("TREATAS two columns", "CALCULATE ( [Total Amount], TREATAS ( { ( \"N\", 1 ) }, 'Sales'[Region], 'Sales'[Amount] ) )"),
            ("TREATAS from another table", "CALCULATE ( [Total Amount], TREATAS ( VALUES ( 'Budget'[Plan] ), 'Sales'[Amount] ) )"),
            ("NATURALINNERJOIN", "COUNTROWS ( NATURALINNERJOIN ( Sales, Budget ) )"),
            ("NATURALLEFTOUTERJOIN with an escaped quote", "COUNTROWS ( NATURALLEFTOUTERJOIN ( 'Sales', 'Bob''s Data' ) )"),
            ("ROW two columns", "MAXX ( ROW ( \"Total\", [Total Amount], \"Plan\", [Total Plan] ), [Total] - [Plan] )"),
            ("NAMEOF", "NAMEOF ( 'Sales'[Total Amount] ) & NAMEOF ( [Total Plan] ) & NAMEOF ( Budget[Amount] ) & NAMEOF ( 'Sales'[Region] )"),
            ("a field parameter table", "{ ( \"Amount\", NAMEOF ( 'Sales'[Total Amount] ), 0 ), ( \"Plan\", NAMEOF ( 'Budget'[Total Plan] ), 1 ) }"),
            ("escaped quote table, space before the column", "SUMX ( 'Bob''s Data', 'Bob''s Data' [Amt] )"),
            ("escaped quote table and its measure", "'Bob''s Data'[Bob's Measure] + 1"),
            ("comment holding brackets and quotes", "[Total Amount] // 'Ghost'[X] and [Ghost] it's\n + [Total Plan] /* ] ' [ \" */"),
            ("a name holding comment marks", "SUMX ( 'Sales', 'Sales'[Net -- Gross] ) + SUMX ( 'Sales', [Net -- Gross] )"),
            ("a string holding a doubled quote and brackets", "IF ( [Total Amount] > 0, \"say \"\"[hi]\"\" it's\", \"x\" )"),
            ("measure and column with one name: the column", "SUM ( Sales[Amount] )"),
            ("measure and column with one name: the measure", "Budget[Amount] * 2 + 'Budget'[Amount]"),
            ("case: table and measure", "sales[total amount] + SUM ( 'SALES'[amount] ) + [TOTAL PLAN]"),
            ("case: GENERATESERIES", "sumx ( generateseries ( 1, 3, 1 ), [VALUE] )"),
            ("case: VAR", "var T = filter ( sales, sales[amount] > 0 ) return sumx ( t, t[AMOUNT] )"),
            ("case: CALENDAR", "maxx ( calendar ( date ( 2020, 1, 1 ), date ( 2020, 2, 1 ) ), [DATE] )"),
            ("case: IN then a constructor", "CALCULATE ( [Total Amount], 'Sales'[Region] in { \"N\" } ) + sumx ( { 1 }, [value] )"),
            ("a date literal", "CALCULATE ( [Total Amount], 'Sales'[Order Date] >= dt\"2020-01-01\" )"),
            ("a tuple IN", "CALCULATE ( [Total Amount], KEEPFILTERS ( ( 'Sales'[Region], 'Sales'[Amount] ) IN { ( \"N\", 1 ) } ) )"),
            ("WINDOW over a constructor", "SUMX ( WINDOW ( 1, ABS, 2, ABS, { 3, 1, 2 }, ORDERBY ( [Value], ASC ) ), [Value] )"),
            ("INDEX on model columns", "CALCULATE ( [Total Amount], INDEX ( 1, ALLSELECTED ( 'Sales'[Region] ), ORDERBY ( 'Sales'[Region], DESC ) ) )"),
            ("EARLIER", "SUMX ( FILTER ( 'Sales', 'Sales'[Region] = EARLIER ( 'Sales'[Region] ) ), 'Sales'[Amount] )"),
            ("DATATABLE column read", "MAXX ( DATATABLE ( \"K\", INTEGER, { { 1 } } ), [K] )"),
            ("the calculation group column", "CALCULATE ( [Total Amount], 'Time'[Name] IN { \"YTD\", \"Ratio\" } )"),
        }.Select(p => new object[] { p.What, p.Dax });

        public static IEnumerable<object[]> ValidSample_Valid_DAX_of_new_shapes_() { var all = Valid().ToArray(); return new[] { all[5] }; }
        [Theory]
        [MemberData(nameof(ValidSample_Valid_DAX_of_new_shapes_))]
        public Task Valid_DAX_of_new_shapes_gives_no_broken_formula_finding_sample(string what, string dax) => Valid_DAX_of_new_shapes_gives_no_broken_formula_finding(what, dax);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Valid))]
        public async Task Valid_DAX_of_new_shapes_gives_no_broken_formula_finding(string what, string dax)
        {
            var (engine, s) = await ModelAsync(m => { Model3(m); m.Tables["Sales"].AddMeasure("Probe", dax); });
            using (engine)
            {
                Assert.True(what.Length > 0);
                Assert.Empty(await s.ReadAsync(Found));
                var checker = await s.ReadAsync(m => FormulaReferences.Checker(m));
                Assert.Empty(checker(dax));
            }
        }

        // A KPI makes three measures of its own, _<measure> Goal, _<measure> Status and _<measure> Trend, that "act as regular measures; you can call them in your DAX code"
        // (SQLBI, Creating KPIs in Power BI Desktop). The status formula Visual Studio writes for a KPI is DIVIDE ( [Total sales], [_Total sales Goal] ). Valid DAX.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_KPI_status_that_reads_its_goal_measure_is_not_a_broken_formula()
        {
            var (engine, s) = await ModelAsync(m =>
            {
                Model3(m);
                var kpi = m.Tables["Sales"].Measures["Total Amount"].AddKPI();
                kpi.TargetExpression = "[Total Plan]";
                kpi.StatusExpression = "VAR x = DIVIDE ( [Total Amount], [_Total Amount Goal] ) RETURN IF ( ISBLANK ( x ), BLANK (), IF ( x < 0.95, -1, IF ( x < 1, 0, 1 ) ) )";
                kpi.TrendExpression = "1";
            });
            using (engine) Assert.Empty(await s.ReadAsync(Found));
        }

        // Calendar-based time intelligence (CL 1701, which Semanticus builds: define_calendar and the Gregorian template): the calendar is named where a date column would be,
        // TOTALYTD ( [Sales], 'Fiscal Calendar' ) (Microsoft Learn, TOTALYTD). Valid DAX on a model with that calendar.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Calendar_based_time_intelligence_on_a_template_calendar_is_not_a_broken_formula()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("VerifyPsc4cal", 1701);
            var r = await engine.DefineCalendarFromTemplateAsync("gregorian", null, null, 7, null, null, null, "agent");
            Assert.Equal("Gregorian", r.Calendar);
            var s = sessions.Require();
            var templateOnly = await s.ReadAsync(Found);
            await s.MutateAsync("human", "measure", m =>
            {
                var t = m.AddTable("Sales");
                t.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
                t.AddMeasure("Total Amount", "SUM ( 'Sales'[Amount] )");
                t.AddMeasure("Amount YTD", "TOTALYTD ( [Total Amount], 'Gregorian' )");
                t.AddMeasure("Amount YTD 2", "CALCULATE ( [Total Amount], DATESYTD ( 'Gregorian' ) )");
            });
            Assert.Empty(templateOnly);
            Assert.Empty(await s.ReadAsync(Found));
        }

        // LINESTX returns a table with columns of its own (Slope1, Intercept, ...), like GENERATESERIES does for Value.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task LINESTX_result_columns_are_the_formula_own()
        {
            var (engine, s) = await ModelAsync(m => { Model3(m); m.Tables["Sales"].AddMeasure("Probe", "VAR l = LINESTX ( 'Sales', 'Sales'[Amount], 'Sales'[Order Date] ) RETURN MAXX ( l, [Slope1] )"); });
            using (engine) Assert.Empty(await s.ReadAsync(Found));
        }

        // ------------------------------------------------------------------ R1: no false negative on broken DAX of the same shapes

        public static IEnumerable<object[]> Broken() => new (string Dax, string What)[]
        {
            ("sales[ghost measure] * 2", "Table 'sales' has no column 'ghost measure'"),
            ("VAR _t = 'Ghost' RETURN COUNTROWS ( _t )", "Unknown table 'Ghost'"),
            ("SUMX ( SUMMARIZE ( 'Sales', 'Sales'[Region], \"Region Total\", [Total Amount] ), [Region Totals] )", "Unknown measure or column '[Region Totals]'"),
            ("CALCULATE ( [Total Amount], TREATAS ( { \"N\" }, 'Sales'[Regoin] ) )", "Table 'Sales' has no column 'Regoin'"),
            ("'Bob''s Data'[Bob's Ghost] + 1", "Table 'Bob's Data' has no column 'Bob's Ghost'"),
            ("Budget[Total Amount]", "Table 'Budget' has no column 'Total Amount'"),
            ("NAMEOF ( 'Sales'[Ghost] )", "Table 'Sales' has no column 'Ghost'"),
            ("[Total Amount] /* fine */ + [Ghost] // gone", "Unknown measure or column '[Ghost]'"),
        }.Select(p => new object[] { p.Dax, p.What });

        public static IEnumerable<object[]> BrokenSample_Broken_DAX_of_the_same_s() { var all = Broken().ToArray(); return new[] { all[0] }; }
        [Theory]
        [MemberData(nameof(BrokenSample_Broken_DAX_of_the_same_s))]
        public Task Broken_DAX_of_the_same_shapes_is_still_found_sample(string dax, string what) => Broken_DAX_of_the_same_shapes_is_still_found(dax, what);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Broken))]
        public async Task Broken_DAX_of_the_same_shapes_is_still_found(string dax, string what)
        {
            var (engine, s) = await ModelAsync(m => { Model3(m); m.Tables["Sales"].AddMeasure("Probe", dax); });
            using (engine)
            {
                var found = await s.ReadAsync(m => FormulaReferences.Scan(m).ToList());
                var b = Assert.Single(found);
                Assert.Contains(what + ".", b.Missing);
            }
        }

        // ------------------------------------------------------------------ R2: reload paths, then a rename

        [Fact]
        public async Task Calendar_template_formulas_follow_a_rename_after_undo_and_redo()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("VerifyPsc4cal2", 1701);
            await engine.DefineCalendarFromTemplateAsync("gregorian", null, null, 7, null, null, null, "agent");
            var s = sessions.Require();
            string Year(Model m) => m.Tables["Date"].Columns["Year"] is CalculatedColumn cc ? cc.Expression : "(not calculated)";
            var made = await s.ReadAsync(Year);
            Assert.Contains("[Date]", made);
            await engine.UndoAsync("human");
            await engine.RedoAsync("human");
            await engine.RenameObjectAsync("column:Date/Date", "Day", "agent");
            var after = await s.ReadAsync(Year);
            Assert.DoesNotContain("[Date]", after);
            Assert.Contains("[Day]", after);
        }

        // R4's comparison skips the rebuild when no formula text and no object changed. A formula that named a missing object starts to resolve when an object is renamed to that
        // name, with no text or object change, so the links still lack it; a second rename in the same batch then leaves it behind. Base (eeb01571) rebuilt here.
        [Fact]
        public async Task A_reference_that_a_rename_makes_resolve_follows_a_second_rename_in_the_same_batch()
        {
            var (engine, s) = await ModelAsync(m =>
            {
                var t = m.AddTable("Sales");
                t.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
                t.AddMeasure("Later", "SUM ( 'Sales'[Amt] )");   // names a column that a rename will make
            });
            using (engine)
            {
                await s.MutateAsync("human", "two renames in one batch", m =>
                {
                    s.Rename(m.Tables["Sales"].Columns["Amount"], "Amt");
                    s.Rename(m.Tables["Sales"].Columns["Amt"], "Value");
                });
                Assert.Equal("SUM ( 'Sales'[Value] )", await s.ReadAsync(m => m.Tables["Sales"].Measures["Later"].Expression));
            }
        }

        [Fact]
        public async Task Apply_tmdl_inside_one_batch_with_renames_keeps_every_formula_whole()
        {
            var (engine, s) = await ModelAsync(m =>
            {
                var t = m.AddTable("Sales");
                t.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
                t.AddDataColumn("Region", "Region", null, DataType.String);
                t.AddMeasure("Total Amount", "SUM ( 'Sales'[Amount] )");
                t.AddMeasure("Scripted", "1");
            });
            using (engine)
            {
                var tmdl = await engine.ScriptObjectsAsync(new[] { "measure:Sales/Scripted" }, "tmdl");
                await engine.ApplyTmdlScriptAsync(tmdl.Replace("measure Scripted = 1", "measure Scripted = SUM ( 'Sales'[Region] ) + [Total Amount]"), "agent");
                await engine.UndoAsync("human");
                await engine.RedoAsync("human");
                await s.MutateAsync("human", "renames", m =>
                {
                    s.Rename(m.Tables["Sales"].Columns["Region"], "Area");
                    s.Rename(m.Tables["Sales"].Measures["Total Amount"], "Amount Total");
                    s.Rename(m.Tables["Sales"], "Orders");
                });
                Assert.Equal("SUM ( 'Orders'[Area] ) + [Amount Total]", await s.ReadAsync(m => m.Tables["Orders"].Measures["Scripted"].Expression.Trim()));
                Assert.Empty(await s.ReadAsync(Found));
            }
        }

        // ------------------------------------------------------------------ R5: the dimension hire-date shape

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };

        private static FabricSchema StaffAndTimesheet(bool timesheetHasDate) => new FabricSchema
        {
            Server = "x", Database = "HR", ForeignKeys = new FabricForeignKey[0],
            Tables = new[]
            {
                new FabricTable { Schema = "dbo", Name = "dim_date", KeyColumns = new string[0], Columns = new[] { C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime") } },
                new FabricTable { Schema = "dbo", Name = "dim_staff", KeyColumns = new string[0], Columns = new[] { C("STAFF_ID", "Int64"), C("STAFF_NM", "String"), C("HIRE_DT_KEY", "Int64") } },
                new FabricTable { Schema = "dbo", Name = "fact_timesheet", KeyColumns = new string[0], Columns = timesheetHasDate
                    ? new[] { C("ROW_ID", "Int64"), C("STAFF_ID", "Int64"), C("WORK_DT_KEY", "Int64"), C("HRS_AMT", "Decimal") }
                    : new[] { C("ROW_ID", "Int64"), C("STAFF_ID", "Int64"), C("HRS_AMT", "Decimal") } },
            },
        };

        [Fact]
        public void A_staff_hire_date_is_not_a_date_link_when_the_fact_has_its_own_date()
        {
            var m = StructureManifest.Derive(StaffAndTimesheet(true), null, null, "seed", "x", "HR", "import", LinkGuessFacts.NamesOnly());
            Assert.DoesNotContain(m.Links, l => l.FromTable == "dim_staff" && l.ToTable == "dim_date");
            Assert.Contains(m.Links, l => l.FromTable == "fact_timesheet" && l.ToTable == "dim_date");
        }

        // ------------------------------------------------------------------ R6: the words name the choice the way the SQL source's own screen does

        // A SQL source's sign-in is set in Connections by the SQL source form (connectionshub.tsx, <AccountPicker mode={form.authMode} ...>), whose choices are SIGN_IN_MODES in
        // copy.ts: the browser way reads "Sign in in a browser". "Browser sign-in" is the label of a different form (Add a model by its address), which a SQL source never shows.
        [Fact]
        public void The_sign_in_words_name_the_browser_choice_the_way_the_SQL_source_form_shows_it()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var src = System.IO.Path.Combine(dir!.FullName, "Semanticus.VSCode", "webview", "src");
            var hub = System.IO.File.ReadAllText(System.IO.Path.Combine(src, "connectionshub.tsx"));
            Assert.Contains("<AccountPicker mode={form.authMode}", hub);   // the SQL source form picks its sign-in with the account picker
            var picker = System.IO.File.ReadAllText(System.IO.Path.Combine(src, "accountpicker.tsx"));
            Assert.Contains("SIGN_IN_MODES.map", picker);
            var copy = System.IO.File.ReadAllText(System.IO.Path.Combine(src, "copy.ts"));
            var label = System.Text.RegularExpressions.Regex.Match(copy, "value: 'interactive', label: '([^']+)'").Groups[1].Value;
            Assert.Equal("Sign in in a browser", label);
            Assert.Contains(label, StartSourceCopy.NoSignIn());
            Assert.Contains(label, StartSourceCopy.SignInRefused());
        }
    }
}
