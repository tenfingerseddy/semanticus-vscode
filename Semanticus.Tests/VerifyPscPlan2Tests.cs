// Verifier, lane m11/psc-plan fix round 2. Attacks on the stored pair (WaiverRecord.PairedWith) and on a declined column format
// on the words queue. A test here that fails names a defect the verifier found; it is left failing on purpose.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class VerifyPscPlan2Tests
    {
        private readonly ITestOutputHelper _out;
        public VerifyPscPlan2Tests(ITestOutputHelper output) => _out = output;

        private const string Why = "Kept off on purpose; a measure uses it.";
        private const string ColumnRule = "SEM_COLUMN_NEEDS_FORMAT_STRING";
        private const string InactiveRule = "INACTIVE_RELATIONSHIPS_THAT_ARE_NEVER_ACTIVATED";
        private const string UseIt = "CALCULATE ( SUM ( fact[AMT] ), USERELATIONSHIP ( fact[DUE_KEY], dim_date[DATE_KEY] ) )";

        private static async Task<DecisionFixture> InactiveModelAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Cov", 1604);
            await e.CreateTableAsync("dim_date", "human");
            await e.CreateColumnAsync("table:dim_date", "DATE_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:dim_date", "CAL_DT", "DateTime", null, "human");
            await e.CreateTableAsync("fact", "human");
            await e.CreateColumnAsync("table:fact", "DATE_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:fact", "DUE_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:fact", "AMT", "Double", null, "human");
            await e.CreateRelationshipAsync("column:fact/DATE_KEY", "column:dim_date/DATE_KEY", null, true, "human");
            await e.CreateRelationshipAsync("column:fact/DUE_KEY", "column:dim_date/DATE_KEY", null, false, "human");
            return f;
        }

        private static async Task<string> RelAsync(LocalEngine e) =>
            Assert.Single((await e.AiReadinessScanAsync()).Findings, x => x.RuleId == "REL-INACTIVE").ObjectRef;
        private static async Task<bool> BpaAccepted(LocalEngine e, string rel) =>
            (await e.BpaScanAsync()).Violations.Single(v => v.RuleId == InactiveRule && v.ObjectRef == rel).IsAccepted();
        private static async Task<bool> AirAccepted(LocalEngine e, string rel) =>
            (await e.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "REL-INACTIVE" && x.ObjectRef == rel).IsAccepted();

        // Accept on AI readiness with the twin firing: both close; undo opens both; redo closes both; the pair survives a save and
        // a fresh open (an engine restart reads the stored record).
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Paired_with_the_twin_firing_survives_undo_redo_and_a_reopen()
        {
            using var f = await InactiveModelAsync();
            var e = f.Engine;
            var rel = await RelAsync(e);
            await e.WaiveFindingAsync("air", "REL-INACTIVE", rel, Why, "human");
            Assert.True(await BpaAccepted(e, rel));
            await e.UndoAsync("human");
            Assert.False(await BpaAccepted(e, rel));
            Assert.False(await AirAccepted(e, rel));
            await e.RedoAsync("human");
            Assert.True(await BpaAccepted(e, rel));
            Assert.Equal(InactiveRule, Assert.Single(await e.ListWaiversAsync()).PairedWith);

            var path = Path.Combine(f.Workspace, "Pair.bim");
            await e.SaveAsync(path, "bim", false, "human");
            await e.OpenAsync(path, true);
            Assert.Equal(InactiveRule, Assert.Single(await e.ListWaiversAsync()).PairedWith);
            Assert.True(await BpaAccepted(e, rel));
        }

        // The other direction: accept the dead-relationship warning on Model quality; the readiness twin was firing, so it closes too.
        [Fact]
        public async Task Paired_from_Model_quality_closes_the_readiness_twin()
        {
            using var f = await InactiveModelAsync();
            var e = f.Engine;
            var rel = await RelAsync(e);
            await e.WaiveFindingAsync("bpa", InactiveRule, rel, Why, "human");
            Assert.Equal("REL-INACTIVE", Assert.Single(await e.ListWaiversAsync()).PairedWith);
            Assert.True(await AirAccepted(e, rel));
        }

        // Accept with the twin NOT firing (a measure activates the relationship): no pair is stored (the "older record" shape);
        // the twin then appears and is open on its own page; the readiness acceptance still holds.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Unpaired_record_then_the_twin_appears_open()
        {
            using var f = await InactiveModelAsync();
            var e = f.Engine;
            var m = await e.CreateMeasureAsync("table:fact", "Due Amount", UseIt, "human");
            var rel = await RelAsync(e);
            await e.WaiveFindingAsync("air", "REL-INACTIVE", rel, Why, "human");
            Assert.Null(Assert.Single(await e.ListWaiversAsync()).PairedWith);
            await e.DeleteObjectAsync(m, "human");
            Assert.False(await BpaAccepted(e, rel));
            Assert.True(await AirAccepted(e, rel));
            // Undo of the delete: the twin goes away again; redo: back, still open.
            await e.UndoAsync("human");
            Assert.DoesNotContain((await e.BpaScanAsync()).Violations, v => v.RuleId == InactiveRule);
            await e.RedoAsync("human");
            Assert.False(await BpaAccepted(e, rel));
        }

        // Twin firing when accepted, then hidden by a new measure, then back when it is deleted: it was seen and decided, so it
        // reads as accepted again (log only; either reading is defensible, neither is harm).
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Paired_twin_that_goes_away_and_comes_back()
        {
            using var f = await InactiveModelAsync();
            var e = f.Engine;
            var rel = await RelAsync(e);
            await e.WaiveFindingAsync("air", "REL-INACTIVE", rel, Why, "human");
            var m = await e.CreateMeasureAsync("table:fact", "Due Amount", UseIt, "human");
            Assert.DoesNotContain((await e.BpaScanAsync()).Violations, v => v.RuleId == InactiveRule);
            await e.DeleteObjectAsync(m, "human");
            _out.WriteLine("back: accepted=" + await BpaAccepted(e, rel));
        }

        // A rule-level acceptance (every inactive relationship) stores no pair and closes nothing on the other page.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_rule_level_acceptance_pairs_nothing()
        {
            using var f = await InactiveModelAsync();
            var e = f.Engine;
            var rel = await RelAsync(e);
            await e.WaiveFindingAsync("air", "REL-INACTIVE", "*", Why, "human");
            Assert.Null(Assert.Single(await e.ListWaiversAsync()).PairedWith);
            Assert.False(await BpaAccepted(e, rel));
            Assert.True(await AirAccepted(e, rel));
        }

        // Declined formats: a rule-level FMT-COLUMN acceptance (no column takes a format) and a rule-level best-practice acceptance
        // keep every column off the words queue; the assistant's write on such a column is recorded below.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("air", "FMT-COLUMN")]
        [InlineData("bpa", ColumnRule)]
        public async Task A_rule_level_format_decline_keeps_every_column_off_the_words_queue(string system, string rule)
        {
            using var f = await InactiveModelAsync();
            var e = f.Engine;
            Assert.Contains((await e.GetWordsQueueAsync()).Items, i => i.Kind == "format" && i.ObjectRef.StartsWith("column:"));
            await e.WaiveFindingAsync(system, rule, "*", "No column formats in this model.", "human");
            var items = (await e.GetWordsQueueAsync()).Items.Where(i => i.Kind == "format" && i.ObjectRef.StartsWith("column:")).ToArray();
            foreach (var i in items) _out.WriteLine("still queued: " + i.ObjectRef + " " + i.Tool);
            Assert.Empty(items);
            var r = await e.SetObjectPropertyAsync("column:fact/AMT", "FormatString", "0", "agent");
            _out.WriteLine($"agent set_property: changed={r.Changed} warning={r.Warning}");
        }

        // Per-object: the key column (only best practice asks) declined through a per-object acceptance of the BPA rule is not queued.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_key_column_whose_best_practice_format_was_accepted_is_not_queued()
        {
            using var f = await InactiveModelAsync();
            var e = f.Engine;
            var keys = (await e.GetWordsQueueAsync()).Items.Where(i => i.Kind == "format" && i.ObjectRef.StartsWith("column:", StringComparison.Ordinal)).Select(i => i.ObjectRef).ToArray();
            _out.WriteLine("queued columns: " + string.Join(", ", keys));
            if (keys.Length == 0) return;
            await e.WaiveFindingAsync("bpa", ColumnRule, keys[0], "Keys take no format.", "human");
            Assert.DoesNotContain((await e.GetWordsQueueAsync()).Items, i => i.Kind == "format" && i.ObjectRef == keys[0]);
        }
    }
}
