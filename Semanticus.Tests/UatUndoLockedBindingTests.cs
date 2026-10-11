using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane m11/uat-undo (security, D1 and D2): under Locked an assistant's edit asks the person, the person is told WHAT it
    /// would change in plain words made from the request, and the approval is that one write and no other. The assistant's
    /// calls go through the gate (AssistantEngine), the only engine an assistant holds. And no inner caller can hand an
    /// assistant's write on as the person's: the origin of a save is not a parameter that has a default.
    /// </summary>
    [Collection("restore-root")]
    public sealed class UatUndoLockedBindingTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public UatUndoLockedBindingTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "uu-bind-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            AgentPolicyStore.RootOverride = _root;
            ApprovalLedger.RootOverride = _root;
        }

        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static async Task<(LocalEngine engine, SessionManager sessions)> LockedAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("LockedBind", 1604);
            await engine.CreateTableAsync("Sales", "human");
            await engine.CreateMeasureAsync("table:Sales", "Total", "1", "human");
            await engine.CreateMeasureAsync("table:Sales", "Margin", "2", "human");
            AgentPolicyStore.SetPreset("locked", "human");
            return (engine, sessions);
        }

        // The only engine an assistant holds: every call it makes is looked up in the registry and asked about before the engine sees it.
        private static IEngine Assistant(LocalEngine engine) => AssistantEngine.ForAssistant(engine);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_request_names_the_object_and_the_new_words()
        {
            var (engine, _) = await LockedAsync();
            using (engine)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).SetDescriptionAsync("measure:Sales/Total", "Sum of sales.", "agent"));
                var pending = Assert.Single(await engine.ListPendingApprovalsAsync());
                Assert.StartsWith("Your assistant wants to set the description of [Total] to: \"Sum of sales.\".", pending.Summary);
                Assert.Contains(" This is about the model \"", pending.Summary);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task One_approval_is_that_one_write_and_the_next_identical_write_asks_again()
        {
            var (engine, sessions) = await LockedAsync();
            using (engine)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).SetDescriptionAsync("measure:Sales/Total", "Sum of sales.", "agent"));
                await engine.ApproveAgentActionAsync(Assert.Single(await engine.ListPendingApprovalsAsync()).Id, "human");
                var done = await Assistant(engine).SetDescriptionAsync("measure:Sales/Total", "Sum of sales.", "agent");
                Assert.True(done.Changed);
                Assert.Equal("Sum of sales.", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description));
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).SetDescriptionAsync("measure:Sales/Total", "Sum of sales, again.", "agent"));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_approval_is_not_spent_on_other_words_for_the_same_object()
        {
            var (engine, sessions) = await LockedAsync();
            using (engine)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).SetDescriptionAsync("measure:Sales/Total", "Sum of sales.", "agent"));
                await engine.ApproveAgentActionAsync(Assert.Single(await engine.ListPendingApprovalsAsync()).Id, "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).SetDescriptionAsync("measure:Sales/Total", "Something else.", "agent"));
                Assert.True(string.IsNullOrEmpty(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description)));
                // The one the person did approve still works afterwards: the wrong try did not spend it.
                Assert.True((await Assistant(engine).SetDescriptionAsync("measure:Sales/Total", "Sum of sales.", "agent")).Changed);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_approval_to_create_a_measure_covers_its_formula()
        {
            var (engine, sessions) = await LockedAsync();
            using (engine)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).CreateMeasureAsync("table:Sales", "Cost", "SUM(Sales[X])", "agent"));
                var pending = Assert.Single(await engine.ListPendingApprovalsAsync());
                Assert.Contains("add the measure [Cost]", pending.Summary);
                await engine.ApproveAgentActionAsync(pending.Id, "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).CreateMeasureAsync("table:Sales", "Cost", "SUM(Sales[Y])", "agent"));
                Assert.False(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains("Cost")));
                await Assistant(engine).CreateMeasureAsync("table:Sales", "Cost", "SUM(Sales[X])", "agent");
                Assert.True(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains("Cost")));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_question_made_to_ask_leaves_no_trace_in_the_model_or_its_history()
        {
            var (engine, sessions) = await LockedAsync();
            using (engine)
            {
                var revision = sessions.Current.Revision;
                var rows = (await engine.GetPlanHistoryAsync()).Rows.Length;
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).SetDescriptionAsync("measure:Sales/Total", "Sum of sales.", "agent"));
                Assert.Equal(revision, sessions.Current.Revision);
                Assert.Equal(rows, (await engine.GetPlanHistoryAsync()).Rows.Length);
                Assert.True(string.IsNullOrEmpty(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description)));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_refused_delete_leaves_the_model_exactly_as_it_was_order_and_all_and_names_what_it_would_delete()
        {
            var (engine, sessions) = await LockedAsync();
            using (engine)
            {
                await engine.CreateFieldParameterAsync("Pick", new[] { new FieldParameterItem { ObjectRef = "measure:Sales/Total" } }, "human");
                string Json() => sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase,
                    new Microsoft.AnalysisServices.Tabular.SerializeOptions { IgnoreTimestamps = true, IgnoreInferredObjects = true, IgnoreInferredProperties = true })).GetAwaiter().GetResult();
                var before = Json();
                // The first measure of the table, so a rehearsal that took it away and put it back would leave it last.
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).DeleteObjectAsync("measure:Sales/Total", "agent"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).DeleteObjectsAsync(new[] { "measure:Sales/Total", "measure:Sales/Margin" }, "agent"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).DeleteObjectAsync("table:Pick", "agent"));
                Assert.Equal(before, Json());
                var asked = (await engine.ListPendingApprovalsAsync()).Select(a => a.Summary).ToArray();
                Assert.Contains(asked, a => a.StartsWith("Your assistant wants to delete [Total].", StringComparison.Ordinal) && a.Contains(" This is about the model \""));
                Assert.Contains(asked, a => a.StartsWith("Your assistant wants to delete [Total], [Margin].", StringComparison.Ordinal) && a.Contains(" This is about the model \""));
                Assert.Contains(asked, a => a.StartsWith("Your assistant wants to delete the table Pick.", StringComparison.Ordinal) && a.Contains(" This is about the model \""));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_assistants_undo_is_asked_about_in_words_and_bound_to_that_step()
        {
            var (engine, sessions) = await LockedAsync();
            using (engine)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).UndoAsync("agent"));
                var pending = Assert.Single(await engine.ListPendingApprovalsAsync());
                Assert.StartsWith("Your assistant wants to undo one change to the open model, taking back: add [Margin].", pending.Summary);
                Assert.Contains(" This is about the model \"", pending.Summary);
                await engine.ApproveAgentActionAsync(pending.Id, "human");
                await engine.CreateMeasureAsync("table:Sales", "Later", "3", "human");   // the newest step is now another one
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).UndoAsync("agent"));
                Assert.True(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains("Later")));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_value_cut_short_in_the_words_still_changes_what_an_approval_is_bound_to()
        {
            // The person reads the first 160 characters of a long text, and the approval covers every character of it: two texts that
            // read the same where they are cut are two different questions.
            var (engine, sessions) = await LockedAsync();
            using (engine)
            {
                var head = new string('a', 200);
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).SetDescriptionAsync("measure:Sales/Total", head + "one", "agent"));
                var pending = Assert.Single(await engine.ListPendingApprovalsAsync());
                Assert.Contains("characters in all", pending.Summary);
                await engine.ApproveAgentActionAsync(pending.Id, "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).SetDescriptionAsync("measure:Sales/Total", head + "two", "agent"));
                Assert.True(string.IsNullOrEmpty(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description)));
                Assert.True((await Assistant(engine).SetDescriptionAsync("measure:Sales/Total", head + "one", "agent")).Changed);
            }
        }

        [Fact]
        public void No_save_entry_point_has_an_origin_that_defaults_to_the_person()
        {
            // Every caller of a save must say whose write it is, so the compiler finds a caller that forgot (D1). A default of
            // "human" is how an assistant's git commit, checkpoint and restore saved the model as the person.
            foreach (var type in new[] { typeof(IEngine), typeof(LocalEngine), typeof(RemoteEngine) })
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(x => x.Name == "SaveAsync"))
                {
                    var origin = m.GetParameters().Single(p => p.Name == "origin");
                    Assert.False(origin.HasDefaultValue, type.Name + ".SaveAsync gives origin a default");
                }
        }
    }
}
