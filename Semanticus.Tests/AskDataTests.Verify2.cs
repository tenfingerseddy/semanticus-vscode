// VERIFY, m11/jev-query-tune round 2 (verifier, 28 Sep). Twenty new everyday questions that could put a wrong number
// in front of a person, WRITTEN AND LABELLED BEFORE ANY OF THEM WAS RUN, and committed that way. Kane's ruling: a data
// question never runs a number it did not ask for without saying so (comparisons side by side, a column in two tables
// read one way with the other named, time phrases resolved against the model's dates and stated, the rest needs-you).
//
// Each case says what a right answer is, what else is acceptable, and the HARMFUL shape it asserts is absent. Only the
// harmful shape is asserted, so any honest outcome passes: a right reading, a needs-you, or a note that says the thing
// plainly. Jev is scripted the way the lane's own tests script it (the number named, sure; a filter "is", sure), and
// where a case needs Jev to lean one way the comment says why that lean is plausible. ScriptTaken guards the harness:
// a scripted answer the engine refused would make a case pass while proving nothing.
//
// The rig is AskDataTests' own (partial class): the Contoso-style model, the fake data connection and scripted Jev.
// A case that fails is left failing, for the builder.

using System;
using System.Collections.Generic;
using System.Globalization;
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
        // ---- the harness for the round-2 attacks ---------------------------------------------------------------

        private static string Seen(DataAnswer a) =>
            $"outcome {a.Outcome}; reading '{a.Reading}'; note '{a.Note}'; unsure '{string.Join(" / ", a.Unsure ?? Array.Empty<string>())}'; assumption '{a.Assumption}'; others {a.Others?.Length ?? 0}";

        /// <summary>The scripted Jev answer was taken. A refused script ends in "Jev did not answer" and proves nothing.</summary>
        private static void ScriptTaken(DataAnswer a) =>
            Assert.False((a.Note ?? "").StartsWith("Jev ", StringComparison.Ordinal), "the scripted Jev answer was not taken, so this case proves nothing: " + a.Note);

        /// <summary>K16: nothing was said beside the number, so a wrong reading would have been shown as right.</summary>
        private static bool Silent(DataAnswer a) => string.IsNullOrEmpty(a.Assumption);
        private static bool Answered(DataAnswer a) => a.Outcome == DataAnswer.Answered;
        private static bool HasFilter(DataAnswer a, string column) => a.Parts?.Filters?.Any(f => f.Column == column) == true;
        private static DataAskFilter FilterOn(DataAnswer a, string column) => a.Parts?.Filters?.FirstOrDefault(f => f.Column == column);
        private static bool SplitBy(DataAnswer a, string column) => a.Parts?.GroupBy?.Contains(column) == true;
        private static bool Says(DataAnswer a, string words) => ((a.Note ?? "") + " " + (a.Assumption ?? "")).IndexOf(words, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The all-time total with nothing said: no filter, no split, no note.</summary>
        private static bool SilentTotal(DataAnswer a) =>
            Answered(a) && (a.Parts?.Filters?.Length ?? 0) == 0 && (a.Parts?.GroupBy?.Length ?? 0) == 0 && string.IsNullOrEmpty(a.Note) && string.IsNullOrEmpty(a.Assumption);

        /// <summary>Jev as the lane's own tests script it: Net Sales, sure; the count of splits given; every plain filter
        /// question "is", sure. Anything else is the scripted oracle's sure "none".</summary>
        private static (string, double)? V2Base(DecisionRequest r, DecisionQuestion q, string splits = "0")
        {
            if (q.Name == JevQueryReader.MeasureQ(1)) return ("m:Net Sales", 0.97);
            if (q.Name == JevQueryReader.SplitsQ) return (splits, 0.95);
            if (IsFilterQ(q) && q.HasOption(QueryOps.Is)) return (QueryOps.Is, 0.95);
            return null;
        }

        /// <summary>The split in one slot, sure: the column when the shortlist offers it, else "another column", then its
        /// table, then the column in it.</summary>
        private static (string, double)? V2Split(DecisionQuestion q, int slot, string column)
        {
            var table = column.Substring(0, column.IndexOf('['));
            if (q.Name == JevQueryReader.GroupQ(slot)) return (q.HasOption("c:" + column) ? "c:" + column : JevQueryReader.Other, 0.93);
            if (q.Name == JevQueryReader.GroupTableQ(slot)) return ("t:" + table, 0.93);
            if (q.Name == JevQueryReader.GroupInQ(slot)) return ("c:" + column, 0.93);
            return null;
        }

        /// <summary>A top N, sure: n, ranked by Net Sales, the highest.</summary>
        private static (string, double)? V2Top(DecisionQuestion q, int n) =>
            q.Name == JevQueryReader.TopQ ? ("n:" + n.ToString(CultureInfo.InvariantCulture), 0.95)
            : q.Name == JevQueryReader.TopByQ ? ("m:Net Sales", 0.95)
            : q.Name == JevQueryReader.TopEndQ ? ("highest", 0.95)
            : ((string, double)?)null;

        /// <summary>A twin filter question (the same words in Store Region and Customer Region): its options name a column.</summary>
        private static bool IsTwinQ(DecisionQuestion q) => IsFilterQ(q) && !q.HasOption(QueryOps.Is);

        /// <summary>Jev leaning to Customer Region on a twin question, the way the lane's tests lean to Store Region.</summary>
        private static IReadOnlyDictionary<string, double> LeanCustomer(DecisionRequest r, DecisionQuestion q) =>
            IsTwinQ(q) ? P((TwinOption(r, q, "Customer[Region]"), 0.55), (TwinOption(r, q, "Store[Region]"), 0.41), (JevQueryReader.None, 0.04)) : null;

        /// <summary>A calendar with a quarter of the year (Q1 to Q4) beside Year, as many calendars hold it.</summary>
        private static async Task<Rig> V2QuarterRigAsync(Func<DecisionRequest, DecisionQuestion, (string, double)?> script)
        {
            Values["Date[Quarter]"] = new object[] { "Q1", "Q2", "Q3", "Q4" };
            var rig = await BuildAsync(script: script, calendar: true);
            await rig.Engine.CreateColumnAsync("table:Date", "Quarter", "String", "Quarter", "human");
            return rig;
        }

        // ---- comparisons and two-table columns -----------------------------------------------------------------

        /// <summary>A01, comparison over a two-table column. "net sales south vs coast": Region is in Store and in
        /// Customer, but only 'coast' is in both (no customer lives in the South region). Right: South and Coast side by
        /// side on ONE table's Region (Store is the only one holding both), with the other named. Acceptable: needs-you.
        /// HARMFUL: Store Region and Customer Region both filtered, which is South stores' sales to Coast customers, not
        /// a comparison of two regions. Plausible Jev: the engine keeps the pair on Store and then asks about Customer
        /// Region's 'coast' on its own, with nothing saying it is already kept; "is", sure, is the natural answer.</summary>
        [Fact]
        public async Task Verify2_A01_south_vs_coast_where_only_coast_is_in_both_tables_is_not_read_on_two_tables()
        {
            using var rig = await BuildAsync(script: (r, q) => V2Base(r, q), twins: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales south vs coast" });
            ScriptTaken(a);
            Assert.False(Answered(a) && Silent(a) && HasFilter(a, "'Store'[Region]") && HasFilter(a, "'Customer'[Region]"),
                "south vs coast was read on Store Region and Customer Region at once: " + Seen(a));
        }

        /// <summary>A02, two-table column, no compare word. "net sales for south and coast", Jev leaning to Customer Region
        /// for 'coast' (0.55 against Store's 0.41: together over the bar, the lane's own "two good readings" shape).
        /// Right: South and Coast side by side on Store Region, Customer named. Acceptable: needs-you. HARMFUL: Store
        /// Region = South and Customer Region = Coast together, one total that no one asked for.</summary>
        [Fact]
        public async Task Verify2_A02_south_and_coast_leaning_to_customer_region_is_not_read_on_two_tables()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => IsTwinQ(q) ? (TwinOption(r, q, "Customer[Region]"), 0.55) : V2Base(r, q);
            using var rig = await BuildAsync(script: Script, twins: true);
            rig.Jev.Probabilities = LeanCustomer;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales for south and coast" });
            ScriptTaken(a);
            Assert.False(Answered(a) && Silent(a) && HasFilter(a, "'Store'[Region]") && HasFilter(a, "'Customer'[Region]"),
                "south and coast became South stores times Coast customers: " + Seen(a));
        }

        /// <summary>A04, top N inside a comparison. "top 3 products, coast vs north". Right: the top 3 products of Coast
        /// beside the top 3 of North. Acceptable: needs-you, or a note that says the top 3 is taken across both places.
        /// HARMFUL: TOPN 3 over every product-and-region pair while the note says the two are shown side by side: all
        /// three rows can be Coast's, and North's top products are then missing from a "comparison".</summary>
        [Fact]
        public async Task Verify2_A04_top_3_products_coast_vs_north_does_not_rank_across_both_places_unsaid()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => V2Split(q, 1, "Product[ProductName]") ?? V2Top(q, 3) ?? V2Base(r, q, splits: "1");
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "top 3 products, coast vs north" });
            ScriptTaken(a);
            Assert.False(Answered(a) && Silent(a) && a.Parts.Top == 3 && SplitBy(a, "'Store'[Region]") && a.Parts.GroupBy.Length == 2 && !Says(a, "top 3"),
                "the top 3 was ranked across both places, so one place can drop out of the comparison: " + Seen(a));
        }

        /// <summary>A17, two-table column with three values. "net sales coast, north and south", Jev leaning to Customer
        /// Region for 'coast' and 'north' (both are in both tables); 'south' is only in Store Region. Right: all three side
        /// by side on Store Region (the one table holding all three), Customer named. Acceptable: needs-you. HARMFUL:
        /// Customer Region = Coast or North beside Store Region = South, the sales of South stores to Coast and North
        /// customers.</summary>
        [Fact]
        public async Task Verify2_A17_three_regions_where_one_is_in_one_table_are_not_read_on_two_tables()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => IsTwinQ(q) ? (TwinOption(r, q, "Customer[Region]"), 0.55) : V2Base(r, q);
            using var rig = await BuildAsync(script: Script, twins: true);
            rig.Jev.Probabilities = LeanCustomer;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales coast, north and south" });
            ScriptTaken(a);
            Assert.False(Answered(a) && Silent(a) && HasFilter(a, "'Store'[Region]") && HasFilter(a, "'Customer'[Region]"),
                "coast, north and south were read on two tables at once: " + Seen(a));
        }

        // ---- periods ----------------------------------------------------------------------------------------------

        /// <summary>A05. "net sales since last year" on data running to 15 December 2025. Right: 2024 and 2025 (from the
        /// start of last year). Acceptable: needs-you, or a note that names 'since'. HARMFUL: Year = 2024 alone (or 2025
        /// alone) with a note that quotes only 'last year', so the 'since' is dropped without a word.</summary>
        [Fact]
        public async Task Verify2_A05_since_last_year_is_not_read_as_last_year_alone()
        {
            using var rig = await BuildAsync(script: Total, calendar: true);
            rig.Data.Latest = new DateTime(2025, 12, 15);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales since last year" });
            ScriptTaken(a);
            var year = FilterOn(a, "'Date'[Year]");
            Assert.False(Answered(a) && Silent(a) && !Says(a, "since") && year != null && year.Op == QueryOps.Is && !(year.Values.Contains("2024") && year.Values.Contains("2025")),
                "'since last year' ran one year only: " + Seen(a));
        }

        /// <summary>A06. "net sales before last year" on data running to 15 December 2025. Right: the years before 2024
        /// (none in this data, said). Acceptable: needs-you, or a note that names 'before'. HARMFUL: Year = 2024, the one
        /// year the question leaves out.</summary>
        [Fact]
        public async Task Verify2_A06_before_last_year_is_not_read_as_last_year()
        {
            using var rig = await BuildAsync(script: Total, calendar: true);
            rig.Data.Latest = new DateTime(2025, 12, 15);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales before last year" });
            ScriptTaken(a);
            var year = FilterOn(a, "'Date'[Year]");
            Assert.False(Answered(a) && Silent(a) && !Says(a, "before") && year != null && year.Op == QueryOps.Is && year.Values.Contains("2024"),
                "'before last year' ran last year itself: " + Seen(a));
        }

        /// <summary>A07. "net sales by category and region in December" with no calendar, on data holding 2024 and 2025.
        /// Both split slots are taken, so there is no room to split December by year. Right: December of one year, or
        /// each December on its own. Acceptable: needs-you, or a note that says the Decembers are added together. HARMFUL:
        /// MonthName = December with no year, which adds December 2024 and December 2025 into one number per row, unsaid
        /// (the lane's own rule: a month name in more than one year is not added up).</summary>
        [Fact]
        public async Task Verify2_A07_december_beside_two_splits_does_not_add_every_years_december_unsaid()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => V2Split(q, 1, "Product[Category]") ?? V2Split(q, 2, "Store[Region]") ?? V2Base(r, q, splits: "2");
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by category and region in December" });
            ScriptTaken(a);
            Assert.False(Answered(a) && Silent(a) && (FilterOn(a, "'Date'[MonthName]")?.Values.Contains("December") ?? false)
                && !SplitBy(a, "'Date'[Year]") && !HasFilter(a, "'Date'[Year]") && !Says(a, "year"),
                "December of every year was added together: " + Seen(a));
        }

        /// <summary>A08. "how did net sales go in Q3" on a calendar with Quarter (Q1 to Q4) and Year, data in 2024 and
        /// 2025. Right: Q3 of each year on its own, or the latest Q3 read against the model's dates and said. Acceptable:
        /// needs-you. HARMFUL: Quarter = Q3 with no year, Q3 2024 and Q3 2025 added into one number, unsaid (the quarter
        /// twin of "how was December").</summary>
        [Fact]
        public async Task Verify2_A08_q3_on_data_holding_two_years_is_not_added_up_unsaid()
        {
            using var rig = await V2QuarterRigAsync((r, q) => V2Base(r, q));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how did net sales go in Q3" });
            ScriptTaken(a);
            Assert.False(Answered(a) && Silent(a) && (FilterOn(a, "'Date'[Quarter]")?.Values.Contains("Q3") ?? false)
                && !SplitBy(a, "'Date'[Year]") && !HasFilter(a, "'Date'[Year]") && !HasFilter(a, "'Date'[Date]") && !Says(a, "year"),
                "Q3 of every year was added together: " + Seen(a));
        }

        /// <summary>A09. "net sales the month before last" on data running to 15 December 2025. Right: October 2025, said.
        /// Acceptable: needs-you naming the words. HARMFUL: the all-time total with nothing said, or last month (November)
        /// with a note that does not name 'before last'.</summary>
        [Fact]
        public async Task Verify2_A09_the_month_before_last_is_not_the_all_time_total_or_last_month_unsaid()
        {
            using var rig = await BuildAsync(script: Total, calendar: true);
            rig.Data.Latest = new DateTime(2025, 12, 15);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales the month before last" });
            ScriptTaken(a);
            var month = FilterOn(a, "'Date'[YearMonth]");
            Assert.False(SilentTotal(a) || (Answered(a) && month != null && month.Values.SequenceEqual(new[] { "2025-11" }) && !Says(a, "before last")),
                "'the month before last' ran the wrong period unsaid: " + Seen(a));
        }

        /// <summary>A11. "net sales year-over-year", hyphenated as it is usually written. Right: split by year, said (the
        /// way "year over year" is read). Acceptable: needs-you. HARMFUL: the all-time total with nothing said.</summary>
        [Fact]
        public async Task Verify2_A11_year_over_year_with_hyphens_is_not_the_all_time_total_unsaid()
        {
            using var rig = await BuildAsync(script: Total, calendar: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales year-over-year" });
            ScriptTaken(a);
            Assert.False(SilentTotal(a), "'year-over-year' ran the all-time total with nothing said: " + Seen(a));
        }

        /// <summary>A12. "net sales in 2023" on data holding 2024 and 2025 only. The engine offers Jev only bounds for a
        /// year the column does not hold (after, from, before, up to) or "none", so "none", sure, is the plausible pick.
        /// Right: needs-you, or an answer that says there is no 2023 in this data. HARMFUL: the all-time total (2024 and
        /// 2025) shown as the answer for 2023, or a bound that still takes in 2024 and 2025.</summary>
        [Fact]
        public async Task Verify2_A12_a_year_the_data_does_not_hold_is_not_answered_with_the_all_time_total()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => IsFilterQ(q) ? (JevQueryReader.None, 0.9) : V2Base(r, q);
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales in 2023" });
            ScriptTaken(a);
            var year = FilterOn(a, "'Date'[Year]");
            var only2023 = year != null && ((year.Op == QueryOps.Is && year.Values.All(v => v == "2023"))
                || (year.Op == QueryOps.UpTo && year.Values[0] == "2023") || (year.Op == QueryOps.Before && year.Values[0] == "2024"));
            Assert.False(Answered(a) && Silent(a) && !only2023 && !Says(a, "2023"), "2023 was answered with numbers from other years: " + Seen(a));
        }

        /// <summary>B2 (hero run 2). "net sales in 2023" on data from 2024: "in" names one year, so it is Year = 2023, never
        /// Year up to 2023. The year is not there, so the answer is one plain line instead of a blank result.</summary>
        [Fact]
        public async Task B2_in_a_year_the_data_does_not_hold_says_so_in_one_line()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => V3FilterOn(r, q, "Date[Year]", "2023") ? (QueryOps.UpTo, 0.95) : V2Base(r, q);
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales in 2023" });
            ScriptTaken(a);
            Assert.NotEqual(QueryOps.UpTo, FilterOn(a, "'Date'[Year]")?.Op);
            Assert.Equal("There is no data for 2023. The data starts in 2024.", Assert.Single(a.Checks, c => !c.Passed).Line);
            Assert.Equal(DataAnswer.Refused, a.Outcome);
        }

        // ---- fiscal words -----------------------------------------------------------------------------------------

        /// <summary>A13. "net sales last FY", the way Australians write it. Right: needs-you naming 'last FY' (where a
        /// financial year starts is the model's business, the rule the lane applies to "last financial year").
        /// Acceptable: a note that names FY. HARMFUL: anything answered with no word about the FY, the all-time total
        /// above all.</summary>
        [Fact]
        public async Task Verify2_A13_last_fy_is_not_answered_without_a_word_about_the_financial_year()
        {
            using var rig = await BuildAsync(script: Total, calendar: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales last FY" });
            ScriptTaken(a);
            Assert.False(Answered(a) && Silent(a) && !Says(a, "FY"), "'last FY' was answered with nothing said about it: " + Seen(a));
        }

        /// <summary>A14. "net sales for financial year 2025" on a calendar-year model. In Australia FY2025 is July 2024 to
        /// June 2025. Right: needs-you (the same reason as "last financial year"). Acceptable: a note that says the
        /// calendar year 2025 was used. HARMFUL: Year = 2025 run as the answer with no word about the financial year.</summary>
        [Fact]
        public async Task Verify2_A14_financial_year_2025_is_not_quietly_the_calendar_year()
        {
            using var rig = await BuildAsync(script: (r, q) => V2Base(r, q), calendar: true);
            rig.Data.Latest = new DateTime(2025, 12, 15);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales for financial year 2025" });
            ScriptTaken(a);
            Assert.False(Answered(a) && Silent(a) && !Says(a, "financial"), "financial year 2025 was answered as the calendar year, unsaid: " + Seen(a));
        }

        // ---- typos ------------------------------------------------------------------------------------------------

        /// <summary>A15. "how did net sales do lsat month": a slip of the fingers on the time word. The values are matched
        /// with typos allowed; the time words are not. Right: last month, said. Acceptable: needs-you. HARMFUL: the
        /// all-time total with nothing said.</summary>
        [Fact]
        public async Task Verify2_A15_a_typo_in_last_month_is_not_the_all_time_total_unsaid()
        {
            using var rig = await BuildAsync(script: Total, calendar: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how did net sales do lsat month" });
            ScriptTaken(a);
            Assert.False(SilentTotal(a), "'lsat month' ran the all-time total with nothing said: " + Seen(a));
        }

        /// <summary>A16. "net sales for rain jakets": a typo and a plural at once, the shape of the fresh set's "water
        /// botles" (harmful twice on 28 Sep). The value is not matched, so Jev is asked about no filter, and "one total"
        /// is its honest count. Right: Rain Jacket's number. Acceptable: needs-you naming the words. HARMFUL: the
        /// all-products total shown as Rain Jacket's.</summary>
        [Fact]
        public async Task Verify2_A16_a_product_named_with_a_typo_is_not_answered_with_every_products_total()
        {
            using var rig = await BuildAsync(script: Total);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales for rain jakets" });
            ScriptTaken(a);
            Assert.False(Answered(a) && Silent(a) && !HasFilter(a, "'Product'[ProductName]") && !Says(a, "jakets"),
                "'rain jakets' ran the all-products total: " + Seen(a));
        }

        // ---- "slowest", "top", "per" ------------------------------------------------------------------------------

        /// <summary>A10. "top 3 products per region". Right: the top 3 products of each region. Acceptable: needs-you, or
        /// a note that says the top 3 is taken across every region. HARMFUL: TOPN 3 over every product-and-region pair,
        /// unsaid: three rows, often all from one region, read as the top 3 of each.</summary>
        [Fact]
        public async Task Verify2_A10_top_3_products_per_region_is_not_the_top_3_pairs_unsaid()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                V2Split(q, 1, "Product[ProductName]") ?? V2Split(q, 2, "Store[Region]") ?? V2Top(q, 3) ?? V2Base(r, q, splits: "2");
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "top 3 products per region" });
            ScriptTaken(a);
            Assert.False(Answered(a) && Silent(a) && a.Parts.Top == 3 && SplitBy(a, "'Product'[ProductName]") && SplitBy(a, "'Store'[Region]") && !Says(a, "top 3"),
                "'top 3 products per region' ranked the top 3 pairs across every region, unsaid: " + Seen(a));
        }

        /// <summary>A19. "our 3 best quarters for net sales" on a calendar with Quarter and Year, data in 2024 and 2025.
        /// Right: the 3 best quarters of any year, each quarter of each year its own row. Acceptable: needs-you. HARMFUL:
        /// Q1 to Q4 ranked with every year's quarter added together, unsaid (the quarter twin of "our 3 biggest months
        /// ever", which the lane fixed for months).</summary>
        [Fact]
        public async Task Verify2_A19_a_ranking_of_quarters_ranks_each_quarter_of_each_year()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => V2Split(q, 1, "Date[Quarter]") ?? V2Top(q, 3) ?? V2Base(r, q, splits: "1");
            using var rig = await V2QuarterRigAsync(Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "our 3 best quarters for net sales" });
            ScriptTaken(a);
            Assert.False(Answered(a) && Silent(a) && SplitBy(a, "'Date'[Quarter]") && !SplitBy(a, "'Date'[Year]") && !SplitBy(a, "'Date'[YearMonth]") && !Says(a, "year"),
                "the best quarters were ranked with every year's quarter added together: " + Seen(a));
        }

        /// <summary>A20. "net sales per month" with no calendar, on data holding 2024 and 2025. Right: each month of each
        /// year (Year and MonthName). Acceptable: needs-you, or a note that says each month adds every year together.
        /// HARMFUL: MonthName alone, January 2024 and January 2025 added into one row, unsaid.</summary>
        [Fact]
        public async Task Verify2_A20_net_sales_per_month_does_not_add_every_years_month_unsaid()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => V2Split(q, 1, "Date[MonthName]") ?? V2Base(r, q, splits: "1");
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales per month" });
            ScriptTaken(a);
            Assert.False(Answered(a) && Silent(a) && SplitBy(a, "'Date'[MonthName]") && !SplitBy(a, "'Date'[Year]") && !HasFilter(a, "'Date'[Year]") && !Says(a, "year"),
                "'per month' added every year's month together, unsaid: " + Seen(a));
        }

        // ---- found after the twenty ran, by reading QueryPeriods.Resolve (not one of the twenty labelled first) ------

        /// <summary>A21. "net sales this year vs last year by month" on a calendar, Jev splitting by MonthName (the
        /// shortlist offers it for "by month"). Resolve puts the two years side by side and then removes MonthName from
        /// the splits whatever the unit, so the month-by-month comparison the question names is dropped and two yearly
        /// totals come back, with a note that says only which years were read. Right: Year and MonthName side by side
        /// (each month of this year beside the same month of last year). Acceptable: needs-you, or a note that says the
        /// month split was dropped. HARMFUL: split by Year alone with nothing said about the months.</summary>
        [Fact]
        public async Task Verify2_A21_this_year_vs_last_year_by_month_keeps_the_month_split()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => V2Split(q, 1, "Date[MonthName]") ?? V2Base(r, q, splits: "1");
            using var rig = await BuildAsync(script: Script, calendar: true);
            rig.Data.Latest = new DateTime(2025, 12, 15);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales this year vs last year by month" });
            ScriptTaken(a);
            Assert.False(Answered(a) && Silent(a) && SplitBy(a, "'Date'[Year]") && !SplitBy(a, "'Date'[MonthName]") && !SplitBy(a, "'Date'[YearMonth]") && !Says(a, "month"),
                "the month split the question names was dropped: " + Seen(a));
        }
    }
}
