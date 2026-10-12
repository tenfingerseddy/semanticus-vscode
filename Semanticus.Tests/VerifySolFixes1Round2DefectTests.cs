// verify: the verifier's failing tests for m11/sol-fixes-1 round 2 (4d81c149, the fix round on D1 to D10). Each test names
// ONE defect the round's own fixes brought in and is LEFT FAILING on purpose: it passes once the defect is fixed, and it
// passes today with the fix that brought it in taken out. The Jev oracle is the same scripted, honest oracle the earlier
// rounds use: it answers the exact request the engine built, through the real recorded oracle and answer reader.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifySolFixes1Round2DefectTests
    {
        // ---- the Primer undo timeline after Save As (D4's rebase) --------------------------------------------------

        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private const string Placeholder = "_Add what people and the AI Assistant should know._";

        /// <summary>VR1 (low-medium, wrong file). D4 makes the Primer steps on the timeline follow the files a Save As moves.
        /// Save the Primer in folder a, Save As into folder b, then Save As back to the first file in folder a: the second
        /// Save As moves nothing (the first model's Primer is still there, under the name it had), so the timeline keeps
        /// naming the copy in folder b. The session is the model in folder a again and shows folder a's Primer. Undo of the
        /// Primer save then deletes the Primer of the model saved in folder b, and the session still shows the Primer the
        /// History says was undone. Before D4 the step named folder a's file all along, and this Undo was right.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR1_after_save_as_to_another_folder_and_back_undo_takes_back_the_primer_the_session_shows()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-verify-sf1r2", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "a"));
            Directory.CreateDirectory(Path.Combine(dir, "b"));
            File.Copy(TestModels.FindBim(), Path.Combine(dir, "a", "Model.bim"));
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Pro());
                await engine.OpenAsync(Path.Combine(dir, "a", "Model.bim"));
                var starter = (await engine.GetPrimerAsync()).Markdown.Replace("\r\n", "\n");
                await McpTools.SetModelPrimer(engine, starter.Replace("## Gotchas\n\n" + Placeholder, "## Gotchas\n\nFiscal year starts in July."));
                await engine.SaveAsync(Path.Combine(dir, "b", "Model2.bim"), "bim", false, "human");
                var inB = Directory.GetFiles(Path.Combine(dir, "b", ".semanticus", "primers"), "*.md").Single();
                await engine.SaveAsync(Path.Combine(dir, "a", "Model.bim"), "bim", false, "human");
                var shown = await engine.GetPrimerAsync();
                Assert.True(shown.Exists && shown.Markdown.Contains("Fiscal year starts in July.", StringComparison.Ordinal));

                await engine.UndoAsync("human");
                var after = await engine.GetPrimerAsync();
                Assert.True(File.Exists(inB), "Undo deleted the Primer of the model saved in folder b: " + inB);
                Assert.False(after.Exists && after.Markdown.Contains("Fiscal year starts in July.", StringComparison.Ordinal),
                    "The History says the Primer save was undone, and the session still shows that Primer");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>VR1b (the lock VR1 leads to; D4). Same round trip: after it the timeline names the Primer of the model saved
        /// in folder b. Someone then saves that other model's Primer (another window, another person): this session's Primer is
        /// untouched, yet its Undo is refused for good, "The Primer file changed on disk after this change", and nothing made
        /// before the Primer save can be undone again. Before D4 the step named folder a's file and this Undo went through.</summary>
        [Fact]
        public async Task VR1b_an_edit_to_another_models_primer_does_not_lock_this_sessions_undo()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-verify-sf1r2", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "a"));
            Directory.CreateDirectory(Path.Combine(dir, "b"));
            File.Copy(TestModels.FindBim(), Path.Combine(dir, "a", "Model.bim"));
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Pro());
                await engine.OpenAsync(Path.Combine(dir, "a", "Model.bim"));
                var table = (await engine.GetModelObjectsAsync()).Tables.First().Name;
                await engine.CreateMeasureAsync("table:" + table, "Made Before The Primer", "1", "human");
                var starter = (await engine.GetPrimerAsync()).Markdown.Replace("\r\n", "\n");
                await McpTools.SetModelPrimer(engine, starter.Replace("## Gotchas\n\n" + Placeholder, "## Gotchas\n\nFiscal year starts in July."));
                await engine.SaveAsync(Path.Combine(dir, "b", "Model2.bim"), "bim", false, "human");
                var inB = Directory.GetFiles(Path.Combine(dir, "b", ".semanticus", "primers"), "*.md").Single();
                await engine.SaveAsync(Path.Combine(dir, "a", "Model.bim"), "bim", false, "human");
                File.AppendAllText(inB, "\nNotes for Model2, saved in another window.\n");

                for (var i = 0; i < 4 && (await engine.ListMeasuresAsync()).Any(m => m.Name == "Made Before The Primer"); i++)
                    await engine.UndoAsync("human");   // throws "Nothing was undone. The Primer file changed on disk ..." at head
                Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == "Made Before The Primer");
                Assert.Contains("Notes for Model2", File.ReadAllText(inB));   // the other model's edit is left alone
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---- the Plan's kept words (D3's bound, D5's reason, D8's new sentence) --------------------------------------

        private sealed class ScriptedOracle : IDecisionOracle
        {
            private readonly Func<string, string, (string Choice, double P)?> _script;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            public ScriptedOracle(Func<string, string, (string, double)?> script) { _script = script; }
            public bool Available => true;
            public IReadOnlyList<string> Asked { get { lock (_seen) return _seen.Where(r => r.PointName == DecisionRegistry.ComposeDraft).Select(r => (string)r.Snapshot["draft_words"]).ToList(); } }

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (_seen) _seen.Add(request);
                if (request.PointName != DecisionRegistry.ComposeDraft)
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "this test answers compose.draft only"));
                var words = request.Snapshot.TryGetValue("draft_words", out var w) ? w as string : null;
                var sb = new StringBuilder("{\"entries\":[{\"digest\":").Append(JsonSerializer.Serialize(request.Digest))
                    .Append(",\"point\":").Append(JsonSerializer.Serialize(request.PointName))
                    .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":30},\"answers\":{");
                var first = true;
                foreach (var q in request.Questions)
                {
                    var a = _script(words, q.Name);
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(':');
                    if (q.Kind == DecisionQuestionKind.Noul)
                        sb.Append("{\"type\":\"noul\",\"noul\":").Append((a?.P ?? 0.02).ToString(CultureInfo.InvariantCulture)).Append('}');
                    else
                    {
                        var (choice, confidence) = a ?? (q.Name.EndsWith(":wanted", StringComparison.Ordinal) ? ("yes", 0.95) : ("none", 0.95));   // join part 2: the last question fails closed, so a script answers it yes
                        sb.Append("{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                          .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append('}');
                    }
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-verify-sf1r2").AskAsync(request, ct);
            }
        }

        private static async Task<(DecisionFixture F, ScriptedOracle Jev)> ProAsync(string file, Func<string, string, (string, double)?> script, bool consent = true)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync(file);
            await f.SaveAsAsync(file);
            if (consent) await f.ConsentAsync();
            var jev = new ScriptedOracle(script);
            f.Engine.UseDecisionsForTest(new DecisionService(jev, new DecisionBudget()));
            return (f, jev);
        }

        private static string Wibbles(int from, int to) =>
            string.Join(", ", Enumerable.Range(from, to - from + 1).Select(i => "zzqx wibble " + i.ToString(CultureInfo.InvariantCulture)));

        /// <summary>VR2 (low). R3-D6b's rule (the coordinator's, 27 Sep): words the person resolved by picking their step by
        /// hand count as read. D3 bounded that match to the first 12 kept words, so "hide the keys", kept 13th, stays kept
        /// after the person adds Hide an object from the menu. A later Add more words asks Jev about it again (a paid ask for
        /// words already resolved) and, Jev being sure, adds a second Hide an object. Before D3 the hand add let the words go.
        /// Seen with the menu's own id "hide".</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR2_words_the_person_resolved_by_picking_their_step_are_not_asked_again_however_far_down_they_are()
        {
            var (f, jev) = await ProAsync("VR2Hand.bim", (w, q) =>
                w != null && w.IndexOf("hide the keys", StringComparison.OrdinalIgnoreCase) >= 0 && q == "step:hide:include" ? ("yes", 0.95) : null);
            using (f)
            {
                var clauses = Enumerable.Range(1, 30).Select(i => i == 26 ? "hide the keys" : "zzqx wibble " + i.ToString(CultureInfo.InvariantCulture));
                var v = await f.Engine.ComposeTaskAsync(string.Join(", ", clauses), null, null, null, null, "human");
                Assert.DoesNotContain("hide the keys", jev.Asked);   // past the first 12: kept, not read
                v = await f.Engine.EditDraftAsync(v.DraftId, "add", null, null, null, "hide", "human");
                for (var round = 0; round < 3; round++)
                    v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "qqzv flonk " + round.ToString(CultureInfo.InvariantCulture), "human");
                var hides = v.Steps.Concat(v.After).Count(s => (s.Title ?? "").StartsWith("Hide", StringComparison.Ordinal));
                Assert.DoesNotContain("hide the keys", jev.Asked);
                Assert.Equal(1, hides);
            }
        }

        /// <summary>VR3 (low, false copy; D5's rule on D8's new sentence). While no ask can leave (here a Pro person has not
        /// given permission), Add more words says "Jev was not asked: ... Your words are kept on the draft." and then, when
        /// earlier words wait for room, "3 of your earlier words are not read yet. The next Add more words reads more of
        /// them." The next Add more words asks no one either, so that promise is false, the one D5 removed from the note
        /// and from WordsKeptUnread. Seen live in lane o (u1-04).</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR3_the_waiting_words_sentence_promises_nothing_while_Jev_cannot_be_asked()
        {
            var (f, jev) = await ProAsync("VR3NoConsent.bim", (w, q) => ("yes", 0.99), consent: false);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync(Wibbles(1, 13), null, null, null, null, "human");
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "qqzv flonk 1, qqzv flonk 2", "human");
                Assert.Empty(jev.Asked);
                Assert.Contains("Jev was not asked", v.Sentence ?? "", StringComparison.Ordinal);
                Assert.DoesNotContain("The next Add more words reads", v.Sentence ?? "", StringComparison.Ordinal);
            }
        }

        /// <summary>VR4 (low, false copy; D5). The reason kept on the draft is a sentence in the present: "Jev was not asked:
        /// you have not given permission to send this model's details for advice." It stays on the draft's note after the
        /// person gives permission, until the next Add more words: the draft tells them they have not done what they just
        /// did. Seen live in lane o (u2-05, after "OK for this model").</summary>
        [Fact]
        public async Task VR4_the_note_stops_saying_permission_is_missing_once_it_is_given()
        {
            var (f, _) = await ProAsync("VR4Stale.bim", (w, q) => ("yes", 0.99), consent: false);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("hide the keys, then save the model", null, null, null, null, "human");
                Assert.Contains(v.Notes, n => n.Contains("you have not given permission", StringComparison.Ordinal));
                await f.ConsentAsync();
                v = await f.Engine.GetDraftAsync(v.DraftId);
                Assert.DoesNotContain(v.Notes, n => n.Contains("you have not given permission", StringComparison.Ordinal));
            }
        }
    }
}
