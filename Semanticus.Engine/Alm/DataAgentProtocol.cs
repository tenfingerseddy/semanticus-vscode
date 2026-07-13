using System;

namespace Semanticus.Engine
{
    // ============================================================================================
    // Wire DTOs for the Fabric Data Agent lane (definition-based Fabric item — see
    // docs/data-agent-tab-plan.md §1). Reads decode the item definition into typed draft/published
    // stages; writes are dry-run by default (commit=false reports the exact request, sends nothing).
    // Open enums (item Type) stay strings — MS adds values over time, and the data-agent type string
    // itself is a [verify-at-build] (docs lag the feature; see DataAgentList.ObservedItemTypes).
    // Nothing here holds a token/secret (golden rule #1).
    // ============================================================================================

    /// <summary>A generic Fabric workspace item (the /items list shape). Data agents are filtered from these
    /// by <c>DataAgentRest.IsDataAgentType</c> (type contains "dataagent", case-insensitive).</summary>
    public sealed class FabricItem
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }          // SemanticModel | Report | DataAgent | … (open enum)
        public string WorkspaceId { get; set; }
    }

    /// <summary>A data agent as listed in a workspace. <see cref="Published"/> is null unless a definition was
    /// fetched (list is items-only; get_data_agent decodes the definition and sets it from publish_info presence).</summary>
    public sealed class DataAgentInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }           // the observed item type string
        public bool? Published { get; set; }        // null = unknown (not fetched); true = publish_info present
    }

    /// <summary>Result of list_data_agents. <see cref="ObservedItemTypes"/> exists so the [verify-at-build] probe
    /// can read the REAL data-agent type string from the tool output when the filter matches nothing (docs lag).</summary>
    public sealed class DataAgentList
    {
        public DataAgentInfo[] Agents { get; set; } = Array.Empty<DataAgentInfo>();
        public string[] ObservedItemTypes { get; set; } = Array.Empty<string>();   // distinct item Type strings in the workspace
        public string Note { get; set; }
        public string Error { get; set; }
    }

    public sealed class DataAgentFewShot
    {
        public string Id { get; set; }
        public string Question { get; set; }
        public string Query { get; set; }
    }

    /// <summary>One data source scoped into a data agent. The raw datasource document is retained so schema edits
    /// can preserve fields the engine does not model; <see cref="ElementsJson"/> is the convenient tree projection.</summary>
    public sealed class DataAgentDataSource
    {
        public string Folder { get; set; }         // the draft/published "{type}-{name}" folder name
        public string Type { get; set; }           // semantic_model | lakehouse | warehouse | kusto | …
        public string DisplayName { get; set; }
        public string ArtifactId { get; set; }
        public string WorkspaceId { get; set; }
        public string UserDescription { get; set; }
        public string DataSourceInstructions { get; set; }
        public string DatasourceJson { get; set; }  // complete datasource.json, retained for lossless edits
        public string ElementsJson { get; set; }    // raw elements tree JSON (null if absent)
        public DataAgentFewShot[] FewShots { get; set; } = Array.Empty<DataAgentFewShot>();
    }

    /// <summary>One stage (draft or published) of a data agent's configuration.</summary>
    public sealed class DataAgentStage
    {
        public string AiInstructions { get; set; }
        public DataAgentDataSource[] DataSources { get; set; } = Array.Empty<DataAgentDataSource>();
        public string PublishDescription { get; set; }   // published stage only (from publish_info.json)
    }

    /// <summary>get_data_agent result: the item info + the decoded draft (and published, if present) stages.</summary>
    public sealed class DataAgentDetail
    {
        public DataAgentInfo Info { get; set; }
        public DataAgentStage Draft { get; set; }
        public DataAgentStage Published { get; set; }    // null when the agent has never been published
        public string Note { get; set; }
        public string Error { get; set; }
    }

    /// <summary>generate_data_agent_config_from_model result — the assembled semantic_model datasource config for
    /// review. Writes NOTHING (feed it to update_data_agent). Ids fall back to placeholders when they can't be
    /// resolved offline (false-safe — never a guessed GUID); the Note discloses that.</summary>
    public sealed class DataAgentConfig
    {
        public string DatasourceJson { get; set; }
        public string AiInstructions { get; set; }
        public string DisplayName { get; set; }
        public string Note { get; set; }
    }

    /// <summary>Result of a data-agent write (create/update/publish/delete). Dry-run by default: Status "dry-run"
    /// means nothing was sent; "ok" = committed; "error" = a (scrubbed) failure. <see cref="RequestSummary"/> is
    /// the exact request shape (op + parts touched + instruction length + datasource count) — NEVER the full payload.</summary>
    public sealed class DataAgentWriteReport
    {
        public string Status { get; set; }          // "ok" | "dry-run" | "error"
        public string AgentId { get; set; }
        public string Message { get; set; }
        public string RequestSummary { get; set; }
    }
}
