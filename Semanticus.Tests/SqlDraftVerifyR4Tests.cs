using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Verifier, round 4 of m11/sql-start (targeted, at a6b0f61b). Every test here fails at a6b0f61b.
    ///
    /// MEDIUM, R2-M2 is still not closed. The brief's rule is that the SQL words count for the Model Spec only BESIDE a
    /// making word, the rule the ask bar's isPlaceAsk uses ("build (a, the, my...) model from"). The engine counts them
    /// when a making word is ANYWHERE in the ask (AskRoutePoint.MatchWords: `making` is any of MakingWords in the whole
    /// text). So "make sure", "start a refresh", "the new columns", "set up a refresh schedule", "create a backup",
    /// "generate documentation", "draft release notes", "build a report" and "the new tables" each switch the SQL words
    /// on, and each of these asks about a model that already exists routes to the Model Spec with confidence 1 (the
    /// decision service is never asked), and a Plan task in these words takes its jump from the same match. Four of them
    /// went to the right page on the lane's base (dc8d3bd6): Docs, Model notes, Reports and Validation. The bar and the
    /// engine carry the same eight words but not the same rule, so the two doors disagree on the same words.
    ///
    /// MEDIUM, harmful when it triggers, the same function as N1. A key to itself no longer decides the source date
    /// table (224 placements of a self key in a sweep changed no pick). But the pick still counts a link from a
    /// dimension the same as a link from a fact, then breaks a tie by name. The coordinator's own shape (a calendar
    /// with a key to itself and one dimension link, beside a plain date dimension the fact names), in the order the
    /// endpoint lists tables (by name, so dim_calendar first), marks dim_calendar and builds "Total AMT YTD =
    /// TOTALYTD([Total AMT], 'dim_calendar'[DAY_DT])" with 0 errors, while the calendar reaches the sales only through
    /// the promotion's start day. Every time-intelligence number is then silently wrong. With more dimension links
    /// than fact links the calendar wins whatever the order.</summary>
    public sealed class SqlDraftVerifyR4Tests
    {
        // ---- R2-M2: a making word somewhere else in the ask ---------------------------------------------------------

        [Theory]
        [InlineData("make sure the model from the sql database is up to date")]
        [InlineData("start a refresh of the model from the sql endpoint")]
        [InlineData("add the new columns to my model from the sql warehouse")]
        [InlineData("set up a refresh schedule for the model from sql server")]
        [InlineData("create a backup of the model from the sql database")]
        [InlineData("generate documentation for my model from the sql endpoint")]
        [InlineData("draft release notes for the model from the sql source")]
        [InlineData("build a report on the model from the sql database")]
        [InlineData("check the new tables in the model from the sql source")]
        public void A_making_word_elsewhere_in_the_ask_does_not_make_the_model_from_sql_the_model_spec(string ask)
        {
            Assert.NotEqual("spec", AskRoutePoint.MatchWords(ask)?.Tool);
            Assert.NotEqual("spec", PlanTargets.JumpFor(ask)?.Tool);
        }

        /// <summary>Where these went on the lane's base, and still should: none of them asks for a new model.</summary>
        [Theory]
        [InlineData("generate documentation for my model from the sql endpoint", "docs")]
        [InlineData("draft release notes for the model from the sql source", "knowledge")]
        [InlineData("build a report on the model from the sql database", "evidence")]
        [InlineData("check the new tables in the model from the sql source", "tests")]
        public void Asks_that_found_their_page_on_the_base_still_find_it(string ask, string tool)
        {
            Assert.Equal(tool, AskRoutePoint.MatchWords(ask)?.Tool);
        }

        /// <summary>A question is what the Studio's ask bar routes (isQuestion), so this one reaches the engine's answer.</summary>
        [Fact]
        public async Task The_ask_box_does_not_answer_a_question_about_new_columns_with_the_model_spec()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteSqlR4.bim");
            f.UseNull();

            var result = await f.Engine.RouteAskAsync("how do I make sure my model from the sql source has the new columns?");

            Assert.NotEqual("spec", result.Tool);
        }

        // ---- The source date table is the one the fact names ---------------------------------------------------------

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };

        private static FabricForeignKey Fk(string from, string fromCol, string to, string toCol) => new FabricForeignKey
        { FromSchema = "dbo", FromTable = from, FromColumn = fromCol, ToSchema = "dbo", ToTable = to, ToColumn = toCol };

        private static FabricTable T(string name, string[] keys, params FabricColumn[] cols) =>
            new FabricTable { Schema = "dbo", Name = name, KeyColumns = keys, Columns = cols };

        /// <summary>fact_sales names one date table (factNames); a dimension that fact_sales also names points at the other
        /// one dimLinks times. Tables in the order given, as the endpoint lists them (by schema and name).</summary>
        private static FabricSchema TwoDateTables(string factNames, int dimLinks, bool calendarFirst, bool calendarSelfKey)
        {
            var calendar = T("dim_calendar", new[] { "DAY_KEY" }, C("DAY_KEY", "Int64"), C("DAY_DT", "DateTime"), C("PREV_DAY_KEY", "Int64"));
            var date = T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime"));
            var promo = T("dim_promo", new[] { "PROMO_KEY" }, C("PROMO_KEY", "Int64"), C("PROMO_NM", "String"), C("START_DAY_KEY", "Int64"), C("END_DAY_KEY", "Int64"));
            var sales = T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("SALE_DT_KEY", "Int64"), C("PROMO_KEY", "Int64"), C("AMT", "Decimal"));
            var other = factNames == "dim_date" ? ("dim_calendar", "DAY_KEY") : ("dim_date", "DATE_KEY");
            var keys = new List<FabricForeignKey>
            {
                Fk("fact_sales", "SALE_DT_KEY", factNames, factNames == "dim_date" ? "DATE_KEY" : "DAY_KEY"),
                Fk("fact_sales", "PROMO_KEY", "dim_promo", "PROMO_KEY"),
                Fk("dim_promo", "START_DAY_KEY", other.Item1, other.Item2),
            };
            if (dimLinks > 1) keys.Add(Fk("dim_promo", "END_DAY_KEY", other.Item1, other.Item2));
            if (calendarSelfKey) keys.Add(Fk("dim_calendar", "PREV_DAY_KEY", "dim_calendar", "DAY_KEY"));
            return new FabricSchema
            {
                Tables = calendarFirst ? new[] { calendar, date, promo, sales } : new[] { date, promo, sales, calendar },
                ForeignKeys = keys.ToArray(),
            };
        }

        /// <summary>First row: the coordinator's shape in the endpoint's own order. The others: the same tie the other way
        /// round, and a dimension with two date keys against the fact's one.</summary>
        [Theory]
        [InlineData("dim_date", 1, true, true)]
        [InlineData("dim_date", 1, true, false)]
        [InlineData("dim_calendar", 1, false, false)]
        [InlineData("dim_date", 2, false, true)]
        public void The_date_table_is_the_one_the_fact_names_not_one_only_a_dimension_names(string factNames, int dimLinks, bool calendarFirst, bool calendarSelfKey)
        {
            var spec = LocalEngine.FabricSchemaToSpec(TwoDateTables(factNames, dimLinks, calendarFirst, calendarSelfKey), "x", "DW", "import");

            Assert.Equal(factNames, spec.DateTable.Name);
        }
    }
}
