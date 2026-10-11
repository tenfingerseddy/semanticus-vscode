// VERIFY, m11/jev-query-tune round 4 of verification (verifier, 29 Sep), on round 5's gate: the gap question alone, the
// engine clearing a gap the reading already covers (StatedReading.Clears), and up to two second opinions.
//
// The brief asks whether the engine's own clearing can clear a gap the reading does NOT cover. It can, in seven ways, each
// proved here with Jev scripted the way the lane's own tests script it (Gate: the gap picked, sure). Every case gives Jev
// a gap that is REAL for the question and shows the engine running anyway, because a fact of the reading that has nothing
// to do with that gap "clears" it:
//   R01  "versions" is cleared by a split by a period (the with-and-without the question asks is not side by side).
//        Seen live in set A (va4-06): Jev said versions at 0.98, the engine ran it with a note about a change.
//   R02  "change" is cleared by two PLACES side by side (the difference between two regions is not a change over time).
//   R03  "change" is cleared by a split by month while the year it is compared with is not in the rows.
//   R04  "share" is cleared because the measure's description holds "generated": the kind check matches "rate" inside
//        another word (StatedReading.Kinds has no word boundaries on the measure side).
//   R05  "share" (an average) is cleared because the description says "as per": "per" anywhere counts as an average.
//   R06  "period" is cleared whenever any filter sits on a period column (StatesAPeriod), so a gap about a DIFFERENT span
//        ("fiscal 2025" read as Year 2025) can never stop a run. Seen live in set A (va4-08): Jev said period at 0.71 for
//        "the 2024 holiday season" read as Year 2024; the engine counted it as nothing missing.
//   R08  "share" is cleared by a total divided by a number: QueryCatalog.IsRatio calls any DIVIDE a ratio.
// And one LOW: R07, a cleared gap's note is dropped when a second opinion let the run go on.
//
// Each case asserts only that the run does not go ahead (and that the scripted reading was the one built, so a refused
// script cannot make it pass). Two more facts close the file: the two doors give the same answered and cleared answer,
// and the offered reading ("Show it anyway") runs the same through both doors.

using System;
using System.Collections.Generic;
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
        private static string V4Seen(DataAnswer a, Rig rig) =>
            Seen(a) + $"; statement '{a.Statement}'; runs {rig.Data.Runs.Count()}";

        [Fact]
        public async Task Verify4_R01_a_split_by_year_does_not_cover_two_versions_of_a_narrowing()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.CheckGapQ ? ("versions", 0.98)
                : V2Split(q, 1, "Date[Year]") ?? (IsFilterQ(q) && q.HasOption(QueryOps.IsNot) ? (QueryOps.IsNot, 0.95) : V3(r, q, splits: "1"));
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by year with and without kitchen products" });
            ScriptTaken(a);
            // The reading built is the "without" version alone, split by year.
            Assert.True(V3Keeps(a, "'Product'[Category]", QueryOps.IsNot, "Kitchen") && SplitBy(a, "'Date'[Year]"), V4Seen(a, rig));
            // K16: the gap is said beside the number, and no note claims it is covered.
            Assert.True(Answered(a), V4Seen(a, rig));
            Assert.False(string.IsNullOrEmpty(a.Assumption), "MEDIUM: Jev named the missing version (0.98) and the engine cleared it because the rows are years: nothing said. " + V4Seen(a, rig));
            Assert.DoesNotContain("off the rows", a.Note ?? "");
            Assert.DoesNotContain("asked for", a.Note ?? "");
        }

        [Fact]
        public async Task Verify4_R02_two_places_side_by_side_do_not_cover_a_change()
        {
            using var rig = await BuildAsync(script: Gate("change", 0.98));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how did coast and north change in 2025" });
            ScriptTaken(a);
            Assert.True(V3Keeps(a, "'Store'[Region]", QueryOps.Is, "Coast") && V3Keeps(a, "'Store'[Region]", QueryOps.Is, "North") && SplitBy(a, "'Store'[Region]")
                && V3Keeps(a, "'Date'[Year]", QueryOps.Is, "2025"), V4Seen(a, rig));
            // K16: the gap is said beside the number, and no note claims it is covered.
            Assert.True(Answered(a), V4Seen(a, rig));
            Assert.False(string.IsNullOrEmpty(a.Assumption), "HIGH: 2025 alone, Coast beside North, with a note to read the difference off the rows, for a change over time: nothing said. " + V4Seen(a, rig));
            Assert.DoesNotContain("off the rows", a.Note ?? "");
            Assert.DoesNotContain("asked for", a.Note ?? "");
        }

        [Fact]
        public async Task Verify4_R03_a_split_by_month_does_not_cover_a_change_against_a_year_it_does_not_show()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.CheckGapQ ? ("change", 0.98) : V2Split(q, 1, "Date[MonthName]") ?? V3(r, q, splits: "1");
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how did net sales in 2025 compare with the year before, month by month" });
            ScriptTaken(a);
            Assert.True(SplitBy(a, "'Date'[MonthName]") && V3Keeps(a, "'Date'[Year]", QueryOps.Is, "2025") && FilterOn(a, "'Date'[Year]").Values.Length == 1, V4Seen(a, rig));
            // K16: the gap is said beside the number, and no note claims it is covered.
            Assert.True(Answered(a), V4Seen(a, rig));
            Assert.False(string.IsNullOrEmpty(a.Assumption), "HIGH: the months of 2025 only, with a note that the periods are side by side so the change can be read off the rows: nothing said. " + V4Seen(a, rig));
            Assert.DoesNotContain("off the rows", a.Note ?? "");
            Assert.DoesNotContain("asked for", a.Note ?? "");
        }

        [Fact]
        public async Task Verify4_R04_rate_inside_another_word_of_a_description_does_not_make_a_share()
        {
            using var rig = await BuildAsync(script: Gate("share", 0.98));
            await rig.Engine.SetDescriptionAsync("measure:Sales/Net Sales", "Revenue generated by every order line, after discounts.", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "what percent of net sales came from the coast" });
            ScriptTaken(a);
            Assert.True(V3Keeps(a, "'Store'[Region]", QueryOps.Is, "Coast") && a.Parts.Measures.SequenceEqual(new[] { "Net Sales" }), V4Seen(a, rig));
            // K16: the gap is said beside the number, and no note claims it is covered.
            Assert.True(Answered(a), V4Seen(a, rig));
            Assert.False(string.IsNullOrEmpty(a.Assumption), "MEDIUM: an amount shown for a percent, with a note that the measure is the rate asked for ('generated' holds 'rate'): nothing said. " + V4Seen(a, rig));
            Assert.DoesNotContain("off the rows", a.Note ?? "");
            Assert.DoesNotContain("asked for", a.Note ?? "");
        }

        [Fact]
        public async Task Verify4_R05_per_in_a_description_does_not_make_an_average()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.CheckGapQ ? ("share", 0.98) : V2Split(q, 1, "Store[Region]") ?? V3(r, q, splits: "1");
            using var rig = await BuildAsync(script: Script);
            await rig.Engine.SetDescriptionAsync("measure:Sales/Net Sales", "Net sales, as per the finance team's month-end definition.", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "what is the average net sales by region" });
            ScriptTaken(a);
            Assert.True(SplitBy(a, "'Store'[Region]") && a.Parts.Measures.SequenceEqual(new[] { "Net Sales" }), V4Seen(a, rig));
            // K16: the gap is said beside the number, and no note claims it is covered.
            Assert.True(Answered(a), V4Seen(a, rig));
            Assert.False(string.IsNullOrEmpty(a.Assumption), "HIGH: each region's total shown as the average asked for, with a note saying so ('as per' holds 'per'): nothing said. " + V4Seen(a, rig));
            Assert.DoesNotContain("off the rows", a.Note ?? "");
            Assert.DoesNotContain("asked for", a.Note ?? "");
        }

        [Fact]
        public async Task Verify4_R06_a_year_filter_does_not_cover_a_period_gap_about_another_span()
        {
            using var rig = await BuildAsync(script: Gate("period", 0.95));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales in fiscal 2025" });
            ScriptTaken(a);
            Assert.True(V3Keeps(a, "'Date'[Year]", QueryOps.Is, "2025"), V4Seen(a, rig));
            // K16: the gap is said beside the number, and no note claims it is covered.
            Assert.True(Answered(a), V4Seen(a, rig));
            Assert.False(string.IsNullOrEmpty(a.Assumption), "HIGH: Jev named the period gap (0.95) and the engine cleared it because a Year filter is stated; 'where Year is 2025' reads as fiscal 2025: nothing said. " + V4Seen(a, rig));
            Assert.DoesNotContain("off the rows", a.Note ?? "");
            Assert.DoesNotContain("asked for", a.Note ?? "");
        }

        /// <summary>QueryCatalog.IsRatio calls any measure whose DAX holds a DIVIDE a ratio. A total scaled by a number (a figure
        /// in thousands, a sum converted by a rate) is still a total, so it covers no share: the gap must stay a stop.</summary>
        [Fact]
        public async Task Verify4_R08_a_total_divided_by_a_number_is_not_a_ratio()
        {
            using var rig = await BuildAsync(script: Gate("share", 0.98, measure: "Net Sales in Thousands"));
            await rig.Engine.CreateMeasureAsync("table:Sales", "Net Sales in Thousands", "DIVIDE ( SUM ( Sales[Amount] ), 1000 )", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "what percent of net sales in thousands came from the coast" });
            ScriptTaken(a);
            Assert.True(V3Keeps(a, "'Store'[Region]", QueryOps.Is, "Coast") && a.Parts.Measures.SequenceEqual(new[] { "Net Sales in Thousands" }), V4Seen(a, rig));
            // K16: the gap is said beside the number, and no note claims it is covered.
            Assert.True(Answered(a), V4Seen(a, rig));
            Assert.False(string.IsNullOrEmpty(a.Assumption), "MEDIUM: an amount in thousands shown for a percent, with a note that it is itself a ratio, so the share asked for: nothing said. " + V4Seen(a, rig));
            Assert.DoesNotContain("off the rows", a.Note ?? "");
            Assert.DoesNotContain("asked for", a.Note ?? "");
        }

        /// <summary>LOW. A gap the reading covers is said to the person ("read the change off the rows") when the run goes on at
        /// once, but not when a second opinion was needed first: CheckReadingAsync returns no note on that path.</summary>
        [Fact]
        public async Task Verify4_R07_a_cleared_gap_is_still_said_when_a_second_opinion_let_the_run_go_on()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.CheckGapQ ? ("nothing", 0.48)
                : q.Name == JevQueryReader.CheckRealQ ? ("yes", 0.05)
                : V2Split(q, 1, "Date[Year]") ?? V3(r, q, splits: "1");
            using var rig = await BuildAsync(script: Script);
            Spread(rig.Jev, ("nothing", 0.48), ("change", 0.32), ("narrowing", 0.2));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales trend by year" });
            ScriptTaken(a);
            Assert.True(Answered(a), V4Seen(a, rig));
            Assert.Single(SecondOpinions(rig));
            Assert.Contains("read the change off the rows", a.Note ?? "");
        }

        /// <summary>Both doors, an answered question whose gap the reading covers: the same statement, note, reading and rows.
        /// And the offered reading of a stopped question, taken through each door: the same run, with no Jev call.
        /// Void by a later ruling (join part 2, 30 Sep): it scripts a SURE change (0.98) and expects the engine to clear it, and
        /// round 6's ruling (the coordinator's ruling 1 for round 5, JevQueryCheck.Sure) is that a gap Jev names at 0.7 or more is
        /// never cleared. The same two-door facts, with a weaker gap, are Verify5_both_doors_..._with_a_weaker_gap (passing).</summary>
        [Fact(Skip = "Void by the round-6 ruling: a gap Jev is sure of (0.7 or more, JevQueryCheck.Sure) is never cleared, and this test scripts change at 0.98 and expects it cleared. Restated with a weaker gap as Verify5_both_doors_give_the_same_cleared_answer_and_the_same_offered_run_with_a_weaker_gap, which passes.")]
        public async Task Verify4_both_doors_give_the_same_cleared_answer_and_the_same_offered_run()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.CheckGapQ ? ("change", 0.98) : V2Split(q, 1, "Date[Year]") ?? V3(r, q, splits: "1");
            using var rig = await BuildAsync(script: Script);
            string Shape(DataAnswer a) => JsonSerializer.Serialize(new
            {
                a.Outcome, a.Statement, a.Reading, a.Note, a.Dax, a.RowCount, a.ReadBy, Unsure = a.Unsure ?? Array.Empty<string>(),
                Others = (a.Others ?? Array.Empty<DataOther>()).Select(o => new { o.Line, o.Reading, o.Instead, o.Anyway, o.Parts }).ToArray(), a.Parts,
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

            // A stopped reading, offered as it is; taking the offer through either door runs the same parts, with no Jev call.
            using var stop = await BuildAsync(script: Gate("change", 0.98, parts: VerifySure));
            var stopped = await stop.Engine.AskDataAsync(new DataAsk { Question = "how did net sales change in 2025" });
            Assert.Equal(DataAnswer.NeedsYou, stopped.Outcome);
            var offer = Assert.Single(stopped.Others, o => o.Anyway);
            var calls = stop.Jev.All.Count;
            var agentHeld = await McpTools.AskData(stop.Engine, measures: offer.Parts.Measures, filters: JsonDocument.Parse(JsonSerializer.Serialize(
                offer.Parts.Filters.Select(f => new { column = f.Column, op = f.Op, values = f.Values }))).RootElement);
            if (agentHeld.ApprovalId != null) Semanticus.Engine.ApprovalLedger.Approve(agentHeld.ApprovalId, "human");
            var anywayStudio = await new EngineRpcTarget(stop.Engine).askData(offer.Parts);
            var anywayAgent = await McpTools.AskData(stop.Engine, measures: offer.Parts.Measures, filters: JsonDocument.Parse(JsonSerializer.Serialize(
                offer.Parts.Filters.Select(f => new { column = f.Column, op = f.Op, values = f.Values }))).RootElement);
            Assert.True(Answered(anywayStudio), V4Seen(anywayStudio, stop));
            Assert.Equal(DataAnswer.Answered, anywayAgent.Outcome);
            Assert.Equal(anywayStudio.Statement, anywayAgent.Statement);
            Assert.Equal(anywayStudio.Dax, anywayAgent.Dax);
            Assert.Equal(stopped.Statement, anywayStudio.Statement);
            Assert.Equal(calls, stop.Jev.All.Count);
        }
    }
}
