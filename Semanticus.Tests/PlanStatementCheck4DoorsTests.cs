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
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Plan statement check 4, P1 across the whole matrix: a start built with the assistant's own names keeps every formula on objects the model has, on Free and Pro, started by the
    /// person's door (Studio) and by the assistant's door (the MCP tools), on the J8 source with its declared keys and on the same source with no keys declared. The date links of the
    /// keyless shape (P3) are checked in the built model too: the facts' date link is on, the date table is marked, the time measures are there. Neither grade is capped by the
    /// broken-formula rule (P2), because nothing is broken.
    /// </summary>
    [Collection("restore-root")]
    public sealed class PlanStatementCheck4DoorsTests : IDisposable
    {
        private const string Server = "wh-j8.datawarehouse.fabric.microsoft.com", Db = "ContosoConsulting";
        private readonly RestoreRootFixture _fixture;
        private readonly string _root;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public PlanStatementCheck4DoorsTests(RestoreRootFixture fixture)
        {
            _fixture = fixture;
            _root = Path.Combine(Path.GetTempPath(), "psc4d-" + Guid.NewGuid().ToString("N").Substring(0, 6));
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

        private static StartWord[] WordsFor(StartWordsPage page) => page.Items.Select(i => new StartWord
        {
            Ref = i.Ref, Kind = i.Kind,
            Text = i.Kind == "synonyms" ? "alias one, alias two"
                 : i.Kind == "display_name" ? (string.Equals(Humanise(i.Ref), (i.Ref ?? "").Substring((i.Ref ?? "").LastIndexOf('/') + 1), StringComparison.OrdinalIgnoreCase) ? Humanise(i.Ref) + " Value" : Humanise(i.Ref))
                 : i.Kind == "instructions" ? "This model answers questions about consulting work: hours, projects, clients and invoices."
                 : "What " + Humanise(i.Ref) + " means for the business, in plain words.",
        }).ToArray();

        private static FabricSchema Source(bool keyless)
        {
            var s = SqlDraftBuildTests.J8Schema();
            if (keyless) s.ForeignKeys = new FabricForeignKey[0];   // the primary keys stay: that is how a Fabric warehouse usually arrives
            return s;
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(true, true, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, true)]
        public async Task A_start_built_with_the_assistants_names_has_no_broken_formula_on_every_tier_door_and_key_shape(bool pro, bool mcpDoor, bool keyless)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, pro ? TestEntitlements.Pro : TestEntitlements.Free, Path.Combine(_root, "ws"));
            _owned.Add(engine); _owned.Add(sessions);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(Source(keyless));
            if (pro) engine.UseDecisionsForTest(new DecisionService(new StartPlanDecideTests.PolicyOracle()));
            var folder = Path.Combine(_root, "model");
            var view = mcpDoor
                ? await McpTools.StartModelFromSql(engine, Server, Db, folder, "Uplift")
                : await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = folder, Name = "Uplift" }, "human");
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
                var wrote = await McpTools.WriteStartWords(engine, WordsFor(page));   // the words are always the assistant's, on both doors
                var keeps = wrote.Refused.Where(r => r.Contains("keep_name")).Select(r => r.Substring(0, r.IndexOf(": ", StringComparison.Ordinal))).Distinct().ToArray();
                var kept = keeps.Length == 0 ? 0 : (await McpTools.WriteStartWords(engine, keeps.Select(k => new StartWord { Ref = k, Kind = "keep_name" }).ToArray())).Written;
                if (wrote.Written + kept == 0) break;
            }
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);   // the approval is always the person's
            Assert.True(view.CanBuild, view.BlockedBecause);
            var built = mcpDoor ? await McpTools.BuildStartPlan(engine, view.Digest) : await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);

            await sessions.Current.ReadAsync(m =>
            {
                var all = m.AllMeasures.ToList();
                Assert.NotEmpty(all);
                var broken = FormulaReferences.Scan(m);
                Assert.True(broken.Count == 0, broken.Count + " of " + all.Count + " measures point at objects the model does not have: "
                    + string.Join("; ", broken.Take(5).Select(b => b.Object.Name + " -> " + string.Join(", ", b.Missing))));
                // Nothing is broken, so neither grade is held down by the broken-formula rule.
                Assert.DoesNotContain(new ReadinessAnalyzer().Analyze(m).GatedBy, g => g.Contains("refers to an object"));
                Assert.DoesNotContain(BpaAnalyzer.Analyze(m, BpaRuleSet.ForModel(m)).Violations, v => v.RuleId == BpaGrading.BrokenFormulaRuleId);
                // The date links of the keyless shape (P3), in the model that was built.
                var date = m.Tables.Single(t => t.DataCategory == "Time");
                var active = m.Relationships.OfType<SingleColumnRelationship>().Where(r => r.IsActive && r.ToTable == date).Select(r => r.FromTable.Name).ToArray();
                Assert.Equal(2, active.Length);   // the timesheet fact and the invoice fact, one active date role each
                Assert.Contains(all, x => x.Expression.Contains("TOTALYTD", StringComparison.OrdinalIgnoreCase));
                return 0;
            });
        }
    }
}
