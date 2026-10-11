using System;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// Governance mutations exist only on an authenticated human connection. Agent connections never register this
    /// target, so they cannot discover or invoke these methods even if they forge a request origin.
    /// </summary>
    internal sealed class HumanGovernanceRpcTarget
    {
        private readonly IEngine _engine;
        // The engine itself, for the one thing the fence's interface does not carry (the licence swap).
        private readonly IEngine _owner;
        private readonly ParentLifetime _parentLifetime;
        private readonly CleanStop _cleanStop;

        internal HumanGovernanceRpcTarget(IEngine engine, ParentLifetime parentLifetime = null, CleanStop cleanStop = null, IEngine served = null)
        {
            _owner = engine;
            _engine = served ?? PersonFence.For(engine);   // the person's plan controls are writes too: a page showing another model cannot make them
            _parentLifetime = parentLifetime;
            _cleanStop = cleanStop;
        }

        /// <summary>A window that is replacing this engine with its own build asks THIS engine, over the connection
        /// it checked it on, to leave, and the engine agrees only while it holds no unsaved work (F-244). The check
        /// and the stop are one step on the dispatcher, so nothing can land in between, and the window never kills a
        /// pid it read from engine.json, which may by then name another window's engine.
        ///
        /// Human-only, like adoptParent, and on no MCP op: which engine serves a VS Code window is that window's
        /// business, and an assistant connection must not be able to stop the engine it is attached to. Only the
        /// owner <c>serve</c> host can stop itself this way; any other host refuses and keeps running.</summary>
        public Task<StopIfCleanResult> stopIfClean() =>
            _cleanStop != null
                ? _cleanStop.TryStopAsync()
                : Task.FromResult(new StopIfCleanResult
                {
                    Stopping = false,
                    Pid = Environment.ProcessId,
                    Reason = "this engine cannot stop itself this way, so it keeps running",
                });

        /// <summary>A returning VS Code window claims this engine as its own. Reloading a window restarts the
        /// extension host, so the engine's parent dies; the engine then holds the session open for its grace,
        /// and this call inside that window switches the watch to the new host and cancels the pending
        /// shutdown. That is what keeps C4.1 true: a reload does not throw unsaved work away.
        ///
        /// It lives HERE, on the human-only target, for the same reason the governance methods do: an agent
        /// connection must not be able to retarget which process the engine dies with. It is a separate
        /// method rather than a new field in the pre-JSON-RPC preamble, because that preamble is a fixed
        /// one-line format an installed older extension still speaks and adding to it would break those.
        ///
        /// Returns false rather than throwing when there is nothing to adopt (an MCP-owned engine has no
        /// parent lifetime), when the pid is not a live watchable process, or when the grace already ran out
        /// and the shutdown is under way.</summary>
        public Task<bool> adoptParent(int pid) =>
            Task.FromResult(_parentLifetime != null && _parentLifetime.Adopt(pid));

        /// <summary>A person activated or removed a licence (F-242). The token is re-verified here, in the engine,
        /// and the entitlement is swapped in place, so the change lands on the engine that is already running
        /// instead of waiting for a restart that an adopted engine never gets. HERE, on the human-only target, and
        /// on no MCP op: activating a licence is a person's action, and an assistant connection cannot see this
        /// method at all. A bad token is refused and changes nothing; verifyOnly checks without swapping.</summary>
        public Task<Entitlement.LicenseSwapResult> applyLicense(string token, bool verifyOnly = false) =>
            _owner is Entitlement.ILicenseHolder holder
                ? Task.FromResult(holder.ApplyLicense(token, verifyOnly))
                : throw new NotSupportedException("This engine cannot change its licence while it runs. Restart it to read the new licence.");

        public Task<AgentPolicy> setAgentPolicyPreset(string preset, string legacyOrigin = null) =>
            _engine.SetAgentPolicyPresetAsync(preset, "human");
        public Task<AgentPolicy> setAgentPolicyCell(string capability, string label, string action, string legacyOrigin = null) =>
            _engine.SetAgentPolicyCellAsync(capability, label, action, "human");
        public Task<AgentPolicy> setAgentPolicyEnabled(bool enabled, string legacyOrigin = null) =>
            _engine.SetAgentPolicyEnabledAsync(enabled, "human");
        public Task<ApprovalRecord> approveAgentAction(string id, string legacyOrigin = null) =>
            _engine.ApproveAgentActionAsync(id, "human");
        public Task<bool> denyAgentAction(string id, string legacyOrigin = null) =>
            _engine.DenyAgentActionAsync(id, "human");

        // ---- the person's Plan controls (Plan at the heart, contract sections 3.3, 4 and 12) ----------------
        // HERE and on no MCP tool, the approveAgentAction precedent: adding a task, Pause and Resume, the doer
        // switch, Up, the queue switch and the note are the person's. An assistant must not be able to invent work
        // for itself or another assistant, or steer these over it; it reads them on get_model_plan. This is a
        // governance exception, not a missing door, and it adds nothing only one door can do to the model itself.

        /// <summary>Add a task: the Plan's field, or a Send button with its page, brief, objects, done-when and link.</summary>
        public Task<ModelPlanActionResult> addPlanTask(PlanTaskRequest request) => _engine.AddPlanTaskAsync(request);

        /// <summary>Pause (held true) or Resume (held false) one row.</summary>
        public Task<ModelPlanView> holdPlanItem(string id, bool held) => _engine.HoldPlanItemAsync(id, held);

        /// <summary>"Give to your assistant", "I'll do it", or "Let it run" where the engine can apply it.</summary>
        public Task<ModelPlanView> setPlanItemDoer(string id, string doer) => _engine.SetPlanItemDoerAsync(id, doer);

        /// <summary>Up, on a task for the assistant: one place earlier in its order.</summary>
        public Task<ModelPlanView> movePlanTaskUp(string id) => _engine.MovePlanTaskUpAsync(id);

        /// <summary>The switch "Tasks for your assistant": "on" or "paused".</summary>
        public Task<ModelPlanView> setAssistantQueue(string state) => _engine.SetAssistantQueueAsync(state);

        /// <summary>One note from the person on a row; empty clears it.</summary>
        public Task<ModelPlanView> setPlanTaskNote(string id, string text) => _engine.SetPlanTaskNoteAsync(id, text);

        // ---- the person's side of the queue (lane E3, contract 3.3, Sol 24 Sep) --------------------------------
        // Not person-only as a CAPABILITY: the assistant claims, answers and declines with the four queue ops on
        // EngineRpcTarget and the MCP door. These three are the person's own hand on the same PlanTaskQueue, so they
        // live here, where an agent connection cannot claim AS the person.

        /// <summary>I'll take it: the person claims the task, the doer becomes You, and it leaves the assistant's offer.</summary>
        public Task<ModelPlanView> claimPlanTask(string id) => _engine.ClaimPlanTaskAsync(id);

        /// <summary>Mark done, with an optional reply shown as the person's.</summary>
        public Task<ModelPlanView> completePlanTask(string id, string reply = null) => _engine.CompletePlanTaskAsync(id, reply);

        /// <summary>Could not, with a reason: the row reads Needs you and offers Give to your assistant and Set aside.</summary>
        public Task<ModelPlanView> declinePlanTask(string id, string reason) => _engine.DeclinePlanTaskAsync(id, reason);

        // ---- Remove, and its Undo (Kane's UAT, 25 Sep: "no way to delete a task once you add it") -------------------
        // Person-only like adding a task: an assistant must not be able to take the person's work off the Plan. A task
        // an assistant holds is removed all the same, and it reads so on its next call.

        /// <summary>Remove a task or a draft row (and the draft's parts) from the Plan. A row Semanticus found is set aside instead.</summary>
        public Task<PlanRemoveResult> removePlanRow(string id) => _engine.RemovePlanRowAsync(id);

        /// <summary>Undo a removal, from the toast: the row goes back where it stood.</summary>
        public Task<ModelPlanView> restorePlanRow(string id) => _engine.RestorePlanRowAsync(id);

        // ---- repeat cards (lane RP, contract 1.7 and 12) -----------------------------------------------------------
        // Person-only for the reason the controls above are: an assistant must not be able to set up work that keeps
        // coming back to it. It reads every repeat and its next due date on get_model_plan.

        /// <summary>Save a repeat: words or a saved workflow, a doer, and a cadence; or one of the stock repeats. Pro.</summary>
        public Task<ModelPlanActionResult> addPlanRepeat(PlanRepeatRequest request) => _engine.AddPlanRepeatAsync(request);

        /// <summary>Change a repeat: its words, doer or cadence, or pause and resume it (Held). Pro.</summary>
        public Task<ModelPlanView> setPlanRepeat(string id, PlanRepeatRequest request) => _engine.SetPlanRepeatAsync(id, request);

        /// <summary>Delete a repeat. Its open card stays as an ordinary row. Free.</summary>
        public Task<ModelPlanView> deletePlanRepeat(string id) => _engine.DeletePlanRepeatAsync(id);

        // ---- the composer's held drafts (lane L4, composer contract 5.4) ------------------------------------------
        // A draft with a row filter, or one that publishes after the change with a part an assistant wrote, never runs
        // by itself. Approve and run is the person's, HERE, like approveAgentAction: run_draft refuses a held draft on
        // every door, and an agent connection cannot see this method at all.

        /// <summary>Approve and run on a held draft.</summary>
        public Task<Composer.DraftView> approveDraft(string draftId) => _engine.ApproveHeldDraftAsync(draftId);

        /// <summary>Continue past a person's stop ("Stop and ask me here"), or past an assistant's step without its
        /// changes. The person's own for the same reason: a stop that asks the person must not be passed by the
        /// assistant it would otherwise be asking about (lane author-gaps, B9).</summary>
        public Task<Composer.DraftView> continueDraft(string draftId) => _engine.ContinueDraftAsync(draftId);

        // ---- the start plan's approval (spec-first start, design S1, lane sf-consent) ---------------------------------------
        // Person-only like approveDraft: the approval is the one thing that lets a start plan be built, so the assistant that wrote the
        // plan must not be able to make it. The assistant may BUILD an approved plan (build_start_plan on both doors); it cannot approve.
        // No MCP tool and no method on EngineRpcTarget carries this name, and StartPlanConsentTests pins both.

        /// <summary>Approve the plan whose digest the person was shown. The approval is void the moment the plan changes at all.</summary>
        // Through the fence's IEngine, which declares the verb itself: `_engine is IStartPlanEngine` was false for the fence's proxy (it implements IEngine only), so after drafts-sol put the
        // fence in front of this target every approval threw "cannot start a model from a SQL source" (join 11, J-A1: StartPlanConsentTests).
        public Task<StartPlan.StartPlanView> approveStartPlan(string digest) => _engine.ApproveStartPlanAsync(digest);
    }
}
