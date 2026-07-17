using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// Minimal client for the Fabric Core REST API v1 (https://api.fabric.microsoft.com/v1) — the ALM cloud lane.
    /// Read-only in this phase (workspaces + deployment pipelines/stages/items). Factors the two cross-cutting
    /// concerns ONCE so every Fabric call inherits them: <b>pagination</b> (continuationToken / continuationUri) and
    /// the <b>long-running-operation poller</b> (202 → x-ms-operation-id / Retry-After → poll /operations/{id} → done),
    /// the latter for the Phase-3 write/deploy lanes. No live write happens here — these are GETs.
    /// The HTTP primitives are <c>internal</c> + take an injected <see cref="HttpClient"/> so the offline smoke can
    /// drive pagination / LRO / error-parsing against a scripted <see cref="HttpMessageHandler"/> (no live tenant).
    /// Every error is scrubbed (incl. any Bearer token) before it crosses a door — Golden Rule #1.
    /// </summary>
    internal static class FabricRest
    {
        internal const string BaseUrl = "https://api.fabric.microsoft.com/v1/";
        internal const int MaxResponseBodyBytes = 64 * 1024 * 1024;
        internal const long MaxPagedResponseBytes = 128L * 1024 * 1024;
        internal const long MaxDecodedDefinitionBytes = 64L * 1024 * 1024;
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private static readonly SocketsHttpHandler SharedHandler = new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };

        // ---- public entry points (own the HttpClient + bearer auth) ----
        internal static async Task<FabricWorkspace[]> ListWorkspacesAsync(string token, CancellationToken ct)
        {
            using var http = NewClient(token);
            return (await GetAllPagesAsync<FabricWorkspace>(http, "workspaces", ct).ConfigureAwait(false)).ToArray();
        }

        internal static async Task<DeploymentPipeline[]> ListDeploymentPipelinesAsync(string token, CancellationToken ct)
        {
            using var http = NewClient(token);
            return (await GetAllPagesAsync<DeploymentPipeline>(http, "deploymentPipelines", ct).ConfigureAwait(false)).ToArray();
        }

        internal static async Task<PipelineStage[]> GetPipelineStagesAsync(string pipelineId, string token, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(pipelineId)) throw new ArgumentException("A deployment-pipeline id is required — list_deployment_pipelines returns the pipelines and their ids.");
            using var http = NewClient(token);
            var url = "deploymentPipelines/" + Uri.EscapeDataString(pipelineId) + "/stages";
            var stages = await GetAllPagesAsync<PipelineStage>(http, url, ct).ConfigureAwait(false);
            return stages.OrderBy(s => s.Order).ToArray();
        }

        internal static async Task<StageItem[]> GetStageItemsAsync(string pipelineId, string stageId, string token, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(pipelineId)) throw new ArgumentException("A deployment-pipeline id is required — list_deployment_pipelines returns the pipelines and their ids.");
            if (string.IsNullOrWhiteSpace(stageId)) throw new ArgumentException("A stage id is required — get_pipeline_stages lists the stages (with ids) for a pipeline.");
            using var http = NewClient(token);
            var url = "deploymentPipelines/" + Uri.EscapeDataString(pipelineId) + "/stages/" + Uri.EscapeDataString(stageId) + "/items";
            return (await GetAllPagesAsync<StageItem>(http, url, ct).ConfigureAwait(false)).ToArray();
        }

        // Test-only seam (mirrors DaxLibRest.ClientFactoryForTests): the deploy-feature tests point this at a factory
        // returning an HttpClient backed by a scripted HttpMessageHandler, so the LocalEngine deploy ops run end-to-end
        // against canned Fabric payloads with NO live tenant. Production leaves it null → NewClient builds a real client.
        // The Bearer header is still applied below either way, so the fake path exercises the real auth wiring. A
        // documented static seam beats a DI rewrite of an otherwise-untestable network boundary; the branch is free.
        internal static Func<HttpClient> TestClientFactory;

        internal static HttpClient NewClient(string token)
        {
            var http = TestClientFactory?.Invoke()
                ?? CreateSharedClient(BaseUrl);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return http;
        }

        internal static HttpClient CreateSharedClient(string baseUrl)
            => new(SharedHandler, disposeHandler: false)
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(100),
            };

        // ---- deploy (write lane — the ONE outward write; gated by the caller, see DeployGuard) ----
        internal sealed class DeployOutcome { public string Status; public string OperationId; public string DeploymentId; public string Error; }

        // POST the deploy then resolve it: 200 = synchronous success, 202 = LRO (poll to terminal), else a parsed error.
        internal static async Task<DeployOutcome> DeployAsync(string pipelineId, string jsonBody, string token, CancellationToken ct)
        {
            using var http = NewClient(token);
            var url = "deploymentPipelines/" + Uri.EscapeDataString(pipelineId) + "/deploy";
            var resp = await SendAsync(http, HttpMethod.Post, url, jsonBody, ct).ConfigureAwait(false);
            if (resp.Status == 202)
            {
                var st = await PollFrom202Async(http, resp, ct).ConfigureAwait(false);
                var err = NonSuccessError(st, "the deploy operation failed", resp.OperationId ?? resp.DeploymentId);
                return new DeployOutcome { Status = st.Status, OperationId = resp.OperationId ?? resp.DeploymentId, DeploymentId = resp.DeploymentId, Error = err };
            }
            if (resp.Ok) return new DeployOutcome { Status = "Succeeded", OperationId = resp.OperationId ?? resp.DeploymentId, DeploymentId = resp.DeploymentId };
            return new DeployOutcome { Status = "Failed", OperationId = resp.OperationId, DeploymentId = resp.DeploymentId, Error = ParseError(resp.Body, resp.Status) };
        }

        internal static async Task<DeploymentHistoryEntry[]> ListDeploymentOperationsAsync(string pipelineId, string token, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(pipelineId)) throw new ArgumentException("A deployment-pipeline id is required — list_deployment_pipelines returns the pipelines and their ids.");
            using var http = NewClient(token);
            var url = "deploymentPipelines/" + Uri.EscapeDataString(pipelineId) + "/operations";
            return (await GetAllPagesAsync<DeploymentHistoryEntry>(http, url, ct).ConfigureAwait(false)).ToArray();
        }

        // ---- generic LRO shapes (testable) ----
        // A GET whose response may be the body directly (200) OR a 202 long-running op whose result lives at
        // operations/{id}/result (Fabric Git status / Items getDefinition both do this). Returns the result JSON.
        internal static async Task<string> GetForResultAsync(HttpClient http, string url, CancellationToken ct)
        {
            var r = await SendAsync(http, HttpMethod.Get, url, ct).ConfigureAwait(false);
            if (r.Status == 202) return await FetchResultAsync(http, r, ct).ConfigureAwait(false);
            if (!r.Ok) throw new InvalidOperationException(ParseError(r.Body, r.Status));
            return r.Body;
        }

        // A POST whose response is 200/201 (done) OR 202 (poll to a terminal status). Returns the outcome; the
        // body is not needed (commit/update/updateDefinition report status only).
        internal static async Task<DeployOutcome> PostForStatusAsync(HttpClient http, string url, string jsonBody, CancellationToken ct)
        {
            var r = await SendAsync(http, HttpMethod.Post, url, jsonBody, ct).ConfigureAwait(false);
            if (r.Status == 202)
            {
                var st = await PollFrom202Async(http, r, ct).ConfigureAwait(false);
                var err = NonSuccessError(st, "the operation failed", r.OperationId);
                return new DeployOutcome { Status = st.Status, OperationId = r.OperationId, Error = err };
            }
            if (r.Ok) return new DeployOutcome { Status = "Succeeded", OperationId = r.OperationId };
            return new DeployOutcome { Status = "Failed", OperationId = r.OperationId, Error = ParseError(r.Body, r.Status) };
        }

        // Translate a polled operation state into an .Error for ANY non-Succeeded terminal — Failed AND the
        // "Running" poll-window timeout (which carries a real ErrorMessage that was otherwise dropped, making a
        // long-running commit/deploy look like a silent no-op). Surfaces the operation id so the caller can re-poll.
        private static string NonSuccessError(FabricOperationState st, string fallback, string operationId)
        {
            if (st.Status == "Succeeded") return null;
            var msg = (st.ErrorCode != null ? st.ErrorCode + ": " : "")
                + (st.ErrorMessage ?? (st.Status != null ? st.Status + " — operation did not succeed" : fallback));
            return string.IsNullOrEmpty(operationId) ? msg : msg + $" (operation {operationId})";
        }

        private static async Task<string> FetchResultAsync(HttpClient http, FabricResponse started, CancellationToken ct)
        {
            var st = await PollFrom202Async(http, started, ct).ConfigureAwait(false);
            if (st.Status != "Succeeded") throw new InvalidOperationException("operation " + st.Status + (st.ErrorMessage != null ? ": " + st.ErrorMessage : ""));
            var url = "operations/" + Uri.EscapeDataString(started.OperationId) + "/result";
            var r = await SendAsync(http, HttpMethod.Get, url, ct).ConfigureAwait(false);
            if (!r.Ok) throw new InvalidOperationException(ParseError(r.Body, r.Status));
            return r.Body;
        }

        // ---- Fabric Git (workspace ⇄ git) ----
        internal static async Task<FabricGitConnection> GetGitConnectionAsync(string workspaceId, string token, CancellationToken ct)
        {
            using var http = NewClient(token);
            var body = await GetForResultAsync(http, "workspaces/" + Uri.EscapeDataString(workspaceId) + "/git/connection", ct).ConfigureAwait(false);
            return ParseGitConnection(body);
        }

        internal static async Task<FabricGitStatus> GetGitStatusAsync(string workspaceId, string token, CancellationToken ct)
        {
            using var http = NewClient(token);
            var body = await GetForResultAsync(http, "workspaces/" + Uri.EscapeDataString(workspaceId) + "/git/status", ct).ConfigureAwait(false);
            return ParseGitStatus(body);
        }

        internal static async Task<DeployOutcome> GitCommitAsync(string workspaceId, string jsonBody, string token, CancellationToken ct)
        {
            using var http = NewClient(token);
            return await PostForStatusAsync(http, "workspaces/" + Uri.EscapeDataString(workspaceId) + "/git/commitToGit", jsonBody, ct).ConfigureAwait(false);
        }

        internal static async Task<DeployOutcome> GitUpdateAsync(string workspaceId, string jsonBody, string token, CancellationToken ct)
        {
            using var http = NewClient(token);
            return await PostForStatusAsync(http, "workspaces/" + Uri.EscapeDataString(workspaceId) + "/git/updateFromGit", jsonBody, ct).ConfigureAwait(false);
        }

        internal static async Task<DeployOutcome> GitConnectAsync(string workspaceId, string jsonBody, string token, CancellationToken ct)
        {
            using var http = NewClient(token);
            return await PostForStatusAsync(http, "workspaces/" + Uri.EscapeDataString(workspaceId) + "/git/connect", jsonBody, ct).ConfigureAwait(false);
        }

        internal static async Task<DeployOutcome> GitDisconnectAsync(string workspaceId, string token, CancellationToken ct)
        {
            using var http = NewClient(token);
            return await PostForStatusAsync(http, "workspaces/" + Uri.EscapeDataString(workspaceId) + "/git/disconnect", "{}", ct).ConfigureAwait(false);
        }

        // ---- Items API: publish a semantic-model definition (the fabric-cicd-equivalent, CI/CD lane) ----
        // POST workspaces/{id}/semanticModels/{itemId}/updateDefinition — a FULL OVERRIDE of the model definition
        // (200 sync OR 202 LRO). The body is { definition: { parts: [{path, payload(base64), payloadType}] } }.
        internal static async Task<DeployOutcome> UpdateSemanticModelDefinitionAsync(string workspaceId, string itemId, string jsonBody, string token, CancellationToken ct)
        {
            using var http = NewClient(token);
            var url = "workspaces/" + Uri.EscapeDataString(workspaceId) + "/semanticModels/" + Uri.EscapeDataString(itemId) + "/updateDefinition";
            return await PostForStatusAsync(http, url, jsonBody, ct).ConfigureAwait(false);
        }

        // ---- Items API: GET a report's definition (the Lineage "Measure Killer" cloud transport) ----
        // POST workspaces/{id}/reports/{reportId}/getDefinition?format=PBIR → 200 sync OR 202 LRO → operations/{id}/result.
        // READ-ONLY in behaviour (we only read the definition), but the Fabric getDefinition API requires a WRITE-capable
        // scope (Item.ReadWrite.All / Report.ReadWrite.All) + Contributor — the caller surfaces that as consent. An
        // encrypted sensitivity label or PBIRLegacy (preview opt-out) report makes this fail; the caller treats a throw
        // as "unreadable" per-report (fail-loud) rather than aborting the batch. $schema drift is tolerated downstream.
        internal sealed class ReportPart { public string Path; public string Content; }   // a base64-decoded text (.json) PBIR part

        internal static async Task<ReportPart[]> GetReportDefinitionAsync(string workspaceId, string reportId, string format, string token, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(workspaceId)) throw new ArgumentException("A workspace id is required.");
            if (string.IsNullOrWhiteSpace(reportId)) throw new ArgumentException("A report id is required.");
            using var http = NewClient(token);
            var url = "workspaces/" + Uri.EscapeDataString(workspaceId) + "/reports/" + Uri.EscapeDataString(reportId)
                      + "/getDefinition?format=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(format) ? "PBIR" : format);
            var body = await PostForResultAsync(http, url, null, ct).ConfigureAwait(false);
            return DecodeDefinitionParts(body);
        }

        // POST analog of GetForResultAsync: 200/201 returns the body directly; a 202 is an LRO whose result lives at
        // operations/{id}/result (getDefinition follows this pattern). Returns the result JSON.
        internal static async Task<string> PostForResultAsync(HttpClient http, string url, string jsonBody, CancellationToken ct)
        {
            var r = await SendAsync(http, HttpMethod.Post, url, jsonBody, ct).ConfigureAwait(false);
            if (r.Status == 202) return await FetchResultAsync(http, r, ct).ConfigureAwait(false);
            if (!r.Ok) throw new InvalidOperationException(ParseError(r.Body, r.Status));
            return r.Body;
        }

        // Pull the *.json parts out of a getDefinition result: { definition: { parts: [ {path, payload(base64), payloadType} ] } }.
        // Only the JSON parts carry model field references (report.json / pages/**/visual.json / reportExtensions.json); a
        // binary asset (theme image, etc.) or a non-decodable payload is skipped. Defensive: a missing definition/parts
        // yields an empty array (a report with nothing to parse) rather than throwing.
        internal static ReportPart[] DecodeDefinitionParts(string body, long maxDecodedBytes = MaxDecodedDefinitionBytes)
        {
            var parts = new List<ReportPart>();
            long decodedBytes = 0;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch { return parts.ToArray(); }   // a non-JSON result body (proxy HTML / gateway page) degrades to "no parts", like ParseError/ParseOperationState
            using (doc)
            if (doc.RootElement.TryGetProperty("definition", out var def) && def.ValueKind == JsonValueKind.Object
                && def.TryGetProperty("parts", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in arr.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    var path = p.TryGetProperty("path", out var pe) && pe.ValueKind == JsonValueKind.String ? pe.GetString() : null;
                    if (string.IsNullOrEmpty(path) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    var payload = p.TryGetProperty("payload", out var pl) && pl.ValueKind == JsonValueKind.String ? pl.GetString() : null;
                    if (string.IsNullOrEmpty(payload)) continue;
                    var upperBound = ((long)payload.Length + 3) / 4 * 3;
                    if (upperBound > maxDecodedBytes - decodedBytes)
                        throw new InvalidOperationException($"The decoded Fabric definition exceeds the {FormatBytes(maxDecodedBytes)} safety limit.");
                    try
                    {
                        var bytes = Convert.FromBase64String(payload);
                        decodedBytes += bytes.LongLength;
                        parts.Add(new ReportPart { Path = path, Content = StrictUtf8.GetString(bytes) });
                    }
                    catch (FormatException) { /* a non-base64 part mislabelled .json is ignored */ }
                    catch (DecoderFallbackException) { /* a binary part mislabelled .json is ignored */ }
                }
            }
            return parts.ToArray();
        }

        internal static FabricGitStatus ParseGitStatus(string body)
        {
            var st = new FabricGitStatus();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            st.WorkspaceHead = Str(root, "workspaceHead");
            st.RemoteCommitHash = Str(root, "remoteCommitHash");
            var changes = new List<FabricGitChange>();
            if (root.TryGetProperty("changes", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var c in arr.EnumerateArray())
                {
                    var ch = new FabricGitChange
                    {
                        WorkspaceChange = Str(c, "workspaceChange"),
                        RemoteChange = Str(c, "remoteChange"),
                        ConflictType = Str(c, "conflictType"),
                    };
                    if (c.TryGetProperty("itemMetadata", out var im) && im.ValueKind == JsonValueKind.Object)
                    {
                        ch.ItemType = Str(im, "itemType");
                        ch.DisplayName = Str(im, "displayName");
                        if (im.TryGetProperty("itemIdentifier", out var id) && id.ValueKind == JsonValueKind.Object)
                        {
                            ch.ObjectId = Str(id, "objectId");
                            ch.LogicalId = Str(id, "logicalId");
                        }
                    }
                    changes.Add(ch);
                }
            st.Changes = changes.ToArray();
            st.Conflicts = changes.Any(c => string.Equals(c.ConflictType, "Conflict", StringComparison.OrdinalIgnoreCase));
            return st;
        }

        internal static FabricGitConnection ParseGitConnection(string body)
        {
            var gc = new FabricGitConnection();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            gc.State = Str(root, "gitConnectionState");
            if (root.TryGetProperty("gitProviderDetails", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                gc.ProviderType = Str(p, "gitProviderType");
                gc.Organization = Str(p, "organizationName") ?? Str(p, "ownerName");
                gc.Project = Str(p, "projectName");
                gc.Repository = Str(p, "repositoryName");
                gc.Branch = Str(p, "branchName");
                gc.Directory = Str(p, "directoryName");
            }
            if (root.TryGetProperty("gitSyncDetails", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                gc.Head = Str(s, "head");
                gc.LastSyncTime = Str(s, "lastSyncTime");
            }
            return gc;
        }

        private static string Str(JsonElement e, string prop)
            => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // ---- pagination (testable) ----
        // Follows continuationToken across pages (preferring the pre-built, already-URL-encoded continuationUri).
        // A missing/empty continuationToken is the documented end-of-pages signal (the field is OMITTED, not null).
        internal static async Task<List<T>> GetAllPagesAsync<T>(HttpClient http, string firstUrl, CancellationToken ct,
            int maxPages = 1000, long maxResponseBytes = MaxPagedResponseBytes)
        {
            if (maxPages <= 0) throw new ArgumentOutOfRangeException(nameof(maxPages));
            if (maxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));
            var items = new List<T>();
            var url = firstUrl;
            long responseBytes = 0;
            for (var page = 0; page < maxPages; page++)
            {
                var r = await SendAsync(http, HttpMethod.Get, url, ct).ConfigureAwait(false);
                if (!r.Ok) throw new InvalidOperationException(ParseError(r.Body, r.Status));
                responseBytes += r.BodyBytes;
                if (responseBytes > maxResponseBytes)
                    throw new InvalidOperationException($"Fabric pagination exceeded the {FormatBytes(maxResponseBytes)} aggregate response limit; results are incomplete.");
                var env = JsonSerializer.Deserialize<Envelope<T>>(r.Body, JsonOpts);
                if (env?.Value != null) items.AddRange(env.Value);
                if (string.IsNullOrEmpty(env?.ContinuationToken)) break;
                if (page == maxPages - 1)
                    throw new InvalidOperationException($"Fabric pagination exceeded the {maxPages}-page safety limit; results are incomplete.");
                // Prefer the API's pre-built continuationUri, but ONLY if it points back at the Fabric host —
                // we carry a Bearer header, so never follow an off-origin URL even if the response was tampered with.
                url = !string.IsNullOrEmpty(env.ContinuationUri) && IsFabricHost(env.ContinuationUri)
                    ? env.ContinuationUri
                    : AppendQuery(firstUrl, "continuationToken", env.ContinuationToken);
            }
            return items;
        }

        // ---- long-running operation poller (testable) — for the Phase-3 write/deploy lanes ----
        // Pull the operation id out of a 202 response's x-ms-operation-id header, then poll to a terminal state.
        internal static async Task<FabricOperationState> PollFrom202Async(HttpClient http, FabricResponse started, CancellationToken ct)
        {
            if (started.Status != 202) throw new InvalidOperationException($"Expected a 202 Accepted to start an operation; got {started.Status}.");
            if (string.IsNullOrEmpty(started.OperationId)) throw new InvalidOperationException("The 202 response carried no x-ms-operation-id header.");
            return await PollOperationAsync(http, started.OperationId, ct, started.RetryAfter ?? 0).ConfigureAwait(false);
        }

        internal static async Task<FabricOperationState> PollOperationAsync(HttpClient http, string operationId, CancellationToken ct, int firstDelaySeconds = 0, int maxPolls = 240)
        {
            var url = "operations/" + Uri.EscapeDataString(operationId);
            var wait = Math.Max(0, Math.Min(firstDelaySeconds, 30));
            for (var i = 0; i < maxPolls; i++)
            {
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
                var r = await SendAsync(http, HttpMethod.Get, url, ct).ConfigureAwait(false);
                if (!r.Ok) throw new InvalidOperationException(ParseError(r.Body, r.Status));
                var st = ParseOperationState(r.Body);
                if (st.Status is "Succeeded" or "Failed") return st;   // terminal set per the MS sample; keep polling otherwise
                wait = Math.Max(1, Math.Min(r.RetryAfter ?? 2, 30));   // honour Retry-After between polls, default 2s
            }
            return new FabricOperationState { Status = "Running", ErrorMessage = "The operation did not reach a terminal state within the poll window." };
        }

        // ---- the one HTTP send: 429 throttle handling (Retry-After, bounded backoff), header extraction ----
        internal sealed class FabricResponse
        {
            public int Status;
            public string Body;
            public long BodyBytes;
            public string Location;     // poll/result URL on a 202 — reserved for the Phase-3 result-fetch leg (operations/{id}/result)
            public string OperationId;  // x-ms-operation-id on a 202
            public string DeploymentId; // deployment-id on a deploy 202 (the pipeline-history operation id)
            public int? RetryAfter;     // seconds
            public bool Ok => Status >= 200 && Status < 300;
        }

        internal static Task<FabricResponse> SendAsync(HttpClient http, HttpMethod method, string url, CancellationToken ct)
            => SendAsync(http, method, url, null, ct);

        // jsonBody is recreated as fresh StringContent on each attempt so a 429 retry is body-safe (content is consumed once).
        internal static async Task<FabricResponse> SendAsync(HttpClient http, HttpMethod method, string url, string jsonBody, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                using var req = new HttpRequestMessage(method, url);
                if (jsonBody != null) req.Content = new System.Net.Http.StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var status = (int)resp.StatusCode;
                var retryAfter = RetryAfterSeconds(resp);
                if (status == 429 && attempt < 4)
                {
                    var delay = Math.Min(retryAfter ?? (int)Math.Pow(2, attempt + 1), 60);
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                    continue;   // bounded retry on throttle
                }
                var (body, bodyBytes) = resp.Content != null
                    ? await ReadBodyAsync(resp.Content, MaxResponseBodyBytes, ct).ConfigureAwait(false)
                    : (string.Empty, 0L);
                resp.Headers.TryGetValues("x-ms-operation-id", out var opIds);
                resp.Headers.TryGetValues("deployment-id", out var depIds);
                return new FabricResponse
                {
                    Status = status,
                    Body = body,
                    BodyBytes = bodyBytes,
                    Location = resp.Headers.Location?.ToString(),
                    OperationId = opIds?.FirstOrDefault(),
                    DeploymentId = depIds?.FirstOrDefault(),
                    RetryAfter = retryAfter,
                };
            }
        }

        internal static async Task<(string Body, long Bytes)> ReadBodyAsync(HttpContent content, int maxBytes, CancellationToken ct)
        {
            var declared = content.Headers.ContentLength;
            if (declared > maxBytes)
                throw new InvalidOperationException($"The Fabric REST response exceeds the {FormatBytes(maxBytes)} safety limit.");

            await using var source = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var destination = new MemoryStream(declared.HasValue && declared.Value >= 0 && declared.Value <= maxBytes ? (int)declared.Value : 0);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > maxBytes)
                        throw new InvalidOperationException($"The Fabric REST response exceeds the {FormatBytes(maxBytes)} safety limit.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                try { return (StrictUtf8.GetString(destination.GetBuffer(), 0, checked((int)total)), total); }
                catch (DecoderFallbackException)
                { throw new InvalidOperationException("The Fabric REST response is not valid UTF-8 text."); }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        private static string FormatBytes(long bytes)
            => bytes % (1024 * 1024) == 0 ? $"{bytes / (1024 * 1024)} MiB" : $"{bytes} byte" + (bytes == 1 ? "" : "s");

        private static int? RetryAfterSeconds(HttpResponseMessage resp)
        {
            var ra = resp.Headers.RetryAfter;
            if (ra == null) return null;
            if (ra.Delta.HasValue) return (int)ra.Delta.Value.TotalSeconds;
            if (ra.Date.HasValue) { var s = (ra.Date.Value - DateTimeOffset.UtcNow).TotalSeconds; return s > 0 ? (int)s : 0; }
            return null;
        }

        // ---- parsing ----
        // Defensive like ParseError: a non-JSON / wrong-typed 2xx poll body (proxy HTML, empty, a number status)
        // degrades to a non-terminal "Running" rather than throwing an unscrubbed exception — so the poller keeps
        // going (bounded by maxPolls) instead of crashing, and never echoes the body.
        internal static FabricOperationState ParseOperationState(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var st = new FabricOperationState
                {
                    Status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : "Running",
                    PercentComplete = root.TryGetProperty("percentComplete", out var p) && p.TryGetInt32(out var pi) ? pi : 0,
                };
                if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                {
                    st.ErrorCode = err.TryGetProperty("errorCode", out var ec) && ec.ValueKind == JsonValueKind.String ? ec.GetString() : null;
                    st.ErrorMessage = err.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String ? Scrub(em.GetString()) : null;
                }
                return st;
            }
            catch { return new FabricOperationState { Status = "Running", ErrorMessage = "The operation poll returned an unparseable body." }; }
        }

        // Branch on errorCode (the stable contract); message is human-readable but subject to change. requestId is
        // always logged for support. Never echo a raw provider message — scrub it.
        internal static string ParseError(string body, int status)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var code = root.TryGetProperty("errorCode", out var c) ? c.GetString() : null;
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                var reqId = root.TryGetProperty("requestId", out var r) ? r.GetString() : null;
                var s = $"Fabric REST {status}";
                if (!string.IsNullOrEmpty(code)) s += " " + code;
                if (!string.IsNullOrEmpty(msg)) s += ": " + msg;
                if (!string.IsNullOrEmpty(reqId)) s += $" (requestId {reqId})";
                return Scrub(s) + StatusHint(status, code);
            }
            catch
            {
                var b = body ?? string.Empty;   // a non-JSON body (HTML interstitial, etc.) — cap it so we don't dump a page
                if (b.Length > 300) b = b.Substring(0, 300) + "…";
                return Scrub($"Fabric REST {status}: " + b) + StatusHint(status, null);
            }
        }

        // Actionable hints for the common gates a read-only caller hits (the gotchas the research surfaced).
        private static string StatusHint(int status, string code) => status switch
        {
            401 => "  [Sign in again, or the token lacks the Fabric scope.]",
            403 => "  [Authenticated, but you lack the role — deployment-pipeline reads need an Admin pipeline role (stages) and Contributor on the stage workspace (items). A service principal also needs the tenant's 'Service principals can use Fabric APIs' setting.]",
            404 => "  [Not found — check the id.]",
            _ => string.Empty,
        };

        // internal so the engine layer can scrub a caught exception message (e.g. an Azure.Identity auth failure,
        // which does NOT route through ParseError) before reporting it on a result DTO.
        internal static string Scrub(string m)
        {
            if (string.IsNullOrEmpty(m)) return m;
            m = System.Text.RegularExpressions.Regex.Replace(m, @"(?i)\b(password|pwd|access[_ ]?token)\s*=\s*[^;]+", "$1=***");
            m = System.Text.RegularExpressions.Regex.Replace(m, @"(?i)bearer\s+[A-Za-z0-9._\-]+", "Bearer ***");
            // A BARE JWT (header.payload.signature, no "Bearer " prefix) — e.g. echoed inside a JSON error body — would
            // otherwise leak. A JWT header always starts base64url("{\"...") = "eyJ", so this pattern never false-positives.
            m = System.Text.RegularExpressions.Regex.Replace(m, @"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", "***");
            return m;
        }

        private static string AppendQuery(string url, string key, string val)
        {
            var sep = url.Contains('?') ? '&' : '?';
            return url + sep + key + "=" + Uri.EscapeDataString(val);
        }

        // We attach a Bearer header to every request, so only ever follow a continuation URL that stays on the
        // Fabric host (defense-in-depth against a tampered response redirecting the token off-origin).
        private static bool IsFabricHost(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out var u)
               && string.Equals(u.Host, new Uri(BaseUrl).Host, StringComparison.OrdinalIgnoreCase);

        private sealed class Envelope<T>
        {
            public List<T> Value { get; set; }
            public string ContinuationToken { get; set; }
            public string ContinuationUri { get; set; }
        }
    }
}
