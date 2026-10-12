// Plan statement check 4 (verifier, 4 Oct, m11/join 676a6ac8): a defect found on the real app, left failing on purpose.
//
// A Free start on the J8 tables, the assistant's words written the way a careful assistant writes them (a business name for every table, column and measure, keep_name
// where the refusal asks for it), approved and built. Every measure the build made must still point at objects the model has. On 676a6ac8 the Free build renames the
// tables, columns and measures and leaves the measures' formulas on the source names: 'fact_timesheet'[HRS], 'dim_date'[CAL_DT], [Total HRS], [Total INV_AMT]. The
// app's own validate_dax answers "Unknown table 'fact_timesheet'", and neither AI readiness nor best practice notices. The Pro build of the same source keeps its
// formulas whole ('Timesheets'[HRS], 'Date'[Date], [Total Hours]).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class VerifyPlanStatementCheck4Tests : IDisposable
    {
        private const string Server = "wh-j8.datawarehouse.fabric.microsoft.com", Db = "ContosoConsulting";
        private readonly RestoreRootFixture _fixture;
        private readonly string _root;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public VerifyPlanStatementCheck4Tests(RestoreRootFixture fixture)
        {
            _fixture = fixture;
            _root = Path.Combine(Path.GetTempPath(), "psc4-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            Directory.CreateDirectory(Path.Combine(_root, "policy"));
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "home"));
            ConnectionRegistry.RootOverride = Path.Combine(_root, "user");
            AgentPolicyStore.RootOverride = Path.Combine(_root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(_root, "policy");
        }

        public void Dispose()
        {
            foreach (var o in _owned.AsEnumerable().Reverse()) { try { o.Dispose(); } catch { } }
            ConnectionRegistry.RootOverride = _fixture.Root; AgentPolicyStore.RootOverride = _fixture.Root; ApprovalLedger.RootOverride = _fixture.Root;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static string Humanise(string r)
        {
            var name = (r ?? "").Substring((r ?? "").LastIndexOf('/') + 1);
            var colon = name.IndexOf(':'); if (colon >= 0) name = name.Substring(colon + 1);
            return string.Join(" ", name.Split('_', ' ').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1).ToLowerInvariant()));
        }

        // The words a careful assistant writes: a plain business name that is not the one the object has, plus a description, synonyms and the AI instructions.
        private static StartWord[] WordsFor(StartWordsPage page) => page.Items.Select(i => new StartWord
        {
            Ref = i.Ref, Kind = i.Kind,
            Text = i.Kind == "synonyms" ? "alias one, alias two"
                 : i.Kind == "display_name" ? (string.Equals(Humanise(i.Ref), (i.Ref ?? "").Substring((i.Ref ?? "").LastIndexOf('/') + 1), StringComparison.OrdinalIgnoreCase) ? Humanise(i.Ref) + " Value" : Humanise(i.Ref))
                 : i.Kind == "instructions" ? "This model answers questions about consulting work: hours, projects, clients and invoices."
                 : "What " + Humanise(i.Ref) + " means for the business, in plain words.",
        }).ToArray();

        private async Task<(string[] Broken, string[] All)> BuildAsync(bool pro)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, pro ? TestEntitlements.Pro : TestEntitlements.Free, Path.Combine(_root, "ws-" + (pro ? "pro" : "free")));
            _owned.Add(engine); _owned.Add(sessions);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            if (pro) engine.UseDecisionsForTest(new DecisionService(new StartPlanDecideTests.PolicyOracle()));
            var folder = Path.Combine(_root, "model-" + (pro ? "pro" : "free"));
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = folder, Name = "Uplift" }, "human");
            Assert.True(view.Exists);
            if (pro)
            {
                await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
                view = await engine.GrantStartDisclosureAsync(view.Digest, "human");
                await engine.StartDecideSettledForTest();
            }
            for (var pass = 0; pass < 60; pass++)
            {
                var page = await engine.GetStartWordsAsync(40, 0);
                if (page.Items.Count == 0) break;
                var wrote = await engine.WriteStartWordsAsync(WordsFor(page), "agent");
                var keeps = wrote.Refused.Where(r => r.Contains("keep_name")).Select(r => r.Substring(0, r.IndexOf(": ", StringComparison.Ordinal))).Distinct().ToArray();
                var kept = keeps.Length == 0 ? 0 : (await engine.WriteStartWordsAsync(keeps.Select(k => new StartWord { Ref = k, Kind = "keep_name" }).ToArray(), "agent")).Written;
                if (wrote.Written + kept == 0) break;
            }
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);
            Assert.True(view.CanBuild, view.BlockedBecause);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            return await sessions.Current.ReadAsync(m =>
            {
                var all = m.AllMeasures.Select(x => "[" + x.Table.Name + "].[" + x.Name + "] = " + x.Expression).ToArray();
                var broken = m.AllMeasures
                    .Select(x => (x, d: DaxValidator.Validate(m, x.Expression).Diagnostics.Where(g => g.Message != null && g.Message.StartsWith("Unknown", StringComparison.Ordinal)).Select(g => g.Message).ToArray()))
                    .Where(t => t.d.Length > 0)
                    .Select(t => "[" + t.x.Table.Name + "].[" + t.x.Name + "] = " + t.x.Expression + "  <- " + string.Join("; ", t.d)).ToArray();
                return (broken, all);
            });
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_Free_start_built_with_the_assistants_names_leaves_every_measure_pointing_at_objects_the_model_has()
        {
            var (broken, all) = await BuildAsync(pro: false);
            Assert.True(all.Length > 0, "the Free build made no measure, so there is nothing to check");
            Assert.True(broken.Length == 0, broken.Length + " of " + all.Length + " measures in the Free-built model point at names the model does not have:\n" + string.Join("\n", broken.Take(12)));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_same_start_on_Pro_keeps_every_measure_whole()
        {
            var (broken, all) = await BuildAsync(pro: true);
            Assert.True(all.Length > 0, "the Pro build made no measure");
            Assert.True(broken.Length == 0, broken.Length + " of " + all.Length + " measures in the Pro-built model point at names the model does not have:\n" + string.Join("\n", broken.Take(12)));
        }
    }
}
