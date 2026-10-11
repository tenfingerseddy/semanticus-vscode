// m11/completeness round 4, the verifier's measure (F1: an add is drafted only for words that ask to add). The labelled
// phrasings are the builder's 83 (tools/jev-replay/compose/take-away-labels.json: the round 3 verifier's 32 take-aways
// and 21 not-deletes, and the builder's 30 new) and the verifier's 20 new (tools/jev-replay/compose/verify-r4-labels.json,
// committed before any run). Each phrasing runs on the three doors that read words: Workflows (describe_workflow_steps),
// the Plan (compose_task from words) and Add more words (edit_draft refine, after the words "Hide the Amount column."),
// on the Studio door (EngineRpcTarget) and the agent door (McpTools), with Jev off (the null path), with a Jev that is sure
// of every template that adds (the worst Jev can do), and against the Jev service (JEV_REPLAY_LIVE=1 only, the agent
// door, so CI never calls it). The one thing held is the harmful direction: no step or menu match that adds for words
// that ask for no add (a wrong pick among adds, for words that do ask to add, is recorded, not held). VC4_OUT, when set to
// a folder outside the repository, gets every row as JSON lines.
//
// Also here: the defects the verifier's attacks found at f5a33d29 (VC4-D1 to D5, left failing on purpose), and the
// coordinator's adding rule version 2 cases for the next round (tools/jev-replay/compose/adding-rule-v2-cases.json).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifyCompletenessRound4Tests
    {
        private sealed class Label
        {
            public string Id, Set, Kind, Words;
            /// <summary>Per clause: the op it must draft, or null for noAdd.</summary>
            public string[] Ops;
        }

        private enum Mode { Off, Sure, Live }

        /// <summary>Add more words runs after these words, which ask for no add and end their sentence.</summary>
        private const string FirstWords = "Hide the Amount column.";

        /// <summary>Its words ask to add a description, so the rule lets any add match it (a wrong pick among adds, not a take-away).</summary>
        private static readonly string[] AddVerbChanges = { "vr4-14" };

        private static string Repo()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Semanticus.sln"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
        }

        private static List<Label> Load(string file)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo(), "tools", "jev-replay", "compose", file)));
            return doc.RootElement.GetProperty("items").EnumerateArray().Select(e => new Label
            {
                Id = e.GetProperty("id").GetString(),
                Set = e.GetProperty("set").GetString(),
                Kind = e.GetProperty("kind").GetString(),
                Words = e.GetProperty("words").GetString(),
                Ops = e.GetProperty("clauses").EnumerateArray().Select(c => c.TryGetProperty("op", out var op) ? op.GetString() : null).ToArray(),
            }).ToList();
        }

        private static List<Label> Labels() => Load("take-away-labels.json").Concat(Load("verify-r4-labels.json")).ToList();

        /// <summary>An op or a step that adds, by the labels' own definition: an op that creates, or a step named Add.</summary>
        private static bool Adding(string op, string name) =>
            (op ?? "").StartsWith("create_", StringComparison.Ordinal) || (name ?? "").StartsWith("Add ", StringComparison.Ordinal);

        /// <summary>The template of a Plan step or a menu item, from its id ("add-measure", or "add-measure-2" for a second).</summary>
        private static DraftMenu.Template TemplateOf(string id) =>
            id == null ? null : DraftMenu.All.FirstOrDefault(t => id == t.Id
                || (id.StartsWith(t.Id + "-", StringComparison.Ordinal)
                    && int.TryParse(id.Substring(t.Id.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _)));

        private static bool AddingTemplate(DraftMenu.Template t) => t != null && Adding(t.Op, t.Title);

        /// <summary>A DRAFTED Plan step that adds, by the Plan door's own reading (the M11 join, the coordinator's ruling of 30 Sep): an op
        /// that creates. The wider reading (a step named Add, so Add synonyms) is the Workflows door's and the menu's here; on the Plan,
        /// Add synonyms makes nothing new, and a Jev sure of it is a step like any other that adds nothing (listed in the join report).</summary>
        private static bool AddingOnPlan(DraftMenu.Template t) => t != null && AddingRule.Adds(t.Op);

        /// <summary>Phrasings labelled noAdd for which the joined rule OFFERS the add unticked on the Plan: the clause holds an adding verb, so
        /// version 5 (29 Sep, which replaced version 2's take-away veto) offers it with the person's words and one true line on why, and the
        /// whole-text belt (v6 B) unticks it. Held: never ticked and never matched on the menu (re-labelled at the M11 join).</summary>
        private static readonly string[] OfferedUnticked =
        {
            "vd-20",      // "make the old measures go away": make is an adding verb (v5 K1), "the old measures" is not new (K2).
            "vn-15",      // "add a measure that counts deleted orders": v6 B ("deleted").
            "v2-vn-15",   // the same words, in the version 2 cases.
            "v2-11",      // "I need a new role removed within the hour": a want of a new thing (h), v6 B ("removed").
            "v2-12",      // "New roles require deletion": a bare want of new things (v6 D), v6 B ("deletion").
            "v2-15",      // "create a measure that excludes the old returns": v6 B ("excludes").
        };

        private static void Write(string name, IEnumerable<object> rows)
        {
            var dir = Environment.GetEnvironmentVariable("VC4_OUT");
            if (string.IsNullOrEmpty(dir)) return;
            if (Path.GetFullPath(dir).StartsWith(Path.GetFullPath(Repo()), StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to write results inside the repository.");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, name + ".jsonl"), rows.Select(r => JsonSerializer.Serialize(r)));
        }

        /// <summary>Workflows, clause by clause: a clause labelled noAdd, or with an op that does not add, drafts no op that
        /// adds (the harm this round is about). A clause labelled with an op that adds may draft another add (the word
        /// match's or Jev's pick among adds stands under the ruling); that is recorded as a wrong pick, not held.</summary>
        private static (List<string> Wrong, List<string> Picks, bool AsLabelled) CheckWorkflows(Label l, DescribedWorkflow d)
        {
            var wrong = new List<string>();
            var picks = new List<string>();
            var aligned = d.Steps.Length == l.Ops.Length;
            var asLabelled = aligned;
            var allowed = l.Ops.Where(op => op != null && Adding(op, null)).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < d.Steps.Length; i++)
            {
                var s = d.Steps[i];
                var want = aligned ? l.Ops[i] : null;
                var adds = s.Ops.Where(op => Adding(op, null)).ToList();
                if (s.Op != null && Adding(null, s.StepName) && !adds.Contains(s.Op)) adds.Add(s.Op);
                var asksAnAdd = aligned ? want != null && (Adding(want, null) || AddVerbChanges.Contains(l.Id)) : allowed.Count > 0;
                foreach (var a in adds.Where(a => a != want))
                    (asksAnAdd ? picks : wrong).Add($"clause {i + 1} '{s.Words}' drafted {a} ({s.StepName}, by {s.ChosenBy})");
                if (aligned && (want == null ? adds.Count > 0 : s.Op != want)) asLabelled = false;
            }
            return (wrong, picks, asLabelled);
        }

        /// <summary>The Plan and Add more words: a phrasing none of whose clauses asks to add gets no menu match and no
        /// drafted step that adds, whatever Jev does. With Jev off, a phrasing that also asks to add gets no add beyond the
        /// labelled adds and the word match's own pick for its adding clauses. With Jev drafting (sure of every add, or
        /// live), a phrasing that asks to add may draft other adds for its adding clause; recorded, not held.</summary>
        private static (List<string> Wrong, List<string> Picks, bool AsLabelled, string[] Matched, string[] Drafted) CheckPlan(Label l, DraftView v, Mode mode)
        {
            var allowed = l.Ops.Where(op => op != null && Adding(op, null)).ToHashSet(StringComparer.Ordinal);
            var asksAnAdd = allowed.Count > 0 || AddVerbChanges.Contains(l.Id);
            var described = DescribedSteps.From(l.Words).Steps;
            if (described.Length == l.Ops.Length)
                for (var i = 0; i < l.Ops.Length; i++)
                    if (l.Ops[i] != null && Adding(l.Ops[i], null) && described[i].Op != null) allowed.Add(described[i].Op);
            var matched = v.Menu.Where(m => m.Matched).Select(m => m.Id).ToArray();
            var drafted = v.Steps.Select(s => s.Id + (s.Kept ? "" : " (dropped)")).ToArray();
            var found = new List<string>();
            foreach (var id in matched)
            {
                var t = TemplateOf(id);
                if (AddingTemplate(t) && !allowed.Contains(t.Op)) found.Add("the menu matches " + t.Title);
            }
            foreach (var s in v.Steps)
            {
                var t = TemplateOf(s.Id);
                if (!s.Kept && OfferedUnticked.Contains(l.Id)) continue;
                if (AddingOnPlan(t) && !allowed.Contains(t.Op)) found.Add("drafts " + t.Title + (s.Kept ? ", kept" : ", dropped"));
            }
            // With Jev off, the word match reads adding clauses the one way it can, so an add beyond its reading is from a
            // clause that asks for no add; words that ask to add a thing that is not a template (a description) may match any add.
            var held = !asksAnAdd || (mode == Mode.Off && !AddVerbChanges.Contains(l.Id));
            var ops = matched.Concat(v.Steps.Select(s => s.Id)).Select(TemplateOf).Where(t => t != null).Select(t => t.Op).ToHashSet(StringComparer.Ordinal);
            return (held ? found : new List<string>(), held ? new List<string>() : found,
                found.Count == 0 && l.Ops.Where(op => op != null).All(ops.Contains), matched, drafted);
        }

        private static async Task<List<string>> RunAsync(LocalEngine engine, List<Label> labels, Mode mode, List<object> rows, bool studioToo)
        {
            var failures = new List<string>();
            var jev = mode.ToString().ToLowerInvariant();
            foreach (var l in labels)
            {
                var asks = DescribedSteps.From(l.Words).Steps.Select(s => s.AsksToAdd).ToArray();

                var described = new List<(string Door, DescribedWorkflow D)>();
                if (studioToo) described.Add(("Studio", await new EngineRpcTarget(engine).describeWorkflowSteps(l.Words)));
                described.Add(("agent", await McpTools.DescribeWorkflowSteps(engine, l.Words)));
                foreach (var (door, d) in described)
                {
                    var (wrong, picks, ok) = CheckWorkflows(l, d);
                    rows.Add(new
                    {
                        l.Id, l.Set, l.Kind, l.Words, surface = "workflows", door, jev, asksToAdd = asks, labels = l.Ops,
                        jevAsked = d.JevAsked, jevNote = d.JevNote, cut = d.Steps.Length,
                        steps = d.Steps.Select(s => new { s.Words, s.Op, s.Ops, s.StepName, s.ChosenBy, s.Reason }),
                        wrong, picks, asLabelled = ok,
                    });
                    failures.AddRange(wrong.Select(w => $"{l.Id} Workflows {door} Jev {jev}: '{l.Words}' {w}"));
                }

                var composed = new List<(string Door, DraftView V)>();
                if (studioToo) composed.Add(("Studio", await new EngineRpcTarget(engine).composeTask(l.Words)));
                composed.Add(("agent", await McpTools.ComposeTask(engine, words: l.Words)));
                var refined = new List<(string Door, DraftView V)>();
                if (studioToo)
                {
                    var first = await new EngineRpcTarget(engine).composeTask(FirstWords);
                    refined.Add(("Studio", await new EngineRpcTarget(engine).editDraft(first.DraftId, "refine", null, null, null, l.Words)));
                }
                var firstAgent = await McpTools.ComposeTask(engine, words: FirstWords);
                refined.Add(("agent", await McpTools.EditDraft(engine, firstAgent.DraftId, "refine", text: l.Words)));
                foreach (var (surface, views) in new[] { ("plan", composed), ("refine", refined) })
                    foreach (var (door, v) in views)
                    {
                        var (wrong, picks, ok, matched, drafted) = CheckPlan(l, v, mode);
                        rows.Add(new
                        {
                            l.Id, l.Set, l.Kind, l.Words, surface, door, jev, asksToAdd = asks, labels = l.Ops,
                            matched, drafted, menuLine = v.MenuLine, sentence = v.Sentence, wrong, picks, asLabelled = ok,
                        });
                        failures.AddRange(wrong.Select(w => $"{l.Id} {(surface == "plan" ? "Plan" : "Add more words")} {door} Jev {jev}: '{l.Words}' {w}"));
                    }
            }
            return failures;
        }

        [Fact]
        public async Task With_Jev_off_no_labelled_phrasing_offers_or_drafts_an_add_it_did_not_ask_for_on_any_door()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("Vc4Off.bim");
            f.Engine.JevDraftsForTest = false;
            var labels = Labels();
            Assert.Equal(103, labels.Count);
            var rows = new List<object>();
            var failures = await RunAsync(f.Engine, labels, Mode.Off, rows, studioToo: true);
            Write("jev-off", rows);
            Assert.True(failures.Count == 0, failures.Count + " adds not asked for:\n" + string.Join("\n", failures));
        }

        [Fact]
        public async Task With_Jev_sure_of_every_add_no_take_away_offers_or_drafts_an_add_on_any_door()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("Vc4Sure.bim");
            await f.ConsentAsync();
            f.Engine.JevDraftsForTest = true;
            f.Engine.UseDecisionsForTest(new DecisionService(new SureOfEveryAdd(), new DecisionBudget()));
            var rows = new List<object>();
            var failures = await RunAsync(f.Engine, Labels(), Mode.Sure, rows, studioToo: true);
            Write("jev-sure", rows);
            Assert.True(failures.Count == 0, failures.Count + " adds not asked for:\n" + string.Join("\n", failures));
        }

        /// <summary>The same set against the Jev service, on the agent door (the Studio door is the same engine call).</summary>
        [JevReplayFact]
        public async Task Live_no_labelled_phrasing_offers_or_drafts_an_add_it_did_not_ask_for_with_Jev_drafting()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("Vc4Live.bim");
            await f.ConsentAsync();
            f.Engine.JevDraftsForTest = true;
            var budget = new DecisionBudget(4_000_000);
            using var jev = new JevDecisionOracle(LiveKey(), budget);
            f.Engine.UseDecisionsForTest(new DecisionService(jev, budget));
            var rows = new List<object>();
            var failures = await RunAsync(f.Engine, Labels(), Mode.Live, rows, studioToo: false);
            Write("jev-live", rows);
            Assert.True(failures.Count == 0, failures.Count + " adds not asked for:\n" + string.Join("\n", failures));
        }

        // ---- The adding rule version 2 (the coordinator, 28 Sep 17:30), for the next round ------------------------------
        // Its cases, labelled the way version 2 decides them (tools/jev-replay/compose/adding-rule-v2-cases.json). They
        // are not this round's brief: each one that fails here is a case the next round's build of version 2 must pass.

        private static List<Label> V2Labels() => Load("adding-rule-v2-cases.json");

        [Fact]
        public async Task Adding_rule_version_2_cases_with_Jev_off_on_every_door()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("Vc4V2Off.bim");
            f.Engine.JevDraftsForTest = false;
            var rows = new List<object>();
            var failures = await RunAsync(f.Engine, V2Labels(), Mode.Off, rows, studioToo: true);
            Write("v2-jev-off", rows);
            Assert.True(failures.Count == 0, failures.Count + " adds version 2 does not allow:\n" + string.Join("\n", failures));
        }

        [Fact]
        public async Task Adding_rule_version_2_cases_with_Jev_sure_of_every_add_on_every_door()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("Vc4V2Sure.bim");
            await f.ConsentAsync();
            f.Engine.JevDraftsForTest = true;
            f.Engine.UseDecisionsForTest(new DecisionService(new SureOfEveryAdd(), new DecisionBudget()));
            var rows = new List<object>();
            var failures = await RunAsync(f.Engine, V2Labels(), Mode.Sure, rows, studioToo: true);
            Write("v2-jev-sure", rows);
            Assert.True(failures.Count == 0, failures.Count + " adds version 2 does not allow:\n" + string.Join("\n", failures));
        }

        [JevReplayFact]
        public async Task Live_adding_rule_version_2_cases_with_Jev_drafting()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("Vc4V2Live.bim");
            await f.ConsentAsync();
            f.Engine.JevDraftsForTest = true;
            var budget = new DecisionBudget(2_000_000);
            using var jev = new JevDecisionOracle(LiveKey(), budget);
            f.Engine.UseDecisionsForTest(new DecisionService(jev, budget));
            var rows = new List<object>();
            var failures = await RunAsync(f.Engine, V2Labels(), Mode.Live, rows, studioToo: false);
            Write("v2-jev-live", rows);
            Assert.True(failures.Count == 0, failures.Count + " adds version 2 does not allow:\n" + string.Join("\n", failures));
        }

        // ---- Defects found by attack at f5a33d29, each left failing on purpose (Jev off: the null path) ---------------

        /// <summary>VC4-D1 (the harmful kind). Add more words joins the new words to the draft's words with a space
        /// (DraftDoors RefineAsync), so new words typed without "and" or a full stop after words that add become one clause
        /// led by the first words' adding verb: "add a Margin measure" then "remove the old role" is read as "add a Margin
        /// measure remove the old role", which asks to add, and the menu matches Add a role for the words that take the
        /// role away (and, once Jev drafts on the Plan, a Jev sure of that add drafts it). With a full stop after the first
        /// words it does not. Both doors.</summary>
        [Theory]
        [InlineData("add a Margin measure", "remove the old role", "role")]
        [InlineData("add a Margin measure", "also delete the old role", "role")]
        [InlineData("create a role for managers", "delete the Sales YTD measure", "add-measure")]
        [InlineData("count the orders", "remove the old role", "role")]
        public async Task Add_more_words_that_take_away_after_words_that_add_offer_no_add_for_them_on_either_door(string first, string more, string notAsked)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("Vc4Join.bim");
            f.Engine.JevDraftsForTest = false;
            var a = await new EngineRpcTarget(f.Engine).composeTask(first);
            var studio = await new EngineRpcTarget(f.Engine).editDraft(a.DraftId, "refine", null, null, null, more);
            var b = await McpTools.ComposeTask(f.Engine, words: first);
            var agent = await McpTools.EditDraft(f.Engine, b.DraftId, "refine", text: more);
            var failures = new[] { ("Studio", studio), ("agent", agent) }
                .Where(x => x.Item2.Menu.Any(m => m.Matched && m.Id == notAsked))
                .Select(x => $"{x.Item1}: '{first}', then Add more words '{more}', matches {TemplateOf(notAsked).Title}").ToList();
            Assert.True(failures.Count == 0, string.Join("; ", failures));
        }

        /// <summary>VC4-D2 to D5 (the harmful kind): take-away asks that still draft an add, on Workflows and the Plan, both
        /// doors, Jev off. D2: measure names led by an adding word at the start of a sentence ("Total Sales and Average
        /// Price can go"); the capital-is-a-name reading skips a sentence's first word. D3: the same names in lower case
        /// ("remove the order count and total cost measures"). D4: an adding verb whose thing is what is then taken away
        /// ("Build a list of unused measures and delete them"). D5: Sol's seventh pass, reported open by the builder.
        /// asked is the one add the words do ask for, or null.</summary>
        [Theory]
        [InlineData("Total Sales and Average Price can go", null)]
        [InlineData("Count Orders and Total Cost are no longer needed", null)]
        [InlineData("Total Sales, Average Price: delete both", null)]
        [InlineData("remove the order count and total cost measures", null)]
        [InlineData("delete sales amount, total cost and average price", null)]
        [InlineData("Delete the total sales and average order value measures", null)]
        [InlineData("Build a list of unused measures and delete them", null)]
        [InlineData("Count the unused measures and delete them", null)]
        [InlineData("Add a warning before you delete the old role", null)]
        [InlineData("Add a measure, then a purge of unused roles", "create_measure")]
        [InlineData("I need a new role removed within the hour", null)]
        [InlineData("New roles require deletion", null)]
        [InlineData("Set up removal of the old role", null)]
        public async Task A_take_away_ask_drafts_no_add_it_did_not_ask_for_on_either_door(string words, string asked)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("Vc4TakeAway.bim");
            f.Engine.JevDraftsForTest = false;
            var failures = new List<string>();
            foreach (var door in new[] { "Studio", "agent" })
            {
                var d = door == "Studio" ? await new EngineRpcTarget(f.Engine).describeWorkflowSteps(words) : await McpTools.DescribeWorkflowSteps(f.Engine, words);
                foreach (var s in d.Steps)
                    foreach (var op in s.Ops.Where(op => Adding(op, s.StepName) && op != asked))
                        failures.Add($"Workflows {door}: '{s.Words}' drafted {s.StepName}");
                var v = door == "Studio" ? await new EngineRpcTarget(f.Engine).composeTask(words) : await McpTools.ComposeTask(f.Engine, words: words);
                foreach (var t in v.Menu.Where(m => m.Matched).Select(m => TemplateOf(m.Id)).Where(t => AddingTemplate(t) && t.Op != asked))
                    failures.Add($"Plan {door}: the menu matches {t.Title}");
            }
            Assert.True(failures.Count == 0, $"'{words}': " + string.Join("; ", failures));
        }

        private static string LiveKey()
        {
            var key = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
            if (!string.IsNullOrEmpty(key)) return key;
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "typesafe", "env");
            var m = Regex.Match(File.ReadAllText(file), @"^\s*(?:export\s+)?[A-Z0-9_]*(?:KEY|TOKEN)[A-Z0-9_]*\s*=\s*[""']?([^""'\s]+)", RegexOptions.Multiline);
            if (!m.Success) throw new InvalidOperationException("No key in the key file.");
            return m.Groups[1].Value;
        }

        /// <summary>Jev sure (0.97) of every template that adds, a sure no (0.02) to every other include, none to every
        /// choice: the worst Jev can do to words that ask for no add.</summary>
        private sealed class SureOfEveryAdd : IDecisionOracle
        {
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.PointName != DecisionRegistry.ComposeDraft)
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "this test answers compose.draft only"));
                var adds = DraftMenu.All.Where(AddingTemplate).Select(t => "step:" + t.Id + ":include").ToHashSet();
                var sb = new StringBuilder("{\"entries\":[{\"digest\":").Append(JsonSerializer.Serialize(request.Digest))
                    .Append(",\"point\":").Append(JsonSerializer.Serialize(request.PointName))
                    .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":30},\"answers\":{");
                var first = true;
                foreach (var q in request.Questions)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(':');
                    if (q.Kind == DecisionQuestionKind.Noul)
                        sb.Append("{\"type\":\"noul\",\"noul\":").Append((adds.Contains(q.Name) ? 0.97 : 0.02).ToString(CultureInfo.InvariantCulture)).Append('}');
                    else
                        sb.Append("{\"type\":\"choice\",\"choice\":\"none\",\"confidence\":0.95}");
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-test-1").AskAsync(request, ct);
            }
        }
    }
}
