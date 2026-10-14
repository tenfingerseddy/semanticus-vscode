using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    // Verifier probes for gaps20 (not for merge).
    public sealed class VGaps20ProbeTests
    {
        private readonly ITestOutputHelper _out;
        public VGaps20ProbeTests(ITestOutputHelper o) { _out = o; }

        [Fact]
        public async Task Probe_D9_only_an_auto_date_table_holds_dates_gives_no_row()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Shop", 1701);
            await f.Engine.CreateTableAsync("Orders", "human");
            await f.Engine.CreateColumnAsync("table:Orders", "Amount", "Decimal", null, "human");
            await f.Engine.CreateTableAsync("LocalDateTable_0b1e7c2a-1111-2222-3333-444455556666", "human");
            await f.Engine.CreateColumnAsync("table:LocalDateTable_0b1e7c2a-1111-2222-3333-444455556666", "Date", "DateTime", null, "human");
            await f.SaveAsAsync("VAuto.bim"); f.UseNull();
            var items = (await f.Engine.GetModelPlanAsync()).Items;
            Assert.DoesNotContain(items, i => i.Id is "datetable/model" or "calendar/model");
        }

        [Fact]
        public async Task Probe_D9_1604_brief_row_and_real_date_column_beside_auto_table()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Shop", 1604);
            await f.Engine.CreateTableAsync("Orders", "human");
            await f.Engine.CreateColumnAsync("table:Orders", "OrderDate", "DateTime", null, "human");
            await f.Engine.CreateTableAsync("LocalDateTable_0b1e7c2a-1111-2222-3333-444455556666", "human");
            await f.Engine.CreateColumnAsync("table:LocalDateTable_0b1e7c2a-1111-2222-3333-444455556666", "Date", "DateTime", null, "human");
            await f.SaveAsAsync("VAuto2.bim"); f.UseNull();
            var row = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "datetable/model");
            _out.WriteLine("WHO=" + row.Who + " TITLE=" + row.Title + " STATUS=" + row.Status);
            _out.WriteLine("WHY=" + string.Join(" | ", row.Why));
            _out.WriteLine("BRIEF=" + row.Brief);
            Assert.Equal(ModelPlanOwners.Assistant, row.Who);
            Assert.Contains("generate_date_table", row.Brief);
        }

        [Fact]
        public async Task Probe_D11_line_count_matches_Review_after()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Shop", 1701);
            await f.Engine.CreateTableAsync("Orders", "human");
            await f.Engine.CreateColumnAsync("table:Orders", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Orders", "Qty", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Orders", "OrderKey", "Int64", null, "human");
            await f.Engine.CreateMeasureAsync("table:Orders", "Total Amount", "SUM ( Orders[Amount] )", "human");
            await f.SaveAsAsync("VFix.bim"); f.UseNull();
            var before = await f.Engine.BpaScanAsync();
            var pressed = before.AutoFixable;
            var withheld = before.Violations.Count(v => v.CanAutoFix && !v.Waived && v.FixWithheld);
            var r = await f.Engine.BpaFixAllAsync("human");
            var fixedSaid = pressed - r.Scorecard.AutoFixable;     // what bpa.tsx's fixAllLine shows: the button before minus the button after
            var reviewDelta = pressed - r.Scorecard.AutoFixable;   // what the Fix N button shows before minus after
            _out.WriteLine($"pressed={pressed} withheld={withheld} applied={r.Applied} unfixed={r.Unfixed.Length} afterAutoFixable={r.Scorecard.AutoFixable} lineFixed={fixedSaid} reviewDelta={reviewDelta} violationsBefore={before.ViolationCount} after={r.Scorecard.ViolationCount}");
            Assert.True(withheld > 0, "probe needs a withheld finding");
            Assert.Equal(reviewDelta, fixedSaid);
            // Every finding the button counted is cleared, so the line says all of them. Unfixed also lists the withheld findings the button
            // never counted: taking them off the count (the first version of the line) said 3, not 4.
            Assert.Equal(0, r.Scorecard.AutoFixable);
            Assert.Equal(pressed, fixedSaid);
            Assert.True(r.Unfixed.Length > r.Scorecard.AutoFixable, "unfixed holds findings the button never counted");
        }
    }
}
