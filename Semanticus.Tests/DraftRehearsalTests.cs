using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The frames' change group (workflow-composer-v3, section 3): import Returns with its seven source columns,
    /// relate it to Sales and to Date, set four column types, add Return rate (a blank for the assistant), add
    /// Refund total (the starter template writes it), describe Return rate (a blank). Contoso-style names only.
    /// Shared by DraftRehearsalTests and ComposedChangeTests.
    /// </summary>
    internal static class ComposerFrame
    {
        internal sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        internal const string ReturnRate = "DIVIDE ( SUM ( Returns[Quantity] ), SUM ( Sales[Quantity] ) )";
        internal const string ReturnsM =
            "let Source = Sql.Database(\"contoso-sql.example.com\", \"ContosoDW\"), Returns = Source{[Schema=\"dbo\",Item=\"Returns\"]}[Data] in Returns";

        /// <summary>A model with Sales and Date, as the frame's model is before the run.</summary>
        internal static async Task<(LocalEngine engine, SessionManager sm)> OpenAsync(bool pro = false, string workspace = null)
        {
            var sm = new SessionManager();
            var engine = new LocalEngine(sm, new Fake(pro), workspace);
            await engine.CreateModelAsync("ComposerFrame", 1601);
            var sales = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(sales, "SalesLineKey", "Int64", "SalesLineKey", "human");
            await engine.CreateColumnAsync(sales, "Quantity", "Int64", "Quantity", "human");
            await engine.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "human");
            var date = await engine.CreateTableAsync("Date", "human");
            await engine.CreateColumnAsync(date, "Date", "DateTime", "Date", "human");
            return (engine, sm);
        }

        private static ComposedAction A(string op, params (string k, string v)[] args)
        {
            var a = new ComposedAction { Op = op };
            foreach (var (k, v) in args) a.Args[k] = v;
            return a;
        }

        private static ComposedAction Column(string name, string sourceType) =>
            A("create_column", ("tableRef", "table:Returns"), ("name", name), ("dataType", sourceType), ("sourceColumn", name));

        /// <summary>The seven change steps. The blanks are open unless the caller fills them.</summary>
        internal static ComposedChange Build(string formula = null, string description = null)
        {
            var step1 = new ComposedStep { Id = "import", Title = "Import the Returns table" };
            step1.Actions.Add(A("create_import_table", ("name", "Returns"), ("mExpression", ReturnsM)));
            // As read from the source schema: every column arrives as text until step 4 sets its type.
            foreach (var c in new[] { "ReturnKey", "SalesLineKey", "ReturnDate", "Quantity", "RefundAmount", "Reason", "Channel" })
                step1.Actions.Add(Column(c, c == "SalesLineKey" ? "Int64" : "String"));

            var step2 = new ComposedStep { Id = "rel-sales", Title = "Relate Returns to Sales" };
            step2.Actions.Add(A("create_relationship", ("fromColumnRef", "column:Returns/SalesLineKey"), ("toColumnRef", "column:Sales/SalesLineKey")));

            var step3 = new ComposedStep { Id = "rel-date", Title = "Relate Returns to Date" };
            step3.Actions.Add(A("create_relationship", ("fromColumnRef", "column:Returns/ReturnDate"), ("toColumnRef", "column:Date/Date")));

            var step4 = new ComposedStep { Id = "types", Title = "Set data types on 4 columns" };
            step4.Actions.Add(A("set_column_data_type", ("columnRef", "column:Returns/ReturnDate"), ("dataType", "DateTime")));
            step4.Actions.Add(A("set_column_data_type", ("columnRef", "column:Returns/Quantity"), ("dataType", "Int64")));
            step4.Actions.Add(A("set_column_data_type", ("columnRef", "column:Returns/RefundAmount"), ("dataType", "Decimal")));
            step4.Actions.Add(A("set_column_data_type", ("columnRef", "column:Returns/ReturnKey"), ("dataType", "Int64")));

            var step5 = new ComposedStep { Id = "return-rate", Title = "Add the measure Return rate" };
            step5.Actions.Add(A("create_measure", ("tableRef", "table:Returns"), ("name", "Return rate"),
                ("expression", "{{blanks.formula}}"), ("displayFolder", "Returns")));
            step5.Actions.Add(A("set_measure_format", ("objRef", "measure:Returns/Return rate"), ("formatString", "0.0%")));
            step5.Blanks.Add(new ComposedBlank { Name = "formula", Kind = "dax", Fills = "expression", Text = formula });
            step5.Checks.Add(new ComposedCheck { Kind = "valid_dax", Phase = "written" });
            step5.Checks.Add(new ComposedCheck { Kind = "refs_within", Phase = "written", Of = new[] { "Returns", "Sales" } });

            var step6 = new ComposedStep { Id = "refund-total", Title = "Add the measure Refund total" };
            step6.Actions.Add(A("starter_measure", ("tableRef", "table:Returns"), ("column", "RefundAmount"), ("option", "sum"), ("name", "Refund total")));

            var step7 = new ComposedStep { Id = "describe", Title = "Describe Return rate" };
            step7.Actions.Add(A("set_description", ("objRef", "measure:Returns/Return rate"), ("text", "{{blanks.description}}")));
            step7.Blanks.Add(new ComposedBlank { Name = "description", Kind = "description", Fills = "text", Text = description, MaxChars = 200 });
            step7.Checks.Add(new ComposedCheck { Kind = "max_chars", Phase = "written" });
            step7.Checks.Add(new ComposedCheck { Kind = "no_formula_text", Phase = "written" });

            return new ComposedChange
            {
                DraftId = "draft-frame",
                Title = "Import Returns and add return measures",
                Steps = new List<ComposedStep> { step1, step2, step3, step4, step5, step6, step7 },
            };
        }

        // The undo stack's own counts. TOM keeps the handler internal, so the test reads it by reflection rather
        // than widening the engine for a test.
        private static TabularEditor.TOMWrapper.Undo.UndoManager Undo(TabularEditor.TOMWrapper.Model m) =>
            ((TabularEditor.TOMWrapper.TabularModelHandler)typeof(TabularEditor.TOMWrapper.TabularObject)
                .GetField("Handler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .GetValue(m)).UndoManager;
        internal static Task<int> UndoStepsAsync(SessionManager sm) => sm.Current.ReadAsync(m => Undo(m).UndoSteps);
        internal static Task<int> RedoSizeAsync(SessionManager sm) => sm.Current.ReadAsync(m => Undo(m).RedoSize);
        internal static Task<bool> HasTableAsync(SessionManager sm, string name) => sm.Current.ReadAsync(m => m.Tables.Contains(name));
    }

    /// <summary>
    /// Composer contract section 7: a written check runs against a rehearsal of the draft, so a formula over a
    /// table this same run adds can be checked before anything changes (v3 prove item 3), and it is held to the
    /// write path's refusal rule, not to DaxValidation.Valid.
    /// </summary>
    public sealed class DraftRehearsalTests
    {
        // v3 prove item 3, written first. Return rate reads Returns; Returns exists only in the draft.
        //
        // Both spellings are pinned. Until F-263 the offline validator skipped an UNQUOTED name it did not know
        // (Returns[Quantity] "could be a variable"), so the frame's unquoted formula passed against the open model;
        // it now reads as an unknown table unless the expression declares the name, so both forms fail alone.
        internal const string QuotedReturnRate = "DIVIDE ( SUM ( 'Returns'[Quantity] ), SUM ( 'Sales'[Quantity] ) )";

        [Fact]
        public async Task ReturnRateFormulaPassesAgainstTheRehearsedDraftAndFailsAgainstTheOpenModel()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync();
            using (sm)
            {
                // The open model alone: the same text, the same check, but no step that imports Returns before it.
                var alone = ComposerFrame.Build();
                alone.Steps.RemoveAll(st => st.Id != "return-rate");
                alone.Steps[0].Actions.RemoveAll(a => a.Op != "create_measure");
                alone.Steps[0].Actions[0].Args["tableRef"] = "table:Sales";   // Returns is not there to hold it
                foreach (var formula in new[] { QuotedReturnRate, ComposerFrame.ReturnRate })
                {
                    var refused = await engine.CheckDraftBlankAsync(alone, "return-rate", "formula", formula);
                    Assert.False(refused.Passed, formula);
                    var why = Assert.Single(refused.Checks, c => c.Kind == "valid_dax");
                    Assert.Equal(ComposedCheckStatus.Failed, why.Status);
                    Assert.Contains("Returns", why.Text);
                }

                // The rehearsed draft: Returns and its seven source columns exist, so the formula passes.
                foreach (var formula in new[] { QuotedReturnRate, ComposerFrame.ReturnRate })
                {
                    var result = await engine.CheckDraftBlankAsync(ComposerFrame.Build(), "return-rate", "formula", formula);
                    Assert.Null(result.RehearsalProblem);
                    Assert.True(result.Passed, string.Join(" | ", result.Checks.Select(c => c.Kind + ":" + c.Status + ":" + c.Text)));
                    Assert.Contains(result.Checks, c => c.Kind == "valid_dax" && c.Status == ComposedCheckStatus.Passed);
                    Assert.Contains(result.Checks, c => c.Kind == "refs_within" && c.Status == ComposedCheckStatus.Passed);
                }
            }
        }

        // A formula that reaches outside the tables the step allows fails refs_within, even though it is valid.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task AFormulaOutsideTheNamedTablesFailsRefsWithin()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync();
            using (sm)
            {
                var result = await engine.CheckDraftBlankAsync(ComposerFrame.Build(), "return-rate", "formula",
                    "DIVIDE ( SUM ( Returns[Quantity] ), COUNTROWS ( 'Date' ) )");
                Assert.False(result.Passed);
                Assert.Contains(result.Checks, c => c.Kind == "valid_dax" && c.Status == ComposedCheckStatus.Passed);
                var refs = Assert.Single(result.Checks, c => c.Kind == "refs_within");
                Assert.Equal(ComposedCheckStatus.Failed, refs.Status);
                Assert.Contains("Date", refs.Text);
            }
        }

        // Sol 24 Sep. Returns[Refund] is in neither the model nor the draft. DaxValidator reports an unknown
        // reference as a WARNING, so DaxValidation.Valid stays true; the write path refuses any diagnostic, and so
        // must valid_dax.
        [Fact]
        public async Task UnknownColumnBlankDoesNotPass()
        {
            const string bad = "DIVIDE ( SUM ( Returns[Refund] ), SUM ( Sales[Quantity] ) )";
            var (engine, sm) = await ComposerFrame.OpenAsync();
            using (sm)
            {
                var result = await engine.CheckDraftBlankAsync(ComposerFrame.Build(), "return-rate", "formula", bad);

                Assert.False(result.Passed);
                var valid = Assert.Single(result.Checks, c => c.Kind == "valid_dax");
                Assert.Equal(ComposedCheckStatus.Failed, valid.Status);
                Assert.Contains("Refund", valid.Text);

                // The premise this test exists for: the validator alone would have called it valid.
                var rehearsedValid = await engine.RehearsedValidityForTestAsync(ComposerFrame.Build(), "return-rate", bad);
                Assert.True(rehearsedValid.Valid);
                Assert.NotEmpty(rehearsedValid.Diagnostics);
            }
        }

        [Fact]
        public async Task TheRehearsalLeavesNoUndoEntryNoRevisionAndNoBroadcast()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync();
            using (sm)
            {
                var undoBefore = await ComposerFrame.UndoStepsAsync(sm);
                var redoBefore = await ComposerFrame.RedoSizeAsync(sm);
                var revBefore = sm.Current.Revision;
                var seen = new List<ChangeNotification>();
                Action<ChangeNotification> handler = n => seen.Add(n);
                sm.Bus.Changed += handler;
                try
                {
                    await engine.CheckDraftBlankAsync(ComposerFrame.Build(), "return-rate", "formula", ComposerFrame.ReturnRate);
                    var report = await engine.RehearseComposedChangeAsync(ComposerFrame.Build(ComposerFrame.ReturnRate, "Share of units sold that came back."));
                    Assert.True(report.WouldSucceed, report.Problem);
                }
                finally { sm.Bus.Changed -= handler; }

                Assert.Empty(seen);
                Assert.Equal(revBefore, sm.Current.Revision);
                Assert.Equal(undoBefore, await ComposerFrame.UndoStepsAsync(sm));
                Assert.Equal(redoBefore, await ComposerFrame.RedoSizeAsync(sm));
                Assert.False(await ComposerFrame.HasTableAsync(sm, "Returns"));
            }
        }

        // An earlier blank still open rehearses as a placeholder, so the steps after it can still be checked.
        [Fact]
        public async Task AnOpenEarlierBlankRehearsesAsAPlaceholder()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync();
            using (sm)
            {
                // The formula blank in step 5 is open; step 7 describes the measure step 5 creates.
                var result = await engine.CheckDraftBlankAsync(ComposerFrame.Build(), "describe", "description",
                    "Share of units sold that came back as returns. Higher is worse.");
                Assert.Null(result.RehearsalProblem);
                Assert.True(result.Passed, string.Join(" | ", result.Checks.Select(c => c.Kind + ":" + c.Status + ":" + c.Text)));

                // A description that carries formula text fails its written check, and one over the limit fails too.
                var formulaText = await engine.CheckDraftBlankAsync(ComposerFrame.Build(), "describe", "description",
                    "Computed as DIVIDE ( SUM ( Returns[Quantity] ), SUM ( Sales[Quantity] ) ).");
                Assert.False(formulaText.Passed);
                Assert.Contains(formulaText.Checks, c => c.Kind == "no_formula_text" && c.Status == ComposedCheckStatus.Failed);
                var tooLong = await engine.CheckDraftBlankAsync(ComposerFrame.Build(), "describe", "description", new string('x', 201));
                Assert.Contains(tooLong.Checks, c => c.Kind == "max_chars" && c.Status == ComposedCheckStatus.Failed);
            }
        }

        // The rehearsal is also the before-run dry run of the whole change: what it would add and change.
        [Fact]
        public async Task TheWholeChangeRehearsesAndCountsWhatItWouldAdd()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync();
            using (sm)
            {
                var report = await engine.RehearseComposedChangeAsync(ComposerFrame.Build());   // blanks open: placeholders
                Assert.True(report.WouldSucceed, report.Problem);
                Assert.Equal(1, report.Kinds["create_import_table"]);
                Assert.Equal(7, report.Kinds["create_column"]);
                Assert.Equal(2, report.Kinds["create_relationship"]);
                Assert.Equal(2, report.Kinds["create_measure"]);
                Assert.False(report.Kinds.ContainsKey("set_data_type"));   // the four types folded into their columns (3.3)
            }
        }
    }
}
