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
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// M11 uat7-links, the whole start on the UAT source shape (five tables, no declared key), through BOTH doors. The UI door is the person's own presses
    /// (human origin); the MCP door is the assistant's tool methods (agent origin). Free is the point of the lane; Pro falls back to the same rule.
    /// The source comes through the offline schema seam, so the plan is made from names, and a test that wants rows hands the plan a sample directly.
    /// </summary>
    [Collection("restore-root")]
    public sealed class StartLinkGuessEngineTests : GateFixtureBase
    {
        private const string Server = "ns.datawarehouse.fabric.microsoft.com", Db = "Northstar Source";
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public StartLinkGuessEngineTests(RestoreRootFixture fixture) : base(fixture)
        {
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
            public bool Available { get; set; }
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct) => Task.FromResult(DecisionAnswers.NotDecided(request, "no answer"));
        }

        private LocalEngine Engine(SessionManager sm, bool pro, FabricSchema schema = null)
        {
            var e = new LocalEngine(sm, pro ? TestEntitlements.Pro : TestEntitlements.Free, Path.Combine(Root, "ws"));
            schema ??= StartLinkGuessTests.Northstar();
            schema.Server = Server; schema.Database = Db;
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(schema);
            e.UseDecisionsForTest(new DecisionService(new Oracle()));
            return e;
        }

        private Task<StartPlanView> Start(LocalEngine e, string door) =>
            door == "mcp"
                ? McpTools.StartModelFromSql(e, Server, Db, Path.Combine(Root, "m"), "Northstar")
                : e.StartModelFromSqlAsync(new StartModelRequest { Name = "Northstar", Folder = Path.Combine(Root, "m"), Server = Server, Database = Db }, "human");

        private static async Task<StartPlanView> Ready(LocalEngine e)
        {
            await e.StartDecideSettledForTest(); await e.StartPreviewSettledForTest();
            await e.GetStartPlanAsync();                 // a read of the plan makes a preview that is missing (the sample's own step does it in production)
            await e.StartPreviewSettledForTest();
            return await e.GetStartPlanAsync();
        }

        private static IEnumerable<StartLine> LinesOf(StartPlanView v) => v.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Lines);
        private static List<StartLine> LinkLines(StartPlanView v) => LinesOf(v).Where(l => l.Item.Kind == "create_relationship").ToList();

        private static Task<StartPlanBuildResult> Build(LocalEngine e, string digest, string door) =>
            door == "mcp" ? McpTools.BuildStartPlan(e, digest) : e.BuildStartPlanAsync(digest, "human");

        private static Task<StartPlanView> Edit(LocalEngine e, string door, string op, string target, string value = null) =>
            door == "mcp" ? McpTools.EditStartPlan(e, op, target, value) : e.EditStartPlanAsync(new StartPlanEdit { Op = op, Target = target, Value = value }, "human");

        private static SourceSampleResult Sample(bool customerKeyUnique, bool withAddress = false)
        {
            SourceTableFacts Table(string name, params (string col, bool unique)[] cols) => new SourceTableFacts
            {
                Schema = "dbo", Name = name, Rows = 100,
                Columns = cols.Select(c => new SourceColumnFacts { Source = c.col, UniqueOnEveryRow = c.unique, Distinct = c.unique ? 100 : 10 }).ToArray(),
            };
            return new SourceSampleResult
            {
                Server = Server, Database = Db, MeasuredAtUtc = DateTime.UtcNow.ToString("O"),
                Tables = new[]
                {
                    Table("Customer", ("CustomerKey", customerKeyUnique)), Table("Date", ("DateKey", true)), Table("Product", ("ProductKey", true)),
                    Table("Sales", ("SalesLineKey", true), ("CustomerKey", false), ("ProductKey", false), ("StoreKey", false), ("OrderDateKey", false)),
                    Table("Store", ("StoreKey", true)),
                }.Concat(withAddress ? new[] { Table("CustomerAddress", ("CustomerKey", true)) } : new SourceTableFacts[0]).ToArray(),
            };
        }

        // ================================================================== the Free start finds the links, the fact table and the measures

        [Theory]
        [InlineData("ui")]
        [InlineData("mcp")]
        public async Task Free_start_on_a_source_with_no_keys_shows_four_guessed_links_a_fact_table_and_measures(string door)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, door);
            var view = await Ready(e);
            var links = LinkLines(view);
            Assert.Equal(4, links.Count);
            Assert.All(links, l => Assert.True(l.Kept));
            var customer = links.Single(l => l.Item.Title.Contains("Customer[CustomerKey]"));
            Assert.StartsWith("Linked Sales[CustomerKey] to Customer[CustomerKey]: same name, and CustomerKey is named like the key of Customer", customer.Item.Title);
            // Each guessed line has its own id, so a press on one line is a press on that line.
            Assert.All(links, l => Assert.False(string.IsNullOrEmpty(l.Item.Id)));
            Assert.Equal(4, links.Select(l => l.Item.Id).Distinct().Count());
            // The measures the existing Free rules make for a fact table.
            var measures = LinesOf(view).Where(l => l.Item.Kind == "create_measure").Select(l => l.Item.ObjectName).ToList();
            Assert.Contains("Total NetSalesAmount", measures);
            Assert.Contains("Total NetSalesAmount YTD", measures);
            Assert.Contains(LinesOf(view), l => l.Item.Kind == "mark_date_table" && l.Item.ObjectName == "Date");
        }

        [Theory]
        [InlineData("ui")]
        [InlineData("mcp")]
        public async Task The_review_equals_the_build_for_a_plan_with_guessed_links(string door)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, door);
            var view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            var reviewedLinks = LinkLines(view).Where(l => l.Kept).Select(l => l.Item.ObjectName).Count();
            var built = await Build(e, view.Digest, door);
            Assert.True(built.Built, built.Refused);
            var model = await sm.Current.ReadAsync(m => new
            {
                Relationships = m.Relationships.Select(r => r.FromTable.Name + "." + r.FromColumn.Name + ">" + r.ToTable.Name + "." + r.ToColumn.Name).OrderBy(x => x).ToList(),
                Measures = m.AllMeasures.Select(x => x.Name).ToList(),
                Active = m.Relationships.OfType<SingleColumnRelationship>().Count(r => r.IsActive),
            });
            Assert.Equal(new[] { "Sales.CustomerKey>Customer.CustomerKey", "Sales.OrderDateKey>Date.DateKey", "Sales.ProductKey>Product.ProductKey", "Sales.StoreKey>Store.StoreKey" }, model.Relationships);
            Assert.Equal(reviewedLinks, model.Active);
            Assert.Contains("Total NetSalesAmount", model.Measures);
        }

        [Theory]
        [InlineData("ui")]
        [InlineData("mcp")]
        public async Task A_dropped_guess_is_not_built_and_the_plan_recomputes(string door)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, door);
            var view = await Ready(e);
            var customer = LinkLines(view).Single(l => l.Item.Title.Contains("Customer[CustomerKey]"));
            // The UI sends the line's own id; the assistant names the link by its ref. Both are the same edit.
            var target = door == "mcp" ? customer.Item.ObjectRef : customer.Item.Id;
            view = await Edit(e, door, "drop_line", target);
            var after = LinkLines(view).Single(l => l.Item.ObjectRef == customer.Item.ObjectRef);
            Assert.False(after.Kept);
            Assert.Equal(door == "mcp" ? "assistant" : "person", after.DecidedBy);
            Assert.Equal(3, LinkLines(view).Count(l => l.Kept));

            view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            var built = await Build(e, view.Digest, door);
            Assert.True(built.Built, built.Refused);
            var rels = await sm.Current.ReadAsync(m => m.Relationships.Select(r => r.ToTable.Name).OrderBy(x => x).ToList());
            Assert.Equal(new[] { "Date", "Product", "Store" }, rels);
        }

        [Fact]
        public async Task Dropping_every_guess_leaves_no_fact_table_and_no_measures_in_the_plan()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, "ui");
            var view = await Ready(e);
            foreach (var l in LinkLines(view)) view = await Edit(e, "ui", "drop_line", l.Item.Id);
            Assert.Empty(LinesOf(view).Where(l => l.Item.Kind == "create_measure" && l.Kept));
            Assert.Empty(LinkLines(view).Where(l => l.Kept));
        }

        [Theory]
        [InlineData("ui")]
        [InlineData("mcp")]
        public async Task An_ambiguous_guess_builds_the_loser_switched_off_and_the_review_still_equals_the_build(string door)
        {
            var s = StartLinkGuessTests.Northstar();
            s.Tables = s.Tables.Concat(new[]
            {
                new FabricTable { Schema = "dbo", Name = "CustomerAddress", Columns = new[] { new FabricColumn { Name = "CustomerKey", DataType = "Int64" }, new FabricColumn { Name = "Street", DataType = "String" } } },
            }).ToArray();
            using var sm = new SessionManager(); using var e = Engine(sm, false, s);
            var started = await Start(e, door);
            await Ready(e);
            e.SetStartPlanSample(started.Id, Sample(customerKeyUnique: true, withAddress: true));   // the rows show both tables keyed on CustomerKey
            var view = await Ready(e);
            var loser = LinkLines(view).Single(l => l.Item.Title.Contains("CustomerAddress[CustomerKey]") && l.Item.Title.Contains("Sales["));
            Assert.False(loser.Kept);
            Assert.Contains("(off)", loser.Item.Title);
            Assert.Contains("Off because Sales[CustomerKey] matches both Customer and CustomerAddress", loser.Why);
            view = await e.ApproveStartPlanAsync(view.Digest);
            var built = await Build(e, view.Digest, door);
            Assert.True(built.Built, built.Refused);   // the build's own check: nothing written that the review did not list
            var off = await sm.Current.ReadAsync(m => m.Relationships.OfType<SingleColumnRelationship>().Where(r => !r.IsActive)
                .Select(r => r.FromTable.Name + "." + r.FromColumn.Name + ">" + r.ToTable.Name).ToList());
            Assert.Equal(new[] { "Sales.CustomerKey>CustomerAddress" }, off);
        }

        // ================================================================== a declared key always wins, on the engine path too

        [Fact]
        public async Task A_source_that_declares_its_keys_gets_no_guessed_line()
        {
            var s = StartLinkGuessTests.Northstar(
                new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "CustomerKey", ToSchema = "dbo", ToTable = "Customer", ToColumn = "CustomerKey" },
                new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "ProductKey", ToSchema = "dbo", ToTable = "Product", ToColumn = "ProductKey" },
                new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "StoreKey", ToSchema = "dbo", ToTable = "Store", ToColumn = "StoreKey" },
                new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "OrderDateKey", ToSchema = "dbo", ToTable = "Date", ToColumn = "DateKey" });
            using var sm = new SessionManager(); using var e = Engine(sm, false, s);
            await Start(e, "ui");
            var view = await Ready(e);
            var links = LinkLines(view);
            Assert.Equal(4, links.Count);
            Assert.All(links, l => Assert.DoesNotContain("Linked ", l.Item.Title));
            Assert.All(links, l => Assert.Equal("The source declares this link.", l.Why));
        }

        // ================================================================== a sample that lands after the plan

        [Fact]
        public async Task A_sample_that_shows_a_key_repeating_takes_that_link_out_and_voids_an_approval()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            var started = await Start(e, "ui");
            var view = await Ready(e);
            Assert.Equal(4, LinkLines(view).Count);
            view = await e.ApproveStartPlanAsync(view.Digest);
            Assert.NotNull(view.Approval);

            e.SetStartPlanSample(started.Id, Sample(customerKeyUnique: false));
            view = await Ready(e);
            Assert.Null(view.Approval);   // the plan moved, so the approval for the old one is void
            Assert.Equal(3, LinkLines(view).Count);
            Assert.DoesNotContain(LinkLines(view), l => l.Item.Title.Contains("Customer["));
        }

        [Fact]
        public async Task A_sample_that_shows_the_keys_unique_changes_the_words_to_from_the_sample()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            var started = await Start(e, "ui");
            await Ready(e);
            e.SetStartPlanSample(started.Id, Sample(customerKeyUnique: true));
            var view = await Ready(e);
            var customer = LinkLines(view).Single(l => l.Item.Title.Contains("Customer[CustomerKey]"));
            Assert.Equal("Linked Sales[CustomerKey] to Customer[CustomerKey]: same name, and CustomerKey is unique in Customer (from the sample)", customer.Item.Title);
        }

        // ================================================================== Pro keeps the rule when Jev has nothing to say

        [Fact]
        public async Task Pro_with_no_answer_from_Jev_falls_back_to_the_same_links()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, true);
            await Start(e, "ui");
            var view = await Ready(e);
            Assert.Equal(4, LinkLines(view).Count(l => l.Kept));
        }
    }
}
