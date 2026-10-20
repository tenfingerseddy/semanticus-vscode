// Verifier, round 2 of the Query UAT (18 Oct, Contoso Sales live, 4 of 4 asks): "net amount last year" showed 25,503,291.69 under
// "where year is 2025 (1 January to 31 December 2025)". Jev read the measure as "Total net amount PY" and the engine read "last year"
// as Year = 2025, so the year moved back twice: the figure is 2024's, said as 2025's (direct DAX: 2025 is 29,914,507.85). A measure that
// already looks one year back, filtered to the year the words name, must not be shown as that year's answer.

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    public sealed partial class AskDataTests
    {
        [Fact]
        public async Task V2UatQuery_last_year_read_with_a_previous_year_measure_shows_the_measure_it_wraps()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, measure: "Net Sales PY"), calendar: true,
                shape: (e, data) => e.CreateMeasureAsync("table:Sales", "Net Sales PY", "CALCULATE ( [Net Sales], DATEADD ( 'Date'[Date], -1, YEAR ) )", "human"));
            rig.Data.Latest = new DateTime(2025, 10, 11);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales last year" });
            Assert.Contains("'last year' is read as", a.Note ?? "", StringComparison.Ordinal);
            // The year moves once, by the words: the measure that also looks back a year gives way to the one it wraps.
            Assert.DoesNotContain("[Net Sales PY]", a.Dax ?? "", StringComparison.Ordinal);
            Assert.Contains("[Net Sales]", a.Dax ?? "", StringComparison.Ordinal);
            Assert.Contains("Net Sales PY already moves the date", a.Note ?? "", StringComparison.Ordinal);
        }

        // Verifier v3 (live, Contoso Sales, "returns last year"): the shown measure was swapped to its base, but the offer built
        // from Jev's second, unsure number still carried a PY measure. One press ran [Total return quantity PY] under year 2025
        // in the "last year" row: 2024's figure (2,815; 2025 direct is 3,425). An offer may not move the year a second time either.
        [Fact]
        public async Task V3UatQuery_an_offer_under_a_period_the_words_set_never_brings_back_a_measure_that_moves_the_date()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.MeasureQ(2) ? ("m:Units PY", 0.63) : Gate("nothing", 0.97, measure: "Net Sales PY")(r, q);
            using var rig = await BuildAsync(script: Script, calendar: true, shape: async (e, data) =>
            {
                await e.CreateMeasureAsync("table:Sales", "Net Sales PY", "CALCULATE ( [Net Sales], DATEADD ( 'Date'[Date], -1, YEAR ) )", "human");
                await e.CreateMeasureAsync("table:Sales", "Units PY", "CALCULATE ( [Units], DATEADD ( 'Date'[Date], -1, YEAR ) )", "human");
            });
            rig.Data.Latest = new DateTime(2025, 10, 11);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales last year" });
            Assert.Contains("'last year' is read as", a.Note ?? "", StringComparison.Ordinal);
            // Precondition: the shown number is already swapped (the round-3 fix), and an offer of a second number was made.
            Assert.DoesNotContain("[Net Sales PY]", a.Dax ?? "", StringComparison.Ordinal);
            Assert.NotEmpty(a.Others ?? Array.Empty<DataOther>());
            var shifted = (a.Others ?? Array.Empty<DataOther>())
                .Where(o => (o.Parts?.Measures ?? Array.Empty<string>()).Any(m => m.Contains(" PY", StringComparison.Ordinal))).ToList();
            Assert.True(shifted.Count == 0, "an offer runs a PY measure under the year the words already set: "
                + string.Join(" | ", shifted.Select(o => o.Instead + " => " + string.Join(", ", o.Parts.Measures))) + " / " + Seen(a));
        }

        [Fact]
        public async Task V2UatQuery_last_year_read_with_a_previous_year_measure_that_wraps_nothing_is_flagged_with_no_figure()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, measure: "Net Sales PY"), calendar: true,
                shape: (e, data) => e.CreateMeasureAsync("table:Sales", "Net Sales PY", "CALCULATE ( SUM ( Sales[Amount] ), DATEADD ( 'Date'[Date], -1, YEAR ), Sales[StoreKey] = 1 )", "human"));
            rig.Data.Latest = new DateTime(2025, 10, 11);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales last year" });
            Assert.False(Answered(a) && a.Flagged == null, Seen(a));
        }

        // Round 3, item 2: a plain count of a thing is counted, not flagged. The engine reads it itself (CountReading), as it reads
        // "bought twice", and says the reading in words.
        [Theory]
        [InlineData("how many customers do we have", "Customers", "DISTINCTCOUNT('Customer'[CustomerKey])", false)]
        [InlineData("how many custmers do we have", "Customers", "DISTINCTCOUNT('Customer'[CustomerKey])", false)]
        [InlineData("how many different customers", "Customers", "DISTINCTCOUNT('Customer'[CustomerKey])", false)]
        [InlineData("number of customers", "Customers", "DISTINCTCOUNT('Customer'[CustomerKey])", false)]
        [InlineData("how many customers in 2023", "Customers", "CALCULATE([Order Count], 'Date'[Year] = 2023)", true)]
        [InlineData("how many different customers did we sell to in 2023", "Customers", "CALCULATE([Order Count], 'Date'[Year] = 2023)", true)]
        [InlineData("how many stores sold in 2025", "Stores", "CALCULATE([Order Count], 'Date'[Year] = 2025)", true)]
        [InlineData("how many prodcuts did we sell in 2023", "Products", "CALCULATE([Order Count], 'Date'[Year] = 2023)", true)]
        [InlineData("how many orders in 2024", "Orders", "CALCULATE([Order Count], 'Date'[Year] = 2024)", true)]
        public async Task V3UatQuery_a_plain_count_of_a_thing_is_counted_the_way_the_order_reader_counts(string words, string plural, string dax, bool active)
        {
            using var rig = await OrdersRigAsync();
            var read = CountReading.TryRead(words, await CatalogAsync(rig));
            Assert.NotNull(read);
            Assert.Equal(plural, read.Plural);
            Assert.Equal(active, read.Active);
            Assert.Contains(dax.Replace(" ", ""), CountReading.Dax(read).Replace(" ", ""));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = words });
            Assert.True(Answered(a), Seen(a));
            Assert.Null(a.Flagged);
            Assert.Empty(rig.Jev.Seen);
            Assert.False(string.IsNullOrWhiteSpace(a.Statement));
        }

        // Verifier v3, gap: a model with no order-count measure (Contoso: fact_sales[order_number]) still counts orders, by the order key.
        [Theory]
        [InlineData("how many orders", "")]
        [InlineData("how many orders in 2024", ", 'Date'[Year] = 2024")]
        public async Task V4UatQuery_orders_on_a_model_with_no_order_measure_are_counted_by_the_order_key(string words, string year)
        {
            using var rig = await BuildAsync(twins: true, shape: async (e, data) =>
                await e.CreateColumnAsync("table:Sales", "OrderNumber", "String", "OrderNumber", "human"));
            var read = CountReading.TryRead(words, await CatalogAsync(rig));
            Assert.NotNull(read);
            Assert.Null(read.Counter);
            Assert.Equal("OrderNumber", read.CounterColumn.Name);
            Assert.Contains(("CALCULATE(DISTINCTCOUNT('Sales'[OrderNumber])" + year + ")").Replace(" ", ""), CountReading.Dax(read).Replace(" ", ""));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = words });
            Assert.True(Answered(a), Seen(a));
            Assert.Empty(rig.Jev.Seen);
            // The sentence is said once: the statement, not again as the assumption line.
            Assert.False(string.IsNullOrWhiteSpace(a.Statement));
            Assert.True(string.IsNullOrWhiteSpace(a.Assumption), a.Assumption);
        }

        // Anything with another part in it is not this reading's to drop: it goes to Jev as before.
        [Theory]
        [InlineData("how many customers by region")]
        [InlineData("how many customers in fiscal 2024")]
        [InlineData("how many customers last year")]
        [InlineData("how many customers in the North")]
        [InlineData("how many customers bought twice in 2025")]
        public async Task V3UatQuery_a_count_with_another_part_is_left_to_the_reader_of_the_whole_question(string words)
        {
            using var rig = await OrdersRigAsync();
            Assert.Null(CountReading.TryRead(words, await CatalogAsync(rig)));
        }

        // Round 3, item 3: two questions in different words that read the same are two rows (a newer flagged twin never hides an older
        // answered row), and the same words asked again (Allow, then ask) are one row.
        [Fact]
        public void V3UatQuery_rows_that_read_the_same_but_were_asked_in_other_words_are_both_kept()
        {
            var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "v3uq-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            try
            {
                DataQuestionStore.Append(file, new DataQuestionRecord { Id = "a", Question = "number of orders last year", Reading = "Order Count", Outcome = "answered", InNotebook = true });
                DataQuestionStore.Append(file, new DataQuestionRecord { Id = "b", Question = "how many stores sold in 2025", Reading = "Order Count", Outcome = "answered", InNotebook = true });
                Assert.Equal(2, DataQuestionStore.Read(file, 0).Count(r => r.InNotebook));
                // Allow, then the same words again: the earlier row gives its place to the newer.
                DataQuestionStore.Append(file, new DataQuestionRecord { Id = "c", Question = "How many stores sold in 2025", Reading = "Order Count", Outcome = "answered", InNotebook = true });
                var shown = DataQuestionStore.Read(file, 0).Where(r => r.InNotebook).Select(r => r.Id).OrderBy(x => x).ToArray();
                Assert.Equal(new[] { "a", "c" }, shown);
            }
            finally { System.IO.File.Delete(file); }
        }

        // Round 3, item 1: any measure whose meaning holds a period is found from its DAX, and its base is the measure it wraps and nothing more.
        [Theory]
        [InlineData("CALCULATE ( [Net Sales], SAMEPERIODLASTYEAR ( 'Date'[Date] ) )", true, "Net Sales")]
        [InlineData("TOTALYTD ( [Net Sales], 'Date'[Date] )", true, "Net Sales")]
        [InlineData("CALCULATE ( [Net Sales], PREVIOUSMONTH ( 'Date'[Date] ) )", true, "Net Sales")]
        [InlineData("CALCULATE ( [Net Sales], DATESYTD ( 'Date'[Date] ) )", true, "Net Sales")]
        [InlineData("CALCULATE ( [Net Sales], DATEADD ( 'Date'[Date], -1, YEAR ), Store[Region] = \"North\" )", true, null)]
        [InlineData("DIVIDE ( [Net Sales], CALCULATE ( [Net Sales], SAMEPERIODLASTYEAR ( 'Date'[Date] ) ) ) - 1", true, null)]
        [InlineData("VAR p = CALCULATE ( [Net Sales], DATEADD ( 'Date'[Date], -1, YEAR ) ) RETURN p", true, null)]
        [InlineData("SUM ( Sales[Amount] )", false, null)]
        [InlineData("CALCULATE ( [Net Sales], Store[Region] = \"North\" )", false, null)]
        public void V3UatQuery_a_measure_that_holds_a_period_is_found_and_its_base_is_only_what_it_wraps(string dax, bool moves, string baseName)
        {
            var cat = new QueryCatalog();
            var net = new QueryMeasure { Table = "Sales", Name = "Net Sales", Expression = "SUM ( Sales[Amount] )" };
            var m = new QueryMeasure { Table = "Sales", Name = "Shifted", Expression = dax };
            cat.Measures.Add(net); cat.Measures.Add(m);
            Assert.Equal(moves, MeasureTime.Moves(m, cat));
            Assert.Equal(baseName, moves ? MeasureTime.Base(m, cat)?.Name : null);
            Assert.False(MeasureTime.Moves(net, cat));
        }

        [Fact]
        public void V3UatQuery_a_measure_that_points_at_a_shifted_measure_is_shifted_and_has_the_same_base()
        {
            var cat = new QueryCatalog();
            var net = new QueryMeasure { Table = "Sales", Name = "Net Sales", Expression = "SUM ( Sales[Amount] )" };
            var py = new QueryMeasure { Table = "Sales", Name = "Net Sales PY", Expression = "CALCULATE ( [Net Sales], SAMEPERIODLASTYEAR ( 'Date'[Date] ) )" };
            var alias = new QueryMeasure { Table = "Sales", Name = "Last Year Sales", Expression = "[Net Sales PY]" };
            cat.Measures.AddRange(new[] { net, py, alias });
            Assert.True(MeasureTime.Moves(alias, cat));
            Assert.Equal("Net Sales", MeasureTime.Base(alias, cat)?.Name);
        }
    }
}
