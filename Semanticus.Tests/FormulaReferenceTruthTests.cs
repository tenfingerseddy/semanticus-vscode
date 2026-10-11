using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Plan statement check 4, fix round 2, R1: the broken-formula check says a formula is broken only when it names an object the model does not have. It reads a formula as tokens
    /// (DaxScan), not as text, so valid DAX is never reported (a wrong grade shown as right): a measure written with its table, the columns a table constructor or GENERATESERIES make,
    /// an apostrophe or ]] inside a [name], a string that holds brackets. And it still finds what is missing.
    /// </summary>
    public sealed class FormulaReferenceTruthTests : IDisposable
    {
        private readonly List<string> _dirs = new List<string>();

        public void Dispose()
        {
            foreach (var d in _dirs) { try { Directory.Delete(d, true); } catch { } }
        }

        private static async Task<(LocalEngine Engine, Session S)> ModelAsync(Action<Model> build)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Free);
            await engine.CreateModelAsync("FormulaTruth", 1604);
            var s = sessions.Require();
            await s.MutateAsync("human", "build", build);
            return (engine, s);
        }

        private static void Sales(Model m)
        {
            var t = m.AddTable("Sales");
            t.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
            t.AddDataColumn("Order Date", "Order Date", null, DataType.DateTime);
            t.AddDataColumn("Region", "Region", null, DataType.String);
            t.AddDataColumn("Size [cm]", "Size [cm]", null, DataType.Decimal);
            t.AddMeasure("Total Amount", "SUM ( 'Sales'[Amount] )");
            t.AddMeasure("Owner's Total", "SUM ( 'Sales'[Amount] )");
            var other = m.AddTable("Budget");
            other.AddDataColumn("Plan", "Plan", null, DataType.Decimal);
            other.AddMeasure("Total Plan", "SUM ( 'Budget'[Plan] )");
            m.AddTable("Bob's Data").AddDataColumn("Amt", "Amt", null, DataType.Decimal);
        }

        // Valid DAX the first version of the check reported (the verifier's 14 probes, VerifyPsc4FixesTests), and more of the same kinds.
        public static IEnumerable<object[]> Valid() => new[]
        {
            "SUMX ( { 1, 2, 3 }, [Value] )",
            "MAXX ( { ( 1, 2 ) }, [Value2] )",
            "SUMX ( { ( 1, 2 ) }, [Value1] + [Value2] )",
            "SUMX ( GENERATESERIES ( 1, 10, 1 ), [Value] )",
            "'Sales'[Total Amount] * 2",
            "Sales[Total Amount] * 2",
            "CALCULATE ( Sales[Total Amount], Sales[Region] = \"N\" )",
            "DIVIDE ( [Owner's Total], [Owner's Total] + 1 )",
            "[Owner's Total] + SUM ( 'Sales'[Amount] )",
            "IF ( [Owner's Total] < 0, \"#,0;[Red]-#,0\", \"#,0\" )",
            "IF ( [Owner's Total] < 0, \"it's\", \"#,0\" ) + SUM ( 'Sales'[Amount] )",
            "SUM ( 'Sales'[Size [cm]]] )",
            "SUMX ( 'Bob''s Data', 'Bob''s Data'[Amt] )",
            "VAR t = FILTER ( 'Sales', 'Sales'[Amount] > 0 ) RETURN SUMX ( t, t[Amount] )",
            "SUMX ( SELECTCOLUMNS ( 'Sales', \"Amt\", 'Sales'[Amount] ), [Amt] )",
            "SUMX ( ADDCOLUMNS ( VALUES ( 'Sales'[Region] ), \"Net\", [Total Amount] ), [Net] )",
            "COUNTROWS ( DATATABLE ( \"K\", INTEGER, { { 1 }, { 2 } } ) )",
            "VAR x = [Total Amount] RETURN x / 2",
            "VAR x = [Owner's Total] VAR t = FILTER ( 'Sales', 'Sales'[Amount] > x ) RETURN SUMX ( t, t[Amount] )",
            "CALCULATE ( [Total Amount], 'Time'[Name] = \"YTD\" )",
            "COUNTROWS ( CALENDAR ( DATE ( 2020, 1, 1 ), DATE ( 2020, 12, 31 ) ) ) + MAXX ( CALENDAR ( DATE ( 2020, 1, 1 ), DATE ( 2020, 12, 31 ) ), [Date] )",
            "SUMX ( ROW ( \"A\", 1 ), [A] )",
            "CALCULATE ( [Total Amount], 'Sales'[Region] IN { \"N\", \"S\" } )",
            "[Total Amount] -- [Ghost] in a comment\n + 1",
            "/* 'Ghost'[X] */ [Total Amount]",
        }.Select(f => new object[] { f });

        // Lean suite (8 Oct): each row of the two theories below builds its own engine and model (about a second each, 40 rows). The default
        // run judges the same formulas on ONE model, one measure each: a formula is read on its own, so the verdict is the same. The theories
        // are the sweep.
        [Fact]
        public async Task Valid_DAX_of_every_row_is_not_reported_as_a_formula_that_names_a_missing_object_on_one_model()
        {
            var rows = Valid().Select(r => (string)r[0]).ToArray();
            var (engine, s) = await ModelAsync(m =>
            {
                Sales(m);
                m.AddCalculationGroup("Time").AddCalculationItem("YTD", "CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'Sales'[Order Date] ) )");
                for (var i = 0; i < rows.Length; i++) m.Tables["Sales"].AddMeasure("Probe" + i, rows[i]);
            });
            using (engine)
            {
                var found = await s.ReadAsync(m => FormulaReferences.Scan(m).Select(b => b.Object.Name + ": " + string.Join(" | ", b.Missing)).ToArray());
                Assert.True(found.Length == 0, "reported as broken: " + string.Join("; ", found));
                var checker = await s.ReadAsync(m => FormulaReferences.Checker(m));
                foreach (var dax in rows)
                {
                    var said = checker(dax);
                    Assert.True(said == null || !said.Any(), "the checker reported valid DAX: " + dax);
                }
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Valid))]
        public async Task Valid_DAX_is_not_reported_as_a_formula_that_names_a_missing_object(string dax)
        {
            var (engine, s) = await ModelAsync(m =>
            {
                Sales(m);
                m.AddCalculationGroup("Time").AddCalculationItem("YTD", "CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'Sales'[Order Date] ) )");
                m.Tables["Sales"].AddMeasure("Probe", dax);
            });
            using (engine)
            {
                var found = await s.ReadAsync(m => FormulaReferences.Scan(m).Select(b => b.Object.Name + ": " + string.Join(" | ", b.Missing)).ToArray());
                Assert.Empty(found);
                var checker = await s.ReadAsync(m => FormulaReferences.Checker(m));
                Assert.Empty(checker(dax));
            }
        }

        public static IEnumerable<object[]> Broken() => new[]
        {
            ("SUM ( 'Ghost'[Amount] )", "Unknown table 'Ghost'"),
            ("SUM ( Ghost[Amount] )", "Unknown table 'Ghost'"),
            ("COUNTROWS ( 'Ghost' )", "Unknown table 'Ghost'"),
            ("SUM ( 'Sales'[Ghost] )", "Table 'Sales' has no column 'Ghost'"),
            ("Sales[Ghost] * 2", "Table 'Sales' has no column 'Ghost'"),
            ("[Ghost] * 2", "Unknown measure or column '[Ghost]'"),
            ("[Owner's Ghost] * 2", "Unknown measure or column '[Owner's Ghost]'"),
            ("SUM ( 'Sales'[Size [mm]]] )", "Table 'Sales' has no column 'Size [mm]'"),
            ("Budget[Total Amount]", "Table 'Budget' has no column 'Total Amount'"),   // a measure of another table, not of Budget
            ("SUMX ( 'Sales', [Value] )", "Unknown measure or column '[Value]'"),   // no constructor, no GENERATESERIES: Value is not the formula's own
            ("CALCULATE ( [Total Amount], 'Sales'[Region] IN { \"N\" } ) + [Value]", "Unknown measure or column '[Value]'"),   // the braces of an IN make no column
            ("COUNTROWS ( DATATABLE ( \"K\", INTEGER, { { 1 } } ) ) + [Value]", "Unknown measure or column '[Value]'"),   // nor do the rows of a DATATABLE
            ("IF ( [Owner's Total] < 0, \"[Red]\", \"x\" ) + [Red]", "Unknown measure or column '[Red]'"),
            ("VAR x = 1 RETURN x + SUM ( 'Sales'[Ghost] )", "Table 'Sales' has no column 'Ghost'"),
            ("[Total Amount] + [Date]", "Unknown measure or column '[Date]'"),   // no CALENDAR here
        }.Select(f => new object[] { f.Item1, f.Item2 });

        [Fact]
        public async Task A_formula_of_every_row_that_names_a_missing_object_is_still_found_and_named_on_one_model()
        {
            var rows = Broken().Select(r => ((string)r[0], (string)r[1])).ToArray();
            var (engine, s) = await ModelAsync(m =>
            {
                Sales(m);
                for (var i = 0; i < rows.Length; i++) m.Tables["Sales"].AddMeasure("Probe" + i, rows[i].Item1);
            });
            using (engine)
            {
                var found = await s.ReadAsync(m => FormulaReferences.Scan(m).ToList());
                Assert.True(found.Count == rows.Length, "found " + found.Count + " broken formulas for " + rows.Length + " broken probes: " + string.Join("; ", found.Select(b => b.Object.Name)));
                for (var i = 0; i < rows.Length; i++)
                {
                    var b = Assert.Single(found, x => x.Object.Name == "Probe" + i);
                    Assert.True(b.Missing.Contains(rows[i].Item2 + "."), rows[i].Item1 + " should say: " + rows[i].Item2 + " but says: " + string.Join(" | ", b.Missing));
                }
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Broken))]
        public async Task A_formula_that_names_a_missing_object_is_still_found_and_named(string dax, string what)
        {
            var (engine, s) = await ModelAsync(m => { Sales(m); m.Tables["Sales"].AddMeasure("Probe", dax); });
            using (engine)
            {
                var found = await s.ReadAsync(m => FormulaReferences.Scan(m).ToList());
                var b = Assert.Single(found);
                Assert.Equal("Probe", b.Object.Name);
                Assert.Contains(what + ".", b.Missing);
            }
        }

        [Fact]
        public async Task The_offsets_of_the_reference_warnings_in_validate_dax_still_point_at_the_name()
        {
            var (engine, s) = await ModelAsync(Sales);
            using (engine)
            {
                var v = await s.ReadAsync(m => DaxValidator.Validate(m, "[Total Amount] +\n SUM ( 'Sales'[Ghost] ) + Ghost2[X]"));
                var w = v.Diagnostics.Where(d => d.Severity == "warning").OrderBy(d => d.Line).ThenBy(d => d.Column).ToArray();
                Assert.Equal(new[] { "Table 'Sales' has no column 'Ghost'.", "Unknown table 'Ghost2'." }, w.Select(d => d.Message).ToArray());
                Assert.Equal(2, w[0].Line);
                Assert.Equal(" SUM ( 'Sales'[".Length + 1, w[0].Column);   // the first letter of Ghost
                Assert.Equal(2, w[1].Line);
            }
        }

        // ---------------------------------------------------------------- every formula of every fixture model

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        private string Scratch()
        {
            var d = Path.Combine(Path.GetTempPath(), "frt-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            _dirs.Add(d);
            return d;
        }

        private static async Task<(int Formulas, string[] Broken)> ScanOpenModelAsync(SessionManager sessions)
        {
            var s = sessions.Require();
            var formulas = await s.ReadAsync(m => FormulaReferences.CountFormulas(m));
            var broken = await s.ReadAsync(m => FormulaReferences.Scan(m).Select(b => b.Object.Name + " (" + b.Formula + "): " + string.Join(" | ", b.Missing)).ToArray());
            return (formulas, broken);
        }

        [Fact]
        public async Task The_AdventureWorks_sample_has_no_formula_reported_and_its_KPIs_are_read()
        {
            var dir = Scratch();
            var bim = Path.Combine(dir, "AdventureWorks.bim");
            File.Copy(TestModels.FindBim(), bim);
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(bim);
            var (formulas, broken) = await ScanOpenModelAsync(sessions);
            Assert.True(formulas > 50, "formulas read: " + formulas);
            Assert.Empty(broken);
            await sessions.Require().ReadAsync(m => { BrokenFormulaGuardTests.AssertSameScoreWithoutTheRule(m, "AdventureWorks"); return 0; });
        }

        // Lean suite (8 Oct): the default run keeps this row; every row is the sweep.
        [Theory]
        [InlineData("kitchen")]
        public Task The_rule_clash_fixtures_report_only_the_formula_that_is_broken_on_purpose_sample(string name) => The_rule_clash_fixtures_report_only_the_formula_that_is_broken_on_purpose(name);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("kitchen")]
        [InlineData("schema")]
        [InlineData("instructions")]
        public async Task The_rule_clash_fixtures_report_only_the_formula_that_is_broken_on_purpose(string name)
        {
            using var f = new DecisionFixture();
            await RuleClashFixtures.BuildAsync(f, name);
            var s = f.Sessions.Require();
            var formulas = await s.ReadAsync(m => FormulaReferences.CountFormulas(m));
            var broken = await s.ReadAsync(m => FormulaReferences.Scan(m).Select(b => b.Object.Name + ": " + string.Join(" | ", b.Missing)).ToArray());
            Assert.True(formulas > 10, "formulas read: " + formulas);
            // The kitchen star carries one formula that is broken on purpose (Dangling Ref, so the rule has a fixture that makes it fire); every other formula of it is whole.
            Assert.Single(broken);
            Assert.StartsWith("Dangling Ref: Unknown table 'Ghost Table'", broken[0]);
            await s.ReadAsync(m => { BrokenFormulaGuardTests.AssertSameScoreWithoutTheRule(m, "clash-" + name); return 0; });   // the cap lowers the grade, never the average
        }

        [Theory]
        [InlineData("FinanceLedger")]
        [InlineData("PeopleOps")]
        [InlineData("SalesOps")]
        [InlineData("StockFlow")]
        [InlineData("WebPulse")]
        public async Task The_held_out_models_have_no_formula_reported(string model)
        {
            var copy = Path.Combine(Scratch(), model);
            CopyDir(Path.Combine(RepoRoot(), "Semanticus.Tests", "fixtures", "verify-format-fill-v1i-heldout", "set", "models", model), copy);
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(copy);
            var (formulas, broken) = await ScanOpenModelAsync(sessions);
            Assert.True(formulas > 5, "formulas read: " + formulas);
            Assert.Empty(broken);
            await sessions.Require().ReadAsync(m => { BrokenFormulaGuardTests.AssertSameScoreWithoutTheRule(m, model); return 0; });
        }

        /// <summary>More fixtures than the repository carries (the Northstar and contoso-mini models of the UAT folder, the raw J8 model): SEMANTICUS_FORMULA_FIXTURES lists model folders or .bim files
        /// separated by ':'. Without it there is nothing to read, and the test says so rather than passing in silence.</summary>
        [Fact]
        public async Task The_models_named_in_SEMANTICUS_FORMULA_FIXTURES_have_no_formula_reported()
        {
            var list = Environment.GetEnvironmentVariable("SEMANTICUS_FORMULA_FIXTURES");
            if (string.IsNullOrWhiteSpace(list)) return;   // xunit here has no runtime skip; the run that sets the variable is the proof
            var problems = new List<string>();
            foreach (var path in list.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                var target = path;
                if (Directory.Exists(path)) { target = Path.Combine(Scratch(), Path.GetFileName(path.TrimEnd('/'))); CopyDir(path, target); }
                else if (File.Exists(path)) { target = Path.Combine(Scratch(), Path.GetFileName(path)); File.Copy(path, target); }
                else { problems.Add(path + ": not found"); continue; }
                var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(target);
                var (formulas, broken) = await ScanOpenModelAsync(sessions);
                Console.WriteLine("formula fixture " + path + ": " + formulas + " formulas, " + broken.Length + " reported");
                problems.AddRange(broken.Select(b => path + ": " + b));
                await sessions.Require().ReadAsync(m => { BrokenFormulaGuardTests.AssertSameScoreWithoutTheRule(m, path); return 0; });
            }
            Assert.Empty(problems);
        }
    }

    /// <summary>
    /// Plan statement check 4, fix round 3. The reader judges three shapes only ('X', T[x], and a bare [x] in a formula whose calls all have a known shape); anything else is unsure and never reported,
    /// because a report caps the grade and the person cannot clear it. One probe per family of valid DAX, the constructs that must stay unsure, and the broken rows that must stay found.
    /// </summary>
    public sealed class FormulaReferenceFamilyTests : IClassFixture<FormulaReferenceFamilyTests.Shared>
    {
        /// <summary>The families model, built once for the class. The rows read it and add one probe at a time, which they take
        /// out again, so every row reads the model the families are written in and a row that fails leaves no probe behind.</summary>
        public sealed class Shared : IAsyncLifetime
        {
            private SessionManager _sessions;
            internal LocalEngine Engine;
            internal Session S;

            public async Task InitializeAsync()
            {
                _sessions = new SessionManager();
                Engine = new LocalEngine(_sessions, TestEntitlements.Free);
                await Engine.CreateModelAsync("FormulaFamilies", 1604);
                S = _sessions.Require();
                await S.MutateAsync("human", "build", Families);
            }

            public Task DisposeAsync()
            {
                Engine?.Dispose();
                return Task.CompletedTask;
            }

            /// <summary>Add the given objects, read the model, and take the additions out again.</summary>
            internal async Task<T> WithAsync<T>(Action<Model> add, Func<Model, T> read, Action<Model> remove)
            {
                await S.MutateAsync("human", "probe", add);
                try { return await S.ReadAsync(read); }
                finally { await S.MutateAsync("human", "probe off", remove); }
            }
        }

        private readonly Shared _shared;
        public FormulaReferenceFamilyTests(Shared shared) => _shared = shared;

        private static void DropProbe(Model m) => m.Tables["Sales"].Measures["Probe"].Delete();

        internal static void Families(Model m)
        {
            var t = m.AddTable("Sales");
            t.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
            t.AddDataColumn("Order Date", "Order Date", null, DataType.DateTime);
            t.AddDataColumn("Region", "Region", null, DataType.String);
            t.AddDataColumn("Size [cm]", "Size [cm]", null, DataType.Decimal);
            t.AddDataColumn("Net -- Gross", "Net -- Gross", null, DataType.Decimal);
            var total = t.AddMeasure("Total Amount", "SUM ( 'Sales'[Amount] )");
            var kpi = total.AddKPI();
            kpi.TargetExpression = "[Total Plan]";
            kpi.StatusExpression = "DIVIDE ( [Total Amount], [_Total Amount Goal] )";
            kpi.TrendExpression = "1";
            t.AddMeasure("Owner's Total", "SUM ( 'Sales'[Amount] )");
            t.AddCalculatedColumn("Double", "[Amount] * 2");
            var b = m.AddTable("Budget");
            b.AddDataColumn("Plan", "Plan", null, DataType.Decimal);
            b.AddMeasure("Total Plan", "SUM ( 'Budget'[Plan] )");
            var hidden = m.AddTable("Hidden Stuff");
            hidden.IsHidden = true;
            hidden.AddDataColumn("Secret", "Secret", null, DataType.Decimal);
            m.AddTable("Bob's Data").AddDataColumn("Amt", "Amt", null, DataType.Decimal);
            var g = m.AddCalculationGroup("Time");
            g.AddCalculationItem("YTD", "CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'Sales'[Order Date] ) )");
            var ratio = g.AddCalculationItem("Ratio", "IF ( ISSELECTEDMEASURE ( [Total Amount], [Owner's Total] ), DIVIDE ( SELECTEDMEASURE (), [Total Plan] ), SELECTEDMEASURE () )");
            ratio.FormatStringExpression = "IF ( ISSELECTEDMEASURE ( [Total Amount] ), \"0.0%\", SELECTEDMEASUREFORMATSTRING () )";
            // A field parameter: a calculated table whose columns the model lists, and a second calculated table whose columns it does not.
            var fields = m.AddCalculatedTable("Fields", "{ ( \"Amount\", NAMEOF ( 'Sales'[Total Amount] ), 0 ) }");
            fields.AddCalculatedTableColumn("Fields", "[Value1]");
            fields.AddCalculatedTableColumn("Fields Fields", "[Value2]");
            fields.AddCalculatedTableColumn("Fields Order", "[Value3]", null, DataType.Int64);
            m.AddCalculatedTable("Unlisted", "ADDCOLUMNS ( VALUES ( 'Sales'[Region] ), \"Kind\", 1 )");
            m.AddRole("Readers").RowLevelSecurity[t] = "[Region] = \"N\" && 'Sales'[Amount] > 0";
        }

        private static string[] Found(Model m) => FormulaReferences.Scan(m).Select(b => b.Object.Name + " (" + b.Formula + "): " + string.Join(" | ", b.Missing)).ToArray();

        // One row per family of valid DAX the first versions flagged or could flag. (family, formula)
        public static IEnumerable<object[]> Valid() => new (string Family, string Dax)[]
        {
            ("1 quoted tables", "SUMX ( 'Bob''s Data', 'Bob''s Data' [Amt] )"),
            ("1 quoted tables", "SUM ( 'SALES' [amount] ) + COUNTROWS ( 'Hidden Stuff' ) + SUM ( 'Hidden Stuff'[Secret] )"),
            ("1 quoted tables", "CALCULATE ( [Total Amount], 'Time'[Name] = \"YTD\" ) + COUNTROWS ( 'Time' )"),
            ("3 measure of its table", "Sales[Total Amount] + 'Sales'[Total Amount] + 'Budget'[Total Plan]"),
            ("3 KPI measures bare", "[_Total Amount Goal] + [_Total Amount Status] + [_Total Amount Trend]"),
            ("3 KPI measures qualified", "Sales[_Total Amount Goal] + 'Sales'[_Total Amount Status]"),
            ("4 calculation group", "ISSELECTEDMEASURE ( [Total Amount], [Total Plan] ) && SELECTEDMEASURENAME () = \"x\" && SELECTEDMEASUREFORMATSTRING () <> \"\""),
            ("4 calculation group column", "SELECTEDVALUE ( 'Time'[Name] ) & COUNTROWS ( VALUES ( 'Time'[Name] ) )"),
            ("5 field parameter columns", "SELECTEDVALUE ( 'Fields'[Fields Order] ) + MAXX ( 'Fields', 'Fields'[Fields Order] ) + MAXX ( Fields, [Fields Order] )"),
            ("5 field parameter, a column the model does not list", "MAXX ( 'Fields', 'Fields'[Fields Extra] ) + MAXX ( 'Fields', [Fields Extra] )"),
            ("5 calculated table, a column the model does not list", "MAXX ( Unlisted, Unlisted[Kind] ) + SUMX ( 'Unlisted', [Kind] ) + SELECTEDVALUE ( 'Unlisted'[Elsewhere] )"),
            ("6 VAR tables", "VAR a = FILTER ( 'Sales', 'Sales'[Amount] > 0 ) VAR b = ADDCOLUMNS ( a, \"X\", 1 ) RETURN SUMX ( b, b[X] ) + SUMX ( b, [X] )"),
            ("6 VAR in an argument", "SUMX ( VAR t = FILTER ( 'Sales', 'Sales'[Amount] > 0 ) RETURN t, 'Sales'[Amount] )"),
            ("6 VAR table, a column the formula does not show", "VAR _t = SUMMARIZE ( 'Sales', 'Sales'[Region] ) RETURN COUNTROWS ( FILTER ( _t, _t[Region] = \"N\" ) ) + COUNTROWS ( FILTER ( _t, _t[Anything] = 1 ) )"),
            ("7 ADDCOLUMNS", "SUMX ( ADDCOLUMNS ( VALUES ( 'Sales'[Region] ), \"Net\", [Total Amount] ), [Net] )"),
            ("7 SELECTCOLUMNS", "SUMX ( SELECTCOLUMNS ( 'Sales', \"Amt\", 'Sales'[Amount] ), [Amt] )"),
            ("7 SUMMARIZE", "SUMX ( SUMMARIZE ( 'Sales', 'Sales'[Region], \"Total\", [Total Amount] ), [Total] )"),
            ("7 SUMMARIZECOLUMNS", "SUMX ( SUMMARIZECOLUMNS ( 'Sales'[Region], \"T\", [Total Amount] ), [T] )"),
            ("7 GROUPBY CURRENTGROUP", "SUMX ( GROUPBY ( 'Sales', 'Sales'[Region], \"M\", SUMX ( CURRENTGROUP (), 'Sales'[Amount] ) ), [M] )"),
            ("7 ROW", "MAXX ( ROW ( \"Total\", [Total Amount], \"Plan\", [Total Plan] ), [Total] - [Plan] )"),
            ("7 DATATABLE", "MAXX ( DATATABLE ( \"K\", INTEGER, { { 1 }, { 2 } } ), [K] )"),
            ("7 ROLLUPADDISSUBTOTAL", "COUNTROWS ( FILTER ( SUMMARIZECOLUMNS ( ROLLUPADDISSUBTOTAL ( 'Sales'[Region], \"IsAll\" ), \"T\", [Total Amount] ), [IsAll] ) )"),
            ("7 SUBSTITUTEWITHINDEX", "COUNTROWS ( FILTER ( SUBSTITUTEWITHINDEX ( VALUES ( 'Sales'[Region] ), \"Ix\", VALUES ( 'Sales'[Region] ), 'Sales'[Region], ASC ), [Ix] > 0 ) )"),
            ("8 constructor Value", "SUMX ( { 1, 2, 3 }, [Value] ) + MAXX ( { ( 1, 2 ) }, [Value2] )"),
            ("8 GENERATESERIES Value", "SUMX ( GENERATESERIES ( 1, 10, 1 ), [Value] )"),
            ("8 CALENDAR Date", "MAXX ( CALENDAR ( DATE ( 2020, 1, 1 ), DATE ( 2020, 12, 31 ) ), [Date] )"),
            ("8 CALENDARAUTO Date", "MAXX ( CALENDARAUTO (), [Date] )"),
            ("9 LINESTX", "MAXX ( LINESTX ( 'Sales', 'Sales'[Amount], 'Sales'[Order Date] ), [Slope1] )"),
            ("9 COLUMNSTATISTICS", "MAXX ( COLUMNSTATISTICS (), [Cardinality] )"),
            ("9 INFO.VIEW", "COUNTROWS ( SELECTCOLUMNS ( INFO.VIEW.TABLES (), \"N\", [Name] ) ) + MAXX ( INFO.VIEW.TABLES (), [Name] )"),
            ("9 DETAILROWS", "COUNTROWS ( DETAILROWS ( [Total Amount] ) ) + MAXX ( DETAILROWS ( [Total Amount] ), [Anything] )"),
            ("9 a function the reader does not know", "MAXX ( MakeRows ( 3 ), [Column Of Its Own] )"),
            ("9 a level of a date variation", "CALCULATE ( [Total Amount], 'Sales'[Order Date].[Year] = 2020 ) + COUNTROWS ( VALUES ( Sales[Order Date].[Month] ) )"),
            ("10 an apostrophe and brackets in a name and a string", "[Owner's Total] + IF ( [Total Amount] > 0, \"say [hi] it's\", \"x\" ) + SUM ( 'Sales'[Size [cm]]] )"),
            ("10 comments", "[Total Amount] // 'Ghost'[X] and [Ghost] it's\n + [Total Plan] /* ] ' [ \" */ -- [Ghost2]"),
            ("10 -- inside a name", "SUMX ( 'Sales', 'Sales'[Net -- Gross] ) + SUMX ( 'Sales', [Net -- Gross] )"),
            ("11 a bare column of the table the formula is in", "SUMX ( 'Sales', [Amount] * [Double] )"),
        }.Select(p => new object[] { p.Family, p.Dax });

        [Theory]
        [MemberData(nameof(Valid))]
        public async Task Valid_or_unsure_DAX_of_every_family_gives_no_finding(string family, string dax)
        {
            Assert.True(family.Length > 0);
            var (found, checker) = await _shared.WithAsync(m => m.Tables["Sales"].AddMeasure("Probe", dax),
                m => (Found(m).Where(f => f.StartsWith("Probe ", StringComparison.Ordinal)).ToArray(), FormulaReferences.Checker(m)), DropProbe);
            Assert.Empty(found);
            Assert.Empty(checker(dax));
        }

        [Fact]
        public async Task The_model_the_families_are_written_in_has_no_finding_of_its_own()
        {
            Assert.Empty(await _shared.S.ReadAsync(Found));   // the KPI status, the calculation items, the role filter and the calculated column
        }

        [Fact]
        public async Task A_calculated_column_of_a_calculated_table_is_not_judged_by_its_bare_names()
        {
            var found = await _shared.WithAsync(m =>
            {
                ((CalculatedTable)m.Tables["Unlisted"]).AddCalculatedTableColumn("Twice", "[Twice]");
                m.Tables["Unlisted"].AddMeasure("Hosted", "MAXX ( VALUES ( 'Sales'[Region] ), [Not Listed Anywhere] )");   // a measure hosted by a calculated table
            }, Found, m => { m.Tables["Unlisted"].Measures["Hosted"].Delete(); ((CalculatedTable)m.Tables["Unlisted"]).Columns["Twice"].Delete(); });
            Assert.DoesNotContain(found, f => f.StartsWith("Hosted ", StringComparison.Ordinal));
        }

        // The calendar a template builds, named in calendar time intelligence.
        [Fact]
        public async Task A_calendar_is_a_name_a_quote_may_hold_and_a_calendar_that_is_not_there_is_found()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("FormulaFamiliesCal", 1701);
            await engine.DefineCalendarFromTemplateAsync("gregorian", null, null, 7, null, null, null, "agent");
            var s = sessions.Require();
            await s.MutateAsync("human", "measure", m =>
            {
                var t = m.AddTable("Sales");
                t.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
                t.AddMeasure("Total Amount", "SUM ( 'Sales'[Amount] )");
                t.AddMeasure("Amount YTD", "TOTALYTD ( [Total Amount], 'Gregorian' )");
                t.AddMeasure("Amount YTD ghost", "TOTALYTD ( [Total Amount], 'Julian' )");
            });
            var found = await s.ReadAsync(Found);
            Assert.Single(found);
            Assert.Contains("Amount YTD ghost", found[0]);
            Assert.Contains("Unknown table 'Julian'.", found[0]);
        }

        // What must stay found. (formula, what it names that is not there)
        public static IEnumerable<object[]> Broken() => new (string Dax, string What)[]
        {
            ("Budget[_Total Amount Goal]", "Table 'Budget' has no column '_Total Amount Goal'"),    // a KPI measure of Sales, written under Budget
            ("[_Total Plan Goal] + 1", "Unknown measure or column '[_Total Plan Goal]'"),             // Total Plan has no KPI
            ("[Ghost] * 2", "Unknown measure or column '[Ghost]'"),
            ("SUMX ( 'Sales', [Ghost] + [Amount] )", "Unknown measure or column '[Ghost]'"),
            ("VAR t = FILTER ( 'Sales', 'Sales'[Amount] > 0 ) RETURN SUMX ( t, [Ghost Two] )", "Unknown measure or column '[Ghost Two]'"),
            ("CALCULATE ( [Total Amount], Ghost[Region] = \"N\" )", "Unknown table 'Ghost'"),
            ("COUNTROWS ( 'Ghost Table' )", "Unknown table 'Ghost Table'"),
            ("SUM ( 'Sales'[Ghost Column] )", "Table 'Sales' has no column 'Ghost Column'"),
            ("TOTALYTD ( [Total Amount], 'Ghost Calendar' )", "Unknown table 'Ghost Calendar'"),
            ("SUMX ( 'Time', 'Time'[Ghost] )", "Table 'Time' has no column 'Ghost'"),
            ("MAXX ( DATATABLE ( \"K\", INTEGER, { { 1 } } ), [Kay] )", "Unknown measure or column '[Kay]'"),
        }.Select(p => new object[] { p.Dax, p.What });

        [Theory]
        [MemberData(nameof(Broken))]
        public async Task Broken_DAX_is_still_found_and_named(string dax, string what)
        {
            var found = await _shared.WithAsync(m => m.Tables["Sales"].AddMeasure("Probe", dax),
                m => FormulaReferences.Scan(m).Select(b => (Name: b.Object.Name, b.Missing)).ToList(), DropProbe);
            var b = Assert.Single(found);
            Assert.Equal("Probe", b.Name);
            Assert.Contains(what + ".", b.Missing);
        }

        // A bare name is judged only where every call has a known shape: the same missing name is found, then not found once a call of unknown shape is added.
        [Fact]
        public async Task A_formula_with_a_call_of_unknown_shape_is_not_judged_by_its_bare_names()
        {
            var found = await _shared.WithAsync(m =>
            {
                m.Tables["Sales"].AddMeasure("Plain", "MAXX ( VALUES ( 'Sales'[Region] ), [Ghost] )");
                m.Tables["Sales"].AddMeasure("Unsure", "MAXX ( UnknownTableFunction ( VALUES ( 'Sales'[Region] ) ), [Ghost] )");
            }, Found, m => { m.Tables["Sales"].Measures["Plain"].Delete(); m.Tables["Sales"].Measures["Unsure"].Delete(); });
            Assert.Single(found, f => f.StartsWith("Plain ", StringComparison.Ordinal));
            Assert.DoesNotContain(found, f => f.StartsWith("Unsure ", StringComparison.Ordinal));
        }
    }
}
