using System;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// Governance mutations exist only on an authenticated human connection. Agent connections never register this
    /// target, so they cannot discover or invoke these methods even if they forge a request origin.
    ///
    /// Kane, 10 Oct 2026: the person's word in chat is enough. Every Plan control (add a task or an aim, mark done, Pause and
    /// Resume, the doer switch, Up, the queue switch, a note, remove and restore, repeats, approving and continuing a draft,
    /// approving a start plan) moved to <see cref="EngineRpcTarget"/>, where an attached assistant reaches it through the gate.
    /// What stays HERE is the Permissions page (the agent policy, and approving or denying a held action), the person's own
    /// claim, Mark done and Could not on a task, and the window's own business (licence, parent lifetime, clean stop).
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

        // ---- the Permissions page: the one thing the assistant never changes (Kane, 10 Oct 2026) ---------------------------

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

        // ---- the person's side of the queue (lane E3, contract 3.3, Sol 24 Sep) --------------------------------
        // Not person-only as a CAPABILITY: the assistant claims, answers and declines with the four queue ops on
        // EngineRpcTarget and the MCP door, and marks a row done with model_plan_action. These three are the person's own
        // hand on the same PlanTaskQueue, so they live here, where an agent connection cannot claim AS the person.

        /// <summary>I'll take it: the person claims the task, the doer becomes You, and it leaves the assistant's offer.</summary>
        public Task<ModelPlanView> claimPlanTask(string id) => _engine.ClaimPlanTaskAsync(id);

        /// <summary>Mark done, with an optional reply shown as the person's.</summary>
        public Task<ModelPlanView> completePlanTask(string id, string reply = null) => _engine.CompletePlanTaskAsync(id, reply);

        /// <summary>Could not, with a reason: the row reads Needs you and offers Give to your assistant and Set aside.</summary>
        public Task<ModelPlanView> declinePlanTask(string id, string reason) => _engine.DeclinePlanTaskAsync(id, reason);

        // ---- the Studio's driver report (1.4, contract section 1). The Studio tells the engine who drives its screen, so show_in_studio can
        // answer "moved" or "noted". Person-only: an assistant must not be able to report that the person gave up the wheel. It reads the
        // answer in show_in_studio's result.

        /// <summary>The Studio reports its mode (claudeDrives or iDrive) and state (idle, claudeDriving or personHasWheel).</summary>
        public Task setStudioDriver(string mode, string state) => _engine.SetStudioDriverAsync(mode, state);

        /// <summary>A query the person ran as written in the Query page is a row of the notebook. Person-only: the assistant's own asks are kept by ask_data, and the rest of its work by query_notebook.</summary>
        public Task<string> recordQueryRun(string dax, int rowCount, double elapsedMs, string headline = null, string replaces = null) => _engine.RecordQueryRunAsync(dax, rowCount, elapsedMs, headline, "human", replaces);

        /// <summary>The page notes what it knows of a notebook row: its number as shown, the line for a part that could not apply, the times from Time all. Person-only.</summary>
        public Task<bool> noteQueryRow(string id, QueryRowNote note) => _engine.NoteQueryRowAsync(id, note);

        /// <summary>The window reports the person's personality, sounds and volume settings. Person-only: an assistant sets them with set_personality_settings, which goes through the gate.</summary>
        public Task reportHostSettings(string personality, bool sounds, int soundVolume) => _engine.ReportHostSettingsAsync(personality, sounds, soundVolume);
    }
}
