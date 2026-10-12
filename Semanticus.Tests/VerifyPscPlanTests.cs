// Verifier, lane m11/psc-plan (plan statement check 2, items 3, 5, 6). Attacks on the paired acceptance, the words row's column
// formats and the agent door of the collisions row's names, plus a re-derivation of the J8 test's Best practice letter.
// A test here that fails names a defect the verifier found; it is left failing on purpose.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class VerifyPscPlanTests
    {
        private readonly ITestOutputHelper _out;
        public VerifyPscPlanTests(ITestOutputHelper output) => _out = output;

        private const string Why = "Kept off on purpose; a measure uses it.";
        private const string ColumnRule = "SEM_COLUMN_NEEDS_FORMAT_STRING";
        private const string InactiveRule = "INACTIVE_RELATIONSHIPS_THAT_ARE_NEVER_ACTIVATED";
        private const string SnowflakeRule = "SNOWFLAKE_SCHEMA_ARCHITECTURE";
        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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

        // ATTACK (hide a real problem). REL-INACTIVE fires on EVERY inactive relationship; the best-practice rule fires only on one
        // no measure activates. The person accepts the readiness finding while a measure DOES activate the relationship (the
        // best-practice warning is not there, so it was never shown, never decided). The measure is then deleted: the
        // relationship is now dead, a real best-practice warning appears, and it must be OPEN. The pairing reads the old
        // acceptance as covering it, so it shows as accepted (with a reason that is no longer true) and Best practice stays A.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_dead_relationship_warning_that_appears_after_the_readiness_acceptance_is_open_not_accepted()
        {
            using var f = await InactiveModelAsync();
            var e = f.Engine;
            var measure = await e.CreateMeasureAsync("table:fact", "Due Amount",
                "CALCULATE ( SUM ( fact[AMT] ), USERELATIONSHIP ( fact[DUE_KEY], dim_date[DATE_KEY] ) )", "human");
            var rel = Assert.Single((await e.AiReadinessScanAsync()).Findings, x => x.RuleId == "REL-INACTIVE").ObjectRef;
            Assert.DoesNotContain((await e.BpaScanAsync()).Violations, v => v.RuleId == InactiveRule);   // a measure activates it

            await e.WaiveFindingAsync("air", "REL-INACTIVE", rel, Why, "human");
            await e.DeleteObjectAsync(measure, "human");

            var dead = Assert.Single((await e.BpaScanAsync()).Violations, v => v.RuleId == InactiveRule);
            Assert.Equal(rel, dead.ObjectRef);
            _out.WriteLine($"after delete: waived={dead.Waived} accepted={dead.IsAccepted()} reason='{dead.WaiverReason}' coveredBy='{dead.CoveredBy}'");
            Assert.False(dead.IsAccepted(), "a warning nobody saw is read as accepted, with the reason '" + dead.WaiverReason + "'");
        }

        // Item 3b: ONE undo of the acceptance reopens both pages' findings for that relationship, and never touches another one.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task One_undo_of_a_paired_acceptance_reopens_both_and_only_that_relationship()
        {
            using var f = await InactiveModelAsync();
            var e = f.Engine;
            await e.CreateColumnAsync("table:fact", "PAID_KEY", "Int64", null, "human");
            await e.CreateRelationshipAsync("column:fact/PAID_KEY", "column:dim_date/DATE_KEY", null, false, "human");
            var refs = (await e.AiReadinessScanAsync()).Findings.Where(x => x.RuleId == "REL-INACTIVE").Select(x => x.ObjectRef).OrderBy(x => x).ToArray();
            Assert.Equal(2, refs.Length);
            await e.WaiveFindingAsync("bpa", InactiveRule, refs[1], Why, "human");     // the other one, accepted on Model quality
            await e.WaiveFindingAsync("air", "REL-INACTIVE", refs[0], Why, "human");
            Assert.True((await e.BpaScanAsync()).Violations.Single(v => v.RuleId == InactiveRule && v.ObjectRef == refs[0]).IsAccepted());

            await e.UndoAsync("human");
            var air = (await e.AiReadinessScanAsync()).Findings.Where(x => x.RuleId == "REL-INACTIVE").ToArray();
            var bpa = (await e.BpaScanAsync()).Violations.Where(v => v.RuleId == InactiveRule).ToArray();
            Assert.False(air.Single(x => x.ObjectRef == refs[0]).IsAccepted());
            Assert.False(bpa.Single(v => v.ObjectRef == refs[0]).IsAccepted());
            Assert.True(air.Single(x => x.ObjectRef == refs[1]).IsAccepted());      // the other relationship keeps its own decision
            Assert.True(bpa.Single(v => v.ObjectRef == refs[1]).IsAccepted());

            // Redo puts both back; reopening on the page that does not hold the record says where it is, and removes nothing.
            await e.RedoAsync("human");
            Assert.True((await e.BpaScanAsync()).Violations.Single(v => v.RuleId == InactiveRule && v.ObjectRef == refs[0]).IsAccepted());
            await e.UnwaiveFindingAsync("bpa", InactiveRule, refs[0], "human");
            Assert.True((await e.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "REL-INACTIVE" && x.ObjectRef == refs[0]).IsAccepted());
            Assert.Equal(2, (await e.ListWaiversAsync()).Length);
        }

        // ATTACK (asked twice / against a decision). The person accepts FMT-COLUMN on a plain number column ("no format here").
        // The best-practice twin stays open beside it ("accept this one too"), and the words row must not now hand the assistant
        // the same column to give a format to: the person already decided it.
        [Fact]
        public async Task A_column_whose_format_the_person_declined_is_not_handed_to_the_assistant()
        {
            using var f = await InactiveModelAsync();
            var e = f.Engine;
            Assert.Contains((await e.AiReadinessScanAsync()).Findings, x => x.RuleId == "FMT-COLUMN" && x.ObjectRef == "column:fact/AMT");
            await e.WaiveFindingAsync("air", "FMT-COLUMN", "column:fact/AMT", "No format on this one, on purpose.", "human");
            var twin = (await e.BpaScanAsync()).Violations.FirstOrDefault(v => v.RuleId == ColumnRule && v.ObjectRef == "column:fact/AMT");
            _out.WriteLine($"twin: {(twin == null ? "none" : $"waived={twin.Waived} asTwin={twin.WaivedAsTwin} accepted={twin.IsAccepted()}")}");
            var queue = await e.GetWordsQueueAsync();
            Assert.DoesNotContain(queue.Items, i => i.ObjectRef == "column:fact/AMT" && i.Kind == "format");
        }

        // What the assistant's write does when it follows that queue item on a column whose format the person declined.
        [Fact]
        public async Task Following_the_queue_on_a_declined_column_writes_nothing_the_person_declined()
        {
            using var f = await InactiveModelAsync();
            var e = f.Engine;
            await e.WaiveFindingAsync("air", "FMT-COLUMN", "column:fact/AMT", "No format on this one, on purpose.", "human");
            var item = (await e.GetWordsQueueAsync()).Items.FirstOrDefault(i => i.ObjectRef == "column:fact/AMT" && i.Kind == "format");
            _out.WriteLine("queue item: " + (item == null ? "none" : item.Tool + " / " + item.Message));
            if (item == null) return;
            Exception thrown = null; SetResult r = null;
            try { r = await e.SetObjectPropertyAsync("column:fact/AMT", "FormatString", "0", "agent"); } catch (Exception x) { thrown = x; }
            var after = (await e.AiReadinessScanAsync()).Findings.FirstOrDefault(x => x.RuleId == "FMT-COLUMN" && x.ObjectRef == "column:fact/AMT");
            _out.WriteLine($"write: thrown={thrown?.Message} changed={r?.Changed} warning={r?.Warning}; FMT-COLUMN finding now {(after == null ? "gone" : "there")}");
            Assert.True(thrown != null || r?.Changed != true, "the assistant wrote a format on a column the person said keeps none");
        }

        // Item 6, the agent door: the MCP tool get_model_plan (through its read shape, not the engine call) carries the names.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_MCP_get_model_plan_tool_carries_the_collisions_rows_names()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateLinguisticSchemaAsync(null, "agent");
            await e.CreateColumnAsync("table:Sales", "Group1Key", "String", null, "human");
            await e.CreateMeasureAsync("table:Sales", "Group1 Key", "SUM ( Sales[Amount] )", "human");
            await f.SaveAsAsync("CollisionMcp.bim");
            f.UseNull();

            var whole = await McpTools.GetModelPlan(e);
            var row = Assert.Single(whole.Items, i => i.Id == "proposal/collisions");
            Assert.Contains("Group1Key", row.Jump.Find);
            Assert.Contains("Group1 Key", row.Jump.Find);
            var one = await McpTools.GetModelPlan(e, "proposal/collisions");
            Assert.Contains("Group1 Key", Assert.Single(one.Items, i => i.Id == "proposal/collisions").Jump.Find);
            var json = JsonSerializer.Serialize(whole, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            Assert.Contains("\"find\":[", json.Replace(" ", ""));
        }

        // The J8 test's letter, re-derived: count the open errors and warnings by hand, run the grading formula on them, and check
        // that nothing was accepted except by the relationships row (the twins), and that the words row never asks for one
        // object's format twice.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task J8_best_practice_letter_rederived()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            var spec = LocalEngine.FabricSchemaToSpec(SqlDraftBuildTests.J8Schema(), "contoso-sql.example.com", "ContosoConsulting", "import");
            await e.CreateModelAsync("ContosoConsulting", 1604);
            await e.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            Assert.Empty((await e.BuildModelFromSpecAsync("human")).Errors);
            await f.SaveAsAsync("J8v.bim");
            f.UseNull();
            void Dump(string at, BpaScorecard c)
            {
                var open = c.Violations.Where(v => !v.IsAccepted()).ToArray();
                _out.WriteLine($"{at}: letter {c.BestPractice} E{open.Count(v => v.Severity >= 3)} W{open.Count(v => v.Severity == 2)} I{open.Count(v => v.Severity <= 1)} objects {c.ObjectsScanned} unknownsBlocking {c.Unknowns.Count(u => u.BlocksGate)}");
                foreach (var g in open.Where(v => v.Severity >= 2).GroupBy(v => v.RuleId)) _out.WriteLine($"   {g.Key} x{g.Count()}");
            }
            Dump("built", await e.BpaScanAsync());

            var presses = 0;
            for (; presses < 14; presses++)
            {
                var plan = await e.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.StateWord == PlanStateWords.Ready && i.Doer == PlanDoers.Semanticus)) break;
                var ran = await e.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                if (!ran.Performed) break;
            }
            Dump($"after {presses} presses", await e.BpaScanAsync());

            var queue = await e.GetWordsQueueAsync(0, 0);
            var all = new List<WordsQueueItem>(queue.Items);
            for (var next = queue.Next; next != null;) { var p = await e.GetWordsQueueAsync(0, next.Value); all.AddRange(p.Items); next = p.Next; }
            Assert.Equal(queue.Total, all.Count);
            var formats = all.Where(i => i.Kind == "format").ToArray();
            _out.WriteLine($"queue {all.Count} items, {formats.Length} formats ({formats.Count(i => i.ObjectRef.StartsWith("column:", StringComparison.Ordinal))} columns)");
            Assert.Equal(formats.Length, formats.Select(i => i.ObjectRef).Distinct(StringComparer.OrdinalIgnoreCase).Count());   // never twice
            var openFmtColumn = (await e.AiReadinessScanAsync()).Findings.Where(x => x.RuleId == "FMT-COLUMN" && !x.Waived).Select(x => x.ObjectRef).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(formats, i => openFmtColumn.Contains(i.ObjectRef));
            var words = (await e.GetModelPlanAsync()).Items.Single(i => i.Id == LocalEngine.WordsItemId || i.OriginLink?.Ref == LocalEngine.WordsItemId);   // Run the plan hands the words row to the assistant as a task linked to it (002e790f)
            _out.WriteLine($"words row: {words.Title} / {words.Sub}");
            // (The row's preview lines are capped, so the count on the row is the lane's unit test The_words_row_stays_while_only_a_column_format_is_left.)

            foreach (var i in formats)
            {
                if (i.ObjectRef.StartsWith("measure:", StringComparison.Ordinal)) await e.SetMeasureFormatAsync(i.ObjectRef, "#,0.00", "agent");
                else await e.SetObjectPropertyAsync(i.ObjectRef, "FormatString", "0", "agent");
            }
            foreach (var x in (await e.AiReadinessScanAsync()).Findings.Where(x => x.RuleId == "FMT-COLUMN" && !x.Waived).ToArray())
                await e.SetObjectPropertyAsync(x.ObjectRef, "FormatString", "0.00", "human");
            Dump("after words + formats", await e.BpaScanAsync());

            foreach (var air in (await e.AiReadinessScanAsync()).Findings.Where(x => x.RuleId.StartsWith("REL-") && !x.Waived).ToArray())
                await e.WaiveFindingAsync("air", air.RuleId, air.ObjectRef, Why, "human");
            var end = await e.BpaScanAsync();
            Dump("after relationships row", end);

            var openEnd = end.Violations.Where(v => !v.IsAccepted()).ToArray();
            var (letter, _, why) = BpaGrading.Compute(openEnd.Count(v => v.Severity >= 3), openEnd.Count(v => v.Severity == 2),
                openEnd.Count(v => v.Severity <= 1), end.ObjectsScanned, end.Unknowns.Count(u => u.BlocksGate));
            _out.WriteLine("re-derived: " + why);
            Assert.Equal(letter, end.BestPractice);
            Assert.Equal("A", letter);
            // Every accepted best-practice finding is a relationship twin the row asked about; nothing else was accepted.
            var accepted = end.Violations.Where(v => v.IsAccepted() && !v.WholeRuleRow).ToArray();
            foreach (var v in accepted) _out.WriteLine($"accepted: {v.RuleId} {v.ObjectRef} by '{v.CoveredBy}'");
            Assert.All(accepted, v => Assert.True(v.RuleId == InactiveRule || v.RuleId == SnowflakeRule, v.RuleId + " " + v.ObjectRef));
            Assert.Equal(4, accepted.Length);   // the checker's count: 3 inactive relationships and 1 snowflake
        }
    }
}
