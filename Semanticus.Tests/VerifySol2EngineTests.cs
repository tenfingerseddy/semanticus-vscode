// Verifier on m11/sol2-engine (1 Oct): attacks on the class claims of A1, A2 and B1 (B5 is in AskDataTests.VerifySol2Engine.cs).
// Each fact tries a path the builder's tests did not walk: another sync that could drop an authored line, another interleaving that could
// bring an undone permission back, and a bundled-rule copy that differs only where the comparison is narrowest.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using TabularEditor.TOMWrapper;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class VerifySol2EngineTests
    {
        private readonly ITestOutputHelper _out;
        public VerifySol2EngineTests(ITestOutputHelper output) => _out = output;

        // ---- A1 ------------------------------------------------------------------------------------------------------------------------

        private const string Words = "Money in, as finance counts it.";

        private static string SummarizeFixture(string digest, string choice) =>
            "{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":" + JsonSerializer.Serialize(digest)
            + ",\"point\":\"column.summarize\",\"usage\":{\"input_tokens\":500,\"output_tokens\":20},\"answers\":{\"summarize_by\":{\"type\":\"choice\",\"choice\":"
            + JsonSerializer.Serialize(choice) + ",\"confidence\":0.95}}}]}";

        private static async Task<(DecisionFixture F, LocalEngine E)> AuthoredAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            await f.ConsentAsync();
            foreach (var other in new[] { DecisionRegistry.MeasureFolderAndFormat, DecisionRegistry.ColumnFormat, DecisionRegistry.ColumnCategory,
                                          DecisionRegistry.RelationshipJoin, DecisionRegistry.DateTable, DecisionRegistry.AiSchema })
                await e.SetDecisionPointEnabledAsync(other, false, "human");
            var prepared = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null));
            var ask = prepared.Asks.Single(a => a.TableName == "Sales" && a.ColumnName == "Amount");
            e.UseDecisionsForTest(new DecisionService(new ByObjectRecordedOracle(SummarizeFixture(ask.Ref, "sum"), "jev-test-1"), new DecisionBudget()));
            await RefreshAsync(e);
            var line = (await e.GetModelPlanAsync()).Items.Single(i => i.Id == "jev.column-summarize.table:Sales")
                .ChangeSet.Lines.Single(l => l.Op == "set_description" && l.Target == "measure:Sales/Total Amount");
            await e.SetPlanItemAsync(line.PlanItemId, Words, null, "human");
            return (f, e);
        }

        private static async Task RefreshAsync(LocalEngine e)
        {
            for (var i = 0; i < 2; i++) { await e.GetModelPlanAsync(); await e.JevSettledForTest(); }
        }

        private static async Task<ChangeItem[]> WordsAsync(LocalEngine e) => (await e.GetPlanAsync()).Items.Where(i => i.After == Words).ToArray();

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task V_A1_a_held_line_is_still_on_the_Plan_page_a_person_reads()
        {
            var (f, e) = await AuthoredAsync("VSol2A1page.bim");
            using (f)
            {
                await e.CreateMeasureAsync("table:Sales", "Total Amount", "SUM ( 'Sales'[Amount] )", "agent");
                await RefreshAsync(e);
                Assert.Single(await WordsAsync(e));
                var shown = (await e.GetModelPlanAsync()).Items.SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()).Where(l => l.After == Words).ToList();
                Assert.True(shown.Count == 1, "the held words are on the change plan but not on the Plan page (" + shown.Count + " lines show them)");
            }
        }

        [Fact]
        public async Task V_A1_switching_the_point_off_keeps_the_words()
        {
            var (f, e) = await AuthoredAsync("VSol2A1off.bim");
            using (f)
            {
                await e.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, false, "human");
                await RefreshAsync(e);
                Assert.Single(await WordsAsync(e));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task V_A1_withdrawing_permission_keeps_the_words()
        {
            var (f, e) = await AuthoredAsync("VSol2A1withdraw.bim");
            using (f)
            {
                await e.WithdrawConsentAsync("human");
                await RefreshAsync(e);
                Assert.Single(await WordsAsync(e));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task V_A1_undoing_the_assistants_measure_keeps_one_copy_of_the_words()
        {
            var (f, e) = await AuthoredAsync("VSol2A1undo.bim");
            using (f)
            {
                await e.CreateMeasureAsync("table:Sales", "Total Amount", "SUM ( 'Sales'[Amount] )", "agent");
                await RefreshAsync(e);
                await e.UndoAsync("human");
                await RefreshAsync(e);
                Assert.Single(await WordsAsync(e));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task V_A1_a_held_line_over_a_measure_deleted_since_writes_nothing()
        {
            var (f, e) = await AuthoredAsync("VSol2A1gone.bim");
            using (f)
            {
                await e.CreateMeasureAsync("table:Sales", "Total Amount", "SUM ( 'Sales'[Amount] )", "agent");
                await RefreshAsync(e);
                await e.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, false, "human");   // so the family does not offer the measure again
                await e.DeleteObjectAsync("measure:Sales/Total Amount", "agent");
                await RefreshAsync(e);
                var held = (await WordsAsync(e)).Single();
                _out.WriteLine("held reason: " + held.Reason);
                try { await e.SetPlanItemAsync(held.Id, null, true, "human"); } catch (Exception ex) { _out.WriteLine("tick refused: " + ex.Message); }
                ApplyPlanReport report = null;
                try { report = await e.ApplyPlanAsync(new[] { held.Id }, "human"); } catch (Exception ex) { _out.WriteLine("apply refused: " + ex.Message); }
                if (report != null) _out.WriteLine($"applied {report.AppliedCount} skipped {report.SkippedCount} failed {report.FailedCount}");
                Assert.True(report == null || report.AppliedCount == 0);
                Assert.False(await f.Sessions.Current.ReadAsync(m => m.AllMeasures.Any(x => x.Name == "Total Amount")), "applying a held line made a measure");
            }
        }

        // ---- A2 ------------------------------------------------------------------------------------------------------------------------

        private sealed class HoldCommit : ISessionObserver
        {
            public readonly ManualResetEventSlim Inside = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim Go = new ManualResetEventSlim(false);
            public void BeforeCommit(Model model) { Inside.Set(); Go.Wait(TimeSpan.FromSeconds(30)); }
            public void AfterCommit(SessionCommit commit) { }
        }

        // Lean suite (8 Oct): the default run keeps the plain undo; undo-to and redo-then-undo are the sweep.
        [Theory]
        [InlineData("undo", "agent")]
        public Task V_A2_an_undo_the_assistant_started_before_another_open_leaves_the_permission_alone_sample(string path, string origin) =>
            V_A2_an_undo_the_assistant_started_before_another_open_leaves_the_permission_alone(path, origin);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("undo", "agent")]
        [InlineData("undo-to", "agent")]
        [InlineData("redo-then-undo", "human")]
        public async Task V_A2_an_undo_the_assistant_started_before_another_open_leaves_the_permission_alone(string path, string origin)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("VSol2A2-" + path + ".bim");
            await f.ConsentAsync();
            var a = f.Sessions.Current;
            string entry = null;
            if (path == "undo-to")
            {
                entry = f.Sessions.Ledger.Entries(a.Id).Last().EntryId;
                await f.Engine.SetObjectPropertyAsync("measure:Sales/Total Sales", "Description", "later edit", "human");
            }
            if (path == "redo-then-undo") { await f.Engine.UndoAsync("human"); await f.Engine.RedoAsync("human"); Assert.True((await f.Engine.GetConsentAsync()).Current); }
            var hold = new HoldCommit();
            a.RegisterObserver(hold);
            var undo = path == "undo-to" ? f.Engine.UndoToEntryAsync(entry, origin) : f.Engine.UndoAsync(origin);
            Assert.True(await Task.Run(() => hold.Inside.Wait(TimeSpan.FromSeconds(20))), "the undo never started");
            Task swap;
            try
            {
                swap = f.Engine.CreateModelAsync("Other", 1604, discardUnsaved: true);
                // The wrapper gate makes the replacement's build wait for the admitted undo, so the old session is not retired
                // while it runs. What matters here is that the swap cannot complete under the running undo.
                await Task.Delay(500);
                Assert.False(swap.IsCompleted, "the swap completed while the undo was still running");
            }
            finally { hold.Go.Set(); }                       // a failed assert must never leave the dispatcher wedged
            try { await undo; } catch (InvalidOperationException) { }
            await swap;
            await f.Engine.OpenAsync(Path.Combine(f.Workspace, "VSol2A2-" + path + ".bim"), true);
            // K19 (8 Oct): permission is kept on this machine, not as a model edit, so an undo, whoever starts it, leaves it alone.
            Assert.True((await f.Engine.GetConsentAsync()).Current, "an undo took the permission away (" + path + ", " + origin + ")");
        }

        // Lean suite (8 Oct): 150 edits and 303 undos and redos took 90 s. The default run walks the same hang and slowdown checks on 10 edits;
        // the 150 edit form is the sweep.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public Task V_A2_the_settler_inside_every_commit_neither_deadlocks_nor_slows_edits_sample() => SettlerNeitherDeadlocksNorSlowsEditsAsync(10, "VSol2A2loadS.bim");

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public Task V_A2_the_settler_inside_every_commit_neither_deadlocks_nor_slows_edits() => SettlerNeitherDeadlocksNorSlowsEditsAsync(150, "VSol2A2load.bim");

        private async Task SettlerNeitherDeadlocksNorSlowsEditsAsync(int n, string file)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync(file);
            var e = f.Engine;
            var plain = Stopwatch.StartNew();
            for (var i = 0; i < n; i++) await e.SetObjectPropertyAsync("measure:Sales/Total Sales", "Description", "plain " + i, "human");
            plain.Stop();
            await f.ConsentAsync();
            var watched = Stopwatch.StartNew();
            for (var i = 0; i < n; i++) await e.SetObjectPropertyAsync("measure:Sales/Total Sales", "Description", "watched " + i, "human");
            watched.Stop();
            _out.WriteLine($"{n} edits: {plain.ElapsedMilliseconds} ms without the settler, {watched.ElapsedMilliseconds} ms with it");
            Assert.True(watched.ElapsedMilliseconds < plain.ElapsedMilliseconds * 3 + 2000, $"edits slowed: {plain.ElapsedMilliseconds} ms to {watched.ElapsedMilliseconds} ms");

            // Undo and redo the edits back to the marker and over it, while other threads read permission and write a decision setting
            // (the two other holders of the consent lock). Nothing may hang.
            var work = Task.Run(async () =>
            {
                for (var i = 0; i < n + 3; i++) await e.UndoAsync(i % 2 == 0 ? "human" : "agent");
                for (var i = 0; i < n + 3; i++) await e.RedoAsync("human");
            });
            var readers = Task.Run(async () => { while (!work.IsCompleted) await e.GetConsentAsync(); });
            var writers = Task.Run(async () =>
            {
                var on = false;
                while (!work.IsCompleted) { await e.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, on, "human"); on = !on; }
            });
            var all = Task.WhenAll(work, readers, writers);
            Assert.True(await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(120))) == all, "undo and redo with the settler hung beside permission reads");
            await all;
            Assert.True((await e.GetConsentAsync()).Current, "after redoing everything the marker is back, so the permission is too");
        }

        // ---- B1 ------------------------------------------------------------------------------------------------------------------------

        private static BpaRule Bundled(string id) => BpaRuleSet.Standard().Single(r => r.ID == id);
        private static BpaRule CopyOf(BpaRule r) => JsonSerializer.Deserialize<BpaRule>(JsonSerializer.Serialize(r));

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task V_B1_a_raised_copy_whose_id_differs_only_in_case_still_blocks_the_gate()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("VSol2B1case", 1604);
            var t = await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync(t, "Rate", "Double", "Rate", "human");
            var dt = await e.CreateTableAsync("Date", "human");
            var dkey = await e.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await e.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await e.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await e.MarkDateTableAsync(dt, "Date", "human");
            Assert.True((await McpTools.DeployGate(e)).Pass);
            var own = CopyOf(Bundled("SEM_COLUMN_NEEDS_FORMAT_STRING"));
            own.ID = own.ID.ToLowerInvariant();
            own.Severity = 3;
            own.Name += " (ours)";
            await e.LoadBpaRulesAsync(JsonSerializer.Serialize(new[] { own }), replace: false, "human");
            var hit = (await e.BpaScanAsync()).Violations.First(v => string.Equals(v.RuleId, own.ID, StringComparison.OrdinalIgnoreCase) && !v.Waived);
            Assert.True(hit.Custom);
            Assert.False((await McpTools.DeployGate(e)).Pass, "the model's severity 3 copy under a lower-case id was skipped");
        }

        [Fact]
        public void V_B1_the_measure_format_rule_copy_is_the_bundled_one_only_while_it_behaves_the_same()
        {
            var bundled = Bundled("PROVIDE_FORMAT_STRING_FOR_MEASURES");
            var words = CopyOf(bundled);
            words.Name = "Our name"; words.Description = "Our words.";
            Assert.True(BpaRuleSet.SameRule(bundled, words));
            foreach (var change in new Action<BpaRule>[] { r => r.Severity = 2, r => r.Category = "Ours", r => r.Scope = "Measure, CalculationItem", r => r.TokenCheck = "calls:IFERROR" })
            {
                var c = CopyOf(bundled);
                change(c);
                Assert.False(BpaRuleSet.SameRule(bundled, c));
            }
            // Whitespace is squashed in EVERY text field, not only the expression. Inside a DAX or LINQ string literal a run of spaces is
            // meaningful, so two copies can differ in behaviour and still compare equal; recorded here as what the comparison does.
            var lit = CopyOf(bundled);
            lit.Expression = bundled.Expression + " and Name != \"a  b\"";
            var lit2 = CopyOf(bundled);
            lit2.Expression = bundled.Expression + " and Name != \"a b\"";
            Assert.True(BpaRuleSet.SameRule(lit, lit2));
        }
    }
}
