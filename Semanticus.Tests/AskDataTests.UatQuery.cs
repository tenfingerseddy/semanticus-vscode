// UAT 17 Oct (two real-user runs of 1.5.104), the Query lane. P21: "how many custmers bought twice" showed 25,669, the order
// count, as if it were the answer. Two parts to the fix: the engine reads "bought twice / N times / more than N times" itself as
// a count of customers by their order count and says its assumption; and a reading the engine's own check flags is marked, so a
// page shows no figure for it. The rig is AskDataTests' own (partial class).

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    public sealed partial class AskDataTests
    {
        /// <summary>The Contoso rig with an order number on the sales rows, an order count, and Customer on an active relationship.</summary>
        private static Task<Rig> OrdersRigAsync([System.Runtime.CompilerServices.CallerMemberName] string test = null) =>
            BuildAsync(twins: true, test: test, shape: async (e, data) =>
            {
                await e.CreateColumnAsync("table:Sales", "OrderNumber", "String", "OrderNumber", "human");
                await e.CreateMeasureAsync("table:Sales", "Order Count", "DISTINCTCOUNT ( Sales[OrderNumber] )", "human");
            });

        [Theory]
        [InlineData("how many custmers bought twice", "=", 2)]
        [InlineData("How many customers bought exactly 3 times?", "=", 3)]
        [InlineData("how many customers bought once", "=", 1)]
        [InlineData("how many customers bought more than 2 times", ">", 2)]
        [InlineData("how many customers bought more than once", ">", 1)]
        [InlineData("how many customers bought 2 or more times", ">=", 2)]
        [InlineData("how many customers bought at least 3 times", ">=", 3)]
        [InlineData("number of customers who ordered at least twice", ">=", 2)]
        public async Task P21_bought_twice_is_read_as_a_count_of_customers_by_their_order_count(string words, string op, int n)
        {
            using var rig = await OrdersRigAsync();
            var read = FrequencyReading.TryRead(words, await CatalogAsync(rig));
            Assert.NotNull(read);
            Assert.Equal(op, read.Op);
            Assert.Equal(n, read.N);
            Assert.Equal("Customer", read.Entity.Table);
            Assert.Equal("Order Count", read.Counter.Name);
        }

        // The same question with a year in it ("bought twice in 2025") gave 13,301, orders again, when Jev's check was not sure: the year
        // is the one narrowing this reading carries.
        [Fact]
        public async Task P21_a_year_in_the_question_is_applied_to_the_orders_each_customer_is_counted_by()
        {
            using var rig = await OrdersRigAsync();
            var read = FrequencyReading.TryRead("how many customers bought twice in 2025", await CatalogAsync(rig));
            Assert.NotNull(read);
            Assert.Equal(2025, read.Year);
            Assert.Equal("Year", read.YearColumn.Name);
            Assert.Contains("CALCULATE([Order Count], 'Date'[Year] = 2025)", FrequencyReading.Dax(read));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how many customers bought twice in 2025" });
            Assert.True(Answered(a), Seen(a));
            Assert.Empty(rig.Jev.Seen);
            Assert.Contains("exactly 2 orders in 2025", a.Assumption);
            Assert.EndsWith(" in 2025", FrequencyReading.QuestionFor(read, FrequencyReading.AtLeast));
            Assert.Null(FrequencyReading.TryRead("how many customers bought twice in 2025 by region", await CatalogAsync(rig)));
        }

        // The Contoso demo: tables "dim_customer" and "fact_sales", and no order-count measure, only the column that names the order.
        [Fact]
        public void P21_a_warehouse_model_without_an_order_measure_counts_the_distinct_orders_of_its_order_column()
        {
            var cat = new QueryCatalog();
            cat.Columns.Add(new QueryColumn { Table = "dim_customer", Name = "customer_key", Kind = "whole", Key = true });
            cat.Columns.Add(new QueryColumn { Table = "fact_sales", Name = "customer_key", Kind = "whole", Key = true });
            cat.Columns.Add(new QueryColumn { Table = "fact_sales", Name = "order_number", Kind = "text" });
            cat.Columns.Add(new QueryColumn { Table = "dim_date", Name = "year", Kind = "whole", Key = true });
            cat.Columns.Add(new QueryColumn { Table = "fact_sales", Name = "date_key", Kind = "whole", Key = true });
            // The first live run counted the RETURNS table's orders (531 customers, where the sales say 188): a table of returns is never the orders.
            cat.Columns.Add(new QueryColumn { Table = "fact_returns", Name = "customer_key", Kind = "whole", Key = true });
            cat.Columns.Add(new QueryColumn { Table = "fact_returns", Name = "order_number", Kind = "text" });
            cat.Edges.Add(("fact_returns", "dim_customer", false));
            cat.Edges.Add(("fact_sales", "dim_customer", false));
            cat.Edges.Add(("fact_sales", "dim_date", false));
            cat.Measures.Add(new QueryMeasure { Table = "fact_sales", Name = "Total net amount", Expression = "SUM ( fact_sales[net_amount] )", Reads = new[] { "fact_sales" } });
            var read = FrequencyReading.TryRead("how many customers bought twice", cat);
            Assert.NotNull(read);
            Assert.Equal("customers", read.Plural.ToLowerInvariant());
            Assert.Equal("fact_sales order_number", read.CounterName);
            Assert.Equal("fact_sales", read.CounterColumn.Table);
            var dax = FrequencyReading.Dax(read);
            Assert.Contains("VALUES('dim_customer'[customer_key])", dax);
            Assert.Contains("CALCULATE(DISTINCTCOUNT('fact_sales'[order_number])", dax);
            var inYear = FrequencyReading.TryRead("how many customers bought twice in 2024", cat);
            Assert.Contains("'dim_date'[year] = 2024", FrequencyReading.Dax(inYear));
        }

        [Fact]
        public void P21_two_tables_that_could_hold_the_orders_and_no_name_to_tell_them_apart_are_left_to_jev()
        {
            var cat = new QueryCatalog();
            cat.Columns.Add(new QueryColumn { Table = "customer", Name = "customer_key", Kind = "whole", Key = true });
            foreach (var t in new[] { "alpha", "beta" })
            {
                cat.Columns.Add(new QueryColumn { Table = t, Name = "customer_key", Kind = "whole", Key = true });
                cat.Columns.Add(new QueryColumn { Table = t, Name = "order_number", Kind = "text" });
                cat.Edges.Add((t, "customer", false));
            }
            Assert.Null(FrequencyReading.TryRead("how many customers bought twice", cat));
        }

        [Theory]
        [InlineData("net sales by region")]
        [InlineData("how many orders did we have")]
        [InlineData("how many customers bought twice by region")]
        [InlineData("how many customers bought twice from the coast")]
        [InlineData("how many customers are there")]
        public async Task P21_a_question_with_anything_else_in_it_is_left_to_jev(string words)
        {
            using var rig = await OrdersRigAsync();
            Assert.Null(FrequencyReading.TryRead(words, await CatalogAsync(rig)));
        }

        [Fact]
        public async Task P21_the_answer_says_exactly_2_orders_and_offers_2_or_more_and_the_figure_is_the_count_of_customers()
        {
            using var rig = await OrdersRigAsync();
            rig.Data.CustomersByOrders = 0L;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how many custmers bought twice" });
            Assert.True(Answered(a), Seen(a));
            Assert.Empty(rig.Jev.Seen);   // read by the engine: Jev was not asked, so the nearest part cannot be picked
            Assert.Contains("exactly 2 orders", a.Assumption);
            Assert.Null(a.Flagged);
            Assert.Contains("[@Orders] = 2", a.Dax);
            Assert.Equal("Customers", Assert.Single(a.Columns).Name);
            Assert.Equal(0L, Assert.Single(Assert.Single(a.Rows)));
            var other = Assert.Single(a.Others);
            Assert.Equal("2 or more orders", other.Instead);

            // The press: asking that other reading's question reads "2 or more" and counts with >=.
            rig.Data.CustomersByOrders = 1848L;
            var more = await rig.Engine.AskDataAsync(other.Parts);
            Assert.True(Answered(more), Seen(more));
            Assert.Contains("[@Orders] >= 2", more.Dax);
            Assert.Contains("2 or more orders", more.Assumption);
            Assert.Equal(1848L, more.Rows[0][0]);
        }

        /// <summary>A reading the engine's own check names a gap in is marked, so a page shows no figure for it (P21). A reading it is
        /// content with is not. A check that could not be made says nothing and marks nothing (it never stops a run: K16).</summary>
        [Fact]
        public async Task P21_a_reading_the_check_flags_carries_the_line_and_one_it_clears_does_not()
        {
            using var rig = await BuildAsync(script: Checked("narrowing", 0.97));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal("The question narrows the numbers in a way this reading does not show.", a.Flagged);
            Assert.Equal(a.Flagged, a.Assumption);

            using var ok = await BuildAsync(script: Checked("nothing", 0.97));
            Assert.Null((await ok.Engine.AskDataAsync(new DataAsk { Question = "net sales" })).Flagged);
        }

        /// <summary>The Contoso demo's shape with a Customers count, the way the second 1.5.104 UAT found it.</summary>
        private static QueryCatalog ContosoCatalog(bool withCustomerCount)
        {
            var cat = new QueryCatalog();
            cat.Columns.Add(new QueryColumn { Table = "dim_customer", Name = "customer_key", Kind = "whole", Key = true });
            cat.Columns.Add(new QueryColumn { Table = "fact_sales", Name = "customer_key", Kind = "whole", Key = true });
            cat.Columns.Add(new QueryColumn { Table = "fact_sales", Name = "order_number", Kind = "text" });
            cat.Columns.Add(new QueryColumn { Table = "dim_date", Name = "year", Kind = "whole" });
            cat.Columns.Add(new QueryColumn { Table = "dim_date", Name = "fiscal_year", Kind = "whole" });
            cat.Columns.Add(new QueryColumn { Table = "fact_sales", Name = "date_key", Kind = "whole", Key = true });
            cat.Edges.Add(("fact_sales", "dim_customer", false));
            cat.Edges.Add(("fact_sales", "dim_date", false));
            cat.Measures.Add(new QueryMeasure { Table = "fact_sales", Name = "Total net amount", Expression = "SUM ( fact_sales[net_amount] )", Reads = new[] { "fact_sales" } });
            cat.Measures.Add(new QueryMeasure { Table = "fact_sales", Name = "Total order line number", Expression = "SUM ( fact_sales[line_number] )", Reads = new[] { "fact_sales" } });
            if (withCustomerCount)
                cat.Measures.Add(new QueryMeasure { Table = "fact_sales", Name = "Customers", Expression = "DISTINCTCOUNT ( fact_sales[customer_key] )", Reads = new[] { "fact_sales" } });
            return cat;
        }

        [Theory]
        [InlineData("how many customers ordered more then 5 times")]
        [InlineData("how many customers ordred more than 5 tiems")]
        [InlineData("How many custmers bougth twice?")]
        public void UatQuery2_a_slip_in_the_key_words_of_the_count_by_orders_still_reads(string words)
        {
            Assert.NotNull(FrequencyReading.TryRead(words, ContosoCatalog(false)));
        }

        [Fact]
        public void UatQuery2_a_question_that_counts_customers_is_not_answered_by_a_sum_of_order_lines()
        {
            var cat = ContosoCatalog(true);
            var spec = new QuerySpec();
            spec.Measures.Add(cat.Measures.First(m => m.Name == "Total order line number"));
            var guard = QueryGuards.CountOfThing("how many customers ordered more than five times", spec, cat);
            Assert.NotNull(guard);
            Assert.Contains("not a count of customers", guard.Value.Line);
            Assert.Equal("Customers", guard.Value.Counter.Name);   // the offer: the measure that does count them

            var counted = new QuerySpec();
            counted.Measures.Add(cat.Measures.First(m => m.Name == "Customers"));
            Assert.Null(QueryGuards.CountOfThing("how many customers ordered more than five times", counted, cat));
            // Not a count of a thing: an amount asked for by its own name is left alone.
            Assert.Null(QueryGuards.CountOfThing("total net amount by customer", spec, cat));
            Assert.Null(QueryGuards.CountOfThing("how many units did we sell", spec, cat));
        }

        // The happy path through the engine, end to end: the typo question is read by the engine (Jev is never asked), counts with ">" and shows
        // the count of customers, not a sum (Contoso live: 3,431, direct DAX equal; the sum of order lines was 400,342).
        [Fact]
        public async Task UatQuery2_the_question_with_then_for_than_is_answered_by_the_count_of_customers()
        {
            using var rig = await OrdersRigAsync();
            rig.Data.CustomersByOrders = 3431L;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how many customers ordered more then 5 times" });
            Assert.True(Answered(a), Seen(a));
            Assert.Empty(rig.Jev.Seen);
            Assert.Null(a.Flagged);
            Assert.Contains("[@Orders] > 5", a.Dax);
            Assert.Contains("more than 5 orders", a.Assumption);
            Assert.Equal(3431L, Assert.Single(Assert.Single(a.Rows)));
        }

        // A question that counts customers, read by Jev as a total, is flagged with no figure and offered the count: and once Jev reads the
        // count itself, nothing is flagged.
        [Fact]
        public async Task UatQuery2_how_many_customers_read_as_an_amount_is_flagged_and_offered_the_count()
        {
            using var amount = await BuildAsync(twins: true, script: Gate("nothing", 0.97, measure: "Net Sales"),
                shape: (e, data) => e.CreateMeasureAsync("table:Sales", "Customers", "DISTINCTCOUNT ( Sales[CustomerKey] )", "human"));
            var a = await amount.Engine.AskDataAsync(new DataAsk { Question = "how many customers bought something" });
            Assert.NotNull(a.Flagged);
            Assert.Contains("not a count of customers", a.Flagged);
            var offer = Assert.Single(a.Others, o => o.Instead == "Customers");
            Assert.Equal("Customers", Assert.Single(offer.Parts.Measures));

            using var counted = await BuildAsync(twins: true, script: Gate("nothing", 0.97, measure: "Customers"),
                shape: (e, data) => e.CreateMeasureAsync("table:Sales", "Customers", "DISTINCTCOUNT ( Sales[CustomerKey] )", "human"));
            var b = await counted.Engine.AskDataAsync(new DataAsk { Question = "how many customers bought something" });
            Assert.True(Answered(b), Seen(b));
            Assert.Null(b.Flagged);
        }

        [Fact]
        public void UatQuery2_a_measure_built_on_the_count_of_customers_is_still_a_count_of_customers()
        {
            var cat = ContosoCatalog(true);
            var active = new QueryMeasure { Table = "fact_sales", Name = "Active Customers", Expression = "CALCULATE ( [Customers], fact_sales[net_amount] > 0 )", Reads = new[] { "fact_sales" } };
            cat.Measures.Add(active);
            var spec = new QuerySpec();
            spec.Measures.Add(active);
            Assert.Null(QueryGuards.CountOfThing("how many customers bought something", spec, cat));
        }

        [Fact]
        public void UatQuery2_one_year_in_the_question_filters_one_date_level_and_the_words_follow_the_filters_that_run()
        {
            var cat = ContosoCatalog(false);
            QueryColumn Col(string n) => cat.Columns.First(c => c.Table == "dim_date" && c.Name == n);
            ValueCandidate Year(string id, string column) => new ValueCandidate { Id = id, Column = Col(column), Words = "2024", Kind = "year", Values = { 2024L } };
            JevQueryReader.Reading Both() => new JevQueryReader.Reading
            {
                Filters = { (Year("f1", "fiscal_year"), QueryOps.Is), (Year("f2", "year"), QueryOps.Is) },
            };

            var plain = Both();
            QueryGuards.OneYearLevel(plain, "total net amount in 2024", cat);
            Assert.Equal("year", Assert.Single(plain.Filters).Candidate.Column.Name);

            var fiscal = Both();
            QueryGuards.OneYearLevel(fiscal, "total net amount in fiscal 2024", cat);
            Assert.Equal("fiscal_year", Assert.Single(fiscal.Filters).Candidate.Column.Name);

            // Built the way the engine builds it, the dates said are made from the one filter that is applied.
            var spec = new QuerySpec();
            foreach (var (c, op) in plain.Filters) spec.Filters.Add(new QueryFilter { Column = c.Column, Op = op, Values = c.Values });
            Assert.Null(QueryGuards.TwoYearLevels(spec, cat));
            Assert.Contains("1 January to 31 December 2024", PeriodDates.Of(spec.Filters[0].Column, spec.Filters, cat));
        }

        // P20b: a time split reads "in time order", never "A to Z"; a name column still reads A to Z.
        [Fact]
        public void P20b_a_time_split_is_said_in_time_order_and_a_name_split_still_in_the_alphabet()
        {
            Assert.Equal("in time order", QueryTemplate.SortWords(new QueryColumn { Table = "Date", Name = "Year", Kind = "whole" }, false));
            Assert.Equal("in time order", QueryTemplate.SortWords(new QueryColumn { Table = "Sales", Name = "Order Date", Kind = "date" }, false));
            Assert.Equal("newest first", QueryTemplate.SortWords(new QueryColumn { Table = "Date", Name = "Month", Kind = "text" }, true));
            Assert.Equal("A to Z", QueryTemplate.SortWords(new QueryColumn { Table = "Store", Name = "City", Kind = "text" }, false));
        }
    }
}
