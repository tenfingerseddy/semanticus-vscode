// verify: the verifier's failing tests for m11/sol-fixes-1 at c4793fbd (Sol's integration fixes, describe-jev round 4
// and the three also-found items). Each test names ONE defect and is LEFT FAILING on purpose: it passes once the defect
// is fixed. The Jev oracle is the same scripted, honest oracle the earlier rounds use: it answers the exact request the
// engine built, through the real recorded oracle and answer reader.

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public sealed class VerifySolFixes1DefectTests
    {
        // ---- the Primer undo guard (Sol's finding 2) ------------------------------------------------------------------

        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private const string Placeholder = "_Add what people and the AI Assistant should know._";
        private static string Fill(string md, string section, string text) =>
            md.Replace("## " + section + "\n\n" + Placeholder, "## " + section + "\n\n" + text);

        private static async Task WithModelAsync(Func<LocalEngine, string, Task> body)
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-verify-sf1", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.Copy(TestModels.FindBim(), Path.Combine(dir, "Model.bim"));
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Pro());
                await engine.OpenAsync(Path.Combine(dir, "Model.bim"));
                await body(engine, dir);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>Undo, one step at a time, until the measure made first is gone. Every step must be allowed: the person
        /// changed nothing on disk, so nothing may lock the timeline.</summary>
        private static async Task AssertUndoneBackToAsync(LocalEngine engine, string measure, int most)
        {
            for (var i = 0; i < most && (await engine.ListMeasuresAsync()).Any(m => m.Name == measure); i++)
                await engine.UndoAsync("human");
            Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == measure);
        }

        /// <summary>V1 (medium). Accept one Primer suggestion, then reject another: both are Semanticus's own writes to the
        /// suggestion list, and the person touches no file. Undo is then refused on both doors, "The Primer's suggestion
        /// list changed on disk after this change", for good: every change made before the accept can never be undone
        /// again in this session. Before the lane Undo worked (and silently put the rejected one back to waiting).</summary>
        [Fact]
        public Task V1_rejecting_a_suggestion_after_accepting_another_does_not_lock_the_undo_timeline() => WithModelAsync(async (engine, dir) =>
        {
            var table = (await engine.GetModelObjectsAsync()).Tables.First().Name;
            await engine.CreateMeasureAsync("table:" + table, "Made Before The Accept", "1", "human");
            await engine.AddInsightAsync("Month-end stock can lag the sales snapshot by one refresh cycle.",
                new[] { "refresh", "primer:Gotchas" }, "post-mortem", "project", true, "agent");
            await engine.AddInsightAsync("Returns are booked against the original order date, not the return date.",
                new[] { "returns", "primer:Gotchas" }, "post-mortem", "project", true, "agent");
            var waiting = (await engine.ListPrimerSuggestionsAsync()).Suggestions;
            Assert.Equal(2, waiting.Length);
            await engine.AcceptPrimerSuggestionAsync(waiting[0].Id, "human");
            await engine.RejectPrimerSuggestionAsync(waiting[1].Id, "human");

            // The first Undo must not be refused, and it must not quietly bring back the rejected one along with the accept.
            await McpTools.UndoChange(engine);
            var now = (await engine.ListPrimerSuggestionsAsync()).Suggestions.Select(s => s.Id).ToArray();
            Assert.False(now.Contains(waiting[0].Id) && now.Contains(waiting[1].Id),
                "One Undo took back the accept AND silently put the rejected suggestion back to waiting.");
            await AssertUndoneBackToAsync(engine, "Made Before The Accept", 4);
        });

        /// <summary>V2 (low-medium). Save the Primer, then save the model under a new name in the same folder: Save As moves
        /// the Primer files to the new name's key (RekeyDiskPrimers). The Primer step on the timeline still names the old
        /// file, now gone, so Undo is refused ("The Primer file changed on disk after this change") for good, and nothing
        /// made before the Primer save can be undone. The person edited nothing. Before the lane Undo went through.</summary>
        [Fact]
        public Task V2_save_as_in_the_same_folder_does_not_lock_the_undo_of_a_Primer_save() => WithModelAsync(async (engine, dir) =>
        {
            var table = (await engine.GetModelObjectsAsync()).Tables.First().Name;
            await engine.CreateMeasureAsync("table:" + table, "Made Before The Primer", "1", "human");
            var starter = (await engine.GetPrimerAsync()).Markdown.Replace("\r\n", "\n");
            await McpTools.SetModelPrimer(engine, Fill(starter, "Gotchas", "Fiscal year starts in July."));
            await engine.SaveAsync(Path.Combine(dir, "Model2.bim"), "bim", false, "human");
            await AssertUndoneBackToAsync(engine, "Made Before The Primer", 4);
        });

        // ---- the Plan's words not read yet (Sol's finding 4, R3-D6b under the coordinator's rule) ---------------------

        private sealed class ScriptedOracle : IDecisionOracle
        {
            private readonly Func<string, string, (string Choice, double P)?> _script;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            public ScriptedOracle(Func<string, string, (string, double)?> script) { _script = script; }
            public bool Available => true;
            public IReadOnlyList<DecisionRequest> Seen { get { lock (_seen) return _seen.Where(r => r.PointName == DecisionRegistry.ComposeDraft).ToList(); } }

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
                return new RecordedDecisionOracle(sb.ToString(), "jev-verify-sf1").AskAsync(request, ct);
            }
        }

        private static (string, double)? Yes(double p) => ("yes", p);
        private static bool About(string words, string fragment) =>
            words != null && words.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Pro, a saved model, Jev answering from the script. No JevDraftsForTest: the committed per-door switches
        /// decide, as in the product. <paramref name="consent"/> false is a Pro person who has not given permission.</summary>
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

        private const string AsksJevAgain = "Add more words asks Jev about";
        private const string KeptUnread = "Jev could not read your words just now";

        /// <summary>V3 (low-medium, false copy). A Pro person who has not given Jev permission types words on the Plan: no
        /// ask is sent, and none will be until they give it. The draft still says "Not read yet: ... Add more words asks
        /// Jev about them again. The list of steps has no step for "save the model", so Add more words is the only way to
        /// add it", and after Add more words "Jev could not read your words just now, so they are kept on the draft and
        /// asked again with your next Add more words". Both are false while permission is not given: the next Add more
        /// words asks no one. The same holds with the drafting point switched off in "what Pro decides". The words must
        /// stay named; only the promise is wrong.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task V3_the_Plan_does_not_promise_Jev_reads_the_words_when_Jev_cannot_be_asked()
        {
            var (f, jev) = await ProAsync("VNoPermission.bim", (w, q) => Yes(0.99), consent: false);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("hide the keys, then save the model", null, null, null, null, "human");
                Assert.Empty(jev.Seen);
                Assert.Contains(v.Notes, n => n.Contains("save the model", StringComparison.Ordinal));   // still named
                Assert.DoesNotContain(v.Notes, n => n.Contains(AsksJevAgain, StringComparison.Ordinal));
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "describe the Sales table", "human");
                Assert.Empty(jev.Seen);
                Assert.DoesNotContain(KeptUnread, v.Sentence);
                Assert.DoesNotContain(v.Notes, n => n.Contains(AsksJevAgain, StringComparison.Ordinal));
            }

            var (g, jev2) = await ProAsync("VPointOff.bim", (w, q) => Yes(0.99));
            using (g)
            {
                await g.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ComposeDraft, false, "human");
                var v = await g.Engine.ComposeTaskAsync("hide the keys, then save the model", null, null, null, null, "human");
                Assert.Empty(jev2.Seen);
                Assert.DoesNotContain(v.Notes, n => n.Contains(AsksJevAgain, StringComparison.Ordinal));
                v = await g.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "describe the Sales table", "human");
                Assert.DoesNotContain(KeptUnread, v.Sentence);
            }
        }

        /// <summary>V7 (low). Sol's finding 4 was "Author shows a 12-step workflow with no warning that an instruction was
        /// omitted". The Workflows door now keeps each clause past 12 as a step to fill in, but a Plan draft whose words
        /// named more than 12 steps, opened in Author (edit_draft open_in_author), still gives Author the drafted steps only:
        /// the clauses the draft names as "Not read yet" are no step in the file, and the call says only "The draft as a
        /// workflow, not saved yet." Either fix passes: the file keeps them as steps, or the call names them.</summary>
        [Fact]
        public async Task V7_opening_a_Plan_draft_in_Author_keeps_the_clauses_it_did_not_read()
        {
            var (f, jev) = await ProAsync("VOpenInAuthor.bim", (w, q) => About(w, "refresh") && q == "step:refresh:include" ? Yes(0.95) : null);
            using (f)
            {
                var words = string.Join(", ", Enumerable.Range(1, 14).Select(i => "refresh Table" + i.ToString(CultureInfo.InvariantCulture)));
                var v = await f.Engine.ComposeTaskAsync(words, null, null, null, null, "human");
                Assert.Contains(v.Notes, n => n.Contains("\"refresh Table14\"", StringComparison.Ordinal));
                v = await f.Engine.EditDraftAsync(v.DraftId, "open_in_author", null, null, null, null, "human");
                Assert.NotNull(v.Document);
                var def = WorkflowParser.Parse(v.Document.Text);
                var kept = def.Steps.Any(s => (s.Title ?? "").Contains("Table14", StringComparison.OrdinalIgnoreCase));
                Assert.True(kept || (v.Sentence ?? "").Contains("refresh Table14", StringComparison.Ordinal),
                    "Author gets " + def.Steps.Length + " steps and no word of the clauses not read: " + v.Sentence);
            }
        }

        /// <summary>V8 (low, copy). Finding 3 made one clause give one step per op in the file, but the sentence still counts
        /// clauses as steps: "refresh Sales with validation", which Jev was sure asks for a refresh and the saved checks,
        /// opens Author on a 2-step workflow under the notice "1 step from your words." (seen live in lane o with
        /// "summarise Unit Price by average": "1 step from your words" beside "Step 2 of 2"). Both doors say it.</summary>
        [Fact]
        public async Task V8_the_describe_sentence_counts_the_steps_the_workflow_has()
        {
            var (f, _) = await ProAsync("VCount.bim", (w, q) => q == "step:refresh:include" || q == "step:run-tests:include" ? Yes(0.95) : null);
            using (f)
            {
                foreach (var d in new[] { await new EngineRpcTarget(f.Engine).describeWorkflowSteps("refresh Sales with validation"),
                                          await McpTools.DescribeWorkflowSteps(f.Engine, "refresh Sales with validation") })
                {
                    Assert.Equal(new[] { "refresh_partition", "run_tests" }, Assert.Single(d.Steps).Ops);
                    var steps = WorkflowParser.Parse(d.Document.Text).Steps.Length;
                    Assert.Equal(2, steps);
                    var lead = System.Text.RegularExpressions.Regex.Match(d.Sentence ?? "", @"^(\d+) steps? from your words");
                    Assert.True(!lead.Success || lead.Groups[1].Value == steps.ToString(CultureInfo.InvariantCulture),
                        "the workflow has " + steps + " steps and the sentence says: " + d.Sentence);
                }
            }
        }

        // ---- size: the limits the lane kept every clause past (Sol's finding 4, and also-found 3's 100,000 cap) --------

        /// <summary>The longest words the cap lets through, cut into as many clauses as they hold: 9,192 of them.</summary>
        private static string LongestWords()
        {
            var sb = new StringBuilder();
            for (var i = 0; ; i++)
            {
                var next = (i == 0 ? "" : ", ") + "step " + i.ToString(CultureInfo.InvariantCulture);
                if (sb.Length + next.Length > PlanTasks.MaxFullTextChars) return sb.ToString();
                sb.Append(next);
            }
        }

        /// <summary>V5 (medium-low, performance). Every clause past the limit is now kept on the Plan draft, and every view of
        /// the draft runs the word match again over each kept clause to say which the list of steps has no step for
        /// (NamesAStepOffTheList). At the 100,000-character cap that is 9,192 clauses: compose_task took 40 s, and EVERY
        /// later get_draft 20.6 s, which is also paid by the Plan's own rows (DraftPlanRows builds each draft's view) and by
        /// the broadcast after each edit. The note names all 9,180 clauses (118,319 characters). Before the lane the same
        /// words drafted from the first 12 and said how many were left. Measured in Debug on a loaded machine; the bound
        /// here is generous.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task V5_a_Plan_draft_at_the_character_cap_still_opens_in_seconds()
        {
            var (f, _) = await ProAsync("VPlanCap.bim", (w, q) => null);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync(LongestWords(), null, null, null, null, "human");
                var sw = Stopwatch.StartNew();
                var again = await f.Engine.GetDraftAsync(v.DraftId);
                var draftMs = sw.ElapsedMilliseconds;
                sw.Restart();
                await f.Engine.GetModelPlanAsync();
                var planMs = sw.ElapsedMilliseconds;
                // 46193fcf took 2.7 s for the same get_draft on the same loaded machine; c4793fbd took 14 s to 20 s.
                Assert.True(draftMs < 8000 && planMs < 8000, $"get_draft {draftMs} ms, get_model_plan {planMs} ms");
                Assert.True(again.Notes.Sum(n => n.Length) < 20_000, "the notes are " + again.Notes.Sum(n => n.Length) + " characters");
            }
        }

        /// <summary>V6 (medium-low). Also-found 3 capped describe_workflow_steps at 100,000 characters because "with every
        /// clause now kept a huge description made a huge workflow". The cap does not bound the workflow: at the cap the
        /// description is 9,192 clauses, and the answer is a 9,192-step workflow (9,180 steps to fill in), a sentence of
        /// 118,486 characters naming every clause not read, a 2 MB workflow file and a 5.8 MB result, which no assistant can
        /// read and which Author then has to open. Before the lane the same words gave 12 steps and a count.</summary>
        [Fact]
        public async Task V6_a_description_at_the_character_cap_gives_an_answer_a_person_and_an_assistant_can_use()
        {
            var (f, _) = await ProAsync("VDescribeCap.bim", (w, q) => null);
            using (f)
            {
                var d = await McpTools.DescribeWorkflowSteps(f.Engine, LongestWords());
                var json = JsonSerializer.Serialize(d).Length;
                Assert.True((d.Sentence ?? "").Length < 2_000, "the sentence is " + (d.Sentence ?? "").Length + " characters");
                Assert.True(json < 1_000_000, "the answer is " + json + " characters, " + d.Steps.Length + " steps");
            }
        }

        /// <summary>V4 (low). Add more words asks the words kept from before FIRST, then the new words, and a draft reads 12
        /// clauses at most. So with 12 or more words kept, the new words are past the limit and not read at all, while the
        /// sentence says "Listed the steps again with your new words. Jev was not sure enough to pick the steps", as if
        /// they had been read. Here the person adds "save the model" to a draft whose words named 30 steps: Jev is never
        /// asked about it, and the draft holds no Save step until a second Add more words. Either fix passes: read the new
        /// words now (their step arrives), or say plainly that they were not read yet.</summary>
        [Fact]
        public async Task V4_add_more_words_reads_the_new_words_when_twelve_or_more_are_kept()
        {
            var (f, jev) = await ProAsync("VStarved.bim", (w, q) => About(w, "save the model") && q == "step:save:include" ? Yes(0.95) : null);
            using (f)
            {
                var words = string.Join(", ", Enumerable.Range(1, 30).Select(i => "zzqx wibble " + i.ToString(CultureInfo.InvariantCulture)));
                var v = await f.Engine.ComposeTaskAsync(words, null, null, null, null, "human");
                var before = jev.Seen.Count;
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "save the model", "human");
                var asked = jev.Seen.Skip(before).Select(r => (string)r.Snapshot["draft_words"]).ToArray();
                if (asked.Contains("save the model"))
                    Assert.Contains(v.Steps.Concat(v.After), s => s.Id == "save");
                else
                {
                    // Not read this time: then the sentence must say so plainly, and must not read as if Jev weighed them.
                    Assert.Contains("\"save the model\"", v.Sentence);
                    Assert.DoesNotContain("Jev was not sure enough to pick the steps", v.Sentence);
                }
            }
        }
    }
}
