using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// The Power BI REST API (https://api.powerbi.com/v1.0/myorg) — used for the ONE thing the Fabric Core API can't
    /// give the Lineage "Measure Killer": the <b>non-admin per-workspace report list with each report's datasetId</b>
    /// (<c>GET /groups/{id}/reports</c>). That is the dataset→reports discovery edge (the plan's validated default;
    /// the admin Scanner API is optional enrichment, not required). The Fabric item list omits datasetId, so a Fabric
    /// report→model match would need an extra getDefinition per report — the Power BI list carries it for free.
    ///
    /// Read-only (a plain GET). Reuses FabricRest's hardened <see cref="FabricRest.SendAsync(HttpClient,HttpMethod,string,CancellationToken)"/>
    /// (bounded 429/Retry-After backoff, token-scrubbing) so this client is just a base address + a small JSON parse;
    /// the token is the Power BI / XMLA audience (EntraToken.AcquireAsync), distinct from the Fabric audience used by
    /// getDefinition. The HTTP primitive takes an injected HttpClient so the offline smoke can script the response.
    /// </summary>
    internal static class PowerBiReports
    {
        internal const string BaseUrl = "https://api.powerbi.com/v1.0/myorg/";

        private static HttpClient NewClient(string token)
        {
            var http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(100) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return http;
        }

        internal static async Task<CloudReport[]> ListReportsAsync(string workspaceId, string token, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(workspaceId)) throw new ArgumentException("A workspace id is required.");
            using var http = NewClient(token);
            var url = "groups/" + Uri.EscapeDataString(workspaceId) + "/reports";
            var r = await FabricRest.SendAsync(http, HttpMethod.Get, url, ct).ConfigureAwait(false);
            if (!r.Ok) throw new InvalidOperationException(ParseError(r.Body, r.Status));
            return ParseReports(r.Body);
        }

        // The Power BI "Reports In Group" response is { value: [ { id, name, datasetId, reportType, webUrl } ] } — no
        // pagination on this list. Tolerant of missing fields (a report without a datasetId still lists, just won't match
        // any model). Kept separate + testable so the smoke can feed a scripted body.
        internal static CloudReport[] ParseReports(string body)
        {
            var reports = new List<CloudReport>();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    reports.Add(new CloudReport
                    {
                        Id = Str(e, "id"),
                        Name = Str(e, "name"),
                        DatasetId = Str(e, "datasetId"),
                        ReportType = Str(e, "reportType"),
                        WebUrl = Str(e, "webUrl"),
                    });
                }
            return reports.ToArray();
        }

        private static string Str(JsonElement e, string prop)
            => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // Power BI REST errors share the Fabric { error: { code, message } } envelope but use a slightly different shape
        // ({ error: { code, message } }) than Fabric ({ errorCode, message }). Parse both, always scrub, add a reports-
        // appropriate hint for the gates a read-only caller hits (vs FabricRest's deploy-pipeline-flavoured hints).
        internal static string ParseError(string body, int status)
        {
            string code = null, msg = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                {
                    code = err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    msg = err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                }
                code ??= root.TryGetProperty("errorCode", out var ec) && ec.ValueKind == JsonValueKind.String ? ec.GetString() : null;
                msg ??= root.TryGetProperty("message", out var mm) && mm.ValueKind == JsonValueKind.String ? mm.GetString() : null;
            }
            catch { /* non-JSON body — fall through to the status-only message */ }

            var s = $"Power BI REST {status}";
            if (!string.IsNullOrEmpty(code)) s += " " + code;
            if (!string.IsNullOrEmpty(msg)) s += ": " + msg;
            return FabricRest.Scrub(s) + status switch
            {
                401 => "  [Sign in again, or the token lacks the Power BI scope.]",
                403 => "  [Authenticated, but you can't list this workspace's reports — you need access to the workspace (a Viewer role is enough for the report list).]",
                404 => "  [Workspace not found — check the id (a personal 'My workspace' isn't a group).]",
                _ => string.Empty,
            };
        }
    }
}
