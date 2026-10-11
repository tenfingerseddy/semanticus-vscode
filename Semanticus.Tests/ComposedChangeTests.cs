using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Jev;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Composer contract 3.2: a change group is ONE MutateAsync through the same ApplyOneItem the Plan uses, so it
    /// is one undo entry; a before-check that moved stops the run and keeps the steps before it as one entry; an
    /// applier that throws rolls the whole batch back and leaves the redo stack as it was; and the hooks the ops
    /// run outside their own batch (binding, the Verified Edits record, the drift baseline) run for composed
    /// writes too. Contract 3.3: a type set on a column born in the same change folds into its creation.
    /// </summary>
    public sealed class ComposedChangeTests
    {
        private const string Description = "Share of units sold that came back as returns. Higher is worse.";

        private static ComposedChange Filled() => ComposerFrame.Build(ComposerFrame.ReturnRate, Description);

        private static Task<string[]> MeasuresAsync(SessionManager sm) =>
            sm.Current.ReadAsync(m => m.AllMeasures.Select(x => ObjectRefs.For(x)).ToArray());

        [Fact]
        public async Task SevenStepsMakeOneUndoEntryAndOneUndoRemovesThemAll()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync();
            using (sm)
            {
                var undoBefore = await ComposerFrame.UndoStepsAsync(sm);
                var seen = new List<ChangeNotification>();
                Action<ChangeNotification> handler = n => seen.Add(n);
                sm.Bus.Changed += handler;
                ComposedChangeResult r;
                try { r = await engine.ApplyComposedChangeAsync(Filled(), "human"); }
                finally { sm.Bus.Changed -= handler; }

                Assert.Equal(ComposedOutcome.Applied, r.Outcome);
                Assert.True(r.Changed);
                Assert.Equal(7, r.AppliedStepIds.Length);
                Assert.Equal(undoBefore + 1, await ComposerFrame.UndoStepsAsync(sm));
                var commit = Assert.Single(seen);                                      // one commit, one broadcast
                Assert.Equal("Import Returns and add return measures", commit.Label);  // the draft title is the undo label

                var shape = await sm.Current.ReadAsync(m => new
                {
                    Columns = m.Tables["Returns"].Columns.Count,
                    Relationships = m.Relationships.Count,
                    ReturnDate = m.Tables["Returns"].Columns["ReturnDate"].DataType,
                    Refund = m.Tables["Returns"].Measures["Refund total"].Expression,
                    Rate = m.Tables["Returns"].Measures["Return rate"],
                });
                Assert.Equal(7, shape.Columns);
                Assert.Equal(2, shape.Relationships);
                Assert.Equal(DataType.DateTime, shape.ReturnDate);
                Assert.Equal("SUM ( 'Returns'[RefundAmount] )", shape.Refund);           // the starter template wrote it
                Assert.Equal(ComposerFrame.ReturnRate, shape.Rate.Expression);
                Assert.Equal("Returns", shape.Rate.DisplayFolder);
                Assert.Equal("0.0%", shape.Rate.FormatString);
                Assert.Equal(Description, shape.Rate.Description);

                await engine.UndoAsync("human");

                Assert.False(await ComposerFrame.HasTableAsync(sm, "Returns"));
                Assert.Equal(0, await sm.Current.ReadAsync(m => m.Relationships.Count));
                Assert.Empty(await MeasuresAsync(sm));
                Assert.True(await ComposerFrame.HasTableAsync(sm, "Sales"));            // the edits before the change stay
                Assert.Equal(undoBefore, await ComposerFrame.UndoStepsAsync(sm));
            }
        }

        // Plan-heart contract 8.4: a composer change group opens the draft cause scope around its one MutateAsync,
        // so the commit is Semanticus's, names the draft, and nests under it in History. Deferred from lane L3 to
        // the slice 8 integration because ChangeCause arrived with lane E4.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task TheChangeGroupIsFiledUnderItsDraftAsSemanticusWithWhoApproved()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync();
            using (sm)
            {
                var seen = new List<ChangeNotification>();
                Action<ChangeNotification> handler = n => { if (n.Deltas?.Length > 0) seen.Add(n); };
                sm.Bus.Changed += handler;
                ComposedChangeResult r;
                try { r = await engine.ApplyComposedChangeAsync(Filled(), "agent"); }
                finally { sm.Bus.Changed -= handler; }

                Assert.Equal(ComposedOutcome.Applied, r.Outcome);
                var n = Assert.Single(seen);
                Assert.Equal("semanticus", n.By);
                Assert.Equal(ChangeCauseKinds.Draft, n.Cause?.Kind);
                Assert.Equal(new[] { "draft-frame" }, n.Cause.Ids);
                Assert.Equal("Import Returns and add return measures", n.Cause.Title);
                Assert.Equal("assistant", n.Cause.ApprovedBy);
                Assert.Null(ChangeCause.Current);   // the scope closed with the call
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task ABeforeCheckThatMovesStopsTheRunAndKeepsTheEarlierStepsAsOneEntry()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync();
            using (sm)
            {
                var change = Filled();
                change.Steps[4].Checks.Add(new ComposedCheck { Kind = "name_free", Phase = "before" });
                // Someone takes the name after the draft was checked: measure names are model-wide.
                await engine.CreateMeasureAsync("table:Sales", "Return rate", "1", "human");
                var undoBefore = await ComposerFrame.UndoStepsAsync(sm);

                var r = await engine.ApplyComposedChangeAsync(change, "human");

                Assert.Equal(ComposedOutcome.Stopped, r.Outcome);
                Assert.Equal(5, r.StopStep);
                Assert.Equal(new[] { "import", "rel-sales", "rel-date", "types" }, r.AppliedStepIds);
                var stop = Assert.Single(r.BeforeChecks, c => c.Status == ComposedCheckStatus.Failed);
                Assert.Contains("Return rate is already taken", stop.Text);
                Assert.Equal(undoBefore + 1, await ComposerFrame.UndoStepsAsync(sm));   // steps 1 to 4, one entry
                Assert.True(await ComposerFrame.HasTableAsync(sm, "Returns"));
                Assert.DoesNotContain("measure:Returns/Refund total", await MeasuresAsync(sm));   // step 6 did not run

                await engine.UndoAsync("human");
                Assert.False(await ComposerFrame.HasTableAsync(sm, "Returns"));
                Assert.Contains("measure:Sales/Return rate", await MeasuresAsync(sm));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task AnApplierThatThrowsLeavesTheModelUnchangedAndTheRedoStackIntact()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync();
            using (sm)
            {
                // Something to redo: the batch must not clear it when it fails.
                await engine.CreateMeasureAsync("table:Sales", "Scratch", "1", "human");
                await engine.UndoAsync("human");
                var redoBefore = await ComposerFrame.RedoSizeAsync(sm);
                var undoBefore = await ComposerFrame.UndoStepsAsync(sm);
                var revBefore = sm.Current.Revision;
                Assert.True(redoBefore > 0);

                var change = Filled();
                change.Steps[2].Actions[0].Args["toColumnRef"] = "column:Date/NoSuchColumn";   // step 3 cannot be made

                var r = await engine.ApplyComposedChangeAsync(change, "human");

                Assert.Equal(ComposedOutcome.Failed, r.Outcome);
                Assert.Equal(3, r.StopStep);
                Assert.False(r.Changed);
                Assert.StartsWith("Nothing changed. Step 3 could not be made:", r.Reason);
                Assert.False(await ComposerFrame.HasTableAsync(sm, "Returns"));          // step 1 and 2 rolled back too
                Assert.Equal(0, await sm.Current.ReadAsync(m => m.Relationships.Count));
                Assert.Equal(revBefore, sm.Current.Revision);
                Assert.Equal(undoBefore, await ComposerFrame.UndoStepsAsync(sm));
                Assert.Equal(redoBefore, await ComposerFrame.RedoSizeAsync(sm));

                await engine.RedoAsync("human");                                           // and it still redoes
                Assert.Contains("measure:Sales/Scratch", await MeasuresAsync(sm));
            }
        }

        [Fact]
        public async Task AnOpenBlankChangesNothing()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync();
            using (sm)
            {
                var undoBefore = await ComposerFrame.UndoStepsAsync(sm);
                var r = await engine.ApplyComposedChangeAsync(ComposerFrame.Build(ComposerFrame.ReturnRate), "human");
                Assert.Equal(ComposedOutcome.NotReady, r.Outcome);
                Assert.Equal(7, r.StopStep);
                Assert.False(await ComposerFrame.HasTableAsync(sm, "Returns"));
                Assert.Equal(undoBefore, await ComposerFrame.UndoStepsAsync(sm));
            }
        }

        // Contract 3.3. The four types in step 4 fold into the columns step 1 creates; the conversion kind, which
        // refuses a column nobody has measured, never sees them.
        [Fact]
        public void ATypeOnAColumnBornInTheSameChangeFoldsIntoItsCreation()
        {
            var compiled = ComposedChangeCompiler.Compile(Filled(), BlankFill.Require);

            Assert.Equal(4, compiled.FoldedTypes);
            Assert.DoesNotContain(compiled.Steps.SelectMany(st => st.Lines), l => l.Item.Kind == "set_data_type");
            Assert.Empty(compiled.Steps[3].Lines);
            var returnDate = compiled.Steps[0].Lines.Single(l => l.Item.ObjectRef == "column:Returns/ReturnDate");
            Assert.Equal("DateTime", returnDate.Item.Args["dataType"]);
        }

        // A type on a column that existed before the run is NOT folded: it keeps the conversion kind and its check.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task ATypeOnAnExistingColumnKeepsItsConversionCheck()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync();
            using (sm)
            {
                var change = new ComposedChange { Title = "Retype", Steps = { new ComposedStep { Id = "t", Title = "Set a type" } } };
                change.Steps[0].Actions.Add(new ComposedAction { Op = "set_column_data_type", Args = { ["columnRef"] = "column:Sales/Amount", ["dataType"] = "Int64" } });
                Assert.Equal(0, ComposedChangeCompiler.Compile(change, BlankFill.Require).FoldedTypes);

                var r = await engine.ApplyComposedChangeAsync(change, "human");
                Assert.Equal(ComposedOutcome.Failed, r.Outcome);
                Assert.Contains("measured in full", r.Reason);   // the conversion kind's own refusal
            }
        }

        // ---- the hooks the ops run outside their batch -------------------------------------------------------

        private const string VehicleMd = @"---
name: bind-vehicle
title: Binding vehicle
whenToUse: ""Author a measure the reviewed way.""
---
## Step 1: Author the measure
Create the measure at this step.
```yaml gate
ops: [create_measure]
```
## Step 2: Review
Review what you made.
";

        private static string NewWorkspace()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-composed-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "bind-vehicle.md"), VehicleMd);
            return ws;
        }

        private static void WriteSettings(string ws, string json) =>
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflow-settings.json"), json);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task AHardBindingRefusesAComposedMeasureAsItRefusesTheBareOp()
        {
            var ws = NewWorkspace();
            var (engine, sm) = await ComposerFrame.OpenAsync(workspace: ws);
            try
            {
                using (sm)
                {
                    WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": { ""require"": [""bind-vehicle""], ""mode"": ""hard"" } } }");

                    var bare = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.CreateMeasureAsync("table:Sales", "Bare", "1", "agent"));
                    var composed = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.ApplyComposedChangeAsync(Filled(), "agent"));

                    Assert.Equal(bare.Message, composed.Message);                         // the same teaching sentence
                    Assert.False(await ComposerFrame.HasTableAsync(sm, "Returns"));      // refused before any write

                    // A composed run OF the required workflow is its performing step, so it goes through.
                    var change = Filled();
                    change.Workflow = "bind-vehicle";
                    var r = await engine.ApplyComposedChangeAsync(change, "agent");
                    Assert.Equal(ComposedOutcome.Applied, r.Outcome);
                }
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task AWarnBindingPublishesTheAdvisoryForEachComposedMeasure()
        {
            var ws = NewWorkspace();
            var (engine, sm) = await ComposerFrame.OpenAsync(workspace: ws);
            try
            {
                using (sm)
                {
                    WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": { ""require"": [""bind-vehicle""], ""mode"": ""warn"" } } }");
                    var events = new List<ActivityEvent>();
                    void Handler(ActivityEvent e) { lock (events) events.Add(e); }
                    sm.Bus.Activity += Handler;
                    try
                    {
                        var r = await engine.ApplyComposedChangeAsync(Filled(), "agent");
                        Assert.Equal(ComposedOutcome.Applied, r.Outcome);
                    }
                    finally { sm.Bus.Activity -= Handler; }
                    lock (events)
                        Assert.Equal(2, events.Count(e => e.Kind == "landed_outside_required_workflow" && e.Target == "create_measure"));
                }
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task TheVerifiedEditsRecordFiresForComposedDaxWrites()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync();
            using (sm)
            {
                await engine.SetVerifiedModeAsync(true, "human");
                await engine.ApplyComposedChangeAsync(Filled(), "human");
                var records = (await engine.ListVerifiedEditsAsync()).Records;

                Assert.Contains(records, x => x.Op == "create_measure" && x.ObjectRef == "measure:Returns/Return rate" && x.Verdict == "validated");
                Assert.Contains(records, x => x.Op == "create_measure" && x.ObjectRef == "measure:Returns/Refund total");
                Assert.Equal(2, records.Length);   // no record for a table, a column or a relationship: the ops write none
            }

            // With the switch off, no record, exactly as for the op.
            var (engine2, sm2) = await ComposerFrame.OpenAsync();
            using (sm2)
            {
                await engine2.ApplyComposedChangeAsync(Filled(), "human");
                Assert.Empty((await engine2.ListVerifiedEditsAsync()).Records);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task TheDriftBaselineIsRecordedForComposedMeasureWrites()
        {
            var ws = NewWorkspace();
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true, workspace: ws);
            try
            {
                using (sm)
                {
                    var run = await engine.StartWorkflowAsync("bind-vehicle", "agent");   // at step 1, which declares create_measure
                    await engine.ApplyComposedChangeAsync(Filled(), "agent");

                    var hashes = sm.CurrentContext.WorkflowAux[run.RunId].AuthoredMeasureHashes;
                    Assert.NotNull(hashes);
                    Assert.Contains("measure:Returns/Return rate", hashes.Keys);
                    Assert.Contains("measure:Returns/Refund total", hashes.Keys);
                }
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- the job executor ------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task TheChangePseudoStepIsOneExecutorCallThatProvesEveryStepItApplied()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync();
            using (sm)
            {
                var executor = new ComposedChangeExecutor(engine, _ => Filled(), "human");
                var job = new JobView { JobId = "job-1" };

                var result = await executor.ExecuteAsync(job, new StepDefinition { Id = "change", Op = "change" },
                    new Dictionary<string, string>(), CancellationToken.None);

                Assert.True(result.Ok, result.Note);
                Assert.Equal(new[] { "import", "rel-sales", "rel-date", "types", "return-rate", "refund-total", "describe" }, result.ObligationsMet);
                Assert.Null(result.AssistantNeed);   // blanks are posted before the change, never mid-step (3.1)

                var other = await executor.ExecuteAsync(job, new StepDefinition { Id = "refresh", Op = "refresh_partition" },
                    new Dictionary<string, string>(), CancellationToken.None);
                Assert.False(other.Ok);
            }
        }

        // ---- one applier, shared by the op, the Plan and the composer ------------------------------------------

        // The op now builds through the same core the composer's line does: the refusals read the same.
        [Fact]
        public async Task TheOpAndTheComposedLineRefuseTheSameWay()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync();
            using (sm)
            {
                await engine.CreateRoleAsync("Readers", null, "human");
                var op = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreateRoleAsync("Readers", null, "human"));

                var change = new ComposedChange { Title = "Add a role", Steps = { new ComposedStep { Id = "r", Title = "Add Readers" } } };
                change.Steps[0].Actions.Add(new ComposedAction { Op = "create_role", Args = { ["name"] = "Readers" } });
                var r = await engine.ApplyComposedChangeAsync(change, "human");

                Assert.Equal(ComposedOutcome.Failed, r.Outcome);
                Assert.Equal("Nothing changed. Step 1 could not be made: " + op.Message, r.Reason);
            }
        }

        [Fact]
        public void AnOpOutsideTheAllowlistIsRefusedByName()
        {
            var change = new ComposedChange { Title = "x", Steps = { new ComposedStep { Id = "d", Title = "Deploy" } } };
            change.Steps[0].Actions.Add(new ComposedAction { Op = "deploy_live" });
            var ex = Assert.Throws<ComposeException>(() => ComposedChangeCompiler.Compile(change, BlankFill.Require));
            Assert.Equal(1, ex.StepNumber);
            Assert.Contains("deploy_live", ex.Message);
        }
    }
}
