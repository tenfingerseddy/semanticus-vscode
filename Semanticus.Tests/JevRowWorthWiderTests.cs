// The row worth, wider than JevRowWorthTests (1.5, the verifier's follow-up): it holds on more than one model, it never shows a number
// for lines that were ticked off, and working it out never writes to the model.
//
// PROVED HERE, ON REAL MODELS WITH RECORDED JEV ANSWERS (Pro): two differently shaped models each give kept Jev lines; every row that says
// "+N AI ready" moves the score by N when applied; unticking half the lines of a row shows no number until the row is worked out again,
// and the new number is the true one; and the rehearsal leaves the model's JSON and revision exactly as they were.

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
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    public sealed class JevRowWorthWiderTests : IClassFixture<ModelTemplates>
    {
        private const string Point = "measure.folder_and_format";
        private readonly ModelTemplates _templates;
        private readonly ITestOutputHelper _out;
        public JevRowWorthWiderTests(ModelTemplates templates, ITestOutputHelper output) { _templates = templates; _out = output; }

        /// <summary>A model: tables with a value column and measures, and what Jev answers for each measure (folder, format).</summary>
        public sealed record Shape(string Name, (string Table, string Column, string Type, (string Measure, string Expr, string Folder, string Format)[] Measures)[] Tables)
        {
            public override string ToString() => Name;
        }

        // Same recipe as JevRowWorthTests: measures a person left bare, a mix of money, counts and shares, and enough of them that a row is worth over a point.
        private static readonly Shape Retail = new("Retail", new[]
        {
            ("Sales", "Amount", "Double", new[]
            {
                ("Total Sales", "SUM ( Sales[Amount] )", "Money", "currency"),
                ("Margin Pct", "DIVIDE ( [Total Sales], [Total Sales] )", "Money", "percent"),
                ("Order Count", "COUNTROWS ( Sales )", "Volume", "whole_number"),
                ("Line Count", "COUNTROWS ( Sales )", "Volume", "whole_number"),
                ("Customer Count", "COUNTROWS ( Sales )", "Volume", "whole_number"),
                ("Product Count", "COUNTROWS ( Sales )", "Volume", "whole_number"),
                ("Store Count", "COUNTROWS ( Sales )", "Volume", "whole_number"),
                ("Return Lines", "COUNTROWS ( Sales )", "Volume", "whole_number"),
            }),
            ("Returns", "Qty", "Int64", new[] { ("Return Count", "COUNTROWS ( Returns )", "Volume", "whole_number") }),
        });

        private static readonly Shape Warehouse = new("Warehouse", new[]
        {
            ("Stock", "OnHand", "Int64", new[]
            {
                ("Units On Hand", "SUM ( Stock[OnHand] )", "Volume", "whole_number"),
                ("Stock Lines", "COUNTROWS ( Stock )", "Volume", "whole_number"),
                ("Bin Count", "COUNTROWS ( Stock )", "Volume", "whole_number"),
                ("Pallet Count", "COUNTROWS ( Stock )", "Volume", "whole_number"),
                ("Aisle Count", "COUNTROWS ( Stock )", "Volume", "whole_number"),
                ("Fill Rate", "DIVIDE ( [Units On Hand], [Units On Hand] )", "Service", "percent"),
            }),
            ("Freight", "Cost", "Double", new[]
            {
                ("Freight Cost", "SUM ( Freight[Cost] )", "Money", "currency"),
                ("Fuel Cost", "SUM ( Freight[Cost] )", "Money", "currency"),
                ("Trip Count", "COUNTROWS ( Freight )", "Volume", "whole_number"),
            }),
        });

        public static IEnumerable<object[]> Shapes() { yield return new object[] { Retail }; yield return new object[] { Warehouse }; }

        private async Task<DecisionFixture> ModelAsync(Shape shape)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "rowworthwide" + shape.Name, async fx =>
            {
                await fx.Engine.CreateModelAsync("Contoso", 1604);
                foreach (var (table, column, type, measures) in shape.Tables)
                {
                    await fx.Engine.CreateTableAsync(table, "human");
                    await fx.Engine.CreateColumnAsync("table:" + table, column, type, null, "human");
                    foreach (var m in measures) await fx.Engine.CreateMeasureAsync("table:" + table, m.Measure, m.Expr, "human");
                }
            }, "RowWorthWide" + shape.Name + ".bim");
            f.UseNull();
            await f.ConsentAsync();
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            var first = true;
            foreach (var (table, _, _, measures) in shape.Tables)
                foreach (var m in measures)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"digest\":").Append(JsonSerializer.Serialize("measure:" + table + "/" + m.Measure))
                      .Append(",\"point\":\"").Append(Point).Append('"')
                      .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":40},\"answers\":{")
                      .Append("\"display_folder\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(MeasureDefaultsPoint.FolderOptionId(m.Folder))).Append(",\"confidence\":0.93},")
                      .Append("\"format_family\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(m.Format)).Append(",\"confidence\":0.95}}}");
                }
            f.UseRecordedByObject(sb.Append("]}").ToString());
            return f;
        }

        private static async Task<ModelPlanView> LandedAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            await f.Engine.GetModelPlanAsync();
            await f.Engine.RowWorthForTest;
            return await f.Engine.GetModelPlanAsync();
        }

        private static ModelPlanItem[] JevRows(ModelPlanView p) => p.Items.Where(i => i.Id.StartsWith("jev.", StringComparison.Ordinal) && i.ChangeSet != null).ToArray();
        private static int Kept(ModelPlanItem r) => r.ChangeSet.Lines.Count(l => l.Kept);
        private static Task<double> ScoreAsync(DecisionFixture f) => f.Sessions.Current.ReadAsync(m => new ReadinessAnalyzer().Analyze(m).Overall);
        private static Task<string> JsonAsync(DecisionFixture f) =>
            f.Sessions.Current.ReadAsync(m => TOM.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase, new TOM.SerializeOptions { IgnoreTimestamps = true }));
        private static int Points(string v) => int.Parse(v.Substring(1, v.IndexOf(' ') - 1), CultureInfo.InvariantCulture);

        [Theory]
        [MemberData(nameof(Shapes))]
        public async Task Each_model_gives_kept_Jev_lines_and_every_worth_said_is_the_move_applying_it_makes(Shape shape)
        {
            using var f = await ModelAsync(shape);
            var plan = await LandedAsync(f);
            var rows = JevRows(plan);
            _out.WriteLine(shape.Name + " rows: " + string.Join(" | ", rows.Select(r => $"{r.Id}={r.Value} kept {Kept(r)}")));
            Assert.True(rows.Any(r => Kept(r) > 0), shape.Name + ": the recorded Jev answers made no kept line, so this test proves nothing");

            var applied = 0;
            foreach (var id in rows.Where(r => !string.IsNullOrEmpty(r.Value)).Select(r => r.Id).ToArray())
            {
                var row = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == id);
                if (string.IsNullOrEmpty(row.Value) || Kept(row) == 0) { await f.Engine.RowWorthForTest; continue; }
                var before = await ScoreAsync(f);
                var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", row.ChangeSet.Digest);
                Assert.True(result.Performed, result.Sentence);
                var moved = await ScoreAsync(f) - before;
                _out.WriteLine($"{shape.Name} {row.Id}: said {row.Value}, moved {moved:0.00}");
                Assert.True(Math.Abs(moved - Points(row.Value)) <= 0.5, $"{row.Id} said {row.Value} but the score moved by {moved:0.00}");
                applied++;
                // Straight after the model moved, no other open row shows a number worked out on the old model.
                foreach (var other in JevRows(await f.Engine.GetModelPlanAsync()).Where(r => r.Id != row.Id && r.Status != "done"))
                    Assert.True(string.IsNullOrEmpty(other.Value), $"{other.Id} shows {other.Value} straight after the model moved");
                await f.Engine.RowWorthForTest;
            }
            Assert.True(applied >= 1, shape.Name + ": no row said a worth");
        }

        [Fact]
        public async Task Unticking_half_the_lines_shows_no_old_number_and_the_new_number_is_true()
        {
            using var f = await ModelAsync(Retail);
            var plan = await LandedAsync(f);
            var row = JevRows(plan).Where(r => !string.IsNullOrEmpty(r.Value)).OrderByDescending(Kept).First();
            var kept = row.ChangeSet.Lines.Where(l => l.Kept).ToList();
            Assert.True(kept.Count >= 2, "need a row with two kept lines");
            foreach (var l in kept.Take(kept.Count / 2)) await f.Engine.SetPlanItemAsync(l.PlanItemId, null, false, "human");

            var straight = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == row.Id);
            Assert.True(string.IsNullOrEmpty(straight.Value), "the old worth stayed after lines were unticked: " + straight.Value);
            await f.Engine.RowWorthForTest;
            var fresh = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == row.Id);
            Assert.Equal(kept.Count - kept.Count / 2, Kept(fresh));

            var before = await ScoreAsync(f);
            var result = await f.Engine.ModelPlanActionAsync(fresh.Id, "approve", "human", fresh.ChangeSet.Digest);
            Assert.True(result.Performed, result.Sentence);
            var moved = await ScoreAsync(f) - before;
            _out.WriteLine($"was {row.Value}, now {fresh.Value}, moved {moved:0.00}");
            if (string.IsNullOrEmpty(fresh.Value)) Assert.True(moved < 0.5 + 1e-9, $"said nothing but moved {moved:0.00}");
            else Assert.True(Math.Abs(moved - Points(fresh.Value)) <= 0.5, $"said {fresh.Value} but the score moved by {moved:0.00}");
        }

        [Theory]
        [MemberData(nameof(Shapes))]
        public async Task Working_out_the_worth_writes_nothing_to_the_model(Shape shape)
        {
            using var f = await ModelAsync(shape);
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            var json = await JsonAsync(f);
            var revision = f.Sessions.Current.Revision;
            await f.Engine.GetModelPlanAsync();
            await f.Engine.RowWorthForTest;
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Contains(JevRows(plan), r => !string.IsNullOrEmpty(r.Value));   // the rehearsal really ran
            Assert.Equal(revision, f.Sessions.Current.Revision);
            Assert.Equal(json, await JsonAsync(f));
        }
    }
}
