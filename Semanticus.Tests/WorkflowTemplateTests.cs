using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Workflow TEMPLATES (docs/pro-mode-spec.md §10-T1) — the customisation layer. Proves the load-bearing
    /// pieces: templates are a SEPARATE shelf (never leak into the workflow library or its count); stock templates
    /// ship proven-instantiable (check_workflow trial); a happy instantiation is a valid runnable
    /// workflow carrying re-instantiable provenance; user shadows stock then reverts on delete; and — the
    /// security spine — the STRUCTURE-PRESERVING INVARIANT refuses every slot value that tries to add an op,
    /// a gate, a check, a step, or change strictness, each with a plain-language diff and nothing written.
    /// </summary>
    public sealed class WorkflowTemplateTests
    {
        private sealed class Free : IEntitlement { public bool IsPro => false; public EntitlementInfo Info => new EntitlementInfo { Tier = "free" }; }

        private static (LocalEngine e, string ws) Make()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-wftmpl-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            return (new LocalEngine(new SessionManager(), new Free(), ws), ws);
        }

        private static readonly string[] StockTemplates = { "metric-certification", "month-end-close", "deploy-freeze-guard", "hard-measure" };

        // Benign values that fill metric-certification correctly (all single-line — the parser's scalar rule).
        private const string GoodCertValues =
            "{\"surface_name\":\"the FY26 Exec Dashboard\"," +
            "\"kpi_dictionary\":\"measure:Sales/Net Sales — net sales — owner: CFO — from the FY25 board pack\"," +
            "\"escalation_rule\":\"stop and raise with the owner — never adjust finance figures to match the model\"," +
            "\"certification_tag\":\"Certified FY26-Q1\"}";

        // ---- the shelf is not the board -----------------------------------------------------------

        [Fact]
        public async Task Stock_templates_are_listed_and_never_leak_into_the_workflow_library()
        {
            var (e, ws) = Make();
            try
            {
                var tmpls = await e.ListWorkflowTemplatesAsync();
                foreach (var name in StockTemplates)
                    Assert.Contains(tmpls, t => t.Name == name && t.Error == null && t.Source == "stock" && t.SlotCount > 0);

                var wfNames = (await e.ListWorkflowsAsync()).Select(w => w.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var name in StockTemplates)
                    Assert.DoesNotContain(name, wfNames);   // a template must never appear as a runnable workflow
            }
            finally { Directory.Delete(ws, true); }
        }

        [Theory]
        [InlineData("metric-certification")]
        [InlineData("month-end-close")]
        [InlineData("deploy-freeze-guard")]
        [InlineData("hard-measure")]
        public async Task Stock_template_ships_proven_instantiable(string name)
        {
            var (e, ws) = Make();
            try
            {
                // check_workflow on a template validates decls<->refs AND trial-instantiates with the example values
                // through the full admission — a stock template must be clean (no warn findings).
                var r = await e.CheckWorkflowAsync(name);
                Assert.Null(r.ParseError);
                Assert.True(r.Ok, $"'{name}' template trial-instantiation had warns: " + string.Join(" | ", r.Findings.Select(f => f.Severity + ":" + f.Message)));
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Template_is_not_runnable_start_workflow_teaches_instantiate()
        {
            var (e, ws) = Make();
            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.StartWorkflowAsync("metric-certification", "human"));
                Assert.Contains("template", ex.Message);
                Assert.Contains("instantiate_workflow_template", ex.Message);   // the error names the recovery op
            }
            finally { Directory.Delete(ws, true); }
        }

        // ---- happy path: instantiate -> runnable workflow + re-instantiable provenance -------------

        [Fact]
        public async Task Instantiate_produces_a_runnable_workflow_with_provenance()
        {
            var (e, ws) = Make();
            try
            {
                var before = (await e.ListWorkflowsAsync()).Length;
                var lib = await e.InstantiateWorkflowTemplateAsync("metric-certification", "fy26-metric-cert", GoodCertValues, "human");

                Assert.Contains(lib, w => w.Name == "fy26-metric-cert" && w.Error == null);
                Assert.Equal(before + 1, (await e.ListWorkflowsAsync()).Length);   // exactly one new workflow

                var def = await e.GetWorkflowAsync("fy26-metric-cert");
                Assert.Null(def.Error);
                Assert.Null(def.Kind);                                              // the instance is a workflow, not a template
                Assert.Empty(def.Slots);                                            // slots were consumed at instantiation
                Assert.Equal("metric-certification", def.Provenance["template"]);
                Assert.Equal("1", def.Provenance["template_version"]);
                Assert.True(def.Provenance.ContainsKey("instantiated"));
                Assert.Contains("FY26 Exec Dashboard", def.Provenance["slot_values"]);   // slot values recorded for re-instantiation

                // The instance is admissible (re-check it — provenance is info, never a warn).
                var chk = await e.CheckWorkflowAsync("fy26-metric-cert");
                Assert.True(chk.Ok, "instance not admissible: " + string.Join(" | ", chk.Findings.Select(f => f.Severity + ":" + f.Message)));
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Instance_can_be_reinstantiated_from_its_recorded_slot_values()
        {
            var (e, ws) = Make();
            try
            {
                await e.InstantiateWorkflowTemplateAsync("metric-certification", "fy26-metric-cert", GoodCertValues, "human");
                var recorded = (await e.GetWorkflowAsync("fy26-metric-cert")).Provenance["slot_values"];

                // Re-render the SAME template from the stored slot_values under a new name (the §10.4 upgrade path).
                var lib = await e.InstantiateWorkflowTemplateAsync("metric-certification", "fy26-metric-cert-copy", recorded, "human");
                Assert.Contains(lib, w => w.Name == "fy26-metric-cert-copy" && w.Error == null);
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Missing_required_slot_teaches_the_slot_and_its_example()
        {
            var (e, ws) = Make();
            try
            {
                // Omit surface_name (required); provide the rest.
                var vals = "{\"kpi_dictionary\":\"x\",\"escalation_rule\":\"y\"}";
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.InstantiateWorkflowTemplateAsync("metric-certification", "cert-out", vals, "human"));
                Assert.Contains("surface_name", ex.Message);
                Assert.Contains("FY26 Exec Dashboard", ex.Message);                 // the slot's own example is in the teaching error
                Assert.DoesNotContain("cert-out", (await e.ListWorkflowsAsync()).Select(w => w.Name));   // nothing written
            }
            finally { Directory.Delete(ws, true); }
        }

        // ---- shadow / revert / read-only stock ----------------------------------------------------

        private const string ShadowMd = @"---
kind: template
name: metric-certification
title: My {{surface}} certification
version: 2
strictness: hard
slots:
  - name: surface
    question: ""What surface?""
    type: text
    required: required
    example: ""the exec dashboard""
---

## Step 1: Do it
Certify {{surface}} carefully.
";

        [Fact]
        public async Task User_template_shadows_stock_then_delete_reverts_to_stock()
        {
            var (e, ws) = Make();
            try
            {
                await e.SaveWorkflowTemplateAsync("metric-certification", ShadowMd, "human");
                var shadowed = (await e.ListWorkflowTemplatesAsync()).Single(t => t.Name == "metric-certification");
                Assert.Equal("user", shadowed.Source);
                Assert.Equal(1, shadowed.SlotCount);                                // the user copy has a single slot

                await e.DeleteWorkflowTemplateAsync("metric-certification", "human");
                var reverted = (await e.ListWorkflowTemplatesAsync()).Single(t => t.Name == "metric-certification");
                Assert.Equal("stock", reverted.Source);                            // stock is back, unharmed
                Assert.True(reverted.SlotCount >= 4);                              // the stock metric-certification's slots
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Deleting_a_stock_template_without_a_user_copy_is_refused()
        {
            var (e, ws) = Make();
            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.DeleteWorkflowTemplateAsync("month-end-close", "human"));
                Assert.Contains("stock", ex.Message);
                Assert.Contains("read-only", ex.Message);
            }
            finally { Directory.Delete(ws, true); }
        }

        // ---- save-time validation (junk never lands on disk) --------------------------------------

        [Fact]
        public async Task Save_refuses_an_undeclared_slot_reference()
        {
            var (e, ws) = Make();
            try
            {
                var md = "---\nkind: template\nname: bad-refs\nstrictness: warn\nslots:\n  - name: a\n    question: \"q\"\n    type: text\n    required: required\n    example: \"ex\"\n---\n\n## Step 1: X\nBody uses {{a}} and {{undeclared}}.\n";
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.SaveWorkflowTemplateAsync("bad-refs", md, "human"));
                Assert.Contains("undeclared", ex.Message);
                Assert.DoesNotContain("bad-refs", (await e.ListWorkflowTemplatesAsync()).Select(t => t.Name));   // nothing written
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Save_refuses_a_slot_with_no_example()
        {
            var (e, ws) = Make();
            try
            {
                var md = "---\nkind: template\nname: no-ex\nstrictness: warn\nslots:\n  - name: a\n    question: \"q\"\n    type: text\n    required: required\n---\n\n## Step 1: X\nBody uses {{a}}.\n";
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.SaveWorkflowTemplateAsync("no-ex", md, "human"));
                Assert.Contains("example", ex.Message);
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Save_refuses_a_workflow_missing_kind_template()
        {
            var (e, ws) = Make();
            try
            {
                var md = "---\nname: not-a-template\nstrictness: warn\n---\n\n## Step 1: X\nBody.\n";
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.SaveWorkflowTemplateAsync("not-a-template", md, "human"));
                Assert.Contains("kind: template", ex.Message);
            }
            finally { Directory.Delete(ws, true); }
        }

        // ---- THE STRUCTURE-PRESERVING INVARIANT — the injection defence ----------------------------

        // A user template whose slots flow into prose (bodyText, last step), a gate-input hint (noteText), and
        // the frontmatter title (titleText). Each injection payload below tries to weaponise one of those.
        private const string InjTemplateMd = @"---
kind: template
name: inj-probe
strictness: hard
title: {{titleText}}
slots:
  - name: titleText
    question: ""A title""
    type: text
    required: required
    example: ""My Process""
  - name: noteText
    question: ""Extra reviewer guidance""
    type: text
    required: required
    example: ""review the totals""
  - name: bodyText
    question: ""Closing notes""
    type: text
    required: required
    example: ""all done""
---

## Step 1: Review
Please review carefully.
```yaml gate
inputs:
  - name: field1
    question: ""Did you review it?""
    type: text
    required: required
    hint: {{noteText}}
```

## Step 2: Wrap up
Wrap-up notes follow.
{{bodyText}}
";

        // Each case: (label, which slot carries the payload, the malicious value). All must be REFUSED.
        public static TheoryData<string, string, string> Injections() => new()
        {
            // add an op to the (gateless) last step by smuggling an ops fence
            { "add-op",        "bodyText",  "ok\n\n```yaml gate\nops: [delete_object]\n```" },
            // add a whole gate + verify check to the last step
            { "add-gate",      "bodyText",  "ok\n\n```yaml gate\nstrictness: hard\nverify:\n  - kind: bpa_clean\n    scope: model\n```" },
            // add an extra step
            { "add-step",      "bodyText",  "ok\n\n## Step 3: Injected\nMalice here." },
            // weaken a gate input's required level from required to optional
            { "alter-input",   "noteText",  "guidance\n    required: optional" },
            // downgrade the whole-workflow strictness by smuggling a second frontmatter key
            { "change-strict", "titleText", "T\nstrictness: warn" },
        };

        [Theory]
        [MemberData(nameof(Injections))]
        public async Task Injection_via_slot_value_is_refused_with_a_plain_diff_and_nothing_written(string label, string slot, string payload)
        {
            var (e, ws) = Make();
            try
            {
                await e.SaveWorkflowTemplateAsync("inj-probe", InjTemplateMd, "human");
                var before = (await e.ListWorkflowsAsync()).Length;

                // All required slots present; the malicious value goes in exactly one of them.
                var vals = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["titleText"] = "My Process", ["noteText"] = "review the totals", ["bodyText"] = "all done",
                };
                vals[slot] = payload;

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.InstantiateWorkflowTemplateAsync("inj-probe", "inj-out", JsonSerializer.Serialize(vals), "human"));

                // The refusal is plain-language (§10.12: no engine jargon), names the offending slot, and says a
                // slot value can't change the enforced structure.
                Assert.Contains("refused", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(slot, ex.Message);
                Assert.Contains("never", ex.Message);          // "...slot values can change wording and values, never checks."
                Assert.DoesNotContain("skeleton", ex.Message, StringComparison.OrdinalIgnoreCase);   // no jargon leaks

                // Nothing landed on disk — the invariant runs BEFORE render/save.
                Assert.Equal(before, (await e.ListWorkflowsAsync()).Length);
                Assert.DoesNotContain("inj-out", (await e.ListWorkflowsAsync()).Select(w => w.Name));
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Benign_instantiation_of_the_same_template_still_succeeds()
        {
            // The invariant must not be a false-positive machine: ordinary values (even with punctuation) pass.
            var (e, ws) = Make();
            try
            {
                await e.SaveWorkflowTemplateAsync("inj-probe", InjTemplateMd, "human");
                var vals = "{\"titleText\":\"Q1: Exec Review\",\"noteText\":\"check net vs gross\",\"bodyText\":\"done — signed off\"}";
                var lib = await e.InstantiateWorkflowTemplateAsync("inj-probe", "inj-good", vals, "human");
                Assert.Contains(lib, w => w.Name == "inj-good" && w.Error == null);
            }
            finally { Directory.Delete(ws, true); }
        }
    }
}
