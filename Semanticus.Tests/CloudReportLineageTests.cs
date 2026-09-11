using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Lineage;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The Lineage Phase-3 CLOUD report transport — fully offline (a scripted HttpMessageHandler stands in for the
    /// tenant). Proves the pieces I can verify without a live workspace: the Fabric getDefinition LRO (202 → poll →
    /// result), base64 PBIR part decode (JSON parts only, binary skipped), the non-admin Power BI report-list parse,
    /// the write-scope CONSENT gate (refuses before any network), and the error-aware analysis overload (a paginated/
    /// blocked report is counted unreadable + surfaces its reason — never silently dropped, so 'safe' is never
    /// overstated). The remaining gate — does getDefinition work for a LIVE-CONNECTED report — needs Kane's tenant.
    /// </summary>
    public sealed class CloudReportLineageTests
    {
        private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

        // A minimal visual.json that references a model column Sales[Amount] (the confirmed PBIR field-ref shape).
        private const string VisualJson =
            "{ \"visual\": { \"query\": { \"queryState\": { \"Y\": { \"projections\": [ " +
            "{ \"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"Sales\" } }, \"Property\": \"Amount\" } } } ] } } } } }";

        private static string DefinitionBody() =>
            "{ \"definition\": { \"parts\": [ " +
            "{ \"path\": \"definition/pages/p1/visuals/v1/visual.json\", \"payload\": \"" + B64(VisualJson) + "\", \"payloadType\": \"InlineBase64\" }, " +
            "{ \"path\": \"definition/report.json\", \"payload\": \"" + B64("{ \\\"$schema\\\": \\\"x\\\" }") + "\", \"payloadType\": \"InlineBase64\" }, " +
            "{ \"path\": \"StaticResources/RegisteredResources/logo.png\", \"payload\": \"bm90LWpzb24=\", \"payloadType\": \"InlineBase64\" } ] } }";

        // ---- base64 part decode (pure) -----------------------------------------------------------------------------

        [Fact]
        public void DecodeDefinitionParts_keeps_json_parts_and_skips_binary()
        {
            var parts = FabricRest.DecodeDefinitionParts(DefinitionBody());
            Assert.Equal(2, parts.Length);                                          // the .png is dropped (only .json carries field refs)
            Assert.All(parts, p => Assert.EndsWith(".json", p.Path, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(parts, p => p.Content.Contains("\"Property\": \"Amount\""));   // decoded back to text

            // …and the decoded parts reconcile to a model field ref via the SAME parser the local path uses.
            var pr = ReportDefinitionReader.Parse(parts.Select(p => p.Content));
            Assert.Contains(pr.Fields, f => f.Kind == "column" && f.Entity == "Sales" && f.Property == "Amount");
        }

        // ---- coverage honesty: a part we couldn't read/parse is a GAP, not a silent drop ---------------------------

        [Fact]
        public void Parse_counts_a_malformed_part_as_skipped_but_still_reads_the_good_parts()
        {
            var parts = new (string path, string content)[]
            {
                ("definition/pages/p1/visuals/v1/visual.json", VisualJson),   // good — Sales[Amount]
                ("definition/pages/p1/visuals/v2/visual.json", "{ this is : not valid json"),   // malformed
            };
            var pr = ReportDefinitionReader.Parse(parts);
            Assert.Equal(1, pr.SkippedParts);                                        // the malformed part is COUNTED, not silently dropped
            Assert.Contains(pr.Fields, f => f.Entity == "Sales" && f.Property == "Amount");   // the good part still parsed
        }

        [Fact]
        public async Task AnalyzeReports_demotes_safe_to_caution_when_a_report_part_could_not_be_parsed()
        {
            // A report that WAS read but had a part we couldn't parse (malformed JSON / read failure) is a real coverage
            // gap: a field used only in that skipped part is invisible, so no would-be-safe item may keep the machine-
            // readable "safe" verdict — it must degrade to "caution" (same honesty rule as an unreadable report).
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                var table = (await engine.ListMeasuresAsync()).First().Table;
                var usedRef = await engine.CreateCalculatedColumnAsync("table:" + table, "Skip_Used", "1", "agent");
                var orphanRef = await engine.CreateCalculatedColumnAsync("table:" + table, "Skip_Orphan", "1", "agent");

                var partial = new ReportDefinitionReader.ParseResult { DefinitionFound = true, SkippedParts = 1 };
                partial.Fields.Add(new ReportDefinitionReader.FieldRef { Entity = table, Property = "Skip_Used", Kind = "column" });
                var parsed = new List<(string, string, ReportDefinitionReader.ParseResult)> { ("Partial", null!, partial) };

                var res = await sessions.Require().ReadAsync(m => LineageGraph.AnalyzeReports(m, parsed));
                Assert.Equal(1, res.ReportsRead);
                Assert.DoesNotContain(res.Unused.Items, i => i.Ref == usedRef);                       // the read part's field is excluded
                Assert.Equal("caution", res.Unused.Items.First(i => i.Ref == orphanRef).Verdict);     // NOT "safe" — a skipped part may use it
                Assert.Equal(0, res.Unused.SafeCount);                                                // every would-be-safe item demoted
                Assert.Contains("could not be parsed", res.Caveat);
            }
            finally { engine.Dispose(); }
        }

        // ---- reference completeness: sibling-scope aliases, hierarchies, bookmarks, the unresolved residue ---------

        [Fact]
        public void Parse_resolves_a_filter_field_alias_defined_in_a_sibling_scope()
        {
            // The REAL filterConfig shape: the filter entry's `field` uses Source "c", whose From clause sits in the
            // SIBLING `filter` object — outside the ancestor chain, resolved by the one-sibling-level scope extension.
            var part = "{ \"filterConfig\": { \"filters\": [ { \"name\": \"f1\", " +
                       "\"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Source\": \"c\" } }, \"Property\": \"City\" } }, " +
                       "\"filter\": { \"Version\": 2, \"From\": [ { \"Name\": \"c\", \"Entity\": \"Customer\", \"Type\": 0 } ], \"Where\": [] } } ] } }";
            var pr = ReportDefinitionReader.Parse(new[] { part });
            Assert.Equal(0, pr.Unresolved);
            Assert.Contains(pr.Fields, f => f.Kind == "column" && f.Entity == "Customer" && f.Property == "City");
        }

        [Fact]
        public void Parse_keeps_an_ambiguous_part_alias_unresolved_and_captures_its_name()
        {
            // The same alias maps to TWO entities in sibling scopes — the scope extension must refuse to guess
            // (mis-attribution would be worse than honesty), so the out-of-scope ref stays unresolved WITH its name.
            var part = "{ \"q1\": { \"From\": [ { \"Name\": \"c\", \"Entity\": \"Customer\" } ], \"Where\": [] }, " +
                       "\"q2\": { \"From\": [ { \"Name\": \"c\", \"Entity\": \"Sales\" } ], \"Where\": [] }, " +
                       "\"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Source\": \"c\" } }, \"Property\": \"City\" } } }";
            var pr = ReportDefinitionReader.Parse(new[] { part });
            Assert.Equal(1, pr.Unresolved);
            Assert.Contains("City", pr.UnresolvedNames);
            Assert.DoesNotContain(pr.Fields, f => f.Property == "City");
        }

        [Fact]
        public void Parse_masks_a_sibling_alias_that_contradicts_the_inherited_binding()
        {
            // Inherited scope binds c -> Sales; a container carries a ref using c AND an unrelated sibling whose From
            // binds c -> Customer. Picking EITHER could attribute the usage to the wrong table and leave the true
            // target falsely "safe" — the alias masks to unresolved for that scope, and the name is captured.
            var part = "{ \"query\": { \"From\": [ { \"Name\": \"c\", \"Entity\": \"Sales\" } ], " +
                       "\"container\": { " +
                       "\"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Source\": \"c\" } }, \"Property\": \"Amount\" } }, " +
                       "\"unrelated\": { \"From\": [ { \"Name\": \"c\", \"Entity\": \"Customer\" } ], \"Where\": [] } } } }";
            var pr = ReportDefinitionReader.Parse(new[] { part });
            Assert.DoesNotContain(pr.Fields, f => f.Property == "Amount");
            Assert.Equal(1, pr.Unresolved);
            Assert.Contains("Amount", pr.UnresolvedNames);
        }

        [Fact]
        public void Parse_masks_an_alias_when_siblings_disagree_even_with_an_inherited_binding()
        {
            // Inherited c -> Sales plus TWO disagreeing sibling clauses: the ref must stay unresolved — falling back
            // to the inherited binding would be a guess (the ref most likely belongs to one of the sibling scopes).
            var part = "{ \"outer\": { \"From\": [ { \"Name\": \"c\", \"Entity\": \"Sales\" } ], " +
                       "\"grp\": { " +
                       "\"s1\": { \"From\": [ { \"Name\": \"c\", \"Entity\": \"Customer\" } ] }, " +
                       "\"s2\": { \"From\": [ { \"Name\": \"c\", \"Entity\": \"Geography\" } ] }, " +
                       "\"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Source\": \"c\" } }, \"Property\": \"Margin\" } } } } }";
            var pr = ReportDefinitionReader.Parse(new[] { part });
            Assert.DoesNotContain(pr.Fields, f => f.Property == "Margin");
            Assert.Equal(1, pr.Unresolved);
            Assert.Contains("Margin", pr.UnresolvedNames);
        }

        [Fact]
        public void Parse_never_lets_a_distant_From_capture_an_orphan_alias()
        {
            // An orphan Source alias (e.g. stale bookmark state) whose only same-named From lives in a DISTANT branch
            // must stay UNRESOLVED with its name captured — resolving it against the distant scope could attribute the
            // usage to the wrong table, and the TRUE target would then keep a false "safe". Scope extension is one
            // sibling level exactly, so nothing wider than the shared parent can bind an alias.
            var part = "{ \"filters\": [ { \"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Source\": \"c\" } }, \"Property\": \"Amount\" } }, " +
                       "\"filter\": { \"From\": [ { \"Name\": \"c\", \"Entity\": \"Customer\" } ], \"Where\": [] } } ], " +
                       "\"bookmarkState\": { \"orphan\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Source\": \"c\" } }, \"Property\": \"Margin\" } } } }";
            var pr = ReportDefinitionReader.Parse(new[] { part });
            Assert.Contains(pr.Fields, f => f.Entity == "Customer" && f.Property == "Amount");   // the sibling scope still works
            Assert.DoesNotContain(pr.Fields, f => f.Property == "Margin");                       // the orphan is NOT stolen by the distant From
            Assert.Equal(1, pr.Unresolved);
            Assert.Contains("Margin", pr.UnresolvedNames);
        }

        [Fact]
        public void Parse_emits_a_hierarchy_binding_and_the_variation_base_column()
        {
            // A user hierarchy on a visual emits kind "hierarchy" (level→column mapping is a MODEL fact, resolved in
            // LineageGraph); an auto date/time variation emits the UNDERLYING COLUMN (the object a delete would break).
            var part = "{ \"visual\": { \"query\": { \"queryState\": { \"Rows\": { \"projections\": [ " +
                       "{ \"HierarchyLevel\": { \"Expression\": { \"Hierarchy\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"Geography\" } }, \"Hierarchy\": \"Geo Drill\" } }, \"Level\": \"City\" } }, " +
                       "{ \"Hierarchy\": { \"Expression\": { \"PropertyVariationSource\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"Sales\" } }, \"Name\": \"Variation\", \"Property\": \"OrderDate\" } }, \"Hierarchy\": \"Date Hierarchy\" } } " +
                       "] } } } } }";
            var pr = ReportDefinitionReader.Parse(new[] { part });
            Assert.Equal(0, pr.Unresolved);
            Assert.Contains(pr.Fields, f => f.Kind == "hierarchy" && f.Entity == "Geography" && f.Property == "Geo Drill");
            Assert.Contains(pr.Fields, f => f.Kind == "column" && f.Entity == "Sales" && f.Property == "OrderDate");
            // Exactly ONE occurrence per binding (the HierarchyLevel wrapper and the inner Hierarchy shape must not
            // both emit): one for the user hierarchy, one for the variation's base column.
            Assert.Equal(2, pr.Occurrences.Count);
        }

        [Fact]
        public void ReadLocalPbir_reads_bookmark_parts()
        {
            // A bookmark's captured filter state is a REAL field usage — and a stale bookmark can be the ONLY
            // remaining user of a field. The local door must read bookmarks/*.json like the cloud door already does.
            var root = Path.Combine(Path.GetTempPath(), "sem-pbir-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "definition", "bookmarks"));
                File.WriteAllText(Path.Combine(root, "definition", "report.json"), "{}");
                File.WriteAllText(Path.Combine(root, "definition", "bookmarks", "b1.bookmark.json"),
                    "{ \"explorationState\": { \"filters\": [ { \"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"Sales\" } }, \"Property\": \"BookmarkOnly\" } } } ] } }");
                var pr = ReportDefinitionReader.ReadLocalPbir(root);
                Assert.True(pr.DefinitionFound);
                Assert.Contains(pr.Fields, f => f.Kind == "column" && f.Entity == "Sales" && f.Property == "BookmarkOnly");
            }
            finally { try { Directory.Delete(root, recursive: true); } catch { } }
        }

        [Fact]
        public async Task AnalyzeReports_demotes_a_safe_item_whose_name_matches_an_unresolved_reference()
        {
            // An unresolved reference is a real usage whose table we could not place — a would-be-safe item carrying
            // that NAME may be its target, so it demotes to caution. Non-matching safe items keep their verdict (the
            // scalpel, not the blanket).
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                var table = (await engine.ListMeasuresAsync()).First().Table;
                var matchRef = await engine.CreateCalculatedColumnAsync("table:" + table, "Unres_Match", "1", "agent");
                var otherRef = await engine.CreateCalculatedColumnAsync("table:" + table, "Unres_Other", "1", "agent");
                await engine.CreateCalculatedColumnAsync("table:" + table, "Unres_Used", "1", "agent");

                // A representative report: one field RESOLVED (so coverage is complete and the wrong-model blanket
                // demote stays out of the way) plus one unresolved residue carrying a name.
                var pr = new ReportDefinitionReader.ParseResult { DefinitionFound = true, Unresolved = 1 };
                pr.Fields.Add(new ReportDefinitionReader.FieldRef { Entity = table, Property = "Unres_Used", Kind = "column" });
                pr.UnresolvedNames.Add("Unres_Match");
                var parsed = new List<(string, string, ReportDefinitionReader.ParseResult)> { ("R", null!, pr) };

                var res = await sessions.Require().ReadAsync(m => LineageGraph.AnalyzeReports(m, parsed));
                var match = res.Unused.Items.First(i => i.Ref == matchRef);
                Assert.Equal("caution", match.Verdict);
                Assert.Contains("could not be fully attributed", match.Reason);
                Assert.Equal("safe", res.Unused.Items.First(i => i.Ref == otherRef).Verdict);
                Assert.Contains("could not be attributed", res.Caveat);
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task AnalyzeReports_resolves_a_hierarchy_reference_to_its_level_columns()
        {
            // The report names only the hierarchy; the analyzer resolves it against the MODEL to the level columns,
            // so both doors see them as report-used (usage attribution for impact / the graph's report layer).
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                var table = (await engine.ListMeasuresAsync()).First().Table;
                var l1 = await engine.CreateCalculatedColumnAsync("table:" + table, "Hier_L1", "1", "agent");
                var l2 = await engine.CreateCalculatedColumnAsync("table:" + table, "Hier_L2", "1", "agent");
                await engine.CreateHierarchyAsync("table:" + table, "Rep Drill", new[] { "Hier_L1", "Hier_L2" }, "agent");

                var pr = new ReportDefinitionReader.ParseResult { DefinitionFound = true };
                pr.Fields.Add(new ReportDefinitionReader.FieldRef { Entity = table, Property = "Rep Drill", Kind = "hierarchy" });
                var parsed = new List<(string, string, ReportDefinitionReader.ParseResult)> { ("R", null!, pr) };

                var res = await sessions.Require().ReadAsync(m => LineageGraph.AnalyzeReports(m, parsed));
                Assert.Contains(l1, res.ModelFieldsUsed);
                Assert.Contains(l2, res.ModelFieldsUsed);
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public void DecodeDefinitionParts_tolerates_a_json_part_with_an_undecodable_payload()
        {
            // A .json-named part whose payload isn't valid base64 must be skipped (not throw) — the rest still decode.
            var body = "{ \"definition\": { \"parts\": [ " +
                       "{ \"path\": \"definition/report.json\", \"payload\": \"" + B64("{}") + "\" }, " +
                       "{ \"path\": \"definition/bad.json\", \"payload\": \"@@@not-base64@@@\" }, " +
                       "{ \"path\": \"definition/nopayload.json\" } ] } }";
            var parts = FabricRest.DecodeDefinitionParts(body);
            Assert.Single(parts);
            Assert.EndsWith("report.json", parts[0].Path);
        }

        [Fact]
        public void DecodeDefinitionParts_returns_empty_when_there_is_no_definition()
        {
            Assert.Empty(FabricRest.DecodeDefinitionParts("{ \"value\": [] }"));
            Assert.Empty(FabricRest.DecodeDefinitionParts("{ \"definition\": { } }"));
            Assert.Empty(FabricRest.DecodeDefinitionParts("<html>proxy interstitial</html>"));   // a non-JSON body degrades, never throws
            Assert.Empty(FabricRest.DecodeDefinitionParts(""));
        }

        // ---- getDefinition over the LRO (202 → poll → result) ------------------------------------------------------

        [Fact]
        public async Task GetDefinition_follows_the_202_LRO_then_decodes_the_result()
        {
            var handler = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
            {
                _ => { var r = Json(HttpStatusCode.Accepted, ""); r.Headers.Add("x-ms-operation-id", "op-1"); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return r; },
                _ => Json(HttpStatusCode.OK, "{\"status\":\"Succeeded\"}"),
                _ => Json(HttpStatusCode.OK, DefinitionBody()),
            });
            using var http = new HttpClient(handler) { BaseAddress = new Uri(FabricRest.BaseUrl) };

            var body = await FabricRest.PostForResultAsync(http, "workspaces/w/reports/r/getDefinition?format=PBIR", null, CancellationToken.None);
            var parts = FabricRest.DecodeDefinitionParts(body);
            Assert.Equal(2, parts.Length);
            Assert.Contains(handler.Requests, u => u.StartsWith("POST") && u.Contains("getDefinition?format=PBIR"));
            Assert.Contains(handler.Requests, u => u.Contains("operations/op-1/result"));
        }

        [Fact]
        public async Task GetDefinition_returns_a_200_sync_body_without_polling()
        {
            var handler = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[] { _ => Json(HttpStatusCode.OK, DefinitionBody()) });
            using var http = new HttpClient(handler) { BaseAddress = new Uri(FabricRest.BaseUrl) };
            var body = await FabricRest.PostForResultAsync(http, "workspaces/w/reports/r/getDefinition?format=PBIR", null, CancellationToken.None);
            Assert.Equal(2, FabricRest.DecodeDefinitionParts(body).Length);
            Assert.Single(handler.Requests);   // no operations/ poll
        }

        // ---- the INTERACTIVE poll budget (fast fail on a stuck report; write lanes stay on the long window) ----------

        [Fact]
        public async Task GetDefinition_with_the_interactive_budget_still_succeeds_and_decodes()
        {
            // The InteractiveRead preset must not break the happy path: 202 -> Succeeded on the first (sub-second) poll
            // -> result decoded. Proves the budget path returns the definition, it does not only fail fast.
            var handler = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
            {
                _ => { var r = Json(HttpStatusCode.Accepted, ""); r.Headers.Add("x-ms-operation-id", "op-1"); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return r; },
                _ => Json(HttpStatusCode.OK, "{\"status\":\"Succeeded\"}"),
                _ => Json(HttpStatusCode.OK, DefinitionBody()),
            });
            using var http = new HttpClient(handler) { BaseAddress = new Uri(FabricRest.BaseUrl) };

            var body = await FabricRest.PostForResultAsync(http, "workspaces/w/reports/r/getDefinition?format=PBIR", null, CancellationToken.None, FabricRest.PollBudget.InteractiveRead);
            Assert.Equal(2, FabricRest.DecodeDefinitionParts(body).Length);
            Assert.Contains(handler.Requests, u => u.Contains("operations/op-1/result"));
        }

        [Fact]
        public async Task GetDefinition_interactive_budget_fails_fast_with_an_honest_message()
        {
            // A 202 whose operation never reaches terminal (the reported symptom). With a short interactive budget the
            // read must fail in well under the write-lane ~8-min ceiling, surface the human message (not the internal
            // "poll window" text), and never fetch operations/{id}/result.
            var handler = new StuckLroHandler();
            using var http = new HttpClient(handler) { BaseAddress = new Uri(FabricRest.BaseUrl) };
            var budget = new FabricRest.PollBudget(TimeSpan.FromSeconds(1), 100, 200);

            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => FabricRest.PostForResultAsync(http, "workspaces/w/reports/r/getDefinition?format=PBIR", null, CancellationToken.None, budget));
            sw.Stop();

            Assert.Contains("Try again shortly", ex.Message);          // the honest, jargon-free copy
            Assert.Contains("within 1s", ex.Message);                  // names the budget it waited (not "within 0s")
            Assert.DoesNotContain("poll window", ex.Message);          // NOT the internal write-lane text
            Assert.DoesNotContain("operation Running", ex.Message);    // no engine "operation <status>:" prefix leaks to the UI
            Assert.DoesNotContain("/result", string.Join(" ", handler.Paths));   // gave up before the result leg
            Assert.True(sw.ElapsedMilliseconds < 5000, $"interactive budget should fail fast; took {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public async Task PollOperation_default_path_keeps_the_write_lane_poll_window()
        {
            // Regression guard for the shared poller: with NO budget (every deploy/git/cicd/data-agent write caller),
            // PollOperationAsync must still run the fixed maxPolls window and return the original "poll window" message.
            // Mirrors what Semanticus.CicdSmoke pins; proves the interactive change did not shorten the write lanes.
            var handler = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
            {
                _ => { var r = Json(HttpStatusCode.OK, "{\"status\":\"Running\"}"); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return r; },
                _ => { var r = Json(HttpStatusCode.OK, "{\"status\":\"Running\"}"); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return r; },
            });
            using var http = new HttpClient(handler) { BaseAddress = new Uri(FabricRest.BaseUrl) };

            var st = await FabricRest.PollOperationAsync(http, "op-w", CancellationToken.None, 0, 2);
            Assert.Equal("Running", st.Status);
            Assert.Contains("poll window", st.ErrorMessage);
            Assert.Equal(2, handler.Requests.Count);   // exactly maxPolls polls, no early exit
        }

        [Fact]
        public async Task GetReportDefinition_hard_ceiling_cancels_a_wedged_request_with_the_same_honest_message()
        {
            // The in-loop deadline can't interrupt a request stuck INSIDE SendAsync (a 429 backoff or a slow socket).
            // The linked-CTS hard ceiling must: a POST that blocks forever (honouring its token) is cancelled at the
            // ceiling and converted to the SAME honest message, never a raw "operation was canceled".
            var handler = new BlockingHandler();
            FabricRest.TestClientFactory = () => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(FabricRest.BaseUrl) };
            try
            {
                var budget = new FabricRest.PollBudget(TimeSpan.FromMilliseconds(300), 50, 100);
                var sw = Stopwatch.StartNew();
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => FabricRest.GetReportDefinitionAsync("w", "r", "PBIR", "tok", CancellationToken.None, budget));
                sw.Stop();

                Assert.Contains("Try again shortly", ex.Message);
                Assert.DoesNotContain("operation", ex.Message);        // not the raw cancellation / operation text
                Assert.True(sw.ElapsedMilliseconds < 6000, $"hard ceiling should fire fast; took {sw.ElapsedMilliseconds}ms");
            }
            finally { FabricRest.TestClientFactory = null; }
        }

        [Fact]
        public async Task GetReportDefinition_a_genuine_caller_cancel_still_propagates_uncaught()
        {
            // A real caller cancel (ct) must NOT be swallowed into an "unreadable" message — it propagates as an
            // OperationCanceledException so the batch aborts, distinct from the budget's fail-loud conversion.
            var handler = new BlockingHandler();
            FabricRest.TestClientFactory = () => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(FabricRest.BaseUrl) };
            try
            {
                using var caller = new CancellationTokenSource();
                caller.CancelAfter(200);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => FabricRest.GetReportDefinitionAsync("w", "r", "PBIR", "tok", caller.Token, new FabricRest.PollBudget(TimeSpan.FromSeconds(30), 50, 100)));
            }
            finally { FabricRest.TestClientFactory = null; }
        }

        [Fact]
        public async Task FetchCloudReportParts_is_bounded_parallel_order_preserving_and_fail_loud()
        {
            // The per-report fan-out (LocalEngine): preserves the caller's order in indexed slots, and each report's
            // outcome (read / blocked-403 / paginated-skipped / not-found) lands in ITS OWN slot without one failure
            // aborting the batch. Driven offline through TestClientFactory (a fresh, keyed, thread-safe client per call).
            var handler = new KeyedHandler();
            FabricRest.TestClientFactory = () => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(FabricRest.BaseUrl) };
            try
            {
                var byId = new Dictionary<string, CloudReport>(StringComparer.OrdinalIgnoreCase)
                {
                    ["r1"] = new CloudReport { Id = "r1", Name = "Report One", ReportType = "PowerBIReport" },
                    ["rblocked"] = new CloudReport { Id = "rblocked", Name = "Blocked One", ReportType = "PowerBIReport" },
                    ["rpag"] = new CloudReport { Id = "rpag", Name = "Paginated One", ReportType = "PaginatedReport" },
                };
                var wanted = new List<string> { "r1", "rblocked", "rpag", "rmissing" };

                var parsed = await LocalEngine.FetchCloudReportPartsAsync("ws", wanted, byId, "tok", CancellationToken.None);

                Assert.Equal(4, parsed.Count);
                Assert.Equal(new[] { "Report One", "Blocked One", "Paginated One", "rmissing" }, parsed.Select(p => p.path).ToArray());   // ORDER preserved
                Assert.Null(parsed[0].error);                                          // r1 read
                Assert.True(parsed[0].result.DefinitionFound);
                Assert.Contains(parsed[0].result.Fields, f => f.Entity == "Sales" && f.Property == "Amount");
                Assert.False(string.IsNullOrEmpty(parsed[1].error));                   // rblocked fail-loud (scrubbed 403), batch continued
                Assert.Contains("Paginated", parsed[2].error);                         // rpag skipped before any HTTP
                Assert.Contains("not found", parsed[3].error);                         // rmissing skipped before any HTTP
            }
            finally { FabricRest.TestClientFactory = null; }
        }

        // ---- Power BI non-admin report discovery -------------------------------------------------------------------

        [Fact]
        public void PowerBi_ParseReports_extracts_fields_and_tolerates_missing()
        {
            const string body = "{ \"value\": [ " +
                "{ \"id\": \"r1\", \"name\": \"Sales\", \"datasetId\": \"ds-9\", \"reportType\": \"PowerBIReport\", \"webUrl\": \"https://app/r1\" }, " +
                "{ \"id\": \"r2\", \"name\": \"Ops (RDL)\", \"reportType\": \"PaginatedReport\" } ] }";
            var reports = PowerBiReports.ParseReports(body);
            Assert.Equal(2, reports.Length);
            var sales = reports.First(r => r.Id == "r1");
            Assert.Equal("ds-9", sales.DatasetId);
            Assert.Equal("PowerBIReport", sales.ReportType);
            Assert.Equal("PaginatedReport", reports.First(r => r.Id == "r2").ReportType);
            Assert.Null(reports.First(r => r.Id == "r2").DatasetId);   // a missing field is null, not a throw
        }

        [Fact]
        public async Task PowerBi_report_list_GET_parses_through_the_hardened_send()
        {
            var handler = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
            {
                _ => Json(HttpStatusCode.OK, "{ \"value\": [ { \"id\": \"r1\", \"name\": \"Sales\", \"datasetId\": \"ds-9\", \"reportType\": \"PowerBIReport\" } ] }"),
            });
            using var http = new HttpClient(handler) { BaseAddress = new Uri(PowerBiReports.BaseUrl) };
            var r = await FabricRest.SendAsync(http, HttpMethod.Get, "groups/ws-1/reports", CancellationToken.None);
            Assert.True(r.Ok);
            var reports = PowerBiReports.ParseReports(r.Body);
            Assert.Single(reports);
            Assert.Equal("ds-9", reports[0].DatasetId);
            Assert.Contains(handler.Requests, u => u.Contains("groups/ws-1/reports"));
        }

        [Fact]
        public void PowerBi_error_is_scrubbed_and_carries_a_workspace_access_hint()
        {
            var msg = PowerBiReports.ParseError("{ \"error\": { \"code\": \"PowerBINotAuthorizedException\", \"message\": \"token Bearer eyJabc.def.ghi\" } }", 403);
            Assert.Contains("403", msg);
            Assert.Contains("PowerBINotAuthorizedException", msg);
            Assert.Contains("access to the workspace", msg);   // reports-appropriate hint (not the deploy-pipeline one)
            Assert.DoesNotContain("eyJabc.def.ghi", msg);      // the JWT is scrubbed
        }

        // ---- the write-scope CONSENT gate --------------------------------------------------------------------------

        [Fact]
        public async Task AnalyzeCloudReports_without_consent_refuses_before_touching_the_network()
        {
            // consent=false throws synchronously (before token acquisition / Require) — proven by the absence of a model:
            // a non-consented call can't even get as far as needing one.
            var engine = new LocalEngine(new SessionManager());
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.AnalyzeCloudReportsAsync("ws-1", new[] { "r1" }, consent: false, "azcli", null));
            Assert.Contains("consent=true", ex.Message);
            Assert.Contains("ReadWrite", ex.Message);   // it names the write-capable scope it needs
        }

        [Fact]
        public async Task AnalyzeCloudReports_requires_a_workspace_id()
        {
            var engine = new LocalEngine(new SessionManager());
            await Assert.ThrowsAsync<ArgumentException>(
                () => engine.AnalyzeCloudReportsAsync("  ", new[] { "r1" }, consent: true, "azcli", null));
        }

        [Fact]
        public void Progress_channel_publishes_updates_in_order_and_unsubscribes()
        {
            var bus = new ChangeBus();
            var seen = new List<OperationProgress>();
            void Record(OperationProgress progress) => seen.Add(progress);
            bus.Progress += Record;

            bus.PublishProgress(new OperationProgress { OpKey = "analyze_cloud_reports", RunId = "run-a", Done = 0, Total = 2, Note = "Sales" });
            bus.PublishProgress(new OperationProgress { OpKey = "analyze_cloud_reports", RunId = "run-a", Done = 1, Total = 2, Note = "Finance" });
            bus.PublishProgress(new OperationProgress { OpKey = "analyze_cloud_reports", RunId = "run-a", Done = 2, Total = 2 });
            bus.Progress -= Record;
            bus.PublishProgress(new OperationProgress { OpKey = "analyze_cloud_reports", RunId = "run-a", Done = 2, Total = 2 });

            Assert.Equal(new[] { 0, 1, 2 }, seen.Select(p => p.Done));
            Assert.All(seen, p => Assert.Equal(2, p.Total));
            Assert.All(seen, p => Assert.Equal("run-a", p.RunId));
            Assert.Equal(new[] { "Sales", "Finance", null }, seen.Select(p => p.Note));
        }

        [Fact]
        public void Progress_channel_isolates_throwing_subscribers()
        {
            var bus = new ChangeBus();
            var seen = new List<OperationProgress>();
            bus.Progress += _ => throw new InvalidOperationException("listener failed");
            bus.Progress += seen.Add;
            var progress = new OperationProgress { OpKey = "analyze_cloud_reports", RunId = "run-a", Done = 0, Total = 1, Note = "Sales" };

            var error = Record.Exception(() => bus.PublishProgress(progress));

            Assert.Null(error);
            Assert.Same(progress, Assert.Single(seen));
        }

        [Fact]
        public void Progress_channel_keeps_concurrent_invocations_distinguishable_on_the_wire()
        {
            var bus = new ChangeBus();
            var seen = new List<OperationProgress>();
            bus.Progress += seen.Add;

            bus.PublishProgress(new OperationProgress { OpKey = "analyze_cloud_reports", RunId = "run-a", Done = 0, Total = 2, Note = "Sales" });
            bus.PublishProgress(new OperationProgress { OpKey = "analyze_cloud_reports", RunId = "run-b", Done = 0, Total = 5, Note = "Finance" });

            Assert.Equal(new[] { "run-a", "run-b" }, seen.Select(p => p.RunId));
            Assert.NotEqual(seen[0].RunId, seen[1].RunId);

            var serializer = new Newtonsoft.Json.JsonSerializer();
            RpcServer.ConfigureSerializer(serializer);
            var json = new StringWriter();
            serializer.Serialize(json, seen[0]);
            Assert.Contains("\"runId\":\"run-a\"", json.ToString());
            Assert.DoesNotContain("\"RunId\"", json.ToString());
        }

        // ---- the error-aware analysis overload (the cloud sink) ----------------------------------------------------

        [Fact]
        public async Task AnalyzeReports_error_aware_overload_counts_a_blocked_report_unreadable_and_keeps_good_usage()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                var table = (await engine.ListMeasuresAsync()).First().Table;
                var colRef = await engine.CreateCalculatedColumnAsync("table:" + table, "Cloud_RptCol", "1", "agent");

                // A "good" cloud report that uses the column, plus a paginated/RDL report carrying an error string.
                var good = new ReportDefinitionReader.ParseResult { DefinitionFound = true };
                good.Fields.Add(new ReportDefinitionReader.FieldRef { Entity = table, Property = "Cloud_RptCol", Kind = "column" });
                var blocked = new ReportDefinitionReader.ParseResult { DefinitionFound = false };

                var parsed = new List<(string, string, ReportDefinitionReader.ParseResult)>
                {
                    ("Good Report", null!, good),
                    ("Paginated Report", "Paginated (RDL) report: its field usage is not parsed.", blocked),
                };

                var res = await sessions.Require().ReadAsync(m => LineageGraph.AnalyzeReports(m, parsed));

                Assert.Equal(1, res.ReportsRead);
                Assert.Equal(1, res.ReportsUnreadable);
                Assert.Contains(res.Reports, r => r.Read && r.Name == "Good Report");
                Assert.Contains(res.Reports, r => !r.Read && r.Error != null && r.Name == "Paginated Report");
                Assert.Contains(res.ModelFieldsUsed, r => r == colRef);
                Assert.DoesNotContain(res.Unused.Items, i => i.Ref == colRef);   // a report uses it ⇒ no longer "safe"
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task AnalyzeReports_demotes_safe_to_caution_when_a_report_is_unreadable()
        {
            // The honesty fix: when SOME reports can't be read (sensitivity-label block / RDL / fetch fail), a field
            // used only by a blocked report would look orphaned — so NO item may keep the machine-readable verdict
            // "safe". Would-be-safe items must degrade to "caution" (not just a prose caveat both doors can ignore).
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                var table = (await engine.ListMeasuresAsync()).First().Table;
                var usedRef = await engine.CreateCalculatedColumnAsync("table:" + table, "Cloud_Used", "1", "agent");
                var orphanRef = await engine.CreateCalculatedColumnAsync("table:" + table, "Cloud_Orphan", "1", "agent");

                var good = new ReportDefinitionReader.ParseResult { DefinitionFound = true };
                good.Fields.Add(new ReportDefinitionReader.FieldRef { Entity = table, Property = "Cloud_Used", Kind = "column" });
                var blocked = new ReportDefinitionReader.ParseResult { DefinitionFound = false };
                var parsed = new List<(string, string, ReportDefinitionReader.ParseResult)>
                {
                    ("Good", null!, good),
                    ("Blocked", "Report blocked by an encrypted sensitivity label.", blocked),
                };

                var res = await sessions.Require().ReadAsync(m => LineageGraph.AnalyzeReports(m, parsed));
                Assert.Equal(1, res.ReportsRead);
                Assert.Equal(1, res.ReportsUnreadable);
                Assert.DoesNotContain(res.Unused.Items, i => i.Ref == usedRef);          // report-used ⇒ excluded
                Assert.Equal("caution", res.Unused.Items.First(i => i.Ref == orphanRef).Verdict);   // NOT "safe"
                Assert.Equal(0, res.Unused.SafeCount);                                   // every would-be-safe item demoted
                Assert.Contains("could not be read", res.Caveat);
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task AnalyzeReports_warns_and_demotes_when_no_report_field_matches_the_open_model()
        {
            // Wrong-model guard: a report that reads fine but whose fields don't reconcile to the OPEN model (the user
            // opened the wrong dataset) must NOT silently produce an all-"safe" list with a caveat claiming usage.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                var table = (await engine.ListMeasuresAsync()).First().Table;
                var orphanRef = await engine.CreateCalculatedColumnAsync("table:" + table, "Cloud_Orphan2", "1", "agent");

                var wrong = new ReportDefinitionReader.ParseResult { DefinitionFound = true };
                wrong.Fields.Add(new ReportDefinitionReader.FieldRef { Entity = "NoSuchTable", Property = "NoSuchColumn", Kind = "column" });
                var parsed = new List<(string, string, ReportDefinitionReader.ParseResult)> { ("Wrong-model report", null!, wrong) };

                var res = await sessions.Require().ReadAsync(m => LineageGraph.AnalyzeReports(m, parsed));
                Assert.Equal(1, res.ReportsRead);
                Assert.Empty(res.ModelFieldsUsed);
                Assert.Contains("NONE of their fields matched", res.Caveat);
                Assert.Equal("caution", res.Unused.Items.First(i => i.Ref == orphanRef).Verdict);
                Assert.Equal(0, res.Unused.SafeCount);
            }
            finally { engine.Dispose(); }
        }

        // ---- a minimal scripted HttpMessageHandler (mirrors the CicdSmoke pattern) ----------------------------------
        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;
            public List<string> Requests { get; } = new();
            public ScriptedHandler(IEnumerable<Func<HttpRequestMessage, HttpResponseMessage>> responses) => _responses = new(responses);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requests.Add(request.Method + " " + (request.RequestUri?.ToString() ?? ""));
                var fn = _responses.Count > 0 ? _responses.Dequeue() : (_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
                return Task.FromResult(fn(request));
            }
        }

        // A stateful handler that mimics a Fabric getDefinition LRO which ACCEPTS (202) but never reaches terminal:
        // request #1 -> 202 + x-ms-operation-id, every later poll -> 200 {status:Running}. Kept SEPARATE from the FIFO
        // ScriptedHandler (thread-safe counter, no fixed queue) so the existing ordered tests are untouched.
        private sealed class StuckLroHandler : HttpMessageHandler
        {
            private int _n;
            public List<string> Paths { get; } = new();
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                lock (Paths) Paths.Add(request.RequestUri?.AbsolutePath ?? "");
                if (Interlocked.Increment(ref _n) == 1)
                {
                    var r = Json(HttpStatusCode.Accepted, "");
                    r.Headers.Add("x-ms-operation-id", "op-stuck");
                    r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                    return Task.FromResult(r);
                }
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":\"Running\"}"));
            }
        }

        // Blocks forever but HONOURS its cancellation token, so the linked-CTS hard ceiling (or a caller cancel) can
        // actually interrupt an in-flight request. Only returns if somehow released without cancellation.
        private sealed class BlockingHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return Json(HttpStatusCode.InternalServerError, "{}");   // unreachable
            }
        }

        // Concurrency-safe, URL-keyed handler for the parallel fan-out test (distinct from the FIFO ScriptedHandler):
        // r1 -> a 200-sync PBIR definition; rblocked -> 403. Order independence is the point (any interleaving is fine).
        private sealed class KeyedHandler : HttpMessageHandler
        {
            private readonly List<string> _paths = new();
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var url = request.RequestUri?.AbsolutePath ?? "";
                lock (_paths) _paths.Add(url);
                if (url.Contains("/reports/r1/")) return Task.FromResult(Json(HttpStatusCode.OK, DefinitionBody()));
                if (url.Contains("/reports/rblocked/")) return Task.FromResult(Json(HttpStatusCode.Forbidden, "{ \"error\": { \"code\": \"Forbidden\", \"message\": \"blocked\" } }"));
                return Task.FromResult(Json(HttpStatusCode.InternalServerError, "{}"));
            }
        }
    }
}
