// What a Jev row is worth (1.5, pick A of the Jev plan): "+N AI ready" on the row's Value, measured by rehearsing the row's kept lines.
//
// PROVED HERE, ON A REAL MODEL: the number on the row is the number the AI readiness score moves by when the row is applied (within
// the rounding the words carry); a row never says a worth it did not measure (nothing before the rehearsal lands, nothing for a row
// with no kept line); and dropping a line makes the old number unknown rather than showing it for a row it no longer describes.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class JevRowWorthTests : IClassFixture<ModelTemplates>
    {
        private const string Point = "measure.folder_and_format";
        private readonly ModelTemplates _templates;
        private readonly ITestOutputHelper _out;
        public JevRowWorthTests(ModelTemplates templates, ITestOutputHelper output) { _templates = templates; _out = output; }

        private async Task<DecisionFixture> ModelAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "rowworth", BuildAsync, file);
            f.UseNull();
            return f;
        }

        /// <summary>Two tables, measures that want a folder and a format, and the measures Jev is sure of.</summary>
        private static async Task BuildAsync(DecisionFixture f)
        {
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Margin Pct", "DIVIDE ( [Total Sales], [Total Sales] )", "human");
            foreach (var n in Counts) await f.Engine.CreateMeasureAsync("table:Sales", n, "COUNTROWS ( Sales )", "human");
            await f.Engine.CreateTableAsync("Returns", "human");
            await f.Engine.CreateColumnAsync("table:Returns", "Qty", "Int64", null, "human");
            await f.Engine.CreateMeasureAsync("table:Returns", "Return Count", "COUNTROWS ( Returns )", "human");
        }

        /// <summary>Eight counts in Sales, so one row is worth more than a point and the number is not lost in rounding.</summary>
        private static readonly string[] Counts = { "Order Count", "Line Count", "Customer Count", "Product Count", "Store Count", "Return Lines", "Ship Count", "Promo Count" };

        private static string Fixture(IEnumerable<(string Ref, string Folder, string Format)> rows)
        {
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            var first = true;
            foreach (var (r, folder, format) in rows)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"digest\":").Append(JsonSerializer.Serialize(r))
                  .Append(",\"point\":\"").Append(Point).Append('"')
                  .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":40}")
                  .Append(",\"answers\":{")
                  .Append("\"display_folder\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(MeasureDefaultsPoint.FolderOptionId(folder)))
                  .Append(",\"confidence\":0.93},")
                  .Append("\"format_family\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(format))
                  .Append(",\"confidence\":0.95}}}");
            }
            return sb.Append("]}").ToString();
        }

        private static async Task AnswerAsync(DecisionFixture f)
        {
            await f.ConsentAsync();
            f.UseRecordedByObject(Fixture(new[]
            {
                ("measure:Sales/Total Sales", "Money", "currency"),
                ("measure:Sales/Margin Pct", "Money", "percent"),
                ("measure:Returns/Return Count", "Volume", "whole_number"),
            }.Concat(Counts.Select(n => ("measure:Sales/" + n, "Volume", "whole_number")))));
        }

        /// <summary>The plan with Jev's rows and their worth landed: read, let Jev land, read, let the rehearsal land, read.</summary>
        private static async Task<ModelPlanView> WorthPlanAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            await f.Engine.GetModelPlanAsync();
            await f.Engine.RowWorthForTest;
            return await f.Engine.GetModelPlanAsync();
        }

        /// <summary>The rows Jev's lines make. Not the "Jev is still looking" progress row, which has no lines and its own Value.</summary>
        private static ModelPlanItem[] JevRows(ModelPlanView plan) =>
            plan.Items.Where(i => i.Id.StartsWith("jev.", StringComparison.Ordinal) && i.ChangeSet != null).ToArray();

        private static Task<double> ScoreAsync(DecisionFixture f) =>
            f.Sessions.Current.ReadAsync(m => new ReadinessAnalyzer().Analyze(m).Overall);

        private static int Points(string value) =>
            int.Parse(value.Substring(1, value.IndexOf(' ') - 1), CultureInfo.InvariantCulture);

        [Fact]
        public async Task A_Jev_row_says_the_points_that_applying_it_moves_the_AI_ready_score_by()
        {
            using var f = await ModelAsync("RowWorth.bim");
            await AnswerAsync(f);
            var plan = await WorthPlanAsync(f);

            var rows = JevRows(plan);
            Assert.NotEmpty(rows);
            var worth = rows.Where(r => !string.IsNullOrEmpty(r.Value)).ToArray();
            Assert.NotEmpty(worth);   // the happy path: at least one row says what it is worth
            foreach (var row in worth)
            {
                Assert.Matches(@"^\+\d+ AI ready$", row.Value);
                Assert.DoesNotContain("—", row.Value, StringComparison.Ordinal);
            }

            // Apply each row that says a worth, on the real model, and read the score it really moved by.
            foreach (var row in worth)
            {
                var again = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == row.Id);
                var before = await ScoreAsync(f);
                var result = await f.Engine.ModelPlanActionAsync(again.Id, "approve", "human", again.ChangeSet.Digest);
                Assert.True(result.Performed, result.Sentence);
                var moved = await ScoreAsync(f) - before;
                _out.WriteLine($"{row.Id}: said {row.Value}, the score moved by {moved:0.00} ({before:0.0} to {before + moved:0.0})");
                Assert.True(Math.Abs(moved - Points(row.Value)) <= 0.5,
                    $"{row.Id} said {row.Value} but the score moved by {moved:0.00}");
                await f.Engine.GetModelPlanAsync();
                await f.Engine.RowWorthForTest;
            }
        }

        [Fact]
        public async Task The_assistants_read_of_the_plan_tells_the_same_story_as_the_page()
        {
            using var f = await ModelAsync("RowWorthMcp.bim");
            await AnswerAsync(f);
            var plan = await WorthPlanAsync(f);
            var row = JevRows(plan).First(r => !string.IsNullOrEmpty(r.Value));

            // get_model_plan answers with PlanReadShape.For; the row it hands the assistant has the mark (source), the worth (value) and the reason (why).
            var read = PlanReadShape.For(plan, null, 0, 0).Items.Single(i => i.Id == row.Id);
            Assert.Equal("jev", read.Source);
            Assert.Equal(row.Value, read.Value);
            Assert.Matches(@"^\+\d+ AI ready$", read.Value);
            Assert.False(string.IsNullOrWhiteSpace(read.Why.FirstOrDefault()));
            Assert.Equal(row.Why, read.Why);
        }

        [Fact]
        public async Task Dropping_every_line_of_a_row_makes_its_worth_unknown_not_stale()
        {
            using var f = await ModelAsync("RowWorthDrop.bim");
            await AnswerAsync(f);
            var plan = await WorthPlanAsync(f);
            var row = JevRows(plan).First(r => !string.IsNullOrEmpty(r.Value));

            foreach (var line in row.ChangeSet.Lines.Where(l => l.Kept))
                await f.Engine.SetPlanItemAsync(line.PlanItemId, null, false, "human");
            var after = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == row.Id);

            Assert.Empty(after.ChangeSet.Lines.Where(l => l.Kept));
            Assert.True(string.IsNullOrEmpty(after.Value), "a number worked out for ticked lines must not stay on a row with none ticked: " + after.Value);
        }

        [Fact]
        public async Task A_row_says_nothing_until_its_worth_is_measured_and_nothing_when_it_has_no_kept_line()
        {
            using var f = await ModelAsync("RowWorthLate.bim");
            await AnswerAsync(f);
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            // The first read after Jev landed starts the rehearsal and does not wait for it.
            var first = await f.Engine.GetModelPlanAsync();
            Assert.All(JevRows(first), r => { if (!string.IsNullOrEmpty(r.Value)) Assert.Matches(@"^\+\d+ AI ready$", r.Value); });

            var plan = await WorthPlanAsync(f);
            foreach (var row in JevRows(plan))
                if (!row.ChangeSet.Lines.Any(l => l.Kept)) Assert.True(string.IsNullOrEmpty(row.Value), row.Id);
        }
    }
}
