// The agent door's half of the spec-first start (lane sf-doors; design-spec-first-start-v2.md).
//
// Eight operations. EngineRpcTarget.cs exposes the same eight as startModelFromSql, getStartPlan, editStartPlan,
// grantStartDisclosure, getStartWords, writeStartWords, buildStartPlan and discardStartPlan, and every one lands on the
// same IEngine method, so the Plan page and an assistant read one StartPlanView.
//
// THERE IS NO APPROVE HERE, ON PURPOSE. The person approves the exact plan, bound to its digest, from Studio
// (approveStartPlan on HumanGovernanceRpcTarget, which an agent connection never registers). build_start_plan runs the
// build of that approved plan, and is refused until they have. Any edit changes the digest and voids the approval.
//
// Every operation is free. Jev deciding for the person is the paid part, and it is drawn inside the engine, so a free
// session still gets the plan, its own assistant's words and one build. The copy follows docs/product-copy-style.md:
// no em dashes, plain words, and each description says what the operation will NOT do.

using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Semanticus.Engine.StartPlan;

namespace Semanticus.Engine
{
    public static partial class McpTools
    {
        [McpServerTool(Name = "start_model_from_sql"), Description("Start a NEW model from a Fabric SQL source, plan first. Creates an empty model called 'name' in 'folder' and saves it there, reads the tables, columns and keys over your Entra sign-in (and, only when the person has allowed it, counts and patterns from a sample, never the rows), and makes ONE start plan: tables and columns, links and dates, measures, words for Copilot and best practice. Where the source declares no key for a pair of tables, the plan guesses the link from matching key names, shows each guess as a line to keep or drop, and builds only the ones kept. It builds nothing: the model stays empty until the person approves the plan and build_start_plan runs. The folder must be empty or not exist yet; a folder that holds anything is refused and nothing is written. It is also refused while the open model has unsaved work, so ask the person to save or discard it first. Next: get_start_plan to read the plan, edit_start_plan to change it, get_start_words and write_start_words for the words. Fabric SQL only. For a model that is already open, use autogenerate_spec_from_model instead. authMode: azcli (default) | serviceprincipal | interactive | devicecode.")]
        public static Task<StartPlanView> StartModelFromSql(IEngine engine,
            [Description("Fabric SQL endpoint, e.g. 'xxxxx.datawarehouse.fabric.microsoft.com'")] string server,
            [Description("Warehouse or Lakehouse database name")] string database,
            [Description("The folder the new model is made in. It must be empty or not exist yet.")] string folder,
            [Description("The model's name. It is saved at <folder>/<name>.SemanticModel")] string name,
            [Description("import (default) | directLake")] string storageMode = "import",
            [Description("azcli (default) | serviceprincipal | interactive | devicecode")] string authMode = "azcli",
            [Description("Entra tenant id or domain that owns the Fabric workspace; leave blank to use your az CLI default")] string tenantId = null)
            => engine.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = server, Database = database, Folder = folder, Name = name,
                StorageMode = string.IsNullOrWhiteSpace(storageMode) ? "import" : storageMode,
                AuthMode = string.IsNullOrWhiteSpace(authMode) ? "azcli" : authMode,
                TenantId = tenantId,
            }, "agent");

        [McpServerTool(Name = "get_start_plan"), Description("Read the start plan for the new model being made from a SQL source: five sections (Tables and columns, Links and dates, Measures, Words for Copilot, Best practice), each with its lines and who decided each one (a rule, Jev, an assistant or the person); what needs the person; how far the deciding has got (off, waiting_grant, deciding, stable or incomplete, and why); the AI-readiness and best-practice grades now and after the build; the digest; whether the person has approved exactly this plan; whether it can be built; and, when it cannot, why. It is the same view the Plan page shows. 'exists' false means there is no start plan. Reads only: it changes nothing and builds nothing.")]
        public static Task<StartPlanView> GetStartPlan(IEngine engine)
            => engine.GetStartPlanAsync();

        [McpServerTool(Name = "edit_start_plan"), Description("Change the start plan. Nothing is built and the model is not touched. One edit per call: 'op' is include_table | include_column | rename | set_link | set_pick | pin | drop_line | keep_line | answer | set_fiscal_year_end | set_table_sample, 'target' is the table, column, link, line or question it is about (as get_start_plan names it), and 'value' is the new value where the op takes one. set_table_sample off refuses the sample of ONE table's rows (Jev still sees that table's names and settings, never facts worked out from its rows); on allows it again, which asks the person again if they had agreed. A line is named by the id get_start_plan gives it (item.id) or its key. What depends on what is followed for you: dropping a measure drops the lines that need it, and keeping a line keeps the measure it needs. The answer shows the knock-on lines. A link the plan guessed from names (the source declared no key for those two tables) is a line you can drop_line, which leaves it out of the build and works out the fact tables, measures and date table again, and keep_line brings back. Every edit changes the digest and so voids any approval the person gave: they approve again in Studio. A person's own pin or answer is never overwritten by a later round of deciding. Returns the new plan.")]
        public static Task<StartPlanView> EditStartPlan(IEngine engine,
            [Description("include_table | include_column | rename | set_link | set_pick | pin | drop_line | keep_line | answer | set_fiscal_year_end | set_table_sample")] string op,
            [Description("What it is about, as get_start_plan names it (a table, a column, a link, a line or a question id)")] string target,
            [Description("The new value, where the op takes one (a name, true or false, a pick such as sum or average, an answer, a fiscal year end such as 6/30, or on / off for set_table_sample)")] string value = null)
            => engine.EditStartPlanAsync(new StartPlanEdit { Op = op, Target = target, Value = value }, "agent");

        [McpServerTool(Name = "grant_start_disclosure"), Description("Give permission for what Jev may see while it decides this start plan: the source (server and database), the tables listed in the plan, and, when it is switched on, counts and patterns from a sample, never rows. Pass the digest get_start_plan showed. The person always decides this, in every preset: your call puts their question (the source, the tables and the sample setting) in the Waiting for you list and answers with asked true and nothing granted. When they say yes, call again with the same digest. The digest covers the source, the tables and whether facts from a sample of rows are on (it is in the plan's sampleFacts field), so the permission is for exactly what you read, and a plan that changed since, including a sample read after you looked, is refused as stale. It covers that source and those tables only: including another table, or switching the sample on, asks again. It builds nothing and approves nothing. Approving the plan is the person's own act in Studio and has no tool here. A free session is never asked, because Jev is not used there.")]
        public static Task<StartPlanView> GrantStartDisclosure(IEngine engine,
            [Description("The digest from get_start_plan")] string digest)
            => engine.GrantStartDisclosureAsync(digest, "agent");

        [McpServerTool(Name = "get_start_words"), Description("The words still to write for the new model's start plan, a page at a time: each table, column and measure that has no description, business name or synonyms yet, with what you need to write them well. Written on the hidden draft the plan is built on, so a column the person renamed or left out is already reflected. Write them with write_start_words. Reads only.")]
        public static Task<StartWordsPage> GetStartWords(IEngine engine,
            [Description("Most items on this page (default 40, at most 100). A page also stops at 40,000 characters, so it can hold fewer.")] int max = StartPlanDoors.WordsPageDefault,
            [Description("Where the page starts in the whole list: 0, then the next offset of the page before")] int offset = 0)
            => engine.GetStartWordsAsync(StartPlanDoors.WordsPageSize(max), StartPlanDoors.WordsOffset(offset));

        [McpServerTool(Name = "write_start_words"), Description("Write words into the start plan: each item names an object ('ref' as get_start_words gave it), what it is ('kind': description | synonyms | display_name | keep_name) and your 'text' (a keep_name needs no text: it says the object's name is right as it is, so the question is closed and the build accepts the naming finding; a business name the naming rules would still flag is refused, and so is the name the object already has: use keep_name for that). Each one becomes a line the person sees in the plan marked as written by an assistant, and they can drop or change it. An item is refused on its own, with its reason, when the name clashes with another, the text holds formula text, or it is too long; the rest still land. Words for an object the person later leaves out are held, not lost. Changes the plan and so its digest, which voids an approval: the person approves again in Studio. Writes nothing to a model.")]
        public static Task<StartWordsResult> WriteStartWords(IEngine engine,
            [Description("The words to write: a list of { ref, kind, text }")] StartWord[] words)
            => engine.WriteStartWordsAsync(words ?? System.Array.Empty<StartWord>(), "agent");

        [McpServerTool(Name = "build_start_plan"), Description("Build the new model from the start plan the person approved: ONE step, so one undo takes the whole build back. Pass the digest get_start_plan showed. It is refused, and writes nothing, when the person has not approved exactly this plan in Studio, when the plan changed after they approved it, when the digest you pass is not the plan's current one, or when the person's empty model changed since the plan began. It does not approve anything itself and no tool here can: ask the person to approve it in Studio first. After the build, the model's own Plan holds whatever is left to do.")]
        public static Task<StartPlanBuildResult> BuildStartPlan(IEngine engine,
            [Description("The digest from get_start_plan, the one the person approved")] string digest)
            => engine.BuildStartPlanAsync(digest, "agent");

        [McpServerTool(Name = "discard_start_plan"), Description("Throw the start plan away: the hidden draft, the decisions, the words and any approval. The empty model that start_model_from_sql made and saved is left as it is, in its folder; this does not delete it. A build already made is not undone by this (use undo_change). Use it when the person wants to start again, then call start_model_from_sql again.")]
        public static Task<StartPlanView> DiscardStartPlan(IEngine engine)
            => engine.DiscardStartPlanAsync("agent");
    }
}
