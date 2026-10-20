// m11/jev-batch: fewer, bigger Jev requests (Kane, 30 Sep 2026: "Yes, this release").
//
// R1 to R3 are the three small fixes from the audit of Jev against the TypeSafe skill (each written to FAIL on the code
// before the fix). The rest is the batching: one request per table with a question per object, an answer accepted
// question by question, and what the relay and the registry now declare for it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class JevBatchTests
    {
        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

        // ---- R1: plan.rank sorts on Jev's expected value --------------------------------------------------------

        /// <summary>A plan.rank reply the way the vendor sends it: the score is the expected position, fractional, counted
        /// from zero. A real reply's confidence sits near 0.6, under the 0.85 bar, so the old code dropped almost every item
        /// and the paid call yielded rule order.</summary>
        private static (DecisionAnswers Answers, ModelPlanItem[] Items) Reply(params (string Id, string Score, string Confidence, string Probabilities)[] rows)
        {
            var items = rows.Select(r => new ModelPlanItem { Id = r.Id, Title = "Do " + r.Id, Section = "Structure" }).ToArray();
            var questions = PlanRankPoint.Questions(items);
            var point = DecisionRegistry.Require(DecisionRegistry.PlanRank);
            var request = new DecisionRequest(point.Name, point.SchemaVersion,
                PlanRankPoint.Snapshot("Contoso", 3, 9, 2, false, items), questions);
            var legend = string.Join(",", questions[0].Options.Select((o, i) => "\"" + i + "\":" + JsonSerializer.Serialize(o.Criterion)));
            var body = "{" + string.Join(",", rows.Select(r =>
                JsonSerializer.Serialize(PlanRankPoint.QuestionFor(r.Id)) + ":{\"type\":\"score\",\"score\":" + r.Score
                + ",\"confidence\":" + r.Confidence + ",\"legend\":{" + legend + "},\"probabilities\":" + r.Probabilities + "}")) + "}";
            var read = DecisionAnswerReader.Read(request, Json(body), "jev-1.13.0");
            Assert.True(read.Decided, read.NotDecidedReason);
            return (read, items);
        }

        [Fact]
        public void R1_answers_under_the_bar_still_order_the_plan_and_the_order_follows_the_expected_value()
        {
            // Three items Jev is only 0.55 to 0.64 sure of (a measured reply had 0.64). a and b both land on the "useful" rung,
            // but b's expected value is higher (2.6 against 2.1), so b must come first; c is expected near "small".
            var (answers, _) = Reply(
                ("a", "2.1", "0.64", "{\"0\":0,\"1\":0.15,\"2\":0.64,\"3\":0.16,\"4\":0.05}"),
                ("b", "2.6", "0.55", "{\"0\":0,\"1\":0.05,\"2\":0.55,\"3\":0.35,\"4\":0.05}"),
                ("c", "1.2", "0.6", "{\"0\":0.05,\"1\":0.6,\"2\":0.3,\"3\":0.05,\"4\":0}"));

            var scores = PlanRankPoint.Scores(answers, 0.85);

            Assert.Equal(3, scores.Count);
            Assert.True(scores["b"].Value > scores["a"].Value, "b is expected to be worth more than a");
            Assert.True(scores["a"].Value > scores["c"].Value, "a is expected to be worth more than c");
        }

        [Fact]
        public void R1_the_number_shown_beside_an_item_still_needs_the_bar()
        {
            var (answers, _) = Reply(
                ("sure", "3.9", "0.93", "{\"0\":0,\"1\":0,\"2\":0.02,\"3\":0.05,\"4\":0.93}"),
                ("unsure", "2.1", "0.55", "{\"0\":0,\"1\":0.15,\"2\":0.55,\"3\":0.25,\"4\":0.05}"));

            var scores = PlanRankPoint.Scores(answers, 0.85);

            Assert.True(scores["sure"].Confidence >= 0.85);
            Assert.Equal(0, scores["unsure"].Confidence);   // zero means no number is shown (ModelPlanItem.Confidence)
        }

        // ---- R3: folder options ranked by relevance, and a cut is said --------------------------------------------

        [Fact]
        public void R3_the_folder_options_are_the_most_relevant_ones_not_the_first_thirty_alphabetically()
        {
            // The caller lists the folders in order of relevance (this table's own folders first). "Zeta Money" is the folder this
            // table's measures already sit in, and it sorts last by name: the old code sorted by name and cut at thirty, so
            // the right folder was the one that went missing.
            var folders = new[] { "Zeta Money" }.Concat(Enumerable.Range(1, 44).Select(i => "Area " + i.ToString("00"))).ToList();

            var questions = MeasureDefaultsPoint.Questions(folders, wantsFolder: true, wantsFormat: false);

            var offered = questions.Single().Options.Select(o => o.Id).ToList();
            Assert.Contains(MeasureDefaultsPoint.FolderOptionId("Zeta Money"), offered);
            Assert.Equal(MeasureDefaultsPoint.FolderOptionId("Zeta Money"), offered[0]);
            Assert.Contains(DecisionRegistry.NoFolder, offered);
            Assert.Equal(31, offered.Count);   // thirty folders and "none"
        }

        [Fact]
        public void R3_a_cut_is_said_and_only_the_cap_cuts()
        {
            var folders = Enumerable.Range(1, 45).Select(i => "Area " + i.ToString("00")).ToList();

            var ranked = MeasureDefaultsPoint.RankFolders(folders);

            Assert.Equal(30, ranked.Offered.Count);
            Assert.Equal(45, ranked.Total);   // so the line can say fifteen were not offered
            // This table's own folders and the ones more of the model uses come first, whatever they are called.
            var uses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Area 40"] = 9, ["Area 41"] = 3 };
            var here = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Area 45" };
            var byRelevance = MeasureDefaultsPoint.RankFolders(folders, uses, here).Offered;
            Assert.Equal(new[] { "Area 45", "Area 40", "Area 41", "Area 01" }, byRelevance.Take(4));
        }

        // ---- a batched reply is read answer by answer -------------------------------------------------------------

        private static DecisionRequest Batched(bool perQuestion, params string[] names) =>
            new DecisionRequest("column.summarize", 1, new Dictionary<string, object>(StringComparer.Ordinal),
                names.Select(n => new DecisionQuestion(n, DecisionQuestionKind.Choice, "For the column: how?",
                    new[] { new DecisionOption("sum", "totals mean something"), new DecisionOption("none", "totals mean nothing") })),
                perQuestion);

        private static string Choice(string id, double confidence) =>
            "{\"type\":\"choice\",\"choice\":\"" + id + "\",\"confidence\":" + confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";

        [Fact]
        public void One_malformed_answer_of_a_batched_request_refuses_only_itself()
        {
            var request = Batched(true, "summarize_by:a", "summarize_by:b", "summarize_by:c");
            var reply = Json("{\"summarize_by:a\":" + Choice("sum", 0.9) + ",\"summarize_by:b\":" + Choice("nonsense", 0.9)
                + ",\"summarize_by:c\":" + Choice("none", 0.8) + "}");

            var read = DecisionAnswerReader.Read(request, reply, "jev-1.13.0");

            Assert.True(read.Decided);
            Assert.Equal("sum", read.For("summarize_by:a").Choice);
            Assert.Null(read.For("summarize_by:b"));
            Assert.Equal("none", read.For("summarize_by:c").Choice);
            Assert.Single(read.Rejected);
            Assert.Contains("nonsense", read.Rejected["summarize_by:b"], StringComparison.Ordinal);
            Assert.False(read.Complete, "a reply with an answer refused is never a whole reply");
        }

        [Fact]
        public void The_same_reply_to_a_request_that_is_not_batched_still_voids_the_whole_ask()
        {
            var request = Batched(false, "summarize_by:a", "summarize_by:b");
            var reply = Json("{\"summarize_by:a\":" + Choice("sum", 0.9) + ",\"summarize_by:b\":" + Choice("nonsense", 0.9) + "}");

            var read = DecisionAnswerReader.Read(request, reply, "jev-1.13.0");

            Assert.False(read.Decided);
            Assert.Empty(read.Answers);
        }

        [Fact]
        public void A_batched_answer_left_out_or_not_asked_is_recorded_and_the_rest_stand()
        {
            var request = Batched(true, "summarize_by:a", "summarize_by:b");
            var reply = Json("{\"summarize_by:a\":" + Choice("sum", 0.9) + ",\"summarize_by:z\":" + Choice("sum", 0.9) + "}");

            var read = DecisionAnswerReader.Read(request, reply, "jev-1.13.0");

            Assert.True(read.Decided);
            Assert.Equal("sum", read.For("summarize_by:a").Choice);
            Assert.Null(read.For("summarize_by:z"));
            Assert.Contains("summarize_by:b", read.Rejected.Keys);   // left out
            Assert.Contains("summarize_by:z", read.Rejected.Keys);   // never asked
        }

        [Fact]
        public void A_batched_reply_with_nothing_usable_is_not_decided()
        {
            var request = Batched(true, "summarize_by:a");

            var read = DecisionAnswerReader.Read(request, Json("{\"summarize_by:a\":" + Choice("nonsense", 0.9) + "}"), "jev-1.13.0");

            Assert.False(read.Decided);
            Assert.Contains("rejected", read.NotDecidedReason, StringComparison.Ordinal);
        }

        [Fact]
        public void The_evidence_digest_does_not_depend_on_how_the_reply_is_read()
        {
            Assert.Equal(Batched(true, "summarize_by:a").Digest, Batched(false, "summarize_by:a").Digest);
        }

        [Fact]
        public void An_object_reads_its_own_answers_under_the_plain_question_names_and_a_refused_one_reads_as_not_decided()
        {
            var request = Batched(true, "summarize_by:a", "summarize_by:b");
            var read = DecisionAnswerReader.Read(request,
                Json("{\"summarize_by:a\":" + Choice("sum", 0.9) + ",\"summarize_by:b\":" + Choice("nonsense", 0.9) + "}"), "jev-1.13.0");

            var a = read.Narrow(new[] { new KeyValuePair<string, string>("summarize_by:a", "summarize_by") });
            var b = read.Narrow(new[] { new KeyValuePair<string, string>("summarize_by:b", "summarize_by") });

            Assert.True(a.Decided);
            Assert.Equal("sum", a.For("summarize_by").Choice);
            Assert.False(b.Decided);
            Assert.Contains("nonsense", b.NotDecidedReason, StringComparison.Ordinal);
        }

        [Fact]
        public void A_score_answer_carries_the_expected_position_and_a_reply_with_none_still_reads()
        {
            var (answers, _) = Reply(("a", "2.45", "0.55", "{\"0\":0,\"1\":0.05,\"2\":0.55,\"3\":0.35,\"4\":0.05}"));
            Assert.Equal(2.45, answers.For(PlanRankPoint.QuestionFor("a")).Expected.Value, 3);
            Assert.Equal("useful", answers.For(PlanRankPoint.QuestionFor("a")).Score);
        }

        // ---- the run's cap (the packing itself is in JevBatchAcrossTablesTests) --------------------------------------------------------------------------------------------------

        [Fact]
        public void A_run_takes_whole_chunks_within_its_cap_and_never_none()
        {
            TableBatch<string> Chunk(int n) => new TableBatch<string> { Asks = Enumerable.Range(0, n).Select(i => "x" + i).ToList() };
            var fresh = new[] { Chunk(20), Chunk(20), Chunk(20) };

            Assert.Equal(2, JevTableBatch.TakeWithinCap(fresh, 40).Count);
            Assert.Equal(2, JevTableBatch.TakeWithinCap(fresh, 59).Count);
            Assert.Single(JevTableBatch.TakeWithinCap(fresh, 5));   // a small cap cannot starve the family
            Assert.Equal(3, JevTableBatch.TakeWithinCap(fresh, 60).Count);
        }

        [Fact]
        public void A_request_that_asks_a_table_at_a_time_gets_a_little_more_time_and_a_small_one_does_not()
        {
            Assert.Equal(TimeSpan.Zero, JevDecisionOracle.ExtraFor(Batched(true, Enumerable.Range(0, 8).Select(i => "q:" + i).ToArray())));
            Assert.Equal(TimeSpan.FromSeconds(3), JevDecisionOracle.ExtraFor(Batched(true, Enumerable.Range(0, 20).Select(i => "q:" + i).ToArray())));
            Assert.Equal(TimeSpan.FromSeconds(8), JevDecisionOracle.ExtraFor(Batched(true, Enumerable.Range(0, 90).Select(i => "q:" + i).ToArray())));
        }
    }
}
