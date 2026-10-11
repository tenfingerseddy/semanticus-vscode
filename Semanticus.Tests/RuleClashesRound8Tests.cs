using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// m11/rule-clashes round 8. Two things are kept apart, always. What no path may WRITE is decided by the declined set (object,
    /// property, and for an own-ask rule the value). What the pages count as accepted, and what the deploy gate lets pass, is decided by
    /// the findings the person accepted, by their own rule id and object. A finding whose change another acceptance covers is left alone
    /// by every path, and it stays open: it is scored, it is counted, it blocks the gate when it is a blocker, and its row says
    /// "You accepted a related finding. Accept this one too, or fix it." The matrix (RuleClashMatrixTests) proves it for every rule;
    /// these cover the pieces it does not read: the words, the score, the per-object identity that survives a rename, and a name with
    /// an edge space.
    /// </summary>
    public sealed class RuleClashesRound8Tests
    {
        private const string Why = "Declined on purpose; reports depend on it as it is.";
        private const string RelatedWords = "You accepted a related finding. Accept this one too, or fix it.";

        private static async Task<DecisionFixture> StarAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Round8", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Order Date", "DateTime", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", "Date", "DateTime", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/Order Date", "column:Date/Date", null, true, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human");
            await f.Sessions.Require().MutateAsync("human", "setup: a name with a trailing space", m => m.Tables["Sales"].Measures["Order Count"].Name = "Order Count ");
            return f;
        }

        private static bool Accepted(BpaViolation v) => v.Waived && !v.WaivedAsTwin;
        private static bool Accepted(ReadinessFinding x) => x.Waived && !x.WaivedAsTwin;
        private static Task<T> Read<T>(DecisionFixture f, Func<Model, T> read) => f.Sessions.Require().ReadAsync(read);

        // ---- a related finding stays open and says so ---------------------------------------------------------------------

        [Fact]
        public void The_words_a_related_finding_carries_are_the_rulings()
        {
            Assert.Equal(RelatedWords, DeclinedChanges.TwinReason);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_related_model_quality_finding_is_counted_and_scored_as_open_while_every_path_leaves_it_alone()
        {
            using var f = await StarAsync();
            await f.Engine.WaiveFindingAsync("air", "VIS-FK", "column:Sales/CustomerKey", Why, "human");
            var before = await f.Engine.BpaScanAsync();
            var twin = before.Violations.Single(v => v.RuleId == "HIDE_FOREIGN_KEYS" && v.ObjectRef == "column:Sales/CustomerKey");

            Assert.Equal(0, before.WaivedCount);   // nobody accepted a Model quality finding
            Assert.False(Accepted(twin), "the related finding reads as accepted");
            Assert.NotNull(twin.CoveredBy);   // and says which acceptance it stands beside
            Assert.Equal(RelatedWords, twin.WaiverReason);
            Assert.Contains(before.Violations, v => v.RuleId == "HIDE_FOREIGN_KEYS" && v.ObjectRef == "column:Sales/CustomerKey" && before.ViolationCount > 0);
            var fixAll = await f.Engine.BpaFixAllAsync("agent");
            Assert.False(await Read(f, m => m.Tables["Sales"].Columns["CustomerKey"].IsHidden));   // the declined write still does not happen
            Assert.Contains(fixAll.Unfixed, u => u.RuleId == "HIDE_FOREIGN_KEYS");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_related_readiness_finding_is_scored_as_open()
        {
            using var f = await StarAsync();
            var before = await f.Engine.AiReadinessScanAsync();
            var visibility = before.Categories.Single(c => c.Category == "Visibility");
            await f.Engine.WaiveFindingAsync("bpa", "HIDE_FOREIGN_KEYS", "column:Sales/CustomerKey", Why, "human");

            var after = await f.Engine.AiReadinessScanAsync();
            var twin = after.Findings.Single(x => x.RuleId == "VIS-FK" && x.ObjectRef == "column:Sales/CustomerKey");

            Assert.Equal(0, after.WaivedCount);   // nobody accepted an AI readiness finding
            Assert.Equal(visibility.Violations, after.Categories.Single(c => c.Category == "Visibility").Violations);   // it still counts against the score
            Assert.Equal(before.Overall, after.Overall);
            Assert.False(Accepted(twin));
            Assert.Equal(RelatedWords, twin.WaiverReason);
        }

        // ---- an acceptance belongs to the object, not to its name ---------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("column", "column:Sales/CustomerKey", "column:Sales/Customer Key")]
        [InlineData("measure", "measure:Sales/Total Sales", "measure:Sales/Sales Total")]
        public async Task An_acceptance_follows_its_object_through_a_rename(string kind, string before, string after)
        {
            using var f = await StarAsync();
            var rule = kind == "column" ? ("air", "VIS-FK") : ("bpa", "PROVIDE_FORMAT_STRING_FOR_MEASURES");
            await f.Engine.WaiveFindingAsync(rule.Item1, rule.Item2, before, Why, "human");

            await new EngineRpcTarget(f.Engine).renameObject(before, after.Substring(after.IndexOf('/') + 1));

            var records = await f.Engine.ListWaiversAsync();
            Assert.Contains(records, w => w.RuleId == rule.Item2 && string.Equals(w.ObjectRef, after, StringComparison.Ordinal));
            if (rule.Item1 == "air") Assert.Contains((await f.Engine.AiReadinessScanAsync()).Findings, x => x.RuleId == rule.Item2 && x.ObjectRef == after && Accepted(x));
            else Assert.Contains((await f.Engine.BpaScanAsync()).Violations, v => v.RuleId == rule.Item2 && v.ObjectRef == after && Accepted(v));
            Assert.Empty((await f.Engine.AiReadinessScanAsync()).OrphanedWaivers);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_table_rename_keeps_the_acceptances_on_its_columns()
        {
            using var f = await StarAsync();
            await f.Engine.WaiveFindingAsync("air", "VIS-FK", "column:Sales/CustomerKey", Why, "human");

            await new EngineRpcTarget(f.Engine).renameObject("table:Sales", "Orders");

            Assert.Contains((await f.Engine.AiReadinessScanAsync()).Findings, x => x.RuleId == "VIS-FK" && x.ObjectRef == "column:Orders/CustomerKey" && Accepted(x));
        }

        /// <summary>An older store names the object by its name alone. It is read and matched at once, and the next write records the
        /// object's identity beside it, so a rename after that keeps it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_record_that_names_an_object_by_name_only_is_read_and_gets_its_identity_on_the_next_write()
        {
            using var f = await StarAsync();
            await f.Sessions.Require().MutateAsync("human", "setup: an older store", m =>
                ((IAnnotationObject)m).SetAnnotation(WaiverStore.Annotation,
                    "[{\"System\":\"air\",\"RuleId\":\"VIS-FK\",\"ObjectRef\":\"column:Sales/CustomerKey\",\"Reason\":\"old\",\"By\":\"human\",\"When\":\"2026-01-01T00:00:00Z\"}]"));
            Assert.Contains((await f.Engine.AiReadinessScanAsync()).Findings, x => x.RuleId == "VIS-FK" && Accepted(x));

            await f.Engine.WaiveFindingAsync("air", "FMT-SUMMARIZE", "column:Customer/CustomerKey", Why, "human");   // any write rewrites the store

            var tags = await Read(f, m => WaiverStore.Load(m).Select(w => (w.RuleId, Tag: typeof(WaiverRecord).GetProperty("Tag")?.GetValue(w) as string)).ToList());
            Assert.All(tags, t => Assert.False(string.IsNullOrEmpty(t.Tag), t.RuleId + " has no recorded identity after the store was written"));
            await new EngineRpcTarget(f.Engine).renameObject("column:Sales/CustomerKey", "Customer Key");
            Assert.Contains((await f.Engine.AiReadinessScanAsync()).Findings, x => x.RuleId == "VIS-FK" && x.ObjectRef == "column:Sales/Customer Key" && Accepted(x));
        }

        // ---- a name with an edge space ------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_acceptance_of_an_object_whose_name_ends_in_a_space_is_stored_under_that_exact_name()
        {
            using var f = await StarAsync();
            var name = "measure:Sales/Order Count ";
            var finding = (await f.Engine.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "FMT-MEASURE" && x.ObjectRef == name);

            await f.Engine.WaiveFindingAsync("air", "FMT-MEASURE", finding.ObjectRef, Why, "human");

            Assert.Contains((await f.Engine.ListWaiversAsync()), w => w.RuleId == "FMT-MEASURE" && w.ObjectRef == name);
            Assert.Contains((await f.Engine.AiReadinessScanAsync()).Findings, x => x.RuleId == "FMT-MEASURE" && x.ObjectRef == name && Accepted(x));
        }
    }
}
