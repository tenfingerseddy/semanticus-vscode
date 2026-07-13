using System;
using System.Collections.Generic;
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
                    ("Paginated Report", "Paginated (RDL) report — its field usage is not parsed.", blocked),
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
    }
}
