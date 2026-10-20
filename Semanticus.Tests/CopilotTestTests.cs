using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The Copilot test (Kane, 15 Oct 2026): the model interview as a test loop. Pins the kind-of-miss classifier, the grade of a round by number in one call,
    /// the held-back fifth that is never handed back, the one approval per round, the cause groups (rules, and Jev when it is on) and the happy path: a bank of 30,
    /// a round, misses, a change, a second round with a better held-back score. The student is a fake that answers from a table; the engine has no AI.
    /// The model has no data, so a table of answers stands in for the live DAX engine (LocalEngine.CopilotDaxForTest): a real run needs a live connection.
    /// </summary>
    [Collection("restore-root")]
    public sealed class CopilotTestTests : GateFixtureBase
    {
        public CopilotTestTests(RestoreRootFixture fixture) : base(fixture) { }

        // ---- the fake DAX engine: [Total] is 100 + the region number, [Margin] is 5, any other measure does not exist ---------------------------------------

        private static Task<ResultSet> FakeDax(string dax)
        {
            if (dax.Contains("BROKEN", StringComparison.Ordinal)) return Task.FromResult(ResultSet.FromError("The syntax is incorrect."));
            var m = Regex.Match(dax, "CALCULATE\\(\\s*\\[(?<m>[^\\]]+)\\]\\s*,\\s*Sales\\[Region\\]\\s*=\\s*\"R(?<k>\\d+)\"\\s*\\)(?<tail>.*)\\)\\s*$", RegexOptions.Singleline);
            if (!m.Success) return Task.FromResult(ResultSet.FromError("Cannot read this query."));
            var k = int.Parse(m.Groups["k"].Value);
            object value = m.Groups["m"].Value switch
            {
                "Total" => 100.0 + k + (m.Groups["tail"].Value.Contains("rounded") ? 0.5 : 0),
                "Margin" => 5.0,
                _ => null,
            };
            if (value == null) return Task.FromResult(ResultSet.FromError("Measure [" + m.Groups["m"].Value + "] was not found."));
            return Task.FromResult(new ResultSet { Rows = new[] { new object[] { value } }, RowCount = 1 });
        }

        private static string Ref(int i) => "EVALUATE ROW(\"v\", CALCULATE([Total], Sales[Region] = \"R" + i + "\"))";

        /// <summary>30 questions. Seven kinds in turn, three of them (7, 14, 21) the model cannot answer. Every fifth is held back: 5, 10, 15, 20, 25, 30.</summary>
        private static CopilotQuestionIn[] Bank30()
        {
            var kinds = new[] { "totals", "filters", "time", "ranking", "words", "names" };
            var items = new List<CopilotQuestionIn>();
            for (var i = 1; i <= 30; i++)
            {
                var cannot = i % 7 == 0;
                items.Add(new CopilotQuestionIn
                {
                    Question = cannot ? $"How many visitors came to region {i}?" : $"What were total sales in region R{i}?",
                    Kind = cannot ? "cannot" : kinds[i % kinds.Length],
                    ReferenceDax = cannot ? null : Ref(i),
                });
            }
            return items.ToArray();
        }

        private static readonly int[] Held = { 5, 10, 15, 20, 25, 30 };

        private enum B { Right, WrongField, WrongFilter, WrongNumber, DaxFailed, SaidNo, ShouldSayNo }

        /// <summary>What the fake student does with question i.</summary>
        private static CopilotAnswerIn Student(int i, B b) => b switch
        {
            B.Right => i % 7 == 0 ? new CopilotAnswerIn { N = i, CannotAnswer = true } : new CopilotAnswerIn { N = i, Dax = Ref(i) },
            B.WrongField => new CopilotAnswerIn { N = i, Dax = $"EVALUATE ROW(\"v\", CALCULATE([Margin], Sales[Region] = \"R{i}\"))" },
            B.WrongFilter => new CopilotAnswerIn { N = i, Dax = $"EVALUATE ROW(\"v\", CALCULATE([Total], Sales[Region] = \"R{i + 1}\"))" },
            B.WrongNumber => new CopilotAnswerIn { N = i, Dax = $"EVALUATE ROW(\"v\", CALCULATE([Total], Sales[Region] = \"R{i}\") /* rounded */)" },
            B.DaxFailed => new CopilotAnswerIn { N = i, Dax = $"EVALUATE ROW(\"v\", CALCULATE([Total Sales], Sales[Region] = \"R{i}\"))" },
            B.SaidNo => new CopilotAnswerIn { N = i, CannotAnswer = true },
            _ => new CopilotAnswerIn { N = i, Dax = Ref(i) },
        };

        private static CopilotAnswerIn[] Round(IDictionary<int, B> behaviour) =>
            Enumerable.Range(1, 30).Select(i => Student(i, behaviour.TryGetValue(i, out var b) ? b : B.Right)).ToArray();

        private static readonly Dictionary<int, B> Round1 = new Dictionary<int, B>
        {
            [1] = B.WrongField, [2] = B.WrongField, [3] = B.WrongFilter, [4] = B.WrongNumber, [6] = B.DaxFailed, [8] = B.SaidNo, [14] = B.ShouldSayNo,
            [5] = B.WrongFilter, [20] = B.WrongField,   // held back
        };

        private async Task<LocalEngine> OpenBankAsync(string tag)
        {
            var (engine, _, _) = await OpenAsync(tag);
            engine.CopilotDaxForTest = FakeDax;
            var added = await engine.AddInterviewQuestionsAsync(Bank30(), "agent");
            Assert.Equal(30, added.Added);
            return engine;
        }

        // ---- the classifier -------------------------------------------------------------------------------------------------------------------------------------

        private static InterviewQuestion Value(string dax, string expected) =>
            new InterviewQuestion { Id = "iq-x", Question = "q", Tier = "value", Kind = "filters", Query = dax, ExpectedValue = expected };

        private static ResultSet One(object v) => new ResultSet { Rows = new[] { new object[] { v } }, RowCount = 1 };

        private const string RefDax = "EVALUATE ROW(\"v\", CALCULATE([Total Sales], Product[Category] = \"Bikes\", Store[Country] = \"Germany\"))";

        [Fact]
        public void A_right_number_is_right()
        {
            var g = CopilotGrader.Grade(Value(RefDax, "2914360"), RefDax, false, One(2914360.0));
            Assert.Equal(CopilotMiss.Right, g.Miss);
        }

        [Fact]
        public void The_number_within_the_house_tolerance_is_right_and_blank_is_never_zero()
        {
            Assert.Equal(CopilotMiss.Right, CopilotGrader.Grade(Value(RefDax, "100"), RefDax, false, One(100.0000000001)).Miss);
            Assert.Equal(CopilotMiss.WrongNumber, CopilotGrader.Grade(Value(RefDax, "BLANK"), RefDax, false, One(0.0)).Miss);
            Assert.Equal(CopilotMiss.WrongNumber, CopilotGrader.Grade(Value(RefDax, "0"), RefDax, false, One(null)).Miss);
        }

        [Fact]
        public void A_different_measure_is_a_wrong_field()
        {
            var student = "EVALUATE ROW(\"v\", CALCULATE([Units], Product[Category] = \"Bikes\", Store[Country] = \"Germany\"))";
            Assert.Equal(CopilotMiss.WrongField, CopilotGrader.Grade(Value(RefDax, "5"), student, false, One(7.0)).Miss);
        }

        [Fact]
        public void A_different_split_column_is_a_wrong_field_and_a_bare_measure_matches_its_qualified_form()
        {
            var rank = "EVALUATE TOPN(5, VALUES(Product[Product]), [Total Sales])";
            var q = new InterviewQuestion { Id = "iq-y", Question = "q", Tier = "value", Kind = "ranking", Query = rank, ExpectedMatrix = new[] { new[] { "A", "9" } } };
            var other = new ResultSet { Rows = new[] { new object[] { "B", 9.0 } }, RowCount = 1 };
            Assert.Equal(CopilotMiss.WrongField, CopilotGrader.Grade(q, "EVALUATE TOPN(5, VALUES(Customer[Customer]), [Total Sales])", false, other).Miss);
            Assert.True(CopilotDaxShape.SameRefs(CopilotDaxShape.Of("Sales[Total Sales]").Fields, CopilotDaxShape.Of("[Total Sales]").Fields));
            Assert.False(CopilotDaxShape.SameRefs(CopilotDaxShape.Of("Product[Name]").Fields, CopilotDaxShape.Of("Customer[Name]").Fields));
        }

        [Fact]
        public void The_same_fields_with_another_value_or_another_date_function_is_a_wrong_filter()
        {
            var student = "EVALUATE ROW(\"v\", CALCULATE([Total Sales], Product[Category] = \"Bikes\", Store[Country] = \"France\"))";
            Assert.Equal(CopilotMiss.WrongFilter, CopilotGrader.Grade(Value(RefDax, "5"), student, false, One(7.0)).Miss);
            var ytd = "EVALUATE ROW(\"v\", CALCULATE([Total Sales], DATESYTD('Date'[Date], \"6-30\")))";
            var cal = "EVALUATE ROW(\"v\", CALCULATE([Total Sales], DATESYTD('Date'[Date])))";
            Assert.Equal(CopilotMiss.WrongFilter, CopilotGrader.Grade(Value(ytd, "5"), cal, false, One(7.0)).Miss);
        }

        [Fact]
        public void The_same_fields_and_filters_with_another_number_is_a_wrong_number_and_a_label_is_not_a_filter()
        {
            var student = "EVALUATE ROW(\"Sales\", CALCULATE([Total Sales], Product[Category] = \"Bikes\", Store[Country] = \"Germany\"))";   // another result label
            Assert.Equal(CopilotMiss.WrongNumber, CopilotGrader.Grade(Value(RefDax, "5"), student, false, One(7.0)).Miss);
        }

        [Fact]
        public void A_dax_that_does_not_run_is_dax_failed_and_a_sign_in_or_policy_stop_is_could_not_check()
        {
            Assert.Equal(CopilotMiss.DaxFailed, CopilotGrader.Grade(Value(RefDax, "5"), RefDax, false, ResultSet.FromError("The syntax is incorrect.")).Miss);
            Assert.Equal(CopilotMiss.CouldNotCheck, CopilotGrader.Grade(Value(RefDax, "5"), RefDax, false, null).Miss);
            Assert.Equal(CopilotMiss.CouldNotCheck, CopilotGrader.Grade(Value(RefDax, "5"), RefDax, false, new ResultSet { Error = "x", AuthFailed = true }).Miss);
            Assert.Equal(CopilotMiss.CouldNotCheck, CopilotGrader.Grade(Value(RefDax, "5"), RefDax, false, new ResultSet { Error = "x", PolicyRefused = true }).Miss);
        }

        [Fact]
        public void Saying_no_is_right_for_a_question_the_model_cannot_answer_and_a_miss_for_one_it_can()
        {
            var cannot = new InterviewQuestion { Id = "iq-c", Question = "q", Tier = "refusal", Kind = "cannot", ExpectRefusal = true };
            Assert.Equal(CopilotMiss.Right, CopilotGrader.Grade(cannot, null, true, null).Miss);
            Assert.Equal(CopilotMiss.ShouldSayNo, CopilotGrader.Grade(cannot, RefDax, false, One(1.0)).Miss);
            Assert.Equal(CopilotMiss.SaidNo, CopilotGrader.Grade(Value(RefDax, "5"), null, true, null).Miss);
            Assert.Equal(CopilotMiss.SaidNo, CopilotGrader.Grade(Value(RefDax, "5"), "  ", false, null).Miss);
        }

        [Fact]
        public void Another_shape_of_answer_is_a_miss_and_only_a_cut_off_result_is_unchecked()
        {
            var many = new ResultSet { Rows = new[] { new object[] { 1.0 }, new object[] { 2.0 } }, RowCount = 2 };
            Assert.Equal(CopilotMiss.WrongNumber, CopilotGrader.Grade(Value(RefDax, "5"), RefDax, false, many).Miss);
            Assert.Equal(CopilotMiss.WrongNumber, CopilotGrader.Grade(Value(RefDax, "5"), RefDax, false, new ResultSet()).Miss);
            var matrix = new InterviewQuestion { Id = "iq-m", Question = "q", Tier = "value", Kind = "ranking", Query = "EVALUATE TOPN(2, VALUES(P[x]), [m])", ExpectedMatrix = new[] { new[] { "A", "1" }, new[] { "B", "2" } } };
            Assert.Equal(CopilotMiss.CouldNotCheck, CopilotGrader.Grade(matrix, "EVALUATE TOPN(2, VALUES(P[x]), [m])", false, new ResultSet { Rows = new[] { new object[] { "A", 1.0 } }, RowCount = 1, Truncated = true }).Miss);
        }

        // Hero 24 D4: the reference answer is one word (the top store's name) and the student's TOPN returns that name with its number in one row.
        private const string TopStoreRef = "EVALUATE ROW(\"v\", MAXX(TOPN(1, ADDCOLUMNS(VALUES(Store[StoreName]), \"s\", CALCULATE(SUM(Sales[NetSalesAmount]), 'Date'[Year] = 2025)), [s], DESC), Store[StoreName]))";
        private const string TopStoreStudent = "EVALUATE TOPN(1, CALCULATETABLE(SUMMARIZECOLUMNS(Store[StoreName], \"Net Sales\", [Net Sales]), 'Date'[Year] = 2025), [Net Sales], DESC)";

        [Fact]
        public void A_ranking_row_that_holds_the_right_name_and_its_number_is_right()
        {
            var q = Value(TopStoreRef, "Northstar Online");
            q.Kind = "ranking";
            var row = new ResultSet { Rows = new[] { new object[] { "Northstar Online", 6109062.02 } }, RowCount = 1 };
            Assert.Equal(CopilotMiss.Right, CopilotGrader.Grade(q, TopStoreStudent, false, row).Miss);
            // a row that holds another name is still a miss, and it is not graded by the number
            var other = new ResultSet { Rows = new[] { new object[] { "North", 6109062.02 } }, RowCount = 1 };
            Assert.NotEqual(CopilotMiss.Right, CopilotGrader.Grade(q, TopStoreStudent, false, other).Miss);
        }

        [Fact]
        public void A_table_answer_with_the_right_names_and_numbers_in_another_column_order_is_right()
        {
            var q = new InterviewQuestion { Id = "iq-r", Question = "q", Tier = "value", Kind = "ranking", Query = "EVALUATE TOPN(2, VALUES(Product[Category]), [Total Sales])",
                ExpectedMatrix = new[] { new[] { "Bikes", "10" }, new[] { "Helmets", "4" } } };
            var flipped = new ResultSet { Rows = new[] { new object[] { 4.0, "Helmets" }, new object[] { 10.0, "Bikes" } }, RowCount = 2 };
            Assert.Equal(CopilotMiss.Right, CopilotGrader.Grade(q, "EVALUATE TOPN(2, SUMMARIZECOLUMNS(Product[Category], \"v\", [Total Sales]), [v])", false, flipped).Miss);
        }

        [Fact]
        public void A_miss_group_names_model_objects_never_dax_words_or_a_local_alias()
        {
            var q = new InterviewQuestion { Id = "iq-g", Question = "Which store sold the most?", Tier = "value", Kind = "ranking", Query = TopStoreRef };
            var facts = CopilotCauseRules.Facts(1, q, "EVALUATE ROW(\"v\", MAXX(CALCULATETABLE(ADDCOLUMNS(VALUES(Store[StoreName]), \"s\", [Units]), 'Date'[Year] = 2025), Store[StoreName]))", CopilotMiss.WrongField);
            var (cause, fix) = CopilotCauseRules.Rule(facts);
            var group = CopilotCauseRules.Group(new[] { (facts, cause, fix, false) }).Single();
            Assert.DoesNotContain(group.Objects, o => o.Equals("s", StringComparison.OrdinalIgnoreCase) || o.Contains("calculate", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(group.Objects, o => o.Contains("Units"));
        }

        // Verifier v-fix-hero24 (D4 attack): matching rows by their cells must never turn a wrong answer Right.
        [Fact]
        public void Verifier_a_wrong_number_beside_the_right_name_is_still_wrong()
        {
            // the trusted answer holds the name AND its number: the right name with another number is wrong
            var q = new InterviewQuestion { Id = "iq-v1", Question = "Which store sold the most in 2025, and how much?", Tier = "value", Kind = "ranking", Query = TopStoreRef,
                ExpectedMatrix = new[] { new[] { "Northstar Online", "6109062.02" } } };
            var wrong = new ResultSet { Rows = new[] { new object[] { "Northstar Online", 291894.0 } }, RowCount = 1 };
            Assert.NotEqual(CopilotMiss.Right, CopilotGrader.Grade(q, TopStoreStudent, false, wrong).Miss);
            // the trusted answer is the number: the right name with another number is wrong
            var qn = Value(TopStoreRef, "6109062.02");
            qn.Kind = "ranking";
            Assert.NotEqual(CopilotMiss.Right, CopilotGrader.Grade(qn, TopStoreStudent, false, wrong).Miss);
        }

        [Fact]
        public void Verifier_each_row_may_not_choose_its_own_column_order()
        {
            // month number and a count: (1, 2) and (2, 3) are trusted; the student says month 2 had 1 and month 3 had 2. One column order
            // for the whole table would show it wrong; letting each row pick its own order reads it as the trusted rows.
            var q = new InterviewQuestion { Id = "iq-v2", Question = "New stores per month in early 2025", Tier = "value", Kind = "trend", Query = "EVALUATE SUMMARIZECOLUMNS('Date'[MonthNo], \"n\", [New Stores])",
                ExpectedMatrix = new[] { new[] { "1", "2" }, new[] { "2", "3" } } };
            var wrong = new ResultSet { Rows = new[] { new object[] { 2L, 1L }, new object[] { 3L, 2L } }, RowCount = 2 };
            Assert.NotEqual(CopilotMiss.Right, CopilotGrader.Grade(q, "EVALUATE SUMMARIZECOLUMNS('Date'[MonthNo], \"n\", [New Stores])", false, wrong).Miss);
        }

        [Fact]
        public void A_bare_expression_is_made_a_query()
        {
            Assert.Equal("EVALUATE ROW(\"v\", [Total])", CopilotGrader.AsQuery("[Total]"));
            Assert.Equal("EVALUATE ROW(\"v\", 1)", CopilotGrader.AsQuery("EVALUATE ROW(\"v\", 1)"));
        }

        // ---- the bank: proven on add, every fifth held back ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task The_reference_answer_is_proven_by_running_it_and_a_question_that_does_not_run_does_not_enter_the_bank()
        {
            var (engine, _, _) = await OpenAsync("cpt-add");
            using (engine)
            {
                engine.CopilotDaxForTest = FakeDax;
                var items = new[]
                {
                    new CopilotQuestionIn { Question = "Total for R1?", Kind = "totals", ReferenceDax = Ref(1) },
                    new CopilotQuestionIn { Question = "Broken one?", Kind = "totals", ReferenceDax = "EVALUATE ROW(\"v\", BROKEN)" },
                    new CopilotQuestionIn { Question = "Total for R1?", Kind = "totals", ReferenceDax = Ref(1) },   // asked twice
                    new CopilotQuestionIn { Question = "No kind", Kind = "weather", ReferenceDax = Ref(2) },
                    new CopilotQuestionIn { Question = "No DAX", Kind = "filters" },
                    new CopilotQuestionIn { Question = "Visitors?", Kind = "cannot" },
                };
                var r = await engine.AddInterviewQuestionsAsync(items, "agent");
                Assert.Equal(2, r.Added);
                Assert.Equal(4, r.Rejected);
                Assert.Equal("101", r.Items[0].Answer);
                Assert.Contains("did not run", r.Items[1].Error);
                Assert.Contains("already in the bank", r.Items[2].Error);
                Assert.Contains("not a kind", r.Items[3].Error);
                Assert.Contains("referenceDax", r.Items[4].Error);
                Assert.Equal("Say no", r.Items[5].Answer);
                var stored = (await engine.ListInterviewQuestionsAsync("project")).Questions;
                Assert.Equal(2, stored.Length);
                Assert.Equal("101", stored.First(q => q.Kind == "totals").ExpectedValue);
                Assert.Equal("refusal", stored.First(q => q.Kind == "cannot").Tier);
            }
        }

        [Fact]
        public async Task Without_a_live_connection_nothing_is_proven_and_nothing_is_added()
        {
            var (engine, _, _) = await OpenAsync("cpt-offline");
            using (engine)
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.AddInterviewQuestionsAsync(Bank30(), "agent"));
                Assert.Contains("live connection", ex.Message);
                Assert.Empty((await engine.ListInterviewQuestionsAsync("project")).Questions);
            }
        }

        [Fact]
        public async Task Every_fifth_question_of_the_bank_is_held_back_and_adding_more_continues_the_count()
        {
            using var engine = await OpenBankAsync("cpt-held");
            var q = (await engine.ListInterviewQuestionsAsync("project")).Questions;
            Assert.Equal(Held, q.Select((x, i) => (x, n: i + 1)).Where(t => t.x.HeldBack).Select(t => t.n).ToArray());
            await engine.AddInterviewQuestionsAsync(new[] { new CopilotQuestionIn { Question = "Extra 31", Kind = "totals", ReferenceDax = Ref(31) }, new CopilotQuestionIn { Question = "Extra 32", Kind = "totals", ReferenceDax = Ref(32) },
                new CopilotQuestionIn { Question = "Extra 33", Kind = "totals", ReferenceDax = Ref(33) }, new CopilotQuestionIn { Question = "Extra 34", Kind = "totals", ReferenceDax = Ref(34) },
                new CopilotQuestionIn { Question = "Extra 35", Kind = "totals", ReferenceDax = Ref(35) } }, "agent");
            var after = (await engine.ListInterviewQuestionsAsync("project")).Questions;
            Assert.True(after[34].HeldBack);   // the 35th
            Assert.Equal(7, after.Count(x => x.HeldBack));
        }

        [Fact]
        public async Task The_assistant_is_not_handed_a_held_back_question_by_the_list_or_by_a_single_run()
        {
            using var engine = await OpenBankAsync("cpt-hide");
            var heldId = (await engine.ListInterviewQuestionsAsync("project")).Questions.First(x => x.HeldBack).Id;
            var viaTool = await McpTools.ListInterviewQuestions(engine);
            var hidden = viaTool.Questions.First(x => x.Id == heldId);
            Assert.True(hidden.HeldBack);
            Assert.DoesNotContain("region", hidden.Question, StringComparison.OrdinalIgnoreCase);
            Assert.Null(hidden.Query);
            Assert.Null(hidden.ExpectedValue);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunInterviewAsync(heldId, null, false, Ref(5), "agent"));
            Assert.Contains("held back", ex.Message);
            // The person's own door still reads and runs it.
            var human = (await engine.ListInterviewQuestionsAsync("project")).Questions.First(x => x.Id == heldId);
            Assert.NotNull(human.Query);
        }

        [Fact]
        public async Task The_bank_is_not_replayed_at_the_deploy_gate()
        {
            using var engine = await OpenBankAsync("cpt-replay");
            Assert.Null(await engine.InterviewGateAdvisoryAsync());   // a bank of hundreds is a test loop of its own, not a deploy-time replay
        }

        // ---- the round: graded by number in one call -----------------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_round_is_graded_by_number_and_every_kind_of_miss_is_named()
        {
            using var engine = await OpenBankAsync("cpt-round");
            var asked = await engine.GetStudentQuestionsAsync();
            Assert.Equal(1, asked.Round);
            Assert.Equal(30, asked.Total);
            Assert.All(asked.Questions, a => Assert.False(string.IsNullOrWhiteSpace(a.Question)));
            var r = await engine.RunInterviewBatchAsync(null, asked.BankKey, "Haiku 5.5", Round(Round1), null, "agent");
            Assert.True(r.Complete);
            Assert.Equal(30, r.Answered);
            var s = r.Summary;
            Assert.Equal(1, s.Number);
            Assert.Equal("Haiku 5.5", s.Student);
            // 24 practice questions: misses are 1, 2, 3, 4, 6, 8, 14; held back (5, 20) are not among them.
            Assert.Equal(24, s.Practice.Total);
            Assert.Equal(17, s.Practice.Right);
            Assert.Equal(6, s.HeldBack.Total);
            Assert.Equal(4, s.HeldBack.Right);
            Assert.Equal(2, s.MissCounts[CopilotMiss.WrongField]);
            Assert.Equal(1, s.MissCounts[CopilotMiss.WrongFilter]);
            Assert.Equal(1, s.MissCounts[CopilotMiss.WrongNumber]);
            Assert.Equal(1, s.MissCounts[CopilotMiss.DaxFailed]);
            Assert.Equal(1, s.MissCounts[CopilotMiss.SaidNo]);
            Assert.Equal(1, s.MissCounts[CopilotMiss.ShouldSayNo]);
            Assert.Equal(new[] { 1, 2, 3, 4, 6, 8, 14 }, s.Misses.Select(m => m.N).ToArray());
            var one = s.Misses.First(m => m.N == 1);
            Assert.Contains("[Margin]", one.StudentDax);
            Assert.Contains("[Total]", one.ReferenceDax);
            Assert.Equal("5", one.StudentAnswer);
            Assert.Equal("101", one.ReferenceAnswer);
            Assert.Equal("Say no", s.Misses.First(m => m.N == 14).ReferenceAnswer);
            // Hero 26 N4: a kind's score leaves the 6 held-back questions out, or the assistant could tell which held-back one was missed from a kind's drop.
            Assert.Equal(24, s.ByKind.Sum(k => k.Total));
        }

        [Fact]
        public async Task The_held_back_questions_score_but_their_misses_are_never_returned_or_written()
        {
            using var engine = await OpenBankAsync("cpt-held-hidden");
            var asked = await engine.GetStudentQuestionsAsync();
            var r = await engine.RunInterviewBatchAsync(null, asked.BankKey, null, Round(Round1), null, "agent");
            // Neither the result of the call nor the read of the round names a held-back number.
            Assert.DoesNotContain(r.Summary.Misses, m => Held.Contains(m.N));
            var read = await engine.InterviewRoundsAsync("1");
            Assert.DoesNotContain(read.Rounds[0].Misses, m => Held.Contains(m.N));
            Assert.DoesNotContain(read.Rounds[0].Groups.SelectMany(g => g.Numbers), n => Held.Contains(n));
            Assert.Equal(4, read.Rounds[0].HeldBack.Right);
            Assert.Equal(6, read.HeldBack);
            // The rounds file keeps a held-back answer's kind of miss and nothing else: no DAX, no value, no words.
            var file = Directory.GetFiles(Root, "rounds.jsonl", SearchOption.AllDirectories).Single();
            var lines = File.ReadAllLines(file).Select(l => JsonDocument.Parse(l).RootElement).Where(e => e.GetProperty("op").GetString() == "answer").ToList();
            var held = lines.Where(e => e.TryGetProperty("held", out var h) && h.GetBoolean()).ToList();
            Assert.Equal(6, held.Count);
            Assert.All(held, e =>
            {
                Assert.False(e.TryGetProperty("studentDax", out var d) && d.ValueKind != JsonValueKind.Null);
                Assert.False(e.TryGetProperty("question", out var q) && q.ValueKind != JsonValueKind.Null);
                Assert.False(e.TryGetProperty("referenceAnswer", out var a) && a.ValueKind != JsonValueKind.Null);
            });
        }

        [Fact]
        public async Task A_bank_that_changed_since_the_numbers_were_given_is_refused()
        {
            using var engine = await OpenBankAsync("cpt-key");
            var asked = await engine.GetStudentQuestionsAsync();
            await engine.AddInterviewQuestionsAsync(new[] { new CopilotQuestionIn { Question = "One more", Kind = "totals", ReferenceDax = Ref(40) } }, "agent");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunInterviewBatchAsync(null, asked.BankKey, null, Round(Round1), null, "agent"));
            Assert.Contains("bank changed", ex.Message);
        }

        [Fact]
        public async Task A_number_outside_the_bank_or_asked_twice_is_listed_and_not_graded()
        {
            using var engine = await OpenBankAsync("cpt-skip");
            var r = await engine.RunInterviewBatchAsync(null, null, null, new[] { Student(1, B.Right), Student(1, B.WrongField), new CopilotAnswerIn { N = 99, Dax = Ref(1) } }, null, "agent");
            Assert.Equal(1, r.Graded);
            Assert.Equal(2, r.Skipped.Length);
            Assert.False(r.Complete);
            Assert.Equal(1, r.Summary.Answered);
        }

        [Fact]
        public async Task A_round_can_be_sent_in_parts_and_is_complete_when_the_last_answer_is_in()
        {
            using var engine = await OpenBankAsync("cpt-parts");
            var all = Round(Round1);
            var first = await engine.RunInterviewBatchAsync(null, null, null, all.Take(20).ToArray(), null, "agent");
            Assert.False(first.Complete);
            Assert.Empty(first.Summary.Groups);
            var second = await engine.RunInterviewBatchAsync(first.RoundId, null, null, all.Skip(20).ToArray(), null, "agent");
            Assert.True(second.Complete);
            Assert.Equal(first.RoundId, second.RoundId);
            Assert.Equal(1, (await engine.InterviewRoundsAsync(null)).Rounds.Length);
            Assert.NotEmpty(second.Summary.Groups);
        }

        [Fact]
        public async Task A_round_not_finished_is_continued_and_only_the_numbers_not_answered_are_asked_again()
        {
            using var engine = await OpenBankAsync("cpt-continue");
            var all = Round(Round1);
            var first = await engine.RunInterviewBatchAsync(null, null, null, all.Take(20).ToArray(), null, "agent");
            var asked = await engine.GetStudentQuestionsAsync();
            Assert.Equal(first.RoundId, asked.OpenRoundId);
            Assert.Equal(1, asked.Round);
            Assert.Equal(Enumerable.Range(21, 10).ToArray(), asked.Questions.Select(q => q.N).ToArray());
            Assert.Contains("not finished", asked.Note);
            var done = await engine.RunInterviewBatchAsync(asked.OpenRoundId, asked.BankKey, null, all.Skip(20).ToArray(), null, "agent");
            Assert.True(done.Complete);
            Assert.Null((await engine.GetStudentQuestionsAsync()).OpenRoundId);
            Assert.Equal(2, (await engine.GetStudentQuestionsAsync()).Round);
        }

        [Fact]
        public async Task An_answer_that_cannot_be_run_for_want_of_a_connection_is_could_not_check_and_never_a_miss()
        {
            using var engine = await OpenBankAsync("cpt-uncheck");
            engine.CopilotDaxForTest = _ => Task.FromResult(new ResultSet { Error = "x", AuthFailed = true });
            var r = await engine.RunInterviewBatchAsync(null, null, null, new[] { Student(1, B.Right) }, null, "agent");
            Assert.Equal(1, r.Summary.All.CouldNotCheck);
            Assert.Equal(0, r.Summary.All.Total);
            Assert.Null(r.Summary.All.Share);
        }

        // ---- the groups: rules, and Jev when it is on and sure ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task The_practice_misses_are_grouped_by_cause_with_the_rules_when_Jev_is_off()
        {
            using var engine = await OpenBankAsync("cpt-groups");
            var r = await engine.RunInterviewBatchAsync(null, null, null, Round(Round1), null, "agent");
            var groups = r.Summary.Groups;
            Assert.NotEmpty(groups);
            Assert.All(groups, g => Assert.Equal("rules", g.By));
            Assert.Equal(7, groups.Sum(g => g.Count));   // every practice miss is in exactly one group
            Assert.DoesNotContain(groups.SelectMany(g => g.Numbers), n => Held.Contains(n));
            Assert.Contains(groups, g => g.Cause == CopilotCause.NoData && g.Numbers.Contains(14));
            Assert.Contains(groups, g => g.Cause == CopilotCause.DaxSlip && g.Numbers.Contains(6));
            Assert.Contains(groups, g => g.Cause == CopilotCause.UnknownWord && g.Numbers.Contains(8));
            Assert.All(groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Label)));
            Assert.Contains("Margin", groups.First(g => g.Numbers.Contains(1)).Label);
            Assert.StartsWith("not asked", engine.CopilotJevNote);
        }

        [Fact]
        public void The_cause_rules_read_the_names_the_two_answers_differ_on()
        {
            var q = new InterviewQuestion { Id = "iq-r", Question = "What was revenue in 2025?", Tier = "value", Kind = "names", Query = "EVALUATE ROW(\"v\", CALCULATE([Total Sales], 'Date'[Fiscal Year] = \"FY2025\"))" };
            var wrongField = CopilotCauseRules.Facts(1, q, "EVALUATE ROW(\"v\", CALCULATE([Net Price], 'Date'[Year] = 2025))", CopilotMiss.WrongField);
            Assert.Equal(new[] { "Net Price" }, wrongField.StudentOnly);
            Assert.Equal(new[] { "Total Sales" }, wrongField.ReferenceOnly);
            Assert.Equal((CopilotCause.UnknownWord, CopilotCause.FixOtherName), CopilotCauseRules.Rule(wrongField));
            var year = CopilotCauseRules.Facts(2, q, "EVALUATE ROW(\"v\", CALCULATE([Total Sales], 'Date'[Year] = 2025))", CopilotMiss.WrongFilter);
            Assert.Equal((CopilotCause.CalendarVsFiscal, CopilotCause.FixInstruction), CopilotCauseRules.Rule(year));
            var sibling = CopilotCauseRules.Facts(3, new InterviewQuestion { Id = "iq-s", Question = "What is our margin?", Tier = "value", Kind = "words", Query = "EVALUATE ROW(\"v\", [Margin])" }, "EVALUATE ROW(\"v\", [Margin %])", CopilotMiss.WrongField);
            Assert.Equal((CopilotCause.SiblingMeasure, CopilotCause.FixDescription), CopilotCauseRules.Rule(sibling));
        }

        private sealed class MissOracle : IDecisionOracle
        {
            private readonly Func<int, (string Choice, double Confidence)> _answer;
            public readonly List<DecisionRequest> Seen = new List<DecisionRequest>();
            public MissOracle(Func<int, (string, double)> answer) { _answer = answer; }
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (!string.Equals(request.PointName, DecisionRegistry.CopilotMisses, StringComparison.Ordinal)) return Task.FromResult(DecisionAnswers.NotDecided(request, "not the Copilot test"));
                lock (Seen) Seen.Add(request);
                var answers = new Dictionary<string, object>();
                foreach (var q in request.Questions)
                {
                    var (choice, confidence) = _answer(int.Parse(q.Name.Substring(CopilotMissPoint.Prefix.Length)));
                    answers[q.Name] = new Dictionary<string, object> { ["type"] = "choice", ["choice"] = choice, ["confidence"] = confidence };
                }
                return Task.FromResult(DecisionAnswerReader.Read(request, JsonSerializer.SerializeToElement(answers), "jev-test-1"));
            }
        }

        private async Task UseJevAsync(LocalEngine engine, MissOracle oracle)
        {
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            engine.UseDecisionsForTest(new DecisionService(oracle, new DecisionBudget()));
        }

        [Fact]
        public async Task The_point_ships_off_so_Jev_is_not_asked_unless_it_is_switched_on()
        {
            Assert.False(LocalEngine.CopilotJevDecides);
            using var engine = await OpenBankAsync("cpt-jev-off");
            var oracle = new MissOracle(_ => ("dax_slip:none", 0.99));
            await UseJevAsync(engine, oracle);
            await engine.RunInterviewBatchAsync(null, null, null, Round(Round1), null, "agent");
            Assert.Empty(oracle.Seen);
        }

        [Fact]
        public async Task Jev_groups_the_misses_in_one_call_naming_the_objects_and_never_sends_a_value_or_a_held_back_question()
        {
            using var engine = await OpenBankAsync("cpt-jev-on");
            engine.CopilotJevDecidesForTest = true;
            var oracle = new MissOracle(n => n == 14 ? ("no_data:instruction", 0.95) : ("unknown_word:other_name", 0.9));
            await UseJevAsync(engine, oracle);
            var r = await engine.RunInterviewBatchAsync(null, null, null, Round(Round1), null, "agent");
            var request = Assert.Single(oracle.Seen);   // one call for the round, not one per miss
            Assert.Equal(7, request.Questions.Count);
            Assert.Equal(new[] { "filter_differences", "miss_kinds", "question_words", "right_fields", "student_fields" }, request.Snapshot.Keys.OrderBy(k => k).ToArray());
            Assert.Contains("Margin", request.Questions.First(q => q.Name == "miss:1").Instructions);   // the object is named in the question
            var json = JsonSerializer.Serialize(request.Snapshot) + string.Join("", request.Questions.Select(q => q.Instructions));
            foreach (var held in Held) Assert.DoesNotContain($"region {held}?", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("101", json);   // no value
            Assert.All(request.Questions, q => Assert.Equal(DecisionRegistry.CopilotCausePairs.ToArray(), q.Options.Select(o => o.Id).ToArray()));
            var groups = r.Summary.Groups;
            Assert.Contains(groups, g => g.By == "jev" && g.Cause == CopilotCause.NoData && g.Numbers.SequenceEqual(new[] { 14 }));
            Assert.Contains(groups, g => g.Cause == CopilotCause.UnknownWord && g.Count == 6);
        }

        [Fact]
        public async Task A_Jev_answer_below_the_bar_leaves_the_rule_and_a_slow_service_leaves_every_rule()
        {
            using var engine = await OpenBankAsync("cpt-jev-low");
            engine.CopilotJevDecidesForTest = true;
            await UseJevAsync(engine, new MissOracle(_ => ("other:none", 0.2)));
            var r = await engine.RunInterviewBatchAsync(null, null, null, Round(Round1), null, "agent");
            Assert.All(r.Summary.Groups, g => Assert.Equal("rules", g.By));
            Assert.Contains(r.Summary.Groups, g => g.Cause == CopilotCause.NoData);
        }

        // ---- the happy path: a bank of 30, a round, misses, a change, a second round with a better held-back score -------------------------------------

        [Fact]
        public async Task The_loop_works_end_to_end_a_bank_a_round_the_misses_a_change_and_a_second_round_with_a_better_held_back_score()
        {
            using var engine = await OpenBankAsync("cpt-happy");
            var asked1 = await engine.GetStudentQuestionsAsync();
            var one = await engine.RunInterviewBatchAsync(null, asked1.BankKey, "Haiku 5.5", Round(Round1), null, "agent");

            // The assistant reads the groups and the practice misses, and proposes a change for misses 1, 2 and 3.
            Assert.Equal(7, one.Summary.Misses.Length);
            var change = new CopilotRoundChange
            {
                Title = "Say that sales means Total, in money", Where = "AI instructions", On = "Model", Before = "(nothing)",
                After = "When someone says sales, use Total. It is money.", ForMisses = new[] { 1, 2, 3 },
            };

            // Round 2: the change helps misses 1 and 2 and the held-back 5 and 20, leaves 3, and breaks 9.
            var round2 = new Dictionary<int, B> { [3] = B.WrongFilter, [4] = B.WrongNumber, [6] = B.DaxFailed, [8] = B.SaidNo, [14] = B.ShouldSayNo, [9] = B.WrongFilter };
            var asked2 = await engine.GetStudentQuestionsAsync();
            Assert.Equal(2, asked2.Round);
            var two = await engine.RunInterviewBatchAsync(null, asked2.BankKey, "Haiku 5.5", Round(round2), new[] { change }, "agent");

            Assert.Equal(2, two.Round);
            Assert.True(two.Complete);
            var s2 = two.Summary;
            Assert.True(s2.HeldBack.Share > one.Summary.HeldBack.Share, "the held-back score must go up");
            Assert.Equal(1.0, s2.HeldBack.Share);
            Assert.Equal(6, s2.Practice.Total - s2.Practice.Right);
            Assert.Equal(18, s2.Practice.Right);
            Assert.Equal(1, s2.NewMisses);
            var c = Assert.Single(s2.Changes);
            Assert.Equal(2, c.Fixed);   // 1 and 2 are right now, 3 is not
            Assert.Equal(new[] { 1, 2, 3 }, c.ForMisses);

            // The page reads both rounds back with the score over the rounds.
            var read = await engine.InterviewRoundsAsync(null);
            Assert.Equal(new[] { 1, 2 }, read.Rounds.Select(r => r.Number).ToArray());
            Assert.True(read.Rounds[1].Practice.Share > read.Rounds[0].Practice.Share);
            Assert.Equal(30, read.BankSize);
            Assert.Equal(4, read.BankByKind.First(k => k.Kind == "cannot").Count);
        }

        // hero16 N1's lesson: a test that calls LocalEngine directly never crosses the Newtonsoft pipe. This sends the Copilot test through a real RpcServer and
        // RemoteEngine (the MCP door's path), with the arrays and the changes that the wire has to carry.
        [Fact]
        public async Task The_Copilot_test_works_through_the_real_engine_pipe()
        {
            var (engine, sessions, _) = await OpenAsync("cpt-pipe");
            using (engine)
            {
                engine.CopilotDaxForTest = FakeDax;
                var pipeName = TestPipe.Mint("semanticus-cpt-");
                using var server = new RpcServer(sessions, engine, pipeName);
                using var stop = new CancellationTokenSource();
                var serving = server.RunAsync(stop.Token);
                RemoteEngine remote = null;
                try
                {
                    remote = await RemoteEngine.ConnectAsync(pipeName, timeoutMs: 5000);
                    var added = await remote.AddInterviewQuestionsAsync(Bank30(), "agent");
                    Assert.Equal(30, added.Added);
                    var asked = await remote.GetStudentQuestionsAsync();
                    Assert.Equal(30, asked.Questions.Length);
                    var change = new CopilotRoundChange { Title = "A change", Where = "Description", On = "Total", Before = "", After = "Sum.", ForMisses = new[] { 1, 2 } };
                    var r = await remote.RunInterviewBatchAsync(null, asked.BankKey, "Haiku 5.5", Round(Round1), new[] { change }, "agent");
                    Assert.True(r.Complete);
                    Assert.Equal(17, r.Summary.Practice.Right);
                    Assert.Equal(4, r.Summary.HeldBack.Right);
                    Assert.Equal(7, r.Summary.Misses.Length);
                    Assert.Equal("A change", Assert.Single(r.Summary.Changes).Title);
                    var read = await remote.InterviewRoundsAsync("1");
                    Assert.Equal(7, read.Rounds[0].Misses.Length);
                    Assert.Contains("[Margin]", read.Rounds[0].Misses[0].StudentDax);
                    var view = await remote.GetCopilotViewAsync();
                    Assert.Contains("Table Sales", view.Text);
                }
                finally { remote?.Dispose(); stop.Cancel(); try { await serving; } catch { } }
            }
        }

        // ---- the gate: one approval for a round, never one per question or per batch --------------------------------------------------------------------

        [Fact]
        public async Task A_round_is_asked_about_once_when_it_is_made_and_adding_answers_to_it_is_not_asked_again()
        {
            var (engine, _, _) = await OpenAsync("cpt-gate");
            using (engine)
            {
                engine.CopilotDaxForTest = FakeDax;
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);

                // The bank: one question to the person for the whole list of 30.
                var asked = await RefusedAsync(() => gate.AddInterviewQuestionsAsync(Bank30(), "agent"));
                Assert.Contains("approval", asked.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Single(await engine.ListPendingApprovalsAsync());
                await ApproveAsync(engine);
                var added = await gate.AddInterviewQuestionsAsync(Bank30(), "agent");
                Assert.Equal(30, added.Added);
                Assert.Empty(await engine.ListPendingApprovalsAsync());

                // The round: the call that makes it asks once.
                var all = Round(Round1);
                await RefusedAsync(() => gate.RunInterviewBatchAsync(null, null, null, all.Take(20).ToArray(), null, "agent"));
                Assert.Single(await engine.ListPendingApprovalsAsync());
                await ApproveAsync(engine);
                var first = await gate.RunInterviewBatchAsync(null, null, null, all.Take(20).ToArray(), null, "agent");
                Assert.False(first.Complete);

                // The rest of the round, in two more calls: nothing is asked.
                var second = await gate.RunInterviewBatchAsync(first.RoundId, null, null, all.Skip(20).Take(5).ToArray(), null, "agent");
                Assert.False(second.Complete);
                var third = await gate.RunInterviewBatchAsync(first.RoundId, null, null, all.Skip(25).ToArray(), null, "agent");
                Assert.True(third.Complete);
                Assert.Empty(await engine.ListPendingApprovalsAsync());

                // Reads never ask.
                await gate.GetCopilotViewAsync();
                await gate.GetStudentQuestionsAsync();
                await gate.InterviewRoundsAsync(null);
                Assert.Empty(await engine.ListPendingApprovalsAsync());
            }
        }

        // ---- what Copilot sees ----------------------------------------------------------------------------------------------------------------------------

        [Fact]
        public async Task The_Copilot_view_holds_what_Copilot_is_given_and_leaves_out_hidden_objects_and_every_value()
        {
            var (engine, sessions, _) = await OpenAsync("cpt-view");
            using (engine)
            {
                await engine.SetDescriptionAsync("measure:Sales/Total", "Total sales, in money.", "human");
                await engine.SetSynonymsAsync("measure:Sales/Total", new[] { "revenue", "turnover" }, null, "human");
                await engine.SetAiInstructionsAsync("When someone says sales, use Total.", null, "human");
                await engine.SetColumnMetadataAsync("column:Sales/Code", true, null, null, null, "human");
                var view = await engine.GetCopilotViewAsync();
                Assert.Contains("Table Sales", view.Text);
                Assert.Contains("Measure [Total]", view.Text);
                Assert.Contains("Total sales, in money.", view.Text);
                Assert.True(view.Text.Contains("also called: revenue, turnover"), view.Text);
                Assert.True(view.Text.Contains("When someone says sales, use Total."), view.Text);
                Assert.Contains("Sales[Region]", view.Text);
                Assert.DoesNotContain("Sales[Code]", view.Text);   // hidden
                Assert.DoesNotContain("SUM(Sales[Amount])", view.Text);   // no formulas
                Assert.Equal(1, view.MeasuresDescribed);
                Assert.Equal(2, view.OtherNames);
                Assert.True(view.LeftOut >= 1);
                Assert.Contains(view.Sees, x => x.What == "Your data and the right answers" && !x.On);
                Assert.Contains(view.Sees, x => x.What == "AI instructions" && x.On && x.Count == "6 words");
            }
        }

        // ---- verifier (v-copilot-test, 15 Oct 2026): grading by number, the kind of miss, the held-back fifth, the gate under each preset -------------

        [Fact]
        public void V_the_same_number_from_other_dax_is_right_for_a_number_and_for_a_table()
        {
            var other = "EVALUATE ROW(\"x\", SUMX(FILTER(Sales, RELATED(Product[Category]) = \"Bikes\" && RELATED(Store[Country]) = \"Germany\"), Sales[Amount]))";
            Assert.Equal(CopilotMiss.Right, CopilotGrader.Grade(Value(RefDax, "2914360.25"), other, false, One(2914360.25m)).Miss);   // a decimal against the stored text
            var top = new InterviewQuestion { Id = "iq-t", Question = "q", Tier = "value", Kind = "ranking", Query = "EVALUATE TOPN(2, VALUES(Product[Category]), [Total Sales])",
                ExpectedMatrix = new[] { new[] { "Bikes", "10" }, new[] { "Helmets", "4" } } };
            var rows = new ResultSet { Rows = new[] { new object[] { "Helmets", 4.0 }, new object[] { "Bikes", 10.0 } }, RowCount = 2 };
            Assert.Equal(CopilotMiss.Right, CopilotGrader.Grade(top, "EVALUATE TOPN(2, SUMMARIZECOLUMNS(Product[Category], \"s\", [Total Sales]), [s])", false, rows).Miss);
        }

        [Fact]
        public void V_a_rounded_answer_is_a_wrong_number_not_a_wrong_filter()
        {
            // Same measure, same filters: the only difference is ROUND(..., 0). The number is wrong; nothing was filtered differently.
            var student = "EVALUATE ROW(\"v\", ROUND(CALCULATE([Total Sales], Product[Category] = \"Bikes\", Store[Country] = \"Germany\"), 0))";
            Assert.Equal(CopilotMiss.WrongNumber, CopilotGrader.Grade(Value(RefDax, "2914360.4"), student, false, One(2914360.0)).Miss);
        }

        [Fact]
        public void V_zero_where_the_answer_is_blank_is_a_wrong_number_not_a_wrong_filter()
        {
            // Same measure, same filters, "+ 0" turns the blank into 0. Blank is never zero, and the miss is the number, not a filter.
            var student = "EVALUATE ROW(\"v\", CALCULATE([Total Sales], Product[Category] = \"Bikes\", Store[Country] = \"Germany\") + 0)";
            Assert.Equal(CopilotMiss.WrongNumber, CopilotGrader.Grade(Value(RefDax, "BLANK"), student, false, One(0.0)).Miss);
        }

        [Fact]
        public void V_re_check_a_literal_in_a_filter_position_still_names_a_wrong_filter_and_arithmetic_in_a_table_query_does_not()
        {
            // The fix for ROUND and + 0 must not cost the filter literals: a year, an IN list, a DATEADD step and a TOPN count are filter values.
            Assert.Equal(CopilotMiss.WrongFilter, CopilotGrader.Grade(Value(RefDax, "5"), RefDax.Replace("\"Germany\"", "\"France\""), false, One(7.0)).Miss);
            var inRef = "EVALUATE ROW(\"v\", CALCULATE([Total Sales], 'Date'[Year] IN {2023, 2024}))";
            Assert.Equal(CopilotMiss.WrongFilter, CopilotGrader.Grade(Value(inRef, "5"), inRef.Replace("2023", "2022"), false, One(7.0)).Miss);
            var stepRef = "EVALUATE ROW(\"v\", CALCULATE([Total Sales], DATEADD('Date'[Date], -1, YEAR)))";
            Assert.Equal(CopilotMiss.WrongFilter, CopilotGrader.Grade(Value(stepRef, "5"), stepRef.Replace("-1", "-2"), false, One(7.0)).Miss);
            var top = new InterviewQuestion { Id = "iq-t", Question = "q", Tier = "value", Kind = "ranking", Query = "EVALUATE TOPN(2, VALUES(Product[Category]), [Total Sales])",
                ExpectedMatrix = new[] { new[] { "Bikes", "10" }, new[] { "Helmets", "4" } } };
            var three = new ResultSet { Rows = new[] { new object[] { "Bikes", 10.0 }, new object[] { "Helmets", 4.0 }, new object[] { "Locks", 1.0 } }, RowCount = 3 };
            Assert.Equal(CopilotMiss.WrongFilter, CopilotGrader.Grade(top, "EVALUATE TOPN(3, VALUES(Product[Category]), [Total Sales])", false, three).Miss);
            var byYear = "EVALUATE SUMMARIZECOLUMNS('Date'[Year], \"v\", [Total Sales])";
            var q = new InterviewQuestion { Id = "iq-y", Question = "q", Tier = "value", Kind = "totals", Query = byYear, ExpectedMatrix = new[] { new[] { "2024", "10.4" }, new[] { "2025", "4.4" } } };
            var rounded = new ResultSet { Rows = new[] { new object[] { 2024L, 10.0 }, new object[] { 2025L, 4.0 } }, RowCount = 2 };
            Assert.Equal(CopilotMiss.WrongNumber, CopilotGrader.Grade(q, "EVALUATE SUMMARIZECOLUMNS('Date'[Year], \"v\", ROUND([Total Sales], 0))", false, rounded).Miss);
        }

        [Fact]
        public void V_a_table_for_a_number_and_a_number_for_a_table_are_misses()
        {
            var table = new ResultSet { Rows = new[] { new object[] { "Bikes", 2914360.0 }, new object[] { "Helmets", 1.0 } }, RowCount = 2 };
            Assert.NotEqual(CopilotMiss.Right, CopilotGrader.Grade(Value(RefDax, "2914360"), "EVALUATE SUMMARIZECOLUMNS(Product[Category], \"s\", [Total Sales])", false, table).Miss);
            var top = new InterviewQuestion { Id = "iq-t2", Question = "q", Tier = "value", Kind = "ranking", Query = "EVALUATE TOPN(2, VALUES(Product[Category]), [Total Sales])",
                ExpectedMatrix = new[] { new[] { "Bikes", "10" }, new[] { "Helmets", "4" } } };
            var g = CopilotGrader.Grade(top, "EVALUATE ROW(\"v\", [Total Sales])", false, One(14.0));
            Assert.NotEqual(CopilotMiss.Right, g.Miss);
            Assert.NotEqual(CopilotMiss.CouldNotCheck, g.Miss);
        }

        [Fact]
        public void V_an_answer_to_a_should_say_no_question_is_a_miss_even_when_it_comes_back_blank()
        {
            var cannot = new InterviewQuestion { Id = "iq-c2", Question = "How many visitors?", Tier = "refusal", Kind = "cannot", ExpectRefusal = true };
            Assert.Equal(CopilotMiss.ShouldSayNo, CopilotGrader.Grade(cannot, "EVALUATE ROW(\"v\", BLANK())", false, One(null)).Miss);
            Assert.Equal(CopilotMiss.ShouldSayNo, CopilotGrader.Grade(cannot, "EVALUATE ROW(\"v\", BLANK())", false, ResultSet.FromError("x")).Miss);
        }

        [Fact]
        public async Task V_a_held_back_answer_sent_on_its_own_does_not_give_away_its_grade()
        {
            // run_interview_batch takes a round in parts. Sent alone, held-back question 5 is the only answer in the round, so any score in the result
            // (held back, all, or by kind) is that one question's grade, one held-back question at a time.
            using var engine = await OpenBankAsync("v-held-part");
            var asked = await engine.GetStudentQuestionsAsync();
            var r = await engine.RunInterviewBatchAsync(null, asked.BankKey, null, new[] { Student(5, B.WrongFilter) }, null, "agent");
            Assert.False(r.Complete);
            Assert.True(r.Summary.HeldBack.Total == 0, $"the held-back score of a round with only question 5 in it is {r.Summary.HeldBack.Right}/{r.Summary.HeldBack.Total}: question 5's grade");
            Assert.True(r.Summary.ByKind.Sum(k => k.Total) == 0, "the score by kind of a round with only question 5 in it is question 5's grade");
        }

        [Fact]
        public async Task V_a_number_already_graded_in_a_round_is_not_graded_again()
        {
            // A finished round takes more answers by its id without asking (the round exists). If a number already graded is graded again, the
            // assistant can try other DAX for held-back question 5 and read its grade off the held-back score, and the round's recorded score moves.
            using var engine = await OpenBankAsync("v-held-regrade");
            var asked = await engine.GetStudentQuestionsAsync();
            var one = await engine.RunInterviewBatchAsync(null, asked.BankKey, null, Round(Round1), null, "agent");
            Assert.True(one.Complete);
            Assert.Equal(4, one.Summary.HeldBack.Right);   // 5 and 20 were wrong
            var again = await engine.RunInterviewBatchAsync(one.RoundId, asked.BankKey, null, new[] { Student(5, B.Right) }, null, "agent");
            Assert.True(4 == again.Summary.HeldBack.Right, $"re-sending held-back question 5 moved the held-back score of round 1 from 4 to {again.Summary.HeldBack.Right}");
        }

        [Theory]
        [InlineData("locked")]
        [InlineData("cautious")]
        [InlineData("standard")]
        [InlineData("open")]
        public async Task V_the_loop_asks_at_most_once_for_the_bank_and_once_per_round_under_each_preset(string preset)
        {
            var (engine, _, _) = await OpenAsync("v-gate-" + preset);
            using (engine)
            {
                engine.CopilotDaxForTest = FakeDax;
                AgentPolicyStore.SetPreset(preset, "human");
                var gate = Gate(engine);
                var asks = 0;
                async Task<T> Through<T>(Func<Task<T>> call)
                {
                    try { return await call(); }
                    catch (InvalidOperationException)
                    {
                        if (!(await engine.ListPendingApprovalsAsync()).Any()) throw;
                        asks++;
                        await ApproveAsync(engine);
                        return await call();
                    }
                }

                await Through(() => gate.AddInterviewQuestionsAsync(Bank30(), "agent"));
                Assert.True(asks <= 1, preset + ": the bank asked " + asks + " times");
                foreach (var behaviour in new[] { Round1, new Dictionary<int, B> { [3] = B.WrongFilter } })
                {
                    var before = asks;
                    var all = Round(behaviour);
                    var q = await Through(() => gate.GetStudentQuestionsAsync());
                    var first = await Through(() => gate.RunInterviewBatchAsync(null, q.BankKey, null, all.Take(10).ToArray(), null, "agent"));
                    await Through(() => gate.RunInterviewBatchAsync(first.RoundId, q.BankKey, null, all.Skip(10).Take(10).ToArray(), null, "agent"));
                    var last = await Through(() => gate.RunInterviewBatchAsync(first.RoundId, q.BankKey, null, all.Skip(20).ToArray(), null, "agent"));
                    Assert.True(last.Complete);
                    Assert.True(asks - before <= 1, preset + ": round " + last.Round + " asked " + (asks - before) + " times");
                }
                if (preset == "open") Assert.Equal(0, asks);
            }
        }
    }
}
