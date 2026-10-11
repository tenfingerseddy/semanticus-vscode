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
    }
}
