// VERIFY (m11/uatj8, the fixes from Sol's UAT of join 8): the verifier's own checks, beside the lane's UatJ8FixTests.
//
// U2  the Studio per-table sample control's edit (set_table_sample off from the person's door, by the table's name AS THE PLAN SHOWS IT, renamed or not) narrows
//     what Jev is sent exactly as MCP set_table_sample does: no fact of that table reaches a later request, the granted table's facts still do (positive control),
//     and turning it back on asks for the grant again; nothing of that table is sent until the person grants again.
// U3  the plan's key equals the build's key (ToSpec) on every date-table shape: a marked date table with an integer key, one keyed on its date column, one with
//     no declared key, no date table, and a marked date table whose date column the person renames or leaves out.
// U5  a date member with a time of day is sent as DAX the engine can read. DAX has no DATETIME function: a live Northstar run of the lane refused
//     "DATETIME ( 2018, 6, 25, 13, 30, 0 )" with "Failed to resolve name 'DATETIME'" (verify report, u5-live-report.md).
// U7  review equals build for each kind of Jev question: an unanswered pick is built, an unanswered unsure line is not.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Facts;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class VerifyUatJ8SampleTests : GateFixtureBase
    {
        private const string Server = "probe.datawarehouse.fabric.microsoft.com", Db = "Warehouse";
        private const string GrantedRows = "424242", RefusedRows = "987654", RefusedDistinct = "7531", RefusedBlank = "0.2468";
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public VerifyUatJ8SampleTests(RestoreRootFixture fixture) : base(fixture)
        {
            ConnectionRegistry.RootOverride = Path.Combine(Root, "connections");
            AgentPolicyStore.SetPreset("open", "human");
        }

        public override void Dispose()
        {
            ConnectionRegistry.RootOverride = _oldConnections;
            base.Dispose();
        }

        private sealed class Recorder : IDecisionOracle
        {
            public bool Available { get; set; } = true;
            private readonly List<string> _requests = new List<string>();
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                string whole;
                try { whole = System.Text.Json.JsonSerializer.Serialize(request); }
                catch { whole = System.Text.Json.JsonSerializer.Serialize(new { request.PointName, request.Snapshot }); }
                lock (_requests) _requests.Add(request.PointName + " " + whole);
                return Task.FromResult(DecisionAnswers.NotDecided(request, "recording oracle"));
            }
            public string[] All() { lock (_requests) return _requests.ToArray(); }
            public void Clear() { lock (_requests) _requests.Clear(); }
        }

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };

        private static FabricSchema Schema() => new FabricSchema
        {
            Server = Server, Database = Db,
            Tables = new[]
            {
                new FabricTable { Schema = "dbo", Name = "Sales", KeyColumns = new[] { "Id" }, Columns = new[] { C("Id", "Int64"), C("DateKey", "Int64"), C("Amount", "Decimal") } },
                new FabricTable { Schema = "dbo", Name = "DimDate", KeyColumns = new[] { "DateKey" }, Columns = new[] { C("DateKey", "Int64"), C("Date", "DateTime") } },
                new FabricTable { Schema = "dbo", Name = "Payroll", KeyColumns = new[] { "Id" }, Columns = new[] { C("Id", "Int64"), C("GrossPay", "Decimal") } },
            },
            ForeignKeys = new[] { new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "DateKey", ToSchema = "dbo", ToTable = "DimDate", ToColumn = "DateKey" } },
        };

        private static readonly string Measured = "2026-10-04T00:00:00.0000000Z";
        private static SourceSampleResult Sample() => new SourceSampleResult
        {
            Server = Server, Database = Db, MeasuredAtUtc = Measured,
            Tables = new[]
            {
                new SourceTableFacts { Schema = "dbo", Name = "Sales", Rows = 424242, Columns = new[] { new SourceColumnFacts { Source = "Amount", Distinct = 3131, BlankShare = 0.1414 } } },
                new SourceTableFacts { Schema = "dbo", Name = "Payroll", Rows = 987654, Columns = new[] { new SourceColumnFacts { Source = "GrossPay", Distinct = 7531, BlankShare = 0.2468 } } },
            },
        };

        private static bool CarriesRefused(string request) => request.Contains(RefusedRows) || request.Contains(RefusedDistinct) || request.Contains(RefusedBlank);
        private static bool CarriesGranted(string request) => request.Contains(GrantedRows) || request.Contains("3131") || request.Contains("0.1414");

        private static async Task<StartPlanView> Ready(LocalEngine e)
        {
            await e.StartDecideSettledForTest(); await e.StartPreviewSettledForTest();
            return await e.GetStartPlanAsync();
        }

        private static Task<StartPlanView> Edit(LocalEngine e, string door, string op, string target, string value) =>
            door == "mcp" ? McpTools.EditStartPlan(e, op, target, value) : e.EditStartPlanAsync(new StartPlanEdit { Op = op, Target = target, Value = value }, "human");

        private async Task<(LocalEngine E, SessionManager Sm, Recorder Oracle, string Id)> GrantedAsync(string ws)
        {
            var oracle = new Recorder();
            var sm = new SessionManager();
            var e = new LocalEngine(sm, TestEntitlements.Pro, Path.Combine(Root, ws));
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(Schema());
            e.UseDecisionsForTest(new DecisionService(oracle));
            await e.StartModelFromSqlAsync(new StartModelRequest { Name = "Model " + ws, Folder = Path.Combine(Root, ws + "-model"), Server = Server, Database = Db }, "human");
            var id = e.StartPlanDocForTest().Id;
            e.SetStartPlanSample(id, Sample());
            await e.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            var view = await Ready(e);
            await e.GrantStartDisclosureAsync(view.Digest, "human");
            await Ready(e);
            Assert.Contains(oracle.All(), CarriesRefused);   // positive control: before any refusal the recorder sees Payroll's numbers
            Assert.Contains(oracle.All(), CarriesGranted);
            return (e, sm, oracle, id);
        }

        /// <summary>The control on the plan sends set_table_sample with the table's name as the plan shows it (group.table), so a table the person renamed is refused by
        /// its new name. The refusal is the same on both doors: the same cut for Jev, the same plan, and the granted table's facts still flow.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_studio_refusal_of_a_renamed_table_cuts_the_same_sample_as_mcp_and_the_other_table_still_flows()
        {
            var cuts = new Dictionary<string, string>();
            foreach (var door in new[] { "ui", "mcp" })
            {
                var (e, sm, oracle, id) = await GrantedAsync("ws-" + door);
                using (sm) using (e)
                {
                    var view = await Edit(e, "ui", "rename", "table:Payroll", "Pay Roll");
                    var group = view.Sections.SelectMany(s => s.Groups).Single(g => g.Columns != null && g.SourceName == "Payroll");
                    Assert.Equal("Pay Roll", group.Table);
                    await Ready(e);
                    oracle.Clear();
                    view = await Edit(e, door, "set_table_sample", group.Table, "off");
                    Assert.True(view.Sections.SelectMany(s => s.Groups).Single(g => g.Columns != null && g.SourceName == "Payroll").SampleOff, door + ": the refusal by the shown name stands");
                    // A rename gives the next question a new request digest, so no cached answer hides a request.
                    await Edit(e, door, "rename", "column:Pay Roll/GrossPay", "Base Pay");
                    await Ready(e);
                    var after = oracle.All();
                    Assert.NotEmpty(after);
                    Assert.DoesNotContain(after, CarriesRefused);
                    Assert.Contains(after, CarriesGranted);   // positive control: the table still granted is still sent
                    cuts[door] = System.Text.Json.JsonSerializer.Serialize(await e.SampleForJevAsync(id, Sample()));
                    await e.DiscardStartPlanAsync("human");   // one plan in progress at a time: the other door's run starts its own
                }
            }
            Assert.Equal(cuts["mcp"], cuts["ui"]);
            Assert.False(CarriesRefused(cuts["ui"]));
        }

        /// <summary>Turning a refused table's sample back on never widens what Jev is sent by itself: the plan asks for the grant again, nothing of that table is sent
        /// until the person grants, and after the grant it is sent (the positive control that the silence was the missing grant).</summary>
        // Lean suite (8 Oct): the default run keeps the agent door's row; the sweep runs both doors.
        [Fact]
        public Task Turning_a_refused_sample_back_on_asks_for_the_grant_again_and_sends_nothing_of_it_until_then_sample()
            => Turning_a_refused_sample_back_on_asks_for_the_grant_again_and_sends_nothing_of_it_until_then("mcp");

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("ui")]
        [InlineData("mcp")]
        public async Task Turning_a_refused_sample_back_on_asks_for_the_grant_again_and_sends_nothing_of_it_until_then(string door)
        {
            var (e, sm, oracle, id) = await GrantedAsync("ws-on-" + door);
            using (sm) using (e)
            {
                await Edit(e, door, "set_table_sample", "Payroll", "off");
                await Ready(e);
                oracle.Clear();
                var view = await Edit(e, door, "set_table_sample", "Payroll", "on");
                Assert.False(view.Sections.SelectMany(s => s.Groups).Single(g => g.Columns != null && g.Table == "Payroll").SampleOff);
                view = await Ready(e);
                Assert.True(view.Decide?.State == StartDecideState.WaitingGrant || view.NeedsYou.Any(n => n.Kind == "grant"),
                    "turning the sample back on did not ask for the grant again: decide " + view.Decide?.State + ", needs " + string.Join(",", view.NeedsYou.Select(n => n.Kind)));
                await Edit(e, door, "rename", "column:Payroll/GrossPay", "Base Pay");
                view = await Ready(e);
                Assert.DoesNotContain(oracle.All(), CarriesRefused);
                Assert.False(CarriesRefused(System.Text.Json.JsonSerializer.Serialize(await e.SampleForJevAsync(id, Sample()))), "the cut widened before the person granted again");

                await e.GrantStartDisclosureAsync(view.Digest, "human");
                await Ready(e);
                Assert.Contains(oracle.All(), CarriesRefused);
            }
        }
    }

    [Collection("restore-root")]
    public sealed class VerifyUatJ8KeyTests : GateFixtureBase
    {
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public VerifyUatJ8KeyTests(RestoreRootFixture fixture) : base(fixture)
        {
            ConnectionRegistry.RootOverride = Path.Combine(Root, "connections");
            AgentPolicyStore.SetPreset("open", "human");
        }

        public override void Dispose()
        {
            ConnectionRegistry.RootOverride = _oldConnections;
            base.Dispose();
        }

        private sealed class NoAnswer : IDecisionOracle
        {
            public bool Available { get; set; }
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct) => Task.FromResult(DecisionAnswers.NotDecided(request, "no answer"));
        }

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };

        private static FabricSchema Shape(string shape)
        {
            var sales = new FabricTable { Schema = "dbo", Name = "Sales", KeyColumns = new[] { "Id" }, Columns = new[] { C("Id", "Int64"), C("OrderDate", "Date"), C("DateKey", "Int64"), C("Amount", "Decimal") } };
            FabricTable date; FabricForeignKey[] fks;
            switch (shape)
            {
                case "int-key":
                case "renamed":
                case "date-out":
                    date = new FabricTable { Schema = "dbo", Name = "DimDate", KeyColumns = new[] { "DateKey" }, Columns = new[] { C("DateKey", "Int64"), C("Date", "Date"), C("Month", "String") } };
                    fks = new[] { new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "DateKey", ToSchema = "dbo", ToTable = "DimDate", ToColumn = "DateKey" } };
                    break;
                case "date-key":
                    date = new FabricTable { Schema = "dbo", Name = "DimDate", KeyColumns = new[] { "Date" }, Columns = new[] { C("Date", "Date"), C("Month", "String") } };
                    fks = new[] { new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "OrderDate", ToSchema = "dbo", ToTable = "DimDate", ToColumn = "Date" } };
                    break;
                case "no-key":
                    date = new FabricTable { Schema = "dbo", Name = "DimDate", KeyColumns = Array.Empty<string>(), Columns = new[] { C("DateKey", "Int64"), C("Date", "Date"), C("Month", "String") } };
                    fks = Array.Empty<FabricForeignKey>();
                    break;
                default:   // no date table at all
                    return new FabricSchema { Server = "s.datawarehouse.fabric.microsoft.com", Database = "W", Tables = new[] { sales }, ForeignKeys = Array.Empty<FabricForeignKey>() };
            }
            return new FabricSchema { Server = "s.datawarehouse.fabric.microsoft.com", Database = "W", Tables = new[] { sales, date }, ForeignKeys = fks };
        }

        [Theory]
        [InlineData("int-key", "ui")]
        [InlineData("int-key", "mcp")]
        [InlineData("date-key", "ui")]
        [InlineData("date-key", "mcp")]
        [InlineData("no-key", "ui")]
        [InlineData("no-key", "mcp")]
        [InlineData("none", "ui")]
        [InlineData("renamed", "ui")]
        [InlineData("renamed", "mcp")]
        [InlineData("date-out", "ui")]
        public async Task The_key_the_plan_shows_is_the_key_the_build_makes_on_every_date_table_shape(string shape, string door)
        {
            using var sm = new SessionManager();
            using var e = new LocalEngine(sm, TestEntitlements.Free, Path.Combine(Root, "ws"));
            e.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(Shape(shape));
            e.UseDecisionsForTest(new DecisionService(new NoAnswer()));
            var view = await e.StartModelFromSqlAsync(new StartModelRequest { Server = "s.datawarehouse.fabric.microsoft.com", Database = "W", Folder = Path.Combine(Root, "m"), Name = "Keys" }, "human");
            await e.StartPreviewSettledForTest();
            // The date table cannot be unmarked (StartPlanEditing.ToggleShown refuses it): the person can rename its date column, or leave that column out.
            if (shape == "renamed") { await e.EditStartPlanAsync(new StartPlanEdit { Op = "rename", Target = "column:DimDate/Date", Value = "Calendar Day" }, "human"); await e.StartPreviewSettledForTest(); }
            if (shape == "date-out") { await e.EditStartPlanAsync(new StartPlanEdit { Op = "include_column", Target = "DimDate/Date", Value = "false" }, "human"); await e.StartPreviewSettledForTest(); }
            view = door == "mcp" ? await McpTools.GetStartPlan(e) : await e.GetStartPlanAsync();
            var spec = e.StartPlanDocForTest().Manifest.ToSpec();
            foreach (var t in spec.Tables)
            {
                var g = view.Sections.SelectMany(s => s.Groups).SingleOrDefault(x => x.Table == t.Name && x.Columns != null);
                Assert.True(g != null, $"{shape}: the plan has no column list for {t.Name}");
                var shown = g.Columns.Where(c => c.Included && c.Key).Select(c => c.Name).OrderBy(x => x).ToArray();
                var built = t.Columns.Where(c => c.IsKey).Select(c => c.Name).OrderBy(x => x).ToArray();
                Assert.True(built.SequenceEqual(shown), $"{shape} {door}: {t.Name} plan key [{string.Join(",", shown)}], build key [{string.Join(",", built)}]");
            }
        }
    }

    public sealed class VerifyUatJ8ExplainTests
    {
        /// <summary>DAX has no DATETIME function (live Northstar, lane build: "Failed to resolve name 'DATETIME'"). A member with a time of day must be a value
        /// the engine reads, such as DATE ( y, m, d ) + TIME ( h, m, s ).</summary>
        [Fact]
        public void A_date_member_with_a_time_of_day_is_sent_as_dax_the_engine_can_read()
        {
            var f = DaxBench.CompileMemberFilter("'Customer'[JoinDate]", new[] { "2018-06-25T13:30:05" }, dateColumn: true);
            Assert.DoesNotContain("DATETIME", f);
            // From 1900-03-01 a whole-second member is the one-division form (join 11, J-S1): the stored number, not DATE + TIME, which lands a bit off for some seconds.
            Assert.Contains("DATE ( 1899, 12, 30 ) + TIME ( 0, 0, 0 ) + 3739095005000 / 86400000", f);
        }
    }

    [Collection("restore-root")]
    public sealed class VerifyUatJ8BandTests : IDisposable
    {
        private readonly string _root, _safeRoot, _origHome;
        private readonly List<IDisposable> _owned = new List<IDisposable>();
        private const string Server = "wh-j8.datawarehouse.fabric.microsoft.com", Db = "ContosoConsulting";

        public VerifyUatJ8BandTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "vb-" + Guid.NewGuid().ToString("N").Substring(0, 6));
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

        private static Func<string, string, string, int, Task<ResultSet>> J8Rows(FabricSchema schema)
        {
            var t = typeof(StartPlanEndToEndTests).GetNestedType("J8Source", BindingFlags.NonPublic);
            var src = Activator.CreateInstance(t, schema);
            return (Func<string, string, string, int, Task<ResultSet>>)Delegate.CreateDelegate(typeof(Func<string, string, string, int, Task<ResultSet>>), src, t.GetMethod("Execute"));
        }

        /// <summary>A Pro plan Jev has looked at, approved and built with every Jev question left unanswered: what the band says of each kind is what the build did.
        /// unsure_pick: "the plan builds the rule's pick" (the measure is in the model). unsure_line and oscillation: "stays out of the build until you answer"
        /// (the line's value is not in the model).</summary>
        [Fact]
        public async Task Each_kind_of_unanswered_Jev_question_is_built_or_left_out_as_the_band_says()
        {
            AgentPolicyStore.SetPreset("open", "human");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, Path.Combine(_root, "ws"));
            _owned.Add(engine); _owned.Add(sessions);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            engine.SourceSampleExecutorForTest = J8Rows(SqlDraftBuildTests.J8Schema());
            engine.UseDecisionsForTest(new DecisionService(new StartPlanDecideTests.PolicyOracle()));
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = Path.Combine(_root, "model"), Name = "Uplift" }, "human");
            await engine.StartSampleSettledForTest();
            view = await engine.GetStartPlanAsync();
            var fy = view.NeedsYou.SingleOrDefault(x => x.Kind == "fiscal_year");
            if (fy != null) view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = "fiscal_year", Value = fy.Recommended }, "human");
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            view = await engine.GrantStartDisclosureAsync(view.Digest, "human");
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            view = await engine.GetStartPlanAsync();
            var kinds = view.NeedsYou.Select(n => n.Kind).Distinct().ToArray();
            var lines = view.NeedsYou.Where(n => n.Kind == "unsure_line" || n.Kind == "oscillation").ToList();
            var doc = engine.StartPlanDocForTest();
            var picks = view.NeedsYou.Where(n => n.Kind == "unsure_pick").Select(n => doc.Manifest.BuiltBases().Single(b => "unsure_pick:" + b.Identity == n.Id).Name).ToList();
            var lineFacts = lines.Select(n =>
            {
                var key = n.Id.Substring(n.Id.IndexOf(':') + 1);
                var l = doc.Lines.Single(x => string.Equals(StartPlanDecide.LineKey(x), key, StringComparison.OrdinalIgnoreCase));
                return (Need: n, Line: l);
            }).ToList();
            Assert.True(lineFacts.Count > 0, "the J8 plan asked no unsure line, so the left-out half is not proven: " + string.Join(",", kinds));
            Assert.All(lineFacts, x => Assert.False(x.Line.Kept, "an unsure line the band says stays out is kept: " + x.Line.Item.Title));

            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True((await engine.GetStartPlanAsync()).Built, "the build did not finish");

            await sessions.Current.ReadAsync(m =>
            {
                foreach (var name in picks) Assert.True(m.AllMeasures.Any(x => x.Name == name), "the band says the plan builds the rule's pick, and the model has no " + name);
                foreach (var (need, line) in lineFacts)
                {
                    var obj = (TabularEditor.TOMWrapper.ITabularNamedObject)m.AllMeasures.FirstOrDefault(x => string.Equals(ObjectRefs.For(x), line.Item.ObjectRef, StringComparison.OrdinalIgnoreCase))
                              ?? m.AllColumns.FirstOrDefault(c => string.Equals(ObjectRefs.For(c), line.Item.ObjectRef, StringComparison.OrdinalIgnoreCase));
                    if (obj == null) continue;   // the object itself is not built, so neither is the line
                    var format = obj is TabularEditor.TOMWrapper.Measure ms ? ms.FormatString : ((TabularEditor.TOMWrapper.Column)obj).FormatString;
                    if (LocalEngine.IsJevFormatLine(line.Item)) Assert.True(format != line.Item.After, $"{need.Kind} '{need.Text}': the band says it stays out, and the model has its format {format}");
                }
                return true;
            });
        }
    }
}
