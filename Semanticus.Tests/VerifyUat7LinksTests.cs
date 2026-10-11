using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Verifier, M11 uat7-links (links guessed from names). Attacks by CLASS, not only the named case: tricky names (CustomerKey and CustomerKeyOld, Id in many
    /// tables, a fact with two date keys, a self reference, plural and separator variants), a sample that lands after the plan, a declared source that must not move,
    /// and the review against the build on a tricky shape through both doors. A test here that fails is a defect the verifier found; it is left failing.
    /// </summary>
    public sealed class VerifyUat7LinksTests
    {
        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };
        private static FabricTable T(string name, params FabricColumn[] cols) => new FabricTable { Schema = "dbo", Name = name, KeyColumns = new string[0], Columns = cols };
        private static FabricSchema S(params FabricTable[] tables) => new FabricSchema { Server = "x", Database = "DW", Tables = tables };
        private static StructureManifest Derive(FabricSchema s, LinkGuessFacts f) => StructureManifest.Derive(s, null, null, "seed", "x", "DW", "import", f);
        private static string Key(ManifestLink l) => l.FromTable + "." + l.FromColumns[0] + ">" + l.ToTable + "." + l.ToColumns[0];
        private static string[] Active(StructureManifest m) => m.Links.Where(l => l.Active && !l.Dropped).Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        private static string[] All(StructureManifest m) => m.Links.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        private static LinkGuessFacts Unique(LinkGuessFacts f, string table, params (string col, bool unique)[] cols) =>
            f.Measured("dbo." + table, cols.ToDictionary(c => c.col, c => c.unique));

        // ================================================================== CustomerKey and CustomerKeyOld

        [Fact]
        public void A_column_that_only_starts_with_a_key_name_is_not_linked_with_or_without_rows()
        {
            var s = S(T("Customer", C("CustomerKey", "Int64"), C("CustomerKeyOld", "Int64"), C("Name", "String")),
                      T("Sales", C("CustomerKey", "Int64"), C("CustomerKeyOld", "Int64"), C("Amount", "Decimal")));
            var names = Derive(s, LinkGuessFacts.NamesOnly());
            Assert.Equal(new[] { "Sales.CustomerKey>Customer.CustomerKey" }, All(names));
            var rows = Derive(s, Unique(LinkGuessFacts.NamesOnly(), "Customer", ("CustomerKey", true), ("CustomerKeyOld", true)));
            Assert.Equal(new[] { "Sales.CustomerKey>Customer.CustomerKey" }, All(rows));
        }

        // ================================================================== Id in many tables

        [Fact]
        public void Id_in_every_table_links_only_the_table_then_Id_columns_and_never_Id_to_Id()
        {
            var s = S(T("Customers", C("Id", "Int64"), C("Name", "String")),
                      T("Categories", C("Id", "Int64"), C("Name", "String")),
                      T("Products", C("Id", "Int64"), C("Name", "String"), C("CategoryId", "Int64")),
                      T("Orders", C("Id", "Int64"), C("CustomerId", "Int64"), C("ProductId", "Int64"), C("Amount", "Decimal")));
            var expected = new[] { "Orders.CustomerId>Customers.Id", "Orders.ProductId>Products.Id", "Products.CategoryId>Categories.Id" };
            Assert.Equal(expected, All(Derive(s, LinkGuessFacts.NamesOnly())));
            var f = LinkGuessFacts.NamesOnly();
            foreach (var t in new[] { "Customers", "Categories", "Products", "Orders" }) Unique(f, t, ("Id", true), ("Name", t != "Orders"));
            var m = Derive(s, f);
            Assert.Equal(expected, All(m));
            Assert.Equal("fact", m.Tables.Single(t => t.Name == "Orders").Role);
            Assert.Contains(m.BaseMeasures, b => b.Table == "Orders" && b.Column == "Amount");
            Assert.DoesNotContain(m.BaseMeasures, b => b.Column.EndsWith("Id", StringComparison.Ordinal));
        }

        // ================================================================== a fact with two date keys

        [Fact]
        public void Two_date_keys_keep_one_on_and_the_other_off_with_the_rule_s_reason_and_the_time_measures_use_the_one_on()
        {
            var s = S(T("Date", C("DateKey", "Int64"), C("Date", "DateTime"), C("Year", "Int64")),
                      T("Sales", C("OrderDateKey", "Int64"), C("ShipDateKey", "Int64"), C("Amount", "Decimal")));
            var m = Derive(s, Unique(LinkGuessFacts.NamesOnly(), "Date", ("DateKey", true), ("Date", true), ("Year", false)));
            Assert.Equal(new[] { "Sales.OrderDateKey>Date.DateKey", "Sales.ShipDateKey>Date.DateKey" }, All(m));
            Assert.Equal(new[] { "Sales.OrderDateKey>Date.DateKey" }, Active(m));
            var off = m.Links.Single(l => l.FromColumns[0] == "ShipDateKey");
            Assert.Contains("Only one link between two tables can be on", off.Note);
            Assert.Equal("Date", m.DateTable.Name);
            Assert.NotEmpty(m.TimeMeasures());
            // The review line for the off one says it is off.
            var doc = new StartPlanDoc { Id = "sp", Manifest = m, IdentitySeed = "seed" };
            var line = StartPlanViewBuilder.Build(doc).Sections.SelectMany(x => x.Groups).SelectMany(g => g.Lines).Single(l => l.Item.ObjectRef == "relationship:" + off.Identity);
            Assert.False(line.Kept);
            Assert.Contains("(off)", line.Item.Title);
        }

        // ================================================================== a self reference

        [Fact]
        public void A_self_reference_is_never_guessed_and_does_not_change_the_table_role()
        {
            var s = S(T("Employee", C("EmployeeKey", "Int64"), C("ParentEmployeeKey", "Int64"), C("ManagerEmployeeKey", "Int64"), C("Name", "String")),
                      T("Sales", C("EmployeeKey", "Int64"), C("Amount", "Decimal")));
            var m = Derive(s, Unique(LinkGuessFacts.NamesOnly(), "Employee", ("EmployeeKey", true), ("ParentEmployeeKey", false)));
            Assert.Equal(new[] { "Sales.EmployeeKey>Employee.EmployeeKey" }, All(m));
            Assert.Equal("dimension", m.Tables.Single(t => t.Name == "Employee").Role);
            Assert.Equal("fact", m.Tables.Single(t => t.Name == "Sales").Role);
        }

        // ================================================================== plural and separator variants

        [Fact]
        public void Plural_prefixed_and_separated_names_link_and_a_type_mismatch_does_not()
        {
            var s = S(new FabricTable { Schema = "dbo", Name = "dim_customers", KeyColumns = new string[0], Columns = new[] { C("customer_id", "Int64"), C("name", "String") } },
                      new FabricTable { Schema = "dbo", Name = "Categories", KeyColumns = new string[0], Columns = new[] { C("CategoryID", "String"), C("Label", "String") } },
                      new FabricTable { Schema = "dbo", Name = "fact_sales", KeyColumns = new string[0], Columns = new[] { C("Customer_ID", "Int64"), C("category_id", "Int64"), C("sales_amount", "Decimal") } });
            var m = Derive(s, LinkGuessFacts.NamesOnly());
            // CategoryID is text in Categories and a whole number in fact_sales: not linked.
            Assert.Equal(new[] { "fact_sales.Customer_ID>dim_customers.customer_id" }, All(m));
        }

        // ================================================================== an exact name loses to a role only because of column order

        [Fact]
        public void An_exact_key_name_is_the_link_kept_on_when_a_role_column_comes_first()
        {
            // Sales lists OldCustomerKey before CustomerKey. Both point at Customer: the exact name is the plain link and the role is the twin.
            var s = S(T("Customer", C("CustomerKey", "Int64"), C("Name", "String")),
                      T("Sales", C("OldCustomerKey", "Int64"), C("CustomerKey", "Int64"), C("Amount", "Decimal")));
            var m = Derive(s, Unique(LinkGuessFacts.NamesOnly(), "Customer", ("CustomerKey", true)));
            Assert.Equal(new[] { "Sales.CustomerKey>Customer.CustomerKey" }, Active(m));
        }

        // ================================================================== the sample lands and changes which table won

        [Fact]
        public void When_the_sample_rules_out_the_winner_the_other_table_s_link_is_not_left_off_with_a_reason_about_a_link_that_is_gone()
        {
            var s = S(T("DimProduct", C("ProductKey", "Int64"), C("Name", "String")),
                      T("Product", C("ProductKey", "Int64"), C("Label", "String")),
                      T("Sales", C("ProductKey", "Int64"), C("Amount", "Decimal")));
            var m = Derive(s, LinkGuessFacts.NamesOnly());
            Assert.Equal(new[] { "Sales.ProductKey>DimProduct.ProductKey" }, Active(m));   // names alone: both answer, the first is kept on
            var doc = new StartPlanDoc { Id = "sp", Manifest = m, IdentitySeed = "seed", Schema = s };
            var facts = Unique(Unique(LinkGuessFacts.NamesOnly(), "DimProduct", ("ProductKey", false)), "Product", ("ProductKey", true));
            Assert.True(LinkGuesser.Reconsider(doc, facts));
            var left = Assert.Single(m.Links);
            Assert.Equal("Sales.ProductKey>Product.ProductKey", Key(left));
            // Nothing else points at Product now, so the link stands on, or at the least it does not say it was kept off for DimProduct.
            Assert.True(left.Active, left.Note);
            Assert.DoesNotContain("DimProduct", left.Note ?? "");
        }

        // ================================================================== codes shared by two dimensions

        [Fact]
        public void A_code_two_dimensions_share_never_stands_on_beside_the_fact_s_own_links()
        {
            var s = S(T("Customer", C("CustomerKey", "Int64"), C("PostalCode", "String")),
                      T("Store", C("StoreKey", "Int64"), C("PostalCode", "String")),
                      T("Sales", C("CustomerKey", "Int64"), C("StoreKey", "Int64"), C("Amount", "Decimal")));
            var f = Unique(Unique(LinkGuessFacts.NamesOnly(), "Customer", ("CustomerKey", true), ("PostalCode", true)), "Store", ("StoreKey", true), ("PostalCode", true));
            var m = Derive(s, f);
            Assert.Contains("Sales.CustomerKey>Customer.CustomerKey", Active(m));
            Assert.Contains("Sales.StoreKey>Store.StoreKey", Active(m));
            // Any guess between Customer and Store is shown, but off: on, it would give Sales a second path, and two ways round is never on at once.
            Assert.DoesNotContain(Active(m), k => k.StartsWith("Customer.", StringComparison.Ordinal) || k.StartsWith("Store.", StringComparison.Ordinal));
            Assert.Equal(m.Links.Count(l => l.Active && !l.Dropped), m.ToSpec().Relationships.Count(r => r.IsActive != false));
        }

        // ================================================================== a declared source does not move when the sample lands

        [Fact]
        public void A_sample_landing_on_a_fully_declared_source_changes_nothing()
        {
            Func<FabricSchema> make = () =>
            {
                var s = S(T("Customer", C("CustomerKey", "Int64"), C("Name", "String")),
                          T("Date", C("DateKey", "Int64"), C("Date", "DateTime")),
                          T("Sales", C("CustomerKey", "Int64"), C("OrderDateKey", "Int64"), C("ShipDateKey", "Int64"), C("Amount", "Decimal"), C("Qty", "Int64")),
                          T("Returns", C("CustomerKey", "Int64"), C("Amount", "Decimal")));
                s.ForeignKeys = new[]
                {
                    new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "CustomerKey", ToSchema = "dbo", ToTable = "Customer", ToColumn = "CustomerKey" },
                    new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "OrderDateKey", ToSchema = "dbo", ToTable = "Date", ToColumn = "DateKey" },
                    new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "ShipDateKey", ToSchema = "dbo", ToTable = "Date", ToColumn = "DateKey" },
                    new FabricForeignKey { FromSchema = "dbo", FromTable = "Returns", FromColumn = "CustomerKey", ToSchema = "dbo", ToTable = "Customer", ToColumn = "CustomerKey" },
                };
                return s;
            };
            var m = Derive(make(), LinkGuessFacts.NamesOnly());
            var before = System.Text.Json.JsonSerializer.Serialize(m);
            var doc = new StartPlanDoc { Id = "sp", Manifest = m, IdentitySeed = "seed", Schema = make() };
            var f = Unique(Unique(LinkGuessFacts.NamesOnly(), "Customer", ("CustomerKey", true)), "Date", ("DateKey", true), ("Date", true));
            Assert.False(LinkGuesser.Reconsider(doc, f));
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(m));
            Assert.DoesNotContain(m.Links, l => l.Guessed);
        }

        // ================================================================== a rename, a drop, then the sample

        [Fact]
        public void A_drop_survives_a_table_rename_and_a_later_sample_with_no_second_copy_of_the_link()
        {
            var s = StartLinkGuessTests.Northstar();
            var m = Derive(s, LinkGuessFacts.NamesOnly());
            var doc = new StartPlanDoc { Id = "sp", Manifest = m, IdentitySeed = "seed", Schema = StartLinkGuessTests.Northstar() };
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "rename", Target = "table:Customer", Value = "Clients" }, "human").Ok);
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "rename", Target = "column:Clients/CustomerKey", Value = "Client Key" }, "human").Ok);
            var link = m.Links.Single(l => l.ToTable == "Clients");
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = "relationship:" + link.Identity }, "human").Ok);
            LinkGuesser.Reconsider(doc, StartLinkGuessTests.NorthstarSample());
            var toClients = m.Links.Where(l => l.ToTable == "Clients").ToList();
            Assert.Single(toClients);
            Assert.True(toClients[0].Dropped);
            Assert.DoesNotContain(m.ToSpec().Relationships, r => r.ToTable == "Clients");
        }

        // ================================================================== no wrong number: a dropped guess takes its measures' time variants with it

        [Fact]
        public void Dropping_the_date_guess_takes_the_time_measures_away_and_keeping_it_brings_them_back()
        {
            var m = Derive(StartLinkGuessTests.Northstar(), StartLinkGuessTests.NorthstarSample());
            var doc = new StartPlanDoc { Id = "sp", Manifest = m, IdentitySeed = "seed" };
            var date = m.Links.Single(l => l.ToTable == "Date");
            var timeBefore = m.TimeMeasures().Count();
            Assert.True(timeBefore > 0);
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = "relationship:" + date.Identity }, "human").Ok);
            Assert.Empty(m.TimeMeasures());
            Assert.DoesNotContain(m.ToSpec().Relationships, r => r.ToTable == "Date");
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "keep_line", Target = "relationship:" + date.Identity }, "human").Ok);
            Assert.Equal(timeBefore, m.TimeMeasures().Count());
        }
    }

    /// <summary>A real warehouse shape (the J8 tables) with the keys taken away, the way a Fabric warehouse arrives: every link guessed must be one the source's own keys
    /// would have declared, so no wrong link stands, and a fact table stays a fact table.</summary>
    public sealed class VerifyUat7LinksJ8Tests
    {
        private static string Key(ManifestLink l) => l.FromTable + "." + l.FromColumns[0] + ">" + l.ToTable + "." + l.ToColumns[0];
        private static readonly HashSet<string> Declared = new HashSet<string>(SqlDraftBuildTests.J8Schema().ForeignKeys.Select(f => f.FromTable + "." + f.FromColumn + ">" + f.ToTable + "." + f.ToColumn));

        private static FabricSchema Stripped(bool keepPrimaryKeys)
        {
            var s = SqlDraftBuildTests.J8Schema();
            s.ForeignKeys = new FabricForeignKey[0];
            if (!keepPrimaryKeys) foreach (var t in s.Tables) t.KeyColumns = new string[0];
            return s;
        }

        // What the J8 generator's rows show: each dimension's own id is unique; a fact's links repeat; ref_codes' CODE is unique.
        private static LinkGuessFacts J8Sample(bool tinyInvoices = false) => LinkGuessFacts.NamesOnly()
            .Measured("dbo.ref_codes", new Dictionary<string, bool> { ["CODE_TYPE"] = false, ["CODE"] = true, ["CODE_DESC"] = true })
            .Measured("dbo.dim_date", new Dictionary<string, bool> { ["DATE_KEY"] = true, ["CAL_DT"] = true, ["FY_NO"] = false })
            .Measured("dbo.dim_client", new Dictionary<string, bool> { ["CLNT_ID"] = true, ["CLNT_NM"] = true, ["IND_CD"] = false, ["RGN_CD"] = false, ["ONBRD_DT_KEY"] = false })
            .Measured("dbo.dim_staff", new Dictionary<string, bool> { ["EMP_NO"] = true, ["EMP_NM"] = true, ["GRADE_CD"] = false, ["PRAC_CD"] = false, ["HIRE_DT_KEY"] = false })
            .Measured("dbo.dim_project", new Dictionary<string, bool> { ["PRJ_ID"] = true, ["CLNT_ID"] = false, ["PRJ_NM"] = true, ["PRAC_CD"] = false, ["START_DT_KEY"] = false, ["END_DT_KEY"] = false })
            .Measured("dbo.fact_timesheet", new Dictionary<string, bool> { ["TS_LINE_ID"] = true, ["EMP_NO"] = false, ["PRJ_ID"] = false, ["WORK_DT_KEY"] = false, ["SRC_ROW_TXT"] = true })
            // A young warehouse: three invoices so far, each to its own client and project, so in the sample those columns happen to be unique.
            .Measured("dbo.fact_invoice", new Dictionary<string, bool> { ["INV_NO"] = true, ["PRJ_ID"] = tinyInvoices, ["CLNT_ID"] = tinyInvoices, ["INV_DT_KEY"] = tinyInvoices, ["DUE_DT_KEY"] = tinyInvoices, ["PAID_DT_KEY"] = false });

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Every_guess_on_the_keyless_J8_shape_is_a_link_its_keys_would_have_declared(bool keepPrimaryKeys, bool withSample)
        {
            var m = StructureManifest.Derive(Stripped(keepPrimaryKeys), null, null, "seed", "x", "DW", "import", withSample ? J8Sample() : LinkGuessFacts.NamesOnly());
            Assert.NotEmpty(m.Links);
            Assert.All(m.Links, l => Assert.True(Declared.Contains(Key(l)), "guessed a link the keys never declared: " + Key(l)));
            Assert.Equal("fact", m.Tables.Single(t => t.Name == "fact_timesheet").Role);
            Assert.Equal("fact", m.Tables.Single(t => t.Name == "fact_invoice").Role);
        }

        [Fact]
        public void A_young_fact_whose_link_columns_are_unique_in_the_sample_is_not_turned_into_a_dimension()
        {
            var m = StructureManifest.Derive(Stripped(false), null, null, "seed", "x", "DW", "import", J8Sample(tinyInvoices: true));
            // No dimension points at the fact: a key unique in a small sample does not make a fact the one side of its own dimension.
            Assert.DoesNotContain(m.Links, l => l.ToTable == "fact_invoice" || l.ToTable == "fact_timesheet");
            Assert.Equal("fact", m.Tables.Single(t => t.Name == "fact_invoice").Role);
            Assert.Contains(m.BaseMeasures, b => b.Table == "fact_invoice" && b.Column == "INV_AMT");
        }
    }

    /// <summary>The review against the build on a tricky shape, through both doors, Free and Pro with Jev unavailable.</summary>
    [Collection("restore-root")]
    public sealed class VerifyUat7LinksEngineTests : GateFixtureBase
    {
        private const string Server = "tr.datawarehouse.fabric.microsoft.com", Db = "Tricky";
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public VerifyUat7LinksEngineTests(RestoreRootFixture fixture) : base(fixture)
        {
            ConnectionRegistry.RootOverride = Path.Combine(Root, "connections");
            AgentPolicyStore.SetPreset("open", "human");
        }

        public override void Dispose() { ConnectionRegistry.RootOverride = _oldConnections; base.Dispose(); }

        private sealed class Oracle : IDecisionOracle
        {
            public bool Available { get; set; }
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct) => Task.FromResult(DecisionAnswers.NotDecided(request, "no answer"));
        }

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };
        private static FabricTable T(string name, params FabricColumn[] cols) => new FabricTable { Schema = "dbo", Name = name, KeyColumns = new string[0], Columns = cols };

        // Id in many tables, a fact with two date keys, a self reference, a role column before the exact key, plural names.
        private static FabricSchema Tricky() => new FabricSchema
        {
            Server = Server, Database = Db,
            Tables = new[]
            {
                T("Customers", C("Id", "Int64"), C("Name", "String"), C("ReferredByCustomerId", "Int64")),
                T("Products", C("Id", "Int64"), C("Name", "String")),
                T("Date", C("DateKey", "Int64"), C("Date", "DateTime"), C("Year", "Int64")),
                T("Orders", C("Id", "Int64"), C("CustomerId", "Int64"), C("ProductId", "Int64"), C("OrderDateKey", "Int64"), C("ShipDateKey", "Int64"),
                    C("Quantity", "Int64"), C("Amount", "Decimal")),
            },
        };

        private LocalEngine Engine(SessionManager sm, bool pro)
        {
            var e = new LocalEngine(sm, pro ? TestEntitlements.Pro : TestEntitlements.Free, Path.Combine(Root, "ws"));
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(Tricky());
            e.UseDecisionsForTest(new DecisionService(new Oracle()));
            return e;
        }

        private static async Task<StartPlanView> Ready(LocalEngine e)
        {
            await e.StartDecideSettledForTest(); await e.StartPreviewSettledForTest();
            await e.GetStartPlanAsync();
            await e.StartPreviewSettledForTest();
            return await e.GetStartPlanAsync();
        }

        private static List<StartLine> LinkLines(StartPlanView v) =>
            v.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Lines).Where(l => l.Item.Kind == "create_relationship").ToList();

        [Fact]
        public async Task A_dropped_guess_stays_dropped_after_an_engine_restart_and_is_still_not_built()
        {
            string id, modelPath, customerRef;
            using (var sm1 = new SessionManager())
            {
                var e1 = new LocalEngine(sm1, TestEntitlements.Free, Path.Combine(Root, "ws"));
                e1.StartPlanSchemaReaderForTest = (r, o) => { var s = StartLinkGuessTests.Northstar(); s.Server = Server; s.Database = Db; return Task.FromResult(s); };
                e1.UseDecisionsForTest(new DecisionService(new Oracle()));
                var started = await e1.StartModelFromSqlAsync(new StartModelRequest { Name = "Northstar", Folder = Path.Combine(Root, "r"), Server = Server, Database = Db }, "human");
                id = started.Id; modelPath = started.Destination.ModelPath;
                var view = await Ready(e1);
                var customer = LinkLines(view).Single(l => l.Item.Title.Contains("Customer[CustomerKey]"));
                customerRef = customer.Item.ObjectRef;
                await e1.EditStartPlanAsync(new StartPlanEdit { Op = "drop_line", Target = customer.Item.Id }, "human");
                e1.Dispose();
            }
            using var sm2 = new SessionManager();
            using var e2 = new LocalEngine(sm2, TestEntitlements.Free, Path.Combine(Root, "ws"));
            e2.StartPlanSchemaReaderForTest = (r, o) => { var s = StartLinkGuessTests.Northstar(); s.Server = Server; s.Database = Db; return Task.FromResult(s); };
            e2.UseDecisionsForTest(new DecisionService(new Oracle()));
            await e2.OpenAsync(modelPath);
            var back = await Ready(e2);
            Assert.Equal(id, back.Id);
            var line = LinkLines(back).Single(l => l.Item.ObjectRef == customerRef);
            Assert.False(line.Kept);
            Assert.Equal(3, LinkLines(back).Count(l => l.Kept));
            back = await e2.ApproveStartPlanAsync(back.Digest);
            var built = await e2.BuildStartPlanAsync(back.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var rels = await sm2.Current.ReadAsync(m => m.Relationships.Select(r => r.ToTable.Name).OrderBy(x => x, StringComparer.Ordinal).ToList());
            Assert.Equal(new[] { "Date", "Product", "Store" }, rels);
        }

        [Theory]
        [InlineData("ui", false)]
        [InlineData("mcp", false)]
        [InlineData("ui", true)]
        public async Task On_a_tricky_shape_every_link_built_is_a_line_reviewed_and_on_or_off_as_the_review_said(string door, bool pro)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, pro);
            if (door == "mcp") await McpTools.StartModelFromSql(e, Server, Db, Path.Combine(Root, "m"), "Tricky");
            else await e.StartModelFromSqlAsync(new StartModelRequest { Name = "Tricky", Folder = Path.Combine(Root, "m"), Server = Server, Database = Db }, "human");
            var view = await Ready(e);
            var lines = LinkLines(view);
            // Drop the ship date guess: it must not be built.
            var ship = lines.Single(l => l.Item.Title.Contains("[ShipDateKey]"));
            view = door == "mcp" ? await McpTools.EditStartPlan(e, "drop_line", ship.Item.ObjectRef, null)
                                 : await e.EditStartPlanAsync(new StartPlanEdit { Op = "drop_line", Target = ship.Item.Id }, "human");
            view = await Ready(e);
            lines = LinkLines(view);
            var on = lines.Where(l => l.Kept).Select(l => l.Item.Title).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(3, on.Count);
            Assert.Contains(on, t => t.StartsWith("Linked Orders[CustomerId] to Customers[Id]", StringComparison.Ordinal));
            Assert.Contains(on, t => t.StartsWith("Linked Orders[ProductId] to Products[Id]", StringComparison.Ordinal));
            Assert.Contains(on, t => t.StartsWith("Linked Orders[OrderDateKey] to Date[DateKey]", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.Item.Title.Contains("ReferredByCustomerId"));

            view = await e.ApproveStartPlanAsync(view.Digest);
            var built = door == "mcp" ? await McpTools.BuildStartPlan(e, view.Digest) : await e.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var model = await sm.Current.ReadAsync(m => new
            {
                All = m.Relationships.OfType<SingleColumnRelationship>().Select(r => r.FromTable.Name + "." + r.FromColumn.Name + ">" + r.ToTable.Name + "." + r.ToColumn.Name + (r.IsActive ? "" : " off")).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                Measures = m.AllMeasures.Select(x => x.Name).ToList(),
            });
            Assert.Equal(new[] { "Orders.CustomerId>Customers.Id", "Orders.OrderDateKey>Date.DateKey", "Orders.ProductId>Products.Id" }, model.All);
            Assert.Contains(model.Measures, n => n.Contains("Amount"));
            Assert.DoesNotContain(model.Measures, n => n.EndsWith("Id", StringComparison.Ordinal) || n.Contains("DateKey"));
        }
    }
}
