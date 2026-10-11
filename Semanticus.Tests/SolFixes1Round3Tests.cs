// review-fix tests for the verifier's round 2 on m11/sol-fixes-1 (4d81c149): the cases around its five defect tests that
// those do not pin, and the older kept-work harm the round names. Each names the defect it guards. The Jev oracle answers
// the exact request the engine built, through the real recorded oracle and answer reader, as the verifier's tests do.

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
using Xunit;

namespace Semanticus.Tests
{
    public sealed class SolFixes1Round3Tests
    {
        private const string Placeholder = "_Add what people and the AI Assistant should know._";

        private static async Task<string> SetGotchaAsync(LocalEngine engine, string gotcha)
        {
            var starter = (await engine.GetPrimerAsync()).Markdown.Replace("\r\n", "\n");
            var saved = await McpTools.SetModelPrimer(engine, starter.Replace("## Gotchas\n\n" + Placeholder, "## Gotchas\n\n" + gotcha));
            return saved.FilePath;
        }

        private static async Task InFoldersAsync(Func<string, Task> body)
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-sf1-r3", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "a"));
            Directory.CreateDirectory(Path.Combine(dir, "b"));
            File.Copy(TestModels.FindBim(), Path.Combine(dir, "a", "Model.bim"));
            try { await body(dir); }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---- VR1: after a Save As, a Primer step acts only on this session's model's own Primer ------------------------

        /// <summary>VR1 (a target with its own Primer). Save As onto a model that already has its own Primer: the carry keeps
        /// that Primer (it never overwrites), so the Primer the session saved did not come along. Undo must then write
        /// neither the old model's Primer (another model's file now) nor the target's own, and must not be refused: the
        /// step lets its file go. At 4d81c149 the step kept naming the old model's Primer and Undo deleted it.</summary>
        [Fact]
        public Task Save_as_onto_a_model_with_its_own_Primer_leaves_both_Primers_and_undo_goes_on() => InFoldersAsync(async dir =>
        {
            var target = Path.Combine(dir, "b", "Model2.bim");
            File.Copy(TestModels.FindBim(), target);
            string theirs;
            using (var otherSessions = new SessionManager())
            using (var other = new LocalEngine(otherSessions, TestEntitlements.Pro))
            {
                await other.OpenAsync(target);
                theirs = await SetGotchaAsync(other, "Model2 has its own notes.");
            }
            var theirText = File.ReadAllText(theirs);

            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(Path.Combine(dir, "a", "Model.bim"));
            var table = (await engine.GetModelObjectsAsync()).Tables.First().Name;
            await engine.CreateMeasureAsync("table:" + table, "Made Before The Primer", "1", "human");
            var mine = await SetGotchaAsync(engine, "Fiscal year starts in July.");
            var myText = File.ReadAllText(mine);
            await engine.SaveAsync(target, "bim", false, "human");

            for (var i = 0; i < 4 && (await engine.ListMeasuresAsync()).Any(m => m.Name == "Made Before The Primer"); i++)
                await engine.UndoAsync("human");
            Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == "Made Before The Primer");
            Assert.True(File.Exists(mine), "Undo deleted the Primer of the model the session left: " + mine);
            Assert.Equal(myText, File.ReadAllText(mine));
            Assert.Equal(theirText, File.ReadAllText(theirs));
        });

        /// <summary>VR1 (the redo side). Undo a Primer save (the file goes), Save As into another folder, Redo: the Primer comes
        /// back for the model the session now is, and the old model's folder gets nothing back. At 4d81c149 nothing was moved,
        /// so the step still named the old folder and Redo wrote the Primer there.</summary>
        [Fact]
        public Task Redo_after_save_as_puts_the_Primer_back_for_the_saved_model_only() => InFoldersAsync(async dir =>
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(Path.Combine(dir, "a", "Model.bim"));
            var mine = await SetGotchaAsync(engine, "Fiscal year starts in July.");
            var myText = File.ReadAllText(mine);
            await engine.UndoAsync("human");
            Assert.False(File.Exists(mine));
            await engine.SaveAsync(Path.Combine(dir, "b", "Model2.bim"), "bim", false, "human");

            await engine.RedoAsync("human");
            Assert.False(File.Exists(mine), "Redo wrote the Primer into the folder of the model the session left: " + mine);
            var primers = Path.Combine(dir, "b", Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(mine))), "primers");
            var carried = Directory.Exists(primers) ? Directory.GetFiles(primers, "*.md") : Array.Empty<string>();
            Assert.Equal(myText, File.ReadAllText(Assert.Single(carried)));
        });

        // ---- the Plan's kept words ----------------------------------------------------------------------------------

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
                return new RecordedDecisionOracle(sb.ToString(), "jev-sf1r3").AskAsync(request, ct);
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

        /// <summary>The longest words the cap lets through, cut into as many clauses as it holds, every one of them naming a
        /// step of the menu so the word match has real work on each.</summary>
        private static string LongestWords()
        {
            var sb = new StringBuilder();
            for (var i = 0; ; i++)
            {
                var next = (i == 0 ? "" : ", ") + "hide column " + i.ToString(CultureInfo.InvariantCulture);
                if (sb.Length + next.Length > PlanTasks.MaxFullTextChars) return sb.ToString();
                sb.Append(next);
            }
        }

        /// <summary>The test gap on D3's bound, and VR2's fix: picking a step from the menu lets go of every kept word that
        /// names it, however far down, and stays quick on a draft at the cap. Here thousands of kept clauses all name
        /// Hide an object: one pick resolves them all. The time bound is loose on purpose; the count test guards the cost.</summary>
        [Fact]
        public async Task A_step_picked_from_the_menu_lets_go_of_every_kept_word_it_names_quickly_at_the_cap()
        {
            var (f, jev) = await ProAsync("R3MenuCap.bim", (w, q) => null);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync(LongestWords(), null, null, null, null, "human");
                Assert.Contains(v.Notes, n => n.StartsWith("Not read yet:", StringComparison.Ordinal));
                var sw = Stopwatch.StartNew();
                v = await f.Engine.EditDraftAsync(v.DraftId, "add", null, null, null, "hide", "human");
                var ms = sw.ElapsedMilliseconds;
                Assert.DoesNotContain(v.Notes, n => n.StartsWith("Not read yet:", StringComparison.Ordinal));
                // The guard on the cost is the count test below (A_step_picked_from_the_menu_reads_only_the_kept_words_that_name_it),
                // which no machine's load can move. This bound only catches a pick that has gone badly wrong: it took 5.6 s
                // of an 8 s bound in Debug on a busy host (verifier on m11/sol-fixes-1 round 3), so it is generous.
                Assert.True(ms < 30000, "adding a step from the menu took " + ms + " ms");
            }
        }

        /// <summary>The guard on the menu add's cost, counted rather than timed (a quiet machine runs even the slow way under
        /// any sane time bound): of hundreds of kept words, a pick from the menu reads against the whole list only those
        /// that name the picked step, and lets exactly those go, however far down they are (VR2).</summary>
        [Fact]
        public async Task A_step_picked_from_the_menu_reads_only_the_kept_words_that_name_it()
        {
            var (f, _) = await ProAsync("R3MenuCount.bim", (w, q) => null);
            using (f)
            {
                var clauses = Enumerable.Range(1, 400).Select(i => i == 30 || i == 300 ? "hide the keys " + i.ToString(CultureInfo.InvariantCulture)
                                                                                    : "zzqx wibble " + i.ToString(CultureInfo.InvariantCulture));
                var v = await f.Engine.ComposeTaskAsync(string.Join(", ", clauses), null, null, null, null, "human");
                int KeptBeyondNamed(DraftView view) => int.Parse(System.Text.RegularExpressions.Regex.Match(
                    view.Notes.Single(n => n.StartsWith("Not read yet:", StringComparison.Ordinal)), @"and (\d+) more").Groups[1].Value, CultureInfo.InvariantCulture);
                var before = KeptBeyondNamed(v);
                var box = new System.Runtime.CompilerServices.StrongBox<int>();
                DraftMenu.FullReadsForTest.Value = box;
                try { v = await f.Engine.EditDraftAsync(v.DraftId, "add", null, null, null, "hide", "human"); }
                finally { DraftMenu.FullReadsForTest.Value = null; }
                Assert.Equal(2, box.Value);
                Assert.Equal(before - 2, KeptBeyondNamed(v));   // the two kept words that name Hide an object, and only they, let go
            }
        }

        /// <summary>VR3 on the first draft: with no ask able to leave (no permission), words past the most one draft reads
        /// must not be said as "only the first 12 were read" with a promise that Add more words adds the rest: nothing was
        /// read, and the next Add more words asks no one either. The note says why.</summary>
        [Fact]
        public async Task A_first_draft_no_one_could_read_never_says_its_first_words_were_read()
        {
            var (f, jev) = await ProAsync("R3NoConsent.bim", (w, q) => ("yes", 0.99), consent: false);
            using (f)
            {
                var words = string.Join(", ", Enumerable.Range(1, 14).Select(i => "zzqx wibble " + i.ToString(CultureInfo.InvariantCulture)));
                var v = await f.Engine.ComposeTaskAsync(words, null, null, null, null, "human");
                Assert.Empty(jev.Asked);
                Assert.DoesNotContain("were read", v.Sentence ?? "", StringComparison.Ordinal);
                Assert.DoesNotContain("with Add more words", v.Sentence ?? "", StringComparison.Ordinal);
                Assert.Contains(v.Notes, n => n.Contains("you have not given permission", StringComparison.Ordinal));
            }
        }

        /// <summary>VR4's other half: the reason stays while it is still true. Without permission the note keeps saying why
        /// on a later read, and never promises that Add more words asks Jev.</summary>
        [Fact]
        public async Task The_note_keeps_saying_why_while_permission_is_still_missing()
        {
            var (f, _) = await ProAsync("R3StillNo.bim", (w, q) => ("yes", 0.99), consent: false);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("hide the keys, then save the model", null, null, null, null, "human");
                v = await f.Engine.GetDraftAsync(v.DraftId);
                var note = Assert.Single(v.Notes, n => n.StartsWith("Not read yet:", StringComparison.Ordinal));
                Assert.Contains("you have not given permission", note, StringComparison.Ordinal);
                Assert.DoesNotContain("Add more words asks Jev", note, StringComparison.Ordinal);
            }
        }

        /// <summary>VR4 in full: once permission is given the note promises what is now true, that Add more words asks Jev.</summary>
        [Fact]
        public async Task Once_permission_is_given_the_note_says_add_more_words_asks_jev()
        {
            var (f, _) = await ProAsync("R3NowYes.bim", (w, q) => ("yes", 0.99), consent: false);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("hide the keys, then save the model", null, null, null, null, "human");
                await f.ConsentAsync();
                v = await f.Engine.GetDraftAsync(v.DraftId);
                var note = Assert.Single(v.Notes, n => n.StartsWith("Not read yet:", StringComparison.Ordinal));
                Assert.Contains("Add more words asks Jev", note, StringComparison.Ordinal);
                Assert.DoesNotContain("Jev was not asked", note, StringComparison.Ordinal);
            }
        }

        /// <summary>VR4 the other way round: words kept past the limit while permission stood carry the promise that Add more
        /// words asks Jev. Once permission is withdrawn that promise is false (the next Add more words asks no one), so the
        /// note says why instead, read when the draft is shown.</summary>
        [Fact]
        public async Task Once_permission_is_withdrawn_the_note_stops_promising_that_jev_is_asked()
        {
            var (f, _) = await ProAsync("R3NowNo.bim", (w, q) => null);
            using (f)
            {
                var words = string.Join(", ", Enumerable.Range(1, 14).Select(i => "zzqx wibble " + i.ToString(CultureInfo.InvariantCulture)));
                var v = await f.Engine.ComposeTaskAsync(words, null, null, null, null, "human");
                Assert.Contains(v.Notes, n => n.StartsWith("Not read yet:", StringComparison.Ordinal) && n.Contains("Add more words asks Jev", StringComparison.Ordinal));
                await f.Engine.WithdrawConsentAsync("human");
                v = await f.Engine.GetDraftAsync(v.DraftId);
                var note = Assert.Single(v.Notes, n => n.StartsWith("Not read yet:", StringComparison.Ordinal));
                Assert.DoesNotContain("Add more words asks Jev", note, StringComparison.Ordinal);
                Assert.Contains("Jev was not asked", note, StringComparison.Ordinal);
            }
        }

        // ---- the Plan's fallback adds only when the clause asks to add (the coordinator's ruling, 28 Sep) ------------------

        private static Task<DraftView> ComposeOnDoor(LocalEngine engine, string door, string words) =>
            door == "studio" ? new EngineRpcTarget(engine).composeTask(words: words)
                             : McpTools.ComposeTask(engine, words: words);

        private static string[] StepTitles(DraftView v) => v.Steps.Concat(v.After ?? Array.Empty<DraftStepView>()).Select(s => s.Title ?? "").ToArray();

        /// <summary>The completeness verifier's round 3, F1, on this lane: a clause Jev is not sure of took its word match as a
        /// SURE step, so "delete the old measures and the old role" drafted Add a measure and Add a role as kept Plan steps.
        /// The ruling: a step that adds is chosen only when the clause's own action adds, and a clause the cut left with no
        /// action of its own takes the action of the clause before it. Both clauses here delete, so neither adds.</summary>
        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task A_clause_jev_is_not_sure_of_takes_an_adding_step_only_when_it_asks_to_add(string door)
        {
            var (f, _) = await ProAsync("R3AddRule" + door + ".bim", (w, q) => null);
            using (f)
            {
                var v = await ComposeOnDoor(f.Engine, door, "delete the old measures and the old role");
                Assert.DoesNotContain(StepTitles(v), t => t.StartsWith("Add a", StringComparison.Ordinal));

                // The rule takes nothing else away: a clause that asks to add still gets its step. A clause with no verb of its
                // own no longer takes the adding action of the one before it (THE ADDING RULE version 3, a, 29 Sep): that add
                // is lost on purpose, the safe side, and said with its own verb it is drafted.
                v = await ComposeOnDoor(f.Engine, door, "create a measure for margin and a role for managers");
                Assert.Contains(StepTitles(v), t => t == "Add a measure");
                Assert.DoesNotContain(StepTitles(v), t => t == "Add a role");
                v = await ComposeOnDoor(f.Engine, door, "create a measure for margin and create a role for managers");
                Assert.Contains(StepTitles(v), t => t == "Add a measure");
                Assert.Contains(StepTitles(v), t => t == "Add a role");
                v = await ComposeOnDoor(f.Engine, door, "hide the keys");
                Assert.Contains(StepTitles(v), t => t == "Hide an object");
            }
        }

        // ---- Author: a step title typed with a space before a new word -------------------------------------------------

        /// <summary>Author sends a step's title as it is typed, one edit per key, so "Define the measure " comes before
        /// "Define the measure FIRST". A heading keeps no spaces at its ends, so the first of those used to be refused with
        /// "The source spelling cannot represent the requested 'title' value" and took the whole save with it (older than
        /// this lane, seen by the verifier in round 2).</summary>
        [Fact]
        public void A_step_title_typed_through_a_space_saves_as_the_whole_title()
        {
            const string source = "---\nname: title-test\ntitle: Title test\nversion: 1\n---\n## Step 1: Define the measure\nWrite it.\n";
            var opened = WorkflowDocumentPatcher.Apply("title-test", source, "[]");
            Assert.Null(opened.Error);
            var key = opened.Model.Definition.Steps.Single().Key;
            var edits = JsonSerializer.Serialize(new object[]
            {
                new { op = "set_field", target = key, field = "title", expect = "Define the measure", value = "Define the measure " },
                new { op = "set_field", target = key, field = "title", expect = "Define the measure ", value = "Define the measure F" },
                new { op = "set_field", target = key, field = "title", expect = "Define the measure F", value = "Define the measure FIRST" },
            });
            var saved = WorkflowDocumentPatcher.Apply("title-test", source, edits);
            Assert.True(saved.Error == null, saved.ErrorCode + ": " + saved.Error);
            Assert.Contains("## Step 1: Define the measure FIRST\n", saved.Text, StringComparison.Ordinal);

            // Stopping at the space saves the title without it: the heading could never hold it.
            var stopped = WorkflowDocumentPatcher.Apply("title-test", source, JsonSerializer.Serialize(new object[]
            {
                new { op = "set_field", target = key, field = "title", expect = "Define the measure", value = "Define the measure more " },
            }));
            Assert.True(stopped.Error == null, stopped.ErrorCode + ": " + stopped.Error);
            Assert.Contains("## Step 1: Define the measure more\n", stopped.Text, StringComparison.Ordinal);
        }
    }
}
