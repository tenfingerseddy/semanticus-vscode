// Verifier, m11/psc4 (plan statement check 4 fixes), 4 Oct. Attacks on P1 (a rename keeps every formula whole outside the start build too) and P2 (the broken-formula
// check gives no finding on valid DAX). A test left failing here names a defect in the lane; a passing one is evidence the attack did not land.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifyPsc4FixesTests
    {
        private static ReadinessRule Rule() => ReadinessRuleSet.Default().Single(r => r.Id == "DAX-REFERENCE");

        private static async Task<(LocalEngine Engine, Session S)> ModelAsync(Action<Model> build)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Free);
            await engine.CreateModelAsync("VerifyPsc4", 1604);
            var s = sessions.Require();
            if (build != null) await s.MutateAsync("human", "build", build);
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
        }

        // ------------------------------------------------------------------ P2: valid DAX is never reported as broken

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("table constructor", "SUMX ( { 1, 2, 3 }, [Value] )")]
        [InlineData("table constructor, two columns", "MAXX ( { ( 1, 2 ) }, [Value2] )")]
        [InlineData("GENERATESERIES", "SUMX ( GENERATESERIES ( 1, 10, 1 ), [Value] )")]
        [InlineData("a measure named with its table, quoted", "'Sales'[Total Amount] * 2")]
        [InlineData("a measure named with its table", "Sales[Total Amount] * 2")]
        [InlineData("two names with an apostrophe", "DIVIDE ( [Owner's Total], [Owner's Total] + 1 )")]
        [InlineData("an apostrophe name and a quoted table", "[Owner's Total] + SUM ( 'Sales'[Amount] )")]
        [InlineData("an apostrophe name and a string with brackets", "IF ( [Owner's Total] < 0, \"#,0;[Red]-#,0\", \"#,0\" )")]
        [InlineData("a column name with an escaped bracket", "SUM ( 'Sales'[Size [cm]]] )")]
        [InlineData("a table variable", "VAR t = FILTER ( 'Sales', 'Sales'[Amount] > 0 ) RETURN SUMX ( t, t[Amount] )")]
        [InlineData("a column the formula names itself", "SUMX ( SELECTCOLUMNS ( 'Sales', \"Amt\", 'Sales'[Amount] ), [Amt] )")]
        [InlineData("DATATABLE", "COUNTROWS ( DATATABLE ( \"K\", INTEGER, { { 1 }, { 2 } } ) )")]
        [InlineData("a measure with spaces and a variable", "VAR x = [Total Amount] RETURN x / 2")]
        [InlineData("a calculation group column", "CALCULATE ( [Total Amount], 'Time'[Name] = \"YTD\" )")]
        public async Task Valid_DAX_gives_no_broken_formula_finding(string what, string dax)
        {
            var (engine, s) = await ModelAsync(m =>
            {
                Sales(m);
                m.AddCalculationGroup("Time").AddCalculationItem("YTD", "CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'Sales'[Order Date] ) )");
                m.AddRole("Readers").RowLevelSecurity[m.Tables["Sales"]] = "[Region] = USERPRINCIPALNAME ()";
                m.Tables["Sales"].AddMeasure("Probe", dax);
            });
            using (engine)
            {
                var ev = await s.ReadAsync(m => Rule().Evaluate(m));
                Assert.True(ev.Violations.Count == 0, what + ": valid DAX reported as a formula that refers to an object the model does not have: "
                    + string.Join(" | ", ev.Violations.Select(v => v.ObjectRef + " " + v.Message)));
                var bpa = await s.ReadAsync(m => BpaAnalyzer.Analyze(m, BpaRuleSet.ForModel(m)));
                Assert.DoesNotContain(bpa.Violations, v => v.RuleId == BpaGrading.BrokenFormulaRuleId);
            }
        }

        // ------------------------------------------------------------------ P1: renames outside the start build

        private static string[] AllFormulas(Model m) => FormulaReferences.Holders(m).SelectMany(h => FormulaReferences.FormulasOf(h).Select(f => h.Name + "/" + f.Formula + "=" + f.Text)).ToArray();

        private static string[] Broken(Model m) => FormulaReferences.Scan(m).Select(b => b.Object.Name + ": " + FormulaReferences.Describe(b)).ToArray();

        [Fact]
        public async Task A_formula_changed_earlier_in_the_same_batch_follows_a_rename_in_that_batch()
        {
            // apply_plan's shape: DAX rewrites first, renames last, one transaction (LocalEngine.cs ApplyPlan: "renames apply last").
            var (engine, s) = await ModelAsync(m => { Sales(m); m.Tables["Sales"].AddMeasure("Later", "1"); });
            using (engine)
            {
                await s.MutateAsync("human", "rewrite then rename", m =>
                {
                    var t = m.Tables["Sales"];
                    t.Measures["Later"].Expression = "SUM ( 'Sales'[Region] ) + [Total Amount]";
                    s.Rename(t.Columns["Region"], "Area");
                    s.Rename(t.Measures["Total Amount"], "Amount Total");
                    s.Rename(t, "Orders");
                });
                var later = await s.ReadAsync(m => m.Tables["Orders"].Measures["Later"].Expression);
                Assert.Equal("SUM ( 'Orders'[Area] ) + [Amount Total]", later);
                Assert.Empty(await s.ReadAsync(Broken));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Formulas_written_by_apply_dax_script_follow_a_later_rename()
        {
            var (engine, s) = await ModelAsync(m => { Sales(m); m.Tables["Sales"].AddMeasure("Scripted", "1"); });
            using (engine)
            {
                var dax = await engine.ScriptObjectsAsync(new[] { "measure:Sales/Scripted" }, "dax");
                var lines = dax.Replace("\r", "").Split('\n').ToList();
                var body = lines.FindLastIndex(l => l.Trim() == "1");
                Assert.True(body >= 0, "no body line in the DAX script:\n" + dax);
                lines[body] = "SUM ( 'Sales'[Region] ) + [Total Amount]";
                var applied = await engine.ApplyDaxScriptAsync(string.Join("\n", lines), "agent");
                Assert.Contains("measure:Sales/Scripted", applied.Applied);
                await engine.RenameObjectAsync("column:Sales/Region", "Area", "agent");
                await engine.RenameObjectAsync("measure:Sales/Total Amount", "Amount Total", "agent");
                await engine.RenameObjectAsync("table:Sales", "Orders", "agent");
                Assert.Equal("SUM ( 'Orders'[Area] ) + [Amount Total]", await s.ReadAsync(m => m.Tables["Orders"].Measures["Scripted"].Expression.Trim()));
                Assert.Empty(await s.ReadAsync(Broken));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Formulas_written_by_apply_tmdl_follow_a_later_rename()
        {
            var (engine, s) = await ModelAsync(m => { Sales(m); m.Tables["Sales"].AddMeasure("Scripted", "1"); });
            using (engine)
            {
                var tmdl = await engine.ScriptObjectsAsync(new[] { "measure:Sales/Scripted" }, "tmdl");
                Assert.Contains("measure Scripted = 1", tmdl);
                var applied = await engine.ApplyTmdlScriptAsync(tmdl.Replace("measure Scripted = 1", "measure Scripted = SUM ( 'Sales'[Region] ) + [Total Amount]"), "agent");
                Assert.True(applied.Applied.Length > 0, "apply_tmdl applied nothing: " + string.Join("; ", applied.Notes ?? Array.Empty<string>()) + " " + string.Join("; ", applied.Skipped ?? Array.Empty<string>()));
                Assert.Equal("SUM ( 'Sales'[Region] ) + [Total Amount]", await s.ReadAsync(m => m.Tables["Sales"].Measures["Scripted"].Expression.Trim()));
                await engine.RenameObjectAsync("column:Sales/Region", "Area", "agent");
                await engine.RenameObjectAsync("measure:Sales/Total Amount", "Amount Total", "agent");
                await engine.RenameObjectAsync("table:Sales", "Orders", "agent");
                Assert.Equal("SUM ( 'Orders'[Area] ) + [Amount Total]", await s.ReadAsync(m => m.Tables["Orders"].Measures["Scripted"].Expression.Trim()));
                Assert.Empty(await s.ReadAsync(Broken));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_KPI_follows_a_rename_made_through_the_door_in_a_later_call()
        {
            var (engine, s) = await ModelAsync(m =>
            {
                Sales(m);
                var kpi = m.Tables["Sales"].Measures["Total Amount"].AddKPI();
                kpi.TargetExpression = "SUM ( 'Sales'[Amount] ) * 2";
                kpi.StatusExpression = "IF ( [Total Amount] > 0, 1, 0 )";
                kpi.TrendExpression = "[Owner's Total] - 1";
            });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Sales/Amount", "Value", "agent");
                await engine.RenameObjectAsync("measure:Sales/Owner's Total", "Owner Total", "agent");
                await engine.RenameObjectAsync("table:Sales", "Orders", "human");
                var kpi = await s.ReadAsync(m => { var k = m.Tables["Orders"].Measures["Total Amount"].KPI; return k.TargetExpression + "|" + k.StatusExpression + "|" + k.TrendExpression; });
                Assert.Equal("SUM ( 'Orders'[Value] ) * 2|IF ( [Total Amount] > 0, 1, 0 )|[Owner Total] - 1", kpi);
                Assert.Empty(await s.ReadAsync(Broken));
            }
        }

        private static void BuildAll(Model m)
        {
            var fact = m.AddTable("fact_sales");
            fact.AddDataColumn("AMT", "AMT", null, DataType.Decimal);
            fact.AddDataColumn("DT", "DT", null, DataType.DateTime);
            var total = fact.AddMeasure("Total AMT", "SUM ( 'fact_sales'[AMT] )");
            fact.AddMeasure("Share", "DIVIDE ( [Total AMT], CALCULATE ( [Total AMT], ALL ( 'fact_sales' ) ) )").FormatStringExpression = "IF ( [Total AMT] > 0, \"0.0%\", \"0%\" )";
            var kpi = total.AddKPI();
            kpi.TargetExpression = "[Total AMT] * 2";
            kpi.StatusExpression = "IF ( [Total AMT] > 0, 1, 0 )";
            kpi.TrendExpression = "[Total AMT] - 1";
            fact.AddCalculatedColumn("Doubled", "'fact_sales'[AMT] * 2");
            m.AddCalculationGroup("Time calc").AddCalculationItem("Selected", "CALCULATE ( SELECTEDMEASURE (), 'fact_sales'[DT] > 0 )").FormatStringExpression = "FORMAT ( SUM ( 'fact_sales'[AMT] ), \"0\" )";
            m.AddRole("Readers").RowLevelSecurity[fact] = "'fact_sales'[AMT] > 0";
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Undo_then_redo_of_a_batch_that_built_and_renamed_keeps_every_formula_whole()
        {
            var (engine, s) = await ModelAsync(null);
            using (engine)
            {
                await s.MutateAsync("human", "build and rename in one batch", m =>
                {
                    BuildAll(m);
                    var fact = m.Tables["fact_sales"];
                    s.Rename(fact.Columns["AMT"], "Amount");
                    s.Rename(fact.Columns["DT"], "Day");
                    s.Rename(fact.Measures["Total AMT"], "Total Amount");
                    s.Rename(fact, "Facts");
                });
                var built = await s.ReadAsync(AllFormulas);
                Assert.Empty(await s.ReadAsync(Broken));
                await engine.UndoAsync("human");
                Assert.Empty(await s.ReadAsync(m => m.Tables.Select(t => t.Name).ToArray()));
                await engine.RedoAsync("human");
                Assert.Equal(built, await s.ReadAsync(AllFormulas));
                Assert.Empty(await s.ReadAsync(Broken));

                // A rename batch of its own, undone and redone, then renamed again: the formulas follow every step.
                await s.MutateAsync("human", "rename again", m => { s.Rename(m.Tables["Facts"], "Sales Facts"); s.Rename(m.Tables["Sales Facts"].Columns["Amount"], "Value"); });
                var renamed = await s.ReadAsync(AllFormulas);
                Assert.Empty(await s.ReadAsync(Broken));
                await engine.UndoAsync("human");
                Assert.Equal(built, await s.ReadAsync(AllFormulas));
                await engine.RedoAsync("human");
                Assert.Equal(renamed, await s.ReadAsync(AllFormulas));
                await engine.RenameObjectAsync("table:Sales Facts", "Facts Again", "agent");
                Assert.Empty(await s.ReadAsync(Broken));
                Assert.Contains(await s.ReadAsync(AllFormulas), f => f.Contains("'Facts Again'[Value]") && f.StartsWith("Readers/"));
            }
        }
    }

    /// <summary>The Free start build of plan statement check 4, then undo and redo: the built model's formulas stay whole on every step.</summary>
    [Collection("restore-root")]
    public sealed class VerifyPsc4StartUndoTests : IDisposable
    {
        private const string Server = "wh-j8.datawarehouse.fabric.microsoft.com", Db = "ContosoConsulting";
        private readonly RestoreRootFixture _fixture;
        private readonly string _root;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public VerifyPsc4StartUndoTests(RestoreRootFixture fixture)
        {
            _fixture = fixture;
            _root = Path.Combine(Path.GetTempPath(), "vp4-" + Guid.NewGuid().ToString("N").Substring(0, 6));
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

        private static string[] Broken(Model m) => FormulaReferences.Scan(m).Select(b => b.Object.Name + ": " + FormulaReferences.Describe(b)).ToArray();

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task A_start_build_undone_and_redone_keeps_every_formula_whole(bool keyless)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Free, Path.Combine(_root, "ws"));
            _owned.Add(engine); _owned.Add(sessions);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            var schema = SqlDraftBuildTests.J8Schema();
            if (keyless)
            {
                foreach (var t in schema.Tables) t.KeyColumns = new string[0];
                schema.ForeignKeys = new FabricForeignKey[0];
            }
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(schema);
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = Path.Combine(_root, "model"), Name = "Uplift" }, "human");
            Assert.True(view.Exists);
            for (var pass = 0; pass < 60; pass++)
            {
                var page = await engine.GetStartWordsAsync(40, 0);
                if (page.Items.Count == 0) break;
                var wrote = await engine.WriteStartWordsAsync(WordsFor(page), "agent");
                var keeps = wrote.Refused.Where(r => r.Contains("keep_name")).Select(r => r.Substring(0, r.IndexOf(": ", StringComparison.Ordinal))).Distinct().ToArray();
                var kept = keeps.Length == 0 ? 0 : (await engine.WriteStartWordsAsync(keeps.Select(k => new StartWord { Ref = k, Kind = "keep_name" }).ToArray(), "agent")).Written;
                if (wrote.Written + kept == 0) break;
            }
            await engine.StartPreviewSettledForTest();
            view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);
            Assert.True(view.CanBuild, view.BlockedBecause);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var s = sessions.Current;
            var formulas = await s.ReadAsync(m => FormulaReferences.CountFormulas(m));
            Assert.True(formulas > 0, "the build made no formula");
            Assert.Empty(await s.ReadAsync(Broken));
            var measures = await s.ReadAsync(m => m.AllMeasures.Select(x => x.Name + "=" + x.Expression).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            await engine.UndoAsync("human");
            Assert.Empty(await s.ReadAsync(Broken));
            await engine.RedoAsync("human");
            Assert.Empty(await s.ReadAsync(Broken));
            Assert.Equal(measures, await s.ReadAsync(m => m.AllMeasures.Select(x => x.Name + "=" + x.Expression).OrderBy(x => x, StringComparer.Ordinal).ToArray()));
        }
    }
}
