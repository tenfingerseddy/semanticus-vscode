using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Verifier for m11/sol6-gate: five rows that AssistantGateWitnessTests files under "model and arguments only" delete or overwrite
    /// an item in a store beside the model (a workflow, a template, a stored test, an interview question). Their Save twins carry a Basis; these
    /// do not. The person says yes, then rewrites the item, and the assistant's identical retry destroys the newer words with no question and no
    /// undo. Same class as join 6 item 6 (a note), which the lane fixed for notes only. Each test must ask again and leave the newer item.</summary>
    [Collection("restore-root")]
    public sealed class VerifySol6GateDeleteRowsTests : GateFixtureBase
    {
        public VerifySol6GateDeleteRowsTests(RestoreRootFixture fixture) : base(fixture) { }

        private const string Locked = AssistantGateKit.Locked;

        private static async Task<ApprovalRecord[]> UnansweredAsync(LocalEngine engine) =>
            (await engine.ListPendingApprovalsAsync()).Where(p => p.GrantedUtc == null).ToArray();

        private async Task StaleAfterYesAsync(LocalEngine engine, Func<Task> call, Func<Task> change)
        {
            await RefusedAsync(call);
            await engine.ApproveAgentActionAsync(Assert.Single(await UnansweredAsync(engine)).Id, "human");
            await change();
            try { await call(); } catch (InvalidOperationException) { }
        }

        private static async Task AskedAgainAsync(LocalEngine engine) => Assert.Single(await UnansweredAsync(engine));   // the newer item is a new question

        private static string Workflow(string title) => "---\nschemaVersion: 3\nname: wf-a\ntitle: " + title + "\n---\n"
            + "## Step 1: Read the columns\n```yaml step\nid: read\nperformer: semanticus\naction:\n  op: list_columns\n```\n";

        private static string Template(string title) => "---\nkind: template\nname: tpl-a\ntitle: " + title + " {{surface}}\nversion: 1\nstrictness: warn\nslots:\n"
            + "  - name: surface\n    question: \"What surface?\"\n    type: text\n    required: required\n    example: \"the dashboard\"\n---\n\n## Step 1: Do it\nCertify {{surface}}.\n";

        private static TestDefinition Test(string id, string title) => new TestDefinition
        {
            Id = id, Kind = TestKinds.MeasureReconcile, Title = title, TargetRef = "measure:Sales/Total",
            ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest { MeasureRef = "measure:Sales/Total", Sql = "SELECT 1", BlankPolicy = "zero" }),
        };

        [Fact]
        public async Task Delete_workflow_is_not_spent_on_a_workflow_the_person_rewrote_after_the_yes()
        {
            var (engine, _, _) = await OpenAsync("d-wf");
            using (engine)
            {
                await engine.SaveWorkflowAsync("wf-a", Workflow("First"), "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.DeleteWorkflowAsync("wf-a", "agent"),
                    () => engine.SaveWorkflowAsync("wf-a", Workflow("The person rewrote it"), "human"));
                Assert.Equal("The person rewrote it", (await engine.GetWorkflowAsync("wf-a"))?.Title);
                await AskedAgainAsync(engine);
            }
        }

        [Fact]
        public async Task Delete_workflow_template_is_not_spent_on_a_template_the_person_rewrote_after_the_yes()
        {
            var (engine, _, _) = await OpenAsync("d-tpl");
            using (engine)
            {
                await engine.SaveWorkflowTemplateAsync("tpl-a", Template("First"), "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.DeleteWorkflowTemplateAsync("tpl-a", "agent"),
                    () => engine.SaveWorkflowTemplateAsync("tpl-a", Template("Rewrite"), "human"));
                Assert.Contains("Rewrite", (await engine.GetWorkflowTemplateAsync("tpl-a"))?.Markdown ?? "");
                await AskedAgainAsync(engine);
            }
        }

        [Fact]
        public async Task Delete_test_definition_is_not_spent_on_a_test_the_person_rewrote_after_the_yes()
        {
            var (engine, _, _) = await OpenAsync("d-test");
            using (engine)
            {
                var first = await engine.SaveTestDefinitionAsync(Test(null, "First"), "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.DeleteTestDefinitionAsync(first.Id, "agent"),
                    () => engine.SaveTestDefinitionAsync(Test(first.Id, "Person's rewrite"), "human"));
                Assert.Contains((await engine.ListTestDefinitionsAsync()).Definitions, t => t.Id == first.Id && t.Title == "Person's rewrite");
                await AskedAgainAsync(engine);
            }
        }

        private static Task<InterviewQuestion> Question(IEngine e, string answer, string origin, string id = null) =>
            e.AddInterviewQuestionAsync("What is the total?", "value", "EVALUATE ROW(\"v\", " + answer + ")", null, null, null, null, answer, null, false, null, "user", "project", origin, id);

        [Fact]
        public async Task Delete_interview_question_is_not_spent_on_a_question_the_person_rewrote_after_the_yes()
        {
            var (engine, _, _) = await OpenAsync("d-iv");
            using (engine)
            {
                var q = await Question(engine, "1", "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.DeleteInterviewQuestionAsync(q.Id, "agent"),
                    () => Question(engine, "2", "human", q.Id));
                Assert.Contains((await engine.ListInterviewQuestionsAsync("project")).Questions, x => x.Id == q.Id && x.ExpectedValue == "2");
                await AskedAgainAsync(engine);
            }
        }

        [Fact]
        public async Task An_interview_question_edit_is_not_spent_on_a_trusted_answer_the_person_rewrote_after_the_yes()
        {
            var (engine, _, _) = await OpenAsync("e-iv");
            using (engine)
            {
                var q = await Question(engine, "1", "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => Question(gate, "99", "agent", q.Id),
                    () => Question(engine, "2", "human", q.Id));
                Assert.Contains((await engine.ListInterviewQuestionsAsync("project")).Questions, x => x.Id == q.Id && x.ExpectedValue == "2");
                await AskedAgainAsync(engine);
            }
        }
    }
}
