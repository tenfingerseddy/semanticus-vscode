using System;
using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Semanticus.Engine.StartPlan;

namespace Semanticus.Engine
{
    public static partial class McpTools
    {
        [McpServerTool(Name = "get_start_words"), Description("Read words still to write for the start plan, a page at a time. Each item has its ref, kind and context. Write them with write_start_words. Total counts the whole list. Continue at offset + items.length until that reaches total. Pages stop near 7,000 characters (40,000 when max is above 40); one larger item is returned whole. detail=true keeps the existing full page, up to 40,000 characters. Reads only.")]
        public static async Task<StartWordsPage> GetStartWordsOp(IEngine engine, int max = 40, int offset = 0, bool detail = false)
        {
            var page = await GetStartWords(engine, max, offset).ConfigureAwait(false);
            if (detail) return page;
            while (page.Items.Count > 1 && McpLeanResults.Chars(page) > McpLeanResults.StartBudget(max))
                page.Items.RemoveAt(page.Items.Count - 1);
            return page;
        }

        [McpServerTool(Name = "apply_safe_fixes"), Description("Apply deterministic fixes as one undoable batch. system='air' applies AI-readiness fixes and returns counts and scores. It writes no descriptions, names or synonyms. Read its saved details with make_model_ai_ready(item: 'applied' | 'findings', offset: 0, max: 40); these page reads apply no fixes. detail=true returns the full result. system='bpa' runs Best Practice Analyzer fixes until no pass changes anything and keeps its full result. Read remaining and unfixed before reporting it clean; unfixed includes accepted changes left alone and fixes that need the person.")]
        public static async Task<object> ApplySafeFixesOp(IEngine engine, string system, bool detail = false)
        {
            var full = await ApplySafeFixes(engine, system).ConfigureAwait(false);
            if (detail || full is not SafeFixResult fixes) return full;
            return await McpLeanResults.Ready(engine, 0, false, null, 0, 40,
                new ReadinessPlan { Revision = fixes.Revision, Applied = fixes.Applied, Scorecard = fixes.Scorecard }).ConfigureAwait(false);
        }

        [McpServerTool(Name = "start_model_from_sql"), Description("Start a new saved model and make its start plan from a Fabric SQL source. The folder must be empty or absent. Save or discard the open model's unsaved work first. Reads tables, columns and keys with Entra sign-in; sample facts need the person's permission and contain no rows. Undeclared links become guesses to keep or drop. Builds nothing until the plan is approved and build_start_plan runs. Returns counts, decisions, digest and next step. Review with get_start_plan(item, offset, max), edit with edit_start_plan, and write words with get_start_words and write_start_words. detail=true returns the full view. For an open model use autogenerate_spec_from_model.")]
        public static async Task<StartPlanView> StartModelFromSqlOp(IEngine engine,
            string server, string database, string folder, string name,
            [Description("import or directLake")] string storageMode = "import",
            [Description("azcli, serviceprincipal, interactive or devicecode. Leave out to use the sign-in that is ready")] string authMode = null, string tenantId = null,
            [Description("true returns the full view; false returns counts, decisions and the next step")] bool detail = false)
            => McpLeanResults.Start(engine, await StartModelFromSql(engine, server, database, folder, name, storageMode, authMode, tenantId).ConfigureAwait(false), detail);

        [McpServerTool(Name = "get_start_plan"), Description("Read a compact summary of the same start plan Studio shows: section counts, decisions needing the person, deciding state, grades, digest, approval and build status. exists=false means no plan. Page the full lines with item='tables' | 'links' | 'measures' | 'words' | 'bestpractice'; use columns, needs_you, edges or knock_ons for those lists. Set offset=0 and max=40, then continue at summary.page.nextOffset. Each page keeps the digest. detail=true returns the full Studio view. Reads only.")]
        public static async Task<StartPlanView> GetStartPlanOp(IEngine engine,
            [Description("Section id: tables, links, measures, words, bestpractice; or columns, needs_you, edges, knock_ons. Omit for a summary.")] string item = null,
            [Description("Start at 0, then use summary.page.nextOffset. The digest identifies the plan being read.")] int offset = 0,
            [Description("Most entries on a page, 1 to 100. Pages also stop near 7,000 characters, or 40,000 when max is above 40.")] int max = 40,
            [Description("true returns the full Studio view")] bool detail = false)
            => McpLeanResults.Start(engine, await GetStartPlan(engine).ConfigureAwait(false), detail, item, offset, max);

        [McpServerTool(Name = "edit_start_plan"), Description("Edit one start-plan choice without changing the model. The op selects inclusion, naming, a link, a pick, a pin, keeping or dropping a line, an answer, fiscal year end or a table's sample. target is the ref, name, question id or item.id the plan gave. Dropping or keeping a line updates its dependencies; knock_ons pages describe the last edit's effects. The person's pins and answers survive later deciding. Edits clear approval, so review the new digest before building. Returns a compact summary. Read get_start_plan(item, offset, max) for lists; detail=true returns the full view. On Pro, use edit_start_plans to answer many choices in one call.")]
        public static async Task<StartPlanView> EditStartPlanOp(IEngine engine,
            [Description("include_table | include_column | rename | set_link | set_pick | pin | drop_line | keep_line | answer | set_fiscal_year_end | set_table_sample")] string op,
            string target, string value = null, bool detail = false)
            => McpLeanResults.Start(engine, await EditStartPlan(engine, op, target, value).ConfigureAwait(false), detail);

        [McpServerTool(Name = "grant_start_disclosure"), Description("Allow Jev to see this source, its included tables and allowed sample facts, never rows. Ask the person in chat first, naming what would be shared; call this after they say yes. Their yes in chat is enough: the call grants it, with no question in Studio, and turns Jev on for this model if it is not on yet, so one yes is all the build needs. Pass the current plan digest; changes to the source, tables or sample settings invalidate permission. A free session does not use Jev. Builds nothing and approves no plan (approve_start_plan does that). Returns a compact summary. Read get_start_plan pages for lists; detail=true returns the full view.")]
        public static async Task<StartPlanView> GrantStartDisclosureOp(IEngine engine, string digest, bool detail = false)
            => McpLeanResults.Start(engine, await GrantStartDisclosure(engine, digest).ConfigureAwait(false), detail);

        [McpServerTool(Name = "write_start_words"), Description("Write descriptions, synonyms or business names on the start plan's hidden draft. Each word has ref, kind and text from get_start_words. kind=keep_name accepts the existing name and needs no text. Each refused word keeps its reason; the rest still land. Clashing names, formula text and long text are refused. Words for an excluded object are held. Writes no model content; edits clear plan approval. Returns written count, refusals and a compact plan summary. Read get_start_plan pages for full lines; detail=true returns the full view.")]
        public static async Task<StartWordsResult> WriteStartWordsOp(IEngine engine, StartWord[] words, bool detail = false)
        {
            var result = await WriteStartWords(engine, words).ConfigureAwait(false);
            return new StartWordsResult { Written = result.Written, Refused = result.Refused,
                View = McpLeanResults.Start(engine, result.View, detail) };
        }

        [McpServerTool(Name = "approve_start_plan"), Description("Approve the start plan once the person has said yes in chat, or pressed Approve in Studio. Pass the current plan digest. The approval is bound to exactly that plan; any edit changes the digest and voids it. Builds nothing; call build_start_plan with the same digest next. Returns a compact summary; detail=true returns the full view.")]
        public static async Task<StartPlanView> ApproveStartPlanOp(IEngine engine, string digest, bool detail = false)
            => McpLeanResults.Start(engine, await ApproveStartPlan(engine, digest).ConfigureAwait(false), detail);

        [McpServerTool(Name = "build_start_plan"), Description("Build the approved start plan as one step that one undo reverses. Pass its current digest. A missing approval, changed plan, stale digest or changed empty model refuses the build without writing. Returns built, refusal reason, revision and a compact plan summary. Read get_start_plan(item, offset, max) for full lists; detail=true returns the full view. After building, get_model_plan shows the remaining work.")]
        public static async Task<StartPlanBuildResult> BuildStartPlanOp(IEngine engine, string digest, bool detail = false)
        {
            var result = await BuildStartPlan(engine, digest).ConfigureAwait(false);
            return new StartPlanBuildResult { Built = result.Built, Refused = result.Refused, Revision = result.Revision,
                View = McpLeanResults.Start(engine, result.View, detail) };
        }

        [McpServerTool(Name = "discard_start_plan"), Description("Discard the start draft, decisions, words and approval. Leaves the saved model and its folder in place. Does not undo an existing build; use undo_change for that. Returns a compact summary; detail=true returns the full view. Use start_model_from_sql to start again.")]
        public static async Task<StartPlanView> DiscardStartPlanOp(IEngine engine, bool detail = false)
            => McpLeanResults.Start(engine, await DiscardStartPlan(engine).ConfigureAwait(false), detail);

        [McpServerTool(Name = "make_model_ai_ready"), Description("Apply deterministic AI-readiness fixes as one undoable batch. Returns revision, score and counts first. It writes no descriptions, names or synonyms. The queue contains up to maxQueue content findings with context, excluding accepted or declined changes. Author only queued changes. On Free use set_description, rename_object or set_synonyms, or stage them with propose_plan. Read the saved result with item='queue' | 'applied' | 'findings' | 'rule_errors' | 'unevaluated_rules' | 'orphaned_waivers', offset=0, max=40; continue at summary.page.nextOffset. Page reads apply no fixes. item='full' reads the full saved result. detail=true on the first call returns the full result. On Pro, write a whole page with write_words for descriptions, synonyms and folders, then edit_objects for names and static formats. Read every get_words_queue page before writing, or read again from offset 0 after a write; writes change the queue.")]
        public static Task<ReadinessPlan> MakeModelAiReadyOp(IEngine engine,
            [Description("Maximum AI-content items in the saved work queue. Default 40; increase to queue more.")] int maxQueue = 40,
            [Description("queue, applied, findings, rule_errors, unevaluated_rules, orphaned_waivers, or full. Reads the saved result; applies no fixes. Omit to apply fixes and return a summary.")] string item = null,
            [Description("Start at 0, then use summary.page.nextOffset.")] int offset = 0,
            [Description("Most entries on a page, 1 to 100. Pages also stop near 7,000 characters.")] int max = 40,
            [Description("true returns the full result. With item it reads the saved result.")] bool detail = false)
            => McpLeanResults.Ready(engine, maxQueue, detail, item, offset, max);
    }
}
