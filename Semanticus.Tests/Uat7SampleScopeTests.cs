using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Facts;
using Semanticus.Engine.StartPlan;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane m11/uat7-engine, item 4 (Sol UAT R-A2, open and not proven). Payroll (Sol's Store) is left out of the person's grant, but the sample read at the
    /// start still holds its facts beside the model, and get_model_facts, which is the person's and the assistant's own LOCAL read and not Jev, serves them
    /// once the table is added. That is allowed only if no Jev request can carry them. A recording oracle with Jev on holds every request whole (the point,
    /// the facts snapshot and every question) and each path that asks Jev (the start plan's decide round, the Plan's runs and the families under it) is
    /// driven; none of them may carry a number that came from the left-out table's sample, while the granted table's numbers prove the recorder sees facts.
    /// The numbers are unlike each other and unlike any count the fixture model has, so a match is the sample and not a coincidence.
    /// </summary>
    [Collection("restore-root")]
    public sealed class Uat7SampleScopeTests : GateFixtureBase
    {
        private const string Server = "probe.datawarehouse.fabric.microsoft.com", Db = "Warehouse";
        private const string GrantedRows = "424242", GrantedDistinct = "3131";
        private const string LeftOutRows = "987654", LeftOutDistinct = "7531", LeftOutBlank = "0.2468";
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        private readonly ITestOutputHelper _out;

        public Uat7SampleScopeTests(RestoreRootFixture fixture, ITestOutputHelper output) : base(fixture)
        {
            _out = output;
            ConnectionRegistry.RootOverride = Path.Combine(Root, "connections");
            AgentPolicyStore.SetPreset("open", "human");
        }

        public override void Dispose()
        {
            ConnectionRegistry.RootOverride = _oldConnections;
            base.Dispose();
        }

        private sealed class Oracle : IDecisionOracle
        {
            public bool Available { get; set; } = true;
            public readonly List<string> Requests = new List<string>();
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                string whole;
                try { whole = JsonSerializer.Serialize(request); }
                catch { whole = JsonSerializer.Serialize(new { request.PointName, request.Snapshot }); }
                lock (Requests) Requests.Add(request.PointName + " " + whole);
                return Task.FromResult(DecisionAnswers.NotDecided(request, "recording oracle"));
            }
            public string[] All() { lock (Requests) return Requests.ToArray(); }
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

        private static SourceSampleResult Sample() => new SourceSampleResult
        {
            Server = Server, Database = Db, MeasuredAtUtc = DateTime.UtcNow.ToString("O"),
            Tables = new[]
            {
                new SourceTableFacts { Schema = "dbo", Name = "Sales", Rows = 424242, Columns = new[] { new SourceColumnFacts { Source = "Amount", Distinct = 3131, BlankShare = 0.1414 } } },
                new SourceTableFacts { Schema = "dbo", Name = "Payroll", Rows = 987654, Columns = new[] { new SourceColumnFacts { Source = "GrossPay", Distinct = 7531, BlankShare = 0.2468 } } },
            },
        };

        private LocalEngine Engine(SessionManager sm, Oracle oracle)
        {
            var e = new LocalEngine(sm, TestEntitlements.Pro, Path.Combine(Root, "ws"));
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(Schema());
            e.UseDecisionsForTest(new DecisionService(oracle));
            return e;
        }

        private static async Task<StartPlanView> Ready(LocalEngine e)
        {
            await e.StartDecideSettledForTest(); await e.StartPreviewSettledForTest();
            return await e.GetStartPlanAsync();
        }

        /// <summary>Jev permission for the model, then a start plan with Payroll left out, and the person's grant for that plan.</summary>
        private async Task<StartPlanView> GrantWithPayrollLeftOutAsync(LocalEngine e)
        {
            await e.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            await e.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "Payroll", Value = "false" }, "human");
            var view = await Ready(e);
            await e.GrantStartDisclosureAsync(view.Digest, "human");
            return await Ready(e);
        }

        private static bool CarriesLeftOut(string request) =>
            request.Contains(LeftOutRows) || request.Contains(LeftOutDistinct) || request.Contains(LeftOutBlank);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_table_added_back_after_the_grant_is_served_to_the_person_but_reaches_no_Jev_request_on_the_Plan_and_family_paths()
        {
            var oracle = new Oracle();
            using var sm = new SessionManager(); using var e = Engine(sm, oracle);
            await e.StartModelFromSqlAsync(new StartModelRequest { Name = "Model", Folder = Path.Combine(Root, "model"), Server = Server, Database = Db }, "human");
            e.SetStartPlanSample(e.StartPlanDocForTest().Id, Sample());
            var view = await GrantWithPayrollLeftOutAsync(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            var built = await e.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            await e.CreateImportTableAsync("Payroll", "let s = Sql.Database(\"" + Server + "\",\"" + Db + "\"), t = s{[Schema=\"dbo\",Item=\"Payroll\"]}[Data] in t", "human");
            await e.CreateColumnAsync("table:Payroll", "GrossPay", "Decimal", "GrossPay", "human");
            lock (oracle.Requests) oracle.Requests.Clear();

            // The local read, as the assistant makes it, serves the left-out table's facts: that is allowed, and it primes the snapshot cache at this revision.
            var local = await e.GetModelFactsAsync("agent");
            var payroll = local.Tables.Single(t => t.Name == "Payroll");
            Assert.Equal(987654, payroll.Rows);

            // Every Jev path that reads the model's facts, with Jev on.
            await e.GetModelPlanAsync(); await e.JevSettledForTest();
            await e.GetWordsQueueAsync();
            await e.GetModelPlanAsync(); await e.JevSettledForTest();

            var all = oracle.All();
            foreach (var r in all) _out.WriteLine(r.Substring(0, Math.Min(r.IndexOf(' '), 60)) + " granted=" + (r.Contains(GrantedRows) || r.Contains(GrantedDistinct)) + " leftOut=" + CarriesLeftOut(r) + " len=" + r.Length);
            Assert.NotEmpty(all);
            Assert.DoesNotContain(all, CarriesLeftOut);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_table_put_back_into_the_start_plan_after_the_grant_reaches_no_Jev_decide_request()
        {
            var oracle = new Oracle();
            using var sm = new SessionManager(); using var e = Engine(sm, oracle);
            await e.StartModelFromSqlAsync(new StartModelRequest { Name = "Model", Folder = Path.Combine(Root, "model"), Server = Server, Database = Db }, "human");
            e.SetStartPlanSample(e.StartPlanDocForTest().Id, Sample());
            await GrantWithPayrollLeftOutAsync(e);
            var granted = oracle.All();
            // The positive control: the table the person did grant does put its sample facts into what Jev is asked, so the recorder is not blind to them.
            Assert.Contains(granted, r => r.Contains(GrantedRows) || r.Contains(GrantedDistinct));
            Assert.DoesNotContain(granted, CarriesLeftOut);

            // The person puts Payroll back after the grant: the plan changes, a new decide round runs, and the grant no longer covers it.
            await e.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "Payroll", Value = "true" }, "human");
            await Ready(e);
            Assert.DoesNotContain(oracle.All(), CarriesLeftOut);
        }

        [Fact]
        public async Task The_sample_cut_for_Jev_never_holds_the_left_out_tables_numbers()
        {
            var oracle = new Oracle();
            using var sm = new SessionManager(); using var e = Engine(sm, oracle);
            await e.StartModelFromSqlAsync(new StartModelRequest { Name = "Model", Folder = Path.Combine(Root, "model"), Server = Server, Database = Db }, "human");
            var id = e.StartPlanDocForTest().Id;
            e.SetStartPlanSample(id, Sample());
            await GrantWithPayrollLeftOutAsync(e);
            var cut = await e.SampleForJevAsync(id, Sample());
            Assert.NotNull(cut);
            Assert.False(CarriesLeftOut(JsonSerializer.Serialize(cut)), "the sample cut for Jev holds a number from the table the grant left out");
            Assert.True(JsonSerializer.Serialize(cut).Contains(GrantedRows), "the granted table's numbers are in the cut");
        }
    }
}
