// Verifier, join 7 (uat7-start): attacks by CLASS on the lane's items, on the J8 schema with the policy oracle (Pro) and on Free.
// A: a measure the person refused in the start plan must not come back as a Plan row, also when the assistant's business name renames its column (item 4's class).
// B: a keep the person made survives the PERSON's own rename of the table in the start plan (item 6's class, the other rename door).
// C: every property the WHOLE build writes (structure and lines) is on the review, on a kept line, or a default the check knows (item 1's class, both halves).
// D: History's "undo to here" on the build tells the page, as Undo does (item 5's class).
// A (Free) and D fail on f0fd5dbb and are left failing: each proves a defect for the join. A (Pro), B and C pass.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class VerifyUat7StartTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _origHome;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public VerifyUat7StartTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "vu-" + Guid.NewGuid().ToString("N").Substring(0, 6));
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
        private static readonly JsonSerializerOptions Wire = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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
                 : i.Kind == "display_name" ? (string.Equals(Humanise(i.Ref), (i.Ref ?? "").Substring((i.Ref ?? "").LastIndexOf('/') + 1), StringComparison.OrdinalIgnoreCase) ? Humanise(i.Ref) + " Value" : Humanise(i.Ref))
                 : i.Kind == "instructions" ? "This model answers questions about consulting work: hours, projects, clients and invoices."
                 : "What " + Humanise(i.Ref) + " means for the business, in plain words.",
        }).ToArray();

        private static async Task WriteAllWordsAsync(LocalEngine engine)
        {
            for (var pass = 0; pass < 60; pass++)
            {
                var page = await engine.GetStartWordsAsync(40, 0);
                if (page.Items.Count == 0) break;
                await engine.WriteStartWordsAsync(WordsFor(page), "agent");
            }
        }

        private async Task<(LocalEngine Engine, SessionManager Sessions)> StartJ8Async(bool pro)
        {
            AgentPolicyStore.SetPreset("open", "human");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, pro ? TestEntitlements.Pro : TestEntitlements.Free, Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N").Substring(0, 5)));
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
            if (pro)
            {
                await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
                await engine.GrantStartDisclosureAsync(view.Digest, "human");
            }
            await Settle(engine);
            return (engine, sessions);
        }

        private static async Task<StartPlanView> Settle(LocalEngine engine)
        {
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            return await engine.GetStartPlanAsync();
        }

        // ================================================================== A: item 4, a refused measure stays refused when its column is renamed by the words

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task A_a_measure_the_person_refused_does_not_come_back_after_the_assistant_renamed_its_column(bool pro)
        {
            var (engine, sessions) = await StartJ8Async(pro);
            var bm = engine.StartPlanDocForTest().Manifest.BuiltBases().First(b => b.Column == "HRS" && b.Pick != StarterMeasureTemplates.None);
            await engine.EditStartPlanAsync(new StartPlanEdit { Op = "drop_line", Target = StarterMeasuresFamily.MeasureRef(bm.Table, bm.Name) }, "human");
            await Settle(engine);
            await WriteAllWordsAsync(engine);   // the assistant gives HRS a business name, as an assistant does
            var view = await Settle(engine);
            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var (table, column) = await sessions.Current.ReadAsync(m =>
            {
                var c = m.AllColumns.OfType<TabularEditor.TOMWrapper.DataColumn>().First(x => x.SourceColumn == "HRS");
                return (c.Table.Name, c.Name);
            });
            Assert.NotEqual("HRS", column);   // the precondition: the words renamed the column
            var plan = await engine.GetModelPlanAsync();
            var text = JsonSerializer.Serialize(plan, Wire);
            var offered = DecisionRegistry.SummarizeOptions.Where(p => p != StarterMeasureTemplates.None)
                .Select(p => StarterMeasureTemplates.Name(p, column)).Where(n => text.Contains(n, StringComparison.Ordinal)).ToList();
            Assert.True(offered.Count == 0, "the person refused a measure on " + table + "[" + column + "] (HRS) in the start plan, and after the build the Plan offers " + string.Join(", ", offered));
        }

        // ================================================================== B: item 6, a keep survives the person's own table rename

        [Fact]
        public async Task B_a_keep_survives_the_persons_own_rename_of_the_table_in_the_start_plan()
        {
            var (engine, sessions) = await StartJ8Async(true);
            var view = await engine.GetStartPlanAsync();
            var asked = view.NeedsYou.Where(n => n.Kind == "unsure_line" && n.Text.StartsWith("Format as")).ToList();
            Assert.NotEmpty(asked);
            foreach (var n in asked) await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = n.Id, Value = "keep" }, "human");
            await Settle(engine);
            var kept = engine.StartPlanDocForTest().Lines.Where(l => l.Kept && l.DecidedBy == StartDecidedBy.Person && LocalEngine.IsJevFormatLine(l.Item)).ToList();
            Assert.NotEmpty(kept);
            var table = kept.Select(k => k.Item.ObjectRef).First(r => r.StartsWith("measure:") || r.StartsWith("column:"));
            table = table.Substring(table.IndexOf(':') + 1); table = table.Substring(0, table.IndexOf('/'));
            var renamed = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "rename", Target = "table:" + table, Value = table + " Renamed" }, "human");
            view = await Settle(engine);
            var held = engine.StartPlanDocForTest().Lines.Where(l => l.Held != null && l.DecidedBy == StartDecidedBy.Person && LocalEngine.IsJevFormatLine(l.Item)).ToList();
            Assert.True(held.Count == 0, "keeps held after the person renamed " + table + ": " + string.Join(" | ", held.Select(h => h.Item.ObjectRef + " :: " + h.Held)));
            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
        }

        // ================================================================== C: item 1, the whole build (structure and lines) writes nothing the review does not show

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task C_every_property_the_whole_build_writes_is_on_the_review(bool pro)
        {
            var (engine, sessions) = await StartJ8Async(pro);
            await WriteAllWordsAsync(engine);
            var view = await Settle(engine);
            view = await engine.ApproveStartPlanAsync(view.Digest);
            StartWriteJournal structure = null, lines = null;
            var structureNet = new List<(string Ref, string Prop, string Old, string New, string Value)>();
            var linesNet = new List<(string Ref, string Prop, string Old, string New)>();
            engine.StartPlanBuildSeamsForTest = new StartPlanBuildSeams
            {
                InsideBuildForTest = () => structure = new StartWriteJournal(sessions.Current),
                AfterStructureForTest = m =>
                {
                    foreach (var n in structure.Net()) structureNet.Add((StartPlanReview.RefOf(n.Obj), n.Property, n.Old, n.New, n.New));
                    structure.Dispose();
                    lines = new StartWriteJournal(sessions.Current);
                },
                AfterLinesForTest = m =>
                {
                    foreach (var n in lines.Net()) linesNet.Add((StartPlanReview.RefOf(n.Obj), n.Property, n.Old, n.New));
                    lines.Dispose();
                },
            };
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var doc = engine.StartPlanDocForTest();
            var keptRefs = new HashSet<string>(doc.Lines.Where(l => l.Kept && l.Item != null).SelectMany(l => new[] { l.Item.ObjectRef ?? "", StartPlanWords.Forward(doc, l.Item.ObjectRef ?? "") }), StringComparer.OrdinalIgnoreCase);
            var renames = doc.Lines.Count(l => l.Kept && l.Item?.Kind == "rename");
            // The lines half: every object a line wrote to is the object of a kept line (by its name before or after the plan's renames), or a measure whose formula a rename rewrote.
            // Follow-ons that are not content: a table's partition takes the table's new name, and the culture's linguistic schema is where the synonym lines are kept
            // (UnreviewedSynonym checks every synonym in it after the lines).
            var synonyms = doc.Lines.Any(l => l.Kept && l.Item?.Kind == "set_synonyms");
            var unexplained = linesNet.Where(n => !keptRefs.Contains(n.Ref) && !keptRefs.Contains(Before(doc, n.Ref))
                                                  && !(n.Prop == "Expression" && renames > 0) && n.Prop != "LineageTag"
                                                  && !(n.Ref.StartsWith("partition:") && n.Prop == "Name" && renames > 0)
                                                  && !(n.Ref.StartsWith("obj:Culture/") && n.Prop == "Content" && synonyms))
                                      .Select(n => n.Ref + " " + n.Prop + ": '" + Cut(n.Old) + "' -> '" + Cut(n.New) + "'").Distinct().ToList();
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "vu7-probe-" + (pro ? "pro" : "free") + ".txt"),
                new[] { "STRUCTURE " + structureNet.Count }.Concat(structureNet.Select(n => n.Ref + " " + n.Prop + " = " + Cut(n.New)))
                .Concat(new[] { "LINES " + linesNet.Count }).Concat(linesNet.Select(n => n.Ref + " " + n.Prop + ": '" + Cut(n.Old) + "' -> '" + Cut(n.New) + "'"))
                .Concat(new[] { "UNEXPLAINED " + unexplained.Count }).Concat(unexplained));
            Assert.True(unexplained.Count == 0, unexplained.Count + " properties the lines wrote on objects no kept line names: " + string.Join(" | ", unexplained.Take(15)));
        }

        // ================================================================== D: item 5, every undo of the build refreshes the page, also History's "undo to here"

        [Fact]
        public async Task D_an_undo_to_the_build_entry_from_History_tells_the_page_the_plan_is_a_plan_again()
        {
            var (engine, sessions) = await StartJ8Async(false);
            var view = await engine.ApproveStartPlanAsync((await Settle(engine)).Digest);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var entry = sessions.Ledger.ForModel(sessions.Current.Id).Last(x => x.Cause?.Kind == ChangeCauseKinds.StartPlan);
            var heard = new List<StartPlanView>();
            sessions.Bus.StartPlanChanged += v => { lock (heard) heard.Add(v); };
            await engine.UndoToEntryAsync(entry.EntryId, "human");
            Assert.Equal(0, await sessions.Current.ReadAsync(m => m.Tables.Count));   // the precondition: History took the build back
            Assert.False((await engine.GetStartPlanAsync()).Built);
            lock (heard) Assert.True(heard.Count > 0 && !heard.Last().Built, "History's undo took the build back and the page was not told (" + heard.Count + " views heard)");
        }

        private static string Before(StartPlanDoc doc, string finalRef)
        {
            foreach (var l in doc.Lines.Where(l => l.Item?.ObjectRef != null))
                if (string.Equals(StartPlanWords.Forward(doc, l.Item.ObjectRef), finalRef, StringComparison.OrdinalIgnoreCase)) return l.Item.ObjectRef;
            return finalRef;
        }

        private static string Cut(string s) => s == null ? "" : s.Length > 80 ? s.Substring(0, 80) + "..." : s;
    }
}
