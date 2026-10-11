using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Verifier, round 2 of m11/sql-start (at 95d0b86f). Every test here fails at 95d0b86f.
    ///
    /// HIGH. The coordinator's link ruling: a fact table's own direct links always stay active, and no fact is left
    /// unable to be filtered by a dimension it references. A fact that declares a key to ITSELF breaks both. The draft
    /// calls a table a fact only when no foreign key points at it, and a key to itself points at it, so the fact is
    /// drafted as a dimension: its own links lose the protection the ruling gives a fact's links, and the second-path
    /// rule then turns one of them off. The self link itself is drafted off and left out of the build (verifier L7,
    /// fixed this round), yet it still decides the role. Both drafts build with 0 errors, so nothing tells the person.
    ///
    /// MEDIUM. What is left of the verifier's M1 (a rebuild that errors on the model's own date table) and M2 (SQL asks
    /// that are not about a new model routed to the Model Spec), each measured on the lane's base first.</summary>
    public sealed class SqlDraftVerifyR2Tests
    {
        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };

        private static FabricForeignKey Fk(string from, string fromCol, string to, string toCol) => new FabricForeignKey
        { FromSchema = "dbo", FromTable = from, FromColumn = fromCol, ToSchema = "dbo", ToTable = to, ToColumn = toCol };

        private static SpecRelationship Rel(ModelSpec spec, string from, string fromCol) =>
            spec.Relationships.Single(r => r.FromTable == from && r.FromColumn == fromCol);

        /// <summary>J8 with a credit note: fact_invoice names the invoice it credits (CREDIT_FOR_INV_NO to INV_NO). At
        /// 95d0b86f fact_invoice is drafted as a dimension and its link to dim_project is drafted OFF ("fact_invoice
        /// already reaches dim_client on its own link"), so no project can filter an invoice.</summary>
        [Fact]
        public async Task A_fact_with_a_key_to_itself_keeps_its_own_links_on()
        {
            var schema = SqlDraftBuildTests.J8Schema();
            schema.Tables = schema.Tables.Select(t => t.Name != "fact_invoice" ? t : new FabricTable
            {
                Schema = t.Schema, Name = t.Name, KeyColumns = t.KeyColumns,
                Columns = t.Columns.Append(C("CREDIT_FOR_INV_NO", "String")).ToArray(),
            }).ToArray();
            schema.ForeignKeys = schema.ForeignKeys.Append(Fk("fact_invoice", "CREDIT_FOR_INV_NO", "fact_invoice", "INV_NO")).ToArray();

            var spec = LocalEngine.FabricSchemaToSpec(schema, "x", "DW", "import");

            Assert.Equal("fact", spec.Tables.Single(t => t.Name == "fact_invoice").Role);
            Assert.NotEqual(false, Rel(spec, "fact_invoice", "PRJ_ID").IsActive);
            Assert.NotEqual(false, Rel(spec, "fact_invoice", "CLNT_ID").IsActive);
            Assert.NotEqual(false, Rel(spec, "fact_invoice", "INV_DT_KEY").IsActive);
            Assert.False(Rel(spec, "fact_invoice", "CREDIT_FOR_INV_NO").IsActive);

            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("ContosoConsulting", 1604);
            await engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            var report = await engine.BuildModelFromSpecAsync("human");
            Assert.Empty(report.Errors);
            var graph = await engine.GetModelGraphAsync();
            Assert.Contains(graph.Relationships, r => r.FromTable == "fact_invoice" && r.ToTable == "dim_project" && r.IsActive);
        }

        /// <summary>An order that names the order it replaces, and a customer with a sign-up date. At 95d0b86f the order's
        /// own date link is drafted OFF and the customer's sign-up date stays on, so the date table filters orders by the
        /// customer's sign-up date: the verifier's H2 again, through a self key.</summary>
        [Fact]
        public void A_fact_with_a_key_to_itself_keeps_its_own_date_link_on()
        {
            var schema = new FabricSchema
            {
                Tables = new[]
                {
                    new FabricTable { Schema = "dbo", Name = "dim_date", KeyColumns = new[] { "DATE_KEY" },
                        Columns = new[] { C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime") } },
                    new FabricTable { Schema = "dbo", Name = "dim_customer", KeyColumns = new[] { "CUST_ID" },
                        Columns = new[] { C("CUST_ID", "Int64"), C("CUST_NM", "String"), C("SIGNUP_DT_KEY", "Int64") } },
                    new FabricTable { Schema = "dbo", Name = "fact_order", KeyColumns = new[] { "ORDER_ID" },
                        Columns = new[] { C("ORDER_ID", "Int64"), C("REPLACES_ORDER_ID", "Int64"), C("CUST_ID", "Int64"), C("ORDER_DT_KEY", "Int64"), C("AMT", "Decimal") } },
                },
                ForeignKeys = new[]
                {
                    Fk("dim_customer", "SIGNUP_DT_KEY", "dim_date", "DATE_KEY"),
                    Fk("fact_order", "CUST_ID", "dim_customer", "CUST_ID"),
                    Fk("fact_order", "ORDER_DT_KEY", "dim_date", "DATE_KEY"),
                    Fk("fact_order", "REPLACES_ORDER_ID", "fact_order", "ORDER_ID"),
                },
            };

            var spec = LocalEngine.FabricSchemaToSpec(schema, "x", "DW", "import");

            Assert.Equal("fact", spec.Tables.Single(t => t.Name == "fact_order").Role);
            Assert.NotEqual(false, Rel(spec, "fact_order", "ORDER_DT_KEY").IsActive);
            Assert.NotEqual(false, Rel(spec, "fact_order", "CUST_ID").IsActive);
            var signup = Rel(spec, "dim_customer", "SIGNUP_DT_KEY");
            Assert.False(signup.IsActive);
            Assert.False(string.IsNullOrWhiteSpace(signup.Note));
        }

        /// <summary>MEDIUM, the rest of verifier M1. A model whose own date table is a calculated table named Date that
        /// is not marked yet (Date = CALENDARAUTO(), made offline): Autogenerate from model names it as the spec's date
        /// table with no column, and Build into model at 95d0b86f answers "1 errors: date table Date: 'Date' has no
        /// DateTime column, so it cannot be marked as a date table. Add a date column first, then mark the table." The
        /// table has its date column as soon as it is calculated, so the line is untrue and asks for something the
        /// person cannot do. On the lane's base (dc8d3bd6) the same rebuild left the table alone with no error.</summary>
        [Fact]
        public async Task Rebuilding_a_model_with_an_unmarked_calculated_Date_table_reports_no_date_table_error()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("M", 1604);
            await engine.CreateCalculatedTableAsync("Date", "CALENDARAUTO()", "human");
            await engine.AutogenerateSpecFromModelAsync("human");

            var report = await engine.BuildModelFromSpecAsync("human");

            Assert.Empty(report.Errors);
        }

        /// <summary>MEDIUM, the rest of verifier M2. The Model Spec's SQL words are now "from sql, from my sql, from a sql,
        /// from the sql", so any ask with "from the sql" in it is the Model Spec, with full confidence and without asking
        /// the decision service (RouteAskAsync takes a word match as the answer). Each of these asks for a SQL check
        /// (Validation), a refresh or a schema update, not a new model. Measured with AskRoutePoint.MatchWords: on the lane's base
        /// (dc8d3bd6) every one found no place; at 95d0b86f every one is "spec".</summary>
        [Theory]
        [InlineData("compare the totals from the sql source with the model")]
        [InlineData("check my measure against the total from the sql endpoint")]
        [InlineData("test a measure against the numbers from my sql database")]
        [InlineData("validate the numbers from the sql source")]
        [InlineData("reconcile sales from the sql database")]
        [InlineData("refresh the data from the sql endpoint")]
        [InlineData("update the schema from the sql source")]
        public void Sql_checks_and_schema_asks_that_say_from_the_sql_do_not_route_to_the_model_spec(string ask)
        {
            Assert.NotEqual("spec", AskRoutePoint.MatchWords(ask)?.Tool);
        }
    }
}
