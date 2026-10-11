// The consent gate for facts worked out from a sample of rows (M11 spec-first start, fix round after integration check 2, defect D1).
//
// THE RULE. No fact derived from sample rows reaches Jev unless a stored grant that covered the sample (SampleFacts) is in force for this model. The sample sits in a
// sidecar beside the saved model (source-facts.json) and was read by several paths, so the rule is held at ONE gate (LocalEngine.SampleDisclosedAsync) that every
// builder of Jev facts asks: the decide rounds' scratch, and the Plan's own runs on the built model. One test per path below, each with the arm where the
// person DID allow it, so a gate that shuts everything cannot pass.
//
//   decide rounds and words ........ The_scratch_...
//   Plan runs after the build ...... The_plan_after_the_build_...
//   a Free start upgraded to Pro ... A_free_start_later_upgraded_...
//   an engine restart .............. After_an_engine_restart_...
//   the gate itself ................ The_gate_...

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class StartPlanSampleGateTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _origHome;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public StartPlanSampleGateTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sg-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            _origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "home"));
            ConnectionRegistry.RootOverride = Path.Combine(_root, "user");
            AgentPolicyStore.RootOverride = Path.Combine(_root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(_root, "policy");
            Directory.CreateDirectory(Path.Combine(_root, "policy"));
        }

        public void Dispose()
        {
            foreach (var o in _owned) { try { o.Dispose(); } catch { } }
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _origHome);
            ConnectionRegistry.RootOverride = _safeRoot; AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private const string Server = "wh-j8.datawarehouse.fabric.microsoft.com", Db = "ContosoConsulting";
        private static readonly Regex SampleShares = new Regex("\"(blank_share|blankShare|distinct_share)\":\\s*[0-9]", RegexOptions.Compiled);

        private static Func<string, string, string, int, Task<ResultSet>> J8Rows(FabricSchema schema)
        {
            var t = typeof(StartPlanEndToEndTests).GetNestedType("J8Source", BindingFlags.NonPublic);
            var src = Activator.CreateInstance(t, schema);
            return (Func<string, string, string, int, Task<ResultSet>>)Delegate.CreateDelegate(typeof(Func<string, string, string, int, Task<ResultSet>>), src, t.GetMethod("Execute"));
        }

        private (LocalEngine Engine, SessionManager Sessions, StartPlanDecideTests.PolicyOracle Oracle) NewEngine(bool pro, string workspace)
        {
            AgentPolicyStore.SetPreset("open", "human");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, pro ? TestEntitlements.Pro : TestEntitlements.Free, workspace);
            _owned.Add(engine); _owned.Add(sessions);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            engine.SourceSampleExecutorForTest = J8Rows(SqlDraftBuildTests.J8Schema());
            var oracle = new StartPlanDecideTests.PolicyOracle();
            engine.UseDecisionsForTest(new DecisionService(oracle));
            return (engine, sessions, oracle);
        }

        private static StartWord[] WordsFor(StartWordsPage page) => page.Items.Select(i => new StartWord
        {
            Ref = i.Ref, Kind = i.Kind,
            Text = i.Kind == "synonyms" ? "alias one, alias two"
                 : i.Kind == "display_name" ? NewName(i.Ref)
                 : i.Kind == "instructions" ? "This model answers questions about consulting work: hours, projects, clients and invoices."
                 : "What " + Humanise(i.Ref) + " means for the business, in plain words.",
        }).ToArray();

        private static string NewName(string r)
        {
            var raw = (r ?? "").Substring((r ?? "").LastIndexOf('/') + 1);
            var h = Humanise(r);
            return string.Equals(h, raw, StringComparison.OrdinalIgnoreCase) ? h + " Value" : h;
        }

        private static string Humanise(string r)
        {
            var name = (r ?? "").Substring((r ?? "").LastIndexOf('/') + 1);
            var colon = name.IndexOf(':'); if (colon >= 0) name = name.Substring(colon + 1);
            return string.Join(" ", name.Split('_', ' ').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1).ToLowerInvariant()));
        }

        private static async Task WordsAsync(LocalEngine engine)
        {
            for (var pass = 0; pass < 60; pass++)
            {
                var page = await engine.GetStartWordsAsync(40, 0);
                if (page.Items.Count == 0) break;
                await engine.WriteStartWordsAsync(WordsFor(page), "agent");
            }
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
        }

        /// <summary>How the person answered the source card.</summary>
        public enum Answer { Allow, NotNow, None }

        /// <summary>A start on the J8 schema to a BUILT, SAVED model. Pro takes the model's permission, then the source grant (Allow) or Not now; Free takes neither.</summary>
        private async Task<(LocalEngine Engine, SessionManager Sessions, StartPlanDecideTests.PolicyOracle Oracle, string ModelPath, string Workspace)> BuiltAsync(bool pro, Answer answer)
        {
            var ws = Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            var (engine, sessions, oracle) = NewEngine(pro, ws);
            var folder = Path.Combine(_root, "model-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = folder, Name = "Uplift" }, "human");
            await engine.StartSampleSettledForTest();
            view = await engine.GetStartPlanAsync();
            Assert.True(view.SampleFacts, "no sample was read, so this proves nothing");
            var fy = view.NeedsYou.SingleOrDefault(x => x.Kind == "fiscal_year");
            if (fy != null) view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = "fiscal_year", Value = fy.Recommended }, "human");
            if (pro && answer != Answer.None)
            {
                await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
                if (answer == Answer.Allow) view = await engine.GrantStartDisclosureAsync(view.Digest, "human");
                else view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = "grant", Value = "Not now" }, "human");
                await engine.StartDecideSettledForTest();
            }
            await WordsAsync(engine);
            view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var modelPath = engine.StartPlanDocForTest().Destination.ModelPath;
            await engine.SaveAsync(null, "tmdl", false, "human");
            return (engine, sessions, oracle, modelPath, ws);
        }

        /// <summary>The Plan's own Jev run on the open model, as a person's page read starts it.</summary>
        private static async Task RunPlanAsync(LocalEngine engine, StartPlanDecideTests.PolicyOracle oracle)
        {
            oracle.Phase = 2;
            await engine.GetModelPlanAsync();
            await engine.JevSettledForTest();
            await engine.GetModelPlanAsync();
        }

        private static int RequestsWithSample(StartPlanDecideTests.PolicyOracle o) => o.Snapshots.Count(x => SampleShares.IsMatch(x.SnapshotJson ?? ""));

        // ============================================================ the Plan's runs after the build

        [Theory]
        [InlineData(Answer.NotNow, false)]
        [InlineData(Answer.Allow, true)]
        public async Task The_plan_after_the_build_sends_the_samples_facts_only_under_a_grant_that_covered_it(Answer answer, bool expectSample)
        {
            var (engine, sessions, oracle, _, _) = await BuiltAsync(pro: true, answer);
            if (answer == Answer.NotNow) Assert.Equal(0, oracle.Asks);   // the decide rounds and the words rounds asked nothing either
            await RunPlanAsync(engine, oracle);
            Assert.True(oracle.Asks > 0, "Jev was never asked, so this proves nothing");
            Assert.NotNull(engine.LoadSourceFacts(sessions.Current));   // the sidecar is still there in both arms: it is the person's own
            if (expectSample) Assert.True(RequestsWithSample(oracle) > 0, "the person allowed the sample and no request carried it: the gate shut everything");
            else Assert.True(RequestsWithSample(oracle) == 0, RequestsWithSample(oracle) + " requests carried facts worked out from the sample the person said Not now to");
        }

        // ============================================================ the decide rounds and the words (the scratch they read)

        [Theory]
        [InlineData(Answer.NotNow, false)]
        [InlineData(Answer.Allow, true)]
        public async Task The_scratch_the_decide_rounds_and_the_words_read_lays_the_sample_over_the_model_only_under_a_covering_grant(Answer answer, bool expectSample)
        {
            var ws = Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            var (engine, sessions, oracle) = NewEngine(true, ws);
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = Path.Combine(_root, "model-scratch"), Name = "Uplift" }, "human");
            await engine.StartSampleSettledForTest();
            view = await engine.GetStartPlanAsync();
            Assert.True(view.SampleFacts, "no sample was read");
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            view = answer == Answer.Allow
                ? await engine.GrantStartDisclosureAsync(view.Digest, "human")
                : await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = "grant", Value = "Not now" }, "human");
            await engine.StartDecideSettledForTest();
            await WordsAsync(engine);   // the words rounds read a scratch of their own, from the same host
            var doc = engine.StartPlanDocForTest();
            var scratch = await new EngineStartScratchHost(engine).BuildAsync(doc, CancellationToken.None);
            try
            {
                var facts = scratch.Facts?.Invoke();
                var carriesSample = facts != null && facts.Data == Semanticus.Engine.Facts.ModelFactsData.SourceSample;
                Assert.Equal(expectSample, carriesSample);
                if (!expectSample) Assert.Equal(0, RequestsWithSample(oracle));
            }
            finally { scratch.Dispose(); }
        }

        // ============================================================ a Free start, later upgraded to Pro

        [Fact]
        public async Task A_free_start_later_upgraded_to_Pro_sends_no_sample_facts_until_the_person_allows_the_source()
        {
            var (free, freeSessions, _, modelPath, ws) = await BuiltAsync(pro: false, Answer.None);
            Assert.NotNull(free.LoadSourceFacts(freeSessions.Current));
            free.Dispose(); freeSessions.Dispose();

            var (pro, proSessions, oracle) = NewEngine(true, ws);   // the same person, the same folders, now with Pro
            await pro.OpenAsync(modelPath);
            Assert.NotNull(pro.LoadSourceFacts(proSessions.Current));
            await pro.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");   // the model's permission, never the source's
            await RunPlanAsync(pro, oracle);
            Assert.True(oracle.Asks > 0, "Jev was never asked, so this proves nothing");
            Assert.True(RequestsWithSample(oracle) == 0, RequestsWithSample(oracle) + " requests carried sample facts on a Free start nobody allowed the source for");
        }

        // ============================================================ an engine restart

        [Theory]
        [InlineData(Answer.NotNow, false)]
        [InlineData(Answer.Allow, true)]
        public async Task After_an_engine_restart_the_plan_still_honours_what_the_person_said_about_the_sample(Answer answer, bool expectSample)
        {
            var (first, firstSessions, _, modelPath, ws) = await BuiltAsync(pro: true, answer);
            first.Dispose(); firstSessions.Dispose();

            var (second, secondSessions, oracle) = NewEngine(true, ws);
            await second.OpenAsync(modelPath);
            await RunPlanAsync(second, oracle);
            Assert.True(oracle.Asks > 0, "Jev was never asked after the restart, so this proves nothing");
            if (expectSample) Assert.True(RequestsWithSample(oracle) > 0, "the grant did not survive the restart: the sample the person allowed was withheld");
            else Assert.True(RequestsWithSample(oracle) == 0, RequestsWithSample(oracle) + " requests carried sample facts after a restart, for a person who said Not now");
        }

        // ============================================================ the gate itself

        [Fact]
        public async Task The_gate_says_yes_only_for_the_plan_a_current_grant_named_the_sample_for()
        {
            var (engine, sessions, _, _, _) = await BuiltAsync(pro: true, Answer.Allow);
            var planId = engine.LoadSourceFacts(sessions.Current).PlanId;
            Assert.False(string.IsNullOrEmpty(planId), "the sidecar was not stamped with the plan it was read for");
            Assert.True(await engine.SampleDisclosedAsync(planId));
            Assert.False(await engine.SampleDisclosedAsync(null));
            Assert.False(await engine.SampleDisclosedAsync("sp-other000"), "another plan's id was covered by this plan's grant");

            // A grant that named the tables but not the sample covers nothing the sample says.
            var scope = await engine.DecisionConsentScopeAsync();
            var consent = engine.Decisions.ReadConsent(scope);
            var granted = StartDisclosureStore.Read(scope, planId, consent.Generation);
            StartDisclosureStore.Grant(scope, planId, new DisclosedScope(granted.SourceKey, granted.IncludedTables, false), consent.Generation, DateTime.UtcNow);
            Assert.False(await engine.SampleDisclosedAsync(planId), "a grant without the sample covered the sample");

            // A grant from an earlier generation of the model's permission is over.
            StartDisclosureStore.Grant(scope, planId, new DisclosedScope(granted.SourceKey, granted.IncludedTables, true), consent.Generation - 1, DateTime.UtcNow);
            Assert.False(await engine.SampleDisclosedAsync(planId), "a grant from before the permission was given again still counted");
            StartDisclosureStore.Grant(scope, planId, new DisclosedScope(granted.SourceKey, granted.IncludedTables, true), consent.Generation, DateTime.UtcNow);
            Assert.True(await engine.SampleDisclosedAsync(planId));

            // The model's own permission withdrawn: no sample goes anywhere.
            await engine.WithdrawConsentAsync("human");
            Assert.False(await engine.SampleDisclosedAsync(planId), "the permission was withdrawn and the sample was still covered");
        }
    }
}
