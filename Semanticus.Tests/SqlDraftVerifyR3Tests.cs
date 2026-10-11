using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Verifier, round 3 of m11/sql-start (targeted, at e921fd3d). Every test here fails at e921fd3d.
    ///
    /// MEDIUM, R2-M2 is not closed, only moved. The Model Spec's SQL words are now "model from sql, model from my sql,
    /// model from a sql, model from the sql". An ask that only says "from the sql" no longer routes there, but every
    /// ask that says "the model from the sql" does, whatever it asks for: a refresh, a schema update, a SQL check. A
    /// word match is the whole answer (RouteAskAsync: confidence 1, the decision service is never asked), and a Plan
    /// task in these words takes its jump from the same match. On the lane's base (dc8d3bd6) none of these went to the
    /// Model Spec: "check the model from the sql source" and "test the model from the sql endpoint" went to Validation,
    /// and the rest found no place.
    ///
    /// MEDIUM, the rest of R2-H1: a key to itself still counts as a link that points at a table in one place, the
    /// choice of the source date table (FindSourceDateTable ranks the candidates by how many links point at them, self
    /// links included). Two tables named only for dates, dim_date named by the fact and dim_calendar named by nothing
    /// but itself: the draft marks dim_calendar and puts every time-intelligence measure on it. Built at e921fd3d: 0
    /// errors, dim_calendar marked, "Total AMT YTD = TOTALYTD([Total AMT], 'dim_calendar'[DAY_DT])", and the only
    /// relationship is fact_sales to dim_date, so every time-intelligence number is silently wrong and nothing says so.
    /// Without the key to itself the same source drafts dim_date as the date table.</summary>
    public sealed class SqlDraftVerifyR3Tests
    {
        [Theory]
        [InlineData("refresh the model from the sql endpoint")]
        [InlineData("update my model from the sql database")]
        [InlineData("how do I update the model from the sql source?")]
        [InlineData("validate the model from the sql source")]
        [InlineData("sync the model from sql server")]
        [InlineData("check the model from the sql source")]
        [InlineData("test the model from the sql endpoint")]
        public void Asks_to_refresh_update_or_check_the_model_from_sql_do_not_route_to_the_model_spec(string ask)
        {
            Assert.NotEqual("spec", AskRoutePoint.MatchWords(ask)?.Tool);
            Assert.NotEqual("spec", PlanTargets.JumpFor(ask)?.Tool);
        }

        /// <summary>The same through the door the ask box and route_ask use.</summary>
        [Fact]
        public async Task The_ask_box_does_not_answer_a_refresh_of_the_model_from_sql_with_the_model_spec()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteSqlR3.bim");
            f.UseNull();

            var result = await f.Engine.RouteAskAsync("refresh the model from the sql endpoint");

            Assert.NotEqual("spec", result.Tool);
        }

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };

        private static FabricForeignKey Fk(string from, string fromCol, string to, string toCol) => new FabricForeignKey
        { FromSchema = "dbo", FromTable = from, FromColumn = fromCol, ToSchema = "dbo", ToTable = to, ToColumn = toCol };

        /// <summary>One key to itself with the calendar listed first (the endpoint lists tables by name, and
        /// "dim_calendar" sorts before "dim_date"), or two keys to itself with the calendar listed last.</summary>
        [Theory]
        [InlineData(1, true)]
        [InlineData(2, false)]
        public void A_key_to_itself_does_not_decide_which_table_is_the_date_table(int selfKeys, bool calendarFirst)
        {
            var calendar = new FabricTable
            {
                Schema = "dbo", Name = "dim_calendar", KeyColumns = new[] { "DAY_KEY" },
                Columns = new[] { C("DAY_KEY", "Int64"), C("DAY_DT", "DateTime"), C("PREV_DAY_KEY", "Int64"), C("NEXT_DAY_KEY", "Int64") },
            };
            var date = new FabricTable
            {
                Schema = "dbo", Name = "dim_date", KeyColumns = new[] { "DATE_KEY" },
                Columns = new[] { C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime") },
            };
            var sales = new FabricTable
            {
                Schema = "dbo", Name = "fact_sales", KeyColumns = new[] { "SALE_ID" },
                Columns = new[] { C("SALE_ID", "Int64"), C("SALE_DT_KEY", "Int64"), C("AMT", "Decimal") },
            };
            var schema = new FabricSchema
            {
                Tables = calendarFirst ? new[] { calendar, date, sales } : new[] { date, sales, calendar },
                ForeignKeys = new[]
                {
                    Fk("fact_sales", "SALE_DT_KEY", "dim_date", "DATE_KEY"),
                    Fk("dim_calendar", "PREV_DAY_KEY", "dim_calendar", "DAY_KEY"),
                    Fk("dim_calendar", "NEXT_DAY_KEY", "dim_calendar", "DAY_KEY"),
                }.Take(1 + selfKeys).ToArray(),
            };

            var spec = LocalEngine.FabricSchemaToSpec(schema, "x", "DW", "import");

            Assert.Equal("dim_date", spec.DateTable.Name);
            Assert.Equal("CAL_DT", spec.DateTable.DateColumn);
            Assert.NotEqual("date", spec.Tables.Single(t => t.Name == "dim_calendar").Role);
        }
    }
}
