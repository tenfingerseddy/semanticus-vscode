// Verifier, m11/psc4 fix round 3, 4 Oct. Attacks the redesigned broken-formula check with families of valid DAX that no earlier round listed (window functions, logging,
// relationship modifiers, PATH, time intelligence on a calendar, names that look like functions, names with dots, brackets and Unicode, very long formulas, formulas that are
// only comments) and with broken shapes that must stay found. A test left failing here names a defect in the lane; a passing one is evidence the attack did not land.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifyPsc4Fixes3Tests
    {
        private static async Task<(LocalEngine Engine, Session S)> ModelAsync(Action<Model> build)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Free);
            await engine.CreateModelAsync("VerifyPsc4c", 1604);
            var s = sessions.Require();
            await s.MutateAsync("human", "build", build);
            return (engine, s);
        }

        private static void Shapes(Model m)
        {
            var t = m.AddTable("Sales");
            t.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
            t.AddDataColumn("Region", "Region", null, DataType.String);
            t.AddDataColumn("Order Date", "Order Date", null, DataType.DateTime);
            t.AddDataColumn("Ship Date", "Ship Date", null, DataType.DateTime);
            t.AddMeasure("Total Amount", "SUM ( 'Sales'[Amount] )");
            // Measures named like functions.
            t.AddMeasure("SUM", "SUM ( 'Sales'[Amount] )");
            t.AddMeasure("Filter", "COUNTROWS ( 'Sales' )");
            t.AddMeasure("Value", "1");
            // A table named like a function, used unquoted.
            var d = m.AddTable("Date");
            d.AddDataColumn("Date", "Date", null, DataType.DateTime);
            d.AddDataColumn("Year", "Year", null, DataType.Int64);
            m.AddTable("Values").AddDataColumn("Kind", "Kind", null, DataType.String);
            // Names with dots, brackets and Unicode.
            m.AddTable("Sales.EU [Old]").AddDataColumn("Amt", "Amt", null, DataType.Decimal);
            var fr = m.AddTable("Ventes été");
            fr.AddDataColumn("Montant €", "Montant €", null, DataType.Decimal);
            fr.AddMeasure("Coût total", "SUM ( 'Ventes été'[Montant €] )");
            var gr = m.AddTable("Πωλήσεις");
            gr.AddDataColumn("Ποσό", "Ποσό", null, DataType.Decimal);
            gr.AddMeasure("売上", "SUM ( 'Πωλήσεις'[Ποσό] )");
            // A parent-child table for PATH.
            var e = m.AddTable("Employee");
            e.AddDataColumn("Id", "Id", null, DataType.Int64);
            e.AddDataColumn("Parent Id", "Parent Id", null, DataType.Int64);
            e.AddCalculatedColumn("Path", "PATH ( Employee[Id], Employee[Parent Id] )");
            e.AddCalculatedColumn("Level 1", "PATHITEM ( [Path], 1, INTEGER )");
            e.AddCalculatedColumn("Depth", "PATHLENGTH ( PATH ( [Id], [Parent Id] ) )");
            m.AddRole("Managers").RowLevelSecurity[e] = "PATHCONTAINS ( [Path], LOOKUPVALUE ( Employee[Id], Employee[Id], 1 ) ) && USERPRINCIPALNAME () <> \"\"";
            t.AddCalculatedColumn("Year", "RELATED ( 'Date'[Year] ) + YEAR ( [Order Date] )");
            var total = t.Measures["Total Amount"];
            total.DetailRowsExpression = "SELECTCOLUMNS ( 'Sales', \"Region\", 'Sales'[Region], \"Amount\", [Amount] )";
            total.FormatStringExpression = "IF ( [Total Amount] > 1000, \"#,0\", \"0.00\" )";
        }

        private static string[] Found(Model m) => FormulaReferences.Scan(m).Select(b => b.Object.Name + " (" + b.Formula + "): " + string.Join(" | ", b.Missing)).ToArray();

        // (shape, formula). Valid DAX, or a shape the design calls unsure: no finding either way.
        public static IEnumerable<object[]> Valid() => new (string Shape, string Dax)[]
        {
            ("OFFSET with PARTITIONBY", "CALCULATE ( [Total Amount], OFFSET ( -1, ALLSELECTED ( 'Date'[Year], Sales[Region] ), ORDERBY ( 'Date'[Year], ASC ), PARTITIONBY ( Sales[Region] ) ) )"),
            ("WINDOW with ABS and REL", "SUMX ( WINDOW ( 1, ABS, 0, REL, ALLSELECTED ( 'Date'[Year] ), ORDERBY ( 'Date'[Year] ) ), [Total Amount] )"),
            ("INDEX with ORDERBY a measure", "CALCULATE ( [Total Amount], INDEX ( 1, ALLSELECTED ( Sales[Region] ), ORDERBY ( [Total Amount], DESC ) ) )"),
            ("RANK DENSE", "RANK ( DENSE, ALLSELECTED ( Sales[Region] ), ORDERBY ( [Total Amount], DESC ) )"),
            ("ROWNUMBER with MATCHBY", "ROWNUMBER ( ALLSELECTED ( Sales[Region] ), ORDERBY ( Sales[Region] ), , MATCHBY ( Sales[Region] ) )"),
            ("EVALUATEANDLOG", "EVALUATEANDLOG ( [Total Amount], \"probe\", 10 ) + COUNTROWS ( EVALUATEANDLOG ( VALUES ( Sales[Region] ) ) )"),
            ("TOPN of a table with a column of its own", "SUMX ( TOPN ( 3, ADDCOLUMNS ( VALUES ( Sales[Region] ), \"T\", [Total Amount] ), [T], DESC ), [T] )"),
            ("CROSSJOIN of VAR tables", "VAR a = VALUES ( Sales[Region] ) VAR b = VALUES ( 'Date'[Year] ) VAR x = CROSSJOIN ( a, b ) RETURN SUMX ( x, [Total Amount] ) + COUNTROWS ( FILTER ( x, [Region] <> \"N\" && [Year] > 2000 ) )"),
            ("nested CALCULATETABLE", "COUNTROWS ( CALCULATETABLE ( CALCULATETABLE ( VALUES ( Sales[Region] ), 'Date'[Year] = 2020 ), REMOVEFILTERS ( 'Date' ) ) )"),
            ("USERELATIONSHIP", "CALCULATE ( [Total Amount], USERELATIONSHIP ( Sales[Ship Date], 'Date'[Date] ) )"),
            ("CROSSFILTER", "CALCULATE ( [Total Amount], CROSSFILTER ( Sales[Order Date], 'Date'[Date], BOTH ) )"),
            ("PATH functions in a measure", "MAXX ( Employee, PATHLENGTH ( PATH ( Employee[Id], Employee[Parent Id] ) ) ) + COUNTROWS ( FILTER ( Employee, PATHCONTAINS ( Employee[Path], 1 ) ) )"),
            ("classic SAMEPERIODLASTYEAR on a date column", "CALCULATE ( [Total Amount], SAMEPERIODLASTYEAR ( 'Date'[Date] ) ) + CALCULATE ( [Total Amount], DATEADD ( Date[Date], -1, YEAR ) )"),
            ("measures named like functions", "[SUM] + [Filter] + [Value] + Sales[SUM] + 'Sales'[Filter] + SUM ( Sales[Amount] ) + COUNTROWS ( FILTER ( Sales, [Value] > 0 ) )"),
            ("tables named like functions", "COUNTROWS ( Values ) + COUNTROWS ( 'Values' ) + COUNTROWS ( VALUES ( Values[Kind] ) ) + MAXX ( Date, Date[Year] ) + COUNTROWS ( DATESYTD ( Date[Date] ) )"),
            ("a table name with a dot and brackets", "SUM ( 'Sales.EU [Old]'[Amt] ) + COUNTROWS ( 'Sales.EU [Old]' ) + SUMX ( 'Sales.EU [Old]', [Amt] )"),
            ("Unicode names", "SUM ( 'Ventes été'[Montant €] ) + [Coût total] + [COÛT TOTAL] + SUMX ( 'VENTES ÉTÉ', [Montant €] ) + [売上] + SUM ( 'Πωλήσεις'[Ποσό] ) + SUM ( 'ΠΩΛΉΣΕΙΣ'[ΠΟΣΌ] )"),
            ("only a // comment", "// a note about [Ghost] and 'Ghost Table'[X], it's here"),
            ("only a block comment", "/* 'Ghost'[X]\n [Ghost] it's ] [ */"),
            ("only -- comments on two lines", "-- [Ghost One]\r\n-- 'Ghost'[Two]"),
            ("a string with doubled quotes and brackets", "IF ( [Total Amount] > 0, \"He said \"\"[Ghost]\"\" and 'Ghost'[X] it's\", \"\" )"),
            ("TREATAS of a tuple onto two columns", "CALCULATE ( [Total Amount], KEEPFILTERS ( TREATAS ( { ( \"N\", 2020 ) }, Sales[Region], 'Date'[Year] ) ) )"),
            ("SELECTCOLUMNS without names", "MAXX ( SELECTCOLUMNS ( Sales, Sales[Region], 'Sales'[Amount] ), [Region] & [Amount] )"),
            ("GENERATE with ROW", "SUMX ( GENERATE ( VALUES ( Sales[Region] ), ROW ( \"R2\", [Total Amount] ) ), [R2] )"),
            ("UNION of constructors and ROW", "COUNTROWS ( UNION ( { 1 }, { 2 } ) ) + MAXX ( UNION ( ROW ( \"K\", 1 ), ROW ( \"K\", 2 ) ), [K] )"),
            ("NATURALLEFTOUTERJOIN of VAR tables", "VAR a = SELECTCOLUMNS ( Sales, \"Key\", Sales[Region], \"A\", Sales[Amount] ) VAR b = SELECTCOLUMNS ( Values, \"Key\", Values[Kind] ) RETURN SUMX ( NATURALLEFTOUTERJOIN ( a, b ), [A] )"),
            ("ISINSCOPE and HASONEVALUE", "IF ( ISINSCOPE ( Sales[Region] ), [Total Amount], IF ( HASONEVALUE ( 'Date'[Year] ), [SUM], BLANK () ) )"),
            ("CONCATENATEX with an order", "CONCATENATEX ( VALUES ( Sales[Region] ), Sales[Region], \", \", Sales[Region], ASC )"),
            ("a quoted table on one line, its column on the next", "SUM ( 'Sales'\r\n[Amount] ) + SUM ( 'Sales'\t[Amount] )"),
            ("SWITCH TRUE", "SWITCH ( TRUE (), [Total Amount] > 10, \"big\", [Total Amount] > 1, \"small\", \"none\" )"),
            ("dotted function names", "PERCENTILEX.INC ( Sales, Sales[Amount], 0.5 ) + STDEV.P ( Sales[Amount] ) + [Total Amount]"),
            ("lower case everything", "sumx ( sales, sales[amount] ) + calculate ( [total amount], 'date'[year] = 2020 ) + sales[total amount]"),
            ("IN a table and a tuple list", "CALCULATE ( [Total Amount], FILTER ( Sales, Sales[Region] IN VALUES ( Values[Kind] ) && ( Sales[Region], Sales[Amount] ) IN { ( \"N\", 1 ) } ) )"),
            ("a VAR named like a column", "VAR Amount = [Total Amount] VAR Region = SELECTEDVALUE ( Sales[Region] ) RETURN Amount + LEN ( Region )"),
            ("a Unicode line break ends a comment", "[Total Amount] -- [Ghost]\u2028 + [SUM]"),
            ("ALLEXCEPT and ALLNOBLANKROW", "CALCULATE ( [Total Amount], ALLEXCEPT ( Sales, Sales[Region] ) ) + COUNTROWS ( ALLNOBLANKROW ( 'Date' ) )"),
            ("SUMMARIZECOLUMNS with a filter table and IGNORE", "SUMX ( SUMMARIZECOLUMNS ( Sales[Region], TREATAS ( { 2020 }, 'Date'[Year] ), \"T\", IGNORE ( [Total Amount] ) ), [T] )"),
            ("a measure whose name holds a dot", "[Total Amount] + [Total.Amount.Ext]"),
            ("EARLIER in a calculated shape", "SUMX ( Sales, CALCULATE ( SUM ( Sales[Amount] ), FILTER ( Sales, Sales[Region] = EARLIER ( Sales[Region] ) ) ) )"),
            ("time intelligence on the Date table, unquoted", "TOTALYTD ( [Total Amount], Date[Date] ) + CALCULATE ( [Total Amount], PARALLELPERIOD ( Date[Date], -1, YEAR ) )"),
        }.Select(p => new object[] { p.Shape, p.Dax });

        public static IEnumerable<object[]> ValidSample_A_new_valid_or_unsure_sh() { var all = Valid().ToArray(); return new[] { all[0], all[13], all[16] }; }
        [Theory]
        [MemberData(nameof(ValidSample_A_new_valid_or_unsure_sh))]
        public Task A_new_valid_or_unsure_shape_gives_no_finding_sample(string shape, string dax) => A_new_valid_or_unsure_shape_gives_no_finding(shape, dax);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Valid))]
        public async Task A_new_valid_or_unsure_shape_gives_no_finding(string shape, string dax)
        {
            var (engine, s) = await ModelAsync(m =>
            {
                Shapes(m);
                m.Tables["Sales"].AddMeasure("Total.Amount.Ext", "1");
                m.Tables["Sales"].AddMeasure("Probe", dax);
            });
            using (engine)
            {
                Assert.True(shape.Length > 0);
                Assert.Empty(await s.ReadAsync(Found));   // the probe and every formula of the shape model (PATH columns, RLS, detail rows, format string)
                var checker = await s.ReadAsync(m => FormulaReferences.Checker(m));
                Assert.Empty(checker(dax));
            }
        }

        // (formula, what must be named). Broken shapes, each must be found.
        public static IEnumerable<object[]> Broken() => new (string Dax, string What)[]
        {
            ("SUM ( 'Ghost Table'[Amount] )", "Unknown table 'Ghost Table'"),
            ("SUM ( Ghost[Amount] )", "Unknown table 'Ghost'"),
            ("SUM ( Sales[Ghost Column] )", "Table 'Sales' has no column 'Ghost Column'"),
            ("SUM ( 'Sales'[Ghost Column] )", "Table 'Sales' has no column 'Ghost Column'"),
            ("SUM ( Sales[Amount] ) + [Ghost Measure]", "Unknown measure or column '[Ghost Measure]'"),
            ("SUM ( [Ghost] )", "Unknown measure or column '[Ghost]'"),
            ("VAR a = 1 RETURN [Ghost] + a", "Unknown measure or column '[Ghost]'"),
            ("IF ( NOT [Ghost], 1, 0 )", "Unknown measure or column '[Ghost]'"),
            ("SUM ( 'Ventes été'[Montant $] )", "Table 'Ventes été' has no column 'Montant $'"),
            ("SUM ( 'Sales.EU'[Amt] )", "Unknown table 'Sales.EU'"),
            ("CALCULATE ( [Total Amount], USERELATIONSHIP ( Sales[Ship Date], 'Date'[Day] ) )", "Table 'Date' has no column 'Day'"),
            ("Budget[Total Amount]", "Unknown table 'Budget'"),
        }.Select(p => new object[] { p.Dax, p.What });

        public static IEnumerable<object[]> BrokenSample_A_broken_shape_is_found() { var all = Broken().ToArray(); return new[] { all[0], all[4] }; }
        [Theory]
        [MemberData(nameof(BrokenSample_A_broken_shape_is_found))]
        public Task A_broken_shape_is_found_sample(string dax, string what) => A_broken_shape_is_found(dax, what);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Broken))]
        public async Task A_broken_shape_is_found(string dax, string what)
        {
            var (engine, s) = await ModelAsync(m => { Shapes(m); m.Tables["Sales"].AddMeasure("Probe", dax); });
            using (engine)
            {
                var found = await s.ReadAsync(Found);
                var probe = Assert.Single(found);
                Assert.StartsWith("Probe ", probe);
                Assert.Contains(what, probe);
            }
        }

        [Fact]
        public async Task A_very_long_formula_is_read_whole_and_fast()
        {
            var sb = new StringBuilder();
            for (var i = 0; i < 5000; i++) sb.Append(i == 0 ? "" : " + ").Append(i % 3 == 0 ? "[Total Amount]" : i % 3 == 1 ? "SUM ( 'Sales'[Amount] )" : "CALCULATE ( [SUM], Date[Year] = " + i + " )");
            var whole = sb.ToString();
            var broken = whole + " + [Ghost At The End]";
            var (engine, s) = await ModelAsync(m => { Shapes(m); m.Tables["Sales"].AddMeasure("Long", whole); m.Tables["Sales"].AddMeasure("Long broken", broken); });
            using (engine)
            {
                var sw = Stopwatch.StartNew();
                var found = await s.ReadAsync(Found);
                sw.Stop();
                Console.WriteLine("long formulas: " + whole.Length + " and " + broken.Length + " chars, scan " + sw.ElapsedMilliseconds + " ms");
                var f = Assert.Single(found);
                Assert.StartsWith("Long broken ", f);
                Assert.Contains("[Ghost At The End]", f);
                Assert.True(sw.ElapsedMilliseconds < 5000, "scan took " + sw.ElapsedMilliseconds + " ms");
            }
        }

        // Calendar-based time intelligence on a calendar the template builds, by every function shape that takes a calendar.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Time_intelligence_on_a_calendar_gives_no_finding()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("VerifyPsc4cCal", 1701);
            await engine.DefineCalendarFromTemplateAsync("gregorian", null, null, 7, null, null, null, "agent");
            var s = sessions.Require();
            var (table, cal, dateCol) = await s.ReadAsync(m =>
            {
                var (t, c) = CalendarOps.Enumerate(m).First();
                var dc = t.Columns.FirstOrDefault(x => x.DataType == DataType.DateTime)?.Name;
                return (t.Name, c.Name, dc);
            });
            await s.MutateAsync("human", "measures", m =>
            {
                var t = m.AddTable("Sales");
                t.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
                t.AddMeasure("Total Amount", "SUM ( 'Sales'[Amount] )");
                var q = "'" + cal.Replace("'", "''") + "'";
                t.AddMeasure("PY", "CALCULATE ( [Total Amount], SAMEPERIODLASTYEAR ( " + q + " ) )");
                t.AddMeasure("Prev", "CALCULATE ( [Total Amount], DATEADD ( " + q + ", -1, YEAR ) )");
                t.AddMeasure("QTD", "TOTALQTD ( [Total Amount], " + q + " ) + CALCULATE ( [Total Amount], DATESYTD ( " + q + " ) )");
                t.AddMeasure("Classic PY", "CALCULATE ( [Total Amount], SAMEPERIODLASTYEAR ( '" + table.Replace("'", "''") + "'[" + dateCol + "] ) )");
            });
            Assert.Empty(await s.ReadAsync(Found));
        }
    }

    /// <summary>Check 4 rebuilt, read strictly: the start is built (Free and Pro, both doors, the keyless source), and every [name] and every 'Table' or Table[name] in every built
    /// formula is read by a test-side resolver that is never unsure, so a zero here cannot come from the check declining to judge.</summary>
    [Collection("restore-root")]
    public sealed class VerifyPsc4Fixes3StartTests : IDisposable
    {
        private const string Server = "wh-j8.datawarehouse.fabric.microsoft.com", Db = "ContosoConsulting";
        private readonly RestoreRootFixture _fixture;
        private readonly string _root;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public VerifyPsc4Fixes3StartTests(RestoreRootFixture fixture)
        {
            _fixture = fixture;
            _root = Path.Combine(Path.GetTempPath(), "vp4s-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            Directory.CreateDirectory(Path.Combine(_root, "policy"));
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "home"));
            ConnectionRegistry.RootOverride = Path.Combine(_root, "user");
            AgentPolicyStore.RootOverride = Path.Combine(_root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(_root, "policy");
        }

        public void Dispose()
        {
            foreach (var o in _owned.AsEnumerable().Reverse()) { try { o.Dispose(); } catch { } }
            ConnectionRegistry.RootOverride = _fixture.Root; AgentPolicyStore.RootOverride = _fixture.Root; ApprovalLedger.RootOverride = _fixture.Root;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static string Humanise(string r)
        {
            var name = (r ?? "").Substring((r ?? "").LastIndexOf('/') + 1);
            var colon = name.IndexOf(':'); if (colon >= 0) name = name.Substring(colon + 1);
            return string.Join(" ", name.Split('_', ' ').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1).ToLowerInvariant()));
        }

        private static StartWord[] WordsFor(StartWordsPage page) => page.Items.Select(i => new StartWord
        {
            Ref = i.Ref, Kind = i.Kind,
            Text = i.Kind == "synonyms" ? "alias one, alias two"
                 : i.Kind == "display_name" ? (string.Equals(Humanise(i.Ref), (i.Ref ?? "").Substring((i.Ref ?? "").LastIndexOf('/') + 1), StringComparison.OrdinalIgnoreCase) ? Humanise(i.Ref) + " Value" : Humanise(i.Ref))
                 : i.Kind == "instructions" ? "This model answers questions about consulting work: hours, projects, clients and invoices."
                 : "What " + Humanise(i.Ref) + " means for the business, in plain words.",
        }).ToArray();

        // Every reference a formula makes, resolved with no unsure answer: a quoted name is a table or a calendar; Word[x] or 'T'[x] is a column or measure of T; a bare [x] is a measure or a column.
        private static List<string> Strict(Model m, string dax)
        {
            var tables = new HashSet<string>(m.Tables.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var (_, c) in CalendarOps.Enumerate(m)) tables.Add(c.Name);
            var names = new HashSet<string>(m.AllMeasures.Select(x => x.Name).Concat(m.AllColumns.Select(c => c.Name)), StringComparer.OrdinalIgnoreCase);
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var toks = DaxScan.Tokenize(dax);
            foreach (var t in toks) if (t.Kind == DaxScan.Kind.String) own.Add(t.Inner);
            var bad = new List<string>();
            for (var k = 0; k < toks.Count; k++)
            {
                var t = toks[k];
                var next = k + 1 < toks.Count ? toks[k + 1] : null;
                var isBracket = next != null && next.Kind == DaxScan.Kind.Name && next.Delim == '[';
                if (t.Kind == DaxScan.Kind.Name && t.Delim == '\'')
                {
                    if (!tables.Contains(t.Inner)) bad.Add("'" + t.Inner + "'");
                    else if (isBracket)
                    {
                        var tb = m.Tables.FirstOrDefault(x => string.Equals(x.Name, t.Inner, StringComparison.OrdinalIgnoreCase));
                        if (tb != null && !tb.Columns.Any(c => string.Equals(c.Name, next.Inner, StringComparison.OrdinalIgnoreCase)) && !tb.Measures.Any(c => string.Equals(c.Name, next.Inner, StringComparison.OrdinalIgnoreCase)))
                            bad.Add("'" + t.Inner + "'[" + next.Inner + "]");
                        k++;
                    }
                    continue;
                }
                if (t.Kind == DaxScan.Kind.Word && isBracket && next.Start == t.End)
                {
                    var tb = m.Tables.FirstOrDefault(x => string.Equals(x.Name, t.Text, StringComparison.OrdinalIgnoreCase));
                    if (tb == null || (!tb.Columns.Any(c => string.Equals(c.Name, next.Inner, StringComparison.OrdinalIgnoreCase)) && !tb.Measures.Any(c => string.Equals(c.Name, next.Inner, StringComparison.OrdinalIgnoreCase))))
                        bad.Add(t.Text + "[" + next.Inner + "]");
                    k++;
                    continue;
                }
                if (t.Kind == DaxScan.Kind.Name && t.Delim == '[' && !names.Contains(t.Inner) && !own.Contains(t.Inner)) bad.Add("[" + t.Inner + "]");
            }
            return bad;
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task The_start_build_has_no_broken_formula_read_strictly(bool pro, bool mcpDoor)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, pro ? TestEntitlements.Pro : TestEntitlements.Free, Path.Combine(_root, "ws"));
            _owned.Add(engine); _owned.Add(sessions);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            var schema = SqlDraftBuildTests.J8Schema();
            schema.ForeignKeys = new FabricForeignKey[0];
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(schema);
            if (pro) engine.UseDecisionsForTest(new DecisionService(new StartPlanDecideTests.PolicyOracle()));
            var folder = Path.Combine(_root, "model");
            var view = mcpDoor
                ? await McpTools.StartModelFromSql(engine, Server, Db, folder, "Uplift")
                : await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = folder, Name = "Uplift" }, "human");
            if (pro)
            {
                await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
                view = await engine.GrantStartDisclosureAsync(view.Digest, "human");
                await engine.StartDecideSettledForTest();
            }
            for (var pass = 0; pass < 60; pass++)
            {
                var page = await engine.GetStartWordsAsync(40, 0);
                if (page.Items.Count == 0) break;
                var wrote = await McpTools.WriteStartWords(engine, WordsFor(page));
                var keeps = wrote.Refused.Where(r => r.Contains("keep_name")).Select(r => r.Substring(0, r.IndexOf(": ", StringComparison.Ordinal))).Distinct().ToArray();
                var kept = keeps.Length == 0 ? 0 : (await McpTools.WriteStartWords(engine, keeps.Select(k => new StartWord { Ref = k, Kind = "keep_name" }).ToArray())).Written;
                if (wrote.Written + kept == 0) break;
            }
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);
            Assert.True(view.CanBuild, view.BlockedBecause);
            var built = mcpDoor ? await McpTools.BuildStartPlan(engine, view.Digest) : await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            await sessions.Current.ReadAsync(m =>
            {
                var formulas = FormulaReferences.Holders(m).SelectMany(h => FormulaReferences.FormulasOf(h).Select(f => (h.Name, f.Text))).ToList();
                var bad = formulas.SelectMany(f => Strict(m, f.Text).Select(b => f.Name + ": " + b)).ToList();
                Console.WriteLine("start build pro=" + pro + " mcp=" + mcpDoor + ": " + formulas.Count + " formulas, " + m.AllMeasures.Count() + " measures, strict misses " + bad.Count + ", check " + FormulaReferences.Scan(m).Count);
                Assert.True(formulas.Count >= 15, "formulas " + formulas.Count);
                Assert.Empty(bad);
                Assert.Empty(FormulaReferences.Scan(m));
                return 0;
            });
        }
    }
}
