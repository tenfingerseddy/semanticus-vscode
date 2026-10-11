// A person's keep, end to end on the real engine and BOTH doors (M11 fix round after integration check 2, defect D2). The table in StartPlanKeepGateTests proves the
// build's line step for every accept path; this proves the whole journey on the J8 schema: Jev is unsure of a format, the person answers keep, approves, and the
// build is pressed on the person's door or on the assistant's (K3: either door runs the build of the snapshot the person approved).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class StartPlanKeepDoorTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _origHome;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public StartPlanKeepDoorTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "kd-" + Guid.NewGuid().ToString("N").Substring(0, 6));
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

        private static Func<string, string, string, int, Task<ResultSet>> J8Rows(FabricSchema schema)
        {
            var t = typeof(StartPlanEndToEndTests).GetNestedType("J8Source", BindingFlags.NonPublic);
            var src = Activator.CreateInstance(t, schema);
            return (Func<string, string, string, int, Task<ResultSet>>)Delegate.CreateDelegate(typeof(Func<string, string, string, int, Task<ResultSet>>), src, t.GetMethod("Execute"));
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

        private async Task<(LocalEngine Engine, SessionManager Sessions, StartPlanView View, StartNeed Need)> UnsureFormatAsync()
        {
            AgentPolicyStore.SetPreset("open", "human");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N").Substring(0, 5)));
            _owned.Add(engine); _owned.Add(sessions);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            engine.SourceSampleExecutorForTest = J8Rows(SqlDraftBuildTests.J8Schema());
            engine.UseDecisionsForTest(new DecisionService(new StartPlanDecideTests.PolicyOracle()));
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = Path.Combine(_root, "model-" + Guid.NewGuid().ToString("N").Substring(0, 5)), Name = "Uplift" }, "human");
            await engine.StartSampleSettledForTest();
            view = await engine.GetStartPlanAsync();
            var fy = view.NeedsYou.SingleOrDefault(x => x.Kind == "fiscal_year");
            if (fy != null) view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = "fiscal_year", Value = fy.Recommended }, "human");
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            view = await engine.GrantStartDisclosureAsync(view.Digest, "human");
            await engine.StartDecideSettledForTest();
            for (var pass = 0; pass < 60; pass++)
            {
                var page = await engine.GetStartWordsAsync(40, 0);
                if (page.Items.Count == 0) break;
                await engine.WriteStartWordsAsync(WordsFor(page), "agent");
            }
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            view = await engine.GetStartPlanAsync();
            var need = view.NeedsYou.FirstOrDefault(n => n.Kind == "unsure_line" && n.Text.StartsWith("Format as"));
            Assert.True(need != null, "no unsure format line to answer: " + string.Join(" | ", view.NeedsYou.Select(n => n.Kind)));
            return (engine, sessions, view, need);
        }

        private static async Task<string> FormatOfAsync(SessionManager sessions, string objectRef) =>
            await sessions.Current.ReadAsync(m =>
                m.AllMeasures.Where(x => string.Equals(Semanticus.Engine.ObjectRefs.For(x), objectRef, StringComparison.OrdinalIgnoreCase)).Select(x => x.FormatString)
                 .Concat(m.AllColumns.Where(c => string.Equals(Semanticus.Engine.ObjectRefs.For(c), objectRef, StringComparison.OrdinalIgnoreCase)).Select(c => c.FormatString))
                 .FirstOrDefault() ?? "(object not found)");

        private static StartLine LineOf(LocalEngine engine, StartNeed need)
        {
            var key = need.Id.Substring(need.Id.IndexOf(':') + 1);
            return engine.StartPlanDocForTest().Lines.Single(l => string.Equals(StartPlanDecide.LineKey(l), key, StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task A_person_keeps_an_unsure_format_approves_and_either_door_builds_it(bool assistantBuilds)
        {
            var (engine, sessions, view, need) = await UnsureFormatAsync();
            view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = need.Id, Value = "keep" }, "human");
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            var line = LineOf(engine, need);
            Assert.True(line.Kept && line.DecidedBy == StartDecidedBy.Person);
            Assert.True(line.Held == null || !line.Held.Contains("nobody ticked", StringComparison.OrdinalIgnoreCase), "the plan says nobody ticked a line the person kept: " + line.Held);
            view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);   // the person's door: an assistant cannot approve
            var built = assistantBuilds ? await McpTools.BuildStartPlan(engine, view.Digest) : await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            Assert.Equal(line.Item.After, await FormatOfAsync(sessions, line.Item.ObjectRef));
        }

        [Fact]
        public async Task An_assistants_keep_of_an_unsure_format_is_not_built_and_the_plan_never_says_you_kept_it()
        {
            var (engine, sessions, view, need) = await UnsureFormatAsync();
            view = await McpTools.EditStartPlan(engine, "answer", need.Id, "keep");   // the assistant's door
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            var line = LineOf(engine, need);
            Assert.Equal(StartDecidedBy.Assistant, line.DecidedBy);
            Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"\bby you\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase), line.Why ?? "");   // "Kept by your assistant." is true (join 6)
            view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);   // the line is held with its reason; the rest of the plan builds
            Assert.True(string.IsNullOrEmpty(await FormatOfAsync(sessions, line.Item.ObjectRef)), "a format only the assistant kept went into the model");
            var after = engine.StartPlanDocForTest().Lines.Single(l => string.Equals(StartPlanDecide.LineKey(l), StartPlanDecide.LineKey(line), StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(after.Held), "the line was neither built nor held with a reason");
        }
    }
}
