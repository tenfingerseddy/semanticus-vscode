using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// The assistant's gate, Q5 items 5 and 6 (lane m11/uat-undo). An approval is that one call, on that model, at that revision, for
    /// that path as it really is: approve once, then change one thing at a time, and each asks again, while the unchanged retry runs
    /// exactly once. And the person's own door asks nobody, in every preset.
    /// </summary>
    [Collection("restore-root")]
    public sealed class AssistantGateBindingTests : GateFixtureBase
    {
        public AssistantGateBindingTests(RestoreRootFixture fixture) : base(fixture) { }

        private static async Task<string> DescriptionAsync(SessionManager sessions, string measure = "Total") =>
            await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures[measure].Description);

        // ---- the model's own revision and the call's own arguments ---------------------------------------------------------

        [Fact]
        public async Task The_unchanged_retry_runs_exactly_once()
        {
            var (engine, sessions, _) = await OpenAsync("once");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                var refused = await RefusedAsync(() => gate.SetDescriptionAsync("measure:Sales/Total", "approved words", "agent"));
                Assert.Contains("approval", refused.Message, StringComparison.OrdinalIgnoreCase);
                var asked = await OnlyPendingAsync(engine);
                Assert.StartsWith("Your assistant wants to set the description of [Total] to: \"approved words\".", asked.Summary);
                Assert.Contains(" This is about the model \"", asked.Summary);
                await ApproveAsync(engine);
                Assert.True((await gate.SetDescriptionAsync("measure:Sales/Total", "approved words", "agent")).Changed);
                Assert.Equal("approved words", await DescriptionAsync(sessions));
                // the second use is a new question
                await RefusedAsync(() => gate.SetDescriptionAsync("measure:Sales/Total", "approved words", "agent"));
                Assert.Single(await engine.ListPendingApprovalsAsync());
            }
        }

        [Theory]
        [InlineData("other text")]
        [InlineData("other object")]
        [InlineData("other method")]
        [InlineData("a person's edit")]
        [InlineData("another model")]
        [InlineData("the same file opened again")]
        public async Task After_one_approval_changing_one_thing_asks_again(string variation)
        {
            var (engine, sessions, path) = await OpenAsync("var-" + Math.Abs(variation.GetHashCode()).ToString("x"));
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.SetDescriptionAsync("measure:Sales/Total", "approved words", "agent"));
                await ApproveAsync(engine);
                string[] watched = { "Total", "Margin" };
                Func<Task> vary;
                switch (variation)
                {
                    case "other text": vary = () => gate.SetDescriptionAsync("measure:Sales/Total", "other words", "agent"); break;
                    case "other object": vary = () => gate.SetDescriptionAsync("measure:Sales/Margin", "approved words", "agent"); break;
                    case "other method": vary = () => gate.SetObjectPropertyAsync("measure:Sales/Total", "Description", "approved words", "agent"); break;
                    case "a person's edit":
                        await engine.SetDescriptionAsync("measure:Sales/Margin", "the person's own words", "human");
                        vary = () => gate.SetDescriptionAsync("measure:Sales/Total", "approved words", "agent"); break;
                    case "another model":
                    {
                        var copy = AssistantGateKit.WriteModel(Root, "var-copy");
                        await engine.OpenAsync(copy);   // a different model, with the same measure names
                        vary = () => gate.SetDescriptionAsync("measure:Sales/Total", "approved words", "agent"); break;
                    }
                    case "the same file opened again":
                        await engine.OpenAsync(path);   // a new session on the same file
                        vary = () => gate.SetDescriptionAsync("measure:Sales/Total", "approved words", "agent"); break;
                    default: throw new InvalidOperationException(variation);
                }
                var ex = await RefusedAsync(vary);
                Assert.Contains("approval", ex.Message, StringComparison.OrdinalIgnoreCase);
                foreach (var w in watched)
                    Assert.True(string.IsNullOrEmpty(await DescriptionAsync(sessions, w)) || (variation == "a person's edit" && w == "Margin"),
                        variation + ": the approval was spent: " + w + " now reads \"" + await DescriptionAsync(sessions, w) + "\"");
            }
        }

        [Fact]
        public async Task A_wrong_try_does_not_spend_the_approval_the_person_gave()
        {
            var (engine, sessions, _) = await OpenAsync("wrong");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.SetDescriptionAsync("measure:Sales/Total", "approved words", "agent"));
                await ApproveAsync(engine);
                await RefusedAsync(() => gate.SetDescriptionAsync("measure:Sales/Total", "other words", "agent"));
                Assert.True((await gate.SetDescriptionAsync("measure:Sales/Total", "approved words", "agent")).Changed);
                Assert.Equal("approved words", await DescriptionAsync(sessions));
            }
        }

        // ---- a walk ------------------------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_approved_walk_is_not_spent_on_a_longer_one_and_not_on_a_model_that_moved()
        {
            var (engine, sessions, _) = await OpenAsync("walk");
            using (engine)
            {
                await engine.SetDescriptionAsync("measure:Sales/Total", "first", "human");
                await engine.SetDescriptionAsync("measure:Sales/Total", "second", "human");
                await engine.SetDescriptionAsync("measure:Sales/Total", "third", "human");
                var rows = (await engine.GetPlanHistoryAsync()).Rows.Where(r => r.Kind == "edit").ToArray();   // newest first
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.UndoToEntryAsync(rows[0].Id, "agent"));
                var asked = await OnlyPendingAsync(engine);
                Assert.Contains("undo one change", asked.Summary);
                Assert.Contains("third", asked.Summary);
                await ApproveAsync(engine);
                await RefusedAsync(() => gate.UndoToEntryAsync(rows[2].Id, "agent"));   // three steps, not one
                Assert.Equal("third", await DescriptionAsync(sessions));
                await engine.SetDescriptionAsync("measure:Sales/Margin", "a person's words", "human");   // the model moved
                await RefusedAsync(() => gate.UndoToEntryAsync(rows[0].Id, "agent"));
                Assert.Equal("third", await DescriptionAsync(sessions));
            }
        }

        // ---- discarding unsaved work ---------------------------------------------------------------------------------------

        [Fact]
        public async Task An_approval_to_discard_unsaved_work_is_not_spent_on_work_done_after_it_nor_on_another_file()
        {
            var (engine, sessions, path) = await OpenAsync("discard");
            using (engine)
            {
                await engine.SetDescriptionAsync("measure:Sales/Total", "a small unsaved edit", "human");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.OpenAsync(path, true));
                var asked = await OnlyPendingAsync(engine);
                Assert.Contains("will be lost", asked.Summary);
                await ApproveAsync(engine);
                await engine.SetDescriptionAsync("measure:Sales/Margin", "an hour of the person's later work", "human");
                await RefusedAsync(() => gate.OpenAsync(path, true));
                Assert.Equal("an hour of the person's later work", await DescriptionAsync(sessions, "Margin"));
                // and an approval is for the file it named
                await ClearQuestionsAsync(engine);
                await RefusedAsync(() => gate.OpenAsync(path, true));
                await ApproveAsync(engine);
                await RefusedAsync(() => gate.OpenAsync(AssistantGateKit.WriteModel(Root, "discard-other"), true));
                Assert.Equal("an hour of the person's later work", await DescriptionAsync(sessions, "Margin"));
                await gate.OpenAsync(path, true);   // the one it did approve still goes
                Assert.True(string.IsNullOrEmpty(await DescriptionAsync(sessions, "Margin")));
            }
        }

        // ---- a path as it really is -------------------------------------------------------------------------------------

        [Theory]
        [InlineData("symlinked parent")]
        [InlineData("symlinked leaf")]
        [InlineData("a file that appears")]
        [InlineData("changed content")]
        public async Task After_one_approval_a_change_to_the_path_asks_again(string variation)
        {
            var (engine, sessions, modelPath) = await OpenAsync("path-" + Math.Abs(variation.GetHashCode()).ToString("x"));
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                var dir = Path.Combine(Root, "out-" + Math.Abs(variation.GetHashCode()).ToString("x"));
                Directory.CreateDirectory(dir);
                var inner = Path.Combine(dir, "inner");
                Directory.CreateDirectory(inner);
                var target = variation == "symlinked parent" ? Path.Combine(inner, "x.vpax") : Path.Combine(dir, "x.vpax");
                if (variation == "changed content") File.WriteAllText(target, "version one");
                await RefusedAsync(() => gate.ExportVpaxAsync(target, "agent"));
                await ApproveAsync(engine);
                var modelBytes = File.ReadAllBytes(modelPath);
                var elsewhere = Path.Combine(dir, "elsewhere");
                Directory.CreateDirectory(elsewhere);
                switch (variation)
                {
                    case "symlinked parent":
                        Directory.Move(inner, Path.Combine(dir, "inner-moved"));
                        Directory.CreateSymbolicLink(inner, elsewhere);
                        break;
                    case "symlinked leaf":
                        File.CreateSymbolicLink(target, modelPath);
                        break;
                    case "a file that appears":
                        File.WriteAllText(target, "someone else's file");
                        break;
                    case "changed content":
                        File.WriteAllText(target, "version two");
                        break;
                }
                await RefusedAsync(() => gate.ExportVpaxAsync(target, "agent"));
                Assert.Equal(modelBytes, File.ReadAllBytes(modelPath));
                Assert.Empty(Directory.GetFiles(elsewhere));
                if (variation == "a file that appears") Assert.Equal("someone else's file", File.ReadAllText(target));
                if (variation == "changed content") Assert.Equal("version two", File.ReadAllText(target));
            }
        }

        [Fact]
        public async Task A_new_file_is_written_once_when_the_person_approved_it_and_then_it_asks_again()
        {
            var (engine, sessions, _) = await OpenAsync("path-once");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                var target = Path.Combine(Root, "once.vpax");
                var refused = await RefusedAsync(() => gate.ExportVpaxAsync(target, "agent"));
                Assert.Contains("approval", refused.Message, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(target));
                await ApproveAsync(engine);
                var result = await gate.ExportVpaxAsync(target, "agent");
                Assert.True(result.Exported, result.Error);
                Assert.True(File.Exists(target));
                await RefusedAsync(() => gate.ExportVpaxAsync(target, "agent"));   // now it exists: a different question
            }
        }

        [Fact]
        public async Task A_save_as_is_bound_to_the_place_it_names_and_the_model_files_it_would_leave()
        {
            var (engine, sessions, modelPath) = await OpenAsync("saveas");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                var target = Path.Combine(Root, "saveas-out");
                var refused = await RefusedAsync(() => gate.SaveAsync(target, "TMDL", true, "agent"));
                Assert.Contains("approval", refused.Message, StringComparison.OrdinalIgnoreCase);
                Assert.False(Directory.Exists(target));
                await ApproveAsync(engine);
                Directory.CreateDirectory(target);   // somebody put a folder where the person was told there was none
                File.WriteAllText(Path.Combine(target, "theirs.txt"), "not the model");
                await RefusedAsync(() => gate.SaveAsync(target, "TMDL", true, "agent"));
                Assert.Equal("not the model", File.ReadAllText(Path.Combine(target, "theirs.txt")));
                Assert.Single(Directory.GetFileSystemEntries(target));
                // the question as it stands now is the one approved
                await ClearQuestionsAsync(engine);
                await RefusedAsync(() => gate.SaveAsync(target, "TMDL", true, "agent"));
                await ApproveAsync(engine);
                await gate.SaveAsync(target, "TMDL", true, "agent");
                Assert.True(Directory.Exists(Path.Combine(target, "tables")));
            }
        }

        // ---- Q5 item 6: the person's own door asks nobody, in every preset ---------------------------------------------------

        [Theory]
        [InlineData("locked")]
        public Task The_persons_own_door_asks_zero_times_in_every_preset_sample(string preset) => The_persons_own_door_asks_zero_times_in_every_preset(preset);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("open")]
        [InlineData("standard")]
        [InlineData("cautious")]
        [InlineData("client")]
        [InlineData("locked")]
        public async Task The_persons_own_door_asks_zero_times_in_every_preset(string preset)
        {
            var (engine, sessions, path) = await OpenAsync("person-" + preset);
            using (engine)
            {
                AgentPolicyStore.SetPreset(preset, "human");
                var door = new EngineRpcTarget(engine, "human");
                await door.setDescription("measure:Sales/Total", "the person's words", "human");
                await engine.SetDescriptionAsync("measure:Sales/Margin", "more of the person's words", "human");
                await engine.CreateMeasureAsync("table:Sales", "Mine", "1", "human");
                await engine.RenameObjectAsync("measure:Sales/Mine", "Yours", "human");
                await engine.DeleteObjectAsync("measure:Sales/Yours", "human");
                await engine.UndoAsync("human");
                await engine.RedoAsync("human");
                await engine.SetPerspectiveMemberAsync("perspective:View", "measure:Sales/Total", true, "human");
                var saved = await engine.SaveAsync(null, "BIM", true, "human");
                Assert.False(string.IsNullOrEmpty(saved.Path));
                var vpax = await engine.ExportVpaxAsync(Path.Combine(Root, "person.vpax"), "human");
                Assert.True(vpax.Exported, vpax.Error);
                await engine.SetDescriptionAsync("measure:Sales/Total", "unsaved words", "human");
                await door.open(path, discardUnsaved: true);   // throwing away their own unsaved work is theirs to do
                var scaffold = await engine.CicdGenerateAsync("github", null, "PROD", true, "human");
                Assert.True(scaffold.Written, scaffold.Error);
                await engine.SetWorkflowBindingAsync("create_measure", Array.Empty<string>(), "off", "human");
                Assert.Empty(await engine.ListPendingApprovalsAsync());
            }
        }

        // ---- the plan's lines are part of what an apply_plan approval is for ------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_a_plan_line_edited_after_the_yes_is_a_new_question()
        {
            var (engine, sessions, _) = await OpenAsync("plan-bind");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                var plan = await gate.AddPlanItemAsync("measure:Sales/Total", "set_description", "first words", null, null, null, "agent");
                var id = plan.Items.Single(i => i.After == "first words").Id;
                await RefusedAsync(() => gate.ApplyPlanAsync(new[] { id }, "agent"));
                var asked = (await OnlyPendingAsync(engine)).Summary;
                Assert.Contains("[Total]", asked);
                Assert.Contains("first words", asked);
                await ApproveAsync(engine);

                // the line is rewritten (a Pass row: the assistant owns its own proposal) after the person said yes to the old words
                await gate.SetPlanItemAsync(id, "second words", null, "agent");
                await RefusedAsync(() => gate.ApplyPlanAsync(new[] { id }, "agent"));
                Assert.True(string.IsNullOrEmpty(await DescriptionAsync(sessions)));
                var again = (await engine.ListPendingApprovalsAsync()).Single(p => p.Summary.Contains("second words"));   // the yes to the old words is still there, unspent
                Assert.DoesNotContain("first words", again.Summary);

                await engine.ApproveAgentActionAsync(again.Id, "human");
                await gate.ApplyPlanAsync(new[] { id }, "agent");
                Assert.Equal("second words", await DescriptionAsync(sessions));
            }
        }

        [Fact]
        public async Task An_approval_for_one_set_of_plan_lines_is_refused_inside_the_apply_when_the_lines_have_changed_since()
        {
            var (engine, sessions, _) = await OpenAsync("plan-race");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                var plan = await gate.AddPlanItemAsync("measure:Sales/Total", "set_description", "words", null, null, null, "agent");
                var id = plan.Items.Single(i => i.After == "words").Id;
                // a ticket minted for the plan as it read once, run against the plan as it reads now
                var stale = new AssistantTicket
                {
                    Capabilities = new[] { AgentCapability.EditLocal }, Method = "ApplyPlanAsync", ExpectedRevision = -1,
                    SessionId = sessions.Current.Id, ContentBasis = "the lines as they once were",
                };
                using (GateContext.Enter("ApplyPlanAsync", stale))
                {
                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ApplyPlanAsync(new[] { id }, "agent"));
                    Assert.Contains("plan changed", ex.Message, StringComparison.OrdinalIgnoreCase);
                }
                Assert.True(string.IsNullOrEmpty(await DescriptionAsync(sessions)));
                // and a ticket that matches runs
                var fresh = new AssistantTicket
                {
                    Capabilities = new[] { AgentCapability.EditLocal }, Method = "ApplyPlanAsync", ExpectedRevision = -1, SessionId = sessions.Current.Id,
                    ContentBasis = LocalEngine.PlanLinesBasis(sessions.CurrentContext.Plans.Current, new[] { id }),
                };
                using (GateContext.Enter("ApplyPlanAsync", fresh))
                    await engine.ApplyPlanAsync(new[] { id }, "agent");
                Assert.Equal("words", await DescriptionAsync(sessions));
            }
        }

        // ---- below Locked the matrix is today's ------------------------------------------------------------------------------

        [Theory]
        [InlineData("standard")]
        public Task Below_Locked_an_assistants_local_writes_go_through_unasked_as_the_matrix_says_sample(string preset) => Below_Locked_an_assistants_local_writes_go_through_unasked_as_the_matrix_says(preset);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("open")]
        [InlineData("standard")]
        [InlineData("cautious")]
        [InlineData("client")]
        public async Task Below_Locked_an_assistants_local_writes_go_through_unasked_as_the_matrix_says(string preset)
        {
            var (engine, sessions, path) = await OpenAsync("below-" + preset);
            using (engine)
            {
                AgentPolicyStore.SetPreset(preset, "human");
                var gate = Gate(engine);
                Assert.True((await gate.SetDescriptionAsync("measure:Sales/Total", "words", "agent")).Changed);
                await gate.CreateMeasureAsync("table:Sales", "Extra", "1", "agent");
                await gate.UndoAsync("agent");
                var saved = await gate.SaveAsync(null, "BIM", true, "agent");
                Assert.False(string.IsNullOrEmpty(saved.Path));
                var vpax = await gate.ExportVpaxAsync(Path.Combine(Root, "below.vpax"), "agent");
                Assert.True(vpax.Exported, vpax.Error);
                await gate.SetDescriptionAsync("measure:Sales/Total", "unsaved words", "agent");
                await gate.OpenAsync(path, true);   // discarding is the matrix's EditLocal, allowed here
                Assert.Empty(await engine.ListPendingApprovalsAsync());
            }
        }
    }
}
