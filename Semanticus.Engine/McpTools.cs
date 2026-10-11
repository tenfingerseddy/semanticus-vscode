using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Semanticus.Analysis;

namespace Semanticus.Engine
{
    /// <summary>
    /// The MCP tool surface exposed to the user's Claude Code over stdio. Each tool is a thin call
    /// into the shared <see cref="IEngine"/> (Local when Claude owns the model, Remote when attaching
    /// to a running VS Code engine). The <see cref="IEngine"/> singleton is injected by the MCP SDK's
    /// DI; the string parameters are the model-supplied tool arguments. Agent mutations are tagged
    /// origin = "agent" so they appear in the UI's change feed and shared undo as Claude's edits.
    /// </summary>
    [McpServerToolType]
    public static partial class McpTools
    {
        private static string JsonTextOrNull(JsonElement value, string name, string expected)
        {
            if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            throw new ArgumentException($"{name} must be {expected}. Check the action's description, then call again.");
        }

        private static string JsonObjectTextOrNull(JsonElement value, string name)
        {
            if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind == JsonValueKind.Object) return value.GetRawText();
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            throw new ArgumentException($"{name} must be a JSON object of values. Check the action's description, then call again.");
        }

        private static string[] JsonTextList(JsonElement value, string name)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                var result = new List<string>();
                foreach (var item in value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) result.Add(item.GetString());
                    else throw new ArgumentException($"{name} must be a list of text values. Check the action's description, then call again.");
                return result.ToArray();
            }
            if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined) return null;
            throw new ArgumentException($"{name} must be a list of text values. Check the action's description, then call again.");
        }

        [McpServerTool(Name = "model_overview"), Description("Summarize the open semantic model: name, source, sessionId, table/measure counts, and whether there are unsaved changes. A cheap check of which model is open. For session-start orientation (connection, tier, grade, in-flight work, next actions) use get_model_summary.")]
        public static Task<SessionInfo> ModelOverview(IEngine engine) => engine.SessionInfoAsync();

        [McpServerTool(Name = "get_entitlement"), Description("Get the current Semanticus tier (free | pro) and the features it grants. `features` carries the wire ids for the TABS this installation may open: today that is workflows and askData (Ask your data: ask_data, list_data_questions and pick_chart_kind), so a Pro session reports [\"workflows\",\"askData\"] and a free session reports []. Two more things are paid and carry no wire id because neither is a tab: route_ask, and the ranked and run model plan. Read the tier for those. Everything else is free, every read and every bulk apply included. Check this before calling a workflow tool, ask_data or route_ask, so a refusal is never a surprise.")]
        public static Task<Entitlement.EntitlementInfo> GetEntitlement(IEngine engine) => engine.GetEntitlementAsync();

        [McpServerTool(Name = "get_verified_mode"), Description("Is Record verified edits on? This tool keeps its older name so existing assistants and scripts still work. The switch only adds an audit record: every DAX write (update_measure, apply_dax_script, and creating a measure, calculated column, calculated table, calculation item or function) is checked offline whether it is on or off, and malformed syntax, arity problems and unknown table, column or measure references are refused before anything is written. When on, each single-object DAX write that passes is also recorded in the Verified Edits audit trail (list_verified_edits). The record states validity only, not an equivalence or drift proof. Session-scoped (resets on reconnect). 'available' is always true: Record verified edits is free.")]
        public static Task<VerifiedModeState> GetVerifiedMode(IEngine engine) => engine.GetVerifiedModeAsync();

        [McpServerTool(Name = "set_verified_mode"), Description("Turn Record verified edits on or off. This tool keeps its older name so existing assistants and scripts still work. It does not change what is refused: every DAX write is checked offline either way (malformed syntax, arity problems and unknown table, column or measure references are refused before anything is written). When on, each single-object DAX write that passes (update_measure and creating a measure, calculated column, calculated table, calculation item or function) is also recorded in the Verified Edits audit trail (list_verified_edits). The record states validity only, not an equivalence proof. This is the human's audit switch, free on any tier. As the assistant you can turn it on but not off: the audit switch is the person's, and you are told to ask them.")]
        public static Task<VerifiedModeState> SetVerifiedMode(IEngine engine,
            [Description("true = turn Record verified edits on, false = turn it off")] bool on) => engine.SetVerifiedModeAsync(on, "agent");

        [McpServerTool(Name = "open_model"), Description("Open a semantic model from a Power BI PBIP (point at the .pbip file, the project folder, or the .SemanticModel folder), a TMDL folder, or a .bim file path. Returns basic counts. Refuses while the open model has unsaved edits unless discardUnsaved is true.")]
        public static async Task<OpenResult> OpenModel(IEngine engine,
            [Description("Absolute path to a .bim file, a TMDL folder, or a PBIP semantic-model folder")] string path,
            [Description("true = throw away unsaved edits and open anyway; default false refuses while unsaved")] bool discardUnsaved = false)
        {
            await UnsavedWorkGuard.ThrowIfBlockedAsync(engine, discardUnsaved, "agent", "open the model at " + path);
            return await engine.OpenAsync(path, discardUnsaved);
        }

        [McpServerTool(Name = "create_model"), Description("Create a brand-new, EMPTY semantic model from scratch and make it the current session (replaces any open model). Default compatibility level 1604 (Direct-Lake-capable, Power BI mode). The model is UNSAVED: the first save_model MUST be given a path. Build it with create_data_source / create_named_expression / create_import_table / create_directlake_table / create_column / create_measure / create_relationship, etc. Returns the new session. Refuses while the open model has unsaved edits unless discardUnsaved is true.")]
        public static async Task<OpenResult> CreateModel(IEngine engine,
            [Description("Model/database name (e.g. 'Sales'); empty = 'SemanticModel'")] string name = null,
            [Description("Compatibility level; default 1604 (>=1604 is required for Direct Lake). Use the default unless you have a reason.")] int compatibilityLevel = 1604,
            [Description("true = throw away unsaved edits and create anyway; default false refuses while unsaved")] bool discardUnsaved = false)
        {
            await UnsavedWorkGuard.ThrowIfBlockedAsync(engine, discardUnsaved, "agent", "start a new empty model" + (string.IsNullOrWhiteSpace(name) ? "" : " named " + name));
            return await engine.CreateModelAsync(name, compatibilityLevel, discardUnsaved);
        }

        [McpServerTool(Name = "open_local"), Description("UNIFIED open of a running local Power BI Desktop model: makes the SAME instance both editable in the tree (its metadata is snapshotted into the session) AND queryable live (DAX/DMV). Localhost uses integrated Windows auth: no token. Leave dataSource empty to auto-pick the running instance, or pass 'localhost:51234' (see list_local_instances). Refuses while the open model has unsaved edits unless discardUnsaved is true.")]
        public static async Task<OpenResult> OpenLocal(IEngine engine,
            [Description("Data source, e.g. 'localhost:51234'; empty = auto-discover the running Power BI Desktop instance")] string dataSource = null,
            [Description("Optional database/dataset name; empty = the instance's first/only model")] string database = null,
            [Description("true = throw away unsaved edits and open anyway; default false refuses while unsaved")] bool discardUnsaved = false)
        {
            await UnsavedWorkGuard.ThrowIfBlockedAsync(engine, discardUnsaved, "agent", "open the model that is running in Power BI Desktop");
            return await engine.OpenLocalAsync(dataSource, database, discardUnsaved);
        }

        [McpServerTool(Name = "list_objects"), Description("List child objects of a node. Pass an empty/null parentRef to list tables; pass a table ref (e.g. 'table:Sales') to list its measures and columns.")]
        public static Task<TreeNode[]> ListObjects(IEngine engine,
            [Description("Parent object ref, or null/empty for the model root (tables)")] string parentRef)
            => engine.ListTreeAsync(parentRef);

        [McpServerTool(Name = "get_object"), Description("Get an object's properties by ref (e.g. 'measure:Sales/Total Sales', 'column:Sales/Amount', 'table:Sales').")]
        public static Task<ObjectInfo> GetObject(IEngine engine,
            [Description("Object ref")] string objRef)
            => engine.GetObjectAsync(objRef);

        [McpServerTool(Name = "get_model_graph"), Description("Get the model's structure as a graph, for relationship and star-schema checks. detail='summary' returns counts only: tables and relationships, tables by type (date, calculated, hidden), the relationship breakdown (many-to-many, bidirectional, inactive) and the names of visible tables that sit in no relationship. It is the lighter first call on a large model. detail='full' (the default) returns every table (column and measure counts, key columns, date-table and hidden flags) and every relationship (both ends, cardinality, cross-filter direction, active or not). Read-only; works offline.")]
        public static async Task<object> GetModelGraphOp(IEngine engine,
            [Description("full (the default: every table and relationship) or summary (counts only)")] string detail = "full")
            => IsSummary(detail) ? await ModelGraphSummary(engine) : (object)await GetModelGraph(engine);

        /// <summary>The whole graph: get_model_graph with detail='full'.</summary>
        public static Task<ModelGraph> GetModelGraph(IEngine engine) => engine.GetModelGraphAsync();

        [McpServerTool(Name = "get_layout"), Description("Get the saved diagram positions for the model's tables (x/y, and width/height when set). Returns only tables that HAVE a stored position. Tables not listed are unplaced (auto-laid-out by the UI). keep=true marks a table the Diagram leaves where it is when it moves tables apart to make room for the lines; the Diagram may otherwise move tables it finds too close for their lines and save the new spots. Positions are engine-owned (a .semanticus/layout.json sidecar beside the model), keyed by a stable LineageTag so a position survives a table RENAME, and excluded from the model diff. Read it to render or reason about the table layout the human sees.")]
        public static Task<LayoutData> GetLayout(IEngine engine) => engine.GetLayoutAsync();

        [McpServerTool(Name = "save_layout"), Description("Persist diagram positions for tables (x/y, optional width/height). Identify each table by its `ref` (preferred) or `name`. Set keep=true on a table to keep that exact spot: otherwise, when tables sit on top of each other or too close for the lines between them, the Diagram moves them apart just enough and saves the new spots (read them back with get_layout). A table saved without keep loses an earlier keep. MERGES with existing positions (tables you omit keep theirs; deleted tables are pruned) and the engine re-keys each by its stable LineageTag, so the layout survives renames. Writes the .semanticus/layout.json sidecar beside the model. This is NOT a model edit (it does not touch the model definition or its diff). The model must be saved on disk first; otherwise returns an Error.")]
        public static Task<SaveLayoutResult> SaveLayout(IEngine engine,
            [Description("Tables to position. Each: ref (preferred) or name, plus x and y (and optional width/height, and keep=true to keep exactly that spot).")] LayoutNode[] tables,
            [Description("Originating driver tag for the change (default 'agent')")] string origin = "agent")
            => engine.SaveLayoutAsync(tables, origin);

        [McpServerTool(Name = "export_vpax"), Description("Export the open model to a VertiPaq-Analyzer .vpax file (the interchange format for VertiPaq Analyzer, DAX Studio, and the SQLBI ecosystem), using Microsoft/SQLBI's official Dax.Vpax. Writes the model METADATA (tables, columns, measures, relationships). Works offline. Storage statistics (column sizes / cardinality) require a processed/live model and are not included yet. Returns the path, table count, and a note; door-safe (a bad path returns .Error rather than throwing).")]
        public static Task<VpaxExportResult> ExportVpax(IEngine engine,
            [Description("Target .vpax file path to write.")] string path)
            => engine.ExportVpaxAsync(path, "agent");

        [McpServerTool(Name = "search_model"), Description("Detailed model-wide search. Default (no filters): case-insensitive substring over object names, descriptions, and DAX expressions: the classic behaviour. Optional modes: caseSensitive, wholeWord, regex (validated, with a timeout guard). Optional filters: kinds[] (measure/column/table/hierarchy/calcitem/function/role/perspective/partition), fields[] (name, description, expression, displayFolder, formatString, rlsFilter, mExpression, synonyms. Pass fields to widen BEYOND the default name+description+expression), and scope ('table:Name' or 'folder:Name'). Each hit carries ref, kind, table, the field matched, a snippet, raw-offset spans[], and (key for replace) a matchClass (ObjectName | PlainText | DaxReference | DaxLiteral | DaxComment | DaxCode | MExpression) with replaceable + replaceHint. matchClass tells you how to change it safely: ObjectName → rename_object (references auto-update); DaxReference/DaxCode → NOT text-replaceable (rename the object); DaxLiteral/DaxComment/PlainText/MExpression → replace_in_object. Results carry byField/byMatchClass/byKind facet counts and paging (offset). An invalid regex is reported in .Error (not thrown).")]
        public static Task<SearchResult> SearchModel(IEngine engine,
            [Description("Text to find. A literal substring, or a regex when regex=true.")] string query,
            [Description("Max hits to return (default 100)")] int max = 100,
            [Description("Match case exactly (default false)")] bool caseSensitive = false,
            [Description("Match whole words only (word boundaries) (default false)")] bool wholeWord = false,
            [Description("Treat query as a .NET regular expression (default false)")] bool regex = false,
            [Description("Restrict to object kinds, e.g. ['measure','column']. Empty = all kinds.")] string[] kinds = null,
            [Description("Restrict to fields, e.g. ['name','expression','displayFolder']. Empty = name+description+expression (the default surface).")] string[] fields = null,
            [Description("Restrict scope: 'table:Sales' or 'folder:Financials'. Empty = whole model.")] string scope = null,
            [Description("Paging offset: skip this many hits before returning (default 0)")] int offset = 0)
            => engine.SearchModelAsync(new SearchOptions { Query = query, Max = max, CaseSensitive = caseSensitive, WholeWord = wholeWord, Regex = regex, Kinds = kinds, Fields = fields, Scope = scope, Offset = offset });

        [McpServerTool(Name = "replace_in_object"), Description("Safely replace matched text in ONE object field, routed by the field's matchClass (from search_model). name → a reference-aware RENAME (FormulaFixup rewrites every DAX/RLS reference); description/displayFolder/formatString → plain-text set; expression → replaces text INSIDE a DAX string-literal or comment only (validated for syntax); mExpression → literal M edit (with a warning: M is not reference-fixed); synonyms → updates the linguistic terms. HARD-BLOCKED: a match on a DAX identifier/reference or on DAX code. It returns the 'rename the object instead' hint rather than corrupting the formula. RLS filters are rename-only. This is a single, free, undoable edit. Pass spanStart/spanLen (from a hit's spans[]) to change ONLY that one occurrence; omit them to replace every occurrence in the field. preview=true rehearses: returns the same before/after + warnings (+ .references blast radius for a rename) WITHOUT mutating, and reports a safety refusal in .blocked instead of throwing. Returns before/after, the (possibly new) ref, and any warnings (e.g. M references the old name). For MANY matches at once, use propose_replace + apply_plan instead.")]
        public static Task<ReplaceResult> ReplaceInObject(IEngine engine,
            [Description("Object ref, e.g. 'measure:Sales/Total Sales' (or 'namedexpr:Name' for a shared M expression)")] string objRef,
            [Description("Field to edit: name | description | expression | displayFolder | formatString | mExpression | synonyms")] string field,
            [Description("Text to find (a literal substring, or a regex when regex=true)")] string find,
            [Description("Replacement text (literal: capture-group expansion like $1 is not applied in this version)")] string replace,
            [Description("Match case exactly (default false)")] bool caseSensitive = false,
            [Description("Match whole words only (default false)")] bool wholeWord = false,
            [Description("Treat find as a regex (default false)")] bool regex = false,
            [Description("Optional: replace ONLY the occurrence at this raw offset (from a hit's spans[].start). -1 = every occurrence.")] int spanStart = -1,
            [Description("Length of that occurrence (from a hit's spans[].len). Required if spanStart is set.")] int spanLen = 0,
            [Description("Synonyms only: which culture's terms to edit (default: the one the search found)")] string culture = null,
            [Description("true = rehearse only: compute before/after + warnings, change NOTHING (a safety refusal lands in .blocked, not a throw)")] bool preview = false)
            => engine.ReplaceInObjectAsync(new ReplaceRequest
            {
                Ref = objRef, Field = field, Find = find, Replace = replace,
                CaseSensitive = caseSensitive, WholeWord = wholeWord, Regex = regex,
                Span = spanStart >= 0 ? new SearchSpan { Start = spanStart, Len = spanLen } : null, Culture = culture,
                Preview = preview,
            }, "agent");

        [McpServerTool(Name = "propose_replace"), Description("Bulk find & replace as a reviewable Change Plan (nothing mutates). Runs the same detailed search as search_model, then turns every replaceable match into the RIGHT KIND of plan item: name matches → 'rename' items (FormulaFixup rewrites every DAX/RLS reference at apply; renames are opt-in 'proposed'); description/displayFolder/formatString/synonyms → plain-text set items (pre-approved, safe); matches INSIDE DAX string literals/comments → 'set_dax' items spliced span-wise so reference spans are NEVER touched (opt-in: they change results; validated for syntax); M bodies → 'set_m' items (opt-in, literal, not reference-fixed). DAX-reference/DAX-code/RLS matches yield NO items. The view's .note reports them honestly (rename the referenced object instead). Items that cannot apply (name collisions, empty results) come back status='skipped' with the reason. REPLACES any current plan. Next: get_plan to review, set_plan_item to approve/reject, then apply_plan.")]
        public static Task<ChangePlanView> ProposeReplace(IEngine engine,
            [Description("Text to find (a literal substring, or a regex when regex=true)")] string find,
            [Description("Replacement text (literal: capture-group expansion like $1 is not applied in this version)")] string replace,
            [Description("Match case exactly (default false)")] bool caseSensitive = false,
            [Description("Match whole words only (default false)")] bool wholeWord = false,
            [Description("Treat find as a regex (default false)")] bool regex = false,
            [Description("Restrict to object kinds, e.g. ['measure','column']. Empty = all kinds.")] string[] kinds = null,
            [Description("Restrict to fields, e.g. ['name','description','displayFolder']. Empty = name+description+expression (the search_model default surface).")] string[] fields = null,
            [Description("Restrict scope: 'table:Sales' or 'folder:Financials'. Empty = whole model.")] string scope = null,
            [Description("Cap on plan items (default 500); overflow is reported in the view's note.")] int maxItems = 500)
            => engine.ProposeReplaceAsync(new SearchOptions
            {
                Query = find, CaseSensitive = caseSensitive, WholeWord = wholeWord, Regex = regex,
                Kinds = kinds, Fields = fields, Scope = scope,
            }, replace, maxItems, "agent");

        [McpServerTool(Name = "list_measures"), Description("List every measure in the model with the metadata that drives AI-readiness and authoring: ref, name, owning table, display folder, format string, hidden flag, whether it has a description (+ the description), and its DAX expression. One call to audit naming/folders/formats/descriptions across all measures (e.g. find measures missing a description or a format string).")]
        public static Task<MeasureRow[]> ListMeasures(IEngine engine) => engine.ListMeasuresAsync();

        [McpServerTool(Name = "list_columns"), Description("List every column (excluding the internal RowNumber) with the metadata that drives column AI-readiness/authoring: ref, name, table, data type, display folder, format string, summarize-by, data category, and flags for key / hidden / calculated / has-description (+ description, + DAX expression for calculated columns). One call to audit smells like a visible key column, a key with SummarizeBy != None, or a missing description/format.")]
        public static Task<ColumnRow[]> ListColumns(IEngine engine) => engine.ListColumnsAsync();

        [McpServerTool(Name = "get_model_objects"), Description("One-call browser dataset: every table + its measures, columns, and hierarchies (with the level names, in order), each with its display folder, data type, and hidden/key flags. Backs the Advanced-Modelling object browser (perspective/field-parameter/RLS pickers). Prefer this over separate list_measures + list_columns when you need the WHOLE object surface at once (it also carries hierarchies, which those omit). Read-only metadata; works offline.")]
        public static Task<ModelObjects> GetModelObjects(IEngine engine) => engine.GetModelObjectsAsync();

        /// <summary>The graph counts: get_model_graph with detail='summary'.</summary>
        public static async Task<ModelGraphSummary> ModelGraphSummary(IEngine engine) => Semanticus.Engine.ModelGraphSummary.From(await engine.GetModelGraphAsync());

        [McpServerTool(Name = "get_model_summary"), Description("Session-start orientation for a fresh session, after a context compaction, or at a handoff. One round-trip, token-budgeted (≤~2k tokens), giving connection state · entitlement · model overview · a compact excerpt from the model's declared Primer · AI-readiness · structural graph · in-flight work · recent activity · deterministic next actions. Drill down with get_model_primer, connection_status, get_entitlement, ai_readiness_scan, get_model_graph, get_workflow_run, get_plan, get_spec and list_calendars. It omits full findings, object detail and DAX bodies.")]
        public static Task<ModelSummary> GetModelSummary(IEngine engine) => engine.GetOrientationAsync();

        [McpServerTool(Name = "get_dax"), Description("Get the DAX expression of a measure or calculated column/table by ref (e.g. 'measure:Sales/Total Sales').")]
        public static Task<string> GetDax(IEngine engine,
            [Description("Ref of a measure or calculated column/table")] string objRef)
            => engine.GetDaxAsync(objRef);

        [McpServerTool(Name = "update_measure"), Description("Set the DAX expression of a measure, calculated column, calculated table, calculation item or DAX function (a function takes a lambda such as (x: INT64) => x + 1). The expression is validated offline first: malformed syntax, arity problems and unknown table, column or measure references are refused before anything is written, whatever the Record verified edits setting. An unchanged expression is a no-op. The change is applied to the live shared session, appears immediately in the VS Code UI, and is a single undoable step.")]
        public static Task<SetResult> UpdateMeasure(IEngine engine,
            [Description("Ref of the measure/calculated object, e.g. 'measure:Sales/Total Sales'")] string objRef,
            [Description("The new DAX expression")] string expression,
            [Description("Optional session targeting: the sessionId you read for the model you mean to edit (create_model / open_model / open_local / open_live return it; model_overview reads it back). If the live session is no longer that one, the edit is refused before anything is written. Omit it to edit whatever model is currently open (the existing behavior).")] string sessionId = null)
            // T172: the agent door says WHICH session it meant, instead of inheriting SessionManager._current in silence.
            // The engine already owns the check: GuardExpectedModel runs inside the single-writer dispatch turn, so no swap
            // can slip between a preflight and the write, and a mismatch refuses before the model, undo or bus is touched.
            // Forwarded verbatim, so only an OMITTED argument (null) is unfenced; any supplied value meets that same guard.
            => engine.SetDaxAsync(objRef, expression, "agent", sessionId);

        [McpServerTool(Name = "create_measure"), Description("Create a new measure on a table. Returns the new object ref. Undoable. Pass displayFolder to create it already filed ('Parent\\Child' nests). Create + folder is ONE undo step.")]
        public static Task<string> CreateMeasure(IEngine engine,
            [Description("Table ref, e.g. 'table:Sales'")] string tableRef,
            [Description("Measure name")] string name,
            [Description("DAX expression")] string expression,
            [Description("Optional display folder path, e.g. 'KPIs' or 'Sales\\Growth'. Empty = table root.")] string displayFolder = null)
            => engine.CreateMeasureAsync(tableRef, name, expression, "agent", displayFolder);

        [McpServerTool(Name = "set_display_folder"), Description("File measures/columns/hierarchies into a display folder in ONE undoable batch (or clear with an empty folder). A display folder is just the DisplayFolder string on each member ('Parent\\Child' nests). It has no existence of its own, so an EMPTY folder cannot persist: create one by moving/creating a member into it. All-or-nothing: any bad ref rolls the whole batch back. For a single object set_property(DisplayFolder) works too; this op keeps a multi-object gesture to one undo step.")]
        public static Task<SetResult> SetDisplayFolder(IEngine engine,
            [Description("Object refs to file, e.g. ['measure:Sales/Total Sales','measure:Sales/Margin'] (measures/columns/hierarchies; see list_measures / list_columns)")] string[] refs,
            [Description("Target folder path ('KPIs' or 'Sales\\Growth'); empty/null clears (back to the table root)")] string folder = null)
            => engine.SetDisplayFolderAsync(refs, folder, "agent");

        [McpServerTool(Name = "rename_display_folder"), Description("Rename (or move) a display folder on a table by rewriting the DisplayFolder prefix on EVERY member (measures, columns and hierarchies, nested subfolders included) in ONE undoable batch. toPath may be nested ('Parent\\Child') to move the folder; empty toPath removes the folder level (members are promoted). Fails with a teaching message if no member is in fromPath (folders exist only through their members).")]
        public static Task<FolderRenameResult> RenameDisplayFolder(IEngine engine,
            [Description("Table ref, e.g. 'table:Sales'")] string tableRef,
            [Description("The current folder path exactly as on the members, e.g. 'KPIs' or 'Sales\\Growth'")] string fromPath,
            [Description("The new folder path; empty = remove this folder level")] string toPath = null)
            => engine.RenameDisplayFolderAsync(tableRef, fromPath, toPath, "agent");

        [McpServerTool(Name = "list_functions"), Description("List the model's DAX user-defined functions (UDFs). Each has a ref like 'function:MyFunc'. Read its body with get_dax, edit with update_measure, rename with rename_object.")]
        public static Task<TreeNode[]> ListFunctions(IEngine engine) => engine.ListFunctionsAsync();

        [McpServerTool(Name = "create_function"), Description("Create a DAX user-defined function (UDF). The expression is a lambda, e.g. '(x: INT64, y: INT64) => x + y'. Names cannot contain spaces. Requires model compatibility level >= 1702 (call set_compatibility_level first if needed). Returns the new ref ('function:Name'). Undoable.")]
        public static Task<string> CreateFunction(IEngine engine,
            [Description("Function name (no spaces; letters/digits/_/. only)")] string name,
            [Description("Lambda expression, e.g. '(x: INT64) => x * 2'")] string expression)
            => engine.CreateFunctionAsync(name, expression, "agent");

        // ---- DaxLib package manager (Advanced Modelling): browse + install DAX UDF packages from daxlib.org ----

        [McpServerTool(Name = "daxlib_search"), Description("Search the DaxLib feed (daxlib.org, the 'app store for DAX UDFs') for packages. Anonymous + read-only; no model needs to be open. Omit 'text' to list all. Returns package summaries (id, latest version, description, authors, downloads). Then use daxlib_package_info to preview a package's functions, and daxlib_install to add them.")]
        public static Task<DaxLibPackage[]> DaxLibSearch(IEngine engine,
            [Description("Free-text filter over id/description; omit to list everything")] string text = null,
            [Description("Page offset (default 0)")] int skip = 0,
            [Description("Page size 1..100; 0 = fetch all pages")] int take = 0)
            => engine.DaxLibSearchAsync(text, skip, take);

        [McpServerTool(Name = "daxlib_versions"), Description("List the published versions of a DaxLib package (newest-first, including -prerelease tags). Anonymous + read-only.")]
        public static Task<string[]> DaxLibVersions(IEngine engine, [Description("Package id, e.g. 'Daxlib.Sample'")] string id)
            => engine.DaxLibVersionsAsync(id);

        [McpServerTool(Name = "daxlib_package_info"), Description("Get a DaxLib package's detail: metadata, dependencies, and the list of UDF names it would install (the blast radius to review before installing). Anonymous + read-only. Omit 'version' for the latest stable.")]
        public static Task<DaxLibPackageDetail> DaxLibPackageInfo(IEngine engine,
            [Description("Package id")] string id,
            [Description("Version (omit for latest stable)")] string version = null)
            => engine.DaxLibPackageInfoAsync(id, version);

        [McpServerTool(Name = "daxlib_install"), Description("Install a DaxLib package's DAX UDFs into the OPEN model as ONE atomic, undoable transaction (raises compatibility level to 1702 if needed; pulls in dependency packages, deps-first). By default existing functions of the same name are left untouched (reported as skipped); pass replaceExisting=true to overwrite. Provenance is recorded so daxlib_list_installed / daxlib_uninstall work. Undoable in one step.")]
        public static Task<DaxLibInstallResult> DaxLibInstall(IEngine engine,
            [Description("Package id")] string id,
            [Description("Version (omit for latest stable)")] string version = null,
            [Description("Overwrite functions of the same name (default false = skip them)")] bool replaceExisting = false)
            => engine.DaxLibInstallAsync(id, version, replaceExisting, "agent");

        [McpServerTool(Name = "daxlib_list_installed"), Description("List the DaxLib packages installed in the open model (from the Semanticus_DaxLibInstalled provenance annotation): id, version, and the UDF names each owns. Offline, no network.")]
        public static Task<DaxLibInstalledRecord[]> DaxLibListInstalled(IEngine engine) => engine.DaxLibListInstalledAsync();

        [McpServerTool(Name = "daxlib_uninstall"), Description("Remove a DaxLib package previously installed by Semanticus: deletes the UDFs it owns (per provenance) in one undoable batch and clears its provenance record.")]
        public static Task<SetResult> DaxLibUninstall(IEngine engine, [Description("Package id to remove")] string id)
            => engine.DaxLibUninstallAsync(id, "agent");

        // ---- Object authoring: create / delete / duplicate ---------------------------------------

        [McpServerTool(Name = "create_table"), Description("Create a new (empty) table. Add columns with create_column/create_calculated_column; it needs a partition before it can be deployed/refreshed. Returns the new ref ('table:Name'). Undoable.")]
        public static Task<string> CreateTable(IEngine engine, [Description("Table name (unique)")] string name)
            => engine.CreateTableAsync(name, "agent");

        [McpServerTool(Name = "create_column"), Description("Add a DATA column (a physical/source column) to a table. dataType: String|Int64|Decimal|Double|DateTime|Boolean (default String). sourceColumn is the underlying physical column name (defaults to the column name). Returns the new ref ('column:Table/Name'). Undoable.")]
        public static Task<string> CreateColumn(IEngine engine,
            [Description("Table ref or bare table name, e.g. 'table:Sales' or 'Sales'")] string tableRef,
            [Description("Column name")] string name,
            [Description("String | Int64 | Decimal | Double | DateTime | Boolean")] string dataType = "String",
            [Description("Underlying physical/source column (defaults to name)")] string sourceColumn = null)
            => engine.CreateColumnAsync(tableRef, name, dataType, sourceColumn, "agent");

        [McpServerTool(Name = "create_data_source"), Description("Create a structured (M) data source pointing at a Fabric SQL endpoint / Lakehouse-or-Warehouse SQL analytics endpoint (protocol 'tds'). Needs compatibility level >= 1400. Referenced by NAME from create_directlake_table or inside an M expression. Returns the data source name. Undoable. (Credentials are supplied out-of-band at refresh. None are stored here.).")]
        public static Task<string> CreateDataSource(IEngine engine,
            [Description("Data source name (unique), e.g. 'FabricSql'")] string name,
            [Description("Server, e.g. 'xxxxx.datawarehouse.fabric.microsoft.com'")] string server = null,
            [Description("Database / Lakehouse / Warehouse name")] string database = null)
            => engine.CreateDataSourceAsync(name, server, database, "agent");

        [McpServerTool(Name = "create_named_expression"), Description("Create a shared (model-level) M expression / parameter: the source target for a Direct Lake partition, or reusable M referenced by name (#\"Name\") inside import partitions. Returns the expression name. Undoable.")]
        public static Task<string> CreateNamedExpression(IEngine engine,
            [Description("Expression name (unique), e.g. 'Lakehouse'")] string name,
            [Description("The M expression, e.g. 'let s = Sql.Database(\"x.datawarehouse.fabric.microsoft.com\",\"LH\") in s'")] string expression = null)
            => engine.CreateNamedExpressionAsync(name, expression, "agent");

        [McpServerTool(Name = "list_partitions"), Description("List a table's partitions (ref, mode, source type, data source). Read a partition's M with get_partition_m, edit it with set_partition_m.")]
        public static Task<PartitionInfo[]> ListPartitions(IEngine engine,
            [Description("Table ref or bare table name, e.g. 'table:Sales' or 'Sales'")] string tableRef)
            => engine.ListPartitionsAsync(tableRef);

        [McpServerTool(Name = "get_partition_m"), Description("Get a partition's M source expression. Errors if the partition is not an M source (e.g. a Direct Lake Entity or DAX calculated partition).")]
        public static Task<string> GetPartitionM(IEngine engine,
            [Description("Partition ref, e.g. 'partition:Sales/Sales'")] string partitionRef)
            => engine.GetPartitionMAsync(partitionRef);

        [McpServerTool(Name = "set_partition_m"), Description("Replace a partition's M source expression (M partitions only). Use to add an incremental-refresh RangeStart/RangeEnd date filter, or to edit the query. Undoable.")]
        public static Task<SetResult> SetPartitionM(IEngine engine,
            [Description("Partition ref, e.g. 'partition:Sales/Sales'")] string partitionRef,
            [Description("The full M expression (a let … in … query)")] string mExpression)
            => engine.SetPartitionMAsync(partitionRef, mExpression, "agent");

        [McpServerTool(Name = "list_named_expressions"), Description("List the model's shared M expressions + parameters (name, kind, M, description). RangeStart/RangeEnd parameters show here. Edit one with update_named_expression, create with create_named_expression.")]
        public static Task<DocExpression[]> ListNamedExpressions(IEngine engine)
            => engine.ListNamedExpressionsAsync();

        [McpServerTool(Name = "get_named_expression"), Description("Get one shared M expression / parameter's M text by name (e.g. 'RangeStart').")]
        public static Task<string> GetNamedExpression(IEngine engine,
            [Description("Expression name")] string name)
            => engine.GetNamedExpressionAsync(name);

        [McpServerTool(Name = "update_named_expression"), Description("Replace the M of an existing shared expression / parameter (create it first with create_named_expression). Undoable.")]
        public static Task<SetResult> UpdateNamedExpression(IEngine engine,
            [Description("Expression name")] string name,
            [Description("The new M expression")] string expression)
            => engine.UpdateNamedExpressionAsync(name, expression, "agent");

        [McpServerTool(Name = "create_import_table"), Description("Create an IMPORT-mode table whose data is loaded via an M partition, e.g. from a Fabric SQL endpoint via Sql.Database(...). Bundles the table + its M partition (no orphaned placeholder). Add columns with create_column. Returns the table ref ('table:Name'). Undoable.")]
        public static Task<string> CreateImportTable(IEngine engine,
            [Description("Table name (unique)")] string name,
            [Description("The M expression that returns the table, e.g. 'let s = Sql.Database(\"...\",\"DW\"), t = s{[Schema=\"dbo\",Item=\"Sales\"]}[Data] in t'")] string mExpression = null)
            => engine.CreateImportTableAsync(name, mExpression, "agent");

        [McpServerTool(Name = "create_directlake_table"), Description("Create a DIRECT LAKE table backed by a Fabric Lakehouse/Warehouse entity (Parquet/Delta, no data copy). Needs compatibility level >= 1604. Bind it to a source created first with create_named_expression (M to the SQL endpoint) OR create_data_source (structured), referenced by name. Returns the table ref ('table:Name'). Undoable.")]
        public static Task<string> CreateDirectLakeTable(IEngine engine,
            [Description("Table name (unique)")] string name,
            [Description("The lakehouse/warehouse entity (physical table) name; defaults to the table name")] string entityName = null,
            [Description("Schema of the entity, e.g. 'dbo' (optional)")] string schemaName = null,
            [Description("Name of the shared expression (create_named_expression) or structured data source (create_data_source) to read from")] string sourceName = null)
            => engine.CreateDirectLakeTableAsync(name, entityName, schemaName, sourceName, "agent");

        [McpServerTool(Name = "create_calculated_table"), Description("Create a DAX calculated table (its single partition is a DAX expression, e.g. a date dimension or bridge). Returns the table ref ('table:Name'). Undoable.")]
        public static Task<string> CreateCalculatedTable(IEngine engine,
            [Description("Table name (unique)")] string name,
            [Description("DAX table expression, e.g. 'CALENDARAUTO()' or \"SUMMARIZE(Sales,Sales[Region])\"")] string expression)
            => engine.CreateCalculatedTableAsync(name, expression, "agent");

        [McpServerTool(Name = "create_field_parameter"), Description("Create a Power BI FIELD PARAMETER: a calculated table of NAMEOF(...) rows that drives a slicer to swap which measures/columns a visual shows. Builds the full Power-BI-Desktop-identical structure (the 3 columns, the ParameterMetadata marker, sort-by, hidden plumbing columns, and the field-switch key). Items are measure/column refs in display order; each label defaults to the object's name. Requires a Power BI-mode model at compatibility level >= 1400. Returns the table ref ('table:Name'). Undoable.")]
        public static Task<string> CreateFieldParameter(IEngine engine,
            [Description("Field-parameter table name (unique)")] string name,
            [Description("Fields in display order. Each is {objectRef: 'measure:…' or 'column:…', label?: 'optional display name'}")] FieldParameterItem[] items)
            => engine.CreateFieldParameterAsync(name, items, "agent");

        [McpServerTool(Name = "set_column_data_type"), Description("Set a data (source) column's data type: String|Int64|Decimal|Double|DateTime|Boolean. Only applies to physical/source columns (a calculated column derives its type from its DAX). Undoable.")]
        public static Task<SetResult> SetColumnDataType(IEngine engine,
            [Description("Column ref, e.g. 'column:Sales/Amount'")] string columnRef,
            [Description("String | Int64 | Decimal | Double | DateTime | Boolean")] string dataType)
            => engine.SetColumnDataTypeAsync(columnRef, dataType, "agent");

        [McpServerTool(Name = "get_source_schema"), Description("Update-Schema step 1: re-read a table's SOURCE columns (names + data types) schema-only, WITHOUT importing data, by probing the SQL/Fabric endpoint its partition query reads (server/database/schema/table parsed from the partition M, or read off a Direct-Lake entity partition) over TDS. Returns the source columns mapped to TOM types (String|Int64|Decimal|Double|DateTime|Boolean). Needs a reachable source: for an OFFLINE snapshot, a non-SQL source, or a native-query partition it returns Reachable=false with a clear message (never throws). Use diff_schema to compare against the model.")]
        public static Task<SourceSchema> GetSourceSchema(IEngine engine,
            [Description("Table ref or name, e.g. 'table:Sales'")] string tableRef,
            [Description("Entra auth mode for the SQL endpoint: azcli (default) | interactive | devicecode | serviceprincipal | token")] string authMode = "azcli",
            [Description("Optional Entra tenant id/domain to target")] string tenantId = null)
            => engine.GetSourceSchemaAsync(tableRef, authMode, tenantId, "agent");

        [McpServerTool(Name = "diff_schema"), Description("Update-Schema step 2: diff a table's SOURCE columns against its CURRENT model columns: ADDED at the source, REMOVED from it, or TYPE-CHANGED. Each item carries a stable id for per-item selection. By default it probes the live source (like get_source_schema); pass sourceColumns to diff a SUPPLIED/synthesized schema instead (the offline/manual path, no connection needed). Calculated columns are never flagged (they're DAX-derived, not source-driven). Returns Reachable=false + a message when the live source can't be read. Feed the accepted items to apply_schema_update.")]
        public static Task<SchemaDiff> DiffSchema(IEngine engine,
            [Description("Table ref or name, e.g. 'table:Sales'")] string tableRef,
            [Description("Optional SUPPLIED source columns to diff against (skips the live probe). Each: {name, dataType (TOM type), sqlType?}. Omit/empty to probe the live source.")] SourceColumn[] sourceColumns = null,
            [Description("Entra auth mode when probing live: azcli (default) | interactive | devicecode | serviceprincipal | token")] string authMode = "azcli",
            [Description("Optional Entra tenant id/domain to target when probing live")] string tenantId = null)
            => engine.DiffSchemaAsync(tableRef, sourceColumns, authMode, tenantId, "agent");

        [McpServerTool(Name = "apply_schema_update"), Description("Update-Schema step 3: apply a chosen SUBSET of a schema diff as ONE undoable change: add the source columns you accept, retype the ones whose type drifted, remove the ones the source dropped. Each item: {change: Added|Removed|TypeChanged, column, dataType? (TOM type, for Added/TypeChanged), sourceColumn? (physical name for Added, defaults to column)}. Items that can't apply (a calculated column, a name clash, an unknown column) are SKIPPED with a reason. The accepted changes still commit atomically. Returns per-kind counts + the applied/skipped lists.")]
        public static Task<ApplySchemaResult> ApplySchemaUpdate(IEngine engine,
            [Description("Table ref or name, e.g. 'table:Sales'")] string tableRef,
            [Description("The accepted diff items to apply. Each: {change, column, dataType?, sourceColumn?}.")] SchemaUpdateItem[] items)
            => engine.ApplySchemaUpdateAsync(tableRef, items, "agent");

        [McpServerTool(Name = "create_calculated_column"), Description("Add a DAX calculated column to a table. Returns the new ref ('column:Table/Name'). Undoable.")]
        public static Task<string> CreateCalculatedColumn(IEngine engine,
            [Description("Table ref or name")] string tableRef,
            [Description("Column name")] string name,
            [Description("DAX expression")] string expression)
            => engine.CreateCalculatedColumnAsync(tableRef, name, expression, "agent");

        [McpServerTool(Name = "create_relationship"), Description("Create a single-column relationship from a foreign-key column (the MANY side) to a lookup column (the ONE side). crossFilter: OneDirection|BothDirections (optional). isActive optional. Returns the new ref ('relationship:Name'). Undoable.")]
        public static Task<string> CreateRelationship(IEngine engine,
            [Description("FROM column (many / foreign-key side), e.g. 'column:Sales/CustomerKey'")] string fromColumnRef,
            [Description("TO column (one / lookup side), e.g. 'column:Customer/CustomerKey'")] string toColumnRef,
            [Description("OneDirection | BothDirections (optional)")] string crossFilter = null,
            [Description("true=active (default), false=inactive (optional)")] bool? isActive = null)
            => engine.CreateRelationshipAsync(fromColumnRef, toColumnRef, crossFilter, isActive, "agent");

        [McpServerTool(Name = "create_hierarchy"), Description("Create a hierarchy on a table from an ordered list of its column names (top level first). Returns the new ref. Undoable.")]
        public static Task<string> CreateHierarchy(IEngine engine,
            [Description("Table ref or name")] string tableRef,
            [Description("Hierarchy name")] string name,
            [Description("Ordered column names for the levels (top first), e.g. ['Year','Quarter','Month']")] string[] levelColumns)
            => engine.CreateHierarchyAsync(tableRef, name, levelColumns, "agent");

        [McpServerTool(Name = "create_calculation_group"), Description("Create a calculation group (a special table). Add calculation items with create_calculation_item. Requires compatibility level >= 1470. Returns the new ref ('table:Name'). Undoable.")]
        public static Task<string> CreateCalculationGroup(IEngine engine, [Description("Calculation group name (unique)")] string name)
            => engine.CreateCalculationGroupAsync(name, "agent");

        [McpServerTool(Name = "create_calculation_item"), Description("Add a calculation item to a calculation group. The expression typically uses SELECTEDMEASURE(), e.g. 'CALCULATE(SELECTEDMEASURE(), DATESYTD(...))'. Returns the new ref. Undoable.")]
        public static Task<string> CreateCalculationItem(IEngine engine,
            [Description("Calculation group ref or name, e.g. 'table:Time Intelligence'")] string calcGroupRef,
            [Description("Calculation item name")] string name,
            [Description("DAX expression (usually wraps SELECTEDMEASURE())")] string expression)
            => engine.CreateCalculationItemAsync(calcGroupRef, name, expression, "agent");

        [McpServerTool(Name = "set_calc_item_format_string"), Description("Set (or clear) a calculation item's DYNAMIC format-string expression: the DAX that overrides the format string of measures evaluated under this item (e.g. a '% of Total' item rendering 0.0%, or a currency item). Pass an empty string to clear it (the item falls back to the base/model format). Undoable.")]
        public static Task<SetResult> SetCalcItemFormatString(IEngine engine,
            [Description("Calculation item ref, e.g. 'calcitem:Time Intelligence/YTD'")] string calcItemRef,
            [Description("Format-string DAX expression; empty clears it")] string formatExpression)
            => engine.SetCalcItemFormatStringAsync(calcItemRef, formatExpression, "agent");

        [McpServerTool(Name = "set_calc_group_precedence"), Description("Set a calculation group's precedence (an integer; a HIGHER precedence is applied FIRST when multiple calculation groups combine). Give each calc group a distinct precedence so their combination order is deterministic. Undoable.")]
        public static Task<SetResult> SetCalcGroupPrecedence(IEngine engine,
            [Description("Calculation group ref, e.g. 'calcgroup:Time Intelligence' or 'table:Time Intelligence'")] string calcGroupRef,
            [Description("Precedence integer (higher applies first)")] int precedence)
            => engine.SetCalcGroupPrecedenceAsync(calcGroupRef, precedence, "agent");

        [McpServerTool(Name = "create_perspective"), Description("Create a perspective: a named, curated subset of the model's tables/columns/measures/hierarchies (a focused Copilot or report view). Add members with set_perspective_member; delete with delete_object and rename with rename_object. Returns the new ref ('perspective:Name'). Undoable.")]
        public static Task<string> CreatePerspective(IEngine engine, [Description("Perspective name (unique)")] string name)
            => engine.CreatePerspectiveAsync(name, "agent");

        [McpServerTool(Name = "set_perspective_member"), Description("Include (or exclude) one object in a perspective. The object ref is a table/column/measure/hierarchy. Including a TABLE cascades to all its columns/measures/hierarchies. Undoable.")]
        public static Task<SetResult> SetPerspectiveMember(IEngine engine,
            [Description("Perspective ref or name, e.g. 'perspective:Finance'")] string perspectiveRef,
            [Description("Object ref to include/exclude: table:/column:/measure:/hierarchy:")] string objectRef,
            [Description("true to include in the perspective, false to exclude")] bool include)
            => engine.SetPerspectiveMemberAsync(perspectiveRef, objectRef, include, "agent");

        [McpServerTool(Name = "get_perspectives"), Description("List the model's perspectives and the object refs currently shown in each (the objects×perspectives membership matrix). Read-only.")]
        public static Task<PerspectiveInfo[]> GetPerspectives(IEngine engine) => engine.GetPerspectivesAsync();

        [McpServerTool(Name = "list_calculation_groups"), Description("List the model's calculation groups with their precedence and ordered items (each item's DAX expression + dynamic format-string). Read-only. Author with create_calculation_group / create_calculation_item / set_calc_group_precedence / set_calc_item_format_string.")]
        public static Task<CalcGroupInfo[]> ListCalculationGroups(IEngine engine) => engine.ListCalculationGroupsAsync();

        [McpServerTool(Name = "delete_object"), Description("Delete any model object by ref (measure/column/table/hierarchy/relationship/calculation-group/calculation-item/role…). Deleting an absent ref is a net-zero no-op. DAX references to a deleted object are NOT auto-rewritten. They will error; check dependents first. Undoable.")]
        public static Task<SetResult> DeleteObject(IEngine engine, [Description("Object ref, e.g. 'measure:Sales/Old Total' or 'table:Staging'")] string objRef)
            => engine.DeleteObjectAsync(objRef, "agent");

        [McpServerTool(Name = "delete_objects"), Description("Delete many objects in one undoable step (the multi-select delete). Same rules as delete_object: an absent ref is skipped, DAX references are NOT auto-rewritten. One undo restores every object that this call removed. Use delete_object for a single ref.")]
        public static Task<SetResult> DeleteObjects(IEngine engine,
            [Description("Object refs to delete, e.g. ['measure:Sales/Old Total','measure:Sales/Unused']")] string[] objRefs)
            => engine.DeleteObjectsAsync(objRefs, "agent");

        [McpServerTool(Name = "duplicate_object"), Description("Duplicate a measure, calculated/data column, hierarchy, calculation item, table (incl. calculated tables / field parameters), or calculation group (a deep clone). newName optional (collision-safe auto-name if omitted). targetRef optional: a 'table:' ref to clone a MEASURE onto another table, or a calc group's 'table:' ref to clone a CALCULATION ITEM onto another group. Columns/hierarchies stay on their own table; tables duplicate at model scope. Returns the new ref. Undoable.")]
        public static Task<string> DuplicateObject(IEngine engine,
            [Description("Object ref to duplicate")] string objRef,
            [Description("Name for the copy (optional)")] string newName = null,
            [Description("Optional target container: 'table:X' for a measure, a calculation group's 'table:' ref for a calc item. Omit to duplicate beside the original.")] string targetRef = null)
            => engine.DuplicateObjectAsync(objRef, newName, targetRef, "agent");

        [McpServerTool(Name = "get_properties"), Description("List an object's editable properties (the property grid surface). Each: name, displayName, category, kind (string/bool/number/enum), current value, enum options, read-only. Pass 'model:' for model-level settings, or any table/column/measure/relationship/function ref. Pair with set_property.")]
        public static Task<ObjectProperty[]> GetProperties(IEngine engine, [Description("Object ref, e.g. 'model:' or 'column:Sales/Amount'")] string objRef)
            => engine.GetObjectPropertiesAsync(objRef);

        [McpServerTool(Name = "set_property"), Description("Set one property of an object by name (from get_properties). value is the string form: 'true'/'false' for bool, the enum NAME for enum, the number for numeric. Setting 'Name' renames (DAX refs auto-rewritten). Undoable.")]
        public static Task<SetResult> SetProperty(IEngine engine,
            [Description("Object ref")] string objRef,
            [Description("Property name (exactly as returned by get_properties)")] string propertyName,
            [Description("New value, as a string")] string value)
            => engine.SetObjectPropertyAsync(objRef, propertyName, value, "agent");

        [McpServerTool(Name = "set_properties"), Description("Set ONE property to ONE value across MANY objects as a single atomic, undoable change (the multi-select property edit). Same value encoding as set_property. ALL-OR-NOTHING: if any object lacks the property or the value is invalid, NOTHING changes and the error names the object; one undo reverts the whole batch. Use set_property for a single object.")]
        public static Task<SetResult> SetProperties(IEngine engine,
            [Description("Object refs to edit, e.g. ['measure:Sales/Total','measure:Sales/Margin'] (see list_objects / list_measures / list_columns)")] string[] objRefs,
            [Description("Property name (exactly as returned by get_properties), applied to every object")] string propertyName,
            [Description("New value as a string, applied to every object: same encoding as set_property (true/false, enum NAME, number)")] string value)
            => engine.SetObjectPropertiesAsync(objRefs, propertyName, value, "agent");

        [McpServerTool(Name = "get_dependencies"), Description("DAX dependency edges for an object (measure, calculated column/table, calculation item, UDF, column). direction='dependsOn' (default) lists what its DAX consumes (check before editing); direction='dependents' lists what REFERENCES it (check before delete_object/rename_object to see what would dangle). Returns each ref/name/kind; empty when the object has no DAX edges.")]
        public static Task<DependentInfo[]> GetDependencies(IEngine engine,
            [Description("Object ref, e.g. 'measure:Sales/Total Sales', 'column:Sales/Amount', 'function:Utils/fmt'")] string objRef,
            [Description("'dependsOn' or 'upstream' (what it consumes); 'dependents' or 'downstream' (what references it). Default dependsOn.")] string direction = "dependsOn")
        {
            var d = (direction ?? "dependsOn").Trim();
            if (d.Equals("dependents", StringComparison.OrdinalIgnoreCase)
                || d.Equals("downstream", StringComparison.OrdinalIgnoreCase))
                return engine.GetDependentsAsync(objRef);
            if (d.Equals("dependsOn", StringComparison.OrdinalIgnoreCase)
                || d.Equals("upstream", StringComparison.OrdinalIgnoreCase))
                return engine.GetDependenciesAsync(objRef);
            throw new InvalidOperationException($"Unknown direction '{direction}': use dependsOn or upstream (what this object's DAX consumes), or dependents or downstream (what references it).");
        }

        [McpServerTool(Name = "get_lineage"), Description("Read-only LINEAGE GRAPH of the open model (offline, from TOM): every node (data source, table, column, measure) and every directed edge: source→table (resolved from partition M / shared expressions), table→column/measure (contains), dependant→dependency (DAX), and FK→PK (relationship). Use it to trace where a field comes from and what flows from it. NOTE (Phase 1): model-only. Published-report field usage is not yet included (see .Caveat).")]
        public static Task<LineageResult> GetLineage(IEngine engine) => engine.GetLineageAsync();

        [McpServerTool(Name = "unused_objects"), Description("Read-only reverse SAFE-TO-REMOVE sweep (the 'Measure Killer'): every measure/column that NO model object references, a deletion-candidate list. CONSERVATIVE: a column used by a relationship, hierarchy, sort-by, or RLS is never listed. Verdict per item: 'safe' (nothing references it), 'usedByUnusedOnly' (referenced only by other unused/dead objects, surfaced with BlockedBy), 'caution' (a report reading could not rule it out), 'visibleAttribute' (a visible column of a dimension table with no model reference: people query by it in reports, Q&A and Copilot, so a sweep never takes it. That is not a refusal of the person: a reviewed removal still removes it, through add_plan_item kind 'delete_if_unused' + apply_plan, or delete_object). It reads this model's report scope (set_report_scope, check_reports): with no report chosen it is a model-only sweep, so a field used only by a published report still appears as 'safe'; when every chosen report has been checked, fields those reports use are excluded; while any chosen report is still unchecked, every would-be 'safe' item comes back as 'caution'. .Caveat names the basis the list carries.")]
        public static Task<UnusedResult> UnusedObjects(IEngine engine) => engine.UnusedObjectsAsync();

        [McpServerTool(Name = "analyze_reports"), Description("REPORT-AWARE lineage (the safe-to-remove gap-closer): parse one or more LOCAL Power BI report definitions in PBIR format (a PBIP '<Report>.Report' folder, its 'definition' folder, a .pbip file, or a project root) and return which model fields each report uses, PLUS a safe-to-remove sweep that EXCLUDES anything a report uses. This closes the model-only blind spot where a descriptive column a report displays (but no measure references) wrongly looks unused. Report field references are reconciled to the open model BY NAME (the model must be the one the reports bind to). Offline (local files); the cloud 'Get Report Definition' path will reuse the same parser. Legacy .pbix / RDL are not parsed yet (reported as unreadable → result stays model-only with a caveat).")]
        public static Task<ReportAnalysisResult> AnalyzeReports(IEngine engine,
            [Description("Local PBIR report path(s): a '<Report>.Report' folder, a 'definition' folder, a .pbip file, or a PBIP project root.")] string[] paths)
            => engine.AnalyzeReportsAsync(paths);

        [McpServerTool(Name = "remove_safe_objects"), Description("The safe-to-remove sweep's ACT half: delete the verified-safe objects (unused measures/columns) as ONE undoable transaction. The safe set is RECOMPUTED server-side and each item RE-VERIFIED at apply time. An item whose status changed since your scan (something now references it) is SKIPPED with the reason, never deleted stale. refs (optional) narrows the sweep to those candidates from unused_objects/analyze_reports; omit to remove every currently-safe item. A 'visibleAttribute' item is never swept, named or not; its skip reason names the reviewed route that does remove it. reportPaths (optional, local PBIR, same forms as analyze_reports) makes the verification report-aware, so a field a report displays is never removed. Returns removed[] / skipped[{ref,reason}] / count; writes ONE audit record with the evidence; undo_change reverses the whole sweep in one step.")]
        public static Task<RemoveSafeReport> RemoveSafeObjects(IEngine engine,
            [Description("Optional candidate refs (e.g. 'measure:Sales/Old Total'); omit/empty = every currently verified-safe item. A 'visibleAttribute' ref skips here; the reason names the route that removes it.")] string[] refs = null,
            [Description("Optional local PBIR report path(s): verification then also requires each item to be unused by these reports")] string[] reportPaths = null)
            => engine.RemoveSafeObjectsAsync(refs, reportPaths, "agent");

        [McpServerTool(Name = "list_report_scope"), Description("Which published reports and local report folders this model's answers are checked against, and what came of reading each one. State per report: checked | couldNotBeFullyChecked | notChecked | needsChecking (the model changed after the report was read). The CHOICES are saved with the model and come back on reopen as notChecked, because a saved choice is not evidence that anything was read; the CHECK RESULTS live in this session. impact_assessment, unused_objects, remove_safe_objects and the apply-time recheck of a delete_if_unused plan item all read this same scope. Free and read-only.")]
        public static Task<Lineage.ReportScopeResult> ListReportScope(IEngine engine) => engine.ListReportScopeAsync();

        [McpServerTool(Name = "set_report_scope"), Description("Choose the reports this model is checked against. Replaces the whole list. Each choice is either kind='published' (with workspaceId, reportId and name, from list_workspaces + list_reports) or kind='local' (with path: a Power BI project (.pbip) or its .Report folder; PBIX is not supported). Saves the choice beside the model; nothing is read here, so every choice comes back as notChecked until check_reports runs. Read the reports with check_reports.")]
        public static Task<Lineage.ReportScopeResult> SetReportScope(IEngine engine,
            [Description("The whole chosen set. An empty array clears the list.")] Lineage.ReportScopeChoice[] choices)
            => engine.SetReportScopeAsync(choices, "agent");

        [McpServerTool(Name = "check_reports"), Description("Read the reports chosen for this model and keep what each one uses, so impact_assessment, unused_objects, remove_safe_objects and the apply-time recheck all answer from the same reading. ids empty = every chosen report. Local report folders need no sign-in. A PUBLISHED report needs consent=true: reading its definition asks Fabric for permission that can also edit reports, even though Semanticus only reads it. A report that cannot be read is reported as couldNotBeFullyChecked with the reason, never silently dropped, so 'no use found' is never overstated.")]
        public static Task<Lineage.ReportScopeResult> CheckReports(IEngine engine,
            [Description("Chosen report ids to read; empty = every report chosen for this model")] string[] ids = null,
            [Description("Must be true to read a PUBLISHED report: the sign-in asks for permission that can also edit reports. Semanticus uses it only to read.")] bool consent = false,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode. Published reports only.")] string authMode = null,
            [Description("Optional Entra tenant id or domain. Published reports only.")] string tenantId = null,
            CancellationToken cancellationToken = default)
            => engine.CheckReportsAsync(ids, consent, authMode, tenantId, null, "agent", cancellationToken);

        [McpServerTool(Name = "list_reports"), Description("List the published reports in a Fabric/Power BI workspace (id, name, datasetId, reportType, webUrl) via the non-admin per-workspace path. The datasetId tells you which reports bind to a given semantic model. Match it to the open model to find the reports that use it. reportType 'PaginatedReport' = RDL (its field usage can't be parsed). Live read against api.powerbi.com using your Entra identity. Read-only. Pair with analyze_cloud_reports to close the safe-to-remove blind spot using real published-report usage.")]
        public static Task<CloudReport[]> ListReports(IEngine engine,
            [Description("Workspace (group) id")] string workspaceId,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            [Description("Optional Entra tenant id or domain (cross-tenant / guest override; default = the signed-in tenant)")] string tenantId = null,
            CancellationToken cancellationToken = default)
            => engine.ListReportsAsync(workspaceId, authMode, tenantId, "agent", cancellationToken);

        [McpServerTool(Name = "analyze_cloud_reports"), Description("REPORT-AWARE safe-to-remove over CLOUD (published) reports: the Measure-Killer headline against the live service. Fetches each report's PBIR via the Fabric 'Get Report Definition' API, reconciles its field usage to the OPEN model BY NAME, and recomputes safe-to-remove EXCLUDING any field a report uses. reportIds empty = every non-paginated report in the workspace. Behaviour is READ-ONLY (Semanticus never modifies the report), BUT getDefinition requires a WRITE-CAPABLE Fabric scope (Item.ReadWrite.All / Report.ReadWrite.All) + the Contributor role, so you MUST pass consent=true to acknowledge that (tell the user first). Per-report failures (paginated/RDL, a report blocked by an encrypted sensitivity label, a download error) are reported as unreadable with a reason, never silently dropped, so 'safe' is never overstated. A model must be open and must be the one these reports bind to.")]
        public static Task<ReportAnalysisResult> AnalyzeCloudReports(IEngine engine,
            [Description("Workspace (group) id the reports live in")] string workspaceId,
            [Description("Report ids to analyze; empty = every non-paginated report in the workspace")] string[] reportIds,
            [Description("Must be true to acknowledge getDefinition needs a write-capable Fabric scope + Contributor role (behaviour stays read-only)")] bool consent = false,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            [Description("Optional Entra tenant id or domain (cross-tenant / guest override; default = the signed-in tenant)")] string tenantId = null,
            CancellationToken cancellationToken = default)
            => engine.AnalyzeCloudReportsAsync(workspaceId, reportIds, consent, authMode, tenantId, runId: null, origin: "agent", cancellationToken: cancellationToken);

        [McpServerTool(Name = "script_objects"), Description("Script one or MANY objects to text (read-only, never mutates). format: 'dax' (annotated expressions for measures/calc columns/calc tables/calc items/UDFs), 'tmdl' (per-object TMDL: the model's native, readable on-disk format; the preferred export), or 'tmsl' (createOrReplace JSON; TMSL's unit is the table/role, so a child object scripts its containing table, deduped to containers). Pass several refs to capture a whole batch (e.g. all of a table's measures) in one document. Unresolvable refs are skipped.")]
        public static Task<string> ScriptObjects(IEngine engine,
            [Description("Object refs to script, e.g. ['measure:Sales/Total','measure:Sales/Margin']")] string[] refs,
            [Description("Output format: dax | tmdl | tmsl (default dax)")] string format = "dax")
            => engine.ScriptObjectsAsync(refs, format);

        [McpServerTool(Name = "apply_dax_script"), Description("Apply an edited DAX script (the 'dax' output of script_objects, with '// @object <ref>' headers) back to the model: each block's expression is set on its object in ONE undoable batch. Lets you bulk-edit many measures/calc columns/calc items in one document and commit together. All-or-nothing: a missing ref or invalid DAX refuses the whole script and writes nothing. Keep each block's '@base:' marker on its header. When someone changed an object after the script was made: a block you did not edit is left as the object is now (leftAsIs[]); a block you edited too is a conflict and nothing is written for it (conflicts[]); lines of two versions are never mixed. notes[] says so in plain words. To write your version of a conflict anyway, call again with the same script and applyAnyway=[the refs from conflicts[]]: only those blocks are applied, as written. Returns {applied[], skipped[], leftAsIs[], conflicts[], notes[]}.")]
        public static Task<ApplyScriptResult> ApplyDaxScript(IEngine engine,
            [Description("The DAX script text (with // @object <ref> headers, as produced by script_objects format='dax')")] string script,
            [Description("Optional. Refs from a previous result's conflicts[] to write anyway. When given, ONLY those blocks are applied, as written.")] string[] applyAnyway = null)
            => engine.ApplyDaxScriptAsync(script, "agent", applyAnyway);

        [McpServerTool(Name = "apply_tmdl"), Description("Apply edited output from script_objects(format='tmdl') back to the model IN PLACE. Existing table/role documents and individually scripted measures round-trip through TOM's native partial-update path in ONE undoable batch. Measure ownership is pinned by the emitted // @object ref; changing its header/parent is refused (use rename_object/create_measure for structural changes). A brand-new top-level object is skipped (use its typed create op). Keep each block's '@base:' marker on its header. When someone changed an object after the script was made: a block you did not edit is left as the object is now (leftAsIs[]); a block you edited too is a conflict and nothing is written for it (conflicts[]); lines of two versions are never mixed. notes[] says so in plain words. To write your version of a conflict anyway, call again with the same script and applyAnyway=[the refs from conflicts[]]: only those blocks are applied, as written. Returns {applied[], skipped[], leftAsIs[], conflicts[], notes[]}.")]
        public static Task<ApplyScriptResult> ApplyTmdl(IEngine engine,
            [Description("TMDL text produced by script_objects: top-level table/role documents or sentinel-owned measure blocks")] string script,
            [Description("Optional. Refs from a previous result's conflicts[] to write anyway. When given, ONLY those blocks are applied, as written.")] string[] applyAnyway = null)
            => engine.ApplyTmdlScriptAsync(script, "agent", applyAnyway);

        [McpServerTool(Name = "set_compatibility_level"), Description("Raise the model's compatibility level (a ONE-WAY upgrade). Needed for newer features. DAX user-defined functions require 1702+. Refuses to lower it. Undoable within the session, but re-deploying an upgraded model to an older runtime may fail.")]
        public static Task<SetResult> SetCompatibilityLevel(IEngine engine,
            [Description("Target compatibility level, e.g. 1702")] int level)
            => engine.SetCompatibilityLevelAsync(level, "agent");

        [McpServerTool(Name = "rename_object"), Description("Rename a measure/column/table. ALL DAX references to it are automatically rewritten (safe rename). Returns the new ref. Undoable. (Report-layer bindings outside the model can't be updated. Review for renames used in reports.)")]
        public static Task<RenameResult> RenameObject(IEngine engine,
            [Description("Object ref, e.g. 'measure:Sales/Sales Amt'")] string objRef,
            [Description("New name")] string newName,
            [Description("Optional session targeting: the sessionId you read for the model you mean to edit (create_model / open_model / open_local / open_live return it; model_overview reads it back). If the live session is no longer that one, the rename is refused before anything is written. Omit it to rename in whatever model is currently open (the existing behavior).")] string sessionId = null)
            // T172: the same fence as update_measure. See the note there for why the engine, not a preflight, owns it.
            => engine.RenameObjectAsync(objRef, newName, "agent", sessionId);

        [McpServerTool(Name = "save_model"), Description("Save the model. Omit path to overwrite the file or folder you opened. Pass a folder path to save as TMDL (tabular model definition language), or a .bim file path. format defaults to TMDL; when overwriting, a .bim stays a .bim. If the files on disk changed after this model was opened, the save is refused unless overwrite is true. A model that has never been saved is never written into another model or over an existing .bim: it goes into a new folder (or a new .bim) next to it, and the result's Note says where.")]
        public static Task<SaveResult> SaveModel(IEngine engine,
            [Description("Target path. Omit or pass null to overwrite the source.")] string path = null,
            [Description("Format: TMDL (default), bim, or folder. Ignored when overwriting a .bim source.")] string format = null,
            [Description("true = replace files that changed on disk after this model was opened; default false refuses that overwrite")] bool overwrite = false)
            => engine.SaveAsync(path, format, overwrite, "agent");

        [McpServerTool(Name = "undo_change"), Description("Undo the most recent change on the shared session: steps back one entry on the model's SINGLE undo timeline. A whole batch (apply_plan / apply_safe_fixes / a generator) undoes as ONE step. The timeline is shared across both drivers, so this reverts the last change regardless of who made it (your edits or the human's in the VS Code UI). The revert is live. It broadcasts to the UI immediately. toEntry (optional): an entry id from get_plan_history; undoes that change and every later one in ONE step, one broadcast. Refused with a sentence when the entry is from before this session or already undone. Returns CanUndo / CanRedo / AtCheckpoint / Steps / RedoSteps. Use it to back out a change you just applied.")]
        public static Task<UndoState> UndoChange(IEngine engine, [Description("Optional: the get_plan_history entry id to undo back to, including it. Omit for one step.")] string toEntry = null) =>
            string.IsNullOrWhiteSpace(toEntry) ? engine.UndoAsync("agent") : engine.UndoToEntryAsync(toEntry, "agent");

        [McpServerTool(Name = "redo_change"), Description("Re-apply the change most recently undone with undo_change: steps forward one entry on the shared undo timeline. Live (broadcasts to the UI). toEntry (optional): an undone entry id from get_plan_history; redoes every undone change up to and including it in ONE step. Returns CanUndo / CanRedo / AtCheckpoint / Steps / RedoSteps. Making a NEW edit after an undo clears the redo stack.")]
        public static Task<UndoState> RedoChange(IEngine engine, [Description("Optional: the undone get_plan_history entry id to redo up to, including it. Omit for one step.")] string toEntry = null) =>
            string.IsNullOrWhiteSpace(toEntry) ? engine.RedoAsync("agent") : engine.RedoToEntryAsync(toEntry, "agent");

        [McpServerTool(Name = "get_plan_history"), Description("The model's History, newest first: every edit with who made it (By: you, assistant, semanticus, workflow), what it changed with before and after values (secrets and Power Query text hidden), and what it served (Cause: a plan item, workflow run, draft or task, whose edits nest under one parent row); Plan decisions that changed nothing; saved and restored versions; finished workflow runs with their report. Each row says whether it is Undoable (pass its Id to undo_change toEntry), how many later changes that would also take back (LaterCount), its Verified word, and whether it is from before this session (never undoable). Filters: by, objectRef (one object), before (a row's When, to page back), limit (default 50). Kept=false means the model has nowhere to keep history yet (save it) and it lasts this session. Free. Read-only.")]
        public static Task<PlanHistoryView> GetPlanHistory(IEngine engine,
            [Description("Optional: only rows by 'you', 'assistant', 'semanticus' or 'workflow'.")] string by = null,
            [Description("Optional: only rows that touched this object, e.g. 'measure:Sales/Total'.")] string objectRef = null,
            [Description("Optional: only rows older than this When (ISO time from an earlier page).")] string before = null,
            [Description("Optional: how many rows, default 50, at most 500.")] int limit = 50) =>
            engine.GetPlanHistoryAsync(by, objectRef, before, limit);

        // ---- Best Practice Analyzer (general-purpose) ------------------------------------------

        /// <summary>The BPA counts: bpa_scan with detail='summary'.</summary>
        public static async Task<BpaSummary> BpaScanSummary(IEngine engine) => BpaSummary.From(await engine.BpaScanAsync());

        [McpServerTool(Name = "bpa_scan"), Description("Check the model against the Best Practice Analyzer: general modelling rules for performance, DAX, formatting, naming and layout. Both details carry the Best practice letter, A to F, as bestPractice with bestPracticeWhy (ai_readiness_scan's grade is the other letter, AI readiness). The rule: " + BpaGrading.Rule + " It uses the bundled Power BI rule set plus any custom rules saved on the model (load_rules and reset_rules with system='bpa'). detail='summary' returns counts only: rule count, violation count, auto-fixable count, rule errors, orphaned waivers, and counts by category, rule and severity. It is the lighter first call on a large model. detail='full' (the default) also returns the violations, each with rule, category, severity, object, message and whether it can be fixed automatically (CanAutoFix). The filters narrow only the returned violations, and the counts always cover the whole model. A filter given with detail='summary' is refused, because a summary has no list to narrow. For AI readiness use ai_readiness_scan. Read-only.")]
        public static async Task<object> BpaScanOp(IEngine engine,
            [Description("full (the default: counts plus the violations) or summary (counts only)")] string detail = "full",
            [Description("Only return violations in this BPA category. Null/empty = all.")] string category = null,
            [Description("Only return violations of this BPA rule id. Null/empty = all.")] string ruleId = null,
            [Description("Only return violations at this severity or higher: Info|Warning|Error. Null/empty = all.")] string severityMin = null,
            [Description("true = only return deterministically auto-fixable violations.")] bool autoFixableOnly = false,
            [Description("Cap the returned violations to this many (0 = no cap, the default).")] int maxViolations = 0)
        {
            if (!IsSummary(detail)) return await BpaScan(engine, category, ruleId, severityMin, autoFixableOnly, maxViolations);
            RefuseSummaryFilter(!string.IsNullOrWhiteSpace(category) || !string.IsNullOrWhiteSpace(ruleId)
                || !string.IsNullOrWhiteSpace(severityMin) || autoFixableOnly || maxViolations > 0);
            return await BpaScanSummary(engine);
        }

        /// <summary>bpa_scan with detail='full': the scorecard with its violations filtered.</summary>
        public static async Task<BpaScorecard> BpaScan(IEngine engine,
            string category = null,
            string ruleId = null,
            string severityMin = null,
            bool autoFixableOnly = false,
            int maxViolations = 0)
        {
            var c = await engine.BpaScanAsync();
            IEnumerable<BpaViolation> vs = c.Violations;
            if (!string.IsNullOrWhiteSpace(category)) vs = vs.Where(v => string.Equals(v.Category, category, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(ruleId)) vs = vs.Where(v => string.Equals(v.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(severityMin))
            {
                // Fail loudly on an unrecognised value instead of silently returning EVERYTHING, which would be
                // the opposite of the intended narrowing. Mirrors ai_readiness_scan's severityMin.
                var min = BpaSevRank(severityMin);
                if (min == 0)
                    throw new ArgumentException($"severityMin '{severityMin}' is not recognised; use Info, Warning, or Error.", nameof(severityMin));
                vs = vs.Where(v => v.Severity >= min);
            }
            if (autoFixableOnly) vs = vs.Where(v => v.CanAutoFix);
            if (maxViolations > 0) vs = vs.Take(maxViolations);
            c.Violations = vs.ToArray();
            return c;
        }

        /// <summary>The BPA severity scale as a rank (the corpus convention: 1=Info, 2=Warning, 3=Error), or 0
        /// for a word this scale does not have. 0 is what makes an unrecognised value a refusal rather than a
        /// filter that matches everything.</summary>
        private static int BpaSevRank(string word) => (word ?? "").Trim().ToLowerInvariant() switch
        {
            "info" => 1,
            "warning" => 2,
            "error" => 3,
            _ => 0,
        };

        [McpServerTool(Name = "load_rules"), Description("Save custom rules on the model, from a file path, an http(s) URL or inline JSON. They travel with the model, the load is undoable, and they add to the built-in rules without ever replacing them. replace=false merges with the model's existing custom rules by id; replace=true makes them exactly this set. system='bpa' takes the standard BPARules.json schema: an array of {ID, Name, Category, Severity, Scope, Expression, FixExpression?, Description}. system='air' takes AI-readiness rules: {ID, Name?, Category (an existing readiness category: Naming, Descriptions, Synonyms, Relationships, Visibility, Formatting, CopilotLimits, DataAgentConfig or BestPractice), Severity? (Info, Medium, High or Critical; default Medium), Scope (the BPA scope words, such as Measure, Column, Table), Expression (the violation test, in the BPA expression language), AppliesTo? (the population; an empty population leaves the rule dormant), Message?, Description?, FixKind? (None, AiContent or Proposal; SafeFix is refused)}. Readiness rules are checked against the open model before anything is written, a built-in rule id is refused, and a custom rule cannot set a hard gate. Preview first with validate_rule, list what is saved with get_custom_rules, clear with reset_rules. Returns the new rule counts.")]
        public static async Task<object> LoadRules(IEngine engine,
            [Description("Which rule system: 'bpa' (Best Practice Analyzer) or 'air' (AI readiness)")] string system,
            [Description("A file path, an http(s) URL, or inline JSON array of rules")] string source,
            [Description("false = merge with the model's existing custom rules; true = replace them")] bool replace = false)
            => RuleSystem(system) == "bpa"
                ? await LoadBpaRules(engine, source, replace)
                : (object)await LoadReadinessRules(engine, source, replace);

        [McpServerTool(Name = "reset_rules"), Description("Remove every custom rule of one system from the model, so scans use only the built-in rules again: the bundled Power BI set for system='bpa', the built-in readiness rules for system='air'. Undoable.")]
        public static async Task<object> ResetRules(IEngine engine,
            [Description("Which rule system: 'bpa' (Best Practice Analyzer) or 'air' (AI readiness)")] string system)
            => RuleSystem(system) == "bpa" ? await ResetBpaRules(engine) : (object)await ResetReadinessRules(engine);

        /// <summary>load_rules with system='bpa'.</summary>
        public static Task<BpaRulesInfo> LoadBpaRules(IEngine engine, string source, bool replace = false)
            => engine.LoadBpaRulesAsync(source, replace, "agent");

        /// <summary>reset_rules with system='bpa'.</summary>
        public static Task<BpaRulesInfo> ResetBpaRules(IEngine engine) => engine.ResetBpaRulesAsync("agent");

        /// <summary>The rule system a system argument names: "bpa" or "air" ('readiness' is read as 'air', the word
        /// validate_rule uses). One parse for every merged tool, so one refusal sentence.</summary>
        private static string RuleSystem(string system) => (system ?? "").Trim().ToLowerInvariant() switch
        {
            "bpa" => "bpa",
            "air" or "readiness" => "air",
            _ => throw new ArgumentException("system must be 'bpa' (Best Practice Analyzer) or 'air' (AI readiness).", nameof(system)),
        };

        /// <summary>Whether a detail argument asks for the counts-only shape. Empty reads as the default, full.</summary>
        private static bool IsSummary(string detail) => (detail ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "full" => false,
            "summary" => true,
            _ => throw new ArgumentException("detail must be 'full' or 'summary'.", nameof(detail)),
        };

        /// <summary>A summary has no list to narrow, so a filter beside detail='summary' is refused rather than
        /// silently ignored (the same stance severityMin takes on a word it does not know).</summary>
        private static void RefuseSummaryFilter(bool anyFilter)
        {
            if (anyFilter)
                throw new ArgumentException("A filter was given with detail='summary', but a summary has no list to narrow. Drop the filter, or use detail='full'.");
        }

        // ---- Custom rule authoring (both kinds) --------------------------------------------------

        [McpServerTool(Name = "validate_rule"), Description("Compile + TEST-RUN a custom rule (or a whole set) against the open model WITHOUT saving: the authoring preview for both rule kinds. kind='bpa' or 'readiness'; rules = inline JSON (one rule object or an array, the same schema the matching load op accepts). Per rule you get: compile errors from the real expression parser (with the failing position), the would-be Applicable population, the violation count, the first few flagged object names, and a dormant flag (empty population = the rule would never move a score). Save with load_rules (system='bpa' or 'air') once valid.")]
        public static Task<RuleValidationResult> ValidateRule(IEngine engine,
            [Description("Which rule system: 'bpa' or 'readiness'")] string kind,
            [Description("Inline JSON: one rule object or an array of rules")] string rules)
            => engine.ValidateRuleAsync(kind, rules);

        [McpServerTool(Name = "get_custom_rules"), Description("List the custom (user-authored) rules embedded on the model, both kinds: BPA rules (BestPracticeAnalyzer annotation) and AI-readiness rules (Semanticus_ReadinessRules annotation), plus the valid readiness Category list and the supported Scope vocabulary. Read-only; the manage view behind the Studio 'Custom rules' panels.")]
        public static Task<CustomRulesInfo> GetCustomRules(IEngine engine) => engine.GetCustomRulesAsync();

        /// <summary>load_rules with system='air'.</summary>
        public static Task<ReadinessRulesInfo> LoadReadinessRules(IEngine engine, string source, bool replace = false)
            => engine.LoadReadinessRulesAsync(source, replace, "agent");

        /// <summary>reset_rules with system='air'.</summary>
        public static Task<ReadinessRulesInfo> ResetReadinessRules(IEngine engine) => engine.ResetReadinessRulesAsync("agent");

        // ---- AI-readiness ("BPA for AI") -------------------------------------------------------

        /// <summary>The readiness counts: ai_readiness_scan with detail='summary'.</summary>
        public static async Task<ReadinessSummary> AiReadinessSummary(IEngine engine)
            => ReadinessSummary.From(await engine.AiReadinessScanAsync());

        [McpServerTool(Name = "ai_readiness_scan"), Description("Score the open model A to F for AI readiness, meaning how well Copilot and Fabric data agents can use it. It returns the overall grade and 0-100 score, per-category scores, coverage KPIs and the reasons any hard gate holds the grade down. detail='summary' adds the total finding count and counts by severity, fix kind and top rule, with no findings list, and stays small on any model. detail='full' (the default) also returns the findings in priority order, each tagged SafeFix (a deterministic fix exists), AiContent (you write it) or Proposal (a person should review it). live=true adds the live cardinality rules: it reads each column's distinct-value count (COLUMNSTATISTICS) from the connected model and checks the Copilot index limits (SCALE-QNA-INDEX, the 5,000,000-value index ceiling, and SCALE-HICARD-COLUMN). live=true needs a live connection (connect_xmla or connect_local) and is refused without one. The filters narrow only the returned findings; scores and counts always cover the whole model, and a filter given with detail='summary' is refused. Read-only.")]
        public static async Task<object> AiReadinessScanOp(IEngine engine,
            [Description("full (the default: scores plus the findings) or summary (scores and counts only)")] string detail = "full",
            [Description("true = also run the live cardinality rules on the connected model (needs a live connection)")] bool live = false,
            [Description("Only return findings in this category (e.g. 'Naming'). Null/empty = all categories.")] string category = null,
            [Description("Only return findings at this severity or higher: Info|Medium|High|Critical. Null/empty = all.")] string severityMin = null,
            [Description("Cap the returned findings to this many (0 = no cap, the default). Capping is opt-in so findings are never hidden silently.")] int maxFindings = 0)
        {
            var summary = IsSummary(detail);
            if (summary)
                RefuseSummaryFilter(!string.IsNullOrWhiteSpace(category) || !string.IsNullOrWhiteSpace(severityMin) || maxFindings > 0);
            var card = live ? await engine.AiReadinessScanLiveAsync() : await engine.AiReadinessScanAsync();
            return summary ? ReadinessSummary.From(card) : FilterFindings(card, category, severityMin, maxFindings);
        }

        /// <summary>ai_readiness_scan with detail='full' and live=false: the scorecard with its findings filtered.</summary>
        public static async Task<Scorecard> AiReadinessScan(IEngine engine, string category = null, string severityMin = null, int maxFindings = 0)
            => FilterFindings(await engine.AiReadinessScanAsync(), category, severityMin, maxFindings);

        /// <summary>Narrow the returned findings of either scorecard (offline or live). Scores and counts are left
        /// whole, so they always describe the full model.</summary>
        private static Scorecard FilterFindings(Scorecard c, string category, string severityMin, int maxFindings)
        {
            IEnumerable<ReadinessFinding> fs = c.Findings;
            if (!string.IsNullOrWhiteSpace(category))
                fs = fs.Where(f => string.Equals(f.Category, category, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(severityMin))
            {
                var min = ReadinessSummary.SevRank(severityMin);
                // Fail loudly on an unrecognised value (e.g. 'Low', this scale has no Low) instead of silently
                // returning EVERYTHING, which would be the opposite of the intended narrowing.
                if (min == 0)
                    throw new ArgumentException($"severityMin '{severityMin}' is not recognised; use Info, Medium, High, or Critical.", nameof(severityMin));
                fs = fs.Where(f => ReadinessSummary.SevRank(f.Severity) >= min);
            }
            if (maxFindings > 0) fs = fs.Take(maxFindings);
            c.Findings = fs.ToArray();
            return c;
        }

        [McpServerTool(Name = "apply_safe_fixes"), Description("Apply every deterministic fix for one rule system as one undoable batch, then return what changed and the new scorecard. system='air' applies the AI-readiness safe fixes: hide foreign-key columns, set SummarizeBy to None on key, relationship and identifier columns, set geographic data categories, and mark each dimension's row label. It renames nothing and writes no content. system='bpa' applies every Best Practice Analyzer auto-fix, repeating until a pass changes nothing, so a fix that creates another finding is cleared in the same call. For system='bpa', read 'unfixed' before you report the model clean: it lists each auto-fixable finding still present with the reason it survived (including one left alone because the person accepted the same change under another rule), and 'remaining' is how many. The batch did everything it said only when remaining is 0. A data type change, and a key on a table that already has one, are left for the person: they stay in 'unfixed' with the reason, and apply_fix still makes them one at a time. Content findings, and naming findings that need a new name, are left alone; get_fix_prompt covers those. For the readiness fixes plus a queue of content to write, use make_model_ai_ready.")]
        public static async Task<object> ApplySafeFixes(IEngine engine,
            [Description("Which rule system: 'bpa' (Best Practice Analyzer) or 'air' (AI readiness)")] string system)
            => RuleSystem(system) == "bpa" ? await engine.BpaFixAllAsync("agent") : (object)await engine.ApplySafeFixesAsync("agent");

        [McpServerTool(Name = "apply_fix"), Description("Apply the deterministic fix for one finding on one object, as one undoable change. system='bpa' fixes a Best Practice Analyzer violation that bpa_scan marks CanAutoFix, for example a bi-directional relationship made single, or SummarizeBy set to None on a key column. system='air' fixes an AI-readiness SafeFix finding: VIS-FK (hide a foreign-key column), FMT-SUMMARIZE and SUMMARIZE-DIMENSION (SummarizeBy None), CAT-GEO (geographic data category), DIM-PRIMARY-NAME (mark the column that names each row of a dimension as its row label). A finding with no deterministic fix is refused; get its instruction from get_fix_prompt with the same system, rule and object.")]
        public static Task<SetResult> ApplyFix(IEngine engine,
            [Description("Which rule system: 'bpa' (Best Practice Analyzer) or 'air' (AI readiness)")] string system,
            [Description("Rule id from bpa_scan or ai_readiness_scan, e.g. 'VIS-FK' for air")] string ruleId,
            [Description("Object ref the finding is on")] string objRef)
            => RuleSystem(system) == "bpa" ? engine.BpaFixAsync(ruleId, objRef, "agent") : engine.ApplyFixAsync(ruleId, objRef, "agent");

        [McpServerTool(Name = "waive_finding"), Description("WAIVE (accept) a finding so it stops counting against the score, for findings you've consciously decided not to fix (e.g. 'we keep these unused columns'). system: 'bpa' or 'air'. objRef set = waive THIS instance; objRef empty/'*' = waive the ENTIRE rule, every instance model-wide. A reason is REQUIRED: a waiver is an audited decision (stored with who+when on the model), never a silent suppression. The finding is still surfaced (tagged 'waived' with its reason) and the scorecard reports a waived count, so the grade is honest. Hard gates (Copilot index ceiling, >50% measures undescribed) are NOT lifted by waivers. Persisted on the model (undoable, travels with it); per-instance BPA waivers also write Tabular Editor's BestPracticeAnalyzer_IgnoreRules so TE3 honours them. Re-scan to see the score move.")]
        public static Task<SetResult> WaiveFinding(IEngine engine,
            [Description("Which system the rule belongs to: 'bpa' or 'air'")] string system,
            [Description("Rule id (e.g. 'UNNECESSARY_COLUMNS' for bpa, 'NAME-MEASURE' for air)")] string ruleId,
            [Description("Object ref to waive (e.g. 'column:Sales/Foo'); empty or '*' = waive the whole rule model-wide")] string objRef,
            [Description("Why this finding is accepted: required, stored for audit")] string reason)
            => engine.WaiveFindingAsync(system, ruleId, objRef, reason, "agent");

        [McpServerTool(Name = "unwaive_finding"), Description("Remove a waiver so the finding counts against the score again (re-instates it). system: 'bpa' or 'air'. objRef set = un-waive that instance; empty/'*' = remove the rule-level waiver. Never gated (it makes the model more compliant). Undoable; also clears Tabular Editor's ignore annotation for a per-instance BPA waiver.")]
        public static Task<SetResult> UnwaiveFinding(IEngine engine,
            [Description("'bpa' or 'air'")] string system,
            [Description("Rule id")] string ruleId,
            [Description("Object ref, or empty/'*' for the rule-level waiver")] string objRef)
            => engine.UnwaiveFindingAsync(system, ruleId, objRef, "agent");

        [McpServerTool(Name = "list_waivers"), Description("List every accepted (waived) finding on the model: system, rule id, object ref (null = rule-level/model-wide), the reason, who waived it and when. The audit trail behind the scorecard's waived count. This is the RAW store, so it can include waivers whose rule id is no longer in the loaded rule set: those accept nothing, and bpa_scan and ai_readiness_scan report them as OrphanedWaivers with a count. Compare the two before you trust a waiver.")]
        public static Task<Semanticus.Analysis.WaiverRecord[]> ListWaivers(IEngine engine) => engine.ListWaiversAsync();

        [McpServerTool(Name = "get_fix_prompt"), Description("Get a ready-made instruction for fixing one finding that has no deterministic fix: which tool to call and what to write, with the rule and the object's context. system='bpa' covers Best Practice Analyzer violations. system='air' covers AI-readiness content findings such as DESC-MEASURE, NAME-COLUMN, FMT-MEASURE and SYN-FIELD, and includes grounding (DAX, table, sibling names). Read-only; it changes nothing.")]
        public static Task<FixPrompt> GetFixPrompt(IEngine engine,
            [Description("Which rule system: 'bpa' (Best Practice Analyzer) or 'air' (AI readiness)")] string system,
            [Description("Rule id from bpa_scan or ai_readiness_scan, e.g. 'DESC-MEASURE' for air")] string ruleId,
            [Description("Object ref the finding is on")] string objRef)
            => RuleSystem(system) == "bpa" ? engine.BpaGetFixPromptAsync(ruleId, objRef) : engine.GetFixPromptAsync(ruleId, objRef);

        [McpServerTool(Name = "get_grounding"), Description("Get grounding context for an object (name, table, DAX expression, format string, existing description, sibling names) so YOU can author a high-quality business description or a clearer name. Call this before set_description.")]
        public static Task<GroundingBundle> GetGrounding(IEngine engine,
            [Description("Object ref, e.g. 'measure:Sales/Total Sales' or 'column:Customer/City'")] string objRef)
            => engine.GetGroundingAsync(objRef);

        [McpServerTool(Name = "validate_dax"), Description("Validate a DAX expression against the OPEN model BEFORE you write it (create_measure/update_measure). Catches unbalanced brackets (error) and unknown table/column/measure references (warning), with line:column. Offline/read-only, no live engine needed. By default valid=false only on a structural error. strict=true judges it the way a DAX write does: every warning, such as a missing column, is an error and valid is false.")]
        public static Task<DaxValidation> ValidateDax(IEngine engine,
            [Description("The DAX expression to check, e.g. \"CALCULATE ( SUM ( Sales[Amount] ), Sales[Year] = 2024 )\"")] string expression,
            [Description("true = treat every warning (a missing column or measure, an arity problem) as an error, the same verdict a write would give. Default false.")] bool strict = false)
            => engine.ValidateDaxAsync(expression, strict);

        [McpServerTool(Name = "set_description"), Description("Set an object's description: a measure, table, column, hierarchy, perspective, calculation item or any other object that has one. Keep it <=200 chars and front-loaded: Copilot truncates. Applies as a reviewable, undoable change tagged as the agent. Use after get_grounding.")]
        public static Task<SetResult> SetDescription(IEngine engine,
            [Description("Object ref")] string objRef,
            [Description("Business description, <=200 chars, front-loaded")] string text)
            => engine.SetDescriptionAsync(objRef, text, "agent");

        [McpServerTool(Name = "make_model_ai_ready"), Description("Applies every deterministic AI-readiness safe fix (the same set as apply_safe_fixes) as one undoable batch, then returns the new scorecard plus a prioritized work queue of up to maxQueue AI-content findings (accepted ones left out, and so is any change the person declined by accepting another finding about the same thing; do not make a change that is not in the queue), each with its grounding (DAX, table, sibling names). It writes no descriptions, names or synonyms itself: the queue is for you to author with set_description, rename_object or set_synonyms, or to stage for review with propose_plan.")]
        public static Task<ReadinessPlan> MakeModelAiReady(IEngine engine,
            [Description("Max AI-content items to return with grounding (default 40)")] int maxQueue = 40)
            => engine.MakeAiReadyAsync("agent", maxQueue);

        [McpServerTool(Name = "set_measure_format"), Description("Set a measure's STATIC format string (e.g. '0.0%', '\\$#,0', '#,0'). Deterministic, undoable. For a DYNAMIC (per-filter-context) format that switches by magnitude/sign/currency, use set_measure_format_expression. Browse curated strings with list_format_templates.")]
        public static Task<SetResult> SetMeasureFormat(IEngine engine,
            [Description("Measure ref, e.g. 'measure:Sales/Total Sales'")] string objRef,
            [Description("Format string")] string formatString)
            => engine.SetMeasureFormatAsync(objRef, formatString, "agent");

        [McpServerTool(Name = "list_format_templates"), Description("Browse the curated NUMBER-format-string template library (colour is report-side, out of scope): 86 templates across 17 categories (whole numbers, decimals, percentages, currency, scaled K/M/B, signed/variance, Unicode change-indicators, accounting, dates, times, durations, KPI glyphs, ratios, basis points, custom units, blank/zero, scientific). Each has an id, category, name, kind ('static' literal | 'dynamic' DAX expression), the format string, a sampleInput->exampleOutput, a 'common' flag (~20 pinned), and any credit/pack. Apply a STATIC template with set_measure_format (measure) or set_calc_item_format_string (a quoted literal on a calc item); apply a DYNAMIC one with set_measure_format_expression (measure) or set_calc_item_format_string (raw expression on a calc item). Read-only; works with no model open. Use this instead of guessing glyphs / scaling-commas.")]
        public static Task<FormatTemplateInfo[]> ListFormatTemplates(IEngine engine,
            [Description("Optional category filter (equals or contains, case-insensitive), e.g. 'Percentages' or 'change'")] string category = null,
            [Description("Optional fuzzy search over id/name/category/format-string/description")] string search = null)
            => engine.ListFormatTemplatesAsync(category, search);

        [McpServerTool(Name = "set_measure_format_expression"), Description("Set (or clear) a measure's DYNAMIC format-string expression: the DAX that RETURNS a format string, evaluated per filter context (Power BI's 'Format = Dynamic'). This is how magnitude-adaptive scaling (500->'500', 5e6->'5M'), per-currency, per-selected-measure, and duration/bps formats work on a plain measure (a static string can't). Pass an empty string to clear it (the measure falls back to its static/model format). A non-blank expression AUTO-CLEARS any static format string. Requires model compatibility level 1601+. Undoable. (For a static literal use set_measure_format; browse ready-made expressions with list_format_templates.)")]
        public static Task<SetResult> SetMeasureFormatExpression(IEngine engine,
            [Description("Measure ref, e.g. 'measure:Sales/YoY %'")] string objRef,
            [Description("Format-string DAX expression (e.g. a SWITCH over SELECTEDMEASURE()); empty clears it")] string formatExpression)
            => engine.SetMeasureFormatExpressionAsync(objRef, formatExpression, "agent");

        [McpServerTool(Name = "set_data_category"), Description("Set a column's data category (e.g. 'City','Country','StateOrProvince','PostalCode','WebUrl','ImageUrl'). Helps Copilot/maps. Deterministic, undoable.")]
        public static Task<SetResult> SetDataCategory(IEngine engine,
            [Description("Column ref, e.g. 'column:Geography/City'")] string objRef,
            [Description("Data category")] string dataCategory)
            => engine.SetColumnMetadataAsync(objRef, null, null, dataCategory, null, "agent");

        [McpServerTool(Name = "set_summarize_by"), Description("Set a column's default aggregation (None/Sum/Average/Min/Max/Count/DistinctCount). Use None for keys/years/codes. Deterministic, undoable. On Pro the Plan can propose an explicit base measure for a number column in place of the column adding up by itself (the column.summarize point); this op is the same setting done by hand.")]
        public static Task<SetResult> SetSummarizeBy(IEngine engine,
            [Description("Column ref")] string objRef,
            [Description("Aggregate: None, Sum, Average, Min, Max, Count, DistinctCount")] string summarizeBy)
            => engine.SetColumnMetadataAsync(objRef, null, summarizeBy, null, null, "agent");

        [McpServerTool(Name = "set_column_hidden"), Description("Show/hide a column. Hidden columns are excluded from Copilot/data-agent grounding. Hide technical/key/FK columns. Deterministic, undoable.")]
        public static Task<SetResult> SetColumnHidden(IEngine engine,
            [Description("Column ref")] string objRef,
            [Description("true to hide, false to show")] bool hidden)
            => engine.SetColumnMetadataAsync(objRef, hidden, null, null, null, "agent");

        [McpServerTool(Name = "set_sort_by_column"), Description("Set a column's sort-by column (e.g. sort 'Month Name' by 'Month Number'). Deterministic, undoable.")]
        public static Task<SetResult> SetSortByColumn(IEngine engine,
            [Description("Column ref to sort")] string objRef,
            [Description("Name of the column to sort by (same table)")] string sortByColumnName)
            => engine.SetColumnMetadataAsync(objRef, null, null, null, sortByColumnName, "agent");

        [McpServerTool(Name = "mark_date_table"), Description("Mark a table as the model's date table (DataCategory='Time'). Required for good time-intelligence and Copilot date handling. Deterministic, undoable.")]
        public static Task<SetResult> MarkDateTable(IEngine engine,
            [Description("Table ref, e.g. 'table:Date'")] string tableRef,
            [Description("The date/key column name on that table")] string dateColumn)
            => engine.MarkDateTableAsync(tableRef, dateColumn, "agent");

        [McpServerTool(Name = "get_incremental_refresh_policy"), Description("Read a table's incremental refresh policy (rolling-window store + incremental window, granularity, mode). Returns enabled=false if no policy is defined. Read-only.")]
        public static Task<RefreshPolicyInfo> GetIncrementalRefreshPolicy(IEngine engine,
            [Description("Table ref, e.g. 'table:Sales'")] string tableRef)
            => engine.GetIncrementalRefreshPolicyAsync(tableRef);

        [McpServerTool(Name = "set_incremental_refresh_policy"), Description("Define/update a table's incremental refresh policy: store N periods of history and re-import the latest M periods. Set autoWire=true to create or repair RangeStart/RangeEnd and append the required half-open date filter without replacing existing M steps. Leave it false to validate and refuse when prerequisites are missing. Metadata only; it does not refresh data. Deterministic and undoable. Existing-policy fields are preserved when omitted.")]
        public static Task<SetResult> SetIncrementalRefreshPolicy(IEngine engine,
            [Description("Table ref, e.g. 'table:Sales'")] string tableRef,
            [Description("The Date/DateTime column the partition M filters on RangeStart/RangeEnd (validated to exist; pass empty to skip the column check)")] string dateColumn,
            [Description("Rolling-window periods to STORE (history). Default 5")] int? rollingWindowPeriods = null,
            [Description("Rolling-window granularity: Day|Month|Quarter|Year. Default Year")] string rollingWindowGranularity = null,
            [Description("Incremental periods to RE-IMPORT each refresh. Default 10")] int? incrementalPeriods = null,
            [Description("Incremental granularity: Day|Month|Quarter|Year. Default Day")] string incrementalGranularity = null,
            [Description("Offset in periods from now to the rolling-window head. Default 0")] int? incrementalPeriodsOffset = null,
            [Description("Import (default) or Hybrid (real-time DirectQuery hot bucket; needs compatibility level 1565+)")] string mode = null,
            [Description("Detect-Data-Changes polling M expression (a scalar checked to skip re-importing an unchanged partition, e.g. a max(last-modified) query). null preserves, empty string clears, non-empty sets it.")] string pollingExpression = null,
            [Description("Create or repair the two date/time parameters and append the half-open partition filter before enabling the policy. Requires dateColumn and an M partition.")] bool autoWire = false)
            => engine.SetIncrementalRefreshPolicyAsync(tableRef, dateColumn, rollingWindowPeriods, rollingWindowGranularity, incrementalPeriods, incrementalGranularity, incrementalPeriodsOffset, mode, pollingExpression, autoWire, "agent");

        [McpServerTool(Name = "remove_incremental_refresh_policy"), Description("Remove a table's incremental refresh policy (sets RefreshPolicy = null). Leaves RangeStart/RangeEnd and the partition M intact. Deterministic, undoable.")]
        public static Task<SetResult> RemoveIncrementalRefreshPolicy(IEngine engine,
            [Description("Table ref, e.g. 'table:Sales'")] string tableRef)
            => engine.RemoveIncrementalRefreshPolicyAsync(tableRef, "agent");

        [McpServerTool(Name = "set_relationship_crossfilter"), Description("Set a relationship's cross-filter direction (OneDirection or BothDirections). Prefer OneDirection in a star schema. Bidirectional can confuse AI filter context. Undoable.")]
        public static Task<SetResult> SetRelationshipCrossfilter(IEngine engine,
            [Description("Relationship name")] string relationshipName,
            [Description("OneDirection or BothDirections")] string crossFilteringBehavior)
            => engine.SetRelationshipAsync(relationshipName, crossFilteringBehavior, null, "agent");

        [McpServerTool(Name = "set_relationship_active"), Description("Activate or deactivate a relationship. Only ONE active relationship can exist on a given path between two tables. Inactive ones are reached on demand via USERELATIONSHIP in DAX (e.g. role-playing date relationships like Order Date vs Ship Date). isActive=true activates it, false deactivates it. Deterministic, undoable.")]
        public static Task<SetResult> SetRelationshipActive(IEngine engine,
            [Description("Relationship name")] string relationshipName,
            [Description("true = active, false = inactive")] bool isActive)
            => engine.SetRelationshipAsync(relationshipName, null, isActive, "agent");

        [McpServerTool(Name = "set_relationship_cardinality"), Description("Set a single-column relationship's end cardinalities. fromCardinality is the FK/from side, toCardinality the lookup/to side; each is 'Many' or 'One' (a null/empty end is left unchanged). A normal star-schema relationship is Many→One. Use this to fix a mis-shaped relationship in place instead of deleting and recreating it. Deterministic, undoable.")]
        public static Task<SetResult> SetRelationshipCardinality(IEngine engine,
            [Description("Relationship name")] string relationshipName,
            [Description("From (FK) side cardinality: 'Many' or 'One' (null = unchanged)")] string fromCardinality,
            [Description("To (lookup) side cardinality: 'Many' or 'One' (null = unchanged)")] string toCardinality)
            => engine.SetRelationshipCardinalityAsync(relationshipName, fromCardinality, toCardinality, "agent");

        // ---- Row-Level Security (RLS) roles ----------------------------------------------------

        [McpServerTool(Name = "list_roles"), Description("List the model's security roles: each role's name, model permission (None/Read/ReadRefresh/Refresh/Administrator), per-table RLS row-filter DAX, and members. RLS gates what data a Copilot / data-agent persona can see, so it matters for AI grounding.")]
        public static Task<RoleInfo[]> ListRoles(IEngine engine) => engine.ListRolesAsync();

        [McpServerTool(Name = "create_role"), Description("Create a security role. modelPermission defaults to None (Read is auto-applied once you add an RLS filter). Then use set_table_permission to add per-table row filters and set_role_member to add members. Undoable.")]
        public static Task<RoleInfo> CreateRole(IEngine engine,
            [Description("Role name (unique)")] string name,
            [Description("None (default) | Read | ReadRefresh | Refresh | Administrator")] string modelPermission = null)
            => engine.CreateRoleAsync(name, modelPermission, "agent");

        [McpServerTool(Name = "delete_role"), Description("Delete a security role (and its RLS filters + members). Undoable.")]
        public static Task<SetResult> DeleteRole(IEngine engine,
            [Description("Role name")] string name)
            => engine.DeleteRoleAsync(name, "agent");

        [McpServerTool(Name = "set_role_permission"), Description("Set a role's model-level permission: None | Read | ReadRefresh | Refresh | Administrator. Undoable.")]
        public static Task<SetResult> SetRolePermission(IEngine engine,
            [Description("Role name")] string name,
            [Description("None | Read | ReadRefresh | Refresh | Administrator")] string modelPermission)
            => engine.SetRolePermissionAsync(name, modelPermission, "agent");

        [McpServerTool(Name = "set_table_permission"), Description("Set (or clear) a table's RLS ROW-FILTER DAX for a role: the boolean filter applied to that table's rows for members of the role, e.g. \"[Region] = LOOKUPVALUE(...)\" or \"[Owner] = USERPRINCIPALNAME()\". Pass an empty filter to remove it. Invalid DAX (unknown columns/functions, incomplete expressions, unknown tables) is refused and nothing is saved. Setting a filter auto-promotes the role's permission from None to Read. The result echoes the resulting ModelPermission and a Promoted flag so the elevation is explicit. RLS row-filters cannot target a calculation group. The filter is a DAX expression and participates in rename fixup. Undoable.")]
        public static Task<SetTablePermissionResult> SetTablePermission(IEngine engine,
            [Description("Role name")] string roleName,
            [Description("Table ref or name, e.g. 'table:Sales' or 'Sales'")] string tableRef,
            [Description("DAX boolean row filter; empty/null removes the filter")] string filterDax)
            => engine.SetTablePermissionAsync(roleName, tableRef, filterDax, "agent");

        [McpServerTool(Name = "set_role_member"), Description("Add or remove an (Azure AD / external) member of a role, by name (e.g. a UPN or group). add=true adds, add=false removes. A member must look like an email, a group name, or an object id. Adding a member that is already on the role reports that and does not create a new edit. NOTE: adding members is blocked on a governed Power BI model (V3Restricted). Members are then managed in the Power BI service; removing/listing always works. Undoable.")]
        public static Task<SetResult> SetRoleMember(IEngine engine,
            [Description("Role name")] string roleName,
            [Description("Member name (UPN / group / object id)")] string memberName,
            [Description("true = add, false = remove")] bool add = true)
            => engine.SetRoleMemberAsync(roleName, memberName, add, "agent");

        [McpServerTool(Name = "set_table_ols"), Description("Set a table's OBJECT-LEVEL (metadata) security for a role: controls whether the role can SEE the table at all (distinct from set_table_permission's row filter). 'None' hides the table's metadata + data from the role; 'Read' grants it; 'Default' removes the override (visible). Requires model compatibility level >= 1400 (errors clearly below). A calculation group cannot carry OLS. Surfaced via list_roles → ObjectPermissions. Undoable.")]
        public static Task<SetResult> SetTableOls(IEngine engine,
            [Description("Role name")] string roleName,
            [Description("Table ref or name, e.g. 'table:Salary' or 'Salary'")] string tableRef,
            [Description("Default | None | Read")] string permission)
            => engine.SetTableObjectPermissionAsync(roleName, tableRef, permission, "agent");

        [McpServerTool(Name = "set_column_ols"), Description("Set a column's OBJECT-LEVEL (metadata) security for a role: controls whether the role can SEE the column. 'None' hides the column from the role; 'Read' grants it; 'Default' removes the override (visible). Requires model compatibility level >= 1400 (errors clearly below). Surfaced via list_roles → ObjectPermissions[].Columns. Undoable.")]
        public static Task<SetResult> SetColumnOls(IEngine engine,
            [Description("Role name")] string roleName,
            [Description("Column ref, e.g. 'column:Employee/Salary'")] string columnRef,
            [Description("Default | None | Read")] string permission)
            => engine.SetColumnObjectPermissionAsync(roleName, columnRef, permission, "agent");

        [McpServerTool(Name = "create_linguistic_schema"), Description("Create the model's linguistic schema: the synonym store Copilot and data agents read, which also holds the AI instructions and the AI data schema. Seeds a culture's linguistic metadata; it does not change the Power BI Q&A setting. Call once before set_synonyms. Pass null culture for the default (en-US). On an existing schema it self-heals: entities bound to hidden or deleted objects are pruned, and any visible-name collisions Copilot can't tell apart are surfaced as a warning.")]
        public static Task<SetResult> CreateLinguisticSchema(IEngine engine,
            [Description("Culture id (e.g. 'en-US'), or null for default")] string culture = null)
            => engine.CreateLinguisticSchemaAsync(culture, "agent");

        [McpServerTool(Name = "set_synonyms"), Description("Set the synonyms (alternate words users might say) for a measure/column/table, e.g. Total Sales -> ['revenue','turnover','sales amount']. Auto-enables the linguistic schema if needed. Helps Copilot and data agents map natural language to the right field. The list replaces the object's own synonyms: a word left out is removed. The object's name term and Power BI's own generated words are kept, except what Power BI still records from an old name, which a list that leaves it out clears. Hidden targets are refused, and entities bound to hidden or deleted objects are pruned automatically. Two visible fields that read as the same word do not make a write fail; the readiness scan reports them as a Copilot ambiguity.")]
        public static Task<SetResult> SetSynonyms(IEngine engine,
            [Description("Object ref, e.g. 'measure:Sales/Total Sales'")] string objRef,
            [Description("Synonym terms")] string[] terms,
            [Description("Culture id, or null for default")] string culture = null)
            => engine.SetSynonymsAsync(objRef, terms, culture, "agent");

        [McpServerTool(Name = "set_ai_instructions"), Description("Set the model's Prep-for-AI AI instructions: business context, terminology and analysis rules that guide Copilot/data agents (stored on the model as the LSDL 'CustomInstructions' string). Pass empty/null to clear. Max 10,000 chars (the service ignores anything beyond; the call is rejected if over). Auto-enables the linguistic schema if needed; the write is round-trip verified, live in the shared session, and a single undoable step. CAVEAT: for a DEPLOYED model an LSDL edit syncs to the service only after a model refresh. It is not instant. Persist to disk with save_model.")]
        public static Task<SetInstructionsResult> SetAiInstructions(IEngine engine,
            [Description("The AI instructions text (<=10000 chars); empty/null clears them")] string instructions,
            [Description("Culture id (e.g. 'en-US'), or null for default")] string culture = null)
            => engine.SetAiInstructionsAsync(instructions, culture, "agent");

        [McpServerTool(Name = "get_ai_instructions"), Description("Read the model's Prep-for-AI AI instructions (the LSDL 'CustomInstructions' string set_ai_instructions writes): the text, its length vs the 10,000-char cap, and the culture read. Read-only. Call BEFORE editing instructions so an append/update doesn't clobber what's already there.")]
        public static Task<AiInstructionsInfo> GetAiInstructions(IEngine engine,
            [Description("Culture id (e.g. 'en-US'), or null for default")] string culture = null)
            => engine.GetAiInstructionsAsync(culture);

        [McpServerTool(Name = "set_ai_data_schema"), Description("Include or exclude a field (measure/column/table) from the Prep-for-AI 'AI data schema': the focused field set Copilot/data agents reason over. included=false EXCLUDES it (marks it Hidden in the LSDL); included=true restores it. Excluding noise (keys, sort/helper columns, intermediate measures) sharpens AI answers without hiding the field from report authors. Auto-enables the linguistic schema if needed; round-trip verified, live, a single undoable step. CAVEAT: for a DEPLOYED model the LSDL edit syncs only after a model refresh. Persist with save_model. On Pro the Plan can propose these choices as rows (the ai.schema point, what Copilot sees); this op is the same change done by hand.")]
        public static Task<SetResult> SetAiDataSchema(IEngine engine,
            [Description("Object ref, e.g. 'measure:Sales/Total Sales', 'column:Sales/CustomerKey', 'table:Bridge'")] string objRef,
            [Description("true = include in the AI data schema; false = exclude (mark Hidden)")] bool included,
            [Description("Culture id (e.g. 'en-US'), or null for default")] string culture = null)
            => engine.SetAiDataSchemaAsync(objRef, included, culture, "agent");

        // ---- Authoring generators (calendar / time-intelligence) -------------------------------

        [McpServerTool(Name = "generate_date_table"), Description("CLASSIC approach: create a calculated date table (Date, Year, Quarter, Month Number/Name, Year-Month, Day, Day-of-Week) over a date range and mark it as the model's date table (DataCategory='Time', with its Date column as the key). For calendar-based time intelligence (CL 1701+, the modern approach with calendar-aware DAX like TOTALYTD(expr, 'Fiscal')), prefer define_calendar_from_template. Defaults: name 'Date', range 5 years back to end of this year. Columns materialize on deploy/process. Undoable.")]
        public static Task<GenerateResult> GenerateDateTable(IEngine engine,
            [Description("Table name (default 'Date')")] string tableName = "Date",
            [Description("DAX start-date expression (default DATE(YEAR(TODAY())-5,1,1))")] string startExpr = null,
            [Description("DAX end-date expression (default DATE(YEAR(TODAY()),12,31))")] string endExpr = null,
            [Description("Mark as the date table (DataCategory='Time' and the Date column as the key); default true")] bool markAsDate = true)
            => engine.GenerateDateTableAsync(tableName, startExpr, endExpr, markAsDate, "agent");

        [McpServerTool(Name = "generate_time_intelligence"), Description("Generate a suite of time-intelligence measures for a base measure, each correctly written against the given date column, named '<base> <variant>', placed in a 'Time Intelligence' display folder, and inheriting the base format. Default suite: YTD, QTD, MTD, PY (prior year), YoY, YoYPct. Additional opt-in variants (pass in 'variants'): ROLL12/R3M/R6M (rolling 12/3/6-month windows), SPLY (same period last year), PYTD (prior-year YTD), PM (prior month), MoM + MoM% (month-over-month delta and %). Undoable.")]
        public static Task<GenerateResult> GenerateTimeIntelligence(IEngine engine,
            [Description("Base measure ref, e.g. 'measure:Sales/Total Sales'")] string baseMeasureRef,
            [Description("Date column as DAX ('Date'[Date]) or a column ref (column:Date/Date)")] string dateColumn,
            [Description("Variants (default YTD,QTD,MTD,PY,YoY,YoYPct). Also: ROLL12, R3M, R6M, SPLY, PYTD, PM, MoM, MoM%")] string[] variants = null,
            [Description("Display folder (default 'Time Intelligence')")] string displayFolder = null)
            => engine.GenerateTimeIntelligenceAsync(baseMeasureRef, dateColumn, variants, displayFolder, "agent");

        // ---- Calendar-based time intelligence (CL 1701+) ---------------------------------------

        [McpServerTool(Name = "list_calendars"), Description("List the model's calendars (calendar-based time intelligence, CL 1701+): per table, each calendar's column→TimeUnit mappings (Year/Quarter/Month/Week/Date/…, primary + associated) and its time-related (untagged) bucket. Pure offline metadata read, no connection needed. Also reports whether the model's compatibility level supports calendars at all.")]
        public static Task<CalendarListResult> ListCalendars(IEngine engine,
            [Description("Table name or 'table:Name' ref to filter to one table; null = all tables")] string tableRef = null)
            => engine.ListCalendarsAsync(tableRef);

        [McpServerTool(Name = "define_calendar"), Description("Create a calendar on a table (calendar-based time intelligence, CL 1701+): map its columns to TimeUnit categories (Year, Quarter, Month, Week, Date, MonthOfYear, DayOfWeek, …: absolute units and recurring '…OfYear' variants), with optional associated columns per unit and a 'timeRelated' bucket for untagged time columns (e.g. IsWorkingDay). Enables calendar-aware DAX: TOTALYTD(expr, '<calendar>'). A table can hold several calendars (Gregorian + Fiscal + ISO…) over the same columns. One undoable step; persist with save_model. Requires CL 1701+ (set_compatibility_level).")]
        public static Task<CalendarResult> DefineCalendar(IEngine engine,
            [Description("Table name or 'table:Name' ref")] string tableRef,
            [Description("Calendar name, e.g. 'Fiscal'")] string name,
            [Description("Column→category mappings; timeUnit = a TimeUnit name, or 'timeRelated' for the untagged bucket; associated=true adds the column as an ASSOCIATED column of that unit (map the primary first)")] CalendarMappingSpec[] mappings,
            [Description("Optional calendar description")] string description = null)
            => engine.DefineCalendarAsync(tableRef, name, mappings, description, "agent");

        [McpServerTool(Name = "delete_calendar"), Description("Delete a calendar from a table (the columns themselves are untouched). Undoable.")]
        public static Task<SetResult> DeleteCalendar(IEngine engine,
            [Description("Table name or 'table:Name' ref")] string tableRef,
            [Description("Calendar name")] string name)
            => engine.DeleteCalendarAsync(tableRef, name, "agent");

        [McpServerTool(Name = "tag_calendar_column"), Description("Incrementally edit one column mapping on an existing calendar: tag a column to a TimeUnit (replacing that unit's primary if it has one), add it as an ASSOCIATED column of a unit, put it in the 'timeRelated' bucket, or remove=true to untag it. Undoable.")]
        public static Task<CalendarResult> TagCalendarColumn(IEngine engine,
            [Description("Table name or 'table:Name' ref")] string tableRef,
            [Description("Calendar name (must exist: define_calendar first)")] string calendarName,
            [Description("Column name on the calendar's table")] string column,
            [Description("TimeUnit name (Year/Quarter/Month/Week/Date/MonthOfYear/…), or 'timeRelated'/null for the untagged bucket")] string timeUnit = null,
            [Description("true = add as an ASSOCIATED column of the unit (its primary must already be mapped)")] bool associated = false,
            [Description("true = remove this column's mapping instead of adding one")] bool remove = false)
            => engine.TagCalendarColumnAsync(tableRef, calendarName, column, timeUnit, associated, remove, "agent");

        [McpServerTool(Name = "define_calendar_from_template"), Description("One-step calendar setup (the modern replacement for generate_date_table): generate a template's calculated columns (only where absent: existing columns are kept and mapped as-is) AND the calendar with its TimeUnit mappings, as one undoable step. Templates: gregorian (Year/Quarter/Month/Day + name-by-number sort pairing) · fiscal (fiscalStartMonth, FY labeled by ENDING year) · iso (ISO-8601 weeks, Thursday rule) · 445 (4-4-5 retail periods over ISO weeks) · 13period (13 four-week periods). Targets an existing table with a date column (dateColumn, auto-detected when unambiguous), or creates a fresh CALENDAR() calculated table when tableName doesn't exist. The week-based templates (iso/445/13period) share ISO scaffolding columns so they coexist on one table. Requires CL 1701+.")]
        public static Task<CalendarResult> DefineCalendarFromTemplate(IEngine engine,
            [Description("gregorian | fiscal | iso | 445 | 13period")] string template,
            [Description("Target table (default 'Date'; created as a calculated CALENDAR() table if absent)")] string tableName = null,
            [Description("Date column on the table (default: 'Date', else the single DateTime column)")] string dateColumn = null,
            [Description("Fiscal template: first month of the fiscal year, 1-12 (default 7 = July)")] int fiscalStartMonth = 7,
            [Description("New-table only: DAX start-date expression (default DATE(YEAR(TODAY())-5,1,1))")] string startExpr = null,
            [Description("New-table only: DAX end-date expression (default DATE(YEAR(TODAY()),12,31))")] string endExpr = null,
            [Description("Calendar name override (default: the template's, e.g. 'Fiscal', '4-4-5')")] string calendarName = null)
            => engine.DefineCalendarFromTemplateAsync(template, tableName, dateColumn, fiscalStartMonth, startExpr, endExpr, calendarName, "agent");

        // ---- Live connectivity (attached-readonly DAX/DMV) -------------------------------------

        [McpServerTool(Name = "connect_xmla"), Description("Connect (read-only) to a Power BI / Fabric / Azure AS XMLA endpoint to run DAX/DMV against the LIVE deployed model while you edit files offline. endpoint e.g. 'powerbi://api.powerbi.com/v1.0/myorg/<Workspace>'. authMode: azcli (default, uses your `az login`), interactive (browser), or devicecode. tenantId optional: target a specific Entra tenant when your `az login` is a different tenant than the model's (same as open_live). The result's account field names the identity signed in, when known.")]
        public static async Task<ConnectionStatus> ConnectXmla(IEngine engine,
            [Description("XMLA endpoint, e.g. powerbi://api.powerbi.com/v1.0/myorg/<Workspace>")] string endpoint,
            [Description("Dataset / database name")] string database,
            [Description("interactive (browser/MFA via Azure.Identity) | serviceprincipal (AZURE_*/FABRIC_* env) | azcli | devicecode")] string authMode = "azcli",
            [Description("Optional Entra tenant id or domain to authenticate against")] string tenantId = null,
            [Description("Optional saved account profile id (from list_account_profiles) to connect AS, for THIS connection only, and never changes the tenant default. Must be an already-signed-in profile; adding a new account is a human action (you will be refused).")] string accountProfileId = null)
        {
            var s = await engine.ConnectXmlaAsync(endpoint, database, authMode, null, tenantId, accountProfileId: accountProfileId, origin: "agent");
            // Broadcast the connection-state change (HIGH 7): without it, an agent connecting over MCP left the UI door's
            // identity/sync chip stale. The UI relays this connection-kind activity to a native status refresh.
            Emit(engine, new ActivityEvent { Kind = "connect_xmla", Origin = "agent", Label = "Connected to a published model for tests and queries", Ok = s?.Connected == true, Result = s });
            return s;
        }

        [McpServerTool(Name = "open_live"), Description("LOAD the full editable model from a Power BI / Fabric / Azure AS XMLA endpoint into the session, so the WHOLE workbench (AI-readiness scan, BPA, the change-plan, and every edit) operates on the LIVE deployed model. Unlike connect_xmla (which only enables read-only DAX/DMV), this loads the model's metadata via TOM, exactly as Tabular Editor attaches to a deployed model. Edits stay IN-MEMORY and undoable; push them back with deploy_live (the write half, dry-run by default) or persist to a TMDL folder with save_model for review/deployment. endpoint e.g. 'powerbi://api.powerbi.com/v1.0/myorg/<Workspace>'. Leave database empty to load the workspace's only/first dataset. authMode: interactive (browser/MFA via Azure.Identity) | serviceprincipal (AZURE_*/FABRIC_* env, most reliable for Fabric) | azcli (default) | devicecode. tenantId optional: target a specific Entra tenant when your `az login` is a different tenant than the model's.")]
        public static async Task<OpenResult> OpenLive(IEngine engine,
            [Description("XMLA endpoint, e.g. powerbi://api.powerbi.com/v1.0/myorg/<Workspace>")] string endpoint,
            [Description("Dataset / database name; empty = the workspace's first/only dataset")] string database = null,
            [Description("interactive | serviceprincipal | azcli | devicecode")] string authMode = "azcli",
            [Description("Optional Entra tenant id or domain to authenticate against")] string tenantId = null,
            [Description("Optional saved account profile id (from list_account_profiles) to open AS, for THIS open only, and never changes the tenant default. Must be an already-signed-in profile; adding a new account or an interactive sign-in is a human action (you will be refused).")] string accountProfileId = null,
            [Description("true = throw away unsaved edits and open anyway; default false refuses while unsaved")] bool discardUnsaved = false,
            [Description("true = reuse a sign-in this machine already holds, or answer that a sign in is needed. Never opens a browser or a device-code prompt. An assistant open is already like this; the flag exists so both doors take the same call.")] bool silentOnly = false)
        {
            await UnsavedWorkGuard.ThrowIfBlockedAsync(engine, discardUnsaved, "agent", "open the published model " + (string.IsNullOrWhiteSpace(database) ? endpoint : database));
            return await engine.OpenLiveAsync(endpoint, database, authMode, rawToken: null, tenantId: tenantId, accountProfileId: accountProfileId, origin: "agent", discardUnsaved: discardUnsaved, silentOnly: silentOnly);
        }

        [McpServerTool(Name = "deploy_live"), Description("DEPLOY the session's metadata edits back to the live Power BI / Fabric / Azure AS model over XMLA (the write half of open_live). Metadata only (names/descriptions/visibility/categories/format strings/folders/summarize-by, measure + calc-column DAX, partition sources (M / calc-table DAX / legacy), shared expressions, and the linguistic schema) via Model.SaveChanges(), matched by LineageTag so renames land on the right object. NO data refresh. Absence never deletes: an object that is only on the live model is reported in LiveOnly and left alone unless you pass it in deleteRefs (the opt-in tick). A NEW CALCULATED table IS created (and Calculate-recalced on commit); other structural changes (new DATA/import tables, new data columns, new partitions, source-type or Direct Lake rebinds, incremental-refresh policy) are reported in Unmatched/LiveOnly, never silently dropped. Empty endpoint = deploy back to the model this session opened (the round-trip); or pass endpoint + database. DRY RUN by default: commit=true writes, and that write needs the review token the preview returned. A committed write first saves a restore point of the live model's metadata (list_restore_points) so rollback_push can undo it; if that snapshot fails, a write with deleteRefs is refused and a write without deletes goes ahead with a warning. Needs a Read-Write XMLA endpoint and a write principal (authMode=serviceprincipal is the reliable one).")]
        public static Task<DeployReport> DeployLive(IEngine engine,
            [Description("XMLA endpoint; empty = deploy back to the live model this session was opened from (open_live)")] string endpoint = null,
            [Description("Dataset / database name; ignored when endpoint is empty (the bound source's dataset is used)")] string database = null,
            [Description("serviceprincipal (recommended) | azcli | interactive | devicecode")] string authMode = "serviceprincipal",
            [Description("Optional Entra tenant id or domain")] string tenantId = null,
            [Description("false = DRY RUN (report the change set, write nothing); true = COMMIT the live write")] bool commit = false,
            [Description("why you are shipping past a RED deploy gate: required to override; recorded in the model's append-only audit trail")] string overrideReason = null,
            [Description("the review token returned by the preview; required to write, and it must still match")] string confirmToken = null,
            [Description("object refs from the preview LiveOnly list to remove; omit to leave extra live objects alone. Never a flag that means remove everything missing.")] string[] deleteRefs = null)
            => engine.DeployLiveAsync(endpoint, database, authMode, rawToken: null, tenantId: tenantId, commit: commit, origin: "agent", overrideReason: overrideReason, confirmToken: confirmToken, deleteRefs: deleteRefs);

        [McpServerTool(Name = "publish_new_model"), Description("PUBLISH the open model as a NEW semantic model in a Power BI / Fabric workspace, load its data, and make it this model's publish destination (the next deploy_live or apply_model_diff updates it) and the model Tests and queries read. The person's Publish page does the same with its 'New model in a workspace' card. Use it when the model has no published copy yet (a model built with start_model_from_sql, a new local file); to update a model that already exists, use deploy_live or apply_model_diff. Nothing is overwritten: a name already taken in the workspace is refused before anything is written. A model whose source is a Fabric SQL endpoint is published as Direct Lake, so its data loads with no sign-in step, and the local copy turns Direct Lake in the same undoable step. Any other source is published as an import model; its result says to set the data source sign-in in the Power BI service, then refresh, so pass that line on. DRY RUN by default: commit=false names the workspace, the name and Direct Lake or Import, and contacts nothing; commit=true creates the model. The write follows the person's permission settings like every live write. list_workspaces gives workspace names.")]
        public static Task<NewModelResult> PublishNewModel(IEngine engine,
            [Description("The workspace's name, as list_workspaces shows it")] string workspace,
            [Description("The new model's name; empty = the open model's name")] string name = null,
            [Description("serviceprincipal | azcli | interactive | devicecode; empty = the sign-in the model's SQL source was read with")] string authMode = null,
            [Description("Optional Entra tenant id or domain")] string tenantId = null,
            [Description("false = DRY RUN (say what would be created, contact nothing); true = create the model and load its data")] bool commit = false)
            => engine.PublishNewModelAsync(workspace, name, authMode, tenantId, origin: "agent", workspaceId: null, commit: commit);

        [McpServerTool(Name = "list_refresh_types"), Description("List the partition refresh ('process') options, each with a plain-language explanation: Full (reload + recalc), DataOnly (reload, no recalc), Calculate (recalc only), ClearValues (empty it), Automatic (refresh if stale), Add (append rows), plus the table-level Defragment / Indexes. Read-only. Use it to pick a type for refresh_partition.")]
        public static Task<RefreshTypeInfo[]> ListRefreshTypes(IEngine engine) => engine.ListRefreshTypesAsync();

        [McpServerTool(Name = "refresh_partition"), Description("Refresh ('process') a SINGLE partition of the live model. Types: Full (reload + recalc, the default), DataOnly (reload, no recalc), Calculate (recalc only), ClearValues (empty it, destructive), Automatic (refresh only if stale), Add (append rows). LIVE DATA WRITE against the Analysis Services engine: runs as a DRY RUN by default (reports what would run, connects to nothing); pass commit=true to actually execute. Leave endpoint EMPTY to target the live model this session was opened from (open_live/open_local binds it); a file model has nothing to refresh. See list_refresh_types for full explanations.")]
        public static Task<RefreshReport> RefreshPartition(IEngine engine,
            [Description("Partition ref, e.g. 'partition:Sales/2024'")] string partitionRef,
            [Description("Refresh type: Full (default) | DataOnly | Calculate | ClearValues | Automatic | Add")] string refreshType = "Full",
            [Description("XMLA endpoint; empty = the live model this session was opened from (open_live/open_local)")] string endpoint = null,
            [Description("Dataset / database name; ignored when endpoint is empty (the bound source's dataset is used)")] string database = null,
            [Description("serviceprincipal (recommended) | azcli | interactive | devicecode")] string authMode = "serviceprincipal",
            [Description("Optional Entra tenant id or domain")] string tenantId = null,
            [Description("false = DRY RUN (report what would run, execute nothing); true = COMMIT the live refresh")] bool commit = false)
            => engine.RefreshPartitionAsync(partitionRef, refreshType, endpoint, database, authMode, rawToken: null, tenantId: tenantId, commit: commit, origin: "agent");

        [McpServerTool(Name = "list_local_instances"), Description("List running local Power BI Desktop Analysis Services instances (port + workspace). Use one with connect_local.")]
        public static Task<LocalInstance[]> ListLocalInstances(IEngine engine) => engine.ListLocalInstancesAsync();

        [McpServerTool(Name = "connect_local"), Description("Connect (read-only) to a local Analysis Services instance (an open Power BI Desktop). Pass a Data Source like 'localhost:51234', or leave it empty to auto-pick the running Power BI Desktop instance.")]
        public static async Task<ConnectionStatus> ConnectLocal(IEngine engine,
            [Description("Data source, e.g. 'localhost:51234'; empty = auto-discover")] string dataSource = null,
            [Description("Optional database name")] string database = null)
        {
            var s = await engine.ConnectLocalAsync(dataSource, database);
            Emit(engine, new ActivityEvent { Kind = "connect_local", Origin = "agent", Label = "Connected to a running local model", Ok = s?.Connected == true, Result = s });   // HIGH 7: keep the UI door's identity/sync chip fresh
            return s;
        }

        [McpServerTool(Name = "connection_status"), Description("Report the current live query connection: connected, kind, data source. For which model is being edited versus which one answers queries (they can be two different copies), use connection_context.")]
        public static Task<ConnectionStatus> ConnectionStatusTool(IEngine engine) => engine.ConnectionStatusAsync();

        [McpServerTool(Name = "disconnect"), Description("Close the live query connection (the one connect_xmla, connect_local, open_live or open_local attached). The open model, its unsaved edits and its undo history stay in the session; run_dax, run_dmv and the other live tools need a new connection afterwards. Returns the resulting connection status.")]
        public static async Task<ConnectionStatus> Disconnect(IEngine engine)
        {
            var s = await engine.DisconnectAsync();
            Emit(engine, new ActivityEvent { Kind = "disconnect", Origin = "agent", Label = "Closed the live connection", Ok = true, Result = s });   // HIGH 7: broadcast so the UI door's chips refresh
            return s;
        }

        // ---- live activity: surface the agent's read ops to the human's Studio (best-effort broadcast) ----------
        // The agent already has the FULL result over MCP; we publish a TOKEN-LIGHT, row-capped preview so the human's
        // Studio reflects what Claude is running, live (the read counterpart of the mutation change-feed). Fire-and-
        // forget: a broadcast failure must NEVER fail the tool. In the cross-process case engine is a RemoteEngine,
        // so PublishActivity forwards to the owner whose ChangeBus broadcasts model/activity to the UI client.
        private const int ActivityRowCap = 500;
        private static string TruncDax(string q) => string.IsNullOrEmpty(q) || q.Length <= 600 ? q : q.Substring(0, 600) + " …";
        private static ResultSet CapResult(ResultSet r)
        {
            if (r?.Rows == null || r.Rows.Length <= ActivityRowCap) return r;
            return new ResultSet { Columns = r.Columns, Rows = r.Rows.Take(ActivityRowCap).ToArray(), RowCount = r.RowCount, Truncated = true, ElapsedMs = r.ElapsedMs, Error = r.Error, PolicyRefused = r.PolicyRefused, ApprovalId = r.ApprovalId, Query = r.Query, Cancelled = r.Cancelled };
        }
        private static EvalLogResult CapEvalLog(EvalLogResult r)
        {
            if (r == null) return r;
            const int entryCap = 50;
            return new EvalLogResult
            {
                TraceAvailable = r.TraceAvailable,
                Entries = (r.Entries ?? Array.Empty<EvalLogEntry>()).Select(en => new EvalLogEntry
                {
                    Label = en.Label, Expression = en.Expression, RowCount = en.RowCount, Columns = en.Columns,
                    Rows = en.Rows != null && en.Rows.Length > entryCap ? en.Rows.Take(entryCap).ToArray() : en.Rows, Raw = null,
                }).ToArray(),
                ResultColumns = r.ResultColumns,
                ResultRows = r.ResultRows != null && r.ResultRows.Length > ActivityRowCap ? r.ResultRows.Take(ActivityRowCap).ToArray() : r.ResultRows,
                ResultRowCount = r.ResultRowCount, ElapsedMs = r.ElapsedMs, Note = r.Note, Error = r.Error, ApprovalId = r.ApprovalId,
            };
        }
        internal static void Emit(IEngine engine, ActivityEvent e)
        {
            // Fire-and-forget, but OBSERVE the task's fault — the cross-process RemoteEngine path RPCs over the pipe
            // and faults ASYNCHRONOUSLY if the owner is gone, which a bare try/catch wouldn't catch (it would surface
            // later as an UnobservedTaskException). Swallow it: a broadcast failure must never fail/hang the tool.
            try { engine.PublishActivityAsync(e).ContinueWith(t => { _ = t.Exception; }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted | System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously); }
            catch { /* live feed is best-effort */ }
        }

        [McpServerTool(Name = "run_dax"), Description("Run a DAX query (EVALUATE / DEFINE...EVALUATE) against the connected live model and return columns + rows (capped). Returns {error} if not connected or the query fails. Read-only. A ROW-returning query (`EVALUATE <table>`, SUMMARIZECOLUMNS, FILTER, VALUES, TOPN, …) reads source rows, so the agent policy's QueryData rule applies to the connected target. An Ask refusal names the approval to request; a Deny means the target's policy forbids it (a human can run it or change the policy). A scalar probe (EVALUATE ROW(...) / EVALUATE { … }) is a calculation and is never gated.")]
        public static async Task<ResultSet> RunDax(IEngine engine,
            [Description("DAX query, e.g. EVALUATE TOPN(100, Sales)")] string query,
            [Description("Max rows to return (default 10000)")] int maxRows = 10000,
            CancellationToken cancellationToken = default)
        {
            var r = await engine.RunDaxAsync(query, maxRows, origin: "agent", cancellationToken);
            Emit(engine, new ActivityEvent { Kind = "run_dax", Origin = "agent", Label = "Ran DAX query", Query = TruncDax(query),
                Ok = string.IsNullOrEmpty(r.Error), Error = r.Error, ApprovalId = r.ApprovalId, RowCount = r.RowCount, ElapsedMs = r.ElapsedMs, Result = CapResult(r) });
            return r;
        }

        [McpServerTool(Name = "run_dmv"), Description("Run a DMV query (e.g. SELECT * FROM $SYSTEM.TMSCHEMA_MEASURES) against the connected live model. Read-only.")]
        public static Task<ResultSet> RunDmv(IEngine engine,
            [Description("DMV SELECT query")] string query,
            [Description("Max rows (default 10000)")] int maxRows = 10000)
            => engine.RunDmvAsync(query, maxRows);

        [McpServerTool(Name = "preview_table"), Description("Preview the first N rows of a table on the connected live model (EVALUATE TOPN(N, 'Table')). Accepts a table name or a 'table:Name' ref. Requires a live connection. Reads DATA rows, so the agent policy's QueryData rule applies to the connected target. An Ask refusal names the approval to request; a Deny means the target's policy forbids it (a human can run it or change the policy).")]
        public static async Task<ResultSet> PreviewTable(IEngine engine,
            [Description("Table name or 'table:Name' ref")] string table,
            [Description("How many rows (default 200)")] int topN = 200)
        {
            var r = await engine.PreviewTableAsync(table, topN, origin: "agent");
            Emit(engine, new ActivityEvent { Kind = "preview_table", Origin = "agent", Label = $"Previewed '{table}'", Target = table,
                Ok = string.IsNullOrEmpty(r.Error), Error = r.Error, ApprovalId = r.ApprovalId, RowCount = r.RowCount, ElapsedMs = r.ElapsedMs, Result = CapResult(r) });
            return r;
        }

        [McpServerTool(Name = "pivot_measure"), Description("Test a measure (or any scalar DAX) across a pivot: EVALUATE SUMMARIZECOLUMNS(rowFields..., colField?, filters..., \"value\", measureExpr). Returns long-form rows the caller pivots into a row x column matrix. Use it to sanity-check a measure's values by category/date before trusting it. Read-only; requires a live connection. Returns rows of source grouping values, so the agent policy's QueryData rule applies to the connected target. An Ask refusal names the approval to request; a Deny means the target's policy forbids it (a human can run it or change the policy).")]
        public static async Task<ResultSet> PivotMeasure(IEngine engine,
            [Description("Scalar DAX to evaluate, e.g. [Total Sales] or SUM(Sales[Amount])")] string measureExpr,
            [Description("Row group-by columns, e.g. [\"'Product'[Category]\"]")] string[] rowFields,
            [Description("Optional single column group-by for the matrix columns, e.g. \"'Date'[Year]\"")] string colField = null,
            [Description("Optional table filters, e.g. [\"KEEPFILTERS('Date'[Year]=2024)\"]")] string[] filters = null,
            [Description("Max rows (default 100000)")] int maxRows = 100000)
        {
            var r = await engine.PivotMeasureAsync(measureExpr, rowFields, colField, filters, maxRows, origin: "agent");
            Emit(engine, new ActivityEvent { Kind = "pivot_measure", Origin = "agent", Label = "Pivoted a measure", Query = TruncDax(measureExpr),
                Ok = string.IsNullOrEmpty(r.Error), Error = r.Error, ApprovalId = r.ApprovalId, RowCount = r.RowCount, ElapsedMs = r.ElapsedMs,
                Result = new PivotActivityResult { Result = CapResult(r), RowFields = rowFields?.Length ?? 0, HasColField = !string.IsNullOrWhiteSpace(colField) } });
            return r;
        }

        [McpServerTool(Name = "reconcile_measure"), Description("Reconcile a DAX measure against an INDEPENDENT SQL ground truth, cell by cell (the Tests-tab correctness spine). The engine runs the DAX side (SUMMARIZECOLUMNS over your groupBy + the measure, plus an independent grand-total query) over the live XMLA connection and runs YOUR supplied SQL over the Fabric SQL endpoint, full-outer-joins them by the composite grouping key, and judges every cell under an explicit tolerance + blank policy. The engine NEVER authors the SQL. You supply the ground truth. The SQL runs against the source endpoint as YOUR OWN identity (the engine holds no elevated credential. It can only read what you already can; the endpoint's nature + your warehouse permissions decide what is possible). Whether you (an agent) may run it is governed by the QueryData permission setting on the SQL target (the permissions tab, Ask/Deny/Allow), exactly like preview_table / run_dax. A human runs ungated. groupBy entries are resolved to real model columns (a 'column:Table/Column' ref or a plain 'Table'[Column]); an unresolved entry is refused (this prevents WRONG RESULTS, not writes). blankPolicy is REQUIRED (\"zero\"|\"null\"|\"distinct\"). Grade on `status` (Reconciled|Mismatch|InsufficientCoverage|NothingVerifiable|InputError), NOT `anyMismatch`; an uncaveated pass is Reconciled AND unverifiable==0 AND complete==true, and even then expected-member coverage is UNKNOWN (coverageKnown=false; it reconciles the members observed, not that none is missing). A total-only run is InsufficientCoverage by design (a matching total can hide the VertiPaq blank-row trap). Requires a live connection; reads source-row aggregates from BOTH the model and the SQL endpoint. Returns the status, summary, worst offender, coverage facts, per-side execution timestamps (independent connections may see different snapshots), snapshotNote, and the mismatching cells (capped).")]
        public static async Task<ReconcileRunResult> ReconcileMeasure(IEngine engine,
            [Description("Measure to test (a name or 'measure:Name')")] string measureRef,
            [Description("Ground-truth SQL. Grouped mode: return the groupBy key columns (same order) then the aggregate value column. Total-only mode (empty groupBy): aggregate to one row/value. The engine never writes this.")] string sql,
            [Description("Blank policy, REQUIRED, no default: \"zero\" (BLANK/NULL read as 0; additive), \"null\" (BLANK≈NULL, both differ from 0), or \"distinct\" (BLANK, NULL and 0 all distinct)")] string blankPolicy,
            [Description("DAX grouping columns, e.g. [\"'Date'[Year]\"]. Empty = grand-total-only (InsufficientCoverage by design).")] string[] groupBy = null,
            [Description("Absolute tolerance floor (finite, >= 0). Default 1e-9.")] double toleranceAbsolute = 1e-9,
            [Description("Relative tolerance as a FRACTION of abs(sql): 1.0 = 100%, not 1%. Finite, >= 0. Default 1e-7.")] double toleranceRelative = 1e-7,
            [Description("Optional independent grand-total SQL for grouped mode (a single row/value at total context). Queried separately, never rebuilt from members.")] string sqlGrandTotal = null,
            [Description("Member-row cap (default 100000). A truncated run is marked incomplete, never a pass.")] int maxRows = 100000,
            [Description("Optional Fabric SQL endpoint override; derived from the model's SQL source when omitted")] string server = null,
            [Description("Optional database override")] string database = null,
            [Description("SQL Entra auth mode (default azcli)")] string authMode = null,
            [Description("Optional tenant id for the SQL token")] string tenantId = null)
        {
            var req = new ReconcileRequest
            {
                MeasureRef = measureRef, Sql = sql, BlankPolicy = blankPolicy, GroupBy = groupBy ?? Array.Empty<string>(),
                ToleranceAbsolute = toleranceAbsolute, ToleranceRelative = toleranceRelative, SqlGrandTotal = sqlGrandTotal,
                MaxRows = maxRows, Server = server, Database = database, AuthMode = authMode, TenantId = tenantId,
            };
            var r = await engine.ReconcileMeasureAsync(req, origin: "agent");
            // Minimal-but-composable (harness result contract): keep the headline facts + the MISMATCHING cells (capped)
            // so the agent can act, not the whole cell blob. The UI/RPC door still gets every cell.
            if (r.Cells != null && r.Cells.Length > 0)
                r.Cells = r.Cells.Where(c => c.Verdict == ReconcileVerdict.Mismatch).Take(25).ToArray();
            Emit(engine, new ActivityEvent { Kind = "reconcile_measure", Origin = "agent",
                Label = $"Reconciled '{measureRef}' vs SQL ({r.Status})", Target = measureRef,
                Ok = string.IsNullOrEmpty(r.Error), Error = r.Error, ApprovalId = r.ApprovalId, ElapsedMs = r.DaxElapsedMs + r.SqlElapsedMs });
            return r;
        }

        [McpServerTool(Name = "vpaq_scan"), Description("Storage analysis of the connected live query model. Returns observed column and table bytes, component bytes, encoding, an observed-denominator percentage, and an explicit storageMode. Import mode establishes complete model totals, directLake is resident-only, and unknown leaves completeness unconfirmed. Requires a live connection.")]
        public static async Task<VpaqReport> VpaqScan(IEngine engine,
            [Description("How many top columns to return (default 25)")] int topN = 25)
        {
            var r = await engine.VertiPaqScanAsync(topN);
            // Kind stays "vpaq_scan" (the reflection key the Storage tab listens on); only the user-facing Label uses
            // the tab's "Storage" language (no product names in copy — see the copy rulebook).
            Emit(engine, new ActivityEvent { Kind = "vpaq_scan", Origin = "agent", Label = "Scanned storage",
                Ok = string.IsNullOrEmpty(r.Error), Error = r.Error, Result = r });
            return r;
        }

        // ---- AI-native DAX optimize/verify loop ------------------------------------------------

        [McpServerTool(Name = "benchmark_dax"), Description("Time a DAX query on the connected live model. mode='warm' (the default) runs it 'runs' times without clearing the cache and returns the first run, the fastest and median warm runs in milliseconds, and each run. mode='coldWarm' runs it cold (the cache is cleared before each run) and warm, and returns the average, standard deviation, minimum and maximum of total time and storage-engine time for each, plus each run. Cold runs and the storage-engine split need a local Power BI Desktop or an admin XMLA endpoint. Clearing the cache on a shared or cloud endpoint affects everyone using it, so there it needs confirm=true, and without it the result is warm only. clearForCold=false keeps a coldWarm run warm only. Use it before and after a rewrite to show the rewrite is faster. It does not change the model. runs: 1 to 25.")]
        public static async Task<object> BenchmarkDaxOp(IEngine engine,
            [Description("DAX query, e.g. EVALUATE SUMMARIZECOLUMNS('Date'[Year], \"Sales\", [Total Sales])")] string query,
            [Description("How many runs (per temperature in coldWarm mode). Default 5, max 25.")] int runs = 5,
            [Description("warm (the default) or coldWarm")] string mode = "warm",
            [Description("coldWarm only: clear the cache before each cold run (default true). false = warm only.")] bool clearForCold = true,
            [Description("coldWarm only: required to clear the cache on a shared or cloud endpoint (affects all users); ignored on local Power BI Desktop.")] bool confirm = false)
        {
            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case "":
                case "warm": return await BenchmarkDax(engine, query, runs);
                case "coldwarm": return await BenchmarkDaxColdWarm(engine, query, runs, clearForCold, confirm);
                default: throw new ArgumentException("mode must be 'warm' or 'coldWarm'.", nameof(mode));
            }
        }

        /// <summary>benchmark_dax with mode='warm'. The activity kind stays "benchmark_dax": the DAX Lab reads a
        /// BenchmarkResult off that kind.</summary>
        public static async Task<BenchmarkResult> BenchmarkDax(IEngine engine, string query, int runs = 5)
        {
            var r = await engine.BenchmarkDaxAsync(query, runs);
            Emit(engine, new ActivityEvent { Kind = "benchmark_dax", Origin = "agent", Label = "Benchmarked a query", Query = TruncDax(query),
                Ok = string.IsNullOrEmpty(r.Error), Error = r.Error, Result = r });
            return r;
        }

        [McpServerTool(Name = "evaluate_and_log"), Description("Debug a DAX query that contains EVALUATEANDLOG(<expr>, \"label\") calls: runs it and returns the query result PLUS every logged intermediate (label, the sub-expression, and its evaluated rows/values) captured from the engine. Wrap the sub-expressions you want to inspect in EVALUATEANDLOG to see what they actually evaluate to. Needs a local Power BI Desktop or admin XMLA. Read-only. The logged intermediates can include row samples of any table, so the agent policy's QueryData rule applies to EVERY call on the connected target (regardless of the outer query shape). An Ask refusal names the approval to request; a Deny means the target's policy forbids it (a human can run it or change the policy).")]
        public static async Task<EvalLogResult> EvaluateAndLog(IEngine engine,
            [Description("DAX query containing EVALUATEANDLOG(...) calls")] string query,
            [Description("Max rows for the query result (default 1000)")] int maxRows = 1000)
        {
            var r = await engine.EvaluateAndLogAsync(query, maxRows, origin: "agent");
            Emit(engine, new ActivityEvent { Kind = "evaluate_and_log", Origin = "agent", Label = "Debugged a DAX query", Query = TruncDax(query),
                Ok = string.IsNullOrEmpty(r.Error), Error = r.Error, ApprovalId = r.ApprovalId, RowCount = r.ResultRowCount, ElapsedMs = r.ElapsedMs, Result = CapEvalLog(r) });
            return r;
        }

        [McpServerTool(Name = "profile_dax"), Description("Profile a DAX query's SERVER timings on the connected live model: total ms, the Formula-Engine vs Storage-Engine split, the number of SE scans, SE CPU and parallelism, and the heaviest scans (xmSQL). Use it to understand WHY a query is slow (FE-bound vs SE-bound) when optimizing. Needs a local Power BI Desktop or an admin XMLA endpoint; falls back to wall-clock only otherwise. Read-only.")]
        public static async Task<ServerTimings> ProfileDax(IEngine engine,
            [Description("DAX query to profile, e.g. EVALUATE SUMMARIZECOLUMNS(...)")] string query)
        {
            var r = await engine.ProfileDaxAsync(query);
            Emit(engine, new ActivityEvent { Kind = "profile_dax", Origin = "agent", Label = "Profiled server timings", Query = TruncDax(query),
                Ok = string.IsNullOrEmpty(r.Error), Error = r.Error, ElapsedMs = r.TotalMs, Result = r });
            return r;
        }

        [McpServerTool(Name = "verify_dax_equivalence"), Description("PROVE a candidate DAX rewrite is behavior-preserving before applying it: evaluates exprA (original) and exprB (rewrite) side by side across MULTIPLE context shapes built from groupBy (the fully-crossed leaf rows AND each single-column subtotal AND the grand total) and reports whether ALL values match plus any mismatching contexts. The extra shapes matter: SUMMARIZECOLUMNS leaves pin every dimension, so a wrong-SCOPE rewrite (wrong denominator, collapsed context transition, over-broad ALL/ALLEXCEPT) can match at every leaf yet diverge at a subtotal or the grand total. This gate catches that. Use it, over a representative group-by matrix, before replacing a measure with a hand-applied rewrite (update_measure); optimize_measure and apply_plan run the same proof themselves. Read-only.")]
        public static async Task<EquivalenceResult> VerifyDaxEquivalence(IEngine engine,
            [Description("Original scalar DAX expression (e.g. the current measure body)")] string exprA,
            [Description("Candidate rewrite scalar DAX expression")] string exprB,
            [Description("Group-by columns forming the filter-context matrix, e.g. [\"'Date'[Year]\", \"'Product'[Category]\"]")] string[] groupBy,
            [Description("Optional table filters applied to the whole comparison, e.g. [\"KEEPFILTERS('Date'[Year]=2024)\"]")] string[] filters = null,
            [Description("Max matrix rows to compare (default 100000)")] int maxRows = 100000)
        {
            var r = await engine.VerifyEquivalenceAsync(exprA, exprB, groupBy, filters, maxRows);
            Emit(engine, new ActivityEvent { Kind = "verify_equivalence", Origin = "agent", Label = EquivalenceLabel(r, groupBy),
                Query = TruncDax(exprB), Ok = string.IsNullOrEmpty(r.Error), Error = r.Error, Result = r });
            return r;
        }

        // The activity label follows the ONE evidence ladder (DaxBench.ClassifyEquivalenceEvidence) — "Verified"
        // is reserved for the proven/failed rungs where the comparison itself is authoritative. Everything else
        // (zero rows, truncation, thin grid, degraded fidelity, run failure) says what actually happened, so the
        // feed can't launder a caveat into a proof. Internal for unit tests.
        internal static string EquivalenceLabel(EquivalenceResult r, string[] groupBy = null)
        {
            if (r == null) return "Compared a rewrite";
            var (state, why) = DaxBench.ClassifyEquivalenceEvidence(r, DaxBench.NormalizeGroupBy(groupBy).Length);
            static string Short(string s) => string.IsNullOrEmpty(s) ? "" : s.Length > 90 ? s.Substring(0, 87) + "..." : s;
            return state switch
            {
                "proven" => "Verified a rewrite: equivalent",
                "failed" => "Verified a rewrite: NOT equivalent",
                "degraded_mismatch" => "Compared a rewrite (degraded): difference observed, not authoritative",
                "degraded" => "Compared a rewrite (degraded: " + Short(r.Fidelity) + "): values matched, NOT verified",
                "thin" => "Compared a rewrite: matched at the grand total only, NOT verified",
                _ => "Compared a rewrite: could not verify (" + Short(why) + ")",
            };
        }

        public static async Task<OptimizeMeasureResult> OptimizeMeasure(IEngine engine,
            string measureRef,
            string[] candidates,
            string[] verifyGroupBy,
            string[] verifyFilters = null,
            bool apply = true)
        {
            return await OptimizeMeasureCore(engine, measureRef, candidates, verifyGroupBy, verifyFilters, apply);
        }

        private static async Task<OptimizeMeasureResult> OptimizeMeasureCore(IEngine engine, string measureRef, string[] candidates, string[] verifyGroupBy, string[] verifyFilters, bool apply)
        {
            var r = await engine.OptimizeMeasureAsync(measureRef, candidates, verifyGroupBy, verifyFilters, apply, "agent");
            Emit(engine, new ActivityEvent { Kind = "optimize_measure", Origin = "agent",
                Label = r.Applied ? $"Optimized {measureRef}: applied candidate {r.WinnerIndex}" : $"Optimized {measureRef}: {r.Verdict}",
                Target = measureRef, Query = TruncDax(r.WinnerExpression), Ok = string.IsNullOrEmpty(r.Error), Error = r.Error, Result = r });
            return r;
        }

        [McpServerTool(Name = "optimize_measure"), Description("VERIFIED EDITS: optimize a measure with evidence. Give 2 or more candidate DAX rewrites; the engine validates each (a candidate that references the measure itself is rejected), checks each returns identical values to the current body across the SUMMARIZECOLUMNS(verifyGroupBy) matrix you supply, benchmarks only the ones that matched (warm wall-clock over that grid, not a server FE/SE trace), and, when apply is true (the default), applies the fastest that beats the current body beyond a measured noise band as one undoable edit, recording the proof and benchmark as evidence. apply=false is a dry run that returns the same evidence and changes nothing. Equivalence is only as strong as verifyGroupBy, which must be a JSON list of text column names: a grand-total-only match is downgraded to unverified and never applied, so pass representative group-by columns. Nothing is applied with fewer than 2 valid candidates, without a live connection (open_live or open_local), or when no candidate is proven; a candidate that matched only under degraded fidelity is reported, not applied. Free on every tier.")]
        public static Task<OptimizeMeasureResult> OptimizeMeasureWire(IEngine engine,
            [Description("Measure/calc ref, e.g. 'measure:Sales/Total Sales'")] string measureRef,
            [Description(">=2 candidate DAX rewrites to race")] string[] candidates,
            [Description("A JSON list of text group-by columns, e.g. [\"'Date'[Year]\"]")] JsonElement verifyGroupBy,
            [Description("Optional table filters applied to the comparison")] string[] verifyFilters = null,
            [Description("true (default) applies the fastest proven winner as one undoable edit; false = dry run: return the evidence, change nothing")] bool apply = true)
            => OptimizeMeasureCore(engine, measureRef, candidates, JsonTextList(verifyGroupBy, "verifyGroupBy"), verifyFilters, apply);

        [McpServerTool(Name = "probe_measure"), Description("VERIFIED EDITS: run a measure across MANY filter contexts to gather EVIDENCE before you trust it (value/BLANK/ERROR per member, non-blank coverage, additivity) across a scenario matrix. DAX context-dependence bugs (missing time-intel guards, blanks, non-additivity) are INVISIBLE until you run the measure in different contexts. It replicates a real visual (SUMMARIZECOLUMNS with your filter args as the OUTER/slicer context + a group-by axis) so ALLSELECTED / SELECTEDVALUE / ISINSCOPE resolve correctly. A naive one-context test lies about those. Pass a primaryAxis to auto-generate grand/single/multi/empty/boundary scenarios, and/or scenarios[] for cross-filter cases (name columns + members; do NOT hand-write DAX). Reports BEHAVIOR across contexts, NEVER 'correct'. You judge it against intent. Read-only; needs a live connection.")]
        public static async Task<ProbeResult> ProbeMeasure(IEngine engine,
            [Description("The measure DAX expression to probe (inlined; not registered in the model)")] string expr,
            [Description("A column to auto-generate default scenarios from, e.g. \"'Date'[Month]\" (optional if you pass scenarios[])")] string primaryAxis = null,
            [Description("Optional agent-authored scenarios: each {name, groupBy:[cols], filters:[{column, members:[...], empty?}]}")] ProbeScenario[] scenarios = null,
            [Description("Also append the engine's default scenarios (grand/single/multi/empty/boundary) from primaryAxis")] bool includeDefaults = true)
        {
            var r = await engine.ProbeMeasureAsync(expr, primaryAxis, scenarios, includeDefaults, 5000);
            Emit(engine, new ActivityEvent { Kind = "probe_measure", Origin = "agent",
                Label = r.Status == "ok" ? $"Probed a measure across {r.ScenariosRun} contexts" : $"Probe {r.Status}",
                Query = TruncDax(expr), Ok = r.Status == "ok" || r.Status == "no-connection", Error = r.Status == "ok" ? null : r.Message, Result = r });
            return r;
        }

        [McpServerTool(Name = "capture_baseline"), Description("VERIFIED EDITS: value-capture at edit-START (run this BEFORE a risky structural edit: rename, retype, relationship change, delete, M rewrite). Freezes the MEASURED values of the object's blast radius (the lineage-downstream measures, evaluated BY REFERENCE over the SUMMARIZECOLUMNS(groupBy) grid you supply) into a session-held baseline, and returns a captureId. After the edit, compare_baseline re-evaluates the same grid and reports exactly which numbers moved. Pass representative groupBy columns: a grand-total-only baseline is thin evidence (disclosed in the result). Over-cap measures are listed in Skipped, never silently dropped. CERTIFIED TOTALS (month-end close): pass a `label` (the period, e.g. \"close FY26 · July\") to also PERSIST this capture beside the model as a certified baseline: one control total per call (objRef = the measure, filters = its stated context, includeDependents:false), all under the same label. That certified set survives the session, so a later refresh or edit that moves a signed-off number is caught by compare_baseline(label:…). needs a live connection (a baseline is measured values, not metadata).")]
        public static async Task<BaselineCaptureResult> CaptureBaseline(IEngine engine,
            [Description("The object you are ABOUT TO CHANGE, e.g. 'column:Sales/Amount', 'measure:Sales/Total Sales', 'table:Sales'")] string objRef,
            [Description("Group-by columns forming the evidence grid, e.g. [\"'Date'[Year]\", \"'Product'[Category]\"]. Representative coverage is what makes the later compare meaningful. For a certified TOTAL, leave empty (a single number at a stated context).")] string[] groupBy = null,
            [Description("Table filters applied to the whole grid. For a certified total, these ARE the stated context, e.g. [\"'Date'[Fiscal Year]=\\\"FY26\\\"\", \"'Date'[Month]=\\\"July\\\"\"].")] string[] filters = null,
            [Description("Also capture every lineage-downstream measure (the blast radius). false = only the ref itself (must be a measure). Use false for a certified control total.")] bool includeDependents = true,
            [Description("Cap on captured measures (overflow reported in Skipped)")] int maxMeasures = 25,
            [Description("Certified-totals label (month-end close): the period, e.g. \"close FY26 · July\". When set, the capture is also PERSISTED beside the model under this label so it outlives the session (merge-by-ref across calls). Omit for an ordinary session-only baseline.")] string label = null)
        {
            var r = await engine.CaptureBaselineAsync(objRef, groupBy, filters, includeDependents, maxMeasures, 2000, label, "agent");
            // The session capture may succeed while the CERTIFICATION is refused (immutable / tamper / not-evaluable /
            // volatile) — the summary must reflect the certification outcome, not just "ok" (P2-9: match the verdict).
            var certRefused = !string.IsNullOrWhiteSpace(label) && (r.Message?.Contains("NOT CERTIFIED", StringComparison.Ordinal) ?? false);
            Emit(engine, new ActivityEvent { Kind = "capture_baseline", Origin = "agent",
                Label = r.Status != "ok" ? $"Baseline capture {r.Status}"
                    : string.IsNullOrWhiteSpace(label) ? $"Captured a baseline of {r.Entries.Length} measure(s) for {objRef}"
                    : certRefused ? $"Certification REFUSED under '{label.Trim()}': {objRef}"
                    : $"Certified figures under '{label.Trim()}': {objRef}",
                // P2-E / P3-3: Ok reflects the VERDICT — a refused certification is not Ok, and a LABELED capture that
                // certified nothing offline (no-connection) is not Ok either (only an unlabeled offline probe is an
                // expected no-op).
                Target = objRef, Ok = (r.Status == "ok" && !certRefused) || (r.Status == "no-connection" && string.IsNullOrWhiteSpace(label)),
                Error = certRefused ? r.Message : (r.Status == "ok" ? null : r.Message), Result = r });
            return r;
        }

        [McpServerTool(Name = "compare_baseline"), Description("VERIFIED EDITS: the edit-END half of capture_baseline: re-evaluates a captured baseline's grid on the LIVE model and reports per measure whether its numbers are unchanged, MOVED (with the exact contexts and before→after values), or MISSING (the measure no longer resolves. That is an impact, not a skip). Safe=true only when everything is unchanged, nothing is missing/errored, AND coverage wasn't truncated, honesty first. IMPORTANT: it reads the LIVE model, so run it AFTER the edit is deployed (deploy_live). An undeployed local edit is not validated (the result discloses session edits since capture). CERTIFIED TOTALS (month-end close): pass a `label` instead of a captureId to re-check that period's PERSISTED certified figures. Each is re-evaluated at ITS stated context and reported held / moved (old→new) / missing / not-checkable, so drift from what was signed off is named. Detection, not prevention: it cannot stop a refresh or an out-of-tool edit, only report what moved. The verdict is recorded in the Verified Edits audit trail. needs the live connection.")]
        public static async Task<BaselineCompareResult> CompareBaseline(IEngine engine,
            [Description("The captureId from capture_baseline (omit for the most recent session capture)")] string captureId = null,
            [Description("Certified-totals label (month-end close): re-check the persisted certified figures signed off under this period, e.g. \"close FY26 · July\". Takes precedence over captureId.")] string label = null)
        {
            var r = await engine.CompareBaselineAsync(captureId, label, "agent");
            var certified = !string.IsNullOrWhiteSpace(label);
            // P2-9: the summary must match the engine's verdict exactly. When nothing moved/missing but the result is
            // NOT safe, the verdict is NOT-CHECKABLE (identity change / context mismatch / error), never "DRIFT".
            string CertifiedLabel() =>
                r.Safe ? $"Certified figures HELD for '{label.Trim()}' ({r.Diffs.Length} unchanged)"
                : (r.MovedCount == 0 && r.MissingCount == 0) ? $"Certified figures NOT CHECKABLE for '{label.Trim()}'"
                : $"Certified DRIFT for '{label.Trim()}' ({r.MovedCount} moved, {r.MissingCount} missing)";
            string SessionLabel() =>
                r.Safe ? $"Baseline compare: SAFE ({r.Diffs.Length} measures unchanged)"
                : (r.MovedCount == 0 && r.MissingCount == 0) ? "Baseline compare: NOT CHECKABLE"
                : $"Baseline compare: IMPACT ({r.MovedCount} moved, {r.MissingCount} missing)";
            // P2-E: Ok reflects the VERDICT. A NOT-CHECKABLE result (ok status, not safe, nothing moved/missing) is
            // NOT a trustworthy answer, so it is not Ok — only HELD/SAFE or a definitive DRIFT/IMPACT (or the expected
            // offline state) count as Ok.
            var notCheckable = r.Status == "ok" && !r.Safe && r.MovedCount == 0 && r.MissingCount == 0;
            Emit(engine, new ActivityEvent { Kind = "compare_baseline", Origin = "agent",
                Label = r.Status == "ok" ? (certified ? CertifiedLabel() : SessionLabel()) : $"Baseline compare {r.Status}",
                Target = r.Root, Ok = (r.Status == "ok" && !notCheckable) || r.Status == "no-connection",
                Error = notCheckable ? r.Message : (r.Status == "ok" ? null : r.Message), Result = r });
            return r;
        }

        [McpServerTool(Name = "blame_value"), Description("NUMBER TIME-MACHINE: answer \"what moved this number?\" deterministically. The engine ambiently snapshots the top measures' values + their whole DAX dependency-cone expressions at checkpoint moments (apply_plan / optimize_measure / deploy_live / save_model, automatic); this op finds the most recent movement of a measure (or since a given point) and attributes it HONESTLY: verdict 'attributed' when exactly ONE recorded edit sits in the window; 'interval' when several do: candidates come RANKED by dependency overlap (formula-changing edits first) and are a ranking, NEVER a causal claim; 'data-suspected' when every formula is identical but the value moved (data/refresh is the suspect. Proving it needs a shadow pre-edit instance, which v1 does not keep); 'inconclusive' when the history can't answer. Formula-caused movement is proven by a deterministic expression diff of the dependency cone between the two points (ExprDiffs). Free. Read-only.")]
        public static async Task<BlameResult> BlameValue(IEngine engine,
            [Description("The measure whose number moved, e.g. 'measure:Sales/Total Sales'")] string measureRef,
            [Description("Which cell to explain: omit for the grand total ('(model context)'), or a recorded slice key from list_value_history, e.g. \"Product[Category]=Audio\"")] string context = null,
            [Description("Explain the movement since this recorded point instead of the most recent one: a revision number or an ISO timestamp from list_value_history")] string sinceCheckpoint = null)
        {
            // No Emit here: the ENGINE publishes the Kind="blame_value" activity itself (both doors get the
            // same shared evidence record — the compare_baseline precedent taken one step further), so an
            // MCP-side emit would double-log the L0 stream.
            return await engine.BlameValueAsync(measureRef, context, sinceCheckpoint, "agent");
        }

        [McpServerTool(Name = "list_value_history"), Description("NUMBER TIME-MACHINE: the recorded values of one measure across every checkpoint moment (apply_plan / optimize_measure / deploy_live / save_model), the raw time series behind blame_value, and the UI sparkline's feed. Points with a null value are honest gaps: the formulas were snapshotted but the number wasn't observed there (offline capture, or the edit's impact cone never reached the measure). Truncated=true means retention pruned older points (last 200 checkpoints / 20MB) or unreadable lines were skipped. Free, read-only.")]
        public static async Task<ValueHistoryResult> ListValueHistory(IEngine engine,
            [Description("Measure ref, e.g. 'measure:Sales/Total Sales'")] string measureRef,
            [Description("Which cell's history: omit for the grand total, or a recorded slice key, e.g. \"Product[Category]=Audio\"")] string context = null)
        {
            var r = await engine.ListValueHistoryAsync(measureRef, context);
            Emit(engine, new ActivityEvent { Kind = "list_value_history", Origin = "agent",
                Label = r.Status == "ok" ? $"Value history of {measureRef}: {r.Points.Length} point(s)" : $"Value history {r.Status}",
                Target = measureRef, Ok = r.Status != "error", Error = r.Status == "error" ? r.Note : null, RowCount = r.Points?.Length, Result = r });
            return r;
        }

        [McpServerTool(Name = "explain_value"), Description("EXPLAIN THIS NUMBER: a deterministic evidence dossier for ONE cell of ONE measure: the value re-derived in the cell's exact filter context (the same SUMMARIZECOLUMNS shape a real visual runs), the measure's dependency chain (what feeds it), source lineage (where each leaf column's data comes from), TOP CONTRIBUTORS split by a related dimension, and (when the value is BLANK) a why-is-this-blank checklist (filters remove every row / no relationship path / filter flows the wrong way / inactive relationship / blank by design / RLS advisory) with the likely cause named. HONESTY RULES: contributors ship ONLY when the parts provably sum to the total (distinct counts / ratios / min-max / semi-additive measures get an explicit refusal, never a misleading breakdown); RLS is listed as roles that WOULD filter these tables (the engine cannot impersonate a role, so never 'computed under role X'). Describe the cell with filterContext: Filters = column+members (a pivot cell's row/col keys are single-member filters), ExtraPredicates = raw DAX boolean lines (page-filter style). OFFLINE the dossier degrades to chain/lineage/RLS with Evidence.Available=false. Connect (open_local / open_live) for the value, blank checks, and breakdown. Next steps: probe_measure to test the measure across MANY contexts; blame_value to ask what CHANGED a number over time. Free, read-only.")]
        public static async Task<ExplainDossier> ExplainValue(IEngine engine,
            [Description("The measure, e.g. 'measure:Sales/Total Sales' (a unique bare name like 'Total Sales' also works)")] string measureRef,
            [Description("The cell's filter context: { filters: [{column: \"'Table'[Column]\", members: [\"…\"], empty: false}], extraPredicates: [\"'Date'[Year] >= 2024\"], groupBy: [] }. Omit for the grand total.")] ExplainFilterContext filterContext = null,
            [Description("Compute the top-contributors breakdown (default true; it is skipped automatically when the parts don't sum)")] bool decompose = true,
            [Description("Break the number down by this column ('Table'[Column]); omit to let the engine pick a related dimension deterministically")] string decomposeBy = null,
            [Description("How many top contributors to return (default 5, max 25)")] int topK = 5)
        {
            // No Emit here: the ENGINE publishes the Kind="explain_value" activity itself (the blame_value
            // precedent — both doors get the same dossier), so an MCP-side emit would double-log the L0 stream.
            return await engine.ExplainValueAsync(measureRef, filterContext, decompose, decomposeBy, topK, "agent");
        }

        [McpServerTool(Name = "list_verified_edits"), Description("VERIFIED EDITS: read the append-only, hash-chained audit trail persisted ON the model: every verified op (or accountable override) with its verdict, evidence, and any override reason, in chain order. Each record links the previous by hash, so the trail self-checks: ChainIntact=false (with FirstBrokenSeq) means a record was edited, removed, or reordered after it was written. Read-only, free.")]
        public static Task<VerifiedEditsChain> ListVerifiedEdits(IEngine engine) => engine.ListVerifiedEditsAsync();

        [McpServerTool(Name = "export_verified_edits"), Description("VERIFIED EDITS: export the model's audit trail as a shareable report: 'md' (default) for a human-readable markdown summary (boss/auditor) or 'json' for the raw serialized chain (CI/tooling). Free, read-only.")]
        public static Task<string> ExportVerifiedEdits(IEngine engine,
            [Description("Output format: 'md' (human markdown, default) or 'json' (the serialized chain for CI)")] string format = "md")
            => engine.ExportVerifiedEditsAsync(format);

        // ---- The workflow engine (enforced, evidence-verified playbooks). Workflows is a Semanticus Pro feature. ----

        [McpServerTool(Name = "list_workflows"), Description("WORKFLOWS: list the workflows: your own in this project's `.semanticus/workflows/*.md` plus the starters shipped with the engine (a project file shadows a starter of the same name; files are re-read on every call, so edits are live). Each entry shows step count, whether it is GATED, starter (a shipped copy to start from; instantiate_workflow_template copies it), required (a team rule names it), replacesRequired (your copy shadows the shipped version a team rule requires) and any parse error (a malformed file is surfaced here, never hidden). Workflows is a Semanticus Pro feature, reads included. Read-only.")]
        public static Task<WorkflowInfo[]> ListWorkflows(IEngine engine) => engine.ListWorkflowsAsync();

        [McpServerTool(Name = "get_workflow_enforcement"), Description("WORKFLOWS: read the model-wide enforcement mode, the user's kill-switch over gate strictness. null mode = no override (each workflow's own strictness applies, engine default hard); 'off' = every gate is skipped (runs record no verified evidence); 'warn'/'hard' force that strictness everywhere. Workflows is a Semanticus Pro feature, reads included. Read-only.")]
        public static Task<WorkflowEnforcement> GetWorkflowEnforcement(IEngine engine) => engine.GetWorkflowEnforcementAsync();

        [McpServerTool(Name = "set_workflow_enforcement"), Description("WORKFLOWS: set (or clear) the model-wide enforcement mode. mode='off' disables every gate, for quick tasks where the user explicitly doesn't want enforced workflows; mode='default' (or null) restores per-definition strictness; 'hard'/'warn' force that level everywhere. Sits at the TOP of the strictness resolution (above per-gate and per-workflow overrides), is persisted in .semanticus/workflow-settings.json beside the model, and the VS Code views update at once. You see the change on your next call. Ask the user before turning enforcement off. It is their accountability lever, not yours. Workflows is a Semanticus Pro feature. As the assistant you can only set 'hard'. Relaxing enforcement is the person's, and you are told to ask them.")]
        public static Task<WorkflowEnforcement> SetWorkflowEnforcement(IEngine engine,
            [Description("'hard' | 'warn' | 'off' | 'default' (clear the override)")] string mode)
            => engine.SetWorkflowEnforcementAsync(mode, "agent");

        [McpServerTool(Name = "set_workflow_enabled"), Description("WORKFLOWS: turn a workflow ON or OFF for this project, the availability toggle (which workflows are on the menu). Disabled = hidden from the run picker and refused by start_workflow, but STILL listed (marked enabled:false) so it can be re-enabled. Persisted in .semanticus/workflow-settings.json beside the model (git-tracked, so the curation travels with the repo). The VS Code views update at once; you see the change on your next call. Orthogonal to strictness (how hard gates bite) and to whether a workflow is required for a task. Curating your menu is content. Workflows is a Semanticus Pro feature. As the assistant you can only turn a workflow on. Turning one off is the person's, and you are told to ask them.")]
        public static Task<WorkflowInfo[]> SetWorkflowEnabled(IEngine engine,
            [Description("The workflow name from list_workflows.")] string name,
            [Description("true = on the menu (default); false = hidden and unstartable.")] bool enabled)
            => engine.SetWorkflowEnabledAsync(name, enabled, "agent");

        [McpServerTool(Name = "set_workflow_binding"), Description("WORKFLOWS: require an op to route through a workflow, the \"Required for…\" control (the third axis: availability · REQUIRED · strictness). require is the allowed workflow set (you pick among them by whenToUse); mode 'hard' REFUSES the bare op and steers you to start_workflow, 'warn' allows it but records a compliance advisory, 'off' (or an empty require) CLEARS the binding. The bare op is exempt only while an active run of a required workflow is AT a step that performs it (start-and-freestyle does not satisfy the mandate). Persisted in .semanticus/workflow-settings.json beside the model (git-tracked, so the mandate travels with the repo). The VS Code views update at once; you see the change on your next call. Independent of the strictness kill-switch. A mandate is whether you must ENTER a run, not how hard its gates bite. Workflows is a Semanticus Pro feature, reads included. Clearing is refused while the binding is locked team policy. get_workflow_policy shows the current rules. As the assistant you can only make a requirement stricter (a new one, a stronger mode, fewer ways to satisfy it). Taking one off or loosening it is the person's, and you are told to ask them. Under the Locked policy a stricter one asks first like any file write.")]
        public static Task<WorkflowInfo[]> SetWorkflowBinding(IEngine engine,
            [Description("The op to route, e.g. 'create_measure' (bindable set: create_measure, update_measure, create_calculated_column, create_calculation_item, create_table, create_relationship)")] string op,
            [Description("The allowed workflow names (from list_workflows). Empty clears the binding.")] string[] require = null,
            [Description("'hard' (refuse the bare op) | 'warn' (allow + record) | 'off' (clear). Default 'off'.")] string mode = "off")
            => engine.SetWorkflowBindingAsync(op, require, mode, "agent");

        [McpServerTool(Name = "set_workflow_activation"), Description("WORKFLOWS: show a workflow only when a condition holds, the dynamic-activation control (which workflows are on the CURRENT menu). e.g. show 'deploy-freeze-guard' only during the month-end window, or 'prod-checklist' only on the prod workspace. `when` is a plain condition over facts the engine knows: date (date.monthEndOffset >= -3, date.dayOfMonth >= 28), connection (connection.workspace ~ '*prod*', connection.kind == 'xmla'), git (git.branch == 'main'), the model (model.tableCount > 50, model.hasRls == true, model.readinessGrade < 'B'), or the session (session.tier == 'pro'), joined with && / || (no parentheses; split into separate rules for grouping). set='on' shows it only when the condition holds; set='off' hides it then. Passing neither `when` nor `set` CLEARS the rule (it shows normally again). Activation CURATES the menu. It is NOT a lock: a hidden workflow is still startable on demand, and a workflow REQUIRED by a binding is always shown. Persisted in .semanticus/workflow-settings.json (git-tracked). The VS Code views update at once; you see the change on your next call. Workflows is a Semanticus Pro feature, reads included. get_workflow_policy shows the current rules + any contradictions. As the assistant you can show a workflow with a rule but not hide one: a rule with set='off' is the person's, and you are told to ask them.")]
        public static Task<WorkflowInfo[]> SetWorkflowActivation(IEngine engine,
            [Description("The workflow name from list_workflows.")] string workflow,
            [Description("The condition, e.g. \"date.monthEndOffset >= -3\" or \"connection.workspace ~ '*prod*'\". Omit for an unconditional rule; omit BOTH when and set to clear the rule.")] string when = null,
            [Description("'on' = show it only when the condition holds; 'off' = hide it when the condition holds. Omit (with when) to clear.")] string set = null)
            => engine.SetWorkflowActivationAsync(workflow, when, set, "agent");

        [McpServerTool(Name = "get_workflow_policy"), Description("WORKFLOWS: this project's whole workflow POLICY in one compact read: the model-wide enforcement mode, one row per workflow (on the menu? gated? its whenToUse hint, and the ops that REQUIRE it), and the raw op→workflow bindings (op, required set, mode, whether locked team policy). Token-lean (no step bodies): read it BEFORE authoring so you self-route into a required workflow instead of hitting a mandate by rejection. Read-only. Workflows is a Semanticus Pro feature.")]
        public static Task<WorkflowPolicy> GetWorkflowPolicy(IEngine engine) => engine.GetWorkflowPolicyAsync();

        [McpServerTool(Name = "list_workflow_profiles"), Description("WORKFLOWS: list the sets of team rules available in Studio (Team standard, Consulting delivery, No team rules), what each changes in plain language, whether it requires Pro, and which one is selected. A set of team rules is one reviewed bundle of menu, requirement and check-strength settings; 'standard' is No team rules. Read-only. Use activate_workflow_profile to apply one atomically. Workflows is a Semanticus Pro feature.")]
        public static async Task<WorkflowProfileInfo[]> ListWorkflowProfiles(IEngine engine)
        {
            var profiles = await engine.ListWorkflowProfilesAsync();
            Emit(engine, new ActivityEvent { Kind = "list_workflow_profiles", Origin = "agent", Label = "Reviewed the sets of team rules", Ok = true });
            return profiles;
        }

        [McpServerTool(Name = "activate_workflow_profile"), Description("WORKFLOWS: atomically replace the project's team rules with one named set from list_workflow_profiles. standard (No team rules) clears prior menu rules, requirements and automatic visibility rules. team-standard (Team standard) and consulting-delivery (Consulting delivery) require Pro because they make workflows required. The settings remain source-controlled and any later manual policy edit marks the set Custom. Workflows is a Semanticus Pro feature. Replacing the team rules can loosen them, so it is the person's: you are told to ask them.")]
        public static Task<WorkflowProfileResult> ActivateWorkflowProfile(IEngine engine,
            [Description("The set's name from list_workflow_profiles: standard, team-standard, or consulting-delivery.")] string name)
            => engine.ActivateWorkflowProfileAsync(name, "agent");

        [McpServerTool(Name = "get_workflow"), Description("WORKFLOWS: read one workflow's FULL definition: every step's instruction text, gate inputs (the questions you must ask the user), and verify checks. use it to follow a workflow by hand, or to preview what start_workflow will enforce. To customise a starter (a shipped workflow), call instantiate_workflow_template with its name: that copies it into this project as your workflow, and the copy is what you edit. If a team rule requires that workflow, nothing is copied and the required one is returned as it is. Workflows is a Semanticus Pro feature.")]
        public static Task<WorkflowDef> GetWorkflow(IEngine engine,
            [Description("The workflow name from list_workflows, e.g. 'optimize-dax'")] string name)
            => engine.GetWorkflowAsync(name);

        [McpServerTool(Name = "get_workflow_document"), Description("WORKFLOWS: read the exact UTF-8 source text of one saved workflow, its disk byte hash, parse metadata and engine-owned editModel projection. User files shadow stock files. editModel contains every supported field, opaque edit keys, restrictions and finite choices; it is absent when broken source cannot be projected. Keep path and byteHash for preview_workflow_edit and edit_workflow_document. Broken workflow syntax remains readable for Source repair; invalid UTF-8 is refused without changing bytes. This is a next-call disk read; agents receive no workflow library push notifications. Workflows is a Semanticus Pro feature.")]
        public static Task<WorkflowDocumentResult> GetWorkflowDocument(IEngine engine,
            [Description("Kebab-case workflow name from list_workflows.")] string name,
            [Description("Optional sessionId from model_overview; refuses if that model is no longer current.")] string sessionId = null)
            => engine.GetWorkflowDocumentAsync(name, sessionId);

        [McpServerTool(Name = "preview_workflow_edit"), Description("WORKFLOWS: project and preview typed edits against exact workflow source without writing. Pass the saved path and byteHash from get_workflow_document, plus editsJson as an ordered JSON array. Optional draftText is the unsaved Source/Steps/Canvas draft. Returns the exact proposed text, byte hash, full-context diff, complete edit model, warnings, key changes and the existing writer call to use after review. Invalid, stale, ambiguous and stock edits keep the draft and offer no apply action. For a new workflow set create:true, omit path/hash, then apply through save_workflow with createOnly:true. Structural edits to positional step ids need stabilize_step_ids earlier in the same list. Preview adds no approval gate; existing save and admission rules remain authoritative. Read-only. Workflows is a Semanticus Pro feature.")]
        public static Task<WorkflowEditPreviewResult> PreviewWorkflowEdit(IEngine engine,
            [Description("Kebab-case workflow name.")] string name,
            [Description("Saved document byteHash. Required for edits; omit for create:true.")] string expectByteHash = null,
            [Description("Saved document absolute path. Required for edits; omit for create:true.")] string expectPath = null,
            [Description("JSON array of typed edit operations. Use [] to project an unsaved source draft without changing it.")] string editsJson = "[]",
            [Description("Optional exact unsaved source draft shared by Source, Steps and Canvas.")] string draftText = null,
            [Description("True only when previewing a new user workflow; path and hash must be absent.")] bool create = false,
            [Description("Optional sessionId from model_overview; refuses if that model is no longer current.")] string sessionId = null)
            => engine.PreviewWorkflowEditAsync(name, expectByteHash, expectPath, editsJson, draftText, create, sessionId);

        [McpServerTool(Name = "upgrade_workflow"), Description("WORKFLOWS: preview a deterministic version-1-to-version-2 source upgrade. Defaults to dryRun:true and writes nothing. Adds stable step ids and inserts schemaVersion:2, or promotes and moves an existing active version-1 declaration first. Preserves all unrelated source bytes, line endings and comments. Returns exact proposedText, a full-context diff and parse/semantic-drift refusals. Review that preview before dryRun:false; applying requires its document.path as expectPath and document.byteHash as expectByteHash. Optional preview fences are honored too. Stock previews are allowed but apply is read-only: create a project copy first. Existing valid version-2 files are no-ops. changed means an actual write; canApply identifies a safe user-file proposal. The Studio library updates after apply; agents see changes on their next read. File upgrades are outside model undo, and an external editor can still race between the final hash check and atomic replacement. Workflows is a Semanticus Pro feature.")]
        public static Task<WorkflowUpgradeResult> UpgradeWorkflow(IEngine engine,
            [Description("Kebab-case workflow name.")] string name,
            [Description("Default true: return a preview without writing. False applies only with the reviewed source path and hash.")] bool dryRun = true,
            [Description("Preview document.byteHash. Required for apply; when supplied on a preview it fences that read too.")] string expectByteHash = null,
            [Description("Preview document.path. Required for apply; prevents an identical same-name file in a different project from being upgraded.")] string expectPath = null,
            [Description("Optional sessionId from model_overview; refuses if that model is no longer current.")] string sessionId = null)
            => engine.UpgradeWorkflowAsync(name, dryRun, expectByteHash, expectPath, "agent", sessionId);

        [McpServerTool(Name = "edit_workflow_document"), Description("WORKFLOWS: replace a saved USER workflow's full source text after comparing its current disk byte hash with expectByteHash from get_workflow_document. Stale hashes, parse errors and name changes refuse without writing; reconcile your draft with the returned current document before retrying. Exact UTF-8 text, including BOM, line endings and trailing whitespace, is preserved. Stock files are read-only; save_workflow with createOnly:true creates a project copy. Identical bytes cause no write or library broadcast. Successful edits update the Studio library; agents reread on their next call. An external editor can still race in the tiny final hash-check-to-replace interval. file edits are outside model undo. Workflows is a Semanticus Pro feature.")]
        public static async Task<WorkflowDocumentEditResult> EditWorkflowDocument(IEngine engine,
            [Description("Kebab-case user workflow name.")] string name,
            [Description("Required exact byteHash returned by get_workflow_document.")] string expectByteHash,
            [Description("Complete replacement source text. Do not normalize line endings or remove a leading BOM unless intended.")] string exactText,
            [Description("Required exact path returned by get_workflow_document; refuses if the active project now resolves this name to another file.")] string expectPath,
            [Description("Optional sessionId from model_overview; refuses if that model is no longer current.")] string sessionId = null)
        {
            var r = await engine.EditWorkflowDocumentAsync(name, expectByteHash, exactText, expectPath, "agent", sessionId);
            if (r.Changed)
                Emit(engine, new ActivityEvent { Kind = "edit_workflow_document", Origin = "agent", Label = $"Edited workflow '{name}'", Target = name, Ok = true });
            return r;
        }

        [McpServerTool(Name = "start_workflow"), Description("WORKFLOWS: start an ENFORCED run of a workflow. Returns the run id plus the current step. For an ordinary first step that is its full instructions and its gate questions: ask the USER those questions (do not invent answers), do the step's work with the normal tools, then submit_workflow_step. A file whose steps carry forEach: now starts too. If the first row IS an unexpanded loop, what comes back is that loop's SETUP call, not body work: it expands the loop and nothing else, so do not do iteration work yet, and expect no gate, no verification and no receipt until the run reaches the first applicable iteration, which is <loop>#0 only when #0's own when: holds. submit_workflow_step describes the three setup shapes. Calls run inside the same run and certificate. Their steps are expanded at start; a loop carrying call: expands a separate call for each item during loop setup. Submit a call header to check its gate and pass its declared with: inputs. Only declared returns: answers come back to its caller. Use the exact current stepId, including its call and iteration qualifiers. providedAnswers contains values already available for the current questions; ask only for missing information or corrections. The engine (not you) evaluates each gate: required inputs must be answered or explicitly declined, and verify checks (probe / equivalence / BPA / readiness) run against the live model. Every transition broadcasts to the Studio UI; the finished run's full record (answers, declines, evidence) is appended to the experience log. Workflows is a Semanticus Pro feature, reads included.")]
        public static async Task<WorkflowRunView> StartWorkflow(IEngine engine,
            [Description("The workflow name from list_workflows")] string name,
            [Description("Optional. Run once: the workflow's full text as edited and not saved yet; the run uses it instead of the saved file. Refused for a workflow a team rule requires, which runs only as saved.")] string document = null)
        {
            var r = await engine.StartWorkflowAsync(name, "agent", document);
            var total = r.TotalStepsProvisional == true
                ? $"at least {r.TotalSteps} steps"
                : $"{r.TotalSteps} steps";
            Emit(engine, new ActivityEvent { Kind = "start_workflow", Origin = "agent", Label = $"Started workflow '{r.Workflow}' ({total})", Target = r.Workflow, Ok = true, Result = r });
            return r;
        }

        [McpServerTool(Name = "get_workflow_run"), Description("WORKFLOWS: the live state of a run: per-step status (pending/in_progress/passed/not_applicable/skipped/failed), recorded answers and declines, verify evidence (each verify is one of: passed = proven; failed = evidence produced, gate unmet; unavailable = applicable but no authoritative evidence, blocks a hard step, Missing names what was absent; not_applicable = its when: condition did not hold; skipped = legacy advisory non-blocking skip, with a per-shape equivalence breakdown + engine-reported mismatch cells where present), and any adjudication receipts (witness locks + witness/partition revisions). Plus the CURRENT step's full instructions + unanswered questions. Omit runId for the most recent run. Free, read-only.")]
        public static Task<WorkflowRunView> GetWorkflowRun(IEngine engine,
            [Description("The run id from start_workflow (omit for the most recent run)")] string runId = null)
            => engine.GetWorkflowRunAsync(runId);

        public static async Task<WorkflowRunView> SubmitWorkflowStep(IEngine engine,
            string runId = null,
            string stepId = null,
            string answers = null,
            string callGate = null,
            string writes = null)
        {
            var r = await engine.SubmitWorkflowStepAsync(runId, stepId, answers, "agent", callGate, writes);
            Emit(engine, new ActivityEvent { Kind = "submit_workflow_step", Origin = "agent", Label = $"Workflow '{r.Workflow}': submission accepted → {(r.Status == "completed" ? "run COMPLETED" : $"now on {r.CurrentStep?.StepId}")}", Target = r.Workflow, Ok = true, Result = null });
            return r;
        }

        [McpServerTool(Name = "submit_workflow_step"), Description("WORKFLOWS: submit the run's CURRENT step. Since public start admits forEach:, a run can be sitting on an unexpanded loop from its very first row, so the setup shapes below are reachable straight after start_workflow, not only from inside a run. answers is a JSON object keyed by gate-input name: a value, or the explicit decline sentinel {\"declined\": true, \"reason\": \"...\"} (answer-or-decline inputs accept a reasoned decline; required ones do not). An ordinary or iteration submission runs its gate and verification against the live model. A hard gate that fails blocks the step with the failure evidence (fix and re-submit, or skip_workflow_step with a reason); a warn gate records it and passes. THREE current-row shapes are setup calls and each one only expands the loop. A current unexpanded self-sourced loop is a source-only setup call: answer exactly the visible source question. A current unexpanded inline loop is a no-answer setup call: submit the current base id with no answers. A current unexpanded loop whose list is declared on an earlier step rather than on this call is also a no-answer setup call: submit the current base id with no answers, and do not answer the list input here, because it belongs to the step that declares it. Do not do iteration work yet on any of the three. None of them runs the input gate, body verification, or stamps a receipt: no gate, no verification, no receipt until the first applicable iteration. That third call is also how an unanswered or unusable list is reported, so reaching it does not mean the list is settled. Every setup call validates its source BEFORE it handles the condition: missing, declined, disallowed blank, over-bound and cross-frame scope cases refuse before anything is recorded, with skip_workflow_step with a reason as the recorded way past; a source only another frame holds reaches the loop only when its declaration says scope: run, and one behind a closed call frame cannot be reached that way at all, so that refusal does not offer it; only a source declared required: optional and answered blank becomes an empty not_applicable loop. Only after that validation is a loop-free false or unreadable when: recorded, and it creates zero iterations. Answering a list input that the immediately following forEach: step reads is not a setup call: it runs that step's own gate and verify and decides the next loop in the same submission. That loop expands only when the usable-list decision is expand or empty; a loop-free false or unreadable when records applied condition_false and creates no iterations. TotalStepsProvisional remains true for an unexpanded row even after not_applicable. After ANY loop expands, the next current row is the first APPLICABLE iteration, which is <loop>#0 only when #0's own when: holds, and it carries the loop's full authored gate except for a self-sourced source frozen at expansion. A when: that reads loop.index or loop.<as> is judged per iteration, so leading iterations may be marked not_applicable with their own loop facts and the run moves to the next one. That first applicable iteration returns the first real instructions, remaining questions, verify, ops, and receipt. An empty list marks that loop not_applicable and the run moves past it. An unusable list on the declaring submission (unanswered, declined, or longer than maxIterations) is refused by the pre-pair checks this unit owns, before anything is recorded. Answers target the current frame. A call header passes its declared with: values only after its own gate succeeds. The callee sees those inputs plus its own answers; only returns: names return to the immediate caller when the call finishes. A missing input reference refuses the header before it advances. providedAnswers carries current-question values already supplied to this call, so preserve or correct them instead of inventing replacements. Run-scoped answers survive loop iterations inside their own call; they do not cross a call boundary unless explicitly passed or returned. On success you get the resulting current step's instructions. callGate ('on' or 'off') matters only when the step hands off to a child whose gate is off while this call expects values back: 'on' turns the child's gate on for this run. writes (format-3 workflows, a step your assistant does): the step's model changes as an ordered list of {op, args} from the composer's change ops; they are applied as ONE change before the gate runs, so the step is exactly one undo entry, and a write that fails leaves the model as it was. Workflows is a Semanticus Pro feature.")]
        public static Task<WorkflowRunView> SubmitWorkflowStepWire(IEngine engine,
            [Description("The run id (omit for the most recent run)")] string runId = null,
            [Description("The current step id (omit for the current step)")] string stepId = null,
            [Description("A JSON object of answers keyed by question")] JsonElement? answers = null,
            [Description("Text 'on' or 'off' when changing a child gate")] JsonElement? callGate = null,
            [Description("Format-3 direct step: the step's writes as a JSON list of {op, args}, applied as one change")] JsonElement? writes = null)
            => SubmitWorkflowStep(engine, runId, stepId,
                JsonObjectTextOrNull(answers ?? default, "answers"),
                JsonTextOrNull(callGate ?? default, "callGate", "text such as 'on' or 'off'"),
                JsonArrayTextOrNull(writes ?? default, "writes"));

        [McpServerTool(Name = "skip_workflow_step"), Description("WORKFLOWS: skip the run's current step WITH A REASON. The accountable-override shape: never a hard wall, never a silent bypass. The reason is recorded on the run and lands in the experience log with the terminal record. Prefer fixing and re-submitting over skipping a failed hard gate. Workflows is a Semanticus Pro feature.")]
        public static async Task<WorkflowRunView> SkipWorkflowStep(IEngine engine,
            [Description("Why this step is being skipped: recorded, not a formality")] string reason,
            [Description("The run id (omit for the most recent run)")] string runId = null,
            [Description("The current step's id (safety check; omit to mean the current step)")] string stepId = null)
        {
            return await engine.SkipWorkflowStepAsync(runId, stepId, reason, "agent");
        }

        [McpServerTool(Name = "abort_workflow"), Description("WORKFLOWS: abort a run. The partial record (steps passed, answers, declines, evidence so far) is preserved and appended to the experience log. An abandoned run is data, not a deletion. Workflows is a Semanticus Pro feature.")]
        public static Task<WorkflowRunView> AbortWorkflow(IEngine engine,
            [Description("The run id (omit for the most recent run)")] string runId = null,
            [Description("Why the run is being abandoned")] string reason = null)
            => engine.AbortWorkflowAsync(runId, reason, "agent");

        [McpServerTool(Name = "export_workflow_evidence"), Description("WORKFLOWS: export one terminal run as the shared sealed evidence artifact: canonical JSON plus deterministic self-contained HTML and a SHA-256 content signature. Every step, instruction, declared action, answer or explicit decline, gate result, effective strictness, skip reason and abort reason is preserved. The run is bound to the model that owned it and is refused from a different model. Omit runId for the most recent run. Workflows is a Semanticus Pro feature, reads included. Read-only.")]
        public static async Task<Semanticus.Engine.Evidence.EvidenceArtifact> ExportWorkflowEvidence(IEngine engine,
            [Description("The terminal run id from start_workflow; omit for the most recent run.")] string runId = null)
        {
            var r = await engine.ExportWorkflowEvidenceAsync(runId);
            var exported = !string.IsNullOrWhiteSpace(r.Json);
            Emit(engine, new ActivityEvent
            {
                Kind = "export_workflow_evidence",
                Origin = "agent",
                Label = exported ? "Exported workflow evidence" : r.Error != null ? "Workflow evidence export failed" : "Workflow evidence export refused",
                Ok = exported,
                Error = exported ? null : r.Error ?? r.Note,
                Result = exported ? "Evidence artifact generated with content signature " + r.ContentHash : r.Note,
            });
            return r;
        }

        [McpServerTool(Name = "get_workflow_layout"), Description("Read this project's shared workflow Canvas positions keyed by step ID, and revision. Missing layouts use automatic arrangement. Works for stock workflows too. Presentation only; does not change workflow execution. Workflows is a Semanticus Pro feature.")]
        public static Task<WorkflowLayout> GetWorkflowLayout(IEngine engine, string name) => engine.GetWorkflowLayoutAsync(name);

        [McpServerTool(Name = "save_workflow_layout"), Description("Replace shared workflow Canvas positions: {stepId:{x,y}} with finite coordinates. Pass the revision from get_workflow_layout as expectedRevision to reject stale writes. Empty positions resets arrangement. Deleted step IDs are pruned. Stored in the project's .semanticus/workflow-layouts; broadcasts to Studio. Does not edit steps or the model. Workflows is a Semanticus Pro feature.")]
        public static Task<WorkflowLayout> SaveWorkflowLayout(IEngine engine, string name, Dictionary<string, WorkflowPosition> positions, string expectedRevision = null) => engine.SaveWorkflowLayoutAsync(name, positions, expectedRevision);

        [McpServerTool(Name = "save_workflow"), Description("WORKFLOWS: author or edit a workflow. Write the full markdown (frontmatter + '## Step N:' sections + optional yaml-gate fences with inputs/verify/ops) to this project's `.semanticus/workflows/<name>.md`. The engine PARSE-VALIDATES FIRST: a file the parser refuses is never written and the parse error comes back verbatim. Fix and retry. Saving a starter's name creates your project's customised copy (it shadows the starter), except that creating a copy of a workflow a team rule requires is refused, because the copy would quietly replace it. To start from a starter, instantiate_workflow_template copies it. A workflow is a SKILL definition: gate-free = pure instructions; gates make it enforced. broadcasts workflow/libraryDidChange so the Studio library updates live. Workflows is a Semanticus Pro feature.")]
        public static async Task<WorkflowInfo[]> SaveWorkflow(IEngine engine,
            [Description("Kebab-case workflow name: becomes the filename and must equal the frontmatter name")] string name,
            [Description("The complete workflow markdown (see get_workflow on any stock workflow for the format)")] string markdown,
            [Description("When true, create a project copy only if none exists. Refuses instead of replacing another writer's copy. Default false preserves wholesale create/replace behavior.")] bool createOnly = false,
            [Description("Optional sessionId from model_overview or preview_workflow_edit; refuses if that model is no longer current.")] string sessionId = null)
        {
            var r = await engine.SaveWorkflowAsync(name, markdown, "agent", createOnly, sessionId);
            Emit(engine, new ActivityEvent { Kind = "save_workflow", Origin = "agent", Label = $"Saved workflow '{name}'", Target = name, Ok = true });
            return r;
        }

        [McpServerTool(Name = "delete_workflow"), Description("WORKFLOWS: delete a USER workflow from `.semanticus/workflows`. Stock workflows are read-only (deleting a customised copy reverts to the stock version). Workflows is a Semanticus Pro feature.")]
        public static async Task<WorkflowInfo[]> DeleteWorkflow(IEngine engine,
            [Description("The user workflow's name")] string name)
        {
            var r = await engine.DeleteWorkflowAsync(name, "agent");
            Emit(engine, new ActivityEvent { Kind = "delete_workflow", Origin = "agent", Label = $"Deleted workflow '{name}'", Target = name, Ok = true });
            return r;
        }

        // ---- Workflow TEMPLATES (docs/pro-mode-spec.md §10 — the customisation layer: fill-in-your-own-process) ----

        [McpServerTool(Name = "list_workflow_templates"), Description("STARTERS: list the starters that ask setup questions before they become your workflow (templates: stock ones shipped with the engine + this project's `.semanticus/workflow-templates/*.md`; a user file shadows a stock one of the same name). A template is NOT a runnable workflow: it has declared SLOTS you fill once (your KPI dictionary, your close checklist, your freeze window) and instantiate into a concrete workflow. Each entry shows the slot count + names and any parse error (surfaced, never hidden). Read-only. Next: get_workflow_template to read one, instantiate_workflow_template to fill it in. Workflows is a Semanticus Pro feature.")]
        public static Task<WorkflowTemplateInfo[]> ListWorkflowTemplates(IEngine engine) => engine.ListWorkflowTemplatesAsync();

        [McpServerTool(Name = "get_workflow_template"), Description("STARTERS: read one template's full definition, its slot declarations (the questions to ask the user, each with an example) plus the raw markdown body with the {{slot}} references intact, so you can see exactly what will render. Read-only. To fill it in, collect a value for each slot (ask the user the slot's question verbatim) and call instantiate_workflow_template. Workflows is a Semanticus Pro feature.")]
        public static Task<WorkflowTemplate> GetWorkflowTemplate(IEngine engine,
            [Description("The template name from list_workflow_templates, e.g. 'metric-certification'")] string name)
            => engine.GetWorkflowTemplateAsync(name);

        public static async Task<WorkflowInfo[]> InstantiateWorkflowTemplate(IEngine engine,
            string template,
            string name = null,
            string valuesJson = null)
        {
            var r = await engine.InstantiateWorkflowTemplateAsync(template, name, valuesJson, "agent");
            var used = r.FirstOrDefault()?.Name ?? name ?? template;
            Emit(engine, new ActivityEvent { Kind = "instantiate_workflow_template", Origin = "agent", Label = $"Used '{template}' → workflow '{used}'", Target = used, Ok = true });
            return r;
        }

        [McpServerTool(Name = "instantiate_workflow_template"), Description("STARTERS: Use a starter. For a shipped workflow (starter: true on list_workflows) it copies the file create-only into this project's workflows under its own name or `name`, ready to edit; if a team rule requires that workflow, nothing is copied and the required workflow is returned as it is. For a template it fills the template into a concrete, runnable workflow, asking its setup questions once. valuesJson is a JSON object keyed by slot name; each value is the user's answer to that slot's question (get_workflow_template lists them). The engine renders by deterministic text substitution (no inference), renders once more with dummy values, and refuses if your values changed anything the engine enforces (a step, a gate, a check, strictness): slot values may change what a step says and what a question asks, never what it enforces, and the refusal names what changed where. It then runs the same admission check a saved workflow gets and saves the instance with provenance (template, version, date, slot values) so it can be re-instantiated. A missing required slot is refused with the slot's own question. Next: start_workflow(name) to run it. Workflows is a Semanticus Pro feature.")]
        public static Task<WorkflowInfo[]> InstantiateWorkflowTemplateWire(IEngine engine,
            [Description("The starter's name: a shipped workflow from list_workflows or a template from list_workflow_templates")] string template,
            [Description("Kebab-case name for your new workflow; becomes the filename. Omit it to keep the starter's own name.")] string name = null,
            [Description("A JSON object of slot values (templates only)")] JsonElement? valuesJson = null)
            => InstantiateWorkflowTemplate(engine, template, name, JsonObjectTextOrNull(valuesJson ?? default, "valuesJson"));

        [McpServerTool(Name = "save_workflow_template"), Description("STARTERS: author or edit a USER template. Write the full markdown (frontmatter with `kind: template` + a `slots:` block + '## Step N:' sections whose prose/questions reference each slot as {{slotName}}) to `.semanticus/workflow-templates/<name>.md`. PARSE-VALIDATES FIRST (a file the parser refuses is never written; the error returns verbatim), and slot-validates: every {{ref}} must name a declared slot, and every slot needs an example (the trial instantiation renders with it). Saving a stock template's name creates your customised copy (it shadows the stock one). Authoring is content. Next: check_workflow(name) to trial-instantiate, then instantiate_workflow_template to fill it in. Workflows is a Semanticus Pro feature.")]
        public static async Task<WorkflowTemplateInfo[]> SaveWorkflowTemplate(IEngine engine,
            [Description("Kebab-case template name: becomes the filename and must equal the frontmatter name")] string name,
            [Description("The complete template markdown (see get_workflow_template on any stock template for the format)")] string markdown)
        {
            var r = await engine.SaveWorkflowTemplateAsync(name, markdown, "agent");
            Emit(engine, new ActivityEvent { Kind = "save_workflow_template", Origin = "agent", Label = $"Saved template '{name}'", Target = name, Ok = true });
            return r;
        }

        [McpServerTool(Name = "delete_workflow_template"), Description("STARTERS: delete a USER template from `.semanticus/workflow-templates`. Stock templates are read-only (deleting a customised copy reverts to the stock version). Workflows is a Semanticus Pro feature.")]
        public static async Task<WorkflowTemplateInfo[]> DeleteWorkflowTemplate(IEngine engine,
            [Description("The user template's name")] string name)
        {
            var r = await engine.DeleteWorkflowTemplateAsync(name, "agent");
            Emit(engine, new ActivityEvent { Kind = "delete_workflow_template", Origin = "agent", Label = $"Deleted template '{name}'", Target = name, Ok = true });
            return r;
        }

        [McpServerTool(Name = "get_op_catalog"), Description("WORKFLOWS: the engine's own MCP tool catalog (name + one-line description), reflected from the live tool surface. Use it to fill a workflow step's `ops:` action chain or `triggers:` with real op names. Free, read-only.")]
        public static Task<OpInfo[]> GetOpCatalog(IEngine engine) => engine.GetOpCatalogAsync();

        [McpServerTool(Name = "check_workflow"), Description("WORKFLOWS (Learning Loop L4): the admission dry-run for a learned/authored workflow. Replay-of-deterministic-steps is a later layer. Parses the file (parse errors surface as usual), then statically resolves it against the live op surface and its own gate inputs: every triggers:/ops: entry must be a real op; every verify when/probe must name an input some gate collects; probe/equivalence (and object-scoped bpa_clean) verifies need a target objectRef input. Returns Ok plus info/warn findings (a distilled workflow's derived_from provenance surfaces as an info). Ok = parses AND no warn. Read-only. Workflows is a Semanticus Pro feature.")]
        public static Task<WorkflowCheckReport> CheckWorkflow(IEngine engine,
            [Description("The workflow name from list_workflows")] string name)
            => engine.CheckWorkflowAsync(name);

        [McpServerTool(Name = "replay_check_workflow"), Description("WORKFLOWS (Learning Loop L4): the admission layer's EXPENSIVE half: runs check_workflow (parse + op-catalog resolution) THEN a dry_run REHEARSAL of every step op the workflow's exemplar run can drive. Args come from the distilled workflow's `exemplar_answers` frontmatter (the L0 log has no op args; /distill-workflow embeds the exemplar). Each op is REHEARSED (dry_run ran: wouldSucceed + delta count, or the op's own error; the model is untouched, guaranteed rollback), SKIPPED-DENIED (deny-listed/unknown, not a failure), or SKIPPED-UNBINDABLE (a required param the exemplar can't supply, named). Returns Admissible = parses clean AND no rehearsed op would fail. Deliberately does NOT: execute live DAX probes (marked replayable, they need a connection), touch the model, or run sidecar/bookkeeping ops. No exemplar → replay is SKIPPED with an instructive note, never a failure. Read-only. Workflows is a Semanticus Pro feature.")]
        public static Task<WorkflowReplayReport> ReplayCheckWorkflow(IEngine engine,
            [Description("The workflow name from list_workflows")] string name)
            => engine.ReplayCheckWorkflowAsync(name);

        [McpServerTool(Name = "dry_run"), Description("PLAN MODE for the model: rehearse ANY single model-mutating op through its REAL code path and get back the EXACT change set it would make (the model/didChange deltas) plus the op's own return value, with a hard guarantee of NO mutation, NO undo entry, NO broadcast, and NO audit record (the edit is applied, then rolled back). Preview a create_measure / update_measure / rename_object / set_property / define_calendar before committing, or check whether an op WOULD fail. A rehearsal that finds the failure is still a SUCCESSFUL dry-run (WouldSucceed=false with the op's real teaching error). 'argsJson' is a JSON object of the op's arguments by name, e.g. {\"tableRef\":\"table:Sales\",\"name\":\"Margin\",\"expression\":\"[Sales]-[Cost]\"}. Entitlement gates stay ACTIVE (a Pro refusal is the real answer you'd get). DENIED (each refusal names the right path): external/system I/O + lifecycle (save_model/open_*/connect_*/refresh_partition/export_vpax/clear_cache/evaluate_and_log/benchmark_*), deploy/cloud/git/fabric/cicd/daxlib/data-agent (already commit-gated, run directly), the undo timeline (undo_change/redo_change), multi-mutate composites whose later steps read earlier writes (apply_plan/apply_safe_fixes/make_model_ai_ready/apply_model_diff/cherry_pick/build_model_from_spec/apply_dax_script/apply_tmdl: use propose_plan / preview paths / apply one typed op), and workflow/knowledge/waiver/connection bookkeeping. After a green rehearsal, run the op itself to apply.")]
        public static Task<DryRunReport> DryRun(IEngine engine,
            [Description("The op name to rehearse (from get_op_catalog), e.g. 'create_measure'")] string op,
            [Description("The op's arguments as a JSON object keyed by argument name; omit for an op that takes none")] string argsJson = null)
            => engine.DryRunOpAsync(op, argsJson);

        // ---- Learning Loop: knowledge store (L1) + deterministic recall (L2) ----------------------

        [McpServerTool(Name = "get_model_primer"), Description("PRIMER: read the open model's single project orientation document. It is plain Markdown in a .semanticus/primers sidecar, beside the model (travels in source control) for a disk model, or in the workspace for a live/local connection, and always uses six fixed sections: Overview, Business context, Gotchas, Patterns, Known issues, History. Read this before model work for the declared business context and known traps. Read-only.")]
        public static async Task<PrimerDocument> GetModelPrimer(IEngine engine)
        {
            var r = await engine.GetPrimerAsync();
            Emit(engine, new ActivityEvent { Kind = "get_model_primer", Origin = "agent", Label = "Read the model Primer", Ok = r.Markdown != null, Error = r.Markdown == null ? r.Note : null });
            return r;
        }

        [McpServerTool(Name = "set_model_primer"), Description("PRIMER: replace the open model's declared orientation document with reviewed Markdown. The document must keep exactly six second-level sections in this order: Overview, Business context, Gotchas, Patterns, Known issues, History. Stored beside the model in .semanticus/primers so it travels in source control without changing the model definition. Show the user the proposed edit before writing.")]
        public static Task<PrimerDocument> SetModelPrimer(IEngine engine,
            [Description("The complete Primer Markdown, including the six required ## section headings in their fixed order.")] string markdown)
            => engine.SetPrimerAsync(markdown, "agent");

        [McpServerTool(Name = "list_primer_suggestions"), Description("PRIMER: list approved, model-scoped learning as proposed edits to the open model's fixed Primer sections. Free on any tier. Each proposal carries the source lesson id, capture time and source-run provenance, and needsReview=true when you wrote the lesson and no person has approved it yet (only the person can accept those, in Model notes). This never changes the Primer. Show the proposal to the user, then call accept_primer_suggestion only after explicit approval or reject_primer_suggestion to dismiss it.")]
        public static async Task<PrimerSuggestionList> ListPrimerSuggestions(IEngine engine)
        {
            var r = await engine.ListPrimerSuggestionsAsync();
            Emit(engine, new ActivityEvent { Kind = "list_primer_suggestions", Origin = "agent", Label = "Reviewed suggested Primer updates", Ok = true });
            return r;
        }

        [McpServerTool(Name = "accept_primer_suggestion"), Description("PRIMER: apply ONE already-reviewed suggestion to its fixed Primer section and preserve its source provenance in the Markdown. Human approval is required before this call, and a suggestion with needsReview=true (a lesson you wrote that no person has approved) is refused here: the person accepts it in Model notes. The accepted source id is recorded so it cannot resurface. To write directly without suggestion automation, use set_model_primer.")]
        public static Task<PrimerSuggestionDecision> AcceptPrimerSuggestion(IEngine engine,
            [Description("The suggestion id from list_primer_suggestions, after the user approved its exact proposed Markdown.")] string id)
            => engine.AcceptPrimerSuggestionAsync(id, "agent");

        [McpServerTool(Name = "reject_primer_suggestion"), Description("PRIMER: dismiss ONE suggested update for this model without changing the Primer. The source learning remains in the hidden learning store, while this model records the rejection so the same suggestion does not keep resurfacing.")]
        public static Task<PrimerSuggestionDecision> RejectPrimerSuggestion(IEngine engine,
            [Description("The suggestion id from list_primer_suggestions.")] string id)
            => engine.RejectPrimerSuggestionAsync(id, "agent");

        [McpServerTool(Name = "recall_experience"), Description("KNOWLEDGE (L2): before starting work on the open model, recall prior experience for THIS model shape. The engine computes the model's fingerprint, reads BOTH scopes' released insights (status approved, or unreviewed and marked untrusted), and returns a DETERMINISTICALLY-ranked candidate set (key-term overlap with your query + same-shape fingerprint bonus + importance score + temporal decay), each with WHY it matched (matchedKeys). This is retrieval, not judgment: YOU do the semantic ranking over the candidates and decide what applies. Returns the fingerprint too, and says so plainly when there is no prior experience for this shape. Needs an open model.")]
        public static Task<RecallResult> RecallExperience(IEngine engine,
            [Description("Optional query: what you are about to do (e.g. 'optimize a time-intelligence measure'); its terms drive the lexical key-overlap ranking. Omit to rank by fingerprint + score + recency alone.")] string query = null,
            [Description("Max candidates to return (default 12)")] int maxResults = 12)
            => engine.RecallExperienceAsync(query, maxResults);

        [McpServerTool(Name = "get_model_fingerprint"), Description("KNOWLEDGE: the open model's deterministic fingerprint: table/measure/column counts, source types, fact/dim classification, a naming-convention hash, and the top domain-word tokens, plus the stable FingerprintKey used to scope insights to matching model shapes. No inference, no embeddings. Use it to reason about 'have I seen a model like this before'. Needs an open model. Free, read-only.")]
        public static Task<ModelFingerprint> GetModelFingerprint(IEngine engine) => engine.GetModelFingerprintAsync();

        [McpServerTool(Name = "add_insight"), Description("KNOWLEDGE (L1): record ONE actionable lesson, a distilled insight or a post-mortem root cause. Insights are the USER'S OWN DATA: plain append-only JSONL (`.semanticus/knowledge/insights.jsonl` for project scope, `~/.semanticus/knowledge/` for global), readable without us, never rewritten. Give deterministic match keys (rule ids, gate signatures, error types, op names, domain tokens) so recall can find it. Write-gated (SSGM): it lands 'pending' (held out of recall) until approve_insight, UNLESS the auto-approve setting is on, which DEFAULTS TO TRUE for single-user local mode (set knowledge-settings.json {\"autoApprove\": false} to force review). Whatever you record is never 'approved': it lands 'unreviewed' (or 'pending'), untrusted and needsReview=true, until a PERSON approves it in Model notes. Recall returns it marked untrusted, as data not instruction, and it reaches the Primer only when the person accepts it. Text that looks like an instruction to skip checks or swap models is NEVER auto-approved: it stays pending, lists under status=pending, and keeps its untrusted marker even once a person approves it. fingerprintScoped=true pins it to the CURRENT model's shape (needs an open model); false = a user-level lesson that travels across all models.")]
        public static Task<InsightRecord> AddInsight(IEngine engine,
            [Description("The insight: one actionable sentence to a short paragraph")] string text,
            [Description("Deterministic match keys: rule ids, gate signatures, error types, op names, domain tokens")] string[] keys = null,
            [Description("'insight' (default) or 'post-mortem' (a failure's root cause)")] string kind = "insight",
            [Description("'project' (default, beside this model) or 'global' (travels across all your models)")] string scope = "project",
            [Description("true = scope to the CURRENT model's fingerprint (surfaces only on matching shapes); false = applies everywhere")] bool fingerprintScoped = false)
            => engine.AddInsightAsync(text, keys, kind, scope, fingerprintScoped, "agent");

        [McpServerTool(Name = "approve_insight"), Description("KNOWLEDGE (L1): release a 'pending' insight so recall_experience can surface it (the SSGM write-gate release). From you it never makes an insight 'approved' or trusted: the result is status 'unreviewed', untrusted, recalled only as data, until a person approves it in Model notes. No-op-safe on an insight that is already released.")]
        public static Task<InsightRecord> ApproveInsight(IEngine engine,
            [Description("The insight id (ki-xxxxxxxx) from list_insights")] string id) => engine.ApproveInsightAsync(id, "agent");

        [McpServerTool(Name = "edit_insight"), Description("KNOWLEDGE (L1): refine an insight's text and/or match keys, a delta append (the store is never rewritten; constraint against context collapse). Provide new text and/or keys; omit one to leave it unchanged.")]
        public static Task<InsightRecord> EditInsight(IEngine engine,
            [Description("The insight id (ki-xxxxxxxx)")] string id,
            [Description("New text (omit to keep the current text)")] string text = null,
            [Description("New match keys (omit to keep the current keys)")] string[] keys = null)
            => engine.EditInsightAsync(id, text, keys, "agent");

        [McpServerTool(Name = "upvote_insight"), Description("KNOWLEDGE (L1): +1 an insight's importance counter (ExpeL: the engine counts, you judge). An insight that keeps proving useful ranks higher and resists decay.")]
        public static Task<InsightRecord> UpvoteInsight(IEngine engine,
            [Description("The insight id (ki-xxxxxxxx)")] string id) => engine.UpvoteInsightAsync(id, "agent");

        [McpServerTool(Name = "downvote_insight"), Description("KNOWLEDGE (L1): -1 an insight's importance counter. When the score falls to 0 the insight is materialized OUT of the live set (the delta trail is kept, nothing is erased). Use it to retire a lesson that stopped applying.")]
        public static Task<InsightRecord> DownvoteInsight(IEngine engine,
            [Description("The insight id (ki-xxxxxxxx)")] string id) => engine.DownvoteInsightAsync(id, "agent");

        [McpServerTool(Name = "delete_insight"), Description("KNOWLEDGE (L1): tombstone an insight (delta append; the JSONL is not rewritten). It vanishes from the live set and recall. Prefer downvote for a lesson that merely lost relevance.")]
        public static Task<SetResult> DeleteInsight(IEngine engine,
            [Description("The insight id (ki-xxxxxxxx)")] string id) => engine.DeleteInsightAsync(id, "agent");

        [McpServerTool(Name = "list_insights"), Description("KNOWLEDGE (L1): list the live insight set with counters (score/uses/retrievals) and provenance (who/when/session/source-runs). Filter by scope ('project'|'global'; omit for both) and/or status ('pending' = held out of recall, 'unreviewed' = recalled as untrusted data, 'approved' = a person approved it). Reports the count of any corrupt JSONL lines it skipped. The store never bricks. Read-only.")]
        public static Task<InsightListResult> ListInsights(IEngine engine,
            [Description("'project' | 'global'; omit for both")] string scope = null,
            [Description("'pending' | 'unreviewed' | 'approved'; omit for all")] string status = null)
            => engine.ListInsightsAsync(scope, status);

        [McpServerTool(Name = "purge_knowledge"), Description("KNOWLEDGE (L1): scoped one-op purge (the MemoryGraft safety valve). DRY RUN by default: confirm=false returns how many live insights WOULD be erased and changes nothing; confirm=true appends a purge marker so everything before it in that scope becomes invisible on replay (the file is not rewritten).")]
        public static Task<PurgeResult> PurgeKnowledge(IEngine engine,
            [Description("'project' (default) or 'global'")] string scope = "project",
            [Description("false (default) = dry-run count; true = actually purge")] bool confirm = false)
            => engine.PurgeKnowledgeAsync(scope, confirm, "agent");

        [McpServerTool(Name = "lint_dax"), Description("Deterministic DAX best-practice lint (SQLBI/DAX-Patterns), ~15 token-path rules over one DAX expression: IFERROR/ISERROR plus the DIVIDE- and SEARCH/FIND-specific forms, ERROR() as control flow, EARLIER/EARLIEST, extended columns inside SUMMARIZE, comparison-to-BLANK(), hand-rolled zero guards, VAR-as-live-alias inside CALCULATE, unused VARs, the SELECTEDVALUE / DISTINCTCOUNT / REMOVEFILTERS idioms, a measure in a boolean filter predicate, and bare-table CALCULATE filters. With an open session the table-identity rules activate (bare-table filter). Token-based, so comments / strings / [bracketed names] never false-fire. Read-only, free, advisory per-expression. The model-scored layer is the AI-readiness BestPractice category (ai_readiness_scan). Run it on a rewrite before applying.")]
        public static async Task<DaxLintResult> LintDax(IEngine engine,
            [Description("The DAX expression to lint (e.g. a measure body)")] string expression)
        {
            var r = await engine.LintDaxAsync(expression);
            Emit(engine, new ActivityEvent { Kind = "lint_dax", Origin = "agent", Label = $"Linted DAX: {r.Findings.Length} finding(s)", Query = TruncDax(expression), Ok = true, Result = r });
            return r;
        }

        [McpServerTool(Name = "clear_cache"), Description("Clear the Storage-Engine (VertiPaq) cache for the connected model: the DAX-Studio 'Clear Cache' primitive that makes cold-vs-warm benchmarking meaningful (run it before a query to measure the cold/worst-case cost). NON-DESTRUCTIVE: it only evicts the cache (the engine rebuilds it on the next query); no data or metadata changes. SAFETY: on a SHARED/cloud endpoint this evicts the cache for ALL users (their next queries run cold), so it is REFUSED there unless you pass confirm=true. Needs a local Power BI Desktop or an admin XMLA endpoint.")]
        public static async Task<ClearCacheResult> ClearCache(IEngine engine,
            [Description("Required on a shared/cloud endpoint to acknowledge it affects ALL users; ignored (always allowed) on a local Power BI Desktop instance.")] bool confirm = false)
        {
            var r = await engine.ClearCacheAsync(confirm);
            Emit(engine, new ActivityEvent { Kind = "clear_cache", Origin = "agent", Label = r.Cleared ? "Cleared the SE cache" : "Clear cache (refused/failed)",
                Target = r.Database, Ok = r.Cleared, Error = r.Error, ElapsedMs = r.ElapsedMs, Result = r });
            return r;
        }

        /// <summary>benchmark_dax with mode='coldWarm'. The activity kind stays "benchmark_coldwarm": the DAX Lab reads
        /// a ColdWarmBenchmark off that kind, so one kind for both modes would hand it the wrong shape.</summary>
        public static async Task<ColdWarmBenchmark> BenchmarkDaxColdWarm(IEngine engine, string query, int runs = 5, bool clearForCold = true, bool confirm = false)
        {
            var r = await engine.BenchmarkColdWarmAsync(query, runs, clearForCold, confirm);
            Emit(engine, new ActivityEvent { Kind = "benchmark_coldwarm", Origin = "agent", Label = "Benchmarked cold vs warm", Query = TruncDax(query),
                Ok = string.IsNullOrEmpty(r.Error), Error = r.Error, RowCount = r.RowCount, Result = r });
            return r;
        }

        [McpServerTool(Name = "capture_query_plan"), Description("Capture a DAX query's LOGICAL and PHYSICAL query plans (the DAXQueryPlan trace) to understand WHY it's slow. The Formula Engine builds the logical plan, the Storage Engine the physical plan (with #Records per operator). Use it alongside profile_dax when a query is FE-bound (a tall physical plan / huge intermediate records points at the costly operators). Needs a local Power BI Desktop or an admin XMLA endpoint; degrades to 'unavailable' otherwise. Read-only.")]
        public static async Task<QueryPlanResult> CaptureQueryPlan(IEngine engine,
            [Description("DAX query to plan, e.g. EVALUATE SUMMARIZECOLUMNS(...)")] string query)
        {
            var r = await engine.CaptureQueryPlanAsync(query);
            Emit(engine, new ActivityEvent { Kind = "capture_query_plan", Origin = "agent", Label = "Captured the query plan", Query = TruncDax(query),
                Ok = string.IsNullOrEmpty(r.Error), Error = r.Error, ElapsedMs = r.TotalMs, Result = r });
            return r;
        }

        // ---- Change-Plan engine ("analyse → review → fix everything", plus incremental edits) -------

        [McpServerTool(Name = "propose_plan"), Description("Analyse the model and assemble a CHANGE PLAN (a 'pull request for your model') WITHOUT mutating anything: deterministic safe fixes + BPA auto-fixes are fully specified; AI-content items (descriptions, formats, names, synonyms) come back as a work queue with grounding for YOU to fill via set_plan_item. Review with get_plan, then apply_plan applies the approved items as one undoable transaction. scope null = whole model; 'table:Name' = just that table. Everything you propose here ALSO lands on the Model Plan (get_model_plan), filed under the item whose family and objects it serves, and a change no item serves opens an item of its own owned by the assistant. So this is the flat list of the same changes the Model Plan groups by goal, not a second list of different work.")]
        public static Task<ChangePlanView> ProposePlan(IEngine engine,
            [Description("Scope: null/empty = whole model, or 'table:Name' for one table")] string scope = null,
            [Description("Include AI-content items (descriptions/names/formats/synonyms) you must fill; default true")] bool includeAi = true,
            [Description("Max AI-content items to queue (default 40)")] int maxAiItems = 40)
            => engine.ProposePlanAsync(scope, includeAi, maxAiItems, "agent");

        [McpServerTool(Name = "get_plan"), Description("Get the current change plan: every proposed item with its before→after, source (deterministic/bpa/ai), risk, status, and a summary. Use it to see what still needs content (status 'needs_content') and what is approved/ready to apply. These are the SAME changes the Model Plan groups under its items (get_model_plan), as one flat list: this is the change-by-change view, that is the by-goal view, and there is one set of changes behind both.")]
        public static Task<ChangePlanView> GetPlan(IEngine engine) => engine.GetPlanAsync();

        [McpServerTool(Name = "set_plan_item"), Description("Fill an AI-content plan item with your authored value (and/or approve/reject it). Authoring a value moves the item to 'approved', EXCEPT the opt-in kinds (rename, set_dax without a non-empty verify matrix, set_m), which land on 'proposed' after ANY value revision (even one made after an approval: the consent covered the old value) and need an explicit approve. e.g. set_plan_item('ci-0007', after='Total revenue recognised in the period.'). Use get_plan to see item ids + grounding first. THIS IS ALSO HOW YOU KEEP AND DROP one line of a Model Plan item's change set: approved=true keeps the line, approved=false drops it, and the item's changeSet.digest changes either way, so an approval given before you dropped a line can no longer be honoured. The line's planItemId on get_model_plan is the itemId to pass here. Keeping and dropping change NOTHING in the model and are free on every tier. Touching a line this way is what makes a cleanup line that arrived ticked as a suggestion (suggested: true) the person's own, and apply_plan from your door does not apply an untouched suggested line.")]
        public static Task<ChangePlanView> SetPlanItem(IEngine engine,
            [Description("Plan item id from get_plan, e.g. 'ci-0007'")] string itemId,
            [Description("The authored value (description text, format string, new name, comma-separated synonyms, or DAX); null to only approve/reject")] string after = null,
            [Description("true to approve, false to reject, null to leave as-is")] bool? approved = null)
            => engine.SetPlanItemAsync(itemId, after, approved, "agent");

        [McpServerTool(Name = "add_plan_item"), Description("Add a custom change to the plan (an incremental edit, or a DAX rewrite you want verified before applying). kind is the operation: set_dax | set_description | set_measure_format | set_summarize_by | set_column_hidden | set_data_category | rename | set_synonyms | set_relationship_crossfilter | mark_date_table | set_ai_instructions (model-level: objRef is the model, after = the instructions text) | set_ai_data_schema (objRef a measure/column/table, after = 'Excluded' or 'Included') | delete (remove the object) | delete_if_unused (remove ONLY if unused_objects' safe verdict still holds at apply time, recomputed server-side, else the item is skipped with the reason). An identical pending item (same ref+kind+target+after) is deduped: the existing item is returned, not re-added. For a set_dax rewrite, pass verifyGroupBy so apply_plan proves equivalence (and skips the item if results change). A set_dax item WITHOUT verifyGroupBy is added as 'proposed' (opt-in, like a rename). It will NOT be applied unless you explicitly approve it via set_plan_item, and if applied it goes in UNVERIFIED. Nothing is mutated until apply_plan. The change also appears on the Model Plan (get_model_plan) as a line on the item it serves, or on a new item owned by the assistant when no item serves it, so a person reads it in one list with everything else.")]
        public static Task<ChangePlanView> AddPlanItem(IEngine engine,
            [Description("Object ref, e.g. 'measure:Sales/Total Sales'")] string objRef,
            [Description("Operation kind, e.g. 'set_dax', 'set_description', 'rename'")] string kind,
            [Description("The proposed value (DAX/description/new name/etc.)")] string after = null,
            [Description("Short human title for the change (optional)")] string title = null,
            [Description("For set_dax: SUMMARIZECOLUMNS group-by matrix to verify equivalence over, e.g. [\"'Date'[Year]\"]")] string[] verifyGroupBy = null,
            [Description("For set_dax: optional table filters for the verify matrix")] string[] verifyFilters = null)
            => engine.AddPlanItemAsync(objRef, kind, after, title, verifyGroupBy, verifyFilters, "agent");

        [McpServerTool(Name = "apply_plan"), Description("Execute the APPROVED subset of the change plan as ONE undoable transaction. Only approved items are applied. Passing approvedIds narrows the approved set (it never applies rejected/proposed/needs_content items). DAX rewrites carrying a verify matrix are proven equivalent first and skipped if they'd change results or can't be proven; renames apply last. Returns a report: applied/skipped/failed counts + before→after BPA violations and AI-readiness grade. A single undo reverts everything; re-run propose_plan to iterate the tail. This is APPROVE-ALL over the changes the Model Plan groups under its items: it applies every change you have approved, wherever it sits, in one step. It is the batch tool, so it applies what it can and REPORTS what it could not; to approve one item's changes all-or-nothing instead, quoting the digest of exactly what you read, use model_plan_action(id, 'approve', digest).")]
        public static Task<ApplyPlanReport> ApplyPlan(IEngine engine,
            [Description("Item ids to apply (e.g. ['ci-0001','ci-0002']); null/empty = all approved items")] string[] approvedIds = null,
            [Description("plan item ids to apply even though their equivalence verdict is failed/unprovable: requires overrideReason; each shipped override is recorded")] string[] overrideIds = null,
            [Description("why you are shipping items past a failed/unprovable equivalence verdict: required to override; recorded in the model's append-only audit trail")] string overrideReason = null)
            => engine.ApplyPlanAsync(approvedIds, "agent", overrideIds, overrideReason);

        [McpServerTool(Name = "clear_plan"), Description("Drop every proposed change. Nothing in the model changes: a proposal never wrote anything. This is CANCEL-ALL over the changes the Model Plan groups under its items, so every item goes back to having nothing worked out for it. To drop one item's changes instead, use model_plan_action(id, 'cancel').")]
        public static Task<ChangePlanView> ClearPlan(IEngine engine) => engine.ClearPlanAsync("agent");

        // ---- Documentation narrative (collaborate on the built docs) ----------------------------------
        [McpServerTool(Name = "get_doc_model"), Description("Get the complete documentation snapshot of the model that the exporter renders: header (name, compatibility level, culture, counts), the relationship graph, every table's detail (description, hierarchies, partitions with their source (M/SQL/DAX or, for Direct Lake, the Entity binding), calc-group items), all measures & columns (with DAX), KPIs, roles/RLS, perspectives, data sources, shared expressions, the Prep-for-AI surface, the AI-readiness + BPA scorecards, and (when live-connected) VertiPaq storage stats, PLUS any authored Documentation narrative. Read-only. Use it to understand the whole model at once or to see what the built docs will contain.")]
        public static async Task<DocModelDto> GetDocModel(IEngine engine,
            [Description("Max columns to include in the storage (VertiPaq) top-columns list; only used when live-connected")] int topN = 50)
        {
            var dto = await engine.GetDocModelAsync(topN);
            Emit(engine, new ActivityEvent { Kind = "get_doc_model", Origin = "agent", Label = "Read doc model", Ok = true, Result = dto });
            return dto;
        }

        [McpServerTool(Name = "get_doc_outline"), Description("List the model objects that can carry Documentation NARRATIVE (the model, every table, every measure) and which narrative sections each already has. Use this to discover WHERE to add business context for the exported documentation. The narrative is ADDITIONAL context for the docs, separate from each object's first-class Description (which you set with set_description).")]
        public static Task<DocOutline> GetDocOutline(IEngine engine) => engine.GetDocOutlineAsync();

        [McpServerTool(Name = "get_doc_section"), Description("Read one Documentation narrative section (Markdown) for an object. objRef is 'model' for the model-wide narrative, or 'table:Name' / 'measure:Table/Name' (narrative is supported on the model, tables and measures). Common sectionKeys: 'overview', 'businessContext', 'notes' (per object); 'overview', 'glossary', 'methodology' (model). Returns null if empty.")]
        public static Task<string> GetDocSection(IEngine engine,
            [Description("Object ref, or 'model' for the model-wide narrative")] string objRef,
            [Description("Section key, e.g. 'businessContext'")] string sectionKey)
            => engine.GetDocSectionAsync(objRef, sectionKey);

        [McpServerTool(Name = "set_doc_section"), Description("Author/insert a Documentation narrative section (Markdown) for an object: ADDITIONAL business context that merges into the exported documentation, SEPARATE from the model's Descriptions. objRef is 'model' or 'table:Name' / 'measure:Table/Name' (narrative is supported on the model, tables and measures; other refs are rejected). Common sectionKeys: 'overview', 'businessContext', 'notes' (per object); 'overview', 'glossary', 'methodology' (model). Set markdown to empty/null to clear the section. This is the collaborate-on-the-docs path: it's stored as a model annotation, broadcasts live to the human's Documentation tab, and is undoable.")]
        public static async Task<SetResult> SetDocSection(IEngine engine,
            [Description("Object ref, or 'model' for the model-wide narrative")] string objRef,
            [Description("Section key, e.g. 'businessContext'")] string sectionKey,
            [Description("Markdown content (empty/null clears the section)")] string markdown)
        {
            var r = await engine.SetDocSectionAsync(objRef, sectionKey, markdown, "agent");
            Emit(engine, new ActivityEvent { Kind = "set_doc_section", Origin = "agent", Label = "Added doc context", Target = objRef, Query = sectionKey,
                Ok = true, Result = r });
            return r;
        }

        // ---- Model spec (spec-driven authoring: autogenerate -> refine -> build) ------------------------
        [McpServerTool(Name = "get_spec"), Description("Get the current MODEL SPEC: the structured plan for the model to build (storage mode + Fabric source, tables with columns/types/role, relationships, core measures, time-intelligence). The spec is an authoring artifact (NOT the model itself); build_model_from_spec materialises it. Returns { version, source, spec } (spec is null if none is loaded). Read-only.")]
        public static async Task<SpecView> GetSpec(IEngine engine)
        {
            var v = await engine.GetSpecAsync();
            Emit(engine, new ActivityEvent { Kind = "get_spec", Origin = "agent", Label = "Read the model spec", Ok = true, Result = v });
            return v;
        }

        [McpServerTool(Name = "set_spec"), Description("Replace the current model spec with the supplied JSON (the whole ModelSpec document). Use this to author or refine the spec; it broadcasts live to the Spec tab. Shape: { name, compatibilityLevel, storageMode: 'import'|'directLake', source: { kind:'fabric-sql', server, database, schema }, tables: [{ name, role:'fact'|'dimension'|'date'|'calculated'|'isolated', entity, schema, mExpression, calculatedExpression, sourceName, columns: [{ name, dataType, sourceColumn, isKey, hidden, summarizeBy }] }], relationships: [{ fromTable, fromColumn, toTable, toColumn, cardinality:'manyToOne'(default)|'oneToOne'|'oneToMany'|'manyToMany', crossFilter, isActive, note }], measures: [{ table, name, dax (or expression), description, formatString, displayFolder }], timeIntelligence: ['YTD','PY',...], timeIntelligenceBaseMeasures: [...], dateTable: { name, dateColumn, startExpr, endExpr, markAsDate } }. A relationship's note is plain words for the person on why a link is off; the build ignores it. When dateTable.name is one of the spec's own tables, the build marks that table as the date table keyed on dateColumn (or its one date column) and adds no CALENDAR table; a table already marked is left as it is. Otherwise a CALENDAR table named dateTable.name is added from startExpr and endExpr. Does NOT touch the model. Returns the new spec view.")]
        public static async Task<SpecView> SetSpec(IEngine engine,
            [Description("The whole ModelSpec as JSON")] string specJson)
        {
            var v = await engine.SetSpecAsync(specJson, "agent");
            Emit(engine, new ActivityEvent { Kind = "set_spec", Origin = "agent", Label = "Set the model spec", Ok = true, Result = v });
            return v;
        }

        [McpServerTool(Name = "clear_spec"), Description("Discard the current model spec (nothing in the model is affected. The spec never mutated it). Returns the empty spec view.")]
        public static async Task<SpecView> ClearSpec(IEngine engine)
        {
            var v = await engine.ClearSpecAsync("agent");
            Emit(engine, new ActivityEvent { Kind = "clear_spec", Origin = "agent", Label = "Cleared the model spec", Ok = true, Result = v });
            return v;
        }

        [McpServerTool(Name = "save_spec"), Description("Save the current model spec to a JSON file (for version control / sharing). Pass an absolute path, e.g. '.../model.spec.json'. Returns the spec view.")]
        public static Task<SpecView> SaveSpec(IEngine engine,
            [Description("Absolute path to write the spec JSON to")] string path)
            => engine.SaveSpecAsync(path, "agent");

        [McpServerTool(Name = "load_spec"), Description("Load a model spec from a JSON file (written by save_spec) and make it the current spec. Broadcasts live to the Spec tab. Returns the loaded spec view.")]
        public static async Task<SpecView> LoadSpec(IEngine engine,
            [Description("Absolute path to a spec JSON file")] string path)
        {
            var v = await engine.LoadSpecAsync(path, "agent");
            Emit(engine, new ActivityEvent { Kind = "load_spec", Origin = "agent", Label = "Loaded a model spec", Ok = true, Result = v });
            return v;
        }

        [McpServerTool(Name = "build_model_from_spec"), Description("Materialise the current model spec INTO the open model as ONE undoable transaction (a single undo reverts the whole build). Composes the authoring primitives in dependency order: data source + shared M -> tables (import/Direct Lake/calculated, with columns + data types + keys) -> relationships (FK->PK) -> date table -> measures -> time-intelligence. Existing objects are skipped (re-run safe); per-row failures are reported without aborting. Build INTO the current session. Call create_model first for a from-scratch build. Returns a report (created/skipped/errors + before->after counts). For a NEW model made from a SQL source, use start_model_from_sql instead: it makes the empty model, keeps the plan on a hidden draft, and builds it in one step once the person approves one plan. This tool is the two-step path for a model that is already open.")]
        public static async Task<SpecBuildReport> BuildModelFromSpec(IEngine engine)
        {
            var r = await engine.BuildModelFromSpecAsync("agent");
            Emit(engine, new ActivityEvent { Kind = "build_model_from_spec", Origin = "agent", Label = "Built the model from the spec", Ok = true, Result = r });
            return r;
        }

        [McpServerTool(Name = "autogenerate_spec_from_model"), Description("Auto-generate a starter model SPEC from the currently OPEN model (read-only, does not change the model): classifies each table fact/dimension/date/calculated by its relationship roles, captures columns + data types + keys + relationships + existing measures, PROPOSES additive 'Total <col>' measures for numeric fact columns, and suggests a time-intelligence set + date table. The autogen is a FIRST DRAFT. Refine it (set_spec / the Spec tab), then build_model_from_spec. Returns the spec view.")]
        public static async Task<SpecView> AutogenerateSpecFromModel(IEngine engine)
        {
            var v = await engine.AutogenerateSpecFromModelAsync("agent");
            Emit(engine, new ActivityEvent { Kind = "autogenerate_spec_from_model", Origin = "agent", Label = "Auto-generated a spec from the model", Ok = true, Result = v });
            return v;
        }

        [McpServerTool(Name = "autogenerate_spec_from_fabric"), Description("Auto-generate a starter model SPEC by introspecting a FABRIC SQL ENDPOINT (Warehouse / Lakehouse SQL analytics endpoint) over TDS: reads INFORMATION_SCHEMA (tables, columns + types, declared PK/FK) using your Entra identity (a deterministic, read-only schema read; no data is copied). Classifies fact/dimension from FK topology, maps SQL types to TOM types, hides key columns, and proposes additive measures + a time-intelligence set. storageMode 'import' (M) or 'directLake'. authMode: azcli (default) | serviceprincipal | interactive | devicecode. If the endpoint rejects the sign-in with an authentication error, the workspace is usually in a DIFFERENT tenant than your az login. Pass tenantId. To reuse a SQL endpoint the person already saved on Connections (same sign-in, no password stored), read it with list_sql_sources and pass its server, database, authMode and tenantId. Which links are drafted INACTIVE, each with a plain note on the relationship saying why (a measure can still use it with USERELATIONSHIP): a fact table's own links always stay on (to the date table and to each dimension it references); when two of a fact's links reach the same table (an invoice date and a due date), the one listed first among the fact's columns stays on and the rest are off; any other link (a snowflake hop, a dimension's own date key) that would give a table a second path is off; a table linked to itself is off and not built. A source date table (a non-fact table named like date or calendar with a date column) is set as dateTable and MARKED as the date table on build, keyed on its date column; only a source with none gets a CALENDAR table. The result is a FIRST DRAFT. Refine it, then create_model + build_model_from_spec. (Fabric often declares no enforced keys, so relationships may be empty. Add them in the spec.) Returns the spec view. For a NEW model with nothing in it yet, use start_model_from_sql instead: it reads the same source, decides the spec on a hidden draft and builds it in one step once the person approves one plan. This tool is the two-step path, for a model that is already open.")]
        public static async Task<SpecView> AutogenerateSpecFromFabric(IEngine engine,
            [Description("Fabric SQL endpoint, e.g. 'xxxxx.datawarehouse.fabric.microsoft.com'")] string server,
            [Description("Warehouse / Lakehouse database name")] string database,
            [Description("import | directLake")] string storageMode = "import",
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            [Description("Entra tenant id or domain that owns the Fabric workspace; leave blank to use your az CLI default. Needed when your az login is in a different tenant than the workspace.")] string tenantId = null)
        {
            var v = await engine.AutogenerateSpecFromFabricAsync(server, database, authMode, storageMode, "agent", tenantId);
            Emit(engine, new ActivityEvent { Kind = "autogenerate_spec_from_fabric", Origin = "agent", Label = "Auto-generated a spec from a Fabric SQL endpoint", Ok = true, Result = v });
            return v;
        }

        // ---- Source control (local git, the ALM lane) -------------------------------------------------
        [McpServerTool(Name = "git_status"), Description("Source control status of the OPEN model's git repository: current branch, ahead/behind, and the changed TMDL files (staged/worktree). Also flags whether the open model has unsaved in-memory edits (git_commit saves them to disk first). Read-only.")]
        public static Task<GitStatus> GitStatus(IEngine engine) => engine.GitStatusAsync();

        [McpServerTool(Name = "git_diff"), Description("The unified text diff of the open model's git working tree (the on-disk TMDL diff). Pass a file path to scope it; staged=true for the staged diff. Read-only.")]
        public static Task<GitDiffResult> GitDiff(IEngine engine,
            [Description("Optional file path to scope the diff (relative to the repo)")] string path = null,
            [Description("true = the staged diff; false = the working-tree diff")] bool staged = false)
            => engine.GitDiffAsync(path, staged);

        [McpServerTool(Name = "git_log"), Description("Recent commits on the open model's repository (hash, author, date, subject). Read-only.")]
        public static Task<GitLogEntry[]> GitLog(IEngine engine, [Description("Max commits (default 20)")] int max = 20) => engine.GitLogAsync(max);

        [McpServerTool(Name = "git_commit"), Description("Commit the open model to git. DRY RUN by default (commit=false returns the file set that WOULD be committed and whether the model needs saving first; nothing changes). Pass commit=true to actually commit. It SAVES the open model's unsaved edits to disk first, stages the model folder, then commits. A blank message gets the default \"Saved from Semanticus\". Returns the commit hash + files.")]
        public static async Task<GitCommitResult> GitCommit(IEngine engine,
            [Description("Commit message. A blank one gets a default.")] string message,
            [Description("Optional explicit file list to stage (default: stage the model folder)")] string[] files = null,
            [Description("false = preview (default); true = actually commit")] bool commit = false)
        {
            var r = await engine.GitCommitAsync(message, files, commit, "agent");
            Emit(engine, new ActivityEvent { Kind = "git_commit", Origin = "agent", Label = commit ? "Committed to git" : "Previewed a git commit", Ok = string.IsNullOrEmpty(r.Error), Result = r });
            return r;
        }

        [McpServerTool(Name = "list_history_checkpoints"), Description("HISTORY (Free): list durable checkpoints for the open model on the current git branch. Checkpoints are ordinary model-scoped commits, so they survive closing the session and cloning the repository. Returns supported=false for a model outside git; session undo remains the short-term alternative. Read-only.")]
        public static Task<HistoryCheckpointList> ListHistoryCheckpoints(IEngine engine,
            [Description("Maximum checkpoints to return (1-500, default 50)")] int max = 50)
            => engine.ListHistoryCheckpointsAsync(max);

        [McpServerTool(Name = "create_history_checkpoint"), Description("HISTORY (Free): create a durable, model-scoped git checkpoint without committing unrelated repository files. DRY RUN by default: commit=false reports the owned paths, changed files and whether the model will be saved. Pass commit=true after review. Refuses a detached branch, conflicts, or unrelated staged files instead of sweeping them into the commit. The result names the exact checkpoint hash; use restore_history_checkpoint to preview a restore.")]
        public static async Task<HistoryCheckpointResult> CreateHistoryCheckpoint(IEngine engine,
            [Description("Plain accepted-state label, e.g. 'Before pricing refactor'")] string label = null,
            [Description("false = preview only (default); true = save and create the checkpoint commit")] bool commit = false)
        {
            var r = await engine.CreateHistoryCheckpointAsync(label, commit, "agent");
            Emit(engine, new ActivityEvent { Kind = "history_checkpoint", Origin = "agent", Label = commit ? "Saved a version to History" : "Previewed saving a version to History", Ok = string.IsNullOrEmpty(r.Error), Result = r });
            return r;
        }

        [McpServerTool(Name = "restore_history_checkpoint"), Description("HISTORY (Free): restore the open FILE model to one of its durable checkpoints without resetting the branch or unrelated files. DRY RUN by default: restore=false names the model paths and safety sequence. Pass restore=true only after review. The engine first commits the current model as a rescue checkpoint, restores the selected model paths, reopens the model, and commits the restored state. Returns all three hashes so the action stays reversible. Live published models use rollback_push and its restore points instead.")]
        public static async Task<HistoryRestoreResult> RestoreHistoryCheckpoint(IEngine engine,
            [Description("Full or short hash returned by list_history_checkpoints")] string hash,
            [Description("false = preview only (default); true = create rescue checkpoint and restore")] bool restore = false)
        {
            var r = await engine.RestoreHistoryCheckpointAsync(hash, restore, "agent");
            Emit(engine, new ActivityEvent { Kind = "history_restore", Origin = "agent", Label = restore ? "Restored a saved version from History" : "Previewed restoring a saved version", Ok = string.IsNullOrEmpty(r.Error), Result = r });
            return r;
        }

        [McpServerTool(Name = "git_branch"), Description("List branches with no name or flags. To change state, pass a branch name and set create=true, checkout=true, or both. If switching changes the on-disk model, the result names the model path to reopen and flags modelReloadNeeded.")]
        public static async Task<GitActionResult> GitBranch(IEngine engine,
            [Description("Branch name; empty = list branches")] string name = null,
            [Description("Create the branch")] bool create = false,
            [Description("Switch to it")] bool checkout = false)
        {
            var r = await engine.GitBranchAsync(name, create, checkout, "agent");
            if (create || checkout) Emit(engine, new ActivityEvent { Kind = "git_branch", Origin = "agent", Label = r.Ok ? (checkout ? "Switched git branch" : "Created a git branch") : "Git branch failed", Ok = r.Ok, Error = r.Error, Result = r });
            return r;
        }

        [McpServerTool(Name = "git_checkout"), Description("Switch to a git ref (branch, tag, or commit). Refuses while the open model has unsaved edits. If the on-disk model changes, the result names the model path to reopen and flags modelReloadNeeded.")]
        public static async Task<GitActionResult> GitCheckout(IEngine engine, [Description("Branch / tag / commit to check out")] string gitRef)
        {
            var r = await engine.GitCheckoutAsync(gitRef, "agent");
            Emit(engine, new ActivityEvent { Kind = "git_checkout", Origin = "agent", Label = r.Ok ? "Checked out " + gitRef : "Git checkout failed", Ok = r.Ok, Error = r.Error, Result = r });
            return r;
        }

        [McpServerTool(Name = "git_pull"), Description("Fast-forward pull the current branch from its upstream. Refuses while the open model has unsaved edits. If the on-disk model changes, the result names the model path to reopen and flags modelReloadNeeded.")]
        public static async Task<GitActionResult> GitPull(IEngine engine)
        {
            var r = await engine.GitPullAsync("agent");
            Emit(engine, new ActivityEvent { Kind = "git_pull", Origin = "agent", Label = r.Ok ? (r.ModelReloadNeeded ? "Pulled from upstream" : "Already up to date") : "Git pull failed", Ok = r.Ok, Error = r.Error, Result = r });
            return r;
        }

        [McpServerTool(Name = "git_push"), Description("Push commits to the remote. DRY RUN by default (confirm=false reports what would push). Pass confirm=true to push. Uses the user's git credential helper. The engine never handles git credentials.")]
        public static async Task<GitActionResult> GitPush(IEngine engine,
            [Description("Remote (default: the branch's configured remote)")] string remote = null,
            [Description("Branch (default: current)")] string branch = null,
            [Description("false = preview (default); true = actually push")] bool confirm = false)
        {
            var r = await engine.GitPushAsync(remote, branch, confirm, "agent");
            Emit(engine, new ActivityEvent { Kind = "git_push", Origin = "agent", Label = confirm ? "Pushed to remote" : "Previewed a git push", Ok = r.Ok, Result = r });
            return r;
        }

        [McpServerTool(Name = "git_clone"), Description("Clone a git repository and locate the semantic model inside it. A relative target stays inside the current workspace, and the target must not already exist. Returns the model path to open with open_model. Uses the user's git credential helper.")]
        public static async Task<GitActionResult> GitClone(IEngine engine,
            [Description("Repository URL")] string url,
            [Description("Target directory to clone into")] string directory)
        {
            var r = await engine.GitCloneAsync(url, directory, "agent");
            Emit(engine, new ActivityEvent { Kind = "git_clone", Origin = "agent", Label = r.Ok ? "Cloned a repository" : "Git clone failed", Ok = r.Ok, Error = r.Error, Result = r });
            return r;
        }

        [McpServerTool(Name = "get_reference_tree"), Description("Get the model's object tree as flat nodes: every table (and calculation group) with its measures, calculated columns, and calculation items, each carrying a ref (e.g. 'measure:Sales/Total Sales'), display name, kind, and whether it has children. This is the same reference tree the VS Code compare/cherry-pick picker shows. Use it to enumerate the model's authorable objects in ONE call, to pick refs for model_diff / apply_model_diff / cherry_pick, or as a cheap structural map of what exists. Defaults to the OPEN model; pass sourceFile to read a model on disk instead. Read-only.")]
        public static Task<TreeNode[]> GetReferenceTree(IEngine engine,
            [Description("Read this model on disk (TMDL/PBIP folder or .bim) instead of the open session")] string sourceFile = null)
            => engine.ListReferenceTreeAsync(string.IsNullOrWhiteSpace(sourceFile)
                ? new ModelRef { Kind = "session" }
                : new ModelRef { Kind = "file", Path = sourceFile }, "agent");   // an agent never pops UI; a workspace ref (if ever passed) is refused

        // ---- Model compare (ALM Toolkit / BISM Normalizer grade) + the deploy gate --------------------
        [McpServerTool(Name = "model_diff"), Description("ALM-Toolkit-grade SEMANTIC compare of the open model against another version, by default the git ref 'HEAD' (i.e. what you changed since the last commit). Returns an object-level diff (per table/column/measure/relationship/role/...: Create/Update/Delete) with each object's before/after text. Pass a different gitRef to compare against a branch/commit, or targetFile to compare against a model on disk, or sourceFile to compare two files. Read-only.")]
        public static async Task<ModelDiff> ModelDiff(IEngine engine,
            [Description("Compare the open model against this git ref (default 'HEAD'); ignored if targetFile is set")] string gitRef = "HEAD",
            [Description("Compare against a model on disk (TMDL/PBIP folder or .bim) instead of git")] string targetFile = null,
            [Description("Compare THIS file (left/source) instead of the open model")] string sourceFile = null)
        {
            ModelRef left = !string.IsNullOrWhiteSpace(sourceFile) ? new ModelRef { Kind = "file", Path = sourceFile } : new ModelRef { Kind = "session" };
            ModelRef right = !string.IsNullOrWhiteSpace(targetFile) ? new ModelRef { Kind = "file", Path = targetFile } : new ModelRef { Kind = "gitref", GitRef = gitRef };
            // origin="agent": model_diff only ever targets a gitref/file (never a workspace), so it can't reach the
            // interactive-sign-in path — but pass the honest origin anyway so the fail-closed contract holds if that
            // ever changes (an agent must never trigger a browser sign-in; it gets a teaching refusal instead).
            var d = await engine.CompareModelsAsync(left, right, false, "agent");
            Emit(engine, new ActivityEvent { Kind = "model_diff", Origin = "agent", Label = "Compared the model", Ok = string.IsNullOrEmpty(d.Error), Result = d });
            return d;
        }

        [McpServerTool(Name = "apply_model_diff"), Description("Selectively MERGE a source model's changes INTO a target (the ALM-Toolkit 'Update'): makes the target match the source for the chosen objects. TARGET is the OPEN model when you omit targetFile and targetEndpoint (undoable merge), a model file on disk (targetFile), OR a PUBLISHED model on an XMLA endpoint (targetEndpoint + targetDatabase). SOURCE defaults to the open model; pass sourceFile or sourceGitRef to merge FROM elsewhere. DRY RUN by default (commit=false reports what WOULD apply, including any DELETES). Pass commit=true to write. A write to a file or into the open model needs the review token the preview returned. selectedRefs limits to specific object refs (e.g. ['measure:Sales/Total']); omit to apply all differences. Pushing to a published model runs an ALWAYS-ON drift guard: if a target object changed since the diff, the push is REFUSED unless you pass overrideReason (recorded in the audit trail). Every committed push to a published model FIRST writes a restore point (returned as restorePointId) so it can be undone with rollback_push; if a push contains DELETES and the restore point cannot be written, the push is REFUSED. Next: run model_diff first to see the object refs; on a drift refusal, re-run model_diff to see the new state before overriding.")]
        public static async Task<ApplyDiffResult> ApplyModelDiff(IEngine engine,
            [Description("Target model on disk (TMDL/PBIP folder or .bim) to update; omit with targetEndpoint empty to merge into the OPEN model")] string targetFile = null,
            [Description("Target XMLA endpoint of a PUBLISHED model to push to (e.g. powerbi://api.powerbi.com/v1.0/myorg/Workspace); requires targetDatabase")] string targetEndpoint = null,
            [Description("Target dataset (database) name on the endpoint: required with targetEndpoint")] string targetDatabase = null,
            [Description("Object refs to apply (e.g. 'measure:Sales/Total'); empty = all differences")] string[] selectedRefs = null,
            [Description("Merge FROM this model on disk (TMDL/PBIP or .bim) instead of the open model")] string sourceFile = null,
            [Description("...or merge FROM this git ref of the open model's repo (e.g. 'main', a commit hash)")] string sourceGitRef = null,
            [Description("Accountable override for the drift guard on a published-model push: the reason you're pushing despite the target having changed since the diff. Recorded in the audit trail.")] string overrideReason = null,
            [Description("Auth mode for a published-model target (default 'azcli'; e.g. 'serviceprincipal', 'interactive')")] string authMode = null,
            [Description("Optional Entra tenant id or domain for a published-model target. Set it when the target lives in a different tenant than your default az login, so the read AND the write both authenticate against that tenant")] string targetTenantId = null,
            [Description("false = preview (default); true = write the target")] bool commit = false,
            [Description("the review token returned by the preview; required to write a file or the open model, and it must still match")] string confirmToken = null)
        {
            // Reject AMBIGUOUS source/target combos rather than silently picking one by precedence — a caller who set
            // both must not have the tool guess (it could push to the WRONG target or merge from the wrong source).
            if (!string.IsNullOrWhiteSpace(sourceFile) && !string.IsNullOrWhiteSpace(sourceGitRef))
                return new ApplyDiffResult { Error = "Ambiguous source: pass EITHER sourceFile OR sourceGitRef, not both (omit both to merge from the open model)." };
            if (!string.IsNullOrWhiteSpace(targetFile) && !string.IsNullOrWhiteSpace(targetEndpoint))
                return new ApplyDiffResult { Error = "Ambiguous target: pass EITHER targetFile (write to disk) OR targetEndpoint + targetDatabase (push to a published model), not both. Omit both to merge into the open model." };

            ModelRef left = !string.IsNullOrWhiteSpace(sourceFile) ? new ModelRef { Kind = "file", Path = sourceFile }
                : !string.IsNullOrWhiteSpace(sourceGitRef) ? new ModelRef { Kind = "gitref", GitRef = sourceGitRef }
                : new ModelRef { Kind = "session" };
            var isWs = !string.IsNullOrWhiteSpace(targetEndpoint);
            var isSession = string.IsNullOrWhiteSpace(targetEndpoint) && string.IsNullOrWhiteSpace(targetFile);
            ModelRef right = isWs
                ? new ModelRef { Kind = "workspace", Endpoint = targetEndpoint, Database = targetDatabase, AuthMode = authMode, TenantId = targetTenantId }
                : isSession
                    ? new ModelRef { Kind = "session" }
                    : new ModelRef { Kind = "file", Path = targetFile };
            var r = await engine.ApplyDiffAsync(left, right, selectedRefs, commit, "agent", overrideReason, confirmToken);
            Emit(engine, new ActivityEvent { Kind = "apply_model_diff", Origin = "agent", Label = commit ? (isWs ? "Pushed changes to a published model" : isSession ? "Merged changes into the open model" : "Merged changes into a file") : "Previewed a merge", Ok = string.IsNullOrEmpty(r.Error), Result = r });
            return r;
        }

        [McpServerTool(Name = "list_connections"), Description("Every model source this machine has opened or connected to (XMLA endpoints, local running models, and local files or projects), newest first. Use this INSTEAD of asking the user to retype an endpoint or path. Each record carries kind (xmla | localDesktop | file), endpoint (the XMLA address, local data source, or file path), dataset, auth mode, model name, last-used, and the user's target label. Holds NO secrets: authMode is a mode NAME, never a token. 'label' is what the USER declared the environment to be (local | dev | uat | prod); an EMPTY label means the target is treated as PRODUCTION, the strictest reading and the only inference ever made. NEVER guess a label from an endpoint's name. Only the user can set a label, in Connections in VS Code; the agent cannot. 'workingFolder' is a durable local copy made from that source. 'publishConnectionId' is the separate final XMLA destination when one is linked; a local source does not become a published destination by implication. A file record is a local file or project this machine has opened; reopen it with open_model on its endpoint path. It cannot answer queries by itself. Read-only and free. Next: connection_context for the active edit/query/publish identities, or prepare_working_copy to preview a local workflow.")]
        public static async Task<ModelConnectionRecord[]> ListConnections(IEngine engine)
            => await engine.ListConnectionsAsync();

        [McpServerTool(Name = "list_sql_sources"), Description("The named SQL sources saved on this machine, newest first. A SQL source is where an INDEPENDENT ground truth is read from: the warehouse or lakehouse a check compares a measure against, or the table a row count is counted in. It is NOT the model that answers DAX (that is list_connections). Each record carries id, name, server, database, the sign-in mode NAME, tenant, when it was created, when it was last tested and whether that test passed. lastTestOk is null when it has NEVER been tested, false when the last test FAILED: a never-tested source is not a working one. Holds NO secrets: authMode is a mode name, never a token, and there is no password anywhere in the record. Use the id as sqlSourceId on a saved check or a table mapping so an endpoint is typed once instead of per check. Read-only and free. Next: test_sql_source to prove one works, save_sql_source to add or change one, set_table_source_mapping to point a table at one.")]
        public static async Task<SqlSourceRecord[]> ListSqlSources(IEngine engine)
            => await engine.ListSqlSourcesAsync();

        [McpServerTool(Name = "list_sql_source_usage"), Description("What each saved SQL source is USED BY: for every source, its id, its name, the titles of the saved checks pointing at it, and the model tables mapped to it. Nothing else: no check definitions, no expected values, no verdicts, no row counts. Read-only and free, on both doors. Call it before removing or re-pointing a source so you know what would break, and to show source usage without reading the Tests feature. It can only see the MODEL THAT IS OPEN; with none open every source comes back with no usage rather than an error. Next: list_sql_sources for the connection details, delete_sql_source to remove an unused one.")]
        public static async Task<SqlSourceUsage[]> ListSqlSourceUsage(IEngine engine)
            => await engine.ListSqlSourceUsageAsync();

        [McpServerTool(Name = "set_model_sql_endpoint"), Description("Give a model its SQL endpoint: the ONE saved SQL source its checks read from unless a check names its own. Pass a sqlSourceId from list_sql_sources, or null to clear it. It applies to the OPEN model, or to a saved model from list_connections when you pass its connectionId. Resolution for every check is: the check's own source or inline server first (the quiet per-check override for a model whose tables come from two warehouses), then the model's endpoint, then what the model's own partitions read from. A table-count mapping from set_table_source_mapping still wins for its table. A working copy made with prepare_working_copy starts with the endpoint of the model it came from. The source record is not copied: several models can share one saved source. Writes only machine-local metadata, never the model. Free. Next: connection_context to read it back, run_tests to count against it.")]
        public static async Task<ModelSqlEndpointResult> SetModelSqlEndpoint(
            IEngine engine,
            [Description("Source id from list_sql_sources, or null to clear the model's endpoint")] string sqlSourceId,
            [Description("Optional connection id from list_connections for a saved model that is not open; omit for the open model")] string connectionId = null)
            => await engine.SetModelSqlEndpointAsync(sqlSourceId, connectionId, "agent");

        [McpServerTool(Name = "list_model_sql_endpoints"), Description("Every model's SQL endpoint ('rows': model name, connectionId when the model is a saved connection, and the endpoint's source id, name, server, database and last test), plus 'notLinked': the saved SQL sources that no model uses as its endpoint, each with how many of the OPEN model's checks and table mappings still name it (a source with zero of both can be removed). Existing setups were linked once on upgrade from what each model's checks already used, by id, so no check changed where it points. Read-only and free. Next: set_model_sql_endpoint to link or clear one, delete_sql_source to remove an unused source.")]
        public static async Task<ModelSqlEndpointList> ListModelSqlEndpoints(IEngine engine)
            => await engine.ListModelSqlEndpointsAsync();

        [McpServerTool(Name = "save_sql_source"), Description("Save a named SQL source, or update one by id. Pass name, server (JUST the address, like contoso-sql.database.windows.net), database, and authMode: 'interactive' (a browser sign-in), 'devicecode' (a code entered on another device), 'azcli' (the Azure CLI sign-in already on this machine), or 'serviceprincipal' (an app registration). tenantId is optional. NEVER pass a password, a token or a connection string: a connection-string shape in server or database is REFUSED at the boundary rather than stripped, 'token' is refused as a mode by name, and the record has no password field to put one in. Semanticus signs in at use time with the mode you name here, so every check that uses this source signs in the way the source says. Two sources cannot share a name. Omit id to create; pass an id from list_sql_sources to update. Next: test_sql_source to prove it works before a check depends on it.")]
        public static async Task<SqlSourceRecord> SaveSqlSource(
            IEngine engine,
            [Description("What to call it, shown wherever a source is picked (e.g. 'Contoso warehouse')")] string name,
            [Description("The SQL server address ONLY, never a connection string")] string server,
            [Description("The database on that server")] string database,
            [Description("How to sign in: interactive | devicecode | azcli | serviceprincipal. Never a token.")] string authMode,
            [Description("Optional Entra tenant id")] string tenantId = null,
            [Description("Id from list_sql_sources to update an existing source; omit to create a new one")] string id = null)
            => await engine.SaveSqlSourceAsync(id, name, server, database, authMode, tenantId, "agent");

        [McpServerTool(Name = "test_sql_source"), Description("Open a connection to one saved SQL source using ITS OWN sign-in and run a single test query (SELECT 1). Returns ok plus a plain-words note, and records the outcome on the source so list_sql_sources can show it. A failure is recorded as a failure and is never rounded up to ok. This reads nothing from the user's data: one constant value, no table touched. Agent-origin calls go through the standard QueryData permission gate on that SQL target and can never trigger an interactive sign-in: a stale cached sign-in fails honestly instead of opening a browser on the user's machine. Use it before a check or a table mapping depends on the source. FREE.")]
        public static async Task<SqlSourceTestResult> TestSqlSource(
            IEngine engine,
            [Description("Source id from list_sql_sources")] string id)
            => await engine.TestSqlSourceAsync(id, "agent");

        [McpServerTool(Name = "delete_sql_source"), Description("Remove a saved SQL source. REFUSED while any model uses it as its SQL endpoint (the refusal names those models; clear them with set_model_sql_endpoint first), and while the open model's saved checks or table mappings still point at it, and the refusal SAYS HOW MANY of each, so 'still in use' is never an unexplained no. Change or remove those first. Returns deleted=false with a plain note rather than throwing. Honest limit: this can only see the model that is OPEN. A source used by a model that is not open can still be removed, and those checks then report that the source they were set to use is no longer saved rather than quietly running somewhere else. Next: list_tests to see which checks use a source.")]
        public static async Task<SqlSourceDeleteResult> DeleteSqlSource(
            IEngine engine,
            [Description("Source id from list_sql_sources")] string id)
            => await engine.DeleteSqlSourceAsync(id, "agent");

        [McpServerTool(Name = "list_table_mappings"), Description("EVERY table in the model, where its row count is read from, and how the last count went. This row IS the table-count check: there is no saved-test kind for a table count, and mapping a table with set_table_source_mapping is the same action as adding one. 'source' says where the coordinates came from: 'mapping' (a person chose a saved SQL source for this table), 'endpoint' (the model's SQL endpoint, with the schema and table read from the table's own partition), 'detected' (read from the table's own partition, when the model has no endpoint) or 'none'. 'canCount' false means 'reason' says in plain words what is missing, for example a table that is not mapped yet, a mapping that does not name a schema and table, an ambiguous source, or a source that has been removed. 'editable' is true for every table that could be counted, so a table with no source at all still has a row to point at a source. The last outcome (lastVerdict, lastMessage, lastModelCount, lastSourceCount, lastRunUtc) comes from the last run in this session and is ABSENT when nothing has been counted yet, which is its own state and never a pass or a failure. Counts that differ without proven matching snapshots stay 'couldn't check'. Read-only. Next: set_table_source_mapping to point a table at a saved source, run_tests to count.")]
        public static async Task<TableSourceMappingList> ListTableMappings(IEngine engine)
            => await engine.ListTableSourceMappingsAsync();

        [McpServerTool(Name = "set_table_source_mapping"), Description("Point one model table at a named SQL source and the table inside it, so the ambient row-count check knows WHERE to count. Without a mapping the check reads whatever the table's own partition reveals, and a table whose partition does not say enough reports 'not mapped to a SQL source yet' with nothing to fix it. Pass the model table, a sqlSourceId from list_sql_sources, and the schema and table name in the source. The mapping WINS over anything detected from the model, replaces any mapping that table already had, and is stored with the model's test data (.semanticus/tests/) so it travels with the model. It supplies where to count, NEVER a verdict: counts that differ without proven snapshot alignment stay 'couldn't check', not a failure. Next: run_tests to count, clear_table_source_mapping to undo it.")]
        public static async Task<TableSourceMappingInfo> SetTableSourceMapping(
            IEngine engine,
            [Description("The model table to map (a name or 'table:Name')")] string table,
            [Description("Source id from list_sql_sources")] string sqlSourceId,
            [Description("The schema in the source, e.g. 'dbo'")] string schema = null,
            [Description("The table name in the source, e.g. 'fact_sales'")] string entity = null)
            => await engine.SetTableSourceMappingAsync(table, sqlSourceId, schema, entity, "agent");

        [McpServerTool(Name = "clear_table_source_mapping"), Description("Forget one model table's SQL source mapping. The row-count check then goes back to reading the table's own partition, which is where it was before anyone mapped it. Returns false when that table had no mapping. Changes no model object and needs no deploy.")]
        public static async Task<bool> ClearTableSourceMapping(
            IEngine engine,
            [Description("The model table to unmap (a name or 'table:Name')")] string table)
            => await engine.ClearTableSourceMappingAsync(table, "agent");

        [McpServerTool(Name = "list_connection_history"), Description("The device-local connection TIMELINE (connects, opens, account switches and sign-ins), newest first, the same log the Connections drawer shows. Optionally filter to ONE connection id (from list_connections). Each event carries the WHERE (endpoint/dataset), the account UPN in play when known, the outcome (ok. A failed open that changed nothing is still recorded), a short detail note, and a UTC timestamp. Holds NO credentials, never a token or secret. Use it to see who last connected where, or to confirm an account switch actually took effect. Read-only and free. Next: probe_connection_accounts for who the NEXT open will sign in as; list_connections for the target list.")]
        public static async Task<ConnectionHistoryEvent[]> ListConnectionHistory(IEngine engine,
            [Description("Only this connection id (from list_connections); omit for the whole timeline")] string connectionId = null,
            [Description("Most-recent N events to return (default 50)")] int limit = 50)
        {
            var all = await engine.ListConnectionHistoryAsync(string.IsNullOrWhiteSpace(connectionId) ? null : connectionId);
            return limit > 0 && all.Length > limit ? all.Take(limit).ToArray() : all;   // newest-first already — cap keeps the result token-frugal
        }

        [McpServerTool(Name = "probe_connection_accounts"), Description("For each remembered XMLA target, the account (UPN) the NEXT open will actually sign in AS, a cheap SILENT probe (a local disk read of the saved sign-in, no network, no prompt). A sign-in is remembered per tenant and credential family (interactive / device-code), so 'account' is that identity, which may differ from a target's own last-used one. 'account' is null when it is genuinely UNKNOWN: no saved MSAL record for that tenant and family (for example azcli / service-principal keep none); a null account is unknown, never signed-out. 'previousAccount' surfaces the target's last-used account as provenance ('was' / 'last opened as'), both when a different current account supersedes it AND when the current account is unknown, so a prediction is never invented from the last-used hint. Holds no credential. Use it to show 'as <account>' before connecting, or to confirm which identity a switch left in place. Read-only and free. Next: list_connections for the full target list; list_connection_history for the timeline.")]
        public static async Task<ConnectionAccountProbe[]> ProbeConnectionAccounts(IEngine engine)
            => await engine.ProbeConnectionAccountsAsync();

        [McpServerTool(Name = "probe_auth_prerequisites"), Description("PREVIEW whether an ADVANCED sign-in mode (serviceprincipal | azcli) can actually authenticate on this machine, so you can warn the user BEFORE a connect attempt instead of failing after one. Returns PRESENCE + NAMES ONLY, NEVER a secret value: for serviceprincipal, which of the client id / client secret / tenant env vars (each with its accepted alias names) are set; for azcli, whether a signed-in CLI session was detected (and its account/tenant when so). 'ready' is the overall verdict; 'detail' is an honest, actionable note when NOT ready (e.g. 'Run az login'). This is a LOCAL-ONLY probe: no interactive prompt, no network sign-in beyond a local `az account show`. Read-only and free. Next: connect_xmla / open_live to actually connect once ready is true.")]
        public static async Task<AuthPrerequisites> ProbeAuthPrerequisites(IEngine engine,
            [Description("serviceprincipal (or sp) | azcli (default)")] string mode = "azcli",
            [Description("Optional Entra tenant id or domain to target")] string tenantId = null)
            => await engine.ProbeAuthPrerequisitesAsync(mode, tenantId);

        [McpServerTool(Name = "list_account_profiles"), Description("The saved Microsoft identities on THIS device: the multi-account profile store. Device-local and credential-free (username, tenant, sign-in method, last sign-in, last use), a local read with no network and no prompt; it holds NO token or secret (tokens stay only in the encrypted Microsoft cache). 'isDefault' marks the account an UNQUALIFIED open of that tenant uses; 'signedIn' is whether a saved sign-in RECORD exists here (the strongest a no-network read can prove: the cached token may still need a silent refresh, and if it has aged out YOUR acquisition fails with an honest error, never a prompt). A signed-out profile has no record and needs an interactive sign-in, a human action. Pass a profile 'id' as accountProfileId to open_live / connect_xmla to open AS that account for one open WITHOUT changing the tenant default. You may SELECT a signed-in profile, but adding a NEW account, an interactive sign-in, and making an account the default are all HUMAN actions done from Connections. Read-only and free. Next: open_live/connect_xmla with accountProfileId; list_connections for the target list.")]
        public static async Task<AccountProfile[]> ListAccountProfiles(IEngine engine)
            => await engine.ListAccountProfilesAsync();

        [McpServerTool(Name = "remember_xmla_connection"), Description("Remember an XMLA endpoint in the shared connection registry without connecting to it. Stores endpoint, optional model name and auth MODE only; never a token or secret. New records are unlabelled and therefore treated as production until the human labels them. Use this only when the endpoint is already known, then list_connections to retrieve its id. This does not open, query, edit, or publish a model.")]
        public static async Task<ModelConnectionRecord> RememberXmlaConnection(IEngine engine,
            [Description("XMLA endpoint")] string endpoint,
            [Description("Optional model/database name")] string database = null,
            [Description("Optional display model name")] string modelName = null,
            [Description("azcli | interactive | serviceprincipal")] string authMode = "azcli")
            => await engine.RememberXmlaConnectionAsync(endpoint, database, modelName, authMode, "agent");

        [McpServerTool(Name = "connection_context"), Description("Explain the model identity currently being EDITED and the model identity currently answering QUERIES. They may deliberately be two copies: for example, a safe local working copy for edits while the published model answers real-data queries. Returns relationship=sameInstance only when that is proven; workingCopyAndPublished and twoModels explicitly mean two model contexts are in play. Read this before comparing, testing, or deploying so you never assume the query target is the write target. 'sqlEndpoint' is the open model's SQL endpoint (the saved SQL source its checks read from unless a check names its own), null when it has none; 'sqlEndpointSuggestion' is what the model's own sources read from, offered when no endpoint is set. Holds no credentials. Read-only and free. Next: list_connections to choose a remembered model, connect_xmla/connect_local to change only the query model, or open_live/open_local to edit and query one live model.")]
        public static async Task<ConnectionContext> GetConnectionContext(IEngine engine)
        {
            var result = await engine.ConnectionContextAsync();
            Emit(engine, new ActivityEvent { Kind = "connection_context", Origin = "agent", Label = "Inspected editing and query models", Ok = true, Result = result });
            return result;
        }

        [McpServerTool(Name = "prepare_working_copy"), Description("Preview or confirm the safe local-edit workflow from a remembered SOURCE connection. The source may be XMLA or a local running model. Default commit=false is read-only: it names the local folder, explains which model will answer tests/queries, names the separate XMLA publish destination when one is selected, and refuses any non-empty folder not proven to belong to that source. commit=true creates or opens the durable local model, then attaches the optional queryConnectionId. Omit queryConnectionId to query the source. For a local source, publishConnectionId is optional and must name an XMLA connection; for an XMLA source it defaults to the source. Local files alone cannot execute DAX. Reopening NEVER refreshes or overwrites local edits, and this operation NEVER pushes changes. Use list_connections for the ids. Next: read connection_context, edit and test locally, then review changes before an explicit push to the linked published model.")]
        public static async Task<WorkingCopyResult> PrepareWorkingCopy(IEngine engine,
            [Description("Connection id from list_connections")] string connectionId,
            [Description("Parent folder for a new <model>.SemanticModel working copy; omit to reopen the connection's existing workingFolder")] string parentFolder = null,
            [Description("false previews without writing; true confirms creation/open and query attachment")] bool commit = false,
            [Description("Optional connection id that will answer tests/queries; omit to query the source")] string queryConnectionId = null,
            [Description("Optional final XMLA publish destination; defaults to the source when the source is XMLA, and remains unlinked when the source is localDesktop")] string publishConnectionId = null)
        {
            try
            {
                var result = await engine.PrepareWorkingCopyAsync(connectionId, parentFolder, commit, queryConnectionId, publishConnectionId, "agent");
                Emit(engine, new ActivityEvent { Kind = "prepare_working_copy", Origin = "agent", Label = commit ? "Prepared local working copy" : "Previewed local working copy", Ok = result.CanCommit && string.IsNullOrEmpty(result.Error), Result = result });
                return result;
            }
            catch (Exception ex)
            {
                Emit(engine, new ActivityEvent { Kind = "prepare_working_copy", Origin = "agent", Label = "Prepare local working copy failed", Ok = false, Error = XmlaAuthHint.Scrub(ex.Message) });
                throw;
            }
        }

        [McpServerTool(Name = "set_publish_destination"), Description("Link the OPEN editable model to a remembered XMLA connection as its explicit final publish destination. Works for Semanticus-created working copies and existing user-owned local files, including files in source control. Writes only machine-local connection metadata: it never edits model/repository files and never deploys. Use list_connections for the XMLA connection id. The actual push remains a separate preview + confirmation through apply_model_diff. Pass clear=true with no connectionId to let go of the destination; the change is recorded in the connection history like a set. Next: connection_context to verify Editing and Publish to before reviewing changes.")]
        public static async Task<ConnectionContext> SetPublishDestination(IEngine engine,
            [Description("XMLA connection id from list_connections. Omit it when clear is true.")] string connectionId = null,
            [Description("true lets go of the open model's publish destination instead of setting one. Nothing is deployed or deleted; clearing when none is set changes nothing and says so in 'note'. A model open live from its workspace saves straight back to it, so its destination cannot be cleared: open a local copy instead.")] bool clear = false)
            => await engine.SetPublishDestinationAsync(connectionId, "agent", clear);

        [McpServerTool(Name = "forget_connection"), Description("Remove a connection from the remembered list (see list_connections). Does not disconnect anything and does not touch the model. It only forgets the endpoint, its auth mode, its target label and its working folder. Re-connecting to the same endpoint records it again, UNLABELLED, which means it will be treated as production until the user labels it.")]
        public static async Task<object> ForgetConnection(IEngine engine,
            [Description("Connection id from list_connections")] string id)
        {
            // Agent origin: the store lets an agent forget an unlabelled scratch connection but refuses a labelled one
            // (its label is governance data). Turn that refusal into a teaching error rather than a stack trace.
            try
            {
                var forgotten = await engine.ForgetConnectionAsync(id, "agent");
                return forgotten
                    ? new { forgotten = true, error = (string)null }
                    : new { forgotten = false, error = "No remembered connection with that id. Run list_connections to see what is known." };
            }
            catch (Exception ex) { return new { forgotten = false, error = ex.Message }; }
        }

        [McpServerTool(Name = "get_agent_policy"), Description("Read the agent-permissions policy: what YOU (the agent) are allowed to do to a live target, by environment. Returns the global on/off switch, the active preset, and the capability×label matrix. Labels are local | dev | uat | prod; an UNLABELLED target is treated as prod (the strictest). Actions are allow | ask | deny: 'ask' means a human must approve the specific action in the UI before you can do it (see list_pending_approvals. You request approval simply by attempting the action). Reading, and running a calculation, are never gated. Use this to know BEFORE you try: if pushing to a target is 'deny' or 'ask', tell the user rather than failing repeatedly. Only a human can change this policy or the labels it reads; the user sets labels in Connections, in VS Code. Read-only and free.")]
        public static async Task<AgentPolicy> GetAgentPolicy(IEngine engine) => await engine.GetAgentPolicyAsync();

        [McpServerTool(Name = "list_pending_approvals"), Description("The 'waiting for you' queue: actions you attempted that the policy said need a human's approval (an 'ask'). Each entry is bound to the EXACT action (capability + target + intent); the human approves it in the UI, then you retry the SAME action and it proceeds once (a grant is one-shot and expires). Use this to see what you're waiting on. You cannot approve your own actions. That asymmetry is the point. Read-only and free.")]
        public static async Task<ApprovalRecord[]> ListPendingApprovals(IEngine engine) => await engine.ListPendingApprovalsAsync();

        [McpServerTool(Name = "list_restore_points"), Description("List the pre-push snapshots a published model can be rolled back to. Every committed deploy_live write and every committed apply_model_diff push to an XMLA endpoint writes one FIRST, so the push can be undone. This is the only way back from a live DELETE (a deploy re-creates measures, calculated columns, calculated tables and named expressions, but a relationship, role, perspective, hierarchy, partition, culture, datasource or data table removed from a published model is otherwise gone for good). Newest first. Only the newest 10 per target are kept. Filter by endpoint + database, or omit both for every target. Snapshots live in ~/.semanticus/restore and hold the target's full model METADATA (measures, M queries, role filters, connection strings, never row data) in clear text; purge_restore_points removes them. An integrityError means rollback is refused, while purge remains available through its validated storage path. Read-only and free. Next: rollback_push(id) to preview a restore.")]
        public static async Task<RestorePointRecord[]> ListRestorePoints(IEngine engine,
            [Description("Only restore points for this XMLA endpoint (requires database)")] string endpoint = null,
            [Description("Only restore points for this dataset (database) name")] string database = null)
            => await engine.ListRestorePointsAsync(endpoint, database);

        [McpServerTool(Name = "rollback_push"), Description("Undo a push to a PUBLISHED model by restoring it to a pre-push snapshot (see list_restore_points). DRY RUN by default: commit=false reports exactly what would be restored and (critically) what would be REMOVED, i.e. every object that exists on the target now but not in the snapshot. That includes anything the push added AND anything anyone else added since, so READ RemovedRefs before committing. Pass commit=true to write. Restores the object kinds a redeploy cannot: relationships, roles (RLS), perspectives, hierarchies, partitions, cultures, datasources and data tables. Resolution is by lineage identity, so an object republished under you is refused rather than overwritten. Recorded in the audit trail. FREE at both tiers. The undo for an irreversible write is never gated. Next: run list_restore_points to find the id; run rollback_push(id) to preview; then rollback_push(id, commit=true).")]
        public static async Task<RollbackResult> RollbackPush(IEngine engine,
            [Description("Restore point id from list_restore_points (e.g. '20260710T031500Z-3f9c1a')")] string restorePointId,
            [Description("Auth mode for the endpoint (default 'azcli'; e.g. 'serviceprincipal', 'interactive')")] string authMode = null,
            [Description("false = preview what would change (default); true = write the target")] bool commit = false)
        {
            var r = await engine.RollbackPushAsync(restorePointId, commit, authMode, "agent");
            Emit(engine, new ActivityEvent { Kind = "rollback_push", Origin = "agent", Label = commit ? "Rolled a published model back to a restore point" : "Previewed a rollback", Ok = string.IsNullOrEmpty(r.Error), Result = r });
            return r;
        }

        [McpServerTool(Name = "purge_restore_points"), Description("Permanently delete pre-push snapshots from ~/.semanticus/restore using a review-then-confirm flow. Choose exactly one selector: id removes one restore point, or olderThanDays selects every snapshot older than that non-negative age. DRY RUN by default: confirm=false returns the exact candidates and a token without deleting. After reviewing them, pass confirm=true with that exact confirmToken; a stale or changed candidate set is refused. Reports partial file-delete failures honestly. Use this to clear model metadata from disk only when you accept that the guarded live push can no longer be undone.")]
        public static Task<RestorePointPurgeResult> PurgeRestorePoints(IEngine engine,
            [Description("Select exactly this restore point id; do not combine with olderThanDays")] string id = null,
            [Description("Or select every restore point older than this non-negative number of days; do not combine with id")] int? olderThanDays = null,
            [Description("false (default) previews exact candidates; true permanently deletes only with the matching preview token")] bool confirm = false,
            [Description("Exact token returned by the immediately preceding dry-run preview")] string confirmToken = null)
            => engine.PurgeRestorePointsAsync(id, olderThanDays, confirm, confirmToken, "agent");

        [McpServerTool(Name = "cherry_pick"), Description("Copy objects FROM another model INTO the OPEN model (cross-model copy/paste), e.g. copy a measure from a teammate's model into yours. DRY RUN by default (commit=false reports what would copy, what already exists (would overwrite), and what can't). Pass commit=true to apply as ONE undoable batch. Source is a model on disk (sourceFile) or a git ref of the open model's repo (gitRef). refs are object refs like ['measure:Sales/Margin %']. Supported into the open model: measures, calculated columns, and calculation items (into an existing group). For a whole table or calculation group, use apply_model_diff to a file target instead.")]
        public static async Task<CherryPickResult> CherryPick(IEngine engine,
            [Description("Object refs to copy into the open model, e.g. ['measure:Sales/Margin %']")] string[] refs,
            [Description("Copy FROM this model on disk (TMDL/PBIP folder or .bim)")] string sourceFile = null,
            [Description("...or copy FROM this git ref of the open model's repo (e.g. 'main', a commit hash)")] string gitRef = null,
            [Description("false = preview (default); true = apply into the open model")] bool commit = false)
        {
            ModelRef source = !string.IsNullOrWhiteSpace(sourceFile) ? new ModelRef { Kind = "file", Path = sourceFile }
                : !string.IsNullOrWhiteSpace(gitRef) ? new ModelRef { Kind = "gitref", GitRef = gitRef }
                : new ModelRef { Kind = "session" };
            var r = await engine.CherryPickAsync(source, refs, true, commit, "agent");
            Emit(engine, new ActivityEvent { Kind = "cherry_pick", Origin = "agent", Label = commit ? "Copied objects into the model" : "Previewed a copy", Ok = string.IsNullOrEmpty(r.Error), Result = r });
            return r;
        }

        [McpServerTool(Name = "deploy_gate"), Description("The deploy-readiness gate for THIS change: findings the change set introduces or touches can block; older whole-model warnings become olderWarnings (a count, never a block) plus checkLine pointing at AI Readiness. With no change set to scope to (no edits in this session yet, a model-wide edit, or no saved or published copy) it judges the WHOLE model instead: wholeModel=true and checkLine opens with why. Read-only. WAIVED BPA findings never block (they're audited acceptances, bpaWaivedBlocking counts them); readiness hard-gates still apply when this change touches them.")]
        public static Task<DeployGate> DeployGate(IEngine engine) => engine.DeployGateAsync(null, "agent");

        // ---- Fabric REST (the cloud ALM lane — read-only discovery) -----------------------------------
        // Live GETs against api.fabric.microsoft.com using the user's OWN Entra identity (the engine holds no
        // Anthropic/Fabric credentials). Read-only — no deploy/write here. authMode defaults to azcli; a service
        // principal (AZURE_CLIENT_ID/SECRET/TENANT env) is the reliable headless path.
        [McpServerTool(Name = "list_workspaces"), Description("List the Fabric workspaces you can access (id, displayName, type, capacity). Live read against api.fabric.microsoft.com using your Entra identity. Read-only.")]
        public static Task<FabricWorkspace[]> ListWorkspaces(IEngine engine,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
            => engine.ListWorkspacesAsync(authMode, null, "agent", cancellationToken);

        [McpServerTool(Name = "list_workspace_models"), Description("List the semantic models in one Fabric workspace (id, displayName, description). Pair with list_workspaces to find the workspace id, then connect_xmla or open_live with the workspace address and the model's displayName as the database. Live read against api.fabric.microsoft.com using your Entra identity. Read-only.")]
        public static Task<FabricSemanticModel[]> ListWorkspaceModels(IEngine engine,
            [Description("Workspace id, from list_workspaces")] string workspaceId,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
            => engine.ListWorkspaceModelsAsync(workspaceId, authMode, null, "agent", cancellationToken);

        [McpServerTool(Name = "list_deployment_pipelines"), Description("List the Fabric deployment pipelines you can access (id, displayName, description). Use get_pipeline_stages then get_stage_items to drill into a pipeline's Dev/Test/Prod stages. Live read; read-only.")]
        public static Task<DeploymentPipeline[]> ListDeploymentPipelines(IEngine engine,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
            => engine.ListDeploymentPipelinesAsync(authMode, null, "agent", cancellationToken);

        [McpServerTool(Name = "get_pipeline_stages"), Description("List a deployment pipeline's stages in order (Dev=0, Test=1, Prod=2…) with each stage's assigned workspace. Needs an Admin role on the pipeline. Live read; read-only.")]
        public static Task<PipelineStage[]> GetPipelineStages(IEngine engine,
            [Description("Deployment-pipeline id (from list_deployment_pipelines)")] string pipelineId,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
            => engine.GetPipelineStagesAsync(pipelineId, authMode, null, "agent", cancellationToken);

        [McpServerTool(Name = "get_stage_items"), Description("List the items in a pipeline stage (itemType e.g. SemanticModel/Report, with source/target item ids + last deployment time). Use it to resolve the SemanticModel itemId to deploy. Needs Contributor on the stage workspace. Live read; read-only.")]
        public static Task<StageItem[]> GetStageItems(IEngine engine,
            [Description("Deployment-pipeline id")] string pipelineId,
            [Description("Stage id (from get_pipeline_stages)")] string stageId,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
            => engine.GetStageItemsAsync(pipelineId, stageId, authMode, null, "agent", cancellationToken);

        // ---- Deployment pipeline: preview + the GATED deploy (write lane) + history -------------------
        [McpServerTool(Name = "preview_deploy"), Description("Preview what promoting a model between pipeline stages WOULD change (New vs Update per item, by source→target pairing) + the readiness gate. Read-only, deploys nothing. ('Update' items may be identical; Different-vs-NoDifference is only known after a real deploy.)")]
        public static Task<DeployPreview> PreviewDeploy(IEngine engine,
            [Description("Deployment-pipeline id")] string pipelineId,
            [Description("Source stage id (e.g. Dev)")] string sourceStageId,
            [Description("Target stage id (e.g. Test)")] string targetStageId,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
            => engine.PreviewDeployAsync(pipelineId, sourceStageId, targetStageId, authMode, null, "agent", cancellationToken);

        [McpServerTool(Name = "deploy_stage"), Description("Promote items between Fabric deployment-pipeline stages. DRY RUN by default (the New/Update preview); commit=true deploys. An agent may deploy to NON-production stages only. Promoting to PRODUCTION needs a human confirming from the Deploy tab with the confirmToken their own dry-run shows. The readiness gate does not apply to a stage move. items = source item ids (empty = all supported).")]
        public static async Task<DeployStageReport> DeployStage(IEngine engine,
            [Description("Deployment-pipeline id")] string pipelineId,
            [Description("Source stage id (e.g. Dev)")] string sourceStageId,
            [Description("Target stage id (e.g. Test)")] string targetStageId,
            [Description("Source item ids to deploy; empty = all supported items")] string[] items = null,
            [Description("Optional deployment note")] string note = null,
            [Description("false = DRY RUN preview (default); true = deploy")] bool commit = false,
            [Description("The confirmToken from a human dry-run (production only; an agent cannot obtain it)")] string confirmToken = null,
            [Description("Ignored. The readiness gate no longer applies to a stage move.")] bool forceOverride = false,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            [Description("Ignored. The readiness gate no longer applies to a stage move.")] string overrideReason = null,
            CancellationToken cancellationToken = default)
        {
            var r = await engine.DeployStageAsync(pipelineId, sourceStageId, targetStageId, items, note, commit, confirmToken, forceOverride, authMode, null, "agent", overrideReason, cancellationToken);
            Emit(engine, new ActivityEvent { Kind = "deploy_stage", Origin = "agent", Label = commit ? (r.Committed ? "Deployed a pipeline stage" : "Deploy refused/failed") : "Previewed a deploy", Ok = string.IsNullOrEmpty(r.Error), Result = r });
            return r;
        }

        [McpServerTool(Name = "deployment_history"), Description("List a deployment pipeline's recent deployments (status, source→target stages, item counts, who deployed). Read-only.")]
        public static Task<DeploymentHistoryEntry[]> DeploymentHistory(IEngine engine,
            [Description("Deployment-pipeline id")] string pipelineId,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
            => engine.DeploymentHistoryAsync(pipelineId, authMode, null, "agent", cancellationToken);

        // ---- Fabric Git (workspace ⇄ git) — reads + GATED writes ----------------------------------------
        [McpServerTool(Name = "fabric_git_connection"), Description("The workspace's Fabric Git connection: provider (Azure DevOps / GitHub), repo/branch/directory, connection state, last-synced commit. Read-only.")]
        public static Task<FabricGitConnection> FabricGitConnection(IEngine engine,
            [Description("Workspace id")] string workspaceId,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
            => engine.FabricGitConnectionAsync(workspaceId, authMode, null, "agent", cancellationToken);

        [McpServerTool(Name = "fabric_git_status"), Description("The workspace ⇄ git diff: each changed item (workspace change vs remote change vs conflict), plus the workspace + remote commit hashes. Read-only.")]
        public static Task<FabricGitStatus> FabricGitStatus(IEngine engine,
            [Description("Workspace id")] string workspaceId,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
            => engine.FabricGitStatusAsync(workspaceId, authMode, null, "agent", cancellationToken);

        [McpServerTool(Name = "fabric_git_commit"), Description("Commit the WORKSPACE's changes to git (workspace→git). DRY RUN by default (commit=false reports the pending change count; writes nothing). Pass commit=true to commit. items = item objectIds for a selective commit (empty = all).")]
        public static async Task<FabricGitResult> FabricGitCommit(IEngine engine,
            [Description("Workspace id")] string workspaceId,
            [Description("Commit message")] string comment = null,
            [Description("Item objectIds to commit; empty = all workspace changes")] string[] items = null,
            [Description("false = DRY RUN (default); true = commit to git")] bool commit = false,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
        {
            var r = await engine.FabricGitCommitAsync(workspaceId, comment, items, commit, authMode, null, "agent", cancellationToken);
            Emit(engine, new ActivityEvent { Kind = "fabric_git_commit", Origin = "agent", Label = commit ? "Committed workspace to git" : "Previewed a Fabric git commit", Ok = string.IsNullOrEmpty(r.Error), Result = r });
            return r;
        }

        [McpServerTool(Name = "fabric_git_update"), Description("Update the WORKSPACE from git (git→workspace): OVERWRITES workspace items with the git version. DRY RUN by default (commit=false reports the incoming change count + any conflicts). Pass commit=true to apply. conflictPolicy = PreferRemote (default) | PreferWorkspace; allowOverride=true permits overwriting items that have workspace changes.")]
        public static async Task<FabricGitResult> FabricGitUpdate(IEngine engine,
            [Description("Workspace id")] string workspaceId,
            [Description("PreferRemote (default) | PreferWorkspace")] string conflictPolicy = "PreferRemote",
            [Description("Allow overwriting items that have workspace changes")] bool allowOverride = false,
            [Description("false = DRY RUN (default); true = update the workspace from git")] bool commit = false,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
        {
            var r = await engine.FabricGitUpdateAsync(workspaceId, conflictPolicy, allowOverride, commit, authMode, null, "agent", cancellationToken);
            Emit(engine, new ActivityEvent { Kind = "fabric_git_update", Origin = "agent", Label = commit ? "Updated workspace from git" : "Previewed a Fabric git update", Ok = string.IsNullOrEmpty(r.Error), Result = r });
            return r;
        }

        [McpServerTool(Name = "fabric_git_connect"), Description("Connect a workspace to a git repo (Azure DevOps or GitHub). DRY RUN by default (commit=false). Pass commit=true to connect. Needs workspace Admin. After connecting, initialize + fabric_git_update/commit to sync.")]
        public static async Task<FabricGitResult> FabricGitConnect(IEngine engine,
            [Description("Workspace id")] string workspaceId,
            [Description("AzureDevOps | GitHub")] string provider,
            [Description("Azure DevOps organization OR GitHub owner")] string organization,
            [Description("Azure DevOps project (ADO only)")] string project = null,
            [Description("Repository name")] string repository = null,
            [Description("Branch (default main)")] string branch = "main",
            [Description("Directory within the repo (default /)")] string directory = null,
            [Description("A configured-connection id (required for GitHub; optional for ADO which can use automatic creds)")] string connectionId = null,
            [Description("false = DRY RUN (default); true = connect")] bool commit = false,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
        {
            var r = await engine.FabricGitConnectAsync(workspaceId, provider, organization, project, repository, branch, directory, connectionId, commit, authMode, null, "agent", cancellationToken);
            Emit(engine, new ActivityEvent { Kind = "fabric_git_connect", Origin = "agent", Label = commit ? "Connected workspace to git" : "Previewed a Fabric git connect", Ok = string.IsNullOrEmpty(r.Error), Result = r });
            return r;
        }

        [McpServerTool(Name = "fabric_git_disconnect"), Description("Disconnect a workspace from git. DRY RUN by default (commit=false). Pass commit=true to disconnect. Needs workspace Admin.")]
        public static async Task<FabricGitResult> FabricGitDisconnect(IEngine engine,
            [Description("Workspace id")] string workspaceId,
            [Description("false = DRY RUN (default); true = disconnect")] bool commit = false,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
        {
            var r = await engine.FabricGitDisconnectAsync(workspaceId, commit, authMode, null, "agent", cancellationToken);
            Emit(engine, new ActivityEvent { Kind = "fabric_git_disconnect", Origin = "agent", Label = commit ? "Disconnected workspace from git" : "Previewed a Fabric git disconnect", Ok = string.IsNullOrEmpty(r.Error), Result = r });
            return r;
        }

        [McpServerTool(Name = "cicd_publish"), Description("Publish the OPEN model's on-disk definition (PBIP/TMDL) to a Fabric workspace via updateDefinition, a FULL OVERWRITE of the target semantic model. DRY RUN by default (enumerates parts + target, writes nothing); the live publish (commit=true) is human-only from the Deploy tab. An agent's commit is refused, previewing is allowed.")]
        public static async Task<CicdPublishResult> CicdPublish(IEngine engine,
            [Description("Target workspace id")] string workspaceId = null,
            [Description("Target semantic-model item id (in that workspace)")] string itemId = null,
            [Description("false = DRY RUN (default); true = publish (refused for the agent door)")] bool commit = false,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
        {
            var r = await engine.CicdPublishAsync(workspaceId, itemId, commit, authMode, null, "agent", cancellationToken);
            Emit(engine, new ActivityEvent { Kind = "cicd_publish", Origin = "agent", Label = r.Committed ? "Published model to a workspace" : "Previewed a CI/CD publish", Ok = string.IsNullOrEmpty(r.Error), Result = r });
            return r;
        }

        [McpServerTool(Name = "cicd_generate"), Description("Generate a ready-to-run fabric-cicd CI scaffold (parameter.yml + a GitHub Actions or Azure DevOps workflow + deploy.py) running the real publish_all_items in CI. Pure file authoring, no Fabric call. Returns the contents; write=true also writes them into the repo. With write=true the files are written into the person's repository, which is a file write: under the Locked policy the person is asked first, and the question lists the files.")]
        public static async Task<CicdScaffold> CicdGenerate(IEngine engine,
            [Description("github (default, GitHub Actions) | ado (Azure DevOps)")] string target = "github",
            [Description("Target workspace id to seed parameter.yml (optional)")] string workspaceId = null,
            [Description("Environment name for the parameter.yml replace_value key (default PROD)")] string environment = "PROD",
            [Description("false = return contents only (default); true = also write the files into the repo")] bool write = false)
        {
            var r = await engine.CicdGenerateAsync(target, workspaceId, environment, write, "agent");
            Emit(engine, new ActivityEvent { Kind = "cicd_generate", Origin = "agent", Label = r.Written ? "Wrote a fabric-cicd CI scaffold" : "Generated a fabric-cicd CI scaffold", Ok = string.IsNullOrEmpty(r.Error), Result = r });
            return r;
        }

        // ---- Fabric Data Agent (definition-based item) ---------------------------------------------------
        // The whole Data agent view is Pro; create/update/publish/delete are DRY-RUN by default.
        // The engine publishes the ActivityEvent for every EXECUTED write (data_agent_*), so these tools do NOT
        // Emit again (a dry-run changes nothing; a commit is logged once by the engine on both doors).
        [McpServerTool(Name = "list_data_agents"), Description("List the Fabric data agents in a workspace (id, name, description, type). Live read against api.fabric.microsoft.com with your Entra identity; read-only. [verify-at-build]: the item type string isn't documented yet, so this filters items whose type CONTAINS 'dataagent' (case-insensitive). If none match, ObservedItemTypes lists every item type in the workspace so you can confirm the real one.")]
        public static Task<DataAgentList> ListDataAgents(IEngine engine,
            [Description("Workspace id (from list_workspaces)")] string workspaceId,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
            => engine.ListDataAgentsAsync(workspaceId, authMode, null, "agent", cancellationToken);

        [McpServerTool(Name = "get_data_agent"), Description("Get one data agent's decoded configuration: the draft (and published, if any) stage, aiInstructions, each data source (type, ids, descriptions, the raw elements tree, few-shots), and the publish description. Live read; read-only.")]
        public static Task<DataAgentDetail> GetDataAgent(IEngine engine,
            [Description("Workspace id")] string workspaceId,
            [Description("Data agent item id (from list_data_agents)")] string agentId,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
            => engine.GetDataAgentAsync(workspaceId, agentId, authMode, null, "agent", cancellationToken);

        [McpServerTool(Name = "generate_data_agent_config"), Description("Build a complete semantic_model datasource config FROM the open model in one shot: an element tree of every table/column/measure with model descriptions carried and is_selected honoring hidden objects + the Prep-for-AI AI-data-schema exclusions; aiInstructions seeded from the model's LSDL instructions. Returns the JSON for review (feed it to update_data_agent). Writes NOTHING. artifactId/workspaceId come back as placeholders (resolve the real Fabric ids first, see the Note).")]
        public static Task<DataAgentConfig> GenerateDataAgentConfig(IEngine engine,
            [Description("Max columns to emit per table (default 200)")] int maxColumnsPerTable = 200)
            => engine.GenerateDataAgentConfigFromModelAsync(maxColumnsPerTable);

        [McpServerTool(Name = "create_data_agent"), Description("Create a new (empty draft) Fabric data agent: data_agent.json + a minimal draft stage_config with aiInstructions. DRY RUN by default: commit=false returns the exact request and changes NOTHING; commit=true creates it. aiInstructions capped at 15000 chars.")]
        public static Task<DataAgentWriteReport> CreateDataAgent(IEngine engine,
            [Description("Target workspace id")] string workspaceId,
            [Description("Data agent display name")] string name,
            [Description("AI instructions for the agent (<=15000 chars; empty allowed)")] string aiInstructions = null,
            [Description("false = DRY RUN (default, sends nothing); true = create")] bool commit = false,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
            => engine.CreateDataAgentAsync(workspaceId, name, aiInstructions, commit, authMode, null, "agent", cancellationToken);

        [McpServerTool(Name = "update_data_agent"), Description("Update a data agent's DRAFT: replace only the parts you pass (null = keep). Read-modify-write: fetches the current definition and re-emits ALL existing parts plus your changes (never drops unknown parts). aiInstructions capped at 15000 chars. datasourceJson/fewshotsJson go under the draft/{datasourceFolder}/ folder (folder = 'semantic_model-<name>'). DRY RUN by default: commit=false returns the exact request and changes NOTHING.")]
        public static Task<DataAgentWriteReport> UpdateDataAgent(IEngine engine,
            [Description("Workspace id")] string workspaceId,
            [Description("Data agent item id")] string agentId,
            [Description("New AI instructions (null = keep; <=15000 chars)")] string aiInstructions = null,
            [Description("Datasource folder name, e.g. 'semantic_model-Sales' (required to write datasourceJson/fewshotsJson)")] string datasourceFolder = null,
            [Description("datasource.json content (from generate_data_agent_config); null = keep")] string datasourceJson = null,
            [Description("fewshots.json content; null = keep. NOTE: few-shots aren't supported for semantic-model sources yet ([verify-at-build]).")] string fewshotsJson = null,
            [Description("false = DRY RUN (default, sends nothing); true = update")] bool commit = false,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
            => engine.UpdateDataAgentAsync(workspaceId, agentId, aiInstructions, datasourceFolder, datasourceJson, fewshotsJson, commit, authMode, null, "agent", null, null, cancellationToken);

        [McpServerTool(Name = "publish_data_agent"), Description("Publish a data agent: copy every draft/* part to published/* and write publish_info.json with a description ([verify-at-build]: v1 publishes via the documented definition-write path). DRY RUN by default: commit=false returns the exact request and changes NOTHING; commit=true publishes.")]
        public static Task<DataAgentWriteReport> PublishDataAgent(IEngine engine,
            [Description("Workspace id")] string workspaceId,
            [Description("Data agent item id")] string agentId,
            [Description("Publish description")] string description = null,
            [Description("false = DRY RUN (default, sends nothing); true = publish")] bool commit = false,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
            => engine.PublishDataAgentAsync(workspaceId, agentId, description, commit, authMode, null, "agent", cancellationToken);

        [McpServerTool(Name = "delete_data_agent"), Description("Delete a Fabric data agent item. DRY RUN by default: commit=false returns the exact request and changes NOTHING; commit=true deletes it (irreversible).")]
        public static Task<DataAgentWriteReport> DeleteDataAgent(IEngine engine,
            [Description("Workspace id")] string workspaceId,
            [Description("Data agent item id")] string agentId,
            [Description("false = DRY RUN (default, sends nothing); true = delete")] bool commit = false,
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            CancellationToken cancellationToken = default)
            => engine.DeleteDataAgentAsync(workspaceId, agentId, commit, authMode, null, "agent", cancellationToken);
    }

    /// <summary>Shared wording for the one waiver state that is easy to miss: a stored waiver whose rule id is not in
    /// the loaded rule set. It is appended to the summary Note (not left to a field the caller may not read) because a
    /// waiver that accepts nothing must never pass for protection.</summary>
    internal static class WaiverNotes
    {
        public static string Orphan(int count, string scanOp) => count == 0 ? "" :
            $" Heads up: {count} stored waiver{(count == 1 ? "" : "s")} point at rule ids this rule set does not contain, or at objects the model no longer has, " +
            $"so they accept nothing. See OrphanedWaiverRules (or OrphanedWaivers on {scanOp}); clear them with unwaive_finding, " +
            "or waive again against the rule id that replaced them.";
    }

    /// <summary>A readiness category the summary leaves out of its scores, and why.</summary>
    public sealed class NotScoredCategory
    {
        public string Category { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Token-light projection of a <see cref="Scorecard"/> for ai_readiness_scan with detail='summary':
    /// the grade + per-category scores + finding COUNTS (by severity / fix / top rule), without the findings list.</summary>
    public sealed class ReadinessSummary
    {
        public string Grade { get; set; }
        public double Overall { get; set; }
        public double RawOverall { get; set; }
        public string[] GatedBy { get; set; }
        /// <summary>The categories that have a score. One with nothing to check is listed in <see cref="NotScored"/>
        /// instead, because its Score of 0 read as a failing grade in a summary meant to be skimmed.</summary>
        public CategoryScore[] Categories { get; set; }
        public NotScoredCategory[] NotScored { get; set; } = Array.Empty<NotScoredCategory>();
        public Dictionary<string, double> Coverage { get; set; }
        public int TotalFindings { get; set; }     // ACTIVE (un-waived) findings
        public int SafeFixCount { get; set; }
        public int WaivedCount { get; set; }        // accepted findings excluded from the score (surfaced, never hidden)
        public Dictionary<string, int> FindingsBySeverity { get; set; }
        public Dictionary<string, int> FindingsByFix { get; set; }
        public Dictionary<string, int> TopRules { get; set; }
        public string[] RuleErrors { get; set; }    // custom-rule problems surfaced loudly (mirrors BpaSummary)
        /// <summary>Rules that could not be evaluated, so they were forced dormant and scored nothing. Carried beside
        /// the grade because a grade computed over fewer rules than the caller thinks is the same lie as a short
        /// violation list (mirrors BpaSummary.UnknownCount).</summary>
        public int UnevaluatedRuleCount { get; set; }
        public string[] UnevaluatedRules { get; set; } = Array.Empty<string>();
        // Stored waivers whose rule id is not in the loaded rule set: they protect nothing and are NOT in WaivedCount.
        // Carried in the token-light summary so a dead waiver can't hide behind the "call the full scan" advice.
        public int OrphanedWaiverCount { get; set; }
        public string[] OrphanedWaiverRules { get; set; } = Array.Empty<string>();
        public string Note { get; set; }

        // Severity rank for the severityMin filter (mirrors the Analysis Severity enum: Info=1,Medium=2,High=3,Critical=5).
        public static int SevRank(string s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "critical" => 5, "high" => 3, "medium" => 2, "info" => 1, _ => 0,
        };

        public static ReadinessSummary From(Scorecard c)
        {
            var active = c.Findings.Where(f => !f.IsAccepted()).ToArray();   // counts reflect what still counts against the score
            return new ReadinessSummary
            {
                Grade = c.Grade, Overall = c.Overall, RawOverall = c.RawOverall, GatedBy = c.GatedBy,
                Categories = c.Categories.Where(x => x.HasRules).ToArray(),
                NotScored = c.Categories.Where(x => !x.HasRules)
                    .Select(x => new NotScoredCategory { Category = x.Category, Reason = x.NotScoredReason }).ToArray(),
                Coverage = c.Coverage, TotalFindings = active.Length, SafeFixCount = c.SafeFixCount,
                WaivedCount = c.WaivedCount, RuleErrors = c.RuleErrors,
                UnevaluatedRuleCount = c.UnevaluatedRules.Length, UnevaluatedRules = c.UnevaluatedRules,
                FindingsBySeverity = active.GroupBy(f => f.Severity ?? "?").ToDictionary(g => g.Key, g => g.Count()),
                FindingsByFix = active.GroupBy(f => f.Fix ?? "?").ToDictionary(g => g.Key, g => g.Count()),
                TopRules = active.GroupBy(f => f.RuleId ?? "?").OrderByDescending(g => g.Count()).Take(12)
                            .ToDictionary(g => g.Key, g => g.Count()),
                OrphanedWaiverCount = c.OrphanedWaiverCount,
                OrphanedWaiverRules = c.OrphanedWaivers.Select(w => w.RuleId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Note = (active.Length == 0
                    ? (c.WaivedCount > 0 ? $"No active findings ({c.WaivedCount} waived)." : "No findings.")
                    : "Call ai_readiness_scan (detail='full') with category=<one of Categories> or severityMin to pull only the findings you'll act on.")
                    + WaiverNotes.Orphan(c.OrphanedWaiverCount,"ai_readiness_scan"),
            };
        }
    }

    /// <summary>Token-light projection of a <see cref="BpaScorecard"/> for bpa_scan with detail='summary':
    /// rule/violation/auto-fix counts + violation COUNTS (by category / rule / severity), without the violations list.</summary>
    public sealed class BpaSummary
    {
        public int RuleCount { get; set; }
        public int ViolationCount { get; set; }     // ACTIVE (un-waived) violations
        public int AutoFixable { get; set; }
        public int WaivedCount { get; set; }        // accepted violations excluded from the count (surfaced, never hidden)
        public string[] RuleErrors { get; set; }
        /// <summary>Rules that could not be evaluated on this model. Carried in the token-light summary on purpose: an
        /// agent that only reads the summary must not be able to read "No violations." as a checked-and-clean model.</summary>
        public int UnknownCount { get; set; }
        public string[] UnknownRules { get; set; } = Array.Empty<string>();
        public Dictionary<string, int> ByCategory { get; set; }
        public Dictionary<string, int> ByRule { get; set; }
        public Dictionary<int, int> BySeverity { get; set; }
        // See ReadinessSummary: waivers pointing at rule ids the loaded corpus no longer has. Surfaced, never in WaivedCount.
        public int OrphanedWaiverCount { get; set; }
        public string[] OrphanedWaiverRules { get; set; } = Array.Empty<string>();
        /// <summary>The "Best practice" letter, A to F (BpaGrading), and why. The same name on every surface.</summary>
        public string BestPractice { get; set; }
        public string BestPracticeWhy { get; set; }
        public int ObjectsScanned { get; set; }
        public string Note { get; set; }

        public static BpaSummary From(BpaScorecard c)
        {
            var active = c.Violations.Where(v => !v.IsAccepted()).ToArray();   // the counts the user acts on
            return new BpaSummary
            {
                RuleCount = c.RuleCount, ViolationCount = c.ViolationCount, AutoFixable = c.AutoFixable, WaivedCount = c.WaivedCount, RuleErrors = c.RuleErrors,
                UnknownCount = c.UnknownCount,
                UnknownRules = c.Unknowns.Select(u => u.RuleId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ByCategory = active.GroupBy(v => v.Category ?? "?").ToDictionary(g => g.Key, g => g.Count()),
                ByRule = active.GroupBy(v => v.RuleId ?? "?").OrderByDescending(g => g.Count()).Take(15).ToDictionary(g => g.Key, g => g.Count()),
                BySeverity = active.GroupBy(v => v.Severity).ToDictionary(g => g.Key, g => g.Count()),
                OrphanedWaiverCount = c.OrphanedWaiverCount,
                OrphanedWaiverRules = c.OrphanedWaivers.Select(w => w.RuleId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                BestPractice = c.BestPractice, BestPracticeWhy = c.BestPracticeWhy, ObjectsScanned = c.ObjectsScanned,
                Note = (c.BestPracticeWhy == null ? "" : c.BestPracticeWhy + " ") + (c.ViolationCount == 0
                    ? (c.WaivedCount > 0 ? $"No active violations ({c.WaivedCount} waived)." : "No violations.")
                    : "Call bpa_scan (detail='full') with category/ruleId/autoFixableOnly to pull only the violations you'll act on; apply_safe_fixes with system='bpa' applies all auto-fixable ones.")
                    // The unknown clause rides on the SAME line as the counts, because the counts are the thing it
                    // qualifies: without it "No violations." would read as a clean model when part of the scan
                    // never ran. It names the rules so the reader can go and look rather than just distrust the number.
                    + (c.UnknownCount > 0
                        ? $" {c.UnknownCount} rule(s) could not be evaluated on this model, so their coverage is unknown, not clean: "
                          + string.Join(", ", c.Unknowns.Select(u => u.RuleId).Distinct(StringComparer.OrdinalIgnoreCase))
                          + ". Call bpa_scan to see each one's reason."
                        : "")
                    + WaiverNotes.Orphan(c.OrphanedWaiverCount,"bpa_scan"),
            };
        }
    }

    /// <summary>Token-light projection of a <see cref="ModelGraph"/> for get_model_graph with detail='summary':
    /// structural counts + the disconnected-table list, without the full per-table/per-relationship detail.</summary>
    public sealed class ModelGraphSummary
    {
        public int TableCount { get; set; }
        public int RelationshipCount { get; set; }
        public int DateTables { get; set; }
        public int CalculatedTables { get; set; }
        public int HiddenTables { get; set; }
        public int ManyToMany { get; set; }
        public int Bidirectional { get; set; }
        public int Inactive { get; set; }
        public string[] DisconnectedTables { get; set; }
        public string Note { get; set; }

        public static ModelGraphSummary From(ModelGraph g)
        {
            var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in g.Relationships)
            {
                if (!string.IsNullOrEmpty(r.FromTable)) connected.Add(r.FromTable);
                if (!string.IsNullOrEmpty(r.ToTable)) connected.Add(r.ToTable);
            }
            return new ModelGraphSummary
            {
                TableCount = g.Tables.Length,
                RelationshipCount = g.Relationships.Length,
                DateTables = g.Tables.Count(t => t.IsDateTable),
                CalculatedTables = g.Tables.Count(t => t.IsCalculated),
                HiddenTables = g.Tables.Count(t => t.IsHidden),
                ManyToMany = g.Relationships.Count(r => string.Equals(r.FromCardinality, "Many", StringComparison.OrdinalIgnoreCase)
                                                        && string.Equals(r.ToCardinality, "Many", StringComparison.OrdinalIgnoreCase)),
                Bidirectional = g.Relationships.Count(r => string.Equals(r.CrossFilter, "BothDirections", StringComparison.OrdinalIgnoreCase)),
                Inactive = g.Relationships.Count(r => !r.IsActive),
                // Visible tables in NO relationship — the high-signal star-schema smell. Capped so the summary stays light.
                DisconnectedTables = g.Tables.Where(t => !t.IsHidden && !connected.Contains(t.Name)).Select(t => t.Name).Take(50).ToArray(),
                Note = "Call get_model_graph for the full per-table / per-relationship detail.",
            };
        }
    }

    /// <summary>The §6 orientation primer (harness-engineering.md) for the MCP <c>get_model_summary</c> tool:
    /// the whole map in one round-trip, ruthlessly minimal so it stays under ~2k tokens (a test enforces the
    /// budget). Each section is a COUNT/NAME/GRADE summary that names its drill-down op — never a blob.
    /// Nullable sections are omitted (JsonIgnore) when they don't apply, so an idle model spends no tokens on
    /// empty scaffolding.</summary>
    public sealed class ModelSummary
    {
        public SessionInfo Overview { get; set; }
        public ConnectionBrief Connection { get; set; }
        public EntitlementBrief Entitlement { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ReadinessSummary Readiness { get; set; }   // null with no session open
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ModelGraphSummary Graph { get; set; }      // null with no session open
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PrimerBrief Primer { get; set; }           // null with no session open
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ActiveWorkBrief ActiveWork { get; set; }   // null when nothing is in flight
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public LastSessionBrief LastSession { get; set; } // null when there is no log
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NextAction[] SuggestedNextActions { get; set; } // null/omitted when terminal
        public string Note { get; set; }   // the doc-map: which op to call to drill into each section
    }

    /// <summary>The declared model context, clipped so the session-start orientation stays within its hard budget.</summary>
    public sealed class PrimerBrief
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string UpdatedUtc { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Markdown { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Note { get; set; }

        public static PrimerBrief From(PrimerDocument doc)
        {
            if (doc == null) return null;
            var markdown = (doc.Markdown ?? string.Empty)
                .Replace("_Add what people and the AI Assistant should know._", string.Empty)
                .Trim();
            const int cap = 1800;
            if (markdown.Length > cap) markdown = markdown.Substring(0, cap).TrimEnd() + "\n\n[Primer excerpt clipped. Call get_model_primer for the complete document.]";
            return new PrimerBrief
            {
                UpdatedUtc = doc.UpdatedUtc,
                Markdown = markdown.Length == 0 ? null : markdown,
                Note = doc.Note ?? "Call get_model_primer for the complete document.",
            };
        }
    }

    /// <summary>Compact live-connection state (drill: connection_status).</summary>
    public sealed class ConnectionBrief
    {
        public bool Connected { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Kind { get; set; }        // "local" | "xmla" | null offline
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string DataSource { get; set; }  // endpoint/instance, null offline
        public static ConnectionBrief From(ConnectionStatus s) =>
            new ConnectionBrief { Connected = s.Connected, Kind = s.Kind, DataSource = s.DataSource };
    }

    /// <summary>The tier gate (drill: get_entitlement). Reason carried only when free — the upsell line.</summary>
    public sealed class EntitlementBrief
    {
        public string Tier { get; set; }   // "free" | "pro"
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Reason { get; set; }
    }

    /// <summary>What is mid-flight this session — each sub-summary omitted when absent (drill:
    /// get_workflow_run / get_plan / get_spec).</summary>
    public sealed class ActiveWorkBrief
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ActiveRunBrief[] Workflows { get; set; }  // active runs; null when none
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? PlanItems { get; set; }             // loaded change-plan size; null when no plan
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string SpecName { get; set; }            // loaded spec name; null when no spec
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Jev.ActiveTasksBrief Tasks { get; set; } // the person's tasks for the assistant (Plan at the heart, lane E3); null when none
    }

    public sealed class ActiveRunBrief
    {
        public string RunId { get; set; }
        public string Workflow { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string CurrentStep { get; set; }   // current step id (null once terminal)
    }

    /// <summary>The tail of the L0 experience log so a fresh/compacted agent has continuity — read-only and
    /// fail-soft (a missing/corrupt log yields null or a Note, NEVER a failed orientation).</summary>
    public sealed class LastSessionBrief
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public LastEntry[] Recent { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Note { get; set; }
    }

    public sealed class LastEntry
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string When { get; set; }    // ISO-8601 UTC
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Origin { get; set; }  // human | agent
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Label { get; set; }
    }

    /// <summary>A DETERMINISTIC navigation hint (§1 result contract; never inferred). op + optional arg hint +
    /// one-line reason.</summary>
    public sealed class NextAction
    {
        public string Op { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Args { get; set; }
        public string Reason { get; set; }
    }
}
