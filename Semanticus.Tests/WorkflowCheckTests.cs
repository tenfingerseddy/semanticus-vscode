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
    /// Learning Loop L4 (docs/learning-loop-plan.md §3.3): check_workflow, the ADMISSION DRY-RUN — the cheap
    /// half of the admission pipeline (parse-valid → dry-run). Proves: an unknown op is flagged (warn), an
    /// unresolved verify when-input is flagged (warn), a clean stock workflow passes (Ok, no warn), and a
    /// distilled workflow's derived_from provenance is captured by the parser AND surfaced as an info finding.
    /// The expensive replay check is a later layer and is NOT exercised here.
    /// </summary>
    public sealed class WorkflowCheckTests
    {
        private sealed class Free : IEntitlement { public bool IsPro => false; public EntitlementInfo Info => new EntitlementInfo { Tier = "free" }; }

        private static (LocalEngine e, string ws) Make()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-wfcheck-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            return (new LocalEngine(new SessionManager(), new Free(), ws), ws);
        }

        // A distilled workflow: derived_from provenance in frontmatter, an unknown op, and a verify whose
        // when references an input no gate collects — exercising every finding path at once.
        private const string DistilledMd = @"---
name: distilled-flow
title: Distilled flow
derived_from: [wfr-1, wfr-2]
distilled_at: 2026-07-03
---
## Step 1: Do the thing
Body.
```yaml gate
ops: [create_measure, totally_not_an_op]
strictness: warn
inputs:
  - name: note
    question: ""A note?""
    required: optional
verify:
  - kind: dax_probe
    when: inputs.doesNotExist.answered
    probe: doesNotExist
```
";

        // A verify whose KIND requires a probe (the value to compare against) but declares none: it slips past
        // the "unresolved probe" finding (that only fires for a NON-empty, unresolved probe) yet fails at run
        // time — admission must catch the empty-probe case too (Codex P2). Carries an objectRef target so the
        // ONLY defect is the missing probe.
        private const string NoProbeMd = @"---
name: no-probe-flow
title: A dax_probe verify that declares no probe
strictness: hard
---
## Step 1: Verify
Probe the measure — but forget to name the value to compare against.
```yaml gate
ops: [probe_measure]
inputs:
  - name: target
    question: ""The measure ref to probe.""
    type: objectRef
    required: required
verify:
  - kind: dax_probe
```
";

        [Fact]
        public void Parser_captures_unknown_frontmatter_keys_as_provenance()
        {
            var d = WorkflowParser.Parse(DistilledMd);
            Assert.Null(d.Error);
            Assert.Equal("[wfr-1, wfr-2]", d.Provenance["derived_from"]);
            Assert.Equal("2026-07-03", d.Provenance["distilled_at"]);
        }

        [Fact]
        public async Task Check_flags_unknown_op_unresolved_when_and_surfaces_provenance()
        {
            var (e, ws) = Make();
            try
            {
                await e.SaveWorkflowAsync("distilled-flow", DistilledMd, "human");
                var r = await e.CheckWorkflowAsync("distilled-flow");

                Assert.Null(r.ParseError);
                Assert.False(r.Ok);                                         // warns present → not admissible
                Assert.Contains(r.Findings, f => f.Severity == "info" && f.Message.Contains("derived_from"));   // provenance surfaced
                Assert.Contains(r.Findings, f => f.Severity == "warn" && f.Message.Contains("totally_not_an_op"));  // unknown op
                Assert.Contains(r.Findings, f => f.Severity == "warn" && f.Message.Contains("doesNotExist"));   // unresolved when/probe input
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Check_flags_a_probe_kind_verify_that_declares_no_probe()
        {
            var (e, ws) = Make();
            try
            {
                await e.SaveWorkflowAsync("no-probe-flow", NoProbeMd, "human");
                var r = await e.CheckWorkflowAsync("no-probe-flow");

                Assert.Null(r.ParseError);
                Assert.False(r.Ok);                                         // a probe-kind verify with no probe is not admissible
                Assert.Contains(r.Findings, f => f.Severity == "warn" && f.Message.Contains("no probe"));
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Clean_stock_workflow_passes_the_dry_run()
        {
            var (e, ws) = Make();
            try
            {
                var r = await e.CheckWorkflowAsync("new-measure");          // shipped beside the test binary
                Assert.Null(r.ParseError);
                Assert.True(r.Ok, "clean stock workflow should have no warn findings: " + string.Join(" | ", r.Findings.Select(f => f.Severity + ":" + f.Message)));
                Assert.DoesNotContain(r.Findings, f => f.Severity == "warn");
            }
            finally { Directory.Delete(ws, true); }
        }

        // The FULL 14-workflow launch set (all shipped stock workflows are gated): every one must survive the
        // admission dry-run — its triggers/ops are real ops, every verify probe/when names a collected input,
        // and each dax_probe/dax_equivalence/object-bpa verify has an objectRef target to act on. Regression-
        // tests check_workflow admission per shipped workflow, so a future edit naming a phantom op or an
        // unresolved probe is caught here, not in production.
        [Theory]
        [InlineData("add-relationship")]
        [InlineData("calendar-setup")]
        [InlineData("deploy-to-production")]
        [InlineData("governed-rename")]
        [InlineData("import-table")]
        [InlineData("incremental-refresh-setup")]
        [InlineData("make-ai-ready")]
        [InlineData("model-hygiene-pass")]
        [InlineData("new-measure")]
        [InlineData("optimize-dax")]
        [InlineData("refactor-to-calculation-group")]
        [InlineData("secure-with-rls")]
        [InlineData("time-intelligence-variants")]
        [InlineData("verified-measure")]
        public async Task Every_stock_workflow_passes_the_admission_dry_run(string name)
        {
            var (e, ws) = Make();
            try
            {
                var r = await e.CheckWorkflowAsync(name);
                Assert.Null(r.ParseError);
                Assert.True(r.Ok, $"'{name}' should have no warn findings: " + string.Join(" | ", r.Findings.Select(f => f.Severity + ":" + f.Message)));
                Assert.DoesNotContain(r.Findings, f => f.Severity == "warn");
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Missing_workflow_throws_instructively()
        {
            var (e, ws) = Make();
            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.CheckWorkflowAsync("no-such-workflow"));
                Assert.Contains("not found", ex.Message);
            }
            finally { Directory.Delete(ws, true); }
        }
    }
}
