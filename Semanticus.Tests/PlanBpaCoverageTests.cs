// "Reach best-practice A from a SQL source with the Plan ticking off best practice" (Kane, plan statement check 2, item 3).
// Each test was red before the change. The Plan must carry every best-practice warning a person or assistant can act on:
//   (a) a visible numeric or date column with no format string (SEM_COLUMN_NEEDS_FORMAT_STRING) that no readiness rule asks
//       for (a key, a relationship end) goes to the words queue and the words row, for the assistant to choose;
//   (b) the inactive-relationship and snowflake best-practice warnings fold into "Sort out N relationships": accepting the
//       AI readiness finding on that row accepts its best-practice twin, and a best-practice-only one is a line on the row.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanBpaCoverageTests
    {
        private const string Why = "Kept off on purpose; a measure uses it.";
        private const string ColumnRule = "SEM_COLUMN_NEEDS_FORMAT_STRING";
        private const string InactiveRule = "INACTIVE_RELATIONSHIPS_THAT_ARE_NEVER_ACTIVATED";
        private const string SnowflakeRule = "SNOWFLAKE_SCHEMA_ARCHITECTURE";
        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <summary>A fact and a date table joined on a key: both key columns are visible, have no format, and are
        /// relationship ends, so the readiness column-format rule (FMT-COLUMN) never asks about them but the
        /// best-practice rule does.</summary>
        private static async Task<DecisionFixture> KeyedModelAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Cov", 1604);
            await e.CreateTableAsync("dim_date", "human");
            await e.CreateColumnAsync("table:dim_date", "DATE_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:dim_date", "CAL_DT", "DateTime", null, "human");
            await e.CreateTableAsync("fact", "human");
            await e.CreateColumnAsync("table:fact", "DATE_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:fact", "AMT", "Double", null, "human");
            await e.CreateRelationshipAsync("column:fact/DATE_KEY", "column:dim_date/DATE_KEY", null, true, "human");
            return f;
        }

        // ---- (a) column formats ---------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_key_column_with_no_format_is_in_the_words_queue_and_counted_on_the_words_row()
        {
            using var f = await KeyedModelAsync();
            var e = f.Engine;
            Assert.Contains((await e.BpaScanAsync()).Violations, v => v.RuleId == ColumnRule && v.ObjectRef == "column:fact/DATE_KEY" && !v.Waived);

            var queue = await e.GetWordsQueueAsync();
            var item = Assert.Single(queue.Items, i => i.ObjectRef == "column:fact/DATE_KEY" && i.Kind == "format");
            Assert.Equal("edit_objects", item.Tool);
            Assert.Equal(ColumnRule, item.RuleId);
            Assert.Contains("FormatString", item.Message);
            Assert.Contains(queue.Items, i => i.ObjectRef == "column:dim_date/DATE_KEY" && i.Kind == "format");

            var words = (await e.GetModelPlanAsync()).Items.Single(i => i.Id == LocalEngine.WordsItemId);
            Assert.Contains(words.Diff, l => l.Contains("DATE_KEY") && l.Contains("a format to choose"));

            // The assistant sets it, and both the queue and the warning are gone.
            await e.SetObjectPropertyAsync("column:fact/DATE_KEY", "FormatString", "0", "agent");
            Assert.DoesNotContain((await e.GetWordsQueueAsync()).Items, i => i.ObjectRef == "column:fact/DATE_KEY" && i.Kind == "format");
            Assert.DoesNotContain((await e.BpaScanAsync()).Violations, v => v.RuleId == ColumnRule && v.ObjectRef == "column:fact/DATE_KEY");
        }

        [Fact]
        public async Task A_column_the_formats_row_already_asks_about_is_not_asked_a_second_time()
        {
            using var f = await KeyedModelAsync();
            var e = f.Engine;
            // CAL_DT is a plain visible date: FMT-COLUMN (the formats row, or Jev's column-format row) owns it.
            Assert.Contains((await e.AiReadinessScanAsync()).Findings, x => x.RuleId == "FMT-COLUMN" && x.ObjectRef == "column:dim_date/CAL_DT" && !x.Waived);
            Assert.Contains((await e.BpaScanAsync()).Violations, v => v.RuleId == ColumnRule && v.ObjectRef == "column:dim_date/CAL_DT");
            var queue = await e.GetWordsQueueAsync();
            Assert.DoesNotContain(queue.Items, i => i.ObjectRef == "column:dim_date/CAL_DT" && i.Kind == "format");
            Assert.DoesNotContain(queue.Items, i => i.ObjectRef == "column:fact/AMT" && i.Kind == "format");
        }

        [Fact]
        public void The_words_row_stays_while_only_a_column_format_is_left()
        {
            // Every readiness word is written; one key column still has no format. The row must not vanish while the
            // best-practice warning stands, or the Plan runs out with Best practice still C.
            var left = new[] { new WordsQueueItem { Kind = "format", Tool = "set_property", ObjectRef = "column:fact/DATE_KEY", ObjectName = "DATE_KEY" } };
            var row = LocalEngine.WordsItemForTest(Array.Empty<ReadinessFinding>(), 2, left);
            Assert.NotNull(row);
            Assert.Equal("Choose formats for the model", row.Title);
            Assert.Equal("a format for 1 column", row.Sub);
            Assert.Equal("1 object", row.Touches);
            // Nothing left, or a model with no tables: no row.
            Assert.Null(LocalEngine.WordsItemForTest(Array.Empty<ReadinessFinding>(), 2, Array.Empty<WordsQueueItem>()));
            Assert.Null(LocalEngine.WordsItemForTest(Array.Empty<ReadinessFinding>(), 0, left));
        }

        /// <summary>Stands in for the person's assistant on one queue item, with the least that clears its finding.</summary>
        private static async Task DoAsync(LocalEngine e, WordsQueueItem i)
        {
            switch (i.Kind)
            {
                case "describe": await e.SetDescriptionAsync(i.ObjectRef, "Written by the assistant.", "agent"); break;
                case "format" when i.ObjectRef.StartsWith("measure:", StringComparison.Ordinal): await e.SetMeasureFormatAsync(i.ObjectRef, "#,0.00", "agent"); break;
                case "format": await e.SetObjectPropertyAsync(i.ObjectRef, "FormatString", i.ObjectRef.Contains("CAL_DT") ? "yyyy-mm-dd" : "0", "agent"); break;
            }
        }

        // ---- (b) relationships ----------------------------------------------------------------------------------

        /// <summary>Two links from the fact to the date table, the second kept off: REL-INACTIVE and the best-practice twin.</summary>
        private static async Task<DecisionFixture> InactiveModelAsync()
        {
            var f = await KeyedModelAsync();
            var e = f.Engine;
            await e.CreateColumnAsync("table:fact", "DUE_KEY", "Int64", null, "human");
            await e.CreateRelationshipAsync("column:fact/DUE_KEY", "column:dim_date/DATE_KEY", null, false, "human");
            return f;
        }

        /// <summary>fact many-to-one mid, mid many-to-one dim: mid is a snowflake hop for both rules.</summary>
        private static async Task<DecisionFixture> SnowflakeModelAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Snow", 1604);
            foreach (var t in new[] { "fact", "mid", "dim" })
            {
                await e.CreateTableAsync(t, "human");
                await e.CreateColumnAsync("table:" + t, "ID", "Int64", null, "human");
            }
            await e.CreateColumnAsync("table:fact", "MID_ID", "Int64", null, "human");
            await e.CreateColumnAsync("table:mid", "DIM_ID", "Int64", null, "human");
            await e.CreateRelationshipAsync("column:fact/MID_ID", "column:mid/ID", null, true, "human");
            await e.CreateRelationshipAsync("column:mid/DIM_ID", "column:dim/ID", null, true, "human");
            return f;
        }

        private static async Task<BpaViolation> Bpa(LocalEngine e, string rule) =>
            Assert.Single((await e.BpaScanAsync()).Violations, v => v.RuleId == rule);

        private static async Task<ReadinessFinding> Air(LocalEngine e, string rule) =>
            Assert.Single((await e.AiReadinessScanAsync()).Findings, x => x.RuleId == rule);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Accepting_an_inactive_relationship_on_AI_readiness_accepts_its_best_practice_twin_and_back()
        {
            using var f = await InactiveModelAsync();
            var e = f.Engine;
            var rel = (await Air(e, "REL-INACTIVE")).ObjectRef;
            Assert.Equal(rel, (await Bpa(e, InactiveRule)).ObjectRef);   // the twin is the same relationship
            Assert.False((await Bpa(e, InactiveRule)).Waived);

            await e.WaiveFindingAsync("air", "REL-INACTIVE", rel, Why, "human");
            var twin = await Bpa(e, InactiveRule);
            Assert.True(twin.IsAccepted(), "one acceptance should close both");
            Assert.Equal(Why, twin.WaiverReason);
            Assert.Single(await e.ListWaiversAsync());                    // one stored decision, read by both pages

            await e.UnwaiveFindingAsync("air", "REL-INACTIVE", rel, "human");
            Assert.False((await Bpa(e, InactiveRule)).Waived);
            Assert.False((await Air(e, "REL-INACTIVE")).Waived);

            // And from Model quality: accepting the best-practice warning accepts the readiness finding.
            await e.WaiveFindingAsync("bpa", InactiveRule, rel, Why, "human");
            Assert.True((await Air(e, "REL-INACTIVE")).IsAccepted());
            Assert.Single(await e.ListWaiversAsync());
        }

        [Fact]
        public async Task Accepting_a_snowflake_hop_on_AI_readiness_accepts_its_best_practice_twin_and_back()
        {
            using var f = await SnowflakeModelAsync();
            var e = f.Engine;
            var table = (await Air(e, "REL-SNOWFLAKE")).ObjectRef;
            Assert.Equal(table, (await Bpa(e, SnowflakeRule)).ObjectRef);

            await e.WaiveFindingAsync("air", "REL-SNOWFLAKE", table, Why, "human");
            Assert.True((await Bpa(e, SnowflakeRule)).IsAccepted());
            await e.UnwaiveFindingAsync("air", "REL-SNOWFLAKE", table, "human");
            Assert.False((await Bpa(e, SnowflakeRule)).Waived);

            await e.WaiveFindingAsync("bpa", SnowflakeRule, table, Why, "human");
            Assert.True((await Air(e, "REL-SNOWFLAKE")).IsAccepted());
        }

        [Fact]
        public async Task An_acceptance_on_one_relationship_never_accepts_a_different_one()
        {
            using var f = await InactiveModelAsync();
            var e = f.Engine;
            await e.CreateColumnAsync("table:fact", "PAID_KEY", "Int64", null, "human");
            await e.CreateRelationshipAsync("column:fact/PAID_KEY", "column:dim_date/DATE_KEY", null, false, "human");
            var refs = (await e.AiReadinessScanAsync()).Findings.Where(x => x.RuleId == "REL-INACTIVE").Select(x => x.ObjectRef).ToArray();
            Assert.Equal(2, refs.Length);

            await e.WaiveFindingAsync("air", "REL-INACTIVE", refs[0], Why, "human");
            var twins = (await e.BpaScanAsync()).Violations.Where(v => v.RuleId == InactiveRule).ToArray();
            Assert.Equal(2, twins.Length);
            Assert.True(twins.Single(v => v.ObjectRef == refs[0]).IsAccepted());
            Assert.False(twins.Single(v => v.ObjectRef == refs[1]).Waived);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_snowflake_the_readiness_rule_does_not_see_is_a_line_on_the_relationships_row()
        {
            using var f = await SnowflakeModelAsync();
            var e = f.Engine;
            // The second hop is one-to-one: REL-SNOWFLAKE counts many-to-one chains only, the best-practice rule counts any.
            await e.SetObjectPropertyAsync("relationship:'mid'[DIM_ID] --> 'dim'[ID]", "FromCardinality", "One", "human");
            Assert.DoesNotContain((await e.AiReadinessScanAsync()).Findings, x => x.RuleId == "REL-SNOWFLAKE");
            var bpa = await Bpa(e, SnowflakeRule);
            Assert.False(bpa.Waived);

            var row = Assert.Single((await e.GetModelPlanAsync()).Items, i => i.Id == "proposal/relationships");
            Assert.Contains(bpa.ObjectRef, row.Objects);
            Assert.Equal("Fix 1 relationship with a model warning", row.Title);

            // One acceptance, on Model quality (the finding the readiness page does not have), closes the row.
            await e.WaiveFindingAsync("bpa", SnowflakeRule, bpa.ObjectRef, Why, "human");
            Assert.DoesNotContain((await e.GetModelPlanAsync()).Items, i => i.Id == "proposal/relationships" && i.Objects.Contains(bpa.ObjectRef));
        }

        [Fact]
        public async Task An_acceptance_stores_the_twin_that_was_firing_and_only_that_one_closes_the_other_page()
        {
            using var f = await InactiveModelAsync();
            var e = f.Engine;
            var rel = Assert.Single((await e.AiReadinessScanAsync()).Findings, x => x.RuleId == "REL-INACTIVE").ObjectRef;
            await e.WaiveFindingAsync("air", "REL-INACTIVE", rel, Why, "human");
            Assert.Equal(InactiveRule, Assert.Single(await e.ListWaiversAsync()).PairedWith);   // the twin was firing: both pages were decided

            // The same record without the pair (the twin was not firing when it was written) closes nothing on the other page.
            var recs = (await e.ListWaiversAsync()).ToList();
            recs[0].PairedWith = null;
            Assert.Null(PairedAcceptances.TwinAcceptance(recs, "bpa", InactiveRule, rel));
            recs[0].PairedWith = InactiveRule;
            Assert.NotNull(PairedAcceptances.TwinAcceptance(recs, "bpa", InactiveRule, rel));
            Assert.Null(PairedAcceptances.TwinAcceptance(recs, "bpa", SnowflakeRule, rel));      // a pair names one rule, not the family
        }

        [Fact]
        public async Task A_snowflake_acceptance_pairs_the_same_way()
        {
            using var f = await SnowflakeModelAsync();
            var e = f.Engine;
            var air = (await e.AiReadinessScanAsync()).Findings.First(x => x.RuleId == "REL-SNOWFLAKE");
            Assert.Contains((await e.BpaScanAsync()).Violations, v => v.RuleId == SnowflakeRule && v.ObjectRef == air.ObjectRef);
            await e.WaiveFindingAsync("air", "REL-SNOWFLAKE", air.ObjectRef, Why, "human");
            Assert.Equal(SnowflakeRule, Assert.Single(await e.ListWaiversAsync()).PairedWith);
            Assert.True((await e.BpaScanAsync()).Violations.Single(v => v.RuleId == SnowflakeRule && v.ObjectRef == air.ObjectRef).IsAccepted());

            // An acceptance written where the twin is NOT firing stores no pair (a one-to-one second hop: only the best-practice rule sees it).
            await e.UnwaiveFindingAsync("air", "REL-SNOWFLAKE", air.ObjectRef, "human");
            await e.SetObjectPropertyAsync("relationship:'mid'[DIM_ID] --> 'dim'[ID]", "FromCardinality", "One", "human");
            var bpa = await Bpa(e, SnowflakeRule);
            await e.WaiveFindingAsync("bpa", SnowflakeRule, bpa.ObjectRef, Why, "human");
            Assert.All(await e.ListWaiversAsync(), w => Assert.Null(w.PairedWith));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_relationships_row_with_a_line_only_Model_quality_raises_opens_Model_quality()
        {
            using var f = await SnowflakeModelAsync();
            var e = f.Engine;
            Assert.Equal("diagram", Assert.Single((await e.GetModelPlanAsync()).Items, i => i.Id == "proposal/relationships").Jump.Tool);   // AI readiness's own relationship findings keep the Diagram
            await e.SetObjectPropertyAsync("relationship:'mid'[DIM_ID] --> 'dim'[ID]", "FromCardinality", "One", "human");
            Assert.DoesNotContain((await e.AiReadinessScanAsync()).Findings, x => x.RuleId == "REL-SNOWFLAKE");
            var jump = Assert.Single((await e.GetModelPlanAsync()).Items, i => i.Id == "proposal/relationships").Jump;
            Assert.Equal("bpa", jump.Tool);
            Assert.Equal("Model quality", jump.Label);
        }

        // ---- the J8 proof ---------------------------------------------------------------------------------------

        /// <summary>Build a model from a SQL source (the J8 schema through the product's own mapping), press Run the plan until it
        /// has nothing left to run, then let a stand-in for the assistant do the words row. After that, every error or warning
        /// Model quality still lists must be one a Plan row asked for, and accepting the relationships row's findings must
        /// leave Best practice at A (no open error, no open warning).</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task From_a_SQL_source_no_best_practice_warning_is_left_that_no_Plan_row_asked_for()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            var spec = LocalEngine.FabricSchemaToSpec(SqlDraftBuildTests.J8Schema(), "contoso-sql.example.com", "ContosoConsulting", "import");
            await e.CreateModelAsync("ContosoConsulting", 1604);
            await e.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            Assert.Empty((await e.BuildModelFromSpecAsync("human")).Errors);
            await f.SaveAsAsync("J8.bim");
            f.UseNull();

            // The button, until it has nothing left to run.
            for (var press = 0; press < 14; press++)
            {
                var plan = await e.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.StateWord == PlanStateWords.Ready && i.Doer == PlanDoers.Semanticus)) break;
                var ran = await e.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                if (!ran.Performed) break;
            }

            // Is a warning asked for by a Plan row? Strict for the two kinds of warning this lane is about, so a row that merely touches
            // the object for another reason never counts: a column's format by the words queue or the formats row (FMT-COLUMN), a
            // relationship's by the "Sort out N relationships" row; any other rule by anything the plan or the queue names.
            async Task<string[]> Unasked()
            {
                var queue = await e.GetWordsQueueAsync();
                var plan = await e.GetModelPlanAsync();
                var formatAsked = new HashSet<string>(queue.Items.Where(i => i.Kind == "format").Select(i => i.ObjectRef ?? ""), StringComparer.OrdinalIgnoreCase);
                foreach (var x in (await e.AiReadinessScanAsync()).Findings.Where(x => x.RuleId == "FMT-COLUMN" && !x.Waived)) formatAsked.Add(x.ObjectRef);
                var relRow = plan.Items.Where(i => (i.Id == "proposal/relationships" || i.OriginLink?.Ref == "proposal/relationships") && PlanStateWords.IsOpen(i.StateWord))   // a press hands the row to the assistant as a linked task (002e790f)
                    .SelectMany(i => i.Objects ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var other = new HashSet<string>(queue.Items.Select(i => i.ObjectRef ?? ""), StringComparer.OrdinalIgnoreCase);
                foreach (var r in plan.Items.Where(i => PlanStateWords.IsOpen(i.StateWord)))
                {
                    foreach (var o in r.Objects ?? Array.Empty<string>()) other.Add(o);
                    foreach (var k in r.Writes ?? Array.Empty<string>()) other.Add(k.Split('|')[0]);
                }
                bool Asked(BpaViolation v) =>
                    v.RuleId == ColumnRule ? formatAsked.Contains(v.ObjectRef ?? "")
                    : v.RuleId == InactiveRule || v.RuleId == SnowflakeRule ? relRow.Contains(v.ObjectRef ?? "")
                    : other.Contains(v.ObjectRef ?? "");
                return (await e.BpaScanAsync()).Violations
                    .Where(v => v.Severity >= 2 && !v.IsAccepted() && !Asked(v))
                    .Select(v => v.RuleId + " " + v.ObjectRef).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            }
            Assert.Empty(await Unasked());

            // The words row: the assistant writes what each item asks (here only what clears a best-practice finding).
            foreach (var i in (await e.GetWordsQueueAsync(0)).Items.Where(i => i.Kind == "format").ToArray()) await DoAsync(e, i);
            Assert.Empty(await Unasked());

            // The formats row (Jev, or the person): the readiness column-format findings no one has answered yet.
            foreach (var x in (await e.AiReadinessScanAsync()).Findings.Where(x => x.RuleId == "FMT-COLUMN" && !x.Waived).ToArray())
                await e.SetObjectPropertyAsync(x.ObjectRef, "FormatString", "0.00", "human");

            // What is left is the relationships row: accept each finding it names, with a reason (system air, as the row's page does).
            var left = (await e.BpaScanAsync()).Violations.Where(v => v.Severity >= 2 && !v.IsAccepted()).ToArray();
            Assert.All(left, v => Assert.True(v.RuleId == InactiveRule || v.RuleId == SnowflakeRule, v.RuleId + " " + v.ObjectRef));
            var row = Assert.Single((await e.GetModelPlanAsync()).Items, i => i.Id == "proposal/relationships" || i.OriginLink?.Ref == "proposal/relationships");
            foreach (var air in (await e.AiReadinessScanAsync()).Findings.Where(x => x.RuleId.StartsWith("REL-") && !x.Waived).ToArray())
                await e.WaiveFindingAsync("air", air.RuleId, air.ObjectRef, Why, "human");
            Assert.Empty((await e.BpaScanAsync()).Violations.Where(v => v.Severity >= 2 && !v.IsAccepted()).Select(v => v.RuleId + " " + v.ObjectRef));
            Assert.Equal("A", (await e.BpaScanAsync()).BestPractice);
            Assert.NotNull(row);
        }
    }
}
