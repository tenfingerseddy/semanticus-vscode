// v-fix-uat-query verifier (18 Oct 2026): three defects the live journey found on m16/fix-uat-query 88bf6c1e. Each test fails on that
// commit by design. Evidence: /tmp/semanticus-joint-review/m16/v-fix-uat-query/shots (k128, k131, k49 to k51) and direct-c1.txt.
//   1. Contoso Sales, "how many customers ordered more then 5 times" (a typo for "than"): the reader left it to Jev, Jev read
//      "Total order line number as one total", the check did not flag it, and the page showed a big 400,342. Direct DAX: 3,431.
//   2. Northstar, "how many customers bougth more than 3 times" (letters swapped): the same miss; Jev's reading was flagged there,
//      so the page showed no figure, but the question went unanswered while "bought" read it (1,841, direct DAX equal).
//   3. Contoso Sales, "total net amount in 2024": Jev's reading filtered fiscal_year = 2024 AND year = 2024. The statement said
//      "1 January to 31 December 2024" and the page showed 11,547,531.6, which is January to June 2024 only. Direct DAX: calendar
//      2024 = 25,503,291.69, fiscal 2024 = 23,547,569.71. No guard named the fiscal year the question never asked for.

using System.Collections.Generic;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VFixUatQueryTests
    {
        /// <summary>The Contoso demo's shape, as the lane's own warehouse test builds it.</summary>
        private static QueryCatalog Contoso()
        {
            var cat = new QueryCatalog();
            cat.Columns.Add(new QueryColumn { Table = "dim_customer", Name = "customer_key", Kind = "whole", Key = true });
            cat.Columns.Add(new QueryColumn { Table = "fact_sales", Name = "customer_key", Kind = "whole", Key = true });
            cat.Columns.Add(new QueryColumn { Table = "fact_sales", Name = "order_number", Kind = "text" });
            cat.Columns.Add(new QueryColumn { Table = "dim_date", Name = "year", Kind = "whole", Key = true });
            cat.Columns.Add(new QueryColumn { Table = "dim_date", Name = "fiscal_year", Kind = "whole" });
            cat.Columns.Add(new QueryColumn { Table = "fact_sales", Name = "date_key", Kind = "whole", Key = true });
            cat.Edges.Add(("fact_sales", "dim_customer", false));
            cat.Edges.Add(("fact_sales", "dim_date", false));
            cat.Measures.Add(new QueryMeasure { Table = "fact_sales", Name = "Total net amount", Expression = "SUM ( fact_sales[net_amount] )", Reads = new[] { "fact_sales" } });
            return cat;
        }

        [Theory]
        [InlineData("how many customers ordered more then 5 times", ">", 5)]
        [InlineData("how many customers bougth more than 3 times", ">", 3)]
        public void A_common_typo_in_the_count_of_customers_by_their_orders_is_still_read(string words, string op, int n)
        {
            var read = FrequencyReading.TryRead(words, Contoso());
            Assert.True(read != null, "not read, so it goes to Jev, which showed 400,342 (Total order line number) for the first one live");
            Assert.Equal(op, read.Op);
            Assert.Equal(n, read.N);
        }

        [Fact]
        public void A_reading_that_adds_a_fiscal_year_the_question_never_named_is_flagged()
        {
            var cat = Contoso();
            QueryColumn Col(string name) => cat.Columns.Find(c => c.Table == "dim_date" && c.Name == name);
            var spec = new QuerySpec();
            spec.Measures.Add(cat.Measures[0]);
            spec.Filters.Add(new QueryFilter { Column = Col("fiscal_year"), Values = new List<object> { 2024L } });
            spec.Filters.Add(new QueryFilter { Column = Col("year"), Values = new List<object> { 2024L } });
            var gap = QueryGuards.Fiscal("total net amount in 2024", spec, cat);
            Assert.True(gap != null, "fiscal_year = 2024 AND year = 2024 is January to June 2024; the page showed it as the whole of 2024");
        }
    }
}
