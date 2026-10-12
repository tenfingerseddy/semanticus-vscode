using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Round 4 of m11/rule-clashes. A finding the person accepts (waives) AFTER the change plan was proposed
    /// must not be carried out by that plan: the line the plan seeded from it reads as left out while the waiver
    /// holds, comes back when the waiver goes (unwaive, or undo of the waive), and apply re-checks the waiver inside
    /// the write, so a line ticked again still does not override it. A line added by hand is an explicit request and
    /// still applies. The last cases are the starter rule: no total is proposed over any declared key part, on the
    /// Plan's starter row as on the spec generator.</summary>
    public sealed class PlanWaivedAfterPlannedTests
    {
        private const string Left = "Left out because you accepted this finding after it was planned.";
        private const string NotKey = "Staff Number repeats across contracts here, so it is not the key.";

        private static async Task<DecisionFixture> StaffStarAsync(Semanticus.Engine.Entitlement.IEntitlement tier = null)
        {
            var f = new DecisionFixture(tier ?? TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("StaffStar", 1604);
            await e.CreateTableAsync("Timesheets", "human");
            await e.CreateColumnAsync("table:Timesheets", "Staff Number", "Int64", "Staff Number", "human");
            await e.CreateColumnAsync("table:Timesheets", "Hours", "Decimal", "Hours", "human");
            await e.CreateTableAsync("Staff", "human");
            await e.CreateColumnAsync("table:Staff", "Staff Number", "Int64", "Staff Number", "human");
            await e.CreateColumnAsync("table:Staff", "Staff Name", "String", "Staff Name", "human");
            await e.CreateRelationshipAsync("column:Timesheets/Staff Number", "column:Staff/Staff Number", null, true, "human");
            await f.Sessions.Require().MutateAsync("human", "test setup", m => m.Tables["Timesheets"].Columns["Staff Number"].IsHidden = true);
            return f;
        }

        private static Task<ChangePlanView> ProposeAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).proposePlan()
            : McpTools.ProposePlan(f.Engine);

        private static Task<SetResult> WaiveAsync(DecisionFixture f, string door, string system, string ruleId, string objRef, string reason) => door == "studio"
            ? new EngineRpcTarget(f.Engine).waiveFinding(system, ruleId, objRef, reason)
            : McpTools.WaiveFinding(f.Engine, system, ruleId, objRef, reason);

        private static Task<SetResult> UnwaiveAsync(DecisionFixture f, string door, string system, string ruleId, string objRef) => door == "studio"
            ? new EngineRpcTarget(f.Engine).unwaiveFinding(system, ruleId, objRef)
            : McpTools.UnwaiveFinding(f.Engine, system, ruleId, objRef);

        private static Task<ApplyPlanReport> ApplyAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).applyPlan()
            : McpTools.ApplyPlan(f.Engine);

        private static async Task<ChangeItem> LineAsync(DecisionFixture f, string ruleId, string objRef) =>
            (await f.Engine.GetPlanAsync()).Items.Single(i => i.RuleId == ruleId && i.ObjectRef == objRef);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task A_finding_waived_after_the_plan_marks_its_line_left_out_and_unwaiving_brings_it_back(string door)
        {
            using var f = await StaffStarAsync();
            await ProposeAsync(f, door);
            Assert.Equal("approved", (await LineAsync(f, "MARK_PRIMARY_KEYS", "column:Staff/Staff Number")).Status);
            var schema = (await f.Engine.GetPlanAsync()).Items.Single(i => i.Kind == "create_linguistic_schema");
            Assert.Equal("approved", schema.Status);

            await WaiveAsync(f, door, "bpa", "MARK_PRIMARY_KEYS", "column:Staff/Staff Number", NotKey);
            await WaiveAsync(f, door, "air", "SYN-SCHEMA", "*", "No Copilot questions on this model.");

            var key = await LineAsync(f, "MARK_PRIMARY_KEYS", "column:Staff/Staff Number");
            Assert.Equal("skipped", key.Status);
            Assert.Equal(Left, key.Note);
            var held = (await f.Engine.GetPlanAsync()).Items.Single(i => i.Id == schema.Id);
            Assert.Equal("skipped", held.Status);
            Assert.Equal(Left, held.Note);
            // The other lines are untouched.
            Assert.Equal("approved", (await LineAsync(f, "NUMERIC_COLUMN_SUMMARIZE_BY", "column:Timesheets/Hours")).Status);

            await UnwaiveAsync(f, door, "bpa", "MARK_PRIMARY_KEYS", "column:Staff/Staff Number");
            key = await LineAsync(f, "MARK_PRIMARY_KEYS", "column:Staff/Staff Number");
            Assert.Equal("approved", key.Status);
            Assert.Null(key.Note);
            Assert.Equal("skipped", (await f.Engine.GetPlanAsync()).Items.Single(i => i.Id == schema.Id).Status);   // still waived
        }

        /// <summary>The Studio is pushed the plan, so the waiver's own call tells it the line is no longer ticked; it does
        /// not wait for the page to read the plan again.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_studio_is_pushed_the_left_out_line_when_the_assistant_waives()
        {
            using var f = await StaffStarAsync();
            await f.Engine.ProposePlanAsync(null, true, 0, "human");
            ChangePlanView pushed = null;
            f.Sessions.Bus.PlanChanged += v => pushed = v;

            await McpTools.WaiveFinding(f.Engine, "bpa", "MARK_PRIMARY_KEYS", "column:Staff/Staff Number", NotKey);

            Assert.NotNull(pushed);
            var line = pushed.Items.Single(i => i.RuleId == "MARK_PRIMARY_KEYS" && i.ObjectRef == "column:Staff/Staff Number");
            Assert.Equal("skipped", line.Status);
            Assert.Equal(Left, line.Note);
        }

        [Fact]
        public async Task Undoing_the_waiver_brings_the_line_back_and_redo_leaves_it_out_again()
        {
            using var f = await StaffStarAsync();
            await f.Engine.ProposePlanAsync(null, true, 0, "human");
            await f.Engine.WaiveFindingAsync("bpa", "MARK_PRIMARY_KEYS", "column:Staff/Staff Number", NotKey, "human");
            Assert.Equal("skipped", (await LineAsync(f, "MARK_PRIMARY_KEYS", "column:Staff/Staff Number")).Status);

            await f.Engine.UndoAsync("human");
            var line = await LineAsync(f, "MARK_PRIMARY_KEYS", "column:Staff/Staff Number");
            Assert.Equal("approved", line.Status);
            Assert.Null(line.Note);

            await f.Engine.RedoAsync("human");
            line = await LineAsync(f, "MARK_PRIMARY_KEYS", "column:Staff/Staff Number");
            Assert.Equal("skipped", line.Status);
            Assert.Equal(Left, line.Note);
        }

        /// <summary>The apply-time check on its own: the line is ticked again after the waiver, so the plan says
        /// approved, and apply still leaves it out, reports it as skipped with the plain note, and changes nothing
        /// the waiver covers. Free and Pro.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio", true)]
        [InlineData("mcp", true)]
        [InlineData("studio", false)]
        [InlineData("mcp", false)]
        public async Task A_line_ticked_again_after_the_waiver_is_still_left_out_at_apply(string door, bool pro)
        {
            using var f = await StaffStarAsync(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await ProposeAsync(f, door);
            await WaiveAsync(f, door, "bpa", "MARK_PRIMARY_KEYS", "column:Staff/Staff Number", NotKey);
            var line = await LineAsync(f, "MARK_PRIMARY_KEYS", "column:Staff/Staff Number");
            await f.Engine.SetPlanItemAsync(line.Id, null, true, "human");
            // A tick on a line the acceptance left out does not make it read approved (round 6, L5): it stays left out, with its note.
            var ticked = await LineAsync(f, "MARK_PRIMARY_KEYS", "column:Staff/Staff Number");
            Assert.Equal(("skipped", Left), (ticked.Status, ticked.Note));

            var report = await ApplyAsync(f, door);

            var after = await LineAsync(f, "MARK_PRIMARY_KEYS", "column:Staff/Staff Number");
            Assert.Equal(("skipped", Left), (after.Status, after.Note));
            Assert.True(report.AppliedCount >= 1);   // the rest of the plan still went in
            Assert.False(await f.Sessions.Require().ReadAsync(m => m.Tables["Staff"].Columns["Staff Number"].IsKey));
        }

        /// <summary>add_plan_item is an explicit request, so a waiver on the same rule and object does not hold it
        /// back.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task A_line_added_by_hand_still_applies_after_the_waiver(string door)
        {
            using var f = await StaffStarAsync();
            var added = door == "studio"
                ? await new EngineRpcTarget(f.Engine).addPlanItem("column:Timesheets/Hours", "set_summarize_by", "None")
                : await McpTools.AddPlanItem(f.Engine, "column:Timesheets/Hours", "set_summarize_by", "None");
            var line = added.Items.Single(i => i.ObjectRef == "column:Timesheets/Hours");
            Assert.Equal("approved", line.Status);

            await WaiveAsync(f, door, "bpa", "NUMERIC_COLUMN_SUMMARIZE_BY", "column:Timesheets/Hours", "Hours are summed on purpose.");
            Assert.Equal("approved", (await f.Engine.GetPlanAsync()).Items.Single(i => i.Id == line.Id).Status);

            var report = await ApplyAsync(f, door);
            Assert.Equal("applied", report.Items.Single(i => i.Id == line.Id).Status);
            Assert.Equal(AggregateFunction.None, await f.Sessions.Require().ReadAsync(m => m.Tables["Timesheets"].Columns["Hours"].SummarizeBy));
        }

        /// <summary>The Plan's starter row, on a model built from a spec whose fact table is keyed on two columns.
        /// TOM holds its key on one of them; the other is marked as a declared part. Neither gets a total.</summary>
        [Fact]
        public async Task The_plans_starter_row_proposes_no_total_over_a_declared_key_part()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Lines", 1604);
            var spec = new ModelSpec
            {
                Name = "Lines", CompatibilityLevel = 1604, StorageMode = "import",
                Tables = new[]
                {
                    new SpecTable
                    {
                        Name = "Staff", Role = "dimension", Columns = new[]
                        {
                            new SpecColumn { Name = "Staff Number", DataType = "Int64", IsKey = true, Hidden = true },
                            new SpecColumn { Name = "Staff Name", DataType = "String" },
                        },
                    },
                    new SpecTable
                    {
                        Name = "Lines", Role = "fact", Columns = new[]
                        {
                            new SpecColumn { Name = "Sheet ID", DataType = "Int64", IsKey = true, Hidden = true },
                            new SpecColumn { Name = "Line No", DataType = "Int64", IsKey = true, Hidden = true },
                            new SpecColumn { Name = "Staff Number", DataType = "Int64", Hidden = true },
                            new SpecColumn { Name = "Hours", DataType = "Decimal" },
                        },
                    },
                },
                Relationships = new[] { new SpecRelationship { FromTable = "Lines", FromColumn = "Staff Number", ToTable = "Staff", ToColumn = "Staff Number" } },
            };
            await f.Engine.SetSpecAsync(JsonSerializer.Serialize(spec, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), "human");
            Assert.Empty((await f.Engine.BuildModelFromSpecAsync("human")).Errors);
            // Not vacuous: TOM holds one part, so the other part is a key only by its declared mark.
            Assert.Equal(1, await f.Sessions.Require().ReadAsync(m => m.Tables["Lines"].Columns.Count(c => c.IsKey)));

            var summary = await f.Sessions.Require().ReadAsync(m => StarterMeasuresFamily.RuleSummary(m));
            var measures = summary.Where(t => t.Table == "Lines").SelectMany(t => t.Measures).ToList();
            Assert.Contains(measures, x => x.Column == "Hours");
            Assert.DoesNotContain(measures, x => x.Column == "Sheet ID" || x.Column == "Line No");

            var seeds = new List<ChangeItem>();
            var refused = new List<ModelPlanChangeRefusal>();
            await f.Sessions.Require().ReadAsync(m => { StarterMeasuresFamily.RuleLines(m, "Lines", seeds, refused); return 0; });
            Assert.Contains(seeds, l => (l.After ?? "").Contains("[Hours]", StringComparison.Ordinal));
            Assert.DoesNotContain(seeds, l => (l.After ?? "").Contains("[Sheet ID]", StringComparison.Ordinal)
                                              || (l.After ?? "").Contains("[Line No]", StringComparison.Ordinal));
        }
    }
}
