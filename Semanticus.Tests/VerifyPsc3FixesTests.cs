// Verifier, 4 Oct, on m11/psc3 b1d29e8f: attacks on the third Plan statement check's fixes, by class.
//
// 1. The class "the plan says deciding only while a decide runs" across a path that is not a verb: an engine restart while the decide runs, with
//    the plan's model opened again BEFORE anything reads the plan. That is the order the extension takes: connectEngine reopens the remembered model
//    (extension.ts reopenLastModel) and only then tells the Studio it is reconnected, so the Studio's first read comes after the open. The stored plan
//    is not parked (only a swap on a live engine parks it), so the read does not unpark it, nothing kicks the loop, and the plan says "Jev is still
//    working" for ever: Approve greyed, as in defect 1. LEFT FAILING: it fails on b1d29e8f (and on f8c9bb52, where it began).
// 2. A case-only business name (item 4) is accepted by the words check; the build carries it too (passes).
// 3. Every business name the queue asks for, kept: the queue reaches 0, and after the build the scan and the Plan's own words queue agree; one undo
//    takes the acceptances back (passes).

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
    public sealed class VerifyPsc3FixesTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _origHome;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public VerifyPsc3FixesTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "vp3-" + Guid.NewGuid().ToString("N").Substring(0, 6));
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

        private (LocalEngine Engine, SessionManager Sessions, StartPlanDecideTests.PolicyOracle Policy) ProEngine()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, Path.Combine(_root, "ws"));
            _owned.Add(engine); _owned.Add(sessions);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            var policy = new StartPlanDecideTests.PolicyOracle();
            engine.UseDecisionsForTest(new DecisionService(policy));
            return (engine, sessions, policy);
        }

        private async Task<(LocalEngine Engine, SessionManager Sessions)> FreePlanAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Free, Path.Combine(_root, "ws"));
            _owned.Add(engine); _owned.Add(sessions);
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "wh-j8.datawarehouse.fabric.microsoft.com", Database = "ContosoConsulting", Folder = Path.Combine(_root, "model"), Name = "Uplift",
            }, "human");
            Assert.True(view.Exists);
            return (engine, sessions);
        }

        private static async Task<StartPlanBuildResult> ApproveAndBuildAsync(LocalEngine engine)
        {
            await engine.StartPreviewSettledForTest();
            var view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);
            Assert.True(view.CanBuild, view.BlockedBecause);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            return built;
        }

        private static string NameIn(string objRef)
        {
            var r = objRef ?? "";
            var at = Math.Max(r.LastIndexOf('/'), r.IndexOf(':'));
            return at >= 0 ? r.Substring(at + 1) : r;
        }

        // ---------------------------------------------------------------- 1. a restart while the decide runs

        [Fact]
        public async Task An_engine_restart_while_the_decide_runs_never_leaves_the_plan_deciding_with_no_decide_running()
        {
            var (first, firstSessions, policy) = ProEngine();
            var view = await first.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "wh-j8.datawarehouse.fabric.microsoft.com", Database = "ContosoConsulting", Folder = Path.Combine(_root, "model"), Name = "Uplift",
            }, "human");
            var modelPath = view.Destination.ModelPath;
            await first.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            var hold = new SemaphoreSlim(0);
            policy.BeforeAnswer = async (req, ct) => await hold.WaitAsync(TimeSpan.FromSeconds(30), ct);
            await first.GrantStartDisclosureAsync((await first.GetStartPlanAsync()).Digest, "human");
            await Task.Delay(500);
            Assert.True(first.StartPlanDocForTest().Decide.State == StartDecideState.Deciding && first.StartDecideAliveForTest, "precondition: the decide is running");

            // The engine goes away mid-decide (Restart Engine, a crash, VS Code closed).
            first.Dispose(); firstSessions.Dispose();
            hold.Release(1000);

            // A new engine, the same state; the plan's model is opened again before anything reads the plan.
            var (second, _, policy2) = ProEngine();
            await second.OpenAsync(modelPath);
            var back = await second.GetStartPlanAsync();
            await Task.Delay(1000);
            var state = second.StartPlanDocForTest().Decide.State;
            Assert.True(state != StartDecideState.Deciding || second.StartDecideAliveForTest,
                "after a restart the plan says 'deciding' and no decide is running, so Approve stays greyed: " + back.BlockedBecause);
        }

        // ---------------------------------------------------------------- 2. a case-only name, built

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_case_only_business_name_is_built_as_written()
        {
            var (engine, sessions) = await FreePlanAsync();
            // Every page of the queue, not the first hundred: which names fall on the first page follows the order of the lifts, which moves whenever a rule joins the scan
            // (plan statement check 4 added DAX-REFERENCE), and the test is about a case-only name, not about the order.
            var names = new List<StartWordItem>();
            for (var off = 0; off < 4000; off += 100)
            {
                var page = await engine.GetStartWordsAsync(100, off);
                names.AddRange(page.Items.Where(i => i.Kind == "display_name"));
                if (page.Items.Count == 0 || off + 100 >= page.Total) break;
            }
            Assert.NotEmpty(names);
            // Pick a name item whose case-only change the naming rules accept (Title Case of its own words).
            StartWordItem pick = null; string text = null;
            var titled = await sessions.Current.ReadAsync(m => names.Select(n =>
            {
                var cur = NameIn(n.Ref);
                var t = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(cur.ToLowerInvariant().Replace('_', ' '));
                return (n, cur, t);
            }).ToList());
            var res = new StartWordsResult();
            foreach (var (n, cur, t) in titled)
            {
                if (string.Equals(cur, t, StringComparison.Ordinal) || !string.Equals(cur, t, StringComparison.OrdinalIgnoreCase)) continue;
                res = await engine.WriteStartWordsAsync(new[] { new StartWord { Ref = n.Ref, Kind = "display_name", Text = t } }, "agent");
                if (res.Written == 1) { pick = n; text = t; break; }
            }
            Assert.True(pick != null, "no name item takes a case-only change on this schema: " + string.Join(", ", titled.Select(x => x.cur)));
            await ApproveAndBuildAsync(engine);
            var table = pick.Ref.Substring(pick.Ref.IndexOf(':') + 1).Split('/')[0];
            var nowNames = await sessions.Current.ReadAsync(m => m.AllColumns.Select(c => c.Name).Concat(m.AllMeasures.Select(x => x.Name)).Concat(m.Tables.Select(x => x.Name)).ToList());
            Assert.Contains(text, nowNames);
            Assert.DoesNotContain(NameIn(pick.Ref), nowNames);
        }

        // ---------------------------------------------------------------- 3. keep every name: queue 0, and the scan agrees after the build

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Keeping_every_name_the_queue_asks_for_closes_the_queue_and_the_built_model_agrees()
        {
            var (engine, sessions) = await FreePlanAsync();
            // The queue is paged (40 at most): read every page, then keep every name it asks for, as an assistant would.
            async Task<List<string>> NamesAsked()
            {
                var all = new List<string>();
                for (var off = 0; off < 4000; off += 40)
                {
                    var p = await engine.GetStartWordsAsync(40, off);
                    all.AddRange(p.Items.Where(i => i.Kind == "display_name").Select(i => i.Ref));
                    if (p.Items.Count == 0 || off + 40 >= p.Total) break;
                }
                return all;
            }
            var asked = await NamesAsked();
            Assert.True(asked.Count >= 2, "the words queue asks for too few names to prove anything");
            foreach (var chunk in asked.Chunk(40))
            {
                var res = await engine.WriteStartWordsAsync(chunk.Select(r => new StartWord { Ref = r, Kind = "keep_name", Text = "It is the name the business uses." }).ToArray(), "agent");
                Assert.True(res.Written == chunk.Length, "not every keep landed: " + string.Join("; ", res.Refused));
            }
            Assert.Empty(await NamesAsked());
            Assert.Equal(0, (await engine.GetStartPlanAsync()).Words.Open - (await engine.GetStartWordsAsync(40, 0)).Total);

            // The lines say who kept them, so the person reads them before approving.
            var view = await engine.GetStartPlanAsync();
            var words = view.Sections.Single(s => s.Id == StartSectionId.Words).Groups.SelectMany(g => g.Lines).Where(l => l.Item.Kind == "keep_name").ToList();
            Assert.Equal(asked.Count, words.Count);
            Assert.All(words, l => Assert.StartsWith("Your assistant kept this name.", l.Why));

            await ApproveAndBuildAsync(engine);
            var open = await sessions.Current.ReadAsync(m => new ReadinessAnalyzer().Analyze(m).Findings
                .Where(f => f.RuleId.StartsWith("NAME-", StringComparison.Ordinal) && !f.Waived && asked.Contains(f.ObjectRef)).Select(f => f.RuleId + " " + f.ObjectRef).ToList());
            Assert.Empty(open);
            var queue = await engine.GetWordsQueueAsync(200, 0);
            Assert.False(queue.Kinds.TryGetValue("name", out var n) && n > 0, "the Plan's words queue still asks for " + n + " names after the build ("
                + asked.Count + " kept), for example: " + string.Join("; ", queue.Items.Where(i => i.Kind == "name").Take(6).Select(i => i.RuleId + " " + i.ObjectRef + (asked.Contains(i.ObjectRef) ? " (kept)" : " (never asked)")))
                + " | total " + queue.Total + ", kinds " + string.Join(",", queue.Kinds.Select(k => k.Key + "=" + k.Value)) + ", page " + string.Join(",", queue.Items.Select(i => i.Kind + ":" + i.RuleId + ":" + i.ObjectRef).Take(8)));
            // One undo takes the build back, the acceptances with it.
            await engine.UndoAsync("human");
            var waivers = await sessions.Current.ReadAsync(m => WaiverStore.Load(m).Count);
            Assert.Equal(0, waivers);
        }
    }
}
