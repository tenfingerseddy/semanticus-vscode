using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier, lane m11/uat-undo redesign (06ac25f8, one gate door for Locked). The design says the gated engine is the only engine
    /// an assistant holds. dry_run is a Pass row, and it runs the named tool's method with the INNER engine as its receiver
    /// (LocalEngine.DryRun.cs BindArgs: args[0] = this), behind a deny-list. Every write the deny-list misses that lands outside the
    /// model's undo (a sidecar file, the workflow settings, a decision switch) is a road around the door: it is written for real,
    /// under Locked, with no question. Also the race: one approval, two calls in flight.
    /// </summary>
    [Collection("restore-root")]
    public sealed class VerifyUatUndoGateTests : GateFixtureBase
    {
        public VerifyUatUndoGateTests(RestoreRootFixture fixture) : base(fixture) { }

        private const string Mandate =
            "---\nname: bind-vehicle\ntitle: Binding vehicle\nwhenToUse: \"Author a measure the reviewed way.\"\n---\n## Step 1: Author the measure\nCreate the measure at this step.\n```yaml gate\nops: [create_measure]\n```\n## Step 2: Review\nReview what you made.\n";

        private const string Gutted =
            "---\nname: bind-vehicle\ntitle: Binding vehicle\nwhenToUse: \"Author a measure the reviewed way.\"\n---\n## Step 1: Nothing\nNothing to check.\n";

        private async Task<(LocalEngine engine, string ws, string file)> WorkspaceAsync(string tag)
        {
            var ws = Path.Combine(Root, tag);
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            var file = Path.Combine(ws, ".semanticus", "workflows", "bind-vehicle.md");
            File.WriteAllText(file, Mandate);
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro, ws);
            await engine.CreateModelAsync("BindTest", 1701);
            var t = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            await engine.SetWorkflowBindingAsync("create_measure", new[] { "bind-vehicle" }, "hard", "human");
            return (engine, ws, file);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_an_assistants_dry_run_does_not_rewrite_a_workflow_the_person_bound_it_to()
        {
            var (engine, _, file) = await WorkspaceAsync("dr-doc");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                var doc = await gate.GetWorkflowDocumentAsync("bind-vehicle");
                // the door's own row asks for the same write
                await RefusedAsync(() => gate.EditWorkflowDocumentAsync("bind-vehicle", doc.ByteHash, Gutted, doc.Path, "agent"));
                await ClearQuestionsAsync(engine);

                var args = JsonSerializer.Serialize(new { name = "bind-vehicle", expectByteHash = doc.ByteHash, exactText = Gutted, expectPath = doc.Path });
                try { await gate.DryRunOpAsync("edit_workflow_document", args); } catch (InvalidOperationException) { }

                Assert.Equal(Mandate, File.ReadAllText(file));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_an_assistants_dry_run_does_not_hide_a_workflow_the_person_bound_it_to()
        {
            var (engine, ws, _) = await WorkspaceAsync("dr-act");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.SetWorkflowActivationAsync("bind-vehicle", null, "off", "agent"));
                await ClearQuestionsAsync(engine);
                var before = AssistantGateKit.Files(Path.Combine(ws, ".semanticus"));

                var args = JsonSerializer.Serialize(new { workflow = "bind-vehicle", set = "off" });
                try { await gate.DryRunOpAsync("set_workflow_activation", args); } catch (InvalidOperationException) { }

                var after = AssistantGateKit.Files(Path.Combine(ws, ".semanticus"));
                Assert.Empty(AssistantGateKit.Differences(before, after));
            }
        }

        [Fact]
        public async Task Under_Locked_an_assistants_dry_run_does_not_turn_on_a_Jev_question_the_person_turned_off()
        {
            var (engine, _, path) = await OpenAsync("dr-jev");
            using (engine)
            {
                var name = (await engine.GetDecisionPointsAsync()).First().Name;
                await engine.SetDecisionPointEnabledAsync(name, false, "human");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.SetDecisionPointEnabledAsync(name, true, "agent"));
                await ClearQuestionsAsync(engine);

                var args = JsonSerializer.Serialize(new { name, enabled = true });
                try { await gate.DryRunOpAsync("set_decision_point_enabled", args); } catch (InvalidOperationException) { }

                Assert.False((await engine.GetDecisionPointsAsync()).Single(p => p.Name == name).Enabled);
            }
        }

        /// <summary>Ruled Q1: an opaque composite (apply_plan) gets a read-only describe hook listing its items. The item's value is set by
        /// the assistant on a Pass row (add_plan_item), so a card that names only the item's id asks the person to approve what they
        /// cannot see.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_the_question_for_an_assistants_apply_plan_names_the_object_and_the_new_value()
        {
            var (engine, _, _) = await OpenAsync("plan-words");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                var plan = await gate.AddPlanItemAsync("measure:Sales/Total", "rename", "Renamed By Assistant", null, null, null, "agent");
                var id = plan.Items.Single(i => i.After == "Renamed By Assistant").Id;
                await RefusedAsync(() => gate.ApplyPlanAsync(new[] { id }, "agent"));
                var asked = (await OnlyPendingAsync(engine)).Summary;
                Assert.Contains("Total", asked);
                Assert.Contains("Renamed By Assistant", asked);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_one_approval_is_spent_once_by_two_calls_in_flight()
        {
            var (engine, sessions, _) = await OpenAsync("race");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.SetDescriptionAsync("measure:Sales/Total", "raced words", "agent"));
                await ApproveAsync(engine);
                var calls = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
                {
                    try { await gate.SetDescriptionAsync("measure:Sales/Total", "raced words", "agent"); return true; }
                    catch (InvalidOperationException) { return false; }
                })).ToArray();
                var ran = (await Task.WhenAll(calls)).Count(x => x);
                Assert.Equal(1, ran);
                Assert.Equal("raced words", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description));
            }
        }
    }
}
