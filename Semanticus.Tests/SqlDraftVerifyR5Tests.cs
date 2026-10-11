using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Verifier round 5 of m11/sql-start (at 33942de4). Each test here FAILS on that head and proves one defect
    /// of the report /tmp/semanticus-joint-review/lane-reports/v-sql-start-r5.md. They are the verifier's, and a fix makes
    /// them pass without editing them.
    ///
    /// The coordinator's rulings they hold the lane to: (1) the date table is the one the facts name, and none is marked,
    /// with a note, when no fact names one; (2) "a model from SQL" is the Model Spec only when a making word governs it
    /// directly, and an ask about a model that already exists never routes to the Model Spec.</summary>
    public sealed class SqlDraftVerifyR5Tests
    {
        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };
        private static FabricForeignKey Fk(string from, string fromCol, string to, string toCol) => new FabricForeignKey
        { FromSchema = "dbo", FromTable = from, FromColumn = fromCol, ToSchema = "dbo", ToTable = to, ToColumn = toCol };
        private static FabricTable T(string name, string[] keys, params FabricColumn[] cols) =>
            new FabricTable { Schema = "dbo", Name = name, KeyColumns = keys, Columns = cols };
        // The endpoint lists tables by schema and name (Connect/FabricSqlSchema.cs), so these do too.
        private static FabricSchema S(FabricForeignKey[] keys, params FabricTable[] tables) =>
            new FabricSchema { Tables = tables.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray(), ForeignKeys = keys };
        private static ModelSpec Draft(FabricSchema s) => LocalEngine.FabricSchemaToSpec(s, "x", "DW", "import");

        // ---- The shapes -------------------------------------------------------------------------------------------

        /// <summary>A bridge between customers and accounts with an effective date range names dim_calendar twice; the one
        /// fact, the balances, names dim_date.</summary>
        private static FabricSchema BridgeNamesTheCalendar() => S(new[]
        {
            Fk("bridge_cust_acct", "CUST_KEY", "dim_customer", "CUST_KEY"),
            Fk("bridge_cust_acct", "ACCT_KEY", "dim_account", "ACCT_KEY"),
            Fk("bridge_cust_acct", "START_DAY_KEY", "dim_calendar", "DAY_KEY"),
            Fk("bridge_cust_acct", "END_DAY_KEY", "dim_calendar", "DAY_KEY"),
            Fk("fact_balance", "ACCT_KEY", "dim_account", "ACCT_KEY"),
            Fk("fact_balance", "BAL_DT_KEY", "dim_date", "DATE_KEY"),
        },
            T("bridge_cust_acct", new[] { "CUST_KEY", "ACCT_KEY" }, C("CUST_KEY", "Int64"), C("ACCT_KEY", "Int64"), C("START_DAY_KEY", "Int64"), C("END_DAY_KEY", "Int64")),
            T("dim_account", new[] { "ACCT_KEY" }, C("ACCT_KEY", "Int64"), C("ACCT_NM", "String")),
            T("dim_calendar", new[] { "DAY_KEY" }, C("DAY_KEY", "Int64"), C("DAY_DT", "DateTime")),
            T("dim_customer", new[] { "CUST_KEY" }, C("CUST_KEY", "Int64"), C("CUST_NM", "String")),
            T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
            T("fact_balance", new[] { "BAL_ID" }, C("BAL_ID", "Int64"), C("ACCT_KEY", "Int64"), C("BAL_DT_KEY", "Int64"), C("BAL_AMT", "Decimal")));

        public static IEnumerable<object[]> FactsTheDateTableDoesNotFilter()
        {
            // Two facts name different date tables: a budget by calendar day, sales by date.
            yield return new object[] { "two facts name different date tables", S(new[]
            {
                Fk("fact_budget", "BUD_DAY_KEY", "dim_calendar", "DAY_KEY"),
                Fk("fact_sales", "SALE_DT_KEY", "dim_date", "DATE_KEY"),
            },
                T("dim_calendar", new[] { "DAY_KEY" }, C("DAY_KEY", "Int64"), C("DAY_DT", "DateTime")),
                T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                T("fact_budget", new[] { "BUD_ID" }, C("BUD_ID", "Int64"), C("BUD_DAY_KEY", "Int64"), C("BUDGET_AMT", "Decimal")),
                T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("SALE_DT_KEY", "Int64"), C("AMT", "Decimal"))) };
            // The calendar wins only on the returns' second, role-playing link, which the draft turns off.
            yield return new object[] { "a date table wins on an inactive role-playing link", S(new[]
            {
                Fk("fact_returns", "RET_DT_KEY", "dim_calendar", "DAY_KEY"),
                Fk("fact_returns", "ORIG_SALE_DT_KEY", "dim_calendar", "DAY_KEY"),
                Fk("fact_sales", "SALE_DT_KEY", "dim_date", "DATE_KEY"),
            },
                T("dim_calendar", new[] { "DAY_KEY" }, C("DAY_KEY", "Int64"), C("DAY_DT", "DateTime")),
                T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                T("fact_returns", new[] { "RET_ID" }, C("RET_ID", "Int64"), C("RET_DT_KEY", "Int64"), C("ORIG_SALE_DT_KEY", "Int64"), C("RET_AMT", "Decimal")),
                T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("SALE_DT_KEY", "Int64"), C("AMT", "Decimal"))) };
            // A bridge's two date keys outrank the one fact's own date link.
            yield return new object[] { "a bridge names the calendar, the fact names dim_date", BridgeNamesTheCalendar() };
            // A galaxy: web visits carry their own date and name no date table.
            yield return new object[] { "a second fact names no date table", S(new[]
            {
                Fk("fact_sales", "SALE_DT_KEY", "dim_date", "DATE_KEY"),
                Fk("fact_web_visit", "PAGE_KEY", "dim_page", "PAGE_KEY"),
            },
                T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                T("dim_page", new[] { "PAGE_KEY" }, C("PAGE_KEY", "Int64"), C("PAGE_URL", "String")),
                T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("SALE_DT_KEY", "Int64"), C("AMT", "Decimal")),
                T("fact_web_visit", new[] { "VISIT_ID" }, C("VISIT_ID", "Int64"), C("PAGE_KEY", "Int64"), C("VISIT_DT", "DateTime"), C("VISIT_SECONDS", "Int64"))) };
        }

        /// <summary>The tables a fact's rows are filtered from, along the draft's ACTIVE links (many side to one side). A
        /// date table filters a fact only when it is one of them.</summary>
        private static HashSet<string> FilteredFrom(ModelSpec spec, string fact)
        {
            var cmp = StringComparer.OrdinalIgnoreCase;
            var seen = new HashSet<string>(cmp) { fact };
            var queue = new Queue<string>(new[] { fact });
            while (queue.Count > 0)
            {
                var at = queue.Dequeue();
                foreach (var r in spec.Relationships.Where(r => r.IsActive != false && !cmp.Equals(r.FromTable, r.ToTable) && cmp.Equals(r.FromTable, at)))
                    if (seen.Add(r.ToTable)) queue.Enqueue(r.ToTable);
            }
            return seen;
        }

        private static string[] TimeIntelligenceNoDateFilters(ModelSpec spec)
        {
            if ((spec.TimeIntelligence?.Length ?? 0) == 0) return Array.Empty<string>();
            var date = spec.DateTable?.Name ?? "";
            return (spec.TimeIntelligenceBaseMeasures ?? Array.Empty<string>())
                .Select(b => spec.Measures.FirstOrDefault(m => m.Name == b))
                .Where(m => m != null && !FilteredFrom(spec, m.Table).Contains(date))
                .Select(m => $"{m.Name} on {m.Table} (the draft's date table is '{date}')")
                .ToArray();
        }

        // ---- N2 again: a bridge is not the fact ---------------------------------------------------------------------

        /// <summary>Ruling 1: the date table is the one the FACTS name, and a dimension's link never outranks a fact's. The
        /// draft counts a bridge (customer to account, keyed on two dimensions and a date range) as a fact, so its two
        /// date keys outrank the balances' one: dim_calendar is marked, and "Total BAL_AMT YTD" and every other
        /// time-intelligence measure run on a table that does not filter the balances at all. Silently wrong numbers.</summary>
        [Fact]
        public void A_date_table_only_a_bridge_names_is_not_marked_over_the_one_the_fact_names()
        {
            var spec = Draft(BridgeNamesTheCalendar());

            Assert.Equal("dim_date", spec.DateTable?.Name);
            Assert.Empty(TimeIntelligenceNoDateFilters(spec));
        }

        /// <summary>Whatever table the draft marks, it only drafts time intelligence over a measure whose fact that table
        /// actually filters. On each shape below at 33942de4 one fact's "YTD", "PY" and "YoY" run on a date table with no
        /// active path to it, so each returns the fact's plain total for every period, and nothing says so.</summary>
        [Theory]
        [MemberData(nameof(FactsTheDateTableDoesNotFilter))]
        public void Time_intelligence_is_drafted_only_over_facts_the_marked_date_table_filters(string shape, FabricSchema schema)
        {
            var spec = Draft(schema);

            var wrong = TimeIntelligenceNoDateFilters(spec);
            Assert.True(wrong.Length == 0, $"{shape}: time intelligence over a date table that does not filter: {string.Join("; ", wrong)}");
        }

        // ---- Ruling 1: none marked when no fact names one -----------------------------------------------------------

        public static IEnumerable<object[]> DateTablesOnlyADimensionNames()
        {
            // The sales carry their own date; only a promotion's start day names dim_date.
            yield return new object[] { "the promotion's start day", S(new[]
            {
                Fk("fact_sales", "PROMO_KEY", "dim_promo", "PROMO_KEY"),
                Fk("dim_promo", "START_DT_KEY", "dim_date", "DATE_KEY"),
            },
                T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                T("dim_promo", new[] { "PROMO_KEY" }, C("PROMO_KEY", "Int64"), C("PROMO_NM", "String"), C("START_DT_KEY", "Int64")),
                T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("PROMO_KEY", "Int64"), C("SALE_DT", "DateTime"), C("AMT", "Decimal"))) };
            // The sales' own date table keeps its date as text; only a store's opening day names dim_calendar.
            yield return new object[] { "the store's opening day", S(new[]
            {
                Fk("fact_sales", "SALE_DT_KEY", "dim_date", "DATE_KEY"),
                Fk("fact_sales", "STORE_KEY", "dim_store", "STORE_KEY"),
                Fk("dim_store", "OPEN_DAY_KEY", "dim_calendar", "DAY_KEY"),
            },
                T("dim_calendar", new[] { "DAY_KEY" }, C("DAY_KEY", "Int64"), C("DAY_DT", "DateTime")),
                T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("DATE_TXT", "String")),
                T("dim_store", new[] { "STORE_KEY" }, C("STORE_KEY", "Int64"), C("STORE_NM", "String"), C("OPEN_DAY_KEY", "Int64")),
                T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("SALE_DT_KEY", "Int64"), C("STORE_KEY", "Int64"), C("AMT", "Decimal"))) };
        }

        /// <summary>Ruling 1: "When no fact names any candidate, mark none and say so in the draft's note." At 33942de4 a
        /// candidate any fact REACHES through a dimension is eligible (LocalEngine.FindSourceDateTable, the walk), so the
        /// draft marks a date table no fact names and every time-intelligence measure follows a promotion's start day or a
        /// store's opening day instead of the sale. The build reports 0 errors and a note saying the table "already holds
        /// the dates".</summary>
        [Theory]
        [MemberData(nameof(DateTablesOnlyADimensionNames))]
        public void A_date_table_no_fact_names_is_not_marked_even_when_a_dimension_reaches_it(string shape, FabricSchema schema)
        {
            var spec = Draft(schema);

            Assert.True(string.IsNullOrEmpty(spec.DateTable?.Name), $"{shape}: the draft marked {spec.DateTable?.Name}, which no fact names");
            Assert.Empty(spec.TimeIntelligence ?? Array.Empty<string>());
        }

        /// <summary>No date table at all. The draft adds a CALENDAR table that nothing is linked to and drafts every
        /// time-intelligence measure over it, so each returns the plain total whatever the period (the base, dc8d3bd6, did
        /// the same for every source; the lane fixed it only for sources that have a date table). The lane's own note for
        /// the ruled case says it: "marking a table nothing filters by would make date calculations quietly wrong".</summary>
        [Fact]
        public void A_source_with_no_date_table_gets_no_time_intelligence_over_a_calendar_nothing_is_linked_to()
        {
            var spec = Draft(S(new[] { Fk("fact_sales", "STORE_KEY", "dim_store", "STORE_KEY") },
                T("dim_store", new[] { "STORE_KEY" }, C("STORE_KEY", "Int64"), C("STORE_NM", "String")),
                T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("STORE_KEY", "Int64"), C("SALE_DT", "DateTime"), C("AMT", "Decimal"))));

            var wrong = TimeIntelligenceNoDateFilters(spec);
            Assert.True(wrong.Length == 0, "time intelligence over a date table that does not filter: " + string.Join("; ", wrong));
        }

        // ---- The note's promise ------------------------------------------------------------------------------------

        /// <summary>The ruled no-date draft says: "Once a fact table is linked to it, the Plan offers to mark it, and to add
        /// the time-intelligence measures." Linked, the Plan offers neither: DATE-MARK (the only rule behind the Plan's
        /// "Mark the date table" row) sleeps until the model already uses time-intelligence DAX, and no Plan row adds
        /// time-intelligence measures at all. Whatever the fix (a true sentence, or a Plan that offers both), a promise
        /// the note makes must hold.</summary>
        [Fact]
        public async Task The_no_date_note_promises_only_what_the_plan_then_offers()
        {
            var spec = Draft(S(new[] { Fk("fact_sales", "STORE_KEY", "dim_store", "STORE_KEY") },
                T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                T("dim_store", new[] { "STORE_KEY" }, C("STORE_KEY", "Int64"), C("STORE_NM", "String")),
                T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("STORE_KEY", "Int64"), C("SALE_DT", "DateTime"), C("AMT", "Decimal"))));
            using var f = new DecisionFixture();
            await f.Engine.CreateModelAsync("NoDateR5", 1604);
            await f.Engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            var report = await f.Engine.BuildModelFromSpecAsync("human");
            await f.SaveAsAsync("NoDateR5.bim");
            f.UseNull();
            var words = string.Join(" ", report.Notes);

            await f.Engine.CreateRelationshipAsync("column:fact_sales/SALE_DT", "column:dim_date/CAL_DT", null, true, "human");
            var plan = await f.Engine.GetModelPlanAsync();

            if (words.Contains("the Plan offers to mark it", StringComparison.OrdinalIgnoreCase))
                Assert.Contains(plan.Items, i => (i.Title ?? "").Contains("date table", StringComparison.OrdinalIgnoreCase));
            if (words.Contains("time-intelligence measures", StringComparison.OrdinalIgnoreCase) && words.Contains("Plan offers", StringComparison.OrdinalIgnoreCase))
                Assert.Contains(plan.Items, i => (i.Title ?? "").Contains("time intelligence", StringComparison.OrdinalIgnoreCase)
                                              || (i.Title ?? "").Contains("time-intelligence", StringComparison.OrdinalIgnoreCase));
        }

        // ---- Ruling 2: an ask about a model that exists is never the Model Spec ----------------------------------------

        /// <summary>"new" is not a verb that makes anything, yet it is one of the words that switches the SQL rule on
        /// (AskRouteCatalog.MakingWords), and "new model" is a Model Spec noun on its own (the lane's first review-fix). So
        /// every ask about "the new model from the sql ..." the person has just built goes to the Model Spec with
        /// confidence 1 and the decision service never asked. Ruling 2's pattern names the making verbs (create, make,
        /// build, generate, set up, start) and lists "new" only between them and "model".</summary>
        [Theory]
        [InlineData("refresh the new model from the sql endpoint")]
        [InlineData("publish my new model from the sql warehouse")]
        [InlineData("why is the new model from sql slow?")]
        [InlineData("create a measure in the new model from the sql warehouse")]
        [InlineData("document the new model from the sql database")]
        [InlineData("add descriptions to the new model from the sql source")]
        [InlineData("who changed the new model from sql server yesterday?")]
        [InlineData("compare the new model from sql with the published one")]
        [InlineData("publish the new model")]
        [InlineData("undo my last change to the new model")]
        public void An_ask_about_the_new_model_the_person_already_has_is_not_the_model_spec(string ask)
        {
            Assert.NotEqual("spec", AskRoutePoint.MatchWords(ask)?.Tool);
            Assert.NotEqual("spec", PlanTargets.JumpFor(ask)?.Tool);
        }

        /// <summary>The same asks found their page on the lane's base (dc8d3bd6, measured): Published, Queries, History,
        /// Model quality, Diagram and Advanced modelling. At 33942de4 five go to the Model Spec and three find no place.</summary>
        [Theory]
        [InlineData("publish the new model", "deploy")]
        [InlineData("publish my new model from the sql warehouse", "deploy")]
        [InlineData("undo my last change to the new model", "history")]
        [InlineData("who changed the new model from sql server yesterday?", "history")]
        [InlineData("create a measure in the new model from the sql warehouse", "daxlab")]
        [InlineData("run the best practice check on my new model", "bpa")]
        [InlineData("check the relationships in my new model from the sql warehouse", "diagram")]
        [InlineData("make sure the new model from SQL has a date table", "advmodels")]
        public void Asks_about_the_new_model_still_find_the_page_they_found_on_the_base(string ask, string tool)
        {
            Assert.Equal(tool, AskRoutePoint.MatchWords(ask)?.Tool);
            Assert.Equal(tool, PlanTargets.JumpFor(ask)?.Tool);
        }

        /// <summary>The ruled pattern's own limit: a making word right before "the model from ... sql" that does not make a
        /// model ("make it faster", "start it refreshing", "make it use direct lake"). These fit the pattern word for word and
        /// go to the Model Spec; the brief says an ask about a model that exists never does. Kept apart so the coordinator can
        /// rule on them.</summary>
        [Theory]
        [InlineData("make the model from the sql database refresh faster")]
        [InlineData("make my model from sql server smaller")]
        [InlineData("start the model from the sql endpoint refreshing every hour")]
        [InlineData("make the new model from the fabric sql endpoint use direct lake")]
        public void A_making_word_that_changes_the_model_rather_than_makes_it_is_not_the_model_spec(string ask)
        {
            Assert.NotEqual("spec", AskRoutePoint.MatchWords(ask)?.Tool);
            Assert.NotEqual("spec", PlanTargets.JumpFor(ask)?.Tool);
        }

        /// <summary>The ask box's own door (route_ask on MCP, routeAsk on the RPC pipe): the question lands on the Model
        /// Spec with confidence 1 and the decision service is never asked.</summary>
        [Fact]
        public async Task The_ask_box_does_not_answer_a_question_about_the_new_model_with_the_model_spec()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteSqlR5.bim");
            f.UseNull();

            var result = await f.Engine.RouteAskAsync("why is the new model from sql slow?");

            Assert.NotEqual("spec", result.Tool);
        }

        // ---- New-model asks that find no place --------------------------------------------------------------------

        /// <summary>The brief: an ask for a new model from SQL must route to the Model Spec. These three did at round 4
        /// (687f0c8e) and find no place at 33942de4, because the words allowed between the making word and "model" are a
        /// closed list ("data", "tabular" and "first" are not on it). The bar drafts a task with no jump for each.</summary>
        [Theory]
        [InlineData("create a data model from sql server")]
        [InlineData("build a tabular model from our sql warehouse")]
        [InlineData("build my first model from a sql database")]
        public void A_plain_ask_for_a_new_model_from_sql_finds_the_model_spec(string ask)
        {
            Assert.Equal("spec", AskRoutePoint.MatchWords(ask)?.Tool);
            Assert.Equal("spec", PlanTargets.JumpFor(ask)?.Tool);
        }
    }
}
