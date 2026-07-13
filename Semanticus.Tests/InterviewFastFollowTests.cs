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
    /// The Model Interview fast-follows (PR #84 deferred items), offline. Pins:
    /// (1) the PROBE-HARDENED value oracle — the house scoring convention (tools/probench/compare.py,
    ///     pre-registered): BLANK ≠ 0 ≠ error in BOTH directions, numbers under max(1e-6 abs, 1e-9·|want| rel)
    ///     which is STRICTER at scale than the rewrite-equivalence band, and the no-oracle detail teaching the
    ///     confirm-and-record flow with the computed answer shown;
    /// (2) VERIFIED-ANSWERS extraction — the observed-not-documented definition.json parses fail-soft (a corrupt
    ///     or unusable definition is skipped WITH its reason, never guessed at), trigger phrasings and PBIR-shape
    ///     field refs are surfaced;
    /// (3) the BUILT-IN HARD-QUESTION PACK — templates bind ONLY where the required shapes exist, every miss is
    ///     an honest skip naming the missing shape, no leftover placeholder ever escapes, and the pack carries no
    ///     bench data or product names;
    /// (4) the deploy_gate INTERVIEW ADVISORY — it never blocks (Pass/Blockers byte-identical with and without a
    ///     pack), reports per-question deltas vs the last recorded outcomes, and offline outcomes are NOT
    ///     recorded (a connectivity gap must not stomp the last real evidence).
    /// </summary>
    public sealed class InterviewFastFollowTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static string TempDir(string tag)
        {
            var dir = Path.Combine(Path.GetTempPath(), "smx-ivff-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ============================================================================================
        // (1) the probe-hardened value oracle (the house convention)
        // ============================================================================================

        private static InterviewQuestion ValueQ(string expected = null, string[][] matrix = null) => new InterviewQuestion
        { Question = "q", Tier = "value", Query = "EVALUATE ROW(\"v\", [X])", ExpectedValue = expected, ExpectedMatrix = matrix };

        private static ResultSet Rs(params object[][] rows) => new ResultSet { Rows = rows, RowCount = rows.Length };

        [Fact]
        public void Oracle_blank_is_never_zero_in_either_direction()
        {
            // A blank answer against a numeric oracle is confidently wrong — and says "blank", not "wrong number".
            var blankVsZero = InterviewScoring.ScoreValue(ValueQ("0"), Rs(new object[] { null }));
            Assert.Equal("SilentlyWrong", blankVsZero.Outcome);
            Assert.Contains("blank", blankVsZero.Detail);

            // The literal BLANK sentinel records "the right answer is no value at all" — 0 does not satisfy it…
            var zeroVsBlank = InterviewScoring.ScoreValue(ValueQ("BLANK"), Rs(new object[] { 0.0 }));
            Assert.Equal("SilentlyWrong", zeroVsBlank.Outcome);

            // …and an actually-blank answer does.
            Assert.Equal("Correct", InterviewScoring.ScoreValue(ValueQ("BLANK"), Rs(new object[] { (object)null })).Outcome);
            Assert.Equal("Correct", InterviewScoring.ScoreValue(ValueQ("(blank)"), Rs(new object[] { (object)null })).Outcome);
        }

        [Fact]
        public void Oracle_tolerance_is_the_bench_convention_stricter_at_scale()
        {
            // max(1e-6 abs, 1e-9 rel): at 1e9 the band is ±1 — float noise passes…
            Assert.Equal("Correct", InterviewScoring.ScoreValue(ValueQ("1000000000"), Rs(new object[] { 1000000000.5 })).Outcome);
            // …but a 50-unit drift is CAUGHT (the old rewrite-equivalence band of 1e-7 rel would have let ±100 through).
            Assert.Equal("SilentlyWrong", InterviewScoring.ScoreValue(ValueQ("1000000000"), Rs(new object[] { 1000000050.0 })).Outcome);
            // The absolute floor still absorbs noise near zero.
            Assert.Equal("Correct", InterviewScoring.ScoreValue(ValueQ("100"), Rs(new object[] { 100.0000009 })).Outcome);
            Assert.Equal("SilentlyWrong", InterviewScoring.ScoreValue(ValueQ("100"), Rs(new object[] { 100.001 })).Outcome);
        }

        [Fact]
        public void Oracle_an_erroring_query_is_unverified_never_wrong()
        {
            // ERROR is the third world: it proves nothing about the number, even with an oracle recorded.
            var r = InterviewScoring.ScoreValue(ValueQ("100"), new ResultSet { Error = "Unknown column 'X'." });
            Assert.Equal("Unverified", r.Outcome);
        }

        [Fact]
        public void No_oracle_detail_shows_the_computed_answer_to_confirm_and_record()
        {
            var r = InterviewScoring.ScoreValue(ValueQ(), Rs(new object[] { 123.45 }));
            Assert.Equal("Unverified", r.Outcome);          // computed cleanly ≠ verified — the oracle is the proof
            Assert.Contains("123.45", r.Detail);            // the number to confirm is shown, not auto-trusted
            Assert.Contains("expectedValue", r.Detail);     // …and the recovery is taught
            Assert.Contains("BLANK", r.Detail);             // incl. how to record a legitimate no-value answer
        }

        [Fact]
        public void Matrix_blank_cells_only_match_blank_results()
        {
            var matrix = new[] { new[] { "2023", "BLANK" }, new[] { "2024", "5" } };
            var ok = InterviewScoring.ScoreValue(ValueQ(matrix: matrix), Rs(new object[] { "2024", 5.0 }, new object[] { "2023", null }));
            Assert.Equal("Correct", ok.Outcome);
            // 0 in the blank cell is a real mismatch, not a rounding miss.
            var wrong = InterviewScoring.ScoreValue(ValueQ(matrix: matrix), Rs(new object[] { "2024", 5.0 }, new object[] { "2023", 0.0 }));
            Assert.Equal("SilentlyWrong", wrong.Outcome);
        }

        // ============================================================================================
        // (2) verified-answers extraction — fail-soft over the observed shape
        // ============================================================================================

        private static string WriteVa(string modelFolder, string guid, string json)
        {
            var dir = Path.Combine(modelFolder, "VerifiedAnswers", "definitions", guid);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "definition.json");
            File.WriteAllText(file, json);
            return file;
        }

        [Fact]
        public void Verified_answers_parse_the_observed_shape_and_fail_soft_on_surprises()
        {
            var dir = TempDir("va");
            try
            {
                // The observed shape: a label, trigger phrasings, and a visual snapshot carrying PBIR field refs.
                WriteVa(dir, "a1", @"{
                  ""name"": ""Regional revenue"",
                  ""triggers"": [""What was revenue by region?"", ""Show revenue per region""],
                  ""visual"": { ""projections"": [
                    { ""Measure"": { ""Expression"": { ""SourceRef"": { ""Entity"": ""Sales"" } }, ""Property"": ""Total Sales"" } },
                    { ""column"": { ""expression"": { ""sourceRef"": { ""entity"": ""Geography"" } }, ""property"": ""Region"" } }
                  ] }
                }");
                WriteVa(dir, "b2", "{ this is not json");                      // corrupt → skipped with the parse reason
                WriteVa(dir, "c3", "{ \"somethingElse\": true }");             // parses, but no question → skipped honestly

                var (usable, skipped, found) = VerifiedAnswerSeeds.Parse(dir);
                Assert.Equal(3, found);
                Assert.Single(usable);
                Assert.Equal(2, skipped.Count);
                Assert.All(skipped, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
                Assert.Contains(skipped, s => s.Id == "b2" && s.Reason.Contains("parsed"));
                Assert.Contains(skipped, s => s.Id == "c3" && s.Reason.Contains("no question text"));

                var seed = usable[0];
                Assert.Equal("verified-answer", seed.Source);
                Assert.Equal("What was revenue by region?", seed.Question);    // first trigger is the question
                Assert.Contains("Show revenue per region", seed.AltPhrasings);
                Assert.Contains("measure:Sales/Total Sales", seed.Targets);    // field refs surfaced, both casings
                Assert.Contains("column:Geography/Region", seed.Targets);
                Assert.Contains("Regional revenue", seed.Note);                // the label rides along
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Verified_answer_triggers_survive_the_nested_object_form()
        {
            var dir = TempDir("va2");
            try
            {
                // triggers as objects ({ text: … }) + a bare queryRef — both observed variants.
                WriteVa(dir, "d4", @"{ ""triggerPhrases"": [ { ""text"": ""Top products by margin?"" } ],
                                       ""binding"": { ""queryRef"": ""Sales.Margin"" } }");
                var (usable, skipped, found) = VerifiedAnswerSeeds.Parse(dir);
                Assert.Equal(1, found);
                Assert.Empty(skipped);
                Assert.Equal("Top products by margin?", usable.Single().Question);
                Assert.Contains("field:Sales.Margin", usable.Single().Targets);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ============================================================================================
        // (3) the built-in hard-question pack — binds-or-skips, honestly
        // ============================================================================================

        // A model with every shape the pack knows: fact measure, marked date table (+ text attribute),
        // a labelled dimension, an entity key, two plain numerics, and an inactive relationship.
        private static PackShape FullShape()
        {
            var s = new PackShape();
            s.Measures.Add(new PackShape.Meas { Table = "Sales", Name = "Total Sales" });
            s.DateTables.Add("Date");
            s.Columns.Add(new PackShape.Col { Table = "Date", Name = "DateKey", Kind = "date", Key = true });
            s.Columns.Add(new PackShape.Col { Table = "Date", Name = "Day Name", Kind = "text" });
            s.Columns.Add(new PackShape.Col { Table = "Product", Name = "ProductId", Kind = "number", Hidden = true });
            s.Columns.Add(new PackShape.Col { Table = "Product", Name = "Category", Kind = "text" });
            s.Columns.Add(new PackShape.Col { Table = "Sales", Name = "ProductId", Kind = "number", Hidden = true });
            s.Columns.Add(new PackShape.Col { Table = "Sales", Name = "OrderDate", Kind = "date" });
            s.Columns.Add(new PackShape.Col { Table = "Sales", Name = "ShipDate", Kind = "date" });
            s.Columns.Add(new PackShape.Col { Table = "Sales", Name = "Unit Price", Kind = "number" });
            s.Columns.Add(new PackShape.Col { Table = "Sales", Name = "Quantity", Kind = "number" });
            s.Relationships.Add(new PackShape.Rel { FromTable = "Sales", FromColumn = "OrderDate", ToTable = "Date", ToColumn = "DateKey", Active = true });
            s.Relationships.Add(new PackShape.Rel { FromTable = "Sales", FromColumn = "ShipDate", ToTable = "Date", ToColumn = "DateKey", Active = false });
            s.Relationships.Add(new PackShape.Rel { FromTable = "Sales", FromColumn = "ProductId", ToTable = "Product", ToColumn = "ProductId", Active = true });
            return s;
        }

        [Fact]
        public void Pack_binds_every_template_on_a_full_shape_with_no_leftover_placeholders()
        {
            var (candidates, skips) = HardQuestionPack.Bind(FullShape(), null);
            Assert.Empty(skips);
            Assert.Equal(HardQuestionPack.Templates().Length, candidates.Count);
            foreach (var c in candidates)
            {
                Assert.DoesNotContain("{", c.Question);          // a leftover token = a question referencing nothing
                Assert.DoesNotContain("{", c.Query);
                Assert.Contains("measure:Sales/Total Sales", c.Targets);   // the binding is disclosed
                Assert.Equal("value", c.SuggestedTier);
                Assert.Null(c.AltPhrasings.FirstOrDefault());
                Assert.False(string.IsNullOrWhiteSpace(c.Note)); // the trap is explained in plain words
            }
            // Spot-check the bindings landed on real objects, not invented ones.
            var rank = candidates.Single(c => c.Id == "rank-ties");
            Assert.Contains("'Product'[Category]", rank.Query);
            Assert.Contains("[Total Sales]", rank.Query);
            var inactive = candidates.Single(c => c.Id == "inactive-relationship-total");
            Assert.Contains("USERELATIONSHIP('Sales'[ShipDate], 'Date'[DateKey])", inactive.Query);
            var weighted = candidates.Single(c => c.Id == "weighted-average");
            Assert.Contains("'Sales'[Quantity]", weighted.Query);
            Assert.DoesNotContain("ProductId", weighted.Query);  // id-ish relationship columns are never averaged
        }

        [Fact]
        public void Pack_skips_honestly_naming_the_missing_shape()
        {
            // A bare model: one visible measure, nothing else — every template must skip WITH a reason.
            var bare = new PackShape();
            bare.Measures.Add(new PackShape.Meas { Table = "Sales", Name = "Total Sales" });
            var (candidates, skips) = HardQuestionPack.Bind(bare, null);
            Assert.Empty(candidates);
            Assert.Equal(HardQuestionPack.Templates().Length, skips.Count);
            Assert.All(skips, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
            Assert.Contains(skips, s => s.Id == "rank-ties" && s.Reason.Contains("dimension"));
            Assert.Contains(skips, s => s.Id == "prior-full-month" && s.Reason.Contains("date table"));
            Assert.Contains(skips, s => s.Id == "inactive-relationship-total" && s.Reason.Contains("inactive relationship"));
            Assert.Contains(skips, s => s.Id == "weighted-average" && s.Reason.Contains("numeric"));

            // No measures at all: still skips (never throws) — measure-needing templates teach the create-one
            // path, date-needing ones name the missing date table (each miss names ITS shape, not a generic one).
            var (none, allSkips) = HardQuestionPack.Bind(new PackShape(), null);
            Assert.Empty(none);
            Assert.All(allSkips, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
            Assert.Contains(allSkips, s => s.Reason.Contains("no visible measure"));
            Assert.Contains(allSkips, s => s.Reason.Contains("mark_date_table"));
        }

        [Fact]
        public void Pack_measure_arg_binds_the_named_measure_or_teaches()
        {
            var shape = FullShape();
            shape.Measures.Add(new PackShape.Meas { Table = "Sales", Name = "Net Sales", Hidden = true });

            // A named measure wins (even a hidden one — naming it is intent).
            var (candidates, _) = HardQuestionPack.Bind(shape, "Net Sales");
            Assert.All(candidates, c => Assert.Contains("measure:Sales/Net Sales", c.Targets));

            // A wrong name teaches the discovery op instead of guessing.
            var ex = Assert.Throws<InvalidOperationException>(() => HardQuestionPack.Bind(shape, "No Such Measure"));
            Assert.Contains("list_measures", ex.Message);
        }

        [Fact]
        public void Pack_templates_are_patterns_not_bench_data()
        {
            var templates = HardQuestionPack.Templates();
            Assert.InRange(templates.Length, 10, 15);            // "~10-15 model-agnostic templates" is the contract
            var knownNeeds = new[] { "measure", "dimension", "dateTable", "dateAttr", "entityKey", "twoNumeric", "inactiveRel" };
            foreach (var t in templates)
            {
                Assert.False(string.IsNullOrWhiteSpace(t.Id));
                Assert.False(string.IsNullOrWhiteSpace(t.Family));
                Assert.False(string.IsNullOrWhiteSpace(t.Question));
                Assert.False(string.IsNullOrWhiteSpace(t.Query));
                Assert.False(string.IsNullOrWhiteSpace(t.Trap));
                Assert.All(t.Needs, n => Assert.Contains(n, knownNeeds));
                // Patterns, not our bench: no dataset names, no product names, no gold values.
                foreach (var text in new[] { t.Question, t.Query, t.Trap })
                {
                    Assert.DoesNotContain("contoso", text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("power bi", text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("copilot", text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("microsoft", text, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // ============================================================================================
        // (2+3 op level) list_interview_seeds — the dual-drive read over both lanes
        // ============================================================================================

        [Fact]
        public async Task Seeds_op_without_a_model_teaches_instead_of_throwing()
        {
            using var e = new LocalEngine(new SessionManager(), new Fake(true));
            var r = await e.ListInterviewSeedsAsync(null, null);
            Assert.Empty(r.Candidates);
            Assert.Contains("open", r.Note, StringComparison.OrdinalIgnoreCase);   // the recovery is named

            var bad = await Assert.ThrowsAsync<InvalidOperationException>(() => e.ListInterviewSeedsAsync("nonsense", null));
            Assert.Contains("verified-answers", bad.Message);
        }

        [Fact]
        public async Task Seeds_op_extracts_verified_answers_binds_the_pack_and_dedups_saved_questions()
        {
            var dir = TempDir("seeds");
            var origHome = Environment.GetEnvironmentVariable("USERPROFILE");
            var sessions = new SessionManager();
            try
            {
                var home = Path.Combine(dir, "home");
                Directory.CreateDirectory(home);
                Environment.SetEnvironmentVariable("USERPROFILE", home);

                // A temp COPY of the vendored model with the Prep-for-AI disk anchors beside it (opening the
                // vendored bim directly would write sidecars into the repo).
                var copy = Path.Combine(dir, "AdventureWorks.bim");
                File.Copy(TestModels.FindBim(), copy);
                File.WriteAllText(Path.Combine(dir, "definition.pbism"), "{ \"settings\": { \"qnaEnabled\": true } }");
                WriteVa(dir, "va-1", @"{ ""name"": ""Total internet sales"",
                    ""triggers"": [""What were total internet sales?"", ""Total internet sales to date""] }");

                var e = new LocalEngine(sessions, new Fake(true), dir);
                await e.OpenAsync(copy);

                var r = await e.ListInterviewSeedsAsync(null, null);
                Assert.Equal(1, r.VerifiedAnswersFound);
                var va = r.Candidates.Single(c => c.Source == "verified-answer");
                Assert.Equal("What were total internet sales?", va.Question);
                // The pack lane bound-or-skipped, never half-bound: no leftover token anywhere, and the sum of
                // both piles accounts for every template.
                var packCands = r.Candidates.Where(c => c.Source == "hard-pack").ToList();
                var packSkips = r.Skipped.Where(s => s.Source == "hard-pack").ToList();
                Assert.Equal(r.HardPackTemplates, packCands.Count + packSkips.Count);
                Assert.All(packCands, c => { Assert.DoesNotContain("{", c.Question); Assert.DoesNotContain("{", c.Query); });
                Assert.All(packSkips, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
                Assert.Contains("add_interview_question", r.Note);   // the seed flow is taught on the result

                // Saving the VA candidate (the agent authors the query; the human confirmed the number)…
                await e.AddInterviewQuestionAsync(va.Question, "value",
                    "EVALUATE ROW(\"v\", [Internet Total Sales])", null, null, null, null,
                    "29358677.22", null, false, null, "verified-answer", "project", "human");

                // …turns it into an honest SKIP on the next listing (never a silent duplicate).
                var again = await e.ListInterviewSeedsAsync("verified-answers", null);
                Assert.DoesNotContain(again.Candidates, c => c.Source == "verified-answer");
                Assert.Contains(again.Skipped, s => s.Source == "verified-answer" && s.Reason.Contains("already saved"));
            }
            finally
            {
                sessions.Dispose();
                Environment.SetEnvironmentVariable("USERPROFILE", origHome);
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public async Task Guidance_flow_seed_runs_inline_with_no_oracle_instead_of_being_refused()
        {
            // The flow list_interview_seeds ADVERTISES, followed verbatim: take a candidate, run_interview it
            // inline with NO oracle. The validator must not refuse its own guidance — the run executes and
            // grades Unverified (never a pass; offline here, and with a live connection the detail shows the
            // computed number to confirm-and-record, pinned at kernel level above). Persisting without an
            // oracle stays refused: a SAVED question must always grade as a regression check.
            var dir = TempDir("flow");
            var origHome = Environment.GetEnvironmentVariable("USERPROFILE");
            var sessions = new SessionManager();
            try
            {
                var home = Path.Combine(dir, "home");
                Directory.CreateDirectory(home);
                Environment.SetEnvironmentVariable("USERPROFILE", home);
                var copy = Path.Combine(dir, "AdventureWorks.bim");
                File.Copy(TestModels.FindBim(), copy);
                File.WriteAllText(Path.Combine(dir, "definition.pbism"), "{}");
                WriteVa(dir, "va-1", @"{ ""triggers"": [""What were total internet sales?""] }");

                var e = new LocalEngine(sessions, new Fake(true), dir);
                await e.OpenAsync(copy);

                var seed = (await e.ListInterviewSeedsAsync(null, null)).Candidates.First();
                var query = seed.Query ?? "EVALUATE ROW(\"v\", [Internet Total Sales])";   // VA seeds carry no query — the agent authors it
                var inline = System.Text.Json.JsonSerializer.Serialize(new { question = seed.Question, tier = "value", query });

                var r = await e.RunInterviewAsync(null, inline, false, null, "agent");     // no oracle — must NOT throw
                Assert.Equal("Unverified", r.Outcome);                                     // honest grade, never a pass
                Assert.False(r.Recorded);                                                  // inline one-offs persist nothing

                // The add path keeps its teeth — and now teaches the run-first flow in its refusal.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    e.AddInterviewQuestionAsync(seed.Question, "value", query, null, null, null, null,
                        null, null, false, null, seed.Source, "project", "human"));
                Assert.Contains("run_interview", ex.Message);
            }
            finally
            {
                sessions.Dispose();
                Environment.SetEnvironmentVariable("USERPROFILE", origHome);
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ============================================================================================
        // (4) deploy_gate interview advisory — informs, NEVER blocks
        // ============================================================================================

        private static async Task<(LocalEngine engine, SessionManager sessions, string ws, string origHome)> GateModelAsync()
        {
            var ws = TempDir("gate");
            var origHome = Environment.GetEnvironmentVariable("USERPROFILE");
            var home = Path.Combine(ws, "home");
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus"));
            Environment.SetEnvironmentVariable("USERPROFILE", home);
            var sessions = new SessionManager();
            var e = new LocalEngine(sessions, new Fake(true), ws);
            await e.CreateModelAsync("GateModel", 1604);
            sessions.Current.SourcePath = Path.Combine(ws, "GateModel.bim");   // #157-R2: a DURABLE identity so add_interview_question can persist (DirFor == ws/.semanticus)
            var t = await e.CreateTableAsync("Sales", "human");
            await e.CreateMeasureAsync(t, "Total Sales", "1", "human");   // visible + undescribed ⇒ a RED gate
            return (e, sessions, ws, origHome);
        }

        private static void Cleanup(SessionManager sessions, string ws, string origHome)
        {
            sessions.Dispose();
            Environment.SetEnvironmentVariable("USERPROFILE", origHome);
            try { Directory.Delete(ws, true); } catch { }
        }

        [Fact]
        public async Task Deploy_gate_without_a_pack_keeps_the_exact_existing_shape()
        {
            var (e, sessions, ws, home) = await GateModelAsync();
            try
            {
                var gate = await e.DeployGateAsync(null);
                Assert.Null(gate.Interview);   // no saved questions ⇒ nothing added to the result
            }
            finally { Cleanup(sessions, ws, home); }
        }

        [Fact]
        public async Task Deploy_gate_never_asked_questions_are_not_counted_as_changed()
        {
            var (e, sessions, ws, home) = await GateModelAsync();
            try
            {
                // One saved question with NO recorded outcome: "changed since last asked" must stay empty —
                // a delta needs a recorded before; a first-ever grading is its own honest bucket.
                await e.AddInterviewQuestionAsync("What were total sales in 2024?", "value",
                    "EVALUATE ROW(\"v\", [Total Sales])", null, null, null, null, "100", null, false, null, "user", "project", "human");

                var adv = (await e.DeployGateAsync(null)).Interview;
                Assert.NotNull(adv);
                Assert.Empty(adv.Changes);                     // zero changed-deltas — nothing was asked before
                Assert.Equal(1, adv.NeverAsked);               // …the first-ever grading is disclosed separately
                Assert.Equal(1, adv.Unverified);               // and still counted in the outcome tallies (offline ceiling)
                Assert.Contains("first time", adv.Note);
            }
            finally { Cleanup(sessions, ws, home); }
        }

        [Fact]
        public async Task Deploy_gate_interview_is_advisory_never_blocks_and_reports_deltas()
        {
            var (e, sessions, ws, home) = await GateModelAsync();
            try
            {
                // The verdict BEFORE any pack exists is the baseline the advisory must never move.
                var before = await e.DeployGateAsync(null);

                var vq = await e.AddInterviewQuestionAsync("What were total sales in 2024?", "value",
                    "EVALUATE ROW(\"v\", [Total Sales])", null, null, null, null, "100", null, false, null, "user", "project", "human");
                await e.AddInterviewQuestionAsync("What was churn last quarter?", "refusal", null, null, null,
                    null, null, null, null, true, null, "user", "project", "human");

                // Simulate a previously-recorded LIVE outcome, so the offline replay produces a real delta.
                var file = Path.Combine(ws, ".semanticus", "interview", "questions.jsonl");
                File.AppendAllText(file, "{\"op\":\"record-run\",\"id\":\"" + vq.Id + "\",\"when\":\"2026-07-01T00:00:00Z\",\"origin\":\"agent\",\"outcome\":\"Correct\",\"detail\":\"the answer 100 matches the trusted value.\"}\n");

                var gate = await e.DeployGateAsync(null);

                // ADVISORY: the verdict and blockers are byte-identical to the packless gate — it can only inform.
                Assert.Equal(before.Pass, gate.Pass);
                Assert.Equal(before.Blockers, gate.Blockers);
                Assert.DoesNotContain(gate.Blockers, b => b.Contains("interview", StringComparison.OrdinalIgnoreCase));

                var adv = gate.Interview;
                Assert.NotNull(adv);
                Assert.Equal(2, adv.Questions);
                Assert.Equal(1, adv.Replayed);                 // the value question
                Assert.Equal(1, adv.NotReplayable);            // refusal tier is graded in chat, exactly like the card
                Assert.Equal(1, adv.Unverified);               // offline ⇒ the honest ceiling, never a fabricated pass
                Assert.Equal(0, adv.Wrong);
                Assert.Equal(0, adv.NeverAsked);               // the value question HAS a recorded before — no first-time bucket
                Assert.Contains("never blocks", adv.Note);

                // The delta: last recorded Correct → now Unverified (offline), with the evidence line attached.
                var delta = Assert.Single(adv.Changes);
                Assert.Equal(vq.Id, delta.QuestionId);
                Assert.Equal("Correct", delta.Before);
                Assert.Equal("Unverified", delta.After);
                Assert.Contains("offline", delta.Detail);

                // Offline outcomes are NOT recorded: the last real evidence still stands on the saved question.
                var listed = await e.ListInterviewQuestionsAsync("project");
                Assert.Equal("Correct", listed.Questions.Single(q => q.Id == vq.Id).LastRun?.Outcome);
            }
            finally { Cleanup(sessions, ws, home); }
        }
    }
}
