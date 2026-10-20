// ask_data review-fix round 4 (29 Sep): the word check is retired as the gate (the ruling "Jev data questions v2").
// In its place: three engine guards on the engine's own reading (G1 a part another part cancels or makes moot, G2 a
// value matched only through a slip is said, G3 "last year to date" is the same span of last year), one stated reading
// (a sentence the person reads, and the same structure told to Jev in the question's own words), and a check by Jev
// before a number is shown: q:check:gap alone (round 5; round 4 also asked q:check:answers, which was never confident). The check can only stop a run.
//
// CI never calls Jev. The check is scripted here, or answered from the recorded live run (CheckRecordings) for the
// requests that run recorded. The rig is AskDataTests' own (partial class).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    public sealed partial class AskDataTests
    {
        // ---- scripting the check -----------------------------------------------------------------------------------

        /// <summary>The check's one answer (round 5): the gap, and how sure. Null for any other question.</summary>
        private static (string, double)? CheckSays(DecisionQuestion q, string gap, double gapConfidence = 0.92) =>
            q.Name == JevQueryReader.CheckGapQ ? (gap, gapConfidence) : ((string, double)?)null;

        private static Func<DecisionRequest, DecisionQuestion, (string, double)?> Checked(string gap, double gapConfidence = 0.92, string measure = "Net Sales", string splits = "0") =>
            (r, q) => CheckSays(q, gap, gapConfidence) ?? V3(r, q, measure, splits);

        private static string StatedFor(DecisionRequest check) => (string)check.Snapshot["stated_reading"];

        // ---- the labelled items of the check replay set ------------------------------------------------------------------

        /// <summary>A reading that is WRONG for the question is answered, and the recorded live check's gap is said beside the
        /// number (K16, Kane 8 Oct: a gap is an assumption line, never a stop). The reading still ran, the line is there, and the
        /// line came from the recorded live answer (not from the plain "nothing missing, yes" a test without one is given). Label "said".</summary>
        private static void AssertSaidByCheck(Rig rig, DataAnswer a, string harm)
        {
            Assert.True(a.Outcome == DataAnswer.Answered, harm + " was stopped, where a gap is now said beside the number: " + Seen(a));
            Assert.False(string.IsNullOrWhiteSpace(a.Assumption), harm + " was answered with nothing said about the gap: " + Seen(a));
            Assert.DoesNotContain("Nothing was run", a.Assumption);
            var check = Assert.Single(FirstChecks(rig));
            Assert.True(rig.Jev.Recorded.Contains(check.Digest), "no recorded live check holds this reading, so the line proves nothing (" + Seen(a) + ")");
        }

        /// <summary>A reading that is RIGHT for the question ran on the recorded live check's yes. Label "run".</summary>
        private static void AssertRunByCheck(Rig rig, DataAnswer a)
        {
            Assert.True(a.Outcome == DataAnswer.Answered, "a right reading was stopped: " + Seen(a));
            var check = Assert.Single(FirstChecks(rig));
            Assert.True(rig.Jev.Recorded.Contains(check.Digest), "no recorded live check holds this reading, so the run proves nothing");
        }

        // ---- G1: a part the others on the same column imply or cancel means a part was misread ----------------------

        /// <summary>An "is not" beside an "is" on one column: Jev slipped on "north" (Verify3 C12). The two cannot both be
        /// what the person meant. Since K16 (Kane, 8 Oct) it runs and the assumption line names both parts. A guard, not the check.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task G1_an_is_not_beside_an_is_on_one_column_runs_and_names_both_parts()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                V3FilterOn(r, q, "Store[Region]", "coast") ? (QueryOps.IsNot, 0.95)
                : V3FilterOn(r, q, "Store[Region]", "north") ? (QueryOps.Is, 0.9)
                : V3(r, q);
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales excluding coast and north" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Single(rig.Data.Runs);
            Assert.StartsWith("Read as ", a.Assumption);
            Assert.Contains("'coast'", a.Assumption);
            Assert.Contains("'north'", a.Assumption);
            Assert.Contains("Region", a.Assumption);
        }

        /// <summary>An "is" inside a bound: "from 2024 to 2025" with 2025 read as "is" (Verify3 C20). Runs, and the line names both.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task G1_an_is_inside_a_bound_runs_and_names_both_parts()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                V3FilterOn(r, q, "Date[Year]", "2024") ? (QueryOps.From, 0.95)
                : V3FilterOn(r, q, "Date[Year]", "2025") ? (QueryOps.Is, 0.9)
                : V3(r, q);
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales from 2024 to 2025" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Single(rig.Data.Runs);
            Assert.Contains("'2024'", a.Assumption);
            Assert.Contains("'2025'", a.Assumption);
        }

        /// <summary>An "is" against a bound: 2025 "is" beside "before 2024" can never hold. The one stop K16 keeps here: filters that
        /// leave nothing are refused (a number for nothing would be a wrong number), and nothing runs.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task G1_an_is_against_a_bound_that_leaves_nothing_is_refused()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                V3FilterOn(r, q, "Date[Year]", "2024") ? (QueryOps.Before, 0.95)
                : V3FilterOn(r, q, "Date[Year]", "2025") ? (QueryOps.Is, 0.9)
                : V3(r, q);
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales for 2025 before 2024" });
            ScriptTaken(a);
            Assert.Equal(DataAnswer.Refused, a.Outcome);
            Assert.Empty(rig.Data.Runs);
        }

        /// <summary>A bound another bound makes moot: two "from" bounds on one column. Runs, and the line names both.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task G1_a_bound_another_bound_makes_moot_runs_and_names_both()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                V3FilterOn(r, q, "Date[Year]", "2024") ? (QueryOps.From, 0.95)
                : V3FilterOn(r, q, "Date[Year]", "2025") ? (QueryOps.From, 0.9)
                : V3(r, q);
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales from 2024 to 2025" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Single(rig.Data.Runs);
            Assert.Contains("'2024'", a.Assumption);
            Assert.Contains("'2025'", a.Assumption);
        }

        /// <summary>A real range is never refused: "from 2024" with "up to 2025" is a lower and an upper bound. And it runs
        /// with nothing stopping it in the engine.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task G1_a_real_range_is_never_refused()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                V3FilterOn(r, q, "Date[Year]", "2024") ? (QueryOps.From, 0.95)
                : V3FilterOn(r, q, "Date[Year]", "2025") ? (QueryOps.UpTo, 0.9)
                : V3(r, q);
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales from 2024 to 2025" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("2024", V3Filters(a, "'Date'[Year]", QueryOps.From).Single().Values);
            Assert.Contains("2025", V3Filters(a, "'Date'[Year]", QueryOps.UpTo).Single().Values);
        }

        /// <summary>Two values of one column kept ("coast or north") and a left-out value of another column are not
        /// parts that cancel each other: nothing is refused.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task G1_values_of_one_kept_column_and_a_left_out_value_of_another_are_not_refused()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                V3FilterOn(r, q, "Product[Category]", "kitchen") ? (QueryOps.IsNot, 0.95) : V3(r, q);
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales for coast and north excluding kitchen" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
        }

        // ---- G2: a value matched only through a slip is said -----------------------------------------------------

        /// <summary>"coats" matched the value Coast by one swapped letter (Verify3 C10): the note says so, the way a
        /// name's slip is said. The number still runs (the check judges it); it is never silent.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task G2_a_value_matched_only_through_a_slip_is_said_in_the_note()
        {
            using var rig = await BuildAsync(script: (r, q) => V3(r, q, measure: "Units"));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how many coats did we sell?" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("Semanticus read 'coats' as the Region value Coast.", a.Note);
        }

        /// <summary>A plural is not a slip: "jackets" is the value Rain Jacket, and nothing needs saying.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task G2_a_plural_is_not_a_slip()
        {
            using var rig = await BuildAsync(script: (r, q) => V3(r, q));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales for rain jackets" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("Rain Jacket", V3Filters(a, "'Product'[ProductName]", QueryOps.Is).Single().Values);
            Assert.DoesNotContain("Semanticus read", a.Note ?? "");
        }

        /// <summary>A value said exactly is never a slip.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task G2_an_exact_value_is_not_a_slip()
        {
            using var rig = await BuildAsync(script: (r, q) => V3(r, q));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales for coast" });
            Assert.True(Answered(a), Seen(a));
            Assert.DoesNotContain("Semanticus read", a.Note ?? "");
        }

        // ---- G3: "last year to date" is the same span of last year ------------------------------------------------

        [Theory]
        [Trait("Round4", "1")]
        [InlineData("net sales last year to date", "2025-10-11", "2024-01-01", "2024-10-12")]
        [InlineData("net sales last month to date", "2025-10-11", "2025-09-01", "2025-09-12")]
        [InlineData("net sales last quarter to date", "2025-10-11", "2025-07-01", "2025-07-12")]
        // The month before is shorter than the day of the month the data ends on: the whole of it, no more.
        [InlineData("net sales last month to date", "2025-03-31", "2025-02-01", "2025-03-01")]
        // The data ends on the last day of the year: the same span is the whole of the year before.
        [InlineData("net sales last year to date", "2025-12-31", "2024-01-01", "2025-01-01")]
        public async Task G3_last_period_to_date_is_the_same_span_of_the_earlier_period(string question, string latest, string from, string before)
        {
            using var rig = await BuildAsync(script: (r, q) => V3(r, q), calendar: true);
            rig.Data.Latest = DateTime.ParseExact(latest, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Equal(new[] { from }, V3Filters(a, "'Date'[Date]", QueryOps.From).Single().Values);
            Assert.Equal(new[] { before }, V3Filters(a, "'Date'[Date]", QueryOps.Before).Single().Values);
            Assert.False(HasFilter(a, "'Date'[Year]") || HasFilter(a, "'Date'[YearMonth]"), "the whole earlier period was read by its label: " + Seen(a));
            Assert.Contains(question.Replace("net sales ", ""), a.Note);
        }

        /// <summary>No column of days to read the span on: it goes to unsupported, never the whole earlier period.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task G3_without_a_column_of_days_last_year_to_date_is_said_and_covers_every_date()
        {
            using var rig = await BuildAsync(script: (r, q) => V3(r, q));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales last year to date" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Equal("Could not apply 'last year to date', so this covers every date.", a.Assumption);
            Assert.Single(rig.Data.Runs);
        }

        /// <summary>This year to date is still this year (only the earlier period changed).</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task G3_year_to_date_is_still_this_year()
        {
            using var rig = await BuildAsync(script: (r, q) => V3(r, q), calendar: true);
            rig.Data.Latest = new DateTime(2025, 10, 11);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales year to date" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("2025", V3Filters(a, "'Date'[Year]", QueryOps.Is).Single().Values);
        }

        // ---- the stated reading: what the answer shows, in one sentence, first ---------------------------------------

        [Fact]
        [Trait("Round4", "1")]
        public async Task Every_answer_opens_with_what_it_shows()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => V2Split(q, 1, "Store[Region]") ?? V3(r, q, splits: "1");
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by region for 2025" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Equal("This shows Net Sales for each Region, where Year is 2025 (1 January to 31 December 2025).", a.Statement);
        }

        [Fact]
        [Trait("Round4", "1")]
        public async Task The_statement_says_a_single_total_a_left_out_value_and_a_top_few()
        {
            using (var rig = await BuildAsync(script: (r, q) => V3(r, q)))
            {
                var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
                Assert.Equal("This shows Net Sales as one total.", a.Statement);
            }
            (string, double)? NotKitchen(DecisionRequest r, DecisionQuestion q) =>
                V3FilterOn(r, q, "Product[Category]", "kitchen") ? (QueryOps.IsNot, 0.95) : V3(r, q);
            using (var rig = await BuildAsync(script: NotKitchen))
            {
                var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales without kitchen" });
                Assert.Equal("This shows Net Sales as one total, where Category is not Kitchen.", a.Statement);
            }
            (string, double)? Top(DecisionRequest r, DecisionQuestion q) => V2Split(q, 1, "Product[ProductName]") ?? V2Top(q, 3) ?? V3(r, q, splits: "1");
            using (var rig = await BuildAsync(script: Top))
            {
                var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "top 3 products by net sales" });
                Assert.True(Answered(a), Seen(a));
                Assert.Equal("This shows Net Sales for each ProductName, keeping the top 3 by Net Sales.", a.Statement);
            }
        }

        [Fact]
        [Trait("Round4", "1")]
        public async Task The_statement_is_on_a_parts_answer_too_and_on_both_doors()
        {
            using var rig = await BuildAsync(script: (r, q) => V3(r, q));
            var parts = new DataAsk { Measures = new[] { "Net Sales" }, GroupBy = new[] { "'Store'[Region]" } };
            var studio = await new EngineRpcTarget(rig.Engine).askData(parts);
            var agent = await McpTools.AskData(rig.Engine, measures: new[] { "Net Sales" }, group_by: new[] { "'Store'[Region]" });
            if (agent.ApprovalId != null) { Semanticus.Engine.ApprovalLedger.Approve(agent.ApprovalId, "human"); agent = await McpTools.AskData(rig.Engine, measures: new[] { "Net Sales" }, group_by: new[] { "'Store'[Region]" }); }
            Assert.Equal("This shows Net Sales for each Region.", studio.Statement);
            Assert.Equal(studio.Statement, agent.Statement);
            Assert.Empty(rig.Jev.All);
        }

        // ---- what Jev is told: the same structure, in the question's own words -----------------------------------------

        /// <summary>The privacy promise: no value leaves. The reading Jev is shown holds a filter value only as the words the
        /// question itself holds ('coats', not the value Coast), no date, and no name the check does not need.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task The_reading_Jev_is_shown_holds_no_model_value_the_questions_words_do_not_hold()
        {
            using var rig = await BuildAsync(script: Checked("nothing", 0.97, measure: "Units"));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how many coats did we sell?" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            var check = Assert.Single(FirstChecks(rig));
            var stated = StatedFor(check);
            Assert.Contains("'coats'", stated);
            var words = Regex.Matches("how many coats did we sell?", @"[\p{L}\p{N}]+").Select(m => m.Value.ToLowerInvariant()).ToHashSet();
            foreach (var value in Values.Values.SelectMany(v => v).Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)).Distinct())
            {
                if (words.Contains(value.ToLowerInvariant())) continue;
                Assert.False(Regex.IsMatch(stated, @"(?<![\p{L}\p{N}])" + Regex.Escape(value) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase),
                    "the reading Jev is shown holds the model value '" + value + "': " + stated);
            }
            foreach (var sentinel in Sentinels) Assert.DoesNotContain(sentinel, stated, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("Apart from what is said above, it shows totals only. It works out no change of its own between periods, no share or percent of its own, and nothing about things with no rows.", stated);
            // The declared fields, and nothing else: value_matches is sent empty.
            Assert.Equal(new[] { "model_fields", "question_words", "stated_reading", "value_matches" }, check.Snapshot.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Empty((IEnumerable<object>)check.Snapshot["value_matches"]);
        }

        /// <summary>A time is told without a date or a value: the words, and what span of the data they mean.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task A_period_is_told_to_Jev_without_dates()
        {
            using var rig = await BuildAsync(script: Checked("nothing", 0.97), calendar: true);
            rig.Data.Latest = new DateTime(2025, 10, 11);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales last year" });
            Assert.True(Answered(a), Seen(a));
            var stated = StatedFor(Assert.Single(FirstChecks(rig)));
            Assert.Contains("the whole of the year before the latest year the data holds", stated);
            Assert.DoesNotMatch(@"20\d\d", stated);

            using var ytd = await BuildAsync(script: Checked("nothing", 0.97), calendar: true);
            ytd.Data.Latest = new DateTime(2025, 10, 11);
            var b = await ytd.Engine.AskDataAsync(new DataAsk { Question = "net sales last year to date" });
            Assert.True(Answered(b), Seen(b));
            var toDate = StatedFor(Assert.Single(FirstChecks(ytd)));
            Assert.Contains("from the first day of the year before the latest year to the same day of that year as the last day the data holds", toDate);
            Assert.DoesNotMatch(@"20\d\d", toDate);
        }

        [Fact]
        [Trait("Round4", "1")]
        public async Task A_reading_with_no_filter_is_told_as_not_narrowed_by_anything()
        {
            using var rig = await BuildAsync(script: Checked("nothing", 0.97));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("not narrowed by anything", StatedFor(Assert.Single(FirstChecks(rig))));
        }

        /// <summary>Only the measures and columns the reading uses go, with their descriptions; never a list of the model.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task The_check_sends_only_the_measures_and_columns_the_reading_uses()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => CheckSays(q, "nothing", 0.97) ?? V2Split(q, 1, "Store[Region]") ?? V3(r, q, splits: "1");
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by region for 2025" });
            Assert.True(Answered(a), Seen(a));
            var check = Assert.Single(FirstChecks(rig));
            var fields = (IDictionary<string, object>)check.Snapshot["model_fields"];
            var measures = ((IEnumerable<object>)fields["measures"]).Cast<IDictionary<string, object>>().Select(m => (string)m["name"]).ToArray();
            var columns = ((IEnumerable<object>)fields["columns"]).Cast<IDictionary<string, object>>().Select(c => (string)c["table"] + "[" + (string)c["name"] + "]").OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "Net Sales" }, measures);
            Assert.Equal(new[] { "Date[Year]", "Store[Region]" }, columns);
        }

        // ---- the check: plumbing -----------------------------------------------------------------------------------

        [Fact]
        [Trait("Round4", "1")]
        public async Task A_nothing_missing_at_the_bar_runs()
        {
            using var rig = await BuildAsync(script: Checked("nothing", 0.97));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            Assert.True(Answered(a), Seen(a));
            Assert.NotEmpty(rig.Data.Runs);
            Assert.Equal("jev", a.ReadBy);
            Assert.Equal(2, rig.Jev.All.Count);
        }

        /// <summary>K16: a gap the check is sure of never stops the run. The number is shown, with the statement on top, and the
        /// one assumption line says what the reading leaves out. Nothing is offered "anyway": nothing was held back.</summary>
        [Theory]
        [Trait("Round4", "1")]
        [InlineData("change", "The question asks how the numbers changed, and Semanticus does not work out a change yet.")]
        [InlineData("share", "The question asks for a share, a percent or an average that no measure here gives, and Semanticus does not work it out for you yet.")]
        [InlineData("none_or_only", "The question asks about things with no sales, or sold in one place only, and Semanticus does not work that out yet.")]
        public async Task A_gap_is_said_beside_the_number_and_the_run_goes_on(string gap, string line)
        {
            using var rig = await BuildAsync(script: Checked(gap, 0.97));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Equal("This shows Net Sales as one total.", a.Statement);
            Assert.Equal(line, a.Assumption);
            Assert.Empty(a.Unsure);
            Assert.Single(rig.Data.Runs);
            Assert.NotEmpty(a.Rows);
            Assert.NotNull(a.Dax);
            Assert.Empty(a.Others);
        }

        /// <summary>K16: a check that is not sure, or could not be made, says nothing. The number is the answer, with no line.</summary>
        [Theory]
        [Trait("Round4", "1")]
        [InlineData(0.3)]
        [InlineData(0.6)]
        public async Task A_nothing_below_the_bar_with_no_gap_able_to_lift_it_runs_without_a_line(double nothing)
        {
            using var rig = await BuildAsync(script: Checked("nothing", nothing));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Null(a.Assumption);
            Assert.Single(rig.Data.Runs);
        }

        [Fact]
        [Trait("Round4", "1")]
        public async Task A_check_that_was_not_decided_runs_without_a_line()
        {
            using var rig = await BuildAsync(script: (r, q) => V3(r, q));
            rig.Jev.NotDecideChecks = true;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales" });
            Assert.True(Answered(a), Seen(a));
            Assert.Null(a.Assumption);
            Assert.Single(rig.Data.Runs);
            Assert.Equal("This shows Net Sales as one total.", a.Statement);
        }

        [Fact]
        [Trait("Round4", "1")]
        public async Task Given_parts_are_not_checked()
        {
            using var rig = await BuildAsync(script: Checked("change", 0.0));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } });
            Assert.True(Answered(a), Seen(a));
            Assert.Empty(rig.Jev.All);
        }

        /// <summary>A split on a column the number cannot reach through active relationships would refuse the whole question; since
        /// K16 it is left out and the line says why, so the number still comes.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task A_split_the_number_cannot_reach_is_left_out_and_said()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => V2Split(q, 1, "Customer[Segment]") ?? V3(r, q, splits: "1");
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by segment" });
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("not linked to Net Sales", a.Assumption ?? "");
            Assert.Empty(a.Parts.GroupBy ?? Array.Empty<string>());
        }

        /// <summary>The check runs immediately before the run, whatever Run says.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task A_built_but_not_run_reading_is_checked_too()
        {
            using var rig = await BuildAsync(script: Checked("change", 0.97));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales", Run = false });
            Assert.Equal(DataAnswer.Built, a.Outcome);
            Assert.Equal("The question asks how the numbers changed, and Semanticus does not work out a change yet.", a.Assumption);
            Assert.Single(FirstChecks(rig));

            using var ok = await BuildAsync(script: Checked("nothing", 0.97));
            var b = await ok.Engine.AskDataAsync(new DataAsk { Question = "net sales", Run = false });
            Assert.Equal(DataAnswer.Built, b.Outcome);
            Assert.Single(FirstChecks(ok));
        }

        /// <summary>Golden rule 2: the Studio and the assistant give the same answer to a question with a gap said beside it.</summary>
        [Fact]
        [Trait("Round4", "1")]
        public async Task The_studio_and_the_assistant_give_the_same_answer_to_a_reading_with_a_said_gap()
        {
            using var rig = await BuildAsync(script: Checked("change", 0.97));
            string Shape(DataAnswer a) => JsonSerializer.Serialize(new
            {
                a.Outcome, a.Statement, a.Reading, a.Note, a.ReadBy, a.Assumption, Unsure = a.Unsure ?? Array.Empty<string>(),
                Others = (a.Others ?? Array.Empty<DataOther>()).Select(o => new { o.Line, o.Reading, o.Instead, o.Anyway, o.Parts }).ToArray(), a.Parts,
            });
            var held = await McpTools.AskData(rig.Engine, question: "net sales");
            if (held.ApprovalId != null) Semanticus.Engine.ApprovalLedger.Approve(held.ApprovalId, "human");
            var viaStudio = await new EngineRpcTarget(rig.Engine).askData(new DataAsk { Question = "net sales" });
            var viaAgent = await McpTools.AskData(rig.Engine, question: "net sales");
            Assert.Equal(DataAnswer.Answered, viaStudio.Outcome);
            Assert.False(string.IsNullOrEmpty(viaStudio.Assumption));
            Assert.Equal(Shape(viaStudio), Shape(viaAgent));
        }
    }
}
