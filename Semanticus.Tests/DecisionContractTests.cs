// The typed decision contract: the digest, the field allow-list, and what a reply has to look like to be
// believed. Nothing here touches a model or a network.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DecisionContractTests
    {
        private static DecisionQuestion Choice(params string[] ids) =>
            new DecisionQuestion("pick", DecisionQuestionKind.Choice, "Which one?",
                ids.Select(i => new DecisionOption(i, "the world looks like " + i)));

        private static DecisionRequest Request(params DecisionQuestion[] questions) =>
            new DecisionRequest("measure.folder_and_format", 1,
                new Dictionary<string, object>(StringComparer.Ordinal) { { "measure_name", "Total Sales" } },
                questions);

        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

        // ---- the digest ------------------------------------------------------------------------------

        [Fact]
        public void The_same_evidence_produces_the_same_digest()
        {
            // Two requests built separately from the same evidence: the digest is a function of what was sent,
            // not of the object that carried it.
            var first = Request(Choice("a", "b"));
            var second = Request(Choice("a", "b"));
            Assert.Equal(first.Digest, second.Digest);
        }

        [Fact]
        public void A_different_option_ORDER_is_a_different_digest()
        {
            // A score ladder is ordered by meaning, and for a choice the order is still part of what was sent.
            // Two runs that ordered the options differently are two pieces of evidence, not one.
            Assert.NotEqual(Request(Choice("a", "b")).Digest, Request(Choice("b", "a")).Digest);
        }

        [Fact]
        public void Snapshot_key_order_does_NOT_change_the_digest()
        {
            var forward = new Dictionary<string, object>(StringComparer.Ordinal) { { "a", 1 }, { "b", 2 } };
            var backward = new Dictionary<string, object>(StringComparer.Ordinal) { { "b", 2 }, { "a", 1 } };
            Assert.Equal(DecisionDigest.OfSnapshot(forward), DecisionDigest.OfSnapshot(backward));
        }

        [Fact]
        public void A_changed_snapshot_value_changes_the_digest()
        {
            var one = DecisionDigest.OfSnapshot(new Dictionary<string, object> { { "measure_name", "Total Sales" } });
            var two = DecisionDigest.OfSnapshot(new Dictionary<string, object> { { "measure_name", "Total Cost" } });
            Assert.NotEqual(one, two);
        }

        [Fact]
        public void The_schema_version_is_in_the_digest()
        {
            var questions = new[] { Choice("a", "b") };
            var snapshot = new Dictionary<string, object> { { "measure_name", "Total Sales" } };
            var v1 = new DecisionRequest("measure.folder_and_format", 1, snapshot, questions);
            var v2 = new DecisionRequest("measure.folder_and_format", 2, snapshot, questions);
            Assert.NotEqual(v1.Digest, v2.Digest);
        }

        [Fact]
        public void A_snapshot_may_only_hold_typed_values()
        {
            var bad = new Dictionary<string, object> { { "when", new Uri("https://example.invalid") } };
            var e = Assert.Throws<ArgumentException>(() => DecisionDigest.OfSnapshot(bad));
            Assert.Contains("may only hold text", e.Message, StringComparison.Ordinal);
        }

        // ---- the field allow-list --------------------------------------------------------------------

        [Fact]
        public void A_point_cannot_send_a_field_it_has_not_declared()
        {
            var point = DecisionRegistry.Require(DecisionRegistry.MeasureFolderAndFormat);
            var e = Assert.Throws<InvalidOperationException>(() => DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("measure_name", "Total Sales"),
                new KeyValuePair<string, object>("row_values", "1, 2, 3"),
            }));
            Assert.Contains("has not declared", e.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void An_oversized_snapshot_is_refused_rather_than_truncated()
        {
            var snapshot = new Dictionary<string, object> { { "measure_name", new string('x', 4096) } };
            var e = Assert.Throws<InvalidOperationException>(() => DecisionSnapshot.RequireWithin(snapshot, 1024));
            Assert.Contains("never truncated", e.Message, StringComparison.Ordinal);
        }

        // ---- reading a reply -------------------------------------------------------------------------

        [Fact]
        public void A_well_formed_choice_is_accepted_with_its_confidence()
        {
            var request = Request(Choice("a", "b"));
            var answers = DecisionAnswerReader.Read(request,
                Json("{\"pick\":{\"type\":\"choice\",\"choice\":\"a\",\"confidence\":0.91,\"probabilities\":{\"a\":0.91,\"b\":0.09}}}"),
                "jev-test-1");

            Assert.True(answers.Decided);
            Assert.Equal("a", answers.For("pick").Choice);
            Assert.Equal(0.91, answers.For("pick").Confidence, 3);
        }

        [Fact]
        public void An_option_id_that_was_never_sent_is_rejected()
        {
            var answers = DecisionAnswerReader.Read(Request(Choice("a", "b")),
                Json("{\"pick\":{\"type\":\"choice\",\"choice\":\"c\",\"confidence\":1}}"), "jev-test-1");

            Assert.False(answers.Decided);
            Assert.Contains("not one of the options", answers.NotDecidedReason, StringComparison.Ordinal);
        }

        [Fact]
        public void A_reply_in_the_wrong_SHAPE_is_rejected()
        {
            var answers = DecisionAnswerReader.Read(Request(Choice("a", "b")),
                Json("{\"pick\":{\"type\":\"noul\",\"noul\":0.9}}"), "jev-test-1");

            Assert.False(answers.Decided);
            Assert.Contains("where 'choice' was asked", answers.NotDecidedReason, StringComparison.Ordinal);
        }

        [Fact]
        public void A_question_that_was_not_asked_makes_the_WHOLE_ask_not_decided()
        {
            var answers = DecisionAnswerReader.Read(Request(Choice("a", "b")),
                Json("{\"pick\":{\"type\":\"choice\",\"choice\":\"a\",\"confidence\":1},"
                   + "\"smuggled\":{\"type\":\"choice\",\"choice\":\"a\",\"confidence\":1}}"), "jev-test-1");

            Assert.False(answers.Decided);
            Assert.Empty(answers.Answers);
        }

        [Fact]
        public void A_missing_answer_makes_the_whole_ask_not_decided()
        {
            var request = Request(Choice("a", "b"),
                new DecisionQuestion("second", DecisionQuestionKind.Choice, "And?",
                    new[] { new DecisionOption("x", "the world is x"), new DecisionOption("y", "the world is y") }));

            var answers = DecisionAnswerReader.Read(request,
                Json("{\"pick\":{\"type\":\"choice\",\"choice\":\"a\",\"confidence\":1}}"), "jev-test-1");

            Assert.False(answers.Decided);
            Assert.Contains("did not answer 'second'", answers.NotDecidedReason, StringComparison.Ordinal);
        }

        [Fact]
        public void A_probability_outside_zero_to_one_is_rejected()
        {
            var answers = DecisionAnswerReader.Read(Request(Choice("a", "b")),
                Json("{\"pick\":{\"type\":\"choice\",\"choice\":\"a\",\"confidence\":0.9,\"probabilities\":{\"a\":1.4}}}"),
                "jev-test-1");

            Assert.False(answers.Decided);
            Assert.Contains("outside 0 to 1", answers.NotDecidedReason, StringComparison.Ordinal);
        }

        [Fact]
        public void A_noul_carries_no_confidence_field_so_the_confidence_is_derived()
        {
            // Measured from the pilot replies: a noul answer is one probability and nothing else.
            var request = new DecisionRequest("measure.folder_and_format", 1,
                new Dictionary<string, object> { { "measure_name", "Total Sales" } },
                new[]
                {
                    new DecisionQuestion("is_money", DecisionQuestionKind.Noul, "The measure is an amount of money.",
                        new[] { new DecisionOption("yes", "it is money"), new DecisionOption("no", "it is not money") }),
                });

            var sure = DecisionAnswerReader.Read(request, Json("{\"is_money\":{\"type\":\"noul\",\"noul\":0.02}}"), "jev-test-1");
            Assert.True(sure.Decided);
            Assert.False(sure.For("is_money").Noul);
            Assert.Equal(0.98, sure.For("is_money").Confidence, 3);

            var unsure = DecisionAnswerReader.Read(request, Json("{\"is_money\":{\"type\":\"noul\",\"noul\":0.5}}"), "jev-test-1");
            Assert.True(unsure.Decided);
            Assert.Equal(0.5, unsure.For("is_money").Confidence, 3);
            Assert.Null(unsure.AboveBar("is_money", 0.85));
        }

        [Fact]
        public void A_score_rung_may_come_back_as_a_position_but_never_off_the_ladder()
        {
            var request = new DecisionRequest("measure.folder_and_format", 1,
                new Dictionary<string, object> { { "measure_name", "Total Sales" } },
                new[]
                {
                    new DecisionQuestion("how_clear", DecisionQuestionKind.Score, "How clear is the name?",
                        new[]
                        {
                            new DecisionOption("unclear", "a reader could not say what it means"),
                            new DecisionOption("workable", "a reader could guess what it means"),
                            new DecisionOption("clear", "a reader knows what it means"),
                        }),
                });

            // Zero-based, as the vendor's own legend numbers the rungs ("0" is the first criterion sent). 26 Sep: the
            // reader used to count from one, which no reply ever did.
            var byPosition = DecisionAnswerReader.Read(request,
                Json("{\"how_clear\":{\"type\":\"score\",\"score\":2,\"confidence\":0.9}}"), "jev-test-1");
            Assert.True(byPosition.Decided);
            Assert.Equal("clear", byPosition.For("how_clear").Score);

            foreach (var off in new[] { "3", "7", "-1" })
            {
                var offLadder = DecisionAnswerReader.Read(request,
                    Json("{\"how_clear\":{\"type\":\"score\",\"score\":" + off + ",\"confidence\":0.9}}"), "jev-test-1");
                Assert.False(offLadder.Decided, off);
                Assert.Contains("outside the ladder", offLadder.NotDecidedReason, StringComparison.Ordinal);
            }
        }

        /// <summary>LIVE: a plan.rank request, exactly as the engine builds it, is answered by the vendor and read as
        /// decided (it had been refused with HTTP 422 in every live lane). Runs only with JEV_REPLAY_LIVE=1 and a
        /// development key in SEMANTICUS_JEV_KEY, which the test reads from its own process and never prints.</summary>
        [JevReplayFact]
        public async Task Live_the_vendor_answers_a_plan_rank_request()
        {
            var key = Environment.GetEnvironmentVariable(JevDecisionOracle.KeyVariable);
            Assert.False(string.IsNullOrWhiteSpace(key), JevDecisionOracle.KeyVariable + " is not set for the live run");
            var items = new[]
            {
                new Semanticus.Engine.ModelPlanItem { Id = "proposal/relationships", Title = "Sort out 2 relationships", Sub = "Sales, Returns", Section = "Structure", Why = new[] { "Two columns point at another table, but no relationship joins them." } },
                new Semanticus.Engine.ModelPlanItem { Id = "jev.relationship.table:Sales", Title = "Add 4 relationships in Sales", Section = "Structure", Why = new[] { "4 columns in Sales point at another table." } },
            };
            var point = DecisionRegistry.Require(DecisionRegistry.PlanRank);
            var request = new DecisionRequest(point.Name, point.SchemaVersion,
                Semanticus.Engine.PlanRankPoint.Snapshot("Contoso", 4, 12, 3, false, items), Semanticus.Engine.PlanRankPoint.Questions(items));
            using var oracle = new JevDecisionOracle(key, new DecisionBudget());

            var answers = await oracle.AskAsync(request, CancellationToken.None);

            Assert.True(answers.Decided, answers.NotDecidedReason);
            foreach (var item in items)
                Assert.Contains(answers.For(Semanticus.Engine.PlanRankPoint.QuestionFor(item.Id)).Score, DecisionRegistry.PlanValueRungs);
        }

        /// <summary>A score reply exactly as the vendor sent it for a plan.rank question on 26 Sep (the numbers are the
        /// live reply's): the score is the expected rung, fractional, counted from zero; the probabilities and the
        /// legend are keyed by position. It reads as the MOST PROBABLE rung (the later ruling, m11/plan-harm 28 Sep: the
        /// mean of "surely none" and "surely urgent" is a rung nobody chose; the 26 Sep reading took the nearest rung to
        /// the mean, "large" here), its probabilities as rung ids, and a legend that does not name the criteria that were
        /// sent, in order, is refused.</summary>
        [Fact]
        public void A_vendor_score_reply_reads_as_the_rung_it_lands_on()
        {
            var item = new Semanticus.Engine.ModelPlanItem { Id = "a1", Title = "Add 4 relationships in Sales", Section = "Structure" };
            var questions = Semanticus.Engine.PlanRankPoint.Questions(new[] { item });
            var point = DecisionRegistry.Require(DecisionRegistry.PlanRank);
            var request = new DecisionRequest(point.Name, point.SchemaVersion,
                Semanticus.Engine.PlanRankPoint.Snapshot("Contoso", 3, 9, 2, false, new[] { item }), questions);
            var criteria = questions[0].Options.Select(o => o.Criterion).ToArray();
            string Reply(string[] legend) =>
                "{\"value:a1\":{\"type\":\"score\",\"score\":3.45,\"confidence\":0.54,\"legend\":{"
                + string.Join(",", legend.Select((c, i) => "\"" + i + "\":" + JsonSerializer.Serialize(c)))
                + "},\"probabilities\":{\"0\":0.02,\"1\":0.04,\"2\":0.11,\"3\":0.14,\"4\":0.69}}}";

            var read = DecisionAnswerReader.Read(request, Json(Reply(criteria)), "jev-1.13.0");

            Assert.True(read.Decided, read.NotDecidedReason);
            var answer = read.For("value:a1");
            Assert.Equal("urgent", answer.Score);
            Assert.Equal(0.54, answer.Confidence, 3);
            Assert.Equal(0.69, answer.Probabilities["urgent"], 3);
            Assert.Equal(0.02, answer.Probabilities["none"], 3);

            var swapped = (string[])criteria.Clone();
            (swapped[0], swapped[4]) = (swapped[4], swapped[0]);
            var mislabelled = DecisionAnswerReader.Read(request, Json(Reply(swapped)), "jev-1.13.0");
            Assert.False(mislabelled.Decided);
            Assert.Contains("legend", mislabelled.NotDecidedReason, StringComparison.Ordinal);
        }

        // ---- numbers a reply can carry that a reader must not throw on (F-173) -----------------------

        /// <summary>Codex on PR 384: a syntactically valid JSON number that no double can hold, such as 1e400,
        /// made GetDouble throw where the contract promises "not decided". Every number the reader reads is
        /// covered here, because one unguarded read is the whole hole.</summary>
        [Fact]
        public void A_number_no_double_can_hold_is_not_decided_rather_than_an_exception()
        {
            var choiceRequest = Request(Choice("a", "b"));
            foreach (var body in new[]
            {
                "{\"pick\":{\"type\":\"choice\",\"choice\":\"a\",\"confidence\":1e400}}",
                "{\"pick\":{\"type\":\"choice\",\"choice\":\"a\",\"confidence\":0.9,\"probabilities\":{\"a\":1e400}}}",
            })
            {
                var answers = DecisionAnswerReader.Read(choiceRequest, Json(body), "jev-test-1");
                Assert.False(answers.Decided, body);
                Assert.Contains("not a number", answers.NotDecidedReason, StringComparison.Ordinal);
            }

            var noulRequest = new DecisionRequest("measure.folder_and_format", 1,
                new Dictionary<string, object> { { "measure_name", "Total Sales" } },
                new[]
                {
                    new DecisionQuestion("is_money", DecisionQuestionKind.Noul, "The measure is an amount of money.",
                        new[] { new DecisionOption("yes", "it is money"), new DecisionOption("no", "it is not money") }),
                });
            var noul = DecisionAnswerReader.Read(noulRequest,
                Json("{\"is_money\":{\"type\":\"noul\",\"noul\":-1e400}}"), "jev-test-1");
            Assert.False(noul.Decided);

            var scoreRequest = new DecisionRequest("measure.folder_and_format", 1,
                new Dictionary<string, object> { { "measure_name", "Total Sales" } },
                new[]
                {
                    new DecisionQuestion("how_clear", DecisionQuestionKind.Score, "How clear is the name?",
                        new[]
                        {
                            new DecisionOption("unclear", "a reader could not say what it means"),
                            new DecisionOption("clear", "a reader knows what it means"),
                        }),
                });
            var score = DecisionAnswerReader.Read(scoreRequest,
                Json("{\"how_clear\":{\"type\":\"score\",\"score\":1e400,\"confidence\":0.9}}"), "jev-test-1");
            Assert.False(score.Decided);
        }

        // ---- a model folder that collides with the reserved no-folder option (F-172) ------------------

        /// <summary>Codex on PR 384: a display folder literally named "none" used to produce the reserved
        /// no-folder option id a second time, so DecisionQuestion threw and the whole operation failed before
        /// it could return its normal answer. Model-derived ids now carry a prefix no reserved id has.</summary>
        [Fact]
        public void A_model_folder_named_none_cannot_collide_with_the_reserved_no_folder_option()
        {
            var questions = MeasureDefaultsPoint.Questions(new[] { "none", "Volume" }, wantsFolder: true, wantsFormat: false);
            var folder = Assert.Single(questions);

            Assert.Equal(3, folder.Options.Count);
            Assert.Equal(3, folder.Options.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.True(folder.HasOption(DecisionRegistry.NoFolder));
            Assert.NotEqual(DecisionRegistry.NoFolder, MeasureDefaultsPoint.FolderOptionId("none"));
        }

        [Fact]
        public void The_folder_named_none_reads_back_as_that_folder_and_the_reserved_option_as_no_folder()
        {
            var questions = MeasureDefaultsPoint.Questions(new[] { "none", "Volume" }, wantsFolder: true, wantsFormat: false);
            var request = new DecisionRequest(DecisionRegistry.MeasureFolderAndFormat, 1,
                new Dictionary<string, object>(StringComparer.Ordinal) { { "measure_name", "Total Sales" } }, questions);

            var pickedTheFolder = DecisionAnswerReader.Read(request,
                Json("{\"display_folder\":{\"type\":\"choice\",\"choice\":"
                     + JsonSerializer.Serialize(MeasureDefaultsPoint.FolderOptionId("none")) + ",\"confidence\":0.93}}"),
                "jev-test-1");
            double confidence;
            Assert.True(pickedTheFolder.Decided, pickedTheFolder.NotDecidedReason);
            Assert.Equal("none", MeasureDefaultsPoint.ProposedFolder(pickedTheFolder, 0.85, out confidence));
            Assert.Equal(0.93, confidence, 3);

            var pickedNoFolder = DecisionAnswerReader.Read(request,
                Json("{\"display_folder\":{\"type\":\"choice\",\"choice\":"
                     + JsonSerializer.Serialize(DecisionRegistry.NoFolder) + ",\"confidence\":0.93}}"),
                "jev-test-1");
            Assert.True(pickedNoFolder.Decided, pickedNoFolder.NotDecidedReason);
            Assert.Null(MeasureDefaultsPoint.ProposedFolder(pickedNoFolder, 0.85, out confidence));
            Assert.Equal(0.93, confidence, 3);
        }

        // ---- the oracles -----------------------------------------------------------------------------

        [Fact]
        public async Task The_null_oracle_is_unavailable_and_decides_nothing()
        {
            var answers = await NullDecisionOracle.Instance.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);
            Assert.False(NullDecisionOracle.Instance.Available);
            Assert.False(answers.Decided);
            Assert.Empty(answers.Answers);
        }

        [Fact]
        public async Task The_recorded_oracle_answers_only_the_EXACT_evidence_it_holds()
        {
            var request = DecisionFixture.SampleRequest();
            var oracle = new RecordedDecisionOracle(
                DecisionFixture.ProofFixture(request.Digest, "Volume", 0.93, "currency", 0.97), "jev-test-1");

            var hit = await oracle.AskAsync(request, CancellationToken.None);
            Assert.True(hit.Decided);
            // The wire carries the option ID, which for a model-derived folder is the encoded one.
            Assert.Equal(MeasureDefaultsPoint.FolderOptionId("Volume"), hit.For("display_folder").Choice);
            Assert.Equal(900, hit.InputTokens);

            // Same questions, one field of the snapshot different: a different digest, so a miss, not a guess.
            var point = DecisionRegistry.Require(DecisionRegistry.MeasureFolderAndFormat);
            var moved = new DecisionRequest(request.PointName, request.SchemaVersion,
                DecisionSnapshot.Build(point, new[]
                {
                    new KeyValuePair<string, object>("measure_name", "Total Cost"),
                }),
                request.Questions);

            var miss = await oracle.AskAsync(moved, CancellationToken.None);
            Assert.False(miss.Decided);
            Assert.Contains("no recorded answer", miss.NotDecidedReason, StringComparison.Ordinal);
            Assert.Equal(2, oracle.AskCount);
        }

        /// <summary>The measure point sends facts about the formula, not the formula (F-229), so the facts are
        /// the evidence the digest is built from. Two measures whose facts differ are two pieces of evidence and
        /// never share a digest or a cached answer. (Two formulas that differ only in a literal value have the
        /// same facts and do share one, which is right: the service was shown the same thing.)</summary>
        [Fact]
        public void Two_measures_whose_formula_facts_differ_never_share_a_digest()
        {
            var point = DecisionRegistry.Require(DecisionRegistry.MeasureFolderAndFormat);

            Func<IReadOnlyDictionary<string, object>, DecisionRequest> build = facts => new DecisionRequest(point.Name, point.SchemaVersion,
                DecisionSnapshot.Build(point, new[]
                {
                    new KeyValuePair<string, object>("measure_name", "Total Sales"),
                    new KeyValuePair<string, object>("measure_formula_facts", facts),
                }),
                new[] { Choice("a", "b") });

            Assert.NotEqual(build(DecisionFixture.SampleFormulaFacts("SUM")).Digest,
                            build(DecisionFixture.SampleFormulaFacts("SUMX")).Digest);
            // The same facts built twice, as two separate requests, must digest the same (the digest is stable).
            var first = build(DecisionFixture.SampleFormulaFacts("SUM"));
            var second = build(DecisionFixture.SampleFormulaFacts("SUM"));
            Assert.Equal(first.Digest, second.Digest);
        }
    }
}
