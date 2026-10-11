// The fixes for the third Plan statement check (m11/psc3). Each test here failed first on m11/join f8c9bb52.
//
// ITEM 1, the class rule. The start plan says "deciding" only while a decide is running. A verb that holds the plan's gate stops the loop first and
// may then change nothing (every word refused, an edit that moves nothing, a build the checks refuse, an error), and the stored round stayed at
// "deciding" with no loop: Approve greyed for ever. The test holds a decide on a slow answer, runs each verb, and asks the engine the moment the verb
// returns: deciding means a decide task is alive.

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
    public sealed class PlanStatementCheck3FixTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _origHome;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public PlanStatementCheck3FixTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "pc3-" + Guid.NewGuid().ToString("N").Substring(0, 6));
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

        private sealed class Rig
        {
            public LocalEngine Engine;
            public StartPlanDecideTests.PolicyOracle Policy;
            public SemaphoreSlim Hold;
            public StartPlanView View;
            /// <summary>The business names the queue asked for before the first batch of words landed (the queue holds none after it).</summary>
            public List<StartWordItem> Names;
        }

        /// <summary>A Pro engine with a settled plan on the J8 schema, then every answer Jev gives held, then one batch of words that lands so a decide is running.</summary>
        private async Task<Rig> DecidingAsync()
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

            var hold = new SemaphoreSlim(0);
            policy.BeforeAnswer = async (req, ct) => await hold.WaitAsync(TimeSpan.FromSeconds(20), ct);
            var page = await engine.GetStartWordsAsync(40, 0);
            var names = page.Items.Where(i => i.Kind == "display_name").ToList();
            Assert.True(names.Count >= 2, "the words queue asks for no business names: " + string.Join(", ", page.Items.Select(i => i.Kind).Distinct()));
            var landed = await engine.WriteStartWordsAsync(names.Select((i, k) => new StartWord { Ref = i.Ref, Kind = "display_name", Text = "Business name " + k }).ToArray(), "agent");
            Assert.True(landed.Written > 0, "the first batch landed nothing: " + string.Join("; ", landed.Refused.Take(3)));
            // Wait for the decide to start rather than for 300 ms: under a loaded full run it started later than that.
            for (var waited = 0; waited < 100 && !(engine.StartPlanDocForTest().Decide.State == StartDecideState.Deciding && engine.StartDecideAliveForTest); waited++)
                await Task.Delay(100);
            Assert.True(engine.StartPlanDocForTest().Decide.State == StartDecideState.Deciding && engine.StartDecideAliveForTest,
                "precondition: the decide the words started is running (" + engine.StartPlanDocForTest().Decide.State + ")");
            return new Rig { Engine = engine, Policy = policy, Hold = hold, View = await engine.GetStartPlanAsync(), Names = names };
        }

        private static void AssertDecidingOnlyIfAlive(Rig r, string after)
        {
            var state = r.Engine.StartPlanDocForTest().Decide.State;
            var alive = r.Engine.StartDecideAliveForTest;
            Assert.True(state != StartDecideState.Deciding || alive, "after " + after + " the plan says 'deciding' and no decide is running");
        }

        private static async Task LetGoAsync(Rig r)
        {
            r.Hold.Release(1000);
            r.Policy.BeforeAnswer = null;
            await r.Engine.StartDecideSettledForTest();
        }

        [Theory]
        [InlineData("words every word refused")]
        [InlineData("words that change nothing")]
        [InlineData("edit that moves nothing")]
        [InlineData("edit that is refused")]
        [InlineData("build that is refused")]
        [InlineData("approve")]
        [InlineData("consent given again")]
        public async Task After_any_start_verb_returns_the_plan_is_deciding_only_if_a_decide_is_running(string verb)
        {
            var r = await DecidingAsync();
            var e = r.Engine;
            switch (verb)
            {
                case "words every word refused":
                {
                    // One object given the name another object already has (the first batch gave the first object "Business name 0").
                    var res = await e.WriteStartWordsAsync(new[] { new StartWord { Ref = r.Names[1].Ref, Kind = "display_name", Text = "Business name 0" } }, "agent");
                    Assert.Equal(0, res.Written);
                    break;
                }
                case "words that change nothing":
                    await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "column:Nowhere/Nothing", Kind = "description", Text = "There is no such column." } }, "agent");
                    break;
                case "edit that moves nothing":
                {
                    // The year end the plan already has, said again: Ok, and nothing moves.
                    var fy = e.StartPlanDocForTest().Manifest.DateTable?.FiscalYearEnd;
                    var edited = await e.EditStartPlanAsync(new StartPlanEdit { Op = "set_fiscal_year_end", Value = fy ?? "calendar" }, "human");
                    Assert.True(edited.Exists);
                    break;
                }
                case "edit that is refused":
                    await Assert.ThrowsAsync<InvalidOperationException>(() => e.EditStartPlanAsync(new StartPlanEdit { Op = "not_an_op" }, "human"));
                    break;
                case "build that is refused":
                {
                    var res = await e.BuildStartPlanAsync((await e.GetStartPlanAsync()).Digest, "human");
                    Assert.False(res.Built);
                    break;
                }
                case "approve":
                    try { await e.ApproveStartPlanAsync((await e.GetStartPlanAsync()).Digest); } catch (InvalidOperationException) { }
                    break;
                case "consent given again":
                    try { await e.GrantStartDisclosureAsync((await e.GetStartPlanAsync()).Digest, "human"); } catch (InvalidOperationException) { }
                    break;
            }
            AssertDecidingOnlyIfAlive(r, verb);
            await LetGoAsync(r);
            // And it ends: whatever was restarted runs to its end, and the person can carry on.
            var view = await e.GetStartPlanAsync();
            Assert.True(view.Decide.State != StartDecideState.Deciding, "the decide never ended: " + view.Decide.State);
        }

        // ---------------------------------------------------------------- items 3 and 4: a Free plan (no Jev), words, build

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

        private static IEnumerable<StartWordItem> NameItems(StartWordsPage page) => page.Items.Where(i => i.Kind == "display_name");

        // ITEM 3. The After-the-build card said "0 findings stay open" beside "Best practice D": it counted the plan's own Best practice lines. The count now
        // comes from the preview's own scan, and it is the count of open errors and warnings the built model's scan gives.
        [Fact]
        public async Task The_preview_says_how_many_errors_and_warnings_stay_open_and_the_built_models_scan_agrees()
        {
            var (engine, sessions) = await FreePlanAsync();
            await engine.StartPreviewSettledForTest();
            var view = await engine.GetStartPlanAsync();
            Assert.NotNull(view.Grades.AfterBuildBestPractice);
            Assert.True(view.Grades.AfterBuildBestPracticeOpen.HasValue, "the plan says no count of what stays open");
            var shown = view.Grades.AfterBuildBestPracticeOpen.Value;
            await ApproveAndBuildAsync(engine);
            var scan = await engine.BpaScanAsync();
            var open = scan.Violations.Count(v => !v.IsAccepted() && v.Severity >= 2);
            Assert.Equal(open, shown);
            // The letter and the count agree: A only when nothing is open.
            Assert.Equal(scan.BestPractice == "A", shown == 0);
        }

        // ITEM 4. The queue kept asking for a name the object has, the check refused the name it has ("It already has that name"), and there was no way to keep it.
        [Theory]
        [InlineData("agent")]
        [InlineData("human")]
        public async Task A_name_can_be_kept_on_either_door_and_the_question_closes_and_the_scan_does_not_flag_it_after_the_build(string door)
        {
            var (engine, sessions) = await FreePlanAsync();
            var page = await engine.GetStartWordsAsync(100, 0);
            var names = NameItems(page).ToList();
            Assert.True(names.Count >= 2, "the words queue asks for no business names");
            var keep = names[0];
            var openBefore = (await engine.GetStartPlanAsync()).Words.Open;

            // The name it has is refused as a "new" name (that is right), and it says what to send instead.
            var same = await engine.WriteStartWordsAsync(new[] { new StartWord { Ref = keep.Ref, Kind = "display_name", Text = NameIn(keep.Ref) } }, door);
            Assert.Equal(0, same.Written);
            Assert.Contains("keep_name", string.Join(" ", same.Refused));

            var res = await engine.WriteStartWordsAsync(new[] { new StartWord { Ref = keep.Ref, Kind = "keep_name" } }, door);
            Assert.True(res.Written == 1, "the keep was not written: " + string.Join("; ", res.Refused));
            var after = await engine.GetStartWordsAsync(100, 0);
            Assert.DoesNotContain(NameItems(after), i => i.Ref == keep.Ref);
            Assert.Equal(openBefore - 1, (await engine.GetStartPlanAsync()).Words.Open);
            var line = engine.StartPlanDocForTest().Lines.Single(l => l.Item.Kind == "keep_name");
            Assert.Equal(door == "human" ? StartDecidedBy.Person : StartDecidedBy.Assistant, line.DecidedBy);

            // The build carries it: the name is the same, and the naming finding it raised is accepted, so the scan does not ask again.
            await ApproveAndBuildAsync(engine);
            var findings = await sessions.Current.ReadAsync(m => new ReadinessAnalyzer().Analyze(m).Findings.Where(f => f.RuleId.StartsWith("NAME-") && f.ObjectRef == keep.Ref).ToList());
            Assert.True(findings.Count > 0, "the name the queue asked about raised no naming finding on the built model, so the test proves nothing");
            Assert.All(findings, f => Assert.True(f.Waived, f.RuleId + " still flags the kept name " + keep.Ref + " after the build"));
        }

        [Fact]
        public async Task A_business_name_the_naming_rules_would_flag_is_refused_and_one_it_accepts_is_not_flagged_after_the_build()
        {
            var (engine, sessions) = await FreePlanAsync();
            var names = NameItems(await engine.GetStartWordsAsync(100, 0)).ToList();
            Assert.True(names.Count >= 3);
            // A name that ends in Key reads as a code to NAME-COLUMN: the check says so now, instead of taking it and leaving the question open for ever.
            var cryptic = await engine.WriteStartWordsAsync(new[] { new StartWord { Ref = names[0].Ref, Kind = "display_name", Text = "Onboarded Date Key" } }, "agent");
            Assert.Equal(0, cryptic.Written);
            Assert.Contains("code or an abbreviation", string.Join(" ", cryptic.Refused));
            // A plain name lands, and after the build the scan does not flag it.
            var plain = await engine.WriteStartWordsAsync(new[] { new StartWord { Ref = names[1].Ref, Kind = "display_name", Text = "Date Onboarded" } }, "agent");
            Assert.True(plain.Written == 1, string.Join("; ", plain.Refused));
            await ApproveAndBuildAsync(engine);
            var newRef = names[1].Ref.Substring(0, names[1].Ref.LastIndexOf('/') + 1) + "Date Onboarded";
            var flagged = await sessions.Current.ReadAsync(m => new ReadinessAnalyzer().Analyze(m).Findings.Where(f => f.RuleId.StartsWith("NAME-") && f.ObjectRef == newRef && !f.Waived).ToList());
            Assert.Empty(flagged);
        }

        // The check and the rules are one: over a model of names, a name the check would refuse is exactly a name one of the four naming rules flags.
        [Fact]
        public async Task The_words_check_and_the_naming_rules_flag_the_same_names_over_the_real_scan()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Free, Path.Combine(_root, "ws2"));
            _owned.Add(engine); _owned.Add(sessions);
            await engine.CreateModelAsync("PscNames", 1604);
            var sales = await engine.CreateTableAsync("Sales", "human");
            foreach (var n in new[] { "Onboarded Date Key", "Customer ID", "Code", "Order Date", "Average BILL RATE", "Mean Bill Rate", "WORK_DT_KEY", "Product Code", "CurrencyName", "Revenue", "Id", "Region Cd", "Status", "Net Total YoY", "Employee Number" })
                await engine.CreateColumnAsync(sales, n, "String", n, "human");
            foreach (var n in new[] { "Total HRS MTD", "Total Hours MTD", "Margin", "Sales QoQ", "totalCost" })
                await engine.CreateMeasureAsync(sales, n, "1", "human");
            await engine.CreateTableAsync("dim_Customer", "human");
            await engine.CreateTableAsync("Customers", "human");

            var (objs, scanned) = await sessions.Current.ReadAsync(m =>
            {
                var rules = ReadinessRuleSet.Default().Where(r => new[] { "NAME-COLUMN", "NAME-COLUMN-ID", "NAME-MEASURE", "NAME-TABLE" }.Contains(r.Id)).ToList();
                var byRef = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var r in rules) foreach (var v in r.Evaluate(m).Violations) byRef[v.ObjectRef] = r.Id;
                var all = new List<(string Ref, string Name, string Rule)>();
                foreach (var t in m.Tables) all.Add(("table:" + t.Name, t.Name, ReadinessRuleSet.NamingRuleFor(t, t.Name)));
                foreach (var c in m.AllColumns) all.Add(("column:" + c.Table.Name + "/" + c.Name, c.Name, ReadinessRuleSet.NamingRuleFor(c, c.Name)));
                foreach (var me in m.AllMeasures) all.Add(("measure:" + me.Table.Name + "/" + me.Name, me.Name, ReadinessRuleSet.NamingRuleFor(me, me.Name)));
                return (all, byRef);
            });
            Assert.True(objs.Count(o => o.Rule != null) >= 6, "the model of names raised too few findings to prove anything: " + string.Join(", ", objs.Where(o => o.Rule != null).Select(o => o.Name)));
            foreach (var o in objs)
                Assert.True(scanned.TryGetValue(o.Ref, out var rule) ? rule == o.Rule : o.Rule == null,
                    o.Ref + ": the scan says " + (scanned.TryGetValue(o.Ref, out var r2) ? r2 : "nothing") + ", the words check says " + (o.Rule ?? "nothing"));
        }

        // A change of case only is a change ("Average BILL RATE" to "Average Bill Rate" was refused as "It already has that name").
        [Fact]
        public async Task A_change_of_case_only_is_a_rename()
        {
            var (engine, sessions) = await FreePlanAsync();
            var names = NameItems(await engine.GetStartWordsAsync(100, 0)).ToList();
            var target = names[0].Ref;
            var first = await engine.WriteStartWordsAsync(new[] { new StartWord { Ref = target, Kind = "display_name", Text = "Date Onboarded" } }, "agent");
            Assert.True(first.Written == 1, string.Join("; ", first.Refused));
            var second = await engine.WriteStartWordsAsync(new[] { new StartWord { Ref = target, Kind = "display_name", Text = "Date onboarded" } }, "agent");
            Assert.True(second.Written == 1, "a change of case only was refused: " + string.Join("; ", second.Refused));
            Assert.Equal("Date onboarded", engine.StartPlanDocForTest().Lines.Single(l => l.Item.Kind == "rename" && l.Item.ObjectRef == target).Item.After);
        }

        // A new name settles the question the other way: the assistant's keep goes; the person's keep stands.
        [Fact]
        public async Task A_new_name_replaces_the_assistants_keep_and_never_the_persons()
        {
            var (engine, sessions) = await FreePlanAsync();
            var names = NameItems(await engine.GetStartWordsAsync(100, 0)).ToList();
            await engine.WriteStartWordsAsync(new[] { new StartWord { Ref = names[0].Ref, Kind = "keep_name" } }, "agent");
            await engine.WriteStartWordsAsync(new[] { new StartWord { Ref = names[1].Ref, Kind = "keep_name" } }, "human");
            Assert.Equal(2, engine.StartPlanDocForTest().Lines.Count(l => l.Item.Kind == "keep_name"));

            var first = await engine.WriteStartWordsAsync(new[] { new StartWord { Ref = names[0].Ref, Kind = "display_name", Text = "Date Onboarded" } }, "agent");
            Assert.True(first.Written == 1, string.Join("; ", first.Refused));
            Assert.DoesNotContain(engine.StartPlanDocForTest().Lines, l => l.Item.Kind == "keep_name" && l.Item.ObjectRef == names[0].Ref);

            var second = await engine.WriteStartWordsAsync(new[] { new StartWord { Ref = names[1].Ref, Kind = "display_name", Text = "Date Booked" } }, "agent");
            Assert.Equal(0, second.Written);
            Assert.Contains("kept this name yourself", string.Join(" ", second.Refused));
            // And the assistant cannot take a person's keep back.
            var back = await engine.WriteStartWordsAsync(new[] { new StartWord { Ref = names[1].Ref, Kind = "keep_name" } }, "agent");
            Assert.Equal(0, back.Written);
        }
    }
}
