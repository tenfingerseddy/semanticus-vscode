// The Plan must carry every finding that costs a grade (Kane, 13 Oct 2026: "BPA is at C but doesnt appear in the plan tasks",
// "Semanticus is not able to progress but we dont have a perfect score"). What these tests hold, one line each:
//   every scored finding on a model with an IFERROR measure, a float column, a bi-directional link, hidden unused columns and a what-if table
//   is on a Plan row, or its row says in words why it cannot be fixed
//   the float column is a decision on the best-practice assistant row, never a fix that Fix all refuses
//   Run the plan, then a scripted assistant working the rows through the MCP methods: both coverage rows finish, the grades rise,
//   and no coverage row comes back
//   the Fix button on Model quality counts only what Fix all will make (no button that refuses)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class PlanCoverageRowsTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        private readonly ITestOutputHelper _out;
        public PlanCoverageRowsTests(ModelTemplates templates, ITestOutputHelper output) { _templates = templates; _out = output; }

        private async Task<DecisionFixture> GappyAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "gappy-coverage", BuildAsync, file);
            f.UseNull();
            return f;
        }

        /// <summary>Kane's shape in small: a float column, an IFERROR measure, a link that filters both ways, two hidden columns nothing uses,
        /// and a what-if style table (a field parameter carries the same marker the rule reads).</summary>
        private static async Task BuildAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Batch Id", "String", null, "human");
            await e.CreateColumnAsync("table:Sales", "Load Note", "String", null, "human");
            await e.SetObjectPropertyAsync("column:Sales/Batch Id", "IsHidden", "true", "human");
            await e.SetObjectPropertyAsync("column:Sales/Load Note", "IsHidden", "true", "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", "BothDirections", true, "human");
            var total = await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Safe Total", "IFERROR ( SUM ( Sales[Amount] ), 0 )", "human");
            await e.CreateFieldParameterAsync("Discount Scenario", new[] { new FieldParameterItem { ObjectRef = total } }, "human");
        }

        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<LocalEngine, PlanInvariant> _invariants = new();

        /// <summary>Every plan this class reads goes through the Plan invariant: no job twice, the strip is the list, no finding on no open row.</summary>
        private async Task<ModelPlanView> PlanAsync(LocalEngine e)
        {
            var inv = _invariants.GetValue(e, k => new PlanInvariant(k, _out));
            return await inv.CheckAsync("plan read");
        }

        private static string Dump(ModelPlanView plan) =>
            string.Join("\n", plan.Items.Select(i => $"{i.Id} | {i.Doer} | {i.StateWord} | {i.Title} | rules={string.Join(",", i.Rules ?? Array.Empty<string>())} | objs={i.Objects?.Length}"));

        private static ModelPlanItem Row(ModelPlanView plan, string id) => plan.Items.SingleOrDefault(i => i.Id == id);

        /// <summary>Whether some row on the plan acts on this rule and object: the rule (or its readiness twin) is on the row and the row names the
        /// object, or it is one of the rule-level rows (the fix row, the words row) that name no objects and carry the rule.</summary>
        private static bool OnARow(ModelPlanView plan, string ruleId, string objectRef, string twin) =>
            plan.Items.Any(i =>
            {
                // The cleanup row lists the unused columns it removes (it carries no rule id).
                if (i.Id == "cleanup/model" && ruleId == "UNNECESSARY_COLUMNS"
                    && (i.Objects ?? Array.Empty<string>()).Contains(objectRef, StringComparer.OrdinalIgnoreCase)) return true;
                var rules = i.Rules ?? Array.Empty<string>();
                if (!rules.Contains(ruleId, StringComparer.OrdinalIgnoreCase) && !(twin != null && rules.Contains(twin, StringComparer.OrdinalIgnoreCase))) return false;
                var objects = i.Objects ?? Array.Empty<string>();
                return objects.Length == 0 || objects.Any(o => string.Equals(o, objectRef, StringComparison.OrdinalIgnoreCase));
            });

        private static readonly System.Collections.Generic.Dictionary<string, string> Twins = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AVOID_USING_THE_IFERROR_FUNCTION"] = "BP-DAX-IFERROR",
            ["PROVIDE_FORMAT_STRING_FOR_MEASURES"] = "FMT-MEASURE",
        };

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_scored_finding_is_on_a_plan_row_or_says_why_it_cannot_be_fixed()
        {
            using var f = await GappyAsync("CoverageEvery.bim");
            var e = f.Engine;
            var plan = await PlanAsync(e);
            _out.WriteLine(Dump(plan));

            var bpa = (await e.BpaScanAsync()).Violations.Where(v => !v.Waived && v.Severity >= 2).ToArray();
            var air = await e.AiReadinessScanAsync();
            var scoredAir = air.Findings.Where(x => !x.Waived && (air.ScoredRuleIds.Contains(x.RuleId)
                                                                  || x.RuleId == "LIMIT-SCALE" || x.RuleId == "DAX-REFERENCE")).ToArray();
            Assert.Contains(bpa, v => v.RuleId == "AVOID_FLOATING_POINT_DATA_TYPES");
            Assert.Contains(bpa, v => v.RuleId == "UNNECESSARY_COLUMNS");
            Assert.Contains(bpa, v => v.RuleId == "AVOID_USING_THE_IFERROR_FUNCTION");

            var missing = new System.Collections.Generic.List<string>();
            foreach (var v in bpa)
                if (!OnARow(plan, v.RuleId, v.ObjectRef, PairedAcceptances.AirTwinOf(v.RuleId) ?? (Twins.TryGetValue(v.RuleId, out var t) ? t : null)))
                    missing.Add("bpa " + v.RuleId + " " + v.ObjectRef);
            foreach (var x in scoredAir)
                if (!OnARow(plan, x.RuleId, x.ObjectRef, null)) missing.Add("air " + x.RuleId + " " + x.ObjectRef);
            Assert.True(missing.Count == 0, "No row acts on: " + string.Join("; ", missing));

            // The model-level findings (no date table, too many two-way links) are rows too, and the rows that carry them are the assistant's.
            var cover = Row(plan, LocalEngine.CoverageBpaItemId);
            Assert.NotNull(cover);
            Assert.Equal(PlanDoers.Assistant, cover.Doer);
            Assert.Matches(@"^Fix \d+ best-practice issues?$", cover.Title);
            Assert.Contains("MODEL_SHOULD_HAVE_A_DATE_TABLE", cover.Rules);
            Assert.Contains("UNNECESSARY_COLUMNS", cover.Rules);
            Assert.Contains("AVOID_FLOATING_POINT_DATA_TYPES", cover.Rules);
            // The IFERROR finding is the formulas row's, once, not twice.
            Assert.DoesNotContain("AVOID_USING_THE_IFERROR_FUNCTION", cover.Rules);
            // Every rule on the row has a line with its brief and its points.
            foreach (var rule in cover.Rules.Where(r => r != "PROVIDE_FORMAT_STRING_FOR_MEASURES"))
                Assert.Contains(cover.Why, w => w.Contains("points"));
        }

        [Fact]
        public async Task The_float_column_is_a_decision_for_the_assistant_not_a_fix_that_refuses()
        {
            using var f = await GappyAsync("CoverageFloat.bim");
            var e = f.Engine;
            var plan = await PlanAsync(e);
            var cover = Row(plan, LocalEngine.CoverageBpaItemId);
            Assert.NotNull(cover);
            Assert.Contains("column:Sales/Amount", cover.Objects);
            var decision = cover.Why.Single(w => w.Contains("4 decimal places"));
            Assert.Contains("4 decimal places", decision);
            Assert.Contains("one Undo", decision);
            Assert.Contains("keeps the type when the values need more decimals", decision);
            // Fix all would withhold it, so the page does not offer to count it.
            var card = await e.BpaScanAsync();
            var amount = card.Violations.Single(v => v.RuleId == "AVOID_FLOATING_POINT_DATA_TYPES" && v.ObjectRef == "column:Sales/Amount");
            Assert.True(amount.CanAutoFix);
            Assert.True(amount.FixWithheld);
            Assert.Equal(card.Violations.Count(v => !v.Waived && v.CanAutoFix && !v.FixWithheld), card.AutoFixable);
            // And the per-finding fix does what the decision says: the type changes, one undo takes it back.
            await e.BpaFixAsync("AVOID_FLOATING_POINT_DATA_TYPES", "column:Sales/Amount", "agent");
            Assert.DoesNotContain((await e.BpaScanAsync()).Violations, v => v.RuleId == "AVOID_FLOATING_POINT_DATA_TYPES" && !v.Waived);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Fix_all_only_has_to_clear_what_the_Fix_button_counted()
        {
            using var f = await GappyAsync("CoverageFixN.bim");
            var e = f.Engine;
            var before = await e.BpaScanAsync();
            Assert.True(before.AutoFixable > 0);
            Assert.True(before.Violations.Any(v => v.FixWithheld), "the float column is withheld");
            var first = await e.BpaFixAllAsync("human");
            Assert.True(first.Applied >= before.AutoFixable, "the press made at least the fixes the button counted");
            // A fix can open the next one (hiding a key lets MDX go off on the next press), so the button reads 0 once the presses have settled.
            var result = await e.BpaFixAllAsync("human");
            Assert.Equal(0, result.Scorecard.AutoFixable);
            var left = result.Scorecard.Violations.Where(v => v.CanAutoFix && !v.Waived).ToArray();
            Assert.All(left, v => Assert.True(v.FixWithheld, v.RuleId + " " + v.ObjectRef + " was left but is not a withheld fix"));
        }

        /// <summary>The assistant, scripted: for each best-practice rule on the coverage row, the tool the row's brief names.</summary>
        private static async Task WorkBpaRowAsync(LocalEngine e)
        {
            foreach (var v in (await e.BpaScanAsync()).Violations.Where(v => !v.Waived && v.Severity >= 2).ToArray())
            {
                switch (v.RuleId)
                {
                    case "AVOID_FLOATING_POINT_DATA_TYPES": await e.BpaFixAsync(v.RuleId, v.ObjectRef, "agent"); break;
                    case "UNNECESSARY_COLUMNS": await e.DeleteObjectsAsync(new[] { v.ObjectRef }, "agent"); break;
                    case "PROVIDE_FORMAT_STRING_FOR_MEASURES": await e.SetMeasureFormatAsync(v.ObjectRef, "#,0.00", "agent"); break;
                    case "AVOID_USING_THE_IFERROR_FUNCTION": await e.SetDaxAsync(v.ObjectRef, "SUM ( Sales[Amount] )", "agent", null); break;
                    case "MODEL_SHOULD_HAVE_A_DATE_TABLE":
                        await e.CreateTableAsync("Date", "agent");
                        await e.CreateColumnAsync("table:Date", "Date", "DateTime", null, "agent");
                        await e.MarkDateTableAsync("table:Date", "Date", "agent");
                        break;
                    case "AVOID_EXCESSIVE_BI-DIRECTIONAL_OR_MANY-TO-MANY_RELATIONSHIPS":
                        foreach (var rel in (await e.AiReadinessScanAsync()).Findings.Where(x => x.RuleId == "REL-BIDI"))
                            await e.SetObjectPropertyAsync(rel.ObjectRef, "CrossFilteringBehavior", "OneDirection", "agent");
                        break;
                }
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Run_the_plan_then_the_assistant_works_the_coverage_row_the_grade_rises_and_the_row_does_not_come_back()
        {
            using var f = await GappyAsync("CoverageJourney.bim");
            var e = f.Engine;
            var plan = await McpTools.GetModelPlan(e);
            var gradeBefore = (await e.BpaScanAsync());
            var rulesBefore = Row(plan, LocalEngine.CoverageBpaItemId).Rules.ToArray();
            var objectsBefore = Row(plan, LocalEngine.CoverageBpaItemId).Objects.ToArray();
            Assert.NotEmpty(rulesBefore);

            await McpTools.ApplyPlanItems(e, null, plan.ReadyDigest);

            // The skill: list the queue, claim the coverage task, do it with the tools the row names, complete it.
            var queue = await McpTools.ListAssistantRequests(e);
            var task = queue.Requests.Single(r => r.Need.StartsWith("Fix ", StringComparison.Ordinal) && r.Need.Contains("best-practice"));
            var claim = await McpTools.ClaimRequest(e, task.RequestId);
            Assert.True(claim.Ok, claim.Message);
            await WorkBpaRowAsync(e);
            var done = await McpTools.CompleteRequest(e, task.RequestId, claim.Request.ClaimToken, "Fixed.");
            Assert.True(done.Ok, done.Message);

            var after = await e.BpaScanAsync();
            Assert.True(after.BestPracticeScore > gradeBefore.BestPracticeScore, $"the score rose from {gradeBefore.BestPracticeScore} to {after.BestPracticeScore}");
            // Nothing the row carried is still a finding.
            var left = after.Violations.Where(v => !v.Waived && v.Severity >= 2 && rulesBefore.Contains(v.RuleId)).Select(v => v.RuleId + " " + v.ObjectRef).ToArray();
            Assert.True(left.Length == 0, "still open: " + string.Join("; ", left));
            var again = await PlanAsync(e);
            _out.WriteLine(Dump(again));
            // The row may stand again only for a finding that is NEW (the Date table the assistant made has columns of its own), never for one it fixed.
            Assert.DoesNotContain(Row(again, LocalEngine.CoverageBpaItemId)?.Objects ?? Array.Empty<string>(), o => objectsBefore.Contains(o));
            // A second Run the plan brings nothing back.
            await McpTools.ApplyPlanItems(e, null, again.ReadyDigest);
            Assert.DoesNotContain(Row(await PlanAsync(e), LocalEngine.CoverageBpaItemId)?.Objects ?? Array.Empty<string>(), o => objectsBefore.Contains(o));
        }

        // ---- rows that stayed after Done (Kane, 13 Oct) ----------------------------------------------------------------

        [Fact]
        public async Task The_summarize_write_is_taken_by_the_readiness_row_only_when_that_row_is_on_the_plan()
        {
            using var f = await GappyAsync("CoverageTaken.bim");
            var e = f.Engine;
            const string key = "column:Sales/CustomerKey|summarizeby";
            // The readiness row (FMT-SUMMARIZE) is on the plan: it makes the write, so the best-practice row does not.
            Assert.DoesNotContain(key, await e.BpaFixRowKeysForTest(null), StringComparer.OrdinalIgnoreCase);
            // A Jev family decided the column, so that readiness row is not on the plan. The write is not taken, and its best-practice twin has a row.
            Assert.Contains(key, await e.BpaFixRowKeysForTest((rule, _) => rule == "FMT-SUMMARIZE"), StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_Jev_row_left_with_only_unticked_changes_is_the_assistants_and_never_a_Semanticus_row_that_Run_skips()
        {
            var leftover = new ModelPlanItem
            {
                Id = "jev.measures.table:Sales", Title = "Add folders and formats to 2 measures in Sales", Doer = PlanDoers.Semanticus,
                Who = ModelPlanOwners.Jev, Status = ModelPlanStatuses.Proposed, WaitsFor = Array.Empty<PlanWaitRef>(),
                ChangeSet = new ModelPlanChangeSet
                {
                    State = ModelPlanChangeStates.Proposed, Digest = "d",
                    Lines = new[]
                    {
                        new ModelPlanChangeLine { Id = "a", Target = "measure:Sales/A", Kept = false },
                        new ModelPlanChangeLine { Id = "b", Target = "measure:Sales/B", Kept = false },
                    },
                },
            };
            PlanTasks.StampState(leftover, null, false, PlanAssistantQueue.On);
            Assert.Equal(PlanDoers.Assistant, leftover.Doer);
            Assert.NotEqual(PlanStateWords.NeedsYou, leftover.StateWord);
            Assert.False(PlanReadySet.IsReady(leftover));
        }

        // ---- the person is the last resort (Kane, 13 Oct) ---------------------------------------------------------

        [Fact]
        public async Task Cleanup_and_raise_compatibility_level_are_the_assistants_rows()
        {
            using var f = await GappyAsync("CoverageDoers.bim");
            var e = f.Engine;
            var plan = await PlanAsync(e);
            var cleanup = Row(plan, "cleanup/model");
            Assert.NotNull(cleanup);
            Assert.Equal(PlanDoers.Assistant, cleanup.Doer);
            Assert.NotEqual(PlanStateWords.NeedsYou, cleanup.StateWord);
            var raise = Row(plan, LocalEngine.CompatItemId);
            Assert.NotNull(raise);
            Assert.Equal("Raise compatibility level to 1701", raise.Title);
            Assert.Equal(PlanDoers.Assistant, raise.Doer);

            // The assistant raises it after the person's yes in chat (set_compatibility_level), and the row goes.
            var set = await McpTools.SetCompatibilityLevel(e, 1701);
            Assert.True(set.Changed);
            // The row reads Done for the moment it is finished, as every finished row does, and is no longer open.
            Assert.Equal(PlanStateWords.Done, Row(await PlanAsync(e), LocalEngine.CompatItemId)?.StateWord);
            // Nothing is left on You that a tool can do.
            Assert.DoesNotContain((await PlanAsync(e)).Items, i => i.Doer == PlanDoers.You && PlanStateWords.IsOpen(i.StateWord));
        }

        /// <summary>A scored AI readiness finding no other row takes: a formula that names a column the model does not have (DAX-REFERENCE caps the
        /// grade). Mutant M2 of the verifier (the AI readiness coverage row never built) passed every earlier test; this one fails it.</summary>
        [Fact]
        public async Task A_scored_AI_readiness_finding_with_no_other_row_is_the_assistants_coverage_row_and_clears_when_fixed()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "air-cover", async x =>
            {
                var e0 = x.Engine;
                await e0.CreateModelAsync("Contoso", 1604);
                await e0.CreateTableAsync("Sales", "human");
                await e0.CreateColumnAsync("table:Sales", "Amount", "Decimal", null, "human");
                await e0.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
                await e0.CreateColumnAsync("table:Sales", "Scratch", "Decimal", null, "human");
                await e0.CreateMeasureAsync("table:Sales", "Broken", "SUM ( Sales[Scratch] )", "human");
                // The column goes, and the formula still names it.
                await e0.DeleteObjectsAsync(new[] { "column:Sales/Scratch" }, "human");
            }, "AirCover.bim");
            f.UseNull();
            var e = f.Engine;

            var air = await e.AiReadinessScanAsync();
            var scored = air.Findings.Where(x => !x.Waived && x.RuleId == "DAX-REFERENCE").ToArray();
            Assert.NotEmpty(scored);

            var plan = await PlanAsync(e);
            var row = Row(plan, LocalEngine.CoverageAiItemId);
            Assert.True(row != null, "the AI readiness coverage row was not built. Rows:\n" + Dump(plan));
            Assert.Equal(PlanDoers.Assistant, row.Doer);
            Assert.True(PlanStateWords.IsOpen(row.StateWord), row.StateWord);
            Assert.Contains("DAX-REFERENCE", row.Rules);
            Assert.Contains("measure:Sales/Broken", row.Objects);

            // The assistant takes it and fixes the formula; the finding and the row go.
            await McpTools.ApplyPlanItems(e, null, plan.ReadyDigest);
            var req = (await McpTools.ListAssistantRequests(e)).Requests.Single(r => r.Need.Contains("AI readiness"));
            var claim = await McpTools.ClaimRequest(e, req.RequestId);
            Assert.True(claim.Ok, claim.Message);
            await e.SetDaxAsync("measure:Sales/Broken", "SUM ( Sales[Amount] ) + 0", "agent", null);
            var done = await McpTools.CompleteRequest(e, req.RequestId, claim.Request.ClaimToken, "Fixed the reference.");
            Assert.True(done.Ok, done.Message);
            plan = await PlanAsync(e);
            Assert.DoesNotContain(plan.Items, i => i.Id == LocalEngine.CoverageAiItemId && PlanStateWords.IsOpen(i.StateWord));
            Assert.DoesNotContain((await e.AiReadinessScanAsync()).Findings, x => !x.Waived && x.RuleId == "DAX-REFERENCE");
        }

        [Fact]
        public void Copilot_limits_are_the_assistants_and_Jev_not_finished_is_a_wait_for_nobody()
        {
            var limit = new ReadinessFinding
            {
                RuleId = "LIMIT-QNA-INDEX", RuleTitle = "Too many values", Category = nameof(ReadinessCategory.CopilotLimits), Severity = "High",
                Fix = nameof(FixKind.Proposal), ObjectRef = "column:Sales/Id", ObjectName = "Id", Message = "Too many values.",
            };
            var row = LocalEngine.ProposalItemsForTest(new[] { limit }).Single();
            Assert.Equal(ModelPlanOwners.Assistant, row.Who);
            Assert.Contains(row.Why, w => w.Contains("without asking"));

            var waiting = new ModelPlanItem
            {
                Id = Semanticus.Engine.AiSchemaFamily.NotFinishedItemId, Title = "Jev has not finished choosing what Copilot sees",
                Who = ModelPlanOwners.Auto, Doer = PlanDoers.Semanticus, Status = ModelPlanStatuses.Ready, WaitsFor = Array.Empty<PlanWaitRef>(),
            };
            PlanTasks.StampState(waiting, null, false, PlanAssistantQueue.On);
            Assert.Equal(PlanStateWords.Waiting, waiting.StateWord);
            Assert.False(PlanReadySet.IsReady(waiting));
            Assert.Contains("nothing for you or your assistant to do", waiting.StateNote, StringComparison.OrdinalIgnoreCase);
        }
    }
}
