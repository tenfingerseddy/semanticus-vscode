using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Hero run 20, 8s-c (13 Oct 2026): a model at 1701 with a datetime column and no date table, and no time intelligence DAX,
    /// got no date table row, because DATE-MARK is dormant without time intelligence and the row hung on that finding. The row now
    /// comes from the model's own facts: a date column and no marked table.
    /// </summary>
    public sealed class DateTableNoTiRowTests
    {
        private static async Task<DecisionFixture> ModelAsync(string file, int level, bool withDate = true, bool mark = false)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Shop", level);
            await f.Engine.CreateTableAsync("Orders", "human");
            await f.Engine.CreateColumnAsync("table:Orders", "Amount", "Double", null, "human");
            if (withDate) await f.Engine.CreateColumnAsync("table:Orders", "OrderDate", "DateTime", null, "human");
            await f.Engine.CreateMeasureAsync("table:Orders", "Total Amount", "SUM ( Orders[Amount] )", "human");
            if (mark) await f.Engine.MarkDateTableAsync("table:Orders", "OrderDate", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        [Fact]
        public async Task A_date_column_and_no_date_table_gets_a_row_even_with_no_time_intelligence()
        {
            using var f = await ModelAsync("DtNoTi1701.bim", 1701);
            var row = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "datetable/model");
            Assert.Equal(ModelPlanOwners.Assistant, row.Who);
            Assert.Equal("Add a date table", row.Title);
            var said = string.Join(" ", row.Why);
            Assert.Contains("calendar", said);
            Assert.Contains("hierarchy", said);
            Assert.Contains("generate_date_table", row.Brief);
        }

        [Fact]
        public async Task Below_1701_the_row_still_comes_but_names_no_calendar()
        {
            using var f = await ModelAsync("DtNoTi1604.bim", 1604);
            var row = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "datetable/model");
            Assert.DoesNotContain("calendar", string.Join(" ", row.Why), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hierarchy", string.Join(" ", row.Why));
        }

        [Fact]
        public async Task No_date_column_means_no_row()
        {
            using var f = await ModelAsync("DtNoCol.bim", 1701, withDate: false);
            Assert.DoesNotContain((await f.Engine.GetModelPlanAsync()).Items, i => i.Id is "datetable/model" or "calendar/model");
        }

        [Fact]
        public async Task A_marked_date_table_means_no_row()
        {
            using var f = await ModelAsync("DtMarked.bim", 1701, mark: true);
            Assert.DoesNotContain((await f.Engine.GetModelPlanAsync()).Items, i => i.Id is "datetable/model" or "calendar/model");
        }

        [Fact]
        public async Task The_row_goes_when_the_assistant_adds_the_table()
        {
            using var f = await ModelAsync("DtDone.bim", 1701);
            await f.Engine.GenerateDateTableAsync("Date", null, null, true, "agent");
            Assert.DoesNotContain((await f.Engine.GetModelPlanAsync()).Items, i => i.Id is "datetable/model" or "calendar/model");
        }

        // The row promises a hierarchy, so the tools it names must be able to build one on what they make.
        [Fact]
        public async Task The_tools_the_row_names_can_make_a_marked_table_with_a_calendar_and_a_hierarchy()
        {
            using var f = await ModelAsync("DtTools.bim", 1701);
            await f.Engine.DefineCalendarFromTemplateAsync("gregorian", "Date", null, 1, null, null, null, "agent");
            await f.Engine.MarkDateTableAsync("table:Date", "Date", "agent");
            var h = await f.Engine.CreateHierarchyAsync("table:Date", "Calendar", new[] { "Year", "Year Quarter", "Year Month", "Date" }, "agent");
            Assert.False(string.IsNullOrEmpty(h));
        }
    }
}
