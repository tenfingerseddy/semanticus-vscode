// ask_data review-fix round 6 (29 Sep): what the verifier's round-4 acceptance test sent back. Written before the fixes; each
// test below failed on d696b5c6 for the reason in its summary.
//   D1  a ratio says what it divides (IsRatio, RatioParts, the statement, what Jev is told, and a share question that a ratio of
//       another thing does not answer)
//   D2  G4, the period's kind: a fiscal or financial period asked of a model with no fiscal column the reading uses is said
//   D3  the engine never overrides a gap Jev is sure of, and clears a weaker one only when the reading provably covers it
//   D7  a bound word ("from", "since", "after", "before", "until") that came out as an equality is the range it names
//   L3  no second opinion when the first gap is sure
// Since K16 (Kane, 8 Oct) nothing here stops a run, a gap is said beside the number; CI never calls Jev (the answers are scripted).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    public sealed partial class AskDataTests
    {
        private static async Task ReturnRateAsync(Rig rig)
        {
            await rig.Engine.CreateMeasureAsync("table:Sales", "Returns Amount", "CALCULATE ( SUM ( Sales[Amount] ), Sales[Qty] < 0 )", "human");
            await rig.Engine.CreateMeasureAsync("table:Sales", "Return Rate %", "DIVIDE ( [Returns Amount], [Net Sales] )", "human");
        }

        private static IReadOnlyList<DecisionRequest> Calls(Rig rig) => rig.Jev.Checks;

        // ---- D1. a ratio says what it divides -------------------------------------------------------------------------------

        [Theory]
        [Trait("Round6", "1")]
        [InlineData("DIVIDE ( [A], [B] )", true)]
        [InlineData("DIVIDE ( [A], [B], 0 )", true)]
        [InlineData("[A] / [B]", true)]
        [InlineData("DIVIDE ( SUM ( Sales[Amount] ), SUM ( Sales[Qty] ) )", true)]
        [InlineData("DIVIDE ( [Returns Amount], CALCULATE ( SUM ( Sales[NetSalesAmount] ), Sales[IsReturn] = 0 ) )", true)]
        [InlineData("DIVIDE ( SUM ( Sales[Amount] ), 1000 )", false)]
        [InlineData("SUM ( Sales[Amount] ) / 1000", false)]
        [InlineData("DIVIDE ( 1, [B] )", false)]
        [InlineData("DIVIDE ( 100, 4 )", false)]
        [InlineData("SUM ( Sales[Amount] ) - SUM ( Sales[Qty] )", false)]
        [InlineData("SUM ( Sales[Amount] ) // per unit, / 1000", false)]
        [InlineData("", false)]
        public void A_ratio_has_a_non_literal_numerator_and_denominator_that_both_aggregate_model_data(string dax, bool ratio)
        {
            var m = new QueryMeasure { Table = "Sales", Name = "M", Expression = dax, FormatString = "0.0%" };
            Assert.Equal(ratio, m.IsRatio);
        }

        [Fact]
        [Trait("Round6", "1")]
        public void A_percent_format_alone_is_not_a_ratio_and_the_parts_are_named_by_the_measures_and_columns_they_read()
        {
            Assert.False(new QueryMeasure { Table = "Sales", Name = "Growth", Expression = "SUM ( Sales[Amount] ) - SUM ( Sales[Qty] )", FormatString = "0.0%" }.IsRatio);
            var rate = new QueryMeasure { Table = "Sales", Name = "Return Rate %", Expression = "DIVIDE ( [Returns Amount], CALCULATE ( SUM ( Sales[NetSalesAmount] ), Sales[IsReturn] = 0 ) )" };
            Assert.Equal(("Returns Amount", "Net Sales Amount"), rate.RatioParts);
            var count = new QueryMeasure { Table = "Sales", Name = "Orders per Store", Expression = "DIVIDE ( DISTINCTCOUNT ( Sales[OrderID] ), COUNTROWS ( Store ) )" };
            Assert.Equal(("distinct count of Order ID", "rows of Store"), count.RatioParts);
            Assert.Null(new QueryMeasure { Table = "Sales", Name = "Thousands", Expression = "DIVIDE ( SUM ( Sales[Amount] ), 1000 )" }.RatioParts);
        }

        [Fact]
        [Trait("Round6", "1")]
        public async Task The_statement_and_what_Jev_is_told_name_what_a_ratio_divides()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, measure: "Average Price"), ratio: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "what is the average price" });
            Assert.True(Answered(a), Seen(a));
            Assert.Equal("This shows Average Price (Net Sales divided by Units) as one total.", a.Statement);
            var told = StatedFor(Assert.Single(Calls(rig)));
            Assert.Contains("Average Price is a ratio, Net Sales divided by Units", told);
            // Names only: never a value or a date.
            Assert.DoesNotContain("2025", told);
        }

        /// <summary>va4-01, "What percentage of orders had a return?": Return Rate % is returned value over sales value. A share of
        /// ORDERS is another number. Jev said nothing missing (0.96), so the engine, which knows what the ratio divides, runs it and
        /// says what the number divides beside it, and spends no Jev call on it.</summary>
        [Fact]
        [Trait("Round6", "1")]
        public async Task A_share_question_answered_by_a_ratio_of_something_else_runs_and_says_what_it_divides()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.96, measure: "Return Rate %"));
            await ReturnRateAsync(rig);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "what percentage of orders had a return" });
            ScriptTaken(a);
            Assert.True(Answered(a), "stopped, where the ratio is shown with a line: " + V4Seen(a, rig));
            Assert.Contains("Return Rate % divides amounts of money", a.Assumption ?? "");
            Assert.Single(rig.Data.Runs);
            Assert.Empty(Calls(rig));
        }

        [Theory]
        [Trait("Round6", "1")]
        [InlineData("what percentage of sales were returned")]
        [InlineData("what is the return rate %")]
        [InlineData("return rate percent by region")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task A_share_question_answered_by_a_ratio_the_words_name_or_divide_by_runs(string question)
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.96, measure: "Return Rate %"));
            await ReturnRateAsync(rig);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("what percentage of sales were returned")]
        public Task A_share_question_answered_by_a_ratio_the_words_name_or_divide_by_runs_sample(string question) => A_share_question_answered_by_a_ratio_the_words_name_or_divide_by_runs(question);

        // ---- D2. G4, the period's kind ---------------------------------------------------------------------------------------

        [Theory]
        [Trait("Round6", "1")]
        [InlineData("net sales in fiscal 2025")]
        [InlineData("net sales for financial year 2025")]
        [InlineData("net sales in FY 2025")]
        public async Task G4_a_fiscal_period_read_on_a_calendar_column_is_said_when_the_model_has_no_fiscal_year(string question)
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.95), calendar: true);
            rig.Data.Latest = new DateTime(2025, 12, 15);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            ScriptTaken(a);
            Assert.True(Answered(a), "stopped, where the calendar number is shown with a line: " + V4Seen(a, rig));
            Assert.StartsWith("This shows Net Sales as one total, where Year is 2025 (1 January to 31 December 2025).", a.Statement);
            Assert.Contains("no fiscal year", a.Assumption ?? "");
            Assert.Single(rig.Data.Runs);
            Assert.Empty(Calls(rig));
        }

        [Fact]
        [Trait("Round6", "1")]
        public async Task G4_names_the_fiscal_column_the_model_has_when_the_reading_does_not_use_it()
        {
            Values["Date[Fiscal Quarter]"] = new object[] { "Q1", "Q2", "Q3", "Q4" };
            using var rig = await BuildAsync(script: Gate("nothing", 0.95), calendar: true);
            await rig.Engine.CreateColumnAsync("table:Date", "Fiscal Quarter", "String", "Fiscal Quarter", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales in fiscal 2025" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("Fiscal Quarter", a.Assumption ?? "");
        }

        /// <summary>The guard is about a fact of the model, not a word: a reading that uses a fiscal column is left alone, and a
        /// question that names no fiscal period is never asked.</summary>
        [Fact]
        [Trait("Round6", "1")]
        public async Task G4_leaves_a_reading_that_uses_a_fiscal_column_and_a_question_with_no_fiscal_word_alone()
        {
            Values["Date[Fiscal Quarter]"] = new object[] { "Q1", "Q2", "Q3", "Q4" };
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => q.Name == JevQueryReader.CheckGapQ ? ("nothing", 0.95) : V2Split(q, 1, "Date[Fiscal Quarter]") ?? V3(r, q, splits: "1");
            using var rig = await BuildAsync(script: Script, calendar: true);
            await rig.Engine.CreateColumnAsync("table:Date", "Fiscal Quarter", "String", "Fiscal Quarter", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales for 2025 by fiscal quarter" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.True(SplitBy(a, "'Date'[Fiscal Quarter]") && V3Keeps(a, "'Date'[Year]", QueryOps.Is, "2025"), Seen(a));
            using var plain = await BuildAsync(script: Gate("nothing", 0.95), calendar: true);
            var b = await plain.Engine.AskDataAsync(new DataAsk { Question = "net sales in 2025" });
            Assert.True(Answered(b), Seen(b));
        }

        // ---- D7. G1 reads a bound word that came out as an equality ---------------------------------------------------

        [Theory]
        [Trait("Round6", "1")]
        [InlineData("net sales from 2024 in coast", QueryOps.From, "2024")]
        [InlineData("net sales since 2024", QueryOps.From, "2024")]
        [InlineData("net sales until 2025", QueryOps.UpTo, "2025")]
        [InlineData("net sales before 2025", QueryOps.Before, "2025")]
        public async Task G1_a_bound_word_read_as_an_equality_on_a_period_is_the_range_it_names(string question, string op, string year)
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.74));
            Spread(rig.Jev, ("nothing", 0.74), ("change", 0.19), ("period", 0.04));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            ScriptTaken(a);
            Assert.True(Answered(a), "stopped, where a bound word is the range it names: " + V4Seen(a, rig));
            Assert.Contains(year, V3Filters(a, "'Date'[Year]", op).Single().Values);
            Assert.False(V3Filters(a, "'Date'[Year]", QueryOps.Is).Any(), "a range read as one year: " + V4Seen(a, rig));
        }

        [Fact]
        [Trait("Round6", "1")]
        public async Task G1_a_bound_word_that_came_out_as_a_bound_or_beside_a_name_is_left_alone()
        {
            using var rig = await BuildAsync(script: Sure(op: QueryOps.From));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales from 2024" });
            Assert.True(Answered(a), Seen(a));
            using var place = await BuildAsync(script: Gate("nothing", 0.97));
            var b = await place.Engine.AskDataAsync(new DataAsk { Question = "net sales from the coast" });
            Assert.True(Answered(b), Seen(b));
        }

        // ---- D3. the engine never overrides a sure gap ---------------------------------------------------------------

        [Fact]
        [Trait("Round6", "1")]
        public async Task A_gap_Jev_is_sure_of_is_said_even_when_a_fact_says_it_is_covered()
        {
            using var rig = await BuildAsync(script: Gate("change", 0.99, parts: VerifySure));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "did 2025 beat 2024 on net sales" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("changed", a.Assumption ?? "");
            Assert.Single(rig.Data.Runs);
            Assert.Empty(SecondOpinions(rig));
        }

        [Fact]
        [Trait("Round6", "1")]
        public async Task A_weaker_change_gap_is_cleared_when_both_periods_are_in_the_rows_and_the_note_says_so()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.6, parts: VerifySure));
            Spread(rig.Jev, ("nothing", 0.6), ("change", 0.35));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "did 2025 beat 2024 on net sales" });
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("read the change off the rows", a.Note);
            Assert.Empty(SecondOpinions(rig));
        }

        [Fact]
        [Trait("Round6", "1")]
        public async Task A_weaker_change_gap_is_not_cleared_by_a_split_by_month_when_the_year_compared_with_is_not_in_the_rows()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.CheckGapQ ? ("nothing", 0.55) : q.Name == JevQueryReader.CheckRealQ ? ("yes", 0.97) : V2Split(q, 1, "Date[MonthName]") ?? V3(r, q, splits: "1");
            using var rig = await BuildAsync(script: Script);
            Spread(rig.Jev, ("nothing", 0.55), ("change", 0.4));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how did net sales in 2025 compare with the year before, month by month" });
            ScriptTaken(a);
            Assert.True(SplitBy(a, "'Date'[MonthName]") && FilterOn(a, "'Date'[Year]").Values.Length == 1, Seen(a));
            Assert.True(Answered(a), Seen(a));
            Assert.DoesNotContain("read the change off the rows", a.Note ?? "");
            Assert.Single(SecondOpinions(rig));
        }

        [Fact]
        [Trait("Round6", "1")]
        public async Task A_weaker_versions_gap_is_not_cleared_by_a_split_by_year()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.CheckGapQ ? ("nothing", 0.55) : q.Name == JevQueryReader.CheckRealQ ? ("yes", 0.97)
                : V2Split(q, 1, "Date[Year]") ?? (IsFilterQ(q) && q.HasOption(QueryOps.IsNot) ? (QueryOps.IsNot, 0.95) : V3(r, q, splits: "1"));
            using var rig = await BuildAsync(script: Script);
            Spread(rig.Jev, ("nothing", 0.55), ("versions", 0.4));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by year with and without kitchen products" });
            ScriptTaken(a);
            Assert.True(Answered(a), V4Seen(a, rig));
            Assert.DoesNotContain("read the difference off the rows", a.Note ?? "");
            Assert.Single(SecondOpinions(rig));
        }

        /// <summary>A kind of number is matched on whole words of the measure's own name only: a description that says "generated"
        /// or "as per" is not a rate or an average.</summary>
        [Fact]
        [Trait("Round6", "1")]
        public async Task A_kind_is_cleared_by_a_whole_word_of_the_measures_name_and_not_by_its_description()
        {
            (string, double)? Real(DecisionRequest r, DecisionQuestion q, string measure = "Net Sales") =>
                q.Name == JevQueryReader.CheckGapQ ? ("nothing", 0.55) : q.Name == JevQueryReader.CheckRealQ ? ("yes", 0.97) : V3(r, q, measure, "0");
            using var rig = await BuildAsync(script: (r, q) => Real(r, q));
            Spread(rig.Jev, ("nothing", 0.55), ("other_number", 0.4));
            await rig.Engine.SetDescriptionAsync("measure:Sales/Net Sales", "The average of what customers paid, as per the finance team.", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "what is the average net sales" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.DoesNotContain("average asked for", a.Note ?? "");
            Assert.Single(SecondOpinions(rig));

            using var named = await BuildAsync(script: (r, q) => Real(r, q, "Revenue per Unit"));
            await named.Engine.CreateMeasureAsync("table:Sales", "Revenue per Unit", "SUM ( Sales[Amount] )", "human");
            Spread(named.Jev, ("nothing", 0.55), ("other_number", 0.4));
            var b = await named.Engine.AskDataAsync(new DataAsk { Question = "what is the average revenue per unit" });
            ScriptTaken(b);
            Assert.True(Answered(b), Seen(b));
            Assert.Contains("average asked for", b.Note);
        }

        /// <summary>A period gap is cleared only when the engine read the span itself from the question's words ("last year"),
        /// so the span shown IS the asked span. A year filter alone is not that: "the 2024 holiday season" is a Year filter too.</summary>
        [Fact]
        [Trait("Round6", "1")]
        public async Task A_weaker_period_gap_is_cleared_only_when_the_engine_read_the_span_from_the_words()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.CheckGapQ ? ("nothing", 0.55) : q.Name == JevQueryReader.CheckRealQ ? ("yes", 0.97) : V3(r, q);
            using var year = await BuildAsync(script: Script, calendar: true);
            year.Data.Latest = new DateTime(2025, 12, 15);
            Spread(year.Jev, ("nothing", 0.55), ("period", 0.4));
            var a = await year.Engine.AskDataAsync(new DataAsk { Question = "net sales during the 2025 holiday season" });
            ScriptTaken(a);
            Assert.True(V3Keeps(a, "'Date'[Year]", QueryOps.Is, "2025"), Seen(a));
            Assert.True(Answered(a), Seen(a));
            Assert.Single(SecondOpinions(year));

            using var last = await BuildAsync(script: Script, calendar: true);
            last.Data.Latest = new DateTime(2025, 12, 15);
            Spread(last.Jev, ("nothing", 0.55), ("period", 0.4));
            var b = await last.Engine.AskDataAsync(new DataAsk { Question = "net sales last year" });
            ScriptTaken(b);
            Assert.True(Answered(b), Seen(b));
            Assert.Empty(SecondOpinions(last));
        }

        // ---- D5. the split doubt: a split the reading has one obvious answer for does not ask -------------------------------------

        /// <summary>The split answers as Jev gave them in the round-4 fresh sets: a column with a clear lead but under the 0.85 bar (0.83 against
        /// 0.08 for "which store had the highest net sales", 0.8 against 0.16 for a top 10 of products). <paramref name="which"/> picks the
        /// column out of the options the engine offered, by its place in the list (0 is the engine's own first pick).</summary>
        private static (Func<DecisionRequest, DecisionQuestion, (string, double)?> Script, Func<DecisionRequest, DecisionQuestion, IReadOnlyDictionary<string, double>> Mass) SplitBelowTheBar(int which, double mass)
        {
            string Pick(DecisionQuestion q) => q.Options.Where(o => o.Id.StartsWith("c:", StringComparison.Ordinal)).ElementAt(which).Id;
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.GroupQ(1) ? (Pick(q), mass - 0.1) : q.Name == JevQueryReader.SplitsQ ? ("1", 0.95) : V3(r, q, splits: "1");
            IReadOnlyDictionary<string, double> Mass(DecisionRequest r, DecisionQuestion q)
            {
                if (q.Name != JevQueryReader.GroupQ(1)) return null;
                // The rest of the mass is on "another column", so no second column of the same kind makes it a torn pick.
                return new Dictionary<string, double> { [Pick(q)] = mass, [JevQueryReader.Other] = 1 - mass };
            }
            return (Script, Mass);
        }

        [Fact]
        [Trait("Round6", "1")]
        public async Task A_split_column_with_a_clear_lead_that_is_the_engines_own_first_pick_does_not_ask()
        {
            var (script, mass) = SplitBelowTheBar(0, 0.8);
            using var rig = await BuildAsync(script: script);
            rig.Jev.Probabilities = mass;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "top 10 products by net sales" });
            ScriptTaken(a);
            Assert.True(Answered(a), "needless ask on a split with one obvious column: " + Seen(a));
            Assert.True(SplitBy(a, "'Product'[ProductName]"), Seen(a));
        }

        [Fact]
        [Trait("Round6", "1")]
        public async Task A_split_column_without_a_clear_lead_runs_the_best_guess_and_says_it()
        {
            // Twins near a tie (0.41 against 0.38) are a real doubt: the best guess runs and the line says it (K16).
            (string, double)? Twin(DecisionRequest r, DecisionQuestion q) => q.Name == JevQueryReader.GroupQ(1) ? ("c:Store[Region]", 0.11) : q.Name == JevQueryReader.SplitsQ ? ("1", 0.95) : V3(r, q, splits: "1");
            using var twins = await BuildAsync(script: Twin, twins: true);
            twins.Jev.Probabilities = (r, q) => q.Name == JevQueryReader.GroupQ(1) ? P(("c:Store[Region]", 0.41), ("c:Customer[Region]", 0.38), (JevQueryReader.Other, 0.21)) : null;
            var a = await twins.Engine.AskDataAsync(new DataAsk { Question = "show net sales by region" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("Split by", a.Assumption ?? "");

            // A clear lead on a column the engine's own shortlist does not put first is still a doubt: two readers must agree.
            var (script, mass) = SplitBelowTheBar(1, 0.8);
            using var apart = await BuildAsync(script: script);
            apart.Jev.Probabilities = mass;
            var b = await apart.Engine.AskDataAsync(new DataAsk { Question = "net sales by category and product name" });
            ScriptTaken(b);
            Assert.True(Answered(b), Seen(b));
            Assert.Contains("Split by", b.Assumption ?? "");
        }

        /// <summary>"Which state had the most returned units": Jev was not sure how many splits (0.5) and, for the second slot, put
        /// "another column" at 0.46 and "none" at 0.43. The words hold no split marker (by, per, each, across), so no second split was
        /// asked for and the first, sure one stands.</summary>
        [Fact]
        [Trait("Round6", "1")]
        public async Task A_second_split_nobody_asked_for_does_not_ask_when_the_words_hold_no_split_marker()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.SplitsQ ? ("0", 0.3)
                : q.Name == JevQueryReader.GroupQ(1) ? ("c:Store[Region]", 0.93)
                : q.Name == JevQueryReader.GroupQ(2) ? (JevQueryReader.Other, 0.18)
                : V3(r, q, splits: "0");
            using var rig = await BuildAsync(script: Script);
            rig.Jev.Probabilities = (r, q) => q.Name == JevQueryReader.SplitsQ ? P(("0", 0.5), ("1", 0.49), ("2", 0.01))
                : q.Name == JevQueryReader.GroupQ(1) ? P(("c:Store[Region]", 0.98), (JevQueryReader.Other, 0.02))
                : q.Name == JevQueryReader.GroupQ(2) ? P((JevQueryReader.Other, 0.46), (JevQueryReader.None, 0.43), ("c:Store[Region]", 0.11)) : null;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "which region had the lowest net sales" });
            ScriptTaken(a);
            Assert.True(Answered(a), "needless ask on a second split nobody named: " + Seen(a));
            Assert.Equal(new[] { "'Store'[Region]" }, a.Parts.GroupBy);
        }

        /// <summary>"Another column" with a clear lead is the way to the table question, not a doubt: its own answer gates the rest.</summary>
        [Fact]
        [Trait("Round6", "1")]
        public async Task Another_column_with_a_clear_lead_goes_on_to_the_table_question()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.GroupQ(1) ? (JevQueryReader.Other, 0.66)
                : q.Name == JevQueryReader.GroupTableQ(1) ? ("t:Product", 0.93)
                : q.Name == JevQueryReader.GroupInQ(1) ? ("c:Product[Category]", 0.93)
                : q.Name == JevQueryReader.SplitsQ ? ("1", 0.95) : V3(r, q, splits: "1");
            using var rig = await BuildAsync(script: Script);
            rig.Jev.Probabilities = (r, q) => q.Name == JevQueryReader.GroupQ(1) ? P((JevQueryReader.Other, 0.85), (q.Options.First(o => o.Id.StartsWith("c:", StringComparison.Ordinal)).Id, 0.15)) : null;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales for each kind of product" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.True(SplitBy(a, "'Product'[Category]"), Seen(a));
        }

        // ---- L6. two period words are quoted as the question wrote them ----------------------------------------------

        [Theory]
        [Trait("Round6", "1")]
        [InlineData("net sales this year vs last year", "this year vs last year")]
        [InlineData("net sales this month compared with last month", "this month compared with last month")]
        [InlineData("net sales last year against this year", "last year against this year")]
        public void Two_periods_named_together_are_quoted_as_the_question_wrote_them(string question, string together)
        {
            Assert.Equal(together, QueryPeriods.Read(question).Together);
            Assert.Null(QueryPeriods.Read("net sales last year").Together);
        }

        // ---- date cells (hero run 4, "2025-01-01T00:00:00"): a date column's own format reaches the grid ---------------------------

        [Fact]
        public async Task An_answer_carries_a_date_columns_own_format_so_a_date_reads_as_a_date()
        {
            using var rig = await BuildAsync(calendar: true);
            await rig.Engine.SetObjectPropertyAsync("column:Date/Date", "FormatString", "dd/MM/yyyy", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, GroupBy = new[] { "Date[Date]" }, Run = true });
            Assert.True(Answered(a), Seen(a));
            Assert.Equal(new string[] { "dd/MM/yyyy", null }, a.Formats);
        }

        // ---- L5. the grid shows a percent measure in its format ------------------------------------------------------

        [Fact]
        [Trait("Round6", "1")]
        public async Task An_answer_carries_each_measures_own_format_so_a_percent_reads_as_a_percent()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, measure: "Return Rate %"));
            await ReturnRateAsync(rig);
            await rig.Engine.SetMeasureFormatAsync("measure:Sales/Return Rate %", "0.0%;-0.0%;0.0%", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "what is the return rate %", Run = true });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Equal(new string[] { "0.0%;-0.0%;0.0%" }, a.Formats);
            // A model whose measures carry no format sends none.
            using var plain = await BuildAsync(script: Gate("nothing", 0.97));
            var b = await plain.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            Assert.True(Answered(b), Seen(b));
            Assert.Null(b.Formats);
        }

        // ---- both doors, with a weaker gap (the verifier's Verify4 two-door test scripts a sure change that is now a stop) ---

        [Fact]
        [Trait("Round6", "1")]
        public async Task Both_doors_give_the_same_cleared_answer_and_the_same_said_gap()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.CheckGapQ ? ("nothing", 0.6) : V2Split(q, 1, "Date[Year]") ?? V3(r, q, splits: "1");
            using var rig = await BuildAsync(script: Script);
            Spread(rig.Jev, ("nothing", 0.6), ("change", 0.35));
            string Shape(DataAnswer a) => System.Text.Json.JsonSerializer.Serialize(new
            {
                a.Outcome, a.Statement, a.Reading, a.Note, a.Assumption, a.Dax, a.RowCount, a.ReadBy, Unsure = a.Unsure ?? Array.Empty<string>(),
                Others = (a.Others ?? Array.Empty<DataOther>()).Select(o => new { o.Line, o.Reading, o.Instead, o.Anyway, o.Parts }).ToArray(), a.Parts, a.Formats,
            });
            var studio = new EngineRpcTarget(rig.Engine);
            var held = await McpTools.AskData(rig.Engine, question: "net sales trend by year");
            if (held.ApprovalId != null) Semanticus.Engine.ApprovalLedger.Approve(held.ApprovalId, "human");
            var viaStudio = await studio.askData(new DataAsk { Question = "net sales trend by year" });
            var viaAgent = await McpTools.AskData(rig.Engine, question: "net sales trend by year");
            Assert.True(Answered(viaStudio), V4Seen(viaStudio, rig));
            Assert.Contains("read the change off the rows", viaStudio.Note ?? "");
            Assert.StartsWith("This shows Net Sales for each Year", viaStudio.Statement);
            Assert.Equal(Shape(viaStudio), Shape(viaAgent));

            // A sure gap is said beside the number, the same through either door.
            using var said = await BuildAsync(script: Gate("change", 0.98, parts: VerifySure));
            var asked = await said.Engine.AskDataAsync(new DataAsk { Question = "how did net sales change in 2025" });
            Assert.True(Answered(asked), V4Seen(asked, said));
            Assert.Contains("changed", asked.Assumption ?? "");
            var viaStudioSaid = await new EngineRpcTarget(said.Engine).askData(new DataAsk { Question = "how did net sales change in 2025" });
            var heldSaid = await McpTools.AskData(said.Engine, question: "how did net sales change in 2025");
            if (heldSaid.ApprovalId != null) Semanticus.Engine.ApprovalLedger.Approve(heldSaid.ApprovalId, "human");
            var viaAgentSaid = await McpTools.AskData(said.Engine, question: "how did net sales change in 2025");
            Assert.Equal(viaStudioSaid.Assumption, viaAgentSaid.Assumption);
            Assert.Equal(asked.Assumption, viaStudioSaid.Assumption);
        }

        // ---- L3. no second opinion when the first gap is sure --------------------------------------------------------

        [Fact]
        [Trait("Round6", "1")]
        public async Task No_second_opinion_is_asked_when_the_first_gap_is_sure()
        {
            using var rig = await BuildAsync(script: Gate("share", 0.96));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "what share of net sales was the coast" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("share, a percent or an average", a.Assumption ?? "");
            Assert.Single(Calls(rig));
            Assert.Empty(SecondOpinions(rig));
        }
    }
}
