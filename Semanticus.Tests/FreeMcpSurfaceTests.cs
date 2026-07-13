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
    /// also traverses its public MCP wrapper.</summary>
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
