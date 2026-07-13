using System;

namespace Semanticus.Engine
{
    /// <summary>
    /// Fans applied-change notifications out to every connected client (both doors). The RpcHost
    /// subscribes once and broadcasts model/didChange to all attached pipe clients; the McpHost
    /// (Phase B) will fold recent changes into tool results so Claude sees the human's edits.
    /// </summary>
    public sealed class ChangeBus
    {
        public event Action<ChangeNotification> Changed;

        public void Publish(ChangeNotification n) => Changed?.Invoke(n);

        /// <summary>Fired when the session's change plan is proposed/edited/applied/cleared. The RpcServer
        /// re-broadcasts this as <c>plan/didChange</c> so the UI watches the plan assemble in real time.</summary>
        public event Action<ChangePlanView> PlanChanged;

        public void PublishPlan(ChangePlanView v) => PlanChanged?.Invoke(v);

        /// <summary>Fired when the session's model spec is set/cleared/loaded. The RpcServer re-broadcasts this as
        /// <c>spec/didChange</c> so the Spec tab watches the spec assemble live (by the human OR the user's Claude).</summary>
        public event Action<SpecView> SpecChanged;

        public void PublishSpec(SpecView v) => SpecChanged?.Invoke(v);

        /// <summary>Fired when a NON-mutating execute/read op runs (run_dax, profile_dax, …). The RpcServer
        /// re-broadcasts as <c>model/activity</c> so the human's Studio watches what the agent runs, live.</summary>
        public event Action<ActivityEvent> Activity;

        /// <summary>Wired by <see cref="SessionManager"/>: resolves the CURRENT session id so EVERY activity
        /// publish — including the direct <c>Bus.PublishActivity</c> emitters that never pass
        /// <c>LocalEngine.PublishActivityAsync</c> (binding warnings, enforcement/enable/binding/activation
        /// writes) — carries the session identity frozen at emit. The experience tee attributes on it; an
        /// event a call site already stamped wins (??=).</summary>
        public System.Func<string> ActivitySessionId { get; set; }

        public void PublishActivity(ActivityEvent e)
        {
            if (e != null) e.SessionId ??= ActivitySessionId?.Invoke();
            Activity?.Invoke(e);
        }

        /// <summary>Fired on every workflow-run transition (start/submit/skip/abort). The RpcServer
        /// re-broadcasts as <c>workflow/didChange</c> so the Studio UI and the agent door see the same
        /// live run state (golden rule 2).</summary>
        public event Action<WorkflowRunView> WorkflowChanged;

        public void PublishWorkflow(WorkflowRunView v) => WorkflowChanged?.Invoke(v);

        /// <summary>Fired when the workflow LIBRARY changes (save_workflow / delete_workflow from either
        /// door). Carries the refreshed list so the UI updates without a refetch. Separate from the run
        /// channel — a library edit is not a run transition.</summary>
        public event Action<WorkflowInfo[]> WorkflowLibraryChanged;

        public void PublishWorkflowLibrary(WorkflowInfo[] v) => WorkflowLibraryChanged?.Invoke(v);

        /// <summary>Fired when diagram layout is saved (either door). The RpcServer re-broadcasts as
        /// <c>layout/didChange</c> so the other door's diagram moves live. Layout is sidecar state, not a model
        /// edit, so it has its OWN channel (never model/didChange — that would trigger needless model reloads).</summary>
        public event Action<LayoutChange> LayoutChanged;

        public void PublishLayout(LayoutChange v) => LayoutChanged?.Invoke(v);
    }
}
