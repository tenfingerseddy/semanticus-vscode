using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// THE FORMAT FILL RULE, round V1f (coordinator addendum 1, 29 Sep, after the V1e FAIL). One test group per addendum
    /// item, each written to fail on the round V1e code and pass on the fix, on both doors and both tiers where a door or a
    /// tier is involved:
    ///  1. Jev's format lines ask FormatFill.MayFill: a line it proves may arrive approved, any other is a suggestion the
    ///     person keeps, never applied by Run the plan.
    ///  2. A calculation group item or expression with no format string expression of its own stops every measure fill.
    ///  3. Structure, not names: MIN and MAX are never proved, a column is a quantity only by what the model says about it.
    ///  4. A // or -- comment ends at every line break DAX reads.
    ///  5. A skipped planned fill says why in true words.
    ///  6. The deploy gate is never blocked by a format the rule leaves blank.
    ///  7. Blank spaces in a format are the person's: counted as blank for advice, never written over.
    /// plus the two copy items: the advice for a pair that is not a finding, and the date column's words.
    /// </summary>
    public sealed class DogfoodFormatFillV1fTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public DogfoodFormatFillV1fTests(ModelTemplates templates) => _templates = templates;

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "test setup", edit);

        private static DecisionFixture NewFixture(string tier) =>
            new DecisionFixture(tier == "pro" ? TestEntitlements.Pro : TestEntitlements.Free);

        /// <summary>Every write path a format fill has, on both doors: the fix for one finding, Fix all, apply_safe_fixes, and
        /// every fix the change plan seeds. A refusal is an acceptable answer for the single fix.</summary>
        private static async Task PressEverythingAsync(DecisionFixture f, string ruleId, params string[] objRefs)
        {
            var rpc = new EngineRpcTarget(f.Engine);
            foreach (var objRef in objRefs)
            {
                try { await McpTools.ApplyFix(f.Engine, "bpa", ruleId, objRef); } catch (InvalidOperationException) { }
                try { await rpc.bpaFix(ruleId, objRef); } catch (InvalidOperationException) { }
            }
            await rpc.bpaFixAll();
            await McpTools.ApplySafeFixes(f.Engine, "bpa");
            var plan = await McpTools.ProposePlan(f.Engine, null, false, 0);
            var fixes = plan.Items.Where(i => i.Kind == "bpa_fix").Select(i => i.Id).ToArray();
            if (fixes.Length > 0) await McpTools.ApplyPlan(f.Engine, fixes);
            var studioPlan = await rpc.proposePlan(null, false, 0);
            var studioFixes = studioPlan.Items.Where(i => i.Kind == "bpa_fix").Select(i => i.Id).ToArray();
            if (studioFixes.Length > 0) await rpc.applyPlan(studioFixes);
        }

        private static async Task<string> MeasureFormatAsync(DecisionFixture f, string table, string measure) =>
            await f.Sessions.Require().ReadAsync(m => m.Tables[table].Measures[measure].FormatString ?? "");

        // ---- item 3 (and 2's controls): structure, not names ----------------------------------------------

        /// <summary>One model in which each Int64 column differs from a plain quantity in exactly one way the model itself
        /// can say: a key, a relationship end (either side), a sort-by column, a hierarchy level, a data category, unique, or
        /// summarised by None. The names are ordinary words no name list would veto, so what decides each is structure.</summary>
        // Lean suite: each model is built once for the class and each test opens a copy.
        private async Task<DecisionFixture> StructureModelAsync(string tier, bool toSideRelationship = true)
        {
            var f = NewFixture(tier);
            await _templates.OpenCopyAsync(f, "structure-" + toSideRelationship, g => BuildStructureModelAsync(g, toSideRelationship), "Structure.bim");
            return f;
        }

        private static async Task BuildStructureModelAsync(DecisionFixture f, bool toSideRelationship)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Structure", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateTableAsync("Other", "human");
            await e.CreateColumnAsync("table:Other", "Pick", "Int64", "Pick", "human");
            await e.CreateColumnAsync("table:Other", "Fk", "Int64", "Fk", "human");
            foreach (var name in new[]
            {
                "Quantity", "Units Sold", "Units Explicit", "Units None", "Units Keyed", "Units From", "Units To", "Units Cat",
                "Units Level", "Units Unique", "Units Sorted",
                // the names a list can never finish: each is an Int64 with no other sign, so only the name can veto it
                "FY", "FISCALYEAR", "ordernumber", "Invoice #", "CustomerID2", "EMPNUM", "YYYYMM",
            })
                await e.CreateColumnAsync("table:Sales", name, "Int64", name.Replace(" ", "").Replace("#", "N"), "human");
            await e.CreateColumnAsync("table:Sales", "Label", "String", "Label", "human");
            await e.CreateRelationshipAsync("column:Sales/Units From", "column:Other/Pick", null, true, "human");
            // The shipped MARK_PRIMARY_KEYS fix marks the "one" end of a relationship as the table's key, and the wrapper allows one key
            // column a table, so under Fix all this end would un-key "Units Keyed". The doors test therefore leaves it out; the unit test keeps it.
            if (toSideRelationship) await e.CreateRelationshipAsync("column:Other/Fk", "column:Sales/Units To", null, false, "human");
            await Mutate(f, m =>
            {
                var t = m.Tables["Sales"];
                t.Columns["Units Explicit"].SummarizeBy = AggregateFunction.Sum;
                t.Columns["Units None"].SummarizeBy = AggregateFunction.None;
                t.Columns["Units Keyed"].IsKey = true;
                t.Columns["Units Cat"].DataCategory = "Years";
                t.Columns["Units Unique"].IsUnique = true;
                t.Columns["Label"].SortByColumn = t.Columns["Units Sorted"];
                t.AddHierarchy("Levels", null, "Units Level");
            });
        }

        private static readonly (string column, bool fills)[] ColumnExpectations =
        {
            ("Quantity", true), ("Units Sold", true), ("Units Explicit", true),
            ("Units None", false), ("Units Keyed", false), ("Units From", false), ("Units To", false), ("Units Cat", false),
            ("Units Level", false), ("Units Unique", false), ("Units Sorted", false),
            ("FY", false), ("FISCALYEAR", false), ("ordernumber", false), ("Invoice #", false), ("CustomerID2", false),
            ("EMPNUM", false), ("YYYYMM", false),
        };

        /// <summary>ADDENDUM 3: a column's own format, and a SUM of it, are proved only by structure. MIN and MAX are never
        /// proved. COUNT, COUNTROWS, DISTINCTCOUNT and CALCULATE of those stay proved on any column, key included.</summary>
        [Fact]
        public async Task Structure_and_never_a_name_decides_a_quantity_column_and_a_sum_of_it()
        {
            using var f = await StructureModelAsync("pro");
            // Lean suite: the 58 measures go in through one write of the model, not 58 engine calls; the proof reads the model either way.
            await Mutate(f, m =>
            {
                var t = m.Tables["Sales"];
                foreach (var (column, _) in ColumnExpectations)
                {
                    t.AddMeasure("Sum " + column, "SUM ( Sales[" + column + "] )");
                    t.AddMeasure("Max " + column, "MAX ( Sales[" + column + "] )");
                    t.AddMeasure("Min " + column, "MIN ( Sales[" + column + "] )");
                }
                t.AddMeasure("Rows", "COUNTROWS ( Sales )");
                t.AddMeasure("Keys", "DISTINCTCOUNT ( Sales[Units Keyed] )");
                t.AddMeasure("Filtered Keys", "CALCULATE ( DISTINCTCOUNT ( Sales[Units Keyed] ), Sales[Quantity] > 1 )");
                t.AddMeasure("Years Seen", "COUNT ( Sales[FY] )");
            });

            var wrong = await f.Sessions.Require().ReadAsync(m =>
            {
                var problems = new List<string>();
                var t = m.Tables["Sales"];
                foreach (var (column, fills) in ColumnExpectations)
                {
                    if (FormatFill.MayFill(t.Columns[column], "#,0") != fills) problems.Add($"column {column} may fill = {!fills}");
                    if (FormatFill.MayFill(t.Measures["Sum " + column], "#,0") != fills) problems.Add($"SUM ( {column} ) may fill = {!fills}");
                    if (FormatFill.MayFill(t.Measures["Max " + column], "#,0")) problems.Add($"MAX ( {column} ) may fill");
                    if (FormatFill.MayFill(t.Measures["Min " + column], "#,0")) problems.Add($"MIN ( {column} ) may fill");
                }
                foreach (var counted in new[] { "Rows", "Keys", "Filtered Keys", "Years Seen" })
                    if (!FormatFill.MayFill(t.Measures[counted], "#,0")) problems.Add($"{counted}, a count, may not fill");
                return problems;
            });
            Assert.True(wrong.Count == 0, "the fill proof is not structural: " + string.Join("; ", wrong));
        }

        /// <summary>ADDENDUM 3 through the doors: every automatic path on both doors and both tiers fills only what the proof
        /// allows on that model, so no year, key, sorted, related, unique or None column and no MIN or MAX is touched.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("pro")]
        [InlineData("free")]
        public async Task Every_automatic_path_writes_only_the_proven_formats_on_the_structure_model(string tier)
        {
            using var f = await StructureModelAsync(tier, toSideRelationship: false);
            var e = f.Engine;
            var expectations = ColumnExpectations.Where(c => c.column != "Units To").ToArray();
            foreach (var (column, _) in expectations)
            {
                await e.CreateMeasureAsync("table:Sales", "Sum " + column, "SUM ( Sales[" + column + "] )", "human");
                await e.CreateMeasureAsync("table:Sales", "Max " + column, "MAX ( Sales[" + column + "] )", "human");
            }
            await e.CreateMeasureAsync("table:Sales", "Rows", "COUNTROWS ( Sales )", "human");
            // The shipped NUMERIC_COLUMN_SUMMARIZE_BY fix (Fix all, visible numeric columns only) sets SummarizeBy to None, which
            // takes a quantity column out of the fill. Hidden columns are outside that rule, so the order of the fixes cannot
            // decide what this test proves.
            await Mutate(f, m => { foreach (var c in m.Tables["Sales"].Columns) c.IsHidden = true; });
            await e.LoadBpaRulesAsync("[{\"ID\":\"TEST_COLUMN_WHOLE\",\"Name\":\"Whole-number columns\",\"Category\":\"Formatting\",\"Severity\":2,"
                + "\"Scope\":\"DataColumn\",\"Expression\":\"DataType = \\\"Int64\\\" and (FormatString = null or FormatString = \\\"\\\")\","
                + "\"FixExpression\":\"FormatString = \\\"#,0\\\"\"}]", replace: false, "human");

            await PressEverythingAsync(f, "INTEGER_FORMATTING", "measure:Sales/Rows", "measure:Sales/Max FY", "measure:Sales/Sum FY");
            await PressEverythingAsync(f, "TEST_COLUMN_WHOLE", expectations.Select(c => "column:Sales/" + c.column).ToArray());

            var (measures, columns) = await f.Sessions.Require().ReadAsync(m =>
                (m.Tables["Sales"].Measures.ToDictionary(x => x.Name, x => x.FormatString ?? ""),
                 m.Tables["Sales"].Columns.ToDictionary(x => x.Name, x => x.FormatString ?? "")));
            var bad = new List<string>();
            foreach (var (column, fills) in expectations)
            {
                if ((columns[column] != "") != fills) bad.Add($"column {column} reads \"{columns[column]}\"");
                if ((measures["Sum " + column] != "") != fills) bad.Add($"SUM ( {column} ) reads \"{measures["Sum " + column]}\"");
                if (measures["Max " + column] != "") bad.Add($"MAX ( {column} ) reads \"{measures["Max " + column]}\"");
            }
            if (measures["Rows"] != "#,0") bad.Add($"the count Rows reads \"{measures["Rows"]}\", the control that must fill");
            Assert.True(bad.Count == 0, tier + ": " + string.Join("; ", bad));
        }

        // ---- item 4: line ends ----------------------------------------------------------------------------

        private static string LineBreak(string name) => name switch
        {
            "lf" => "\n",
            "cr" => "\r",
            "crlf" => "\r\n",
            "nel" => ((char)0x0085).ToString(),
            "ls" => ((char)0x2028).ToString(),
            "ps" => ((char)0x2029).ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        [Theory]
        [InlineData("lf")]
        [InlineData("cr")]
        [InlineData("crlf")]
        [InlineData("nel")]
        [InlineData("ls")]
        [InlineData("ps")]
        public void A_line_comment_ends_at_every_line_break_the_dax_lexer_reads(string lineBreak)
        {
            var toks = DaxScan.Tokenize("// a comment" + LineBreak(lineBreak) + "Live");
            var live = Assert.Single(toks);
            Assert.Equal(DaxScan.Kind.Word, live.Kind);
            Assert.Equal("Live", live.Text);
            var dashes = Assert.Single(DaxScan.Tokenize("-- a comment" + LineBreak(lineBreak) + "Live"));
            Assert.Equal("Live", dashes.Text);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("lf", "//")]
        [InlineData("cr", "//")]
        [InlineData("crlf", "//")]
        [InlineData("nel", "//")]
        [InlineData("ls", "//")]
        [InlineData("ps", "//")]
        [InlineData("cr", "--")]
        [InlineData("ls", "--")]
        public async Task A_division_after_any_line_break_is_not_hidden_in_a_comment_and_a_comment_alone_is_harmless(string lineBreak, string marker)
        {
            using var f = NewFixture("pro");
            var e = f.Engine;
            await e.CreateModelAsync("Breaks", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateMeasureAsync("table:Sales", "Per Day", "1", "human");
            await e.CreateMeasureAsync("table:Sales", "Plain", "1", "human");
            var lb = LineBreak(lineBreak);
            await Mutate(f, m =>
            {
                m.Tables["Sales"].Measures["Per Day"].Expression = "COUNTROWS ( Sales ) " + marker + " per day" + lb + "/ 30";
                m.Tables["Sales"].Measures["Plain"].Expression = "COUNTROWS ( Sales ) " + marker + " per day" + lb;
            });
            var (ratio, plain) = await f.Sessions.Require().ReadAsync(m =>
                (FormatFill.MayFill(m.Tables["Sales"].Measures["Per Day"], "#,0"), FormatFill.MayFill(m.Tables["Sales"].Measures["Plain"], "#,0")));
            Assert.False(ratio, $"COUNTROWS ( Sales ) / 30 after a {marker} comment ended by {lineBreak} is proved a count");
            // The TOM wrapper strips every carriage return from an expression it is given, so a comment "ended" by one reaches
            // the end of the text: a formula that ends inside a comment is not trusted either, and gets no fill (the safe side).
            Assert.Equal(lineBreak != "cr", plain);
        }

        // Default tier: the witness row of the sweep form, through the same body.
        [Fact]
        public Task A_division_after_any_line_break_is_not_hidden_in_a_comment_and_a_comment_alone_is_harmless_sample() =>
            A_division_after_any_line_break_is_not_hidden_in_a_comment_and_a_comment_alone_is_harmless("cr", "//");

        // ---- item 2: calculation groups -------------------------------------------------------------------

        private async Task<DecisionFixture> CalcGroupModelAsync(string kind)
        {
            var f = NewFixture("pro");
            await _templates.OpenCopyAsync(f, "calc-group-" + kind, g => BuildCalcGroupModelAsync(g, kind), "Groups.bim");
            return f;
        }

        private static async Task BuildCalcGroupModelAsync(DecisionFixture f, string kind)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Groups", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Units Sold", "Int64", "UnitsSold", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Sales", "Units", "SUM ( Sales[Units Sold] )", "human");
            var cg = await e.CreateCalculationGroupAsync("Views", "human");
            switch (kind)
            {
                case "item":
                    await e.CreateCalculationItemAsync(cg, "Per Day", "DIVIDE ( SELECTEDMEASURE (), 30 )", "human");
                    break;
                case "item with its own format":
                    await e.CreateCalculationItemAsync(cg, "Per Day", "DIVIDE ( SELECTEDMEASURE (), 30 )", "human");
                    await Mutate(f, m => ((CalculationGroupTable)m.Tables["Views"]).CalculationItems["Per Day"].FormatStringExpression = "\"0.0\"");
                    break;
                case "no-selection":
                    await Mutate(f, m => ((CalculationGroupTable)m.Tables["Views"]).NoSelectionExpression = "DIVIDE ( SELECTEDMEASURE (), 30 )");
                    break;
                case "no-selection with its own format":
                    await Mutate(f, m =>
                    {
                        var g = (CalculationGroupTable)m.Tables["Views"];
                        g.NoSelectionExpression = "DIVIDE ( SELECTEDMEASURE (), 30 )";
                        g.NoSelectionFormatStringExpression = "\"0.0\"";
                    });
                    break;
                case "multiple-or-empty selection":
                    await Mutate(f, m => ((CalculationGroupTable)m.Tables["Views"]).MultipleOrEmptySelectionExpression = "DIVIDE ( SELECTEDMEASURE (), 30 )");
                    break;
                case "multiple-or-empty selection with its own format":
                    await Mutate(f, m =>
                    {
                        var g = (CalculationGroupTable)m.Tables["Views"];
                        g.MultipleOrEmptySelectionExpression = "DIVIDE ( SELECTEDMEASURE (), 30 )";
                        g.MultipleOrEmptySelectionFormatStringExpression = "\"0.0\"";
                    });
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("item", false)]
        [InlineData("no-selection", false)]
        [InlineData("multiple-or-empty selection", false)]
        [InlineData("item with its own format", true)]
        [InlineData("no-selection with its own format", true)]
        [InlineData("multiple-or-empty selection with its own format", true)]
        public async Task A_calculation_group_without_its_own_format_stops_every_measure_fill_on_every_door(string kind, bool fills)
        {
            using var f = await CalcGroupModelAsync(kind);
            var (orders, units) = await f.Sessions.Require().ReadAsync(m =>
                (FormatFill.MayFill(m.Tables["Sales"].Measures["Orders"], "#,0"), FormatFill.MayFill(m.Tables["Sales"].Measures["Units"], "#,0")));
            Assert.Equal(fills, orders);
            Assert.Equal(fills, units);

            await PressEverythingAsync(f, "INTEGER_FORMATTING", "measure:Sales/Orders", "measure:Sales/Units");
            var formats = new[] { await MeasureFormatAsync(f, "Sales", "Orders"), await MeasureFormatAsync(f, "Sales", "Units") };
            Assert.All(formats, x => Assert.Equal(fills ? "#,0" : "", x));
        }

        // Default tier: the witness row of the sweep form, through the same body.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public Task A_calculation_group_without_its_own_format_stops_every_measure_fill_on_every_door_sample() =>
            A_calculation_group_without_its_own_format_stops_every_measure_fill_on_every_door("item", false);

        [Fact]
        public async Task The_calculation_group_stop_is_lifted_when_the_group_is_gone_and_never_touches_a_column_fill()
        {
            using var f = await CalcGroupModelAsync("item");
            Assert.True(await f.Sessions.Require().ReadAsync(m => FormatFill.MayFill(m.Tables["Sales"].Columns["Units Sold"], "#,0")),
                "a calculation group changes how a MEASURE reads, so it must not stop a column fill");
            await Mutate(f, m => m.Tables["Views"].Delete());
            Assert.True(await f.Sessions.Require().ReadAsync(m => FormatFill.MayFill(m.Tables["Sales"].Measures["Orders"], "#,0")));
        }

        // ---- item 7: blank spaces ------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_format_of_only_blank_spaces_counts_as_blank_for_advice_but_is_never_written_over_on_any_door()
        {
            using var f = NewFixture("pro");
            var e = f.Engine;
            await e.CreateModelAsync("Spaces", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Units Sold", "Int64", "UnitsSold", "human");
            foreach (var name in new[] { "Spaced", "Spaced Expression", "Blank" })
                await e.CreateMeasureAsync("table:Sales", name, "COUNTROWS ( Sales )", "human");
            await Mutate(f, m =>
            {
                m.Tables["Sales"].Measures["Spaced"].FormatString = "  ";
                // The wrapper drops a blank expression, so a file that carries one is imitated on the metadata object itself.
                var meta = typeof(Measure).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .First(x => x.Name == "MetadataObject" && x.PropertyType.Namespace == "Microsoft.AnalysisServices.Tabular")
                    .GetValue(m.Tables["Sales"].Measures["Spaced Expression"]);
                var tom = meta.GetType();
                tom.GetProperty("FormatStringDefinition").SetValue(meta,
                    Activator.CreateInstance(tom.GetProperty("FormatStringDefinition").PropertyType));
                var def = tom.GetProperty("FormatStringDefinition").GetValue(meta);
                def.GetType().GetProperty("Expression").SetValue(def, "  ");
                m.Tables["Sales"].Columns["Units Sold"].FormatString = " ";
            });
            var (advice, may) = await f.Sessions.Require().ReadAsync(m =>
            {
                var t = m.Tables["Sales"];
                var subjects = new ITabularNamedObject[] { t.Measures["Spaced"], t.Measures["Spaced Expression"], t.Columns["Units Sold"] };
                return (subjects.Select(FormatFill.HasNoFormat).ToArray(), subjects.Select(s => FormatFill.MayFill(s, "#,0")).ToArray());
            });
            Assert.All(advice, x => Assert.True(x, "a format of only blank spaces is still 'no format yet' for the advice words"));
            Assert.All(may, x => Assert.False(x, "a format of only blank spaces is text a person typed and is never written over"));

            await e.LoadBpaRulesAsync("[{\"ID\":\"TEST_COLUMN_WHOLE\",\"Name\":\"Whole-number columns\",\"Category\":\"Formatting\",\"Severity\":2,"
                + "\"Scope\":\"DataColumn\",\"Expression\":\"DataType = \\\"Int64\\\" and (FormatString = null or FormatString.Trim() = \\\"\\\")\","
                + "\"FixExpression\":\"FormatString = \\\"#,0\\\"\"}]", replace: false, "human");
            await PressEverythingAsync(f, "INTEGER_FORMATTING", "measure:Sales/Spaced", "measure:Sales/Spaced Expression", "measure:Sales/Blank");
            await PressEverythingAsync(f, "TEST_COLUMN_WHOLE", "column:Sales/Units Sold");
            var (spaced, expr, blank, column) = await f.Sessions.Require().ReadAsync(m =>
                (m.Tables["Sales"].Measures["Spaced"].FormatString, m.Tables["Sales"].Measures["Spaced Expression"].FormatStringExpression,
                 m.Tables["Sales"].Measures["Blank"].FormatString, m.Tables["Sales"].Columns["Units Sold"].FormatString));
            Assert.Equal("  ", spaced);
            Assert.Equal("  ", expr);   // still there, byte for byte
            Assert.Equal(" ", column);
            Assert.Equal("#,0", blank);   // the control: a truly blank count is still filled
        }

        // ---- item 5: the words on a skipped planned fill ---------------------------------------------------

        private static async Task<string> SkippedNoteAsync(DecisionFixture f, Func<Task> makeItStale)
        {
            var plan = await f.Engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, "human");
            var item = Assert.Single(plan.Items, i => i.Kind == "bpa_fix" && i.RuleId == "INTEGER_FORMATTING" && i.ObjectRef == "measure:Sales/Orders");
            await makeItStale();
            var report = await f.Engine.ApplyPlanAsync(new[] { item.Id }, "human");
            var done = report.Items.Single(i => i.Id == item.Id);
            Assert.Equal("skipped", done.Status);
            Assert.False(string.IsNullOrWhiteSpace(done.Note), "the skipped fill carries no note");
            return done.Note;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        // The first three are controls: another check (the plan's change guard, or "the rule no longer raises it") speaks first.
        [InlineData("a static format was typed", "changed after the plan")]
        [InlineData("blank spaces were typed", "changed after the plan")]
        [InlineData("a format string expression was set", "no longer raises")]
        [InlineData("the measure stopped being a count", "no longer a count")]
        [InlineData("a calculation group now reads the measure", "calculation group")]
        public async Task A_skipped_planned_fill_says_the_true_reason(string cause, string mustSay)
        {
            using var f = NewFixture("pro");
            var e = f.Engine;
            await e.CreateModelAsync("Skips", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            var note = await SkippedNoteAsync(f, async () =>
            {
                switch (cause)
                {
                    case "a static format was typed": await Mutate(f, m => m.Tables["Sales"].Measures["Orders"].FormatString = "0.00"); break;
                    case "blank spaces were typed": await Mutate(f, m => m.Tables["Sales"].Measures["Orders"].FormatString = "  "); break;
                    case "a format string expression was set": await Mutate(f, m => m.Tables["Sales"].Measures["Orders"].FormatStringExpression = "\"0.0\""); break;
                    case "the measure stopped being a count": await e.SetDaxAsync("measure:Sales/Orders", "DIVIDE ( COUNTROWS ( Sales ), 30 )", "human"); break;
                    default:
                        var cg = await e.CreateCalculationGroupAsync("Views", "human");
                        await e.CreateCalculationItemAsync(cg, "Per Day", "DIVIDE ( SELECTEDMEASURE (), 30 )", "human");
                        break;
                }
            });
            Assert.Contains(mustSay, note, StringComparison.OrdinalIgnoreCase);
            if (cause is "the measure stopped being a count" or "a calculation group now reads the measure")
            {
                Assert.DoesNotContain("already there", note, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("blank", note, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Default tier: the witness row of the sweep form, through the same body.
        [Fact]
        public Task A_skipped_planned_fill_says_the_true_reason_sample() =>
            A_skipped_planned_fill_says_the_true_reason("the measure stopped being a count", "no longer a count");

        // ---- item 6: the deploy gate ----------------------------------------------------------------------

        [Fact]
        public async Task A_measure_the_rule_leaves_blank_never_blocks_the_deploy_gate_and_shows_as_advice()
        {
            using var f = NewFixture("pro");
            var e = f.Engine;
            await e.CreateModelAsync("Gate", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Revenue", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Share", "DIVIDE ( [Revenue], 3 )", "human");
            await e.CreateMeasureAsync("table:Sales", "Other Rate", "DIVIDE ( [Revenue], 7 )", "human");

            var card = await e.BpaScanAsync();
            var findings = card.Violations.Where(v => v.RuleId == "PROVIDE_FORMAT_STRING_FOR_MEASURES").ToArray();
            Assert.Equal(3, findings.Length);
            Assert.All(findings, v => { Assert.False(v.CanAutoFix); Assert.True(v.Advice, "the blank-format finding is advice, not a fault"); });
            var blank = await e.DeployGateAsync(null, "human");

            await Mutate(f, m => { foreach (var x in m.Tables["Sales"].Measures) x.FormatString = "#,0.00"; });
            Assert.DoesNotContain((await e.BpaScanAsync()).Violations, v => v.RuleId == "PROVIDE_FORMAT_STRING_FOR_MEASURES");
            var formatted = await e.DeployGateAsync(null, "human");
            Assert.True(blank.BpaBlocking == formatted.BpaBlocking,
                $"a blank format blocks the deploy gate: {blank.BpaBlocking} blocking beside {formatted.BpaBlocking} once formatted. {string.Join(" | ", blank.Blockers)}");
            Assert.DoesNotContain(blank.Blockers, b => b.Contains("Provide format string", StringComparison.OrdinalIgnoreCase) || b.Contains("PROVIDE_FORMAT", StringComparison.Ordinal));

            // The control: a real error still blocks. The same gate, a severity 3 rule with no fix that a measure trips.
            await e.LoadBpaRulesAsync("[{\"ID\":\"TEST_BAD_NAME\",\"Name\":\"No bad names\",\"Category\":\"Test\",\"Severity\":3,"
                + "\"Scope\":\"Measure\",\"Expression\":\"Name.Contains(\\\"Bad\\\")\"}]", replace: false, "human");
            await e.CreateMeasureAsync("table:Sales", "Bad Measure", "1", "human");
            var real = await e.DeployGateAsync(null, "human");
            Assert.True(real.BpaBlocking > formatted.BpaBlocking, "a real severity 3 error must still block the gate");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_advice_for_a_measure_with_no_format_says_it_is_advice_on_both_doors()
        {
            using var f = NewFixture("pro");
            var e = f.Engine;
            await e.CreateModelAsync("Gate", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Revenue", "SUM ( Sales[Amount] )", "human");
            var mcp = await McpTools.GetFixPrompt(e, "bpa", "PROVIDE_FORMAT_STRING_FOR_MEASURES", "measure:Sales/Revenue");
            var studio = await new EngineRpcTarget(e).bpaGetFixPrompt("PROVIDE_FORMAT_STRING_FOR_MEASURES", "measure:Sales/Revenue");
            Assert.Equal("(advice)", mcp.Tool);
            Assert.Equal(JsonSerializer.Serialize(mcp), JsonSerializer.Serialize(studio));
            Assert.Contains("It has no format yet", mcp.Prompt, StringComparison.Ordinal);
            Assert.Contains("set_measure_format", mcp.Prompt, StringComparison.Ordinal);
        }

        // ---- low items 3 and 4: the words fit the object ----------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_advice_for_a_pair_the_rule_does_not_raise_says_so_and_suggests_no_format()
        {
            using var f = NewFixture("pro");
            var e = f.Engine;
            await e.CreateModelAsync("Pairs", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Rate", "DIVIDE ( SUM ( Sales[Amount] ), 3 )", "human");
            await Mutate(f, m => m.Tables["Sales"].Measures["Rate"].FormatString = "0.0%");
            Assert.DoesNotContain((await e.BpaScanAsync()).Violations, v => v.RuleId == "INTEGER_FORMATTING" && v.ObjectRef == "measure:Sales/Rate");
            var mcp = await McpTools.GetFixPrompt(e, "bpa", "INTEGER_FORMATTING", "measure:Sales/Rate");
            var studio = await new EngineRpcTarget(e).bpaGetFixPrompt("INTEGER_FORMATTING", "measure:Sales/Rate");
            Assert.Equal(JsonSerializer.Serialize(mcp), JsonSerializer.Serialize(studio));
            Assert.NotEqual("(advice)", mcp.Tool);
            Assert.DoesNotContain("#,0", mcp.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("if they agree", mcp.Prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("does not raise", mcp.Prompt, StringComparison.Ordinal);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_advice_for_a_blank_date_column_talks_about_dates_and_not_about_rates()
        {
            using var f = NewFixture("pro");
            var e = f.Engine;
            await e.CreateModelAsync("Dates", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Order Date", "DateTime", "OrderDate", "human");
            await e.LoadBpaRulesAsync("[{\"ID\":\"TEST_DATE_FORMAT\",\"Name\":\"Dates read as dates\",\"Category\":\"Formatting\",\"Severity\":1,"
                + "\"Scope\":\"DataColumn\",\"Expression\":\"DataType = \\\"DateTime\\\" and (FormatString = null or FormatString = \\\"\\\")\","
                + "\"FixExpression\":\"FormatString = \\\"yyyy-mm-dd\\\"\"}]", replace: false, "human");
            var mcp = await McpTools.GetFixPrompt(e, "bpa", "TEST_DATE_FORMAT", "column:Sales/Order Date");
            var studio = await new EngineRpcTarget(e).bpaGetFixPrompt("TEST_DATE_FORMAT", "column:Sales/Order Date");
            Assert.Equal(JsonSerializer.Serialize(mcp), JsonSerializer.Serialize(studio));
            Assert.Equal("(advice)", mcp.Tool);
            Assert.DoesNotContain("73%", mcp.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("#,0", mcp.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("cents", mcp.Prompt, StringComparison.Ordinal);
            Assert.Contains("date format", mcp.Prompt, StringComparison.OrdinalIgnoreCase);
        }

        // ---- item 1: Jev's format lines ------------------------------------------------------------------

        private static string MeasuresFixture(params (string digest, string family)[] rows)
        {
            var entries = rows.Select(r =>
                "{\"digest\":" + JsonSerializer.Serialize(r.digest)
                + ",\"point\":\"" + DecisionRegistry.MeasureFolderAndFormat + "\""
                + ",\"usage\":{\"input_tokens\":900,\"output_tokens\":40}"
                + ",\"answers\":{\"display_folder\":{\"type\":\"choice\",\"choice\":\"" + DecisionRegistry.NoFolder + "\",\"confidence\":0.95}"
                + ",\"format_family\":{\"type\":\"choice\",\"choice\":\"" + r.family + "\",\"confidence\":0.97}}}");
            return "{\"servedModelVersion\":\"jev-test-1\",\"entries\":[" + string.Join(",", entries) + "]}";
        }

        private static readonly string[] JevMeasures = { "Orders", "Units", "Revenue", "Share", "Latest" };

        /// <summary>The measures Jev is asked about: a count and a SUM of an Int64 quantity (both provable), a SUM of a
        /// decimal, a ratio, and a MAX of the Int64 column (never provable). A money format is in use, so currency has one.</summary>
        private async Task<DecisionFixture> JevMeasuresModelAsync(string tier, params string[] only)
        {
            var f = NewFixture(tier);
            await _templates.OpenCopyAsync(f, "jev-measures-" + string.Join(",", only), g => BuildJevMeasuresModelAsync(g, only), "Northstar.bim");
            f.UseNull();
            return f;
        }

        private static async Task BuildJevMeasuresModelAsync(DecisionFixture f, string[] only)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateColumnAsync("table:Sales", "Units Sold", "Int64", "UnitsSold", "human");
            var anchor = await e.CreateMeasureAsync("table:Sales", "Money Anchor", "SUM ( Sales[Amount] ) + 1", "human", "Money");
            await e.SetMeasureFormatAsync(anchor, "$#,##0.00", "human");
            var all = new Dictionary<string, string>
            {
                ["Orders"] = "COUNTROWS ( Sales )", ["Units"] = "SUM ( Sales[Units Sold] )", ["Revenue"] = "SUM ( Sales[Amount] )",
                ["Share"] = "DIVIDE ( [Revenue], 3 )", ["Latest"] = "MAX ( Sales[Units Sold] )",
            };
            foreach (var kv in all)
                if (only.Length == 0 || only.Contains(kv.Key))
                    await e.CreateMeasureAsync("table:Sales", kv.Key, kv.Value, "human");
        }

        private static async Task RecordAsync(DecisionFixture f, IReadOnlyDictionary<string, string> familyByMeasure)
        {
            await f.ConsentAsync();
            var rows = new List<(string, string)>();
            foreach (var kv in familyByMeasure)
                rows.Add(("measure:Sales/" + kv.Key, kv.Value));   // a table is one request now (m11/jev-batch): an entry is keyed by the measure's ref
            f.UseRecordedByObject(MeasuresFixture(rows.ToArray()));
        }

        private static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f, bool viaMcp = false)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return viaMcp ? await McpTools.GetModelPlan(f.Engine) : await new EngineRpcTarget(f.Engine).getModelPlan();
        }

        private static Dictionary<string, ModelPlanChangeLine> FormatLines(ModelPlanItem row) =>
            (row?.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>())
                .Where(l => string.Equals(l.Op, "set_measure_format", StringComparison.Ordinal) || string.Equals(l.Op, "set_format_string", StringComparison.Ordinal))
                .ToDictionary(l => l.Target.Substring(l.Target.LastIndexOf('/') + 1), StringComparer.Ordinal);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Jev_answering_whole_number_for_every_measure_is_approved_only_where_the_engine_proves_it(string door)
        {
            using var f = await JevMeasuresModelAsync("pro");
            await RecordAsync(f, JevMeasures.ToDictionary(x => x, _ => "whole_number"));
            var plan = await SettledPlanAsync(f, door == "mcp");
            var row = Assert.Single(plan.Items, i => i.Id == "jev.measures.table:Sales");
            var lines = FormatLines(row);
            Assert.Equal(JevMeasures.OrderBy(x => x), lines.Keys.OrderBy(x => x));
            foreach (var proven in new[] { "Orders", "Units" })
                Assert.True(lines[proven].Kept, $"{proven} is proved a whole number, so its line may arrive approved");
            foreach (var unproven in new[] { "Revenue", "Share", "Latest" })
            {
                Assert.False(lines[unproven].Kept, $"{unproven} cannot be proved a whole number, so its line is only a suggestion");
                Assert.Contains("Jev suggests this format; check it fits before you run it", lines[unproven].Note ?? "", StringComparison.Ordinal);
            }
            Assert.Equal("semanticus", row.Doer);   // it still holds approved lines, so Run the plan may take it

            var run = door == "mcp"
                ? DoorResult.Of(await McpTools.ApplyPlanItems(f.Engine, null, plan.ReadyDigest))
                : DoorResult.Of(await new EngineRpcTarget(f.Engine).applyPlanItems(null, "human", plan.ReadyDigest));
            Assert.True(run.Performed, run.Sentence);
            var formats = JevMeasures.ToDictionary(x => x, x => MeasureFormatAsync(f, "Sales", x).Result);
            Assert.All(new[] { "Orders", "Units" }, x => Assert.True(FormatFill.IsWholeNumberFormat(formats[x]), $"{x} reads \"{formats[x]}\""));
            Assert.All(new[] { "Revenue", "Share", "Latest" }, x => Assert.Equal("", formats[x]));
        }

        // Default tier: the witness row of the sweep form, through the same body.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public Task Jev_answering_whole_number_for_every_measure_is_approved_only_where_the_engine_proves_it_sample() =>
            Jev_answering_whole_number_for_every_measure_is_approved_only_where_the_engine_proves_it("mcp");

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Jev_answering_the_right_families_still_only_suggests_every_line_the_engine_cannot_prove()
        {
            using var f = await JevMeasuresModelAsync("pro");
            await RecordAsync(f, new Dictionary<string, string>
            {
                ["Orders"] = "whole_number", ["Units"] = "whole_number", ["Revenue"] = "currency", ["Share"] = "percent", ["Latest"] = "whole_number",
            });
            var plan = await SettledPlanAsync(f);
            var lines = FormatLines(Assert.Single(plan.Items, i => i.Id == "jev.measures.table:Sales"));
            Assert.True(lines["Orders"].Kept);
            Assert.True(lines["Units"].Kept);
            foreach (var suggested in new[] { "Revenue", "Share", "Latest" })
                Assert.False(lines[suggested].Kept, $"{suggested}: {lines[suggested].After} is a suggestion, never an approved fill");
            Assert.Equal("$#,##0.00", lines["Revenue"].After);   // the suggestion is still worked out: the person sees it
            await new EngineRpcTarget(f.Engine).applyPlanItems(null, "human", plan.ReadyDigest);
            Assert.All(new[] { "Revenue", "Share", "Latest" }, x => Assert.Equal("", MeasureFormatAsync(f, "Sales", x).Result));
        }

        [Fact]
        public async Task A_row_of_only_suggestions_waits_on_the_person_and_run_the_plan_cannot_take_it()
        {
            using var f = await JevMeasuresModelAsync("pro", "Revenue", "Share");
            await RecordAsync(f, new Dictionary<string, string> { ["Revenue"] = "whole_number", ["Share"] = "percent" });
            var plan = await SettledPlanAsync(f);
            var row = Assert.Single(plan.Items, i => i.Id == "jev.measures.table:Sales");
            // The assistant's since 13 Oct (Kane: the person is the last resort): it checks the formats fit and asks. Run the plan still never
            // takes it, because Run the plan takes only Semanticus's rows.
            Assert.Equal("assistant", row.Doer);
            Assert.DoesNotContain(PlanReadySet.Rows(plan.Items), r => r.Id == row.Id);
            Assert.All(FormatLines(row).Values, l => Assert.False(l.Kept));
            Assert.Contains(row.Why, w => w.Contains("only suggestions", StringComparison.Ordinal));
            await McpTools.ApplyPlanItems(f.Engine, null, plan.ReadyDigest);
            Assert.All(new[] { "Revenue", "Share" }, x => Assert.Equal("", MeasureFormatAsync(f, "Sales", x).Result));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_person_who_keeps_a_suggestion_gets_it_applied_and_only_that_one()
        {
            using var f = await JevMeasuresModelAsync("pro", "Revenue", "Share");
            await RecordAsync(f, new Dictionary<string, string> { ["Revenue"] = "currency", ["Share"] = "percent" });
            var plan = await SettledPlanAsync(f);
            var lines = FormatLines(Assert.Single(plan.Items, i => i.Id == "jev.measures.table:Sales"));
            await f.Engine.SetPlanItemAsync(lines["Share"].PlanItemId, null, approved: true, "human");
            plan = await new EngineRpcTarget(f.Engine).getModelPlan();
            var row = Assert.Single(plan.Items, i => i.Id == "jev.measures.table:Sales");
            Assert.True(FormatLines(row)["Share"].Kept);
            Assert.False(FormatLines(row)["Revenue"].Kept);
            var done = await new EngineRpcTarget(f.Engine).applyPlanItems(new[] { new PlanItemDigest { Id = row.Id, Digest = row.ChangeSet.Digest } }, "human");
            Assert.True(done.Performed, done.Sentence);
            Assert.NotEqual("", await MeasureFormatAsync(f, "Sales", "Share"));
            Assert.Equal("", await MeasureFormatAsync(f, "Sales", "Revenue"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Free_has_no_jev_lines_and_no_automatic_path_fills_what_the_rule_refuses()
        {
            using var f = await JevMeasuresModelAsync("free");
            var plan = await SettledPlanAsync(f);
            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith("jev.", StringComparison.Ordinal));
            await PressEverythingAsync(f, "INTEGER_FORMATTING", JevMeasures.Select(x => "measure:Sales/" + x).ToArray());
            var formats = JevMeasures.ToDictionary(x => x, x => MeasureFormatAsync(f, "Sales", x).Result);
            Assert.All(new[] { "Orders", "Units" }, x => Assert.Equal("#,0", formats[x]));
            Assert.All(new[] { "Revenue", "Share", "Latest" }, x => Assert.Equal("", formats[x]));
        }

        /// <summary>Answers the column format point per column NAME, read off the snapshot the request carries.</summary>
        private sealed class ColumnAnswers : IDecisionOracle
        {
            private readonly Func<string, string> _familyFor;
            public ColumnAnswers(Func<string, string> familyFor) { _familyFor = familyFor; }
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.PointName != DecisionRegistry.ColumnFormat)
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "not asked in this case"));
                // m11/jev-batch: a table's columns arrive in one request, one question each, named "format_family:<column ref>".
                if (request.PerQuestion)
                    return Task.FromResult(BatchedByName.Reply(request, (plain, objectRef) =>
                    {
                        var wanted = _familyFor(BatchedByName.NameOf(objectRef));
                        return wanted == null || plain != "format_family" ? null
                            : "{\"type\":\"choice\",\"choice\":\"" + wanted + "\",\"confidence\":0.97}";
                    }));
                var family = _familyFor(request.Snapshot["column_name"] as string);
                if (family == null) return Task.FromResult(DecisionAnswers.NotDecided(request, "no answer for this column"));
                var fixture = "{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":" + JsonSerializer.Serialize(request.Digest)
                    + ",\"point\":\"" + request.PointName + "\",\"usage\":{\"input_tokens\":900,\"output_tokens\":40}"
                    + ",\"answers\":{\"format_family\":{\"type\":\"choice\",\"choice\":\"" + family + "\",\"confidence\":0.97}}}]}";
                return new RecordedDecisionOracle(fixture, "jev-test-1").AskAsync(request, ct);
            }
        }

        private static readonly string[] JevColumns = { "Quantity", "Rate", "FY", "FISCALYEAR", "Order Date" };

        private async Task<DecisionFixture> JevColumnsModelAsync(string tier, Func<string, string> familyFor)
        {
            var f = NewFixture(tier);
            await _templates.OpenCopyAsync(f, "jev-columns", BuildJevColumnsModelAsync, "Northstar.bim");
            await f.ConsentAsync();
            f.Engine.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(new ColumnAnswers(familyFor), new DecisionBudget()));
            return f;
        }

        private static async Task BuildJevColumnsModelAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", "Quantity", "human");
            await e.SetObjectPropertyAsync("column:Sales/Quantity", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateColumnAsync("table:Sales", "Rate", "Double", "Rate", "human");
            await e.CreateColumnAsync("table:Sales", "FY", "Int64", "FY", "human");
            await e.SetObjectPropertyAsync("column:Sales/FY", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateColumnAsync("table:Sales", "FISCALYEAR", "Int64", "FISCALYEAR", "human");
            await e.SetObjectPropertyAsync("column:Sales/FISCALYEAR", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateColumnAsync("table:Sales", "Order Date", "DateTime", "OrderDate", "human");
        }

        private static async Task<string> ColumnFormatAsync(DecisionFixture f, string column) =>
            await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Columns[column].FormatString ?? "");

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Jev_column_formats_are_approved_only_for_a_proven_quantity_and_the_rest_are_suggestions(string door)
        {
            using var f = await JevColumnsModelAsync("pro", c => c == "Order Date" ? "date_or_time" : "whole_number");
            var plan = await SettledPlanAsync(f, door == "mcp");
            var row = Assert.Single(plan.Items, i => i.Id.StartsWith("jev.column-format.", StringComparison.Ordinal));
            var lines = FormatLines(row);
            Assert.True(lines["Quantity"].Kept, "an Int64 quantity is proved, so its line may arrive approved");
            foreach (var suggested in new[] { "Rate", "FY", "FISCALYEAR", "Order Date" })
            {
                Assert.False(lines[suggested].Kept, $"{suggested} is not proved a whole-number quantity, so its line is a suggestion");
                Assert.Contains("Jev suggests this format; check it fits before you run it", lines[suggested].Note ?? "", StringComparison.Ordinal);
            }
            var run = door == "mcp"
                ? DoorResult.Of(await McpTools.ApplyPlanItems(f.Engine, null, plan.ReadyDigest))
                : DoorResult.Of(await new EngineRpcTarget(f.Engine).applyPlanItems(null, "human", plan.ReadyDigest));
            Assert.True(run.Performed, run.Sentence);
            Assert.True(FormatFill.IsWholeNumberFormat(await ColumnFormatAsync(f, "Quantity")));
            foreach (var suggested in new[] { "Rate", "FY", "FISCALYEAR", "Order Date" })
                Assert.Equal("", await ColumnFormatAsync(f, suggested));
        }

        // Default tier: the witness row of the sweep form, through the same body.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public Task Jev_column_formats_are_approved_only_for_a_proven_quantity_and_the_rest_are_suggestions_sample() =>
            Jev_column_formats_are_approved_only_for_a_proven_quantity_and_the_rest_are_suggestions("mcp");

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Jev_column_formats_for_the_right_families_are_still_only_suggestions_for_rates_and_dates()
        {
            using var f = await JevColumnsModelAsync("pro", c => c switch { "Rate" => "percent", "Order Date" => "date_or_time", _ => "whole_number" });
            var plan = await SettledPlanAsync(f);
            var lines = FormatLines(Assert.Single(plan.Items, i => i.Id.StartsWith("jev.column-format.", StringComparison.Ordinal)));
            Assert.False(lines["Rate"].Kept);
            Assert.False(lines["Order Date"].Kept);
            await McpTools.ApplyPlanItems(f.Engine, null, plan.ReadyDigest);
            Assert.Equal("", await ColumnFormatAsync(f, "Rate"));
            Assert.Equal("", await ColumnFormatAsync(f, "Order Date"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Free_asks_no_column_format_question_and_writes_no_column_format()
        {
            using var f = await JevColumnsModelAsync("free", _ => "whole_number");
            var plan = await SettledPlanAsync(f);
            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith("jev.", StringComparison.Ordinal));
        }
    }
}

namespace Semanticus.Tests
{
    /// <summary>The two doors answer a plan action in different shapes (the MCP door's is the short one, the Studio's is the full one),
    /// and these tests only ask both the same two questions: did it happen, and what does it say.</summary>
    internal static class DoorResult
    {
        public static (bool Performed, string Sentence) Of(Semanticus.Engine.PlanActionShort r) => (r.Performed, r.Sentence);
        public static (bool Performed, string Sentence) Of(Semanticus.Engine.ApplyPlanItemsResult r) => (r.Performed, r.Sentence);
        public static (bool Performed, string Sentence) Of(Semanticus.Engine.ModelPlanActionResult r) => (r.Performed, r.Sentence);
        /// <summary>The assistant's short answer to apply_plan_items, read as the Studio door's full one (join, part 2: both doors
        /// answer Run the plan in different shapes now).</summary>
        public static Semanticus.Engine.ApplyPlanItemsResult AsItems(Semanticus.Engine.PlanActionShort r) => new Semanticus.Engine.ApplyPlanItemsResult
        {
            Performed = r.Performed, Applied = r.Applied ?? 0, ItemIds = r.ItemIds, Skipped = r.Skipped ?? System.Array.Empty<Semanticus.Engine.ApplyPlanSkip>(),
            Revision = r.Revision, Sentence = r.Sentence,
        };
    }
}
