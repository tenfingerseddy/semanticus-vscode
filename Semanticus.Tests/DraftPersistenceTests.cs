using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Composer contract 10.1 and 12: drafts persist and runs do not. A draft survives an engine restart with its
    /// answers, parts and approval; a change the restart cut off reports the restart line and may run again.
    /// </summary>
    public sealed class DraftPersistenceTests
    {
        private static async Task<DecisionFixture> SavedAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        private static string Steps() => JsonSerializer.Serialize(new object[]
        {
            new { id = "rate", title = "Add Units sold", op = "create_measure",
                  args = new { tableRef = "table:Sales", name = "Units sold", expression = "{{blanks.formula}}" },
                  blanks = new[] { new { name = "formula", kind = "dax", fills = "expression" } } },
            new { id = "describe", title = "Describe Units sold", op = "set_description",
                  args = new { objRef = "measure:Sales/Units sold", text = "{{blanks.description}}" },
                  blanks = new[] { new { name = "description", kind = "description", fills = "text" } } },
        });

        private static string DraftFile(DecisionFixture f, string id) =>
            Directory.EnumerateFiles(f.Workspace, id + ".json", SearchOption.AllDirectories).Single();

        [Fact]
        public async Task A_draft_survives_an_engine_restart_with_its_parts_and_approval()
        {
            using var f = await SavedAsync("Drafts.bim");
            var v = await McpTools.ComposeTask(f.Engine, JsonDocument.Parse(Steps()).RootElement, title: "Units sold");
            await f.Engine.EditDraftAsync(v.DraftId, "write", "rate", "formula", null, "SUM ( Sales[Quantity] )", "human");
            await McpTools.RunDraft(f.Engine, v.DraftId);
            Assert.EndsWith(Path.Combine(".semanticus", "drafts", v.DraftId + ".json"), DraftFile(f, v.DraftId));

            // A restart: a new engine over the same workspace, opening the same file.
            using var sessions = new SessionManager();
            using var again = new LocalEngine(sessions, TestEntitlements.Pro, f.Workspace);
            await again.OpenAsync(Path.Combine(f.Workspace, "Drafts.bim"));

            var back = await again.GetDraftAsync(v.DraftId);
            Assert.Equal("Units sold", back.Title);
            Assert.True(back.Approved);
            Assert.Equal(DraftBlankStates.Self, back.Blanks.Single(b => b.Name == "formula").State);
            Assert.Equal("SUM ( Sales[Quantity] )", back.Blanks.Single(b => b.Name == "formula").Text);
            // The description's request is a Plan task, kept in the Plan's own file: still waiting after the restart.
            Assert.Equal(DraftStates.WaitingForAssistant, back.State);
            var row = (await again.GetModelPlanAsync()).Items.Single(i => i.Id == "draft/" + v.DraftId);
            Assert.Equal(PlanStateWords.Ready, row.StateWord);

            // Finishing the last part on the new engine runs the approved draft there.
            var id = back.Blanks.Single(b => b.Name == "description").RequestId;
            var claim = await again.ClaimAssistantRequestAsync(id);
            Assert.True((await again.CompleteAssistantRequestAsync(id, claim.Request.ClaimToken, "How many units were sold.")).Ok);
            Assert.Equal(DraftStates.Done, (await again.GetDraftAsync(v.DraftId)).State);
            Assert.Equal("How many units were sold.", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Units sold"].Description));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_run_a_restart_cut_off_reports_the_restart_line_and_can_run_again()
        {
            using var f = await SavedAsync("Cut.bim");
            var rpc = new EngineRpcTarget(f.Engine);
            var v = await rpc.composeTask(stepsJson: Steps(), title: "Units sold");
            await rpc.editDraft(v.DraftId, "write", "rate", "formula", text: "SUM ( Sales[Quantity] )");
            await rpc.editDraft(v.DraftId, "write", "describe", "description", text: "How many units were sold.");

            // The file as a run that an ended engine started would leave it: running, under a run id nobody holds.
            var file = DraftFile(f, v.DraftId);
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            json["Run"] = new JsonObject
            {
                ["StartedUtc"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o"),
                ["EngineRunId"] = "er-00000000",
                ["Outcome"] = "running",
            };
            File.WriteAllText(file, json.ToJsonString());

            var cut = await rpc.getDraft(v.DraftId);
            Assert.Equal(DraftStates.Stopped, cut.State);
            Assert.Equal("restarted", cut.Banner.Kind);
            Assert.Equal(DraftCopy.Restarted, cut.Banner.Text);
            Assert.Equal(PlanStateWords.NeedsYou, cut.PlanWord);

            var ran = await rpc.runDraft(v.DraftId);
            Assert.Equal(DraftStates.Done, ran.State);
            Assert.Equal("SUM ( Sales[Quantity] )", await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Units sold"].Expression));
        }
    }
}
