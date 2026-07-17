using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>
    /// Fabric Data Agent ops (docs/data-agent-tab-plan.md §3) — dual-drive like every other capability. Reads
    /// (list/get) are free; EVERY write on this surface is Pro (Kane 2026-07-07: create/update/publish/delete +
    /// generate — one legible line, "configure and publish data agents with Pro", instead of gating only the
    /// generate verb). Writes stay DRY-RUN by default for Pro (commit=false returns the exact request and changes
    /// NOTHING). The engine holds no inference and never queries the agent (golden rule #1) — a published agent's
    /// MCP endpoint is the query surface, out of scope here. Every EXECUTED write rides an ActivityEvent onto the
    /// bus so the deploy trail + experience log capture it.
    /// </summary>
    public sealed partial class LocalEngine
    {
        // The Fabric item type for a data agent. [verify-at-build]: the item-management support matrix does not yet
        // list it; "DataAgent" is the expected string — list_data_agents' ObservedItemTypes discovers the real one
        // from a live workspace. Kept in ONE place so the confirmed string is a single edit.
        private const string DataAgentItemType = "DataAgent";

        // Mirrors DAC-AI-INSTRUCTIONS-LEN: Prep-for-AI LSDL caps AI instructions at 10k, but the data-agent
        // stage_config allows more; the portal caps at 15k. Refuse past that (nothing is sent).
        private const int AiInstructionsCap = 15000;

        // ---- reads (free) --------------------------------------------------------------------------

        public async Task<DataAgentList> ListDataAgentsAsync(string workspaceId, string authMode, string tenantId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(workspaceId)) return new DataAgentList { Error = "A workspaceId is required — list_workspaces shows your workspaces and their ids." };
            try
            {
                var token = await EntraToken.AcquireFabricAsync(authMode, null, cancellationToken, tenantId);
                var items = await DataAgentRest.ListItemsAsync(workspaceId, token, cancellationToken);
                var agents = items.Where(i => DataAgentRest.IsDataAgentType(i.Type))
                    .Select(i => new DataAgentInfo { Id = i.Id, Name = i.DisplayName, Description = i.Description, Type = i.Type, Published = null })
                    .ToArray();
                var observed = items.Select(i => i.Type).Where(t => !string.IsNullOrEmpty(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();
                // Fail-loud on a zero match (docs lag the type string): surface the observed types so the caller can
                // confirm the real one, rather than silently claiming the workspace has no agents.
                var note = agents.Length == 0
                    ? $"No items matched the data-agent type filter (type contains 'dataagent'). Observed item types: {(observed.Length > 0 ? string.Join(", ", observed) : "(none)")}. [verify-at-build] confirm the real data-agent type string from this list."
                    : null;
                return new DataAgentList { Agents = agents, ObservedItemTypes = observed, Note = note };
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return new DataAgentList { Error = FabricRest.Scrub(ex.Message) }; }
        }

        public async Task<DataAgentDetail> GetDataAgentAsync(string workspaceId, string agentId, string authMode, string tenantId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
                return new DataAgentDetail { Error = "A workspaceId and agentId are required — list_workspaces finds the workspace, list_data_agents finds the agent id within it." };
            try
            {
                var token = await EntraToken.AcquireFabricAsync(authMode, null, cancellationToken, tenantId);
                var parts = await DataAgentRest.GetDefinitionAsync(workspaceId, agentId, token, cancellationToken);
                var parsed = DataAgentRest.ParseDataAgentParts(parts);
                var published = parsed.PublishInfo != null;
                return new DataAgentDetail
                {
                    // Name/description live on the /items list (not the definition parts) — get_data_agent decodes the
                    // definition, so those stay null here and list_data_agents carries them.
                    Info = new DataAgentInfo { Id = agentId, Type = DataAgentItemType, Published = published },
                    Draft = BuildStage(parsed, published: false),
                    Published = published ? BuildStage(parsed, published: true) : null,
                    Note = parts.Length == 0 ? "The item has no decodable definition parts (empty or a non-data-agent item)." : null,
                };
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return new DataAgentDetail { Error = FabricRest.Scrub(ex.Message) }; }
        }

        // ---- the Semanticus verb: scope the OPEN model into a semantic_model datasource (Pro) ------

        public async Task<DataAgentConfig> GenerateDataAgentConfigFromModelAsync(int maxColumnsPerTable = 200)
        {
            // Part of the one-line rule above: configuring data agents (this one-click scope included) is Pro.
            Entitlement.EntitlementGuard.RequirePro(_entitlement, "generate_data_agent_config_from_model (one-click scope of the whole model into a data-agent datasource)",
                "Browsing data agents stays free (list_data_agents / get_data_agent); configuring and publishing them is Pro.");
            var s = _sessions.Require();   // an instructive throw when no model is open
            var cap = maxColumnsPerTable <= 0 ? 200 : maxColumnsPerTable;
            var connectionContext = await ConnectionContextAsync();

            return await s.ReadAsync(m =>
            {
                PrepForAiConfig prep = null;
                try { prep = PrepForAiReader.Read(m); } catch { /* prep-for-AI best-effort */ }
                var excluded = new HashSet<string>(prep?.AiSchemaExcludedNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

                var elements = new List<object>();
                var excludedHit = 0;
                foreach (var t in m.Tables)
                {
                    var tableExcluded = excluded.Contains(t.Name);
                    if (tableExcluded) excludedHit++;
                    var children = new List<object>();
                    foreach (var c in t.Columns.Where(c => c.Type != ColumnType.RowNumber).Take(cap))
                    {
                        // Match the LSDL exclusion on BOTH the bare column name and the table-qualified form.
                        var colExcluded = excluded.Contains(c.Name) || excluded.Contains(t.Name + "." + c.Name);
                        if (colExcluded) excludedHit++;
                        children.Add(new Dictionary<string, object>
                        {
                            ["display_name"] = c.Name,
                            ["type"] = "semantic_model.column",
                            ["data_type"] = c.DataType.ToString(),
                            ["is_selected"] = !c.IsHidden && !colExcluded && !tableExcluded,
                            ["description"] = c.Description ?? string.Empty,
                        });
                    }
                    foreach (var me in t.Measures)
                        children.Add(new Dictionary<string, object>
                        {
                            ["display_name"] = me.Name,
                            ["type"] = "semantic_model.measure",
                            ["is_selected"] = !me.IsHidden && !tableExcluded,
                            ["description"] = me.Description ?? string.Empty,
                        });
                    elements.Add(new Dictionary<string, object>
                    {
                        ["display_name"] = t.Name,
                        ["type"] = "semantic_model.table",
                        ["is_selected"] = !t.IsHidden && !tableExcluded,
                        ["description"] = t.Description ?? string.Empty,
                        ["children"] = children,
                    });
                }

                var modelName = m.Database?.Name ?? m.Name ?? "SemanticModel";
                // Ids: the datasource needs Fabric artifact GUIDs. The live connection (LiveOrigin) holds the XMLA
                // endpoint + dataset NAME, not those GUIDs, and resolving name→GUID needs a live call the caller runs
                // later — so v1 emits placeholders (false-safe, never a guessed id) + discloses it in the Note.
                var datasource = new Dictionary<string, object>
                {
                    ["artifactId"] = "<dataset-id>",
                    ["workspaceId"] = "<workspace-id>",
                    ["displayName"] = modelName,
                    ["type"] = "semantic_model",
                    ["elements"] = elements,
                };

                var aiInstructions = prep?.AiInstructions ?? string.Empty;
                var notes = new List<string>();
                notes.Add("artifactId/workspaceId are PLACEHOLDERS — fill them with the target semantic model's Fabric ids before update_data_agent (resolvable from list_workspaces + the workspace items).");
                var publish = connectionContext?.Publishing;
                if (publish?.Available == true)
                    notes.Add($"Publish destination selected: endpoint '{publish.Endpoint}', model '{publish.Database ?? publish.ModelName}'. Resolve the GUIDs from that destination.");
                else
                {
                    var live = _sessions.Current?.LiveOrigin;
                    if (live != null) notes.Add($"Live-bound to endpoint '{live.Endpoint}', dataset '{live.Database}'. Resolve the GUIDs from those.");
                    else notes.Add("No publish destination is selected. Choose one in Connections before applying this source so the live semantic model is unambiguous.");
                }
                if (string.IsNullOrEmpty(aiInstructions)) notes.Add("No LSDL AI instructions on the model — aiInstructions seeded empty; author them or set_ai_instructions first.");
                if (excludedHit > 0) notes.Add($"Scoped from Prep-for-AI: {excludedHit} object(s) unselected by the model's AI data schema.");

                return new DataAgentConfig
                {
                    DatasourceJson = JsonSerializer.Serialize(datasource, new JsonSerializerOptions { WriteIndented = true }),
                    AiInstructions = aiInstructions,
                    DisplayName = modelName,
                    Note = string.Join(" ", notes),
                };
            });
        }

        // ---- writes (Pro; dry-run by default) ------------------------------------------------------

        // One gate helper for every data-agent write so the line stays legible ("writes are Pro, reads are
        // free") and the message identical on all four ops. Thrown at the very top — before validation and
        // before the dry-run branch — so a free call never gets halfway into a write flow it can't finish.
        private void RequireProDataAgentWrite(string verb)
            => Entitlement.EntitlementGuard.RequirePro(_entitlement, $"{verb} (data-agent writes)",
                "Browsing data agents stays free (list_data_agents / get_data_agent); configuring and publishing them from here is Pro.");

        public async Task<DataAgentWriteReport> CreateDataAgentAsync(string workspaceId, string name, string aiInstructions, bool commit, string authMode, string tenantId, string origin, CancellationToken cancellationToken = default)
        {
            RequireProDataAgentWrite("create_data_agent");
            aiInstructions ??= string.Empty;
            if (string.IsNullOrWhiteSpace(workspaceId)) return Err("A workspaceId is required — list_workspaces shows your workspaces and their ids.");
            if (string.IsNullOrWhiteSpace(name)) return Err("A name is required.");
            if (aiInstructions.Length > AiInstructionsCap) return Err($"aiInstructions is {aiInstructions.Length} chars — over the {AiInstructionsCap} cap. Nothing was sent.");
            var summary = $"create_data_agent '{name}' in {workspaceId} → parts: {DataAgentRest.DataAgentJsonPath}, {DataAgentRest.DraftStageConfigPath}; aiInstructions {aiInstructions.Length} chars; 0 datasources";
            // Dry-run path returns BEFORE token acquisition — no HTTP, offline-safe.
            if (!commit) return DryRun(summary);
            // Agent-permissions gate — creating an item in a Fabric workspace is a live cloud write. The workspace id
            // is the gate's target: unlabelled ⇒ prod (fail closed) until the user labels it. Refusal on the DTO.
            { var refusal = GuardAgent(AgentCapability.DeployLive, workspaceId, null, origin, isCommit: true, summary: summary, intentBasis: "create_data_agent:" + name); if (refusal != null) return Err(refusal); }
            try
            {
                var token = await EntraToken.AcquireFabricAsync(authMode, null, cancellationToken, tenantId);
                var parts = new (string, string)[]
                {
                    (DataAgentRest.DataAgentJsonPath, JsonSerializer.Serialize(new Dictionary<string, object> { ["$schema"] = DataAgentRest.DataAgentSchema })),
                    (DataAgentRest.DraftStageConfigPath, JsonSerializer.Serialize(new Dictionary<string, object> { ["$schema"] = DataAgentRest.StageConfigSchema, ["aiInstructions"] = aiInstructions })),
                };
                var id = await DataAgentRest.CreateItemAsync(workspaceId, name, DataAgentItemType, DataAgentRest.BuildDefinitionJson(parts), token, cancellationToken);
                await EmitDataAgentActivity("create_data_agent", true, $"Created data agent '{name}'", id ?? name, origin);
                return new DataAgentWriteReport { Status = "ok", AgentId = id, Message = $"Created data agent '{name}'" + (id != null ? $" ({id})." : "."), RequestSummary = summary };
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return await FailAsync("create_data_agent", name, origin, ex, summary); }
        }

        // READ-MODIFY-WRITE: fetch the current definition, replace ONLY the parts provided (null = keep), re-emit ALL
        // existing parts + the changed ones — never drop an unknown part.
        public async Task<DataAgentWriteReport> UpdateDataAgentAsync(string workspaceId, string agentId, string aiInstructions, string datasourceFolder, string datasourceJson, string fewshotsJson, bool commit, string authMode, string tenantId, string origin, CancellationToken cancellationToken = default)
        {
            RequireProDataAgentWrite("update_data_agent");
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId)) return Err("A workspaceId and agentId are required — list_workspaces finds the workspace, list_data_agents finds the agent id within it.");
            if (aiInstructions != null && aiInstructions.Length > AiInstructionsCap) return Err($"aiInstructions is {aiInstructions.Length} chars — over the {AiInstructionsCap} cap. Nothing was sent.");
            if ((datasourceJson != null || fewshotsJson != null) && string.IsNullOrWhiteSpace(datasourceFolder))
                return Err("Choose a data source before saving its schema or examples. Nothing was sent.");
            if (datasourceJson != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(datasourceJson);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return Err("The data source schema must be a JSON object. Nothing was sent.");
                }
                catch (JsonException)
                {
                    return Err("The data source schema is not valid JSON. Nothing was sent.");
                }
            }

            var touched = new List<string>();
            if (aiInstructions != null) touched.Add(DataAgentRest.DraftStageConfigPath);
            if (!string.IsNullOrEmpty(datasourceFolder) && datasourceJson != null) touched.Add(DataAgentRest.DraftPrefix + datasourceFolder + "/datasource.json");
            if (!string.IsNullOrEmpty(datasourceFolder) && fewshotsJson != null) touched.Add(DataAgentRest.DraftPrefix + datasourceFolder + "/fewshots.json");
            if (touched.Count == 0) return Err("Nothing changed. Edit the instructions, schema, or examples before saving.");
            var summary = $"update_data_agent {agentId} → replace parts: {(touched.Count > 0 ? string.Join(", ", touched) : "(none)")}; aiInstructions {(aiInstructions == null ? "unchanged" : aiInstructions.Length + " chars")}; datasource {(string.IsNullOrEmpty(datasourceFolder) ? "unchanged" : datasourceFolder)}";
            if (!commit) return DryRun(summary);
            // Agent-permissions gate (live cloud write; parts pinned so approving one edit never covers another).
            { var refusal = GuardAgent(AgentCapability.DeployLive, workspaceId, null, origin, isCommit: true, summary: summary, intentBasis: "update_data_agent:" + agentId + "|" + string.Join(",", touched)); if (refusal != null) return Err(refusal); }
            try
            {
                var token = await EntraToken.AcquireFabricAsync(authMode, null, cancellationToken, tenantId);
                var existing = await DataAgentRest.GetDefinitionAsync(workspaceId, agentId, token, cancellationToken);
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in existing) if (!string.IsNullOrEmpty(p.Path)) map[p.Path.Replace('\\', '/')] = p.Content;   // keep ALL existing parts
                if (aiInstructions != null) map[DataAgentRest.DraftStageConfigPath] = SetStageConfigInstructions(map.TryGetValue(DataAgentRest.DraftStageConfigPath, out var sc) ? sc : null, aiInstructions);
                if (!string.IsNullOrEmpty(datasourceFolder) && datasourceJson != null) map[DataAgentRest.DraftPrefix + datasourceFolder + "/datasource.json"] = datasourceJson;
                if (!string.IsNullOrEmpty(datasourceFolder) && fewshotsJson != null) map[DataAgentRest.DraftPrefix + datasourceFolder + "/fewshots.json"] = fewshotsJson;
                var def = DataAgentRest.BuildDefinitionJson(map.Select(kv => (kv.Key, kv.Value)));
                var outcome = await DataAgentRest.UpdateDefinitionAsync(workspaceId, agentId, def, token, cancellationToken);
                return await FinishWrite("update_data_agent", agentId, origin, outcome, summary, $"Updated data agent {agentId}");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return await FailAsync("update_data_agent", agentId, origin, ex, summary); }
        }

        public async Task<DataAgentWriteReport> PublishDataAgentAsync(string workspaceId, string agentId, string description, bool commit, string authMode, string tenantId, string origin, CancellationToken cancellationToken = default)
        {
            RequireProDataAgentWrite("publish_data_agent");
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId)) return Err("A workspaceId and agentId are required — list_workspaces finds the workspace, list_data_agents finds the agent id within it.");
            var summary = $"publish_data_agent {agentId} → copy {DataAgentRest.DraftPrefix}* to {DataAgentRest.PublishedPrefix}* + write {DataAgentRest.PublishInfoPath}; description {(description ?? string.Empty).Length} chars";
            if (!commit) return DryRun(summary);
            // Agent-permissions gate — publishing makes the draft the LIVE agent users query. A live cloud write.
            { var refusal = GuardAgent(AgentCapability.DeployLive, workspaceId, null, origin, isCommit: true, summary: summary, intentBasis: "publish_data_agent:" + agentId); if (refusal != null) return Err(refusal); }
            try
            {
                var token = await EntraToken.AcquireFabricAsync(authMode, null, cancellationToken, tenantId);
                var existing = await DataAgentRest.GetDefinitionAsync(workspaceId, agentId, token, cancellationToken);
                var def = DataAgentRest.BuildDefinitionJson(DataAgentRest.BuildPublishParts(existing, description));
                var outcome = await DataAgentRest.UpdateDefinitionAsync(workspaceId, agentId, def, token, cancellationToken);
                return await FinishWrite("publish_data_agent", agentId, origin, outcome, summary, $"Published data agent {agentId}");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return await FailAsync("publish_data_agent", agentId, origin, ex, summary); }
        }

        public async Task<DataAgentWriteReport> DeleteDataAgentAsync(string workspaceId, string agentId, bool commit, string authMode, string tenantId, string origin, CancellationToken cancellationToken = default)
        {
            RequireProDataAgentWrite("delete_data_agent");
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId)) return Err("A workspaceId and agentId are required — list_workspaces finds the workspace, list_data_agents finds the agent id within it.");
            var summary = $"delete_data_agent {agentId} → DELETE item in {workspaceId}";
            if (!commit) return DryRun(summary);
            // Agent-permissions gate — deleting a workspace item is IRREVERSIBLE (no restore point covers it), so it
            // escalates to the delete capability, exactly like a delete-bearing selective push.
            { var refusal = GuardAgent(AgentCapability.DeployDelete, workspaceId, null, origin, isCommit: true, summary: summary, intentBasis: "delete_data_agent:" + agentId); if (refusal != null) return Err(refusal); }
            try
            {
                var token = await EntraToken.AcquireFabricAsync(authMode, null, cancellationToken, tenantId);
                await DataAgentRest.DeleteItemAsync(workspaceId, agentId, token, cancellationToken);
                await EmitDataAgentActivity("delete_data_agent", true, $"Deleted data agent {agentId}", agentId, origin);
                return new DataAgentWriteReport { Status = "ok", AgentId = agentId, Message = $"Deleted data agent {agentId}.", RequestSummary = summary };
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return await FailAsync("delete_data_agent", agentId, origin, ex, summary); }
        }

        // ---- write helpers ----
        private static DataAgentWriteReport Err(string message) => new DataAgentWriteReport { Status = "error", Message = message };
        private static DataAgentWriteReport DryRun(string summary) => new DataAgentWriteReport { Status = "dry-run", Message = "DRY RUN — this changed NOTHING. Re-run with commit=true to apply.", RequestSummary = summary };

        private async Task<DataAgentWriteReport> FinishWrite(string kind, string agentId, string origin, FabricRest.DeployOutcome outcome, string summary, string okMsg)
        {
            var ok = outcome.Status == "Succeeded";
            await EmitDataAgentActivity(kind, ok, ok ? okMsg : $"{kind} {outcome.Status}", agentId, origin);
            return new DataAgentWriteReport
            {
                Status = ok ? "ok" : "error",
                AgentId = agentId,
                Message = ok ? okMsg + $" ({outcome.Status})." : $"{outcome.Status}" + (string.IsNullOrEmpty(outcome.Error) ? "." : ": " + outcome.Error),
                RequestSummary = summary,
            };
        }

        private async Task<DataAgentWriteReport> FailAsync(string kind, string agentId, string origin, Exception ex, string summary)
        {
            var msg = FabricRest.Scrub(ex.Message);
            await EmitDataAgentActivity(kind, false, $"{kind} failed", agentId, origin);
            return new DataAgentWriteReport { Status = "error", AgentId = agentId, Message = msg, RequestSummary = summary };
        }

        // Every EXECUTED write rides the Activity bus (deploy trail + experience-log ride-along).
        private Task EmitDataAgentActivity(string kind, bool ok, string label, string target, string origin)
            => PublishActivityAsync(new ActivityEvent { Kind = kind, Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin, Label = label, Target = target, Ok = ok });

        // Read-modify-write within the stage_config JSON: preserve every existing field, replace only aiInstructions.
        private static string SetStageConfigInstructions(string existingJson, string aiInstructions)
        {
            var dict = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(existingJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(existingJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var p in doc.RootElement.EnumerateObject())
                            dict[p.Name] = JsonSerializer.Deserialize<JsonElement>(p.Value.GetRawText());
                }
                catch { /* an unparseable existing stage_config is replaced with a fresh minimal one */ }
            }
            if (!dict.ContainsKey("$schema")) dict["$schema"] = DataAgentRest.StageConfigSchema;
            dict["aiInstructions"] = aiInstructions;
            return JsonSerializer.Serialize(dict);
        }

        // ---- read decode helpers ----
        private static DataAgentStage BuildStage(DataAgentRest.DataAgentParts parts, bool published)
        {
            var stage = new DataAgentStage
            {
                AiInstructions = ReadAiInstructions(published ? parts.PublishedStageConfig : parts.DraftStageConfig),
                DataSources = (published ? parts.PublishedDatasources : parts.DraftDatasources).Select(BuildDataSource).ToArray(),
            };
            if (published) stage.PublishDescription = ReadJsonString(parts.PublishInfo, "description");
            return stage;
        }

        internal static DataAgentDataSource BuildDataSource(DataAgentRest.DataAgentDatasourceFolder folder)
        {
            var ds = new DataAgentDataSource { Folder = folder.Folder, DatasourceJson = folder.DatasourceJson };
            if (!string.IsNullOrWhiteSpace(folder.DatasourceJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(folder.DatasourceJson);
                    var r = doc.RootElement;
                    ds.Type = Str(r, "type"); ds.DisplayName = Str(r, "displayName");
                    ds.ArtifactId = Str(r, "artifactId"); ds.WorkspaceId = Str(r, "workspaceId");
                    ds.UserDescription = Str(r, "userDescription"); ds.DataSourceInstructions = Str(r, "dataSourceInstructions");
                    if (r.TryGetProperty("elements", out var el)) ds.ElementsJson = el.GetRawText();
                }
                catch { /* a malformed datasource.json degrades to folder-name-only */ }
            }
            ds.FewShots = ReadFewShots(folder.FewshotsJson);
            return ds;
        }

        private static DataAgentFewShot[] ReadFewShots(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<DataAgentFewShot>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("fewShots", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    return arr.EnumerateArray().Select(f => new DataAgentFewShot { Id = Str(f, "id"), Question = Str(f, "question"), Query = Str(f, "query") }).ToArray();
            }
            catch { /* best-effort */ }
            return Array.Empty<DataAgentFewShot>();
        }

        private static string ReadAiInstructions(string stageConfigJson) => ReadJsonString(stageConfigJson, "aiInstructions");

        private static string ReadJsonString(string json, string prop)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { using var doc = JsonDocument.Parse(json); return Str(doc.RootElement, prop); }
            catch { return null; }
        }

        private static string Str(JsonElement e, string prop)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
