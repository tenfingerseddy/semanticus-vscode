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
    /// The designer/authoring surface (docs/workflow-designer-plan.md §2): save_workflow's
    /// parse-validate-before-write contract (a broken file is NEVER written), stock shadowing +
    /// delete semantics (stock is read-only by construction), the `ops:` action-chain grammar, the
    /// workflow/libraryDidChange broadcast, and the reflected op catalog that can't drift from the
    /// real MCP tool surface.
    /// </summary>
    public sealed class WorkflowDesignerTests
    {
        private sealed class Free : IEntitlement { public bool IsPro => false; public EntitlementInfo Info => new EntitlementInfo { Tier = "free" }; }

        private const string OpsMd = @"---
name: with-ops
title: Ops chain visibility
---
## Step 1: Build
Create the measure, then format it.
```yaml gate
ops: [create_measure, set_measure_format]   # the declared action chain
```

## Step 2: Gate and chain
Verify it.
```yaml gate
ops: [bpa_scan]
strictness: warn
inputs:
  - name: note
    question: ""Anything to record?""
    required: optional
```
";

        private static (LocalEngine e, SessionManager s, string ws) Make()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-wfdesign-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            var sessions = new SessionManager();
            return (new LocalEngine(sessions, new Free(), ws), sessions, ws);
        }

        [Fact]
        public void Ops_chain_parses_and_an_ops_only_fence_does_not_create_a_gate()
        {
            var d = WorkflowParser.Parse(OpsMd);
            Assert.Null(d.Error);
            Assert.Equal(new[] { "create_measure", "set_measure_format" }, d.Steps[0].Ops);
            Assert.Null(d.Steps[0].Gate);                              // ops-only fence: chain declared, nothing gated
            Assert.Equal(new[] { "bpa_scan" }, d.Steps[1].Ops);
            Assert.NotNull(d.Steps[1].Gate);                           // ops + real gate coexist in one fence
            Assert.True(d.HasEnforcedGate());                          // a warn gate still records evidence — that IS enforcement
        }

        [Fact]
        public async Task Save_validates_before_writing_and_a_broken_file_is_never_written()
        {
            var (e, _, ws) = Make();
            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SaveWorkflowAsync("broken", "not a workflow at all", "human"));
                Assert.Contains("does not parse", ex.Message);
                Assert.Contains("frontmatter", ex.Message);            // the parser error rides back verbatim
                Assert.False(File.Exists(Path.Combine(ws, ".semanticus", "workflows", "broken.md")));

                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SaveWorkflowAsync("Not Kebab!", OpsMd, "human"));
                await Assert.ThrowsAsync<InvalidOperationException>(   // frontmatter name must equal the save name
                    () => e.SaveWorkflowAsync("other-name", OpsMd, "human"));

                var list = await e.SaveWorkflowAsync("with-ops", OpsMd, "human");
                Assert.True(File.Exists(Path.Combine(ws, ".semanticus", "workflows", "with-ops.md")));
                Assert.Contains(list, w => w.Name == "with-ops" && w.Source == "user" && w.Error == null);
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Save_broadcasts_the_refreshed_library_and_delete_honors_stock_read_only()
        {
            var (e, sessions, ws) = Make();
            try
            {
                WorkflowInfo[] broadcast = null;
                sessions.Bus.WorkflowLibraryChanged += v => broadcast = v;

                await e.SaveWorkflowAsync("with-ops", OpsMd, "human");
                Assert.NotNull(broadcast);
                Assert.Contains(broadcast, w => w.Name == "with-ops");

                // shadow a STOCK workflow (the seed library ships beside the test binary), then delete
                // the shadow — the stock one must reappear (delete = revert-to-stock, never a hole)
                var stockMd = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "workflows", "new-measure.md"));
                var afterShadow = await e.SaveWorkflowAsync("new-measure", stockMd.Replace("version: 1", "version: 9"), "human");
                Assert.Equal(9, (await e.GetWorkflowAsync("new-measure")).Version);
                Assert.Equal("user", afterShadow.Single(w => w.Name == "new-measure").Source);

                var afterDelete = await e.DeleteWorkflowAsync("new-measure", "human");
                Assert.Equal("stock", afterDelete.Single(w => w.Name == "new-measure").Source);

                // deleting a stock name with no user copy is refused instructively
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.DeleteWorkflowAsync("new-measure", "human"));
                Assert.Contains("read-only", ex.Message);
                await Assert.ThrowsAsync<InvalidOperationException>(() => e.DeleteWorkflowAsync("no-such", "human"));
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Op_catalog_reflects_the_real_mcp_tool_surface()
        {
            var (e, _, ws) = Make();
            try
            {
                var cat = await e.GetOpCatalogAsync();
                Assert.True(cat.Length > 100);                          // the real surface, not a hand-kept list
                foreach (var expected in new[] { "create_measure", "run_dax", "start_workflow", "save_workflow", "export_workflow_evidence", "get_op_catalog" })
                    Assert.Contains(cat, o => o.Name == expected);
                Assert.All(cat, o => Assert.False(string.IsNullOrWhiteSpace(o.Description)));
                Assert.All(cat, o => Assert.True(o.Description.Length <= 201));   // first sentence, picker-sized
            }
            finally { Directory.Delete(ws, true); }
        }
    }
}
