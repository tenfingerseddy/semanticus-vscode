// The base measure family, column.summarize (T268, M4 columns; Kane's redesign of 26 September). Kane: "Jev should set
// summarisation of columns off as a best practice. It should suggest an explicit base measure aggregation instead."
//
// WHAT IS PROVED HERE, AND HOW.
//   Free: no row, nothing asked, no run started, nothing measured.
//   Pro: one row per table. Every number column the family hears back on is set not to add up (a good-practice line,
//   rule provenance), and the aggregation it decides (Jev's sure pick, else the principled rule) is an explicit measure
//   written by the engine from a fixed template: its DAX, its format and a plain description. "none" is no measure.
//   A measure the model already has, under any name, is never proposed again, and a new one takes the model's naming.
//   Approve sets the columns and creates the kept measures as one undo step.
//   What goes to Jev: the four declared fields, never a column's own summarize setting or any value.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ColumnSummarizeFamilyTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public ColumnSummarizeFamilyTests(ModelTemplates templates) => _templates = templates;

        private const string Point = "column.summarize";

        /// <summary>Sales holds Amount, Quantity, Unit Price, Fiscal Year, Order Number, Month Number, a CustomerKey on
        /// the many side of a relationship, and Note (text, never asked).</summary>
        private async Task<DecisionFixture> ModelAsync(string file, bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await _templates.OpenCopyAsync(f, "sales", b => BuildModelAsync(b), file);
            f.UseNull();
            return f;
        }

        private static async Task BuildModelAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Unit Price", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Fiscal Year", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Order Number", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Month Number", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Note", "String", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, null, "human");
        }

        private sealed class CapturingOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            public CapturingOracle(IDecisionOracle inner) { _inner = inner; }
            public bool Available => true;
            public IReadOnlyList<DecisionRequest> Seen { get { lock (_seen) return _seen.ToList(); } }
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (_seen) _seen.Add(request);
                return _inner.AskAsync(request, ct);
            }
        }

        private static async Task<Dictionary<string, string>> DigestsAsync(DecisionFixture f)
        {
            var point = DecisionRegistry.Require(Point);
            var prepared = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null));
            return prepared.Asks.ToDictionary(
                a => a.TableName + "/" + a.ColumnName,
                a => a.Ref,   // the family asks a table's columns at once (m11/jev-batch): an entry is keyed by the column's ref
                StringComparer.Ordinal);
        }

        private static string Fixture(IEnumerable<(string Digest, string Choice, double Confidence)> rows)
        {
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            var first = true;
            foreach (var (digest, choice, confidence) in rows)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"digest\":").Append(JsonSerializer.Serialize(digest))
                  .Append(",\"point\":\"").Append(Point).Append('"')
                  .Append(",\"usage\":{\"input_tokens\":500,\"output_tokens\":20}")
                  .Append(",\"answers\":{\"summarize_by\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                  .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append("}}}");
            }
            return sb.Append("]}").ToString();
        }

        /// <summary>Unit Price average (sure), Fiscal Year none (sure), CustomerKey none (sure), Amount sum (sure,
        /// the setting it already has), Order Number sum (sure, the setting it already has), Quantity sum (unsure),
        /// Month Number none (unsure), Customer/CustomerKey none (unsure).
        /// <paramref name="renamedSales"/> answers the Sales columns again under that table name too, as Jev would after a rename.</summary>
        private static async Task<CapturingOracle> AnswerAsync(DecisionFixture f, string renamedSales = null)
        {
            await f.ConsentAsync();
            var d = await DigestsAsync(f);
            var rows = new[]
            {
                (d["Sales/Unit Price"], "average", 0.93),
                (d["Sales/Fiscal Year"], "none", 0.9),
                (d["Sales/CustomerKey"], "none", 0.97),
                (d["Sales/Amount"], "sum", 0.95),
                (d["Sales/Order Number"], "sum", 0.9),
                (d["Sales/Quantity"], "sum", 0.5),
                (d["Sales/Month Number"], "none", 0.6),
                (d["Customer/CustomerKey"], "none", 0.6),
            };
            if (renamedSales != null)
                rows = rows.Concat(rows.Where(r => r.Item1.StartsWith("column:Sales/", StringComparison.Ordinal))
                                       .Select(r => ("column:" + renamedSales + "/" + r.Item1.Substring("column:Sales/".Length), r.Item2, r.Item3))).ToArray();
            var recorded = new ByObjectRecordedOracle(Fixture(rows), "jev-test-1");
            var capturing = new CapturingOracle(recorded);
            f.Engine.UseDecisionsForTest(new DecisionService(capturing, new DecisionBudget()));
            return capturing;
        }

        private static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return await f.Engine.GetModelPlanAsync();
        }

        private static async Task<string> SummarizeOf(DecisionFixture f, string columnRef) =>
            (await f.Engine.GetObjectAsync(columnRef)).Properties["summarizeBy"]?.ToString() ?? "";

        /// <summary>The summarize rules' own lines, as proposing their Plan row shows them.</summary>
        private static async Task<string[]> RuleSummarizeTargetsAsync(DecisionFixture f)
        {
            var plan = await f.Engine.GetModelPlanAsync();
            var row = plan.Items.SingleOrDefault(i => i.Id == "safefix/Formatting");
            if (row == null) return Array.Empty<string>();
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human", null);
            Assert.True(proposed.Plan != null, proposed.Sentence);
            // No change set means the row had nothing left to propose for this model.
            return (proposed.Plan.Items.SingleOrDefault(i => i.Id == row.Id)?.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>())
                .Where(l => l.Property == "summarizeBy").Select(l => l.Target).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        }

        private static async Task<MeasureRow> MeasureAsync(DecisionFixture f, string name) =>
            (await f.Engine.ListMeasuresAsync()).SingleOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));

        private static ModelPlanChangeLine[] Lines(ModelPlanItem row, string kind) =>
            row.ChangeSet.Lines.Where(l => l.Op == kind).ToArray();

        [Fact]
        public void The_point_is_registered_for_Pro_only_with_the_replay_options()
        {
            var point = DecisionRegistry.Require(Point);
            Assert.True(point.ProOnly);
            Assert.Equal(0.85, point.ConfidenceBar);
            Assert.Equal(new[] { "sum", "none", "average", "count", "distinctcount", "min", "max" },
                         point.Questions.Single().FixedOptions.ToArray());
        }

        [Fact]
        public async Task A_free_session_asks_nothing_and_has_no_summarize_item()
        {
            using var f = await ModelAsync("ColSumFree.bim", pro: false);
            await f.ConsentAsync();
            var oracle = f.UseRecorded(Fixture(new[] { (new string('0', 64), "none", 0.99) }));

            var plan = await f.Engine.GetModelPlanAsync();
            await f.Engine.SetDescriptionAsync("table:Sales", "What we sold.", "human");
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();

            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith("jev.column-summarize.", StringComparison.Ordinal));
            Assert.Equal(0, oracle.AskCount);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
            Assert.Equal(0, f.Engine.JevRunsStartedForTest);
        }

        [Fact]
        public async Task Pro_sets_every_number_column_not_to_add_up_and_adds_the_measure_Jev_picked()
        {
            using var f = await ModelAsync("ColSumPro.bim");
            await AnswerAsync(f);

            var plan = await SettledPlanAsync(f);
            var sales = Assert.Single(plan.Items, i => i.Id == "jev.column-summarize.table:Sales");
            Assert.Equal("Add 4 measures for the numbers in Sales", sales.Title);   // three sure picks and Quantity by the rule
            Assert.Equal(ModelPlanOwners.Jev, sales.Who);

            // Every number column goes to None, as good practice: a rule line, not anybody's pick.
            var off = Lines(sales, "set_summarize_by");
            Assert.Equal(new[] { "Amount", "CustomerKey", "Fiscal Year", "Month Number", "Order Number", "Quantity", "Unit Price" },
                         off.Select(l => l.Target.Substring("column:Sales/".Length)).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.All(off, l => { Assert.Equal("None", l.After); Assert.Equal(ModelPlanChangeKinds.Rule, l.Provenance.Kind); });

            // Jev's sure average is a measure the engine wrote: its DAX, its format and a plain description.
            var avg = sales.ChangeSet.Lines.Where(l => l.Target == "measure:Sales/Average Unit Price").ToArray();
            Assert.Equal("AVERAGE ( 'Sales'[Unit Price] )", avg.Single(l => l.Op == "create_measure").After);
            Assert.Equal("#,0.0", avg.Single(l => l.Op == "set_measure_format").After);
            Assert.Equal("The average Unit Price in Sales.", avg.Single(l => l.Op == "set_description").After);
            Assert.All(avg, l => { Assert.Equal(ModelPlanChangeKinds.Decision, l.Provenance.Kind); Assert.Equal("sure", l.Provenance.ConfidenceWord); });
            Assert.Contains(sales.ChangeSet.Lines, l => l.Target == "measure:Sales/Total Amount" && l.Op == "create_measure");
            // "none" is no measure: a key, a year.
            Assert.DoesNotContain(sales.ChangeSet.Lines, l => l.Op == "create_measure" && (l.Target.Contains("CustomerKey") || l.Target.Contains("Fiscal Year")));
            foreach (var line in sales.ChangeSet.Lines)
            {
                Assert.False(string.IsNullOrWhiteSpace(line.Reason));
                Assert.True(line.Check.Exists);
                Assert.DoesNotContain("\u2014", line.Reason, StringComparison.Ordinal);
            }
            // The row says who chose: Jev chose most measures, the rule one, and the None lines are neither.
            Assert.Contains(sales.Why, w => w.StartsWith("Jev chose 3 of these measures", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Approve_sets_the_columns_and_adds_the_measures_as_one_undo_step()
        {
            using var f = await ModelAsync("ColSumApprove.bim");
            await AnswerAsync(f);
            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-summarize.table:Sales");

            var result = await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest);

            Assert.True(result.Performed, result.Sentence);
            Assert.Equal("None", await SummarizeOf(f, "column:Sales/Unit Price"));
            Assert.Equal("None", await SummarizeOf(f, "column:Sales/Amount"));
            // Its measure does the adding now, so the column is hidden, as BPA's "Hide fact table columns" asks.
            Assert.Equal("True", (await f.Engine.GetObjectAsync("column:Sales/Unit Price")).Properties["isHidden"]?.ToString(), ignoreCase: true);
            var avg = await MeasureAsync(f, "Average Unit Price");
            Assert.NotNull(avg);
            Assert.Equal("AVERAGE ( 'Sales'[Unit Price] )", avg.Expression);
            Assert.Equal("#,0.0", avg.FormatString);
            Assert.Equal("The average Unit Price in Sales.", avg.Description);
            Assert.NotNull(await MeasureAsync(f, "Total Quantity"));

            var beforeRefresh = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(beforeRefresh.Items, i => i.Id == sales.Id);
            var refreshed = await f.Engine.RefreshPlanAsync();
            Assert.True(refreshed.Cleared > beforeRefresh.Items.Count(i => i.Status == ModelPlanStatuses.Done));
            Assert.Contains(refreshed.Plan.History.Last(h => h.Sentence?.StartsWith("Refreshed the plan:") == true).Detail, t => t == sales.Title);
            Assert.Equal("Nothing new.", (await f.Engine.RefreshPlanAsync()).Sentence);

            await f.Engine.UndoAsync("human");
            Assert.Equal("Default", await SummarizeOf(f, "column:Sales/Unit Price"));
            Assert.Equal("Default", await SummarizeOf(f, "column:Sales/Amount"));
            Assert.Equal("False", (await f.Engine.GetObjectAsync("column:Sales/Unit Price")).Properties["isHidden"]?.ToString(), ignoreCase: true);
            Assert.Null(await MeasureAsync(f, "Average Unit Price"));
            Assert.Null(await MeasureAsync(f, "Total Quantity"));
        }

        [Fact]
        public async Task Below_the_bar_the_principled_rule_chooses_the_measure_and_says_so()
        {
            using var f = await ModelAsync("ColSumPrincipled.bim");
            await AnswerAsync(f);
            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-summarize.table:Sales");

            // Quantity: Jev unsure, the principled rule says a quantity adds up, as a rule line.
            var quantity = sales.ChangeSet.Lines.Single(l => l.Target == "measure:Sales/Total Quantity" && l.Op == "create_measure");
            Assert.Equal(ModelPlanChangeKinds.Rule, quantity.Provenance.Kind);
            Assert.Equal("SUM ( 'Sales'[Quantity] )", quantity.After);
            // Month Number: Jev unsure, the rule says a period number gets no measure; the column still stops adding up.
            Assert.DoesNotContain(sales.ChangeSet.Lines, l => l.Op == "create_measure" && l.Target.Contains("Month Number"));
            Assert.Single(sales.ChangeSet.Lines, l => l.Target == "column:Sales/Month Number");
            // The summarize rules leave the columns the family decided to its row.
            Assert.DoesNotContain("column:Sales/Month Number", await RuleSummarizeTargetsAsync(f));
        }

        [Fact]
        public async Task A_measure_the_model_already_has_is_never_proposed_again_and_new_ones_take_its_naming()
        {
            using var f = await ModelAsync("ColSumExisting.bim");
            // The model already adds up Amount, under its own name and its own spelling of the DAX.
            await f.Engine.CreateMeasureAsync("table:Sales", "Amount Sum", "sum( Sales [Amount] )", "human");
            await AnswerAsync(f);
            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-summarize.table:Sales");

            Assert.DoesNotContain(sales.ChangeSet.Lines, l => l.Op == "create_measure" && l.After.StartsWith("SUM ( 'Sales'[Amount]", StringComparison.Ordinal));
            Assert.Contains(sales.ChangeSet.Refused ?? Array.Empty<ModelPlanChangeRefusal>(),
                r => r.Target == "column:Sales/Amount" && r.Reason.StartsWith("Amount Sum already gives the total of Amount", StringComparison.Ordinal));
            // The model names a total "<column> Sum", so the new totals do too.
            Assert.Contains(sales.ChangeSet.Lines, l => l.Op == "create_measure" && l.Target == "measure:Sales/Quantity Sum");
            Assert.Contains(sales.ChangeSet.Lines, l => l.Op == "create_measure" && l.Target == "measure:Sales/Order Number Sum");
            // Averages have no pattern in this model, so they keep the engine's name.
            Assert.Contains(sales.ChangeSet.Lines, l => l.Op == "create_measure" && l.Target == "measure:Sales/Average Unit Price");
        }

        // hero15 H13 (hero14 D12): the assistant set "jev.column-summarize.table:dim_product" aside, the table was renamed to
        // Product, the row's id moved with the table's name, and the skip, kept by the old id, matched nothing: the row came back.
        [Fact]
        public async Task A_row_set_aside_stays_set_aside_when_a_rename_gives_it_a_new_id()
        {
            using var f = await ModelAsync("ColSumSkipRename.bim");
            await AnswerAsync(f, renamedSales: "Sales Facts");
            Assert.Contains((await SettledPlanAsync(f)).Items, i => i.Id == "jev.column-summarize.table:Sales");
            await f.Engine.SkipModelPlanItemAsync("jev.column-summarize.table:Sales", "a sum here means nothing", "agent");

            await f.Engine.RenameObjectAsync("table:Sales", "Sales Facts", "agent");

            var after = await SettledPlanAsync(f);
            var moved = Assert.Single(after.Items, i => i.Id == "jev.column-summarize.table:Sales Facts");
            Assert.Equal(ModelPlanStatuses.Skipped, moved.Status);
            Assert.Equal("a sum here means nothing", moved.SkipReason);

            // It can be put back by the id it has now.
            var back = await f.Engine.UnskipModelPlanItemAsync("jev.column-summarize.table:Sales Facts", "agent");
            Assert.NotEqual(ModelPlanStatuses.Skipped, back.Items.Single(i => i.Id == "jev.column-summarize.table:Sales Facts").Status);
        }

        [Fact]
        public async Task The_point_switches_off_on_its_own()
        {
            using var f = await ModelAsync("ColSumSwitch.bim");
            await AnswerAsync(f);
            Assert.Contains((await SettledPlanAsync(f)).Items, i => i.Id == "jev.column-summarize.table:Sales");
            await f.Engine.SetDecisionPointEnabledAsync(Point, false, "human");
            Assert.DoesNotContain((await SettledPlanAsync(f)).Items, i => i.Id.StartsWith("jev.column-summarize.", StringComparison.Ordinal));
        }

        [Fact]
        public void Map_turns_one_pick_into_the_column_line_and_the_measure_lines()
        {
            ColumnSummarizeFamily.Ask Ask(string name, string current = "Default") => new ColumnSummarizeFamily.Ask
            {
                Ref = "column:Sales/" + name, ColumnName = name, TableRef = "table:Sales", TableName = "Sales", Current = current,
                NameOf = StarterMeasureTemplates.MeasureOptions.ToDictionary(o => o, o => StarterMeasureTemplates.Name(o, name)),
            };
            List<ChangeItem> Run(ColumnSummarizeFamily.Ask ask, string pick, double conf)
            {
                var lines = new List<ChangeItem>();
                ColumnSummarizeFamily.Map(ask, pick, conf, 0.85, lines, new List<ModelPlanChangeRefusal>());
                return lines;
            }
            var sure = Run(Ask("Rate"), "average", 0.9);
            Assert.Equal(new[] { "set_summarize_by", "create_measure", "set_measure_format", "set_description", "set_column_hidden" }, sure.Select(l => l.Kind).ToArray());
            // A camel-cased column reads in words in the measure's name, and a model that keeps synonyms gets a few.
            var camel = Ask("ListPrice");
            camel.NameOf = StarterMeasureTemplates.MeasureOptions.ToDictionary(o => o, o => new BaseMeasures().NameFor(o, "ListPrice"));
            camel.HasLinguistic = true;
            var words = Run(camel, "average", 0.9);
            Assert.Equal("measure:Sales/Average List Price", words.Single(l => l.Kind == "create_measure").ObjectRef);
            Assert.Equal("avg list price,mean list price", words.Single(l => l.Kind == "set_synonyms").After);
            Assert.Equal("The average List Price in Sales.", words.Single(l => l.Kind == "set_description").After);
            // Each hiding line says why in plain words: the measure now does the adding up.
            Assert.StartsWith("Average Rate now does the adding up for Rate", sure.Single(l => l.Kind == "set_column_hidden").Reason, StringComparison.Ordinal);
            var mdxAsk = Ask("Rate"); mdxAsk.MdxCanGoOff = true;
            Assert.StartsWith("Average Rate now does the adding up for Rate", Run(mdxAsk, "average", 0.9).Last().Reason, StringComparison.Ordinal);
            Assert.Equal("set_is_available_in_mdx", Run(mdxAsk, "average", 0.9).Last().Kind);
            Assert.Equal("None", sure[0].After);
            Assert.Equal(ColumnSummarizeFamily.OffRuleId, sure[0].RuleId);
            Assert.Equal("measure:Sales/Average Rate", sure[1].ObjectRef);
            // Below the bar the principled rule decides: a rate is averaged whatever Jev said, as a rule line.
            var ruled = Run(Ask("Rate"), "sum", 0.84);
            Assert.Equal("AVERAGE ( 'Sales'[Rate] )", ruled.Single(l => l.Kind == "create_measure").After);
            Assert.Null(ruled.Single(l => l.Kind == "create_measure").DecisionConfidence);
            // Already not adding up: no column line, only the measure.
            Assert.DoesNotContain(Run(Ask("Rate", "None"), "average", 0.9), l => l.Kind == "set_summarize_by");
            // "none": the column line and no measure.
            Assert.Equal(new[] { "set_summarize_by" }, Run(Ask("Region Key"), "none", 0.99).Select(l => l.Kind).ToArray());
            Assert.Empty(Run(Ask("Rate"), "median", 0.99));   // never offered
            Assert.True(ColumnSummarizeFamily.Decides(0.85, 0.85));
            Assert.False(ColumnSummarizeFamily.Decides(0.84, 0.85));
        }

        [Fact]
        public async Task Only_the_declared_fields_go_and_never_a_summarize_setting()
        {
            using var f = await ModelAsync("ColSumSent.bim");
            var oracle = await AnswerAsync(f);
            await SettledPlanAsync(f);

            var sent = oracle.Seen.Where(r => r.PointName == Point).ToArray();
            // One request per TABLE (m11/jev-batch): Customer's one number column and Sales' seven, not eight requests.
            Assert.Equal(2, sent.Length);
            var declared = DecisionRegistry.Require(Point).SnapshotFields;
            foreach (var r in sent)
            {
                Assert.Equal(new[] { "columns", "model_tables", "table_columns", "table_facts", "table_name" }, r.Snapshot.Keys.OrderBy(x => x, StringComparer.Ordinal));
                Assert.All(r.Snapshot.Keys, k => Assert.Contains(k, declared));
                var json = JsonSerializer.Serialize(r.Snapshot);
                Assert.DoesNotContain("summarize", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Default", json, StringComparison.Ordinal);
                // The model's tables go ONCE for the request, not inside each column's facts, and one question for each column names its entry.
                Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(json, "\"model_tables\"").Count);
                using var entries = JsonDocument.Parse(JsonSerializer.Serialize(r.Snapshot["columns"]));
                Assert.Equal(entries.RootElement.GetArrayLength(), r.Questions.Count);
                for (var i = 0; i < r.Questions.Count; i++)
                {
                    Assert.StartsWith("For the column `columns[" + i + "]`: " + ColumnSummarizeFamily.Instructions, r.Questions[i].Instructions, StringComparison.Ordinal);
                    Assert.StartsWith("summarize_by:column:", r.Questions[i].Name, StringComparison.Ordinal);
                }
            }
            Assert.DoesNotContain(sent, r =>
            {
                using var entries = JsonDocument.Parse(JsonSerializer.Serialize(r.Snapshot["columns"]));
                return entries.RootElement.EnumerateArray().Any(e => e.GetProperty("name").GetString() == "Note");
            });
        }
    }
}
