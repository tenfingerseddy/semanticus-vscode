using System.Collections.Generic;

namespace Semanticus.Engine
{
    // Wire DTOs shared by the RPC (and later MCP) doors. Serialized as camelCase JSON.

    public sealed class OpenResult
    {
        public string SessionId { get; set; }
        public long Revision { get; set; }
        public string ModelName { get; set; }
        public int Tables { get; set; }
        public int Measures { get; set; }
        public string Source { get; set; }
        public bool LiveConnected { get; set; }   // a unified live open (open_local / open_live) also bound the query engine
    }

    /// <summary>The non-secret coordinates of the live model a session was opened from (open_live/open_local bind it).
    /// Lets "Save to live model" (deploy_live) and refresh_partition push back "to source" with the SAME identity —
    /// no re-auth, like Tabular Editor. Holds NO token or secret (golden rule #1) — only the WHERE (endpoint +
    /// dataset), the tenant, and the auth MODE name (e.g. "interactive"/"serviceprincipal" — not a credential).
    /// The actual token is reused from the session's in-memory live-auth cache (Session.GetOrBuildLiveCredential),
    /// which keeps one credential per identity so interactive auth renews silently — no second browser prompt.</summary>
    public sealed class LiveOrigin
    {
        public string Endpoint { get; }
        public string Database { get; }   // the actual dataset resolved at open time (not the connection-string form)
        public string TenantId { get; }
        public string AuthMode { get; }   // the open's auth mode name (NOT a secret) — reused by deploy to avoid re-prompting
        public LiveOrigin(string endpoint, string database, string tenantId, string authMode = null)
        { Endpoint = endpoint; Database = database; TenantId = tenantId; AuthMode = authMode; }
    }

    public sealed class TreeNode
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }      // "table" | "measure" | "column" | ...
        public bool HasChildren { get; set; }
        // The object's DisplayFolder path ("Parent\Child"), null for kinds without one. Additive: the fan stays
        // FLAT (agents' list_objects contract unchanged) — the VS Code tree groups folder nodes client-side.
        public string DisplayFolder { get; set; }
    }

    public sealed class ObjectInfo
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }

    public sealed class ChangeDelta
    {
        public string Kind { get; set; }      // "update" | "add" | "remove" | "structure"
        public string Ref { get; set; }
        public string[] Props { get; set; }
    }

    public sealed class UndoState
    {
        public bool CanUndo { get; set; }
        public bool CanRedo { get; set; }
        public bool AtCheckpoint { get; set; }
    }

    public sealed class ChangeNotification
    {
        public string SessionId { get; set; }
        public long Revision { get; set; }
        public string Origin { get; set; }    // "human" | "agent" | "system"
        public string Label { get; set; }
        public ChangeDelta[] Deltas { get; set; }
        public UndoState Undo { get; set; }
        /// <summary>The health movement THIS commit caused (grade letter, net-new findings on touched objects,
        /// blast-radius count). Pro-only and threshold-suppressed: null on the free tier and below threshold —
        /// additive, so pre-feature clients simply never see it (see <see cref="HealthDelta"/>).</summary>
        public HealthDelta Health { get; set; }
    }

    /// <summary>The deterministic health delta ONE committed mutation caused — "spell-check for your model"
    /// (docs/product-innovation-brainstorm.md §4; harness-engineering §3 ground-truth feedback made ambient).
    /// Computed once per commit at the single construction point (<c>HealthDeltaProbe</c>, where the SOFT Pro
    /// gate lives) and delivered to BOTH doors: the human chip rides <see cref="ChangeNotification.Health"/>;
    /// the agent block is appended to the mutating MCP tool result. OMIT-WHEN-QUIET (P-Efficiency): the whole
    /// object is null unless the grade LETTER moved, a net-new Warning+ finding landed on a touched object, or
    /// the blast radius is &gt; 0 — and each field is null when it carries nothing, so the wire form stays terse.</summary>
    public sealed class HealthDelta
    {
        public string Grade { get; set; }     // "B->C" — null when the letter didn't move (improvements report too)
        public string[] New { get; set; }     // net-new ACTIVE finding rule-ids on TOUCHED objects (actionable; distinct, capped — Findings has the true count); null when none
        public int? Findings { get; set; }    // total net-new active findings on touched objects (null when 0)
        public int? Impact { get; set; }      // blast radius: distinct downstream objects of this commit's SEMANTIC changes (null when 0)
        public bool? Warn { get; set; }       // true when a net-new finding is Warning+ severity (null otherwise) — lets the chip tint honestly (Info-only + impact stays calm)
    }

    /// <summary>The result of a <c>dry_run</c> rehearsal (docs/harness-engineering.md §4): what an op WOULD do,
    /// with a hard guarantee it did nothing — no mutation, no undo entry, no broadcast, no audit record. A
    /// rehearsal that finds the op would FAIL is still a SUCCESSFUL dry-run (<see cref="WouldSucceed"/> false,
    /// <see cref="Error"/> carries the op's own teaching text).</summary>
    public sealed class DryRunReport
    {
        public string Op { get; set; }
        public bool WouldSucceed { get; set; }
        public string Error { get; set; }                 // the op's real failure text (already teaching) when WouldSucceed=false
        public ChangeDelta[] Deltas { get; set; }         // the would-be change set, exactly what model/didChange would have carried
        public string[] Mutations { get; set; }           // the mutation labels rehearsed (empty = the op didn't mutate)
        public string Result { get; set; }                // the op's own return value, JSON-serialized — what the caller WOULD have received
        public string Note { get; set; }
    }

    /// <summary>A live "the agent just ran something" event for the NON-mutating execute/read ops (run_dax,
    /// evaluate_and_log, profile_dax, benchmark_dax, preview_table, pivot_measure, vpaq_scan, verify_equivalence).
    /// Broadcast as <c>model/activity</c> so the human's Studio reflects what Claude is doing live — the read
    /// counterpart of <see cref="ChangeNotification"/> (which only covers mutations). Carries a TOKEN-LIGHT,
    /// TRUNCATED result preview (the agent already has the full result over MCP); the webview renders it in the
    /// matching tab and the live activity feed, attributed to the origin. Seq is stamped server-side for ordering.</summary>
    public sealed class ActivityEvent
    {
        public long Seq { get; set; }          // monotonic, stamped by the host on publish (ordering/dedupe)
        public string Kind { get; set; }       // "run_dax" | "evaluate_and_log" | "profile_dax" | "benchmark_dax" | "preview_table" | "pivot_measure" | "vpaq_scan" | "verify_equivalence" | "health_delta" (post-commit evidence record, feature #4)
        public string SessionId { get; set; }  // the session this activity belongs to, FROZEN at emit (PublishActivityAsync). The experience tee attributes on THIS, not on whatever session is current when the (possibly-forwarded) event is finally handled — so a model swap mid-op can't record the result under the new model.
        public string Origin { get; set; }     // "agent" | "human"
        public string Label { get; set; }      // short human-readable summary, e.g. "Profiled query" / "Previewed 'Sales'"
        public string Query { get; set; }      // the DAX/query text (truncated) — null where not applicable
        public string Target { get; set; }     // e.g. the table name for preview_table
        public bool Ok { get; set; }           // did the op succeed
        public string Error { get; set; }      // scrubbed error if it failed
        public string ApprovalId { get; set; } // exact pending approval when policy refused the operation
        public int? RowCount { get; set; }     // result rows (true count, even if Result is truncated)
        public long? ElapsedMs { get; set; }   // wall-clock if known
        public object Result { get; set; }     // op-specific TRUNCATED payload for live tab rendering (ResultSet/ServerTimings/…)
    }

    /// <summary>The Result payload for a pivot_measure ActivityEvent: the raw SUMMARIZECOLUMNS rows PLUS the
    /// shape metadata (how many leading columns are row fields, whether a column field follows) the Pivot tab
    /// needs to rebuild the row × column matrix — which a bare ResultSet can't convey.</summary>
    public sealed class PivotActivityResult
    {
        public ResultSet Result { get; set; }
        public int RowFields { get; set; }
        public bool HasColField { get; set; }
    }

    /// <summary>One object that can carry Documentation narrative + which sections currently have content.</summary>
    public sealed class DocOutlineItem
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }     // "model" | "table" | "measure" | …
        public string[] Sections { get; set; } = System.Array.Empty<string>();
    }

    /// <summary>The Documentation-narrative outline: every annotatable object + its authored sections.</summary>
    public sealed class DocOutline
    {
        public DocOutlineItem[] Items { get; set; } = System.Array.Empty<DocOutlineItem>();
    }

    // ---- Documentation model (getDocModel → the read-only snapshot the doc renderer consumes) -----------

    /// <summary>One authored narrative section (Markdown) as it appears in the exported documentation.</summary>
    public sealed class DocSection
    {
        public string Key { get; set; }        // "overview" | "businessContext" | "notes" | "glossary" | "methodology" | …
        public string Markdown { get; set; }
    }

    /// <summary>The Documentation narrative for one object (who last authored it + the authored sections).</summary>
    public sealed class DocNarrative
    {
        public string Author { get; set; }     // "agent" | "human" — who last edited
        public string UpdatedUtc { get; set; }
        public DocSection[] Sections { get; set; } = System.Array.Empty<DocSection>();
    }

    public sealed class DocLevel
    {
        public string Name { get; set; }
        public int Ordinal { get; set; }
        public string Column { get; set; }      // the column this level maps to (null if unresolved)
    }

    public sealed class DocHierarchy
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsHidden { get; set; }
        public DocLevel[] Levels { get; set; } = System.Array.Empty<DocLevel>();
    }

    public sealed class DocPartition
    {
        public string Name { get; set; }
        public string Mode { get; set; }         // Import | DirectQuery | Dual | DirectLake
        public string SourceType { get; set; }   // M | Query | Calculated | Entity | …
        public string Source { get; set; }       // the M / SQL / DAX text, or "Entity: schema.table" for a Direct Lake source (never null when a source exists)
        public string EntityName { get; set; }   // Direct Lake (Entity source): the lakehouse/warehouse table name (null otherwise)
        public string SchemaName { get; set; }   // Direct Lake (Entity source): the schema (null otherwise)
        public string DataSource { get; set; }   // referenced data source name (Query/Entity sources)
        public string RefreshedTime { get; set; } // last-processed ISO timestamp (null if never)
    }

    /// <summary>A table partition's identity (without its full source text — fetch M via get_partition_m).</summary>
    public sealed class PartitionInfo
    {
        public string Ref { get; set; }          // partition:&lt;Table&gt;/&lt;Name&gt;
        public string Table { get; set; }
        public string Name { get; set; }
        public string Mode { get; set; }         // Import | DirectQuery | Dual | DirectLake
        public string SourceType { get; set; }   // M | Query | Calculated | Entity | …
        public string DataSource { get; set; }   // referenced data source name (Query/Entity sources)
    }

    /// <summary>One column as the SOURCE query would return it (name + data type), read schema-only (no data).
    /// The type is mapped to the TOM vocabulary (String|Int64|Decimal|Double|DateTime|Boolean); SqlType keeps the
    /// raw source type for display.</summary>
    public sealed class SourceColumn
    {
        public string Name { get; set; }
        public string DataType { get; set; }   // TOM data type (String|Int64|Decimal|Double|DateTime|Boolean)
        public string SqlType { get; set; }     // raw source type (e.g. nvarchar, int) — informational
    }

    /// <summary>The SOURCE-side schema of a table, probed schema-only from its partition's SQL source (no data
    /// imported). <see cref="Reachable"/>=false + <see cref="Error"/> when the source can't be reached — an offline
    /// snapshot, a non-SQL / native-query source, or an auth/connect failure — so the caller shows the message
    /// rather than crashing (Update Schema degrades gracefully to the synthesized/manual diff path).</summary>
    public sealed class SourceSchema
    {
        public string Table { get; set; }
        public string TableRef { get; set; }     // table:&lt;Name&gt;
        public bool Reachable { get; set; }
        public string Method { get; set; }        // "fabric-sql" when read over TDS; null when unreachable
        public string Server { get; set; }
        public string Database { get; set; }
        public string SchemaName { get; set; }
        public string Entity { get; set; }        // the physical source table probed
        public SourceColumn[] Columns { get; set; } = System.Array.Empty<SourceColumn>();
        public string Error { get; set; }         // null when Reachable
    }

    /// <summary>One change the source has drifted from the model: a column Added at the source, Removed from it, or
    /// whose source data type no longer matches (TypeChanged). Carries a stable <see cref="Id"/> so the UI can offer
    /// a per-item checkbox and apply only the accepted subset.</summary>
    public sealed class SchemaDiffItem
    {
        public string Id { get; set; }            // stable ("add:Col" | "remove:Col" | "retype:Col")
        public string Change { get; set; }        // Added | Removed | TypeChanged
        public string Column { get; set; }
        public string ColumnRef { get; set; }     // model column ref (Removed / TypeChanged); null for Added
        public string ModelType { get; set; }     // model's current TOM type (Removed / TypeChanged)
        public string SourceType { get; set; }    // source TOM type (Added / TypeChanged)
        public string SqlType { get; set; }        // raw source type where known
    }

    /// <summary>A table's schema diff (source vs model) for the Update-Schema review. <see cref="Reachable"/>=false
    /// + <see cref="Error"/> when the source read failed (empty Items). <see cref="Source"/> is "fabric-sql" when
    /// probed live or "supplied" when the caller passed synthesized source columns (the test / manual path).</summary>
    public sealed class SchemaDiff
    {
        public string Table { get; set; }
        public string TableRef { get; set; }
        public bool Reachable { get; set; }
        public string Source { get; set; }        // "fabric-sql" | "supplied" | null
        public string Error { get; set; }
        public SchemaDiffItem[] Items { get; set; } = System.Array.Empty<SchemaDiffItem>();
        public int Added { get; set; }
        public int Removed { get; set; }
        public int TypeChanged { get; set; }
    }

    /// <summary>One accepted change to apply in apply_schema_update. <see cref="Change"/> = Added | Removed |
    /// TypeChanged; <see cref="DataType"/> is required for Added/TypeChanged (the TOM type to set); SourceColumn is
    /// the underlying physical name for an Added column (defaults to <see cref="Column"/>).</summary>
    public sealed class SchemaUpdateItem
    {
        public string Change { get; set; }
        public string Column { get; set; }
        public string DataType { get; set; }
        public string SourceColumn { get; set; }
    }

    /// <summary>The result of applying a chosen SUBSET of a schema diff as ONE undoable change. Items that can't be
    /// applied (a calculated column, a name clash, an unknown column) are skipped with a reason rather than failing
    /// the whole batch — the applied changes still commit atomically.</summary>
    public sealed class ApplySchemaResult
    {
        public long Revision { get; set; }
        public bool Changed { get; set; }
        public int Added { get; set; }
        public int Removed { get; set; }
        public int Retyped { get; set; }
        public string[] Applied { get; set; } = System.Array.Empty<string>();
        public string[] Skipped { get; set; } = System.Array.Empty<string>();   // "Column: reason"
    }

    /// <summary>One field in a field parameter: a measure/column ref + an optional display label (defaults to the
    /// object's own name). Order is the position in the array passed to create_field_parameter.</summary>
    public sealed class FieldParameterItem
    {
        public string ObjectRef { get; set; }   // measure:&lt;Table&gt;/&lt;Measure&gt; or column:&lt;Table&gt;/&lt;Column&gt;
        public string Label { get; set; }        // optional display label (null/empty ⇒ the object's name)
    }

    /// <summary>A model perspective + its current membership. <see cref="Members"/> are the object refs
    /// (table/column/measure/hierarchy) shown in it — i.e. the checked cells of the objects×perspectives
    /// matrix. A table ref appears when it (or any of its children) is included.</summary>
    public sealed class PerspectiveInfo
    {
        public string Ref { get; set; }          // perspective:&lt;Name&gt;
        public string Name { get; set; }
        public string Description { get; set; }
        public string[] Members { get; set; }    // included object refs (empty array = an empty perspective)
    }

    /// <summary>A calculation group + its items, for the Advanced-Modelling calc-group editor (read).</summary>
    public sealed class CalcGroupInfo
    {
        public string Ref { get; set; }          // table:&lt;Name&gt;
        public string Name { get; set; }
        public int Precedence { get; set; }      // higher applies first when groups combine
        public CalcItemInfo[] Items { get; set; }
    }

    /// <summary>One calculation item (ordered) within a <see cref="CalcGroupInfo"/>.</summary>
    public sealed class CalcItemInfo
    {
        public string Ref { get; set; }          // calcitem:&lt;Group&gt;/&lt;Name&gt;
        public string Name { get; set; }
        public int Ordinal { get; set; }
        public string Expression { get; set; }
        public string FormatStringExpression { get; set; }   // null/empty ⇒ inherits the base measure's format
    }

    /// <summary>One curated NUMBER-format-string template (list_format_templates). The catalog is engine-side data
    /// (a single source of truth) served identically to the webview picker and the agent, so both author correct,
    /// idiomatic format strings instead of guessing glyphs/scaling-commas. Colour is report-side, out of scope.</summary>
    public sealed class FormatTemplateInfo
    {
        public string Id { get; set; }            // stable id, e.g. "chg-triangle-pct"
        public string Category { get; set; }      // display category, e.g. "Change with Unicode indicators"
        public string Name { get; set; }
        public string Description { get; set; }
        public string Kind { get; set; }          // "static" (a literal format string) | "dynamic" (a DAX expression that returns one)
        public string FormatString { get; set; }  // the literal (static) OR the DAX expression to place in a dynamic format slot (dynamic)
        public string SampleInput { get; set; }   // a representative input for the live preview
        public string ExampleOutput { get; set; } // the documented rendered output (canned for dynamic; evaluate live via run_dax when connected)
        public bool Common { get; set; }          // pinned into the tighter "Common" default view (~20)
        public string Note { get; set; }          // one-line caveat/help (nullable)
        public string Credit { get; set; }         // attribution when a pattern is borrowed (e.g. Kurt Buhler / DaxLib) — nullable
        public string Pack { get; set; }           // optional UDF power-pack id (daxlib_install) for the dynamic power-pack rows — nullable
    }

    // ---- DaxLib UDF package manager (Studio v2 Advanced Modelling, 6th area) — anonymous READ-ONLY browse + install ----

    /// <summary>A package summary from the DaxLib feed (daxlib_search). <see cref="Version"/> is the latest known.</summary>
    public sealed class DaxLibPackage
    {
        public string Id { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string[] Authors { get; set; }
        public string[] Tags { get; set; }
        public long Downloads { get; set; }
        public string ProjectUrl { get; set; }
    }

    /// <summary>A package dependency edge ({id, version}). Version may be null (= "latest"); parsed leniently — the
    /// dependencies[] field is real-world but not in the published manifest 1.0.0 schema.</summary>
    public sealed class DaxLibDependency
    {
        public string Id { get; set; }
        public string Version { get; set; }
    }

    /// <summary>Full detail for one package@version (daxlib_package_info): metadata + parsed dependencies + a preview
    /// of the UDF names it would install (so the human/agent can see the blast radius before installing).</summary>
    public sealed class DaxLibPackageDetail
    {
        public string Id { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string[] Authors { get; set; }
        public string[] Tags { get; set; }
        public long Downloads { get; set; }
        public string ReleaseNotes { get; set; }
        public string ProjectUrl { get; set; }
        public string RepositoryUrl { get; set; }
        public string Published { get; set; }
        public DaxLibDependency[] Dependencies { get; set; }
        public string[] FunctionNames { get; set; }   // the UDFs this package defines (dotted names)
        public int FunctionCount { get; set; }
    }

    /// <summary>Result of daxlib_install — the bulk primitive (Pro). One atomic, undoable transaction installs the
    /// package's UDFs (and any dependencies, deps-first); <see cref="Skipped"/> are functions that already existed
    /// (when replaceExisting=false). <see cref="Warning"/> surfaces non-fatal notes (version conflict / cycle break).</summary>
    public sealed class DaxLibInstallResult
    {
        public long Revision { get; set; }
        public string[] Functions { get; set; }                 // function refs created/replaced
        public string[] Skipped { get; set; }                   // function names that already existed (left as-is)
        public string[] DependenciesInstalled { get; set; }     // "id version" of each dependency pulled in
        public string Warning { get; set; }
    }

    /// <summary>Provenance for an installed DaxLib package (persisted in the Semanticus_DaxLibInstalled annotation;
    /// also the daxlib_list_installed DTO). <see cref="Functions"/> are the UDF names this package owns (for uninstall).</summary>
    public sealed class DaxLibInstalledRecord
    {
        public string PackageId { get; set; }
        public string Version { get; set; }
        public string[] Functions { get; set; }
        public string Authors { get; set; }     // comma-joined (attribution; the packages are MIT)
        public string By { get; set; }           // origin: "human" | "agent"
        public string When { get; set; }         // ISO-8601 UTC
    }

    /// <summary>One calculation item in a calculation group.</summary>
    public sealed class DocCalcItem
    {
        public string Name { get; set; }
        public int Ordinal { get; set; }
        public string Expression { get; set; }
        public string FormatStringExpression { get; set; }
        public string Description { get; set; }
    }

    /// <summary>A KPI defined on a measure (target / status / trend).</summary>
    public sealed class DocKpi
    {
        public string MeasureRef { get; set; }
        public string Measure { get; set; }
        public string TargetExpression { get; set; }
        public string StatusExpression { get; set; }
        public string StatusGraphic { get; set; }
        public string TrendExpression { get; set; }
        public string TrendGraphic { get; set; }
    }

    /// <summary>A data source (name + type only — connection strings / credentials are never read).</summary>
    public sealed class DocDataSource
    {
        public string Name { get; set; }
        public string Type { get; set; }         // Provider | Structured
    }

    /// <summary>A shared (model-level) M / DAX expression.</summary>
    public sealed class DocExpression
    {
        public string Name { get; set; }
        public string Kind { get; set; }         // M | DAX
        public string Expression { get; set; }
        public string Description { get; set; }
    }

    /// <summary>Per-table detail for the documentation (the global Measures/Columns lists group onto these by Table).</summary>
    public sealed class DocTable
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsHidden { get; set; }
        public bool IsCalculated { get; set; }
        public bool IsDateTable { get; set; }
        public bool IsCalculationGroup { get; set; }
        public int CalcGroupPrecedence { get; set; }       // calc-group tables only
        public string DataCategory { get; set; }
        public long? RowCount { get; set; }                // from storage stats when a live connection supplied them
        public DocHierarchy[] Hierarchies { get; set; } = System.Array.Empty<DocHierarchy>();
        public DocPartition[] Partitions { get; set; } = System.Array.Empty<DocPartition>();
        public DocCalcItem[] CalcItems { get; set; } = System.Array.Empty<DocCalcItem>();
        public DocNarrative Narrative { get; set; }        // null when the table has no authored narrative
    }

    /// <summary>Header / summary facts about the model for the documentation cover + overview.</summary>
    public sealed class DocModelHeader
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Culture { get; set; }
        public string DefaultMode { get; set; }
        public long CompatibilityLevel { get; set; }
        public string Source { get; set; }
        public int TableCount { get; set; }
        public int MeasureCount { get; set; }
        public int ColumnCount { get; set; }
        public int RelationshipCount { get; set; }
        public bool LiveConnected { get; set; }
        public string LiveKind { get; set; }
        public string GeneratedUtc { get; set; }           // when this snapshot was assembled (ISO-8601)
    }

    /// <summary>The complete read-only documentation snapshot consumed by the (webview) doc renderer. Composes the
    /// existing model / graph / measure / column / role / readiness / BPA / storage readers + the Documentation
    /// narrative + structural detail (calc groups, hierarchies, KPIs, partitions, sources, shared expressions,
    /// Prep-for-AI). Analyzer add-ons are best-effort: any may be null if its analyzer was unavailable or failed.</summary>
    public sealed class DocModelDto
    {
        public DocModelHeader Header { get; set; }
        public ModelGraph Graph { get; set; }
        public DocTable[] Tables { get; set; } = System.Array.Empty<DocTable>();
        public MeasureRow[] Measures { get; set; } = System.Array.Empty<MeasureRow>();
        public ColumnRow[] Columns { get; set; } = System.Array.Empty<ColumnRow>();
        public RoleInfo[] Roles { get; set; } = System.Array.Empty<RoleInfo>();
        public DocKpi[] Kpis { get; set; } = System.Array.Empty<DocKpi>();
        public DocDataSource[] DataSources { get; set; } = System.Array.Empty<DocDataSource>();
        public DocExpression[] Expressions { get; set; } = System.Array.Empty<DocExpression>();
        public DocNarrative ModelNarrative { get; set; }   // model-wide narrative (overview / glossary / methodology)

        // Best-effort analyzer add-ons — any may be null if the analyzer was unavailable or threw:
        public Semanticus.Analysis.Scorecard Readiness { get; set; }
        public Semanticus.Analysis.BpaScorecard Bpa { get; set; }
        public Semanticus.Analysis.PrepForAiConfig PrepForAi { get; set; }
        public VpaqReport Storage { get; set; }
        public bool StorageAvailable { get; set; }         // a live connection supplied VertiPaq storage stats
    }

    public sealed class SetResult
    {
        public long Revision { get; set; }
        public bool Changed { get; set; }
        public string Warning { get; set; }   // non-fatal advisory (e.g. corrupt existing data was preserved aside) — null on the clean path
    }

    /// <summary>rename_display_folder's receipt: how many members had their DisplayFolder prefix rewritten
    /// (a folder IS its members — the count is the proof the rename touched something) in the ONE undo step.</summary>
    public sealed class FolderRenameResult
    {
        public long Revision { get; set; }
        public int Members { get; set; }
        public string From { get; set; }
        public string To { get; set; }        // "" = the folder level was removed (members promoted)
    }

    /// <summary>A table's incremental-refresh policy (TOM BasicRefreshPolicy), read-only projection.
    /// Enabled=false means no policy object exists; the other fields are then their "not set" defaults
    /// (granularities "Invalid", periods 0).</summary>
    public sealed class RefreshPolicyInfo
    {
        public string Table { get; set; }
        public bool Enabled { get; set; }                       // RefreshPolicy != null (Table.EnableRefreshPolicy)
        public string PolicyType { get; set; }                  // "Basic" (the only value) — null when disabled
        public string Mode { get; set; }                        // "Import" | "Hybrid" — null when disabled
        public string RollingWindowGranularity { get; set; }    // Day | Month | Quarter | Year | Invalid
        public int RollingWindowPeriods { get; set; }           // periods of history STORED
        public string IncrementalGranularity { get; set; }      // Day | Month | Quarter | Year | Invalid
        public int IncrementalPeriods { get; set; }             // recent periods RE-IMPORTED each refresh
        public int IncrementalPeriodsOffset { get; set; }       // lag/lead from now to the window head
        public string SourceExpression { get; set; }            // partition M template referencing RangeStart/RangeEnd
        public string PollingExpression { get; set; }           // optional "detect data changes" bookmark M
    }

    /// <summary>Result of applying an edited DAX script (apply_dax_script): the refs whose expression was applied,
    /// and the refs that were skipped (not found, or not a DAX-expression object). Lets the UI report "N applied, M skipped".</summary>
    public sealed class ApplyScriptResult
    {
        public long Revision { get; set; }
        public string[] Applied { get; set; }
        public string[] Skipped { get; set; }
    }

    /// <summary>One editable property of a model object, reflected from the TOM wrapper's ComponentModel metadata —
    /// the unit the property grid renders. Kind drives the editor (text / checkbox / number / dropdown).</summary>
    public sealed class ObjectProperty
    {
        public string Name { get; set; }          // the reflection property name (used by set_property)
        public string DisplayName { get; set; }   // [DisplayName] or Name
        public string Category { get; set; }      // [Category] grouping, e.g. "Basic", "Options"
        public string Description { get; set; }    // [Description] tooltip
        public string Kind { get; set; }          // string | bool | number | enum | formatExpression (a measure's dynamic format slot)
        public string Value { get; set; }         // current value as a string ("" when null)
        public string[] Options { get; set; } = System.Array.Empty<string>();   // enum choices (Kind == enum)
        public bool ReadOnly { get; set; }
        /// <summary>PREFILL side-channel: values already in use nearby (e.g. the model's display folders), so the
        /// grid offers them without minting a new op — the same payload both doors read (agents keep list-op parity).
        /// Null (not an empty array) when a property has no prefills, so payloads stay lean.</summary>
        public string[] Suggestions { get; set; }
        /// <summary>Analyst-plain status note (why a row is locked / what it needs, e.g. the compatibility-level
        /// floor for dynamic format strings). Null when there is nothing to say.</summary>
        public string Hint { get; set; }
    }

    /// <summary>An object that references the target (a DAX dependent) — so a caller can check before delete/rename.</summary>
    public sealed class DependentInfo
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
    }

    public sealed class SetInstructionsResult
    {
        public long Revision { get; set; }
        public bool Changed { get; set; }
        public int Length { get; set; }
        public string Culture { get; set; }   // WHICH linguistic culture the instructions were written to (surfaced when the model has several)
        /// <summary>Carries the LSDL service-refresh caveat: the change is live in the model, but a deployed model needs a refresh to sync.</summary>
        public string Note { get; set; }
    }

    /// <summary>The Prep-for-AI AI instructions read back (LSDL CustomInstructions) — the reader half of set_ai_instructions.</summary>
    public sealed class AiInstructionsInfo
    {
        public bool Present { get; set; }
        public string Instructions { get; set; }   // null when not set
        public int Length { get; set; }
        public int Limit { get; set; }             // the Prep-for-AI cap (content beyond it is ignored by the service)
        public string Culture { get; set; }        // the linguistic culture read (null if the model has no linguistic schema)
        public string Note { get; set; }
    }

    public sealed class SaveResult
    {
        public long Revision { get; set; }
        public string Path { get; set; }
        public string Format { get; set; }
        public int FileCount { get; set; }
    }

    /// <summary>Result of a metadata-only deploy of the open session to a live XMLA model (deploy_live).</summary>
    public sealed class DeployReport
    {
        public bool Committed { get; set; }          // false = dry-run (nothing written)
        public string Endpoint { get; set; }
        public string Database { get; set; }
        public int Descriptions { get; set; }
        public int Renames { get; set; }
        public int Visibility { get; set; }
        public int DataCategories { get; set; }
        public int Formats { get; set; }
        public int Folders { get; set; }
        public int Expressions { get; set; }
        public int SummarizeBy { get; set; }
        public int Cultures { get; set; }            // linguistic schema (synonyms + AI instructions)
        public int Partitions { get; set; }          // partition source expressions (M / calc-table DAX / legacy query)
        public int NamedExpressions { get; set; }    // shared M expressions / parameters
        public int CalcGroup { get; set; }           // calc-group Precedence + calc-item Ordinal changes — the calc-only props with no generic bucket (calc-item description/expression/format count under the generic buckets)
        public int Added { get; set; }               // NEW objects created on the live model (measures / calculated columns / calculated tables / named expressions / calculation items)
        public int Deleted { get; set; }             // objects REMOVED from the live model — ONLY the refs a caller explicitly named (selective push); never derived from absence
        public int TotalChanges { get; set; }
        public string[] CalcTablesAdded { get; set; } = System.Array.Empty<string>();  // names of NEW calculated tables created (recalced via a Calculate pass on commit)
        public string[] SyncedRefs { get; set; } = System.Array.Empty<string>();       // object-level, SOURCE-keyed refs the live deploy ACTUALLY synced (adds + updates) — lets a caller reconcile a local merge against what truly reached the model (a merged object of a type this deploy doesn't carry, e.g. a relationship/role, never appears here). Deletes are tracked in DeletedRefs.
        public string[] DeletedRefs { get; set; } = System.Array.Empty<string>();      // the explicitly-named refs that were removed live (part of the same SaveChanges)
        public string[] DeletesAlreadyAbsent { get; set; } = System.Array.Empty<string>(); // named delete refs whose target is genuinely GONE (no object carries the identity AND no same-named object exists) — a benign NO-OP (someone else already removed it)
        public string[] DeletesRefused { get; set; } = System.Array.Empty<string>();        // named delete refs REFUSED: the carried lineage identity no longer resolves live BUT a DIFFERENT object now bears that name — we refused to delete the wrong object (a real signal: re-diff), distinct from an already-absent no-op
        public string[] Changes { get; set; } = System.Array.Empty<string>();   // sample change log (capped)
        public string[] Unmatched { get; set; } = System.Array.Empty<string>(); // session objects with no live counterpart (left unwritten)
        public string[] LiveOnly { get; set; } = System.Array.Empty<string>();  // live objects (tables/columns/measures) absent from the session (left untouched)
        public string[] Conflicts { get; set; } = System.Array.Empty<string>(); // renames skipped because the target name is already taken on live
        public string Error { get; set; }                                       // set if the commit (SaveChanges) failed — nothing was written
    }

    public sealed class SafeFixResult
    {
        public long Revision { get; set; }
        public string[] Applied { get; set; } = System.Array.Empty<string>();
        public Semanticus.Analysis.Scorecard Scorecard { get; set; }
    }

    public sealed class ReadinessWorkItem
    {
        public Semanticus.Analysis.ReadinessFinding Finding { get; set; }
        public Semanticus.Analysis.GroundingBundle Grounding { get; set; }
    }

    /// <summary>Result of make_model_ai_ready: deterministic fixes applied + the AI-content work queue (with grounding) for Claude.</summary>
    public sealed class ReadinessPlan
    {
        public long Revision { get; set; }
        public string[] Applied { get; set; } = System.Array.Empty<string>();
        public Semanticus.Analysis.Scorecard Scorecard { get; set; }
        public ReadinessWorkItem[] AiQueue { get; set; } = System.Array.Empty<ReadinessWorkItem>();
    }

    /// <summary>One partition-refresh ("process") option with a plain-language explanation — the catalog behind
    /// list_refresh_types and the refresh_partition type parameter.</summary>
    public sealed class RefreshTypeInfo
    {
        public string Name { get; set; }          // "Full" | "DataOnly" | "Calculate" | "ClearValues" | "Automatic" | "Add" | "Defragment" | "Indexes"
        public string Explanation { get; set; }   // what it does / when to use it / the caveat
        public bool PartitionLevel { get; set; }  // valid to run on a SINGLE partition (false = table/model-level only)
        public bool Recommended { get; set; }      // the suggested default for a single-partition refresh (Full)
    }

    /// <summary>Result of refresh_partition. A DRY RUN (Committed=false) reports what WOULD run and never connects;
    /// commit=true executes RequestRefresh + SaveChanges against the live engine. Errors are reported here, not thrown.</summary>
    public sealed class RefreshReport
    {
        public string Partition { get; set; }     // "partition:Table/Name"
        public string Table { get; set; }
        public string RefreshType { get; set; }   // the resolved type, e.g. "Full"
        public string Explanation { get; set; }   // what this type does
        public string Endpoint { get; set; }      // the live endpoint it targets (resolved from the session's live origin or the arg)
        public string Database { get; set; }
        public bool Live { get; set; }             // the session is bound to a live model (a refresh can actually run)
        public bool Committed { get; set; }        // false = DRY RUN (nothing executed)
        public string Plan { get; set; }           // one-line human summary of what would happen / happened
        public string Error { get; set; }          // set when a commit fails (nothing was executed); never carries a secret
    }

    /// <summary>Result of validate_dax: a syntactic/reference check of a DAX expression against the open model.
    /// Valid=false only when there is a structural ERROR (unbalanced brackets); unknown refs are warnings.</summary>
    public sealed class DaxValidation
    {
        public bool Valid { get; set; }
        public DaxDiagnostic[] Diagnostics { get; set; } = System.Array.Empty<DaxDiagnostic>();
    }

    public sealed class DaxDiagnostic
    {
        public string Severity { get; set; }   // "error" | "warning"
        public string Message { get; set; }
        public int Line { get; set; }           // 1-based
        public int Column { get; set; }         // 1-based
    }

    // ---- Verified Edits: optimize_measure (the enforced author→prove→benchmark→apply workflow) ----------
    // The engine RACES >=2 candidate rewrites: proves each equivalent to the current body over a filter-context
    // matrix, benchmarks ONLY the proven-equivalent ones (correctness gates speed), and applies the fastest that
    // beats the baseline beyond a noise band. It refuses to finalize without >=2 candidates, without a live
    // connection to prove/benchmark, or with nothing proven — so a measure can't be "optimized" without evidence.

    /// <summary>Per-candidate evidence from an optimize_measure race — the proof + benchmark for one rewrite.</summary>
    public sealed class CandidateEvidence
    {
        public int Index { get; set; }
        public string Expression { get; set; }
        public string VerifyState { get; set; }   // pending | invalid | unverified | failed | proven (shares the ApplyPlan vocabulary)
        public EquivalenceResult Equivalence { get; set; }   // null until proven (offline/invalid)
        public BenchmarkResult Benchmark { get; set; }       // null unless it reached the benchmark (proven-equivalent only)
        public string Note { get; set; }
    }

    public sealed class OptimizeMeasureResult
    {
        // applied | dry-run | paused-free | no-improvement | none-proven | insufficient-valid | unproven-offline | error
        public string Verdict { get; set; }
        public bool Applied { get; set; }
        public long Revision { get; set; }             // set only when Applied
        public int WinnerIndex { get; set; } = -1;     // -1 = baseline kept / none
        public string WinnerExpression { get; set; }
        public string BaselineExpression { get; set; }
        public CandidateEvidence[] Candidates { get; set; } = System.Array.Empty<CandidateEvidence>();
        public string Note { get; set; }
        public string Error { get; set; }
    }

    // ---- Verified Edits: probe_measure (run a measure across many filter contexts for EVIDENCE) ----------
    // Replicates a real visual's filter context (SUMMARIZECOLUMNS axis + filter args + sentinel) so context-
    // sensitive functions (ALLSELECTED / SELECTEDVALUE / ISINSCOPE) resolve correctly. Reports BEHAVIOR
    // (value/BLANK/ERROR per member, coverage, additivity) across a scenario matrix — NEVER "correct".

    /// <summary>One probe scenario = (outer filter set × group-by grain). Agent names columns/members; the engine
    /// compiles the DAX. GroupBy empty ⇒ grand total. A filter with Empty=true is the empty-selection context.</summary>
    public sealed class ProbeScenario
    {
        public string Name { get; set; }
        public string[] GroupBy { get; set; } = System.Array.Empty<string>();
        public ProbeFilter[] Filters { get; set; } = System.Array.Empty<ProbeFilter>();
    }
    public sealed class ProbeFilter
    {
        public string Column { get; set; }
        public string[] Members { get; set; } = System.Array.Empty<string>();
        public bool Empty { get; set; }   // true ⇒ empty-selection (FILTER(ALL,FALSE())); else TREATAS(Members)
    }

    public sealed class ProbeRow
    {
        public System.Collections.Generic.Dictionary<string, object> Members { get; set; } = new();
        public object V { get; set; }
        public bool Blank { get; set; }
    }
    public sealed class ProbeCoverage { public int Total { get; set; } public int NonBlank { get; set; } public double BlankPct { get; set; } }
    public sealed class ScenarioEvidence
    {
        public string Name { get; set; }
        public string Status { get; set; }   // ok | error | unfaithful
        public string Error { get; set; }
        public string Dax { get; set; }      // echoed verbatim — diff against the real visual
        public ProbeRow[] Rows { get; set; } = System.Array.Empty<ProbeRow>();
        public ProbeCoverage Coverage { get; set; }
        public string Additivity { get; set; }   // additive | non-additive | undefined
        public bool Truncated { get; set; }
        public string[] Flags { get; set; } = System.Array.Empty<string>();
    }
    public sealed class ProbeFidelity
    {
        public string[] Modeled { get; set; } = System.Array.Empty<string>();
        public string[] NotModeled { get; set; } = System.Array.Empty<string>();
        public string Note { get; set; }
    }
    public sealed class ProbeResult
    {
        public string Status { get; set; }   // ok | no-connection | unfaithful | error
        public string Message { get; set; }
        public ProbeFidelity Fidelity { get; set; }
        public ScenarioEvidence[] Scenarios { get; set; } = System.Array.Empty<ScenarioEvidence>();
        public int ScenariosRun { get; set; }
        public int ErrorCount { get; set; }
        public int[] BlankScenarios { get; set; } = System.Array.Empty<int>();
        public int[] NonAdditiveScenarios { get; set; } = System.Array.Empty<int>();
    }

    /// <summary>Verified Mode (Pro) — the human-controlled runtime toggle. When Enabled, mutating DAX edits are
    /// verified before they commit (v1: validated — invalid DAX refused). Available = the tier may turn it on (Pro).</summary>
    public sealed class VerifiedModeState
    {
        public bool Enabled { get; set; }
        public bool Available { get; set; }
        public string Note { get; set; }
    }

    public sealed class RenameResult
    {
        public long Revision { get; set; }
        public bool Changed { get; set; }
        public string NewRef { get; set; }
    }

    public sealed class SessionInfo
    {
        public string SessionId { get; set; }
        public long Revision { get; set; }
        public string ModelName { get; set; }
        public string Source { get; set; }
        public bool HasUnsavedChanges { get; set; }
        public int Tables { get; set; }
        public int Measures { get; set; }
        public bool LiveBound { get; set; }        // opened from a live XMLA model (open_live) — deploy_live can push back to source
        public string LiveEndpoint { get; set; }   // the bound XMLA endpoint (null when not live-bound)
        public string LiveDatabase { get; set; }   // the bound dataset/database (null when not live-bound)
        public bool LiveConnected { get; set; }    // a live QUERY connection is attached (drives Studio DAX/DMV); set by a unified open or connect_*
        public string LiveKind { get; set; }       // "local" | "xmla" — the attached query engine's kind (null when not connected)
        public string LiveDataSource { get; set; } // the attached query engine's data source (null when not connected)
        public string QueryDatabase { get; set; }  // resolved dataset the attached query engine answers from
        public string QueryConnectionId { get; set; } // stable id in the shared connection/permissions registry
    }

    // ---- Model graph (powers the ER/relationship diagram + structure overview) -----------------

    public sealed class GraphTable
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public bool IsHidden { get; set; }
        public bool IsDateTable { get; set; }     // DataCategory == "Time"
        public bool IsCalculated { get; set; }    // CalculatedTable
        public int Columns { get; set; }          // excludes the RowNumber column
        public int Measures { get; set; }
        public string[] KeyColumns { get; set; } = System.Array.Empty<string>();
        public bool HasDescription { get; set; }
    }

    public sealed class GraphRelationship
    {
        public string Name { get; set; }
        public string FromTable { get; set; }
        public string FromColumn { get; set; }
        public string ToTable { get; set; }
        public string ToColumn { get; set; }
        public string FromCardinality { get; set; }   // "Many" | "One"
        public string ToCardinality { get; set; }     // "One" | "Many"
        public string CrossFilter { get; set; }        // "OneDirection" | "BothDirections" | "Automatic"
        public bool IsActive { get; set; }
    }

    public sealed class ModelGraph
    {
        public GraphTable[] Tables { get; set; } = System.Array.Empty<GraphTable>();
        public GraphRelationship[] Relationships { get; set; } = System.Array.Empty<GraphRelationship>();
    }

    /// <summary>One table's position on the diagram canvas. The engine-authoritative key is <see cref="LineageTag"/>
    /// (stable across renames; null on CL&lt;1540 models, where <see cref="Name"/> is the fallback key). <see cref="Ref"/>
    /// is set by get_layout so the UI can map a position back to the live object; it is not persisted. Width/Height are
    /// 0 when unsized (the UI uses its default node box). Persisted to the <c>.semanticus/layout.json</c> sidecar.</summary>
    public sealed class LayoutNode
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string LineageTag { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    /// <summary>The reconciled diagram layout for the open model: a position for every table that has a stored one
    /// (tables with no stored position are simply absent — the UI auto-lays those out). Returned by get_layout.</summary>
    public sealed class LayoutData
    {
        public LayoutNode[] Tables { get; set; } = System.Array.Empty<LayoutNode>();
    }

    /// <summary>Broadcast as <c>layout/didChange</c> when either door saves diagram layout, so the other door's
    /// diagram updates live (dual-drive). <see cref="Origin"/> lets a client ignore its own echo.</summary>
    public sealed class LayoutChange
    {
        public string Origin { get; set; }
        public LayoutNode[] Tables { get; set; } = System.Array.Empty<LayoutNode>();
    }

    /// <summary>Result of export_vpax. Door-safe: a bad path / extraction failure returns <see cref="Error"/> rather
    /// than throwing, mirroring the git/cicd/layout write ops.</summary>
    public sealed class VpaxExportResult
    {
        public bool Exported { get; set; }
        public string Path { get; set; }     // the .vpax written (null on error)
        public int Tables { get; set; }       // tables in the exported DAX model
        public string Note { get; set; }      // e.g. "metadata only — connect a live model for storage statistics"
        public string Error { get; set; }
    }

    /// <summary>Result of save_layout. Door-safe: a non-persistable model (never saved to disk) returns
    /// <see cref="Error"/> rather than throwing, mirroring the git/cicd write ops.</summary>
    public sealed class SaveLayoutResult
    {
        public bool Saved { get; set; }
        public string Path { get; set; }     // the sidecar file written (null on error)
        public int Count { get; set; }        // tables persisted
        public string Error { get; set; }
    }

    /// <summary>One match group from search_model: which object matched, on which field, and — critically — the
    /// MatchClass that decides whether/how the match can be safely replaced (see replace_in_object). One hit groups
    /// all same-class spans in one field of one object; an object matching in two fields (or two classes within one
    /// DAX body) yields two hits.</summary>
    public sealed class SearchHit
    {
        public string Ref { get; set; }
        public string Kind { get; set; }
        public string Name { get; set; }
        public string Table { get; set; }     // owning table name (null for tables/roles)
        public string Where { get; set; }      // LEGACY label kept for back-compat: "Name" | "Description" | "Expression" | <field>
        public string Snippet { get; set; }    // a short context snippet around the first match
        // ---- detailed-search additions (all optional; a legacy 2-arg search populates Where/Snippet as before) ----
        public string Field { get; set; }       // "name"|"description"|"expression"|"displayFolder"|"formatString"|"rlsFilter"|"mExpression"|"synonyms"
        public string MatchClass { get; set; }  // ObjectName|PlainText|DaxReference|DaxLiteral|DaxComment|DaxCode|MExpression
        public SearchSpan[] Spans { get; set; } = System.Array.Empty<SearchSpan>(); // offsets INTO THE RAW field value (highlight + scoped replace)
        public bool Replaceable { get; set; }   // false for DaxReference/DaxCode/rlsFilter — the engine refuses a literal poke
        public string ReplaceHint { get; set; } // why-not / how-to (e.g. "rename the referenced object") when not replaceable
        public string Context { get; set; }     // extra locator, e.g. the table name for an rlsFilter hit (role→table)
    }

    /// <summary>A [Start,Len) span within a field's RAW value — for UI highlighting and to scope a one-match replace.</summary>
    public sealed class SearchSpan
    {
        public int Start { get; set; }
        public int Len { get; set; }
    }

    public sealed class SearchResult
    {
        public string Query { get; set; }
        public SearchHit[] Hits { get; set; } = System.Array.Empty<SearchHit>();
        public int Total { get; set; }          // total matches found (before the `max`/offset window)
        public bool Truncated { get; set; }     // true when Total > returned Hits
        public int Offset { get; set; }         // paging offset applied
        public string Error { get; set; }       // fail-soft error (e.g. an invalid regex) — set instead of throwing
        // Facet counts over ALL matches (before paging) so the UI can render the left rail without a second round-trip.
        // Arrays (not dictionaries) on purpose: the RPC door camelCases dictionary KEYS, which would desync them from
        // the (untouched) MatchClass/field string VALUES on the hits — an array of {key,count} keeps keys stable.
        public FacetCount[] ByField { get; set; } = System.Array.Empty<FacetCount>();
        public FacetCount[] ByMatchClass { get; set; } = System.Array.Empty<FacetCount>();
        public FacetCount[] ByKind { get; set; } = System.Array.Empty<FacetCount>();
    }

    /// <summary>One facet bucket: a group key (a field name, MatchClass, or object kind) and how many hits carry it.</summary>
    public sealed class FacetCount
    {
        public string Key { get; set; }
        public int Count { get; set; }
    }

    /// <summary>Options for the detailed search_model. Back-compatible: a null/empty Fields defaults to the legacy
    /// surface (name + description + DAX expression) so the 2-arg call and existing agents behave unchanged.</summary>
    public sealed class SearchOptions
    {
        public string Query { get; set; }
        public int Max { get; set; } = 100;
        public int Offset { get; set; }
        public bool CaseSensitive { get; set; }
        public bool WholeWord { get; set; }
        public bool Regex { get; set; }
        public string[] Kinds { get; set; }     // restrict to object kinds (measure/column/table/…); null/empty = all
        public string[] Fields { get; set; }    // restrict to fields; null/empty = {name,description,expression} (legacy)
        public string Scope { get; set; }        // "table:Name" or "folder:Name" to restrict; null = whole model
    }

    /// <summary>A single safe replace routed by MatchClass. The engine (never the caller) decides the path: an
    /// ObjectName replace becomes a reference-aware rename (FormulaFixup); a DAX-identifier match is REFUSED.</summary>
    public sealed class ReplaceRequest
    {
        public string Ref { get; set; }         // object ref (or "namedexpr:Name" for a shared M expression)
        public string Field { get; set; }       // the field to replace in (see SearchHit.Field)
        public string Find { get; set; }
        public string Replace { get; set; }
        public bool CaseSensitive { get; set; }
        public bool WholeWord { get; set; }
        public bool Regex { get; set; }
        public SearchSpan Span { get; set; }     // optional: replace ONLY the occurrence at this raw offset; null = every occurrence in the field
        public string Culture { get; set; }      // synonyms only: which culture's terms to edit (null = the one search found)
        public bool Preview { get; set; }        // true = rehearse only: compute before/after + warnings, mutate NOTHING (safety blocks land in .Blocked, not a throw)
    }

    public sealed class ReplaceResult
    {
        public long Revision { get; set; }
        public bool Changed { get; set; }
        public string Ref { get; set; }          // the (possibly NEW, after a rename) ref
        public string Field { get; set; }
        public string MatchClass { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public int Replacements { get; set; }
        public string[] Warnings { get; set; } = System.Array.Empty<string>();  // e.g. M/partition references the old name (FormulaFixup can't reach M)
        public string Note { get; set; }
        public bool Preview { get; set; }        // this was a rehearsal — nothing was changed
        public string Blocked { get; set; }      // preview only: why the replace WOULD be refused (an apply throws instead)
        public int? References { get; set; }     // renames only: how many objects reference this one (fixup rewrites them all)
    }

    /// <summary>A measure row for the audit grid / list_measures (the metadata that drives "is this AI-ready?").</summary>
    public sealed class MeasureRow
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Table { get; set; }
        public string DisplayFolder { get; set; }
        public string FormatString { get; set; }
        public bool IsHidden { get; set; }
        public bool HasDescription { get; set; }
        public string Description { get; set; }
        public string Expression { get; set; }
        public DocNarrative Narrative { get; set; }   // authored documentation narrative (null for list_measures; set by getDocModel)
    }

    /// <summary>A column row for the audit grid / list_columns (the metadata that drives column AI-readiness:
    /// hidden keys, summarize-by on keys, missing descriptions/format, data category).</summary>
    public sealed class ColumnRow
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Table { get; set; }
        public string DataType { get; set; }
        public string DisplayFolder { get; set; }
        public string FormatString { get; set; }
        public string SummarizeBy { get; set; }
        public string DataCategory { get; set; }
        public bool IsKey { get; set; }
        public bool IsHidden { get; set; }
        public bool IsCalculated { get; set; }
        public bool HasDescription { get; set; }
        public string Description { get; set; }
        public string Expression { get; set; }   // calculated columns only
        public string SortByColumn { get; set; } // the column this column sorts by (null if none)
    }

    /// <summary>A table row for the object browser (get_model_objects) — enough to render a group header + filter it.</summary>
    public sealed class TableRow
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public bool IsHidden { get; set; }
        public bool IsCalculationGroup { get; set; }
    }

    /// <summary>A hierarchy row for the object browser (get_model_objects). Levels are the level display names in
    /// ordinal order — a hierarchy is a valid perspective member + a draggable object, so the browser lists it.</summary>
    public sealed class HierarchyRow
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Table { get; set; }
        public string DisplayFolder { get; set; }
        public bool IsHidden { get; set; }
        public string[] Levels { get; set; }
    }

    /// <summary>Consolidated model-object read for the Advanced-Modelling object browser (Studio backbone B):
    /// tables + their measures / columns / hierarchies with folders, data types, and hidden/key flags — everything
    /// the virtualized, group-by-folder, type-filtered browser needs in ONE round-trip (vs. separate list_columns +
    /// list_measures, which also miss hierarchies). Read-only metadata, works offline. Dual-drive: the agent door
    /// gets the same single-call browser dataset.</summary>
    public sealed class ModelObjects
    {
        public TableRow[] Tables { get; set; }
        public MeasureRow[] Measures { get; set; }
        public ColumnRow[] Columns { get; set; }
        public HierarchyRow[] Hierarchies { get; set; }
    }

    public sealed class BpaFixAllResult
    {
        public long Revision { get; set; }
        public int Applied { get; set; }
        public Semanticus.Analysis.BpaScorecard Scorecard { get; set; }
    }

    /// <summary>Result of load_bpa_rules / reset_bpa_rules: how the active BPA rule set changed.</summary>
    public sealed class BpaRulesInfo
    {
        public long Revision { get; set; }
        public string Source { get; set; }        // "file" | "url" | "inline" | "reset"
        public int Loaded { get; set; }           // rules parsed from this load (0 for reset)
        public int StandardRules { get; set; }    // the bundled standard base count
        public int ModelRules { get; set; }       // custom rules now embedded on the model annotation
        public int ActiveRules { get; set; }      // effective rules a scan will use (standard + model, de-duped)
        public string Note { get; set; }
        public string Warning { get; set; }       // e.g. the model's prior custom rules were unparseable and not preserved
    }

    /// <summary>Result of load_readiness_rules / reset_readiness_rules: how the custom readiness rule set changed.
    /// Custom rules are ADDITIVE (a built-in rule id can never be overridden), so Active = Builtin + Model.</summary>
    public sealed class ReadinessRulesInfo
    {
        public long Revision { get; set; }
        public string Source { get; set; }        // "file" | "url" | "inline" | "reset"
        public int Loaded { get; set; }           // rules parsed from this load (0 for reset)
        public int BuiltinRules { get; set; }     // the compiled built-in rule count
        public int ModelRules { get; set; }       // custom rules now embedded on the model annotation
        public int ActiveRules { get; set; }      // effective rules a scan will use (builtin + model)
        public string Note { get; set; }
        public string Warning { get; set; }       // e.g. the model's prior custom rules were unparseable and not preserved
    }

    /// <summary>Result of validate_rule: per-rule compile verdicts + an honest test-run preview against the open
    /// model (counts + first flagged objects). A preview only — nothing is saved by validating.</summary>
    public sealed class RuleValidationResult
    {
        public string Kind { get; set; }          // "bpa" | "readiness"
        public int RuleCount { get; set; }
        public bool AllValid { get; set; }
        public Semanticus.Analysis.RuleCheck[] Rules { get; set; } = System.Array.Empty<Semanticus.Analysis.RuleCheck>();
        public string Note { get; set; }
    }

    /// <summary>The model's custom (user-authored) rules of both kinds, plus the valid category and scope
    /// vocabularies — the single source of truth behind the Studio authoring form's dropdowns.</summary>
    public sealed class CustomRulesInfo
    {
        public Semanticus.Analysis.BpaRule[] Bpa { get; set; } = System.Array.Empty<Semanticus.Analysis.BpaRule>();
        public Semanticus.Analysis.CustomReadinessRuleDef[] Readiness { get; set; } = System.Array.Empty<Semanticus.Analysis.CustomReadinessRuleDef>();
        public string[] ReadinessCategories { get; set; } = System.Array.Empty<string>();
        public string[] Scopes { get; set; } = System.Array.Empty<string>();
        public string Note { get; set; }
    }

    public sealed class GeneratedObject
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Expression { get; set; }
    }

    /// <summary>Result of a generator (date table, time-intelligence suite): what was created/skipped.</summary>
    public sealed class GenerateResult
    {
        public long Revision { get; set; }
        public GeneratedObject[] Created { get; set; } = System.Array.Empty<GeneratedObject>();
        public string[] Skipped { get; set; } = System.Array.Empty<string>();
        public string Note { get; set; }
    }

    // ---- Calendar-based time intelligence (CL 1701+) ------------------------------------------

    /// <summary>One requested column→category mapping for define_calendar. TimeUnit is a
    /// Microsoft.AnalysisServices.Tabular.TimeUnit name (Year/Quarter/Month/Week/Date/MonthOfYear/…);
    /// null/empty/"timeRelated" puts the column in the calendar's time-related (untagged) bucket.</summary>
    public sealed class CalendarMappingSpec
    {
        public string Column { get; set; }
        public string TimeUnit { get; set; }
        public bool Associated { get; set; }   // true = add as an ASSOCIATED column of the unit (its primary must exist/also be mapped)
    }

    /// <summary>One column group inside a calendar: either a TimeUnit association or the time-related bucket.</summary>
    public sealed class CalendarGroupInfo
    {
        public string TimeUnit { get; set; }               // null for the time-related bucket
        public string PrimaryColumn { get; set; }
        public string[] AssociatedColumns { get; set; } = System.Array.Empty<string>();
        public string[] TimeRelatedColumns { get; set; } = System.Array.Empty<string>();   // set only on the bucket entry
    }

    public sealed class CalendarInfo
    {
        public string Table { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public CalendarGroupInfo[] Groups { get; set; } = System.Array.Empty<CalendarGroupInfo>();
    }

    public sealed class CalendarListResult
    {
        public CalendarInfo[] Calendars { get; set; } = System.Array.Empty<CalendarInfo>();
        public int CompatibilityLevel { get; set; }
        public bool CalendarsSupported { get; set; }       // CL >= 1701
        public string Note { get; set; }
    }

    /// <summary>Result of a calendar write (define/tag/template): what was created and how columns were mapped.</summary>
    public sealed class CalendarResult
    {
        public long Revision { get; set; }
        public string Table { get; set; }
        public string Calendar { get; set; }
        public GeneratedObject[] CreatedColumns { get; set; } = System.Array.Empty<GeneratedObject>();
        public string[] Mappings { get; set; } = System.Array.Empty<string>();   // human-readable "Column → TimeUnit" lines
        public string[] Skipped { get; set; } = System.Array.Empty<string>();
        public string Note { get; set; }
    }

    // ---- Row-Level Security (RLS) roles -------------------------------------------------------

    /// <summary>One table's RLS filter on a role (the DAX row filter applied to that table for the role).</summary>
    public sealed class TablePermissionInfo
    {
        public string Table { get; set; }
        public string FilterExpression { get; set; }   // the DAX RLS filter, e.g. [Region] = USERPRINCIPALNAME()
    }

    /// <summary>A security role: its model-level permission, per-table RLS filters, and members.</summary>
    public sealed class RoleInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string ModelPermission { get; set; }    // None | Read | ReadRefresh | Refresh | Administrator
        public TablePermissionInfo[] TableFilters { get; set; } = System.Array.Empty<TablePermissionInfo>();
        public string[] Members { get; set; } = System.Array.Empty<string>();
        /// <summary>Object-level (metadata) security: tables/columns this role can't see (CL ≥ 1400; empty otherwise).
        /// Only non-Default permissions are listed (Default = no OLS override = visible).</summary>
        public ObjectPermissionInfo[] ObjectPermissions { get; set; } = System.Array.Empty<ObjectPermissionInfo>();
    }

    /// <summary>Object-level (metadata) security for one table within a role: the table's own metadata permission
    /// (None hides it, Read grants it) plus any per-column overrides. Default permissions are omitted.</summary>
    public sealed class ObjectPermissionInfo
    {
        public string Table { get; set; }
        public string MetadataPermission { get; set; }   // table-level OLS: None | Read (null = Default/visible)
        public ColumnPermissionInfo[] Columns { get; set; } = System.Array.Empty<ColumnPermissionInfo>();
    }

    /// <summary>A per-column object-level (metadata) permission within a role's table permission.</summary>
    public sealed class ColumnPermissionInfo
    {
        public string Column { get; set; }
        public string MetadataPermission { get; set; }   // None | Read
    }

    /// <summary>Result of set_table_permission. Echoes the role's resulting model-level permission so a caller sees
    /// the auto-promotion (None→Read, a TOM side effect of adding an RLS filter) without a separate list_roles read;
    /// <see cref="Promoted"/> flags that elevation explicitly — it matters because RLS is a security boundary.</summary>
    public sealed class SetTablePermissionResult
    {
        public long Revision { get; set; }
        public bool Changed { get; set; }
        public string ModelPermission { get; set; }   // the role's model permission AFTER the change
        public bool Promoted { get; set; }            // true when setting the filter elevated the role None→Read
    }

    /// <summary>A ready-to-paste prompt for the user's Claude Code to remediate one AI-content finding.</summary>
    public sealed class FixPrompt
    {
        public string ObjectRef { get; set; }
        public string RuleId { get; set; }
        public string Tool { get; set; }        // the MCP tool the agent should call (e.g. set_description)
        public string Prompt { get; set; }      // the natural-language instruction (copy to Claude Code)
    }
}
