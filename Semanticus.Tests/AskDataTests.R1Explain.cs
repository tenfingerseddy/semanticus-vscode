// Round 1, hero journey 6 (Kane, 8 Oct): "why is Margin % 35.8% in 2025?" must be explained, not answered again; the 8 of
// 35.8 is not a filter; the explanation speaks plainly in the measure's format; months come in calendar order.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    public sealed partial class AskDataTests
    {
        [Fact]
        public void A_number_with_a_decimal_point_in_the_question_is_one_word_and_never_a_filter()
        {
            var cat = new QueryCatalog();
            cat.Columns.Add(new QueryColumn { Table = "Date", Name = "MonthNumber", Kind = "whole" });
            cat.Columns.Add(new QueryColumn { Table = "Sales", Name = "OrderLine", Kind = "whole" });
            var values = new Dictionary<string, object[]>
            {
                ["Date[MonthNumber]"] = Enumerable.Range(1, 12).Select(i => (object)(long)i).ToArray(),
                ["Sales[OrderLine]"] = Enumerable.Range(1, 12).Select(i => (object)(long)i).ToArray(),
            };
            Assert.Empty(ValueMatcher.Match("why is Margin % 35.8% in 2025?", cat, values));
            Assert.Empty(ValueMatcher.Match("why is net sales 1,234.56 high", cat, values));
            // A plain 8 still names the value 8.
            Assert.NotEmpty(ValueMatcher.Match("sales for month 8", cat, values));
        }

        [Fact]
        public async Task Months_are_ordered_by_the_column_the_model_sorts_them_by()
        {
            using var rig = await BuildAsync();
            await rig.Engine.CreateColumnAsync("table:Date", "MonthNumber", "Int64", "MonthNumber", "human");
            await rig.Engine.SetColumnMetadataAsync("column:Date/MonthName", null, null, null, "MonthNumber", "human");
            var cat = await CatalogAsync(rig);
            var s = Spec(cat, new[] { "Date[MonthName]" }, new[] { "Net Sales" });
            var dax = QueryTemplate.Build(s);
            Assert.Contains("'Date'[MonthNumber]", dax);
            Assert.Contains("ORDER BY\n    'Date'[MonthNumber] ASC", dax);
            // A sort the person asked for by the month says what order it runs in, not "A to Z".
            s.SortColumn = s.GroupBy[0];
            Assert.Contains("MonthName in the model's own order", QueryTemplate.Reading(s, cat));
            Assert.DoesNotContain("A to Z", QueryTemplate.Reading(s, cat));
            s.SortColumn = null;

            // The helper column is only for the order: the answer shows the month and the number.
            rig.Data.RowsToReturn = 3;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, GroupBy = new[] { "Date[MonthName]" } });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal(2, a.Columns.Length);
            Assert.All(a.Rows, r => Assert.Equal(2, r.Length));
            Assert.DoesNotContain(a.Columns, c => c.Name.Contains("MonthNumber"));
        }

        [Fact]
        public void The_plain_note_for_a_number_whose_parts_do_not_add_up_has_no_numbers_and_no_engine_words()
        {
            var c = ExplainLogic.BuildContributors("'Product'[Brand]", 0.3577527,
                new[] { new KeyValuePair<string, double>("A", 3.5), new KeyValuePair<string, double>("B", 0.07) }, false, 5);
            Assert.False(c.Additive);
            Assert.Empty(c.Rows);
            Assert.Contains("do not add up", c.Note);
            Assert.DoesNotContain("0.35", c.Note);
            Assert.DoesNotContain("decomposeBy", c.Note);
            Assert.DoesNotContain(" vs ", c.Note);
            Assert.Empty(c.Spread);   // BuildContributors alone says nothing of the spread; the engine adds it
            var spread = ExplainLogic.Spread(Enumerable.Range(1, 8).Select(i => new KeyValuePair<string, double>("M" + i, i)).ToList());
            Assert.Equal(new[] { "M8", "M7", "M6", "M2", "M1" }, spread.Select(r => r.Member).ToArray());
        }

        private static ResultSet WhyFake(string q)
        {
            ResultSet Of(string[] names, object[][] rows) => new ResultSet { Columns = names.Select(n => new ColumnDef { Name = n }).ToArray(), Rows = rows, RowCount = rows.Length };
            var axis = Regex.Matches(q, @"^\s+'[^']+'\[[^\]]+\],?$", RegexOptions.Multiline).Count;
            double v = q.Contains("[Net Sales]") ? 1200.0 : q.Contains("[Units]") ? 34.0 : 0.3578;
            if (axis == 0) return Of(new[] { "[v]", "[__present]" }, new[] { new object[] { v, 1L } });
            return Of(new[] { "g", "[v]", "[__present]" }, new[] { new object[] { "A", v * 2, 1L }, new object[] { "B", v * 5, 1L } });
        }

        [Fact]
        public async Task A_why_question_about_one_number_is_explained_in_the_measures_format_with_what_drives_it()
        {
            using var rig = await BuildAsync(ratio: true);
            await rig.Engine.SetMeasureFormatAsync("measure:Sales/Average Price", "0.0%", "human");
            rig.Data.Connected = (await CatalogAsync(rig)).Measures.ToDictionary(m => m.Name, m => (m.Expression, m.FormatString));
            rig.Data.RowsToReturn = 1;
            rig.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Retail", "Contoso Retail",
                q => q.Contains("\"__present\"") ? WhyFake(q) : rig.Data.Execute(q)));

            var ask = new DataAsk
            {
                Question = "why is Average Price 35.8% in 2025?",
                Measures = new[] { "Average Price" },
                Filters = new[] { new DataAskFilter { Column = "Date[Year]", Op = "is", Values = new[] { "2025" } } },
            };
            var a = await rig.Engine.AskDataAsync(ask);
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.NotNull(a.Why);
            Assert.Equal("ok", a.Why.Status);
            Assert.Equal("0.3578", a.Why.Value);
            Assert.Equal("0.0%", a.Why.Format);
            Assert.Equal(new[] { "Net Sales", "Units" }, a.Why.Drivers.Select(d => d.Name).OrderBy(n => n).ToArray());
            Assert.Equal("1200", a.Why.Drivers.Single(d => d.Name == "Net Sales").Value);
            Assert.False(a.Why.Contributors.Additive);
            Assert.DoesNotContain("decomposeBy", a.Why.Contributors.Note);
            Assert.Equal(new[] { "B", "A" }, a.Why.Contributors.Spread.Select(r => r.Member).ToArray());

            // The same question without the word why is just the number.
            ask.Question = "Average Price in 2025";
            Assert.Null((await rig.Engine.AskDataAsync(ask)).Why);
        }

        // ---- Kane, 17 Oct: Explain split Sales Amount by City, five cities at 2 to 5% and "everything else, 220 more" at 85% ----

        // A live model for the split: how many values each column holds, and the cell's number (1000) cut up by each column.
        private static Func<string, ResultSet> SplitModel(Dictionary<string, double[]> parts, Dictionary<string, long> counts, Func<string, ResultSet> rest)
        {
            ResultSet Of(string[] names, object[][] rows) => new ResultSet { Columns = names.Select(n => new ColumnDef { Name = n }).ToArray(), Rows = rows, RowCount = rows.Length };
            return q =>
            {
                if (q.Contains("DISTINCTCOUNT"))
                {
                    var refs = Regex.Matches(q, @"DISTINCTCOUNT\(('[^']+'\[[^\]]+\])\)").Select(m => m.Groups[1].Value).ToArray();
                    return Of(refs.Select((r, i) => "[c" + i + "]").ToArray(), new[] { refs.Select(r => (object)(counts.TryGetValue(r, out var n) ? n : 1L)).ToArray() });
                }
                if (!q.Contains("\"__present\"")) return rest(q);
                var axis = Regex.Matches(q, @"^\s+('[^']+'\[[^\]]+\]),?$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray();
                if (axis.Length == 0) return Of(new[] { "[v]", "[__present]" }, new[] { new object[] { 1000.0, 1L } });
                var cut = parts[axis[axis.Length - 1]];
                return Of(new[] { "g", "[v]", "[__present]" }, cut.Select((v, i) => new object[] { "m" + i, v, 1L }).ToArray());
            };
        }

        // ---- Threat check 16b, area 7: explain_value's contributors are member names with values, so the person's QueryData Deny holds for it ----

        private static async Task<(ExplainDossier d, List<string> queries)> ExplainAsAsync(Rig rig, string origin)
        {
            var queries = new List<string>();
            var model = SplitModel(SplitParts(), SplitCounts(), rig.Data.Execute);
            rig.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Retail", "Contoso Retail", q => { lock (queries) queries.Add(q); return model(q); }));
            return (await rig.Engine.ExplainValueAsync("Net Sales", Year2025(), true, null, 5, origin), queries);
        }

        [Fact]
        public async Task An_assistant_denied_data_reads_gets_the_structure_of_a_number_and_no_values()
        {
            using var rig = await BuildAsync();                                  // the target is labelled prod
            Semanticus.Engine.AgentPolicyStore.SetPreset("client", "human");     // QueryData is Deny on prod
            var (d, queries) = await ExplainAsAsync(rig, "agent");
            Assert.Equal("ok", d.Status);
            Assert.False(d.ValueEvaluated);
            Assert.False(d.Evidence.Available);
            Assert.Equal(LocalEngine.ExplainDeniedNote, d.Evidence.Note);
            Assert.Null(d.Value);
            Assert.True(d.Contributors == null || d.Contributors.Rows.Length == 0);
            Assert.NotEmpty(d.Chain);                                            // what feeds the number is still explained
            Assert.Empty(queries);                                               // and nothing was read from the data
            Assert.DoesNotContain("—", d.Summary);

            // The person on the same target is never gated.
            var (mine, _) = await ExplainAsAsync(rig, "human");
            Assert.True(mine.ValueEvaluated);
            Assert.NotEmpty(mine.Contributors.Rows);
        }

        [Fact]
        public async Task A_default_preset_answers_the_assistants_explain_with_no_extra_question()
        {
            using var rig = await BuildAsync();
            foreach (var preset in new[] { "standard", "open" })
            {
                Semanticus.Engine.AgentPolicyStore.SetPreset(preset, "human");   // standard asks for rows on prod, but an Ask is not a denial: explain never queues one
                var (d, queries) = await ExplainAsAsync(rig, "agent");
                Assert.True(d.ValueEvaluated, preset);
                Assert.True(d.Evidence.Available, preset);
                Assert.NotEmpty(d.Contributors.Rows);
                Assert.NotEmpty(queries);
                Assert.Empty((await rig.Engine.ListPendingApprovalsAsync()) ?? Array.Empty<ApprovalRecord>());
            }
        }

        [Fact]
        public void Explain_value_is_a_data_read_in_the_capability_map_and_probe_measure_stays_a_calculation()
        {
            Assert.Equal(Semanticus.Engine.AgentCapability.QueryData, Semanticus.Engine.CapabilityMap.For("explain_value"));
            Assert.Equal(Semanticus.Engine.AgentCapability.QueryCalc, Semanticus.Engine.CapabilityMap.For("probe_measure"));
        }

        private static ExplainFilterContext Year2025() => new ExplainFilterContext { Filters = new[] { new ExplainFilter { Column = "'Date'[Year]", Members = new[] { "2025" } } } };

        private static Dictionary<string, double[]> SplitParts() => new Dictionary<string, double[]>
        {
            ["'Customer'[Segment]"] = new[] { 600.0, 400 },
            ["'Date'[MonthName]"] = new[] { 150.0, 140, 130, 120, 110, 100, 90, 80, 40, 20, 10, 10 },
            ["'Product'[Category]"] = new[] { 500.0, 300, 200 },
            ["'Product'[ProductName]"] = Enumerable.Repeat(1000.0 / 60, 60).ToArray(),
            ["'Store'[City]"] = Enumerable.Range(0, 225).Select(i => i < 5 ? 40.0 : 800.0 / 220).ToArray(),
            ["'Store'[Region]"] = new[] { 400.0, 300, 200, 100 },
        };

        private static Dictionary<string, long> SplitCounts() => new Dictionary<string, long>
        {
            ["'Customer'[Segment]"] = 2, ["'Date'[MonthName]"] = 12, ["'Product'[Category]"] = 3, ["'Product'[ProductName]"] = 60, ["'Store'[City]"] = 225, ["'Store'[Region]"] = 4,
        };

        private static double Sum(ExplainContributors c) => c.Rows.Sum(r => double.Parse(r.Value, System.Globalization.CultureInfo.InvariantCulture));

        [Fact]
        public async Task The_split_the_engine_picks_explains_most_of_the_number_and_the_others_are_offered()
        {
            using var rig = await BuildAsync();
            var queries = new List<string>();
            var model = SplitModel(SplitParts(), SplitCounts(), rig.Data.Execute);
            rig.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Retail", "Contoso Retail", q => { lock (queries) queries.Add(q); return model(q); }));

            var d = await rig.Engine.ExplainValueAsync("Net Sales", Year2025(), true, null, 5, "human");
            Assert.Equal("ok", d.Status);
            var c = d.Contributors;
            Assert.True(c.Additive);
            Assert.NotEqual("'Store'[City]", c.Dimension);                                   // 225 values, the top five hold 15%: never the pick
            Assert.DoesNotContain(c.Rows, r => r.Member.Contains("everything else"));        // the parts are the whole number
            Assert.Equal(1000.0, Sum(c), 6);
            Assert.Equal("'Store'[Region]", c.Dimension);                                    // few values, all of it, first in the model's order (Customer is not reached: its relationship is inactive)
            Assert.Equal(new[] { "'Product'[Category]", "'Date'[MonthName]" }, c.Alternatives);   // the others that also explain most of it, best first
            Assert.DoesNotContain(queries, q => q.Contains("'Store'[City]") && q.Contains("SUMMARIZECOLUMNS"));    // a column of 225 values is not tried
            Assert.DoesNotContain("'Date'[Year]", string.Join("|", c.Alternatives));         // the year is pinned: nothing to split
        }

        [Fact]
        public async Task The_next_level_of_a_pinned_level_goes_first_in_the_model_order()
        {
            using var rig = await BuildAsync();
            await rig.Engine.CreateHierarchyAsync("table:Date", "Calendar", new[] { "Year", "MonthName" }, "human");
            var parts = SplitParts();
            parts["'Date'[MonthName]"] = new[] { 250.0, 250, 250, 250 };     // all of it, like the segments: a tie, so the model's order decides
            var model = SplitModel(parts, SplitCounts(), rig.Data.Execute);
            rig.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Retail", "Contoso Retail", model));

            var d = await rig.Engine.ExplainValueAsync("Net Sales", Year2025(), true, null, 5, "human");
            Assert.Equal("'Date'[MonthName]", d.Contributors.Dimension);      // the date's next level inside the pinned year
        }

        [Fact]
        public async Task In_a_date_table_without_a_hierarchy_the_column_after_the_pinned_one_goes_first()
        {
            using var rig = await BuildAsync(calendar: true);
            await rig.F.Sessions.Require().MutateAsync("human", "mark the date table", m => m.Tables["Date"].DataCategory = "Time");
            var parts = SplitParts();
            parts["'Date'[MonthName]"] = new[] { 250.0, 250, 250, 250 };     // a tie with the others at all of it: the model's order decides
            var model = SplitModel(parts, SplitCounts(), rig.Data.Execute);
            rig.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Retail", "Contoso Retail", model));

            var d = await rig.Engine.ExplainValueAsync("Net Sales", Year2025(), true, null, 5, "human");
            Assert.Equal("'Date'[MonthName]", d.Contributors.Dimension);      // after Year in the Date table, ahead of Store and Product
        }

        [Fact]
        public async Task When_no_column_explains_most_of_the_number_the_answer_says_so_in_one_line()
        {
            using var rig = await BuildAsync();
            var flat = SplitParts().Keys.ToDictionary(k => k, k => Enumerable.Repeat(50.0, 20).ToArray());   // twenty equal parts: the top five hold 25%
            var counts = SplitParts().Keys.ToDictionary(k => k, k => 20L);
            rig.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Retail", "Contoso Retail", SplitModel(flat, counts, rig.Data.Execute)));

            var d = await rig.Engine.ExplainValueAsync("Net Sales", Year2025(), true, null, 5, "human");
            Assert.Equal("ok", d.Status);
            Assert.Empty(d.Contributors.Rows);
            Assert.Equal("No single column explains most of this number.", d.Contributors.Note);
        }

        [Fact]
        public async Task A_column_the_person_names_is_split_as_asked_and_the_same_choice_works_for_the_assistant()
        {
            using var rig = await BuildAsync();
            rig.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Retail", "Contoso Retail", SplitModel(SplitParts(), SplitCounts(), rig.Data.Execute)));

            var d = await rig.Engine.ExplainValueAsync("Net Sales", Year2025(), true, "'Store'[City]", 5, "agent");
            Assert.Equal("'Store'[City]", d.Contributors.Dimension);          // asked for, so shown, with its everything else
            Assert.Contains(d.Contributors.Rows, r => r.Member.Contains("everything else"));
            Assert.Equal(1000.0, Sum(d.Contributors), 6);
            var region = await rig.Engine.ExplainValueAsync("Net Sales", Year2025(), true, "'Store'[Region]", 5, "agent");
            Assert.Equal(new[] { "m0", "m1", "m2", "m3" }, region.Contributors.Rows.Select(r => r.Member).ToArray());
            Assert.Equal(1000.0, Sum(region.Contributors), 6);
        }

        // Verifier, m16/v-fix-explain-split (17 Oct, live on the Demo model): a date table holds many columns of few values (quarter, quarter name,
        // fiscal quarter, weekday, is weekend, month, fiscal year: 15 on Contoso Sales). With a year pinned they come first in the model's order and
        // fill all twelve probes, so a product, store or customer split is never tried: Contoso 2024 was offered only quarter, is weekend, fiscal
        // year and weekday, while customer segment (3 parts, all of it) was never worked out. The row must be able to offer another table's split.
        [Fact]
        public async Task V_a_date_table_of_many_small_columns_does_not_use_up_every_probe()
        {
            using var rig = await BuildAsync(calendar: true);
            await rig.F.Sessions.Require().MutateAsync("human", "mark the date table", m => m.Tables["Date"].DataCategory = "Time");
            var parts = SplitParts();
            var counts = SplitCounts();
            for (var i = 1; i <= 12; i++)
            {
                await rig.Engine.CreateColumnAsync("table:Date", "Part" + i, "String", "Part" + i, "human");
                parts["'Date'[Part" + i + "]"] = new[] { 250.0 + i, 250.0 - i, 250, 250 };   // four values, all of the number, each cut its own way
                counts["'Date'[Part" + i + "]"] = 4;
            }
            rig.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Retail", "Contoso Retail", SplitModel(parts, counts, rig.Data.Execute)));

            var d = await rig.Engine.ExplainValueAsync("Net Sales", Year2025(), true, null, 5, "human");
            var offered = new[] { d.Contributors.Dimension }.Concat(d.Contributors.Alternatives).ToArray();
            Assert.True(offered.Any(o => !o.StartsWith("'Date'")), "offered: " + string.Join(", ", offered));   // Product Category (3 parts) and Store Region (4 parts) each explain all of it
        }
    }
}
