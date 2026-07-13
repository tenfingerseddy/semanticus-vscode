using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Learning Loop L4 REPLAY CHECK (docs/learning-loop-plan.md §3.3): replay_check_workflow extends the parse/
    /// op-catalog admission (check_workflow) with a dry_run REHEARSAL of every step op the workflow's EXEMPLAR run
    /// can drive. Proves: the flat exemplar block round-trips through the parser into Provenance; no exemplar →
    /// SKIPPED (instructive, not a failure); an exemplar that drives create_measure → REHEARSED wouldSucceed=true
    /// with the model UNTOUCHED (the dry_run rollback guarantee); a nonexistent-table arg → REHEARSED wouldSucceed=
    /// false + the teaching error + Admissible=false; a deny-listed op → SKIPPED-DENIED (not a failure); a missing
    /// required param → SKIPPED-UNBINDABLE naming the param; a dax_probe verify → replayable-needs-live when offline.
    /// </summary>
    public sealed class ReplayCheckTests
    {
        private sealed class Free : IEntitlement { public bool IsPro => false; public EntitlementInfo Info => new EntitlementInfo { Tier = "free" }; }

        // A workspace-anchored engine (so SaveWorkflowAsync has a place to write); optionally with a small model.
        private static async Task<(LocalEngine e, string ws)> MakeAsync(bool withModel = true)
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-replay-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            var e = new LocalEngine(new SessionManager(), new Free(), ws);
            if (withModel)
            {
                await e.CreateModelAsync("ReplayTest", 1701);
                var t = await e.CreateTableAsync("Facts", "human");
                await e.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
                await e.CreateMeasureAsync(t, "Total", "1", "human");   // existing object for the probe target
            }
            return (e, ws);
        }

        // A distilled-style workflow: FLAT exemplar block (the only shape the parser round-trips) + an ops chain.
        private static string GoodMd(string answersJson, string ops = "create_measure") => $@"---
name: replay-flow
title: Replay flow
version: 1
exemplar_run: wfr-9
exemplar_answers: {answersJson}
---
## Step 1: Create the measure
Author and create it.
```yaml gate
ops: [{ops}]
```
";

        [Fact]
        public void Exemplar_block_round_trips_through_the_parser_into_provenance()
        {
            // The flat convention: `exemplar_answers` is preserved verbatim (as a one-line JSON string) + `exemplar_run`.
            var md = GoodMd("{\"tableRef\":\"table:Facts\",\"name\":\"Margin\",\"expression\":\"SUM(Facts[Amount])\"}");
            var d = WorkflowParser.Parse(md);
            Assert.Null(d.Error);
            Assert.Equal("wfr-9", d.Provenance["exemplar_run"]);
            Assert.Equal("{\"tableRef\":\"table:Facts\",\"name\":\"Margin\",\"expression\":\"SUM(Facts[Amount])\"}", d.Provenance["exemplar_answers"]);
        }

        [Fact]
        public async Task No_exemplar_block_is_skipped_with_an_instructive_note()
        {
            var (e, ws) = await MakeAsync();
            try
            {
                await e.SaveWorkflowAsync("replay-flow", @"---
name: replay-flow
title: No exemplar
version: 1
---
## Step 1: Do it
Body.
```yaml gate
ops: [create_measure]
```
", "human");
                var r = await e.ReplayCheckWorkflowAsync("replay-flow");
                Assert.True(r.ReplaySkipped);
                Assert.Empty(r.Rows);
                Assert.Contains("no exemplar block", r.Note, StringComparison.OrdinalIgnoreCase);
                Assert.True(r.Admissible);   // parse-clean and nothing rehearsed can fail — a skip is not a failure
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Exemplar_driving_create_measure_rehearses_true_and_leaves_the_model_untouched()
        {
            var (e, ws) = await MakeAsync();
            try
            {
                await e.SaveWorkflowAsync("replay-flow",
                    GoodMd("{\"tableRef\":\"table:Facts\",\"name\":\"Margin\",\"expression\":\"SUM(Facts[Amount])\"}"), "human");
                var r = await e.ReplayCheckWorkflowAsync("replay-flow");

                var row = Assert.Single(r.Rows);
                Assert.Equal("create_measure", row.Op);
                Assert.Equal("rehearsed", row.Outcome);
                Assert.True(row.WouldSucceed);
                Assert.True(r.Admissible);
                Assert.Equal(0, r.RehearsedFailed);
                Assert.Equal("wfr-9", r.ExemplarRun);
                // The dry_run guarantee: the rehearsed measure was NOT created.
                Assert.DoesNotContain(await e.ListMeasuresAsync(), m => m.Name == "Margin");
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Exemplar_arg_naming_a_nonexistent_table_rehearses_false_and_is_not_admissible()
        {
            var (e, ws) = await MakeAsync();
            try
            {
                await e.SaveWorkflowAsync("replay-flow",
                    GoodMd("{\"tableRef\":\"table:NoSuch\",\"name\":\"X\",\"expression\":\"1\"}"), "human");
                var r = await e.ReplayCheckWorkflowAsync("replay-flow");

                var row = Assert.Single(r.Rows);
                Assert.Equal("rehearsed", row.Outcome);
                Assert.False(row.WouldSucceed);
                Assert.Contains("not a table", row.Detail);   // create_measure's own teaching text, captured
                Assert.False(r.Admissible);
                Assert.Equal(1, r.RehearsedFailed);
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task A_deny_listed_op_in_the_chain_is_skipped_denied_not_failed()
        {
            var (e, ws) = await MakeAsync();
            try
            {
                await e.SaveWorkflowAsync("replay-flow",
                    GoodMd("{\"tableRef\":\"table:Facts\",\"name\":\"Margin\",\"expression\":\"SUM(Facts[Amount])\"}",
                           ops: "save_model, create_measure"), "human");
                var r = await e.ReplayCheckWorkflowAsync("replay-flow");

                var denied = Assert.Single(r.Rows, x => x.Op == "save_model");
                Assert.Equal("skipped-denied", denied.Outcome);
                Assert.Equal(1, r.SkippedDenied);
                // The deny is not a failure — the sibling create_measure still rehearses and the workflow is admissible.
                Assert.Contains(r.Rows, x => x.Op == "create_measure" && x.Outcome == "rehearsed" && x.WouldSucceed == true);
                Assert.True(r.Admissible);
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task A_missing_required_param_is_skipped_unbindable_naming_the_param()
        {
            var (e, ws) = await MakeAsync();
            try
            {
                // No 'expression' answer — create_measure(expression) can't bind.
                await e.SaveWorkflowAsync("replay-flow",
                    GoodMd("{\"tableRef\":\"table:Facts\",\"name\":\"X\"}"), "human");
                var r = await e.ReplayCheckWorkflowAsync("replay-flow");

                var row = Assert.Single(r.Rows);
                Assert.Equal("skipped-unbindable", row.Outcome);
                Assert.Contains("expression", row.Detail);
                Assert.Equal(1, r.SkippedUnbindable);
                Assert.True(r.Admissible);   // an unbindable op is surfaced, not scored as a failure
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task A_dax_probe_verify_is_marked_replayable_needs_live_when_offline()
        {
            var (e, ws) = await MakeAsync();
            try
            {
                await e.SaveWorkflowAsync("replay-flow", @"---
name: replay-flow
title: Probe flow
version: 1
exemplar_run: wfr-9
exemplar_answers: {""target"":""measure:Facts/Total"",""verificationValue"":""1""}
---
## Step 1: Verify the number
Probe it against the known-good value.
```yaml gate
inputs:
  - name: target
    question: ""The measure to probe.""
    type: objectRef
    required: required
  - name: verificationValue
    question: ""Known-good value.""
    type: text
    required: answer-or-decline
verify:
  - kind: dax_probe
    probe: verificationValue
```
", "human");
                var r = await e.ReplayCheckWorkflowAsync("replay-flow");

                var row = Assert.Single(r.Rows, x => x.Op == "verify:dax_probe");
                Assert.Equal("replayable", row.Outcome);
                Assert.Contains("needs a live/attached session", row.Detail);
                Assert.Equal(1, r.Replayable);
                Assert.True(r.Admissible);   // a replayable (unexecuted) probe never docks admissibility
            }
            finally { Directory.Delete(ws, true); }
        }
    }
}
