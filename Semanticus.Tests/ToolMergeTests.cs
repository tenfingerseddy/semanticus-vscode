using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Semanticus.Engine.Lineage;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The slice 6 tool merge (Kane, 24 Sep, prompt audit decision 2): fourteen near-duplicate MCP tools fold into
    /// their survivors. Only the MCP layer changes; each merged tool calls the same IEngine method the old tool
    /// called, and the UI door keeps its RPC methods. The retired names stay readable in saved files through
    /// OpAliases, and a direct call to one gets a teaching error instead of the SDK's bare refusal.
    /// </summary>
    public sealed class ToolMergeTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        internal static readonly string[] RetiredTools =
        {
            "bpa_summary", "ai_readiness_summary", "ai_readiness_scan_live", "model_graph_summary", "bpa_fix",
            "bpa_get_fix_prompt", "bpa_fix_all", "load_bpa_rules", "load_readiness_rules", "reset_bpa_rules",
            "reset_readiness_rules", "benchmark_dax_coldwarm", "impact_of", "label_connection",
        };

        private static async Task<(LocalEngine engine, SessionManager sessions)> PercentageModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("PctFix", 1604);
            var t = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            var m = await engine.CreateMeasureAsync(t, "Margin", "1", "human");
            await engine.SetMeasureFormatAsync(m, "0%", "human");
            // A fix only fills a BLANK format string, and only on a value the engine proves is a whole number (THE
            // FORMAT FILL RULE, FormatFill.MayFill), so the routing tests fix this count.
            await engine.CreateMeasureAsync(t, "Units", "COUNTROWS ( Facts )", "human");
            return (engine, sessions);
        }

        // ---- step 0a: plan item kinds are not tool names -------------------------------------------------------
        // bpa_fix is BOTH a retired tool name (it folded into apply_fix) and a live change-plan item kind that
        // stored plans carry. The one alias table used to serve both vocabularies, so retiring the tool would have
        // rewritten every stored bpa_fix item to apply_fix, which no plan switch knows: the item would fail.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_bpa_fix_plan_item_still_applies_after_bpa_fix_retired_as_a_tool()
        {
            Assert.Equal("apply_fix", OpAliases.Canonical("bpa_fix"));   // the tool name is retired

            var (engine, sessions) = await PercentageModelAsync();
            using (engine)
            {
                var plan = await engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, origin: "test");
                var item = Assert.Single(plan.Items.Where(i => i.Kind == "bpa_fix" && i.RuleId == "INTEGER_FORMATTING"));
                Assert.Equal("Formatting", item.Category);

                var report = await engine.ApplyPlanAsync(new[] { item.Id }, "human");
                Assert.Equal(0, report.FailedCount);
                var written = await sessions.Require().ReadAsync(m => m.AllMeasures.Single(x => x.Name == "Units").FormatString);
                Assert.Equal("#,0", written);
            }
        }

        [Fact]
        public void Plan_kinds_keep_bpa_fix_and_still_read_the_renamed_enable_qna()
        {
            Assert.Equal("bpa_fix", OpAliases.CanonicalPlanKind("bpa_fix"));
            Assert.Equal("create_linguistic_schema", OpAliases.CanonicalPlanKind("enable_qna"));
            Assert.Equal("set_description", OpAliases.CanonicalPlanKind("set_description"));
        }

        // ---- step 0b: no shipped text names a retired tool --------------------------------------------------------
        // An agent reads engine teaching text, skills, the assistant pack and stock workflows. Any of them naming a
        // retired tool sends the agent to a name that is no longer listed. OpAliases.cs is the one place that must
        // name them. Two literals are other vocabularies that share a spelling: the plan kind "bpa_fix" and the
        // activity kind "label_connection". Frozen compat files, parked workflows and test fixtures are left alone:
        // they are frozen by design and the alias table keeps them valid.

        [Fact]
        public void No_shipped_text_names_a_retired_tool()
        {
            var root = DecisionRegistryTests.RepoRoot();
            var names = new Regex(@"(?<![A-Za-z0-9_])(" + string.Join("|", RetiredTools.Select(Regex.Escape)) + @")(?![A-Za-z0-9_])");
            var allowedLiterals = new HashSet<string>(StringComparer.Ordinal) { "bpa_fix", "label_connection" };
            var hits = new List<string>();

            foreach (var dir in new[] { "Semanticus.Engine", "Semanticus.Analysis" })
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel == "Semanticus.Engine/OpAliases.cs") continue;
                    foreach (var (line, literal) in StringLiterals(File.ReadAllText(file)))
                    {
                        if (allowedLiterals.Contains(literal)) continue;
                        var m = names.Match(literal);
                        if (m.Success) hits.Add($"{rel}:{line} {m.Value}");
                    }
                }

            foreach (var dir in new[] { ".claude/skills", "Semanticus.VSCode/assistant-pack", "Semanticus.Engine/workflows", "Semanticus.Engine/workflow-templates" })
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*", SearchOption.AllDirectories))
                {
                    if (!(file.EndsWith(".md") || file.EndsWith(".json") || file.EndsWith(".yaml") || file.EndsWith(".yml"))) continue;
                    var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    var lines = File.ReadAllLines(file);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        var m = names.Match(lines[i]);
                        if (m.Success) hits.Add($"{rel}:{i + 1} {m.Value}");
                    }
                }

            Assert.True(hits.Count == 0, "shipped text still names a retired tool:\n" + string.Join("\n", hits));
        }

        /// <summary>Every string literal in C# source, with the line it starts on. Comments are skipped. Handles
        /// regular, verbatim (@ and $@) and interpolated strings and char literals; an interpolation hole is kept
        /// as part of the literal's text, which only makes the scan stricter.</summary>
        internal static IEnumerable<(int Line, string Text)> StringLiterals(string src)
        {
            var line = 1;
            var i = 0;
            while (i < src.Length)
            {
                var c = src[i];
                if (c == '\n') { line++; i++; continue; }
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '/') { while (i < src.Length && src[i] != '\n') i++; continue; }
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) { if (src[i] == '\n') line++; i++; }
                    i += 2;
                    continue;
                }
                if (c == '\'')
                {
                    i++;
                    while (i < src.Length && src[i] != '\'') { if (src[i] == '\\') i++; i++; }
                    i++;
                    continue;
                }
                if (c == '"')
                {
                    var verbatim = i > 0 && (src[i - 1] == '@' || (i > 1 && src[i - 1] == '$' && src[i - 2] == '@'));
                    var start = line;
                    var sb = new StringBuilder();
                    i++;
                    while (i < src.Length)
                    {
                        var d = src[i];
                        if (d == '\n') line++;
                        if (verbatim)
                        {
                            if (d == '"' && i + 1 < src.Length && src[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                            if (d == '"') break;
                        }
                        else
                        {
                            if (d == '\\' && i + 1 < src.Length) { sb.Append(d).Append(src[i + 1]); i += 2; continue; }
                            if (d == '"' || d == '\n') break;
                        }
                        sb.Append(d);
                        i++;
                    }
                    i++;
                    yield return (start, sb.ToString());
                    continue;
                }
                i++;
            }
        }

        [Fact]
        public void The_literal_scanner_sees_strings_and_skips_comments()
        {
            var src = "// \"impact_of\" in a comment\nvar a = \"x impact_of y\"; /* \"bpa_fix_all\" */ var b = @\"q \"\"z\"\" \";\nvar c = $\"{n} bpa_summary\";";
            var lits = StringLiterals(src).ToArray();
            Assert.Equal(new[] { "x impact_of y", "q \"z\" ", "{n} bpa_summary" }, lits.Select(l => l.Text).ToArray());
            Assert.Equal(new[] { 2, 2, 3 }, lits.Select(l => l.Line).ToArray());
        }

        // ---- step 0c: a direct call to a retired name teaches the new call --------------------------------------
        // Claude Code will not usually call an unlisted tool, but scripts and saved tool allowlists do. The SDK's own
        // answer to an unknown name is a bare protocol error. This drives the real SDK in process, with the door's
        // own filter registration, to prove a call-tool filter sees the name before the SDK refuses it.

        private static async Task<(IHost host, McpClient client)> StartDoorAsync(IEngine engine)
        {
            var toServer = new Pipe();
            var toClient = new Pipe();
            var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IEngine>(engine);
            builder.Services.AddMcpServer()
                .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
                .WithToolsFromAssembly(typeof(McpTools).Assembly)
                .WithRequestFilters(f => McpDoorFilters.Add(f, engine));
            var host = builder.Build();
            await host.StartAsync();
            var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
            return (host, client);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_direct_call_to_a_retired_tool_names_the_tool_and_argument_that_replaced_it()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake());
            var (host, client) = await StartDoorAsync(engine);
            try
            {
                var listed = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
                Assert.DoesNotContain("impact_of", listed);
                Assert.Contains("impact_assessment", listed);

                var result = await client.CallToolAsync("impact_of", new Dictionary<string, object> { ["objRef"] = "measure:Sales/Total" });
                Assert.True(result.IsError);
                var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
                Assert.Contains("impact_of was merged into impact_assessment", text);
                Assert.Contains("scope='model'", text);

                // A merged tool returns one of two shapes, declared as object; the SDK must serialise the runtime
                // shape, not an empty object.
                await engine.CreateModelAsync("DoorShapes", 1604);
                await engine.CreateTableAsync("Facts", "human");
                var graph = await client.CallToolAsync("get_model_graph", new Dictionary<string, object> { ["detail"] = "summary" });
                Assert.NotEqual(true, graph.IsError);
                var graphText = string.Join("\n", graph.Content.OfType<TextContentBlock>().Select(b => b.Text));
                Assert.Contains("\"TableCount\":1", graphText.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await client.DisposeAsync();
                await host.StopAsync();
                host.Dispose();
            }
        }

        // ---- the surface: 346 tools (get_plan_history added by Plan-heart E4, three Connections tools by slice C, the four draft ops by lane L4, describe_workflow_steps by Kane's UAT on 25 Sep, ask_data and list_data_questions by Jev question-to-DAX on 26 Sep, pick_chart_kind by D6 on 28 Sep, publish_new_model by r1/newmodel2 on 9 Oct; 359 now), none retired, every retired name still resolves ------------------------------

        [Fact]
        public void The_tool_list_carries_no_retired_name_and_each_resolves_to_a_listed_tool()
        {
            var tools = ProFeatureMapTests.CompiledOperationNames();
            Assert.Equal(359, tools.Length);
            foreach (var old in RetiredTools)
            {
                Assert.DoesNotContain(old, tools);
                Assert.NotNull(OpAliases.RetiredToolMessage(old));
                Assert.Contains(old, OpAliases.RetiredIds);
                var current = OpAliases.Canonical(old);
                if (old == "label_connection") Assert.Equal(old, current);   // no successor: it was never an agent capability
                else Assert.Contains(current, tools);
            }
            Assert.Contains("load_rules", tools);
            Assert.Contains("reset_rules", tools);
        }

        [Fact]
        public void The_UI_door_keeps_every_RPC_method_the_merged_tools_sat_beside()
        {
            var rpc = ProFeatureMapTests.RpcEntries().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var name in new[] { "bpaScan", "bpaFix", "bpaFixAll", "bpaGetFixPrompt", "loadBpaRules", "resetBpaRules",
                                         "loadReadinessRules", "resetReadinessRules", "aiReadinessScan", "aiReadinessScanLive",
                                         "applySafeFixes", "applyFix", "getFixPrompt", "getModelGraph", "benchmarkDax",
                                         "benchmarkColdWarm", "impactOf", "impactAssessment", "labelConnection" })
                Assert.Contains(name, rpc);
        }

        // ---- K1 K2 K3: one read with detail='full' or 'summary' ---------------------------------------------------

        private static string J(object o) => System.Text.Json.JsonSerializer.Serialize(o);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Detail_summary_returns_what_the_three_summary_tools_returned()
        {
            var (engine, _) = await PercentageModelAsync();
            using (engine)
            {
                Assert.Equal(J(BpaSummary.From(await engine.BpaScanAsync())), J(await McpTools.BpaScanOp(engine, detail: "summary")));
                Assert.Equal(J(ReadinessSummary.From(await engine.AiReadinessScanAsync())), J(await McpTools.AiReadinessScanOp(engine, detail: "summary")));
                Assert.Equal(J(ModelGraphSummary.From(await engine.GetModelGraphAsync())), J(await McpTools.GetModelGraphOp(engine, detail: "summary")));

                // The default reproduces the old surviving tool exactly.
                Assert.Equal(J(await McpTools.BpaScan(engine)), J(await McpTools.BpaScanOp(engine)));
                Assert.Equal(J(await McpTools.AiReadinessScan(engine)), J(await McpTools.AiReadinessScanOp(engine)));
                Assert.Equal(J(await engine.GetModelGraphAsync()), J(await McpTools.GetModelGraphOp(engine)));
            }
        }

        [Fact]
        public async Task A_filter_beside_detail_summary_is_refused_and_an_unknown_detail_is_refused()
        {
            var (engine, _) = await PercentageModelAsync();
            using (engine)
            {
                var bpa = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.BpaScanOp(engine, detail: "summary", autoFixableOnly: true));
                Assert.Contains("summary has no list to narrow", bpa.Message);
                var air = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.AiReadinessScanOp(engine, detail: "summary", category: "Naming"));
                Assert.Contains("summary has no list to narrow", air.Message);
                var bad = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.GetModelGraphOp(engine, detail: "brief"));
                Assert.Contains("detail must be 'full' or 'summary'", bad.Message);
            }
        }

        [Fact]
        public async Task Live_readiness_offline_gives_the_live_scan_refusal()
        {
            var (engine, _) = await PercentageModelAsync();
            using (engine)
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.AiReadinessScanOp(engine, live: true));
                Assert.Contains("Not connected to a live model", ex.Message);
            }
        }

        // ---- K4 K5 K6 K7 K8: one tool per verb, keyed by system ---------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("x")]
        [InlineData("")]
        [InlineData(null)]
        public async Task System_must_be_bpa_or_air(string system)
        {
            var (engine, _) = await PercentageModelAsync();
            using (engine)
            {
                var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.ApplySafeFixes(engine, system));
                Assert.Contains("system must be 'bpa' (Best Practice Analyzer) or 'air' (AI readiness).", ex.Message);
                await Assert.ThrowsAsync<ArgumentException>(() => McpTools.ResetRules(engine, system));
                await Assert.ThrowsAsync<ArgumentException>(() => McpTools.GetFixPrompt(engine, system, "DESC-MEASURE", "measure:Facts/Margin"));
            }
        }

        [Fact]
        public async Task Apply_fix_routes_bpa_to_the_BPA_fix_and_air_to_the_readiness_fix()
        {
            var (engine, sessions) = await PercentageModelAsync();
            using (engine)
            {
                // INTEGER_FORMATTING is a BPA rule: the air system does not know it, the bpa system fixes it.
                await Assert.ThrowsAnyAsync<Exception>(() => McpTools.ApplyFix(engine, "air", "INTEGER_FORMATTING", "measure:Facts/Units"));
                var r = await McpTools.ApplyFix(engine, "bpa", "INTEGER_FORMATTING", "measure:Facts/Units");
                Assert.True(r.Changed);
                Assert.Equal("#,0", await sessions.Require().ReadAsync(m => m.AllMeasures.Single(x => x.Name == "Units").FormatString));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Get_fix_prompt_returns_what_each_old_tool_returned()
        {
            var (engine, _) = await PercentageModelAsync();
            using (engine)
            {
                var v = (await engine.BpaScanAsync()).Violations.First();
                Assert.Equal(J(await engine.BpaGetFixPromptAsync(v.RuleId, v.ObjectRef)), J(await McpTools.GetFixPrompt(engine, "bpa", v.RuleId, v.ObjectRef)));
                Assert.Equal(J(await engine.GetFixPromptAsync("DESC-MEASURE", "measure:Facts/Margin")),
                    J(await McpTools.GetFixPrompt(engine, "readiness", "DESC-MEASURE", "measure:Facts/Margin")));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Apply_safe_fixes_returns_the_BPA_remainder_for_bpa_and_the_readiness_result_for_air()
        {
            var (engine, _) = await PercentageModelAsync();
            using (engine)
            {
                var bpa = Assert.IsType<BpaFixAllResult>(await McpTools.ApplySafeFixes(engine, "bpa"));
                Assert.NotNull(bpa.Unfixed);
                Assert.Equal(bpa.Unfixed.Length, bpa.Remaining);
                Assert.NotNull(bpa.Scorecard);
                var air = Assert.IsType<SafeFixResult>(await McpTools.ApplySafeFixes(engine, "air"));
                Assert.NotNull(air.Scorecard);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Load_rules_and_reset_rules_round_trip_both_systems()
        {
            var (engine, _) = await PercentageModelAsync();
            using (engine)
            {
                const string air = "[{\"ID\":\"TM-AIR\",\"Category\":\"BestPractice\",\"Scope\":\"Model\",\"Expression\":\"Name <> null\"}]";
                var loaded = Assert.IsType<ReadinessRulesInfo>(await McpTools.LoadRules(engine, "air", air));
                Assert.Equal(1, loaded.ModelRules);
                Assert.Contains((await engine.AiReadinessScanAsync()).Findings, f => f.RuleId == "TM-AIR");
                var reset = Assert.IsType<ReadinessRulesInfo>(await McpTools.ResetRules(engine, "air"));
                Assert.Equal(0, reset.ModelRules);
                Assert.DoesNotContain((await engine.AiReadinessScanAsync()).Findings, f => f.RuleId == "TM-AIR");

                const string bpa = "[{\"ID\":\"TM_BPA\",\"Name\":\"x\",\"Category\":\"Test\",\"Severity\":1,\"Scope\":\"Model\",\"Expression\":\"Name <> null\"}]";
                var std = (await engine.BpaScanAsync()).RuleCount;
                var bl = Assert.IsType<BpaRulesInfo>(await McpTools.LoadRules(engine, "bpa", bpa));
                Assert.Equal(std + 1, bl.ActiveRules);
                var br = Assert.IsType<BpaRulesInfo>(await McpTools.ResetRules(engine, "bpa"));
                Assert.Equal(std, br.ActiveRules);
            }
        }

        // ---- K9: one benchmark, two modes, two activity kinds ----------------------------------------------------
        // The DAX Lab reflects an agent's run by activity kind and casts the result by it, so each mode keeps its kind.

        [Fact]
        public async Task Benchmark_dax_keeps_one_activity_kind_per_mode_and_refuses_an_unknown_mode()
        {
            var (engine, sessions) = await PercentageModelAsync();
            using (engine)
            {
                var kinds = new List<string>();
                sessions.Bus.Activity += a => { lock (kinds) kinds.Add(a.Kind); };
                var warm = await McpTools.BenchmarkDaxOp(engine, "EVALUATE ROW(\"x\", 1)", 2);
                Assert.IsType<BenchmarkResult>(warm);
                var cold = await McpTools.BenchmarkDaxOp(engine, "EVALUATE ROW(\"x\", 1)", 2, mode: "coldWarm");
                Assert.IsType<ColdWarmBenchmark>(cold);
                for (var i = 0; i < 50 && kinds.Count < 2; i++) await Task.Delay(20);   // the broadcast is fire-and-forget
                lock (kinds) Assert.Equal(new[] { "benchmark_dax", "benchmark_coldwarm" }, kinds.ToArray());

                var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.BenchmarkDaxOp(engine, "EVALUATE ROW(\"x\", 1)", 2, mode: "hot"));
                Assert.Contains("mode must be 'warm' or 'coldWarm'", ex.Message);
            }
        }

        // ---- K10: impact_assessment(scope='model') carries everything impact_of returned --------------------------

        [Fact]
        public async Task Impact_assessment_scope_model_carries_the_impact_of_answer()
        {
            var (engine, _) = await PercentageModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Facts", "Double Margin", "[Margin] * 2", "human");
                var old = await engine.ImpactOfAsync("measure:Facts/Margin");
                Assert.NotEmpty(old.Impacted);
                var result = await McpToolsImpactAssessment.ImpactAssessment(engine, "measure:Facts/Margin", scope: "model");
                Assert.Equal(J(old), J(result.ModelImpact));
            }
        }
    }
}
