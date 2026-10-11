// Plan statement check 3 (verifier, 3 Oct, on m11/join f8c9bb52): a defect found driving the start plan with real Jev.
//
// THE DEFECT. WriteStartWordsGatedAsync (StartPlan/StartPlanWords.cs) stops the decide in flight BEFORE it applies the words, and starts it
// again only when the words changed the plan (outcome.Changed). A batch where every word is refused (a name clash, formula text, a name the
// object already has) changes nothing, so a decide that was running is cancelled and never started again. The loop had already written
// Decide.State = deciding at the start of its round, so the plan says "Jev is still working on the plan. It will be ready to approve when it
// stops." for ever: Approve and Approve and build stay greyed, and nothing the assistant can write that is refused again ever wakes it.
// Measured on the real app (real Jev, the J8 source): two Pro runs ended exactly so, 8 and 15 minutes with no thread in Semanticus code and no
// connection to Jev; the stored plan read {"State":"deciding","Round":1,"Progress":[]}.
//
// The test holds the decide on a slow answer, so it is surely running when the refused batch lands, then lets it go.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
    public sealed class VerifyPlanStatementCheck3Tests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _origHome;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public VerifyPlanStatementCheck3Tests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "psc3-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            Directory.CreateDirectory(Path.Combine(_root, "policy"));
            _origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "home"));
            ConnectionRegistry.RootOverride = Path.Combine(_root, "user");
            AgentPolicyStore.RootOverride = Path.Combine(_root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(_root, "policy");
        }

        public void Dispose()
        {
            foreach (var o in _owned) { try { o.Dispose(); } catch { } }
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _origHome);
            ConnectionRegistry.RootOverride = _safeRoot; AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static string NameIn(string objRef)
        {
            var r = objRef ?? "";
            var at = Math.Max(r.LastIndexOf('/'), r.IndexOf(':'));
            return at >= 0 ? r.Substring(at + 1) : r;
        }

        [Fact]
        public async Task A_words_batch_where_every_word_is_refused_never_leaves_the_plan_deciding_with_no_decide_running()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, Path.Combine(_root, "ws"));
            _owned.Add(engine); _owned.Add(sessions);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            var policy = new StartPlanDecideTests.PolicyOracle();
            engine.UseDecisionsForTest(new DecisionService(policy));

            var view = await engine.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "wh-j8.datawarehouse.fabric.microsoft.com", Database = "ContosoConsulting", Folder = Path.Combine(_root, "model"), Name = "Uplift",
            }, "human");
            Assert.True(view.Exists);
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            view = await engine.GrantStartDisclosureAsync((await engine.GetStartPlanAsync()).Digest, "human");
            await engine.StartDecideSettledForTest();
            view = await engine.GetStartPlanAsync();
            Assert.True(view.Decide.State == StartDecideState.Stable, "the first decide did not settle: " + view.Decide.State + " " + view.Decide.Reason);

            // From here every answer Jev gives takes a while, so the decide the next words start is still running when the refused batch lands.
            var hold = new SemaphoreSlim(0);
            policy.BeforeAnswer = async (req, ct) => await hold.WaitAsync(TimeSpan.FromSeconds(20), ct);

            // 1. a batch that lands: every business name the queue asks for, as a new name. This starts the decide again (S10).
            var page = await engine.GetStartWordsAsync(40, 0);
            var names = page.Items.Where(i => i.Kind == "display_name").ToList();
            Assert.True(names.Count >= 2, "the words queue asks for no business names: " + string.Join(", ", page.Items.Select(i => i.Kind).Distinct()));
            var landed = await engine.WriteStartWordsAsync(names.Select((i, k) => new StartWord { Ref = i.Ref, Kind = "display_name", Text = "Business name " + k }).ToArray(), "agent");
            Assert.True(landed.Written > 0, "the first batch landed nothing: " + string.Join("; ", landed.Refused.Take(3)));
            await Task.Delay(300);
            Assert.True(engine.StartPlanDocForTest().Decide.State == StartDecideState.Deciding, "precondition: the decide the words started is running (" + engine.StartPlanDocForTest().Decide.State + ")");

            // 2. a batch where every word is refused: one object given the name another object already has.
            var clash = await engine.WriteStartWordsAsync(new[] { new StartWord { Ref = names[1].Ref, Kind = "display_name", Text = "Business name 0" } }, "agent");
            Assert.True(clash.Written == 0, "precondition: the clash was written: " + clash.Written);

            // Jev answers again; whatever runs now runs to its end.
            hold.Release(1000);
            policy.BeforeAnswer = null;
            await engine.StartDecideSettledForTest();
            await Task.Delay(500);
            await engine.StartDecideSettledForTest();
            view = await engine.GetStartPlanAsync();

            // The plan must not say Jev is working when no decide is running: it is stable (or incomplete, with its reason), and the person can approve it.
            Assert.True(view.Decide.State != StartDecideState.Deciding,
                "the plan is left 'deciding' with no decide running, so Approve stays greyed for ever: " + view.BlockedBecause);
        }
    }
}
