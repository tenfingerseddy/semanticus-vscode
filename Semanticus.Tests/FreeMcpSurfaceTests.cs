using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Semanticus.Engine.Lineage;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The ratified adoption contract: Free users can ask the referee one question at a time and
    /// create their own source-control safety points. Pro gates automation and recorded enforcement, never
    /// these read-only/manual checks. Driving the shared engine pins both doors; the equivalence assertion
    /// also traverses its public MCP wrapper.
    ///
    /// Widened on 22 September 2026, when Kane set "free is the tool, Pro is the tool that works for you" and
    /// eight whole tabs came across. The new tests at the bottom drive one operation from each of those tabs on
    /// a free engine, because a gate removed from the map but still firing somewhere is exactly what an
    /// untested removal looks like.</summary>
    public sealed class FreeMcpSurfaceTests
    {
        private sealed class FreeEntitlement : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "free" };
        }

        private static async Task<LocalEngine> OpenAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new FreeEntitlement());
            await engine.OpenAsync(TestModels.FindBim());
            return engine;
        }

        [Fact]
        public async Task Free_keeps_on_demand_dax_checks_without_manufacturing_live_evidence()
        {
            using var engine = await OpenAsync();

            var validation = await engine.ValidateDaxAsync("1 + 1");
            Assert.True(validation.Valid);

            var query = await engine.RunDaxAsync("EVALUATE ROW(\"value\", 1)", 10, "agent");
            Assert.Contains("Not connected", query.Error);
            Assert.False(query.PolicyRefused);

            var equivalence = await McpTools.VerifyDaxEquivalence(
                engine, "1", "1", System.Array.Empty<string>());
            Assert.Contains("Not connected", equivalence.Error);
            Assert.False(equivalence.AllMatch);
            Assert.Equal(0, equivalence.RowsCompared);
        }

        [Fact]
        public async Task Free_keeps_single_object_lineage_and_audit_reads()
        {
            using var engine = await OpenAsync();
            var measure = (await engine.ListMeasuresAsync()).First();

            var lineage = await engine.GetLineageAsync();
            Assert.NotEmpty(lineage.Nodes);

            var impact = await engine.ImpactAssessmentAsync(new ImpactAssessmentRequest
            {
                ObjectRef = measure.Ref,
                Intent = "change",
                Scope = "model"
            });
            Assert.Equal(measure.Ref, impact.ObjectRef);

            var unused = await engine.UnusedObjectsAsync();
            Assert.NotNull(unused.Items);

            var audit = await engine.ListVerifiedEditsAsync();
            Assert.True(audit.ChainIntact);
        }

        [Fact]
        public async Task Free_keeps_manual_durable_checkpoint_create_list_and_restore()
        {
            // A durable checkpoint is a git commit, so this must run inside a real repo ON A BRANCH - not the
            // ambient checkout, which CI clones at a detached HEAD (where create/restore correctly refuse).
            var (dir, model) = InitRepoWithModel();
            try
            {
                var engine = new LocalEngine(new SessionManager(), new FreeEntitlement());
                await engine.OpenAsync(model);
                using (engine)
                {
                    var list = await engine.ListHistoryCheckpointsAsync();
                    Assert.True(list.Supported);

                    var create = await engine.CreateHistoryCheckpointAsync("Before experiment", commit: false, "agent");
                    Assert.True(create.Preview);
                    Assert.Null(create.Error);

                    var restore = await engine.RestoreHistoryCheckpointAsync("abc1234", restore: false, "agent");
                    Assert.True(restore.Preview);
                    Assert.Contains("current branch history", restore.Error);
                }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort temp cleanup */ }
            }
        }

        // ---- the eight tabs that became free on 22 September 2026 -------------------------------------------

        /// <summary>One operation per freed tab, driven on a real free engine. Only an entitlement refusal fails
        /// this: anything else an operation may refuse for (no live connection, nothing to do, a bad ref) is not
        /// what is being proven here, which is the same rule ProGateRemovalTests uses.</summary>
        [Fact]
        public async Task Free_reaches_every_tab_that_stopped_being_Pro()
        {
            using var engine = await OpenAsync();

            // Collected rather than failed one at a time, so a run names EVERY tab that is still gated
            // instead of stopping at the first. The assertion is the list, which is the claim this test
            // makes: no tab on it refuses a free session on entitlement grounds.
            var stillGated = new[]
            {
                await RefusalForAsync(() => engine.AutogenerateSpecFromModelAsync("human"), "Model Spec"),
                await RefusalForAsync(() => engine.ListCalendarsAsync(null), "Advanced modelling"),
                await RefusalForAsync(() => engine.ListPartitionsAsync("table:Sales"), "Power Query"),
                await RefusalForAsync(() => engine.GetDocOutlineAsync(), "Docs"),
                await RefusalForAsync(() => engine.ListInsightsAsync(null, null), "Model notes"),
                await RefusalForAsync(() => engine.ListTestDefinitionsAsync(), "Validation"),
                await RefusalForAsync(() => engine.ListEvidenceAsync(), "Reports"),
                await RefusalForAsync(() => engine.GitLogAsync(5), "Published, advanced"),
            }.Where(x => x != null).ToArray();

            Assert.Equal(Array.Empty<string>(), stillGated);
        }

        /// <summary>And the two things that stayed paid still refuse, so the sweep above is proving a change
        /// rather than proving the gate stopped working.</summary>
        [Fact]
        public async Task Free_is_still_refused_the_two_things_that_stayed_Pro()
        {
            using var engine = await OpenAsync();
            await Assert.ThrowsAsync<EntitlementException>(() => engine.ListWorkflowsAsync());
            await Assert.ThrowsAsync<EntitlementException>(() => engine.RouteAskAsync("create a measure"));
        }

        /// <summary>The entitlement refusal this call produced, named by its tab, or NULL when there was
        /// none. Anything else the operation may refuse for (no live connection, nothing to do, a bad ref) is
        /// swallowed on purpose: this test is about the gate and nothing else.</summary>
        private static async Task<string> RefusalForAsync(Func<Task> call, string tab)
        {
            try { await call(); }
            catch (EntitlementException ex) { return tab + " is still Pro-gated on free: " + ex.Message; }
            catch { /* out of scope for the gate */ }
            return null;
        }

        // A durable checkpoint commits into git, so the free-tier checkpoint test needs its own repo on a branch
        // (mirrors HistoryCheckpointTests) rather than the ambient checkout that CI clones at a detached HEAD.
        private static (string Dir, string Model) InitRepoWithModel()
        {
            var dir = Path.Combine(Path.GetTempPath(), "free-checkpoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            Git(dir, "init", "-q");
            Git(dir, "config", "user.email", "free@test.local");
            Git(dir, "config", "user.name", "Free Test");
            Git(dir, "config", "commit.gpgsign", "false");
            Git(dir, "add", "--", "model.bim");
            Git(dir, "commit", "-q", "-m", "initial");
            return (dir, model);
        }

        private static void Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
            p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException(stderr);
        }
    }
}
