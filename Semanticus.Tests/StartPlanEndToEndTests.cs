// THE END-TO-END PROOF of the spec-first start (the wiring step): the whole start on the J8 schema, through BOTH doors, Pro and Free.
//
//   start, sample, (Pro: model permission, source grant), decide to stable, the assistant's words, approve, build, then read the Plan.
//
// It counts what the person pressed and what the assistant wrote, the grades before and after as letters, the asks Jev was sent, and that the Plan after
// the build offers nothing the start plan did not show. The numbers go to a file when SEMANTICUS_E2E_REPORT names one, so the report quotes a run, not a
// memory of one.
//
// THE SOURCE. The J8 schema (SqlDraftBuildTests.J8Schema, the nine foreign keys of the check's j8-schema.json) through the offline schema seam. Its ROWS
// come from the fixture folder named by SEMANTICUS_UPLIFT_FIXTURE (the seven CSVs of uat-sol/fixtures/uplift-services/raw) when it is set, and from a small
// in-code generator of the same shape otherwise, so the test runs anywhere. Either way the rows go only to the sample's own queries, inside the engine.
//
// THE DOORS. "The UI door" is what the Studio does: human origin, the person's own presses. "The MCP door" is what the person's assistant does: the
// MCP tool methods themselves (McpTools.*, origin "agent"), and the only press it cannot make, the approval, stays the person's. The words come from
// the assistant on both doors, because that is where words come from (golden rule 1).
//
// THE ORACLE IS RECORDED, THEN REPLAYED (the way the decide tests do it). A policy oracle (StartPlanDecideTests.PolicyOracle) answers by what the object is called and
// writes every request down. The first run records; the measured run is a RecordedDecisionOracle over that fixture, which names the version it served, so the
// service's cache works exactly as it does in production: that is what "the Plan after the build asks nothing new" depends on, and a request the recording never
// saw is counted as an ask.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
    public sealed class StartPlanEndToEndTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _origHome;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public StartPlanEndToEndTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "e2e-" + Guid.NewGuid().ToString("N").Substring(0, 6));
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

        // ================================================================== the source's rows

        private static readonly string Server = "wh-j8.datawarehouse.fabric.microsoft.com", Db = "ContosoConsulting";

        private sealed class J8Source
        {
            public readonly Dictionary<string, (string[] Cols, List<object[]> Rows)> Tables = new Dictionary<string, (string[], List<object[]>)>(StringComparer.OrdinalIgnoreCase);
            public int Queries;
            public readonly List<int> MaxRows = new List<int>();

            public J8Source(FabricSchema schema)
            {
                var dir = Environment.GetEnvironmentVariable("SEMANTICUS_UPLIFT_FIXTURE");
                var raw = string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, "raw");
                foreach (var t in schema.Tables)
                {
                    var cols = t.Columns.Select(c => c.Name).ToArray();
                    var rows = raw != null && File.Exists(Path.Combine(raw, t.Name + ".csv")) ? FromCsv(Path.Combine(raw, t.Name + ".csv"), t) : Generated(t);
                    Tables["dbo." + t.Name] = (cols, rows);
                }
            }

            public string Origin => Environment.GetEnvironmentVariable("SEMANTICUS_UPLIFT_FIXTURE") is { Length: > 0 } ? "the uplift-services CSVs" : "the in-code generator";

            private static object Convert(string text, FabricColumn c)
            {
                if (string.IsNullOrEmpty(text)) return null;
                switch (c.DataType)
                {
                    case "Int64": return long.Parse(text, CultureInfo.InvariantCulture);
                    case "Decimal": return decimal.Parse(text, CultureInfo.InvariantCulture);
                    case "DateTime": return DateTime.Parse(text, CultureInfo.InvariantCulture);
                    default: return text;
                }
            }

            private static List<object[]> FromCsv(string path, FabricTable t)
            {
                var lines = File.ReadAllLines(path);
                var header = lines[0].Split(',');
                var idx = t.Columns.Select(c => Array.IndexOf(header, c.Name)).ToArray();
                var rows = new List<object[]>();
                foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
                {
                    var parts = Regex.Split(line, ",(?=(?:[^\"]*\"[^\"]*\")*[^\"]*$)").Select(p => p.Trim('"')).ToArray();
                    rows.Add(t.Columns.Select((c, i) => idx[i] < 0 || idx[i] >= parts.Length ? null : Convert(parts[idx[i]], c)).ToArray());
                }
                return rows;
            }

            // The same shape as the fixture's gen.py, smaller: yyyymmdd date keys, Y/N flags, coded dimensions, a blank PAID_DT_KEY for unpaid invoices,
            // a unique audit text column. Deterministic, no clock.
            private static List<object[]> Generated(FabricTable t)
            {
                var r = new Random(20260927 + t.Name.Length);
                var rows = new List<object[]>();
                string Pick(params string[] a) => a[r.Next(a.Length)];
                long Key(DateTime d) => d.Year * 10000 + d.Month * 100 + d.Day;
                var first = new DateTime(2024, 7, 1);
                switch (t.Name)
                {
                    case "ref_codes":
                        foreach (var type in new[] { "IND", "RGN", "GRD", "PRC", "PST", "ENG" })
                            for (var i = 1; i <= 4; i++) rows.Add(new object[] { type, type + i, type + " description " + i });
                        break;
                    case "dim_date":
                        for (var i = 0; i < 730; i++)
                        {
                            var d = first.AddDays(i);
                            var fy = d.Year + (d.Month >= 7 ? 1 : 0);
                            rows.Add(new object[] { Key(d), d, (long)fy, (long)(((d.Month - 7 + 12) % 12) / 3 + 1), (long)d.Month, d.ToString("MMM", CultureInfo.InvariantCulture), (long)((int)d.DayOfWeek + 1), d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday ? "N" : "Y" });
                        }
                        break;
                    case "dim_client":
                        for (var i = 1; i <= 60; i++) rows.Add(new object[] { "C" + i.ToString("000"), "Client " + i, Pick("IND1", "IND2", "IND3"), Pick("RGN1", "RGN2", "RGN3"), Pick("Y", "Y", "N"), Key(first.AddDays(r.Next(300))) });
                        break;
                    case "dim_staff":
                        for (var i = 1; i <= 80; i++) rows.Add(new object[] { "E" + i.ToString("000"), "Staff " + i, Pick("GRD1", "GRD2", "GRD3", "GRD4"), Pick("PRC1", "PRC2", "PRC3"), 80m + r.Next(0, 120), Key(first.AddDays(-r.Next(900))), Pick("N", "N", "N", "Y") });
                        break;
                    case "dim_project":
                        for (var i = 1; i <= 180; i++) rows.Add(new object[] { "P" + i.ToString("0000"), "C" + (r.Next(60) + 1).ToString("000"), "Project " + i, Pick("PRC1", "PRC2", "PRC3"), Pick("ENG1", "ENG2", "ENG3"), (long)r.Next(50, 800), 10000m + r.Next(0, 90000), Key(first.AddDays(r.Next(300))), Key(first.AddDays(300 + r.Next(300))), Pick("PST1", "PST2", "PST3") });
                        break;
                    case "fact_timesheet":
                        for (var i = 1; i <= 600; i++) rows.Add(new object[] { "TS" + i.ToString("00000"), "E" + (r.Next(80) + 1).ToString("000"), "P" + (r.Next(180) + 1).ToString("0000"), Key(first.AddDays(r.Next(700))), (decimal)(r.Next(1, 16)) / 2m, Pick("Y", "Y", "N"), 90m + r.Next(0, 110), Pick("Y", "Y", "Y", "N"), "audit row " + i + " " + Guid.NewGuid().ToString("N").Substring(0, 8) });
                        break;
                    case "fact_invoice":
                        for (var i = 1; i <= 300; i++)
                        {
                            var d = first.AddDays(r.Next(600));
                            rows.Add(new object[] { "INV" + i.ToString("00000"), "P" + (r.Next(180) + 1).ToString("0000"), "C" + (r.Next(60) + 1).ToString("000"), Key(d), Key(d.AddDays(30)), i % 4 == 0 ? null : (object)Key(d.AddDays(r.Next(20, 60))), 1000m + r.Next(0, 90000), 100m + r.Next(0, 9000), Pick("AUD", "AUD", "NZD"), Pick("N", "N", "N", "Y") });
                        }
                        break;
                }
                return rows;
            }

            public Task<ResultSet> Execute(string server, string database, string sql, int maxRows)
            {
                lock (Tables) { Queries++; MaxRows.Add(maxRows); }
                var count = Regex.Match(sql, @"^SELECT COUNT_BIG\(\*\) FROM \[((?:[^\]]|\]\])*)\]\.\[((?:[^\]]|\]\])*)\]$");
                if (count.Success)
                {
                    var t = Tables[count.Groups[1].Value + "." + count.Groups[2].Value];
                    return Task.FromResult(new ResultSet { Columns = new[] { new ColumnDef { Name = "" } }, Rows = new[] { new object[] { (long)t.Rows.Count } }, RowCount = 1 });
                }
                var top = Regex.Match(sql, @"^SELECT TOP \((\d+)\) (.+) FROM \[((?:[^\]]|\]\])*)\]\.\[((?:[^\]]|\]\])*)\]$");
                if (!top.Success) return Task.FromResult(ResultSet.FromError("Unexpected query in the J8 fixture: " + sql));
                var n = int.Parse(top.Groups[1].Value, CultureInfo.InvariantCulture);
                var tbl = Tables[top.Groups[3].Value + "." + top.Groups[4].Value];
                var names = Regex.Matches(top.Groups[2].Value, @"\[((?:[^\]]|\]\])*)\]").Select(m => m.Groups[1].Value.Replace("]]", "]")).ToArray();
                var idx = names.Select(c => Array.IndexOf(tbl.Cols, c)).ToArray();
                var rows = tbl.Rows.Take(Math.Min(n, maxRows)).Select(r => idx.Select(j => r[j]).ToArray()).ToArray();
                return Task.FromResult(new ResultSet { Columns = names.Select(c => new ColumnDef { Name = c }).ToArray(), Rows = rows, RowCount = rows.Length, Truncated = tbl.Rows.Count > rows.Length });
            }
        }

        // ================================================================== the run

        public sealed class Numbers
        {
            public string Door, Tier, Source;
            public int PersonPresses, AssistantWrites, WordsWritten, WordBatches;
            public List<string> Presses = new List<string>(), Writes = new List<string>();
            public bool SampleRead; public int SampleQueries, SampleTables;
            public string DecideState; public int DecideRounds; public int JevAsks;
            public string GradesBefore, GradesAfter, GradesPreview;
            public int NeedsYouAtApproval;
            public int PlanItemsAfterBuild, ReadySemanticusRows, NewAsksAfterBuild, NewJevRunsAfterBuild;
            public List<string> PlanRows = new List<string>(), FreshRequests = new List<string>(), ReadyDetail = new List<string>(), WhyAgain = new List<string>();
            public int Tables, Measures, Relationships; public string DateDiag; public int RankAsksAfterBuild, DeclinesLeftUnticked, DeclinesCarried; public HashSet<string> RankDigests = new HashSet<string>();
            public long BuildUndoSteps;
            public override string ToString() =>
                $"{Door} / {Tier} ({Source}): person pressed {PersonPresses} [{string.Join("; ", Presses)}], assistant wrote {AssistantWrites} [{string.Join("; ", Writes)}] ({WordsWritten} words in {WordBatches} batches); "
                + $"sample read={SampleRead} ({SampleTables} tables, {SampleQueries} queries); decide {DecideState} in {DecideRounds} rounds, {JevAsks} asks to Jev; "
                + $"grades before {GradesBefore}, preview {GradesPreview}, after {GradesAfter}; needs-you at approval {NeedsYouAtApproval} ({DeclinesLeftUnticked} lines left unticked, {DeclinesCarried} carried into the Plan as the person's drops); "
                + $"built {Tables} tables, {Measures} measures, {Relationships} links; Plan after build: {PlanItemsAfterBuild} rows, {ReadySemanticusRows} ready Semanticus rows, {NewAsksAfterBuild} new decision asks (+{RankAsksAfterBuild} Plan ranking), {NewJevRunsAfterBuild} new Jev runs "
                + $"[{string.Join(" | ", PlanRows)}] ready: [{string.Join(" || ", ReadyDetail)}] fresh requests: [{string.Join(" | ", FreshRequests.Take(30))}] why again: [{string.Join(" || ", WhyAgain)}] date diag: {DateDiag}";
        }

        private static void Report(Numbers n)
        {
            var path = Environment.GetEnvironmentVariable("SEMANTICUS_E2E_REPORT");
            if (!string.IsNullOrWhiteSpace(path)) File.AppendAllText(path, n + Environment.NewLine);
        }

        private static string GradesOf(Scorecard ai, BpaScorecard bpa) => $"AI {ai?.Grade ?? "?"} ({(int)Math.Round(ai?.Overall ?? 0)}), best practice {bpa?.BestPractice ?? "?"}";

        /// <summary>Words for every open item: plain, short, no formula text, a different one per object. The assistant's side of the start.</summary>
        private static StartWord[] WordsFor(StartWordsPage page) => page.Items.Select(i => new StartWord
        {
            Ref = i.Ref, Kind = i.Kind,
            Text = i.Kind == "synonyms" ? "alias one, alias two"
                 : i.Kind == "display_name" ? NewName(i.Ref)
                 : i.Kind == "instructions" ? "This model answers questions about consulting work: hours, projects, clients and invoices."
                 : "What " + Humanise(i.Ref) + " means for the business, in plain words.",
        }).ToArray();

        /// <summary>A business name that is not the one the object already has (a name the object already has is refused: "it already has that name").</summary>
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

        private async Task<(Numbers N, string Fixture)> RunAsync(bool pro, bool mcpDoor, string fixture = null, ISet<string> rankDigests = null)
        {
            var measured = fixture != null || !pro;   // the recording run of a Pro start proves nothing about the cache; Free has nothing to record
            // Each run is its own session: its own state folder, so the recording run's plan (built, never saved) is not the replay's in-progress plan.
            var runDir = Path.Combine(_root, "run-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            Directory.CreateDirectory(Path.Combine(runDir, "home"));
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(runDir, "home"));
            ConnectionRegistry.RootOverride = Path.Combine(runDir, "user");
            var n = new Numbers { Door = mcpDoor ? "MCP door (assistant starts)" : "UI door (person starts)", Tier = pro ? "Pro" : "Free" };
            var schema = SqlDraftBuildTests.J8Schema();
            var source = new J8Source(schema);
            n.Source = source.Origin;
            AgentPolicyStore.SetPreset("open", "human");   // the person allowed the assistant to read a sample (QueryData)

            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, pro ? TestEntitlements.Pro : TestEntitlements.Free, Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N").Substring(0, 5)));
            _owned.Add(engine); _owned.Add(sessions);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            engine.SourceSampleExecutorForTest = source.Execute;
            var policy = new StartPlanDecideTests.PolicyOracle();
            var replay = fixture == null ? null : new RecordedDecisionOracle(fixture, "jev-test-1");
            engine.UseDecisionsForTest(new DecisionService(replay != null ? (IDecisionOracle)replay : policy));
            Func<int> asksNow = () => replay != null ? replay.AskedDigests.Count : policy.Asks;
            var history = new List<string>();
            sessions.Bus.StartPlanChanged += v => { lock (history) history.Add(v.Digest?.Substring(0, 6) + "/" + v.Decide?.State + "/r" + v.Decide?.Round + (v.Grades?.AfterBuildProvisional == false ? "/prev-ok" : "")); };
            var origin = mcpDoor ? "agent" : "human";
            void Person(string what) { n.PersonPresses++; n.Presses.Add(what); }
            void Assistant(string what) { n.AssistantWrites++; n.Writes.Add(what); }

            // ---- start (the person's press on the UI door, the assistant's call on the MCP door)
            var folder = Path.Combine(_root, "model-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            StartPlanView view;
            if (mcpDoor) { Assistant("start_model_from_sql"); view = await McpTools.StartModelFromSql(engine, Server, Db, folder, "Uplift"); }
            else { Person("Start"); view = await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = folder, Name = "Uplift" }, "human"); }
            Assert.True(view.Exists);
            await engine.StartSampleSettledForTest();
            view = await engine.GetStartPlanAsync();
            n.SampleRead = view.SampleFacts; n.SampleQueries = source.Queries; n.SampleTables = view.Sample?.Measured ?? 0;
            Assert.True(view.SampleFacts, "the sample was not read: " + view.Sample?.State + " " + view.Sample?.Reason);
            Assert.NotNull(engine.LoadSourceFacts(sessions.Current));   // kept beside the saved model, so it survives a restart and serves the model after the build

            // ---- the fiscal year the sample's dates suggest is asked as a "You" question and answered (the door's own edit)
            var fy = view.NeedsYou.SingleOrDefault(x => x.Kind == "fiscal_year");
            Assert.True(fy != null, "the dates say a July to June year but no fiscal year question was asked: " + string.Join(" | ", view.NeedsYou.Select(x => x.Kind)));
            Assert.Equal(new[] { "6/30", "calendar" }, fy.Options);
            if (mcpDoor) { Assistant("edit_start_plan (fiscal year)"); view = await McpTools.EditStartPlan(engine, "answer", "fiscal_year", fy.Recommended); }
            else { Person("Answer the fiscal year question"); view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = "fiscal_year", Value = fy.Recommended }, "human"); }
            Assert.DoesNotContain(view.NeedsYou, x => x.Kind == "fiscal_year");
            Assert.Equal("6/30", engine.StartPlanDocForTest().Manifest.DateTable.FiscalYearEnd);

            // ---- Pro: the model's permission (the person), then the source grant (either door)
            if (pro)
            {
                Person("Allow Jev for this model");
                await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
                Assert.Equal(StartDecideState.WaitingGrant, view.Decide.State);
                if (mcpDoor)
                {
                    // the source grant is the person's question in every preset (join 6, Sol item 1): the assistant's call asks, the person says yes, the same call goes through
                    Assistant("grant_start_disclosure (asks the person)");
                    var asked = await McpTools.GrantStartDisclosure(engine, view.Digest);
                    Assert.True(asked.Asked, "the assistant's grant did not ask the person");
                    Person("Say yes to Jev seeing this source");
                    await engine.ApproveAgentActionAsync((await engine.ListPendingApprovalsAsync()).Single().Id, "human");
                    Assistant("grant_start_disclosure (again)");
                    view = await McpTools.GrantStartDisclosure(engine, view.Digest);
                }
                else { Person("Allow Jev to see this source"); view = await engine.GrantStartDisclosureAsync(view.Digest, "human"); }
                await engine.StartDecideSettledForTest();
                view = await engine.GetStartPlanAsync();
                Assert.True(view.Decide.State == StartDecideState.Stable || view.Decide.State == StartDecideState.Incomplete, view.Decide.State + ": " + view.Decide.Reason);
            }
            else
            {
                Assert.Equal(StartDecideState.Off, view.Decide.State);
                Assert.Equal(0, engine.JevRunsStartedForTest);   // Free sends nothing, not even to count
            }
            n.DecideState = view.Decide.State; n.DecideRounds = engine.StartDecideRoundsForTest; n.JevAsks = asksNow();

            // ---- the assistant's words, page by page, until none are open
            for (var pass = 0; pass < 60; pass++)
            {
                var page = await engine.GetStartWordsAsync(40, 0);
                if (page.Items.Count == 0) break;
                Assistant("write_start_words");
                var wrote = await engine.WriteStartWordsAsync(WordsFor(page), "agent");
                n.WordBatches++; n.WordsWritten += wrote.Written;
                // A careful assistant keeps the name an object has when the naming rules would flag the name it was about to give (psc3, defect 4): the refusal says so.
                var keeps = wrote.Refused.Where(r => r.Contains("keep_name")).Select(r => r.Substring(0, r.IndexOf(": ", StringComparison.Ordinal))).Distinct().ToArray();
                var kept = 0;
                if (keeps.Length > 0)
                {
                    Assistant("write_start_words (keep_name)");
                    kept = (await engine.WriteStartWordsAsync(keeps.Select(k => new StartWord { Ref = k, Kind = "keep_name" }).ToArray(), "agent")).Written;
                    n.WordsWritten += kept;
                }
                Assert.True(wrote.Written + kept > 0, "no word landed (pass " + pass + ", " + page.Total + " open): " + string.Join("; ", wrote.Refused.Take(3)));
            }
            Assert.Equal(0, (await engine.GetStartWordsAsync(40, 0)).Items.Count);
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            view = await engine.GetStartPlanAsync();
            n.DecideState = view.Decide.State; n.DecideRounds = engine.StartDecideRoundsForTest; n.JevAsks = asksNow();
            if (pro) Assert.True(view.Decide.State == StartDecideState.Stable, view.Decide.State + ": " + view.Decide.Reason);
            var pd = engine.StartPlanDocForTest();
            Assert.False(view.Grades.AfterBuildProvisional, "the grades after the build are still provisional: decide " + pd.Decide.State + " preview digest " + pd.Preview?.PlanDigest + " plan digest " + view.Digest + " have projection " + (pd.Preview?.ProjectionDigest != null) + " PREVIEW ERROR " + engine.StartPreviewLastErrorForTest + " HISTORY " + string.Join(" > ", history.TakeLast(14)));
            n.GradesPreview = $"AI {view.Grades.AfterBuildGrade} ({view.Grades.AfterBuild}), best practice {view.Grades.AfterBuildBestPractice}";
            Assert.Matches("^[A-F]$", view.Grades.AfterBuildBestPractice ?? "");
            n.NeedsYouAtApproval = view.NeedsYou.Count;

            // ---- before: the model the source's own draft gives, with no start plan (the two-step path's default), graded the same way
            n.GradesBefore = await GradesOfDraftAsync(StructureManifest.Derive(SqlDraftBuildTests.J8Schema(), null, null, "before", Server, Db, "import"));

            var dd = engine.StartPlanDocForTest();
            n.DateDiag = "bases " + string.Join("; ", dd.Manifest.BaseMeasures.Where(b => b.Table.Contains("ate")).Select(b => b.Table + "/" + b.Column + "->" + b.Name + ":" + b.Pick + ":" + b.DecidedBy))
                + " lines " + string.Join("; ", dd.Lines.Where(l => (l.Item.ObjectRef ?? "").Contains("DOW") ).Select(l => l.Item.Kind + " " + l.Item.ObjectRef + " kept=" + l.Kept + " by " + l.DecidedBy + " held=" + l.Held + " why=" + l.Why));

            // ---- approve (the person, always) and build
            var declinedBefore = engine.StartPlanDocForTest().Lines.Count(l => !l.Kept && !StartPlanDecide.IsLeftOut(l) && l.Item != null && JevFamily.IsJevLine(l.Item));
            n.DeclinesLeftUnticked = declinedBefore;
            Person("Approve this plan");
            view = await engine.ApproveStartPlanAsync(view.Digest);
            Assert.True(view.CanBuild, view.BlockedBecause);
            var stepsBefore = await sessions.Current.RunAsync(() => sessions.Current.UndoStateNow().Steps);
            StartPlanBuildResult built;
            if (mcpDoor) { Assistant("build_start_plan"); built = await McpTools.BuildStartPlan(engine, view.Digest); }
            else { Person("Build the model"); built = await engine.BuildStartPlanAsync(view.Digest, "human"); }
            Assert.True(built.Built, built.Refused);
            n.BuildUndoSteps = await sessions.Current.RunAsync(() => sessions.Current.UndoStateNow().Steps) - stepsBefore;
            var after = await engine.GetStartPlanAsync();
            Assert.True(after.Exists && after.Built, "a read after the build must say it was built");

            // ---- the person's "no" travelled: every suggestion left unticked in the start plan is in the Plan as a drop by the person
            var declinedInPlan = sessions.CurrentContext.Plans.Current?.Snapshot().Count(i => i.DroppedBy == "person") ?? 0;
            n.DeclinesCarried = declinedInPlan;
            if (pro) Assert.True(declinedInPlan > 0 && declinedInPlan == declinedBefore, declinedInPlan + " carried into the Plan, " + declinedBefore + " left unticked in the start plan");
            else Assert.Equal(0, declinedInPlan);

            // ---- the grades after, on the built model, and what it holds
            var ai = await engine.AiReadinessScanAsync();
            var bpa = await engine.BpaScanAsync();
            n.GradesAfter = GradesOf(ai, bpa);
            n.Tables = await sessions.Current.ReadAsync(m => m.Tables.Count);
            n.Measures = await sessions.Current.ReadAsync(m => m.AllMeasures.Count());
            n.Relationships = await sessions.Current.ReadAsync(m => m.Relationships.Count);
            var viaGradesOf = await sessions.Current.ReadAsync(m => StartPlanBuild.GradesOf(m));
            Assert.True(view.Grades.AfterBuildBestPractice == bpa.BestPractice,
                "preview shown " + view.Grades.AfterBuildBestPractice + ", scan of the built model " + bpa.BestPractice + ", GradesOf on the built model " + viaGradesOf.BestPractice
                + "; scan: " + bpa.ViolationCount + " open, " + bpa.WaivedCount + " accepted, objects " + bpa.ObjectsScanned + "; " + string.Join(" | ", bpa.Violations.Where(v => !v.IsAccepted()).GroupBy(v => v.RuleId + "/" + v.Severity).Select(g => g.Key + " x" + g.Count()).Take(12)));

            // ---- the Plan after the build offers nothing the start plan did not show
            var digestsBefore = replay?.AskedDigests.ToList();
            var asks = asksNow(); var runs = engine.JevRunsStartedForTest;
            policy.Phase = 2;   // every request from here on is the Plan's own, after the build
            var plan = await engine.GetModelPlanAsync();
            await engine.JevSettledForTest();
            plan = await engine.GetModelPlanAsync();
            n.NewAsksAfterBuild = asksNow() - asks; n.NewJevRunsAfterBuild = engine.JevRunsStartedForTest - runs;
            n.PlanItemsAfterBuild = plan.Items.Length;
            n.ReadySemanticusRows = plan.Items.Count(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held);
            n.PlanRows = plan.Items.Select(i => i.Doer + "/" + i.StateWord + ": " + i.Title).Take(12).ToList();
            n.ReadyDetail = plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held)
                .Select(i => i.Title + " / " + i.Sub + " / " + i.Touches + " / " + string.Join("; ", i.Diff ?? new string[0])).ToList();
            var fresh = policy.Requests.Where(r => r.Phase == 2).Select(r => r.Digest + " " + r.Point + ": " + string.Join(", ", r.QuestionNames.Take(3))).ToList();
            n.FreshRequests = fresh;
            if (replay != null)
            {
                // The Plan ranks its own sections with Jev (point plan.rank): a presentation order, not a decision about the model, so it is not what "the Plan after the
                // build asks nothing new" is about. It is counted apart, never hidden.
                var fresh2 = replay.AskedDigests.Except(digestsBefore).ToList();
                n.RankAsksAfterBuild = fresh2.Count(d => rankDigests != null && rankDigests.Contains(d));
                n.FreshRequests = fresh2.Where(d => rankDigests == null || !rankDigests.Contains(d)).Select(d => "digest " + d + " (a decision the recording did not make before the build)").ToList();
                n.NewAsksAfterBuild = n.FreshRequests.Count;
            }
            else n.RankDigests = policy.Requests.Where(r => r.Point == DecisionRegistry.PlanRank).Select(r => r.Digest).ToHashSet();
            // Why was it asked again? For each new request, how its snapshot differs from the earlier request about the same object (the keys whose values moved).
            foreach (var f in policy.Snapshots.Where(x => x.Phase == 2).Take(6))
            {
                var earlier = policy.Snapshots.LastOrDefault(x => x.Phase == 1 && x.QuestionNames.Length > 0 && x.QuestionNames[0] == f.QuestionNames[0]);
                if (earlier.SnapshotJson == null) { n.WhyAgain.Add(f.QuestionNames[0] + ": no earlier request about it (a new object or a new name)"); continue; }
                var a = System.Text.Json.JsonDocument.Parse(earlier.SnapshotJson).RootElement; var b = System.Text.Json.JsonDocument.Parse(f.SnapshotJson).RootElement;
                var moved = b.EnumerateObject().Where(p => !a.TryGetProperty(p.Name, out var q) || q.GetRawText() != p.Value.GetRawText())
                    .Select(p => p.Name + ": " + (a.TryGetProperty(p.Name, out var q2) ? q2.GetRawText() : "(absent)") + " -> " + p.Value.GetRawText()).ToList();
                n.WhyAgain.Add(f.QuestionNames[0] + " moved " + string.Join(" ; ", moved.Select(m => m.Length > 260 ? m.Substring(0, 260) : m))
                    + " | questions now [" + string.Join(", ", f.QuestionNames) + "] earlier [" + string.Join(", ", earlier.QuestionNames) + "]");
            }
            if (!measured) n.Door += " [recording run]";
            Report(n);
            if (pro && measured)
            {
                // The promise (design 2.7): with the decide stable the Plan's own Jev run on the built model sends nothing new and offers no Ready Semanticus row.
                Assert.True(n.NewAsksAfterBuild == 0, n.NewAsksAfterBuild + " new asks after the build: " + string.Join(" | ", n.FreshRequests.Take(8)) + "\nWHY: " + string.Join("\n", n.WhyAgain));
                Assert.True(n.ReadySemanticusRows == 0, "ready Semanticus rows after the build: " + string.Join(" | ", plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held)
                    .Select(i => i.Title + " / " + i.Sub + " / " + i.Touches + " / " + string.Join("; ", i.Diff ?? new string[0]) + " / " + string.Join("; ", i.Why ?? new string[0]))));
            }
            else if (!pro)
            {
                // Free sends nothing, ever. Its plan has no best-practice fixes by design (K2, F-238: those stay one at a time after the build), so the Plan after
                // the build still offers them; they are counted, not hidden.
                Assert.Equal(0, asksNow());
            }
            return (n, pro && fixture == null ? policy.Fixture() : null);
        }

        /// <summary>Grade the model a manifest's own spec builds (no Jev, no words, no best-practice fixes): the "before" the start plan is measured against.</summary>
        private async Task<string> GradesOfDraftAsync(StructureManifest manifest)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Free, Path.Combine(_root, "before-" + Guid.NewGuid().ToString("N").Substring(0, 5)));
            _owned.Add(engine); _owned.Add(sessions);
            await engine.CreateModelAsync("Before", 1604);
            await engine.SetSpecAsync(System.Text.Json.JsonSerializer.Serialize(manifest.ToSpec(), StartPlanDecideTests.Camel), "human");
            await engine.BuildModelFromSpecAsync("human");
            return GradesOf(await engine.AiReadinessScanAsync(), await engine.BpaScanAsync());
        }

        private async Task<object> ProAsync(bool mcpDoor)
        {
            var recording = await RunAsync(pro: true, mcpDoor: mcpDoor);
            var run = await RunAsync(pro: true, mcpDoor: mcpDoor, fixture: recording.Fixture, rankDigests: recording.N.RankDigests);
            Assert.NotNull(run.N);
            return run.N;
        }

        [Fact] public async Task Pro_UI_door() => Assert.NotNull(await ProAsync(false));
        [Fact] public async Task Pro_MCP_door() => Assert.NotNull(await ProAsync(true));
        [Fact] public async Task Free_UI_door() => Assert.NotNull((await RunAsync(pro: false, mcpDoor: false)).N);
        [Fact] public async Task Free_MCP_door() => Assert.NotNull((await RunAsync(pro: false, mcpDoor: true)).N);
    }
}
