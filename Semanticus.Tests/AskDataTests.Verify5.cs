// VERIFY, m11/jev-query-tune round 5 of verification (verifier, 30 Sep), on round 6: a ratio says what it divides (D1, G5), the
// period's kind (G4), the engine never overrides a sure gap and clears a weaker one only when the reading covers it (D3), G1's
// bound words (D7). Jev is scripted the lane's way. Each case below fails at a4742e91 and is left failing on purpose:
//
//   R09  a financial year named another way ("tax year") or mistyped ("fisacl") is read as the calendar year and runs: G4's words
//        do not hold it and the check said nothing missing. Seen live in set A: va5-02 (nothing 0.97) and va5-03 (nothing 0.83,
//        two second opinions), both answered "This shows Net Sales as one total, where Year is 2025."
//   R10  a weaker period gap is cleared whenever the engine read ANY span from the words, though the words name a span INSIDE it
//        ("last year's holiday season", "the first quarter of this year"): the stated span is not the asked span.
//   R11  a weaker versions gap is cleared because the query shows two MEASURES, though only one of the two versions asked for is
//        in the rows, and the note says both are side by side.
//   R12  a share of orders runs a ratio of value: the engine clears the gap Jev ranked first (another kind of number, 0.45 against
//        nothing 0.21) because "percent" is a kind of "Rate %", and G5 is skipped because "sales" in "sales orders" is a word of
//        what the ratio divides. Seen live in set A: va5-01, answered with the note "The measure shown is the rate asked for."
//   R13  a ratio asked over another base ("the return rate as a share of gross sales") runs the ratio over net sales: the ratio's
//        own name in the words clears the share gap, whatever base the words name. Seen live in set A: va5-19.
//   R14  a ratio written with variables or a guard (VAR ... RETURN DIVIDE ( r, s ), IF ( ..., [A] / [B] )) is not a ratio to
//        RatioDax, so its statement does not say what it divides and G5 never sees it: round 4's D1 on a model written that way.
//   R15  a bound word before the period words the engine reads itself ("since last year") is read as that one period and runs:
//        G1 looks for bound words only before a matched value. D7's own words name since.
//   F01  friction, G5: "return % by category" stops, though the words name the model's own return percentage. Seen live in set A
//        as vp5-01 ("return % by brand").
//   F02  friction, G4: "net sales for calendar year 2025, not fiscal" stops as a fiscal question. Seen live as vp5-03.
//   F03  friction, G1: "compare net sales from 2024 and 2025" stops, though "from" there names where the numbers come from, and two
//        years are listed; not reached live (the parts call asked first), deterministic here.
// At the join (part 2, 30 Sep) R10, R11, R13, R14, R15 and F03 were fixed in the product and now pass (StatedReading.Clears for the
// period, versions and share gaps; RatioDax reads VAR and guarded ratios; QueryGuards.G1 reads a listed pair and BoundPeriod a bound
// word before the period words). R09, R12, F01 and F02 were fixed in round 7.
// Since K16 (Kane, 8 Oct) a gap is said beside the number and never stops a run; the two-door test lives in Round6.cs.

using System;
using System.Linq;
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
        [Theory]
        [InlineData("net sales for the 2025 tax year")]
        [InlineData("net sales in fisacl 2025")]
        public async Task Verify5_R09_a_financial_year_named_another_way_is_not_the_calendar_year(string question)
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, parts: VerifySure));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            ScriptTaken(a);
            Assert.True(V3Keeps(a, "'Date'[Year]", QueryOps.Is, "2025"), V4Seen(a, rig));
            // K16: the gap is said beside the number, and no note claims it is covered.
            Assert.True(Answered(a), V4Seen(a, rig));
            Assert.False(string.IsNullOrEmpty(a.Assumption), "HIGH: 'where Year is 2025' reads as the financial year asked for, and nothing says the calendar stands in for it: nothing said. " + V4Seen(a, rig));
            Assert.DoesNotContain("off the rows", a.Note ?? "");
            Assert.DoesNotContain("asked for", a.Note ?? "");
        }

        [Fact]
        public async Task Verify5_R12_a_share_of_orders_is_not_the_rate_a_ratio_of_value_gives()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.CheckGapQ ? ("other_number", 0.4) : q.Name == JevQueryReader.CheckRealQ ? ("yes", 0.18) : V3(r, q, "Return Rate %");
            using var rig = await BuildAsync(script: Script);
            await ReturnRateAsync(rig);
            // The live answer to set A's va5-01, as Jev gave it.
            Spread(rig.Jev, ("other_number", 0.45), ("nothing", 0.21), ("narrowing", 0.13), ("split", 0.12), ("share", 0.09));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "what percent of sales orders had a return in 2025" });
            ScriptTaken(a);
            Assert.True(a.Parts.Measures.SequenceEqual(new[] { "Return Rate %" }) && V3Keeps(a, "'Date'[Year]", QueryOps.Is, "2025"), V4Seen(a, rig));
            // K16: the gap is said beside the number, and no note claims it is covered.
            Assert.True(Answered(a), V4Seen(a, rig));
            Assert.False(string.IsNullOrEmpty(a.Assumption), "MEDIUM: returned value over sales value shown for a share of orders, under the note that it is the rate asked for: nothing said. " + V4Seen(a, rig));
            Assert.DoesNotContain("off the rows", a.Note ?? "");
            Assert.DoesNotContain("asked for", a.Note ?? "");
        }

        [Theory]
        [InlineData("VAR r = [Returns Amount] VAR s = [Net Sales] RETURN DIVIDE ( r, s )")]
        [InlineData("IF ( [Net Sales] = 0, BLANK (), [Returns Amount] / [Net Sales] )")]
        [InlineData("IFERROR ( [Returns Amount] / [Net Sales], BLANK () )")]
        public void Verify5_R14_a_ratio_written_with_variables_or_a_guard_is_still_a_ratio(string dax)
        {
            var m = new QueryMeasure { Table = "Sales", Name = "Return Rate %", Expression = dax, FormatString = "0.0%" };
            Assert.True(m.IsRatio, "not read as a ratio: " + dax);
            Assert.Contains("Returns Amount", m.RatioParts?.Numerator ?? "");
            Assert.Contains("Net Sales", m.RatioParts?.Denominator ?? "");
        }

        [Fact]
        public async Task Verify5_R14_a_share_of_orders_is_said_when_the_ratio_is_written_with_variables()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.96, measure: "Return Rate %"));
            await rig.Engine.CreateMeasureAsync("table:Sales", "Returns Amount", "CALCULATE ( SUM ( Sales[Amount] ), Sales[Qty] < 0 )", "human");
            await rig.Engine.CreateMeasureAsync("table:Sales", "Return Rate %", "VAR r = [Returns Amount] VAR s = [Net Sales] RETURN DIVIDE ( r, s )", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "what percentage of orders had a return" });
            ScriptTaken(a);
            Assert.True(Answered(a), V4Seen(a, rig));
            Assert.False(string.IsNullOrEmpty(a.Assumption), "HIGH (round 4's D1): 'This shows Return Rate % as one total.' for a share of orders: nothing said. " + V4Seen(a, rig));
            Assert.Contains("divided by", a.Assumption + " " + a.Statement);
        }

        /// <summary>D7 names the bound words (from, since, after, before, until) read as one period. G1 reads them only before a
        /// matched VALUE ("since 2024"), not before the period words the engine reads itself ("since last year"): "since last year"
        /// is read as the whole of last year alone and runs when the check says nothing missing (the Studio driver's own scene 09i
        /// asks exactly this question, and its mocked check stops it).</summary>
        [Theory]
        [InlineData("net sales since last year")]
        [InlineData("net sales since last quarter")]
        public async Task Verify5_R15_a_bound_word_before_the_period_words_is_not_one_period(string question)
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.CheckGapQ ? ("nothing", 0.97) : q.Name == JevQueryReader.CheckRealQ ? ("yes", 0.97) : V3(r, q);
            using var rig = await BuildAsync(script: Script, calendar: true);
            rig.Data.Latest = new DateTime(2025, 12, 15);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            ScriptTaken(a);
            Assert.Contains("is read as", a.Note ?? "");
            // K16: the gap is said beside the number, and no note claims it is covered.
            Assert.True(Answered(a), V4Seen(a, rig));
            Assert.False(string.IsNullOrEmpty(a.Assumption), "MEDIUM: one earlier period stated for 'since' it, the periods after it left out: nothing said. " + V4Seen(a, rig));
            Assert.DoesNotContain("off the rows", a.Note ?? "");
            Assert.DoesNotContain("asked for", a.Note ?? "");
        }

        [Theory]
        [InlineData("return % by category")]
        [InlineData("returns percentage by category")]
        public async Task Verify5_F01_the_words_for_the_models_own_return_percentage_are_not_a_share_of_something_else(string question)
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, measure: "Return Rate %", splits: "1", parts: (r, q) => V2Split(q, 1, "Product[Category]")));
            await ReturnRateAsync(rig);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            ScriptTaken(a);
            Assert.True(SplitBy(a, "'Product'[Category]") && a.Parts.Measures.SequenceEqual(new[] { "Return Rate %" }), V4Seen(a, rig));
            Assert.True(Answered(a), "friction (G5): Return Rate % by category stopped for 'return %': " + V4Seen(a, rig));
        }

        [Fact]
        public async Task Verify5_F02_a_question_that_asks_for_the_calendar_year_is_not_a_fiscal_question()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, parts: VerifySure));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales for calendar year 2025, not fiscal" });
            ScriptTaken(a);
            Assert.True(V3Keeps(a, "'Date'[Year]", QueryOps.Is, "2025"), V4Seen(a, rig));
            Assert.True(Answered(a), "friction (G4): the calendar year the words ask for stopped as a fiscal question: " + V4Seen(a, rig));
        }

        [Fact]
        public async Task Verify5_F03_from_before_two_listed_years_is_not_a_range()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, splits: "1", parts: (r, q) => V2Split(q, 1, "Date[Year]")));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "compare net sales from 2024 and 2025" });
            ScriptTaken(a);
            Assert.True(V3Keeps(a, "'Date'[Year]", QueryOps.Is, "2024") && V3Keeps(a, "'Date'[Year]", QueryOps.Is, "2025"), V4Seen(a, rig));
            Assert.True(Answered(a), "friction (G1): 2024 beside 2025 stopped because 'from' sits before '2024': " + V4Seen(a, rig));
        }

    }
}
