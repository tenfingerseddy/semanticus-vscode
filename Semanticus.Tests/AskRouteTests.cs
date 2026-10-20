// The box (Jev M2): the catalogue it routes over, the questions the engine answers itself, and what happens
// with no service.
//
// The first test here is the one that matters most and is the least obvious. AskRouteCatalog.cs holds a COPY
// of the help page's guide lines, because the engine ships without the webview and has to route the same way
// headless. A copy rots. This test reads help.tsx itself and holds the copy to it word for word, so changing
// a tab's description in the extension fails here instead of quietly teaching the router about a page that no
// longer says that.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class AskRouteTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        /// <summary>Every guide line in help.tsx, by tool id. Parsed rather than hand-copied, so this test
        /// cannot drift from the file it is meant to police.</summary>
        private static Dictionary<string, string> HelpLeads()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.VSCode", "webview", "src", "help.tsx"));
            var start = src.IndexOf("const HELP: Record<string, TabHelp> = {", StringComparison.Ordinal);
            Assert.True(start > 0, "help.tsx no longer declares the HELP table the router copies from.");
            var body = src.Substring(start);

            var leads = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match entry in Regex.Matches(body, "^  \"(\\w+)\": \\{", RegexOptions.Multiline))
            {
                var next = entry.NextMatch();
                var segment = next.Success ? body.Substring(entry.Index, next.Index - entry.Index) : body.Substring(entry.Index);
                var lead = Regex.Match(segment, "\"lead\": \"((?:[^\"\\\\]|\\\\.)*)\"");
                if (lead.Success) leads[entry.Groups[1].Value] = Regex.Unescape(lead.Groups[1].Value);
            }
            Assert.NotEmpty(leads);
            return leads;
        }

        [Fact]
        public void The_catalogue_words_are_the_help_pages_own_words()
        {
            var leads = HelpLeads();
            foreach (var tool in AskRouteCatalog.Tools)
            {
                Assert.True(leads.ContainsKey(tool.Id),
                    "The router knows a tool '" + tool.Id + "' that help.tsx has no entry for.");
                Assert.Equal(leads[tool.Id], tool.Guide);
            }
        }

        /// <summary>The words the merged vocabulary retired, read out of the ONE place that owns them
        /// (Semanticus.VSCode/test/copy-rules.test.mjs) rather than copied here. Copied, they would rot the
        /// same way the four labels below rotted; read, a word retired tomorrow fails this test tomorrow.</summary>
        private static Regex[] RetiredWords()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.VSCode", "test", "copy-rules.test.mjs"));
            var start = src.IndexOf("const MERGED_VOCABULARY = [", StringComparison.Ordinal);
            Assert.True(start > 0, "copy-rules.test.mjs no longer declares MERGED_VOCABULARY.");
            var end = src.IndexOf("\n];", start, StringComparison.Ordinal);
            Assert.True(end > start, "MERGED_VOCABULARY is not closed where this test expects it.");

            var patterns = Regex.Matches(src.Substring(start, end - start), @"/((?:[^/\\\n]|\\.)+)/i")
                .Select(m => new Regex(m.Groups[1].Value, RegexOptions.IgnoreCase))
                .ToArray();
            Assert.True(patterns.Length >= 30, "only " + patterns.Length + " retired words were parsed; the shape of the list has changed");
            return patterns;
        }

        /// <summary>The catalogue's labels are what a person is SENT to, and they are a copy of the Studio's
        /// own tab names. Four of them had gone stale against the merged vocabulary and were still telling
        /// people to go to places by names the product no longer uses.</summary>
        [Fact]
        public void No_place_is_named_with_a_word_the_product_retired()
        {
            var retired = RetiredWords();
            var failures = new List<string>();

            foreach (var tool in AskRouteCatalog.Tools)
                foreach (var pattern in retired)
                {
                    // Kane's name for the Review page that asks how Copilot answers (15 Oct 2026); copy-rules.test.mjs allows the same phrase.
                    var hit = pattern.Match(tool.Label.Replace("Copilot test", "Copilot"));
                    if (hit.Success)
                        failures.Add(tool.Id + " is labelled \"" + tool.Label + "\", which carries the retired word \"" + hit.Value + "\"");
                }

            Assert.Equal(Array.Empty<string>(), failures.ToArray());
        }

        /// <summary>The strip is the catalogue MINUS the places that are a tab inside another page.
        ///
        /// It used to be "the strip IS the catalogue", and that stopped being true on 22 September 2026 when
        /// Kane picked the seven areas: Reports became a tab on Validation instead of a button of its own, and
        /// AI instructions arrived as a new page. Reports is still somewhere a person can be sent, so dropping
        /// it from the catalogue would have made the one page about saved reports unroutable. Flagging it as a
        /// tab keeps it routable and keeps this comparison exact.
        ///
        /// Three more became tabs on 23 September: Pending changes and Model Spec fold into Plan, and Model
        /// notes folds into AI instructions. Twenty-two places, four of them tabs, eighteen on the strip.
        ///
        /// Slice 11 (Plan-heart lanes R1 and R2) retired the Overview and made Edit History the Plan's History
        /// view: the Overview leaves the catalogue (its words route to the Plan), and History stays a place, as a
        /// tab. Twenty-one places, five of them tabs, sixteen on the strip.
        ///
        /// Both numbers are READ rather than typed, because a number typed in a test is the next stale
        /// premise; the count below is asserted only after it has been measured off App.tsx.
        ///
        /// help.tsx also carries three entries (compare, dataagent, permissions) that are in no area and no
        /// tab, so sending a person to one would send them nowhere. They stay out of the catalogue.</summary>
        [Fact]
        public void The_strip_is_the_catalogue_minus_the_places_that_are_a_tab()
        {
            var strip = StripTools();
            var routableOnTheStrip = AskRouteCatalog.StripTools.Select(t => t.Id).ToArray();

            Assert.Equal(16, strip.Count);                              // measured, then asserted (Query replaced Ask your data and Queries, and the Copilot test joined Review, 15 Oct)
            Assert.Equal(21, AskRouteCatalog.Tools.Count);              // the strip plus the five tabs
            Assert.Equal(strip.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                routableOnTheStrip.OrderBy(x => x, StringComparer.Ordinal).ToArray());

            var tabs = AskRouteCatalog.Tools.Where(t => t.IsTab).Select(t => t.Id).ToArray();
            Assert.Equal(new[] { "optimize", "spec", "knowledge", "evidence", "history" }, tabs);

            foreach (var absent in new[] { "compare", "dataagent", "permissions", "modelhome" })
                Assert.Null(AskRouteCatalog.Find(absent));
            Assert.Equal(AskRouteCatalog.Tools.Count,
                AskRouteCatalog.Tools.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count());
        }

        /// <summary>The three places Kane folded into another page on 23 September are still places. A fold is
        /// a change to where a person clicks, not to what exists, so each one keeps its label, keeps its area
        /// and stays routable; only the strip button goes. Dropping them from the catalogue instead would have
        /// made "where do I review my pending changes" answerable by nothing.</summary>
        [Fact]
        public void The_folded_places_keep_their_names_and_stay_routable()
        {
            foreach (var (id, area, label) in new[]
                     {
                         ("optimize", "plan", "Pending changes"),
                         ("spec", "plan", "Model Spec"),
                         ("knowledge", "edit", "Model notes"),
                         ("history", "plan", "History"),
                     })
            {
                var tool = AskRouteCatalog.Find(id);
                Assert.NotNull(tool);
                Assert.True(tool!.IsTab, id + " is a view inside another page and must be flagged as a tab.");
                Assert.Equal(area, tool.Area);
                Assert.Equal(label, tool.Label);
                Assert.DoesNotContain(tool, AskRouteCatalog.StripTools);
            }

            // Plan itself is still a strip button, and it is the page the two folded ones now live on.
            Assert.False(AskRouteCatalog.Find("plan")!.IsTab);
            Assert.False(AskRouteCatalog.Find("instructions")!.IsTab);

            // The words still reach them, which is the whole reason a tab stays in the catalogue.
            Assert.Equal("optimize", AskRoutePoint.MatchWords("review the pending changes")!.Tool);
            Assert.Equal("knowledge", AskRoutePoint.MatchWords("where are my model notes")!.Tool);
            Assert.Equal("history", AskRoutePoint.MatchWords("show me the edit history")!.Tool);
        }

        /// <summary>The Overview retired in slice 11 (Plan-heart lane R1). The words a person used for it still
        /// get an answer, and the answer is the Plan, the start page that carries the model's facts now.</summary>
        [Theory]
        [InlineData("give me an overview")]
        [InlineData("the whole model at a glance")]
        public void The_overview_words_land_on_the_plan(string ask)
        {
            Assert.Null(AskRouteCatalog.Find("modelhome"));
            Assert.Equal("plan", AskRoutePoint.MatchWords(ask)!.Tool);
        }

        /// <summary>The seven areas Kane picked on 22 September, and nothing else. Analyse was renamed Review
        /// in the same decision, so the word must not survive anywhere in the catalogue.</summary>
        [Fact]
        public void Every_place_sits_in_one_of_the_seven_areas()
        {
            Assert.Equal(new[] { "plan", "explore", "edit", "review", "publish", "workflows" },
                AskRouteCatalog.Areas.ToArray());

            foreach (var tool in AskRouteCatalog.Tools)
            {
                Assert.True(AskRouteCatalog.Areas.Contains(tool.Area),
                    tool.Id + " is in the area '" + tool.Area + "', which is not one of the seven.");
                Assert.DoesNotContain("analyse", tool.Area, StringComparison.OrdinalIgnoreCase);
            }

            // Every area has at least one place in it, or the strip would draw an empty button.
            foreach (var area in AskRouteCatalog.Areas)
                Assert.Contains(AskRouteCatalog.Tools, t => string.Equals(t.Area, area, StringComparison.Ordinal));
        }

        /// <summary>The labels the box sends people to, pinned one by one. They are the strip's own words and
        /// the ask box asserts the same equality from the other side (ask-box.test.mjs), so a rename that
        /// lands on one side only fails here.</summary>
        [Theory]
        [InlineData("plan", "plan", "Plan")]
        [InlineData("optimize", "plan", "Pending changes")]
        [InlineData("spec", "plan", "Model Spec")]
        [InlineData("data", "explore", "Data")]
        [InlineData("diagram", "explore", "Diagram")]
        [InlineData("lineage", "explore", "Lineage")]
        [InlineData("stats", "explore", "Size")]
        [InlineData("query", "explore", "Query")]
        [InlineData("knowledge", "edit", "Model notes")]
        [InlineData("instructions", "edit", "AI instructions")]
        [InlineData("advmodels", "edit", "Advanced modelling")]
        [InlineData("mcode", "edit", "Power Query")]
        [InlineData("bpa", "review", "Model quality (BPA)")]
        [InlineData("readiness", "review", "AI readiness")]
        [InlineData("copilottest", "review", "Copilot test")]
        [InlineData("tests", "review", "Validation")]
        [InlineData("evidence", "review", "Reports")]
        [InlineData("docs", "review", "Docs")]
        [InlineData("deploy", "publish", "Published")]
        [InlineData("history", "plan", "History")]
        [InlineData("workflows", "workflows", "Workflows")]
        public void Each_place_carries_the_area_and_the_label_the_strip_shows(string id, string area, string label)
        {
            var tool = AskRouteCatalog.Find(id);
            Assert.True(tool != null, "the catalogue has no place called '" + id + "'");
            Assert.Equal(area, tool!.Area);
            Assert.Equal(label, tool.Label);
        }

        /// <summary>A question about the numbers goes to Ask your data (Kane, 26 September), not to Queries, where DAX
        /// is written, and not to Data, where rows are browsed. The box only navigates: the page asks.</summary>
        [Theory]
        [InlineData("total sales by region")]
        [InlineData("net sales by store region for 2025")]
        [InlineData("top 10 products by revenue")]
        [InlineData("ask your data")]
        public void A_data_question_lands_on_ask_your_data(string ask)
        {
            Assert.Equal("query", AskRoutePoint.MatchWords(ask)!.Tool);
            Assert.Equal(AskRouteCatalog.DataTool, AskRoutePoint.MatchWords(ask)!.Group);
        }

        /// <summary>verify-askdata-r3 (27 September 2026, m11/askdata fa4fb162), left failing on purpose. Ctrl J now
        /// ROUTES every phrasing isDataQuestion calls a question about the numbers (composer-model.ts), and without
        /// permission (Not now, a withdrawn permission, the switch off, the service down) this word match alone decides
        /// where it goes. Each phrasing below is a data question from the verifier's own labelled set that Ctrl J
        /// routes on fa4fb162, and the words place none of them, so the person is told "Nothing here matches those
        /// words yet" and is neither sent to Ask your data nor given a draft. Live in UAT lane n before permission:
        /// of the 302 labelled replay questions, Ctrl J routes 280, the words place 135 on Ask your data and 136 get
        /// that line.</summary>
        [Theory]
        [InlineData("units sold per brand last year")]
        [InlineData("revenue for each country in 2024")]
        [InlineData("returns per store in March")]
        [InlineData("customer count by city")]
        [InlineData("total discount by promotion")]
        [InlineData("order count by channel for online only")]
        [InlineData("net sales this year vs last year by region")]
        public void Verify_r3_a_data_question_ctrl_j_routes_lands_on_ask_your_data_on_words_alone(string ask)
        {
            Assert.Equal("query", AskRoutePoint.MatchWords(ask)?.Tool);
            Assert.Equal(AskRouteCatalog.DataTool, AskRoutePoint.MatchWords(ask)?.Group);
        }

        /// <summary>fix-askdata-r4 (D2): a data question the words place lands on Ask your data with no permission, on both
        /// doors, at confidence 1 because nothing was asked. The words and the service placing nothing used to mean
        /// "Nothing here matches" for half the data questions (verify-askdata-r3: 136 of 302); the split shape and the
        /// page's own words ("how much") now place these. Nothing here gives permission, so the service is never asked.
        /// Since fix-askdata-final (verify-askdata-r6 F3) Ask your data is FIRST only where the line plainly asks ("how
        /// much ..."); the other two are placed there all the same and read the task first, with Ask your data second.</summary>
        [Theory]
        [InlineData("how much did we sell in March", true)]
        [InlineData("units sold per brand last year", false)]
        [InlineData("total sales by region", false)]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task R4_a_data_question_the_words_place_lands_on_ask_your_data_with_no_permission_on_both_doors(string ask, bool first)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteDataQuestion.bim");
            f.UseEmptyRecorded();

            foreach (var result in new[]
            {
                await McpTools.RouteAsk(f.Engine, ask),
                await new EngineRpcTarget(f.Engine).routeAsk(ask),
            })
            {
                var question = AskRoutePoint.ReadingOf(result, AskRoutePoint.QuestionReading);
                Assert.Equal("query", question?.Tool);
                Assert.Equal(1, question.Confidence);
                Assert.Equal(first ? AskRoutePoint.QuestionReading : AskRoutePoint.TaskReading, result.Reading);
                Assert.Equal(first ? "query" : null, result.Tool);
                Assert.Null(result.Fallback);
                Assert.NotEqual(AskRoutePoint.NoMatchSentence, result.Sentence);
                Assert.False(result.Asked);
            }
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("how much did we sell in March", true)]
        public Task R4_a_data_question_the_words_place_lands_on_ask_your_data_with_no_permission_on_both_doors_sample(string ask, bool first) => R4_a_data_question_the_words_place_lands_on_ask_your_data_with_no_permission_on_both_doors(ask, first);

        /// <summary>fix-askdata-r5 (verify-askdata-r4 V1; coordinator's ruling, 28 September): when no page's words won, a
        /// line is NOT sent to Ask your data because it names one of the model's fields. That fallback sent nearly every
        /// task typed in the Plan's field there, since nearly every task names a field. So a question that only names a
        /// field reads the no-match line on both doors with no permission, at confidence 0 because nothing was asked, and
        /// the Plan's field shows nothing (askbox.tsx fieldSuggestion). With permission the service may place it
        /// (R5_the_service_s_ask_your_data_is_reported_at_its_own_confidence). These three landed on round 4's fallback;
        /// they are the questions that now stay tasks.</summary>
        [Theory]
        [InlineData("what were total sales last month")]
        [InlineData("is order count up on last year?")]
        [InlineData("what were sales last month?")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task R5_a_question_that_only_names_a_field_is_placed_nowhere_without_permission_on_both_doors(string ask)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteOnlyAField.bim");
            f.UseEmptyRecorded();

            foreach (var result in new[]
            {
                await McpTools.RouteAsk(f.Engine, ask),
                await new EngineRpcTarget(f.Engine).routeAsk(ask),
            })
            {
                Assert.Null(result.Tool);
                Assert.Equal("menu", result.Fallback);
                Assert.Equal(AskRoutePoint.NoMatchSentence, result.Sentence);
                Assert.False(result.Asked);
                Assert.Equal(0, result.Confidence);
            }
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("what were total sales last month")]
        public Task R5_a_question_that_only_names_a_field_is_placed_nowhere_without_permission_on_both_doors_sample(string ask) => R5_a_question_that_only_names_a_field_is_placed_nowhere_without_permission_on_both_doors(ask);

        /// <summary>Route one line with permission and a recorded service answer for exactly that line: first an empty
        /// oracle learns the digest the engine really sends, then the answer is recorded for it.</summary>
        private static async Task<(AskRouteResult Studio, AskRouteResult Assistant)> RouteWithServiceAsync(
            DecisionFixture f, string words, string choice, double confidence)
        {
            var probe = f.UseEmptyRecorded();
            await f.Engine.RouteAskAsync(words);
            Assert.Single(probe.AskedDigests);
            f.UseRecorded("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":\"" + probe.AskedDigests[0]
                + "\",\"point\":\"" + DecisionRegistry.AskRoute + "\",\"usage\":{\"input_tokens\":300,\"output_tokens\":10},"
                + "\"answers\":{\"" + AskRoutePoint.ToolQuestion + "\":{\"type\":\"choice\",\"choice\":\"" + choice
                + "\",\"confidence\":" + confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}]}");
            return (await new EngineRpcTarget(f.Engine).routeAsk(words), await McpTools.RouteAsk(f.Engine, words));
        }

        /// <summary>fix-askdata-r5 (verify-askdata-r4 V1): a line the service leaves unplaced reads the no-match line at the
        /// service's OWN confidence. Round 4 sent it to Ask your data anyway and reported confidence 1 with asked true,
        /// so the assistant door read "askdata, confidence 1" for "order a pizza".</summary>
        [Theory]
        [InlineData("what were sales last month?", AskRouteCatalog.NoTool, 0.83)]
        [InlineData("order a pizza", AskRouteCatalog.NoTool, 0.97)]
        [InlineData("what were sales last month?", "askdata", 0.31)]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task R5_a_line_the_service_leaves_unplaced_reads_no_place_at_the_service_s_own_confidence(string words, string choice, double confidence)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteUnplaced.bim");
            await f.ConsentAsync();
            Assert.Null(AskRoutePoint.MatchWords(words));

            var (studio, assistant) = await RouteWithServiceAsync(f, words, choice, confidence);
            foreach (var result in new[] { studio, assistant })
            {
                Assert.True(result.Asked);
                Assert.Null(result.Tool);
                Assert.Equal(AskRoutePoint.NoMatchSentence, result.Sentence);
                Assert.Equal(confidence, result.Confidence, 6);
            }
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("what were sales last month?", AskRouteCatalog.NoTool, 0.83)]
        public Task R5_a_line_the_service_leaves_unplaced_reads_no_place_at_the_service_s_own_confidence_sample(string words, string choice, double confidence) => R5_a_line_the_service_leaves_unplaced_reads_no_place_at_the_service_s_own_confidence(words, choice, confidence);

        /// <summary>fix-askdata-r5: where the service does place a question on Ask your data, it lands there at the
        /// service's own confidence, not a fixed 1.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task R5_the_service_s_ask_your_data_is_reported_at_its_own_confidence()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteServiceAskData.bim");
            await f.ConsentAsync();

            var (studio, assistant) = await RouteWithServiceAsync(f, "what were sales last month?", "askdata", 0.95);
            foreach (var result in new[] { studio, assistant })
            {
                Assert.True(result.Asked);
                Assert.Equal("query", result.Tool);
                Assert.Equal(AskRoutePoint.QuestionReading, result.Reading);
                Assert.Null(result.Fallback);
                Assert.Equal(0.95, result.Confidence, 6);
            }
        }

        /// <summary>fix-askdata-r6 (coordinator, 28 September), replacing fix-askdata-r5's V1 rule. The service is held
        /// to the same rule as the words, and the rule is no longer "never": a task the service places on Ask your data
        /// comes back with BOTH readings. The task is first, because neither line plainly asks, and it names the page
        /// the row names; Ask your data is the named second choice, at the SERVICE'S OWN confidence and never a fixed 1.
        /// The old sentence "These words ask for a change to the model" is gone: it was false for half the lines that
        /// read it (verify-askdata-r5 F2).</summary>
        [Theory]
        [InlineData("Give Total Sales a currency format")]
        [InlineData("Only let managers see Total Sales")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task R6_a_task_the_service_places_on_ask_your_data_keeps_both_readings(string words)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteServiceTask.bim");
            await f.ConsentAsync();
            Assert.Null(AskRoutePoint.MatchWords(words));

            var (studio, assistant) = await RouteWithServiceAsync(f, words, "askdata", 0.95);
            foreach (var result in new[] { studio, assistant })
            {
                Assert.True(result.Asked);
                // The task reading is first and agrees with the row Enter adds from the same words.
                Assert.NotEqual(AskRoutePoint.QuestionReading, result.Reading);
                Assert.Equal(PlanTargets.JumpFor(words)?.Tool, result.Tool);
                // Ask your data is still offered, at the service's own number, and is never the only answer.
                Assert.NotNull(result.Other);
                Assert.Equal(AskRoutePoint.QuestionReading, result.Other.Kind);
                Assert.Equal("query", result.Other.Tool);
                Assert.Equal(0.95, result.Other.Confidence, 6);
                Assert.Equal(AskRoutePoint.AskItAsAQuestion, result.Other.Line);
                Assert.Null(result.Reason);
            }
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("Give Total Sales a currency format")]
        public Task R6_a_task_the_service_places_on_ask_your_data_keeps_both_readings_sample(string words) => R6_a_task_the_service_places_on_ask_your_data_keeps_both_readings(words);

        /// <summary>The coordinator's ruling of 28 September (fix-askdata-r6), which replaces round 5's "never" rule.
        /// Every line labelled a task, a where-question or nothing-in-the-model BEFORE any routing run was measured:
        /// the verifier's 24 tasks, 10 where-questions and 8 lines about nothing (verify-askdata-r4, sha256
        /// a32af467...), round 3's 20 tasks (fresh40.json), and round 5's 30 tasks
        /// (docs/notes/askdata-r5/labels-new40.json, committed before that run). Three things hold for all 92:
        /// the task reading is always there, so Enter keeps the words as a row; it names the same page as the row
        /// itself (PlanTargets.JumpFor), which is what F1 found broken; and Ask your data is never the only answer
        /// and never the first choice for one of these lines. On f2f9996a 46 of the 92 went to Ask your data with no
        /// task reading beside them at all.</summary>
        public static IEnumerable<object[]> LabelledNotAskData => new[]
        {
            new object[] { "r4t:t1", "Hide CustomerKey and ProductKey from report view" },
            new object[] { "r4t:t2", "Mark Date as the date table" },
            new object[] { "r4t:t3", "Give Net Sales a currency format" },
            new object[] { "r4t:t4", "Add a description to every measure in Sales" },
            new object[] { "r4t:t5", "Sort MonthName by MonthNumber" },
            new object[] { "r4t:t6", "Rename Units to Units Sold" },
            new object[] { "r4t:t7", "Make Margin % show one decimal place" },
            new object[] { "r4t:t8", "Delete the Legacy Discount % measure" },
            new object[] { "r4t:t9", "Move the Store measures into a display folder" },
            new object[] { "r4t:t10", "Set Quantity to not summarize" },
            new object[] { "r4t:t11", "Create a hierarchy Category > Subcategory > ProductName" },
            new object[] { "r4t:t12", "Only let managers see Total Cost" },
            new object[] { "r4t:t13", "Add row level security so each region sees its own stores" },
            new object[] { "r4t:t14", "Write docs for the Sales table" },
            new object[] { "r4t:t15", "Replace the AuditNote column with a calculated column" },
            new object[] { "r4t:t16", "Fix Return Rate % so it ignores returns with no sale" },
            new object[] { "r4t:t17", "Add synonyms for Net Sales so Copilot finds revenue" },
            new object[] { "r4t:t18", "Split Gross Profit into a base measure and a margin measure" },
            new object[] { "r4t:t19", "Group Brand values into a new column" },
            new object[] { "r4t:t20", "Tidy up the Customer table before publishing" },
            new object[] { "r4t:t21", "Profit per unit as a new measure" },
            new object[] { "r4t:t22", "Net Sales by Channel should be a measure of its own" },
            new object[] { "r4t:t23", "Turn off auto date time" },
            new object[] { "r4t:t24", "Relate Sales to Store on StoreKey" },
            new object[] { "r4t:p1", "where do I add a hierarchy" },
            new object[] { "r4t:p2", "where can I see which measures use Net Sales" },
            new object[] { "r4t:p3", "how do I publish this model" },
            new object[] { "r4t:p4", "where do I set the sort order for MonthName" },
            new object[] { "r4t:p5", "how do I check the model for Copilot" },
            new object[] { "r4t:p6", "where are the model quality rules" },
            new object[] { "r4t:p7", "how do I write a DAX query" },
            new object[] { "r4t:p8", "where do I edit Power Query" },
            new object[] { "r4t:p9", "what depends on the Store table" },
            new object[] { "r4t:p10", "how do I see the relationships" },
            new object[] { "r4t:n1", "what's the weather in Sydney" },
            new object[] { "r4t:n2", "email this to my manager" },
            new object[] { "r4t:n3", "book a meeting for Monday" },
            new object[] { "r4t:n4", "tell me a joke" },
            new object[] { "r4t:n5", "what is the capital of France" },
            new object[] { "r4t:n6", "play some music" },
            new object[] { "r4t:n7", "translate hello into French" },
            new object[] { "r4t:n8", "order a pizza" },
            new object[] { "r3fresh40:t01", "Month Name should sort by Month Number" },
            new object[] { "r3fresh40:t02", "Now sort the months by month number" },
            new object[] { "r3fresh40:t03", "Net Sales per Customer needs two decimal places" },
            new object[] { "r3fresh40:t04", "divide gross profit by net sales and call that Margin %" },
            new object[] { "r3fresh40:t05", "restrict each sales manager to their own region by email" },
            new object[] { "r3fresh40:t06", "Units per Order shows blank for new products, make it show zero" },
            new object[] { "r3fresh40:t07", "Profit per Unit is wrong when filtered by year" },
            new object[] { "r3fresh40:t08", "Average Price per Unit ignores discounts, fix the formula" },
            new object[] { "r3fresh40:t09", "Quick fix: rename Qty to Quantity by hand in Sales" },
            new object[] { "r3fresh40:t10", "Sales Amount should summarise by sum, not average" },
            new object[] { "r3fresh40:t11", "Store Key and Product Key should be hidden by default" },
            new object[] { "r3fresh40:t12", "Next, translate the captions by language for the French report" },
            new object[] { "r3fresh40:t13", "Only managers should see cost by store" },
            new object[] { "r3fresh40:t14", "Break Net Sales down by channel into three new measures" },
            new object[] { "r3fresh40:t15", "Returns by reason code need a description for the finance team" },
            new object[] { "r3fresh40:t16", "Speed up Net Sales by Region" },
            new object[] { "r3fresh40:t17", "Format Net Sales per Store as currency" },
            new object[] { "r3fresh40:t18", "Weighted average price by volume, please" },
            new object[] { "r3fresh40:t19", "add year to date net sales by fiscal year" },
            new object[] { "r3fresh40:t20", "Customer Lifetime Value per segment should use the new churn table" },
            new object[] { "r5new40:r5t01", "Add a Net Sales YTD measure" },
            new object[] { "r5new40:r5t02", "Hide StoreKey in the Sales table" },
            new object[] { "r5new40:r5t03", "Order Count should count distinct OrderNumber" },
            new object[] { "r5new40:r5t04", "Put Gross Profit and Margin % in a Profit folder" },
            new object[] { "r5new40:r5t05", "Sort Quarter by Year then Quarter" },
            new object[] { "r5new40:r5t06", "Change the format of Average Order Value to two decimals" },
            new object[] { "r5new40:r5t07", "Units per Store as a new measure" },
            new object[] { "r5new40:r5t08", "Net Sales vs last year as a measure" },
            new object[] { "r5new40:r5t09", "Remove the Metric Selector table" },
            new object[] { "r5new40:r5t10", "Describe every column in Customer" },
            new object[] { "r5new40:r5t11", "Gross Profit is wrong for returns, fix it" },
            new object[] { "r5new40:r5t12", "Make DayOfWeek sort by DayOfWeekNumber" },
            new object[] { "r5new40:r5t13", "Rename NetSalesAmount to Net Sales Amount" },
            new object[] { "r5new40:r5t14", "Set the summarize by of UnitPrice to none" },
            new object[] { "r5new40:r5t15", "Turn Channel into a slicer friendly column with no blanks" },
            new object[] { "r5new40:r5t16", "Store managers should only see their own store" },
            new object[] { "r5new40:r5t17", "Check that Net Sales for 2025 is 75249457.11" },
            new object[] { "r5new40:r5t18", "Build a date table from CloseDate" },
            new object[] { "r5new40:r5t19", "Return Rate % needs a description" },
            new object[] { "r5new40:r5t20", "Replace Legacy Discount % with a measure on DiscountAmount" },
            new object[] { "r5new40:r5t21", "Link Returns Amount to the Sales line it came from" },
            new object[] { "r5new40:r5t22", "Mark CustomerCode as a key" },
            new object[] { "r5new40:r5t23", "Add a Total Cost per unit measure" },
            new object[] { "r5new40:r5t24", "Speed up Margin %" },
            new object[] { "r5new40:r5t25", "Split Region into its own table" },
            new object[] { "r5new40:r5t26", "Make Quantity whole numbers" },
            new object[] { "r5new40:r5t27", "Use Time Intelligence for Net Sales last year" },
            new object[] { "r5new40:r5t28", "Clean up the unused columns in Product" },
            new object[] { "r5new40:r5t29", "Net Sales by Segment needs to be faster" },
            new object[] { "r5new40:r5t30", "Give Brand a synonym of label" },
        };

        [Theory]
        [MemberData(nameof(LabelledNotAskData))]
        public void R6_every_labelled_task_is_kept_as_a_task_and_ask_your_data_is_never_its_only_answer(string id, string words)
        {
            var placed = AskRoutePoint.PlaceByWords(new AskRouteResult { Fallback = "menu" }, words, askDataGranted: true);
            var row = PlanTargets.JumpFor(words)?.Tool;

            // The words placed nothing: the Plan's field shows nothing and Enter still keeps the words as a task.
            if (placed == null) { Assert.Null(row); return; }

            // One reading only: it is never Ask your data, and it is the page the row names.
            if (placed.Other == null)
            {
                Assert.True(!string.Equals(AskRoutePoint.MatchWords(words)?.Group, AskRouteCatalog.DataTool, StringComparison.Ordinal),
                    id + " \"" + words + "\" has Ask your data as its ONLY answer");
                Assert.Equal(row, placed.Tool);
                return;
            }

            // Two readings: the task is always one of them, it names the same place as the row Enter adds, and Ask
            // your data is the other, never the only answer.
            var task = AskRoutePoint.ReadingOf(placed, AskRoutePoint.TaskReading);
            var question = AskRoutePoint.ReadingOf(placed, AskRoutePoint.QuestionReading);
            Assert.NotNull(task);
            Assert.NotNull(question);
            Assert.Equal("query", question.Tool);
            Assert.Equal(row, task.Tool);
            // Every one of these is labelled a task, a where-question or a stray line, so the task reading comes
            // first: none of them plainly asks about the numbers.
            Assert.True(string.Equals(placed.Reading, AskRoutePoint.TaskReading, StringComparison.Ordinal),
                id + " \"" + words + "\" shows Ask your data as its FIRST choice, ahead of keeping it as a task");
        }

        /// <summary>A line that is not about the numbers still reads the one honest no-match sentence.</summary>
        [Theory]
        [InlineData("email this model to my manager")]
        [InlineData("what should I have for lunch")]
        public async Task R4_a_line_about_nothing_here_still_reads_the_no_match_sentence(string ask)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteNothing.bim");
            f.UseEmptyRecorded();

            var result = await f.Engine.RouteAskAsync(ask);

            Assert.Null(result.Tool);
            Assert.Equal(AskRoutePoint.NoMatchSentence, result.Sentence);
        }

        /// <summary>fix-askdata-r6, then fix-askdata-final (verify-askdata-r6 F3): which reading is shown FIRST. This is an
        /// order and never a gate: both readings are always offered, so a miss here costs one glance at the second choice
        /// and can no longer lose a task or hide Ask your data. The rule is the ruling's own list and nothing wider: a
        /// question mark, an opening what, which, how, why, when, who or where, an opening "show me" or "list", and the
        /// page's own name. Round 6 also counted "see", "tell me", "compare", "break down", "let me", "give me" and Ask your
        /// data's phrases ANYWHERE in the line, and the verifier's probes showed each of those opening or carrying a task
        /// ("See if gross profit per unit can be added", "Net sales broken down by region is wrong for March"). Those rows
        /// are now false, and the probes are added as rows of their own.</summary>
        [Theory]
        [InlineData("Sort MonthName by MonthNumber", false)]
        [InlineData("Only let managers see Total Cost", false)]
        [InlineData("Units per Order shows blank for new products, make it show zero", false)]
        [InlineData("Month Name should sort by Month Number", false)]
        [InlineData("Profit per Unit is wrong when filtered by year", false)]
        [InlineData("Profit per unit as a new measure", false)]
        [InlineData("Net Sales by Channel should be a measure of its own", false)]
        [InlineData("Quick fix: rename Qty to Quantity by hand in Sales", false)]
        [InlineData("can you hide StoreKey", false)]
        [InlineData("Weighted average price by volume, please", false)]
        [InlineData("Totals per region are double counting", false)]
        [InlineData("Can we group customers into new vs returning", false)]
        [InlineData("Two decimal places on Average Order Value", false)]
        [InlineData("units by brand in 2024", false)]
        [InlineData("store sales by month", false)]
        [InlineData("total sales by region", false)]
        [InlineData("Units per order would be handy on the dashboard", false)]
        [InlineData("Net sales per store two decimals", false)]
        [InlineData("Net sales broken down by region is wrong for March", false)]
        [InlineData("Units split by channel don't add up", false)]
        [InlineData("The count of how many customers per region is off", false)]
        [InlineData("See if gross profit per unit can be added", false)]
        [InlineData("Break down net sales by channel into its own table", false)]
        [InlineData("Show Margin % by brand as a percentage with one decimal", false)]
        [InlineData("Tell me when net sales per store drops below target", false)]
        [InlineData("let me see net sales by region", false)]
        [InlineData("give me net sales by region", false)]
        [InlineData("I need to see sales by region for last year", false)]
        [InlineData("net sales broken down by region for 2025", false)]
        [InlineData("compare channels for 2025", false)]
        [InlineData("show me gross profit by brand", true)]
        [InlineData("please show me net sales by region", true)]
        [InlineData("can you show me net sales by region", true)]
        [InlineData("list the stores with no sales in March", true)]
        [InlineData("which region sold the most in 2025", true)]
        [InlineData("how many customers bought twice", true)]
        [InlineData("What were net sales by region", true)]
        [InlineData("where did returns grow fastest", true)]
        [InlineData("average order value per month in 2024?", true)]
        [InlineData("ask your data please", true)]
        [InlineData("I need ask your data", true)]
        [InlineData("go to ask your data", true)]
        [InlineData("please use ask your data for net sales", true)]
        // The page's name asks only as the opening: a complaint that mentions it does not (Sol on fix-askdata-final).
        [InlineData("Totals in Ask your data are wrong", false)]
        [InlineData("check net sales in ask your data", false)]
        [InlineData("", false)]
        public void Final_a_line_is_read_as_a_question_first_only_when_it_plainly_asks(string words, bool asks)
        {
            Assert.Equal(asks, AskRoutePoint.AsksAQuestion(words));
            Assert.Equal(asks, AskRoutePoint.QuestionReadFirst(words));
        }

        /// <summary>Final batch: the Data page's noun "value" won "average order value per month in 2024?" for the page
        /// that browses sample rows. A question's shape (a number split by something, or an amount asked for) beats the
        /// Data page, because Data shows rows and answers no question; a line with neither still goes to Data.
        ///
        /// Two columns since fix-askdata-r6, because the words PLACING a line and the reading shown FIRST are now two
        /// different things. "fix the blank values per region in Sales" is still redirected off Data by its shape, and
        /// the task reading is still what a person is shown first, because the line tells rather than asks; Ask your
        /// data is the second choice instead of being hidden.</summary>
        [Theory]
        [InlineData("average order value per month in 2024?", "askdata", "query")]
        // fix-askdata-final (verify-askdata-r6 F3): these two do not plainly ask, so the task is read first and names
        // the page its words won; Ask your data is still placed, as the second choice.
        [InlineData("average order value by customer segment", "askdata", "data")]
        [InlineData("total value of returns by store", "askdata", "data")]
        [InlineData("show me the rows in Sales", "data", "data")]
        [InlineData("preview the values in the Product table", "data", "data")]
        // "by default" is never a split (verify-askdata-r4 V4: nothing failed when that exception was reverted).
        [InlineData("preview the rows in Sales sorted by default", "data", "data")]
        // A task the Data page's words won reads as a task first and stays on Data (fix-askdata-r5, then r6).
        [InlineData("fix the blank values per region in Sales", "askdata", "data")]
        public void R4_a_question_shape_beats_the_data_page_s_nouns(string ask, string placed, string first)
        {
            // `placed` is the word group that won; the page a person is sent to is Query for either Query group.
            Assert.Equal(placed, AskRoutePoint.MatchWords(ask)?.Group);
            Assert.Equal(first, AskRoutePoint.PlaceByWords(new AskRouteResult { Fallback = "menu" }, ask, askDataGranted: true)?.Tool);
        }

        /// <summary>A plan target is a task, so where it is done by hand comes from the pages' own words only: the
        /// data-question shape must not send "sort Month Name by Month Number" to Ask your data.</summary>
        [Theory]
        [InlineData("Month Name should sort by Month Number")]
        [InlineData("units per brand")]
        // Final batch: the catalogue's own words ("sales by") used to send this task to Ask your data. Tasks stay tasks.
        [InlineData("Speed up Net Sales by Region")]
        [InlineData("Net Sales by Region as a new measure")]
        public void R4_a_plan_target_that_says_by_is_not_sent_to_ask_your_data(string target)
        {
            AssertJumpIsNotTheQuestion(target);
        }

        /// <summary>verify-askdata-r4 (targeted, 27 September 2026, m11/askdata f2f9996a), left failing on purpose. Ctrl J no
        /// longer routes, so the Plan's field is the one box a person types into that asks route_ask (useAskSuggestion), and
        /// its placeholder is "Describe a task or an aim". D2's fallback (NowhereAsync: AsksAboutTheNumbers over the open
        /// model's names) sends every line that names a field and that no page's words won to Ask your data. So a task typed
        /// there reads "Ask your data is the place for that." with an Open Ask your data button, and a moment later Enter adds
        /// the same words as a row whose jump is none, because PlanTargets.JumpFor refuses Ask your data for a task (the
        /// final batch: "a task is never done there"). The assistant door hears the same wrong place. Each phrasing is from
        /// the verifier's labelled set (sha256 a32af467..., written before any run); on fa4fb162 none of them was sent to Ask
        /// your data. Measured on Northstar's own names: 10 of 24 labelled tasks go there at f2f9996a, 1 at fa4fb162.</summary>
        [Theory]
        [InlineData("Give Net Sales a currency format")]
        [InlineData("Sort MonthName by MonthNumber")]
        [InlineData("Make Margin % show one decimal place")]
        [InlineData("Set Quantity to not summarize")]
        [InlineData("Create a hierarchy Category > Subcategory > ProductName")]
        [InlineData("Only let managers see Total Cost")]
        [InlineData("Fix Return Rate % so it ignores returns with no sale")]
        [InlineData("Turn off auto date time")]
        [InlineData("Relate Sales to Store on StoreKey")]
        [InlineData("where do I set the sort order for MonthName")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Verify_r4_a_task_typed_in_the_plan_field_is_not_sent_to_ask_your_data(string words)
        {
            using var f = new DecisionFixture();
            await f.Engine.CreateModelAsync("VerifyR4Tasks", 1604);
            await f.Engine.CreateTableAsync("Date", "human");
            await f.Engine.CreateColumnAsync("table:Date", "MonthName", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Date", "MonthNumber", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Product", "human");
            await f.Engine.CreateColumnAsync("table:Product", "Category", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Product", "Subcategory", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Product", "ProductName", "String", null, "human");
            await f.Engine.CreateTableAsync("Store", "human");
            await f.Engine.CreateColumnAsync("table:Store", "StoreKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "StoreKey", "Int64", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Net Sales", "SUM ( Sales[Quantity] )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Cost", "SUM ( Sales[Quantity] )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Margin %", "DIVIDE ( [Net Sales] - [Total Cost], [Net Sales] )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Return Rate %", "0", "human");
            await f.SaveAsAsync("VerifyR4Tasks.bim");
            f.UseEmptyRecorded();

            AssertJumpIsNotTheQuestion(words);
            foreach (var (door, result) in new[]
            {
                ("the Plan field (Studio)", await new EngineRpcTarget(f.Engine).routeAsk(words)),
                ("the assistant door", await McpTools.RouteAsk(f.Engine, words)),
            })
            {
                Assert.True(!string.Equals(result.Reading, AskRoutePoint.QuestionReading, StringComparison.Ordinal),
                    $"{door} sends the task \"{words}\" to Ask your data (sentence: {result.Sentence ?? "none"}), which answers questions and changes nothing; the row Enter adds from the same words has no jump");
            }
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("Give Net Sales a currency format")]
        public Task Verify_r4_a_task_typed_in_the_plan_field_is_not_sent_to_ask_your_data_sample(string words) => Verify_r4_a_task_typed_in_the_plan_field_is_not_sent_to_ask_your_data(words);

        /// <summary>verify-askdata-r5 (full, 28 September 2026, m11/askdata add41625), left failing on purpose. The ruling
        /// is that a task typed in the Plan's field is never sent to Ask your data. The task check (ReadsAsATask) knows a
        /// change verb at the start of a clause, a stated need, a fault word and a thing to become; a business person also
        /// writes a task as a complaint with no fault word ("are double counting"), as a request that opens "can we", and
        /// as a bare format ask. Those three read as a question, so the split shape ("per", "vs") or the Data page's
        /// amount redirect ("average" inside the Average Order Value measure's own name) sends them to Ask your data with
        /// no permission, at confidence 1, on both doors, while the row Enter adds from the same words names no place or
        /// Data. From the verifier's 30 labelled lines (sha256 de2f3091..., written before any run): 3 of its 18 tasks go
        /// there. The builder's own 74 labelled tasks all pass, so the check fits the lines it was written against.</summary>
        [Theory]
        [InlineData("Totals per region are double counting")]
        [InlineData("Can we group customers into new vs returning")]
        [InlineData("Two decimal places on Average Order Value")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Verify_r5_a_business_task_typed_in_the_plan_field_is_not_sent_to_ask_your_data(string words)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("VerifyR5Tasks.bim");
            f.UseEmptyRecorded();

            AssertJumpIsNotTheQuestion(words);
            foreach (var (door, result) in new[]
            {
                ("the Plan field (Studio)", await new EngineRpcTarget(f.Engine).routeAsk(words)),
                ("the assistant door", await McpTools.RouteAsk(f.Engine, words)),
            })
            {
                // KEPT FROM THE VERIFIER'S TEST: neither door may make Ask your data the answer to these three.
                Assert.True(!string.Equals(result.Reading, AskRoutePoint.QuestionReading, StringComparison.Ordinal),
                    $"{door} sends the task \"{words}\" to Ask your data (confidence {result.Confidence}, asked {result.Asked}) ahead of keeping it as a task; the row Enter adds from the same words names {PlanTargets.JumpFor(words)?.Tool ?? "no place"}");
                // ADDED for the ruling that replaced the "never" one (coordinator, 28 September): the task reading is
                // first and agrees with the row, and Ask your data is still named as the second choice.
                Assert.Equal(AskRoutePoint.TaskReading, result.Reading);
                Assert.Equal(PlanTargets.JumpFor(words)?.Tool, result.Tool);
                Assert.Equal("query", result.Other?.Tool);
                Assert.Equal(AskRoutePoint.AskItAsAQuestion, result.Other?.Line);
            }
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("Totals per region are double counting")]
        public Task Verify_r5_a_business_task_typed_in_the_plan_field_is_not_sent_to_ask_your_data_sample(string words) => Verify_r5_a_business_task_typed_in_the_plan_field_is_not_sent_to_ask_your_data(words);

        /// <summary>verify-askdata-r5 (full, 28 September 2026, m11/askdata add41625), left failing on purpose. Kane, 28
        /// September: Jev stays on for data questions. The task check reads "need", "please" and "broken" anywhere in a line
        /// as a task, and add41625 holds back every Ask your data answer for such a line, the catalogue's own ("sales by")
        /// and the service's alike. So a question asked the way people ask ("I need to see sales by region for last year",
        /// from the verifier's labelled set; "please show me net sales by region"; "net sales broken down by region for
        /// 2025") never reaches Ask your data from the assistant door, even with permission given and the service placing
        /// it there, and the assistant is told "These words ask for a change to the model". f2f9996a and fa4fb162 placed
        /// all three on Ask your data by the catalogue's words alone.</summary>
        [Theory]
        [InlineData("I need to see sales by region for last year")]
        [InlineData("please show me net sales by region")]
        [InlineData("net sales broken down by region for 2025")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Verify_r5_a_data_question_asked_politely_reaches_ask_your_data_from_the_assistant_door(string words)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("VerifyR5Polite.bim");
            await f.ConsentAsync();

            // Where the words place the question the service is never asked; where they do not, the service places it on
            // Ask your data at 0.93, so the words alone cannot make this pass or fail.
            var probe = f.UseEmptyRecorded();
            var first = await McpTools.RouteAsk(f.Engine, words);
            var (studio, assistant) = probe.AskedDigests.Count == 0
                ? (await new EngineRpcTarget(f.Engine).routeAsk(words), first)
                : await RouteWithServiceAsync(f, words, "askdata", 0.93);
            foreach (var (door, result) in new[] { ("the assistant door", assistant), ("the Plan field (Studio)", studio) })
            {
                // THE VERIFIER'S CHECK, as the ruling now words it (verify-askdata-r6: "reaches it as a choice from both
                // doors"): each door places the data question on Ask your data. Round 5 had one reading, so "reaches" was
                // the top tool; now it is either reading.
                var question = AskRoutePoint.ReadingOf(result, AskRoutePoint.QuestionReading);
                Assert.True(string.Equals(question?.Tool, "query", StringComparison.Ordinal),
                    $"{door} does not place the data question \"{words}\" on Ask your data although the service did at 0.93 "
                    + $"(tool {result.Tool ?? "none"}, reason: {result.Reason ?? "none"})");
                // And keeping it as a task is still named beside it.
                Assert.NotNull(AskRoutePoint.ReadingOf(result, AskRoutePoint.TaskReading));
                // fix-askdata-final (verify-askdata-r6 F3): first only where the line plainly asks. Round 6 read all three
                // question first; "I need to see ..." and "... broken down by ..." do not open with the ruling's words.
                Assert.Equal(AskRoutePoint.AsksAQuestion(words) ? AskRoutePoint.QuestionReading : AskRoutePoint.TaskReading, result.Reading);
            }
            Assert.True(AskRoutePoint.AsksAQuestion("please show me net sales by region"));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("I need to see sales by region for last year")]
        public Task Verify_r5_a_data_question_asked_politely_reaches_ask_your_data_from_the_assistant_door_sample(string words) => Verify_r5_a_data_question_asked_politely_reaches_ask_your_data_from_the_assistant_door(words);



        /// <summary>fix-askdata-r6, then fix-askdata-final (verify-askdata-r6 F3): a question the WORDS place nowhere and the
        /// service places on Ask your data. Round 6 read it question first because the service placed it, and that is what
        /// bent the ruling: what placed a line never decides the order, only whether the line plainly asks. "best selling
        /// product" does not plainly ask, so the task is first, and Ask your data is the second choice at the SERVICE'S OWN
        /// number, on 'other'. The same service answer over a line that does plainly ask puts the question first, at the
        /// same number, on top.</summary>
        [Theory]
        [InlineData("best selling product", false)]
        [InlineData("which product sells best", true)]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Final_a_question_only_the_service_places_is_ordered_by_its_words_at_the_service_s_number(string words, bool first)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteServiceQuestion.bim");
            await f.ConsentAsync();
            Assert.Null(AskRoutePoint.MatchWords(words));
            Assert.Equal(first, AskRoutePoint.AsksAQuestion(words));

            var (studio, assistant) = await RouteWithServiceAsync(f, words, "askdata", 0.91);
            foreach (var result in new[] { studio, assistant })
            {
                Assert.True(result.Asked);
                Assert.Equal(first ? AskRoutePoint.QuestionReading : AskRoutePoint.TaskReading, result.Reading);
                var question = AskRoutePoint.ReadingOf(result, AskRoutePoint.QuestionReading);
                var task = AskRoutePoint.ReadingOf(result, AskRoutePoint.TaskReading);
                Assert.Equal("query", question?.Tool);
                Assert.Equal(0.91, question.Confidence, 6);
                Assert.Equal(PlanTargets.JumpFor(words)?.Tool, task?.Tool);
                Assert.Equal(first ? 0.91 : 0, result.Confidence, 6);
            }
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("best selling product", false)]
        public Task Final_a_question_only_the_service_places_is_ordered_by_its_words_at_the_service_s_number_sample(string words, bool first) => Final_a_question_only_the_service_places_is_ordered_by_its_words_at_the_service_s_number(words, first);

        /// <summary>fix-askdata-r6, then fix-askdata-final: where Ask your data is not part of the tier, the question reading
        /// goes to Queries with the quiet Pro line, first or second by the same rule, and the task reading is unchanged.
        /// Grants are all or nothing today, so this is the day a narrower tier is sold rather than any session shipping now.
        /// It replaces the test of AskRoutePoint.DataQuestionPlace, which had no engine caller left (verify-askdata-r6 L8).</summary>
        [Fact]
        public void R6_without_ask_your_data_the_question_reading_goes_to_the_query_page()
        {
            var second = AskRoutePoint.PlaceByWords(new AskRouteResult { Fallback = "menu" }, "total sales by region", askDataGranted: false);
            Assert.Equal(AskRoutePoint.TaskReading, second.Reading);
            Assert.Null(second.Tool);
            Assert.Equal("query", second.Other?.Tool);
            Assert.Equal(AskRoutePoint.AskItAsAQuestion + " " + AskRoutePoint.DataQuestionProLine, second.Other.Line);

            var first = AskRoutePoint.PlaceByWords(new AskRouteResult { Fallback = "menu" }, "show me total sales by region", askDataGranted: false);
            Assert.Equal(AskRoutePoint.QuestionReading, first.Reading);
            Assert.Equal("query", first.Tool);
            Assert.Equal(AskRoutePoint.DataQuestionProLine, first.Sentence);
            Assert.Null(first.Fallback);
            Assert.Null(first.Reason);
            var task = AskRoutePoint.ReadingOf(first, AskRoutePoint.TaskReading);
            Assert.NotNull(task);
            Assert.Null(task.Tool);
            Assert.DoesNotContain("\u2014", first.Sentence, StringComparison.Ordinal);
        }

        /// <summary>fix-askdata-r6, the ruling's point 6: two lines the catalogue answered with the wrong page.
        /// "report builders" and "report view" are Power BI's canvas and the people on it, not the Reports tab kept
        /// with this model, so one word of them no longer wins Reports. And "power query" is where Power Query is
        /// edited: "query" scoring for Queries inside that phrase used to beat it.</summary>
        [Theory]
        [InlineData("Hide all the ID columns so report builders don't see them", null)]
        [InlineData("Hide CustomerKey and ProductKey from report view", null)]
        [InlineData("where do I edit Power Query", "mcode")]
        [InlineData("where do I change the Power Query steps", "mcode")]
        // Still true, and the reason blanking beats deleting the bare word "report" from the catalogue.
        [InlineData("where are the reports", "evidence")]
        [InlineData("where are the saved reports", "evidence")]
        [InlineData("which reports use Margin %", "lineage")]
        // The Copilot test (15 Oct 2026) claims its own phrases, and a bare "copilot" stays with AI readiness.
        [InlineData("run the copilot test", "copilottest")]
        [InlineData("where is the question bank", "copilottest")]
        [InlineData("is the model ready for copilot", "readiness")]
        public void R6_the_catalogue_names_the_page_the_words_are_actually_about(string words, string tool)
        {
            Assert.Equal(tool, AskRoutePoint.MatchCatalog(words)?.Tool);
            Assert.Equal(tool, PlanTargets.JumpFor(words)?.Tool);
        }


        /// <summary>fix-askdata-r6, the ruling's point 3: route_ask's own description says what the code does. It used
        /// to tell an assistant that words asking for a change to the model "are never sent to 'askdata'", which stopped
        /// being true the moment both readings started travelling together, and was already the sentence the engine
        /// printed under lines it was false for (verify-askdata-r5 V5, F2).</summary>
        [Fact]
        public void R6_route_ask_s_description_says_what_the_code_does()
        {
            var d = typeof(McpTools).GetMethod(nameof(McpTools.RouteAsk))
                .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

            Assert.DoesNotContain("never sent to", d, StringComparison.Ordinal);
            Assert.DoesNotContain("ask for a change to the model", d, StringComparison.Ordinal);
            Assert.Contains("BOTH readings", d, StringComparison.Ordinal);
            Assert.Contains("'other'", d, StringComparison.Ordinal);
            Assert.Contains("'task'", d, StringComparison.Ordinal);
            Assert.Contains("'question'", d, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2014", d, StringComparison.Ordinal);
        }

        /// <summary>verify-askdata-r6 (full, 28 September 2026, m11/askdata 5a1d6a9e), left failing on purpose. The ruling:
        /// the Plan's field and the row Enter adds must agree about the task. They agree wherever the WORDS place a line,
        /// because both now read AskRoutePoint.TaskPlace. They do not where the SERVICE places it: with permission and Jev
        /// on (this release), a task the words place nowhere is sent to the service, the service names the page the work
        /// is done on, and the field reads "Advanced modelling is the place for that." with Open Advanced modelling, while
        /// the row Enter adds from the same words reads "Semanticus does not know a page for this one." (PlanTasks.JumpFor
        /// reads the words only). PlanTargets.JumpFor's comment says the service's page "is a page for the QUESTION reading
        /// and never for the task"; for these lines it is the task's page. Both lines are tasks from the verifier's r5
        /// labelled set (k07, k16), which the words place nowhere; with Jev on in r5, k07 read Advanced modelling live.</summary>
        [Theory]
        [InlineData("The ops team should only see their own stores", "advmodels")]
        [InlineData("Descriptions for every measure so people know what they mean", "docs")]
        public async Task Verify_r6_the_row_enter_adds_names_the_page_the_plan_field_named_for_it(string words, string page)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("VerifyR6FieldRow.bim");
            await f.ConsentAsync();
            Assert.Null(AskRoutePoint.MatchWords(words));

            var (studio, assistant) = await RouteWithServiceAsync(f, words, page, 0.9);
            // fix-askdata-final: the request follows what Enter now sends (plan.tsx sends the page the field showed, askbox.tsx
            // fieldTaskPage), as this test's own note allows; the assertion below is unchanged.
            var added = await f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = words, FieldPage = FieldTaskPage(studio) });
            var row = PlanQueueKit.Row(await f.Engine.GetModelPlanAsync(), added.ItemId);
            foreach (var (door, result) in new[] { ("the Plan field (Studio)", studio), ("the assistant door", assistant) })
            {
                Assert.True(result.Asked);
                Assert.True(string.Equals(result.Tool, row.Jump?.Tool, StringComparison.Ordinal),
                    $"{door} names {result.Tool ?? "no page"} for the task \"{words}\" (the service's answer, asked {result.Asked}), "
                    + $"and the row Enter adds from the same words names {row.Jump?.Tool ?? "no page (\"Semanticus does not know a page for this one.\")"}");
            }
        }

        /// <summary>verify-askdata-r6, left failing on purpose. The coordinator's ruling (28 September): where the words can
        /// be a task and Ask your data places them, Ask your data is the named SECOND choice, "never with the sentence
        /// 'Ask your data is the place for that.'", and it may be the first choice only for a line that plainly asks (a
        /// question mark, or an opening what, which, how, why, when, who, where, show me, list). QuestionReadFirst also
        /// reads the question first when the words placed the line as a question and TellsSomething finds nothing, so a
        /// task with no telling word opens on Ask your data on both doors, with no permission: "Units per order would be
        /// handy on the dashboard" is from the verifier's 20 new labelled lines (n03, sha256 fc324e3c..., committed
        /// before any run) and "Net sales per store two decimals" is its r5 probe p15, which the fix report names as the
        /// one known case. AsksAQuestion also outranks TellsSomething, and it fires on Ask your data's phrases ANYWHERE in a
        /// line ("broken down by", "split by", "how many"), so a complaint that uses one opens on Ask your data too: the last
        /// two are the verifier's attack probes a01 and a02, written after reading the lists. None of the four plainly
        /// asks. Enter still keeps them, and "Keep it as a task." is still named.</summary>
        [Theory]
        [InlineData("Units per order would be handy on the dashboard")]
        [InlineData("Net sales per store two decimals")]
        [InlineData("Net sales broken down by region is wrong for March")]
        [InlineData("Units split by channel don't add up")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Verify_r6_a_task_that_does_not_plainly_ask_is_not_answered_with_ask_your_data_first(string words)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("VerifyR6Order.bim");
            f.UseEmptyRecorded();
            // Not plainly a question in the ruling's own terms: no question mark, and no opening what, which, how, why,
            // when, who, where, show me or list.
            var opening = words.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.False(words.TrimEnd().EndsWith("?", StringComparison.Ordinal));
            Assert.DoesNotContain(opening[0], new[] { "what", "which", "how", "why", "when", "who", "where", "list" });
            Assert.False(opening[0] == "show" && opening.Length > 1 && opening[1] == "me");

            foreach (var (door, result) in new[]
            {
                ("the Plan field (Studio)", await new EngineRpcTarget(f.Engine).routeAsk(words)),
                ("the assistant door", await McpTools.RouteAsk(f.Engine, words)),
            })
            {
                Assert.Equal("query", AskRoutePoint.ReadingOf(result, AskRoutePoint.QuestionReading)?.Tool);
                Assert.True(string.Equals(result.Reading, AskRoutePoint.TaskReading, StringComparison.Ordinal),
                    $"{door} answers the task \"{words}\" with Ask your data FIRST (tool {result.Tool}, reading {result.Reading}; "
                    + "the field reads \"Ask your data is the place for that.\"), although the line does not plainly ask");
            }
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("Units per order would be handy on the dashboard")]
        public Task Verify_r6_a_task_that_does_not_plainly_ask_is_not_answered_with_ask_your_data_first_sample(string words) => Verify_r6_a_task_that_does_not_plainly_ask_is_not_answered_with_ask_your_data_first(words);

        private static string RouteAskDescription() =>
            typeof(McpTools).GetMethod(nameof(McpTools.RouteAsk))
                .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

        /// <summary>verify-askdata-r6, left failing on purpose. route_ask's description tells the assistant how to read the
        /// order: "The question reading is shown first only when the line plainly asks (a question mark, a question word,
        /// 'show me', 'list', or Ask your data's own phrase such as 'broken down by'); otherwise the task reading is first".
        /// The code (QuestionReadFirst) also puts the question first for any line the words place as a question that tells
        /// nothing, which is most data questions: the description's OWN examples of lines placed on 'askdata' by their words
        /// ('units per brand', 'customer count by city', 'total sales by region', 'top 10 products') none plainly asks, and
        /// every one comes back question first. Offline over every labelled set, 283 of the 307 question-first lines do
        /// not plainly ask. Either the code follows the sentence or the sentence says what the code does; this passes
        /// both ways.</summary>
        [Theory]
        [InlineData("units per brand")]
        [InlineData("customer count by city")]
        [InlineData("total sales by region")]
        [InlineData("top 10 products")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Verify_r6_route_ask_s_description_states_the_order_the_code_returns(string words)
        {
            var d = RouteAskDescription();
            Assert.Contains("'" + words + "'", d, StringComparison.Ordinal);
            using var f = new DecisionFixture();
            await f.BuildModelAsync("VerifyR6Describe.bim");
            f.UseEmptyRecorded();
            Assert.False(AskRoutePoint.AsksAQuestion(words));

            var result = await McpTools.RouteAsk(f.Engine, words);
            Assert.Equal("query", AskRoutePoint.ReadingOf(result, AskRoutePoint.QuestionReading)?.Tool);
            const string claim = "The question reading is shown first only when the line plainly asks";
            Assert.False(d.Contains(claim, StringComparison.Ordinal)
                    && string.Equals(result.Reading, AskRoutePoint.QuestionReading, StringComparison.Ordinal),
                $"route_ask's description says \"{claim}\", and its own example \"{words}\" does not plainly ask, "
                + $"yet it comes back with the question reading first (tool {result.Tool}, reading {result.Reading})");
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("units per brand")]
        public Task Verify_r6_route_ask_s_description_states_the_order_the_code_returns_sample(string words) => Verify_r6_route_ask_s_description_states_the_order_the_code_returns(words);

        /// <summary>verify-askdata-r6, left failing on purpose. route_ask's description says "'confidence' is 1 when the
        /// words decided and nothing was asked, and the service's own number when it was asked". Where the service places a
        /// task on Ask your data (the fix round's own case, "Give Total Sales a currency format" at 0.95), the task reading
        /// is on top with no page, so the result reads asked true, tool none and confidence 0, and the service's 0.95 sits
        /// only on 'other'. Before this round "asked true, no tool, confidence X" meant the service left the line
        /// unplaced at X (R5_a_line_the_service_leaves_unplaced...), so an assistant reading the top fields as the
        /// description teaches is told the service placed nothing, at 0.</summary>
        [Fact]
        public async Task Verify_r6_route_ask_s_description_states_the_confidence_the_code_returns()
        {
            var d = RouteAskDescription();
            using var f = new DecisionFixture();
            await f.BuildModelAsync("VerifyR6Confidence.bim");
            await f.ConsentAsync();
            const string words = "Give Total Sales a currency format";
            var (studio, assistant) = await RouteWithServiceAsync(f, words, "askdata", 0.95);
            const string claim = "the service's own number when it was asked";
            foreach (var (door, result) in new[] { ("the assistant door", assistant), ("the Plan field (Studio)", studio) })
            {
                Assert.True(result.Asked);
                Assert.False(d.Contains(claim, StringComparison.Ordinal) && Math.Abs(result.Confidence - 0.95) > 1e-6,
                    $"route_ask's description says 'confidence' is \"{claim}\"; {door} was answered by the service at 0.95 "
                    + $"and reads confidence {result.Confidence} (tool {result.Tool ?? "none"}, reading {result.Reading}, "
                    + $"other {result.Other?.Tool} at {result.Other?.Confidence})");
            }
        }

        /// <summary>A plan target is a task, so the Query page is only ever its place when the WRITING words won (create a
        /// measure, speed up a formula). When the words of a question about the numbers won ("Speed up Net Sales by Region"
        /// says "sales by"), the row names no page. Before the two pages became one this was "never Ask your data".</summary>
        private static void AssertJumpIsNotTheQuestion(string words)
        {
            var jump = PlanTargets.JumpFor(words);
            if (jump?.Tool == AskRouteCatalog.QueryPage)
                Assert.Equal(AskRouteCatalog.QueriesTool, AskRoutePoint.MatchCatalog(words)?.Group);
        }

        /// <summary>1.6: Explore > Query replaced Ask your data and Queries. No id and no label of either old page leaves the
        /// engine: not on the words' answer, not on a reading, not on the line a person reads, not on a create thing, not on
        /// a plan jump. A saved link, plan or older extension that still names an old id lands on the Query page instead.</summary>
        [Theory]
        [InlineData("total sales by region", true)]
        [InlineData("total sales by region", false)]
        [InlineData("show me total sales by region", false)]
        [InlineData("how much did we sell in March", true)]
        [InlineData("create a measure", true)]
        [InlineData("I want to write some DAX", false)]
        [InlineData("average order value by customer segment", true)]
        public void The_old_page_names_never_leave_the_engine(string ask, bool askData)
        {
            var retiredIds = new[] { AskRouteCatalog.DataTool, AskRouteCatalog.QueriesTool };
            var retiredLabels = new[] { "Ask your data", "Queries" };
            var placed = AskRoutePoint.PlaceByWords(new AskRouteResult { Fallback = "menu" }, ask, askData);
            Assert.NotNull(placed);

            var strings = new List<string> { placed.Tool, placed.Area, placed.Sentence, placed.Create };
            var readings = new[] { AskRoutePoint.ReadingOf(placed, AskRoutePoint.TaskReading), AskRoutePoint.ReadingOf(placed, AskRoutePoint.QuestionReading) };
            foreach (var reading in readings.Where(r => r != null))
            {
                strings.Add(reading.Tool); strings.Add(reading.Area); strings.Add(reading.Line);
            }
            foreach (var text in strings.Where(x => x != null))
            {
                Assert.DoesNotContain(text, retiredIds);
                foreach (var label in retiredLabels) Assert.DoesNotContain(label, text, StringComparison.Ordinal);
                Assert.DoesNotContain("\u2014", text, StringComparison.Ordinal);
            }
            // The question reading is always the Query page; a task reading names whatever page its words name (or none).
            var question = readings[1];
            if (question != null) Assert.Equal(AskRouteCatalog.QueryPage, question.Tool);
        }

        [Fact]
        public void The_query_page_is_one_page_and_the_old_ids_are_aliases_that_land_on_it()
        {
            var query = AskRouteCatalog.Find("query");
            Assert.NotNull(query);
            Assert.Equal("explore", query!.Area);
            Assert.Equal("Query", query.Label);
            Assert.False(query.IsTab);
            Assert.Same(query, AskRouteCatalog.Find("askdata"));
            Assert.Same(query, AskRouteCatalog.Find("daxlab"));
            Assert.Equal("explore", AskRouteCatalog.AreaOf("daxlab"));
            Assert.DoesNotContain(AskRouteCatalog.Tools, t => t.Id == "askdata" || t.Id == "daxlab" || t.Label == "Queries" || t.Label == "Ask your data");

            // The two word groups still exist underneath, with their old ids, so the question and the create request
            // keep being told apart; the service is shown the same options as before.
            Assert.Equal(new[] { "askdata", "daxlab" }, AskRouteCatalog.WordGroups.Where(g => AskRouteCatalog.PageOf(g.Id) == "query").Select(g => g.Id).ToArray());

            foreach (var thing in AskRouteCatalog.CreateThings.Where(t => t.Group == AskRouteCatalog.QueriesTool))
                Assert.Equal("query", thing.Tool);
            Assert.DoesNotContain(AskRouteCatalog.CreateThings, t => t.Tool == "askdata" || t.Tool == "daxlab");
            Assert.DoesNotContain("Ask your data", AskRoutePoint.DataQuestionProLine, StringComparison.Ordinal);
            Assert.DoesNotContain("Queries", AskRoutePoint.DataQuestionProLine, StringComparison.Ordinal);
            Assert.Contains("Query", AskRoutePoint.DataQuestionProLine, StringComparison.Ordinal);
        }

        /// <summary>A task saved from an old page still jumps and still reads as the page that replaced it.</summary>
        [Fact]
        public async Task A_task_saved_from_an_old_page_jumps_to_the_query_page()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("QuerySourcePage.bim");
            f.UseEmptyRecorded();
            var added = await f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = "Check the totals", SourcePage = "daxlab" });
            var row = PlanQueueKit.Row(await f.Engine.GetModelPlanAsync(), added.ItemId);
            Assert.Equal("query", row.Jump?.Tool);
            Assert.Equal("Query", row.Jump?.Label);
            Assert.Equal("query", row.SourcePage);
        }

        /// <summary>The page the Plan's field shows for the TASK in an answer, which is what Enter sends with the words:
        /// the one reading's place where there is one reading, and the task reading's place where there are two. The same
        /// rule as askbox.tsx fieldTaskPage, which test/ask-box.test.mjs holds to these shapes.</summary>
        private static string FieldTaskPage(AskRouteResult result) =>
            result.Reading == null
                ? result.Tool
                : AskRoutePoint.ReadingOf(result, AskRoutePoint.TaskReading)?.Tool;

        /// <summary>fix-askdata-final (verify-askdata-r6 F1). With Jev on, the six labelled tasks the verifier saw disagree
        /// live (mcp-31), each replayed with the page and the number Jev gave it: the service names the page the work is
        /// done on and the field shows it. Enter now sends that page with the words (plan.tsx, askbox.tsx fieldTaskPage),
        /// and the row names it. Enter does not route the words again, so it can never come back with a different page.</summary>
        [Theory]
        [InlineData("The ops team should only see their own stores", "advmodels", 0.88)]
        [InlineData("Is it possible to hide the cost columns from the sales team", "advmodels", 0.91)]
        [InlineData("Turn Channel into a slicer friendly column with no blanks", "mcode", 0.91)]
        [InlineData("Store managers should only see their own store", "advmodels", 0.87)]
        [InlineData("Only let managers see Total Cost", "advmodels", 0.96)]
        [InlineData("Relate Sales to Store on StoreKey", "diagram", 0.92)]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Final_the_row_enter_adds_names_the_page_jev_named_in_the_field(string words, string page, double confidence)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("FinalFieldRow.bim");
            await f.ConsentAsync();
            Assert.Null(AskRoutePoint.MatchWords(words));
            Assert.Null(PlanTargets.JumpFor(words));

            var (studio, assistant) = await RouteWithServiceAsync(f, words, page, confidence);
            Assert.Equal(page, FieldTaskPage(studio));
            Assert.Equal(page, FieldTaskPage(assistant));

            var added = await f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = words, FieldPage = FieldTaskPage(studio) });
            var row = PlanQueueKit.Row(await f.Engine.GetModelPlanAsync(), added.ItemId);
            Assert.Equal(page, row.Jump?.Tool);
            Assert.Equal(AskRouteCatalog.Find(page).Label, row.Jump?.Label);
            // The source mark stays "your words": the page is where it is done, not where it was sent from.
            Assert.Null(row.SourcePage);
            Assert.Equal(PlanSources.Words, row.Source);
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("The ops team should only see their own stores", "advmodels", 0.88)]
        public Task Final_the_row_enter_adds_names_the_page_jev_named_in_the_field_sample(string words, string page, double confidence) => Final_the_row_enter_adds_names_the_page_jev_named_in_the_field(words, page, confidence);

        /// <summary>fix-askdata-final (verify-askdata-r6 F1): the field and the row agree in every other shape too, and in
        /// each of them the row names no page the field did not show.
        ///   The service placed the line on Ask your data: the task reading is the words' own, so no page, on both.
        ///   The service answered below its bar, or placed it nowhere: the field shows nothing and the row names nothing.</summary>
        [Theory]
        [InlineData("Give Total Sales a currency format", "askdata", 0.95)]
        [InlineData("The ops team should only see their own stores", "advmodels", 0.62)]
        [InlineData("The ops team should only see their own stores", AskRouteCatalog.NoTool, 0.97)]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Final_the_row_and_the_field_agree_whatever_the_service_said(string words, string choice, double confidence)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("FinalFieldRowShapes.bim");
            await f.ConsentAsync();
            var (studio, assistant) = await RouteWithServiceAsync(f, words, choice, confidence);
            Assert.Null(FieldTaskPage(studio));
            Assert.Null(FieldTaskPage(assistant));

            var added = await f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = words, FieldPage = FieldTaskPage(studio) });
            Assert.Null(PlanQueueKit.Row(await f.Engine.GetModelPlanAsync(), added.ItemId).Jump);
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("Give Total Sales a currency format", "askdata", 0.95)]
        public Task Final_the_row_and_the_field_agree_whatever_the_service_said_sample(string words, string choice, double confidence) => Final_the_row_and_the_field_agree_whatever_the_service_said(words, choice, confidence);

        /// <summary>fix-askdata-final (verify-askdata-r6 F1): what the engine keeps of a field page. Ask your data is never a
        /// row's page (it answers questions and changes nothing), a page this build does not know is not kept, and neither
        /// stops the task being added: the row reads the words, as it always did. A Send's own page still wins. And the
        /// engine's own answers ("what changed") name their page in the field, which Enter sends like any other.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Final_a_field_page_is_kept_only_when_it_is_a_real_page_for_a_task()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("FinalFieldPageKept.bim");
            f.UseEmptyRecorded();
            const string words = "The ops team should only see their own stores";
            async Task<ModelPlanItem> Add(PlanTaskRequest r)
            {
                var added = await f.Engine.AddPlanTaskAsync(r);
                return PlanQueueKit.Row(await f.Engine.GetModelPlanAsync(), added.ItemId);
            }

            Assert.Null((await Add(new PlanTaskRequest { Words = words })).Jump);
            // "askdata" is the retired page that only answered a question; an older link may still send it, and it is dropped
            // as before. "query" and the retired "daxlab" are where a calculation is written, so both are kept as Query.
            Assert.Null((await Add(new PlanTaskRequest { Words = words, FieldPage = "askdata" })).Jump);
            Assert.Equal("query", (await Add(new PlanTaskRequest { Words = words, FieldPage = "query" })).Jump?.Tool);
            Assert.Equal("Query", (await Add(new PlanTaskRequest { Words = words, FieldPage = "daxlab" })).Jump?.Label);
            Assert.Null((await Add(new PlanTaskRequest { Words = words, FieldPage = "no-such-page" })).Jump);
            Assert.Equal("advmodels", (await Add(new PlanTaskRequest { Words = words, FieldPage = " advmodels " })).Jump?.Tool);
            Assert.Equal("diagram", (await Add(new PlanTaskRequest { Words = words, SourcePage = "diagram", FieldPage = "advmodels" })).Jump?.Tool);

            var state = await new EngineRpcTarget(f.Engine).routeAsk("what changed");
            Assert.Equal("history", FieldTaskPage(state));
            Assert.Equal("history", (await Add(new PlanTaskRequest { Words = "what changed", FieldPage = FieldTaskPage(state) })).Jump?.Tool);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
        }

        /// <summary>fix-askdata-final (verify-askdata-r6 F1, and Sol on this round): a task longer than a title, or with a
        /// line break, keeps the page the field named for its WHOLE text. The field routes the whole text (plan.tsx sends
        /// P.fieldSubmit's words, the same string Enter adds); the row's title is only its start, and reading the words of
        /// the title alone lost the page. Both lines here are routed by the service and named Advanced modelling.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Final_a_long_task_keeps_the_page_the_field_named_for_its_whole_text(bool lineBreak)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("FinalFieldRowLong.bim");
            await f.ConsentAsync();
            var words = lineBreak
                ? "The ops team should only see their own stores" + (char)10 + "Starting with the North"
                : string.Concat(Enumerable.Repeat("The ops team should only see their own stores. ", 7)) + "starting with the North";
            Assert.True(words.Length <= AskRoutePoint.MaxAskChars);
            Assert.True(lineBreak || words.Length > PlanTasks.MaxWordsChars);

            var (studio, _) = await RouteWithServiceAsync(f, words, "advmodels", 0.9);
            Assert.Equal("advmodels", FieldTaskPage(studio));
            var added = await f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = words, FieldPage = FieldTaskPage(studio) });
            Assert.Equal("advmodels", PlanQueueKit.Row(await f.Engine.GetModelPlanAsync(), added.ItemId).Jump?.Tool);
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData(false)]
        public Task Final_a_long_task_keeps_the_page_the_field_named_for_its_whole_text_sample(bool lineBreak) => Final_a_long_task_keeps_the_page_the_field_named_for_its_whole_text(lineBreak);

        /// <summary>fix-askdata-final (Sol's second pass): a task longer than a title whose field reading names NO page
        /// gives a row that names none either. The field reads the whole text, where "sales by region" places the line on
        /// Ask your data and the task reading names no page; the row used to read its 280-character title, which loses
        /// that phrase, and named Find and replace for the "Rename" left in it.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Final_a_long_task_the_field_names_no_page_for_gets_a_row_with_none()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("FinalFieldRowLongNone.bim");
            f.UseEmptyRecorded();
            var words = "Rename " + string.Concat(Enumerable.Repeat("the old label ", 20)) + "for sales by region";
            Assert.True(words.Length > PlanTasks.MaxWordsChars && words.Length <= AskRoutePoint.MaxAskChars);

            var field = await new EngineRpcTarget(f.Engine).routeAsk(words);
            Assert.Null(FieldTaskPage(field));
            var added = await f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = words, FieldPage = FieldTaskPage(field) });
            var row = PlanQueueKit.Row(await f.Engine.GetModelPlanAsync(), added.ItemId);
            Assert.True(row.Jump == null, $"the row names {row.Jump?.Tool} where the field named no page");
        }

        /// <summary>fix-askdata-final (verify-askdata-r6 F3). The ruling's order over every labelled line the verifier wrote
        /// for round 6 (docs/notes/askdata-r6/labels-verifier-r6.json, committed in 5ccb6d68 before any run) and round 5
        /// (docs/notes/askdata-r5/labels-new40.json): with no permission, every line Ask your data places carries both
        /// readings, the question is first exactly where the line plainly asks, and the task reading names the page the row
        /// names. A labelled task that does not plainly ask never opens on Ask your data.</summary>
        [Fact]
        public async Task Final_every_labelled_line_is_ordered_by_the_ruling_on_both_doors()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("FinalLabelled.bim");
            f.UseEmptyRecorded();
            var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var lines = new List<(string Id, string Label, string Words)>();
            foreach (var file in new[] { "docs/notes/askdata-r6/labels-verifier-r6.json", "docs/notes/askdata-r5/labels-new40.json" })
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, file)));
                foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
                    lines.Add((item.GetProperty("id").GetString(), item.GetProperty("label").GetString(), item.GetProperty("words").GetString()));
            }
            Assert.Equal(60, lines.Count);

            var placed = 0;
            foreach (var (id, label, words) in lines)
            {
                foreach (var (door, result) in new[]
                {
                    ("the Plan field (Studio)", await new EngineRpcTarget(f.Engine).routeAsk(words)),
                    ("the assistant door", await McpTools.RouteAsk(f.Engine, words)),
                })
                {
                    var question = AskRoutePoint.ReadingOf(result, AskRoutePoint.QuestionReading);
                    if (question == null)
                    {
                        Assert.True(!string.Equals(result.Reading, AskRoutePoint.QuestionReading, StringComparison.Ordinal), $"{id} \"{words}\": Ask your data alone on {door}");
                        continue;
                    }
                    placed++;
                    Assert.Equal("query", question.Tool);
                    Assert.Equal(AskRoutePoint.AsksAQuestion(words) ? AskRoutePoint.QuestionReading : AskRoutePoint.TaskReading, result.Reading);
                    Assert.Equal(PlanTargets.JumpFor(words)?.Tool, AskRoutePoint.ReadingOf(result, AskRoutePoint.TaskReading)?.Tool);
                    if (label == "task" && !AskRoutePoint.AsksAQuestion(words))
                        Assert.True(result.Reading == AskRoutePoint.TaskReading, $"{id} \"{words}\" opens on Ask your data on {door}");
                }
            }
            Assert.True(placed > 0);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
        }

        /// <summary>fix-askdata-final (verify-askdata-r6 F2): route_ask's description is tested against what the code does,
        /// not only for sentences that must be gone. THE ORDER: the description names the openings a line plainly asks with,
        /// and the code's own lists are exactly those; each named opening puts the question first on both doors, and each
        /// opening round 6 also counted, which the description does not name, puts the task first.</summary>
        [Fact]
        public async Task Final_route_ask_s_description_states_the_order_the_code_returns()
        {
            var d = RouteAskDescription();
            Assert.Contains("the question reading is first only when the line plainly asks", d, StringComparison.Ordinal);
            Assert.Contains("never from what placed it", d, StringComparison.Ordinal);
            Assert.Contains("opens with what, which, how, why, when, who or where", d, StringComparison.Ordinal);
            Assert.Contains("opens with 'show me' or 'list'", d, StringComparison.Ordinal);
            Assert.Contains("opens by asking for the question box by name", d, StringComparison.Ordinal);
            Assert.Contains("ends in a question mark", d, StringComparison.Ordinal);
            Assert.Contains("'total sales by region' reads task first", d, StringComparison.Ordinal);
            // The code's lists hold exactly what the sentence names (and the three spellings of the same words).
            Assert.Equal(new[] { "how", "what", "whats", "when", "where", "which", "who", "whom", "whose", "why" },
                AskRoutePoint.QuestionWords.OrderBy(w => w, StringComparer.Ordinal).ToArray());
            Assert.Equal(new[] { "list", "show me" },
                AskRoutePoint.AskingStarts.Select(a => string.Join(" ", a)).OrderBy(w => w, StringComparer.Ordinal).ToArray());

            using var f = new DecisionFixture();
            await f.BuildModelAsync("FinalDescribeOrder.bim");
            f.UseEmptyRecorded();
            var named = new[] { "what are", "which are", "how are", "why are", "when were", "who has", "where are", "show me", "list",
                                "please show me", "can you list" };
            var notNamed = new[] { "see", "tell me", "compare", "break down", "give me", "let me see", "show", "i need to see" };
            foreach (var (openings, first) in new[] { (named, true), (notNamed, false) })
                foreach (var opening in openings)
                {
                    var words = opening + " net sales by region";
                    foreach (var result in new[] { await McpTools.RouteAsk(f.Engine, words), await new EngineRpcTarget(f.Engine).routeAsk(words) })
                    {
                        Assert.Equal("query", AskRoutePoint.ReadingOf(result, AskRoutePoint.QuestionReading)?.Tool);
                        Assert.True((first ? AskRoutePoint.QuestionReading : AskRoutePoint.TaskReading) == result.Reading,
                            $"\"{words}\" reads {result.Reading} first; the description says {(first ? "question" : "task")}");
                    }
                }
            foreach (var words in new[] { "net sales by region?", "ask your data for net sales by region", "total sales by region" })
            {
                var result = await McpTools.RouteAsk(f.Engine, words);
                Assert.Equal(words != "total sales by region" ? AskRoutePoint.QuestionReading : AskRoutePoint.TaskReading, result.Reading);
            }
        }

        /// <summary>fix-askdata-final (verify-askdata-r6 F2): THE CONFIDENCE. Every statement the description makes about
        /// 'confidence' and 'other.confidence' is checked against the answer the code gives, one shape each: the words
        /// decided (1); the service named one page (its number, on top); the service placed Ask your data and the line plainly
        /// asks (its number, on top); the service placed Ask your data and the task is first (no tool at 0 on top, its number
        /// on 'other', with a 'reading'); and the service left the line unplaced (its number, no 'reading', no 'other').</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Final_route_ask_s_description_states_the_confidence_the_code_returns()
        {
            var d = RouteAskDescription();
            Assert.Contains("'confidence' is the top reading's and 'other.confidence' the second one's", d, StringComparison.Ordinal);
            Assert.Contains("A reading the words or the engine's own numbers decided is 1", d, StringComparison.Ordinal);
            Assert.Contains("a reading the service decided carries the service's own number", d, StringComparison.Ordinal);
            Assert.Contains("a task reading that names no page is 0", d, StringComparison.Ordinal);
            Assert.Contains("the service's number is 'other.confidence' and the top reads no tool at 0", d, StringComparison.Ordinal);
            Assert.Contains("that answer has a 'reading', which a line the service left unplaced does not", d, StringComparison.Ordinal);
            Assert.DoesNotContain("the service's own number when it was asked", d, StringComparison.Ordinal);

            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync("FinalConfWords.bim");
                f.UseEmptyRecorded();
                var words = await McpTools.RouteAsk(f.Engine, "what uses this measure");
                Assert.False(words.Asked); Assert.Equal("lineage", words.Tool); Assert.Equal(1, words.Confidence); Assert.Null(words.Reading);
                var taskFirst = await McpTools.RouteAsk(f.Engine, "total sales by region");
                Assert.Equal(0, taskFirst.Confidence); Assert.Null(taskFirst.Tool); Assert.Equal(1, taskFirst.Other.Confidence);
            }
            foreach (var (text, choice, reading, top, other) in new (string, string, string, double, double?)[]
            {
                ("The ops team should only see their own stores", "advmodels", null, 0.88, null),
                ("what were sales last month?", "askdata", AskRoutePoint.QuestionReading, 0.95, 0),
                ("Give Total Sales a currency format", "askdata", AskRoutePoint.TaskReading, 0, 0.95),
                ("order a pizza", AskRouteCatalog.NoTool, null, 0.97, null),
            })
            {
                using var f = new DecisionFixture();
                await f.BuildModelAsync("FinalConfService.bim");
                await f.ConsentAsync();
                var confidence = choice == "advmodels" ? 0.88 : choice == AskRouteCatalog.NoTool ? 0.97 : 0.95;
                var (studio, assistant) = await RouteWithServiceAsync(f, text, choice, confidence);
                foreach (var result in new[] { studio, assistant })
                {
                    Assert.True(result.Asked);
                    Assert.Equal(reading, result.Reading);
                    Assert.Equal(top, result.Confidence, 6);
                    if (other == null) Assert.Null(result.Other); else Assert.Equal(other.Value, result.Other.Confidence, 6);
                    if (reading == AskRoutePoint.TaskReading) Assert.Null(result.Tool);
                }
            }
        }

        /// <summary>AI instructions is a page in its own right now, not a panel buried inside the Data agent
        /// setup, so the box can send somebody to it. Its words are the ones a person types when they mean the
        /// standing instructions rather than the AI readiness findings.</summary>
        [Theory]
        [InlineData("ai instructions")]
        [InlineData("where do I set the instructions")]
        public void The_ai_instructions_page_is_reachable_on_words_alone(string ask)
        {
            Assert.Equal("instructions", AskRoutePoint.MatchWords(ask)!.Tool);
        }

        /// <summary>"Review the model" is the general "is this built well" question, so it lands on Model
        /// quality (BPA) and not on AI readiness, which answers the narrower "will an assistant answer well".
        /// Only one place claims the area's own word, because a word two places claim makes a tie and a tie
        /// sends nobody anywhere.</summary>
        [Fact]
        public void The_area_word_review_lands_on_model_quality()
        {
            Assert.Equal("bpa", AskRoutePoint.MatchWords("review the model")!.Tool);

            var claimed = AskRouteCatalog.Tools
                .Where(t => t.Words.Nouns.Contains("review", StringComparer.Ordinal))
                .Select(t => t.Id).ToArray();
            Assert.Equal(new[] { "bpa" }, claimed);

            // And it does not swallow the page that is actually about a set of pending edits.
            Assert.Equal("optimize", AskRoutePoint.MatchWords("review the pending changes")!.Tool);
        }

        /// <summary>The tool ids the area strip really carries, parsed out of App.tsx. The strip is the whole
        /// definition of "a place a person can be sent", so it is read rather than copied.</summary>
        private static List<string> StripTools()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.VSCode", "webview", "src", "App.tsx"));
            var start = src.IndexOf("const TAB_GROUPS", StringComparison.Ordinal);
            Assert.True(start > 0, "App.tsx no longer declares TAB_GROUPS.");
            var end = src.IndexOf("\n];", start, StringComparison.Ordinal);
            Assert.True(end > start, "TAB_GROUPS is not closed where this test expects it.");

            var ids = Regex.Matches(src.Substring(start, end - start), @"\{ id: '([a-z]+)', label: '[^']+' \}")
                .Select(m => m.Groups[1].Value).ToList();
            Assert.True(ids.Count > 10, "only " + ids.Count + " strip tools were parsed; the shape has changed");
            return ids;
        }

        /// <summary>The Plan page is a place the box can send somebody, and the words that mean it are the
        /// words a person types when they have no idea what to do next. It was the one strip tool with no
        /// catalogue entry, which is a page nobody could ever be routed to.</summary>
        [Fact]
        public void The_plan_page_is_in_the_catalogue_and_its_words_reach_it()
        {
            var plan = AskRouteCatalog.Find("plan");
            Assert.NotNull(plan);
            Assert.Equal("plan", plan!.Area);
            Assert.Equal("Plan", plan.Label);
            Assert.Equal("plan", AskRoutePoint.MatchWords("what should I do next")!.Tool);
        }

        [Fact]
        public void Every_place_offers_a_criterion_and_none_of_them_talks_about_the_product_state()
        {
            var questions = AskRoutePoint.Questions();
            var options = Assert.Single(questions).Options;
            // Every word group, plus none. The Query page is two groups (a question about the numbers, and writing a
            // calculation), so there is one more option than there are places.
            Assert.Equal(AskRouteCatalog.WordGroups.Count + 1, options.Count);
            Assert.Equal(AskRouteCatalog.Tools.Count + 1, AskRouteCatalog.WordGroups.Count);
            Assert.Contains(options, o => o.Id == AskRouteCatalog.NoTool);

            foreach (var option in options)
            {
                Assert.False(string.IsNullOrWhiteSpace(option.Criterion));
                Assert.DoesNotContain("—", option.Criterion, StringComparison.Ordinal);
                // A criterion describes the world, never us. These three words would each be the product
                // telling the service what it hopes to hear.
                foreach (var leak in new[] { "most people", "usually chosen", "recommended" })
                    Assert.DoesNotContain(leak, option.Criterion, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void The_option_order_is_fixed_so_two_identical_asks_are_one_piece_of_evidence()
        {
            var first = new DecisionRequest(DecisionRegistry.AskRoute, 1, AskRoutePoint.Snapshot("where do I fix a measure"), AskRoutePoint.Questions());
            var again = new DecisionRequest(DecisionRegistry.AskRoute, 1, AskRoutePoint.Snapshot("where do I fix a measure"), AskRoutePoint.Questions());
            Assert.Equal(first.Digest, again.Digest);

            var other = new DecisionRequest(DecisionRegistry.AskRoute, 1, AskRoutePoint.Snapshot("something else"), AskRoutePoint.Questions());
            Assert.NotEqual(first.Digest, other.Digest);
        }

        // ---- the questions the engine answers itself -------------------------------------------------

        [Fact]
        public void A_question_the_state_answers_is_answered_from_the_state()
        {
            var facts = new AskDeterministicFacts
            {
                ModelName = "Contoso", TableCount = 5, MeasureCount = 39, SizeBytes = 42L * 1024 * 1024,
            };

            var answer = AskRoutePoint.AnswerFromState("How big is this model?", facts);
            Assert.NotNull(answer);
            Assert.Equal("stats", answer!.Tool);
            Assert.Equal("explore", answer.Area);
            Assert.Equal(1, answer.Confidence);
            Assert.False(answer.Asked);              // nothing was sent to answer it
            Assert.Contains("39 measures", answer.Sentence, StringComparison.Ordinal);
            Assert.Contains("42 MB", answer.Sentence, StringComparison.Ordinal);
        }

        [Fact]
        public void An_unknown_count_is_said_to_be_unknown_rather_than_reported_as_zero()
        {
            // No count was read: the suite could not be read, or saved checks are Pro and this is a free
            // session where the engine does not try. Either way nobody looked, and the answer used to say
            // the model had none, which is a claim about a model nobody had looked at.
            var answer = AskRoutePoint.AnswerFromState("how many checks", new AskDeterministicFacts());
            Assert.NotNull(answer);
            Assert.Equal("tests", answer!.Tool);
            Assert.Contains("not known", answer.Sentence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("has no saved checks", answer.Sentence, StringComparison.Ordinal);
        }

        [Fact]
        public void A_count_that_really_is_zero_still_reads_as_none()
        {
            var answer = AskRoutePoint.AnswerFromState("how many checks",
                new AskDeterministicFacts { SavedCheckCount = 0 });
            Assert.Equal("This model has no checks yet.", answer!.Sentence);

            var one = AskRoutePoint.AnswerFromState("how many checks",
                new AskDeterministicFacts { SavedCheckCount = 1 });
            Assert.Equal("This model has one check.", one!.Sentence);
        }

        [Theory]
        [InlineData("what changed")]
        [InlineData("What did I change?")]
        [InlineData("how many tests")]
        public void The_three_families_are_matched_on_their_declared_phrasings(string ask)
        {
            Assert.NotNull(AskRoutePoint.AnswerFromState(ask, new AskDeterministicFacts { UnsavedChangeCount = 2, SavedCheckCount = 7 }));
        }

        [Theory]
        [InlineData("how big is the margin measure")]
        [InlineData("what changed in the sales table last week")]
        [InlineData("")]
        public void Anything_that_is_not_one_of_the_three_is_left_to_be_routed(string ask)
        {
            Assert.Null(AskRoutePoint.AnswerFromState(ask, new AskDeterministicFacts { TableCount = 1, MeasureCount = 1 }));
        }

        // ---- the no-service path ---------------------------------------------------------------------

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task With_no_service_the_box_hands_back_the_menu_and_says_why()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRoute.bim");
            f.UseNull();

            // Deliberately a sentence the WORDS cannot place. The ask this test used to make ("where do I
            // check my numbers against the source") now matches Validation on the word "check" and is
            // answered locally, which is the fix and not a regression, so the no-service path needs an ask
            // that really has nowhere to go.
            var result = await f.Engine.RouteAskAsync("email this model to my manager");

            Assert.Null(result.Tool);
            Assert.Equal("menu", result.Fallback);
            Assert.False(result.Asked);
            Assert.False(string.IsNullOrWhiteSpace(result.Reason));
            Assert.Equal(0, result.Confidence);
            Assert.DoesNotContain("—", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public async Task An_ask_longer_than_the_box_reads_is_refused_and_nothing_is_sent()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteLong.bim");
            var oracle = f.UseEmptyRecorded();
            await f.ConsentAsync();

            var result = await f.Engine.RouteAskAsync(new string('a', AskRoutePoint.MaxAskChars + 1));

            Assert.Null(result.Tool);
            Assert.Equal("menu", result.Fallback);
            Assert.False(result.Asked);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);   // the refusal happened before any ask
            Assert.NotNull(oracle);
        }

        // ---- the first pass: plain words, no service, no permission ----------------------------------
        //
        // This is the fix for what Kane hit on the Yoga build on 21 September. He typed "create a measure"
        // on the Overview of a real model and read back "You have not given permission to send this model's
        // details for advice." and nothing else. Everything that was not one of the fixed phrasings above
        // went straight to the decision service, so with no permission the gate's own sentence WAS the
        // answer to his question. These tests are about the pass that now sits in front of it.

        [Theory]
        // The ask, the place it lands, and the thing it asks to make (null when it asks to make nothing).
        [InlineData("create a measure", "query", "measure")]
        [InlineData("add a new measure", "query", "measure")]
        [InlineData("I want to write some DAX", "query", null)]
        // "breaks" is the noun carrying this one and "rename" is only the verb, so it is about impact.
        [InlineData("what breaks if I rename Total Sales", "lineage", null)]
        [InlineData("mark the date table", "advmodels", null)]
        [InlineData("who can see this", "advmodels", null)]
        [InlineData("create a role", "advmodels", "role")]
        [InlineData("add a calendar", "advmodels", "calendar")]
        [InlineData("set up a calculation group", "advmodels", "calculation group")]
        [InlineData("show me the rows in Sales", "data", null)]
        [InlineData("which tables use the most memory", "stats", null)]
        [InlineData("where do I publish", "deploy", null)]
        [InlineData("undo what I just did", "history", null)]
        // "run the best practice rules" and not "check the model for problems": that second one is a real
        // tie between Validation ("check") and Model quality ("problems"), and a tie is two plausible doors,
        // which is exactly the case the second pass exists for.
        [InlineData("run the best practice rules", "bpa", null)]
        // The five things the Model tree makes. Each one names itself, so the page can run the tree's own
        // command instead of opening a page that cannot make it (Kane, 22 September).
        [InlineData("create a calculated column", "query", "calculated column")]
        [InlineData("create a calculated table", "query", "calculated table")]
        [InlineData("add a function", "query", "function")]
        [InlineData("new calculation item", "advmodels", "calculation item")]
        // A DEPENDENCY QUESTION IS ABOUT THE DEPENDENCY, not about the kind of thing it names (Codex P2 on
        // PR 386). Every one of these used to land somewhere else, or nowhere, because the relation word
        // was worth less than the noun beside it.
        [InlineData("what uses this measure", "lineage", null)]
        [InlineData("what depends on Sales", "lineage", null)]
        [InlineData("which reports use Margin %", "lineage", null)]
        [InlineData("where is Total Sales used", "lineage", null)]
        [InlineData("what uses the date table", "lineage", null)]
        // The Plan page, which was in the strip and in the help page and in no catalogue at all.
        [InlineData("what should I do next", "plan", null)]
        [InlineData("what is left to do", "plan", null)]
        [InlineData("show me the roadmap", "plan", null)]
        [InlineData("what are my priorities", "plan", null)]
        public void The_words_alone_place_the_common_asks(string ask, string tool, string create)
        {
            var match = AskRoutePoint.MatchWords(ask);
            Assert.True(match != null, "'" + ask + "' matched no place on words alone.");
            Assert.Equal(tool, match!.Tool);
            Assert.Equal(tool == "query" ? AskRouteCatalog.QueriesTool : tool, match.Group);   // creating or writing is the writing group
            Assert.Equal(create, match.Create);
            Assert.True(match.Score >= 2, "the bar is 2; '" + ask + "' scored " + match.Score);
            Assert.True(match.Score > match.RunnerUp,
                "'" + ask + "' scored " + match.Score + " against a runner-up of " + match.RunnerUp
                + ", which is not a clear winner");
        }

        /// <summary>The scoring rule that makes a dependency question a dependency question, asserted as
        /// arithmetic rather than as an outcome.
        ///
        /// A RELATION WORD IS THE SUBJECT OF THE QUESTION. "What uses this measure" is not a question about
        /// measures, it is a question about what uses things, and the thing it names is only the object.
        /// So a relation word has to outrank a noun, and it has to outrank a two-word noun PHRASE as well,
        /// or "what uses the date table" would be a tie with Advanced Modelling and answer nobody.</summary>
        [Fact]
        public void A_relation_word_outranks_the_noun_it_is_asked_about()
        {
            var aboutOneThing = AskRoutePoint.MatchWords("what uses this measure");
            Assert.Equal("lineage", aboutOneThing!.Tool);
            Assert.True(aboutOneThing.Score > aboutOneThing.RunnerUp + 1,
                "a relation word must beat a bare noun by more than a rounding: scored "
                + aboutOneThing.Score + " against " + aboutOneThing.RunnerUp);

            // The runner-up here is a two-word noun phrase ("date table"), which is worth more than a
            // single noun. The relation word still wins, and a tie would send this question nowhere.
            var aboutAPhrase = AskRoutePoint.MatchWords("what uses the date table");
            Assert.Equal("lineage", aboutAPhrase!.Tool);
            Assert.True(aboutAPhrase.Score > aboutAPhrase.RunnerUp);

            // And the weight does not swallow a question that only happens to contain the word. "Use" in
            // "use the most memory" is consumption, not dependency, and Size says so in its own
            // words rather than by a rule about which tool wins ties.
            Assert.Equal("stats", AskRoutePoint.MatchWords("which tables use the most memory")!.Tool);
        }

        /// <summary>Singular and plural really are one word, including the one that matters most here.
        /// "Uses" used to stem to "us", so the relation verb that makes a dependency question was never
        /// matched at all: Lineage scored zero on "what uses this measure", not one.</summary>
        [Theory]
        [InlineData("what uses this measure")]
        [InlineData("what use this measure")]
        [InlineData("what used this measure")]
        public void A_relation_word_is_matched_however_it_is_spelled(string ask)
        {
            Assert.Equal("lineage", AskRoutePoint.MatchWords(ask)!.Tool);
        }

        /// <summary>The two rename asks. A question about impact still lands on Lineage. A bare "rename Total
        /// Sales" claims no page now that Find and replace has left the Studio: one verb is not enough evidence,
        /// so it is left for the assistant.</summary>
        [Fact]
        public void A_rename_that_asks_about_impact_lands_on_lineage_and_a_bare_rename_on_no_page()
        {
            Assert.Equal("lineage", AskRoutePoint.MatchWords("what breaks if I rename Total Sales")!.Tool);
            Assert.Null(AskRoutePoint.MatchWords("rename Total Sales"));
        }

        [Theory]
        // Nothing in the catalogue claims any of these words, so there is nothing to be confident about.
        [InlineData("email this model to my manager")]
        [InlineData("what should I have for lunch")]
        [InlineData("")]
        [InlineData("   ")]
        public void A_line_the_words_cannot_place_is_left_for_the_service(string ask)
        {
            Assert.Null(AskRoutePoint.MatchWords(ask));
        }

        [Fact]
        public void A_create_word_only_names_a_thing_that_belongs_to_the_place_that_won()
        {
            // One create verb, one thing, and the thing's own page won: the thing is named.
            Assert.Equal("calendar", AskRoutePoint.MatchWords("create a calendar")!.Create);
            // A create verb with no thing in it names nothing rather than guessing at one.
            Assert.Null(AskRoutePoint.MatchWords("create a new diagram")!.Create);
            // No create verb at all, so nothing is being made however many things are named.
            Assert.Null(AskRoutePoint.MatchWords("what uses this measure")?.Create);
        }

        /// <summary>A tie is two plausible doors, and these are the ones this catalogue really has. Pinned
        /// rather than broken by a tie-break rule, so the day a word list changes underneath one of them the
        /// test says so. "plan out the tables" is a genuine tie between Model Spec (its verb "plan out") and
        /// Plan (its noun "plan"), and it arrived with the Plan entry on 22 September.</summary>
        [Theory]
        [InlineData("check the model for problems")]
        [InlineData("plan out the tables")]
        public void A_real_tie_is_left_for_the_second_pass(string ask)
        {
            Assert.Null(AskRoutePoint.MatchWords(ask));
        }

        [Fact]
        public void Every_place_carries_words_and_every_create_thing_names_a_real_place()
        {
            foreach (var tool in AskRouteCatalog.Tools)
                Assert.True(tool.Words.Nouns.Count > 0, tool.Id + " has no nouns, so nothing can ever match it");

            foreach (var thing in AskRouteCatalog.CreateThings)
            {
                Assert.NotNull(AskRouteCatalog.Find(thing.Tool));
                Assert.True(thing.Nouns.Count > 0, thing.Name + " has no nouns");
            }

            // A word every tool claims is a word that only makes ties. These three are in every other guide
            // line, and a router that scored them would answer every question with a coin toss.
            foreach (var everywhere in new[] { "model", "page", "thing" })
            {
                var claimed = AskRouteCatalog.Tools
                    .Where(t => t.Words.Nouns.Concat(t.Words.Verbs).Concat(t.Words.Relations)
                        .Any(w => string.Equals(w, everywhere, StringComparison.Ordinal)))
                    .Select(t => t.Id).ToArray();
                Assert.Equal(Array.Empty<string>(), claimed);
            }
        }

        /// <summary>The count questions are answered before the words are looked at, so "how many measures
        /// are there" is still a number and not a trip to the Calculations page on the word "measure".</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task A_count_question_is_still_answered_as_a_count()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteCount.bim");
            var oracle = f.UseEmptyRecorded();
            await f.ConsentAsync();

            var result = await f.Engine.RouteAskAsync("how many measures are there");

            Assert.Contains("2 measures", result.Sentence, StringComparison.Ordinal);
            Assert.Null(result.Tool);                                  // the count is the whole answer
            Assert.False(result.Asked);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
            Assert.NotNull(oracle);
        }

        /// <summary>A word match is a complete answer, and the proof is the counting oracle: the service is
        /// asked nothing at all, even on a session that has Pro, permission and a working oracle. Both the
        /// create ask and the dependency ask are here, because they are the two the box gets most and they
        /// take different paths through the scoring.</summary>
        [Theory]
        [InlineData("create a measure", "query", "explore", "measure")]
        [InlineData("what uses this measure", "lineage", "explore", null)]
        public async Task A_word_match_never_reaches_the_service(string ask, string tool, string area, string create)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteWords.bim");
            f.UseEmptyRecorded();
            await f.ConsentAsync();

            var result = await f.Engine.RouteAskAsync(ask);

            Assert.Equal(tool, result.Tool);
            Assert.Equal(area, result.Area);
            Assert.Equal(create, result.Create);
            Assert.False(result.Asked);
            Assert.Null(result.Fallback);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
        }

        /// <summary>The bug itself. With permission withheld and a line the words cannot place, the person
        /// reads a plain sentence about their words and NOT a sentence about permission. The gate's own
        /// wording still travels, in Reason, where the assistant door and the logs can read it.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task A_gated_service_never_becomes_the_sentence_a_person_reads()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteGated.bim");
            f.UseEmptyRecorded();
            // No ConsentAsync: this is Kane's session, with permission never given.

            var result = await f.Engine.RouteAskAsync("email this model to my manager");

            Assert.Equal(AskRoutePoint.NoMatchSentence, result.Sentence);
            Assert.DoesNotContain("permission", result.Sentence, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("permission", result.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Null(result.Tool);
            Assert.Equal("menu", result.Fallback);
            Assert.False(result.Asked);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
        }

        /// <summary>And with Pro and permission, a line the words cannot place is sent on exactly once. One,
        /// not zero (the second pass still exists) and not two (nothing retries behind a person's back).</summary>
        [Fact]
        public async Task An_ask_the_words_cannot_place_reaches_the_service_exactly_once()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteAsked.bim");
            f.UseEmptyRecorded();
            await f.ConsentAsync();

            var result = await f.Engine.RouteAskAsync("email this model to my manager");

            Assert.Equal(1, f.Engine.Decisions.RequestsStarted);
            Assert.True(result.Asked);
            Assert.Equal(AskRoutePoint.NoMatchSentence, result.Sentence);
        }

        /// <summary>Kane, 22 September: ask is Jev, so it is Pro. A free session is refused the way every
        /// other Pro operation refuses, before any word is matched and before anything is sent.</summary>
        [Fact]
        public async Task A_free_session_is_refused_before_any_word_is_matched()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            await f.BuildModelAsync("AskRouteFree.bim");
            f.UseEmptyRecorded();
            await f.ConsentAsync();

            var refused = await Assert.ThrowsAsync<Semanticus.Engine.Entitlement.EntitlementException>(
                () => f.Engine.RouteAskAsync("create a measure"));

            Assert.Contains("Semanticus Pro", refused.Message, StringComparison.Ordinal);
            Assert.Contains("Ask about this model", refused.Message, StringComparison.Ordinal);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
        }

        /// <summary>The assistant door is told what the first pass is, because an agent that believes every
        /// ask costs permission will not use the one that costs nothing. Read off the real attribute rather
        /// than from a copy of it.</summary>
        [Fact]
        public void The_assistant_door_says_the_first_pass_is_words_and_needs_no_permission()
        {
            var method = typeof(McpTools).GetMethod("RouteAsk",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            var description = method!
                .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

            Assert.Contains("word matching", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("NEEDS NO PERMISSION", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Pro", description, StringComparison.Ordinal);
            Assert.DoesNotContain("—", description, StringComparison.Ordinal);
        }

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task An_empty_box_asks_for_words_rather_than_sending_an_empty_question()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteEmpty.bim");
            f.UseEmptyRecorded();
            await f.ConsentAsync();

            var result = await f.Engine.RouteAskAsync("   ");

            Assert.Null(result.Tool);
            Assert.Equal("menu", result.Fallback);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
        }
    }
}
