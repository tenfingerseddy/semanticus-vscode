using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Verifier, m11/sql-start at 78757da5 (28 Sep 2026). Each test here FAILED on that commit and is left
    /// failing on purpose: it proves a defect in "Draft from a SQL source" for the builder to fix. None of them reaches a
    /// sign-in, a browser, the Azure command line or an endpoint: the schema goes through the product's own pure mapping,
    /// <see cref="LocalEngine.FabricSchemaToSpec"/>, and the build runs on an offline model.
    ///
    /// The lane keeps a link on "only while it adds no second path" and places links out of a dimension first. That is
    /// right for J8's shortcut (Invoice to Client beside Invoice to Project to Client), but it decides by position, not
    /// by what the link means, so two common source shapes come out wrong while building with 0 errors:
    /// a dimension that declares its own date key turns off every fact's own date link, and two dimensions that share
    /// an outrigger turn off a fact's link to one of them.</summary>
    public sealed class SqlDraftVerifyTests
    {
        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };

        private static FabricForeignKey Fk(string from, string fromCol, string to, string toCol) => new FabricForeignKey
        { FromSchema = "dbo", FromTable = from, FromColumn = fromCol, ToSchema = "dbo", ToTable = to, ToColumn = toCol };

        private static FabricTable T(string name, string[] keys, params FabricColumn[] cols) =>
            new FabricTable { Schema = "dbo", Name = name, KeyColumns = keys, Columns = cols };

        private static SpecRelationship Rel(ModelSpec spec, string from, string fromCol) =>
            spec.Relationships.Single(r => r.FromTable == from && r.FromColumn == fromCol);

        /// <summary>The first pair of tables with two distinct active filter paths between them, or null. Tabular keeps
        /// one active path between two tables, so a draft with a pair here is ambiguous.</summary>
        private static string AmbiguousPair(SpecRelationship[] rels)
        {
            var active = rels.Where(r => r.IsActive != false
                && !string.Equals(r.FromTable, r.ToTable, StringComparison.OrdinalIgnoreCase)).ToArray();
            var tables = active.SelectMany(r => new[] { r.FromTable, r.ToTable }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            int Paths(string at, string goal, int depth)
            {
                if (depth > 20) return 0;
                if (string.Equals(at, goal, StringComparison.OrdinalIgnoreCase)) return 1;
                return active.Where(r => string.Equals(r.FromTable, at, StringComparison.OrdinalIgnoreCase)).Sum(r => Paths(r.ToTable, goal, depth + 1));
            }
            foreach (var s in tables)
                foreach (var t in tables)
                    if (!string.Equals(s, t, StringComparison.OrdinalIgnoreCase) && Paths(s, t, 0) > 1) return s + " -> " + t;
            return null;
        }

        /// <summary>HIGH. J8 plus ONE more declared key, the client's onboarding date key to dim_date (dim_client carries
        /// ONBRD_DT_KEY in the J8 fixture; a warehouse that declares its date keys declares this one too). Measured at
        /// 78757da5: all four fact date links are drafted off (fact_timesheet WORK_DT_KEY; fact_invoice INV_DT_KEY,
        /// DUE_DT_KEY, PAID_DT_KEY) and only dim_client to dim_date stays on. The draft builds with 0 errors, so the date
        /// table filters invoices and timesheets by WHEN THEIR CLIENT WAS ONBOARDED, and the drafted time intelligence
        /// (Total INV_AMT YTD and the rest, over 'dim_date'[CAL_DT]) reports numbers that are silently wrong.</summary>
        [Fact]
        public async Task A_dimension_date_key_leaves_each_facts_own_date_link_on()
        {
            var schema = SqlDraftBuildTests.J8Schema();
            schema.ForeignKeys = schema.ForeignKeys.Concat(new[] { Fk("dim_client", "ONBRD_DT_KEY", "dim_date", "DATE_KEY") }).ToArray();
            var spec = LocalEngine.FabricSchemaToSpec(schema, "contoso-sql.example.com", "ContosoConsulting", "import");

            // Each fact's own date must be the one the date table filters it by.
            Assert.NotEqual(false, Rel(spec, "fact_invoice", "INV_DT_KEY").IsActive);
            Assert.NotEqual(false, Rel(spec, "fact_timesheet", "WORK_DT_KEY").IsActive);
            Assert.Null(AmbiguousPair(spec.Relationships));

            // And the build is still clean: the fix must not trade this for an ambiguous or failing draft.
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("ContosoConsulting", 1604);
            await engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            var report = await engine.BuildModelFromSpecAsync("human");
            Assert.Empty(report.Errors);
        }

        /// <summary>MEDIUM, a regression the lane introduced. The build now treats a dateTable that names one of the
        /// spec's OWN tables as "mark that table" and calls MarkDateTableCore with no date column. "Autogenerate from
        /// model" always writes the model's own date table that way, so a rebuild of an ordinary model now fails on it.
        /// Measured at 78757da5, on both doors: draft from a source with no date table, build (a CALENDAR table "Date"
        /// is added and marked), Autogenerate from model, build again (the build is re-run safe) = one error, "date table
        /// Date: 'Date' has no DateTime column, so it cannot be marked as a date table", because a calculated table has
        /// no columns offline. On dc8d3bd6 the same rebuild skipped the table and reported no error.</summary>
        [Fact]
        public async Task Rebuilding_after_autogenerate_from_model_reports_no_date_table_error()
        {
            var schema = new FabricSchema
            {
                Tables = new[]
                {
                    T("Sales", new[] { "Id" }, C("Id", "Int64"), C("CustomerId", "Int64"), C("OrderDate", "DateTime"), C("Amount", "Decimal")),
                    T("Customer", new[] { "CustomerId" }, C("CustomerId", "Int64"), C("Name", "String")),
                },
                ForeignKeys = new[] { Fk("Sales", "CustomerId", "Customer", "CustomerId") },
            };
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("NoDate", 1604);
            await engine.SetSpecAsync(JsonSerializer.Serialize(LocalEngine.FabricSchemaToSpec(schema, "x", "DW", "import"), Camel), "human");
            Assert.Empty((await engine.BuildModelFromSpecAsync("human")).Errors);

            await engine.AutogenerateSpecFromModelAsync("human");
            var rebuild = await engine.BuildModelFromSpecAsync("human");

            Assert.DoesNotContain(rebuild.Errors, e => e.StartsWith("date table", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>MEDIUM, the same regression through an import date table with two date columns (a date and a month
        /// start, the usual shape). Its key is known (Date is IsKey and the table is marked), but the build passes no
        /// date column, so MarkDateTableCore refuses: "the date key is ambiguous: pass dateColumn", words a person on the
        /// Model Spec cannot act on. On dc8d3bd6 the rebuild skipped the table with no error.</summary>
        [Fact]
        public async Task Rebuilding_a_model_whose_date_table_has_two_date_columns_reports_no_date_table_error()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("Src", 1604);
            var dt = await engine.CreateTableAsync("Calendar", "human");
            await engine.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await engine.CreateColumnAsync(dt, "Month Start", "DateTime", "Month Start", "human");
            await engine.MarkDateTableAsync(dt, "Date", "human");
            var s = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(s, "Order Date", "DateTime", "Order Date", "human");
            await engine.CreateColumnAsync(s, "Amount", "Decimal", "Amount", "human");
            await engine.CreateRelationshipAsync("column:Sales/Order Date", "column:Calendar/Date", null, null, "human");

            await engine.AutogenerateSpecFromModelAsync("human");
            var rebuild = await engine.BuildModelFromSpecAsync("human");

            Assert.DoesNotContain(rebuild.Errors, e => e.StartsWith("date table", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>MEDIUM, a regression the lane introduced. The Model Spec's new ask words ("sql source", "sql database",
        /// "sql endpoint", "sql server") are the words people use for the OTHER SQL features: checks that compare a
        /// measure against a SQL source (Validation) and the SQL endpoint set on Connections. Measured on dc8d3bd6 +
        /// these tests: "check the numbers against the SQL source" went to tests, "compare a measure to the sql database"
        /// to daxlab, and the four endpoint phrasings to no place (so the page asked the service or said it could not
        /// place it). At 78757da5 every one of them answers "Model Spec is the place for that" (seen in the real
        /// Studio for "where do I add a SQL endpoint?").</summary>
        [Theory]
        [InlineData("check the numbers against the SQL source")]
        [InlineData("compare a measure to the sql database")]
        [InlineData("test my measures against sql server")]
        [InlineData("where do I add a SQL endpoint?")]
        [InlineData("set the SQL endpoint for this model")]
        [InlineData("connect to sql server")]
        public void Sql_words_about_checks_and_endpoints_do_not_route_to_the_model_spec(string ask)
        {
            Assert.NotEqual("spec", AskRoutePoint.MatchWords(ask)?.Tool);
        }

        /// <summary>HIGH. Two dimensions that share an outrigger (a diamond): Sales to Store to Geography and Sales to
        /// Customer to Geography. Measured at 78757da5: fact_sales to dim_customer is drafted OFF ("fact_sales already
        /// reaches dim_geography through dim_store"), so the customer dimension cannot filter sales at all: sales by
        /// customer shows the grand total on every row. The link to switch off is one of the two outrigger links, never a
        /// fact's own link to a dimension it has no other way to reach.</summary>
        [Fact]
        public void A_diamond_snowflake_keeps_each_fact_link_on()
        {
            var schema = new FabricSchema
            {
                Tables = new[]
                {
                    T("dim_geography", new[] { "GEO_ID" }, C("GEO_ID", "Int64"), C("CITY", "String")),
                    T("dim_store", new[] { "STORE_ID" }, C("STORE_ID", "Int64"), C("GEO_ID", "Int64"), C("STORE_NM", "String")),
                    T("dim_customer", new[] { "CUST_ID" }, C("CUST_ID", "Int64"), C("GEO_ID", "Int64"), C("CUST_NM", "String")),
                    T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("STORE_ID", "Int64"), C("CUST_ID", "Int64"), C("AMT", "Decimal")),
                },
                ForeignKeys = new[]
                {
                    Fk("fact_sales", "STORE_ID", "dim_store", "STORE_ID"),
                    Fk("fact_sales", "CUST_ID", "dim_customer", "CUST_ID"),
                    Fk("dim_store", "GEO_ID", "dim_geography", "GEO_ID"),
                    Fk("dim_customer", "GEO_ID", "dim_geography", "GEO_ID"),
                },
            };
            var spec = LocalEngine.FabricSchemaToSpec(schema, "x", "DW", "import");

            Assert.NotEqual(false, Rel(spec, "fact_sales", "STORE_ID").IsActive);
            Assert.NotEqual(false, Rel(spec, "fact_sales", "CUST_ID").IsActive);
            Assert.Equal(1, spec.Relationships.Count(r => r.IsActive == false));
            Assert.Null(AmbiguousPair(spec.Relationships));
        }
    }
}
