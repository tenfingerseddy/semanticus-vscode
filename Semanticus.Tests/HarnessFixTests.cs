using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The harness fixes found by driving a LIVE model over the MCP door (2026-07-03/04). Each of these was a
    /// real failure that cost diagnosis time: (1) the deploy gate counted WAIVED BPA violations as blockers,
    /// defeating the waiver lane; (2) the MCP boundary swallowed engine teaching exceptions into a bare
    /// "An error occurred invoking 'X'"; (3) an owner-engine restart killed the attached proxy permanently
    /// (the UI has a restart button, so that's routine); (4) get_object on a table answered only counts —
    /// "does it have a description?" was unanswerable; (5) AI instructions had 4 writers and NO reader.
    /// </summary>
    public sealed class HarnessFixTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // ---- (1) the deploy gate must respect BPA waivers (readiness hard-gates stay raw elsewhere) ---------------

        [Fact]
        public async Task Deploy_gate_does_not_count_waived_bpa_errors_as_blockers()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: true));   // rule-level waive is the Pro lever
            using (engine)
            {
                await engine.OpenAsync(TestModels.FindBim());
                // A severity-3 rule with no FixExpression = exactly the "blocking BPA error" shape the gate counts.
                await engine.LoadBpaRulesAsync(
                    "[{\"ID\":\"TEST-GATE-BLOCK\",\"Name\":\"Test blocker\",\"Category\":\"Test\",\"Description\":\"always fires\",\"Severity\":3,\"Scope\":\"Measure\",\"Expression\":\"Name.Length >= 0\"}]",
                    replace: false, "human");

                // AdventureWorks also trips standard warning-severity rules (those block too), so assert the DELTA
                // attributable to the test rule, not an absolute zero.
                var mine = (await engine.BpaScanAsync()).Violations.Count(v => v.RuleId == "TEST-GATE-BLOCK");
                Assert.True(mine > 0);
                var before = await engine.DeployGateAsync(null);
                Assert.True(before.BpaBlocking >= mine);
                Assert.Contains(before.Blockers, b => b.Contains("blocking BPA error"));

                await engine.WaiveFindingAsync("bpa", "TEST-GATE-BLOCK", "*", "house standard, accepted", "human");

                var after = await engine.DeployGateAsync(null);
                Assert.Equal(before.BpaBlocking - mine, after.BpaBlocking);   // waived ⇒ no longer blockers…
                Assert.True(after.BpaWaivedBlocking >= mine);                 // …but surfaced, never hidden
                Assert.Contains("waived", after.Note);                        // the gate SAYS what it excluded
            }
        }

        // ---- (2) the MCP error boundary surfaces the engine's teaching message, scrubbed ---------------------------

        private static string TextOf(CallToolResult r) => ((TextContentBlock)r.Content.Single()).Text;

        [Fact]
        public async Task Mcp_boundary_surfaces_the_teaching_message()
        {
            var res = await McpErrorBoundary.InvokeAsync("deploy_live", () => throw new InvalidOperationException(
                "deploy_live: blocked by the deploy gate — 2 blocking BPA error(s). Fix the blockers, or pass overrideReason to ship anyway."));
            Assert.True(res.IsError);
            Assert.Contains("deploy_live failed:", TextOf(res));
            Assert.Contains("blocked by the deploy gate", TextOf(res));   // the message that used to die on the wire
        }

        [Fact]
        public async Task Mcp_boundary_unwraps_wrapper_exceptions_to_the_root_cause()
        {
            var root = new InvalidOperationException("Object not found: measure:Sales/Nope — run list_objects to find the exact ref.");
            var wrapped = new System.Reflection.TargetInvocationException(new AggregateException(root));
            var res = await McpErrorBoundary.InvokeAsync("get_object", () => throw wrapped);
            Assert.True(res.IsError);
            Assert.Contains("Object not found", TextOf(res));
            Assert.DoesNotContain("One or more errors occurred", TextOf(res));   // the AggregateException wrapper, not the cause
        }

        [Fact]
        public async Task Mcp_boundary_scrubs_secrets_from_the_message()
        {
            var res = await McpErrorBoundary.InvokeAsync("connect_xmla",
                () => throw new InvalidOperationException("401 calling Fabric with Bearer eyJhbGciOi.eyJzdWIi.c2lnbmF0dXJl — check the token"));
            Assert.True(res.IsError);
            Assert.DoesNotContain("eyJhbGciOi", TextOf(res));
            Assert.Contains("Bearer ***", TextOf(res));
        }

        [Fact]
        public async Task Mcp_boundary_rethrows_cancellation_and_protocol_errors()
        {
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => McpErrorBoundary.InvokeAsync("slow_op", () => throw new OperationCanceledException()).AsTask());
            await Assert.ThrowsAsync<McpProtocolException>(
                () => McpErrorBoundary.InvokeAsync("nope", () => throw new McpProtocolException("Unknown tool: 'nope'", McpErrorCode.InvalidParams)).AsTask());
        }

        // ---- (3) the attached proxy survives an owner-engine restart ----------------------------------------------

        [Fact]
        public async Task Remote_engine_reattaches_after_the_owner_restarts()
        {
            var ws = Path.Combine(Path.GetTempPath(), "semanticus-reattach-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            var pipe1 = "semanticus-reat1-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var pipe2 = "semanticus-reat2-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            static EngineInfo Info(string pipe, string ws) => new EngineInfo
            { PipeName = pipe, Pid = Environment.ProcessId, StartedUtc = DateTime.UtcNow.ToString("o"), Workspace = ws };

            var sessions1 = new SessionManager();
            var owner1 = new LocalEngine(sessions1, new Fake(false), ws);
            var server1 = new RpcServer(sessions1, owner1, pipe1);
            using var cts1 = new CancellationTokenSource();
            var run1 = server1.RunAsync(cts1.Token);
            EngineBroker.WriteInfo(ws, Info(pipe1, ws));

            SessionManager sessions2 = null; LocalEngine owner2 = null; RpcServer server2 = null;
            CancellationTokenSource cts2 = null; Task run2 = null; RemoteEngine remote = null;
            try
            {
                remote = await RemoteEngine.ConnectAsync(pipe1, ws);
                Assert.Equal("free", (await remote.GetEntitlementAsync()).Tier);   // proven live on owner #1

                // The owner restarts (the UI's restart button): old process gone, NEW pipe published.
                cts1.Cancel(); server1.Dispose(); owner1.Dispose();
                try { await run1; } catch { }
                sessions2 = new SessionManager();
                owner2 = new LocalEngine(sessions2, new Fake(true), ws);          // pro, so the reattach is observable
                server2 = new RpcServer(sessions2, owner2, pipe2);
                cts2 = new CancellationTokenSource();
                run2 = server2.RunAsync(cts2.Token);
                EngineBroker.WriteInfo(ws, Info(pipe2, ws));

                // Same proxy, no reconnect ceremony: the call re-resolves the owner and lands on engine #2.
                Assert.Equal("pro", (await remote.GetEntitlementAsync()).Tier);
            }
            finally
            {
                remote?.Dispose();
                cts2?.Cancel(); server2?.Dispose(); owner2?.Dispose();
                if (run2 != null) { try { await run2; } catch { } }
                sessions1.Dispose(); sessions2?.Dispose();
                cts2?.Dispose();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        // ---- (4) get_object answers the authored-metadata questions, not just counts -------------------------------

        [Fact]
        public async Task Get_object_returns_table_and_column_descriptions()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false));
            using (engine)
            {
                await engine.CreateModelAsync("ShapeTest", 1567);
                var t = await engine.CreateTableAsync("Sales", "human");
                await engine.SetDescriptionAsync(t, "The sales fact table.", "human");
                var c = await engine.CreateColumnAsync(t, "Amount", "decimal", "Amount", "human");
                await engine.SetDescriptionAsync(c, "Line amount in AUD.", "human");

                var table = await engine.GetObjectAsync(t);
                Assert.Equal("The sales fact table.", table.Properties["description"]);
                Assert.True(table.Properties.ContainsKey("isHidden"));
                Assert.True(table.Properties.ContainsKey("columns"));   // the original counts survive

                var col = await engine.GetObjectAsync(c);
                Assert.Equal("Line amount in AUD.", col.Properties["description"]);
                Assert.True(col.Properties.ContainsKey("formatString"));
                Assert.True(col.Properties.ContainsKey("summarizeBy"));
                Assert.True(col.Properties.ContainsKey("isKey"));
            }
        }

        // ---- (5) AI instructions finally have a READER (recovering them used to take a raw DMV query) --------------

        [Fact]
        public async Task Get_ai_instructions_round_trips_and_teaches_when_absent()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false));
            using (engine)
            {
                await engine.CreateModelAsync("AiRead", 1567);

                var empty = await engine.GetAiInstructionsAsync(null);
                Assert.False(empty.Present);
                Assert.Contains("No linguistic schema", empty.Note);

                await engine.SetAiInstructionsAsync("Fiscal year starts in July. PY means prior year.", null, "human");
                var read = await engine.GetAiInstructionsAsync(null);
                Assert.True(read.Present);
                Assert.Equal("Fiscal year starts in July. PY means prior year.", read.Instructions);
                Assert.Equal(read.Instructions.Length, read.Length);
                Assert.Equal(10000, read.Limit);
                Assert.NotNull(read.Culture);
            }
        }

        // ---- (6) multi-culture: the reader AND the writer pick the model's DEFAULT culture, not the FIRST ---------
        // Audit finding: PrepForAi / set_ai_instructions selected the first JSON linguistic culture, so a
        // multi-culture model scanned/wrote the WRONG one. Fixed with a single shared selector (default culture →
        // first-with-instructions → first). Neuter: revert FindLinguisticCulture/ReadLsdl to FirstOrDefault and the
        // reader reports fr-FR (no instructions) — this test fails.
        [Fact]
        public async Task Ai_instructions_target_the_default_culture_not_the_first_on_a_multi_culture_model()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false));
            using (engine)
            {
                await engine.CreateModelAsync("MultiCulture", 1567);
                // Two JSON linguistic cultures: fr-FR added FIRST (no instructions); en-US = the model's DEFAULT and
                // the one that carries instructions. The old "first culture" pick would land on fr-FR.
                await sessions.Require().MutateAsync("human", "seed cultures", m =>
                {
                    m.Culture = "en-US";
                    var fr = m.Cultures.Contains("fr-FR") ? m.Cultures["fr-FR"] : m.AddTranslation("fr-FR");
                    fr.Content = "{\"Version\":\"1.0.0\",\"Language\":\"fr-FR\",\"Entities\":{}}";
                    var en = m.Cultures.Contains("en-US") ? m.Cultures["en-US"] : m.AddTranslation("en-US");
                    en.Content = "{\"Version\":\"1.0.0\",\"Language\":\"en-US\",\"CustomInstructions\":\"Answer in USD.\",\"Entities\":{}}";
                });

                // READER selects the default culture (en-US) and SEES its instructions — not the first (fr-FR).
                var info = await engine.GetAiInstructionsAsync(null);
                Assert.Equal("en-US", info.Culture);
                Assert.Equal("Answer in USD.", info.Instructions);

                // WRITER selects the SAME culture (one shared helper), and surfaces which one on the ambiguous model.
                var set = await engine.SetAiInstructionsAsync("Answer in AUD.", null, "agent");
                Assert.Equal("en-US", set.Culture);
                Assert.Contains("en-US", set.Note);
                Assert.Equal("Answer in AUD.", (await engine.GetAiInstructionsAsync(null)).Instructions);

                // fr-FR was never touched — the write did not land on the wrong culture.
                var frInstr = await sessions.Require().ReadAsync(m =>
                {
                    using var d = System.Text.Json.JsonDocument.Parse(m.Cultures["fr-FR"].Content);
                    return d.RootElement.TryGetProperty("CustomInstructions", out var ci) ? ci.GetString() : null;
                });
                Assert.Null(frInstr);
            }
        }
    }
}
