// The agent door's half of the composer's drafts (lane L4; composer contract section 6, Plan-heart contract 14).
//
// Four ops, the contract's names. EngineRpcTarget.cs exposes the same four as composeTask, getDraft, editDraft and
// runDraft, and all of them land on the same IEngine methods, so a draft read from either door is the same view.
// The held approval ("Approve and run" on a draft with a row filter, or one that publishes) is the person's alone:
// approveDraft on HumanGovernanceRpcTarget, and no MCP op. Workflows is a Semanticus Pro feature.

using System;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Semanticus.Engine.Composer;

namespace Semanticus.Engine
{
    public static partial class McpTools
    {
        // What the words do, per door, from the committed switches (JevDraftBuilder.JevDraftsOnPlan and
        // JevDraftsOnWorkflows): an assistant reads these before it calls, so they say what the build really does (D1, D2).
        private const string ComposeWordsLine = JevDraftBuilder.JevDraftsOnPlan
            ? "'words' alone drafts the steps: on Semanticus Pro with permission given for this model, Jev is asked once for each step the words name and chooses it, and what it works on where it is sure; a step Jev is not sure of takes the step the words name, one it only leaned to arrives dropped, and 'menu' lists the rest to add. A step that adds something is ticked only when the words ask for a NEW thing of a kind they name (\"add a role for managers\") with nothing after it that could change it, whoever chose the step, Jev included; any other add the words hold a verb for arrives dropped, with a note that quotes the words and says why, and words with no adding verb draft no add. The returned steps ARE the draft: read them before you add any with edit_draft add, or a step is there twice. With no suggestion service or no permission, the words open the draft as a menu instead: the steps they name are listed first in 'menu', for you or the person to pick with edit_draft add."
            : "'words' alone opens the draft as a menu: no step is chosen, the steps the words name are listed first in 'menu', and you or the person pick with edit_draft add; Jev does not choose steps yet.";
        private const string ComposeWordsParameter = JevDraftBuilder.JevDraftsOnPlan
            ? "The person's words. Alone, they are drafted into steps (by Jev, on Pro with permission), or open the menu of steps to pick from when Jev cannot be asked"
            : "The person's words. Alone, they open the draft as the menu of steps to pick from";
        private const string RefineLine = JevDraftBuilder.JevDraftsOnPlan
            ? "'refine' a words draft with more words (text): the new words are drafted into steps as compose_task's words are, each submission its own step or steps, and added to the steps already there, which stay as they are; with no suggestion service or no permission it lists the menu again"
            : "'refine' a words draft with more words (text), which lists the menu again and keeps the steps already picked";
        private const string DescribeChooser = JevDraftBuilder.JevDraftsOnWorkflows
            ? "Each step gets the person's words as its title and an op: on Semanticus Pro with permission given for this model, Jev is asked once for each step and chooses its op, and what it works on where it is sure; where Jev is not sure or cannot be asked, Semanticus's word match chooses the op from the composer's step list (plus refresh_partition, run_tests, deploy_stage and save_model after the change, and benchmark_dax and profile_dax); a step neither can choose has no op, a named placeholder to fill in. To ask Jev it reads the model's table, column, measure and partition names and sends them with the words, never a formula or a value."
            : "Each step gets the person's words as its title and the op Semanticus knows for it (the composer's step list, plus refresh_partition, run_tests, deploy_stage and save_model after the change), or no op when the words name none: a named placeholder to fill in. It reads nothing in the model.";

        private static string JsonArrayTextOrNull(JsonElement value, string name)
        {
            if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind == JsonValueKind.Array) return value.GetRawText();
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            throw new ArgumentException($"{name} must be a JSON list. Check the action's description, then call again.");
        }

        [McpServerTool(Name = "compose_task"), Description("Draft a change as steps Semanticus runs, before anything changes. Pass 'steps', an ordered list where each step is {\"op\": \"create_measure\", \"args\": {...}} or {\"title\": \"...\", \"actions\": [{\"op\", \"args\"}, ...], \"blanks\": [...], \"checks\": [...], \"brief\": \"...\"}; ops are the change ops (create_import_table, create_column, create_relationship, create_measure, starter_measure, set_description, set_measure_format, set_column_data_type, set_table_permission and the rest of the composer list) reads that run first (list_columns, get_source_schema, impact_assessment) and, after them, the steps that leave the model (refresh_partition, save_model, run_tests, deploy_stage). A step may instead be {\"performer\": \"person\", \"title\": \"...\"}, a stop where the run waits for the person to press Continue, or {\"performer\": \"assistant\", \"title\": \"...\", \"brief\": \"...\", \"checks\": [...]}, a step you do when the run reaches it (its changes apply as one undo step). Or pass 'workflow', a saved format-3 workflow whose steps Semanticus runs, with 'inputs'; or 'document', a format-3 workflow's full text that is not saved (Run once), with 'inputs'. A piece of text the op needs that you leave out (a formula, a description) becomes a blank; write it with a value like \"{{blanks.formula}}\" or leave the argument out. The blanks go on your queue at once: list_assistant_requests, claim_request, then complete_request with the text, which Semanticus checks against the draft. Nothing changes in the model until run_draft is called and every part has passed. A draft with no step for the person or the assistant runs as ONE undo step; a draft with such a step runs step by step and makes one undo step per run of Semanticus steps, plus one per assistant step, and its summary says how many. Returns the draft view: the state, the summary, each step, each part. " + ComposeWordsLine + " Workflows is a Semanticus Pro feature.")]
        public static Task<DraftView> ComposeTask(IEngine engine,
            [Description("The steps, as a JSON list (see the description)")] JsonElement? steps = null,
            [Description("A saved format-3 workflow's name, instead of steps")] string workflow = null,
            [Description("For a workflow: its inputs, as a JSON object of {name: value}")] JsonElement? inputs = null,
            [Description("The draft's title, shown on the Plan and used as the undo label")] string title = null,
            [Description(ComposeWordsParameter)] string words = null,
            [Description("Run once: a format-3 workflow's full text, not saved, instead of 'workflow'")] string document = null)
            => engine.ComposeTaskAsync(words, workflow, JsonObjectTextOrNull(inputs ?? default, "inputs"),
                JsonArrayTextOrNull(steps ?? default, "steps"), title, "agent", document);

        [McpServerTool(Name = "describe_workflow_steps"), Description("Cut a description of a workflow into steps, the way the Workflows page does before it opens Author. " + DescribeChooser + " A leading 'every month' or 'each Monday' is returned as 'when', not as a step; words that name more than 12 steps have the first 12 read, and each step past them comes back as a step to fill in, marked 'notRead', its words listed in 'notRead' and named in the sentence; a clause is never dropped. Words longer than 100,000 characters are refused, as compose_task's are. Every op a step names (its 'ops') is its own step in 'document', with its own questions and checks. A step that adds (an op that creates) is chosen, by Jev or by the words, only when the adding rule keeps its add: the step's words ask for a NEW thing of a named kind ('add a role for managers', 'we need a new measure for margin'), do not put it into a set of things that exist ('to them', 'to that list') and leave nothing after them that is unread or takes it back. Words that take something away, cancel or decline, and words that only mention an add, never make one: the step keeps another op its words name, or stays a placeholder with the reason. Jev never lifts an add the rule does not keep, and an add is the kind the words name. A step named Add that makes nothing new (Add synonyms) is chosen only when the words hold an adding verb ('add synonyms for Total Sales') and take nothing away, by Jev or by the words. It changes nothing, stores nothing, and puts nothing on the Plan; only running a workflow makes Plan rows. To keep the steps, write them into a workflow with save_workflow. Workflows is a Semanticus Pro feature.")]
        public static Task<DescribedWorkflow> DescribeWorkflowSteps(IEngine engine,
            [Description("The workflow described in words, for example 'Every month, refresh Sales, run the saved checks and publish the model'")] string words)
            => engine.DescribeWorkflowStepsAsync(words);

        [McpServerTool(Name = "get_draft"), Description("Read a draft: its state (needs_you, waiting_for_assistant, ready, running, done, stopped), the Plan's word for its row, the summary sentence, the questions, each part and who wrote it, each step with what it changes and its check, the steps after the change, and, once it ran, the record and any banner (stopped, or one check failed after the change). The draft id is 'dr-...'; on get_model_plan a draft is the row 'draft/dr-...'. Reads only. Workflows is a Semanticus Pro feature.")]
        public static Task<DraftView> GetDraft(IEngine engine,
            [Description("The draft id, 'dr-...' (or the Plan row id 'draft/dr-...')")] string draftId)
            => engine.GetDraftAsync(draftId);

        [McpServerTool(Name = "edit_draft"), Description("Change a draft before it runs. action: 'keep' or 'drop' a step (dropping one drops the steps that need it, and says so); 'answer' a question (question, text), which empties and asks again for any part written from the old answer; 'write' a part yourself (step, blank, text), checked at once, when the person types it in chat; 'pick' one of the choices an assistant offered with a decline (step, blank, text = the ref); 'ask_again' for a part that needs the person; 'check_again' after the model changed under a written part; 'add' a step from a words draft's menu (text = the menu id, like add-measure); " + RefineLine + "; 'try_again' the steps after the change that did not run, and the checks after it that could not check (the change itself is not made again); 'keep_anyway' after a check failed after the change, which keeps the change as it is; 'keep_theirs' when a run stopped because a value the draft writes was changed after the draft was made (the draft's 'overwrite' says which): that step is dropped and the person's value stays (only the person can choose 'apply_draft', which writes the draft over it); 'reorder' a step (step, text = its new position), refused where a step would come before a step it needs; 'open_in_author' returns the draft written as a format-3 workflow ('document': a free name and the file's text), which save_workflow keeps; nothing is saved by it. A step done by your assistant pauses the run and puts one request on your queue: answer it with complete_request, the text a JSON list of {op, args} (the change ops), which apply as one undo step. A step with a gate runs as a guided step: submit it with submit_workflow_step (the runId is on the step and in the request), and a gate that fails stops the draft. A step that stops for the person waits for them to press Continue in Semanticus; no assistant can pass it. Nothing changes in the model except by running. When the draft is approved and every part has passed, the change runs by itself. Workflows is a Semanticus Pro feature.")]
        public static Task<DraftView> EditDraft(IEngine engine,
            [Description("The draft id")] string draftId,
            [Description("keep | drop | answer | write | pick | ask_again | check_again | add | refine | reorder | open_in_author | try_again | keep_anyway | keep_theirs | apply_draft")] string action,
            [Description("The step, by id or number (keep, drop, write, pick, ask_again, reorder)")] string step = null,
            [Description("The part's blank name (write, pick, ask_again)")] string blank = null,
            [Description("The question's name (answer)")] string question = null,
            [Description("The answer, the text, the picked ref, the menu id (add), the more words (refine) or the new position (reorder)")] string text = null)
            => engine.EditDraftAsync(draftId, action, step, blank, question, text, "agent");

        [McpServerTool(Name = "run_draft"), Description("Approve a draft and run it. It runs now when every question is answered and every part has passed; otherwise the approval is kept, any part not yet sent goes on your queue, and the change runs by itself when the last part passes. A draft with no step for the person or the assistant runs as ONE undo step (undo_change takes all of it back); a draft with such a step runs step by step and makes one undo step per run of Semanticus steps, plus one per assistant step, and its summary says how many; the steps after the change (refresh, save, publish) run after it and Undo cannot take those back. Refused for a draft that sets who can see which data or that publishes after the change: the person must press Approve and run in Semanticus. Workflows is a Semanticus Pro feature.")]
        public static Task<DraftView> RunDraft(IEngine engine,
            [Description("The draft id")] string draftId)
            => engine.RunDraftAsync(draftId, "agent");
    }
}
