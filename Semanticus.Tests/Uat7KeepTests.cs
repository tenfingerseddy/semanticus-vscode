// Join 7 (coordinator, from plan-statement-check-3 defect 2): a person's keep, end to end on the real engine and BOTH doors (M11 fix round after integration check 2, defect D2). The table in StartPlanKeepGateTests proves the
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
    public sealed class Uat7KeepTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _origHome;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public Uat7KeepTests(RestoreRootFixture fixture)
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

        private async Task<(LocalEngine Engine, SessionManager Sessions, StartPlanView View, StartNeed Need)> UnsureFormatAsync(bool wordsFirst = true)
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
            if (wordsFirst) await WriteAllWordsAsync(engine);
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            view = await engine.GetStartPlanAsync();
            var need = view.NeedsYou.FirstOrDefault(n => n.Kind == "unsure_line" && n.Text.StartsWith("Format as"));
            Assert.True(need != null, "no unsure format line to answer: " + string.Join(" | ", view.NeedsYou.Select(n => n.Kind)));
            return (engine, sessions, view, need);
        }

        private static async Task WriteAllWordsAsync(LocalEngine engine)
        {
            for (var pass = 0; pass < 60; pass++)
            {
                var page = await engine.GetStartWordsAsync(40, 0);
                if (page.Items.Count == 0) break;
                await engine.WriteStartWordsAsync(WordsFor(page), "agent");
            }
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

        /// <summary>A format line the person kept (answered Keep on a Jev-unsure format).</summary>
        private static List<StartLine> KeptFormats(LocalEngine engine) =>
            engine.StartPlanDocForTest().Lines.Where(l => l.Kept && l.DecidedBy == StartDecidedBy.Person && LocalEngine.IsJevFormatLine(l.Item)).ToList();

        [Fact]
        public async Task A_keep_the_person_made_survives_the_assistants_later_renames_and_builds()
        {
            var (engine, sessions, view, _) = await UnsureFormatAsync(wordsFirst: false);
            var asked = view.NeedsYou.Where(n => n.Kind == "unsure_line" && n.Text.StartsWith("Format as")).ToList();
            Assert.NotEmpty(asked);
            foreach (var n in asked) view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = n.Id, Value = "keep" }, "human");
            await engine.StartDecideSettledForTest();
            var kept = KeptFormats(engine);
            Assert.Equal(asked.Count, kept.Count);

            await WriteAllWordsAsync(engine);   // the assistant's words come AFTER the person's answers, and rename objects
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            view = await engine.GetStartPlanAsync();

            var now = KeptFormats(engine);
            var lost = kept.Select(k => k.Item.After).OrderBy(x => x).Zip(now.Select(k => k.Item.After).OrderBy(x => x), (a, b) => a == b).Count(x => !x) + Math.Abs(kept.Count - now.Count) == 0 ? new List<string>() : new List<string> { kept.Count + " kept before, " + now.Count + " after" };
            var held = engine.StartPlanDocForTest().Lines.Where(l => l.Held != null && l.DecidedBy == StartDecidedBy.Person && LocalEngine.IsJevFormatLine(l.Item)).ToList();
            // THE CLASS RULE: a keep survives a rename, so none of the person's answers is lost or held ...
            Assert.True(held.Count == 0, "kept lines were held after the assistant's renames: " + string.Join(" | ", held.Select(h => h.Item.ObjectRef + " keptId=" + h.Item.KeptIdentity + " keptDigest=" + h.Item.KeptDigest + " workedOut=" + h.Item.WorkedOutDigest + " :: " + h.Held.Substring(0, 60))));
            Assert.True(lost.Count == 0, "kept lines were lost: " + string.Join(", ", lost));
            // ... and the person approves, builds, and every kept format is in the model.
            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var after = engine.StartPlanDocForTest();
            foreach (var k in now) Assert.Equal(k.Item.After, await FormatOfAsync(sessions, StartPlanWords.Forward(after, k.Item.ObjectRef)));   // a line is stored against the name before the plan's renames; the model has the name after
        }

        [Fact]
        public async Task A_keep_that_is_voided_goes_back_into_Needs_you_before_approval_and_a_new_keep_builds()
        {
            var (engine, sessions, view, need) = await UnsureFormatAsync();
            view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = need.Id, Value = "keep" }, "human");
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            var line = LineOf(engine, need);
            Assert.True(line.Kept && line.DecidedBy == StartDecidedBy.Person);
            // What the keep was for is not what the object computes now (the object became something else): the keep cannot stand.
            var key = StartPlanDecide.LineKey(line);
            engine.StartPlanDocForTestMutate(d =>
            {
                var l = d.Lines.Single(x => StartPlanDecide.LineKey(x) == key);
                l.Item.KeptIdentity = "the-identity-of-another-object"; l.Item.KeptDigest = "the-digest-of-another-object"; l.Item.WorkedOutDigest = "the-digest-of-another-object";
            });
            await engine.KickStartDecideOnPlanAsync();
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            view = await engine.GetStartPlanAsync();
            var voided = engine.StartPlanDocForTest().Lines.Single(l => StartPlanDecide.LineKey(l) == key);
            Assert.False(string.IsNullOrEmpty(voided.Held), "the voided keep was neither built nor held: it was written without the person's keep, or lost");
            var ask = view.NeedsYou.SingleOrDefault(n => n.Kind == "voided_keep" && n.Id.EndsWith(key, StringComparison.OrdinalIgnoreCase));
            Assert.True(ask != null, "the keep is held (" + voided.Held + ") and Needs you does not ask about it: " + string.Join(" | ", view.NeedsYou.Select(n => n.Kind + ": " + n.Text)));
            Assert.Contains(voided.Item.ObjectName, ask.Text); Assert.Equal(new[] { "keep", "drop" }, ask.Options);
            // The person answers keep again: the keep is stamped for the object as it is now, and the build writes it.
            view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = ask.Id, Value = "keep" }, "human");
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            view = await engine.GetStartPlanAsync();
            Assert.DoesNotContain(view.NeedsYou, n => n.Kind == "voided_keep");
            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            Assert.Equal(line.Item.After, await FormatOfAsync(sessions, line.Item.ObjectRef));
        }
    }
}
