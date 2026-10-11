// ask_data, the reader tuned on the natural set (the r-jev-unsure investigation, built for real on 28 Sep, Kane): each
// fix the investigation proved behind a test switch, now product, each with the test that failed before it.
//
// The rig is AskDataTests' own (partial class): the Contoso-style model, the fake data connection and scripted Jev.
// Probabilities are scripted where a rule reads them (two good readings, twin columns).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    public sealed partial class AskDataTests
    {
        private static DecisionQuestion Q(DecisionRequest r, string name) => r.Questions.Single(q => q.Name == name);

        /// <summary>Everything a set of requests sends: every snapshot and every question's words and options.</summary>
        private static string Wire(IEnumerable<DecisionRequest> requests)
        {
            var wire = new StringBuilder();
            foreach (var r in requests)
            {
                wire.Append(DecisionDigest.CanonicalSnapshotJson(r.Snapshot));
                foreach (var q in r.Questions)
                {
                    wire.Append('\n').Append(q.Name).Append('\n').Append(q.Instructions);
                    foreach (var o in q.Options) wire.Append('\n').Append(o.Id).Append('\n').Append(o.Criterion);
                }
            }
            return wire.ToString();
        }

        // ---- the wording of the first call ------------------------------------------------------------------

        /// <summary>ordinal, headline, nonone1, count. Asked for "the number" when the words ask for two, Jev put its weight
        /// on "none" (0.23 to 0.59 on the two-number questions); asked for the FIRST and the SECOND number it was sure on
        /// 15 of 16. With no number named ("how are we doing") the first measure says what to pick. The first measure has
        /// no "none": the fits question already says when nothing fits. And a named thing that only narrows is not a split.</summary>
        [Fact]
        public async Task The_first_call_asks_for_the_first_and_the_second_number_and_says_what_is_not_a_split()
        {
            using var rig = await BuildAsync(script: CoastScript);
            await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales and units by category" });
            var first = rig.Jev.Seen[0];
            var m1 = Q(first, JevQueryReader.MeasureQ(1));
            Assert.Contains("FIRST number", m1.Instructions);
            Assert.Contains("the only one, if it asks for one", m1.Instructions);
            Assert.Contains("names no number at all", m1.Instructions);
            Assert.False(m1.HasOption(JevQueryReader.None), "the first measure still offers none");
            var m2 = Q(first, JevQueryReader.MeasureQ(2));
            Assert.Contains("SECOND number", m2.Instructions);
            Assert.True(m2.HasOption(JevQueryReader.None));
            var splits = Q(first, JevQueryReader.SplitsQ);
            Assert.Contains("only narrow the numbers to one named thing (one product, one place, one year) are not a split", splits.Instructions);
            Assert.Contains(splits.Options, o => o.Id == "0" && o.Criterion.Contains("even when it narrows that total to one named thing"));
        }

        /// <summary>fitsdoubt. A lean to "this does not fit" below the bar was thrown away and the reading ran: the one
        /// harmful answer on fresh3 that should have been declined. It is said now (K16: it was a stop): the reading runs and the line says the model may not hold what was asked.</summary>
        [Fact]
        public async Task A_lean_to_does_not_fit_below_the_bar_runs_and_is_said()
        {
            using var rig = await BuildAsync(script: (r, q) => q.Name == JevQueryReader.Fits ? ("no", 0.3) : CoastScript(r, q));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("This model may not hold exactly what you asked.", a.Assumption ?? "");
            Assert.Single(rig.Data.Runs);
        }

        // ---- the model's own other names for a number (synhint, Kane decision 2) ------------------------------

        /// <summary>synhint. Synonyms were matched on this computer and never sent, so Jev saw "turnover" and only a
        /// description. Now the option says the model names that word as another name for the measure. Only a word the
        /// person typed goes: a synonym the question does not use is never sent.</summary>
        [Fact]
        public async Task A_synonym_the_question_uses_is_named_in_the_option_and_no_other_goes()
        {
            using var rig = await BuildAsync(script: CoastScript, synonyms: new[] { ("measure:Sales/Net Sales", new[] { "turnover", "takings" }) });
            await rig.Engine.AskDataAsync(new DataAsk { Question = "turnover by category" });
            var m1 = Q(rig.Jev.Seen[0], JevQueryReader.MeasureQ(1));
            var net = m1.Options.Single(o => o.Id == "m:Net Sales");
            Assert.Equal("the question asks for Net Sales (the model says the question's word 'turnover' is another name for Net Sales)", net.Criterion);
            Assert.Equal("the question asks for Units", m1.Options.Single(o => o.Id == "m:Units").Criterion);
            Assert.DoesNotContain("takings", Wire(rig.Jev.Seen), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The registry's purpose, which the settings list shows, says the synonym goes, and that no value does.</summary>
        [Fact]
        public void The_query_point_says_it_sends_the_models_other_names_a_question_uses()
        {
            var purpose = DecisionRegistry.Require(DecisionRegistry.QuerySummarize).Purpose;
            Assert.Contains("the model's own other names for a measure", purpose);
            Assert.Contains("only when the question uses that word", purpose);
            Assert.Contains("no value, row or count is sent", purpose);
        }

        // ---- two good readings: the top one is shown and the other named (Kane decision 1) ---------------------

        private static IReadOnlyDictionary<string, double> P(params (string Id, double P)[] ps) => ps.ToDictionary(x => x.Id, x => x.P);

        /// <summary>either, on the first measure. Torn between two real measures that together clear the bar, Jev's top
        /// one is shown and the other is named, with its parts ready: asking them runs it with no Jev call. The agent
        /// door gets both.</summary>
        [Fact]
        public async Task Torn_between_two_measures_shows_the_top_one_names_the_other_and_one_ask_switches()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => q.Name == JevQueryReader.MeasureQ(1) ? ("m:Net Sales", 0.45) : CoastScript(r, q);
            using var rig = await BuildAsync(script: Script);
            rig.Jev.Probabilities = (r, q) => q.Name == JevQueryReader.MeasureQ(1) ? P(("m:Net Sales", 0.6), ("m:Units", 0.34), ("m:Budget Amount", 0.06)) : null;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal("Net Sales by Category, Region = Coast, Year = 2025, top 3 by Net Sales", a.Reading);
            var other = Assert.Single(a.Others);
            Assert.Equal("Showing Net Sales. You may have meant Units.", other.Line);
            Assert.Equal("Units", other.Instead);
            Assert.Equal(new[] { "Units" }, other.Parts.Measures);
            Assert.Equal("Units by Category, Region = Coast, Year = 2025, top 3 by Units", other.Reading);
            Assert.DoesNotContain("You may have meant", a.Note ?? "");

            // One ask of the other parts switches, with no Jev call.
            var calls = rig.Jev.Seen.Count;
            var switched = await rig.Engine.AskDataAsync(other.Parts);
            Assert.Equal("you", switched.ReadBy);
            Assert.Equal(other.Reading, switched.Reading);
            Assert.Equal(calls, rig.Jev.Seen.Count);

            // The agent door returns both readings.
            var asked = await McpTools.AskData(rig.Engine, question: CoastQuestion);
            Semanticus.Engine.ApprovalLedger.Approve(asked.ApprovalId, "human");
            var viaMcp = await McpTools.AskData(rig.Engine, question: CoastQuestion);
            Assert.Equal(a.Reading, viaMcp.Reading);
            Assert.Equal(other.Reading, Assert.Single(viaMcp.Others).Reading);
        }

        /// <summary>Two measures that together do not clear the bar are still doubt (K16: the best guess runs and the line says
        /// which number it used); and the other is never "none".</summary>
        [Fact]
        public async Task Two_measures_that_together_miss_the_bar_run_the_best_guess_and_say_which()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => q.Name == JevQueryReader.MeasureQ(1) ? ("m:Net Sales", 0.3) : CoastScript(r, q);
            using var rig = await BuildAsync(script: Script);
            rig.Jev.Probabilities = (r, q) => q.Name == JevQueryReader.MeasureQ(1) ? P(("m:Net Sales", 0.5), ("m:Units", 0.3), ("m:Budget Amount", 0.2)) : null;
            // No top N: a sure ranking measure would settle the number on its own (the next test).
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by category in the coast region for 2025" });
            Assert.True(Answered(a), Seen(a));
            Assert.StartsWith("Using Net Sales.", a.Assumption);
            Assert.Empty(a.Unsure);
        }

        /// <summary>either, on the split: the other column must be the same kind of thing, a column of the same table
        /// holding the same kind of value. "You may have meant OrderNumber" for a month split was the silly one.</summary>
        [Fact]
        public async Task Torn_between_two_splits_names_the_other_only_when_it_is_the_same_kind_of_column()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => q.Name == JevQueryReader.GroupQ(1) ? ("c:Product[Category]", 0.5) : CoastScript(r, q);
            using var rig = await BuildAsync(script: Script);
            rig.Jev.Probabilities = (r, q) => q.Name == JevQueryReader.GroupQ(1) ? P(("c:Product[Category]", 0.62), ("c:Product[ProductName]", 0.3), ("other", 0.08)) : null;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by product category in the coast region for 2025" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            var other = Assert.Single(a.Others);
            Assert.Equal("Split by Category. You may have meant ProductName.", other.Line);
            Assert.Equal(new[] { "'Product'[ProductName]" }, other.Parts.GroupBy);

            // A column of another table is not the same kind of thing: the best guess runs and no other is named.
            using var rig2 = await BuildAsync(script: Script);
            rig2.Jev.Probabilities = (r, q) => q.Name == JevQueryReader.GroupQ(1) ? P(("c:Product[Category]", 0.62), ("c:Store[Region]", 0.3), ("other", 0.08)) : null;
            var b = await rig2.Engine.AskDataAsync(new DataAsk { Question = "net sales by product category in the coast region for 2025" });
            Assert.True(Answered(b), Seen(b));
            Assert.Contains("Split by Category.", b.Assumption ?? "");
            Assert.DoesNotContain(b.Others ?? Array.Empty<DataOther>(), o => o.Line.Contains("Region"));
        }

        /// <summary>either on the number a top N ranks by, and topmeasure: when Jev is sure what ranks a top N, that is
        /// the number, even when the first measure question was not sure.</summary>
        [Fact]
        public async Task The_number_a_top_N_ranks_by_settles_the_measure_and_a_torn_ranking_names_the_other()
        {
            (string, double)? Sure(DecisionRequest r, DecisionQuestion q) => q.Name == JevQueryReader.MeasureQ(1) ? ("m:Net Sales", 0.5) : CoastScript(r, q);
            using var rig = await BuildAsync(script: Sure);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal("Net Sales by Category, Region = Coast, Year = 2025, top 3 by Net Sales", a.Reading);

            (string, double)? Torn(DecisionRequest r, DecisionQuestion q) => q.Name == JevQueryReader.TopByQ ? ("m:Net Sales", 0.5) : CoastScript(r, q);
            using var rig2 = await BuildAsync(script: Torn);
            rig2.Jev.Probabilities = (r, q) => q.Name == JevQueryReader.TopByQ ? P(("m:Net Sales", 0.6), ("m:Units", 0.35), ("none", 0.05)) : null;
            var b = await rig2.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.Equal(DataAnswer.Answered, b.Outcome);
            var other = Assert.Single(b.Others);
            Assert.Equal("Ranked by Net Sales. You may have meant Units.", other.Line);
            Assert.Equal("Units", other.Parts.TopBy);
            Assert.Equal(new[] { "Net Sales", "Units" }, other.Parts.Measures);
        }

        // ---- twin columns: the same words in the same-named column of two tables (twin) ---------------------------

        /// <summary>twin, on a filter. "coast" matched Store Region and Customer Region and was asked once per column,
        /// so its weight was shared and both filters could run together (5 harmful at bar zero). One question for the
        /// pair now, the pair's weight added together, one filter taken, and the other named.</summary>
        [Fact]
        public async Task A_value_in_twin_columns_is_asked_once_and_one_filter_runs_with_the_other_named()
        {
            string storeId = null, customerId = null;
            bool Twin(DecisionRequest r, DecisionQuestion q)
            {
                storeId ??= MatchId(r, "Store[Region]");
                customerId ??= MatchId(r, "Customer[Region]");
                return q.Name == JevQueryReader.FilterQ(storeId ?? "-") || q.Name == JevQueryReader.FilterQ(customerId ?? "-");
            }
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => Twin(r, q) ? (QueryOps.Is + "|" + storeId, 0.55) : CoastScript(r, q);
            using var rig = await BuildAsync(script: Script, twins: true);
            rig.Jev.Probabilities = (r, q) => Twin(r, q) ? P((QueryOps.Is + "|" + storeId, 0.6), (QueryOps.Is + "|" + customerId, 0.36), ("none", 0.04)) : null;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });

            var first = rig.Jev.Seen[0];
            var twin = Assert.Single(first.Questions, q => Twin(first, q));
            Assert.Contains("Either column can be meant", twin.Instructions);
            Assert.True(twin.HasOption(QueryOps.Is + "|" + customerId));

            Assert.Equal(DataAnswer.Answered, a.Outcome);
            var only = Assert.Single(a.Parts.Filters, f => f.Column.Contains("[Region]"));
            Assert.Equal("'Store'[Region]", only.Column);
            var other = Assert.Single(a.Others);
            Assert.Contains("You may have meant", other.Line);
            Assert.Contains(other.Parts.Filters, f => f.Column == "'Customer'[Region]" && f.Values.SequenceEqual(new[] { "Coast" }));
            Assert.DoesNotContain(other.Parts.Filters, f => f.Column == "'Store'[Region]");
        }

        /// <summary>twin, on a split: same-named columns of two tables on the shortlist share Jev's weight (0.78 against
        /// 0.13); added together they clear the bar, the top one is taken and the other named.</summary>
        [Fact]
        public async Task Twin_columns_on_the_split_shortlist_add_their_weight_together()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => q.Name == JevQueryReader.GroupQ(1) ? ("c:Store[Region]", 0.7) : CoastScript(r, q);
            using var rig = await BuildAsync(script: Script, twins: true);
            rig.Jev.Probabilities = (r, q) => q.Name == JevQueryReader.GroupQ(1) ? P(("c:Store[Region]", 0.76), ("c:Customer[Region]", 0.2), ("other", 0.04)) : null;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by region for 2025" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal(new[] { "'Store'[Region]" }, a.Parts.GroupBy);
            Assert.Equal(new[] { "'Customer'[Region]" }, Assert.Single(a.Others).Parts.GroupBy);
        }

        // ---- the split shortlist leaves out money amounts (noamounts) --------------------------------------------

        [Fact]
        public async Task The_split_shortlist_offers_no_decimal_amount_column()
        {
            using var rig = await BuildAsync();
            var cat = await CatalogAsync(rig);
            var first = new JevQueryReader.Reading { Splits = 1 };
            first.Spec.Measures.Add(cat.Measure("Net Sales"));
            var split = JevQueryReader.BuildSplit("net sales by amount and by city", cat, first);
            Assert.Contains(split.Columns, c => c.Id == "Store[City]");
            Assert.DoesNotContain(split.Columns, c => c.Kind == "decimal");
            Assert.True(QuerySplitShortlist.For("net sales by amount", cat, new[] { cat.Measure("Net Sales") }).Any(c => c.Id == "Sales[Amount]"),
                "the shortlist itself no longer finds Sales[Amount], so this test proves nothing");
        }

        // ---- "retail vs trade": two values of one column side by side (compare) --------------------------------

        [Fact]
        public async Task Two_values_of_one_column_compared_are_both_kept_and_shown_side_by_side()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => q.Name == JevQueryReader.SplitsQ ? ("0", 0.9) : CoastScript(r, q);
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales coast vs north" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.DoesNotContain(rig.Jev.Seen[0].Questions, q => q.Name.StartsWith(JevQueryReader.Prefix + "filter:", StringComparison.Ordinal));
            var f = Assert.Single(a.Parts.Filters);
            Assert.Equal(new[] { "Coast", "North" }, f.Values.OrderBy(x => x));
            Assert.Equal(new[] { "'Store'[Region]" }, a.Parts.GroupBy);
            Assert.Contains("side by side, split by Region", a.Note);
        }

        // ---- periods: "last month" on a model with a calendar, and without one (period) -------------------------

        (string, double)? Total(DecisionRequest r, DecisionQuestion q) =>
            q.Name == JevQueryReader.MeasureQ(1) ? ("m:Net Sales", 0.97) : q.Name == JevQueryReader.SplitsQ ? ("0", 0.95) : null;

        /// <summary>With a calendar, "last month" is read against the model's own latest date, and said: the data runs to
        /// 31 December 2025, a whole month, so last month is December 2025. Before, the words were dropped and the
        /// all-time total ran: a wrong number shown as the answer.</summary>
        [Fact]
        public async Task Last_month_is_read_against_the_models_latest_date_and_said()
        {
            using var rig = await BuildAsync(script: Total, calendar: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how did net sales do last month" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            var f = Assert.Single(a.Parts.Filters);
            Assert.Equal("'Date'[YearMonth]", f.Column);
            Assert.Equal(new[] { "2025-12" }, f.Values);
            Assert.Equal("This model's data runs to 31 December 2025, so 'last month' is read as YearMonth 2025-12.", a.Note);
            Assert.Contains(rig.Data.Queries, q => q.Contains("NOT ISBLANK ( [Net Sales] )"));

            // A month the data is still in is this month; last month is the one before it. Both asked: side by side.
            using var rig2 = await BuildAsync(script: Total, calendar: true);
            rig2.Data.Latest = new DateTime(2025, 12, 15);
            var b = await rig2.Engine.AskDataAsync(new DataAsk { Question = "net sales this month compared with last month" });
            Assert.Equal(DataAnswer.Answered, b.Outcome);
            Assert.Equal(new[] { "2025-11", "2025-12" }, Assert.Single(b.Parts.Filters).Values.OrderBy(x => x));
            Assert.Equal(new[] { "'Date'[YearMonth]" }, b.Parts.GroupBy);
            Assert.Contains("15 December 2025", b.Note);

            // A year: the data runs to the last day of 2025, so last year is 2025.
            using var rig3 = await BuildAsync(script: Total, calendar: true);
            var c = await rig3.Engine.AskDataAsync(new DataAsk { Question = "net sales last year" });
            var y = Assert.Single(c.Parts.Filters);
            Assert.Equal("'Date'[Year]", y.Column);
            Assert.Equal(new[] { "2025" }, y.Values);
        }

        /// <summary>"compared to last year" compares this year with last year: both are read, side by side. Found on the
        /// tuning half (nn-037), where only last year was read. A compare word that does not lead into the period
        /// ("last month vs units") compares something else, and only the period named is read.</summary>
        [Fact]
        public async Task Compared_to_last_year_reads_this_year_and_last_year_side_by_side()
        {
            using var rig = await BuildAsync(script: Total, calendar: true);
            rig.Data.Latest = new DateTime(2025, 12, 15);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales compared to last year" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal(new[] { "2024", "2025" }, Assert.Single(a.Parts.Filters).Values.OrderBy(x => x));
            Assert.Equal(new[] { "'Date'[Year]" }, a.Parts.GroupBy);

            Assert.Single(QueryPeriods.Read("net sales last month vs units").Periods);
            Assert.Equal(2, QueryPeriods.Read("how are we doing versus the previous month").Periods.Count);
        }

        /// <summary>A word inside a value the question matched is that value, not a time: "summer sale versus no discount"
        /// names a promotion, and was once split by month for "summer" (ncr-016 on the tuning half).</summary>
        [Fact]
        public void A_time_word_inside_a_matched_value_is_the_value_not_a_period()
        {
            Assert.Null(QueryPeriods.Read("summer sale versus no discount", new[] { "summer sale", "no discount" }));
            Assert.NotNull(QueryPeriods.Read("sales over the summer", new[] { "sales" }));
            Assert.Null(QueryPeriods.Read("sales in the Last Year Clearance promotion", new[] { "Last Year Clearance" }));
        }

        /// <summary>With no calendar the engine cannot know which month is meant, so the numbers are split by the month
        /// of each year and it says so, instead of the all-time total.</summary>
        [Fact]
        public async Task Without_a_calendar_last_month_splits_by_each_month_of_each_year_and_says_so()
        {
            using var rig = await BuildAsync(script: Total);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how did net sales do last month" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal(new[] { "'Date'[Year]", "'Date'[MonthName]" }, a.Parts.GroupBy);
            Assert.Empty(a.Parts.Filters);
            Assert.Equal("Semanticus cannot tell which month you mean by 'last month', so the numbers are split by Year and MonthName. Read off the one you meant.", a.Note);
            Assert.DoesNotContain(rig.Data.Queries, q => q.Contains("MAXX"));
        }

        /// <summary>"summer" is a period too, and which months it is depends on where you are: split, and said.</summary>
        [Fact]
        public async Task Summer_is_a_period_split_by_month_and_said()
        {
            using var rig = await BuildAsync(script: Total, calendar: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how did we go over the summer for net sales" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal(new[] { "'Date'[YearMonth]" }, a.Parts.GroupBy);
            Assert.Contains("which months you mean by 'summer'", a.Note);
        }

        /// <summary>"how was December" matched December in MonthName, and the total added December of both years. When a
        /// month name is in more than one year of the data, the numbers are split by year, and said.</summary>
        [Fact]
        public async Task A_month_name_in_more_than_one_year_is_split_by_year_not_added_up()
        {
            using var rig = await BuildAsync(script: (r, q) => q.Name.StartsWith(JevQueryReader.Prefix + "filter:", StringComparison.Ordinal) ? (QueryOps.Is, 0.95) : Total(r, q));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how was December for net sales" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Contains(a.Parts.Filters, f => f.Column == "'Date'[MonthName]" && f.Values.SequenceEqual(new[] { "December" }));
            Assert.Equal(new[] { "'Date'[Year]" }, a.Parts.GroupBy);
            Assert.Contains("more than one year", a.Note);
        }

        /// <summary>"the 3 biggest months ever" ranked MonthName, which adds the same month of every year together. A
        /// ranking of months is a ranking of each month of each year: YearMonth when the model has it.</summary>
        [Fact]
        public async Task A_ranking_of_months_ranks_each_month_of_each_year()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => q.Name == JevQueryReader.GroupQ(1) ? ("c:Date[MonthName]", 0.95)
                : q.Name == JevQueryReader.TopQ ? ("n:3", 0.95) : q.Name == JevQueryReader.TopByQ ? ("m:Net Sales", 0.95) : q.Name == JevQueryReader.TopEndQ ? ("highest", 0.95)
                : q.Name == JevQueryReader.SplitsQ ? ("1", 0.95) : q.Name == JevQueryReader.MeasureQ(1) ? ("m:Net Sales", 0.97) : null;
            using var rig = await BuildAsync(script: Script, calendar: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "our 3 biggest months ever for net sales" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal(new[] { "'Date'[YearMonth]" }, a.Parts.GroupBy);
            Assert.Contains("each month of each year", a.Note);
        }

        // ---- the value matcher: "bike" is the value "Bikes" -----------------------------------------------------

        [Fact]
        public void A_singular_word_matches_its_plural_value()
        {
            var cat = new QueryCatalog();
            cat.Columns.Add(new QueryColumn { Table = "Product", Name = "Category", Kind = "text" });
            var values = new Dictionary<string, object[]> { ["Product[Category]"] = new object[] { "Bikes", "Accessories", "Clothing" } };
            var bike = Assert.Single(ValueMatcher.Match("bike sales last quarter", cat, values));
            Assert.Equal("bike", bike.Words);
            Assert.Equal(new object[] { "Bikes" }, bike.Values);
            Assert.Equal(new object[] { "Accessories" }, Assert.Single(ValueMatcher.Match("accessory revenue", cat, values)).Values);
            Assert.Empty(ValueMatcher.Match("clothes by month", cat, values));
        }

        // ---- VERIFY (verifier, m11/jev-query-tune round 1, 28 Sep): each test below fails on 29368c52 and shows an answer
        // that would put a wrong number in front of a person. Left failing on purpose for the builder. -------------------

        private static (string, double)? VerifySure(DecisionRequest r, DecisionQuestion q) =>
            q.Name == JevQueryReader.MeasureQ(1) ? ("m:Net Sales", 0.97)
            : q.Name == JevQueryReader.SplitsQ ? ("0", 0.9)
            : q.Name.StartsWith(JevQueryReader.Prefix + "filter:", StringComparison.Ordinal) ? (QueryOps.Is, 0.95) : null;

        private static HashSet<string> VerifyMatchIds(DecisionRequest r, string column)
        {
            var ids = new HashSet<string>();
            foreach (var m in ((IEnumerable<object>)r.Snapshot["value_matches"]).Cast<IDictionary<string, object>>())
                if ((string)m["column"] == column) ids.Add((string)m["id"]);
            return ids;
        }

        /// <summary>VERIFY. "coast vs north" on a model whose Region is in two tables (Store and Customer, as Northstar's
        /// is). CompareGroups leaves a twinned column to the twin question, so each value lands on one Region as its own
        /// filter, AddFilter merges the two into Region = Coast or North, and nothing adds the split: ONE total for the two
        /// places added together, for a question that compares them. The same script on a column with no twin shows them
        /// side by side (Two_values_of_one_column_compared_are_both_kept_and_shown_side_by_side).</summary>
        [Fact]
        public async Task Verify_two_values_compared_in_a_twinned_column_are_not_added_into_one_total()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q)
            {
                if (q.Name == JevQueryReader.MeasureQ(1)) return ("m:Net Sales", 0.97);
                if (q.Name == JevQueryReader.SplitsQ) return ("0", 0.9);
                if (q.Name.StartsWith(JevQueryReader.Prefix + "filter:", StringComparison.Ordinal))
                {
                    var store = VerifyMatchIds(r, "Store[Region]");
                    var pick = q.Options.FirstOrDefault(o => o.Id.StartsWith(QueryOps.Is + "|", StringComparison.Ordinal) && store.Contains(o.Id.Substring(QueryOps.Is.Length + 1)));
                    return (pick?.Id ?? QueryOps.Is, 0.95);
                }
                return null;
            }
            using var rig = await BuildAsync(script: Script, twins: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales coast vs north" });
            Assert.False(a.Outcome == DataAnswer.Answered && !a.Parts.GroupBy.Any(g => g.EndsWith("[Region]", StringComparison.Ordinal))
                && a.Parts.Filters.Any(f => f.Column.EndsWith("[Region]", StringComparison.Ordinal) && f.Values.Length == 2),
                "coast vs north came back as one total for both: " + a.Reading);
        }

        /// <summary>VERIFY, the known miss nca-015. "did 2025 beat 2024" names two years to compare; both are kept as one
        /// filter (Year = 2025 or 2024) and, with no split, the answer is the two years added together. Held-out nca-015
        /// did exactly this live (harmful). A comparison of two values of one column must show them side by side, or ask.</summary>
        [Fact]
        public async Task Verify_two_years_compared_with_beat_are_not_added_into_one_total()
        {
            using var rig = await BuildAsync(script: VerifySure);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "did 2025 beat 2024 on net sales" });
            Assert.False(a.Outcome == DataAnswer.Answered && !a.Parts.GroupBy.Contains("'Date'[Year]")
                && a.Parts.Filters.Any(f => f.Column == "'Date'[Year]" && f.Values.Length == 2),
                "two years compared came back as one total for both: " + a.Reading);
        }

        /// <summary>VERIFY. No calendar, and the question already splits by something: QueryPeriods.Split has room for one
        /// more column only, so Naming drops Year and "last month" is split by MonthName alone, which adds that month of
        /// every year together, and the note tells the person to read last month off it.</summary>
        [Fact]
        public async Task Verify_last_month_beside_another_split_is_not_read_off_a_month_name_that_adds_every_year()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.SplitsQ ? ("1", 0.95) : q.Name == JevQueryReader.GroupQ(1) ? ("c:Product[Category]", 0.93)
                : q.Name == JevQueryReader.MeasureQ(1) ? ("m:Net Sales", 0.97) : null;
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by category last month" });
            Assert.False(a.Outcome == DataAnswer.Answered && a.Parts.GroupBy.Contains("'Date'[MonthName]")
                && !a.Parts.GroupBy.Contains("'Date'[Year]") && !a.Parts.Filters.Any(f => f.Column == "'Date'[Year]"),
                "last month was split by MonthName alone (every year's month added together): " + a.Reading + " / " + a.Note);
        }

        /// <summary>VERIFY. The period rule reads month, quarter and year only. "last week", "yesterday", "this week", "last
        /// financial year" and "month over month" are dropped and the all-time total runs with nothing said: the exact wrong
        /// number the period fix exists to stop. So does "last quarter" on a model with no quarter column (Split returns
        /// without a note when no column names the period).</summary>
        [Theory]
        [InlineData("how did net sales do last week", true)]
        [InlineData("net sales yesterday", true)]
        [InlineData("net sales this week", true)]
        [InlineData("net sales last financial year", true)]
        [InlineData("net sales month over month", true)]
        [InlineData("net sales last quarter", false)]
        public async Task Verify_time_words_the_engine_cannot_read_never_run_the_all_time_total_silently(string question, bool calendar)
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.MeasureQ(1) ? ("m:Net Sales", 0.97) : q.Name == JevQueryReader.SplitsQ ? ("0", 0.95) : null;
            using var rig = await BuildAsync(script: Script, calendar: calendar);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            Assert.False(a.Outcome == DataAnswer.Answered && a.Parts.Filters.Length == 0 && a.Parts.GroupBy.Length == 0 && string.IsNullOrEmpty(a.Note) && string.IsNullOrEmpty(a.Assumption),
                "'" + question + "' ran the all-time total with nothing said: " + a.Reading);
        }

        /// <summary>VERIFY. "last month" read against the latest date lands on YearMonth 2025-12 while the words also named
        /// the year 2024: ResolveColumns only checks the columns it will filter, so Year = 2024 and YearMonth = 2025-12 both
        /// run and leave nothing to count. Contradiction() refuses such a pair only inside one column, so the answer is an
        /// empty result that reads like "there were none" (the very thing Contradiction exists to stop).</summary>
        [Fact]
        public async Task Verify_last_month_with_a_year_named_does_not_run_filters_that_cannot_both_hold()
        {
            using var rig = await BuildAsync(script: VerifySure, calendar: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales last month in 2024" });
            var year = a.Parts.Filters.FirstOrDefault(f => f.Column == "'Date'[Year]");
            var month = a.Parts.Filters.FirstOrDefault(f => f.Column == "'Date'[YearMonth]");
            Assert.False(a.Outcome == DataAnswer.Answered && year != null && month != null
                && !month.Values.Any(v => year.Values.Any(y => v.StartsWith(y, StringComparison.Ordinal))),
                "filters that cannot both hold ran: " + a.Reading);
        }

        /// <summary>VERIFY. "by category, coast vs north": when Jev counts the one split the words name (category) and not
        /// the comparison, Read subtracts the compared column from that count anyway, so no split is asked and category
        /// is dropped without a word; the answer is Region totals for a question that asked for categories.</summary>
        [Fact]
        public async Task Verify_a_comparison_does_not_drop_a_split_the_question_names()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.SplitsQ ? ("1", 0.95) : q.Name == JevQueryReader.GroupQ(1) ? ("c:Product[Category]", 0.93)
                : q.Name == JevQueryReader.MeasureQ(1) ? ("m:Net Sales", 0.97) : null;
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by category, coast vs north" });
            Assert.False(a.Outcome == DataAnswer.Answered && !a.Parts.GroupBy.Contains("'Product'[Category]"),
                "the category split the question names was dropped: " + a.Reading);
        }

        // ---- ROUND 2 (Kane's ruling, 28 Sep: never a silent guess; show the top reading and name the other). A data
        // question never runs a number the question did not ask for without saying so. Each test below failed first. ---

        /// <summary>The option of a twin filter question that keeps the rows of one table's column.</summary>
        private static string TwinOption(DecisionRequest r, DecisionQuestion q, string column)
        {
            var ids = VerifyMatchIds(r, column);
            return q.Options.Select(o => o.Id).FirstOrDefault(id => id.StartsWith(QueryOps.Is + "|", StringComparison.Ordinal) && ids.Contains(id.Substring(QueryOps.Is.Length + 1)));
        }

        private static bool IsFilterQ(DecisionQuestion q) => q.Name.StartsWith(JevQueryReader.Prefix + "filter:", StringComparison.Ordinal);

        /// <summary>"coast vs north" where Region is in Store and in Customer. The two places are shown side by side on
        /// ONE table's Region, the one Jev leaned to over both words, and the other table's Region is named with its own
        /// parts, one ask away. Never one total, and never Coast on one table beside North on the other.</summary>
        [Fact]
        public async Task A_comparison_in_a_twinned_column_shows_one_tables_values_side_by_side_and_names_the_other()
        {
            (string, double)? Leaning(DecisionRequest r, DecisionQuestion q) =>
                IsFilterQ(q) ? (TwinOption(r, q, "Store[Region]"), 0.55) : q.Name == JevQueryReader.SplitsQ ? ("0", 0.9) : Total(r, q);
            using var rig = await BuildAsync(script: Leaning, twins: true);
            rig.Jev.Probabilities = (r, q) => IsFilterQ(q) ? P((TwinOption(r, q, "Store[Region]"), 0.55), (TwinOption(r, q, "Customer[Region]"), 0.41), ("none", 0.04)) : null;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales coast vs north" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal(new[] { "'Store'[Region]" }, a.Parts.GroupBy);
            var f = Assert.Single(a.Parts.Filters);
            Assert.Equal("'Store'[Region]", f.Column);
            Assert.Equal(new[] { "Coast", "North" }, f.Values.OrderBy(x => x));
            Assert.Contains("side by side", a.Note);
            var other = Assert.Single(a.Others);
            Assert.Equal("Showing Store Region for 'coast' and 'north'. You may have meant Customer Region.", other.Line);
            Assert.Equal(new[] { "'Customer'[Region]" }, other.Parts.GroupBy);
            Assert.Equal(new[] { "Coast", "North" }, Assert.Single(other.Parts.Filters, x => x.Column == "'Customer'[Region]").Values.OrderBy(x => x));
            Assert.DoesNotContain(other.Parts.Filters, x => x.Column == "'Store'[Region]");

            // Jev sure of a different table for each word: still one table for both, and the other named.
            (string, double)? Split(DecisionRequest r, DecisionQuestion q) =>
                IsFilterQ(q) ? (TwinOption(r, q, q.Instructions.Contains("'coast'") ? "Store[Region]" : "Customer[Region]"), 0.95) : Leaning(r, q);
            using var rig2 = await BuildAsync(script: Split, twins: true);
            var b = await rig2.Engine.AskDataAsync(new DataAsk { Question = "net sales coast vs north" });
            Assert.Equal(DataAnswer.Answered, b.Outcome);
            var only = Assert.Single(b.Parts.Filters);
            Assert.Equal(new[] { "Coast", "North" }, only.Values.OrderBy(x => x));
            Assert.Equal(new[] { only.Column }, b.Parts.GroupBy);
            Assert.Single(b.Others);

            // Not sure the words narrow a Region at all: the best guess runs and the line says so (K16).
            using var rig3 = await BuildAsync(script: Leaning, twins: true);
            rig3.Jev.Probabilities = (r, q) => IsFilterQ(q) ? P((TwinOption(r, q, "Store[Region]"), 0.4), (TwinOption(r, q, "Customer[Region]"), 0.3), ("none", 0.3)) : null;
            var c = await rig3.Engine.AskDataAsync(new DataAsk { Question = "net sales coast vs north" });
            Assert.True(Answered(c), Seen(c));
            Assert.False(string.IsNullOrEmpty(c.Assumption), Seen(c));
        }

        /// <summary>Two values of one column the words name are compared, whatever word compares them ("beat", "and"): they
        /// are shown side by side, never added into one total. With no room left for the split, the line says so.</summary>
        [Fact]
        public async Task Two_values_of_one_column_are_shown_side_by_side_whatever_word_compares_them()
        {
            using var rig = await BuildAsync(script: VerifySure);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "did 2025 beat 2024 on net sales" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal(new[] { "'Date'[Year]" }, a.Parts.GroupBy);
            Assert.Equal(new[] { "2024", "2025" }, Assert.Single(a.Parts.Filters).Values.OrderBy(x => x));
            Assert.Equal("Showing '2025' and '2024' side by side, split by Year.", a.Note);

            using var rig2 = await BuildAsync(script: VerifySure);
            var b = await rig2.Engine.AskDataAsync(new DataAsk { Question = "net sales for coast and north" });
            Assert.Equal(DataAnswer.Answered, b.Outcome);
            Assert.Equal(new[] { "'Store'[Region]" }, b.Parts.GroupBy);
            Assert.Contains("side by side", b.Note);

            // Two splits already: no room to show them side by side, so the line says so and the run goes on.
            (string, double)? Two(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.SplitsQ ? ("2", 0.95)
                : q.Name == JevQueryReader.GroupQ(1) ? ("c:Product[Category]", 0.93)
                : q.Name == JevQueryReader.GroupQ(2) ? (q.Options.First(o => o.Id.Contains("Product[ProductName]") || o.Id == JevQueryReader.Other).Id, 0.93)
                : q.Name == JevQueryReader.GroupTableQ(2) ? ("t:Product", 0.93)
                : q.Name == JevQueryReader.GroupInQ(2) ? ("c:Product[ProductName]", 0.93) : VerifySure(r, q);
            using var rig3 = await BuildAsync(script: Two);
            var c = await rig3.Engine.AskDataAsync(new DataAsk { Question = "net sales by category and product, coast vs north" });
            Assert.True(Answered(c), Seen(c));
            Assert.Contains("side by side", c.Assumption ?? "");
            Assert.Single(rig3.Data.Runs);
        }

        /// <summary>No calendar and one split already, so the month cannot be split with its year: "last month" is not read
        /// off a month name that adds every year's month together. With a column of days on the rows it is filtered to
        /// the latest month by that column, and said; with none, the line says it could not apply last month and the
        /// run covers every date (K16).</summary>
        [Fact]
        public async Task Last_month_beside_another_split_with_no_calendar_is_filtered_by_the_date_column_or_said_not_applied()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.SplitsQ ? ("1", 0.95) : q.Name == JevQueryReader.GroupQ(1) ? ("c:Product[Category]", 0.93) : Total(r, q);
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by category last month" });
            Assert.True(Answered(a), Seen(a));
            Assert.Equal("Could not apply 'last month', so this covers every date.", a.Assumption);
            Assert.Single(rig.Data.Runs);

            using var rig2 = await BuildAsync(script: Script, orderDate: true);
            var b = await rig2.Engine.AskDataAsync(new DataAsk { Question = "net sales by category last month" });
            Assert.Equal(DataAnswer.Answered, b.Outcome);
            Assert.Equal(new[] { "'Product'[Category]" }, b.Parts.GroupBy);
            Assert.Contains(b.Parts.Filters, f => f.Column == "'Sales'[OrderDate]" && f.Op == QueryOps.From && f.Values.SequenceEqual(new[] { "2025-12-01" }));
            Assert.Contains(b.Parts.Filters, f => f.Column == "'Sales'[OrderDate]" && f.Op == QueryOps.Before && f.Values.SequenceEqual(new[] { "2026-01-01" }));
            Assert.Equal("This model's data runs to 31 December 2025, so 'last month' is read as 1 December to 31 December 2025, by OrderDate.", b.Note);
            Assert.Contains("DATE ( 2025, 12, 1 )", b.Dax);
        }

        /// <summary>Time words the model's column of days allows are read against its latest date and said: a week runs
        /// Monday to Sunday, and the day the data ends on is its last day. The rest are never the all-time total
        /// in silence (K16): the line names the words it could not apply and says the run covers every date.</summary>
        [Theory]
        [InlineData("how did net sales do last week", true, "2025-12-22", "2025-12-29", "'last week' is read as Monday 22 December to Sunday 28 December 2025.")]
        [InlineData("net sales this week", true, "2025-12-29", "2026-01-05", "'this week' is read as Monday 29 December 2025 to Sunday 4 January 2026.")]
        [InlineData("net sales yesterday", true, "2025-12-31", "2026-01-01", "'yesterday' is read as 31 December 2025, its last day.")]
        [InlineData("net sales last quarter", true, "2025-10-01", "2026-01-01", "'last quarter' is read as 1 October to 31 December 2025.")]
        [InlineData("net sales last financial year", true, null, null, "'last financial year'")]
        [InlineData("net sales month over month", true, null, null, "'month over month'")]
        [InlineData("net sales this week vs last week", true, null, null, "'this week' and 'last week'")]
        [InlineData("net sales last quarter", false, null, null, "'last quarter'")]
        [InlineData("net sales last week", false, null, null, "'last week'")]
        [InlineData("net sales yesterday", false, null, null, "'yesterday'")]
        [InlineData("net sales over the past year", true, null, null, "'past year'")]
        [InlineData("net sales for the first half of 2025", true, null, null, "'first half'")]
        [InlineData("net sales over the past two weeks", true, null, null, "'past two weeks'")]
        public async Task Time_words_are_read_by_the_models_days_or_named_as_not_applied_and_the_run_covers_every_date(string question, bool calendar, string from, string before, string said)
        {
            using var rig = await BuildAsync(script: Total, calendar: calendar);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            if (from == null)
            {
                Assert.True(Answered(a), Seen(a));
                Assert.StartsWith("Could not apply " + said + ", so this covers every date.", a.Assumption);
                Assert.Single(rig.Data.Runs);
                return;
            }
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Contains(a.Parts.Filters, f => f.Column == "'Date'[Date]" && f.Op == QueryOps.From && f.Values.SequenceEqual(new[] { from }));
            Assert.Contains(a.Parts.Filters, f => f.Column == "'Date'[Date]" && f.Op == QueryOps.Before && f.Values.SequenceEqual(new[] { before }));
            Assert.Equal("This model's data runs to 31 December 2025, so " + said, a.Note);

            // The parts ride back as they are: asked again by hand they build the same query, with no Jev.
            var again = await rig.Engine.AskDataAsync(a.Parts);
            Assert.Equal(DataAnswer.Answered, again.Outcome);
            Assert.Equal("you", again.ReadBy);
            Assert.Equal(a.Dax, again.Dax);
        }

        /// <summary>"the last 2 years" names several years, none of them on its own: split by year and said, never the
        /// all-time total.</summary>
        [Fact]
        public async Task Several_years_are_split_by_year_and_said()
        {
            using var rig = await BuildAsync(script: Total);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales over the last 2 years" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal(new[] { "'Date'[Year]" }, a.Parts.GroupBy);
            Assert.Contains("which years you mean by 'last 2 years'", a.Note);
        }

        /// <summary>A day column takes a date range only: "is" on a column of days is refused in plain words.</summary>
        [Fact]
        public async Task A_date_column_is_filtered_by_a_range_and_is_on_it_is_refused_plainly()
        {
            using var rig = await BuildAsync(calendar: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, Filters = new[] { new DataAskFilter { Column = "'Date'[Date]", Op = QueryOps.Is, Values = new[] { "2025-12-01" } } } });
            Assert.Equal(DataAnswer.Refused, a.Outcome);
            Assert.Contains(a.Checks, c => !c.Passed && c.Line.Contains("after, from, before or up to"));
            var b = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, Filters = new[] { new DataAskFilter { Column = "'Date'[Date]", Op = QueryOps.From, Values = new[] { "not a day" } } } });
            Assert.Equal(DataAnswer.Refused, b.Outcome);
            var c = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Net Sales" },
                Filters = new[]
                {
                    new DataAskFilter { Column = "'Date'[Date]", Op = QueryOps.From, Values = new[] { "2025-12-10" } },
                    new DataAskFilter { Column = "'Date'[Date]", Op = QueryOps.Before, Values = new[] { "2025-12-01" } },
                },
            });
            Assert.Equal(DataAnswer.Refused, c.Outcome);
            Assert.Contains(c.Checks, x => x.Name == "filters" && !x.Passed);
        }

        /// <summary>"last month in 2024" on data running to December 2025: last month is December 2025, which is not in
        /// 2024, so the two cannot both hold. The line says 'last month' was not applied (K16). When they meet, both filters run.</summary>
        [Fact]
        public async Task A_period_read_against_the_latest_date_that_misses_a_year_the_words_name_is_said()
        {
            using var rig = await BuildAsync(script: VerifySure, calendar: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales last month in 2024" });
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("'last month'", a.Assumption ?? "");

            using var rig2 = await BuildAsync(script: VerifySure, calendar: true);
            var b = await rig2.Engine.AskDataAsync(new DataAsk { Question = "net sales last month in 2025" });
            Assert.Equal(DataAnswer.Answered, b.Outcome);
            Assert.Contains(b.Parts.Filters, f => f.Column == "'Date'[YearMonth]" && f.Values.SequenceEqual(new[] { "2025-12" }));
            Assert.Contains(b.Parts.Filters, f => f.Column == "'Date'[Year]" && f.Values.SequenceEqual(new[] { "2025" }));
        }

        /// <summary>"by category, coast vs north": Jev counting only the category keeps it. The comparison adds its own split
        /// beside it; it no longer takes one away from the count.</summary>
        [Fact]
        public async Task A_comparison_keeps_the_split_the_question_names_and_adds_its_own()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.SplitsQ ? ("1", 0.95) : q.Name == JevQueryReader.GroupQ(1) ? ("c:Product[Category]", 0.93) : Total(r, q);
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by category, coast vs north" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal(new[] { "'Product'[Category]", "'Store'[Region]" }, a.Parts.GroupBy.OrderBy(x => x, StringComparer.Ordinal));
            // The split question kept a way out: the comparison may be the only split the question names.
            var split = rig.Jev.Seen.Single(r => r.Questions.Any(q => q.Name == JevQueryReader.GroupQ(1)));
            Assert.True(Q(split, JevQueryReader.GroupQ(1)).HasOption(JevQueryReader.None));
        }

        // ---- ROUND 2, Sol's review (28 Sep): each test below failed first. ------------------------------------------

        /// <summary>"by region, coast vs north" with Jev splitting by the OTHER table's Region: the side-by-side split is
        /// the compared table's column, not both Regions, and the other table stays one ask away.</summary>
        [Fact]
        public async Task A_split_on_the_other_twin_gives_way_to_the_compared_tables_column()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                IsFilterQ(q) ? (TwinOption(r, q, "Store[Region]"), 0.95)
                : q.Name == JevQueryReader.SplitsQ ? ("1", 0.95)
                : q.Name == JevQueryReader.GroupQ(1) ? (q.Options.Select(o => o.Id).FirstOrDefault(id => id == "c:Customer[Region]") ?? JevQueryReader.Other, 0.95)
                : q.Name == JevQueryReader.GroupTableQ(1) ? ("t:Customer", 0.95)
                : q.Name == JevQueryReader.GroupInQ(1) ? ("c:Customer[Region]", 0.95) : Total(r, q);
            using var rig = await BuildAsync(script: Script, twins: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by region, coast vs north" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal(new[] { "'Store'[Region]" }, a.Parts.GroupBy);
            var other = Assert.Single(a.Others);
            Assert.Equal(new[] { "'Customer'[Region]" }, other.Parts.GroupBy);
        }

        /// <summary>"this year versus last year" beside two splits: no room to show the years side by side, so the line says so.
        /// Before, both years were filtered and added into one total.</summary>
        [Fact]
        public async Task Two_periods_with_no_room_to_split_them_are_not_added_together_and_the_line_says_so()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.SplitsQ ? ("2", 0.95)
                : q.Name == JevQueryReader.GroupQ(1) ? ("c:Product[Category]", 0.93)
                : q.Name == JevQueryReader.GroupQ(2) ? (q.Options.Select(o => o.Id).FirstOrDefault(id => id == "c:Store[Region]") ?? JevQueryReader.Other, 0.93)
                : q.Name == JevQueryReader.GroupTableQ(2) ? ("t:Store", 0.93)
                : q.Name == JevQueryReader.GroupInQ(2) ? ("c:Store[Region]", 0.93) : Total(r, q);
            using var rig = await BuildAsync(script: Script, calendar: true);
            rig.Data.Latest = new DateTime(2025, 12, 15);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by category and region, this year versus last year" });
            Assert.True(Answered(a), Seen(a));
            Assert.StartsWith("Could not apply 'this year' and 'last year', so this covers every date.", a.Assumption);
        }

        /// <summary>A calendar with a column of days and a month name but no year: the month name alone is every year's
        /// month, so the labels are not used; the days are.</summary>
        [Fact]
        public void A_calendar_month_name_with_no_year_is_not_a_label_for_one_month()
        {
            var day = new QueryColumn { Table = "Date", Name = "Date", Kind = "date" };
            var cols = new QueryPeriods.Columns { Day = day, Days = day, MonthOfYear = new QueryColumn { Table = "Date", Name = "MonthName", Kind = "text" } };
            var said = QueryPeriods.Read("net sales last month");
            var reading = new JevQueryReader.Reading();
            Assert.Null(QueryPeriods.ResolveColumns(reading, said, cols));
            Assert.True(QueryPeriods.RangeReady(reading, said, cols));
        }

        /// <summary>"than" is a bound, not a comparison of two values: "more than 2024 and less than 2025" must not keep both
        /// years as one "is" filter without asking.</summary>
        [Fact]
        public void Than_is_not_a_comparison_of_two_values()
        {
            var year = new QueryColumn { Table = "Date", Name = "Year", Kind = "whole" };
            var cands = new List<ValueCandidate>
            {
                new ValueCandidate { Id = "f1", Column = year, Words = "2024", Values = new List<object> { 2024L } },
                new ValueCandidate { Id = "f2", Column = year, Words = "2025", Values = new List<object> { 2025L } },
            };
            Assert.Empty(JevQueryReader.CompareGroups("net sales more than 2024 and less than 2025", cands));
            Assert.Single(JevQueryReader.CompareGroups("did 2025 beat 2024", cands));
        }

        /// <summary>"last year in 2024" on data running to December 2025: last year is 2025, so the two do not meet, and it
        /// says so (K16: in the line) instead of quietly showing 2024. When they meet, one Year filter runs and the note says what was read.</summary>
        [Fact]
        public async Task A_period_the_words_also_name_by_its_own_column_must_meet_the_one_read()
        {
            using var rig = await BuildAsync(script: VerifySure, calendar: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales last year in 2024" });
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("'last year'", a.Assumption ?? "");

            using var rig2 = await BuildAsync(script: VerifySure, calendar: true);
            var b = await rig2.Engine.AskDataAsync(new DataAsk { Question = "net sales last year in 2025" });
            Assert.Equal(DataAnswer.Answered, b.Outcome);
            Assert.Equal(new[] { "2025" }, Assert.Single(b.Parts.Filters, f => f.Column == "'Date'[Year]").Values);
            Assert.Contains("'last year' is read as Year 2025", b.Note);
        }

        /// <summary>A bound on the year ("before 2025") beside a period read against the latest date: they must meet too.</summary>
        [Fact]
        public async Task A_period_read_that_misses_a_year_bound_is_said_not_applied()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => IsFilterQ(q) ? (QueryOps.Before, 0.95) : Total(r, q);
            using var rig = await BuildAsync(script: Script, calendar: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales last week before 2025" });
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("'last week'", a.Assumption ?? "");
        }

        /// <summary>Two different units ("last week versus this month") cannot both be read yet: the line names both,
        /// instead of reading one and dropping the other.</summary>
        [Fact]
        public async Task Two_different_time_units_are_both_named_as_not_applied()
        {
            using var rig = await BuildAsync(script: Total, calendar: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales last week versus this month" });
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("last week", a.Assumption ?? "");
            Assert.Contains("this month", a.Assumption ?? "");
        }

        /// <summary>A matched value is taken out of the words as a whole word only: "Day" inside "yesterday" stays.</summary>
        [Fact]
        public void A_matched_value_inside_a_longer_word_is_not_taken_out()
        {
            var said = QueryPeriods.Read("net sales for product Day yesterday", new[] { "Day" });
            Assert.NotNull(said);
            Assert.Equal("day", said.Unit);
        }

        /// <summary>A day with a time of day is refused, since the query and the parts keep the day only.</summary>
        [Fact]
        public async Task A_day_filter_with_a_time_of_day_is_refused()
        {
            using var rig = await BuildAsync(calendar: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, Filters = new[] { new DataAskFilter { Column = "'Date'[Date]", Op = QueryOps.From, Values = new[] { "2025-12-01T12:00:00" } } } });
            Assert.Equal(DataAnswer.Refused, a.Outcome);
            Assert.Contains(a.Checks, c => !c.Passed && c.Line.Contains("2025-12-31"));
        }

        /// <summary>A measure the model names with time words ("Month over Month Growth") is the measure, not a time the
        /// engine cannot apply.</summary>
        [Fact]
        public async Task Time_words_in_a_chosen_measures_name_are_the_measure()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.MeasureQ(1) ? ("m:Month over Month Growth", 0.97) : q.Name == JevQueryReader.SplitsQ ? ("0", 0.95) : null;
            using var rig = await BuildAsync(script: Script, calendar: true);
            await rig.Engine.CreateMeasureAsync("table:Sales", "Month over Month Growth", "SUM ( Sales[Amount] )", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "show month over month growth" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal(new[] { "Month over Month Growth" }, a.Parts.Measures);
        }

        /// <summary>"by region, coast vs north" with Jev counting the comparison as the one split, and torn between that column and
        /// "nothing beyond the comparison": both give the same answer, so it runs.</summary>
        [Fact]
        public async Task A_split_torn_between_the_compared_column_and_none_is_the_comparison()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.SplitsQ ? ("1", 0.95) : q.Name == JevQueryReader.GroupQ(1) ? (JevQueryReader.None, 0.5) : Total(r, q);
            using var rig = await BuildAsync(script: Script);
            rig.Jev.Probabilities = (r, q) => q.Name == JevQueryReader.GroupQ(1) ? P((JevQueryReader.None, 0.5), ("c:Store[Region]", 0.45), (JevQueryReader.Other, 0.05)) : null;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by region, coast vs north" });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal(new[] { "'Store'[Region]" }, a.Parts.GroupBy);
        }
    }
}
