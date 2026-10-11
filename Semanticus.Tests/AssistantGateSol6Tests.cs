using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Join part 6, area B (Sol 6.1 on the M11 delta, the Locked gate), items 1 and 3 to 8. Every test goes through the REAL assistant door
    /// (<see cref="AssistantEngine"/>), approves what the person was asked, changes the one thing the approval did not cover, and
    /// retries: the retry must ask again and must not write. The class behind items 3 to 6: an approval binds the model's revision and the
    /// call's arguments, but some writes read state that is neither (the spec in memory, a source file, a note, the session at swap
    /// time), so each such row declares a witness for that state and the sink checks it again, under its own lock, right before it writes.
    /// </summary>
    [Collection("restore-root")]
    public sealed class AssistantGateSol6Tests : GateFixtureBase
    {
        public AssistantGateSol6Tests(RestoreRootFixture fixture) : base(fixture) { }

        private const string Locked = AssistantGateKit.Locked;

        /// <summary>The questions the person has not answered yet. An approved question that was never spent stays in the list beside them.</summary>
        private static async Task<ApprovalRecord[]> UnansweredAsync(LocalEngine engine) =>
            (await engine.ListPendingApprovalsAsync()).Where(p => p.GrantedUtc == null).ToArray();

        /// <summary>The person says yes to the one question not yet answered.</summary>
        private static async Task ApproveAskedAsync(LocalEngine engine) =>
            await engine.ApproveAgentActionAsync(Assert.Single(await UnansweredAsync(engine)).Id, "human");

        // ================================ item 1: source-sharing consent is the person's, in every preset ================================

        private sealed class NeverDecides : IDecisionOracle
        {
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct) => Task.FromResult(DecisionAnswers.NotDecided(request, "test oracle never decides"));
        }

        private static FabricSchema PayrollSchema() => new FabricSchema
        {
            Server = "x.datawarehouse.fabric.microsoft.com", Database = "Wh",
            Tables = new[]
            {
                new FabricTable
                {
                    Schema = "dbo", Name = "Payroll", KeyColumns = new[] { "EmployeeId" },
                    Columns = new[]
                    {
                        new FabricColumn { Name = "EmployeeId", DataType = "Int64", SqlType = "bigint", Ordinal = 1 },
                        new FabricColumn { Name = "Salary", DataType = "Decimal", SqlType = "decimal", Ordinal = 2 },
                    },
                },
            },
            ForeignKeys = Array.Empty<FabricForeignKey>(),
        };

        /// <summary>A Pro engine with a spec-first start plan from a fake warehouse that holds dbo.Payroll, and the model's own Jev consent given
        /// (the person's yes to the empty model). The separate source-disclosure question is left unanswered.</summary>
        private async Task<(LocalEngine Engine, string Digest)> StartPlanAsync()
        {
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro, Path.Combine(Root, "ws"));
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(PayrollSchema());
            engine.JevBatchedForTest = _ => true;
            engine.StartDecideKickDisabledForTest = true;   // the decide loop must not ask beside the test
            engine.UseDecisionsForTest(new DecisionService(new NeverDecides(), new DecisionBudget()));
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "x.datawarehouse.fabric.microsoft.com", Database = "Wh", Folder = Path.Combine(Root, "proj"), Name = "Payroll Model",
            }, "human");
            Assert.True(view.Exists);
            await engine.SetConsentAsync(Semanticus.Decisions.DecisionRegistry.DeclaredFields.ToArray(), Semanticus.Decisions.DecisionRegistry.ConsentSchemaVersion, "human");
            await engine.StartSampleSettledForTest();
            return (engine, (await engine.GetStartPlanAsync()).Digest);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("open")]
        [InlineData("standard")]
        [InlineData("cautious")]
        [InlineData("client")]
        [InlineData(Locked)]
        public async Task Item1_the_assistants_grant_of_source_sharing_is_the_persons_question_in_every_preset(string preset)
        {
            var (engine, digest) = await StartPlanAsync();
            using (engine)
            {
                AgentPolicyStore.SetPreset(preset, "human");
                var gate = Gate(engine);
                try { await gate.GrantStartDisclosureAsync(digest, "agent"); } catch (InvalidOperationException) { /* the old door answers Locked with a refusal */ }

                Assert.Null(engine.StartPlanHost.Current().Disclosure);   // nothing was agreed by the assistant's call
                var asked = await OnlyPendingAsync(engine);
                Assert.Contains("Wh", asked.Summary);
                Assert.Contains("Payroll", asked.Summary);               // the included tables are in front of the person
                Assert.Contains("sample", asked.Summary, StringComparison.OrdinalIgnoreCase);

                await ApproveAsync(engine);
                var granted = await gate.GrantStartDisclosureAsync(digest, "agent");   // the person's yes lets the same call through
                Assert.NotNull(engine.StartPlanHost.Current().Disclosure);
                Assert.Contains(engine.StartPlanHost.Current().Disclosure.IncludedTables, t => t.EndsWith("Payroll", StringComparison.OrdinalIgnoreCase));
                Assert.True(granted.Exists);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Item1_the_yes_is_for_the_plan_the_person_was_shown_and_a_changed_plan_asks_again()
        {
            var (engine, digest) = await StartPlanAsync();
            using (engine)
            {
                AgentPolicyStore.SetPreset("standard", "human");
                var gate = Gate(engine);
                try { await gate.GrantStartDisclosureAsync(digest, "agent"); } catch (InvalidOperationException) { }
                await ApproveAsync(engine);
                // the person changes the plan after the question: the sample setting moves, so the digest moves with it
                var moved = await engine.StartPlanHost.UpdateAsync(d => { d.SampleFacts = !d.SampleFacts; return true; });
                Assert.NotEqual(digest, moved.Digest);
                try { await gate.GrantStartDisclosureAsync(moved.Digest, "agent"); } catch (InvalidOperationException) { }
                Assert.Null(engine.StartPlanHost.Current().Disclosure);
                Assert.Single(await UnansweredAsync(engine));   // the new plan is a new question, the old yes is not spent on it
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void Item1_the_grant_row_is_a_consent_row_like_set_consent()
        {
            Assert.Equal(GateKind.Consent, GateRegistry.Find("GrantStartDisclosureAsync").Kind);
            Assert.Equal(GateKind.Consent, GateRegistry.Find("SetConsentAsync").Kind);
        }

        // ================================ item 3: an approved discard does not take work made while the open loads ================================

        private static SemaphoreSlim LifecycleGate(LocalEngine engine) =>
            (SemaphoreSlim)typeof(LocalEngine).GetField("_lifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Item3_work_made_while_the_approved_open_is_loading_is_not_discarded()
        {
            var (engine, sessions, path) = await OpenAsync("disc-a");
            using (engine)
            {
                var other = AssistantGateKit.WriteModel(Root, "disc-b");
                await engine.SetDescriptionAsync("measure:Sales/Total", "first unsaved words", "human");   // revision 1
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.OpenAsync(other, true));
                await ApproveAsync(engine);
                var oldId = sessions.Current.Id;

                var held = LifecycleGate(engine);
                await held.WaitAsync();   // the open waits at the lifecycle gate, as it does behind another open
                Task<OpenResult> open;
                try
                {
                    open = gate.OpenAsync(other, true);
                    await Task.Delay(300);
                    Assert.False(open.IsCompleted, "the open did not wait");
                    await engine.SetDescriptionAsync("measure:Sales/Margin", "the person's later work", "human");   // revision 2, never shown to the person
                }
                finally { held.Release(); }

                await Assert.ThrowsAsync<InvalidOperationException>(() => open);
                Assert.Equal(oldId, sessions.Current.Id);   // the old session is still the open model, and usable
                Assert.Equal("the person's later work", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Margin"].Description));
                await engine.SetDescriptionAsync("measure:Sales/Total", "still editable", "human");
                await RefusedAsync(() => gate.OpenAsync(other, true));
                Assert.Single(await UnansweredAsync(engine));   // and it asks again, about the model as it stands now
            }
        }

        // ================================ item 4: a build approval is for the spec the person was shown ================================

        private static string SpecJson(string table) =>
            "{\"name\":\"S\",\"compatibilityLevel\":1604,\"storageMode\":\"import\",\"tables\":[{\"name\":\"" + table + "\",\"role\":\"dimension\","
            + "\"mExpression\":\"let Source = 1 in Source\",\"columns\":[{\"name\":\"C\",\"dataType\":\"Int64\"}]}],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}";

        private static async Task<bool> HasTableAsync(SessionManager sessions, string name) =>
            await sessions.Current.ReadAsync(m => m.Tables.Any(t => t.Name == name));

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Item4_a_build_approval_is_not_spent_on_a_spec_replaced_after_the_yes()
        {
            var (engine, sessions, _) = await OpenAsync("spec-a");
            using (engine)
            {
                await engine.SetSpecAsync(SpecJson("ApprovedTable"), "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.BuildModelFromSpecAsync("agent"));
                await ApproveAsync(engine);
                await engine.SetSpecAsync(SpecJson("UnapprovedTable"), "human");   // the person (or anything else) replaces the spec after the yes
                await RefusedAsync(() => gate.BuildModelFromSpecAsync("agent"));
                Assert.False(await HasTableAsync(sessions, "UnapprovedTable"), "the replacement spec was built on the old approval");
                Assert.False(await HasTableAsync(sessions, "ApprovedTable"));
                // the new spec is a new question; the unchanged retry of THAT one then runs once
                await ApproveAskedAsync(engine);
                await gate.BuildModelFromSpecAsync("agent");
                Assert.True(await HasTableAsync(sessions, "UnapprovedTable"));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Item4_the_assistant_cannot_replace_the_spec_under_Locked_to_get_round_the_approval()
        {
            var (engine, sessions, _) = await OpenAsync("spec-b");
            using (engine)
            {
                await engine.SetSpecAsync(SpecJson("ApprovedTable"), "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.BuildModelFromSpecAsync("agent"));
                await ApproveAsync(engine);
                await RefusedAsync(() => gate.SetSpecAsync(SpecJson("UnapprovedTable"), "agent"));   // replacing the person's spec is held (item 7)
                Assert.Contains("ApprovedTable", (await engine.GetSpecAsync()).Spec.Tables.Select(t => t.Name));
                Assert.DoesNotContain("UnapprovedTable", (await engine.GetSpecAsync()).Spec.Tables.Select(t => t.Name));
            }
        }

        // ================================ item 5: a copy approval is for the source definition the person reviewed ================================

        private static string WriteSource(string root, string tag, string expression)
        {
            var path = AssistantGateKit.WriteModel(root, tag);
            var db = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(path));
            db.Model.Tables["Sales"].Measures["Margin"].Expression = expression;
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Item5_a_copy_approval_is_not_spent_on_a_source_definition_changed_after_the_yes()
        {
            var (engine, sessions, _) = await OpenAsync("pick-a");
            using (engine)
            {
                var source = WriteSource(Root, "pick-src", "2");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                var from = new ModelRef { Kind = "file", Path = source };
                var refs = new[] { "measure:Sales/Margin" };
                await RefusedAsync(() => gate.CherryPickAsync(from, refs, false, true, "agent"));
                await ApproveAsync(engine);
                WriteSource(Root, "pick-src", "3");   // changed in another editor after the person said yes
                await RefusedAsync(() => gate.CherryPickAsync(from, refs, false, true, "agent"));
                Assert.Equal("1", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Margin"].Expression));
                // the unchanged retry of the new question runs, with the definition the person was then shown
                await ApproveAskedAsync(engine);
                var done = await gate.CherryPickAsync(from, refs, false, true, "agent");
                Assert.Equal(1, done.Count);
                Assert.Equal("3", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Margin"].Expression));
            }
        }

        // ================================ item 6: a note approval is for the note as it was ================================

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Item6_a_note_edit_approval_does_not_overwrite_words_the_person_wrote_after_the_yes()
        {
            var (engine, sessions, _) = await OpenAsync("note-a");
            using (engine)
            {
                var note = await engine.AddInsightAsync("Initial words", new[] { "k" }, "insight", "project", false, "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.EditInsightAsync(note.Id, "Agent replacement", null, "agent"));
                await ApproveAsync(engine);
                await engine.EditInsightAsync(note.Id, "Person's new work", null, "human");
                await RefusedAsync(() => gate.EditInsightAsync(note.Id, "Agent replacement", null, "agent"));
                var now = (await engine.ListInsightsAsync("project", null)).Insights.Single(i => i.Id == note.Id);
                Assert.Equal("Person's new work", now.Text);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Item6_a_delete_approval_is_not_spent_on_a_note_the_person_rewrote()
        {
            var (engine, sessions, _) = await OpenAsync("note-b");
            using (engine)
            {
                var note = await engine.AddInsightAsync("Initial words", new[] { "k" }, "insight", "project", false, "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.DeleteInsightAsync(note.Id, "agent"));
                await ApproveAsync(engine);
                await engine.EditInsightAsync(note.Id, "Person's new work", null, "human");
                await RefusedAsync(() => gate.DeleteInsightAsync(note.Id, "agent"));
                Assert.Contains((await engine.ListInsightsAsync("project", null)).Insights, i => i.Id == note.Id && i.Text == "Person's new work");
            }
        }

        // ================================ item 7: clearing or replacing the person's spec or reviewed plan is held under Locked ================================

        private static async Task<string> PlanItemAsync(LocalEngine engine, string words)
        {
            var plan = await engine.AddPlanItemAsync("measure:Sales/Total", "set_description", words, null, null, null, "human");
            var id = plan.Items.Single(i => i.After == words).Id;
            await engine.SetPlanItemAsync(id, null, true, "human");   // the person reviewed and approved it
            return id;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Item7_clear_spec_is_held_under_Locked_and_bound_to_the_spec_it_would_discard()
        {
            var (engine, _, _) = await OpenAsync("clear-spec");
            using (engine)
            {
                await engine.SetSpecAsync(SpecJson("Mine"), "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.ClearSpecAsync("agent"));
                Assert.NotNull((await engine.GetSpecAsync()).Spec);
                await ApproveAsync(engine);
                await engine.SetSpecAsync(SpecJson("MineAfterTheYes"), "human");   // the spec moved after the yes
                await RefusedAsync(() => gate.ClearSpecAsync("agent"));
                Assert.NotNull((await engine.GetSpecAsync()).Spec);
                await ApproveAskedAsync(engine);   // the question about the spec as it stands now
                await gate.ClearSpecAsync("agent");
                Assert.Null((await engine.GetSpecAsync()).Spec);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Item7_clear_plan_is_held_under_Locked_and_keeps_the_reviewed_line_until_the_person_says_yes()
        {
            var (engine, _, _) = await OpenAsync("clear-plan");
            using (engine)
            {
                var id = await PlanItemAsync(engine, "reviewed words");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.ClearPlanAsync("agent"));
                Assert.Contains((await engine.GetPlanAsync()).Items, i => i.Id == id && i.Status == "approved");
                await ApproveAsync(engine);
                await gate.ClearPlanAsync("agent");
                Assert.Empty((await engine.GetPlanAsync()).Items);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("open")]
        [InlineData("standard")]
        [InlineData("cautious")]
        [InlineData("client")]
        public async Task Item7_below_Locked_clearing_asks_nothing(string preset)
        {
            var (engine, _, _) = await OpenAsync("clear-" + preset);
            using (engine)
            {
                await engine.SetSpecAsync(SpecJson("Mine"), "human");
                await PlanItemAsync(engine, "reviewed words");
                AgentPolicyStore.SetPreset(preset, "human");
                var gate = Gate(engine);
                await gate.ClearSpecAsync("agent");
                await gate.ClearPlanAsync("agent");
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                Assert.Null((await engine.GetSpecAsync()).Spec);
                Assert.Empty((await engine.GetPlanAsync()).Items);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Item7_clearing_nothing_asks_nothing_even_under_Locked()
        {
            var (engine, _, _) = await OpenAsync("clear-none");
            using (engine)
            {
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await gate.ClearSpecAsync("agent");
                await gate.ClearPlanAsync("agent");
                Assert.Empty(await engine.ListPendingApprovalsAsync());
            }
        }

        // ================================ item 8: a link inside a link's target cannot redirect an approved save ================================

        private static bool TryLink(string link, string target)
        {
            try { Directory.CreateSymbolicLink(link, target); return true; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PlatformNotSupportedException) { return false; }
        }

        private string MakeRoutedFiles(out string route, out string entry, out string one, out string two)
        {
            var dir = Path.Combine(Root, "links");
            one = Path.Combine(dir, "one"); two = Path.Combine(dir, "two");
            foreach (var d in new[] { one, two })
            {
                Directory.CreateDirectory(Path.Combine(d, "project"));
                File.WriteAllText(Path.Combine(d, "project", "export.bim"), "same starting words");
            }
            route = Path.Combine(dir, "route"); entry = Path.Combine(dir, "entry");
            return dir;
        }

        [Fact]
        public void Item8_the_witness_key_follows_a_link_inside_the_target_of_a_link()
        {
            MakeRoutedFiles(out var route, out var entry, out var one, out var two);
            if (!TryLink(route, one)) return;   // no link rights on this machine: nothing to prove here
            Assert.True(TryLink(entry, Path.Combine(route, "project")));
            var before = PathWitness.Of(Path.Combine(entry, "export.bim"));
            Directory.Delete(route);   // a link: removes the link only
            Assert.True(TryLink(route, two));
            var after = PathWitness.Of(Path.Combine(entry, "export.bim"));
            Assert.Equal(Path.Combine(one, "project", "export.bim"), before.Key);
            Assert.Equal(Path.Combine(two, "project", "export.bim"), after.Key);
            Assert.NotEqual(before.Key, after.Key);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Item8_an_approved_save_does_not_follow_a_link_that_was_moved_after_the_yes()
        {
            MakeRoutedFiles(out var route, out var entry, out var one, out var two);
            if (!TryLink(route, one)) return;
            Assert.True(TryLink(entry, Path.Combine(route, "project")));
            var (engine, _, _) = await OpenAsync("link-save");
            using (engine)
            {
                await engine.SetDescriptionAsync("measure:Sales/Total", "unsaved words", "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                var target = Path.Combine(entry, "export.bim");
                await RefusedAsync(() => gate.SaveAsync(target, "BIM", true, "agent"));
                await ApproveAsync(engine);
                Directory.Delete(route);
                Assert.True(TryLink(route, two));   // the link moves to the other folder after the yes
                await RefusedAsync(() => gate.SaveAsync(target, "BIM", true, "agent"));
                Assert.Equal("same starting words", File.ReadAllText(Path.Combine(two, "project", "export.bim")));
                Assert.Equal("same starting words", File.ReadAllText(Path.Combine(one, "project", "export.bim")));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void Item8_a_chain_of_links_that_never_ends_fails_closed()
        {
            var dir = Path.Combine(Root, "loop");
            Directory.CreateDirectory(dir);
            var a = Path.Combine(dir, "a"); var b = Path.Combine(dir, "b");
            if (!TryLink(a, b)) return;
            Assert.True(TryLink(b, a));
            var w = PathWitness.Of(Path.Combine(a, "file.bim"));
            Assert.StartsWith("unresolved", w.State);   // never "absent": an unresolved link must not look like a new file
        }
    }
}
