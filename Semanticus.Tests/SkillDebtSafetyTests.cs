using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    /// <summary>
    /// SAFETY BENCHMARK 2 — skill debt (docs/learning-loop-testing.md §7.2). The distiller is fed a realistic mix
    /// of genuine wins AND overfit/degenerate "skills"; the admission pipeline (save_workflow parse-validate →
    /// check_workflow dry-run) must REJECT the bad ones so the learned library never carries debt. The research's
    /// "−1.3pp without verification" is flipped non-negative BY that gate: since no degenerate skill is ever
    /// admitted, the net learned-library effect vs the no-library baseline is ≥ 0 by construction.
    ///   PASS BAR: every genuine skill admitted; ZERO degenerate skills admitted; net = admittedGood − admittedBad ≥ 0.
    /// Deterministic, no model/live connection (check is a static resolve) → CI-able every run. Ship-blocker.
    /// </summary>
    public sealed class SkillDebtSafetyTests
    {
        private readonly ITestOutputHelper _out;
        public SkillDebtSafetyTests(ITestOutputHelper o) => _out = o;

        private sealed class Free : IEntitlement { public bool IsPro => false; public EntitlementInfo Info => new EntitlementInfo { Tier = "free" }; }

        private static (LocalEngine e, SessionManager sessions, string ws) Make()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-skilldebt-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            var sessions = new SessionManager();
            return (new LocalEngine(sessions, new Free(), ws), sessions, ws);
        }

        // ---- the corpus: genuine wins the admission layer must KEEP -----------------------------------------------

        // A plain, well-formed workflow (no verify to resolve) — trivially admissible.
        private const string GoodSimple = @"---
name: good-simple
title: A simple, well-formed skill
strictness: warn
---
## Step 1: Ask
Ask the one question.
```yaml gate
inputs:
  - name: note
    question: ""What is the note?""
    required: optional
```
";

        // A verified skill: real ops, a dax_probe whose probe input is collected, and an objectRef target to act on.
        private const string GoodVerified = @"---
name: good-verified
title: A verified authoring skill
strictness: hard
---
## Step 1: Create and verify
Create the measure, then verify it against a known-good number.
```yaml gate
ops: [create_measure, probe_measure]
inputs:
  - name: target
    question: ""The ref of the measure you created.""
    type: objectRef
    required: required
  - name: known
    question: ""A known-good value from the business.""
    type: verification
    required: answer-or-decline
verify:
  - kind: dax_probe
    when: inputs.known.answered
    probe: known
```
";

        // ---- the corpus: degenerate skills the admission layer must REJECT ---------------------------------------

        // Overfit to a phantom op that does not exist — the classic "distilled from one lucky run" debt.
        private const string BadPhantomOp = @"---
name: bad-phantom-op
title: Names an op that does not exist
strictness: warn
---
## Step 1: Do
Do the thing.
```yaml gate
ops: [totally_not_an_op]
inputs:
  - name: note
    question: ""A note?""
    required: optional
```
";

        // A verify that probes an input NO gate collects — it can never actually run (an unrunnable gate).
        private const string BadProbeNoInput = @"---
name: bad-probe-no-input
title: Probes an input nothing collects
strictness: hard
---
## Step 1: Verify
Verify equivalence against a recorded original.
```yaml gate
inputs:
  - name: target
    question: ""The measure ref.""
    type: objectRef
    required: required
verify:
  - kind: dax_equivalence
    probe: ghostInput
```
";

        // A DAX verify with NO objectRef target anywhere — the check cannot know what to act on.
        private const string BadNoTarget = @"---
name: bad-no-target
title: A dax verify with no target
strictness: hard
---
## Step 1: Probe
Probe against a known value.
```yaml gate
inputs:
  - name: known
    question: ""A known value.""
    type: verification
    required: answer-or-decline
verify:
  - kind: dax_probe
    probe: known
```
";

        // Malformed — an input type outside the grammar. The parser refuses it, so save_workflow never writes it.
        private const string BadParseError = @"---
name: bad-parse-error
title: Malformed gate
strictness: warn
---
## Step 1: Ask
Ask.
```yaml gate
inputs:
  - name: x
    question: ""X?""
    type: not_a_real_type
    required: required
```
";

        // "admitted" = the WHOLE pipeline accepts it: save_workflow parses+writes it AND check_workflow returns Ok.
        // A parse-time refusal (save throws) OR a check warn both mean NOT admitted — the two admission stages.
        private static async Task<bool> Admit(LocalEngine e, string name, string md)
        {
            try
            {
                await e.SaveWorkflowAsync(name, md, "agent");
                var report = await e.CheckWorkflowAsync(name);
                return report.Ok;
            }
            catch { return false; }   // refused at save (parse error) — never enters the library
        }

        [Fact]
        public async Task Admission_admits_every_genuine_skill_and_rejects_every_degenerate_one()
        {
            var (e, sessions, ws) = Make();
            try
            {
                var good = new (string name, string md)[]
                {
                    ("good-simple", GoodSimple),
                    ("good-verified", GoodVerified),
                };
                var bad = new (string name, string md)[]
                {
                    ("bad-phantom-op", BadPhantomOp),
                    ("bad-probe-no-input", BadProbeNoInput),
                    ("bad-no-target", BadNoTarget),
                    ("bad-parse-error", BadParseError),
                };

                var goodAdmitted = new List<string>();
                foreach (var (name, md) in good)
                    if (await Admit(e, name, md)) goodAdmitted.Add(name);

                var badAdmitted = new List<string>();
                foreach (var (name, md) in bad)
                    if (await Admit(e, name, md)) badAdmitted.Add(name);

                var net = goodAdmitted.Count - badAdmitted.Count;
                _out.WriteLine($"[skill-debt] genuine admitted={goodAdmitted.Count}/{good.Length}  " +
                               $"degenerate admitted={badAdmitted.Count}/{bad.Length}  net-library-effect={net}");
                if (badAdmitted.Count > 0) _out.WriteLine("  LEAKED: " + string.Join(", ", badAdmitted));

                Assert.Equal(good.Length, goodAdmitted.Count);   // every genuine win is kept
                Assert.Empty(badAdmitted);                        // PASS BAR: zero degenerate skills admitted
                Assert.True(net >= 0, "net learned-library effect must be non-negative — the admission gate flips the research's -1.3pp");
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // The two admission STAGES do distinct work: pin that each degenerate kind is caught by the stage meant to
        // catch it — parse-refusal at save for the malformed one, a check-warn for the parse-clean-but-broken ones.
        [Fact]
        public async Task Each_degenerate_kind_is_caught_by_its_admission_stage()
        {
            var (e, sessions, ws) = Make();
            try
            {
                // Malformed → REFUSED AT SAVE (never written to the library).
                await Assert.ThrowsAnyAsync<Exception>(() => e.SaveWorkflowAsync("bad-parse-error", BadParseError, "agent"));

                // Parse-clean but broken → written, then REJECTED BY check_workflow with a naming finding.
                foreach (var (name, md, needle) in new[]
                {
                    ("bad-phantom-op", BadPhantomOp, "totally_not_an_op"),
                    ("bad-probe-no-input", BadProbeNoInput, "ghostInput"),
                    ("bad-no-target", BadNoTarget, "objectRef"),
                })
                {
                    await e.SaveWorkflowAsync(name, md, "agent");
                    var rep = await e.CheckWorkflowAsync(name);
                    Assert.False(rep.Ok, $"'{name}' should be rejected by check_workflow");
                    Assert.Contains(rep.Findings, f => f.Severity == "warn" && f.Message.Contains(needle));
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }
    }
}
