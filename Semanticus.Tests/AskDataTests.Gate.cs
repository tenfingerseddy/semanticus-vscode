// ask_data review-fix round 5 (29 Sep): the gate. Round 4's pilot showed the yes/no question is never confident (its top P(yes)
// was 0.83) while the gap question separates well, so the gap question is the gate, alone. Three changes are proved here:
//   1. The first check call holds the gap question only.
//   2. The engine clears a gap the reading already covers, from the reading's own facts (periods or versions side by side,
//      a ratio measure, a measure named for the kind the words name, a period the engine read from the words): the run goes
//      on, with the stated reading on top and a note saying what the person reads off the rows. Round 6 (D3): only a gap Jev
//      is NOT sure of (under 0.7) is ever cleared; these tests script the gap weakly (nothing 0.6, the gap 0.35), and the
//      same reading with a sure gap is a stop (AskDataTests.Round6.cs).
//   3. When the gap is neither "nothing" nor cleared, up to two second opinions ask whether the question really asks for it,
//      side by side, and only a sure "no" lets the run go on. A gap no fact clears is never cleared.
// Since K16 (Kane, 8 Oct) the check never stops a run: a gap it is sure of is said beside the number, one assumption line.
// CI never calls Jev (the answers are scripted).

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
        // ---- scripting the gate --------------------------------------------------------------------------------------

        /// <summary>The gap picked at a confidence, and what the second opinion says of a gap (P of "yes, the question does ask
        /// for it"), by the gap's id. Anything else is the round-3 harness.</summary>
        private static Func<DecisionRequest, DecisionQuestion, (string, double)?> Gate(string gap, double confidence, double real = 0.97, string measure = "Net Sales", string splits = "0",
            Func<DecisionRequest, DecisionQuestion, (string, double)?> parts = null) =>
            (r, q) => q.Name == JevQueryReader.CheckGapQ ? (gap, confidence)
                : q.Name == JevQueryReader.CheckRealQ ? ("yes", real)
                : parts?.Invoke(r, q) ?? V3(r, q, measure, splits);

        /// <summary>The probability mass Jev spreads over the gap options.</summary>
        private static void Spread(ScriptedOracle jev, params (string Gap, double P)[] mass) =>
            jev.Probabilities = (r, q) => q.Name == JevQueryReader.CheckGapQ ? mass.ToDictionary(m => m.Gap, m => m.P) : null;

        private static IReadOnlyList<DecisionRequest> FirstChecks(Rig rig) => rig.Jev.Checks.Where(c => c.Questions.Any(q => q.Name == JevQueryReader.CheckGapQ)).ToList();
        private static IReadOnlyList<DecisionRequest> SecondOpinions(Rig rig) => rig.Jev.Checks.Where(c => c.Questions.Any(q => q.Name == JevQueryReader.CheckRealQ)).ToList();

        // ---- 1. the gap question is the gate, alone -------------------------------------------------------------------

        [Fact]
        [Trait("Round5", "1")]
        public async Task The_first_check_call_holds_the_gap_question_only_and_says_what_a_reading_covers()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.97));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            Assert.True(Answered(a), Seen(a));
            var check = Assert.Single(rig.Jev.Checks);
            var q = Assert.Single(check.Questions);
            Assert.Equal(JevQueryReader.CheckGapQ, q.Name);
            Assert.Equal(DecisionQuestionKind.Choice, q.Kind);
            Assert.Equal("nothing", q.Options[0].Id);
            Assert.Equal(new[] { "nothing", "change", "share", "none_or_only", "future_or_stock", "unnamed", "other_number", "narrowing", "period", "ranking", "split", "versions" }, q.Options.Select(o => o.Id).ToArray());
            // The instructions say plainly what a reading that covers a gap looks like, before the gap is asked for.
            Assert.Contains("side by side", q.Instructions);
            Assert.Contains("ratio", q.Instructions);
            Assert.Contains("typo", q.Instructions);
            Assert.True(q.Instructions.IndexOf("side by side", StringComparison.Ordinal) < q.Instructions.IndexOf("Pick", StringComparison.Ordinal));
        }

        [Fact]
        [Trait("Round5", "1")]
        public async Task A_sure_nothing_runs_with_one_call_and_no_second_opinion()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.9));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            Assert.True(Answered(a), Seen(a));
            Assert.Single(rig.Jev.Checks);
            Assert.Empty(SecondOpinions(rig));
        }

        // ---- 2. the engine clears a gap the reading already covers ----------------------------------------------------------

        /// <summary>"did 2025 beat 2024": the two years are rows of their own, so a change is read off the rows. Jev's "change"
        /// (sure) is cleared by the fact, with no second opinion; the note says so; the statement stays on top.</summary>
        [Fact]
        [Trait("Round5", "1")]
        public async Task A_change_is_cleared_when_the_periods_are_shown_side_by_side()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.6, parts: VerifySure));
            Spread(rig.Jev, ("nothing", 0.6), ("change", 0.35));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "did 2025 beat 2024 on net sales" });
            Assert.True(Answered(a), Seen(a));
            Assert.Empty(SecondOpinions(rig));
            Assert.StartsWith("This shows", a.Statement);
            Assert.Contains("read the change off the rows", a.Note);
            Assert.Contains("side by side", StatedFor(Assert.Single(rig.Jev.Checks)));
        }

        [Fact]
        [Trait("Round5", "1")]
        public async Task A_change_is_cleared_when_the_split_is_by_a_period()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.6, parts: (r, q) => V2Split(q, 1, "Date[Year]") ?? V3(r, q, splits: "1")));
            Spread(rig.Jev, ("nothing", 0.6), ("change", 0.35));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales trend by year" });
            Assert.True(Answered(a), Seen(a));
            Assert.Empty(SecondOpinions(rig));
            Assert.Contains("read the change off the rows", a.Note);
        }

        /// <summary>One period alone shows no change: "change" is not cleared, and the line says so beside the number.</summary>
        [Fact]
        [Trait("Round5", "1")]
        public async Task A_change_is_not_cleared_by_one_period_alone()
        {
            using var rig = await BuildAsync(script: Gate("change", 0.98, parts: VerifySure));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how did net sales change in 2025" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Equal("The question asks how the numbers changed, and Semanticus does not work out a change yet.", a.Assumption);
            Assert.Single(rig.Data.Runs);
        }

        [Fact]
        [Trait("Round5", "1")]
        public async Task A_share_is_cleared_when_the_measure_is_itself_a_ratio()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.6, measure: "Average Price"), ratio: true);
            Spread(rig.Jev, ("nothing", 0.6), ("share", 0.35));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "what is the average price" });
            Assert.True(Answered(a), Seen(a));
            Assert.Empty(SecondOpinions(rig));
            Assert.Contains("ratio", StatedFor(Assert.Single(rig.Jev.Checks)));
        }

        [Fact]
        [Trait("Round5", "1")]
        public async Task A_share_is_not_cleared_by_a_plain_total()
        {
            using var rig = await BuildAsync(script: Gate("share", 0.99), ratio: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "what share of net sales was the coast" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("share or a percent", a.Assumption ?? "");
            Assert.Single(rig.Data.Runs);
        }

        [Fact]
        [Trait("Round5", "1")]
        public async Task Another_kind_of_number_is_cleared_when_the_measures_name_matches_the_words_kind()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.6, measure: "Average Price"), ratio: true);
            Spread(rig.Jev, ("nothing", 0.6), ("other_number", 0.35));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "average price per unit" });
            Assert.True(Answered(a), Seen(a));
            Assert.Empty(SecondOpinions(rig));

            // A count of products is another kind than any total the reading shows: not cleared, and the line says so.
            using var count = await BuildAsync(script: Gate("other_number", 0.99, measure: "Net Sales"), ratio: true);
            var b = await count.Engine.AskDataAsync(new DataAsk { Question = "number of products" });
            ScriptTaken(b);
            Assert.True(Answered(b), Seen(b));
            Assert.Contains("different kind of number", b.Assumption ?? "");
        }

        [Fact]
        [Trait("Round5", "1")]
        public async Task A_period_is_cleared_when_the_period_read_is_stated()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.6, parts: (r, q) => V3(r, q)), calendar: true);
            Spread(rig.Jev, ("nothing", 0.6), ("period", 0.35));
            rig.Data.Latest = new DateTime(2025, 10, 11);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales last year" });
            Assert.True(Answered(a), Seen(a));
            Assert.Empty(SecondOpinions(rig));
            Assert.Contains("the whole of the year before the latest year the data holds", StatedFor(Assert.Single(rig.Jev.Checks)));

            // No period read at all: the gap stands, and is said.
            using var none = await BuildAsync(script: Gate("period", 0.97));
            var b = await none.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            ScriptTaken(b);
            Assert.True(Answered(b), Seen(b));
            Assert.False(string.IsNullOrWhiteSpace(b.Assumption), Seen(b));
        }

        /// <summary>No fact clears these: an absence, a forecast, an unnamed thing. Even with periods shown side by side. Each is
        /// said beside the number, in the engine's own line for it.</summary>
        [Theory]
        [Trait("Round5", "1")]
        [InlineData("none_or_only")]
        [InlineData("future_or_stock")]
        [InlineData("unnamed")]
        [InlineData("narrowing")]
        public async Task A_gap_no_fact_clears_is_said_beside_the_number(string gap)
        {
            using var rig = await BuildAsync(script: Gate(gap, 0.99, parts: VerifySure), ratio: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "did 2025 beat 2024 on net sales" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Equal(JevQueryCheck.Gaps.First(g => g.Id == gap).Line, a.Assumption);
            Assert.Single(rig.Data.Runs);
        }

        // ---- 3. the second opinion --------------------------------------------------------------------------------------

        /// <summary>A gap that is not "nothing" and not cleared, with the mass left able to reach the bar: up to two second
        /// opinions, run side by side, ask whether the question really asks for it. Two sure "no"s let the run go on.</summary>
        [Fact]
        [Trait("Round5", "1")]
        public async Task Two_sure_noes_from_the_second_opinions_let_a_doubtful_reading_run_and_they_run_side_by_side()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.6, real: 0.04));
            Spread(rig.Jev, ("nothing", 0.6), ("narrowing", 0.25), ("ranking", 0.15));
            rig.Jev.CheckDelay = TimeSpan.FromMilliseconds(150);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            Assert.True(Answered(a), Seen(a));
            var seconds = SecondOpinions(rig);
            Assert.Equal(2, seconds.Count);
            Assert.Equal(2, rig.Jev.MaxCheckCallsInFlight);
            // One question each, about one gap each, holding the question's words, the reading, and no model value.
            foreach (var s in seconds)
            {
                var q = Assert.Single(s.Questions);
                Assert.Equal(DecisionQuestionKind.Noul, q.Kind);
                Assert.Equal(new[] { "model_fields", "question_words", "stated_reading", "value_matches" }, s.Snapshot.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            }
            Assert.Contains(seconds, s => s.Questions[0].Instructions.Contains(JevQueryCheck.AskOf("narrowing")));
            Assert.Contains(seconds, s => s.Questions[0].Instructions.Contains(JevQueryCheck.AskOf("ranking")));
        }

        [Fact]
        [Trait("Round5", "1")]
        public async Task A_second_opinion_that_says_yes_leaves_no_line_when_no_gap_is_sure()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.6, real: 0.9));
            Spread(rig.Jev, ("nothing", 0.6), ("narrowing", 0.25), ("ranking", 0.15));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Null(a.Assumption);
            Assert.Single(rig.Data.Runs);
            Assert.NotEmpty(SecondOpinions(rig));
        }

        /// <summary>The mass left over cannot reach the bar even if the two biggest gaps were voided: no calls are spent.</summary>
        [Fact]
        [Trait("Round5", "1")]
        public async Task No_second_opinion_is_asked_when_it_cannot_change_the_answer()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.1, real: 0.0));
            Spread(rig.Jev, ("nothing", 0.1), ("narrowing", 0.3), ("ranking", 0.3), ("split", 0.3));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Empty(SecondOpinions(rig));
            Assert.Single(rig.Jev.Checks);
        }

        /// <summary>A cleared gap counts as nothing: 0.3 nothing and 0.65 change (cleared by two periods side by side) is a run.</summary>
        [Fact]
        [Trait("Round5", "1")]
        public async Task A_cleared_gap_counts_as_nothing_missing_in_the_mass()
        {
            using var rig = await BuildAsync(script: Gate("change", 0.65, parts: VerifySure));
            Spread(rig.Jev, ("nothing", 0.3), ("change", 0.65), ("narrowing", 0.05));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "did 2025 beat 2024 on net sales" });
            Assert.True(Answered(a), Seen(a));
            Assert.Empty(SecondOpinions(rig));
        }

        /// <summary>A small doubt the second opinion doubts in turn is relieved in proportion: 0.8 nothing and 0.2 narrowing, the
        /// second opinion putting "yes" at 0.25, is 0.95 of nothing missing.</summary>
        [Fact]
        [Trait("Round5", "1")]
        public async Task A_small_doubt_the_second_opinion_doubts_is_relieved_in_proportion()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.8, real: 0.25));
            Spread(rig.Jev, ("nothing", 0.8), ("narrowing", 0.2));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            Assert.True(Answered(a), Seen(a));
            Assert.Single(SecondOpinions(rig));

            // The same doubt, when the second opinion believes it (0.9), stays under the bar.
            using var believed = await BuildAsync(script: Gate("nothing", 0.8, real: 0.9));
            Spread(believed.Jev, ("nothing", 0.8), ("narrowing", 0.2));
            var b = await believed.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            ScriptTaken(b);
            Assert.True(Answered(b), Seen(b));
        }

        /// <summary>A gap Jev is sure of (0.7 or more) is named in the stop line and no second opinion is spent on it (round 6, L3);
        /// a doubt below that is "not sure" unless a second opinion believes it.</summary>
        [Fact]
        [Trait("Round5", "1")]
        public async Task A_sure_gap_the_second_opinion_confirms_is_named()
        {
            using var rig = await BuildAsync(script: Gate("narrowing", 0.93, real: 0.92));
            Spread(rig.Jev, ("nothing", 0.05), ("narrowing", 0.93), ("ranking", 0.02));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            ScriptTaken(a);
            Assert.Equal("The question narrows the numbers in a way this reading does not show.", a.Assumption);
            Assert.Empty(SecondOpinions(rig));

            // A gap under 0.7 that a second opinion believes is named too (0.55 with a sure yes), where the bar alone would have said "not sure".
            using var weaker = await BuildAsync(script: Gate("narrowing", 0.6, real: 0.92));
            Spread(weaker.Jev, ("nothing", 0.3), ("narrowing", 0.6), ("ranking", 0.1));
            var b = await weaker.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            ScriptTaken(b);
            Assert.Equal("The question narrows the numbers in a way this reading does not show.", b.Assumption);
            Assert.NotEmpty(SecondOpinions(weaker));
        }

        /// <summary>The second opinion never lets through what a deterministic check refuses, and never runs on the parts path.</summary>
        [Fact]
        [Trait("Round5", "1")]
        public async Task Given_parts_are_not_checked_at_all_in_the_gate()
        {
            using var rig = await BuildAsync(script: Gate("change", 0.99));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } });
            Assert.True(Answered(a), Seen(a));
            Assert.Empty(rig.Jev.Checks);
        }
    }
}
