// The agent door's half of the assistant handoff (Plan at the heart, lane E3; contract sections 3.3 and 3.4). It
// replaces composer lane L2.
//
// Four operations, the ratified names (docs/redesign/contracts/assistant-handoff.md). EngineRpcTarget.cs exposes
// the same four as listAssistantRequests, claimRequest, completeRequest and declineRequest, and all of them land
// on the same IEngine methods. The person's own claim, Mark done and Could not are the UI door's equivalents, on
// HumanGovernanceRpcTarget, over the same queue.
//
// The queue is a view over the Plan's tasks: a request is a task the person gave to their assistant, and its id is
// the task's id. Free on every tier: asking for a task runs no Jev.

using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Semanticus.Engine.Jev;

namespace Semanticus.Engine
{
    public static partial class McpTools
    {
        [McpServerTool(Name = "list_assistant_requests"), Description("The tasks the person has given you on the Plan that are ready now, in their order, plus the ones you already hold and the ones not offered yet with the row each waits for. Each request carries its id (the task id, 'pt-...'), the person's words, their note, the brief a Send carried, the objects, how done is known, and 'independent': true when it changes nothing another offered task touches, so you can take it beside them; a task Semanticus cannot read says so and is never called independent. 'switch' is 'paused' when the person has paused tasks for you: then nothing is offered, and you carry on with what they ask you directly. Reads only. Take one with claim_request.")]
        public static Task<AssistantRequestList> ListAssistantRequests(IEngine engine)
            => engine.ListAssistantRequestsAsync();

        [McpServerTool(Name = "claim_request"), Description("Take one task the person gave you. Refused, with a sentence, while the person has paused tasks for you, while the task is paused, while it waits for another row on the Plan (the sentence names it), or when someone else holds it; the first claim wins. On success the result carries a 'claimToken' (shown once, keep it for complete_request or decline_request) and the task's packet: the words, the note, the brief, the objects, how done is known, and the grounding for each object. While you hold a claim, your edits count toward the task you claimed most recently; claiming a task you already hold switches back to it. Changes nothing in the model. A restart of Semanticus releases every claim, and the task is offered again.")]
        public static Task<HandoffResult> ClaimRequest(IEngine engine,
            [Description("The task id from list_assistant_requests, e.g. 'pt-3f9a01c2'")] string requestId)
            => engine.ClaimAssistantRequestAsync(requestId);

        [McpServerTool(Name = "complete_request"), Description("Say a task you hold is done, with an optional reply the person sees on the Plan. A task Semanticus can check (words on named objects, a finding that should be gone) then reads 'Applied, checking' until the model proves it; a task only you can answer reads Done. Needs the claimToken claim_request gave you; an old or wrong token is refused and nothing changes. Completing the task clears it as your working-on task. For an ordinary task, make the edits first, then complete: completing changes nothing in the model. For a part of a draft (its brief says what to write), 'text' IS the part: Semanticus checks it against the draft in the same call, and a failure keeps your claim, names each failed check and counts a try (the third hands the part to the person). The model changes only when the last part of an approved draft passes: then the draft's change runs as one undo step. For a STEP of a draft that you do (its brief says so), 'text' is the step's changes: a JSON list of {\"op\", \"args\"} from the change ops (create_measure, set_description and the rest), checked and applied as one undo step; send [] when the step needs no change.")]
        public static Task<HandoffResult> CompleteRequest(IEngine engine,
            [Description("The task id")] string requestId,
            [Description("The claimToken from claim_request")] string claimToken,
            [Description("Optional. Your reply, in plain words, shown to the person on the Plan. Up to 4000 characters.")] string text = null)
            => engine.CompleteAssistantRequestAsync(requestId, claimToken, text);

        [McpServerTool(Name = "decline_request"), Description("Hand a task you hold back to the person because you could not do it. The reason is required and is shown to them word for word; the task then reads 'Needs you' and they can give it to you again or set it aside. Needs the claimToken claim_request gave you. Clears it as your working-on task. Changes nothing in the model. For a part of a draft you may add 'choices': the person sees your reason and one button per choice that names a real object.")]
        public static Task<HandoffResult> DeclineRequest(IEngine engine,
            [Description("The task id")] string requestId,
            [Description("The claimToken from claim_request")] string claimToken,
            [Description("Why it could not be done, in one line.")] string reason,
            [Description("Optional, for a part of a draft: 2 to 6 object refs the person can pick from, e.g. ['column:Returns/Quantity', 'column:Returns/RefundAmount']. Only refs that name a real object, or one the draft creates, are kept.")] string[] choices = null)
            => engine.DeclineAssistantRequestAsync(requestId, claimToken, reason, null, choices);
    }
}
