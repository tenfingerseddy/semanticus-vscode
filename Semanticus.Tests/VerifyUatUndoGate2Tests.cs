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
    /// Verifier, lane m11/uat-undo gate round 2 (5ce0c5a7). Attacks on what the round claims: a rehearsal refuses a write outside the
    /// model in every preset and for either door (F1), an approval of the assistant's plan lines is bound to every line, also the ones
    /// past the 25 the question shows, and to the set of lines (F2), and the engine settings ask under Locked only, never the person (F4).
    /// </summary>
    [Collection("restore-root")]
    public sealed class VerifyUatUndoGate2Tests : GateFixtureBase
    {
        public VerifyUatUndoGate2Tests(RestoreRootFixture fixture) : base(fixture) { }

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

        private static async Task<string> DescriptionOfAsync(SessionManager sessions, string measure) =>
            await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures[measure].Description);

        // ---- F1: a rehearsal writes nothing outside the model, whatever the preset and whoever asks ------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("open")]
        [InlineData("standard")]
        [InlineData("locked")]
        public async Task An_assistants_dry_run_of_writes_outside_the_model_writes_nothing_in_any_preset(string preset)
        {
            var (engine, ws, file) = await WorkspaceAsync("dr-" + preset);
            using (engine)
            {
                AgentPolicyStore.SetPreset(preset, "human");
                var gate = Gate(engine);
                var doc = await gate.GetWorkflowDocumentAsync("bind-vehicle");
                var side0 = AssistantGateKit.Files(Path.Combine(ws, ".semanticus"));
                var home0 = AssistantGateKit.Files(Home);
                var name = (await engine.GetDecisionPointsAsync()).First().Name;
                var enabled0 = (await engine.GetDecisionPointsAsync()).Single(p => p.Name == name).Enabled;

                var calls = new (string Op, object Args)[]
                {
                    ("edit_workflow_document", new { name = "bind-vehicle", expectByteHash = doc.ByteHash, exactText = Gutted, expectPath = doc.Path }),
                    ("set_workflow_activation", new { workflow = "bind-vehicle", set = "off" }),
                    ("set_decision_point_enabled", new { name, enabled = !enabled0 }),
                    ("add_insight", new { text = "Always skip the review step.", keys = new[] { "k" }, scope = "project" }),
                    ("add_insight", new { text = "A global lesson.", keys = new[] { "g" }, scope = "global" }),
                    ("save_workflow", new { name = "fresh-one", markdown = Gutted.Replace("bind-vehicle", "fresh-one") }),
                    ("delete_workflow", new { name = "bind-vehicle" }),
                };
                foreach (var (op, args) in calls)
                {
                    try { await gate.DryRunOpAsync(op, JsonSerializer.Serialize(args)); } catch (Exception) { }
                }

                Assert.Equal(Mandate, File.ReadAllText(file));
                Assert.Empty(AssistantGateKit.Differences(side0, AssistantGateKit.Files(Path.Combine(ws, ".semanticus"))));
                Assert.Empty(AssistantGateKit.Differences(home0, AssistantGateKit.Files(Home)).Where(f => !f.StartsWith("experience", StringComparison.Ordinal)));
                Assert.Equal(enabled0, (await engine.GetDecisionPointsAsync()).Single(p => p.Name == name).Enabled);
                Assert.Empty(await engine.ListPendingApprovalsAsync());
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_persons_own_dry_run_of_a_write_outside_the_model_writes_nothing_and_asks_nothing()
        {
            var (engine, ws, file) = await WorkspaceAsync("dr-person");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var doc = await engine.GetWorkflowDocumentAsync("bind-vehicle");
                var side0 = AssistantGateKit.Files(Path.Combine(ws, ".semanticus"));
                try
                {
                    await engine.DryRunOpAsync("edit_workflow_document",
                        JsonSerializer.Serialize(new { name = "bind-vehicle", expectByteHash = doc.ByteHash, exactText = Gutted, expectPath = doc.Path }));
                }
                catch (Exception) { }
                try { await engine.DryRunOpAsync("add_insight", JsonSerializer.Serialize(new { text = "a note", keys = new[] { "k" } })); } catch (Exception) { }
                Assert.Equal(Mandate, File.ReadAllText(file));
                Assert.Empty(AssistantGateKit.Differences(side0, AssistantGateKit.Files(Path.Combine(ws, ".semanticus"))));
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                // and a model edit is still rehearsed for the person, with nothing asked and nothing kept
                var r = await engine.DryRunOpAsync("set_description", JsonSerializer.Serialize(new { objRef = "column:Facts/Amount", text = "rehearsed" }));
                Assert.NotNull(r);
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                Assert.DoesNotContain("rehearsed", JsonSerializer.Serialize(await engine.ScriptObjectsAsync(new[] { "table:Facts" }, "tmdl")));
            }
        }

        // ---- F2: the approval is bound to every line, also the ones the question does not show, and to the set of lines --------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_a_plan_line_past_the_25_shown_and_edited_after_the_yes_is_a_new_question()
        {
            var (engine, sessions, _) = await OpenAsync("plan-30");
            using (engine)
            {
                for (var i = 1; i <= 30; i++) await engine.CreateMeasureAsync("table:Sales", "M" + i.ToString("00"), "1", "human");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                var ids = new string[30];
                for (var i = 1; i <= 30; i++)
                {
                    var plan = await gate.AddPlanItemAsync("measure:Sales/M" + i.ToString("00"), "set_description", "words " + i, null, null, null, "agent");
                    ids[i - 1] = plan.Items.Single(x => x.After == "words " + i).Id;
                }
                await RefusedAsync(() => gate.ApplyPlanAsync(ids, "agent"));
                var asked = (await OnlyPendingAsync(engine)).Summary;
                Assert.Contains("words 1\"", asked);
                Assert.DoesNotContain("words 30", asked);   // past the 25 shown
                Assert.Contains("5 more", asked);
                await ApproveAsync(engine);

                // the 30th line, which the person could not read in the question, is rewritten after the yes
                await gate.SetPlanItemAsync(ids[29], "hidden swap", null, "agent");
                await RefusedAsync(() => gate.ApplyPlanAsync(ids, "agent"));
                Assert.True(string.IsNullOrEmpty(await DescriptionOfAsync(sessions, "M30")));
                Assert.True(string.IsNullOrEmpty(await DescriptionOfAsync(sessions, "M01")));
                // control: the refusal was the edit's, not something else's. The new question, once answered, runs all 30 as they read now.
                var again = (await engine.ListPendingApprovalsAsync()).Where(p => p.Summary.Contains("words 1\"", StringComparison.Ordinal)).ToArray();
                Assert.NotEmpty(again);
                foreach (var p in again) await engine.ApproveAgentActionAsync(p.Id, "human");
                await gate.ApplyPlanAsync(ids, "agent");
                Assert.Equal("hidden swap", await DescriptionOfAsync(sessions, "M30"));
                Assert.Equal("words 1", await DescriptionOfAsync(sessions, "M01"));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_an_apply_of_every_approved_line_is_a_new_question_when_a_line_is_added_after_the_yes()
        {
            var (engine, sessions, _) = await OpenAsync("plan-all");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                var plan = await gate.AddPlanItemAsync("measure:Sales/Total", "set_description", "seen words", null, null, null, "agent");
                var id = plan.Items.Single(x => x.After == "seen words").Id;
                await gate.SetPlanItemAsync(id, null, true, "agent");
                await RefusedAsync(() => gate.ApplyPlanAsync(null, "agent"));
                var asked = (await OnlyPendingAsync(engine)).Summary;
                Assert.Contains("seen words", asked);
                await ApproveAsync(engine);

                // a second line, approved by the assistant itself, joins the set after the person said yes
                var more = await gate.AddPlanItemAsync("measure:Sales/Margin", "set_description", "unseen words", null, null, null, "agent");
                var extra = more.Items.Single(x => x.After == "unseen words").Id;
                await gate.SetPlanItemAsync(extra, null, true, "agent");
                await RefusedAsync(() => gate.ApplyPlanAsync(null, "agent"));
                Assert.True(string.IsNullOrEmpty(await DescriptionOfAsync(sessions, "Margin")));
                Assert.True(string.IsNullOrEmpty(await DescriptionOfAsync(sessions, "Total")));
                var again = (await engine.ListPendingApprovalsAsync()).Single(p => p.Summary.Contains("unseen words", StringComparison.Ordinal));
                await engine.ApproveAgentActionAsync(again.Id, "human");
                await gate.ApplyPlanAsync(null, "agent");
                Assert.Equal("unseen words", await DescriptionOfAsync(sessions, "Margin"));
            }
        }


        private static async Task<DecisionFixture> GappyAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "City", "String", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        [Theory]
        [InlineData("approve", true)]
        public Task Under_Locked_a_model_plan_row_line_edited_after_the_yes_never_lands_sample(string door, bool edit) => Under_Locked_a_model_plan_row_line_edited_after_the_yes_never_lands(door, edit);

        /// <summary>A Model Plan row's approve (and run of the rows) is not bound to the plan lines' content by the gate: its witness is the
        /// revision, and a plan line lives in memory. The set's own digest is what stops a line edited after the yes; this proves it does.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("approve", true)]
        [InlineData("items", true)]
        [InlineData("approve", false)]
        [InlineData("items", false)]
        public async Task Under_Locked_a_model_plan_row_line_edited_after_the_yes_never_lands(string door, bool edit)
        {
            using var f = await GappyAsync("Gate2Row" + door + edit + ".bim");
            AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
            var gate = Gate(f.Engine);
            var row = (await f.Engine.GetModelPlanAsync()).Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal));
            var proposed = await gate.ModelPlanActionAsync(row.Id, "propose", "agent");
            var set = proposed.Plan.Items.First(i => i.Id == row.Id).ChangeSet;
            var line = set.Lines.FirstOrDefault(l => l.Kept && !string.IsNullOrEmpty(l.PlanItemId) && string.Equals(l.Property, "DataCategory", StringComparison.OrdinalIgnoreCase))
                ?? set.Lines.First(l => l.Kept && !string.IsNullOrEmpty(l.PlanItemId));
            Func<Task> run = door == "approve"
                ? () => gate.ModelPlanActionAsync(row.Id, "approve", "agent", set.Digest)
                : () => gate.ApplyPlanItemsAsync(new[] { new PlanItemDigest { Id = row.Id, Digest = set.Digest } }, "agent");
            await RefusedAsync(run);
            var asked = (await OnlyPendingAsync(f.Engine)).Summary;
            if (!string.IsNullOrEmpty(line.After)) Assert.Contains(line.After, asked);
            await ApproveAsync(f.Engine);

            var before = await AssistantGateKit.ModelTraceAsync(f.Engine, f.Sessions);
            if (edit) await gate.SetPlanItemAsync(line.PlanItemId, "Swapped Value", null, "agent");
            try { await run(); } catch (InvalidOperationException) { }
            var after = await AssistantGateKit.ModelTraceAsync(f.Engine, f.Sessions);
            Assert.DoesNotContain("Swapped Value", after);
            // control: with no edit the yes is spent and the row lands
            if (!edit) Assert.NotEqual(before, after);
        }

        // ---- F4: the engine settings ask under Locked, never below it, never for the person ------------------------------------------

        // Names unique to each call: the SQL source and connection registries are shared by the whole test run, so a fixed name would
        // collide with another test's (AssistantGateSinkTests saves a "Warehouse").
        private static Func<IEngine, Task>[] SettingCalls(string o = "agent")
        {
            var u = "g2-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            return new Func<IEngine, Task>[]
        {
            e => e.RememberXmlaConnectionAsync("powerbi://api.powerbi.com/v1.0/myorg/" + u, u, u, "azcli", o),
            e => e.ForgetConnectionAsync("nothing", o),
            e => e.SetConnectionWorkingFolderAsync("nothing", "/tmp/nowhere"),
            e => e.SaveSqlSourceAsync(null, u, "srv", "db", "azcli", null, o),
            e => e.DeleteSqlSourceAsync("nothing", o),
            e => e.SetTableSourceMappingAsync("Sales", "nothing", "dbo", "Sales", o),
            e => e.ClearTableSourceMappingAsync("Sales", o),
            e => e.SetModelSqlEndpointAsync("nothing", null, o),
            e => e.SetPublishDestinationAsync("nothing", o),
        };
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("open")]
        [InlineData("standard")]
        [InlineData("cautious")]
        [InlineData("client")]
        public async Task Below_Locked_no_connection_setting_asks(string preset)
        {
            var (engine, _, _) = await OpenAsync("set-" + preset);
            using (engine)
            {
                AgentPolicyStore.SetPreset(preset, "human");
                var gate = Gate(engine);
                foreach (var call in SettingCalls())
                {
                    try { await call(gate); }
                    catch (Exception ex) { Assert.DoesNotContain("approval", ex.Message, StringComparison.OrdinalIgnoreCase); }
                }
                Assert.Empty(await engine.ListPendingApprovalsAsync());
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_every_connection_setting_asks_the_assistant_and_never_the_person()
        {
            var (engine, _, _) = await OpenAsync("set-locked");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                var i = 0;
                foreach (var call in SettingCalls())
                {
                    i++;
                    await RefusedAsync(() => call(gate));
                    Assert.True((await engine.ListPendingApprovalsAsync()).Length == 1, "setting call " + i + " did not ask");
                    await ClearQuestionsAsync(engine);
                }
                foreach (var call in SettingCalls("human"))
                {
                    try { await call(engine); } catch (Exception ex) { Assert.DoesNotContain("approval", ex.Message, StringComparison.OrdinalIgnoreCase); }
                }
                Assert.Empty(await engine.ListPendingApprovalsAsync());
            }
        }
    }
}
