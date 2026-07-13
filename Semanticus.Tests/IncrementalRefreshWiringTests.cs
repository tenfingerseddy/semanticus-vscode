using System;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class IncrementalRefreshWiringTests
    {
        [Fact]
        public void Bare_source_is_wrapped_then_filtered_without_replacing_it()
        {
            var source = "Sql.Database(\"server\", \"warehouse\"){[Schema=\"dbo\",Item=\"Sales\"]}[Data]";

            var result = IncrementalRefreshWiring.AppendRangeFilter(source, "Order Date");

            Assert.Contains("Source = " + source, result);
            Assert.Contains("Table.SelectRows(Source, each [Order Date] >= RangeStart and [Order Date] < RangeEnd)", result);
            Assert.EndsWith("#\"Filtered for incremental refresh\"", result);
        }

        [Fact]
        public void Existing_let_chain_filters_the_step_named_by_in_not_an_unrelated_last_binding()
        {
            var source = "let\n    Source = #table(type table [Date = datetime], {}),\n    Kept = Table.FirstN(Source, 10),\n    Diagnostic = \"in, RangeStart\"\nin\n    Kept";

            var result = IncrementalRefreshWiring.AppendRangeFilter(source, "Date");

            Assert.Contains("Table.SelectRows(Kept, each [Date] >= RangeStart and [Date] < RangeEnd)", result);
            Assert.DoesNotContain("Table.SelectRows(Diagnostic", result);
            Assert.Contains("Diagnostic = \"in, RangeStart\"", result);
        }

        [Fact]
        public void Generated_step_name_is_unique_and_field_brackets_are_escaped()
        {
            var source = "let\n    Source = #table(type table [#\"Closed]At\" = datetime], {}),\n    #\"Filtered for incremental refresh\" = Source\nin\n    #\"Filtered for incremental refresh\"";

            var result = IncrementalRefreshWiring.AppendRangeFilter(source, "Closed]At");

            Assert.Contains("#\"Filtered for incremental refresh1\" = Table.SelectRows(#\"Filtered for incremental refresh\", each [Closed]]At] >= RangeStart and [Closed]]At] < RangeEnd)", result);
            Assert.EndsWith("#\"Filtered for incremental refresh1\"", result);
        }

        [Theory]
        [InlineData("", "Date", "no source")]
        [InlineData("Source", "", "date column")]
        public void Missing_inputs_fail_before_any_M_is_returned(string source, string column, string message)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => IncrementalRefreshWiring.AppendRangeFilter(source, column));
            Assert.Contains(message, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
