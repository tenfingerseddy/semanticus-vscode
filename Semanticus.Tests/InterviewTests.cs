using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The Model Interview (docs/product-innovation-brainstorm.md §1), offline. Pins:
    /// (1) the STORE kernel — delta replay (add → record-run → delete ordering), corrupt-line skip-and-count,
    ///     per-tier fail-loud add validation, and the free/Pro gate (add = Pro with the free alternative named;
    ///     list/run/delete free);
    /// (2) the SCORING kernel — every tier's outcome mapping including the HONESTY DOWNGRADES (offline / erroring
    ///     query / truncated / zero rows / missing oracle are all Unverified — never a fabricated pass and never a
    ///     fabricated "confidently wrong");
    /// (3) the failure→fix mapping — the author's fixRuleId wins, the embedded {tier,outcome} data table is the
    ///     fallback, and every rule id in the table exists in the real readiness ruleset;
    /// (4) the interview_replay workflow verify executor — an empty pack FAILS instructively (never a quiet pass)
    ///     and offline SKIPS honestly (never blocks a hard gate).
    /// All offline (no live connection, no inference); both scopes write only into disposable temp dirs.
    /// </summary>
    public sealed class InterviewTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // Redirect USERPROFILE so the global-scope fallback (LocateInterviewQuestion searches project → global)
        // lands in a temp home, never the real one; caller restores it.
        private static string RedirectHome(string ws)
        {
            var orig = Environment.GetEnvironmentVariable("USERPROFILE");
            var home = Path.Combine(ws, "home");
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("USERPROFILE", home);
            return orig;
        }

        // A workspace engine with a model OPEN and anchored to a file, so it has a DURABLE identity. #157-R2 made add
        // fail-closed: an unsaved+unconnected model has no durable identity (a per-process session counter would
        // collide across launches), so persistence is refused. Anchoring to ws/StoreModel.bim gives a stable disk
        // identity; DirFor(ws/StoreModel.bim) == ws/.semanticus, so the store stays at ws/.semanticus/interview
        // exactly where these tests write their corrupt/blocking fixtures.
        private static async Task<(LocalEngine engine, string ws, string origHome)> MakeWs(bool pro = true)
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-interview-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus"));
            var origHome = RedirectHome(ws);
            var sessions = new SessionManager();
            var e = new LocalEngine(sessions, new Fake(pro), ws);
            await e.CreateModelAsync("StoreModel", 1604);
            sessions.Current.SourcePath = Path.Combine(ws, "StoreModel.bim");
            return (e, ws, origHome);
        }

        private static void Cleanup(string dir, string origHome)
        {
            Environment.SetEnvironmentVariable("USERPROFILE", origHome);
            try { Directory.Delete(dir, true); } catch { }
        }

        private static Task<InterviewQuestion> AddRefusal(LocalEngine e, string fixRuleId = null) =>
            e.AddInterviewQuestionAsync("What was our churn in 2024?", "refusal", null, null, null, null, null,
                null, null, true, fixRuleId, "user", "project", "human");

        private static Task<InterviewQuestion> AddValue(LocalEngine e) =>
            e.AddInterviewQuestionAsync("What were total sales in 2024?", "value",
                "EVALUATE ROW(\"v\", CALCULATE([Total Sales], 'Date'[Year]=2024))", null, null, null, null,
                "1234567.89", null, false, null, "claude", "project", "human");

        // ---- store round-trip (the kernel invariants) ----

        [Fact]
        public async Task Add_list_delete_round_trip_with_all_fields()
        {
            var (e, ws, home) = await MakeWs();
            try
            {
                var q = await e.AddInterviewQuestionAsync("Which region sold the most?", "paraphrase",
                    null, "[Total Sales]", "SUM(Sales[SalesAmount])", new[] { "'Date'[Year]" }, new[] { "KEEPFILTERS('Date'[Year]=2024)" },
                    null, null, false, "SYN-SCHEMA", "verified-answer", "project", "human");
                Assert.StartsWith("iq-", q.Id);
                Assert.Equal("paraphrase", q.Tier);
                Assert.Equal("[Total Sales]", q.ScalarExpr);
                Assert.Equal("SUM(Sales[SalesAmount])", q.ParaphraseExpr);
                Assert.Equal(new[] { "'Date'[Year]" }, q.GroupBy);
                Assert.Equal("SYN-SCHEMA", q.FixRuleId);
                Assert.Equal("verified-answer", q.SeedSource);   // the pre-seed hook (verified-answers extraction / ProBench pack) rides this
                Assert.Equal("project", q.Scope);
                Assert.Null(q.LastRun);

                var listed = await e.ListInterviewQuestionsAsync("project");
                Assert.Single(listed.Questions, x => x.Id == q.Id);

                var del = await e.DeleteInterviewQuestionAsync(q.Id, "human");
                Assert.True(del.Changed);
                var after = await e.ListInterviewQuestionsAsync("project");
                Assert.DoesNotContain(after.Questions, x => x.Id == q.Id);   // tombstoned

                // Deleting again teaches instead of corrupting (the id is no longer live).
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.DeleteInterviewQuestionAsync(q.Id, "human"));
                Assert.Contains("list_interview_questions", ex.Message);
            }
            finally { Cleanup(ws, home); }
        }

        [Fact]
        public async Task Corrupt_lines_are_skipped_and_counted_never_bricking_the_store()
        {
            var (e, ws, home) = await MakeWs();
            try
            {
                var q = await AddRefusal(e);
                var file = Path.Combine(ws, ".semanticus", "interview", "questions.jsonl");
                Assert.True(File.Exists(file));
                File.AppendAllText(file, "{not json at all\n{\"op\":\"who-knows\",\"id\":\"iq-zz\"}\n");

                var listed = await e.ListInterviewQuestionsAsync("project");
                Assert.Equal(2, listed.SkippedCorruptLines);
                Assert.Single(listed.Questions, x => x.Id == q.Id);          // the good line still replays
                Assert.Contains("not bricked", listed.Note);
            }
            finally { Cleanup(ws, home); }
        }

        [Fact]
        public async Task Add_validation_is_fail_loud_per_tier()
        {
            var (e, ws, home) = await MakeWs();
            try
            {
                // value tier without a query → teaches the EVALUATE shape (GAP C: scalar exprs are not interchangeable).
                var noQuery = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    e.AddInterviewQuestionAsync("Total sales?", "value", null, null, null, null, null, "100", null, false, null, null, "project", "human"));
                Assert.Contains("EVALUATE", noQuery.Message);

                // value tier without an oracle → names expectedValue/expectedMatrix (no oracle = only ever "couldn't check").
                var noOracle = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    e.AddInterviewQuestionAsync("Total sales?", "value", "EVALUATE ROW(\"v\", [X])", null, null, null, null, null, null, false, null, null, "project", "human"));
                Assert.Contains("expectedValue", noOracle.Message);

                // paraphrase tier needs BOTH phrasings.
                var onePhrasing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    e.AddInterviewQuestionAsync("Sales?", "paraphrase", null, "[Total Sales]", null, null, null, null, null, false, null, null, "project", "human"));
                Assert.Contains("paraphraseExpr", onePhrasing.Message);

                // a malformed expectedMatrixJson teaches the exact shape.
                var badMatrix = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    e.AddInterviewQuestionAsync("By year?", "value", "EVALUATE VALUES('Date'[Year])", null, null, null, null, null, "{\"nope\":1}", false, null, null, "project", "human"));
                Assert.Contains("array of rows", badMatrix.Message);
            }
            finally { Cleanup(ws, home); }
        }

        // ---- fail-loud persistence (the Append contract): a "saved" question must have hit disk ----

        [Fact]
        public async Task Add_throws_teaching_the_line_cap_when_the_record_is_too_large_and_persists_nothing()
        {
            var (e, ws, home) = await MakeWs();
            try
            {
                // A giant expectedMatrix (the reachable case Codex named) pushes the JSONL line over the 64KB
                // per-line cap: the add must THROW (never return a "saved" question that never hit disk),
                // naming the cap and what to trim.
                var bigCell = new string('9', 70_000);
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    e.AddInterviewQuestionAsync("By year?", "value", "EVALUATE VALUES('Date'[Year])", null, null,
                        null, null, null, $"[[\"2024\",\"{bigCell}\"]]", false, null, null, "project", "human"));
                Assert.Contains("NOT saved", ex.Message);
                Assert.Contains("64KB", ex.Message);
                Assert.Contains("expectedValue", ex.Message);    // the teaching half: what to trim
                Assert.Empty((await e.ListInterviewQuestionsAsync("project")).Questions);
            }
            finally { Cleanup(ws, home); }
        }

        [Fact]
        public async Task Add_throws_naming_the_write_target_on_an_io_failure_and_persists_nothing()
        {
            var (e, ws, home) = await MakeWs();
            try
            {
                // Block the store's directory with a plain FILE of the same name → CreateDirectory fails with a
                // real IO error. The add must surface it (with the path it tried), not claim success.
                File.WriteAllText(Path.Combine(ws, ".semanticus", "interview"), "not a directory");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AddValue(e));
                Assert.Contains("NOT saved", ex.Message);
                Assert.Contains("could not write to", ex.Message);
                Assert.Contains("questions.jsonl", ex.Message);
                Assert.Empty((await e.ListInterviewQuestionsAsync("project")).Questions);
            }
            finally { Cleanup(ws, home); }
        }

        // ---- free/Pro gate (Kane locked 2026-07-07: persistence = Pro; one-off run + list + delete = free) ----

        [Fact]
        public async Task Add_is_pro_gated_with_the_free_alternative_named_run_and_list_stay_free()
        {
            var (e, ws, home) = await MakeWs(pro: false);
            try
            {
                var ex = await Assert.ThrowsAsync<EntitlementException>(() => AddValue(e));
                Assert.Contains("run_interview", ex.Message);   // the free alternative is named, not a dead end

                // The free paths all work: list (empty + guidance), one-off inline run, and the run is NOT persisted.
                var listed = await e.ListInterviewQuestionsAsync(null);
                Assert.Empty(listed.Questions);
                Assert.Contains("run_interview", listed.Note);

                var r = await e.RunInterviewAsync(null,
                    "{\"question\":\"What was churn in 2024?\",\"tier\":\"refusal\"}", abstained: true, attemptDax: null, origin: "agent");
                Assert.Equal("Refused", r.Outcome);
                Assert.False(r.Recorded);                        // inline one-offs persist nothing
                Assert.Empty((await e.ListInterviewQuestionsAsync(null)).Questions);
            }
            finally { Cleanup(ws, home); }
        }

        // ---- scoring kernel: tier 1 (value oracle) ----

        private static InterviewQuestion ValueQ(string expected = "100", string[][] matrix = null) => new InterviewQuestion
        { Question = "q", Tier = "value", Query = "EVALUATE ROW(\"v\", [X])", ExpectedValue = matrix == null ? expected : null, ExpectedMatrix = matrix };

        private static ResultSet Rs(params object[][] rows) => new ResultSet { Rows = rows, RowCount = rows.Length };

        [Fact]
        public void Value_offline_is_unverified_never_a_fake_pass()
        {
            var (o, d) = InterviewScoring.ScoreValue(ValueQ(), null);
            Assert.Equal("Unverified", o);
            Assert.Contains("offline", d);
        }

        [Fact]
        public void Value_erroring_query_is_unverified_with_the_error_surfaced()
        {
            var (o, d) = InterviewScoring.ScoreValue(ValueQ(), new ResultSet { Error = "Unknown column 'X'." });
            Assert.Equal("Unverified", o);
            Assert.Contains("Unknown column", d);
        }

        [Fact]
        public void Value_match_is_correct_including_float_summation_noise()
        {
            Assert.Equal("Correct", InterviewScoring.ScoreValue(ValueQ("100"), Rs(new object[] { 100.0 })).Outcome);
            // ValuesEqual's relative tolerance: SUM-vs-SUMX reordering noise must not read as "confidently wrong".
            Assert.Equal("Correct", InterviewScoring.ScoreValue(ValueQ("0.3"), Rs(new object[] { 0.30000000000000004 })).Outcome);
        }

        [Fact]
        public void Value_mismatch_is_silently_wrong_with_both_numbers_in_the_evidence()
        {
            var (o, d) = InterviewScoring.ScoreValue(ValueQ("100"), Rs(new object[] { 250.0 }));
            Assert.Equal("SilentlyWrong", o);
            Assert.Contains("250", d);
            Assert.Contains("100", d);
        }

        [Fact]
        public void Value_shape_doubts_downgrade_to_unverified_not_wrong()
        {
            // multi-row result vs a scalar oracle: a real doubt, so Unverified (high-precision bias).
            var multi = InterviewScoring.ScoreValue(ValueQ("100"), Rs(new object[] { 1.0 }, new object[] { 2.0 }));
            Assert.Equal("Unverified", multi.Outcome);
            // zero rows (SUMMARIZECOLUMNS drops all-blank rows): a query-shape artifact, not proof of a wrong number.
            var empty = InterviewScoring.ScoreValue(ValueQ("100"), Rs());
            Assert.Equal("Unverified", empty.Outcome);
            Assert.Contains("EVALUATE ROW", empty.Detail);
            // no oracle recorded at all.
            var noOracle = InterviewScoring.ScoreValue(new InterviewQuestion { Question = "q", Tier = "value", Query = "EVALUATE ROW(\"v\",[X])" }, Rs(new object[] { 1.0 }));
            Assert.Equal("Unverified", noOracle.Outcome);
        }

        [Fact]
        public void Value_matrix_compares_order_insensitively_and_downgrades_on_truncation()
        {
            var matrix = new[] { new[] { "2023", "1200.5" }, new[] { "2024", "1310" } };
            // rows arrive in the OTHER order (SUMMARIZECOLUMNS guarantees none) → still Correct.
            var ok = InterviewScoring.ScoreValue(ValueQ(matrix: matrix), Rs(new object[] { "2024", 1310.0 }, new object[] { "2023", 1200.5 }));
            Assert.Equal("Correct", ok.Outcome);
            // one cell off → SilentlyWrong.
            var wrong = InterviewScoring.ScoreValue(ValueQ(matrix: matrix), Rs(new object[] { "2024", 9999.0 }, new object[] { "2023", 1200.5 }));
            Assert.Equal("SilentlyWrong", wrong.Outcome);
            // row-count mismatch is a wrong-shaped ANSWER (not a doubt) → SilentlyWrong.
            var short1 = InterviewScoring.ScoreValue(ValueQ(matrix: matrix), Rs(new object[] { "2023", 1200.5 }));
            Assert.Equal("SilentlyWrong", short1.Outcome);
            // truncated coverage is a doubt → Unverified.
            var trunc = InterviewScoring.ScoreValue(ValueQ(matrix: matrix), new ResultSet { Rows = new[] { new object[] { "2023", 1200.5 } }, RowCount = 1, Truncated = true });
            Assert.Equal("Unverified", trunc.Outcome);
        }

        // ---- scoring kernel: tier 2 (paraphrase consistency, the honesty-downgrade ladder) ----

        private static InterviewQuestion ParaQ(string[] groupBy = null) => new InterviewQuestion
        { Question = "q", Tier = "paraphrase", ScalarExpr = "[A]", ParaphraseExpr = "[B]", GroupBy = groupBy ?? Array.Empty<string>() };

        [Fact]
        public void Paraphrase_full_agreement_is_correct_only_with_a_per_context_grid()
        {
            var eq = new EquivalenceResult { AllMatch = true, RowsCompared = 24 };
            // No groupBy → THIN evidence: the shared ladder (DaxBench.ClassifyEquivalenceEvidence) holds it at
            // Unverified — a grand-total-only agreement must never grade Correct (it used to, with a parenthetical).
            var (o, d) = InterviewScoring.ScoreParaphrase(ParaQ(), eq);
            Assert.Equal("Unverified", o);
            Assert.Contains("grand total", d);
            var (o2, d2) = InterviewScoring.ScoreParaphrase(ParaQ(new[] { "'Date'[Year]" }), eq);
            Assert.Equal("Correct", o2);
            Assert.DoesNotContain("grand total", d2);
        }

        [Fact]
        public void Paraphrase_mismatch_is_silently_wrong_with_sample_contexts()
        {
            var eq = new EquivalenceResult
            {
                AllMatch = false, RowsCompared = 24, MismatchCount = 3,
                Mismatches = new[] { new EquivalenceMismatch { Context = "'Date'[Year]=2024", ValueA = "100", ValueB = "250" } },
            };
            var (o, d) = InterviewScoring.ScoreParaphrase(ParaQ(new[] { "'Date'[Year]" }), eq);
            Assert.Equal("SilentlyWrong", o);
            Assert.Contains("3/24", d);
            Assert.Contains("2024", d);
        }

        [Fact]
        public void Paraphrase_doubts_downgrade_to_unverified_the_localengine_precedent()
        {
            // Truncated: agreement over incomplete coverage is NOT a proof (LocalEngine.cs OptimizeMeasureAsync ladder).
            Assert.Equal("Unverified", InterviewScoring.ScoreParaphrase(ParaQ(), new EquivalenceResult { AllMatch = true, RowsCompared = 100000, Truncated = true }).Outcome);
            // Zero rows compared: nothing was actually proven.
            Assert.Equal("Unverified", InterviewScoring.ScoreParaphrase(ParaQ(), new EquivalenceResult { AllMatch = true, RowsCompared = 0 }).Outcome);
            // The comparison itself erroring.
            Assert.Equal("Unverified", InterviewScoring.ScoreParaphrase(ParaQ(), new EquivalenceResult { Error = "boom" }).Outcome);
            // Offline.
            Assert.Equal("Unverified", InterviewScoring.ScoreParaphrase(ParaQ(), null).Outcome);
        }

        // ---- scoring kernel: tier 2 oracle PROPERTY PROBE (agreement != correctness — the H16 lesson) ----

        // A paraphrase question that ALSO pins a trusted answer (a verified answer, or a pasted Copilot/known-good
        // number). ScalarExpr/ParaphraseExpr are the two phrasings; ExpectedValue is the oracle the agreed answer
        // must reconcile with.
        private static InterviewQuestion ParaOracleQ(string expected = "100", string[][] matrix = null) => new InterviewQuestion
        {
            Question = "q", Tier = "paraphrase", ScalarExpr = "[A]", ParaphraseExpr = "[B]",
            GroupBy = new[] { "'Date'[Year]" },   // a per-context grid: thin (grand-total-only) evidence never reaches the oracle gate
            ExpectedValue = matrix == null ? expected : null, ExpectedMatrix = matrix,
        };

        [Fact]
        public void Paraphrase_consistently_wrong_is_caught_by_the_oracle_probe_not_passed_as_consistent()
        {
            // The two phrasings AGREE across every context — a pure consistency check passes them.
            var agree = new EquivalenceResult { AllMatch = true, RowsCompared = 12 };
            // …but the number they agree ON is 250, not the trusted 100: both phrasings are confidently wrong the
            // SAME way. The oracle property probe (the agreed answer executed = 250) catches it → SilentlyWrong.
            var wrongProbe = Rs(new object[] { 250.0 });
            var (o, d) = InterviewScoring.ScoreParaphrase(ParaOracleQ("100"), agree, wrongProbe);
            Assert.Equal("SilentlyWrong", o);
            Assert.Contains("AGREES", d);                // the evidence names the trap: they agree, but on a wrong number
            Assert.Contains("250", d);
            Assert.Contains("100", d);
        }

        [Fact]
        public void Paraphrase_genuinely_correct_answer_still_scores_correct_after_the_probe()
        {
            var agree = new EquivalenceResult { AllMatch = true, RowsCompared = 12 };
            // The phrasings agree AND the agreed answer matches the trusted value → the strongest verdict.
            var rightProbe = Rs(new object[] { 100.0 });
            var (o, d) = InterviewScoring.ScoreParaphrase(ParaOracleQ("100"), agree, rightProbe);
            Assert.Equal("Correct", o);
            Assert.Contains("matches the value you trust", d);
            // A matrix oracle over the grid is proven the same order-insensitive way as the value tier.
            var matrix = new[] { new[] { "2023", "10" }, new[] { "2024", "20" } };
            var matrixProbe = Rs(new object[] { "2024", 20.0 }, new object[] { "2023", 10.0 });
            Assert.Equal("Correct", InterviewScoring.ScoreParaphrase(ParaOracleQ(matrix: matrix), agree, matrixProbe).Outcome);
        }

        [Fact]
        public void Paraphrase_with_an_oracle_but_no_live_probe_is_unverified_not_a_bare_right()
        {
            // Phrasings agree, an oracle is pinned, but offline (no probe was run): holding the verdict to the oracle
            // is the honest, high-precision call — never a bare "Right" that ignores the number the user pinned.
            var agree = new EquivalenceResult { AllMatch = true, RowsCompared = 12 };
            var (o, d) = InterviewScoring.ScoreParaphrase(ParaOracleQ("100"), agree, oracleProbe: null);
            Assert.Equal("Unverified", o);
            Assert.Contains("not proof", d);
            // An un-anchorable probe (e.g. the answer returned no rows) also holds at Unverified, not a fabricated pass.
            var empty = InterviewScoring.ScoreParaphrase(ParaOracleQ("100"), agree, Rs());
            Assert.Equal("Unverified", empty.Outcome);
        }

        [Fact]
        public void Paraphrase_without_an_oracle_keeps_the_pure_consistency_verdict_no_regression()
        {
            // The behaviour is untouched when no trusted answer is pinned: PER-CONTEXT agreement alone is Correct
            // (and a supplied probe is ignored, since there is nothing to check it against).
            var grid = new[] { "'Date'[Year]" };
            var agree = new EquivalenceResult { AllMatch = true, RowsCompared = 24 };
            Assert.Equal("Correct", InterviewScoring.ScoreParaphrase(ParaQ(grid), agree).Outcome);
            Assert.Equal("Correct", InterviewScoring.ScoreParaphrase(ParaQ(grid), agree, Rs(new object[] { 999.0 })).Outcome);
            // A mismatch is still SilentlyWrong before the oracle block is ever reached (the probe is not consulted).
            var mismatch = new EquivalenceResult { AllMatch = false, RowsCompared = 24, MismatchCount = 1, Mismatches = new[] { new EquivalenceMismatch { Context = "x", ValueA = "1", ValueB = "2" } } };
            Assert.Equal("SilentlyWrong", InterviewScoring.ScoreParaphrase(ParaOracleQ("100"), mismatch, Rs(new object[] { 100.0 })).Outcome);
        }

        // ---- scoring kernel: tier 3 (refusal — first-class, fully offline) ----

        [Fact]
        public void Refusal_abstain_is_refused_attempt_is_silently_wrong_neither_is_unverified()
        {
            var q = new InterviewQuestion { Question = "q", Tier = "refusal", ExpectRefusal = true };
            Assert.Equal("Refused", InterviewScoring.ScoreRefusal(q, abstained: true, attemptProduced: false).Outcome);
            Assert.Equal("SilentlyWrong", InterviewScoring.ScoreRefusal(q, abstained: false, attemptProduced: true).Outcome);
            Assert.Equal("Unverified", InterviewScoring.ScoreRefusal(q, abstained: false, attemptProduced: false).Outcome);
        }

        // ---- ops-level offline honesty + run records ----

        [Fact]
        public async Task Run_by_id_offline_is_unverified_and_records_the_outcome_on_the_saved_question()
        {
            var (e, ws, home) = await MakeWs();
            try
            {
                var q = await AddValue(e);
                var r = await e.RunInterviewAsync(q.Id, null, false, null, "agent");
                Assert.Equal("Unverified", r.Outcome);           // offline can NEVER be a pass
                Assert.Contains("offline", r.Detail);
                Assert.True(r.Recorded);                          // the outcome is persisted on the saved question…

                var listed = await e.ListInterviewQuestionsAsync("project");
                var back = listed.Questions.Single(x => x.Id == q.Id);
                Assert.Equal("Unverified", back.LastRun?.Outcome);   // …so the Studio card shows it without any agent
            }
            finally { Cleanup(ws, home); }
        }

        [Fact]
        public async Task Run_teaches_when_given_nothing_or_unparseable_inline_json()
        {
            var (e, ws, home) = await MakeWs();
            try
            {
                var nothing = await Assert.ThrowsAsync<InvalidOperationException>(() => e.RunInterviewAsync(null, null, false, null, "agent"));
                Assert.Contains("questionId", nothing.Message);
                var bad = await Assert.ThrowsAsync<InvalidOperationException>(() => e.RunInterviewAsync(null, "{oops", false, null, "agent"));
                Assert.Contains("inlineJson", bad.Message);
            }
            finally { Cleanup(ws, home); }
        }

        // ---- failure→fix mapping (author wins; the embedded data table is the fallback) ----

        [Fact]
        public async Task Fix_mapping_uses_the_data_table_fallback_and_the_author_rule_wins_when_supplied()
        {
            var (e, ws, home) = await MakeWs();
            try
            {
                // No author rule → the {tier: refusal, outcome: SilentlyWrong} table entry applies, with a PLAIN hint.
                var q1 = await AddRefusal(e);
                var r1 = await e.RunInterviewAsync(q1.Id, null, false, "EVALUATE ROW(\"v\", [Made Up])", "agent");
                Assert.Equal("SilentlyWrong", r1.Outcome);
                Assert.Equal("DAC-AI-INSTRUCTIONS", r1.FixRuleId);
                Assert.False(string.IsNullOrWhiteSpace(r1.FixHint));
                Assert.DoesNotContain("DAC-", r1.FixHint);        // the hint is plain language, never a rule id

                // Author-supplied fixRuleId wins over the table.
                var q2 = await AddRefusal(e, fixRuleId: "SYN-SCHEMA");
                var r2 = await e.RunInterviewAsync(q2.Id, null, false, "EVALUATE ROW(\"v\", [Made Up])", "agent");
                Assert.Equal("SYN-SCHEMA", r2.FixRuleId);

                // Correct/Refused/Unverified carry NO fix (nothing to fix / not a model problem).
                var r3 = await e.RunInterviewAsync(q1.Id, null, true, null, "agent");
                Assert.Equal("Refused", r3.Outcome);
                Assert.Null(r3.FixRuleId);
            }
            finally { Cleanup(ws, home); }
        }

        [Fact]
        public void Every_rule_id_in_the_fix_map_exists_in_the_real_readiness_ruleset()
        {
            // The map is DATA (docs/interview-fix-map.json, embedded) — this pins it against drifting from the
            // ruleset it points into (a red row must never link to a rule that doesn't exist).
            var known = Semanticus.Analysis.ReadinessRuleSet.Default().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var tier in new[] { "value", "paraphrase", "refusal" })
            {
                var (ruleId, hint) = InterviewFixMap.Resolve(tier, "SilentlyWrong");
                Assert.False(string.IsNullOrWhiteSpace(ruleId));
                Assert.False(string.IsNullOrWhiteSpace(hint));
                Assert.Contains(ruleId, known);
            }
            // Unverified deliberately has NO entry: "couldn't check" is a connectivity gap, not a model fix.
            Assert.Null(InterviewFixMap.Resolve("value", "Unverified").RuleId);
        }

        [Fact]
        public async Task Tests_replay_saved_questions_as_current_evidence_without_changing_health_or_live_history()
        {
            var (e, ws, home) = await MakeWs();
            try
            {
                var baseline = await e.RunTestSuiteAsync(false, "human");
                var value = await AddValue(e);
                var refusal = await AddRefusal(e);

                var run = await e.RunTestSuiteAsync(false, "human");

                Assert.Equal(baseline.Health.Grade, run.Health.Grade);
                Assert.Equal(baseline.Health.CoveragePct, run.Health.CoveragePct);
                Assert.Equal(baseline.Health.Passed, run.Health.Passed);
                Assert.Equal(baseline.Health.Failed, run.Health.Failed);
                Assert.Equal(baseline.Health.Suspect, run.Health.Suspect);
                Assert.Equal(baseline.Health.NotVerifiable, run.Health.NotVerifiable);

                var replayed = Assert.Single(run.Interview, x => x.QuestionId == value.Id);
                Assert.Equal("replayed", replayed.ReplayStatus);
                Assert.Equal("Unverified", replayed.Outcome);
                Assert.Null(replayed.PreviousOutcome);
                Assert.False(replayed.Changed);
                Assert.True(DateTime.TryParse(replayed.When, out _));

                var chatOnly = Assert.Single(run.Interview, x => x.QuestionId == refusal.Id);
                Assert.Equal("chat-only", chatOnly.ReplayStatus);
                Assert.Null(chatOnly.Outcome);
                Assert.Null(chatOnly.When);
                Assert.Contains("assistant response", chatOnly.Detail);
                Assert.Contains("evidence only", run.InterviewNote);
                Assert.Contains("prior live outcomes were not overwritten", run.InterviewNote);

                var stored = await e.ListInterviewQuestionsAsync("project");
                Assert.All(stored.Questions, q => Assert.Null(q.LastRun));
            }
            finally { Cleanup(ws, home); }
        }

        // ---- interview_replay workflow verify executor (the regression half) ----

        private const string ReplayMd = @"---
name: interview-replay
title: Interview replay honesty
strictness: hard
---
## Step 1: Replay
Replay the model's saved interview pack.
```yaml gate
verify:
  - kind: interview_replay
```
";

        private static async Task<(LocalEngine engine, SessionManager sessions, string ws, string origHome)> MakeWorkflowWs(bool pro = true)
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-interview-wf-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "interview-replay.md"), ReplayMd);
            var origHome = RedirectHome(ws);
            var sessions = new SessionManager();
            var e = new LocalEngine(sessions, new Fake(pro), ws);
            await e.CreateModelAsync("WfModel", 1604);
            sessions.Current.SourcePath = Path.Combine(ws, "WfModel.bim");   // #157-R2: a DURABLE identity so the pack persists and replays
            return (e, sessions, ws, origHome);
        }

        [Fact]
        public async Task Interview_replay_with_an_empty_pack_fails_the_hard_gate_instructively()
        {
            var (e, sessions, ws, home) = await MakeWorkflowWs();
            try
            {
                // The parser accepts the new kind (start would refuse an unparseable file) …
                var run = await e.StartWorkflowAsync("interview-replay", "human");
                // … and an empty pack FAILS (an unrunnable check must never quietly pass a hard gate — the
                // bpa_clean missing-snapshot precedent), teaching where questions come from.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "human"));
                Assert.Contains("no saved interview questions", ex.Message);
                Assert.Contains("add_interview_question", ex.Message);

                var after = await e.GetWorkflowRunAsync(run.RunId);
                Assert.Equal("failed", after.Steps[0].Status);
            }
            finally { sessions.Dispose(); Cleanup(ws, home); }
        }

        [Fact]
        public async Task Interview_replay_offline_comes_back_skipped_not_passed_and_does_not_block_the_hard_gate()
        {
            var (e, sessions, ws, home) = await MakeWorkflowWs();
            try
            {
                await AddValue(e);   // a non-empty pack, but NO live connection
                var run = await e.StartWorkflowAsync("interview-replay", "human");
                var done = await e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "human");

                Assert.Equal("completed", done.Status);            // a hard gate does NOT block on a skip
                var v = done.Steps[0].VerifyResults.Single(x => x.Kind == "interview_replay");
                Assert.Equal("skipped", v.Status);                 // skipped != passed — the offline honesty contract
                Assert.Contains("offline", v.Detail);
                Assert.Contains("open_live", v.Detail);
            }
            finally { sessions.Dispose(); Cleanup(ws, home); }
        }

        [Fact]
        public async Task Check_workflow_accepts_interview_replay_without_target_or_probe_warnings()
        {
            var (e, sessions, ws, home) = await MakeWorkflowWs();
            try
            {
                // interview_replay replays a stored pack: it needs no objectRef target and no probe input, so the
                // admission linter must not warn (needsTarget/needsProbe lists stay untouched by design).
                var report = await e.CheckWorkflowAsync("interview-replay");
                Assert.True(report.Ok);
                Assert.Null(report.ParseError);
            }
            finally { sessions.Dispose(); Cleanup(ws, home); }
        }

        // ============================================================================================
        // #157: a pack is BOUND to the model it was authored against — the cross-model leak Kane hit
        // (open model = Contoso, but the Interview pane listed another model's questions). The store's
        // ephemeral/global fallback shares ONE file across every model opened from a workspace, so
        // whichever model last interviewed leaked into the next. Identity = the edit-STABLE provenance
        // identity (live endpoint|database / on-disk anchor), NOT the shape fingerprint (which changes on
        // the very edits interview_replay guards). Reads filter to the open model; runs of another model's
        // (or an unattributed) question are refused so its trusted answer is never graded against the
        // wrong model's data.
        // ============================================================================================

        // A model open with a controllable identity, over the SHARED workspace fallback file (SourcePath is null =
        // ephemeral, exactly Kane's live-XMLA-no-folder case): LiveOrigin gives the identity, the workspace gives
        // the store. All under a disposable temp dir; the real home is redirected so global-scope reads stay local.
        private static (LocalEngine e, SessionManager sessions, string ws, string origHome) MakeModelWs(bool pro = true)
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-interview-m-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus"));
            var origHome = RedirectHome(ws);
            var sessions = new SessionManager();
            return (new LocalEngine(sessions, new Fake(pro), ws), sessions, ws, origHome);
        }

        private static Task<InterviewQuestion> AddValueFor(LocalEngine e, string question, string expected) =>
            e.AddInterviewQuestionAsync(question, "value", "EVALUATE ROW(\"v\", [Total])", null, null, null, null,
                expected, null, false, null, "user", "project", "human");

        [Fact]
        public async Task Pack_authored_against_one_model_is_not_offered_or_run_for_another()
        {
            var (e, sessions, ws, home) = MakeModelWs();
            try
            {
                await e.CreateModelAsync("M", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("srv", "ModelA", "tenant");
                var qa = await AddValueFor(e, "What were A's sales?", "100");
                Assert.Equal("ModelA", qa.ModelLabel);                       // stamped with the authoring model's identity…
                Assert.False(string.IsNullOrEmpty(qa.ModelIdentity));

                // The workspace now opens a DIFFERENT live model (same shared store — the leak vector).
                sessions.Current.LiveOrigin = new LiveOrigin("srv", "ModelB", "tenant");
                var listed = await e.ListInterviewQuestionsAsync("project");
                Assert.DoesNotContain(listed.Questions, x => x.Id == qa.Id);  // …so it is NOT offered as ModelB's pack
                Assert.Contains(listed.OtherModelQuestions, x => x.Id == qa.Id && x.ModelLabel == "ModelA");

                // …and grading it against ModelB is refused with a teaching message (never a misleading grade).
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.RunInterviewAsync(qa.Id, null, false, null, "agent"));
                Assert.Contains("different model", ex.Message);
                Assert.Contains("ModelA", ex.Message);
                Assert.Contains("delete_interview_question", ex.Message);
            }
            finally { sessions.Dispose(); Cleanup(ws, home); }
        }

        [Fact]
        public async Task Legacy_unbound_pack_in_a_shared_store_is_surfaced_unattributed_not_adopted_and_not_run()
        {
            var (e, sessions, ws, home) = MakeModelWs();
            try
            {
                // A pre-#157 question (no modelIdentity) sitting in the SHARED workspace store — the leaked bamboo
                // pack from Kane's repro. The location vouches for nothing, so it must NOT be adopted into Contoso.
                var file = Path.Combine(ws, ".semanticus", "interview", "questions.jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, "{\"op\":\"add\",\"id\":\"iq-legacy01\",\"when\":\"2026-01-01T00:00:00Z\",\"question\":\"How many bamboo spans are overdue for re-inspection?\",\"tier\":\"value\",\"query\":\"EVALUATE ROW(\\\"v\\\",[Spans])\",\"expectedValue\":\"42\"}\n");

                await e.CreateModelAsync("Contoso", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("srv", "Contoso", "tenant");   // ephemeral anchor → the shared file, but a real identity

                var listed = await e.ListInterviewQuestionsAsync("project");
                Assert.Empty(listed.Questions);                                        // NOT silently adopted into Contoso (fail-closed)
                Assert.Contains(listed.UnattributedQuestions, x => x.Id == "iq-legacy01");   // surfaced honestly instead
                Assert.Contains("unattributed", listed.Note);

                // Running it against Contoso is refused (binding cannot be established).
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.RunInterviewAsync("iq-legacy01", null, false, null, "agent"));
                Assert.Contains("unattributed", ex.Message);

                // Honest, not destructive: the user's work is still on disk (delete_interview_question can retire it).
                Assert.True(File.Exists(file));
            }
            finally { sessions.Dispose(); Cleanup(ws, home); }
        }

        [Fact]
        public async Task Legacy_unbound_pack_is_never_auto_adopted_even_in_a_per_model_sidecar()
        {
            // R1/P1-2: the "per-model sidecar" is actually per-FOLDER (LayoutStore.DirFor drops the filename), so two
            // models in one folder share it — location can NOT vouch for ownership. Automatic adoption is killed: a
            // pre-binding question is ALWAYS unattributed, never silently adopted, even in an anchored on-disk sidecar.
            var (e, sessions, ws, home) = MakeModelWs();
            try
            {
                await e.CreateModelAsync("OnDisk", 1604);
                sessions.Current.SourcePath = Path.Combine(ws, "model", "OnDisk.bim");    // anchored per-model store
                var sidecar = Path.Combine(ws, "model", ".semanticus", "interview", "questions.jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(sidecar));
                File.WriteAllText(sidecar, "{\"op\":\"add\",\"id\":\"iq-own01\",\"when\":\"2026-01-01T00:00:00Z\",\"question\":\"Total sales?\",\"tier\":\"value\",\"query\":\"EVALUATE ROW(\\\"v\\\",[Total])\",\"expectedValue\":\"5\"}\n");

                var listed = await e.ListInterviewQuestionsAsync("project");
                Assert.DoesNotContain(listed.Questions, x => x.Id == "iq-own01");           // NOT adopted (fail-closed)
                Assert.Contains(listed.UnattributedQuestions, x => x.Id == "iq-own01");     // surfaced honestly

                // …and running it is refused, not graded (its provenance cannot be established).
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.RunInterviewAsync("iq-own01", null, false, null, "agent"));
                Assert.Contains("unattributed", ex.Message);
            }
            finally { sessions.Dispose(); Cleanup(ws, home); }
        }

        [Fact]
        public async Task No_open_model_identity_adopts_nothing_fail_closed()
        {
            // R1/P1-5: with NO model open there is no establishable identity, so NOTHING is "mine" — a bound-foreign
            // pack and an unbound legacy pack are both surfaced (never listed as mine, never run, never counted).
            var (e, sessions, ws, home) = MakeModelWs();
            try
            {
                var file = Path.Combine(ws, ".semanticus", "interview", "questions.jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file,
                    "{\"op\":\"add\",\"id\":\"iq-foreign\",\"when\":\"2026-01-01T00:00:00Z\",\"question\":\"Ghost model sales?\",\"tier\":\"value\",\"query\":\"EVALUATE ROW(\\\"v\\\",[G])\",\"expectedValue\":\"7\",\"modelIdentity\":\"live:deadbeef00\",\"modelLabel\":\"Ghost Model\"}\n" +
                    "{\"op\":\"add\",\"id\":\"iq-legacy\",\"when\":\"2026-01-01T00:00:00Z\",\"question\":\"Legacy churn?\",\"tier\":\"value\",\"query\":\"EVALUATE ROW(\\\"v\\\",[L])\",\"expectedValue\":\"9\"}\n");

                // NO CreateModelAsync — the engine has a workspace but no open model (Current == null).
                var listed = await e.ListInterviewQuestionsAsync("project");
                Assert.Empty(listed.Questions);                                              // nothing is mine
                Assert.Contains(listed.OtherModelQuestions, x => x.Id == "iq-foreign");       // bound-foreign → from another model
                Assert.Contains(listed.UnattributedQuestions, x => x.Id == "iq-legacy");      // unbound → unattributed

                await Assert.ThrowsAsync<InvalidOperationException>(() => e.RunInterviewAsync("iq-foreign", null, false, null, "agent"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => e.RunInterviewAsync("iq-legacy", null, false, null, "agent"));
            }
            finally { sessions.Dispose(); Cleanup(ws, home); }
        }

        [Fact]
        public async Task Session_is_this_model_but_the_attached_live_connection_is_a_different_model_refuses_grading()
        {
            // R1/P1-1 — THE core defect: attribution validated the editable SESSION, but grading executes against the
            // independently-attached _live. Open model A (session), confirm the oracle against A's live connection,
            // then attach a live connection to a DIFFERENT model B and try to grade A's pack — it must be refused,
            // because the query would run against B's data.
            var (e, sessions, ws, home) = MakeModelWs();
            try
            {
                await e.CreateModelAsync("M", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("endpoint-A", "", "tenant");     // pane identity = model A
                e.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-A"));     // oracle confirmed against A's live
                var q = await AddValueFor(e, "A's sales?", "100");
                Assert.False(string.IsNullOrEmpty(q.ExecIdentity));                            // the execution target was recorded

                // A separate live connection is now attached to a DIFFERENT model (connect_xmla swaps _live only).
                e.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-B"));
                // Gate 1 still says "mine" (the editable session is unchanged), but Gate 2 refuses the wrong target.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.RunInterviewAsync(q.Id, null, false, null, "agent"));
                Assert.Contains("different model", ex.Message);
                Assert.Contains("endpoint-B", ex.Message);
            }
            finally { sessions.Dispose(); Cleanup(ws, home); }
        }

        [Fact]
        public async Task A_live_swap_between_capture_and_execution_aborts_rather_than_grades_driven_through_the_real_path()
        {
            // R1/P1-3 (TOCTOU), driven END-TO-END through RunInterviewCoreAsync via the BeforeInterviewExecute seam:
            // the op captures the live connection at entry, and a swap that lands right before execution must abort
            // with Unverified, never a graded verdict against the replacement.
            var (e, sessions, ws, home) = MakeModelWs();
            try
            {
                await e.CreateModelAsync("M", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("endpoint-A", "", "tenant");
                e.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-A"));
                var q = await AddValueFor(e, "A's sales?", "100");                              // ExecIdentity = endpoint-A

                // Simulate a real mid-run swap: right before the DAX executes, the live connection is replaced.
                e.BeforeInterviewExecuteForTest = () => e.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-B"));
                var r = await e.RunInterviewAsync(q.Id, null, false, null, "agent");
                Assert.Equal("Unverified", r.Outcome);                                          // aborted, NOT graded against B
                Assert.Contains("live connection changed", r.Detail);
                Assert.False(r.Recorded == false && r.Outcome == "Correct");                    // never a fabricated pass

                // And the guard's session branch, asserted directly (a mid-run session swap can't be driven safely
                // through an async hook without risking a dispatcher deadlock).
                e.BeforeInterviewExecuteForTest = null;
                var s1 = sessions.Current;
                var l1 = LiveConnection.ForTest("xmla", "endpoint-A");
                e.SetLiveConnectionForTest(l1);
                await e.CreateModelAsync("N", 1604);
                Assert.Contains("open model changed", e.StabilityRefusalForTest(s1, l1));
            }
            finally { sessions.Dispose(); Cleanup(ws, home); }
        }

        [Fact]
        public async Task Unsaved_unconnected_model_cannot_persist_a_pack_and_never_adopts_a_sess_id_pack()
        {
            // R2/P1-A: PaneIdentity used to fall back to "sess:"+Session.Id, a PER-PROCESS counter that resets to 0
            // each launch — so the first scratch model of every process was sess:s1 and packs collided across
            // launches (a refusal-tier question would be graded against the wrong schema). Fixed two ways: (1) an
            // unsaved+unconnected model has NO durable identity, so add_interview_question is refused; (2) a pack
            // stamped "sess:s1" (from an older build / another launch) is never adopted by a fresh scratch model.
            var (e, sessions, ws, home) = MakeModelWs();
            try
            {
                await e.CreateModelAsync("Scratch", 1604);   // unsaved, unconnected → no durable identity

                // (1) persistence is refused with a teaching message (even for a refusal-tier question).
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    e.AddInterviewQuestionAsync("Can it answer X?", "refusal", null, null, null, null, null, null, null, true, null, "user", "project", "human"));
                Assert.Contains("no durable identity", ex.Message);
                Assert.Contains("save_model", ex.Message);

                // (2) a legacy sess:s1 pack from another launch is NOT this scratch model's — surfaced, never run.
                var file = Path.Combine(ws, ".semanticus", "interview", "questions.jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, "{\"op\":\"add\",\"id\":\"iq-s1\",\"when\":\"2026-01-01T00:00:00Z\",\"question\":\"Overdue bamboo spans?\",\"tier\":\"refusal\",\"expectRefusal\":true,\"modelIdentity\":\"sess:s1\"}\n");
                var listed = await e.ListInterviewQuestionsAsync("project");
                Assert.DoesNotContain(listed.Questions, x => x.Id == "iq-s1");                  // never Mine
                Assert.Contains(listed.OtherModelQuestions, x => x.Id == "iq-s1");
                await Assert.ThrowsAsync<InvalidOperationException>(() => e.RunInterviewAsync("iq-s1", null, true, null, "agent"));
            }
            finally { sessions.Dispose(); Cleanup(ws, home); }
        }

        [Fact]
        public async Task Two_models_in_one_folder_do_not_share_a_pack()
        {
            // R1/P1-2: the on-disk identity is the FULL model path (not the folder LayoutStore.DirFor hashes), so two
            // models in one directory — which share the sidecar file — still get distinct identities and never
            // classify as each other's.
            var (e, sessions, ws, home) = MakeModelWs();
            try
            {
                var dir = Path.Combine(ws, "shared");
                Directory.CreateDirectory(dir);
                await e.CreateModelAsync("A", 1604);
                sessions.Current.SourcePath = Path.Combine(dir, "A.bim");
                var qa = await AddValueFor(e, "A's total?", "1");

                // A DIFFERENT model file in the SAME folder (same sidecar store) must not see A's pack as its own.
                sessions.Current.SourcePath = Path.Combine(dir, "B.bim");
                var listed = await e.ListInterviewQuestionsAsync("project");
                Assert.DoesNotContain(listed.Questions, x => x.Id == qa.Id);
                Assert.Contains(listed.OtherModelQuestions, x => x.Id == qa.Id);
                await Assert.ThrowsAsync<InvalidOperationException>(() => e.RunInterviewAsync(qa.Id, null, false, null, "agent"));
            }
            finally { sessions.Dispose(); Cleanup(ws, home); }
        }

        [Fact]
        public async Task A_replaced_model_at_the_same_endpoint_with_a_different_name_is_refused_not_graded()
        {
            // R1/P1-4: endpoint|database is a deployment SLOT; a different model can occupy it over time. WITNESS
            // SCOPE (honest): the witness is the EDITABLE SESSION's model name, so it only detects a replacement the
            // session reflects — i.e. open_live re-opened after the slot's model changed (here: two open_live sessions
            // on the same endpoint, one "Sales", then "Bamboo"). It does NOT catch a connect_xmla-only replacement
            // (no session witness), nor a SAME-NAME in-place replacement (disclosed in the list Note, not caught).
            var (e, sessions, ws, home) = MakeModelWs();
            try
            {
                await e.CreateModelAsync("Sales", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("endpoint-L", "", "tenant");
                e.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-L"));      // pane identity == exec identity (open_live shape)
                var q = await AddValueFor(e, "Sales?", "5");
                Assert.Equal("Sales", q.ModelWitness);

                // The model at endpoint-L is REPLACED by a different model of a different name; same slot, so the
                // execution identity is unchanged and only the witness differs.
                await e.CreateModelAsync("Bamboo", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("endpoint-L", "", "tenant");
                e.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-L"));
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.RunInterviewAsync(q.Id, null, false, null, "agent"));
                Assert.Contains("Sales", ex.Message);
                Assert.Contains("Bamboo", ex.Message);
                Assert.Contains("replaced", ex.Message);
            }
            finally { sessions.Dispose(); Cleanup(ws, home); }
        }

        [Fact]
        public async Task A_disk_pack_after_an_in_place_file_overwrite_is_refused_or_caveated_never_silently_graded_clean()
        {
            // R3: a file PATH is an address too — overwrite the same .bim with a different model and re-open, and the
            // disk identity is unchanged. This covers the lane Gate 2 (live) missed: a REFUSAL-tier disk pack has no
            // live connection and is graded offline, so without the tier-independent witness it would be graded clean
            // against the new model. Two outcomes are acceptable, never a silent clean pass:
            var (e, sessions, ws, home) = MakeModelWs();
            try
            {
                // (1) different name after overwrite -> REFUSED (the refusal tier, the exact gap).
                await e.CreateModelAsync("Original", 1604);
                sessions.Current.SourcePath = Path.Combine(ws, "model.bim");
                var q = await e.AddInterviewQuestionAsync("Can it answer X?", "refusal", null, null, null, null, null, null, null, true, null, "user", "project", "human");
                Assert.Equal("Original", q.ModelWitness);

                await e.CreateModelAsync("Replacement", 1604);              // a DIFFERENT model overwrites the same path
                sessions.Current.SourcePath = Path.Combine(ws, "model.bim");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.RunInterviewAsync(q.Id, null, true, null, "agent"));
                Assert.Contains("Original", ex.Message);
                Assert.Contains("Replacement", ex.Message);
                Assert.Contains("replaced", ex.Message);

                // (2) SAME name after overwrite -> the witness passes, so it RUNS, but the confident verdict carries
                // the address caveat (the same-name residual is disclosed on the run, not only the list Note).
                await e.CreateModelAsync("Twin", 1604);
                sessions.Current.SourcePath = Path.Combine(ws, "twin.bim");
                var q2 = await e.AddInterviewQuestionAsync("Can it answer Y?", "refusal", null, null, null, null, null, null, null, true, null, "user", "project", "human");
                await e.CreateModelAsync("Twin", 1604);                     // a same-NAME model overwrites the same path
                sessions.Current.SourcePath = Path.Combine(ws, "twin.bim");
                var r = await e.RunInterviewAsync(q2.Id, null, true, null, "agent");
                Assert.Equal("Refused", r.Outcome);                         // a confident outcome, not silently hidden
                Assert.Contains("matched by the model's address", r.Detail);
                Assert.Contains("file path", r.Detail);                     // the caveat is true for a file path, not just an endpoint
            }
            finally { sessions.Dispose(); Cleanup(ws, home); }
        }

        [Fact]
        public async Task Renaming_the_live_database_orphans_the_pack_the_documented_binding_limit()
        {
            // The chosen identity is the stable PROVENANCE identity (live endpoint|database), NOT the semantic shape
            // fingerprint — a deliberate trade (see LocalEngine.Interview.cs): it survives model EDITS (the shape
            // fingerprint would not, orphaning the pack on the first added measure — fatal for a regression pack),
            // but it does NOT survive a live-DATABASE RENAME. This test proves that limit HONESTLY — a renamed
            // database reads as another model (surfaced under the divider), never silently adopted, so we never
            // claim a durability we lack.
            var (e, sessions, ws, home) = MakeModelWs();
            try
            {
                await e.CreateModelAsync("M", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("srv", "Sales", "tenant");
                var q = await AddValueFor(e, "What were sales?", "1");

                sessions.Current.LiveOrigin = new LiveOrigin("srv", "SalesProd", "tenant");   // the DBA renames the dataset
                var listed = await e.ListInterviewQuestionsAsync("project");
                Assert.DoesNotContain(listed.Questions, x => x.Id == q.Id);
                Assert.Contains(listed.OtherModelQuestions, x => x.Id == q.Id && x.ModelLabel == "Sales");
            }
            finally { sessions.Dispose(); Cleanup(ws, home); }
        }

        [Fact]
        public async Task Interview_replay_does_not_replay_another_models_pack_against_the_open_model()
        {
            // The workflow gate must never grade another model's questions against the open model (misleading grades
            // are the harm). With only a foreign pack present, replay fails "no questions for THIS model" and names
            // the stray count — it does not quietly pass on the foreign pack, and it does not run it.
            var (e, sessions, ws, home) = MakeModelWs();
            try
            {
                Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
                File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "interview-replay.md"), ReplayMd);

                await e.CreateModelAsync("M", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("srv", "ModelA", "tenant");
                await AddValueFor(e, "A's sales?", "100");                       // authored for ModelA
                sessions.Current.LiveOrigin = new LiveOrigin("srv", "ModelB", "tenant");   // gate runs while ModelB is open

                var run = await e.StartWorkflowAsync("interview-replay", "human");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "human"));
                Assert.Contains("no saved interview questions to replay for the open model", ex.Message);
                Assert.Contains("belong to a different model", ex.Message);
            }
            finally { sessions.Dispose(); Cleanup(ws, home); }
        }
    }
}
