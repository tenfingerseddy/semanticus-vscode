using System;

namespace Semanticus.Engine
{
    /// <summary>
    /// Fans applied-change notifications out to every connected client (both doors). The RpcHost
    /// subscribes once and broadcasts model/didChange to all attached pipe clients; the MCP door has no
    /// push, so <see cref="ChangeJournal"/> records each commit and <see cref="McpHealthAppender"/> folds what
    /// others changed into the assistant's next tool result as one line (F-246).
    /// </summary>
    public sealed class ChangeBus
    {
        public event Action<ChangeNotification> Changed;

        public void Publish(ChangeNotification n) => Changed?.Invoke(n);

        /// <summary>Fired when a different model becomes the open one. A swap is not a commit, so it never rode
        /// <see cref="Changed"/>, and a model the assistant created or opened stayed invisible to the window until its
        /// first edit. The RpcServer re-broadcasts this as <c>session/didChange</c>.</summary>
        public event Action<SessionChangeNotification> SessionChanged;

        public void PublishSession(SessionChangeNotification n) => SessionChanged?.Invoke(n);

        /// <summary>Fired when the session's change plan is proposed/edited/applied/cleared. The RpcServer
        /// re-broadcasts this as <c>plan/didChange</c> so the UI watches the plan assemble in real time.</summary>
        public event Action<ChangePlanView> PlanChanged;

        public void PublishPlan(ChangePlanView v) => PlanChanged?.Invoke(v);

        /// <summary>Fired at each step of Run the plan, so the window can show which row is being worked out, applied, checked
        /// or done while the one long call runs. The RpcServer re-broadcasts this as <c>plan/runProgress</c> to the UI door only:
        /// the agent door gets no push (golden rule 2) and reads the same facts, whole, in the run's result.</summary>
        public event Action<PlanRunProgress> PlanRunProgressed;

        public void PublishRunProgress(PlanRunProgress v)
        {
            // Observational, like Progress: a broken listener must never fail the run or starve a healthy one.
            var handlers = PlanRunProgressed;
            if (handlers == null) return;
            foreach (var handler in handlers.GetInvocationList())
            {
                try { ((Action<PlanRunProgress>)handler)(v); }
                catch { /* isolate subscribers */ }
            }
        }

        /// <summary>Fired while a long-running operation advances. The RpcServer re-broadcasts this as
        /// <c>progress/didChange</c> so the UI can show useful progress without treating it as a model edit.</summary>
        public event Action<OperationProgress> Progress;

        public void PublishProgress(OperationProgress v)
        {
            // Progress is observational: a broken listener must never fail the operation or starve healthy listeners.
            var handlers = Progress;
            if (handlers == null) return;
            foreach (var handler in handlers.GetInvocationList())
            {
                try { ((Action<OperationProgress>)handler)(v); }
                catch { /* isolate subscribers */ }
            }
        }

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

        /// <summary>Fired on every change to a composer draft (composer contract 10.8). The RpcServer re-broadcasts it as
        /// <c>draft/didChange</c> beside <c>workflow/didChange</c>; the agent door reads the draft on its next call.</summary>
        public event Action<Composer.DraftView> DraftChanged;

        public void PublishDraft(Composer.DraftView v) => DraftChanged?.Invoke(v);

        /// <summary>Fired on every change to the spec-first start plan, from either door or from the background decide
        /// loop (design-spec-first-start-v2.md). Carries the whole <see cref="StartPlan.StartPlanView"/>, so the
        /// Studio paints it without a refetch. The RpcServer re-broadcasts it as <c>startPlan/didChange</c>. A view with
        /// <c>Exists</c> false is the news that the plan is gone (discarded, or built and retired).
        ///
        /// EVERY change publishes: the Plan's start-plan pointer is kept from these events, and so is the fold line that
        /// tells an assistant somebody else moved the plan. Publish through <see cref="PublishStartPlan"/>, never by
        /// raising the event, so the fold hears it too.</summary>
        public event Action<StartPlan.StartPlanView> StartPlanChanged;

        /// <summary>What the journal's fold hears: the same news as <see cref="StartPlanChanged"/> plus who made the
        /// change. Internal, so it never reaches either wire.</summary>
        internal event Action<StartPlanChangeRecord> StartPlanRecorded;

        /// <summary>Publish one start-plan change. <paramref name="origin"/> is "human" (a person in Studio), "agent" (an
        /// assistant's tool call) or null for the engine's own work (the decide loop, a restart): the fold names the
        /// first two to the other readers, and says nothing about the writer's own change to itself.</summary>
        public void PublishStartPlan(StartPlan.StartPlanView v, string origin = null)
        {
            // Observational like progress: a broken listener must never fail a plan edit or starve the others.
            string writer = null;
            if (origin == "human" || origin == "agent")
                writer = HealthCorrelation.CurrentId != null ? ChangeJournal.InProcessReader : ChangeWriter.Current;
            var recorded = StartPlanRecorded;
            if (recorded != null)
                foreach (var h in recorded.GetInvocationList())
                {
                    try { ((Action<StartPlanChangeRecord>)h)(new StartPlanChangeRecord(v, writer, origin)); }
                    catch { /* isolate subscribers */ }
                }
            var handlers = StartPlanChanged;
            if (handlers == null) return;
            foreach (var h in handlers.GetInvocationList())
            {
                try { ((Action<StartPlan.StartPlanView>)h)(v); }
                catch { /* isolate subscribers */ }
            }
        }

        /// <summary>Fired when the workflow LIBRARY changes (save_workflow / delete_workflow from either
        /// door). Carries the refreshed list so the UI updates without a refetch. Separate from the run
        /// channel — a library edit is not a run transition.</summary>
        public event Action<WorkflowInfo[]> WorkflowLibraryChanged;
        public event Action<WorkflowLayout> WorkflowLayoutChanged;
        public void PublishWorkflowLayout(WorkflowLayout layout) => WorkflowLayoutChanged?.Invoke(layout);

        public void PublishWorkflowLibrary(WorkflowInfo[] v) => WorkflowLibraryChanged?.Invoke(v);

        /// <summary>Fired when diagram layout is saved (either door). The RpcServer re-broadcasts as
        /// <c>layout/didChange</c> so the other door's diagram moves live. Layout is sidecar state, not a model
        /// edit, so it has its OWN channel (never model/didChange — that would trigger needless model reloads).</summary>
        public event Action<LayoutChange> LayoutChanged;

        public void PublishLayout(LayoutChange v) => LayoutChanged?.Invoke(v);

        /// <summary>Fired when the assistant calls show_in_studio. The RpcServer re-broadcasts it as <c>studio/show</c> to every
        /// client; only the extension listens. A show is not a commit, so it never rides <see cref="Changed"/>.</summary>
        public event Action<StudioNote> StudioShow;

        public void PublishStudioShow(StudioNote n) => PublishIsolated(StudioShow, n);

        /// <summary>Fired when the assistant calls suggest_next. Broadcast as <c>studio/suggest</c>.</summary>
        public event Action<StudioSuggestion> StudioSuggest;

        public void PublishStudioSuggest(StudioSuggestion v) => PublishIsolated(StudioSuggest, v);

        /// <summary>Fired when the assistant asked a data question (ask_data). Broadcast as <c>studio/answer</c> so the Ask page shows it.</summary>
        /// <summary>The Next line changed (1.5). Broadcast as <c>studio/nextStep</c>; fired only when the view itself changes.</summary>
        public event Action<NextStepView> StudioNextStep;

        public void PublishStudioNextStep(NextStepView v) => PublishIsolated(StudioNextStep, v);

        public event Action<StudioAnswer> StudioAnswer;

        public void PublishStudioAnswer(StudioAnswer v) => PublishIsolated(StudioAnswer, v);

        /// <summary>Fired first thing in every assistant tool call. Broadcast as <c>assistant/call</c>.</summary>
        public event Action<AgentCallEvent> AgentCall;

        public void PublishAgentCall(AgentCallEvent v) => PublishIsolated(AgentCall, v);

        /// <summary>Fired when the running entitlement changed: a licence applied or removed, or a licence passing its end
        /// while the engine runs (a free Pro month ending, a paid licence going into or out of grace). The RpcServer
        /// re-broadcasts it as <c>entitlement/didChange</c> with the whole get_entitlement answer, so a Studio left open
        /// changes on time. The agent door reads it on its next call, as it learns of every change.</summary>
        public event Action<Entitlement.EntitlementInfo> EntitlementChanged;

        public void PublishEntitlement(Entitlement.EntitlementInfo info) => PublishIsolated(EntitlementChanged, info);

        /// <summary>Set by the RpcServer: true while a person's Studio connection is attached. show_in_studio answers "noStudio" without it.</summary>
        public Func<bool> HumanClientAttached { get; set; }

        // Observational, like progress: a broken listener must never fail the tool call or starve a healthy one.
        private static void PublishIsolated<T>(Action<T> evt, T value)
        {
            if (evt == null) return;
            foreach (var h in evt.GetInvocationList())
            {
                try { ((Action<T>)h)(value); }
                catch { /* isolate subscribers */ }
            }
        }
    }

    /// <summary>One start-plan change as the journal's fold needs it: the view it ended on, who wrote it (a pipe
    /// connection id, or <see cref="ChangeJournal.InProcessReader"/> for this process's MCP door, or null) and the origin.</summary>
    internal sealed class StartPlanChangeRecord
    {
        public StartPlan.StartPlanView View { get; }
        public string Writer { get; }
        public string Origin { get; }
        public StartPlanChangeRecord(StartPlan.StartPlanView view, string writer, string origin) { View = view; Writer = writer; Origin = origin; }
    }
}
