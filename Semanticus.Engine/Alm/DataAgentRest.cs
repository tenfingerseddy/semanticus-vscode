using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// REST client for Fabric Data Agent items (docs/data-agent-tab-plan.md §1) — a definition-based Fabric item on
    /// the Items API. REUSES the FabricRest cross-cutting internals (pagination, the 202→LRO poller, base64 part
    /// decode, error scrub) — this class only adds the data-agent-specific endpoints + the part codec. Every error
    /// is scrubbed by FabricRest before it crosses a door. No live write happens except through the explicit
    /// Create/Update/Delete methods, all gated dry-run by the caller (LocalEngine.DataAgent).
    /// </summary>
    internal static class DataAgentRest
    {
        // ---- part paths (EXACT, from spec §1) ----
        internal const string DataAgentJsonPath = "Files/Config/data_agent.json";
        internal const string DraftStageConfigPath = "Files/Config/draft/stage_config.json";
        internal const string PublishInfoPath = "Files/Config/publish_info.json";
        internal const string PublishedStageConfigPath = "Files/Config/published/stage_config.json";
        internal const string DraftPrefix = "Files/Config/draft/";
        internal const string PublishedPrefix = "Files/Config/published/";
        private const string DatasourceFile = "datasource.json";
        private const string FewshotsFile = "fewshots.json";

        // $schema values: the MS Learn tables show BARE versions ("2.1.0") but the SERVICE's own default
        // definition round-trips FULL schema URLs (live-verified 2026-07-03 on a portal-created agent) —
        // emit what the service emits.
        internal const string DataAgentSchema = "https://developer.microsoft.com/json-schemas/fabric/item/dataAgent/definition/dataAgent/2.1.0/schema.json";
        internal const string StageConfigSchema = "https://developer.microsoft.com/json-schemas/fabric/item/dataAgent/definition/stageConfiguration/1.0.0/schema.json";
        internal const string PublishInfoSchema = "https://developer.microsoft.com/json-schemas/fabric/item/dataAgent/definition/publishInfo/1.0.0/schema.json";
        internal const string FewshotsSchema = "https://developer.microsoft.com/json-schemas/fabric/item/dataAgent/definition/fewShots/1.0.0/schema.json";

        // ---- items ----
        internal static async Task<FabricItem[]> ListItemsAsync(string workspaceId, string token, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(workspaceId)) throw new ArgumentException("A workspace id is required.");
            using var http = FabricRest.NewClient(token);
            var url = "workspaces/" + Uri.EscapeDataString(workspaceId) + "/items";
            var items = await FabricRest.GetAllPagesAsync<FabricItem>(http, url, ct).ConfigureAwait(false);
            return items.ToArray();
        }

        // Data agents are filtered by TYPE: docs lag the exact string, so match case-insensitively on a type that
        // CONTAINS "dataagent" (expected "DataAgent") — the caller surfaces the observed types for [verify-at-build].
        internal static bool IsDataAgentType(string type)
            => !string.IsNullOrEmpty(type) && type.IndexOf("dataagent", StringComparison.OrdinalIgnoreCase) >= 0;

        internal static async Task<FabricRest.ReportPart[]> GetDefinitionAsync(string workspaceId, string itemId, string token, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(workspaceId)) throw new ArgumentException("A workspace id is required.");
            if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("An item id is required.");
            using var http = FabricRest.NewClient(token);
            var url = "workspaces/" + Uri.EscapeDataString(workspaceId) + "/items/" + Uri.EscapeDataString(itemId) + "/getDefinition";
            var body = await FabricRest.PostForResultAsync(http, url, null, ct).ConfigureAwait(false);
            return FabricRest.DecodeDefinitionParts(body);
        }

        // POST /items — 201 sync or 202 LRO (both resolve via PostForResultAsync to a body carrying the created item).
        // Returns the created item id (parsed from the result body).
        internal static async Task<string> CreateItemAsync(string workspaceId, string displayName, string itemType, string definitionJson, string token, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(workspaceId)) throw new ArgumentException("A workspace id is required.");
            using var http = FabricRest.NewClient(token);
            var url = "workspaces/" + Uri.EscapeDataString(workspaceId) + "/items";
            var body = MergeCreateBody(displayName, itemType, definitionJson);
            var result = await FabricRest.PostForResultAsync(http, url, body, ct).ConfigureAwait(false);
            return ParseItemId(result);
        }

        internal static async Task<FabricRest.DeployOutcome> UpdateDefinitionAsync(string workspaceId, string itemId, string definitionJson, string token, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(workspaceId)) throw new ArgumentException("A workspace id is required.");
            if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("An item id is required.");
            using var http = FabricRest.NewClient(token);
            var url = "workspaces/" + Uri.EscapeDataString(workspaceId) + "/items/" + Uri.EscapeDataString(itemId) + "/updateDefinition";
            return await FabricRest.PostForStatusAsync(http, url, definitionJson, ct).ConfigureAwait(false);
        }

        internal static async Task DeleteItemAsync(string workspaceId, string itemId, string token, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(workspaceId)) throw new ArgumentException("A workspace id is required.");
            if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("An item id is required.");
            using var http = FabricRest.NewClient(token);
            var url = "workspaces/" + Uri.EscapeDataString(workspaceId) + "/items/" + Uri.EscapeDataString(itemId);
            var r = await FabricRest.SendAsync(http, HttpMethod.Delete, url, ct).ConfigureAwait(false);
            if (!r.Ok) throw new InvalidOperationException(FabricRest.ParseError(r.Body, r.Status));
        }

        // ---- part codec ----
        // Build the { definition: { parts: [ {path, payload: base64(text), payloadType: "InlineBase64"} ] } } body.
        internal static string BuildDefinitionJson(IEnumerable<(string path, string jsonText)> parts)
        {
            var arr = parts.Select(p => new
            {
                path = p.path,
                payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(p.jsonText ?? string.Empty)),
                payloadType = "InlineBase64",
            }).ToArray();
            return JsonSerializer.Serialize(new { definition = new { parts = arr } });
        }

        /// <summary>A single draft/published datasource folder: its "{type}-{name}" folder name + the two files.</summary>
        internal sealed class DataAgentDatasourceFolder
        {
            public string Folder;
            public string DatasourceJson;
            public string FewshotsJson;
        }

        /// <summary>The typed view of a decoded data-agent definition. <see cref="AllParts"/> keeps EVERY raw part so
        /// a read-modify-write never drops an unknown one (the definition-write contract).</summary>
        internal sealed class DataAgentParts
        {
            public string DataAgentJson;
            public string DraftStageConfig;
            public string PublishInfo;
            public string PublishedStageConfig;
            public List<DataAgentDatasourceFolder> DraftDatasources = new();
            public List<DataAgentDatasourceFolder> PublishedDatasources = new();
            public FabricRest.ReportPart[] AllParts = Array.Empty<FabricRest.ReportPart>();
        }

        internal static DataAgentParts ParseDataAgentParts(FabricRest.ReportPart[] parts)
        {
            var result = new DataAgentParts { AllParts = parts ?? Array.Empty<FabricRest.ReportPart>() };
            var draft = new Dictionary<string, DataAgentDatasourceFolder>(StringComparer.OrdinalIgnoreCase);
            var pub = new Dictionary<string, DataAgentDatasourceFolder>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in result.AllParts)
            {
                var path = p?.Path?.Replace('\\', '/');
                if (string.IsNullOrEmpty(path)) continue;
                if (path.Equals(DataAgentJsonPath, StringComparison.OrdinalIgnoreCase)) result.DataAgentJson = p.Content;
                else if (path.Equals(DraftStageConfigPath, StringComparison.OrdinalIgnoreCase)) result.DraftStageConfig = p.Content;
                else if (path.Equals(PublishInfoPath, StringComparison.OrdinalIgnoreCase)) result.PublishInfo = p.Content;
                else if (path.Equals(PublishedStageConfigPath, StringComparison.OrdinalIgnoreCase)) result.PublishedStageConfig = p.Content;
                else if (path.StartsWith(DraftPrefix, StringComparison.OrdinalIgnoreCase)) AddDatasourcePart(draft, path.Substring(DraftPrefix.Length), p.Content);
                else if (path.StartsWith(PublishedPrefix, StringComparison.OrdinalIgnoreCase)) AddDatasourcePart(pub, path.Substring(PublishedPrefix.Length), p.Content);
            }
            result.DraftDatasources = draft.Values.ToList();
            result.PublishedDatasources = pub.Values.ToList();
            return result;
        }

        // rel = "{folder}/datasource.json" | "{folder}/fewshots.json" (already past the draft/published prefix).
        private static void AddDatasourcePart(Dictionary<string, DataAgentDatasourceFolder> map, string rel, string content)
        {
            var slash = rel.IndexOf('/');
            if (slash <= 0) return;   // not a folder-scoped file (e.g. the stage_config, already handled above)
            var folder = rel.Substring(0, slash);
            var file = rel.Substring(slash + 1);
            if (!map.TryGetValue(folder, out var f)) { f = new DataAgentDatasourceFolder { Folder = folder }; map[folder] = f; }
            if (file.Equals(DatasourceFile, StringComparison.OrdinalIgnoreCase)) f.DatasourceJson = content;
            else if (file.Equals(FewshotsFile, StringComparison.OrdinalIgnoreCase)) f.FewshotsJson = content;
        }

        // Publish = a read-only copy of every draft/* part into published/*, plus publish_info.json. READ-MODIFY-WRITE:
        // the caller passes ALL existing parts; unknown parts are preserved verbatim. Pure + deterministic (testable
        // offline). Returns the full part list to (re)write via updateDefinition.
        internal static List<(string path, string jsonText)> BuildPublishParts(FabricRest.ReportPart[] existing, string description)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in existing ?? Array.Empty<FabricRest.ReportPart>())
                if (p != null && !string.IsNullOrEmpty(p.Path)) map[p.Path.Replace('\\', '/')] = p.Content;
            foreach (var kv in map.Where(k => k.Key.StartsWith(DraftPrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                map[PublishedPrefix + kv.Key.Substring(DraftPrefix.Length)] = kv.Value;
            map[PublishInfoPath] = JsonSerializer.Serialize(new Dictionary<string, object> { ["$schema"] = PublishInfoSchema, ["description"] = description ?? string.Empty });
            return map.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        // ---- helpers ----
        private static string MergeCreateBody(string displayName, string itemType, string definitionJson)
        {
            using var doc = JsonDocument.Parse(definitionJson);
            var def = doc.RootElement.GetProperty("definition");
            var body = new Dictionary<string, object>
            {
                ["displayName"] = displayName,
                ["type"] = itemType,
                ["definition"] = JsonSerializer.Deserialize<JsonElement>(def.GetRawText()),
            };
            return JsonSerializer.Serialize(body);
        }

        private static string ParseItemId(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body ?? "");
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    return id.GetString();
            }
            catch { /* a 202 result body without an id degrades to null — the caller reports the create succeeded without an id */ }
            return null;
        }
    }
}
